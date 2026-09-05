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
