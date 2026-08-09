# Sprint 0 Customer Context Discovery

## Existing authoritative local structures

| Structure | Approximate rows |
|---|---:|
| `CustomerIdentities` | 9,656 |
| `CustomerIdentityPhones` | 13,229 |
| `CustomerIdentityDidarLinks` | 9,653 |
| `CustomerIdentityAccountingLinks` | 55 |
| `DidarContacts` | 9,653 |
| `DidarContactPhones` | 13,141 |
| `AccountingCustomers` | 1,443 |
| `AccountingInvoices` | 936 |
| `AccountingInvoiceItems` | 1,206 |

`dbo.CustomerPhoneDirectory` is the existing consolidated lookup view. It ranks normalized phones and attaches the best Didar and accounting links. `dbo.NormalizeIranPhone` is already used by dashboard queries.

## NEW / RETURNING feasibility

- A known identity can be determined authoritatively through the normalized phone directory.
- A stronger returning-customer definition can use invoice existence/date through the accounting snapshot.
- `UNKNOWN` must remain possible for invalid, ambiguous or missing external phone numbers.
- The LLM must never infer customer class.

## Important limitation

Bulk candidate enrichment using the current normalized lookup timed out after 30 seconds. The candidate manifest deliberately leaves customer class as `UNKNOWN`.

Before Sprint 1:

1. define exact `NEW`, `RETURNING` and `UNKNOWN` business rules;
2. review the execution plan for normalized bulk lookup;
3. prefer joining already-normalized values over invoking normalization repeatedly per call;
4. record context freshness and source;
5. keep SQL2000 and external Didar calls outside the per-call critical path.

No external Didar API and no direct SQL2000 query was executed during Sprint 0.
