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
- Issabel's live recording root is confirmed as `/var/spool/asterisk/monitor/`.
- When `recordingfile` contains only a filename, Issabel extracts the `YYYYMMDD` token and resolves
  `/var/spool/asterisk/monitor/YYYY/MM/DD/filename.wav`.
- Confirmed example:
  `/var/spool/asterisk/monitor/2026/08/09/exten-220-88451277-20260809-001218-1786221734.927.wav`.
- The Monitoring HTTP endpoint is session-protected and resolves the same server-side path; it is not the ingestion transport.

## Read-only access proof

**Completed for one user-approved copied PBX sample.** The sample was opened with read access only. SHA-256 and last-write time were checked before and after inspection and remained unchanged.

- Database match: confirmed without exporting internal identifiers
- Observed group: 12 CDR legs, one shared recording reference
- Sample duration: 98.5 seconds

This proves safe analyzer-side reading, database resolution and the live path rule. A production read-only SFTP credential is still required.

## Required next action

Operations must provide a restricted key-based SFTP account that can only read the approved monitor directory,
plus a verified OpenSSH `known_hosts` entry for `192.168.8.2`. The account must not permit rename, delete or write.
