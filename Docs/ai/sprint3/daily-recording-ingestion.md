# Daily Issabel Recording Ingestion

## Current scope

- Process only the configured business date: today (`0`) or, for a controlled recovery, yesterday (`-1`).
- Do not backfill historical audio.
- Do not expose playback or a streaming API.
- Keep the original recording on Issabel.
- Use Windows storage only as a bounded processing staging area.
- Delete staged WAV data after transcript and structured analysis are committed successfully.

## Data flow

```text
RawCDR
  -> LinkedId logical-call grouping and stabilization
  -> unique recordingfile / AI run
  -> /var/spool/asterisk/monitor/YYYY/MM/DD/filename.wav
  -> read-only targeted OpenSSH SFTP get
  -> same-directory .part file
  -> remote size + RIFF/WAVE + SHA-256 validation
  -> atomic rename
  -> local Faster-Whisper transcription
  -> Persian steel-sales rules and review signals
  -> SQL transcript, facts and review queue
  -> staged WAV purge
```

The resolver always uses Gregorian, invariant formatting. This is required on a Persian-locale Windows host;
ordinary `yyyy/MM/dd` interpolation can otherwise produce a Solar Hijri path.

## Security requirements

- `RecordingIngestion:Enabled` defaults to `false`.
- Authentication is key-based and non-interactive. Password automation is intentionally unsupported.
- `StrictHostKeyChecking=yes` is mandatory.
- `KnownHostsPath` must point to a file whose Issabel host key was verified out of band.
- The service private key must be readable only by the Windows service identity.
- The Issabel account must be read-only and unable to rename, upload or delete recordings.
- CDR filenames are treated as untrusted input; absolute paths outside the monitor root and traversal segments are rejected.
- The worker never scans `/`, Issabel sound folders or the complete monitor tree.

Do not place credentials in the committed example file. Supply them through the ignored
`Source/appsettings.RecordingIngestion.local.json` file or environment variables such as:

```text
RecordingIngestion__PrivateKeyPath
RecordingIngestion__KnownHostsPath
RecordingIngestion__Username
```

## Required migrations

Apply in order:

1. `Sql/AiPipelineVNext.sql`
2. `Sql/AiAnalysisVNext.sql`
3. `Sql/AiRecordingSyncVNext.sql`

All three migrations are repeatable and are exercised twice by the isolated migration test.

## AI outputs

The first production taxonomy stores evidence and timestamps for:

- steel brands and commercial topics;
- likely non-purchase reasons;
- follow-up and seller-behaviour indicators;
- anger or escalation;
- insult or profanity signals;
- bribery or personal-payment risk signals.

Sensitive findings are signals, not allegations. They are always placed in the human review queue and must not trigger an automatic personnel or legal action.

## Activation checklist

1. Apply the three SQL migrations.
2. Create the read-only Issabel SFTP account and private key.
3. Verify and install the Issabel host key in the dedicated `known_hosts` file.
4. Configure the Faster-Whisper Python executable, package path and model cache.
5. Keep `TargetDateOffsetDays=0` for the normal daily flow.
6. Set `RecordingIngestion:Enabled=true` only after a read-only test file succeeds.

Until items 2 and 3 exist, the worker remains safely disabled and no request is made to Issabel.
