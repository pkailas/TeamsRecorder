# -*- coding: utf-8 -*-
"""
TeamsRecorder transcription sidecar.

Transcribes the two WAV files produced by the tray app:
  --mic        mic.wav        (the local user, "Paul")
  --loopback   loopback.wav   (everyone else; diarized with pyannote)

and writes <out>/transcript.json and <out>/transcript.md.

Speakers that were already enrolled (via `rename`) are recognised
automatically: each diarized speaker's voice embedding is matched by cosine
similarity against the store in %LOCALAPPDATA%\\TeamsRecorder\\speakers.json
(override with --speakers-file).

Run the sidecar via:
  sidecar\\.venv\\Scripts\\python.exe sidecar\\transcribe.py --mic ... --loopback ... --out ...

Rename / enroll a speaker (no GPU needed):
  sidecar\\.venv\\Scripts\\python.exe sidecar\\transcribe.py rename --session <dir> --assign "Speaker 1=Sean"

Self-test (acceptance check for GPU):
  python transcribe.py --selftest

# NOTE (Blackwell sm_120): ctranslate2 with compute_type="int8"/"int8_float16"
# crashes with CUBLAS_STATUS_NOT_SUPPORTED on this GPU. float16 is used instead.
"""
from __future__ import annotations
import argparse
import glob
import json
import os
import site
import sys
import time
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

# Line-buffer stdout/stderr so the C# app's sidecar.log shows lines as they
# happen, even though the console is redirected to a file.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(line_buffering=True)
    except Exception:
        pass


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
# Speaker store (enrolled voice embeddings)
# --------------------------------------------------------------------------
def default_speakers_file() -> Path:
    localappdata = os.environ.get("LOCALAPPDATA", str(Path.home()))
    return Path(localappdata) / "TeamsRecorder" / "speakers.json"


MAX_EMBEDDINGS_PER_NAME = 5


def load_speaker_store(path: str | Path) -> dict:
    """Load the speaker store. Missing file / bad JSON = no known speakers."""
    p = Path(path)
    if not p.exists():
        return {"version": 1, "speakers": []}
    try:
        with open(p, "r", encoding="utf-8") as f:
            data = json.load(f)
        if not isinstance(data, dict) or not isinstance(data.get("speakers"), list):
            raise ValueError("malformed speaker store")
        return data
    except Exception as e:
        log(f"Speaker store {p} is unreadable ({e}); starting with an empty store.")
        return {"version": 1, "speakers": []}


def save_speaker_store(path: str | Path, store: dict) -> None:
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    with open(p, "w", encoding="utf-8") as f:
        json.dump(store, f, indent=2, ensure_ascii=False)
    log(f"Wrote {p}")


def get_or_create_name(store: dict, name: str) -> dict:
    for sp in store["speakers"]:
        if sp["name"] == name:
            return sp
    sp = {"name": name, "embeddings": [], "updated": ""}
    store["speakers"].append(sp)
    return sp


def add_embedding_to_store(store: dict, name: str, embedding: list[float]) -> None:
    """Append an embedding under `name`, keeping at most 5 (oldest dropped first)."""
    sp = get_or_create_name(store, name)
    emb = [float(x) for x in embedding]
    sp["embeddings"].append(emb)
    if len(sp["embeddings"]) > MAX_EMBEDDINGS_PER_NAME:
        sp["embeddings"] = sp["embeddings"][-MAX_EMBEDDINGS_PER_NAME:]
    sp["updated"] = datetime.now().isoformat(timespec="seconds")


def match_speakers(store: dict, candidates: list[tuple[str, np.ndarray]],
                   threshold: float) -> dict:
    """
    Match diarized speaker embeddings against the store.

    candidates: list of (diarization_label, embedding) in order of first
    appearance of the label.

    For each candidate, the best (name, score) over ALL stored embeddings is
    computed, then assignments are resolved greedily by descending score so
    that two diarized speakers never resolve to the same name (the loser
    falls back to null).

    Returns {diarization_label: {"name": str|None, "match_score": float|None,
                                 "best_candidate": str|None}}
    """
    results = {}
    pairs = []  # (score, index, name)
    for idx, (label, emb) in enumerate(candidates):
        best_name, best_score = None, -2.0
        for sp in store["speakers"]:
            for stored in sp["embeddings"]:
                s = float(np.dot(emb, np.asarray(stored, dtype=np.float64)))
                if s > best_score:
                    best_name, best_score = sp["name"], s
        results[label] = {
            "name": None,
            "match_score": best_score,
            "best_candidate": best_name,
        }
        if best_name is not None:
            pairs.append((best_score, idx, best_name))

    pairs.sort(key=lambda t: -t[0])
    taken_names = set()
    for score, idx, name in pairs:
        label, _emb = candidates[idx]
        if score >= threshold and name not in taken_names:
            taken_names.add(name)
            results[label]["name"] = name
        # else: stays name=None (falls back to Speaker N); best_candidate kept
    return results


# --------------------------------------------------------------------------
# Speaker-bleed dedup
# --------------------------------------------------------------------------
import re
import string

_PUNCT_RE = re.compile("[" + re.escape(string.punctuation) + "]+")


def _norm_words(text: str) -> list[str]:
    """Lowercase, strip punctuation, split to words."""
    return [w for w in _PUNCT_RE.sub(" ", text.lower()).split() if w]


def dedupe_mic_bleed(mic_utts: list, loopback_utts: list, window: float = 2.0,
                     threshold: float = 0.6) -> tuple[list, list]:
    """
    Drop mic utterances that are bleed (echo) of loopback audio.

    mic.wav is the local user ("Paul"); loopback.wav is everyone else. When
    the user has open speakers, the mic also hears the far end, so mic
    utterances can duplicate loopback ones. The loopback side is never
    contaminated (Teams does not play the local mic back), so loopback
    utterances are ground truth for what the far end actually said.

    For each mic utterance M, consider loopback utterances L whose time span
    overlaps [M.start - window, M.end + window]. M is dropped if either:
      - containment |M ∩ L| / |M| >= threshold  (M is a fragment of a longer
        loopback utterance), or
      - token-set Jaccard |M ∩ L| / |M ∪ L| >= threshold against the union of
        the overlapping L words.
    A mic utterance with < 3 words and no overlapping loopback audio at all
    is always kept (short genuine interjections, e.g. "yes", "mm-hmm").

    Returns (kept_mic_utts, dropped) where dropped entries carry
    start/end/text/matched_loopback_text for auditing.
    """
    kept = []
    dropped = []
    for m in mic_utts:
        m_words = set(_norm_words(m["text"]))
        m_start, m_end = m["start"], m["end"]
        overlap_words: set[str] = set()
        overlap_texts: list[str] = []
        for l in loopback_utts:
            if l["end"] < m_start - window or l["start"] > m_end + window:
                continue
            overlap_words.update(_norm_words(l["text"]))
            overlap_texts.append(l["text"].strip())
        if len(m_words) < 3 and not overlap_texts:
            kept.append(m)  # short interjection, nothing to compare against
            continue
        if not m_words or not overlap_words:
            # No overlapping loopback text: not bleed, keep it.
            kept.append(m)
            continue
        inter = m_words & overlap_words
        union = m_words | overlap_words
        containment = len(inter) / len(m_words)
        jaccard = len(inter) / len(union)
        if containment >= threshold or jaccard >= threshold:
            dropped.append({
                "start": m["start"],
                "end": m["end"],
                "text": m["text"],
                "matched_loopback_text": " | ".join(overlap_texts)[:400],
            })
        else:
            kept.append(m)
    return kept, dropped


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
            # no overlap: nearest turn *edge* (distance from the word to the
            # turn interval). Nearest-midpoint was biased toward short turns
            # and mis-assigned words that fall in the gap between speakers.
            best = min(turns, key=lambda t: max(t[0] - w.end, w.start - t[1], 0.0))[2]
        labeled.append((w.start, w.end, best))
    return labeled


# --------------------------------------------------------------------------
# Speaker embeddings (voice enrollment vectors)
# --------------------------------------------------------------------------
def compute_speaker_embeddings(wav_path: Path, sr: int, turns: list) -> dict:
    """
    Compute one L2-normalized embedding per diarization label present in
    `turns` (list of (start, end, label)), sorted by duration descending.

    Uses the pyannote wespeaker-voxceleb-resnet34-LM embedding model with
    Inference(window="whole"): the speaker's waveform slices are concatenated
    up to 20 s total (turns < 0.5 s are skipped), the vector is L2-normalized.

    Returns {label: np.ndarray (256,)} — labels with no usable audio are absent.
    """
    import torch
    from pyannote.audio import Model, Inference

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    log(f"Loading pyannote/wespeaker-voxceleb-resnet34-LM on {device}...")
    model = Model.from_pretrained(
        "pyannote/wespeaker-voxceleb-resnet34-LM",
        token=os.environ["HF_TOKEN"])
    inf = Inference(model, window="whole").to(device)

    mono = load_wav_mono_float32(wav_path, target_rate=sr)

    by_label: dict[str, list] = {}
    for s, e, l in turns:
        by_label.setdefault(l, []).append((s, e))

    embeddings = {}
    for label, segs in by_label.items():
        segs.sort(key=lambda t: -(t[1] - t[0]))
        chunks, total = [], 0.0
        for s, e in segs:
            if e - s < 0.5:
                continue
            if total >= 20.0:
                break
            i0 = int(s * sr)
            i1 = min(int(e * sr), len(mono))
            i0 = min(i0, i1)
            if i1 - i0 < 1:
                continue
            chunks.append(mono[i0:i1])
            total += (e - s)
        if not chunks:
            log(f"No usable audio for {label} (no turn >= 0.5 s) — no embedding.")
            continue
        x = np.concatenate(chunks).astype(np.float32)[: 20 * sr]
        out = inf({"waveform": torch.from_numpy(x)[None, :], "sample_rate": int(sr)})
        v = np.asarray(out).reshape(-1).astype(np.float64)
        n = float(np.linalg.norm(v))
        if n > 0:
            v = v / n
        embeddings[label] = v
        log(f"Embedded {label}: {len(chunks)} segment(s), {total:.1f}s -> dim {v.shape[0]}.")
    return embeddings


# --------------------------------------------------------------------------
# Transcript output (one writer for both modes)
# --------------------------------------------------------------------------
def write_transcript_md(out_dir: Path, result: dict) -> Path:
    """Write <out_dir>/transcript.md from a transcript dict."""
    md_path = out_dir / "transcript.md"
    with open(md_path, "w", encoding="utf-8") as f:
        f.write(f"# {out_dir.name}\n\n")
        for u in result.get("utterances", []):
            f.write(f"**[{fmt_hms(u['start'])}] {u['speaker']}:** {u['text']}\n")
    return md_path


def transcript_result(args, model_name: str, paul_utterances: list,
                       loopback_utterances: list, diar_turns_out: list,
                       speaker_map: list | None, notes: list,
                       total_duration: float,
                       mic_bleed_dropped: int | None = None,
                       mic_dropped: list | None = None) -> dict:
    """Assemble the transcript.json payload (shared by normal and rename mode)."""
    all_utterances = paul_utterances + loopback_utterances
    all_utterances.sort(key=lambda u: u["start"])

    speakers = ["Paul"] if any(u["speaker"] == "Paul" for u in all_utterances) else []
    seen = set()
    for u in all_utterances:
        if u["speaker"] not in seen:
            seen.add(u["speaker"])
            if u["speaker"] != "Paul":
                speakers.append(u["speaker"])

    result = {
        "created": datetime.now().isoformat(timespec="seconds"),
        "model": model_name,
        "duration_sec": round(total_duration, 3),
        "speakers": speakers,
        "utterances": all_utterances,
        "diarization": diar_turns_out,
    }
    if speaker_map is not None:
        result["speaker_map"] = speaker_map
    if mic_bleed_dropped is not None:
        result["mic_bleed_dropped"] = mic_bleed_dropped
        if mic_dropped:
            result["mic_dropped"] = mic_dropped
    if notes:
        result["notes"] = notes
    return result


def write_transcript(out_dir: Path, result: dict) -> None:
    json_path = out_dir / "transcript.json"
    with open(json_path, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2, ensure_ascii=False)
    log(f"Wrote {json_path}")
    md_path = write_transcript_md(out_dir, result)
    log(f"Wrote {md_path}")


# --------------------------------------------------------------------------
# Pipeline (normal mode)
# --------------------------------------------------------------------------
def run_transcription(args) -> int:
    import torch
    from faster_whisper import WhisperModel

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)
    notes = []
    t_total_start = time.monotonic()

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
        t0 = time.monotonic()
        log(f"Transcribing {path_str}")
        audio = load_wav_mono_float32(path_str, target_rate=16000)
        dur = len(audio) / 16000.0
        log(f"  {os.path.basename(path_str)}: {dur / 60.0:.1f} min")
        # initial_prompt nudges Whisper toward punctuated, cased output (it
        # sometimes drops both on flat or synthetic audio).
        seg_iter, _info = model.transcribe(
            audio, language=args.language,
            word_timestamps=True, vad_filter=True,
            initial_prompt="Hello, welcome to the meeting. Let's get started.")
        segments = []
        last_log_pos = -1e9
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
            # Progress every ~60 s of audio position or every 200 segments,
            # whichever first.
            if (len(segments) % 200 == 0
                    or seg.end - last_log_pos >= 60.0
                    or seg.end >= dur):
                pct = min(100.0, 100.0 * seg.end / dur) if dur > 0 else 0.0
                log(f"  {os.path.basename(path_str)}  {fmt_hms(seg.end)} / "
                    f"{fmt_hms(dur)}  ({pct:.0f}%)  segs={len(segments)}")
                last_log_pos = seg.end
        elapsed = time.monotonic() - t0
        log(f"  {os.path.basename(path_str)} done in {elapsed:.1f}s "
            f"({len(segments)} segments)")
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
    speaker_map = []
    loopback_total = 0.0
    if loopback_path.exists():
        dur = wav_duration_seconds(loopback_path)
        loopback_total = max(loopback_total, dur)
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
            t_diar = time.monotonic()
            turns, _raw = diarize(diar_pipe, loopback_path)
            log(f"Diarization done in {time.monotonic() - t_diar:.1f}s")
            diar_turns_out = [{"start": s, "end": e, "label": l} for (s, e, l) in turns]
            log(f"Diarization: {len(turns)} turns: {turns[:5]}{'...' if len(turns) > 5 else ''}")

            # -- Speaker voice embeddings + name matching ------------------
            emb_by_label: dict[str, np.ndarray] = {}
            if turns and not args.no_embeddings:
                t_emb = time.monotonic()
                emb_by_label = compute_speaker_embeddings(loopback_path, 16000, turns)
                log(f"Embeddings done in {time.monotonic() - t_emb:.1f}s")

            # Match against the enrolled speaker store (read-only here).
            match = {}
            if emb_by_label:
                speakers_file = Path(args.speakers_file) if args.speakers_file \
                    else default_speakers_file()
                store = load_speaker_store(speakers_file)
                # candidates in order of first appearance of the label
                first_seen: list[str] = []
                for _s, _e, l in turns:
                    if l not in first_seen and l in emb_by_label:
                        first_seen.append(l)
                match = match_speakers(
                    store,
                    [(l, emb_by_label[l]) for l in first_seen],
                    threshold=args.match_threshold)
                for l in first_seen:
                    m = match[l]
                    log(f"Match {l}: best={m['best_candidate']} "
                        f"score={m['match_score']:.4f} -> {m['name']}")

            # Map SPEAKER_xx -> Speaker N (unnamed) or the matched name.
            label_map = {}
            counter = 0
            for _s, _e, l in turns:
                if l not in label_map:
                    m = match.get(l)
                    if m is not None and m.get("name"):
                        label_map[l] = m["name"]
                    else:
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
                    # faster-whisper words already carry a leading space
                    utterances[-1]["text"] += word_text
                else:
                    utterances.append({
                        "start": float(wstart), "end": float(wend),
                        "speaker": speaker, "text": word_text})
            for u in utterances:
                u["text"] = u["text"].strip()
            loopback_utterances = [u for u in utterances if u["text"]]

            # -- speaker_map (one entry per diarized speaker, in order of
            #    first appearance; only labels that have an embedding) -----
            talk_time: dict[str, float] = {}
            for s, e, l in turns:
                talk_time[l] = talk_time.get(l, 0.0) + (e - s)

            if turns:
                # order of first appearance over ALL labels (even without embeddings)
                first_all: list[str] = []
                for _s, _e, l in turns:
                    if l not in first_all:
                        first_all.append(l)
                for l in first_all:
                    m = match.get(l, {})
                    emb = emb_by_label.get(l)
                    sample_text = ""
                    for u in loopback_utterances:
                        if u["speaker"] == label_map.get(l, l):
                            if len(u["text"]) > len(sample_text):
                                sample_text = u["text"]
                    entry = {
                        "label": label_map.get(l, l),
                        "diarization_label": l,
                        "name": m.get("name"),
                        "match_score": (round(m["match_score"], 6)
                                        if m.get("match_score") is not None else None),
                        "best_candidate": m.get("best_candidate"),
                        "talk_time_sec": round(talk_time.get(l, 0.0), 3),
                        "sample_text": (sample_text or "")[:200],
                        "embedding": (emb.tolist() if emb is not None else None),
                    }
                    speaker_map.append(entry)
    else:
        notes.append(f"loopback wav not found: {loopback_path}")

    # -- Speaker-bleed dedup (drop mic utterances that echo the far end) --
    mic_bleed_dropped = None
    mic_dropped = []
    if (not getattr(args, "no_dedup", False) and paul_utterances
            and loopback_utterances):
        paul_utterances, mic_dropped = dedupe_mic_bleed(
            paul_utterances, loopback_utterances,
            window=args.dedup_window, threshold=args.dedup_threshold)
        mic_bleed_dropped = len(mic_dropped)
        log(f"Bleed dedup: dropped {mic_bleed_dropped} of "
            f"{mic_bleed_dropped + len(paul_utterances)} mic utterances")

    # -- Merge -------------------------------------------------------------
    total_duration = 0.0
    for p in (mic_path, loopback_path):
        if p.exists():
            try:
                total_duration = max(total_duration, wav_duration_seconds(p))
            except Exception:
                pass

    result = transcript_result(
        args, model_name, paul_utterances, loopback_utterances,
        diar_turns_out, speaker_map, notes, total_duration,
        mic_bleed_dropped=mic_bleed_dropped, mic_dropped=mic_dropped)

    write_transcript(out_dir, result)

    # -- Free GPU ----------------------------------------------------------
    del model
    torch.cuda.empty_cache()
    total_elapsed = time.monotonic() - t_total_start
    rt = total_elapsed / total_duration if total_duration > 0 else 0.0
    log(f"Total {total_elapsed:.1f}s for {total_duration / 60.0:.1f} min audio "
        f"({rt:.1f}x realtime)")
    log(f"Done: {len(result['utterances'])} utterances.")
    return 0


# --------------------------------------------------------------------------
# Rename / enroll sub-mode (no GPU)
# --------------------------------------------------------------------------
def cmd_rename(args) -> int:
    session = Path(args.session)
    json_path = session / "transcript.json"
    if not json_path.exists():
        print(f"error: {json_path} not found", file=sys.stderr)
        return 1
    with open(json_path, "r", encoding="utf-8") as f:
        result = json.load(f)

    old_label_map: dict[str, str] = {}  # old label -> new name
    new_labels: dict[str, str] = {}     # new label -> new label
    speakers_file = Path(args.speakers_file) if args.speakers_file \
        else default_speakers_file()
    store = load_speaker_store(speakers_file)
    store_dirty = False

    speaker_map = result.get("speaker_map")
    for raw in args.assign:
        if "=" not in raw:
            print(f"error: --assign expects 'OldLabel=NewName', got: {raw}",
                  file=sys.stderr)
            return 1
        old, new = raw.split("=", 1)
        old, new = old.strip(), new.strip()
        if not old or not new:
            print(f"error: --assign needs non-empty both sides, got: {raw}",
                  file=sys.stderr)
            return 1
        if old in old_label_map:
            print(f"error: --assign duplicates source label: {old}", file=sys.stderr)
            return 1
        if new in new_labels.values():
            print(f"error: --assign duplicates target name: {new}", file=sys.stderr)
            return 1
        old_label_map[old] = new
        new_labels[new] = new

    if not old_label_map:
        print("error: no --assign arguments given", file=sys.stderr)
        return 1

    # Enroll embeddings (unless --no-enroll) and collect missing labels.
    missing = []
    for old, new in old_label_map.items():
        entry = None
        for e in (speaker_map or []):
            if e.get("label") == old:
                entry = e
                break
        if entry is None:
            missing.append(old)
            entry = {"embedding": None}
        if not args.no_enroll and entry.get("embedding") is not None:
            add_embedding_to_store(store, new, entry["embedding"])
            store_dirty = True

    # Apply the rename: speaker_map labels, utterance speakers, speakers list.
    for e in (speaker_map or []):
        if e.get("label") in old_label_map:
            new_name = old_label_map[e["label"]]
            e["label"] = new_name
            if e.get("name") is None:
                e["name"] = new_name
        elif e.get("label") in new_labels.values():
            # a label that is already a previously-assigned name: leave as-is
            pass
    for u in result.get("utterances", []):
        if u.get("speaker") in old_label_map:
            u["speaker"] = old_label_map[u["speaker"]]
    result["speakers"] = [
        old_label_map.get(s, s) for s in result.get("speakers", [])]
    # rebuild "speakers" preserving first-appearance order over all utterances
    seen = set()
    result["speakers"] = []
    paul = any(u.get("speaker") == "Paul" for u in result.get("utterances", []))
    if paul:
        result["speakers"].append("Paul")
    for u in result.get("utterances", []):
        sp = u.get("speaker")
        if sp and sp != "Paul" and sp not in seen:
            seen.add(sp)
            result["speakers"].append(sp)

    if store_dirty:
        save_speaker_store(speakers_file, store)

    write_transcript(session, result)
    for old, new in old_label_map.items():
        log(f"Renamed {old!r} -> {new!r}"
            + ("" if not args.no_enroll else " (not enrolled)"))
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

    # pyannote/speaker-diarization-community-1 (4.x) returns a DiarizeOutput
    # exposing:
    #   output.speaker_diarization            -> iterable of (turn, speaker)
    #   output.exclusive_speaker_diarization  -> one speaker at a time (we use this)
    #   output.speaker_embeddings             -> (num_speakers, dim) centroids
    #                                            ordered by diarization.labels()
    diar = output.exclusive_speaker_diarization
    turns = []
    for turn, speaker in diar:
        turns.append((float(turn.start), float(turn.end), str(speaker)))
    turns.sort(key=lambda t: t[0])
    return turns, output


# --------------------------------------------------------------------------
# Entry point
# --------------------------------------------------------------------------
def build_parser() -> argparse.ArgumentParser:
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
    parser.add_argument("--no-embeddings", action="store_true",
                        help="Skip computing speaker voice embeddings "
                             "(no name matching).")
    parser.add_argument("--speakers-file",
                        help="Speaker store JSON path "
                             "(default: %%LOCALAPPDATA%%\\TeamsRecorder\\speakers.json).")
    parser.add_argument("--match-threshold", type=float, default=0.60,
                        help="Minimum cosine similarity to label a diarized "
                             "speaker with an enrolled name (default: 0.60).")
    parser.add_argument("--no-dedup", action="store_true",
                        help="Disable mic-bleed dedup (keep mic utterances "
                             "that duplicate loopback audio).")
    parser.add_argument("--dedup-threshold", type=float, default=0.6,
                        help="Token-overlap threshold (containment or Jaccard) "
                             "for dropping a mic utterance as bleed (default: 0.6).")
    parser.add_argument("--dedup-window", type=float, default=2.0,
                        help="Time window (s) around each mic utterance to "
                             "search for overlapping loopback audio (default: 2.0).")
    sub = parser.add_subparsers(dest="command")
    rp = sub.add_parser(
        "rename",
        help="Rename/enroll speakers in an existing session's transcript "
             "(no GPU needed).")
    rp.add_argument("--session", required=True,
                    help="Session directory containing transcript.json.")
    rp.add_argument("--assign", action="append", default=[],
                    help="'OldLabel=NewName', e.g. 'Speaker 1=Sean'. Repeatable.")
    rp.add_argument("--no-enroll", action="store_true",
                    help="Do not append the renamed speakers' embeddings to "
                         "the store.")
    rp.add_argument("--speakers-file",
                    help="Speaker store JSON path "
                         "(default: %%LOCALAPPDATA%%\\TeamsRecorder\\speakers.json).")
    return parser


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()

    if args.selftest:
        return run_selftest()

    if args.command == "rename":
        if not args.session:
            print("error: rename requires --session", file=sys.stderr)
            return 1
        return cmd_rename(args)

    # default: normal transcription mode
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
