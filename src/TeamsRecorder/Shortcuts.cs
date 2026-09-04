namespace TeamsRecorder;

/// <summary>
/// Static helpers for the "Start with Windows" autostart shortcut and the
/// Start Menu (Programs) shortcut. Shortcuts are written as .lnk files via
/// the late-bound <c>WScript.Shell</c> COM object, so no extra packages are
/// needed (System.Runtime.InteropServices + Microsoft.CSharp for <c>dynamic</c>,
/// both part of the shared framework).
/// </summary>
public static class Shortcuts
{
    /// <summary>Full path of the currently running executable.</summary>
    public static string ExePath =>
        Environment.ProcessPath ?? Application.ExecutablePath;

    /// <summary>Path of the autostart shortcut in the per-user Startup folder.</summary>
    public static string StartupLinkPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "TeamsRecorder.lnk");

    /// <summary>
    /// Path of the Start Menu shortcut: %PROGRAMS%\TeamsRecorder\TeamsRecorder.lnk.
    /// The TeamsRecorder sub-folder is created on demand by
    /// <see cref="EnsureStartMenuShortcut"/>.
    /// </summary>
    public static string StartMenuLinkPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "TeamsRecorder",
            "TeamsRecorder.lnk");

    /// <summary>True when the autostart shortcut exists.</summary>
    public static bool StartupEnabled => File.Exists(StartupLinkPath);

    /// <summary>
    /// Creates (enable) or deletes (disable) the autostart shortcut in the
    /// per-user Startup folder. Throws on failure.
    /// </summary>
    public static void SetStartup(bool enable)
    {
        if (enable)
        {
            CreateShortcut(StartupLinkPath, ExePath);
        }
        else
        {
            if (File.Exists(StartupLinkPath))
                File.Delete(StartupLinkPath);
        }
    }

    /// <summary>
    /// Creates the Start Menu (Programs) shortcut if it is missing, or rewrites
    /// it if its target differs from <see cref="ExePath"/>. Does nothing when
    /// the shortcut already points at the current exe. Throws on failure.
    /// </summary>
    public static void EnsureStartMenuShortcut()
    {
        var dir = Path.GetDirectoryName(StartMenuLinkPath)!;
        Directory.CreateDirectory(dir);

        if (File.Exists(StartMenuLinkPath) && ShortcutTarget(StartMenuLinkPath) == ExePath)
            return;

        CreateShortcut(StartMenuLinkPath, ExePath);
    }

    /// <summary>
    /// Creates a Windows .lnk shortcut at <paramref name="lnkPath"/> pointing at
    /// <paramref name="target"/>, using the late-bound WScript.Shell COM object.
    /// Rethrows failures with a clear, contextual message.
    /// </summary>
    public static void CreateShortcut(string lnkPath, string target)
    {
        var dir = Path.GetDirectoryName(lnkPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ??
                throw new InvalidOperationException("The WScript.Shell COM object is not available.");
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic sc = shell.CreateShortcut(lnkPath)!;
                sc.TargetPath = target;
                sc.WorkingDirectory = Path.GetDirectoryName(target) ?? string.Empty;
                sc.IconLocation = target + ",0";
                sc.Description = "Teams meeting recorder";
                sc.Save();
            }
            finally
            {
                // Release the COM objects; best-effort, never throw on cleanup.
                try { ((IDisposable)shell).Dispose(); } catch { /* ignore */ }
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Failed to create shortcut '{lnkPath}' pointing at '{target}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Returns the target path of an existing .lnk, or null if it is missing or
    /// unreadable. Used by <see cref="EnsureStartMenuShortcut"/> to detect stale
    /// shortcuts.
    /// </summary>
    private static string? ShortcutTarget(string lnkPath)
    {
        try
        {
            if (!File.Exists(lnkPath))
                return null;
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic sc = shell.CreateShortcut(lnkPath)!;
                var target = sc.TargetPath as string;
                return string.IsNullOrEmpty(target) ? null : target;
            }
            finally
            {
                try { ((IDisposable)shell).Dispose(); } catch { /* ignore */ }
            }
        }
        catch
        {
            return null;
        }
    }
}
