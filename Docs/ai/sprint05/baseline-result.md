# Sprint 0.5 Baseline Result

## Test input

- One real telephone recording
- PCM WAV, mono, 8 kHz, 16-bit
- Duration: 98.5 seconds
- Speech after VAD: 91.008 seconds
- Processing boundary: local machine only

The recording name, call identifiers, transcript, phone numbers, and content
are intentionally excluded from this repository report.

## Runtime

- Windows 10 (10.0.19045)
- Faster-Whisper 1.2.1
- CTranslate2 4.8.1
- CPU INT8 inference

## Measured runs

| Model | Threads | Model load | Transcription | Real-time factor | Result |
|---|---:|---:|---:|---:|---|
| `small` | 2 | 223.211 s | 240.437 s | 2.441 | Not usable |
| `large-v3-turbo` | 4 | 1119.083 s* | 252.613 s | 2.565 | Better, still not production-ready |
| `large-v3-turbo` (cached/offline) | 4 | 5.865 s | 199.543 s | 2.026 | Repeatable local baseline |

\* The first large-model load includes a slow, resumed network download. It is
not representative of cached startup time and must be measured again offline.

## Qualitative findings

- Both models detected Persian with probability 1.0.
- The stronger model recovered the broad business topic and conversation flow
  more consistently than `small`.
- Product/brand names, colloquial speech, prices, quantities, and numbers still
  contain material errors.
- The stronger model is also slower than real time on the target CPU: about
  2.57 seconds of processing per second of audio.
- Mono audio contains both parties in one channel. Faster-Whisper provides
  timestamps but no speaker identity, so seller/customer attribution cannot be
  claimed from this output.
- No WER/CER is reported yet because there is no human-corrected reference.

## Decision

The reduced Sprint 0.5 scope is complete: a full timestamped raw transcript was
produced locally. Speaker diarization and human-reference accuracy scoring were
deferred by product decision. Sprint 1 foundation work may proceed, but enabling
a production transcription pipeline remains **NO-GO** because measured quality
and target-host throughput do not meet a production baseline.

## Required next experiment

1. Test a Persian/domain-adapted model or a stronger inference host.
2. Repeat on a representative, consented sample set before changing GO status.
3. Revisit human correction, CER, and diarization in the deferred quality sprint.
