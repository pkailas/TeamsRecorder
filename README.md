# TeamsRecorder

A Windows tray app that records the audio of a Microsoft Teams meeting on this
PC as **two WAV files**:

- `mic.wav` — the microphone (you, the local user)
- `loopback.wav` — system loopback (everyone else, i.e. what is playing)

Both files are written as **16 kHz / 16-bit / mono** WAV. When recording stops,
the app launches a Python sidecar (`sidecar/transcribe.py`) to transcribe them.
The sidecar is a separate component — this repo only launches it.

## Sidecar setup

The transcription sidecar lives in `sidecar/` (faster-whisper ASR + pyannote
speaker diarization, running on the NVIDIA GPU). One-time setup:

```powershell
.\sidecar\setup.ps1
```

This is idempotent: it creates `sidecar\.venv`, installs the CUDA torch build
(verified to support the RTX PRO 6000 Blackwell `sm_120`), the rest of the
Python dependencies from `sidecar/requirements.txt`, then runs
`transcribe.py --selftest` (must print `SELFTEST PASS`). Model weights go to
`G:\models` (see `HF_TOKEN` user env var and `sidecar/README.md`).

Per-recording behaviour, output formats, and Blackwell caveats are documented
in [sidecar/README.md](sidecar/README.md).

## Naming speakers

After a recording stops, the sidecar diarizes everyone who spoke and writes a
transcript. Known voices are matched automatically against the enrolled speaker
store (see the "Speaker store" section of [sidecar/README.md](sidecar/README.md));
speakers with no match are labelled `Speaker 1`, `Speaker 2`, …

When transcription finishes and the transcript has unnamed speakers, the app
opens a **Name speakers** dialog: each row shows the speaker's label, how long
they talked, a sample of what they said, and a name box with autocomplete from
the enrolled names (the sidecar's best guess, if any, appears as grey
placeholder text). Entering a name and saving:

1. Renames the speaker throughout `transcript.json` and `transcript.md` by
   running the sidecar's `rename` subcommand (no GPU needed), and
2. Enrolls the speaker's voice in `speakers.json`, so next time the same person
   is recognized automatically.

If every speaker was already recognized, a "Transcript ready" balloon is shown
instead. The right-click menu also has:

- **Name speakers in last recording…** — reopens the dialog for the most recent
  session (disabled until a session has a transcript).
- **Open last transcript** — opens `transcript.md` in the default app.

## Publish (recommended way to run it)

```powershell
.\publish.ps1
```

Publishes a self-contained build to `%LOCALAPPDATA%\TeamsRecorder\app\`, stops any running
instance, re-points the Startup shortcut, and relaunches. The published copy finds the sidecar
through `repo.path` next to the exe, so the repo can be rebuilt freely while the app runs.

## Build

Requires the .NET 10 SDK and Windows.

```powershell
dotnet build
# or run directly:
dotnet run --project src\TeamsRecorder
```

The only NuGet dependency is `NAudio` 2.2.1.

## Run

- An icon appears in the notification area (system tray).
- **Double-click** the icon, or use the right-click menu:
  - **Start recording** / **Stop recording** — or press the hotkey.
  - **Open recordings folder** — opens Explorer at the output folder.
  - **Settings…** — opens the settings JSON in Notepad.
  - **Exit** — stops any in-progress recording and quits.
- Global hotkey (default **Ctrl+Alt+R**) toggles start/stop from anywhere.
- While recording, the tray icon turns into a red circle and the tooltip shows
  elapsed time. A balloon tip is shown on start and on stop.

## Where files go

Each recording creates one session folder:

```
<OutputFolder>\<yyyy-MM-dd_HHmm>\
    mic.wav
    loopback.wav
    sidecar.log        # after transcription (stdout/stderr of the sidecar)
```

Default `OutputFolder` is `%USERPROFILE%\Recordings\Teams`.

## Settings

The settings file lives at `%LOCALAPPDATA%\TeamsRecorder\settings.json`
(created with defaults on first run). Edit it via **Settings…** (Notepad).

| Field                | Default                                             | Meaning                                                        |
| -------------------- | --------------------------------------------------- | -------------------------------------------------------------- |
| `OutputFolder`       | `%USERPROFILE%\Recordings\Teams`                    | Root folder for session sub-folders.                            |
| `PythonExe`          | `<repo>\sidecar\.venv\Scripts\python.exe`           | Python interpreter used to launch the sidecar.                  |
| `SidecarScript`      | `<repo>\sidecar\transcribe.py`                      | The transcription script.                                       |
| `Hotkey`             | `Ctrl+Alt+R`                                        | Global toggle hotkey (`Ctrl+Alt+Shift+Win+<key>`).              |
| `MicDeviceName`      | `null`                                              | Substring match on the capture device's FriendlyName; null = default Communications capture device. |
| `LoopbackDeviceName` | `null`                                              | Substring match on the render device's FriendlyName; null = default render device. |
| `AutoTranscribe`     | `true`                                              | Launch the sidecar automatically after Stop.                    |

`<repo>` is the directory containing `src\` and `sidecar\` — resolved at
runtime as two directories above the executable when running from `bin\`
(see `Settings.ResolveRepoRoot`).

## Notes & gotchas

- **Loopback silence:** `WasapiLoopbackCapture` delivers no audio callbacks
  while nothing is playing, which would make `loopback.wav` drift out of sync
  with `mic.wav`. The recorder plays a silent stream on the same render device
  for the whole session to force continuous loopback frames.
- **Devices:** WASAPI shared-mode capture; the mic uses the default
  *Communications* endpoint so it picks up Teams' echo-cancelling mic path.
  Named-device settings are matched case-insensitively against
  `MMDevice.FriendlyName` (contains match).
- **>2 channel capture:** a >2-channel capture format takes channel 0 via
  `MultiplexingSampleProvider` instead of mixing everything into mono.
- The sidecar runs fire-and-forget; a balloon tip reports "Transcript ready"
  (exit 0) or "Transcription failed — see sidecar.log" (non-zero). When the
  transcript contains unnamed speakers, the **Name speakers** dialog opens
  instead (see [Naming speakers](#naming-speakers)).
- Only one instance runs at a time (named mutex `Global\TeamsRecorder`).

- **Use a headset.** With open speakers the microphone also hears the far end, so the "Paul" track duplicates what everyone else said. A headset (or any mic that doesn't pick up the speakers) keeps the two tracks clean.
- Verified live on 2026-09-04: hotkey → two-track capture → sidecar → diarized transcript → naming dialog, end to end on BEAST.

## Start with Windows / Start Menu

The right-click tray menu has two items for launching and discovering the app:

- **Start with Windows** — creates or deletes a shortcut at
  `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\TeamsRecorder.lnk`
  (per-user; no admin needed), so the app starts automatically at sign-in.
  The check mark is refreshed every time the menu opens, so it always reflects
  the current state.
- **Create Start Menu shortcut** — writes
  `%PROGRAMS%\TeamsRecorder\TeamsRecorder.lnk` (the per-user Programs folder,
  no admin needed) so the app is findable in the Start Menu; a balloon tip
  confirms it.

Both shortcuts are built by `src/TeamsRecorder/Shortcuts.cs` via the late-bound
`WScript.Shell` COM object — no extra packages. On every launch the app also
re-writes the Start Menu shortcut if its target no longer matches the running
exe (e.g. after a rebuild), so the Programs entry never goes stale.
