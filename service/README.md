# TeamsRecorder GPU transcription service

Runs the existing sidecar pipeline (`sidecar/transcribe.py`: faster-whisper
large-v3 + pyannote diarization + speaker embeddings) on **BEAUTY**'s GPU
(RTX PRO 4000 Blackwell, shared with the embedder) so BEAST no longer has to
transcribe on its own GPU.

* One job at a time, guarded by a lock — a second request **waits**, it does not
  get a 409.
* Each job runs `transcribe.py` in a **fresh subprocess**. When it exits, all GPU
  memory it held — including the CUDA context — is gone. Nothing stays resident
  between jobs; VRAM returns to the embedder baseline (~12.4 GB).
* Uploads are streamed to disk (no in-RAM 350 MB WAVs). Reply is a zip of the
  sidecar's output dir plus `sidecar.log`.
* No ffmpeg / muxing here: no `video.mp4` is sent, and the sidecar skips the mux
  cleanly ("no video.mp4; skipping mux"). Video stays on BEAST.

## Deploy

```bash
cd ~/teamsrecorder            # git pull from the NAS remote
cd service
docker compose up -d --build
curl http://10.0.0.129:8095/health
```

One-time: the offline model cache lives at `~/models/teamsrecorder/{whisper,hf}`
(CTranslate2 `models--Systran--faster-whisper-large-v3` + the pyannote HF hub
cache) and is mounted read-only at `/models`. Compose sets
`WHISPER_MODELS=/models/whisper HF_HOME=/models/hf HF_HUB_OFFLINE=1 HF_TOKEN=offline`,
so nothing ever touches the network.

## Endpoints

| | |
|---|---|
| `GET /health` | `{"status":"ok","gpu":{"name","free_mib","total_mib",...},"busy":bool}` — GPU probed with `nvidia-smi`, so the probe never allocates CUDA memory. |
| `POST /transcribe` | multipart: `mic` (wav), `loopback` (wav), `speakers` (`speakers.json`, optional), form field `language` (optional, default `en`). → `application/zip`: `transcript.json`, `transcript.md/.srt/.vtt`, `meeting.html`, `sidecar.log`. Sidecar exits non-zero → HTTP 500 with the log tail as JSON. The job temp dir is deleted after the response. |

Multipart part limit is 1 GiB per file (`LIMIT_FORM_PART_KIB`), enough for a
3-hour meeting (~350 MB per WAV).

## Example

```bash
curl.exe -F mic=@C:\Users\pkailas\Recordings\Teams\2026-10-27_1417\mic.wav `
         -F loopback=@C:\Users\pkailas\Recordings\Teams\2026-10-27_1417\loopback.wav `
         -F speakers=@%LOCALAPPDATA%\TeamsRecorder\speakers.json `
         http://10.0.0.129:8095/transcribe -o H:\tr-compare\svc.zip
```

`speakers.json` is the enrolled-speaker store copied from BEAST; without it the
diarized speakers come back as `Speaker 1..N` with embeddings in
`speaker_map`, ready for BEAST's `rename` step (which needs no GPU — it reuses
the embeddings stored in `transcript.json`).

## Files

| File | |
|---|---|
| `app.py` | FastAPI + uvicorn service |
| `Dockerfile` | python:3.13-slim, pinned torch 2.11.0/torchaudio 2.11.0 (cu128), faster-whisper 1.2.1, ctranslate2 4.8.2, pyannote.audio 4.0.7, soundfile 0.14.0, numpy 2.5.2, nvidia-cublas-cu12, nvidia-cudnn-cu12; runs as uid 1000 (`trsvc`) |
| `docker-compose.yml` | port 8095, all GPUs, `~/models/teamsrecorder:/models:ro`, `restart: unless-stopped` |
