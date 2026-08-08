# Sprint 0 Audio Metadata Discovery

## Status

Audio metadata could not be measured.

Reasons:

- recording references are relative filenames;
- no read-only recording transport/root mapping is configured;
- no approved test recording is locally available;
- `ffmpeg` and `ffprobe` are not installed on the inspected Windows host.

## Known facts

- SQL references use `.wav` consistently in the measured window.
- `.wav` is a container/filename extension and does not prove the codec, sample rate, channel count or whether agent/customer are separated.

## Still unknown

- actual codec;
- sample rate;
- mono/stereo/channel layout;
- whether RX/TX are separate channels;
- corrupt/truncated-file rate;
- file size and audio-duration agreement;
- read-while-write behavior on Issabel.

## Gate

After read-only access is provided, inspect a small approved sample with `ffprobe` or an equivalent pre-approved tool. No package installation was performed during Sprint 0.
