using System.Diagnostics;

namespace TeamsRecorder;

/// <summary>
/// Fire-and-forget launcher for the Python transcription sidecar.
/// Runs:  PythonExe SidecarScript --mic <mic.wav> --loopback <loopback.wav> --out <sessionFolder>
/// with stdout/stderr redirected to <sessionFolder>\sidecar.log.
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
        string? startError = null;
        Process? process = null;

        try
        {
            var logPath = Path.Combine(sessionFolder, "sidecar.log");
            // One thread-safe writer shared by both streams: opening the same file
            // twice throws "being used by another process" and the sidecar never launches.
            var stdout = TextWriter.Synchronized(new StreamWriter(logPath, append: false) { AutoFlush = true });
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
}
