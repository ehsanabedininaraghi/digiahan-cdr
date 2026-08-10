#!/usr/bin/env python3
"""Resumable first-pass transcription and coarse routing for a WAV batch."""

from __future__ import annotations

import argparse
import json
import os
import time
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

from faster_whisper import BatchedInferencePipeline, WhisperModel


BUSINESS_TERMS = (
    "آهن", "تیرآهن", "میلگرد", "ورق", "نبشی", "ناودانی", "قیمت", "موجودی",
    "کارخانه", "بازار", "بار", "تناژ", "تن", "شاخه", "سایز", "خرید", "فروش",
    "سفارش", "پیش فاکتور", "فاکتور", "پرداخت", "واریز", "ارسال", "تحویل",
)
QUEUE_TERMS = (
    "در صف", "اپراتور", "کارشناس", "منتظر بمانید", "تماس شما", "اولین نفر",
    "پاسخگویی", "داخلی مورد نظر", "عدد یک", "صدای بوق", "پیغام بگذارید",
)
PROMPT = (
    "مکالمه تلفنی فارسی شرکت آهن؛ واژه‌های محتمل: تیرآهن، میلگرد، قیمت بازار، "
    "قیمت کارخانه، موجودی، تناژ، شاخه، سایز، بار، سفارش، پیش فاکتور، واریز."
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("--triage", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--model-cache", type=Path, required=True)
    parser.add_argument("--model", default="small")
    parser.add_argument("--threads", type=int, default=4)
    parser.add_argument("--batch-size", type=int, default=8)
    parser.add_argument("--relative-prefix", default="")
    parser.add_argument("--exclude-sha256-file", type=Path)
    return parser.parse_args()


def normalize(text: str) -> str:
    return " ".join(text.replace("ي", "ی").replace("ك", "ک").split())


def route(text: str, triage_class: str) -> tuple[str, int, int]:
    business_hits = sum(text.count(term) for term in BUSINESS_TERMS)
    queue_hits = sum(text.count(term) for term in QUEUE_TERMS)
    if not text:
        return "NO_TRANSCRIPT", business_hits, queue_hits
    if business_hits >= 1:
        return "BUSINESS_CONVERSATION_CANDIDATE", business_hits, queue_hits
    if queue_hits >= 1:
        return "QUEUE_OR_IVR", business_hits, queue_hits
    if triage_class == "LOW_SPEECH_REVIEW" or len(text) < 20:
        return "SHORT_SPEECH_REVIEW", business_hits, queue_hits
    return "SPEECH_NON_BUSINESS_REVIEW", business_hits, queue_hits


def save(path: Path, records: list[dict], started: float, model: str) -> None:
    routes = Counter(item["route"] for item in records)
    payload = {
        "schema_version": 1,
        "updated_at_utc": datetime.now(timezone.utc).isoformat(),
        "model": model,
        "processing_seconds": round(time.perf_counter() - started, 3),
        "summary": {
            "processed_recording_count": len(records),
            "routes": dict(sorted(routes.items())),
            "transcript_character_count": sum(len(item.get("transcript_text", "")) for item in records),
        },
        "recordings": sorted(records, key=lambda item: item["relative_path"]),
    }
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".part")
    temporary.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def main() -> int:
    args = parse_args()
    root = args.input.resolve()
    triage = json.loads(args.triage.read_text(encoding="utf-8"))
    excluded_hashes = set()
    if args.exclude_sha256_file:
        excluded_hashes = {
            str(value).upper()
            for value in json.loads(args.exclude_sha256_file.read_text(encoding="utf-8"))
        }
    candidates = [
        item for item in triage["recordings"]
        if item["triage_class"] in {"SPEECH_CANDIDATE", "LOW_SPEECH_REVIEW"}
        and (not args.relative_prefix or item["relative_path"].startswith(args.relative_prefix))
        and item["sha256"].upper() not in excluded_hashes
    ]

    existing: dict[str, dict] = {}
    if args.output.is_file():
        try:
            previous = json.loads(args.output.read_text(encoding="utf-8"))
            existing = {item["sha256"]: item for item in previous.get("recordings", [])}
        except (KeyError, json.JSONDecodeError):
            existing = {}
    records = list(existing.values())
    pending = [item for item in candidates if item["sha256"] not in existing]
    started = time.perf_counter()
    if not pending:
        save(args.output, records, started, args.model)
        print(json.dumps({"pending": 0, "reused": len(records)}, ensure_ascii=False))
        return 0

    model = WhisperModel(
        args.model,
        device="cpu",
        compute_type="int8",
        cpu_threads=args.threads,
        download_root=str(args.model_cache),
        local_files_only=True,
    )
    pipeline = BatchedInferencePipeline(model=model)
    for index, item in enumerate(pending, 1):
        path = root / item["relative_path"]
        item_started = time.perf_counter()
        try:
            segments_iter, info = pipeline.transcribe(
                str(path),
                language="fa",
                beam_size=1,
                batch_size=args.batch_size,
                vad_filter=True,
                word_timestamps=False,
                condition_on_previous_text=False,
                initial_prompt=PROMPT,
            )
            segments = [
                {"start": round(segment.start, 3), "end": round(segment.end, 3), "text": segment.text.strip()}
                for segment in segments_iter
                if segment.text and segment.text.strip()
            ]
            text = normalize(" ".join(segment["text"] for segment in segments))
            routed, business_hits, queue_hits = route(text, item["triage_class"])
            record = {
                "relative_path": item["relative_path"],
                "file_name": item["file_name"],
                "sha256": item["sha256"],
                "duration_seconds": item.get("duration_seconds"),
                "vad_speech_seconds": item.get("vad_speech_seconds"),
                "triage_class": item["triage_class"],
                "route": routed,
                "business_term_hits": business_hits,
                "queue_term_hits": queue_hits,
                "language_detected": info.language,
                "language_probability": info.language_probability,
                "transcript_text": text,
                "segments": segments,
                "transcription_seconds": round(time.perf_counter() - item_started, 3),
                "error": None,
            }
        except Exception as exc:
            record = {
                "relative_path": item["relative_path"],
                "file_name": item["file_name"],
                "sha256": item["sha256"],
                "duration_seconds": item.get("duration_seconds"),
                "vad_speech_seconds": item.get("vad_speech_seconds"),
                "triage_class": item["triage_class"],
                "route": "TRANSCRIPTION_ERROR",
                "transcript_text": "",
                "segments": [],
                "transcription_seconds": round(time.perf_counter() - item_started, 3),
                "error": str(exc)[:1000],
            }
        records.append(record)
        save(args.output, records, started, args.model)
        print(f"[{index}/{len(pending)}] {record['route']} {item['relative_path']}", flush=True)

    save(args.output, records, started, args.model)
    print(json.dumps(json.loads(args.output.read_text(encoding="utf-8"))["summary"], ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
