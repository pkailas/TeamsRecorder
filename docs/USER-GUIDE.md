# Teams Recorder – User Guide

*Version 1.0 · September 2026*

Teams Recorder is a small Windows tray application that records the audio of any Microsoft Teams meeting you are in, transcribes it locally on your PC, and produces a transcript with each line attributed to a named speaker. Nothing leaves your machine.

## 1. What you get

- A transcript of every meeting you choose to record, as Markdown (`transcript.md`) and JSON (`transcript.json`), plus the original audio.
- Speaker names on every line. Your own voice is always labelled with your name; other participants are recognized automatically after you have named them once.
- Everything stored locally under `Recordings\Teams` in your user profile — one folder per meeting.

## 2. Starting the app

Teams Recorder lives in the system tray (the icons near the clock). Its icon is a white microphone on an indigo tile.

- **Start menu:** Start → TeamsRecorder.
- **Automatically at sign-in:** right-click the tray icon → tick **Start with Windows**.

If the icon is hidden, click the small **^** arrow in the tray to reveal it, or drag it out so it is always visible.

## 3. Recording a meeting

1. Join the Teams meeting as usual.
2. Press **Ctrl + Alt + R** (or right-click the tray icon → **Start recording**). A balloon confirms "Recording started" and the dot on the tray icon turns red.
3. When the meeting ends, press **Ctrl + Alt + R** again (or **Stop recording**). The dot turns grey.

Transcription starts automatically in the background and typically takes 1–2 minutes for a one-hour meeting. You can keep working; a balloon tells you when the transcript is ready.

> **Tip:** use a headset. With open speakers your microphone also hears the other participants, and your track will duplicate what they said.

## 4. Naming speakers

When a transcript contains voices the app does not recognize yet, a **Name speakers** window appears. For each unknown speaker it shows how long they talked and a sample of what they said.

- Type the person's name and click **Save**. The transcript is updated and that voice is remembered — next time it is labelled automatically.
- Leave a box empty to keep the generic "Speaker N" label for now.
- Click **Skip** to close without naming anyone. You can come back later via right-click → **Name speakers in last recording…**.

Names you have used before are offered as you type. If the app has a guess, it appears as grey placeholder text — type it to accept, or overwrite it.

## 5. Finding your transcripts

- Right-click the tray icon → **Open last transcript** opens the newest one.
- Right-click → **Open recordings folder** shows all meetings. Each folder is named by date and time, e.g. `2026-09-05_1030`, and contains:

| File | Contents |
|---|---|
| `transcript.md` | The readable transcript: `[hh:mm:ss] Name: what they said` |
| `transcript.json` | The same data for programs |
| `mic.wav` | Your microphone |
| `loopback.wav` | Everyone else |
| `sidecar.log` | Processing log — useful if something went wrong |

## 6. Settings

Right-click the tray icon → **Settings…** opens a small text file. Edit, save, and restart the app.

| Setting | Meaning | Default |
|---|---|---|
| `OutputFolder` | Where recordings are stored | `Recordings\Teams` in your profile |
| `Hotkey` | Start/stop key combination | `Ctrl+Alt+R` |
| `MicDeviceName` | Part of a microphone's name to use instead of the default | *(default device)* |
| `LoopbackDeviceName` | Part of a speaker/headset name to capture instead of the default | *(default device)* |
| `AutoTranscribe` | Transcribe automatically after stopping | `true` |

## 7. Troubleshooting

| Symptom | What to check |
|---|---|
| Hotkey does nothing | Another program owns Ctrl+Alt+R. Change `Hotkey` in Settings, or use the tray menu. |
| "Transcription failed" balloon | Open the meeting folder and read `sidecar.log`; the last lines say what failed. |
| Other participants missing from the transcript | Teams played through a different output device. Set `LoopbackDeviceName` to the device Teams uses. |
| Your own lines missing | Wrong microphone. Set `MicDeviceName`. |
| Wrong person named on a line | Two voices sounded alike. Use **Name speakers in last recording…** to correct it; the fix also improves future recognition. |
| Your lines echo what others said | Open speakers — switch to a headset. |

## 8. Privacy

Audio and transcripts stay on your PC. Recording other people may require their consent depending on where they are — say so at the start of the meeting.
