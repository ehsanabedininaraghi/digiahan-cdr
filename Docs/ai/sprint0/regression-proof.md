# Sprint 0 Regression Proof

## Release under test

- DigiAhan CDR `v4.3.1`
- Commit `7159bd5`
- Source reconstructed from installed base plus official v4.3.1 payload

## Commands and results

```text
dotnet build Source\DigiAhan.CDR.Receiver.csproj --configuration Release
```

Result: **PASS**, 0 errors.

Warnings: two `NU1900` warnings because the execution environment could not reach `https://api.nuget.org/v3/index.json` to refresh vulnerability metadata. Package restore/build still completed from available sources/cache.

Additional checks:

| Check | Result |
|---|---|
| `RUN-v4.3.1.ps1` PowerShell parser | PASS |
| All required v4.3.1 payload paths | PASS |
| `git diff --check` for release commit | PASS |
| Local settings/log/runtime data staged | None |
| Dedicated test project | Not found |

## Production safety statement

- No production service was stopped or started.
- No installer was run.
- No firewall, Issabel or dialplan setting was changed.
- No migration was executed.
- Database discovery used SELECT-only statements.
- Runtime config, logs, phones and audio were not copied into Git.

## Regression limitation

Build success does not prove behavioral compatibility. Because no automated test project exists, the required Receiver/VoIP/popup regressions remain an explicit blocker for Sprint 1.
