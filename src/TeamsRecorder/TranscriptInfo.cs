using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamsRecorder;

/// <summary>
/// One utterance in a transcript.
/// </summary>
public sealed record Utterance
{
    [JsonPropertyName("start")]
    public double Start { get; init; }

    [JsonPropertyName("end")]
    public double End { get; init; }

    [JsonPropertyName("speaker")]
    public string? Speaker { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

/// <summary>
/// One entry in the sidecar's <c>speaker_map</c> array (see sidecar/README.md).
/// <see cref="Name"/> is null when no enrolled name matched (or when the sidecar
/// ran with <c>--no-embeddings</c>). <c>Embedding</c> is deliberately not
/// deserialized — it is a 256-float vector the C# side never needs.
/// </summary>
public sealed record SpeakerMapEntry
{
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("diarization_label")]
    public string? DiarizationLabel { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Cosine similarity of the best stored voice; may be a negative sentinel.</summary>
    [JsonPropertyName("match_score")]
    public float MatchScore { get; init; }

    /// <summary>Closest stored name even below the match threshold, or null.</summary>
    [JsonPropertyName("best_candidate")]
    public string? BestCandidate { get; init; }

    [JsonPropertyName("talk_time_sec")]
    public float TalkTimeSec { get; init; }

    [JsonPropertyName("sample_text")]
    public string? SampleText { get; init; }
}

/// <summary>
/// The subset of <c>&lt;session&gt;\transcript.json</c> the tray app uses to build
/// the speaker-naming dialog. Load returns null when the file is missing or invalid.
/// </summary>
public sealed class TranscriptInfo
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public string? Created { get; private set; }
    public string? Model { get; private set; }
    public double DurationSec { get; private set; }
    public IReadOnlyList<string>? Speakers { get; private set; }
    public IReadOnlyList<Utterance> Utterances { get; private set; } = [];
    public IReadOnlyList<SpeakerMapEntry> SpeakerMap { get; private set; } = [];

    /// <summary>speaker_map entries whose name is null — the ones needing naming.</summary>
    public IEnumerable<SpeakerMapEntry> Unnamed =>
        SpeakerMap.Where(e => e.Name is null);

    /// <summary>
    /// Loads <c>transcript.json</c> from <paramref name="sessionFolder"/>.
    /// Returns null if the file is missing or cannot be parsed.
    /// </summary>
    public static TranscriptInfo? Load(string sessionFolder)
    {
        var path = Path.Combine(sessionFolder, "transcript.json");
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<TranscriptInfo>(File.ReadAllText(path), Options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"TeamsRecorder: failed to parse transcript.json ({ex.Message}).");
            return null;
        }
    }
}
