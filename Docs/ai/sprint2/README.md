# Sprint 2 - Structured Analysis and Review Queue

## Input learned from the first six recordings

- Four recordings contain business conversations.
- One recording contains only the queue waiting prompt.
- One recording has a strong signal but no detected Persian speech.
- `ANSWERED` on one CDR leg does not prove a human conversation.
- `NO ANSWER` on a representative leg does not prove that the recording is unusable.

## Implemented schema

- `AiCallAssessments`: audio class, speech/business flags, CDR direction and
  extension, confidence, summary, and structured JSON.
- `AiExtractedFacts`: typed product, brand, size, quantity, price, action,
  date, person, company, and topic candidates with evidence timestamps.
- `AiReviewItems`: explicit exceptions with reason, priority, evidence range,
  resolution, and audit fields.

## Decision policy

1. CDR supplies routing facts; speech models do not overwrite them.
2. Queue-only and non-speech recordings do not enter business extraction.
3. Product, quantity, price, and unit are stored separately.
4. Unresolved numeric associations are review items, not accepted facts.
5. New corrections grow the domain dictionary and extraction rules.

Speaker diarization remains deferred by product decision.
