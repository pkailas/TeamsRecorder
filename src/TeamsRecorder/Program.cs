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

        ApplicationConfiguration.Initialize();

        // Single instance: if a previous instance is still running, exit quietly.
        using var mutex = new Mutex(true, @"Global\TeamsRecorder", out bool createdNew);
        if (!createdNew)
            return;

        Application.Run(new TrayContext());
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
