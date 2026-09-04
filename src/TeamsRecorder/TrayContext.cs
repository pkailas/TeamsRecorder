using System.Diagnostics;
using System.Drawing.Drawing2D;

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
    private DateTime _startedAt;
    private Icon? _recordingIcon;

    private static readonly Icon IdleIcon = SystemIcons.Application;

    public TrayContext()
    {
        _settings = Settings.Load();

        // --- Context menu ---
        _startItem = new ToolStripMenuItem("Start recording");
        _startItem.Click += (_, _) => StartRecording();

        _stopItem = new ToolStripMenuItem("Stop recording");
        _stopItem.Click += (_, _) => StopRecording();

        var openFolderItem = new ToolStripMenuItem("Open recordings folder");
        openFolderItem.Click += (_, _) => OpenRecordingsFolder();

        var settingsItem = new ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => OpenSettings();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(openFolderItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        // --- NotifyIcon ---
        _notifyIcon = new NotifyIcon
        {
            Icon = IdleIcon,
            ContextMenuStrip = menu,
            Visible = true,
            Text = "Teams Recorder — idle",
        };
        _notifyIcon.DoubleClick += (_, _) => ToggleRecording();

        // --- Hotkey ---
        _hotkeys.HotkeyPressed += ToggleRecording;
        if (!_hotkeys.TryRegister(_settings.Hotkey, out var hotkeyError))
        {
            if (hotkeyError is not null)
                MessageBox.Show(hotkeyError, "Teams Recorder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

    private void StopRecording()
    {
        if (!_recorder.IsRecording)
            return;

        try
        {
            var (micWav, loopbackWav) = _recorder.Stop();
            UpdateIcon(recording: false);
            _startItem.Enabled = true;
            _stopItem.Enabled = false;
            ShowBalloon("Recording stopped",
                "WAV files written. " + (_settings.AutoTranscribe ? "Transcription starting…" : ""));

            if (_settings.AutoTranscribe && _recorder.SessionFolder is { } folder)
            {
                SidecarRunner.TryStart(_settings, micWav, loopbackWav, folder, (exitCode, error) =>
                {
                    if (exitCode == 0)
                        ShowBalloon("Transcript ready", Path.Combine(folder, "transcripts"));
                    else
                        ShowBalloon("Transcription failed — see sidecar.log",
                            error ?? $"Exit code {exitCode}");
                });
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
        _recordingIcon?.Dispose();
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

    /// <summary>
    /// Idle icon = SystemIcons.Application. While recording, draw a 16×16 red
    /// circle at runtime so no icon resource files are needed.
    /// </summary>
    private void UpdateIcon(bool recording)
    {
        _recordingIcon?.Dispose();
        _recordingIcon = null;

        if (recording)
        {
            using var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var brush = new SolidBrush(Color.Red);
                g.FillEllipse(brush, 1, 1, 14, 14);
            }

            // The bitmap must stay alive for the lifetime of the icon handle.
            var hIcon = bmp.GetHicon();
            _recordingIcon = Icon.FromHandle(hIcon);
            _notifyIcon.Icon = _recordingIcon;
        }
        else
        {
            _notifyIcon.Icon = IdleIcon;
        }
    }

    private void ShowBalloon(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(3000);
    }
}
