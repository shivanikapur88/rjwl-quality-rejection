# Release-candidate readiness

Status per round: see the sections below (round 1 at commit `3313612`, then round 2, round 3 and the reviews).

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
| iOS app (iPhone/iPad) | ◐ project parts and settings | ✗ **needs a Mac** | ◐ settings checked by unit tests | — | ✗ | ✗ | **Mac with Xcode, Apple Developer account, distribution route (decision #20)** |
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

### Second full review (commit `a9fe6f8`)

Three Sonnet 5.5 reviewers found no high-severity code defect. One high finding was in the runbook: the GDC/CNC permissions would have broken correction approvals. It is fixed by `db/15_api_permissions.sql`, and the integration tests now run the API with exactly those rights. All findings and their fixes are in [FINDINGS_CLOSURE.md](FINDINGS_CLOSURE.md#second-full-application-review-sonnet-55). **New action for deployment:** run db/15 in all three databases (runbook section 1 step 6 / section 2 step 8).

### Third full review and phone layout (commit `be27305`)

The dashboard now works at phone width. Three more Sonnet 5.5 reviewers found **no high-severity defect**. The medium findings were:
* readiness and login throttle robustness;
* seed, permissions-script and backfill guards;
* chart resize and per-row rate coverage;
* several app state issues.

All are fixed; most have tests, some are covered only by browser checks (listed per item). See [FINDINGS_CLOSURE.md](FINDINGS_CLOSURE.md#third-full-application-review-sonnet-55). Deployment requirements: SQL Server 2017 or later (corrected by the Fable review: an earlier "compatibility level 130" requirement was wrong), and `sqlcmd -I` for ad-hoc SQL (runbook).

### Complete Fable review and iOS (commit `6808bdd`)

Fable found two high-severity defects, both fixed:
* a wrong database compatibility-level guard that could block the upgrade of the shared GDC/CNC databases;
* "Load more" in My entries reloading page 1 on a device.

The medium findings are also fixed:
* known logins are persisted (db/16);
* no filtered indexes in the shared databases;
* the entry draft can be retried;
* the unknown-outcome id is kept across screens;
* certificate errors are detected more precisely.

Details are in [FINDINGS_CLOSURE.md](FINDINGS_CLOSURE.md#complete-review-by-claude-fable-after-the-third-sonnet-review). Deployment: run **db/16** before **db/15**; SQL Server 2017 or later. iOS project parts are added but **not built** (needs a Mac; decision #20).

### Dashboard desktop app (Windows)

The dashboard installs as a desktop app from Edge or Chrome (PWA). Chromium reports it installable with **no errors**; the offline helper controls the page and saved no `/api` responses (docs/evidence/desktop). An integration test checks the manifest, icons and service worker are served under the CSP. **Not yet tried:** installing on a real Windows PC with Edge over the plant's HTTPS certificate, and the `WebAppInstallForceList` policy. Icons are placeholder artwork until RJWL's are supplied.

### Fifth full review (Sonnet 5.5, after the desktop app)

**No high-severity defect.** Medium findings fixed:
* an unanswered save could swallow a later identical rejection on the same machine (now shown again with its details, and expiring after 30 minutes);
* the shift summary could not recover from a failed first load;
* unreadable entry tables were invisible to readiness and the server log.

A missing produced quantity in a count upload is now refused instead of read as 0. Low items: desktop app on server errors, cache keys, dashboard focus, labels and axis. See [FINDINGS_CLOSURE.md](FINDINGS_CLOSURE.md#fifth-full-application-review-sonnet-55-after-the-desktop-app). Deployment note: `/api/health/ready` now returns 503 when an entries table cannot be read (for example db/12 or db/15 not run), with the SQL error number in the answer and the details in the server log.

**Release-candidate verdict:** the server, database and app logic are complete and tested against SQL Server. The build is **not** ready for production use until the ✗ items in the "Tested on Android" and "Blocked" columns are done: the device acceptance run, the machine list, the package id, keystore and certificate, and business acceptance. See [BUSINESS_DECISIONS.md](BUSINESS_DECISIONS.md) and the checklist in [TEST_EVIDENCE.md §3](TEST_EVIDENCE.md).
