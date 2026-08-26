#!/usr/bin/env python3
"""Build a privacy-safe sales-coaching dashboard payload from local WAV analysis."""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from collections import Counter
from datetime import datetime, timedelta, timezone
from pathlib import Path


TEHRAN = timezone(timedelta(hours=3, minutes=30))
WORD_PATTERN = re.compile(r"[0-9A-Za-zآ-ی]+")

TOPICS = {
    "PRICE": ("قیمت", ("قیمت", "نرخ", "گران", "ارزان")),
    "INVENTORY": ("موجودی", ("موجود", "موجودی", "نداریم", "ناموجود")),
    "PRODUCT": ("محصول و مشخصات", ("تیرآهن", "میلگرد", "ورق", "نبشی", "ناودانی", "سایز", "شاخه")),
    "PAYMENT": ("پرداخت", ("پرداخت", "واریز", "حساب", "چک", "نقد")),
    "DELIVERY": ("ارسال و تحویل", ("تحویل", "بارگیری", "ارسال", "بار")),
    "QUANTITY": ("مقدار و تناژ", ("تناژ", "تن", "کیلو", "شاخه")),
    "INVOICE": ("فاکتور", ("فاکتور", "پیش فاکتور", "پیش‌فاکتور")),
}

NON_PURCHASE_REASONS = {
    "PRICE_TOO_HIGH": ("قیمت بالا", ("گرونه", "گران است", "قیمت بالاست", "قیمت زیاد", "نرخ بالاست")),
    "NO_BUDGET_OR_LIQUIDITY": ("کمبود بودجه یا نقدینگی", ("بودجه ندار", "نقدینگی ندار", "پول ندار")),
    "OUT_OF_STOCK": ("نبود موجودی", ("موجود نیست", "موجود ندار", "نداریم", "ناموجود")),
    "DELIVERY_TIME": ("زمان تحویل", ("دیر میرس", "دیر می‌رس", "زمان تحویل", "تحویل دیر")),
    "PAYMENT_TERMS": ("شرایط پرداخت", ("شرایط پرداخت", "چک قبول", "نقدی نمی", "اعتباری")),
    "COMPETITOR_SELECTED": ("انتخاب تأمین‌کننده دیگر", ("از جای دیگه", "از جای دیگر", "رقیب", "خرید کردم")),
    "NOT_READY": ("آماده نبودن مشتری", ("فعلا نمی", "فعلاً نمی", "بعدا", "بعداً", "تصمیم نگرفتم")),
}

STRENGTHS = {
    "GREETING": ("شروع محترمانه", ("سلام", "وقت بخیر", "در خدمتم")),
    "NEEDS_DISCOVERY": ("پرسش برای کشف نیاز", ("چه سایزی", "چند تن", "چند شاخه", "کدام کارخانه", "کدوم کارخانه", "چه مقداری")),
    "FOLLOW_UP": ("تعهد به پیگیری", ("پیگیری می‌کنم", "پیگیری میکنم", "خبر میدم", "خبر می‌دم", "تماس میگیرم", "تماس می‌گیرم")),
    "DE_ESCALATION": ("تلاش برای آرام‌سازی", ("حق با شماست", "عذر میخوام", "عذر می‌خوام", "اجازه بدید بررسی", "اجازه بدهید بررسی")),
    "RESPECTFUL_CLOSE": ("پایان محترمانه", ("ممنون", "سپاس", "خداحافظ", "روز خوش")),
}

RISKS = {
    "ANGER_OR_ESCALATION": (
        "تنش یا شکایت", "MEDIUM",
        ("داد نزن", "عصبانی", "شکایت", "دیگه زنگ نمی", "دیگر زنگ نمی", "ناراضی"),
        "تماس را مدیر فروش بازبینی کند و فروشنده ابتدا مسئله مشتری را بازگو و سپس راه‌حل و زمان پیگیری مشخص ارائه کند.",
    ),
    "INSULT_OR_PROFANITY": (
        "توهین یا ناسزا", "HIGH",
        ("احمق", "بی شعور", "بی‌شعور", "بیشعور", "حروم", "لعنتی", "نفهم"),
        "فایل و متن توسط انسان بررسی شود؛ در صورت تأیید، مکالمه برای آموزش رفتار حرفه‌ای و اقدام مدیریتی ثبت شود.",
    ),
    "BRIBERY_OR_PERSONAL_PAYMENT": (
        "پرداخت شخصی یا فساد احتمالی", "HIGH",
        ("رشوه", "زیرمیزی", "زیر میزی", "سهم من", "کارت شخصی", "حساب شخصی", "پورسانت من"),
        "هیچ نتیجه‌ای خودکار قطعی نشود؛ مدیر مجاز باید صدا، بافت مکالمه و اسناد مالی را مستقل بررسی کند.",
    ),
}

ACTION_LIBRARY = {
    "ADD_GREETING": ("شروع استاندارد", "با معرفی کوتاه، سلام و اعلام آمادگی برای کمک شروع شود."),
    "DISCOVER_NEED": ("کشف نیاز پیش از قیمت", "محصول، سایز، مقدار، کارخانه و محل تحویل با سؤال روشن ثبت شود."),
    "CONFIRM_NEXT_STEP": ("تعیین قدم بعدی", "در پایان تماس مسئول اقدام و زمان دقیق پیگیری یا ارسال پیش‌فاکتور گفته شود."),
    "RESPECTFUL_CLOSE": ("جمع‌بندی و پایان حرفه‌ای", "خواسته مشتری یک‌بار جمع‌بندی و تماس با تشکر و پایان روشن بسته شود."),
    "HANDLE_PRICE": ("مدیریت اعتراض قیمت", "به‌جای دفاع فوری، بودجه و اولویت مشتری پرسیده و گزینه جایگزین یا اعتبار زمانی قیمت پیشنهاد شود."),
    "VERIFY_SENSITIVE": ("بازبینی مورد حساس", "این نشانه فقط کاندیدای بازبینی است و بدون شنیدن صدا نباید مبنای قضاوت درباره فرد قرار گیرد."),
    "IMPROVE_TRANSCRIPT": ("متن دقیق‌تر", "کیفیت متن برای قضاوت رفتاری کافی نیست؛ این تماس با مدل دقیق‌تر یا بازبینی انسانی پردازش شود."),
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--triage", type=Path, required=True)
    parser.add_argument("--transcripts", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--relative-prefix", default="")
    parser.add_argument("--exclude-sha256-file", type=Path)
    parser.add_argument("--include-baseline", action="store_true")
    return parser.parse_args()


def normalize(value: str) -> str:
    return " ".join(value.replace("ي", "ی").replace("ك", "ک").split())


def parse_call_time(name: str) -> str | None:
    match = re.search(r"-(20\d{6})-(\d{6})-", name)
    if not match:
        return None
    try:
        value = datetime.strptime("".join(match.groups()), "%Y%m%d%H%M%S")
        return value.replace(tzinfo=TEHRAN).astimezone(timezone.utc).isoformat()
    except ValueError:
        return None


def call_label(name: str, sha256: str) -> str:
    match = re.search(r"-(20\d{2})(\d{2})(\d{2})-(\d{2})(\d{2})(\d{2})-", name)
    if not match:
        return f"تماس batch · {sha256[-4:]}"
    _, month, day, hour, minute, _ = match.groups()
    return f"تماس {month}/{day} ساعت {hour}:{minute} · {sha256[-4:]}"


def direction_and_extension(name: str) -> tuple[str, str | None]:
    parts = name.split("-")
    if name.startswith("out-"):
        return "OUTBOUND", parts[2] if len(parts) > 2 and parts[2].isdigit() else None
    if name.startswith("exten-"):
        return "INTERNAL", parts[1] if len(parts) > 1 and parts[1].isdigit() else None
    return "INBOUND", None


def transcript_quality(transcript: dict) -> dict:
    text = normalize(transcript.get("transcript_text", ""))
    tokens = [token for token in WORD_PATTERN.findall(text) if len(token) > 1]
    if not tokens:
        return {"status": "NOT_AVAILABLE", "score": 0, "reason": "متن اولیه موجود نیست."}
    frequencies = Counter(tokens)
    dominant_share = max(frequencies.values()) / len(tokens)
    unique_ratio = len(frequencies) / len(tokens)
    language_probability = float(transcript.get("language_probability") or 0)
    score = 100
    if len(tokens) < 12:
        score -= 35
    if dominant_share >= 0.35:
        score -= 55
    elif dominant_share >= 0.20:
        score -= 30
    elif dominant_share >= 0.12:
        score -= 12
    if len(tokens) >= 40 and unique_ratio < 0.10:
        score -= 45
    elif len(tokens) >= 40 and unique_ratio < 0.22:
        score -= 22
    if language_probability < 0.75:
        score -= 20
    score = max(0, min(100, score))
    status = "HIGH" if score >= 75 else "MEDIUM" if score >= 50 else "LOW"
    reason = {
        "HIGH": "متن اولیه برای استخراج نشانه‌ها قابل استفاده است؛ اعداد همچنان باید کنترل شوند.",
        "MEDIUM": "متن اولیه قابل استفاده محدود است و یافته‌ها باید انسانی تأیید شوند.",
        "LOW": "تکرار یا ابهام متن زیاد است و برای قضاوت رفتاری قابل اتکا نیست.",
    }[status]
    return {
        "status": status, "score": score, "reason": reason,
        "tokenCount": len(tokens),
        "dominantTokenShare": round(dominant_share, 3),
        "uniqueTokenRatio": round(unique_ratio, 3),
    }


def find_matches(text: str, rules: dict) -> list[dict]:
    matches = []
    for code, rule in rules.items():
        label, keywords = rule[0], rule[-1]
        keyword = next((candidate for candidate in keywords if candidate in text), None)
        if keyword:
            matches.append({"code": code, "label": label, "keyword": keyword})
    return matches


def mapped_class(route: str, triage_class: str) -> str:
    if route == "QUEUE_OR_IVR":
        return "QUEUE_ONLY"
    if triage_class in {"NO_SPEECH_OR_DROPPED", "EMPTY_FILE", "SILENCE"}:
        return "NON_SPEECH_OR_UNSUPPORTED"
    if route == "BUSINESS_CONVERSATION_CANDIDATE":
        return "BUSINESS_CONVERSATION"
    return "NEEDS_REVIEW"


def coaching_analysis(text: str, quality: dict, audio_class: str) -> dict:
    if audio_class not in {"BUSINESS_CONVERSATION", "NEEDS_REVIEW"}:
        return {"status": "NOT_APPLICABLE", "score": None, "quality": quality, "strengths": [], "findings": [], "actions": []}
    if quality["status"] in {"LOW", "NOT_AVAILABLE"}:
        title, detail = ACTION_LIBRARY["IMPROVE_TRANSCRIPT"]
        return {
            "status": "TRANSCRIPT_REVIEW", "score": None, "quality": quality,
            "strengths": [], "findings": [],
            "actions": [{"code": "IMPROVE_TRANSCRIPT", "title": title, "detail": detail}],
        }

    topics = find_matches(text, TOPICS)
    reasons = find_matches(text, NON_PURCHASE_REASONS)
    strengths = find_matches(text, STRENGTHS)
    risks = []
    for code, (label, priority, keywords, recommendation) in RISKS.items():
        keyword = next((candidate for candidate in keywords if candidate in text), None)
        if keyword:
            risks.append({
                "code": code, "label": label, "priority": priority,
                "keyword": keyword, "recommendation": recommendation,
            })

    strength_codes = {item["code"] for item in strengths}
    actions = []
    for code, missing in (
        ("ADD_GREETING", "GREETING"),
        ("DISCOVER_NEED", "NEEDS_DISCOVERY"),
        ("CONFIRM_NEXT_STEP", "FOLLOW_UP"),
        ("RESPECTFUL_CLOSE", "RESPECTFUL_CLOSE"),
    ):
        if missing not in strength_codes:
            title, detail = ACTION_LIBRARY[code]
            actions.append({"code": code, "title": title, "detail": detail})
    if any(item["code"] == "PRICE_TOO_HIGH" for item in reasons):
        title, detail = ACTION_LIBRARY["HANDLE_PRICE"]
        actions.insert(0, {"code": "HANDLE_PRICE", "title": title, "detail": detail})
    if risks:
        title, detail = ACTION_LIBRARY["VERIFY_SENSITIVE"]
        actions.insert(0, {"code": "VERIFY_SENSITIVE", "title": title, "detail": detail})

    score = 40
    score += 12 if "GREETING" in strength_codes else 0
    score += 18 if "NEEDS_DISCOVERY" in strength_codes else 0
    score += 18 if "FOLLOW_UP" in strength_codes else 0
    score += 12 if "RESPECTFUL_CLOSE" in strength_codes else 0
    score += 10 if "DE_ESCALATION" in strength_codes else 0
    score = max(0, min(100, score))
    return {
        "status": "READY", "score": score, "quality": quality,
        "topics": topics, "nonPurchaseReasons": reasons, "strengths": strengths,
        "risks": risks, "findings": reasons + risks, "actions": actions[:4],
    }


def fact(call_id: int, index: int, fact_type: str, value: str, confidence: float, status: str = "OPEN") -> dict:
    return {
        "factId": call_id * 20 + index, "factType": fact_type,
        "rawValue": None, "normalizedValue": value, "unit": None,
        "startSeconds": None, "endSeconds": None,
        "confidence": confidence, "reviewStatus": status,
    }


def summary_for(audio_class: str, coaching: dict) -> str:
    if audio_class == "QUEUE_ONLY":
        return "صدای صف یا پیام خودکار شناسایی شد؛ برای مربی‌گری فروش استفاده نمی‌شود."
    if audio_class == "NON_SPEECH_OR_UNSUPPORTED":
        return "گفتار قابل اتکا شناسایی نشد؛ تماس کوتاه، قطع‌شده یا بدون مکالمه است."
    if coaching["status"] == "TRANSCRIPT_REVIEW":
        return "مکالمه دارای گفتار است، اما کیفیت متن برای بازخورد رفتاری کافی نیست و باید دقیق‌تر پردازش شود."
    topics = [item["label"] for item in coaching.get("topics", [])]
    reasons = [item["label"] for item in coaching.get("nonPurchaseReasons", [])]
    parts = ["مکالمه قابل استفاده برای مربی‌گری فروش"]
    if topics:
        parts.append(f"موضوع: {'، '.join(topics[:3])}")
    if reasons:
        parts.append(f"دلیل احتمالی عدم خرید: {'، '.join(reasons[:2])}")
    if coaching.get("risks"):
        parts.append("یک نشانه حساس صرفاً برای بازبینی انسانی")
    return "؛ ".join(parts) + "."


def build_reviews(call_id: int, coaching: dict, created: str, first_id: int) -> list[dict]:
    reviews = []
    review_id = first_id
    quality = coaching["quality"]
    if coaching["status"] == "TRANSCRIPT_REVIEW":
        reviews.append({
            "reviewItemId": review_id, "logicalCallId": call_id,
            "category": "TRANSCRIPT_QUALITY", "priority": "LOW",
            "reasonCode": "REPETITION_OR_LOW_INFORMATION",
            "rawText": quality["reason"], "recommendation": ACTION_LIBRARY["IMPROVE_TRANSCRIPT"][1],
            "startSeconds": None, "endSeconds": None, "reviewStatus": "OPEN",
            "resolution": None, "createdAtUtc": created,
        })
        return reviews
    for item in coaching.get("nonPurchaseReasons", []):
        reviews.append({
            "reviewItemId": review_id, "logicalCallId": call_id,
            "category": "NON_PURCHASE_REASON", "priority": "MEDIUM",
            "reasonCode": item["code"],
            "rawText": f"نشانه احتمالی «{item['label']}» در متن اولیه دیده شد.",
            "recommendation": "دلیل واقعی با مشتری یا فروشنده تأیید و سپس برای تحلیل فروش ثبت شود.",
            "startSeconds": None, "endSeconds": None, "reviewStatus": "OPEN",
            "resolution": None, "createdAtUtc": created,
        })
        review_id += 1
    for item in coaching.get("risks", []):
        reviews.append({
            "reviewItemId": review_id, "logicalCallId": call_id,
            "category": item["code"], "priority": item["priority"],
            "reasonCode": "HUMAN_CONFIRMATION_REQUIRED",
            "rawText": f"نشانه احتمالی «{item['label']}» تشخیص داده شد؛ این نتیجه اتهام یا حکم قطعی نیست.",
            "recommendation": item["recommendation"],
            "startSeconds": None, "endSeconds": None, "reviewStatus": "OPEN",
            "resolution": None, "createdAtUtc": created,
        })
        review_id += 1
    return reviews


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    args = parse_args()
    baseline = json.loads(args.baseline.read_text(encoding="utf-8")) if args.baseline and args.baseline.is_file() else {}
    triage = json.loads(args.triage.read_text(encoding="utf-8"))
    transcribed = json.loads(args.transcripts.read_text(encoding="utf-8")) if args.transcripts.is_file() else {"recordings": []}
    transcript_by_hash = {item["sha256"].upper(): item for item in transcribed.get("recordings", [])}
    excluded_hashes = set()
    if args.exclude_sha256_file:
        excluded_hashes = {str(value).upper() for value in json.loads(args.exclude_sha256_file.read_text(encoding="utf-8"))}
    source = [
        item for item in triage["recordings"]
        if (not args.relative_prefix or item["relative_path"].startswith(args.relative_prefix))
        and item["sha256"].upper() not in excluded_hashes
    ]

    calls = list(baseline.get("calls", [])) if args.include_baseline else []
    reviews = list(baseline.get("reviews", [])) if args.include_baseline else []
    created = datetime.now(timezone.utc).isoformat()
    action_counts: Counter[str] = Counter()
    topic_counts: Counter[str] = Counter()
    reason_counts: Counter[str] = Counter()
    quality_counts: Counter[str] = Counter()
    sensitive_count = 0
    ready_count = 0

    for item in sorted(source, key=lambda row: (parse_call_time(row["file_name"]) or "", row["sha256"]), reverse=True):
        call_id = int(item["sha256"][:10], 16)
        transcript = transcript_by_hash.get(item["sha256"].upper(), {})
        route = transcript.get("route", item["triage_class"])
        audio_class = mapped_class(route, item["triage_class"])
        quality = transcript_quality(transcript)
        quality_counts[quality["status"]] += 1
        text = normalize(transcript.get("transcript_text", ""))
        coaching = coaching_analysis(text, quality, audio_class)
        if coaching["status"] == "READY":
            ready_count += 1
        for action in coaching.get("actions", []):
            action_counts[action["code"]] += 1
        for topic in coaching.get("topics", []):
            topic_counts[topic["code"]] += 1
        for reason in coaching.get("nonPurchaseReasons", []):
            reason_counts[reason["code"]] += 1
        sensitive_count += len(coaching.get("risks", []))

        facts = []
        confidence = 0.78 if quality["status"] == "HIGH" else 0.58
        for match in coaching.get("topics", []):
            facts.append(fact(call_id, len(facts) + 1, "TOPIC", match["label"], confidence, "AUTO_ACCEPTED"))
        for match in coaching.get("nonPurchaseReasons", []):
            facts.append(fact(call_id, len(facts) + 1, "NON_PURCHASE_REASON", match["label"], confidence))
        for match in coaching.get("strengths", []):
            facts.append(fact(call_id, len(facts) + 1, "SELLER_STRENGTH", match["label"], confidence))
        for match in coaching.get("risks", []):
            facts.append(fact(call_id, len(facts) + 1, "RISK_SIGNAL", match["label"], min(confidence, 0.55)))

        call_reviews = build_reviews(call_id, coaching, created, call_id * 20 + 10)
        reviews.extend(call_reviews)
        direction, extension = direction_and_extension(item["file_name"])
        calls.append({
            "logicalCallId": call_id, "runId": call_id,
            "callKey": call_label(item["file_name"], item["sha256"]),
            "startedAt": parse_call_time(item["file_name"]), "legCount": 1,
            "recordingFile": f"batch-{item['sha256'][-8:]}.wav",
            "runStatus": "COMPLETED" if transcript or audio_class == "NON_SPEECH_OR_UNSUPPORTED" else "QUEUED",
            "audioClass": audio_class, "direction": direction, "internalExtension": extension,
            "confidence": round(confidence if coaching["status"] == "READY" else quality["score"] / 100, 2),
            "summary": summary_for(audio_class, coaching),
            "coachingScore": coaching.get("score"), "transcriptQuality": quality["status"],
            "factCount": len(facts), "openReviewCount": len(call_reviews),
            "reviewStatuses": ["OPEN"] if call_reviews else [],
            "sampleRecording": {
                "recordingAssetId": call_id, "originalFileName": f"batch-{item['sha256'][-8:]}.wav",
                "processingStatus": "COMPLETED" if transcript or audio_class == "NON_SPEECH_OR_UNSUPPORTED" else "READY",
                "fileSizeBytes": item["file_size_bytes"], "completedAtUtc": transcribed.get("updated_at_utc"),
                "purgedAtUtc": None, "lastError": item.get("error"),
            },
            "sampleFacts": facts, "sampleCoaching": coaching,
        })

    def ranked(counter: Counter[str], catalog: dict, take: int = 5) -> list[dict]:
        return [
            {"code": code, "label": catalog[code][0], "count": count}
            for code, count in counter.most_common(take) if code in catalog
        ]

    priority_actions = []
    for code, count in action_counts.most_common(5):
        title, detail = ACTION_LIBRARY[code]
        priority_actions.append({"code": code, "title": title, "detail": detail, "count": count})

    metrics = {
        "analysisCount": len(calls), "audioFileCount": len(source), "newAudioCount": len(source),
        "totalBytes": sum(int(item.get("file_size_bytes") or 0) for item in source),
        "totalDurationSeconds": round(sum(float(item.get("duration_seconds") or 0) for item in source), 3),
        "transcribedNewCount": sum(1 for item in source if item["sha256"].upper() in transcript_by_hash),
        "coachingReadyCount": ready_count, "sensitiveReviewCount": sensitive_count,
    }
    coaching_overview = {
        "readyConversationCount": ready_count,
        "transcriptReviewCount": quality_counts["LOW"] + quality_counts["NOT_AVAILABLE"],
        "nonPurchaseFindingCount": sum(reason_counts.values()),
        "sensitiveReviewCount": sensitive_count,
        "qualityCounts": dict(quality_counts),
        "topTopics": ranked(topic_counts, TOPICS),
        "topNonPurchaseReasons": ranked(reason_counts, NON_PURCHASE_REASONS),
        "priorityActions": priority_actions,
        "notice": "امتیازها میزان پوشش اجزای مکالمه را نشان می‌دهند، نه ارزیابی قطعی عملکرد فروشنده. نسبت‌دادن گفتار به فروشنده بدون تفکیک گوینده ممکن نیست.",
    }
    payload = {
        "schemaVersion": 2, "generatedAtUtc": created, "dataMode": "MANUAL_BATCH",
        "status": baseline.get("status", {}), "metrics": metrics, "coaching": coaching_overview,
        "calls": calls, "reviews": reviews,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    temporary = args.output.with_suffix(args.output.suffix + ".part")
    temporary.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temporary, args.output)
    print(json.dumps({"metrics": metrics, "coaching": coaching_overview}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
