#!/usr/bin/env python3
"""Build an anonymized dashboard payload from triage and first-pass transcripts."""

from __future__ import annotations

import argparse
import json
import os
import re
from datetime import datetime, timedelta, timezone
from pathlib import Path


TOPICS = {
    "قیمت": "PRICE",
    "موجودی": "INVENTORY",
    "تیرآهن": "BEAM",
    "میلگرد": "REBAR",
    "کارخانه": "FACTORY",
    "بازار": "MARKET",
    "تناژ": "TONNAGE",
    "بار": "LOAD",
    "سفارش": "ORDER",
    "فاکتور": "INVOICE",
    "پرداخت": "PAYMENT",
    "واریز": "PAYMENT",
    "ارسال": "DELIVERY",
    "تحویل": "DELIVERY",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--triage", type=Path, required=True)
    parser.add_argument("--transcripts", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--relative-prefix", default="")
    parser.add_argument("--exclude-sha256-file", type=Path)
    return parser.parse_args()


def parse_call_time(name: str) -> str | None:
    match = re.search(r"-(20\d{6})-(\d{6})-", name)
    if not match:
        return None
    try:
        value = datetime.strptime("".join(match.groups()), "%Y%m%d%H%M%S")
        tehran = timezone(timedelta(hours=3, minutes=30))
        return value.replace(tzinfo=tehran).astimezone(timezone.utc).isoformat()
    except ValueError:
        return None


def direction_and_extension(name: str) -> tuple[str, str | None]:
    if name.startswith("out-"):
        parts = name.split("-")
        return "OUTBOUND", parts[2] if len(parts) > 2 and parts[2].isdigit() else None
    if name.startswith("exten-"):
        parts = name.split("-")
        return "INTERNAL", parts[1] if len(parts) > 1 and parts[1].isdigit() else None
    return "INBOUND", None


def mapped_class(route: str, triage_class: str) -> str:
    if route == "BUSINESS_CONVERSATION_CANDIDATE":
        return "BUSINESS_CONVERSATION"
    if route == "QUEUE_OR_IVR":
        return "QUEUE_ONLY"
    if route in {"NO_TRANSCRIPT", "NO_SPEECH_OR_DROPPED", "EMPTY_FILE"}:
        return "NON_SPEECH_OR_UNSUPPORTED"
    if triage_class in {"NO_SPEECH_OR_DROPPED", "EMPTY_FILE", "SILENCE"}:
        return "NON_SPEECH_OR_UNSUPPORTED"
    return "NEEDS_REVIEW"


def summary_for(audio_class: str, route: str) -> str:
    if audio_class == "BUSINESS_CONVERSATION":
        return "مکالمه کاری در غربال مرحله اول شناسایی شد؛ متن و اعداد باید بازبینی انسانی شوند."
    if audio_class == "QUEUE_ONLY":
        return "الگوی صف یا پیام خودکار شناسایی شد و مکالمه کاری قابل اتکا دیده نشد."
    if audio_class == "NON_SPEECH_OR_UNSUPPORTED":
        return "گفتار قابل اتکا شناسایی نشد؛ تماس کوتاه، قطع‌شده یا بدون مکالمه است."
    return f"گفتار شناسایی شد اما طبقه‌بندی قطعی نیست ({route}) و باید دستی بررسی شود."


def main() -> int:
    args = parse_args()
    baseline = json.loads(args.baseline.read_text(encoding="utf-8"))
    triage = json.loads(args.triage.read_text(encoding="utf-8"))
    transcribed = json.loads(args.transcripts.read_text(encoding="utf-8")) if args.transcripts.is_file() else {"recordings": []}
    transcript_by_hash = {item["sha256"]: item for item in transcribed.get("recordings", [])}
    excluded_hashes = set()
    if args.exclude_sha256_file:
        excluded_hashes = {
            str(value).upper()
            for value in json.loads(args.exclude_sha256_file.read_text(encoding="utf-8"))
        }
    source = [
        item for item in triage["recordings"]
        if not args.relative_prefix or item["relative_path"].startswith(args.relative_prefix)
        if item["sha256"].upper() not in excluded_hashes
    ]

    calls = list(baseline.get("calls", []))
    reviews = list(baseline.get("reviews", []))
    next_review_id = max((item["reviewItemId"] for item in reviews), default=0) + 1
    for index, item in enumerate(sorted(source, key=lambda row: row["relative_path"]), 1):
        call_id = len(baseline.get("calls", [])) + index
        transcript = transcript_by_hash.get(item["sha256"], {})
        route = transcript.get("route", item["triage_class"])
        audio_class = mapped_class(route, item["triage_class"])
        text = transcript.get("transcript_text", "")
        facts = []
        for raw, normalized in TOPICS.items():
            if raw in text and normalized not in {fact["normalizedValue"] for fact in facts}:
                facts.append({
                    "factId": call_id * 100 + len(facts) + 1,
                    "factType": "TOPIC",
                    "rawValue": raw,
                    "normalizedValue": normalized,
                    "unit": None,
                    "startSeconds": None,
                    "endSeconds": None,
                    "confidence": 0.55,
                    "reviewStatus": "OPEN",
                })
        needs_review = audio_class in {"BUSINESS_CONVERSATION", "NEEDS_REVIEW"}
        direction, extension = direction_and_extension(item["file_name"])
        call = {
            "logicalCallId": call_id,
            "runId": call_id,
            "callKey": f"بچ ۱۴۰۵/۰۵/۱۹ · مورد {index:03d}",
            "startedAt": parse_call_time(item["file_name"]),
            "legCount": 1,
            "recordingFile": f"batch-{index:03d}.wav",
            "runStatus": "COMPLETED" if transcript or audio_class == "NON_SPEECH_OR_UNSUPPORTED" else "QUEUED",
            "audioClass": audio_class,
            "direction": direction,
            "internalExtension": extension,
            "confidence": 0.65 if audio_class == "BUSINESS_CONVERSATION" else 0.9 if audio_class == "NON_SPEECH_OR_UNSUPPORTED" else 0.55,
            "summary": summary_for(audio_class, route),
            "factCount": len(facts),
            "openReviewCount": 1 if needs_review else 0,
            "reviewStatuses": ["OPEN"] if needs_review else [],
            "sampleRecording": {
                "recordingAssetId": call_id,
                "originalFileName": f"batch-{index:03d}.wav",
                "processingStatus": "COMPLETED" if transcript or audio_class == "NON_SPEECH_OR_UNSUPPORTED" else "READY",
                "fileSizeBytes": item["file_size_bytes"],
                "completedAtUtc": None,
                "purgedAtUtc": None,
                "lastError": item.get("error"),
            },
            "sampleFacts": facts,
        }
        calls.append(call)
        if needs_review:
            reviews.append({
                "reviewItemId": next_review_id,
                "logicalCallId": call_id,
                "category": "LOW_CONFIDENCE",
                "priority": "MEDIUM" if audio_class == "BUSINESS_CONVERSATION" else "LOW",
                "reasonCode": "FIRST_PASS_TRANSCRIPT_NEEDS_REVIEW" if transcript else "PENDING_FIRST_PASS_TRANSCRIPT",
                "rawText": "این نتیجه از غربال سریع ساخته شده و پیش از استفاده تجاری باید با متن دقیق یا صدای اصلی تطبیق داده شود.",
                "startSeconds": None,
                "endSeconds": None,
                "reviewStatus": "OPEN",
                "resolution": None,
                "createdAtUtc": datetime.now(timezone.utc).isoformat(),
            })
            next_review_id += 1

    metrics = {
        "analysisCount": len(calls),
        "audioFileCount": triage["summary"]["recording_count"],
        "newAudioCount": len(source),
        "totalBytes": triage["summary"]["total_file_size_bytes"],
        "totalDurationSeconds": triage["summary"]["total_duration_seconds"],
        "transcribedNewCount": sum(1 for item in source if item["sha256"] in transcript_by_hash),
    }
    payload = {
        "status": baseline.get("status", {}),
        "metrics": metrics,
        "calls": calls,
        "reviews": reviews,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    temporary = args.output.with_suffix(args.output.suffix + ".part")
    temporary.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temporary, args.output)
    print(json.dumps(metrics, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
