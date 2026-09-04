# -*- coding: utf-8 -*-
"""
TeamsRecorder transcription sidecar.

Transcribes the two WAV files produced by the tray app:
  --mic        mic.wav        (the local user, "Paul")
  --loopback   loopback.wav   (everyone else; diarized with pyannote)

and writes <out>/transcript.json and <out>/transcript.md.

Run the sidecar via:
  sidecar\\.venv\\Scripts\\python.exe sidecar\\transcribe.py --mic ... --loopback ... --out ...

Self-test (acceptance check for GPU):
  python transcribe.py --selftest

NOTE (Blackwell sm_120): ctranslate2 with compute_type="int8"/"int8_float16"
crashes with CUBLAS_STATUS_NOT_SUPPORTED on this GPU. float16 is used instead.
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import site
import sys
import traceback
import wave
from datetime import datetime
from pathlib import Path

# --------------------------------------------------------------------------
# Hugging Face cache location (must be set before any HF-related import).
# --------------------------------------------------------------------------
os.environ.setdefault("HF_HOME", r"G:\models\hf")

# --------------------------------------------------------------------------
# HF_TOKEN fallback: the tray app launches the sidecar detached from the
# console, so the user environment (where HF_TOKEN lives) may not be visible.
# Read it from the registry if it is missing from os.environ.
# --------------------------------------------------------------------------
if "HF_TOKEN" not in os.environ and sys.platform == "win32":
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Environment") as k:
            val, _ = winreg.QueryValueEx(k, "HF_TOKEN")
        if val:
            os.environ["HF_TOKEN"] = str(val)
    except Exception:
        pass

# --------------------------------------------------------------------------
# Make sure ctranslate2 (CUDA build) can find cuBLAS 12 / cuDNN 9 and
# torch's DLLs. The pip packages nvidia-cublas-cu12 / nvidia-cudnn-cu12
# install their DLLs under <site-packages>\nvidia\<pkg>\bin; torch's live
# in <site-packages>\torch\lib.
# --------------------------------------------------------------------------
if sys.platform == "win32":
    for sp in site.getsitepackages():
        for d in glob.glob(os.path.join(sp, "nvidia", "*", "bin")) + [os.path.join(sp, "torch", "lib")]:
            if os.path.isdir(d):
                os.add_dll_directory(d)
                os.environ["PATH"] = d + os.pathsep + os.environ["PATH"]

import numpy as np  # noqa: E402  (imported after env setup on purpose)
import soundfile as sf  # noqa: E402  (imported after env setup on purpose)


# --------------------------------------------------------------------------
# Small helpers
# --------------------------------------------------------------------------
def log(msg: str) -> None:
    """Timestamped stdout line (C# app redirects to sidecar.log)."""
    print(f"[{datetime.now().strftime('%H:%M:%S')}] {msg}", flush=True)


def fmt_hms(seconds: float) -> str:
    s = max(0, int(seconds))
    return f"{s // 3600:02d}:{(s % 3600) // 60:02d}:{s % 60:02d}"


def wav_duration_seconds(path: str | Path) -> float:
    with wave.open(str(path), "rb") as w:
        frames = w.getnframes()
        rate = w.getframerate() or 1
    return frames / rate


def load_wav_mono_float32(path: str | Path, target_rate: int = 16000) -> np.ndarray:
    """Read a WAV file, downmix to mono, resample to target_rate. Returns float32 1-D."""
    data, rate = sf.read(str(path), dtype="float32", always_2d=True)
    if data.ndim == 2 and data.shape[1] > 1:
        data = data.mean(axis=1, keepdims=True)
    if rate != target_rate and len(data) > 0:
        n_out = max(1, int(round(len(data) * target_rate / rate)))
        pos = np.linspace(0, len(data) - 1, n_out)
        idx = pos.astype(np.int64)
        frac = (pos - idx).astype(np.float32)
        nxt = np.clip(idx + 1, 0, len(data) - 1)
        data = (data[idx] * (1 - frac) + data[nxt] * frac).astype(np.float32)
    return data[:, 0]


# --------------------------------------------------------------------------
# Diarization (pyannote)
# --------------------------------------------------------------------------
def _run_pipeline(pipeline, wav_path: str | Path, sr: int):
    """
    Run the pyannote pipeline, preloading the waveform into memory because
    pyannote.audio 4.x's built-in file decoder (torchcodec) cannot find FFmpeg
    DLLs on this Windows setup. Preloading via the {'waveform', 'sample_rate'}
    dict bypasses file decoding entirely (torchaudio does the resampling).
    """
    import torch

    mono = load_wav_mono_float32(wav_path, target_rate=16000)
    waveform = torch.from_numpy(mono).unsqueeze(0)  # (1, T)
    wf = {"uri": str(wav_path), "audio": str(wav_path),
          "waveform": waveform, "sample_rate": int(sr)}
    return pipeline(wf)


def diarize(pipeline, wav_path: str | Path, sr: int = 16000):
    """
    Run the pyannote pipeline on a wav file.

    Returns a list of (start_sec, end_sec, label) tuples and the raw output
    object (so the caller can print its type / dir() for diagnostics).
    """
    output = _run_pipeline(pipeline, wav_path, sr)

    # pyannote/speaker-diarization-community-1 (4.x) returns a DiarizationObject
    # exposing:
    #   output.speaker_diarization            -> iterable of (turn, speaker)
    #   output.exclusive_speaker_diarization  -> one speaker at a time (we use this)
    diar = output.exclusive_speaker_diarization
    turns = []
    for turn, speaker in diar:
        turns.append((float(turn.start), float(turn.end), str(speaker)))
    turns.sort(key=lambda t: t[0])
    return turns, output


# --------------------------------------------------------------------------
# Word -> speaker assignment
# --------------------------------------------------------------------------
def assign_words_to_speakers(words, turns):
    """
    words: list of objects with .start / .end / .word (faster-whisper words)
    turns: list of (start, end, label)
    Returns list of (word_start, word_end, speaker_label).
    """
    if not turns:
        return [(w.start, w.end, "Speaker 1") for w in words]

    labeled = []
    for w in words:
        best, best_overlap = None, 0.0
        for ts, te, tl in turns:
            overlap = min(w.end, te) - max(w.start, ts)
            if overlap > best_overlap:
                best, best_overlap = tl, overlap
        if best is None:
            # no overlap: nearest turn midpoint
            mid_target = (w.start + w.end) / 2.0
            best = min(turns, key=lambda t: abs((t[0] + t[1]) / 2.0 - mid_target))[2]
        labeled.append((w.start, w.end, best))
    return labeled


# --------------------------------------------------------------------------
# Pipeline
# --------------------------------------------------------------------------
def run_transcription(args) -> int:
    import torch
    from faster_whisper import WhisperModel

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    notes = []

    # -- Load whisper -----------------------------------------------------
    model_name = args.model
    log(f"Loading faster-whisper model '{model_name}' (float16 on cuda)...")
    # float16 is hard-coded on purpose: int8 / int8_float16 crash on sm_120
    # (Blackwell) with CUBLAS_STATUS_NOT_SUPPORTED.
    model = WhisperModel(model_name, device="cuda", compute_type="float16",
                         download_root=r"G:\models\whisper")

    def transcribe(path_str: str):
        """Returns list of segment dicts (start, end, text, words).

        The waveform is preloaded with soundfile and passed to faster-whisper
        as an np.ndarray: faster-whisper's built-in file decoder (pyav) can be
        flaky depending on the FFmpeg DLLs present, and our WAVs are plain
        16-bit PCM anyway. The resample is a no-op (16 kHz -> 16 kHz).
        """
        log(f"Transcribing {path_str}")
        audio = load_wav_mono_float32(path_str, target_rate=16000)
        seg_iter, _info = model.transcribe(
            audio, language=args.language,
            word_timestamps=True, vad_filter=True)
        segments = []
        for seg in seg_iter:
            segments.append({
                "start": float(seg.start),
                "end": float(seg.end),
                "text": (seg.text or "").strip(),
                "words": [
                    {"start": float(w.start), "end": float(w.end), "word": w.word}
                    for w in (seg.words or [])
                ],
            })
        return segments

    # -- Mic (Paul) -------------------------------------------------------
    mic_path = Path(args.mic)
    paul_utterances = []
    if mic_path.exists():
        dur = wav_duration_seconds(mic_path)
        if dur < 1.0:
            notes.append(f"mic.wav is only {dur:.2f}s — skipped (too short).")
            log(notes[-1])
        else:
            for seg in transcribe(str(mic_path)):
                if seg["text"]:
                    paul_utterances.append(
                        {"start": seg["start"], "end": seg["end"],
                         "speaker": "Paul", "text": seg["text"]})
    else:
        notes.append(f"mic wav not found: {mic_path}")

    # -- Loopback (everyone else) ------------------------------------------
    loopback_path = Path(args.loopback)
    loopback_utterances = []
    diar_turns_out = []
    if loopback_path.exists():
        dur = wav_duration_seconds(loopback_path)
        if dur < 1.0:
            notes.append(f"loopback.wav is only {dur:.2f}s — skipped (too short).")
            log(notes[-1])
        else:
            # Transcribe, keeping words.
            loop_segs = transcribe(str(loopback_path))
            words = []
            for seg in loop_segs:
                for w in seg["words"]:
                    words.append(w)

            # Diarize.
            log("Loading pyannote speaker-diarization-community-1...")
            import pyannote.audio  # noqa: F401  (ensures registration)
            from pyannote.audio import Pipeline
            diar_pipe = Pipeline.from_pretrained(
                "pyannote/speaker-diarization-community-1",
                token=os.environ["HF_TOKEN"])
            diar_pipe.to(torch.device("cuda"))

            log("Diarizing loopback.wav...")
            turns, _raw = diarize(diar_pipe, loopback_path)
            diar_turns_out = [{"start": s, "end": e, "label": l} for (s, e, l) in turns]
            log(f"Diarization: {len(turns)} turns: {turns[:5]}{'...' if len(turns) > 5 else ''}")

            # Map SPEAKER_xx -> Speaker N (order of first appearance).
            label_map = {}
            counter = 0
            for _s, _e, l in turns:
                if l not in label_map:
                    counter += 1
                    label_map[l] = f"Speaker {counter}"

            # Assign words to speakers, then group into utterances (one pass).
            class _W:
                __slots__ = ("start", "end", "word")

                def __init__(self, d):
                    self.start = d["start"]
                    self.end = d["end"]
                    self.word = d["word"]

            labeled = assign_words_to_speakers(
                [_W(w) for w in words],
                [(s, e, label_map.get(l, l)) for (s, e, l) in turns])
            utterances = []
            for i, (wstart, wend, speaker) in enumerate(labeled):
                word_text = words[i]["word"]
                if utterances and utterances[-1]["speaker"] == speaker:
                    utterances[-1]["end"] = wend
                    utterances[-1]["text"] += " " + word_text
                else:
                    utterances.append({
                        "start": float(wstart), "end": float(wend),
                        "speaker": speaker, "text": word_text})
            for u in utterances:
                u["text"] = u["text"].strip()
            loopback_utterances = [u for u in utterances if u["text"]]
    else:
        notes.append(f"loopback wav not found: {loopback_path}")

    # -- Merge -------------------------------------------------------------
    all_utterances = paul_utterances + loopback_utterances
    all_utterances.sort(key=lambda u: u["start"])

    speakers = ["Paul"] if any(u["speaker"] == "Paul" for u in all_utterances) else []
    seen = set()
    for u in all_utterances:
        if u["speaker"] not in seen:
            seen.add(u["speaker"])
            if u["speaker"] != "Paul":
                speakers.append(u["speaker"])

    total_duration = 0.0
    for p in (mic_path, loopback_path):
        if p.exists():
            try:
                total_duration = max(total_duration, wav_duration_seconds(p))
            except Exception:
                pass

    created = datetime.now().isoformat(timespec="seconds")

    result = {
        "created": created,
        "model": model_name,
        "duration_sec": round(total_duration, 3),
        "speakers": speakers,
        "utterances": all_utterances,
        "diarization": diar_turns_out,
    }
    if notes:
        result["notes"] = notes

    json_path = out_dir / "transcript.json"
    with open(json_path, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2, ensure_ascii=False)
    log(f"Wrote {json_path}")

    md_path = out_dir / "transcript.md"
    with open(md_path, "w", encoding="utf-8") as f:
        f.write(f"# {out_dir.name}\n\n")
        for u in all_utterances:
            f.write(f"**[{fmt_hms(u['start'])}] {u['speaker']}:** {u['text']}\n")
    log(f"Wrote {md_path}")

    # -- Free GPU ----------------------------------------------------------
    del model
    torch.cuda.empty_cache()
    log(f"Done: {len(all_utterances)} utterances.")
    return 0


# --------------------------------------------------------------------------
# Self-test
# --------------------------------------------------------------------------
def run_selftest() -> int:
    import torch

    log("=== Self-test: environment ===")
    log(f"torch: {torch.__version__}")
    arch = torch.cuda.get_arch_list()
    log(f"torch.cuda.get_arch_list(): {arch}")
    if not torch.cuda.is_available():
        log("torch.cuda.is_available() is FALSE — cannot continue.")
        return 1
    if "sm_120" not in arch:
        log(f"sm_120 NOT in arch list — wrong torch build for Blackwell.")
        return 1
    log(f"GPU: {torch.cuda.get_device_name(0)}")

    import faster_whisper, ctranslate2, pyannote.audio
    log(f"faster-whisper: {faster_whisper.__version__}")
    log(f"ctranslate2: {ctranslate2.__version__}")
    log(f"pyannote.audio: {pyannote.audio.__version__}")

    # -- Make a 4 s 440 Hz sine wav --------------------------------------
    sr = 16000
    t = np.arange(int(sr * 4), dtype=np.float64) / sr
    sig = (0.2 * np.sin(2 * np.pi * 440 * t) * 32767).astype(np.int16)
    tmp_wav = os.path.join(os.environ.get("TEMP", os.environ.get("TMP", ".")),
                           "teamrecorder_selftest.wav")
    with wave.open(tmp_wav, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes(sig.tobytes())
    log(f"Self-test wav: {tmp_wav} ({len(sig) / sr:.1f}s)")

    # -- Whisper ----------------------------------------------------------
    log("Loading faster-whisper large-v3 (float16, cuda)...")
    from faster_whisper import WhisperModel
    model = WhisperModel("large-v3", device="cuda", compute_type="float16",
                         download_root=r"G:\models\whisper")
    audio = load_wav_mono_float32(tmp_wav, target_rate=16000)
    seg_iter, _info = model.transcribe(audio, language="en",
                                       word_timestamps=True, vad_filter=True)
    segments = list(seg_iter)
    log(f"Whisper segments on sine: {len(segments)}")

    # -- Pyannote ----------------------------------------------------------
    log("Loading pyannote/speaker-diarization-community-1...")
    from pyannote.audio import Pipeline
    pipe = Pipeline.from_pretrained(
        "pyannote/speaker-diarization-community-1",
        token=os.environ["HF_TOKEN"])
    pipe.to(torch.device("cuda"))
    out = _run_pipeline(pipe, tmp_wav, 16000)
    log(f"pyannote output type: {type(out)}")
    turns = list(out.exclusive_speaker_diarization)
    log(f"pyannote turns on sine: {len(turns)}")

    # -- Free GPU -----------------------------------------------------------
    del model
    del pipe
    torch.cuda.empty_cache()

    log("SELFTEST PASS")
    return 0


# --------------------------------------------------------------------------
# Entry point
# --------------------------------------------------------------------------
def main() -> int:
    parser = argparse.ArgumentParser(description="TeamsRecorder transcription sidecar")
    parser.add_argument("--mic", help="Path to mic.wav (local user, 'Paul')")
    parser.add_argument("--loopback", help="Path to loopback.wav (everyone else)")
    parser.add_argument("--out", help="Output directory for transcript.json/.md")
    parser.add_argument("--model", default="large-v3",
                        help="faster-whisper model name (default: large-v3)")
    parser.add_argument("--language", default="en",
                        help="Transcription language (default: en)")
    parser.add_argument("--selftest", action="store_true",
                        help="Run the GPU self-test and exit.")
    args = parser.parse_args()

    if args.selftest:
        return run_selftest()

    missing = [a for a in ("mic", "loopback", "out") if not getattr(args, a)]
    if missing:
        print(f"error: missing required argument(s): {', '.join('--' + m for m in missing)}",
              file=sys.stderr)
        return 1

    return run_transcription(args)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:
        traceback.print_exc()
        sys.exit(1)
