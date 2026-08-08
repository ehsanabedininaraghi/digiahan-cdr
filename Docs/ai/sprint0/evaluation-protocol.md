# Golden Set Evaluation Protocol — Draft

## Split

- Development/tuning: prompt and glossary iteration allowed.
- Calibration: thresholds and confidence calibration only.
- Held-out test: no prompt, glossary or model tuning after inspection.

The final split must be customer-aware where practical to reduce leakage across repeated calls from the same customer.

## Metrics

- WER and CER;
- numeric exact match;
- product/grade/dimension/quantity/price precision, recall and F1;
- diarization error rate;
- agent/customer role accuracy;
- objection and next-action evidence precision;
- latency, failure rate and resource utilization.

Every metric report must state its denominator, excluded/unknown cases, model version, prompt version, glossary version and dataset split hash.

## Current status

This protocol has not been executed because no approved recording is reachable and audio metadata is unknown.
