# Sprint 0 Unknowns and Risks

| Unknown/risk | Why it matters | Resolution | Owner | Blocks Sprint 1? |
|---|---|---|---|---|
| Production read-only recording transport absent | The approved copied sample works, but automated resolution from Issabel cannot run | Restricted SFTP/mount using the validated filename rule | IT/VoIP | Yes |
| Population codec/channel distribution unknown | One sample is PCM 8 kHz/16-bit mono; other routes may differ | Inspect a stratified sample after transport is available | IT/AI | Yes |
| Extreme groups up to 96 legs | Wrong grouping corrupts scores and KPIs | Trace representative LinkedIds against Issabel | VoIP/Backend | Yes |
| 44 calls arrive over >90 seconds | Fixed stabilization loses late legs | Measure distribution and define reopen policy | Backend | Yes |
| 23.45% logical calls lack recording reference | Discovery coverage may be much lower than expected | Classify by route/disposition and verify Issabel recording policy | VoIP/Sales | Yes |
| No automated test project | Receiver isolation cannot be proven automatically | Add characterization/regression tests before foundation changes | Backend | Yes |
| Bulk identity lookup timed out | Enrichment could overload dashboard/SQL | Query-plan/index review and normalized batch lookup | DBA/Backend | Yes |
| No dedicated AI GPU/host | Local Whisper/pyannote throughput unknown | Select host and benchmark actual calls | IT/AI | No for foundation; yes for Sprint 2 |
| Python, ffprobe and Docker unavailable | AI/audio toolchain is absent | Approve deployment method after host decision | IT/AI | No for foundation; yes for Sprint 2 |
| C: only 6.56 GB free | Model cache/staging could exhaust system disk | Use dedicated D: path and enforce quotas | IT | Yes for audio staging |
| Direction/extension rules heuristic | Wrong agent assignment affects coaching | Validate samples with sales/VoIP | Sales/VoIP | Yes |
| Recording consent/AI governance unconfirmed | Legal and organizational rollout risk | Complete privacy gate before Canary | Legal/HR/Sales | No for foundation; yes for Canary |
| SQL accessible locally via shared memory, not TCP | Deployment topology may differ | Document service account and network protocol | DBA/IT | No |
