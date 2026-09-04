namespace TeamsRecorder;

/// <summary>
/// Entry point. Single-instance guard via a named mutex; the app lives in the
/// notification area and is driven by <see cref="TrayContext"/>.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Single instance: if a previous instance is still running, exit quietly.
        using var mutex = new Mutex(true, @"Global\TeamsRecorder", out bool createdNew);
        if (!createdNew)
            return;

        Application.Run(new TrayContext());
    }
}
