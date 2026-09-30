using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamsRecorder;

/// <summary>
/// Per-session metadata written to <c>&lt;session&gt;\session.json</c> at record
/// start. The Python sidecar reads this file (a later job does the reading), so
/// the shape of <see cref="SessionInfo"/> is a cross-language contract — keep the
/// JSON property names stable.
/// </summary>
public sealed class SessionInfo
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Bump when the on-disk shape changes so consumers can migrate.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Parsed meeting title (first " | " segment of the window title), or null when
    /// no usable title was found.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>The raw chosen Teams window title, verbatim, or null when no window.</summary>
    [JsonPropertyName("windowTitle")]
    public string? WindowTitle { get; init; }

    /// <summary>Local start time, ISO 8601 with offset (<c>DateTimeOffset.Now</c>).</summary>
    [JsonPropertyName("startedLocal")]
    public DateTimeOffset StartedLocal { get; init; }

    /// <summary>
    /// Extracts the meeting title from a raw Teams window title. Splits on " | ",
    /// takes the first segment, and trims it.
    /// </summary>
    /// <param name="rawWindowTitle">
    /// A title such as
    /// "Caio Cardoso, John Walls | Verbella CMG, LLC | paul.kailas@Verbella.com | Microsoft Teams".
    /// </param>
    /// <returns>
    /// The first " | " segment (e.g. "Caio Cardoso, John Walls"), or null when the
    /// input is null/whitespace, the result is empty, or it equals "Microsoft Teams"
    /// (case-insensitive).
    /// </returns>
    public static string? ParseMeetingTitle(string? rawWindowTitle)
    {
        if (string.IsNullOrWhiteSpace(rawWindowTitle))
            return null;

        var first = rawWindowTitle.Split(" | ")[0].Trim();
        if (first.Length == 0)
            return null;
        if (first.Equals("Microsoft Teams", StringComparison.OrdinalIgnoreCase))
            return null;
        return first;
    }

    /// <summary>
    /// Serializes this record to <c>&lt;sessionFolder&gt;\session.json</c>
    /// (indented, camelCase). Caller treats the result as best-effort.
    /// </summary>
    public void WriteTo(string sessionFolder)
    {
        var json = JsonSerializer.Serialize(this, WriteOptions);
        File.WriteAllText(Path.Combine(sessionFolder, "session.json"), json);
    }
}
