# Sprint 0 Recording Discovery

## SQL facts

| Measure | Value |
|---|---:|
| Raw RecordingFile references | 6,918 of 7,449 rows |
| Logical calls with a reference | 950 of 1,241 calls |
| Logical call reference coverage | 76.55% |
| Logical calls without a reference | 291 (23.45%) |
| Detected extension | `.wav` for all 6,918 populated rows |
| Stored path kind | relative filename for all populated rows |

No absolute Linux, Windows, UNC or SFTP path is stored in `RawCDR`.

## Transport discovery

- The Windows host contains no call `.wav` files under `D:\DigiAhan\CDR4.0`.
- Existing Issabel integration config contains the dashboard URL and AMI settings but no SFTP/SSH recording transport.
- No mapping from relative `RecordingFile` to an Issabel recording root is configured in v4.3.1.

## Read-only access proof

**Not completed.** There is no approved read-only SFTP/SSH account or mounted recording path available in the current environment. Reading an arbitrary production recording through AMI/root access was not attempted.

## Required next action

Operations must provide one of:

1. a restricted SFTP account rooted at the approved recording directory, or
2. a read-only network export/mount, or
3. one explicitly approved test recording plus its exact resolution rule.

The account must not permit rename, delete or write. Sprint 1 remains blocked until one referenced file is resolved and opened read-only without modification.
