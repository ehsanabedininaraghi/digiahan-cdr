# Sprint 0 Database Discovery

## Connection and safety

- Database: `DigiAhan_CDR`
- SQL Server is local and uses Integrated Security.
- TCP connection was unavailable; local shared memory (`lpc:localhost`) succeeded.
- Discovery used `SELECT` only, `READ UNCOMMITTED`, a five-second lock timeout and bounded command timeouts.
- No table, row, procedure or configuration was changed.

## RawCDR schema

`dbo.RawCDR` exists with 33 columns:

| Columns | Types/notes |
|---|---|
| `RawCDRId` | `bigint`, PK, not null |
| `SourceServer` | `nvarchar(100)`, not null |
| `Calldate` | `datetime2(3)`, nullable |
| `Clid`, `Cnam`, `OutboundCnam`, `DstCnam` | caller/display metadata |
| `Src`, `Dst`, `Dcontext` | route/direction inputs |
| `Channel`, `DstChannel` | Asterisk channels |
| `LastApp`, `LastData` | last dialplan application/data |
| `Duration`, `Billsec` | integer seconds |
| `Disposition`, `Amaflags`, `AccountCode` | CDR status/account fields |
| `UniqueId`, `LinkedId` | Asterisk identifiers |
| `RecordingFile` | `nvarchar(500)`, nullable |
| `Cnum`, `OutboundCnum`, `Did`, `PeerAccount` | phone/routing metadata |
| `Sequence`, `SequenceNo` | sequence metadata |
| `SourceRowKey` | source-side row identity |
| `Fingerprint` | `char(64)`, unique and not null |
| `ReceivedAtUtc` | `datetime2(3)`, not null |
| `BatchId` | `uniqueidentifier`, nullable |

The raw machine-readable schema is in `output/sprint0-discovery.json`.

## RawCDR indexes

- `PK_RawCDR(RawCDRId)`
- `UX_RawCDR_Fingerprint(Fingerprint)`
- `IX_RawCDR_Calldate(Calldate)`
- `IX_RawCDR_Calldate_LinkedId(Calldate, LinkedId)`
- `IX_RawCDR_LinkedId(LinkedId)`
- `IX_RawCDR_UniqueId(UniqueId)`
- `IX_RawCDR_ReceivedAtUtc(ReceivedAtUtc)`
- `IX_RawCDR_Src_Dst(Src, Dst)`

## Measured coverage

Measurement window: 2026-07-27 through 2026-08-08. The first and last days are partial.

| Measure | Value |
|---|---:|
| Raw rows | 7,449 |
| Logical calls using namespaced LinkedId grouping | 1,241 |
| Raw rows with LinkedId | 7,449 (100%) |
| Raw rows with UniqueId | 7,449 (100%) |
| Raw rows with RecordingFile | 6,918 (92.87%) |
| Logical calls with at least one RecordingFile | 950 (76.55%) |

All samples written to JSON are masked. Full phone numbers, customer names and raw recording paths are not emitted.

## Important database finding

An initial batch lookup from candidate calls into `CustomerPhoneDirectory` timed out at 30 seconds. Per-call customer lookup exists, but a bulk AI enrichment path must be designed with normalized keys, bounded batches and a query plan review. The candidate manifest therefore marks customer class `UNKNOWN` rather than issuing a heavier query.
