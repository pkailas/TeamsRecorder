using System.IO.Pipes;
using System.Text;

namespace TeamsRecorder;

/// <summary>
/// Entry point. Single-instance guard via a named mutex; the app lives in the
/// notification area and is driven by <see cref="TrayContext"/>.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Hidden CLI switch: --video-smoke <hwnd-or-title-substring> <seconds> <out.mp4>
        // Captures a window for N seconds and exits. Used to verify the capture pipeline
        // without running the tray app.
        if (args.Length >= 4 && args[0] == "--video-smoke")
        {
            RunVideoSmoke(args[1], int.Parse(args[2]), args[3]);
            return;
        }

        // Hidden CLI switches used for verification (see README notes in code):
        //   --parse-url <url>        prints the parsed RenameRequest (or "null") and exits
        //   --register-protocol      registers teamsrecorder:// and prints the command
        if (args.Length == 2 && args[0] == "--parse-url")
        {
            RunParseUrl(args[1]);
            return;
        }

        if (args.Length == 1 && args[0] == "--register-protocol")
        {
            RunRegisterProtocol();
            return;
        }

        // Hidden CLI switch: --meeting-probe
        // Prints every microphone audio session (device | pid | process | teams | state)
        // and the CallActive verdict, then exits. Used to check live what the record-start
        // gate sees — in particular which process owns the Teams capture session.
        if (args.Length == 1 && args[0] == "--meeting-probe")
        {
            RunMeetingProbe();
            return;
        }

        // Hidden CLI switch: --remote-transcribe <sessionFolder> [serviceUrlOverride]
        // Runs the transcription pipeline (see SidecarRunner.TryStart) on an existing
        // session folder — <folder>\mic.wav + <folder>\loopback.wav — and blocks until it
        // finishes, printing the exit code and the tail of sidecar.log. Lets the remote
        // service path be exercised without the tray app. The optional second argument
        // overrides Settings.TranscribeServiceUrl for this run only (e.g. a dead port to
        // watch the local fallback happen).
        if (args.Length >= 2 && args[0] == "--remote-transcribe")
        {
            RunRemoteTranscribe(args[1], args.Length >= 3 ? args[2] : null);
            return;
        }

        // A meeting page's "Save names" click arrives as a teamsrecorder:// URL. If an
        // instance is already running, hand the URL over the named pipe and exit; if the
        // app is not running, fall through to normal startup and let TrayContext process
        // the URL once its UI is up.
        string? pendingUrl = null;
        if (args.Length == 1 && args[0].StartsWith("teamsrecorder://", StringComparison.OrdinalIgnoreCase))
        {
            if (TryHandOffRenameUrl(args[0]))
                return;
            pendingUrl = args[0];
        }

        ApplicationConfiguration.Initialize();

        // Single instance: if a previous instance is still running, exit quietly.
        using var mutex = new Mutex(true, @"Global\TeamsRecorder", out bool createdNew);
        if (!createdNew)
            return;

        Application.Run(new TrayContext(pendingUrl));
    }

    /// <summary>
    /// Writes <paramref name="url"/> to the running instance's rename pipe.
    /// Returns false when no server is listening (the app is not running), in which
    /// case the caller starts the app itself.
    /// </summary>
    private static bool TryHandOffRenameUrl(string url)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", TrayContext.RenamePipeName, PipeDirection.Out);
            pipe.Connect(10000); // generous: a running instance mid-transcription must not drop the request
            using var writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.WriteLine(url);
            writer.Flush();
            return true;
        }
        catch (IOException)
        {
            return false; // No pipe server: no running instance to hand off to.
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Hidden CLI helper: prints what <see cref="RenameRequest.TryParse"/> makes of a URL.</summary>
    private static void RunParseUrl(string url)
    {
        var request = RenameRequest.TryParse(url);
        if (request is null)
        {
            Console.WriteLine("null");
            return;
        }

        Console.WriteLine($"SessionFolder={request.SessionFolder}");
        foreach (var (label, name) in request.Assignments)
            Console.WriteLine($"Assign: {label} = {name}");
    }

    /// <summary>
    /// Hidden CLI helper: runs one transcription (remote when a service URL is configured,
    /// local otherwise) against an existing session folder and reports the outcome.
    /// </summary>
    private static void RunRemoteTranscribe(string sessionFolder, string? serviceUrlOverride)
    {
        if (!Directory.Exists(sessionFolder))
        {
            Console.WriteLine($"[remote-transcribe] FAIL: no such folder: {sessionFolder}");
            Environment.Exit(1);
            return;
        }

        var settings = Settings.Load();
        if (serviceUrlOverride is not null)
            settings.TranscribeServiceUrl = serviceUrlOverride;

        var mic = Path.Combine(sessionFolder, "mic.wav");
        var loopback = Path.Combine(sessionFolder, "loopback.wav");

        Console.WriteLine($"[remote-transcribe] folder={sessionFolder}");
        Console.WriteLine($"[remote-transcribe] service={settings.TranscribeServiceUrl} fallback={settings.TranscribeFallbackToLocal}");

        var done = new ManualResetEventSlim(false);
        int exitCode = -1;
        string? error = null;

        var started = SidecarRunner.TryStart(settings, mic, loopback, sessionFolder, (code, message) =>
        {
            exitCode = code;
            error = message;
            done.Set();
        });

        Console.WriteLine($"[remote-transcribe] started={started}");
        if (!started && error is null)
            error = "nothing was started";

        done.Wait(); // The job is the point of the switch: block for as long as it takes.

        Console.WriteLine($"exit={exitCode} error={error ?? "(none)"}");

        var logPath = Path.Combine(sessionFolder, "sidecar.log");
        Console.WriteLine($"--- tail of {logPath} ---");
        try
        {
            var lines = File.ReadLines(logPath).ToList();
            foreach (var line in lines.Skip(Math.Max(0, lines.Count - 15)))
                Console.WriteLine(line);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(sidecar.log unreadable: {ex.Message})");
        }

        Environment.Exit(exitCode == 0 ? 0 : 1);
    }

    /// <summary>Hidden CLI helper: prints every capture audio session and the CallActive verdict.</summary>
    private static void RunMeetingProbe()
    {
        var status = TeamsCallDetector.Probe();

        foreach (var session in status.Sessions)
            Console.WriteLine(TeamsCallDetector.FormatSession(session));

        if (status.Error is { } error)
            Console.WriteLine($"probe error: {error}");

        Console.WriteLine($"CallActive: {(status.CallActive ? "True" : "False")}");
    }

    /// <summary>Hidden CLI helper: registers the protocol and prints the stored command.</summary>
    private static void RunRegisterProtocol()
    {
        Shortcuts.EnsureProtocolHandler();
        Console.WriteLine($"command = {Shortcuts.ReadProtocolCommand()}");
    }

    /// <summary>
    /// Hidden CLI smoke test: captures a window (by HWND hex or title substring) for
    /// N seconds and writes an MP4. Prints the result to stdout.
    /// </summary>
    private static void RunVideoSmoke(string target, int seconds, string outMp4)
    {
        Console.WriteLine($"[video-smoke] target={target} seconds={seconds} out={outMp4}");

        // Resolve the window.
        IntPtr hwnd = IntPtr.Zero;
        if (target.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || target.StartsWith("-0x", StringComparison.OrdinalIgnoreCase))
        {
            hwnd = new IntPtr(long.Parse(target.StartsWith("-0x") ? target[1..] : target, System.Globalization.NumberStyles.HexNumber));
        }
        else
        {
            // Title substring: find the best ms-teams window.
            var (found, title, candidates) = WindowCapture.FindTeamsMeetingWindowCore();
            if (found == IntPtr.Zero)
            {
                Console.WriteLine("[video-smoke] FAIL: no ms-teams window found.");
                Environment.Exit(1);
            }
            hwnd = found;
            Console.WriteLine($"[video-smoke] found window 0x{hwnd:X} \"{title}\" ({candidates.Count} candidates)");
        }

        var settings = Settings.Load();
        var capture = new WindowCapture();
        capture.Log += line => Console.WriteLine($"[video] {line}");

        try
        {
            capture.Start(hwnd, outMp4, settings);
            Console.WriteLine($"[video-smoke] capturing for {seconds}s…");
            Thread.Sleep(seconds * 1000);
            capture.Stop();
            Console.WriteLine("[video-smoke] done.");

            if (File.Exists(outMp4))
            {
                var size = new FileInfo(outMp4).Length;
                Console.WriteLine($"[video-smoke] OK: {outMp4} ({size:N0} bytes)");
            }
            else
            {
                Console.WriteLine($"[video-smoke] FAIL: {outMp4} not found.");
                Environment.Exit(1);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[video-smoke] FAIL: {ex}");
            try { capture.Dispose(); } catch { }
            Environment.Exit(1);
        }
    }
}
