# Sprint 0 Repository Inventory — DigiAhan CDR v4.3.1

## Scope and provenance

- Target release: `v4.3.1`
- Release commit: `7159bd5`
- Release branch: `codex/v4.3.1`
- Release PR: `#3`
- Discovery branch: `codex/v4.3.1-sprint0`
- Production repository and runtime files were treated as read-only.
- The target source was reconstructed from the installed source plus the official v4.3.1 hotfix payload. Local settings, logs, audio and test requests were not copied.

## Solution structure

The application is a single ASP.NET Core/.NET 8 project:

```text
Source/DigiAhan.CDR.Receiver.csproj
```

There is no `.sln` and no dedicated test project. Important areas:

| Path | Responsibility |
|---|---|
| `Source/Program.cs` | Minimal API endpoints, dependency registration and middleware |
| `Source/Services/SqlCdrRepository.cs` | CDR persistence and batch state |
| `Source/Services/AgentEventStore.cs` | In-memory/current agent popup events |
| `Source/Services/DashboardRepository.cs` | Current dashboard queries |
| `Source/Services/CustomerIntelligenceRepository.cs` | Phone/customer lookup |
| `Source/Services/AccountingSyncService.cs` | Accounting snapshot synchronization |
| `Source/Services/*Identity*` | Authoritative customer identity reconciliation |
| `Source/Sql/` | Schema and dashboard SQL |
| `Source/wwwroot/dashboard/` | Existing management dashboard |
| `Source/wwwroot/agent/` | Existing agent panel |
| `Source/wwwroot/invoice-notifications/` | v4.3 invoice notification UI |
| `payload/` | Installer overlay; must remain aligned with changed source files |

## Existing ingestion and VoIP flows

CDR flow:

```text
Issabel push -> POST /api/cdr -> SqlCdrRepository
             -> dbo.usp_InsertRawCDR -> dbo.RawCDR
```

The stored procedure is called once per received record. `Fingerprint` has a unique index and is the current duplicate barrier.

Live VoIP/popup flow:

```text
Issabel event -> POST /api/voip/events
              -> phone/customer lookup
              -> AgentEventStore / popup state
```

AI must not enter either request path. The future analyzer must consume committed `RawCDR` rows asynchronously.

## Dashboard dependencies

- Static UI is served by the same ASP.NET application.
- Dashboard APIs query `RawCDR`, customer identity, Didar and accounting snapshots.
- Existing call grouping uses `COALESCE(LinkedId, UniqueId, RawCDRId)` without a namespace prefix.
- Several dashboard endpoints share the same process and database dependency as CDR ingest; AI must not make their startup or request handling conditional on an AI provider.

## Configuration and logging

Runtime configuration is split across `appsettings.json` and local overlay files. Only example files are in the release branch. Production secrets remain outside Git.

The project uses `ILogger` plus a daily file logger. Operational logs may contain call identifiers or phone-related data and were not copied into Sprint 0 outputs.

## Build and tests

- `dotnet build Source/DigiAhan.CDR.Receiver.csproj --configuration Release`: PASS
- Dedicated unit/integration test project: NOT FOUND
- Installer PowerShell syntax: PASS
- v4.3.1 required payload inventory: PASS

## Files that must not be touched by Sprint 1 without explicit review

- `Source/Program.cs` endpoint bodies for `/api/cdr` and `/api/voip/events`
- `Source/Services/SqlCdrRepository.cs`
- live popup state and agent panel code
- production `appsettings*.local.json`
- Issabel dialplan, AMI configuration and service scripts

## Repository risks

1. Application functionality is concentrated in one project and a large `Program.cs`.
2. There is no automated regression test suite.
3. The installer is an overlay; `Source` and `payload` can drift.
4. Current dashboard grouping requires validation before reuse by AI.
5. Production and AI observability are not isolated yet.
