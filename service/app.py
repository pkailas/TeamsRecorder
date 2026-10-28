#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
TeamsRecorder GPU transcription service.

Runs the existing sidecar pipeline (sidecar/transcribe.py) on a CUDA GPU inside
a container, so BEAST does not have to transcribe on its own GPU.

Endpoints
  GET  /health      {"status":"ok","gpu":{...nvidia-smi check...},"busy":bool}
  POST /transcribe  multipart: mic (wav) + loopback (wav) + optional speakers
                    (speakers.json) + optional form field language (default en)
                    -> application/zip of the sidecar's out dir + sidecar.log
                    -> HTTP 500 with the log tail when the sidecar exits non-zero

Design notes
  * One job at a time: every job runs as a FRESH `python transcribe.py`
    subprocess guarded by a single asyncio lock. A second request simply waits
    on the lock (no 409). When the subprocess exits, ALL GPU memory it held -
    including the CUDA context - is released, which is the "unload" design:
    nothing is kept resident between jobs.
  * Uploads are streamed to disk (UploadFile spools >1 MB to a temp file, and
    the bytes are copied out in 1 MB chunks), so a 3-hour meeting
    (~350 MB per WAV) never lands in RAM.
  * No mux here: the service receives no video.mp4, and transcribe.py skips
    the mux cleanly when it is absent ("no video.mp4; skipping mux").
    Video muxing stays on BEAST.

Run (container): python -u /app/app.py   (env: HOST, PORT, LIMIT_FORM_PART_KIB,
                                            SIDECAR_DIR, TR_JOBS_DIR)
"""
from __future__ import annotations

import asyncio
import json
import logging
import os
import shutil
import subprocess
import sys
import tempfile
import time
import zipfile
from datetime import datetime
from io import BytesIO
from pathlib import Path

from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from fastapi.responses import JSONResponse, Response

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------
APP_DIR = Path(__file__).resolve().parent
SIDECAR_DIR = Path(os.environ.get("SIDECAR_DIR") or (APP_DIR / "sidecar"))
TRANSCRIBE_PY = SIDECAR_DIR / "transcribe.py"
JOBS_ROOT = Path(os.environ.get("TR_JOBS_DIR") or "/tmp")
HOST = os.environ.get("HOST", "0.0.0.0")
PORT = int(os.environ.get("PORT", "8095"))
# uvicorn's per-multipart-part cap, in KiB. Default 1 GiB per file: a 3-hour
# meeting is ~350 MB per WAV.
LIMIT_FORM_PART_KIB = int(os.environ.get("LIMIT_FORM_PART_KIB", "1048576"))
UPLOAD_CHUNK = 1024 * 1024
LOG_TAIL_LINES = int(os.environ.get("LOG_TAIL_LINES", "80"))

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s trsvc %(message)s",
    stream=sys.stdout,
)
log = logging.getLogger("trsvc").info


def stamp() -> str:
    return datetime.now().strftime("%H:%M:%S")


# ---------------------------------------------------------------------------
# App state
# ---------------------------------------------------------------------------
JOB_LOCK = asyncio.Lock()
app = FastAPI(title="TeamsRecorder Transcribe", version="1.0.0")

# Files transcribe.py writes into <out>; everything else in the job dir
# (the uploaded WAVs, library.html) is deliberately not zipped.
ZIP_SKIP_NAMES = {"mic.wav", "loopback.wav", "speakers.json", "library.html"}


def gpu_status() -> dict:
    """Torch-free GPU check via nvidia-smi (cheap, never loads CUDA in here)."""
    try:
        proc = subprocess.run(
            ["nvidia-smi", "--query-gpu=name,memory.free,memory.total",
             "--format=csv,noheader,nounits"],
            capture_output=True, text=True, timeout=20)
    except (OSError, subprocess.TimeoutExpired) as exc:
        return {"ok": False, "name": None, "free_mib": None, "total_mib": None,
                "error": f"nvidia-smi failed: {exc}"}
    if proc.returncode != 0:
        return {"ok": False, "name": None, "free_mib": None, "total_mib": None,
                "error": (proc.stderr or proc.stdout).strip()[:400]}
    first = (proc.stdout or "").strip().splitlines()[0]
    parts = [p.strip() for p in first.split(",")]
    name = parts[0] if parts else None
    free = int(parts[1]) if len(parts) > 1 and parts[1].isdigit() else None
    total = int(parts[2]) if len(parts) > 2 and parts[2].isdigit() else None
    return {"ok": free is not None, "name": name, "free_mib": free,
            "total_mib": total, "error": None}


def tail_lines(path: Path, n: int) -> list[str]:
    try:
        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
    except OSError as exc:
        return [f"(sidecar.log unreadable: {exc})"]
    return lines[-n:]


# ---------------------------------------------------------------------------
# Endpoints
# ---------------------------------------------------------------------------
@app.get("/health")
async def health() -> dict:
    gpu = await asyncio.to_thread(gpu_status)
    return {"status": "ok", "gpu": gpu, "busy": JOB_LOCK.locked()}


async def save_upload(upload: UploadFile | None, dest: Path) -> int:
    """Stream an UploadFile to disk in chunks. Returns bytes written (0 if absent)."""
    if upload is None:
        return 0
    total = 0
    with open(dest, "wb") as out:
        while True:
            chunk = await upload.read(UPLOAD_CHUNK)
            if not chunk:
                break
            out.write(chunk)
            total += len(chunk)
    return total


def run_sidecar(job_dir: Path, log_path: Path, language: str,
                has_mic: bool, has_loopback: bool, has_speakers: bool) -> int:
    """Run transcribe.py as a subprocess (streaming stdout/stderr to sidecar.log)."""
    cmd = [sys.executable, "-u", str(TRANSCRIBE_PY)]
    if has_mic:
        cmd += ["--mic", str(job_dir / "mic.wav")]
    if has_loopback:
        cmd += ["--loopback", str(job_dir / "loopback.wav")]
    cmd += ["--out", str(job_dir / "out")]
    if has_speakers:
        cmd += ["--speakers-file", str(job_dir / "speakers.json")]
    if language:
        cmd += ["--language", language]

    log(f"[{stamp()}] cmd: {' '.join(cmd)}")
    with open(log_path, "wb") as log_file:
        log_file.write(f"# {' '.join(cmd)}\n".encode("utf-8", "replace"))
        log_file.flush()
        proc = subprocess.run(cmd, cwd=str(job_dir), stdout=log_file,
                              stderr=subprocess.STDOUT, env=os.environ.copy())
    return proc.returncode


def build_zip(out_dir: Path, log_path: Path) -> bytes:
    buf = BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as zf:
        if out_dir.is_dir():
            for p in sorted(out_dir.iterdir()):
                if p.is_file() and p.name not in ZIP_SKIP_NAMES:
                    zf.write(p, arcname=p.name)
        if log_path.exists():
            zf.write(log_path, arcname="sidecar.log")
    return buf.getvalue()


@app.post("/transcribe")
async def transcribe(
        mic: UploadFile | None = File(None),
        loopback: UploadFile | None = File(None),
        speakers: UploadFile | None = File(None),
        language: str = Form("en")):
    if mic is None and loopback is None:
        raise HTTPException(status_code=400,
                            detail="multipart must contain 'mic' and/or 'loopback'")
    if not TRANSCRIBE_PY.exists():
        raise HTTPException(status_code=500,
                            detail=f"sidecar not found: {TRANSCRIBE_PY}")

    # One job at a time. A second request waits here (no 409).
    async with JOB_LOCK:
        job_dir = Path(tempfile.mkdtemp(prefix="trjob-", dir=str(JOBS_ROOT)))
        job_id = job_dir.name
        t0 = time.monotonic()
        log(f"[{stamp()}] job {job_id} START language={language} busy=1")
        try:
            mic_bytes = await save_upload(mic, job_dir / "mic.wav")
            loopback_bytes = await save_upload(loopback, job_dir / "loopback.wav")
            speakers_bytes = await save_upload(speakers, job_dir / "speakers.json")
            log(f"[{stamp()}] job {job_id} uploaded mic={mic_bytes}B "
                f"loopback={loopback_bytes}B speakers={speakers_bytes}B")

            log_path = job_dir / "sidecar.log"
            rc = await asyncio.to_thread(
                run_sidecar, job_dir, log_path, language,
                mic is not None and mic_bytes > 0,
                loopback is not None and loopback_bytes > 0,
                speakers is not None and speakers_bytes > 0)

            elapsed = time.monotonic() - t0
            if rc != 0:
                log(f"[{stamp()}] job {job_id} FAILED exit={rc} in {elapsed:.1f}s")
                return JSONResponse(
                    status_code=500,
                    content={"error": f"transcribe.py exited with code {rc}",
                             "exit_code": rc, "job": job_id,
                             "elapsed_sec": round(elapsed, 1),
                             "log_tail": tail_lines(log_path, LOG_TAIL_LINES)})

            out_dir = job_dir / "out"
            payload = build_zip(out_dir, log_path)
            utts = 0
            tj = out_dir / "transcript.json"
            if tj.exists():
                try:
                    utts = len(json.loads(tj.read_text(encoding="utf-8"))
                               .get("utterances", []))
                except (OSError, ValueError) as exc:
                    log(f"[{stamp()}] job {job_id}: transcript.json unreadable: {exc}")
            log(f"[{stamp()}] job {job_id} DONE in {elapsed:.1f}s "
                f"exit=0 utterances={utts} zip={len(payload)}B")
            return Response(
                content=payload,
                media_type="application/zip",
                headers={"Content-Disposition": f'attachment; filename="{job_id}.zip"'})
        finally:
            shutil.rmtree(job_dir, ignore_errors=True)


def main() -> None:
    import uvicorn

    log(f"sidecar={TRANSCRIBE_PY} exists={TRANSCRIBE_PY.exists()} "
        f"jobs_root={JOBS_ROOT} port={PORT} limit_form_part_kib={LIMIT_FORM_PART_KIB}")
    # uvicorn >= 0.30 enforces a per-multipart-part size cap in KiB; pass it only
    # when the installed uvicorn actually accepts the kwarg.
    import inspect

    kwargs = {}
    if "limit_form_part" in inspect.signature(uvicorn.Config.__init__).parameters:
        kwargs["limit_form_part"] = LIMIT_FORM_PART_KIB
    else:
        log("uvicorn has no limit_form_part — multipart size is uncapped")
    uvicorn.run(app, host=HOST, port=PORT, log_level="info", **kwargs)


if __name__ == "__main__":
    main()
