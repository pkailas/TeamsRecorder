using System.Diagnostics;

namespace TeamsRecorder;

/// <summary>
/// ApplicationContext that owns the NotifyIcon, the Recorder, the hotkey manager,
/// and the 1 s timer that refreshes the tray tooltip.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly Recorder _recorder = new();
    private readonly HotkeyManager _hotkeys = new();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly NotifyIcon _notifyIcon;
    private readonly Settings _settings;
    private readonly ToolStripMenuItem _nameSpeakersItem;
    private readonly ToolStripMenuItem _retranscribeItem;
    private readonly ToolStripMenuItem _openMeetingItem;
    private readonly ToolStripMenuItem _openTranscriptItem;
    private readonly ToolStripMenuItem _startWithWindowsItem;
    private DateTime _startedAt;
    private WindowCapture? _videoCapture;

    /// <summary>
    /// The SynchronizationContext of the UI thread (set in the constructor while
    /// still on it), used to marshal sidecar callbacks back to the UI thread.
    /// </summary>
    private readonly SynchronizationContext? _uiContext;

    /// <summary>Most recent session that reached a finished transcription.</summary>
    private string? _lastSessionFolder;
    private TranscriptInfo? _lastTranscript;

    /// <summary>True while a sidecar transcription is running (auto or manual).</summary>
    private bool _transcriptionRunning;

    /// <summary>
    /// File to open when the "Transcript ready" balloon is clicked (set by that
    /// balloon; cleared on click and by every other balloon). Null = no action.
    /// </summary>
    private string? _pendingOpen;

    // Both icons are embedded from assets\ (see csproj). Idle = grey dot, recording = red dot.
    private static readonly Icon IdleIcon = LoadEmbeddedIcon("TeamsRecorder.idle.ico");
    private static readonly Icon RecordingIcon = LoadEmbeddedIcon("TeamsRecorder.rec.ico");

    private static Icon LoadEmbeddedIcon(string name)
    {
        using var stream = typeof(TrayContext).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded icon '{name}' not found.");
        return new Icon(stream, 16, 16);
    }

    public TrayContext()
    {
        _settings = Settings.Load();

        // --- Context menu ---
        _startItem = new ToolStripMenuItem("Start recording");
        _startItem.Click += (_, _) => StartRecording();

        _stopItem = new ToolStripMenuItem("Stop recording");
        _stopItem.Click += (_, _) => StopRecording();

        _nameSpeakersItem = new ToolStripMenuItem("Name speakers in last recording…")
        {
            Enabled = false,
        };
        _nameSpeakersItem.Click += (_, _) => NameSpeakersInLastRecording();

        _retranscribeItem = new ToolStripMenuItem("Re-transcribe last recording")
        {
            Enabled = false,
        };
        _retranscribeItem.Click += (_, _) => RetranscribeLastRecording();

        _openMeetingItem = new ToolStripMenuItem("Open last meeting")
        {
            Enabled = false,
        };
        _openMeetingItem.Click += (_, _) => OpenLastMeeting();

        _openTranscriptItem = new ToolStripMenuItem("Open last transcript")
        {
            Enabled = false,
        };
        _openTranscriptItem.Click += (_, _) => OpenLastTranscript();

        var openFolderItem = new ToolStripMenuItem("Open recordings folder");
        openFolderItem.Click += (_, _) => OpenRecordingsFolder();

        _startWithWindowsItem = new ToolStripMenuItem("Start with Windows");
        _startWithWindowsItem.Click += (_, _) => ToggleStartWithWindows();

        var createShortcutItem = new ToolStripMenuItem("Create Start Menu shortcut");
        createShortcutItem.Click += (_, _) => CreateStartMenuShortcut();

        var settingsItem = new ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => OpenSettings();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();

        var menu = new ContextMenuStrip();
        // Keep the "Start with Windows" check state current every time the menu opens.
        menu.Opening += (_, _) => _startWithWindowsItem.Checked = Shortcuts.StartupEnabled;
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_nameSpeakersItem);
        menu.Items.Add(_retranscribeItem);
        menu.Items.Add(_openMeetingItem);
        menu.Items.Add(_openTranscriptItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(openFolderItem);
        menu.Items.Add(_startWithWindowsItem);
        menu.Items.Add(createShortcutItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        // Captured in the constructor (on the UI thread) for marshaling sidecar callbacks.
        _uiContext = SynchronizationContext.Current;

        // --- NotifyIcon ---
        _notifyIcon = new NotifyIcon
        {
            Icon = IdleIcon,
            ContextMenuStrip = menu,
            Visible = true,
            Text = "Teams Recorder — idle",
        };
        _notifyIcon.DoubleClick += (_, _) => ToggleRecording();
        _notifyIcon.BalloonTipClicked += OnBalloonTipClicked;

        // Make sure the Start Menu (Programs) entry points at this exe, even
        // after a rebuild moved it. Failures are silent — this is best-effort.
        try { Shortcuts.EnsureStartMenuShortcut(); }
        catch { /* best-effort on startup */ }

        // --- Hotkey ---
        _hotkeys.HotkeyPressed += ToggleRecording;
        if (!_hotkeys.TryRegister(_settings.Hotkey, out var hotkeyError))
        {
            if (hotkeyError is not null)
                MessageBox.Show(hotkeyError, "Teams Recorder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // --- Restore the most recent session across app restarts/crashes ---
        _lastSessionFolder = FindLastSessionFolder(_settings.OutputFolder);
        if (_lastSessionFolder is { } lastFolder)
        {
            _lastTranscript = TranscriptInfo.Load(lastFolder);
            _nameSpeakersItem.Enabled = _lastTranscript is { } t && t.Unnamed.Any();
            _retranscribeItem.Enabled = true;
            _openTranscriptItem.Enabled = File.Exists(Path.Combine(lastFolder, "transcript.md"));
            _openMeetingItem.Enabled = File.Exists(Path.Combine(lastFolder, "meeting.html"));
        }

        // --- 1 s timer for the tooltip ---
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => UpdateTooltip();
        _timer.Start();
    }

    private void ToggleRecording()
    {
        if (_recorder.IsRecording)
            StopRecording();
        else
            StartRecording();
    }

    private void StartRecording()
    {
        if (_recorder.IsRecording)
            return;

        try
        {
            _recorder.Start(_settings);
            _startedAt = DateTime.Now;

            // --- Video capture (best-effort; audio is already running) ---
            if (_settings.VideoOn)
                StartVideoCapture();
            UpdateIcon(recording: true);
            _startItem.Enabled = false;
            _stopItem.Enabled = true;
            ShowBalloon("Recording started",
                $"Recording to {_recorder.SessionFolder}. Press {_settings.Hotkey} to stop.");
        }
        catch (Exception ex)
        {
            UpdateIcon(recording: false);
            _startItem.Enabled = true;
            _stopItem.Enabled = false;
            MessageBox.Show(ex.Message, "Teams Recorder — start failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Starts window video capture for the active session (best-effort). Any failure is
    /// logged + reported with a balloon; audio recording is never affected.
    /// </summary>
    private void StartVideoCapture()
    {
        try
        {
            var sessionFolder = _recorder.SessionFolder
                ?? throw new InvalidOperationException("No active session folder.");

            var (hwnd, title, candidates) = WindowCapture.FindTeamsMeetingWindowCore();

            // Log every candidate ms-teams window (title, hwnd, size) and which one was
            // chosen. The meeting-window title pattern is unverified, so capture the real
            // titles to recorder.log after the first live meeting.
            foreach (var c in candidates)
                LogVideo($"candidate ms-teams window: hwnd=0x{c.Hwnd:X} {c.W}x{c.H} \"{c.Title}\"", "recorder.log");
            LogVideo($"chosen meeting window: hwnd=0x{hwnd:X} \"{title}\"", "recorder.log");

            if (hwnd == IntPtr.Zero)
            {
                LogVideo("No ms-teams window found; video capture skipped.", "recorder.log");
                ShowBalloon("Video capture unavailable", "No Teams window found. Audio recording continues.");
                return;
            }

            var videoPath = Path.Combine(sessionFolder, "video.mp4");
            _videoCapture = new WindowCapture();
            _videoCapture.Log += (line) =>
            {
                Debug.WriteLine(line);
                AppendVideoLog(sessionFolder, line, "video.log");
            };
            _videoCapture.Start(hwnd, videoPath, _settings);
        }
        catch (Exception ex)
        {
            // Video capture failed — audio recording continues.
            Debug.WriteLine($"Video capture failed: {ex}");
            try { _videoCapture?.Dispose(); } catch { /* best effort */ }
            _videoCapture = null;
            ShowBalloon("Video capture unavailable", ex.Message);
        }
    }

    /// <summary>Stops + disposes the active video capture (no-op if none).</summary>
    private void StopVideoCapture()
    {
        if (_videoCapture is null)
            return;
        try
        {
            _videoCapture.Stop();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Video stop failed: {ex.Message}");
        }
        finally
        {
            try { _videoCapture.Dispose(); } catch { /* best effort */ }
            _videoCapture = null;
        }
    }

    /// <summary>Writes a video log line to Debug and to &lt;session&gt;\&lt;fileName&gt;.</summary>
    private void LogVideo(string line, string fileName = "video.log")
    {
        Debug.WriteLine(line);
        if (_recorder.SessionFolder is { } folder)
            AppendVideoLog(folder, line, fileName);
    }

    private static void AppendVideoLog(string sessionFolder, string line, string fileName)
    {
        try
        {
            File.AppendAllText(Path.Combine(sessionFolder, fileName),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {line}{Environment.NewLine}");
        }
        catch
        {
            // Never let logging take down the capture.
        }
    }

    private void StopRecording()
    {
        if (!_recorder.IsRecording)
            return;

        try
        {
            // Stop video before audio so both end at the same wall-clock moment.
            StopVideoCapture();

            var (micWav, loopbackWav) = _recorder.Stop();
            UpdateIcon(recording: false);
            _startItem.Enabled = true;
            _stopItem.Enabled = false;
            ShowBalloon("Recording stopped",
                "WAV files written. " + (_settings.AutoTranscribe ? "Transcription starting…" : ""));

            if (_settings.AutoTranscribe && _recorder.SessionFolder is { } folder)
            {
                StartTranscription(micWav, loopbackWav, folder);
            }
        }
        catch (Exception ex)
        {
            UpdateIcon(recording: false);
            _startItem.Enabled = true;
            _stopItem.Enabled = false;
            MessageBox.Show(ex.Message, "Teams Recorder — stop failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenRecordingsFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.OutputFolder);
            Process.Start(new ProcessStartInfo
            {
                FileName = _settings.OutputFolder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Teams Recorder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ToggleStartWithWindows()
    {
        var enable = !Shortcuts.StartupEnabled;
        try
        {
            Shortcuts.SetStartup(enable);
            _startWithWindowsItem.Checked = enable;
            ShowBalloon("Start with Windows",
                enable ? "Teams Recorder will start automatically when you sign in." :
                          "Teams Recorder will no longer start automatically.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Teams Recorder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void CreateStartMenuShortcut()
    {
        try
        {
            Shortcuts.EnsureStartMenuShortcut();
            ShowBalloon("Shortcut created", "Teams Recorder is available in the Start Menu.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Teams Recorder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenSettings()
    {
        try
        {
            _settings.Save(); // make sure the file exists with current values
            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{Settings.SettingsPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Teams Recorder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ExitApp()
    {
        if (_recorder.IsRecording)
        {
            var result = MessageBox.Show(
                "A recording is in progress. Stop it and exit?",
                "Teams Recorder",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (result == DialogResult.Yes)
                StopRecording();
            else
                return;
        }

        _timer.Stop();
        _hotkeys.Dispose();
        _recorder.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        Application.Exit();
    }

    private void UpdateTooltip()
    {
        if (_recorder.IsRecording)
        {
            var elapsed = DateTime.Now - _startedAt;
            _notifyIcon.Text = $"Teams Recorder — recording ({(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2})";
        }
        else
        {
            _notifyIcon.Text = "Teams Recorder — idle";
        }
    }

    /// <summary>Swap the tray icon between the idle (grey dot) and recording (red dot) variants.</summary>
    private void UpdateIcon(bool recording) => _notifyIcon.Icon = recording ? RecordingIcon : IdleIcon;

    private void ShowBalloon(string title, string message)
    {
        // Every balloon other than the "Transcript ready" meeting-page one has
        // no click action: clear the pending open (that balloon re-sets it
        // after this returns).
        _pendingOpen = null;
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(3000);
    }

    /// <summary>
    /// A balloon was clicked: opens the pending file if the "Transcript ready"
    /// balloon set one (meeting.html); all other balloons leave it null.
    /// </summary>
    private void OnBalloonTipClicked(object? sender, EventArgs e)
    {
        var path = _pendingOpen;
        _pendingOpen = null;
        if (path is null)
            return;
        OpenInShell(path);
    }

    /// <summary>
    /// Starts the sidecar transcription for <paramref name="folder"/> and tracks the
    /// run with <see cref="_transcriptionRunning"/> (which disables the
    /// "Re-transcribe last recording" menu item until the completion callback fires).
    /// Used by both the post-recording flow and the manual re-transcribe item.
    /// </summary>
    private void StartTranscription(string micWav, string loopbackWav, string folder)
    {
        _transcriptionRunning = true;
        _retranscribeItem.Enabled = false;
        ShowBalloon("Transcribing…", folder);
        SidecarRunner.TryStart(_settings, micWav, loopbackWav, folder, (exitCode, error) =>
        {
            if (exitCode == 0)
                OnTranscriptionFinished(folder, error);
            else
            {
                _uiContext?.Post(_ =>
                {
                    _transcriptionRunning = false;
                    _retranscribeItem.Enabled = _lastSessionFolder is not null;
                    ShowBalloon("Transcription failed — see sidecar.log",
                        error ?? $"Exit code {exitCode}");
                }, null);
            }
        });
    }

    /// <summary>
    /// Finds the newest subfolder of <paramref name="outputFolder"/> that contains
    /// both <c>mic.wav</c> and <c>loopback.wav</c>, or null if none exists.
    /// Used at startup to restore the "last session" after an app restart or crash.
    /// </summary>
    private static string? FindLastSessionFolder(string outputFolder)
    {
        try
        {
            if (!Directory.Exists(outputFolder))
                return null;

            return Directory.GetDirectories(outputFolder)
                .Where(d => File.Exists(Path.Combine(d, "mic.wav"))
                          && File.Exists(Path.Combine(d, "loopback.wav")))
                .OrderByDescending(Directory.GetLastWriteTime)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sidecar finished with exit 0 (invoked on a thread-pool thread by
    /// <see cref="SidecarRunner.TryStart"/>). Loads the transcript and either
    /// opens the speaker-naming dialog (unnamed speakers present) or balloons
    /// "Transcript ready". Marshals to the UI thread via the SynchronizationContext
    /// captured in the constructor. <paramref name="sidecarError"/> is null on a
    /// clean exit and is ignored here.
    /// </summary>
    private void OnTranscriptionFinished(string sessionFolder, string? sidecarError)
    {
        _ = sidecarError;
        var info = TranscriptInfo.Load(sessionFolder);
        if (info is null)
        {
            ShowReadyBalloon(sessionFolder);
            return;
        }

        // Remember this session for the context-menu items.
        _lastSessionFolder = sessionFolder;
        _lastTranscript = info;
        _uiContext?.Post(_ =>
        {
            _transcriptionRunning = false;
            _nameSpeakersItem.Enabled = info.Unnamed.Any();
            _retranscribeItem.Enabled = true;
            var md = Path.Combine(sessionFolder, "transcript.md");
            _openTranscriptItem.Enabled = File.Exists(md);
            _openMeetingItem.Enabled = File.Exists(Path.Combine(sessionFolder, "meeting.html"));
        }, null);

        if (info.Unnamed.Any())
        {
            var snapshot = info;
            _uiContext?.Post(_ => ShowNameSpeakersDialog(snapshot, sessionFolder), null);
        }
        else
        {
            ShowReadyBalloon(sessionFolder);
        }
    }

    /// <summary>
    /// Shows the speaker-naming dialog on the UI thread; on Save runs the sidecar's
    /// rename subcommand in the background and reports the result via balloon.
    /// </summary>
    private void ShowNameSpeakersDialog(TranscriptInfo info, string sessionFolder)
    {
        using var form = new NameSpeakersForm(sessionFolder, info);
        if (form.ShowDialog() != DialogResult.OK)
            return;

        if (form.Assignments.Count == 0)
        {
            ShowBalloon("Speakers saved", "No names entered.");
            return;
        }

        ShowBalloon("Naming speakers…", $"{form.Assignments.Count} speaker(s) — running sidecar rename.");
        _ = Task.Run(async () =>
        {
            var exitCode = await SidecarRunner.RenameAsync(_settings, sessionFolder, form.Assignments);
            _uiContext?.Post(_ =>
            {
                if (exitCode == 0)
                {
                    // Re-read so the menu items reflect the renamed transcript.
                    var reloaded = TranscriptInfo.Load(sessionFolder);
                    if (reloaded is not null)
                        _lastTranscript = reloaded;
                    _nameSpeakersItem.Enabled = _lastTranscript is { } t && t.Unnamed.Any();
                    var meeting = Path.Combine(sessionFolder, "meeting.html");
                    _openMeetingItem.Enabled = File.Exists(meeting);
                    // The freshly renamed result is what the user just asked for: show it.
                    if (File.Exists(meeting))
                        OpenInShell(meeting);
                    ShowBalloon("Speakers saved", $"Renamed {form.Assignments.Count} speaker(s). Voice enrolled.");
                }
                else
                {
                    ShowBalloon("Rename failed — see sidecar.log",
                        Path.Combine(sessionFolder, "sidecar.log"));
                }
            }, null);
        });
    }

    /// <summary>Reopens the naming dialog for the most recent session (context menu).</summary>
    private void NameSpeakersInLastRecording()
    {
        if (_lastSessionFolder is not { } folder || _lastTranscript is not { } info)
            return;

        using var form = new NameSpeakersForm(folder, info);
        if (form.ShowDialog() != DialogResult.OK)
            return;

        if (form.Assignments.Count == 0)
        {
            ShowBalloon("Speakers saved", "No names entered.");
            return;
        }

        ShowBalloon("Naming speakers…", $"{form.Assignments.Count} speaker(s) — running sidecar rename.");
        _ = Task.Run(async () =>
        {
            var exitCode = await SidecarRunner.RenameAsync(_settings, folder, form.Assignments);
            _uiContext?.Post(_ =>
            {
                if (exitCode == 0)
                {
                    var reloaded = TranscriptInfo.Load(folder);
                    if (reloaded is not null)
                        _lastTranscript = reloaded;
                    _nameSpeakersItem.Enabled = _lastTranscript is { } t && t.Unnamed.Any();
                    var meeting = Path.Combine(folder, "meeting.html");
                    _openMeetingItem.Enabled = File.Exists(meeting);
                    // The freshly renamed result is what the user just asked for: show it.
                    if (File.Exists(meeting))
                        OpenInShell(meeting);
                    ShowBalloon("Speakers saved", $"Renamed {form.Assignments.Count} speaker(s). Voice enrolled.");
                }
                else
                {
                    ShowBalloon("Rename failed — see sidecar.log",
                        Path.Combine(folder, "sidecar.log"));
                }
            }, null);
        });
    }

    /// <summary>Re-runs the sidecar transcription on the last session's WAV files (context menu).</summary>
    private void RetranscribeLastRecording()
    {
        if (_lastSessionFolder is not { } folder)
            return;

        if (_transcriptionRunning)
        {
            ShowBalloon("Transcription already running", "Please wait for it to finish.");
            return;
        }

        StartTranscription(
            Path.Combine(folder, "mic.wav"),
            Path.Combine(folder, "loopback.wav"),
            folder);
    }

    /// <summary>
    /// Shows the "Transcript ready" balloon; if the session has a meeting page,
    /// the balloon offers to open it (click -> meeting.html via _pendingOpen).
    /// </summary>
    private void ShowReadyBalloon(string sessionFolder)
    {
        var meeting = Path.Combine(sessionFolder, "meeting.html");
        ShowBalloon("Transcript ready",
            File.Exists(meeting) ? "Click to open the meeting page" : sessionFolder);
        // Set after ShowBalloon returns: it clears _pendingOpen for the other
        // balloons, so the ready balloon wins.
        _pendingOpen = File.Exists(meeting) ? meeting : null;
    }

    /// <summary>Opens a file with its default app via the shell; failures show a message box.</summary>
    private static void OpenInShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Teams Recorder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Opens the most recent meeting.html in the default browser (context menu).</summary>
    private void OpenLastMeeting()
    {
        if (_lastSessionFolder is not { } folder)
            return;

        var html = Path.Combine(folder, "meeting.html");
        if (!File.Exists(html))
            return;

        OpenInShell(html);
    }

    /// <summary>Opens the most recent transcript.md with the default app (context menu).</summary>
    private void OpenLastTranscript()
    {
        if (_lastSessionFolder is not { } folder)
            return;

        var md = Path.Combine(folder, "transcript.md");
        if (!File.Exists(md))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = md,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Teams Recorder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
