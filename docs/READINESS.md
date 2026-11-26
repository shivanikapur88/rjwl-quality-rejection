# Release-candidate readiness

Status at commit `3313612` (verified 2026-11-26). Key: ✅ done · ◐ partly · ✗ not done · — not applicable.

| Area | Implemented | Built | Tested with substitutes | Tested against SQL Server | Tested on Android | Business accepted | Blocked / unverified |
|---|:---:|:---:|:---:|:---:|:---:|:---:|---|
| Source complete, no binaries from the package (Q1) | ✅ | ✅ | — | — | ✗ | ✗ | icon is a placeholder |
| .NET 10 for all projects | ✅ | ✅ | ✅ | ✅ | ✗ | — | MAUI 11 move to plan |
| SQL scripts: fresh install (01-04, 08, 09) | ✅ | — | — | ✅ incl. re-run | — | ✗ | RJWL's server, collation, existing objects |
| SQL scripts: upgrade (06/07/08/09) | ✅ | — | — | ✅ (simulated old schema) | — | ✗ | real old databases; quantity = 1 assumption |
| Reference data: 145 phenomena | ✅ | — | ✅ workbook ↔ JSON ↔ SQL | ✅ DB equals seed exactly | — | ✗ | — |
| Production machine load (script 10) | ✅ | — | — | ✅ manual run | — | ✗ | **approved machine list missing** |
| Entry workflow, 4 shops, routing to 3 DBs | ✅ | ✅ | ✅ app logic | ✅ HTTP | ✗ | ✗ | device test |
| Idempotency, double submit, lost response (Q7) | ✅ | ✅ | ✅ app logic | ✅ | ✗ | ✗ | device test |
| Roles and shop scope, server-side | ✅ | ✅ | — | ✅ | ✗ | ✗ | — |
| Token revocation (Q2) | ✅ | ✅ | — | ✅ (mutation-checked) | ✗ | ✗ | users re-sign-in once after upgrade |
| Last administrator (Q3) | ✅ | ✅ | — | ✅ concurrent + trigger | — | ✗ | — |
| Login throttling (Q4) | ✅ | ✅ | ✅ | ✅ | ✗ | ✗ | proxy config (`ReverseProxy:KnownProxies`); single instance |
| Login identifiers (Q5) / employee code (Q6) | ✅ | ✅ | ✅ | ✅ concurrent | ✗ | ✗ | — |
| Phenomenon codes (Q8) / delete vs entry (Q9) | ✅ | ✅ | — | ✅ concurrent, deterministic | — | ✗ | — |
| Stale-screen protection | ✅ | ✅ | ✅ app request | ✅ | ✗ | ✗ | — |
| Paged history, UTC, incomplete notice | ✅ | ✅ | ✅ app logic | ✅ | ✗ | ✗ | shift/production-day rules undecided |
| Session expiry keeps the entry | ✅ | ✅ | ✅ app logic | — | ✗ | ✗ | device test |
| In-app administration (phenomena, machines, users) | ✅ | ✅ | ◐ (versions, errors) | ✅ API | ✗ | ✗ | device test |
| Audit (same transaction, no passwords) | ✅ | ✅ | — | ✅ | — | ✗ | no viewer (not in scope) |
| Health / readiness | ✅ | ✅ | — | ✅ | — | ✗ | — |
| Error mapping (no false "database unavailable") | ✅ | ✅ | ✅ | ✅ | — | ✗ | — |
| Android APK | ✅ | ✅ **debug** only | — | — | ✗ | ✗ | **package id, keystore, certificate** |
| Signed release APK | ◐ (csproj ready) | ✗ | — | — | ✗ | ✗ | package id, keystore |
| Deployment, backup, restore, rollback guidance | ✅ docs | — | — | ◐ (rollback trigger issue found in tests) | — | ✗ | restore drill on RJWL servers |
| Inspector / supervisor / admin instructions | ✅ docs | — | — | — | ✗ | ✗ | screenshots after the device test |

### Round 2 additions (commit `13d6d8c`)

| Area | Implemented | Built | Tested with substitutes | Tested against SQL Server | Tested on Android | Business accepted | Blocked / unverified |
|---|:---:|:---:|:---:|:---:|:---:|:---:|---|
| Shift and production date (#2) | ✅ | ✅ | ✅ boundaries | ✅ incl. backfill | ✗ | ✗ | confirm real shift times per shop |
| Entry corrections (#1) | ✅ | ✅ | ✅ app logic | ✅ incl. races, half-applied, pre-claim | ✗ | ✗ | confirm the 30-day limit |
| Reports and app shift summary (#4) | ✅ | ✅ | ✅ app logic | ✅ exact totals | ✗ | ✗ | percentages need production counts (#13) |
| Web dashboard (#6) | ✅ | ✅ | ✅ rendered in a browser (light/dark) | ✅ API | — | ✗ | RJWL server, certificate and browsers |
| Machine QR (#8) | ✅ | ✅ | ✅ labels decoded independently | ✅ lookup | ✗ **camera on device** | ✗ | label stock and printer |
| Independent review | ✅ 11 findings fixed or documented | | | | | | |

### Round 3 additions (commit `d4acbe0`)

| Area | Implemented | Built | Tested with substitutes | Tested against SQL Server | Tested on Android | Business accepted | Blocked / unverified |
|---|:---:|:---:|:---:|:---:|:---:|:---:|---|
| Dashboard drill-down, comparison, Pareto | ✅ | ✅ | ✅ browser checks | ✅ exact totals, scope | — | ✗ | RJWL browsers |
| Production counts upload | ✅ | ✅ | ✅ browser checks | ✅ validation, upsert, audit | — | ✗ | who uploads, and how often |
| Rejection rate with coverage | ✅ | ✅ | — | ✅ exact rate/coverage | — | ✗ | **counting rules and source (decision #13)** |
| Automatic production feed (db/14) | ◐ template | — | — | ✗ | — | ✗ | source tables and owners' approval |
| Independent review (Fable) | ✅ 13 findings fixed or documented | | | | | | |

**Release-candidate verdict:** the server, database and app logic are complete and tested against SQL Server. The build is **not** ready for production use until the ✗ items in the "Tested on Android" and "Blocked" columns are done: the device acceptance run, the machine list, the package id, keystore and certificate, and business acceptance. See [BUSINESS_DECISIONS.md](BUSINESS_DECISIONS.md) and the checklist in [TEST_EVIDENCE.md §3](TEST_EVIDENCE.md).
