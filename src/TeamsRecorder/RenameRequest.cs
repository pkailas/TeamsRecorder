namespace TeamsRecorder;

/// <summary>
/// A parsed <c>teamsrecorder://rename?session=&lt;folder&gt;&amp;assign=&lt;Label=Name&gt;</c>
/// URL, as produced by the "Save names" button on a session's meeting page.
/// </summary>
/// <param name="SessionFolder">Absolute path of the session folder to rename in.</param>
/// <param name="Assignments">One <c>(label, name)</c> pair per <c>assign</c> parameter.</param>
public sealed record RenameRequest(string SessionFolder, IReadOnlyList<(string label, string name)> Assignments)
{
    private const string Scheme = "teamsrecorder";
    private const string Verb = "rename";

    /// <summary>
    /// Parses a <c>teamsrecorder://rename?...</c> URL, or returns null when the URL is
    /// malformed, points at a session that does not exist, carries an assignment
    /// without a usable <c>Label=Name</c>, or falls outside the configured
    /// <see cref="Settings.OutputFolder"/>.
    /// </summary>
    /// <remarks>
    /// The URL arrives from a browser, so the session folder is validated against the
    /// recordings root: a page on disk must never be able to point the sidecar at an
    /// arbitrary folder.
    /// </remarks>
    public static RenameRequest? TryParse(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;

        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!string.Equals(uri.Host, Verb, StringComparison.OrdinalIgnoreCase))
            return null;

        string? session = null;
        var rawAssignments = new List<string>();

        // Manual query split: System.Web is not referenced, and Uri leaves the
        // percent-encoding in place for a custom scheme.
        var query = uri.Query;
        if (query.Length > 0)
            query = query[1..];

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
                continue;

            var key = Uri.UnescapeDataString(pair[..eq]);
            // Value stays raw here: each Label=Name is decoded in exactly one
            // pass below, so a name containing '%' survives intact.
            var value = pair[(eq + 1)..];

            if (string.Equals(key, "session", StringComparison.OrdinalIgnoreCase))
                session = Uri.UnescapeDataString(value);
            else if (string.Equals(key, "assign", StringComparison.OrdinalIgnoreCase))
                rawAssignments.Add(value);
        }

        if (session is null || rawAssignments.Count == 0)
            return null;

        var sessionFolder = Path.GetFullPath(session);
        if (!Directory.Exists(sessionFolder))
            return null;

        if (!IsUnderOutputFolder(sessionFolder))
            return null;

        var parsed = new List<(string label, string name)>(rawAssignments.Count);
        foreach (var raw in rawAssignments)
        {
            // Decode the whole value first, then split. The page encodes the entire
            // "Label=Name" pair, so the separator usually arrives as %3D and is only
            // visible after unescaping. A literal '=' inside a name must arrive as %3D
            // for the same reason: decoding first is what lets the check below see it.
            var decoded = Uri.UnescapeDataString(raw);

            var eq = decoded.IndexOf('=');
            if (eq < 0)
                return null;

            var label = decoded[..eq].Trim();
            var name = decoded[(eq + 1)..].Trim();

            // The label side is complete; the name side must not carry a further
            // '=' (same rule the Name speakers dialog enforces).
            if (label.Length == 0 || name.Length == 0 || name.Contains('='))
                return null;

            parsed.Add((label, name));
        }

        return new RenameRequest(sessionFolder, parsed);
    }

    /// <summary>
    /// True when <paramref name="sessionFolder"/> (already fully qualified) sits below
    /// <see cref="Settings.OutputFolder"/>. Case-insensitive, path-boundary aware:
    /// <c>...\TeamsExtra\x</c> is not under <c>...\Teams</c>.
    /// </summary>
    private static bool IsUnderOutputFolder(string sessionFolder)
    {
        var outputFolder = Path.GetFullPath(Settings.Load().OutputFolder);

        var relative = Path.GetRelativePath(outputFolder, sessionFolder);
        return !Path.IsPathRooted(relative)
            && relative != "."
            && !relative.StartsWith("..");
    }
}
