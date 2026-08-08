#!/usr/bin/env python3
"""Run a reproducible, local Faster-Whisper transcription benchmark."""

from __future__ import annotations

import argparse
import json
import os
import platform
import sys
import time
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("audio", type=Path)
    parser.add_argument("--model", default="small")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--model-cache", type=Path, required=True)
    parser.add_argument("--threads", type=int, default=2)
    parser.add_argument("--initial-prompt")
    parser.add_argument("--hotwords")
    parser.add_argument("--text-output", type=Path)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if not args.audio.is_file():
        raise SystemExit(f"Audio file not found: {args.audio}")

    from faster_whisper import WhisperModel

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.model_cache.mkdir(parents=True, exist_ok=True)
    os.environ.setdefault("OMP_NUM_THREADS", str(args.threads))

    started = time.perf_counter()
    model = WhisperModel(
        args.model,
        device="cpu",
        compute_type="int8",
        cpu_threads=args.threads,
        download_root=str(args.model_cache),
    )
    loaded = time.perf_counter()
    segments_iter, info = model.transcribe(
        str(args.audio),
        language="fa",
        beam_size=5,
        vad_filter=True,
        word_timestamps=True,
        condition_on_previous_text=True,
        initial_prompt=args.initial_prompt,
        hotwords=args.hotwords,
    )
    segments = []
    for segment in segments_iter:
        segments.append(
            {
                "start": round(segment.start, 3),
                "end": round(segment.end, 3),
                "text": segment.text.strip(),
                "avg_logprob": segment.avg_logprob,
                "no_speech_prob": segment.no_speech_prob,
                "words": [
                    {
                        "start": word.start,
                        "end": word.end,
                        "word": word.word,
                        "probability": word.probability,
                    }
                    for word in (segment.words or [])
                ],
            }
        )
    finished = time.perf_counter()

    result = {
        "schema_version": 1,
        "audio": {"name": args.audio.name},
        "engine": "faster-whisper",
        "model": args.model,
        "device": "cpu",
        "compute_type": "int8",
        "language_requested": "fa",
        "language_detected": info.language,
        "language_probability": info.language_probability,
        "duration_seconds": info.duration,
        "duration_after_vad_seconds": info.duration_after_vad,
        "model_load_seconds": round(loaded - started, 3),
        "transcription_seconds": round(finished - loaded, 3),
        "real_time_factor": round((finished - loaded) / info.duration, 3),
        "cpu_threads": args.threads,
        "platform": platform.platform(),
        "segments": segments,
    }
    args.output.write_text(
        json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    if args.text_output:
        args.text_output.parent.mkdir(parents=True, exist_ok=True)
        lines = [
            f"[{segment['start']:06.2f} - {segment['end']:06.2f}] {segment['text']}"
            for segment in segments
        ]
        args.text_output.write_text("\n".join(lines) + "\n", encoding="utf-8-sig")
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
