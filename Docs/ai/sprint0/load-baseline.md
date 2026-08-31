# Sprint 0 Load Baseline

## Definitions

- Logical call: rows grouped by namespaced `LinkedId`, falling back to namespaced `UniqueId` and `RawCDRId`.
- Call start: minimum `Calldate` in the group.
- Approximate completion: maximum `Calldate + Duration` in the group.
- Duration: maximum raw CDR `Duration` in the group, matching current dashboard behavior.
- Working hours used for the provisional baseline: 08:00–18:00 server time.
- Five-minute percentiles currently include non-empty working-hour buckets only. Zero buckets are excluded and the metric is labeled accordingly.

## Window and volume

- Window: 2026-07-27 through 2026-08-08; boundary days are partial.
- Raw rows: 7,449.
- Logical calls: 1,241.
- Average across 13 represented dates: 95.46 calls/date. This is descriptive, not a capacity SLA.
- Calls occurring during the provisional working-hour range: 1,195.

## Call outcomes and direction

| Measure | Value |
|---|---:|
| Answered logical calls | 1,101 (88.72%) |
| Mutually classified inbound | 941 |
| Mutually classified outbound | 292 |
| Unknown direction | 8 |

Direction uses the existing dashboard rules and still requires business validation.

## Duration

| Percentile | Seconds |
|---|---:|
| P50 | 24 |
| P95 | 134 |
| P99 | 244.6 |

## Completed calls per five minutes

Measured over 556 non-empty working-hour buckets:

| Measure | Calls |
|---|---:|
| P50 | 2 |
| P95 | 5 |
| P99 | 6 |
| Maximum | 8 |

## Recording and grouping rates

| Measure | Value |
|---|---:|
| Call-level recording-reference coverage | 76.55% |
| Missing call-level recording reference | 23.45% |
| Multi-leg call rate | 53.10% |
| Leg-arrival spread over 90 seconds | 3.55% |

## Initial capacity interpretation

The call arrival rate is compatible with a small durable queue, but no STT throughput claim can be made before audio length/codec and target hardware are known. The inspected machine has no suitable dedicated GPU, so local `large-v3` throughput must not be inferred from these call rates.
