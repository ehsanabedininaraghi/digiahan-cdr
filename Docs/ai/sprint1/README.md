# Sprint 1 - AI Pipeline Foundation

## Implemented foundation

- Idempotent SQL migration for logical calls, pipeline runs, and transcripts.
- Logical-call grouping uses namespaced `LinkedId`, then `UniqueId`, then raw ID.
- Stabilization and late-leg states: `STABILIZING`, `FINALIZED`, `REOPENED`.
- A new source maximum raw CDR ID creates at most one new pipeline run.
- Calls with zero or multiple recording references are not queued for audio work.
- A bounded discovery worker and configuration are present.
- An isolated SQL integration test covers repeatable migration, idempotent
  discovery, recording-reference filtering, and late-leg reprocessing.
- The worker is disabled by default; v4.3.1 behavior is unchanged until migration,
  operational validation, and explicit enablement.

## Deferred

- Recording transport from Issabel to the analyzer host.
- Transcription-run leasing and execution.
- Speaker diarization and role attribution.
- Production database migration and enabling the worker.

## Safety gate

Do not set `AiPipeline:Enabled` to `true` until `AiPipelineVNext.sql` has been
reviewed and applied in a non-production database and the extreme multi-leg
groups identified in Sprint 0 have been validated.
