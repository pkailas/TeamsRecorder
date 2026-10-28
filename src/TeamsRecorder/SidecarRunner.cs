using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TeamsRecorder;

/// <summary>
/// Fire-and-forget launcher for the transcription pipeline.
/// Local:  PythonExe SidecarScript --mic <mic.wav> --loopback <loopback.wav> --out <sessionFolder>
/// with stdout/stderr redirected to <sessionFolder>\sidecar.log.
/// Remote (when <see cref="Settings.TranscribeServiceUrl"/> is set): the same job is
/// posted to the GPU service (see service/README.md) and the reply zip is unpacked into
/// the session folder; muxing and the library page are then run locally.
/// </summary>
public static class SidecarRunner
{
    /// <summary>
    /// Starts the sidecar in the background and reports completion via
    /// <paramref name="onFinished"/> (invoked on a thread-pool thread; marshal to UI as needed).
    /// Returns false with a reason if the process could not be started.
    /// </summary>
    public static bool TryStart(
        Settings s,
        string micWav,
        string loopbackWav,
        string sessionFolder,
        Action<int, string?> onFinished)
    {
        // Empty service URL = always the local sidecar: hand straight over to the
        // unchanged local implementation.
        if (string.IsNullOrWhiteSpace(s.TranscribeServiceUrl))
            return TryStartLocal(s, micWav, loopbackWav, sessionFolder, onFinished);

        // Remote path: everything (health probe, upload, unpack, mux, library) runs on a
        // background Task; onFinished is invoked on a thread-pool thread exactly like the
        // local path. Only a synchronous failure to open the session log means "nothing
        // could be started" — mirrors the local behaviour for a failed process launch.
        try
        {
            _ = Task.Run(() => StartRemoteAsync(s, micWav, loopbackWav, sessionFolder, onFinished));
            return true;
        }
        catch (Exception ex)
        {
            var startError = ex.Message;
            Task.Run(() => onFinished(-1, startError));
            return false;
        }
    }

    /// <summary>
    /// The local Python sidecar path. Untouched by the remote feature: when
    /// <see cref="Settings.TranscribeServiceUrl"/> is empty this is exactly what runs.
    /// </summary>
    private static bool TryStartLocal(
        Settings s,
        string micWav,
        string loopbackWav,
        string sessionFolder,
        Action<int, string?> onFinished,
        bool appendLog = false)
    {
        string? startError = null;
        Process? process = null;

        try
        {
            var logPath = Path.Combine(sessionFolder, "sidecar.log");
            // One thread-safe writer shared by both streams: opening the same file
            // twice throws "being used by another process" and the sidecar never launches.
            // appendLog is false for every local-only run (start a fresh log, as always);
            // the remote fallback passes true so the remote header survives above it.
            var stdout = TextWriter.Synchronized(new StreamWriter(logPath, append: appendLog) { AutoFlush = true });
            var stderr = stdout;

            var psi = new ProcessStartInfo
            {
                FileName = s.PythonExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(s.SidecarScript) ?? sessionFolder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(s.SidecarScript);
            psi.ArgumentList.Add("--mic");
            psi.ArgumentList.Add(micWav);
            psi.ArgumentList.Add("--loopback");
            psi.ArgumentList.Add(loopbackWav);
            psi.ArgumentList.Add("--out");
            psi.ArgumentList.Add(sessionFolder);

            process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    stdout.WriteLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    stderr.WriteLine(e.Data);
            };

            if (!process.Start())
            {
                startError = "The sidecar process did not start.";
            }
            else
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            if (startError is null)
            {
                var procRef = process;
                Task.Run(() =>
                {
                    int exitCode = -1;
                    string? errorMessage = null;
                    try
                    {
                        procRef.WaitForExit();
                        exitCode = procRef.ExitCode;
                    }
                    catch (Exception ex)
                    {
                        errorMessage = ex.Message;
                    }
                    finally
                    {
                        stdout.Dispose();
                        procRef.Dispose();
                    }
                    onFinished(exitCode, errorMessage);
                });
                return true;
            }
        }
        catch (Exception ex)
        {
            startError = ex.Message;
            process?.Dispose();
        }

        // Report the start failure asynchronously so callers can show a balloon.
        Task.Run(() => onFinished(-1, startError));
        return false;
    }

    // ------------------------------------------------------------------
    // Remote (GPU service) path — see service/README.md.
    // ------------------------------------------------------------------

    /// <summary>Shared client for the GPU service; the job timeout is enforced by a
    /// <see cref="CancellationTokenSource"/>, not by the client, so it must be infinite.</summary>
    private static readonly HttpClient RemoteHttp = new(new SocketsHttpHandler())
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>Wall-clock budget for one remote transcription job (a 3-hour meeting).</summary>
    private static readonly TimeSpan RemoteJobTimeout = TimeSpan.FromHours(3);

    /// <summary>Maximum characters of a service response body quoted in sidecar.log.</summary>
    private const int RemoteBodyQuoteLimit = 1600;

    /// <summary>
    /// Drives one remote transcription job: health probe, upload, zip unpack, then the
    /// local mux + library steps. Invokes <paramref name="onFinished"/> on a thread-pool
    /// thread with the same contract as the local path.
    /// </summary>
    private static async Task StartRemoteAsync(
        Settings s,
        string micWav,
        string loopbackWav,
        string sessionFolder,
        Action<int, string?> onFinished)
    {
        TextWriter? log = null;
        var cts = new CancellationTokenSource(RemoteJobTimeout);
        var baseUrl = s.TranscribeServiceUrl.Trim();
        var fallback = s.TranscribeFallbackToLocal;
        // Set once the local sidecar has been launched as a fallback: onFinished then
        // belongs to it, and this method must not report anything.
        bool handedToLocal = false;

        // Every write goes through LogLine: once the log writer has been closed (it must
        // be closed before any child process that appends to the same file starts — see
        // CloseLog) later lines are appended by their own writer, and a failed write must
        // never swallow the job's completion callback.
        void LogLine(string line)
        {
            try
            {
                log?.WriteLine(line);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"TeamsRecorder: sidecar.log write failed ({ex.Message}): {line}");
            }
        }

        // The session log is opened with FileShare.Read, so a second writer on it — the
        // local sidecar, the mux, the library step — fails with "being used by another
        // process" while this one is open. Close it before handing the file to them.
        void CloseLog()
        {
            try
            {
                log?.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"TeamsRecorder: closing sidecar.log failed ({ex.Message}).");
            }
            log = null;
        }

        try
        {
            var logPath = Path.Combine(sessionFolder, "sidecar.log");
            // One thread-safe writer shared by every line of this run (see TryStartLocal).
            log = TextWriter.Synchronized(new StreamWriter(logPath, append: false) { AutoFlush = true });
            LogLine($"--- remote transcription via {baseUrl} {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");

            string? healthError = null;
            try
            {
                using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var response = await RemoteHttp.GetAsync(
                    CombineUrl(baseUrl, "/health"), probe.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(probe.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    healthError = $"GET {CombineUrl(baseUrl, "/health")} -> HTTP {(int)response.StatusCode}: {Quote(body)}";
                else if (!string.Equals(HealthStatus(body), "ok", StringComparison.OrdinalIgnoreCase))
                    healthError = $"GET {CombineUrl(baseUrl, "/health")} -> status != ok: {Quote(body)}";
            }
            catch (Exception ex)
            {
                healthError = $"GET {CombineUrl(baseUrl, "/health")} failed: {ex.Message}";
            }

            if (healthError is not null)
            {
                LogLine(healthError);
                if (!fallback)
                {
                    LogLine("no fallback configured; reporting failure.");
                    CloseLog();
                    onFinished(-1, healthError);
                    return;
                }
                LogLine("falling back to local sidecar");
                handedToLocal = true;
                CloseLog();
                TryStartLocal(s, micWav, loopbackWav, sessionFolder, onFinished, appendLog: true);
                return;
            }

            LogLine("health ok; posting the job to the GPU service (the job queue is serial: "
                    + "a job ahead of this one is waited for).");

            string? failure;
            byte[]? zipBytes = null;
            try
            {
                using var form = new MultipartFormDataContent();
                AddWavPart(form, "mic", micWav);
                AddWavPart(form, "loopback", loopbackWav);
                if (File.Exists(KnownSpeakers.StorePath))
                {
                    var speakers = new StreamContent(
                        new FileStream(KnownSpeakers.StorePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                    speakers.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    form.Add(speakers, "speakers", "speakers.json");
                    LogLine($"including speaker store {KnownSpeakers.StorePath}");
                }
                form.Add(new StringContent("en", Encoding.UTF8), "language");

                using var request = new HttpRequestMessage(
                    HttpMethod.Post, CombineUrl(baseUrl, "/transcribe")) { Content = form };
                using var response = await RemoteHttp.SendAsync(
                    request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;

                if (status == 200)
                {
                    zipBytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                    LogLine($"service responded 200 ({zipBytes.Length:N0} bytes of zip)");
                    failure = null;
                }
                else
                {
                    var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    failure = $"POST {CombineUrl(baseUrl, "/transcribe")} -> HTTP {status}: "
                            + $"{Quote(ErrorText(body))}{ServiceLogTail(body)}";
                }
            }
            catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
            {
                failure = $"remote job exceeded the {RemoteJobTimeout.TotalHours:0} h budget: {ex.Message}";
            }
            catch (Exception ex)
            {
                failure = $"remote request failed ({ex.GetType().Name}): {ex.Message}";
            }

            if (failure is not null)
            {
                LogLine(failure);
                if (!fallback)
                {
                    LogLine("no fallback configured; reporting failure.");
                    CloseLog();
                    onFinished(-1, failure);
                    return;
                }
                LogLine("falling back to local sidecar");
                handedToLocal = true;
                CloseLog();
                TryStartLocal(s, micWav, loopbackWav, sessionFolder, onFinished, appendLog: true);
                return;
            }

            ExtractZip(zipBytes!, sessionFolder, LogLine);

            var transcript = Path.Combine(sessionFolder, "transcript.json");
            if (!File.Exists(transcript))
            {
                const string noTranscript = "the service returned no transcript.json";
                LogLine(noTranscript);
                if (!fallback)
                {
                    CloseLog();
                    onFinished(-1, noTranscript);
                    return;
                }
                LogLine("falling back to local sidecar");
                handedToLocal = true;
                CloseLog();
                TryStartLocal(s, micWav, loopbackWav, sessionFolder, onFinished, appendLog: true);
                return;
            }

            // Post-steps the service deliberately does not do: muxing the captured video
            // and refreshing the recordings-root library page. Failures are logged only —
            // the transcript is the result, so the job still reports exit 0. Both append
            // to sidecar.log themselves, so this run's writer closes first.
            CloseLog();

            var muxExit = await RunMuxAsync(s, sessionFolder).ConfigureAwait(false);
            if (muxExit != 0)
                AppendSessionLog(sessionFolder,
                    $"mux: exit {muxExit}; see mux.log and the mux lines above (transcription unaffected).");

            var root = Path.GetDirectoryName(sessionFolder);
            if (!string.IsNullOrEmpty(root))
            {
                var libraryExit = await LibraryAsync(s, root).ConfigureAwait(false);
                if (libraryExit != 0)
                    AppendSessionLog(sessionFolder,
                        $"library: exit {libraryExit}; see {Path.Combine(root, "library.log")} (transcription unaffected).");
            }

            AppendSessionLog(sessionFolder, "remote transcription complete");
            onFinished(0, null);
        }
        catch (Exception ex)
        {
            // Anything unexpected here (typically the session log being unwritable).
            LogLine($"remote transcription error: {ex.Message}");
            if (!handedToLocal)
                onFinished(-1, ex.Message);
        }
        finally
        {
            cts.Dispose();
            CloseLog();
        }
    }

    /// <summary>Appends one line to <c>&lt;session&gt;\sidecar.log</c>, opening and closing
    /// the file for it. Used after this run's own writer has closed, so the mux/library
    /// writers (which use the same file) are never blocked. A failed write is reported to
    /// stderr only: it must not change the job's outcome.</summary>
    private static void AppendSessionLog(string sessionFolder, string line)
    {
        try
        {
            using var writer = new StreamWriter(
                Path.Combine(sessionFolder, "sidecar.log"), append: true) { AutoFlush = true };
            writer.WriteLine(line);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"TeamsRecorder: sidecar.log write failed ({ex.Message}): {line}");
        }
    }

    /// <summary>Adds one WAV as a streamed multipart part (never buffered in memory).</summary>
    private static void AddWavPart(MultipartFormDataContent form, string name, string path)
    {
        var content = new StreamContent(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, name, Path.GetFileName(path));
    }

    /// <summary>
    /// Unpacks the service's zip into the session folder, overwriting. Only plain file
    /// names are accepted (zip-slip guard: the entry name must equal its own file name).
    /// <c>sidecar.log</c> is not written out as a file: its content is appended to the
    /// session's sidecar.log through <paramref name="logLine"/>, under a
    /// <c>--- service sidecar.log ---</c> marker, below this run's header.
    /// </summary>
    private static void ExtractZip(byte[] zipBytes, string sessionFolder, Action<string> logLine)
    {
        var serviceLog = (string?)null;

        using (var ms = new MemoryStream(zipBytes, writable: false))
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                var name = entry.Name;
                if (string.IsNullOrEmpty(name) || Path.GetFileName(name) != name)
                {
                    logLine($"zip entry skipped (not a plain file name): {name}");
                    continue;
                }
                if (name == "sidecar.log")
                {
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                    serviceLog = reader.ReadToEnd();
                    continue;
                }
                using var src = entry.Open();
                using var dst = new FileStream(
                    Path.Combine(sessionFolder, name), FileMode.Create, FileAccess.Write);
                src.CopyTo(dst);
            }
        }

        if (serviceLog is { Length: > 0 })
        {
            logLine("--- service sidecar.log ---");
            foreach (var line in serviceLog.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                logLine(line);
        }
    }

    /// <summary>Runs the sidecar's <c>mux</c> subcommand (no GPU beyond ffmpeg NVENC),
    /// using <see cref="Settings.FfmpegExe"/>. Appends to <c>&lt;session&gt;\sidecar.log</c>.
    /// Returns the exit code; -1 when the process could not be started.</summary>
    public static Task<int> RunMuxAsync(Settings s, string sessionFolder)
    {
        return Task.Run(() =>
        {
            Process? process = null;
            TextWriter? stdout = null;
            TextWriter? stderr = null;
            try
            {
                var logPath = Path.Combine(sessionFolder, "sidecar.log");
                // Single shared writer (see TryStartLocal).
                stdout = TextWriter.Synchronized(new StreamWriter(logPath, append: true) { AutoFlush = true });
                stderr = stdout;
                stdout.WriteLine($"\n--- mux {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");

                var psi = new ProcessStartInfo
                {
                    FileName = s.PythonExe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(s.SidecarScript) ?? sessionFolder,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add(s.SidecarScript);
                psi.ArgumentList.Add("mux");
                psi.ArgumentList.Add("--session");
                psi.ArgumentList.Add(sessionFolder);
                psi.ArgumentList.Add("--ffmpeg");
                psi.ArgumentList.Add(s.FfmpegExe);

                process = new Process { StartInfo = psi };
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                        stdout.WriteLine(e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                        stderr.WriteLine(e.Data);
                };

                if (!process.Start())
                    return -1;

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                return process.ExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"TeamsRecorder: mux failed ({ex.Message}).");
                return -1;
            }
            finally
            {
                stdout?.Dispose();
                process?.Dispose();
            }
        });
    }

    /// <summary>Joins a service base URL and a path, tolerating a trailing slash.</summary>
    private static string CombineUrl(string baseUrl, string path) =>
        baseUrl.TrimEnd('/') + path;

    /// <summary>The <c>status</c> field of the /health JSON, or null when unreadable.</summary>
    private static string? HealthStatus(string body) => JsonField(body, "status");

    /// <summary>The <c>error</c> field of the service's 500 JSON, falling back to the raw body.</summary>
    private static string ErrorText(string body)
    {
        var error = JsonField(body, "error");
        if (!string.IsNullOrEmpty(error))
            return error;
        var trimmed = body.Trim();
        return trimmed.Length == 0 ? "(empty body)" : trimmed;
    }

    /// <summary>The <c>log_tail</c> array of the service's 500 JSON rendered as extra lines.</summary>
    private static string ServiceLogTail(string body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body))
                return string.Empty;
            var doc = JsonSerializer.Deserialize<JsonElement>(body);
            if (doc.ValueKind != JsonValueKind.Object
                || !doc.TryGetProperty("log_tail", out var tail)
                || tail.ValueKind != JsonValueKind.Array)
                return string.Empty;

            var lines = new List<string>();
            foreach (var item in tail.EnumerateArray())
            {
                var text = item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText();
                if (!string.IsNullOrEmpty(text))
                    lines.Add("  " + text);
            }
            return lines.Count == 0 ? string.Empty : "\nlog tail:\n" + string.Join("\n", lines);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>Reads one string property out of a JSON object; null when absent/unreadable.</summary>
    private static string? JsonField(string body, string field)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body))
                return null;
            var doc = JsonSerializer.Deserialize<JsonElement>(body);
            return doc.ValueKind == JsonValueKind.Object
                && doc.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One-line quote of a service response body, capped for the log file.</summary>
    private static string Quote(string body)
    {
        var flat = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (flat.Length == 0)
            return "(empty body)";
        return flat.Length <= RemoteBodyQuoteLimit
            ? flat
            : flat[..RemoteBodyQuoteLimit] + " …";
    }

    /// <summary>
    /// Runs the sidecar's <c>rename</c> subcommand on a background thread:
    /// <c>PythonExe SidecarScript rename --session &lt;folder&gt; --assign "Label=Name" ...</c>
    /// stdout/stderr are appended to <c>&lt;folder&gt;\sidecar.log</c>. Returns the
    /// exit code; -1 if the process could not be started or crashed.
    /// </summary>
    public static Task<int> RenameAsync(
        Settings s,
        string sessionFolder,
        IEnumerable<(string label, string name)> assignments)
    {
        return Task.Run(() =>
        {
            Process? process = null;
            TextWriter? stdout = null;
            TextWriter? stderr = null;
            try
            {
                var logPath = Path.Combine(sessionFolder, "sidecar.log");
                // Single shared writer (see TryStart).
                stdout = TextWriter.Synchronized(new StreamWriter(logPath, append: true) { AutoFlush = true });
                stderr = stdout;
                stdout.WriteLine($"\n--- rename {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");

                var psi = new ProcessStartInfo
                {
                    FileName = s.PythonExe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(s.SidecarScript) ?? sessionFolder,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add(s.SidecarScript);
                psi.ArgumentList.Add("rename");
                psi.ArgumentList.Add("--session");
                psi.ArgumentList.Add(sessionFolder);
                foreach (var (label, name) in assignments)
                {
                    psi.ArgumentList.Add("--assign");
                    psi.ArgumentList.Add($"{label}={name}");
                }

                process = new Process { StartInfo = psi };
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                        stdout.WriteLine(e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                        stderr.WriteLine(e.Data);
                };

                if (!process.Start())
                    return -1;

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                return process.ExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"TeamsRecorder: rename failed ({ex.Message}).");
                return -1;
            }
            finally
            {
                stdout?.Dispose();
                process?.Dispose();
            }
        });
    }

    /// <summary>
    /// Runs the sidecar's <c>library</c> subcommand on a background thread:
    /// <c>PythonExe SidecarScript library --root &lt;root&gt;</c>. stdout/stderr are
    /// appended to <c>&lt;root&gt;\library.log</c> (there is no session folder for
    /// this command; the log lives next to the library.html it produces).
    /// Returns the exit code; -1 if the process could not be started or crashed.
    /// </summary>
    public static Task<int> LibraryAsync(
        Settings s,
        string recordingsRoot)
    {
        return Task.Run(() =>
        {
            Process? process = null;
            TextWriter? stdout = null;
            TextWriter? stderr = null;
            try
            {
                var logPath = Path.Combine(recordingsRoot, "library.log");
                // Single shared writer (see TryStart).
                stdout = TextWriter.Synchronized(new StreamWriter(logPath, append: true) { AutoFlush = true });
                stderr = stdout;
                stdout.WriteLine($"\n--- library {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");

                var psi = new ProcessStartInfo
                {
                    FileName = s.PythonExe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(s.SidecarScript) ?? recordingsRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add(s.SidecarScript);
                psi.ArgumentList.Add("library");
                psi.ArgumentList.Add("--root");
                psi.ArgumentList.Add(recordingsRoot);

                process = new Process { StartInfo = psi };
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                        stdout.WriteLine(e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                        stderr.WriteLine(e.Data);
                };

                if (!process.Start())
                    return -1;

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                return process.ExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"TeamsRecorder: library failed ({ex.Message}).");
                return -1;
            }
            finally
            {
                stdout?.Dispose();
                process?.Dispose();
            }
        });
    }
}
