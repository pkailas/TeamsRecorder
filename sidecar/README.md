# Sidecar — Python transcription pipeline

Transcribes the two WAV files recorded by the tray app:

- `mic.wav` → the local user, always labelled **Paul**
- `loopback.wav` → everyone else; transcribed **and** diarized, then each word
  is assigned to the diarization turn it overlaps (or the nearest turn
  midpoint), producing `Speaker 1`, `Speaker 2`, … in order of first appearance.

  Recurring participants get named automatically: each diarized speaker's
  voice embedding (wespeaker-voxceleb-resnet34-LM, 256-D, L2-normalized) is
  matched by cosine similarity against the speaker store below, and labels
  with a score ≥ the match threshold (default 0.60) are labelled with the
  enrolled name instead of `Speaker N`.

Outputs (written into the session folder):

- `transcript.json` — machine-readable:

  ```json
  {
    "created": "2026-02-15T10:12:00",
    "model": "large-v3",
    "duration_sec": 1234.5,
    "speakers": ["Paul", "Speaker 1"],
    "utterances": [
      {"start": 1.2, "end": 3.4, "speaker": "Paul", "text": "..."}
    ],
    "diarization": [
      {"start": 0.5, "end": 2.0, "label": "SPEAKER_00"}
    ],
    "speaker_map": [
      {
        "label": "Speaker 1",
        "diarization_label": "SPEAKER_00",
        "name": null,
        "match_score": 0.42,
        "best_candidate": "Sean",
        "talk_time_sec": 12.3,
        "sample_text": "...longest utterance, ≤ 200 chars...",
        "embedding": [0.01, "...256 floats..."]
      }
    ]
  }
  ```

  `speaker_map` has one entry per diarized speaker (order of first appearance),
  so the C# app can build its speaker-naming dialog from it:
  `name`/`match_score` are `null` when no enrolled name matched (or when
  `--no-embeddings` was used), `best_candidate` is the closest stored name
  even below the threshold, and `embedding` is the L2-normalized 256-D voice
  vector.

  A `notes` array is added when a track is skipped (e.g. WAV shorter than 1 s).

- `transcript.md` — one line per utterance:

  ```
  **\[00:01:02\] Paul:** hello everyone
  **\[00:01:05\] Speaker 1:** hi Paul
  ```

The C# app launches:

```
sidecar\.venv\Scripts\python.exe sidecar\transcribe.py --mic <mic.wav> --loopback <loopback.wav> --out <session folder>
```

and redirects stdout/stderr to `sidecar.log`. Exit code 0 = transcript ready.

## Setup (one-time)

Run from the repo root:

```powershell
.\sidecar\setup.ps1
```

The script is idempotent. It:

1. Creates `sidecar\.venv` (Python 3.13 on PATH; falls back to `py -3.12` if a
   package has no 3.13 wheel), upgrades pip.
2. Installs `torch` + `torchaudio` from the CUDA 12.8 wheel index
   (`--index-url https://download.pytorch.org/whl/cu128`) and verifies
   `torch.cuda.get_arch_list()` contains **`sm_120`** (Blackwell). If not, it
   falls back to the `nightly/cu130` wheels and re-checks; if neither works it
   stops with a clear message.
3. Installs the rest from `requirements.txt`: `faster-whisper`, `pyannote.audio`,
   `nvidia-cublas-cu12`, `nvidia-cudnn-cu12`.
4. Runs `transcribe.py --selftest` and prints a clear PASS/FAIL.

Model weights land under `G:\models` (standing convention):

- Hugging Face (pyannote + silero VAD): `G:\models\hf` (`HF_HOME`)
- faster-whisper models: `G:\models\whisper` (`download_root`)

First selftest download is large (large-v3 ≈ 3 GB plus pyannote models ≈ 1 GB).

Prerequisites: NVIDIA driver ≥ 550 with CUDA-capable Blackwell support,
`HF_TOKEN` set as a user environment variable (needed for the gated
`pyannote/speaker-diarization-community-1` model).

## Usage

The tray app calls this automatically. Manual invocation:

```powershell
.\sidecar\.venv\Scripts\python.exe sidecar\transcribe.py `
    --mic .\sessions\mic.wav `
    --loopback .\sessions\loopback.wav `
    --out .\sessions
```

Options: `--model large-v3` (default), `--language en` (default),
`--selftest` (ignores the other args; generates a 4 s 440 Hz sine WAV, loads
**both** models on CUDA, runs transcription + diarization, prints versions,
then `SELFTEST PASS`), `--no-embeddings` (skip voice-embedding computation and
name matching), `--speakers-file PATH` (override the speaker store location),
`--match-threshold 0.60` (minimum cosine similarity to use an enrolled name).

## Speaker store (enrollment)

Named voices are stored in `%LOCALAPPDATA%\TeamsRecorder\speakers.json`
(override with `--speakers-file`), created on first write; a missing file
means "no known speakers":

```json
{
  "version": 1,
  "speakers": [
    { "name": "Sean", "embeddings": [["...256 floats..."], "..."], "updated": "2026-02-15T10:20:00" }
  ]
}
```

At most **5** embeddings are kept per name (oldest dropped first), so a voice
that drifts is gradually re-enrolled.

### Matching

During normal transcription, each diarized speaker's embedding is compared
(cosine similarity) against every stored embedding. The best (name, score) per
speaker is resolved **greedily by descending score** so two diarized speakers
never resolve to the same name — the loser falls back to `Speaker N` (numbering
counts only the unnamed ones, in order of first appearance). Names above the
threshold replace the `Speaker N` label in `utterances`, `speakers`,
`speaker_map` and `transcript.md`.

### Rename / enroll (no GPU needed)

```powershell
.\sidecar\.venv\Scripts\python.exe sidecar\transcribe.py rename `
    --session .\sessions `<dir>` `
    --assign "Speaker 1=Sean" `
    --assign "Speaker 2=Kevin" `
    [--no-enroll]            # skip appending embeddings to the store
    [--speakers-file PATH]
```

Loads `<session>\transcript.json`, renames each `OldLabel` to `NewName`
everywhere (`speakers`, `utterances[].speaker`, `speaker_map[].label/name`),
appends that speaker's `embedding` to the store under the new name (unless
`--no-enroll`), rewrites `transcript.json` and regenerates `transcript.md`
from the same writer used by normal mode. Exits 0/1 like the main mode.
`rename` does not import torch/pyannote/faster-whisper, so it starts fast.

## Pipeline details

1. `WhisperModel(model, device="cuda", compute_type="float16", download_root="G:\models\whisper")`
   — both WAVs are transcribed with `language=...`, `word_timestamps=True`,
   `vad_filter=True`.
2. Mic segments become `Paul` utterances directly (segment-level timestamps).
3. Loopback is transcribed at word level, then diarized with
   `pyannote/speaker-diarization-community-1`
   (`Pipeline.from_pretrained(..., token=os.environ["HF_TOKEN"]).to(torch.device("cuda"))`).
   We iterate `output.exclusive_speaker_diarization` (one speaker at a time;
   each item is `(turn, speaker)` with `turn.start`/`turn.end` in seconds and
   labels like `SPEAKER_00`).

   Both models receive the WAV as an in-memory numpy float32 mono 16 kHz
   waveform (read via soundfile) rather than a file path, because
   pyannote 4.x's file decoder (torchcodec) requires FFmpeg DLLs that are
   not present in the pip-installed torchcodec, and this also avoids any
   dependency on faster-whisper's own audio decoding path.
4. Each loopback word is assigned to the turn with the greatest overlap
   (nearest turn midpoint when nothing overlaps); consecutive words with the
   same speaker are merged into one utterance.
5. One embedding per diarized speaker is computed with
   `pyannote/wespeaker-voxceleb-resnet34-LM` via `Inference(window="whole")`
   (turns ≥ 0.5 s concatenated, up to 20 s; output L2-normalized → 256-D),
   then matched against the speaker store (cosine similarity, greedy
   assignment, threshold `--match-threshold`); `--no-embeddings` skips this.
6. Both streams are merged and sorted by start time; JSON (incl. `speaker_map`)
   + Markdown are written by one shared writer (also used by `rename`).
7. GPU memory is released at the end (`del model; torch.cuda.empty_cache()`).
8. WAVs shorter than 1 s are skipped gracefully and noted in the JSON.

## Blackwell (sm_120) caveats

- **Do NOT use `compute_type="int8"` or `"int8_float16"`** — ctranslate2's cuBLAS
  calls fail on sm_120 with `CUBLAS_STATUS_NOT_SUPPORTED`. `float16` is
  hard-coded in `transcribe.py` for this reason.
- The cu128 stable torch wheel may predate sm_120 support; `setup.ps1` checks
  `torch.cuda.get_arch_list()` and falls back to the cu130 nightly wheel when
  needed. If you upgrade torch manually, re-run:
  `python -c "import torch; print(torch.cuda.is_available(), torch.cuda.get_arch_list())"`
  and make sure `sm_120` is in the list.
- ctranslate2's CUDA build needs cuBLAS 12 + cuDNN 9 DLLs at runtime; the pip
  packages `nvidia-cublas-cu12` / `nvidia-cudnn-cu12` provide them and
  `transcribe.py` adds their `nvidia\*\bin` dirs (plus `torch\lib`) to the DLL
  search path before importing `faster_whisper`.
- On first run, pyannote downloads its sub-models (segmentation-3.0,
  embedding, clustering) to `G:\models\hf` — these are gated and require
  `HF_TOKEN`.
