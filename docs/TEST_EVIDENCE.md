# Test evidence

> **Third full review and phone layout: see section 9. Second full review (Sonnet 5.5) fixes: see section 8. Round 3 (dashboard drill-down, comparison, Pareto, rejection rate): see section 7. Round 2 (corrections, shifts, reports, QR, dashboard): see section 6.** Sections 1-5 are the round-1 evidence (commit `3313612`).

Every result below was produced from a **fresh clone** of this repository at the stated commit, with no build output from the supplied package. Raw outputs are in [`docs/evidence/`](evidence/). Counts from the supplied documents (22/23/30/62) are **not** inherited ([SPECIFICATION.md §1](SPECIFICATION.md)).

| | |
|---|---|
| Source commit | `3313612a00a13179f572afd3e8b3d4cd590d522c` (later commits change only documentation and evidence files) |
| Date | 2026-11-26 (UTC) |
| Environment | Linux container (cloud session), .NET SDK 10.0.404, MAUI Android workload 10.0.110, Android SDK platform 36 / build-tools 36.0.0, JDK 21 |
| Database | **Real SQL Server**: Microsoft SQL Server 2022 (RTM-CU28) 16.0.4325.4, Linux container image `mcr.microsoft.com/mssql/server:2022-latest` |
| Not available | Windows, Visual Studio, an Android emulator (no hardware virtualisation in the container), a physical device, RJWL's SQL Server and network |

## 1. Results

| Suite | Command | Result | Raw output |
|---|---|---|---|
| Build, all 7 projects incl. Android | `dotnet build src/QualityRejection.sln -c Debug` | **0 errors**, 135 warnings (all nullable/unawaited-call warnings in the decompiled `Quality.Mobile.Core`, see [RECOVERY.md §3](RECOVERY.md)) | `evidence/build.txt` |
| Android debug APK | (same build) | produced; debug-signed; SHA-256 `06c4a7f8…7479` | `evidence/apk.txt` |
| Unit tests | `dotnet test --project src/Quality.Tests.Unit` | **27 passed**, 0 failed, 0 skipped | `evidence/unit.txt`, names in `evidence/tests_Unit.txt` |
| Integration, SQL Server, READ_COMMITTED_SNAPSHOT **ON** | `QR_TEST_SQL=… dotnet test --project src/Quality.Tests.Integration` | **37 passed**, 0 failed, 0 skipped | `evidence/integration_ON.txt`, names in `evidence/tests_Integration.txt` |
| Integration, READ_COMMITTED_SNAPSHOT **OFF** | `QR_TEST_RCSI=OFF …` | **37 passed**, 0 failed, 0 skipped | `evidence/integration_OFF.txt` |
| Reference data: workbook → JSON → SQL | `python3 -I tools/import_excel.py --check` | **OK** | `evidence/import_check.txt` |
| Reference data: workbook → database | `ReferenceDataTests` (in the integration run) | all 145 seeded rows equal the seed exactly (code, original text, normalized key, stage) | — |
| Fresh install, documented order + idempotent re-run | `tools/create-test-databases.sh` | all scripts OK | `evidence/fresh_install.txt` |
| Upgrade from the supplied schema | `tools/test-upgrade-path.sh` | 07 sets old quantity to 1; 08 refuses a login collision (error 50080, nothing written), succeeds after the fix, re-runs cleanly; trigger blocks removal of the last admin | `evidence/upgrade_path.txt` |
| Machine loader | `db/10_load_production_machines.sql` (manual run on the test server) | example rows and 5 kinds of invalid row refused with nothing changed; valid list applied with audit rows; re-run changes nothing | see [DATABASE_RUNBOOK.md §3](DATABASE_RUNBOOK.md) |

Before the final run, the integration suite also passed **four consecutive times** (two with READ_COMMITTED_SNAPSHOT ON, two OFF) at commit `1258278`, to check that the concurrency tests are not flaky.

## 2. What the integration tests cover

They run against real SQL Server databases built from the `db/` scripts (master + two entry databases, PRE/FINAL in the master), with the **real API** hosted in-process (`WebApplicationFactory`) over HTTP:

* **Brief §16C service rules / §16E HTTP:** the entry flow for all four shops, each landing in the correct database; PRE/FINAL isolation in the shared table; quantity and remarks limits; history snapshots after a machine rename; role and shop enforcement (supervisor and administrator cannot create entries; inspector cannot use admin endpoints or unassigned shops); a removed shop assignment takes effect on the next request; audit rows without passwords; readiness 200/503; malformed and forged tokens → 401.
* **§16D SQL behaviour:** unique constraints (submission id, login identifiers, phenomenon names, machine codes), transaction rollback, concurrency (Q3, Q5, Q8, Q9 races, 6 simultaneous submissions of one entry), row versions (stale edits), schema scripts (fresh and re-run), routing across the three databases, shared PRE/FINAL table isolation.
* **Review findings:** see [FINDINGS_CLOSURE.md](FINDINGS_CLOSURE.md) for test name per finding.

Unit tests cover the login throttle limits and expiry, identifier rules, remark clean-up, UTC normalisation, row versions, error classification, and the recovered app logic (double tap, lost response with the same submission id, validation vs. unknown outcome, session-expiry draft and resume, another user's draft, inactive machine and phenomenon, paging with an incomplete notice, version sent on admin edit). The app logic runs through the **real** `ApiClient` and `Session` against a scripted HTTP handler.

## 3. Tested with substitutes, and not tested

| Area | Status |
|---|---|
| Android UI on an emulator or device (brief §16F: touch targets, keyboard, real navigation, app restart, certificate validation, signed install/update) | **Not tested.** No emulator possible in this environment. The APK builds and every XAML binding compiles; the screen logic is unit-tested without a UI. Needs an approved Windows/Android environment (checklist below). |
| Signed release APK | **Not produced**: needs RJWL's package id and keystore. |
| RJWL's SQL Server (collation, existing objects in `GDC_AndonDB`/`MachineShopDB`, permissions, `qr_api` login) | **Not tested.** Tests used a container with default collation `SQL_Latin1_General_CP1_CI_AS` and `sa`. |
| IIS / reverse proxy / real TLS certificate | **Not tested.** The in-process test host stands in for Kestrel/IIS. |
| Windows build (Visual Studio) | **Not run here.** Same SDK and projects; earlier attempts on the developer PC were blocked by Windows Application Control, which must not be bypassed. |
| Login throttling by real client address | Tested with a single test client address (unit tests cover several addresses). |
| Business acceptance | **Not done.** |

### Android acceptance checklist (to run on an approved PC + emulator/device)
1. Build and deploy Debug to an emulator (API 24+) and to one target device model.
2. Settings → server address → Test connection (`https://10.0.2.2:<port>` on the emulator).
3. Sign in as an inspector of each shop; record one entry per shop; confirm the row in the right database.
4. Double-tap Save → one entry. Airplane mode → Save → message; reconnect → Save → "Already saved earlier – no duplicate created".
5. Stop the API after the request is sent (or drop the response at a proxy) → Save again → one entry.
6. Let the token expire (or reset the user's password from another device) during an entry → sign in → the entry reopens → Save.
7. Deactivate the selected machine / phenomenon from an admin device before Save → alert and refreshed list.
8. Admin screens: create/edit/deactivate/delete for phenomena, machines and users; two admin devices editing the same record → "changed by someone else".
9. Supervisor: history of assigned shops, Load more, pull to refresh.
10. Kill and restart the app → still signed in (token valid) / sign-in screen (token expired).
11. Release build with the real certificate; install and update over the previous version.

## 4. Mutation check: do the tests catch the original defects?

Each fix was removed temporarily and the related tests were run against SQL Server. Every one **failed** without its fix (`evidence/mutation_check.txt`): Q2 (3 tests), Q3, Q7, Q8 and Q9 (deterministic interleaving). The server code at that run is identical to the verified commit.

## 5. How to reproduce

```bash
docker run -d --name qr-sql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<strong pwd>' -p 14333:1433 mcr.microsoft.com/mssql/server:2022-latest
```
```bash
dotnet build src/QualityRejection.sln
```
```bash
dotnet test --project src/Quality.Tests.Unit
```
```bash
QR_TEST_SQL="Server=localhost,14333;User Id=sa;Password=<pwd>;TrustServerCertificate=True" dotnet test --project src/Quality.Tests.Integration
```
```bash
python3 -I tools/import_excel.py --check
```


## 6. Round 2 evidence (commit `13d6d8c`)

Fresh clone of branch `claude/corrections-shifts-dashboard-qr` at `13d6d8c30afcb8c9eece4d7f627d543defe44a04`, 26-27 Nov 2026, same environment as above. Raw output: [`docs/evidence/round2/`](evidence/round2/).

| Suite | Result |
|---|---|
| Build, all projects incl. Android | **0 errors**, 135 warnings (all in the decompiled `Quality.Mobile.Core`; new code has none) |
| Debug APK | SHA-256 `3a46ec35…5a23` (`evidence/round2/apk.txt`) |
| Unit tests | **77 passed** (was 27): + shift calendar boundaries, QR payloads, SQL-script rules, correction request building, corrections screen, shift summary (including overlapping loads), QR scan flow |
| Integration, SQL Server, RCSI ON / OFF | **49 passed** each (was 37). Before the final run the suite also passed four times in a row (two per mode) at `5ca0fb5`. |
| `sqlcmd` fresh install incl. 11/12 + idempotent re-run | OK (`fresh_install_sqlcmd.txt`) |
| Upgrade path | OK (`upgrade_path.txt`) |
| Seed check | OK |

**New integration tests** cover:
* shift stamping on save, and the db/12 backfill (02:00 IST → shift C of the previous day);
* the correction flow (approve with replacement, reverse-only, reject needs a note, withdraw, "no change", reason rules, role and shop scoping);
* two supervisors approving at once (5 rounds, one replacement each);
* idempotent apply in the entry database;
* review regressions: a half-applied approval followed by reject or withdraw, and a pre-claimed replacement id;
* the corrections count;
* report totals (exact numbers, reversed entries excluded, shift filter, scope, limits);
* QR label SVG and lookup (assigned shop, inactive machine, invalid label);
* dashboard served with a strict Content-Security-Policy.

**Visual and decode checks (manual, on a demo database):**
* The dashboard was rendered with Playwright in light and dark mode with no console errors.
* The printed QR labels were decoded with an independent decoder (jsQR): they read `RJWLQR:M:1` and `RJWLQR:M:2`, the ids of those machines.

**Found by the documented `sqlcmd` route, not by the in-process tests:** `sqlcmd` runs with `QUOTED_IDENTIFIER OFF`, which broke the filtered indexes in db/11. Fixed, and a unit test now checks every script.

**Independent review:** a separate reviewer (Claude Fable) read the round-2 diff without seeing my conclusions. Its findings and their fixes are in [FINDINGS_CLOSURE.md](FINDINGS_CLOSURE.md#round-2-independent-review).

**Still not tested in round 2:** camera scanning on a real device (no emulator here); the scan screen's logic is unit-tested and the APK builds with the ZXing camera view. Not yet run either: printing labels on real label stock, and the dashboard against RJWL's server, certificate and data.

## 7. Round 3 evidence (commit `d4acbe0`)

Fresh clone of branch `claude/dashboard-insights-rejection-rate` at `d4acbe0c328b250e7e77d65cb06f76146537bd71`, 26 Nov 2026, same environment as above. The later commit `36a5f25` changes only two dashboard styling/tooltip lines, checked in the browser. Raw output: [`docs/evidence/round3/`](evidence/round3/).

| Suite | Result |
|---|---|
| Build, all projects incl. Android | **0 errors**, 135 warnings (all in the decompiled `Quality.Mobile.Core`; new code has none) |
| Debug APK | SHA-256 `2ada84f2…0a7c` (`evidence/round3/apk.txt`) |
| Unit tests | **84 passed** |
| Integration, SQL Server, RCSI ON / OFF | **54 passed** each (was 49 in round 2) |
| `sqlcmd` fresh install incl. 13 + idempotent re-run | OK (`fresh_install_sqlcmd.txt`) |
| Upgrade path | OK (`upgrade_path.txt`) |
| Seed check | OK |

**New integration tests:**
* comparison with the previous period, and drill-down by machine and phenomenon, with exact numbers and scope;
* production-count upload: all-or-nothing validation listed by row, dry run, upsert, audit, and exact rate and coverage (including the shift filter and partial coverage);
* a shop readable in only one period is left out of both. This test fails without the fix;
* limits: report dates before 2000, more than 5,000 rows, dates more than 400 days ago, and 403 for another shop's entries.

**Browser checks (Playwright, demo database, `visual_checks.txt`):**
* clicking a bar filters to that day, and removing the chip restores the earlier range;
* keyboard drill-down with Enter, with focus moving to the chips and the result announced;
* a pasted link with a wrong label is corrected from the report;
* CSV errors are reported by file line, with a blank line skipped;
* no page errors. Screenshots are in the same folder.

**Independent review:** Claude Fable reviewed round 3 without seeing my conclusions; findings and fixes are in [FINDINGS_CLOSURE.md](FINDINGS_CLOSURE.md#round-3-independent-review-claude-fable).

**Not tested in round 3:**
* automated JavaScript unit tests;
* concurrent uploads, or an upload racing a feed (the lock design is described in the code);
* a real production-count feed (db/14 is a template);
* the dashboard on RJWL's server and browsers.

## 8. Second full review evidence (commit `a9fe6f8`)

Fresh clone of branch `claude/dashboard-insights-rejection-rate` at `a9fe6f8892c133f71c96e4b426f5bcc8cab8243c`, 26 Nov 2026, same environment. Raw output: [`docs/evidence/review2/`](evidence/review2/). The next commit only passes the test cancellation token in two new unit tests (removing two analyzer warnings); unit tests re-run: 97 passed.

| Suite | Result |
|---|---|
| Build, all projects incl. Android | **0 errors**. 141 warnings in the evidence run, 139 after that commit. All are nullable-annotation warnings in the recovered `Quality.Mobile.Core`; 4 more than before, from review fixes made in that recovered code. None are in the server, database or test code. |
| Debug APK | SHA-256 `256246d7…6f87` (`evidence/review2/apk.txt`) |
| Unit tests | **97 passed** (was 84): app fixes (9), throttle on a shared plant address, accent variants, 413 mapping |
| Integration, SQL Server, RCSI ON / OFF | **55 passed** each (was 54) |
| `sqlcmd` fresh install + idempotent re-run (entry databases too) | OK, but see the **correction** below |
| Upgrade path, now including 11, 12 (backfill of an old entry) and 13, with failing checks | OK, but see the **correction** below |
| `db/15` via `sqlcmd`, twice, in all three databases | OK; GDC/CNC show only the entries table with SELECT, INSERT and UPDATE of the two reversal columns |
| Seed check | OK |

**The API now runs as a least-privilege login in every integration test.** The fixture creates a temporary login, applies `db/15_api_permissions.sql` in the three databases, and the API connects with that login (the tests' own SQL checks use the administrator login). With the runbook's old GDC/CNC grants, 6 correction tests fail; with 15, all pass.

**New or extended integration tests:** the seed re-run after an administrator renamed a seeded phenomenon; a machine with production counts cannot be deleted (409); a shift with 0 produced stays outside the rate; the db/12 backfill at 05:59:59.700 stays in shift C; the readiness warning (fresh instance; no counts or file names).

**Browser checks (`visual_checks.txt`):**
* the Pareto shows the shop when several shops are in view;
* two quick "Show" clicks on the labels page show one shop only;
* a forced 40% coverage shows the partial-coverage warning;
* no page errors.

**Correction (found on 27 Nov 2026):** the two `sqlcmd` files in `evidence/review2/` (`fresh_install_sqlcmd.txt`, `upgrade_path.txt`) were produced from a **stale copy of `db/` and `tools/`** inside the SQL container. My verification script could not delete the previous copy (a file-permission problem), so `docker cp` nested the new copy beside it. The scripts of `a9fe6f8` were checked by hand the same day (fresh install, upgrade path including 11–13, db/15 twice: all OK, as stated in the review closure), but those two files are not evidence of it. The verification script now uses a fresh directory per run; section 9 re-runs both on the final scripts.

## 9. Third full review and phone layout (commit `be27305`)

Fresh clone of branch `claude/dashboard-insights-rejection-rate` at `be27305fab7e496dcd6109acfee8ffe460f7887a`, 27 Nov 2026, same environment. Raw output: [`docs/evidence/review3/`](evidence/review3/).

| Suite | Result |
|---|---|
| Build, all projects incl. Android | **0 errors**, 138 warnings: all nullable annotations in the recovered `Quality.Mobile.Core`; none in the server, database or test code |
| Debug APK | SHA-256 `6fa1ae5d…58eb` (`evidence/review3/apk.txt`) |
| Unit tests | **127 passed**. Includes 10 app fixes, 4 throttle/readiness tests, and a per-script check that every SQL script sets `QUOTED_IDENTIFIER ON` (one test case per script). |
| Integration, SQL Server, RCSI ON / OFF | **55 passed** each; the API runs as the least-privilege login from db/15 |
| `sqlcmd` fresh install + idempotent re-run of the master and entry scripts | OK (29 script runs) — run from a fresh copy of the final scripts |
| Upgrade path (`tools/test-upgrade-path.sh`, 10 steps) | OK: collision refusal (50080); 11, 12 and 13 run twice; 05:59:59.700 backfilled as shift C of the previous day; 12 refuses mismatching shift times (50013); db/15 run twice over the old schema-wide grants (grants removed, audit log append-only) |
| Seed check | OK |

**Browser checks (`visual_checks.txt`, Pixel 7 emulation and desktop):**
* no horizontal scrolling at phone width;
* the chart redraws when the width changes (1280 → 700 px);
* the entries table keeps its table role in the card layout;
* a link with an unknown machine shows `#id`, not the link's label;
* the nav is hidden on the sign-in screen;
* no page errors.

Phone screenshots are in the same folder.

**Not tested:**
* the Android app on a device (all app fixes are unit-tested through the real API client);
* screen readers on the phone card layout;
* concurrent uploads.
