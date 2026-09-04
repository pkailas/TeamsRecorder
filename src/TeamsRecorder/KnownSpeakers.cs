using System.Text.Json;

namespace TeamsRecorder;

/// <summary>
/// A single entry in the sidecar's speaker store (enrollment file).
/// </summary>
internal sealed class StoredSpeaker
{
    public string? Name { get; set; }
}

internal sealed class SpeakerStoreFile
{
    public List<StoredSpeaker>? Speakers { get; set; }
}

/// <summary>
/// Reader for <c>%LOCALAPPDATA%\TeamsRecorder\speakers.json</c>, the voice store
/// the sidecar maintains (see the "Speaker store" section of sidecar/README.md).
/// A missing or unreadable file means "no known speakers" — never an error.
/// </summary>
public static class KnownSpeakers
{
    public static string StorePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TeamsRecorder",
            "speakers.json");

    /// <summary>
    /// All enrolled speaker names, in store order. Empty list when the store is
    /// missing or invalid.
    /// </summary>
    public static IReadOnlyList<string> LoadNames()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var store = JsonSerializer.Deserialize<SpeakerStoreFile>(File.ReadAllText(StorePath));
                if (store?.Speakers is { } speakers)
                    return [.. speakers
                        .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                        .Select(s => s.Name!.Trim())];
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"TeamsRecorder: failed to read speakers.json ({ex.Message}); treating as empty.");
        }
        return [];
    }
}
