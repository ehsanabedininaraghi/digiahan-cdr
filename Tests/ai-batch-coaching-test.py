#!/usr/bin/env python3
"""Small regression checks for privacy and conservative coaching rules."""

from __future__ import annotations

import importlib.util
import json
import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "tools" / "ai" / "generate_safe_dashboard_batch.py"
SPEC = importlib.util.spec_from_file_location("batch_coaching", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def expect(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


clean_text = (
    "سلام وقت بخیر برای میلگرد چه سایزی و چند تن نیاز دارید قیمت و موجودی را بررسی می‌کنم "
    "محل تحویل را بفرمایید خبر می‌دهم ممنون روز خوش"
)
clean_transcript = {"transcript_text": clean_text, "language_probability": 0.99}
clean_quality = MODULE.transcript_quality(clean_transcript)
expect(clean_quality["status"] in {"HIGH", "MEDIUM"}, "Clean transcript was rejected.")
clean_coaching = MODULE.coaching_analysis(clean_text, clean_quality, "BUSINESS_CONVERSATION")
expect(clean_coaching["status"] == "READY", "Clean business call was not coaching-ready.")
expect(any(item["code"] == "NEEDS_DISCOVERY" for item in clean_coaching["strengths"]), "Need discovery was not found.")

repeated_text = ("اینجا " * 120) + "حساب شخصی"
repeated_transcript = {"transcript_text": repeated_text, "language_probability": 1.0}
repeated_quality = MODULE.transcript_quality(repeated_transcript)
repeated_coaching = MODULE.coaching_analysis(repeated_text, repeated_quality, "BUSINESS_CONVERSATION")
expect(repeated_quality["status"] == "LOW", "Hallucinated repetition was not rejected.")
expect(not repeated_coaching.get("risks"), "Sensitive claim escaped the low-quality gate.")

risk_text = clean_text + " مشتری پرسید آیا مبلغ را به حساب شخصی واریز کند"
risk_quality = MODULE.transcript_quality({"transcript_text": risk_text, "language_probability": 0.99})
risk_coaching = MODULE.coaching_analysis(risk_text, risk_quality, "BUSINESS_CONVERSATION")
expect(any(item["code"] == "BRIBERY_OR_PERSONAL_PAYMENT" for item in risk_coaching["risks"]), "Sensitive review candidate was not detected.")

if len(sys.argv) > 1:
    payload_path = Path(sys.argv[1])
    payload = json.loads(payload_path.read_text(encoding="utf-8"))
    serialized = json.dumps(payload, ensure_ascii=False)
    expect(payload.get("dataMode") == "MANUAL_BATCH", "Payload is not marked as manual batch.")
    expect(len(payload.get("calls", [])) == payload["metrics"]["analysisCount"], "Call metric mismatch.")
    expect(not re.search(r"(?<!\d)09\d{9}(?!\d)", serialized), "A mobile number leaked to dashboard payload.")
    expect(not re.search(r"(?i)(?:exten|out|q)-[^\s\"]+\.wav", serialized), "A raw recording name leaked to dashboard payload.")

print("AI batch coaching regression checks passed.")
