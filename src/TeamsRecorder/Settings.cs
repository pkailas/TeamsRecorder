using System.Text.Json;

namespace TeamsRecorder;

/// <summary>
/// Persistent settings, loaded/saved as JSON from
/// %LOCALAPPDATA%\TeamsRecorder\settings.json. Missing file = defaults.
/// </summary>
public sealed class Settings
{
    /// <summary>Folder that receives one sub-folder per recording session.</summary>
    public string OutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Recordings", "Teams");

    /// <summary>Path to the Python interpreter that runs the sidecar script.</summary>
    public string PythonExe { get; set; } =
        Path.Combine(ResolveRepoRoot(), "sidecar", ".venv", "Scripts", "python.exe");

    /// <summary>Path to the transcription sidecar script.</summary>
    public string SidecarScript { get; set; } =
        Path.Combine(ResolveRepoRoot(), "sidecar", "transcribe.py");

    /// <summary>Global hotkey that toggles recording, e.g. "Ctrl+Alt+R".</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+R";

    /// <summary>Microphone device name; null = default communications capture device.</summary>
    public string? MicDeviceName { get; set; }

    /// <summary>Render (loopback) device name; null = default render device.</summary>
    public string? LoopbackDeviceName { get; set; }

    /// <summary>Whether to launch the Python sidecar automatically after Stop.</summary>
    public bool AutoTranscribe { get; set; } = true;

    /// <summary>Whether to capture the Teams meeting window as video during recording.</summary>
    public bool VideoOn { get; set; } = true;

    /// <summary>Frame rate (fps) for the video capture. Default 5.</summary>
    public int VideoFps { get; set; } = 5;

    /// <summary>Path to the ffmpeg executable used for video encoding.</summary>
    public string FfmpegExe { get; set; } = @"G:\tools\ffmpeg\8.0.1\ffmpeg.exe";

    /// <summary>
    /// Stop the recording automatically once the meeting window has been gone for
    /// <see cref="AutoStopGraceSeconds"/>. False = manual stop only (the pre-auto-stop behaviour).
    /// </summary>
    public bool AutoStopOnMeetingEnd { get; set; } = true;

    /// <summary>
    /// How long (seconds) the meeting window must stay missing before the recording is
    /// auto-stopped. Absorbs the window churn of navigating elsewhere in Teams mid-meeting.
    /// </summary>
    public int AutoStopGraceSeconds { get; set; } = 60;

    /// <summary>
    /// Refuse to start a recording unless <see cref="TeamsCallDetector"/> sees a Teams
    /// process holding an active microphone session. False = start unconditionally.
    /// </summary>
    public bool RequireMeetingToStart { get; set; } = true;

    /// <summary>
    /// Global hotkey that starts a recording even when no Teams meeting is detected
    /// (same toggle semantics as <see cref="Hotkey"/>).
    /// </summary>
    public string ForceHotkey { get; set; } = "Ctrl+Shift+Alt+R";

    /// <summary>
    /// Base URL of the GPU transcription service (see service/README.md), e.g.
    /// "http://10.0.0.129:8095". Empty = always transcribe with the local Python
    /// sidecar. Old settings.json files without this key get the default.
    /// </summary>
    public string TranscribeServiceUrl { get; set; } = "http://10.0.0.129:8095";

    /// <summary>
    /// When the GPU service is unreachable (or a remote job fails), run the local
    /// Python sidecar on the same session instead of reporting a failure.
    /// </summary>
    public bool TranscribeFallbackToLocal { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TeamsRecorder",
            "settings.json");

    /// <summary>
    /// Loads settings from disk, creating the file with defaults if it is missing
    /// or unreadable.
    /// </summary>
    public static Settings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<Settings>(json, JsonOptions);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"TeamsRecorder: failed to read settings ({ex.Message}); using defaults.");
        }

        var defaults = new Settings();
        try
        {
            defaults.Save();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"TeamsRecorder: failed to write default settings ({ex.Message}).");
        }
        return defaults;
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>
    /// Resolves the repository root (the directory that contains src\ and sidecar\).
    /// When running from bin\ (e.g. src\TeamsRecorder\bin\Debug\net10.0-windows\),
    /// the repo root is two directories above AppContext.BaseDirectory; when the
    /// exe sits at the repo root already, BaseDirectory is the answer.
    /// </summary>
    public static string ResolveRepoRoot()
    {
        // Walk up from the exe location until we find the repo root, identified
        // by sidecar\transcribe.py. Handles bin\Debug\net10.0-windows\, publish
        // folders, or the exe sitting at the root. Falls back to BaseDirectory.
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);

        // A published copy (see publish.ps1) lives outside the repo; it carries a
        // "repo.path" file next to the exe pointing back at the checkout.
        var pointer = Path.Combine(baseDir.FullName, "repo.path");
        if (File.Exists(pointer))
        {
            var root = File.ReadAllText(pointer).Trim();
            if (File.Exists(Path.Combine(root, "sidecar", "transcribe.py")))
                return root;
        }

        for (var dir = baseDir; dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "sidecar", "transcribe.py")))
                return dir.FullName;
        }
        return baseDir.FullName;
    }
}
