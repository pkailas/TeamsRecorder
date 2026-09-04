namespace TeamsRecorder;

/// <summary>
/// WinForms dialog (built in code, no designer) for naming the unnamed speakers
/// in a session's transcript. One row per speaker with <c>name == null</c> in
/// <c>transcript.json</c>: label + talk time, a sample line of what they said,
/// and a name box with autocomplete from the enrolled speaker store. On Save the
/// non-empty boxes are exposed as <see cref="Assignments"/>; the caller then runs
/// the sidecar's <c>rename</c> subcommand (see sidecar/README.md).
/// </summary>
public sealed class NameSpeakersForm : Form
{
    private readonly TextBox[] _nameBoxes = [];
    private readonly string[] _labels = [];

    /// <summary>
    /// (Label, trimmed name) for every box the user filled in. Only valid when
    /// <see cref="DialogResult"/> is <see cref="DialogResult.OK"/>.
    /// </summary>
    public IReadOnlyList<(string Label, string Name)> Assignments { get; private set; } = [];

    public NameSpeakersForm(string sessionFolder, TranscriptInfo info)
    {
        _ = sessionFolder;

        Text = "Name speakers";
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        ClientSize = new Size(700, 400);
        Font = new Font("Segoe UI", 9f);

        var speakers = info.Unnamed.ToList();
        _labels = [.. speakers
            .Select((e, i) => e.Label ?? $"Speaker {i + 1}")];
        var knownNames = KnownSpeakers.LoadNames();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
            Margin = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));

        for (var row = 0; row < speakers.Count; row++)
        {
            var entry = speakers[row];
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // Column 1: label + talk time.
            var header = new Label
            {
                Text = _labels[row],
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(3, 3, 3, 0),
            };
            var talkTime = new Label
            {
                Text = FormatTalkTime(entry.TalkTimeSec),
                AutoSize = true,
                Margin = new Padding(3, 0, 3, 3),
            };
            var col1 = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };
            col1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            col1.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            col1.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            col1.Controls.Add(header, 0, 0);
            col1.Controls.Add(talkTime, 0, 1);

            // Column 2: sample text (read-only, word-wrapped, capped at ~3 lines).
            var sample = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(entry.SampleText) ? "" : entry.SampleText.Trim(),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.None,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Window,
                WordWrap = true,
                TabStop = false,
                Height = 48,
                Margin = new Padding(3, 3, 3, 3),
            };

            // Column 3: name box with autocomplete; best_candidate is a placeholder, not the value.
            var box = new TextBox
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.CustomSource,
                Margin = new Padding(3, 3, 3, 3),
            };
            foreach (var n in knownNames)
                box.AutoCompleteCustomSource?.Add(n);
            if (!string.IsNullOrWhiteSpace(entry.BestCandidate))
                box.PlaceholderText = entry.BestCandidate;

            layout.Controls.Add(col1, 0, row);
            layout.Controls.Add(sample, 1, row);
            layout.Controls.Add(box, 2, row);
            _nameBoxes[row] = box;
        }

        // Button row: Skip (cancel) left, Save (default) right.
        var skip = new Button
        {
            Text = "Skip",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Margin = new Padding(3),
        };
        var save = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            Margin = new Padding(3),
        };
        save.Click += Save_Click;
        CancelButton = skip;
        AcceptButton = save;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(12, 0, 12, 12),
        };
        buttons.Controls.Add(save);
        buttons.Controls.Add(skip);

        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
        };
        scroll.Controls.Add(layout);

        Controls.Add(scroll);
        Controls.Add(buttons);
    }

    private void Save_Click(object? sender, EventArgs e)
    {
        var assignments = new List<(string Label, string Name)>();
        for (var i = 0; i < _nameBoxes.Length; i++)
        {
            var name = _nameBoxes[i].Text.Trim();
            if (name.Length == 0)
                continue;

            if (name.Contains('='))
            {
                MessageBox.Show(
                    "Speaker names must not contain '='.",
                    "Teams Recorder — invalid name",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            assignments.Add((_labels[i], name));
        }

        Assignments = assignments;
        DialogResult = DialogResult.OK;
    }

    private static string FormatTalkTime(float seconds)
    {
        if (seconds < 60)
            return $"{(int)seconds}s";

        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}h {t.Minutes:D2}m";

        return $"{(int)t.TotalMinutes}m {t.Seconds:D2}s";
    }
}
