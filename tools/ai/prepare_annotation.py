#!/usr/bin/env python3
"""Create a private human-review CSV from a Faster-Whisper result."""

from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("transcript", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    data = json.loads(args.transcript.read_text(encoding="utf-8"))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(
            stream,
            fieldnames=[
                "segment_id",
                "start_seconds",
                "end_seconds",
                "speaker",
                "role",
                "model_text",
                "corrected_text",
                "review_status",
                "notes",
            ],
        )
        writer.writeheader()
        for index, segment in enumerate(data["segments"], start=1):
            writer.writerow(
                {
                    "segment_id": index,
                    "start_seconds": segment["start"],
                    "end_seconds": segment["end"],
                    "speaker": "UNKNOWN",
                    "role": "UNKNOWN",
                    "model_text": segment["text"],
                    "corrected_text": "",
                    "review_status": "PENDING",
                    "notes": "",
                }
            )
    print(args.output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
