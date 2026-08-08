# Sprint 0 Call Grouping Discovery

## Candidate key

The discovery query used:

```text
linked:{LinkedId}
unique:{UniqueId}
raw:{RawCDRId}
```

Both `LinkedId` and `UniqueId` are populated on every current row, so all 1,241 measured logical calls selected the `linked:` path.

## Leg distribution

| Measure | Value |
|---|---:|
| Single-leg calls | 582 |
| Multi-leg calls | 659 |
| Multi-leg rate | 53.10% |
| Calls whose leg arrival spread exceeded 90 seconds | 44 (3.55%) |
| Maximum observed rows under one LinkedId | 96 |

Observed high leg counts include 25, 36, 48, 60, 65, 72, 84 and 96 rows. These groups occurred within short call-time spans and are consistent with queue/local-channel fan-out, but they have not yet been manually validated as one business conversation.

## Consequences

1. `one RawCDR row = one call` is definitively false.
2. A fixed 90-second finalization rule would reopen at least 44 observed calls.
3. `LinkedId` is a strong candidate but cannot be accepted blindly until representative 48–96-leg examples are traced against Issabel behavior and recordings.
4. Existing dashboard direction flags are not mutually exclusive across raw legs. For a mutually exclusive display, the current dashboard gives inbound precedence.
5. Sprint 1 needs `DISCOVERING`, `STABILIZING`, `FINALIZED` and `REOPENED` states plus a new `PipelineRun` after late legs.

## Required validation before Sprint 1

- Manually trace at least one single-leg, one ordinary multi-leg and three extreme multi-leg groups.
- Confirm whether multiple recording references can occur in real data; the current window showed zero logical calls with more than one distinct non-empty reference.
- Validate direction and answered-extension rules with operations staff.
- Define maximum stabilization time and late-leg reconciliation policy from measured arrival distributions, not a fixed assumption.
