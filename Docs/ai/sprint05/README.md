# Sprint 0.5 - Real Audio Benchmark

## Goal

Establish a measured Persian speech-to-text baseline on a real, mono, 8 kHz
call recording before Sprint 1 architecture is approved. Speaker diarization
and seller/customer attribution are explicitly deferred to a later sprint.

## Privacy boundary

- Audio stays on the local machine.
- Model inference is local after model download.
- Audio, transcripts, phone numbers, call identifiers, and hashes must not be
  committed or pushed.
- Only aggregate benchmark findings may enter source control after review.

## Selected baseline

- Engine: Faster-Whisper 1.2.1
- Model: multilingual `small`
- Device: CPU
- Quantization: INT8
- Language forced to Persian (`fa`)
- VAD enabled
- Word timestamps enabled

The `small` model is the first quality/speed checkpoint. A smaller model may be
tested for throughput, and a larger model only if the host proves capable.

## Run contract

Use `Tools/ai/transcribe_sample.py`. Store the Python packages, downloaded
models, audio, and generated transcript outside the repository.

The generated JSON records timestamps, confidence-related signals, model load
time, transcription time, and real-time factor. It does not perform speaker
diarization. Because the source is mono, seller/customer attribution requires
either a separate diarization stage plus CDR role mapping or manual annotation.

`Tools/ai/prepare_annotation.py` creates a private UTF-8 CSV for human review.
The reviewer fills `corrected_text`, assigns `speaker` and `role`, and changes
`review_status` to `DONE`. `Tools/ai/score_annotation.py` then reports aggregate
CER and exact numeric-token recall without printing call content.

## Exit criteria

- Produce a timestamped Persian transcript for the sample.
- Manually correct the transcript to create a reference annotation.
- Report word-level or character-level error rate against that reference.
- Report real-time factor and peak memory on the target host.
- Record a GO/NO-GO recommendation for Sprint 1.

## Current status

The reproducible runner and experiment contract are ready. Faster-Whisper and
its CPU dependencies are installed in an isolated local runtime. The first
`small`-model run completed, but its raw transcript is not accurate enough for
business use. A stronger-model comparison and manual reference annotation are
still required before a Sprint 1 GO decision.
