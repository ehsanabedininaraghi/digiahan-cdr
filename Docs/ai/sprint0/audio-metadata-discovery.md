# Sprint 0 Audio Metadata Discovery

## Status

Audio metadata was measured for one user-approved PBX recording sample using a read-only RIFF/WAVE parser. `ffmpeg` and `ffprobe` remain unavailable.

## Measured sample

| Field | Value |
|---|---:|
| Container | RIFF/WAVE |
| Codec | signed PCM, format code 1 |
| Channels | 1 (mono) |
| Sample rate | 8,000 Hz |
| Bits per sample | 16 |
| Byte rate | 16,000 bytes/s |
| Audio data | 1,576,000 bytes |
| Sample frames | 788,000 |
| Calculated duration | 98.5 seconds |
| RMS level | -16.55 dBFS |
| Peak | 0 dBFS |
| Exact-zero samples | 0.95% |

The calculated duration agrees with the provided `00:01:38` description and the principal answered CDR leg (`Duration=98`). Mono audio means agent and customer are not available as separate channels; diarization or conversation-role mapping will be required.

## Still unknown

- codec/channel distribution across the population;
- corrupt/truncated-file rate;
- file size and audio-duration agreement;
- read-while-write behavior on Issabel.

## Gate

After production read-only transport is provided, inspect a stratified sample rather than extrapolating from this single file. No package installation was performed during Sprint 0.
