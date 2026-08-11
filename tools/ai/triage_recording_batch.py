#!/usr/bin/env python3
"""Fast, idempotent WAV triage before expensive speech transcription."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import wave
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
from faster_whisper.audio import decode_audio
from faster_whisper.vad import VadOptions, get_speech_timestamps


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    return parser.parse_args()


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def wav_metadata(path: Path) -> dict:
    with wave.open(str(path), "rb") as source:
        frames = source.getnframes()
        rate = source.getframerate()
        return {
            "channels": source.getnchannels(),
            "sample_rate_hz": rate,
            "bits_per_sample": source.getsampwidth() * 8,
            "frame_count": frames,
            "duration_seconds": round(frames / rate, 3) if rate else 0.0,
        }


def classify(duration: float, speech: float, rms_dbfs: float) -> str:
    if duration <= 0.1:
        return "EMPTY_FILE"
    if speech < 0.35 and rms_dbfs < -55:
        return "SILENCE"
    if speech < 0.8:
        return "NO_SPEECH_OR_DROPPED"
    if speech < 3.0 or speech / duration < 0.05:
        return "LOW_SPEECH_REVIEW"
    return "SPEECH_CANDIDATE"


def inspect(path: Path, root: Path) -> dict:
    stat = path.stat()
    result = {
        "relative_path": str(path.relative_to(root)).replace("\\", "/"),
        "file_name": path.name,
        "sha256": sha256(path),
        "file_size_bytes": stat.st_size,
        "last_write_utc": datetime.fromtimestamp(stat.st_mtime, timezone.utc).isoformat(),
    }
    try:
        result.update(wav_metadata(path))
        audio = decode_audio(str(path), sampling_rate=16000)
        if audio.size:
            rms = float(np.sqrt(np.mean(np.square(audio, dtype=np.float64))))
            peak = float(np.max(np.abs(audio)))
            zero_ratio = float(np.mean(audio == 0))
        else:
            rms = peak = 0.0
            zero_ratio = 1.0
        rms_dbfs = 20 * math.log10(max(rms, 1e-12))
        peak_dbfs = 20 * math.log10(max(peak, 1e-12))
        chunks = get_speech_timestamps(
            audio,
            VadOptions(
                threshold=0.5,
                min_speech_duration_ms=250,
                min_silence_duration_ms=500,
                speech_pad_ms=150,
            ),
            sampling_rate=16000,
        )
        speech_seconds = sum(max(0, chunk["end"] - chunk["start"]) for chunk in chunks) / 16000
        duration = float(result["duration_seconds"])
        result.update(
            {
                "rms_dbfs": round(rms_dbfs, 2),
                "peak_dbfs": round(peak_dbfs, 2),
                "exact_zero_percent": round(zero_ratio * 100, 2),
                "vad_speech_seconds": round(speech_seconds, 3),
                "vad_speech_ratio": round(speech_seconds / duration, 4) if duration else 0.0,
                "vad_chunk_count": len(chunks),
                "triage_class": classify(duration, speech_seconds, rms_dbfs),
                "error": None,
            }
        )
    except Exception as exc:  # preserve the rest of the batch for manual review
        result.update({"triage_class": "INVALID_OR_UNSUPPORTED", "error": str(exc)[:1000]})
    return result


def main() -> int:
    args = parse_args()
    root = args.input.resolve()
    if not root.is_dir():
        raise SystemExit(f"Input directory not found: {root}")

    existing_by_hash: dict[str, dict] = {}
    if args.output.is_file():
        try:
            previous = json.loads(args.output.read_text(encoding="utf-8"))
            existing_by_hash = {item["sha256"]: item for item in previous.get("recordings", [])}
        except (KeyError, json.JSONDecodeError):
            existing_by_hash = {}

    recordings = []
    reused = 0
    for path in sorted(root.rglob("*.wav")):
        digest = sha256(path)
        previous = existing_by_hash.get(digest)
        if previous and previous.get("file_size_bytes") == path.stat().st_size:
            item = dict(previous)
            item["relative_path"] = str(path.relative_to(root)).replace("\\", "/")
            item["file_name"] = path.name
            recordings.append(item)
            reused += 1
        else:
            recordings.append(inspect(path, root))

    classes = Counter(item["triage_class"] for item in recordings)
    payload = {
        "schema_version": 1,
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
        "input_root": str(root),
        "summary": {
            "recording_count": len(recordings),
            "reused_count": reused,
            "total_file_size_bytes": sum(item["file_size_bytes"] for item in recordings),
            "total_duration_seconds": round(sum(item.get("duration_seconds", 0) for item in recordings), 3),
            "total_vad_speech_seconds": round(sum(item.get("vad_speech_seconds", 0) for item in recordings), 3),
            "classes": dict(sorted(classes.items())),
        },
        "recordings": recordings,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    temporary = args.output.with_suffix(args.output.suffix + ".part")
    temporary.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temporary, args.output)
    print(json.dumps(payload["summary"], ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
