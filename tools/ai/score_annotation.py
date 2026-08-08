#!/usr/bin/env python3
"""Score reviewed transcript text without exporting call content."""

from __future__ import annotations

import argparse
import csv
import json
import re
from pathlib import Path


SPACE = re.compile(r"\s+")
NUMBER = re.compile(r"[0-9۰-۹٠-٩]+(?:[.,٬٫][0-9۰-۹٠-٩]+)*")


def normalize(value: str) -> str:
    value = value.replace("ي", "ی").replace("ك", "ک")
    return SPACE.sub(" ", value.strip())


def edit_distance(left: str, right: str) -> int:
    previous = list(range(len(right) + 1))
    for i, left_item in enumerate(left, start=1):
        current = [i]
        for j, right_item in enumerate(right, start=1):
            current.append(
                min(
                    current[-1] + 1,
                    previous[j] + 1,
                    previous[j - 1] + (left_item != right_item),
                )
            )
        previous = current
    return previous[-1]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("annotation", type=Path)
    args = parser.parse_args()

    hypothesis_parts: list[str] = []
    reference_parts: list[str] = []
    roles: dict[str, int] = {}
    pending = 0
    with args.annotation.open(encoding="utf-8-sig", newline="") as stream:
        for row in csv.DictReader(stream):
            if row["review_status"].strip().upper() != "DONE":
                pending += 1
                continue
            hypothesis_parts.append(row["model_text"])
            reference_parts.append(row["corrected_text"])
            role = row["role"].strip().upper() or "UNKNOWN"
            roles[role] = roles.get(role, 0) + 1

    hypothesis = normalize(" ".join(hypothesis_parts))
    reference = normalize(" ".join(reference_parts))
    if not reference:
        raise SystemExit("No DONE rows with corrected_text were found.")

    distance = edit_distance(hypothesis, reference)
    reference_numbers = NUMBER.findall(reference)
    hypothesis_numbers = NUMBER.findall(hypothesis)
    exact_number_matches = sum(
        1 for number in reference_numbers if number in hypothesis_numbers
    )
    result = {
        "reviewed_segments": len(reference_parts),
        "pending_segments": pending,
        "reference_characters": len(reference),
        "character_errors": distance,
        "cer": round(distance / len(reference), 4),
        "reference_numeric_tokens": len(reference_numbers),
        "exact_numeric_token_recall": (
            round(exact_number_matches / len(reference_numbers), 4)
            if reference_numbers
            else None
        ),
        "role_segment_counts": roles,
    }
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
