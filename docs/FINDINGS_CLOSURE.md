# Review findings: closure evidence

Source of the findings: the Q1-Q9 list in the implementation brief. **The review PDF itself (`RJWL-Andon-Review-With-Deep-Dive-2026-11-17.pdf`) was not available in this session**, so the wording below follows the brief, not the PDF. Check it against the PDF before signing off.

"Mutation-checked" means the fix was removed again temporarily and the named test then **failed**, so the test really detects the original defect (see [TEST_EVIDENCE.md §4](TEST_EVIDENCE.md#4-mutation-check-do-the-tests-catch-the-original-defects)). All integration tests ran on SQL Server 2022 with READ_COMMITTED_SNAPSHOT both ON and OFF.

| ID | Defect (as found in the supplied code) | Fix | Evidence | Status |
|---|---|---|---|---|
| **Q1** | Mobile.Core, all pages, platform code, resources and all test projects missing | Recovered from the supplied DLLs ([RECOVERY.md](RECOVERY.md)); new unit and integration test projects | Clean checkout builds all 7 projects, including the Android APK, with no binary from the package; 14 pages' compiled bindings verified at build | **Closed** (icon artwork is a placeholder) |
| **Q2** | Password reset changed only the hash; old tokens stayed valid. Deactivate→reactivate or role change→back revived old tokens. | `Users.TokenVersion` (db/08), `tv` claim, checked on every request (`TokenRevocation`); bumped on reset, deactivation and role change | `SecurityTests.Q2_password_reset_revokes_existing_tokens`, `Q2_deactivate_then_reactivate…`, `Q2_role_change_and_back…` (HTTP, SQL Server); mutation-checked (all three fail without the check) | **Closed** |
| **Q3** | "Last administrator" counted before the transaction, no lock, no DB guard | Count inside the transaction under `UPDLOCK, HOLDLOCK` on all active admin rows; trigger `TR_Users_KeepActiveAdministrator` with `READCOMMITTEDLOCK` as backstop | `LastAdministratorTests.Q3_two_admins_deactivating_each_other_at_once…` (5 rounds, truly concurrent); `Q3_database_trigger_refuses…` (direct SQL); upgrade script test step 6; mutation-checked | **Closed** |
| **Q4** | One server-wide login limit (10/min), so one client could lock everyone out | `LoginThrottle`: failed-attempt counters per client+login (5/5 min), per login (10/15 min), per client (30/15 min); raw cap 300 requests/min per client | `SecurityTests.Q4_one_clients_failures_lock_only_that_login…` (HTTP); 4 unit tests on the limits and expiry | **Closed.** Counters are in memory, per API instance; reverse proxies need `ReverseProxy:KnownProxies` |
| **Q5** | Username and employee code unique only separately; cross check was a query before saving; login took the first match | `qa.LoginIdentifiers` (one PK over UPPER(trimmed) usernames and employee codes), written in the user's transaction; login resolves through it; 08 backfills and stops on existing collisions | `ConcurrencyTests.Q5_concurrent_cross_column_collision…` (10 concurrent rounds); `SecurityTests.Q5_username_may_not_equal…`; upgrade test (collision → 50080, nothing written) | **Closed** |
| **Q6** | Validation allowed 50-character employee codes into a 30-character column; overflow reported as "database unavailable" | `LoginRules`: employee code 2-30 in the API and bootstrap admin; DB CHECK `CK_Users_EmployeeCode_Length`; truncation errors map to 400 `value_too_long`, not 503 | `SecurityTests.Q6_employee_code_limits…` (30 OK, 31 → 400 `employee_code_invalid`); unit tests | **Closed.** `UserSqlGen` was not rebuilt (no third path to fix) |
| **Q7** | Idempotent replay ignored remarks | Replay compares cleaned-up remarks, using the same function that cleans them before saving | `EntryTests.Q7_retry_with_changed_remarks…` (changed → 409, removed → 409, whitespace-only difference → replay); mutation-checked | **Closed** |
| **Q8** | Next phenomenon code computed from the current highest without a lock; a concurrent add got a generic "duplicate" | Stage row locked (`UPDLOCK, HOLDLOCK`) for name check + code allocation + insert; up to 3 retries on a code clash; a name clash → 409 `duplicate_name` | `ConcurrencyTests.Q8_concurrent_phenomenon_creates_get_distinct_codes` (12 at once, all succeed, 12 distinct codes); `Q8_two_concurrent_creates_of_the_same_name…`; mutation-checked | **Closed** |
| **Q9** | Delete checked "never used" then deleted, while an entry was being validated and saved in another database with no shared lock (same gap for deactivation) | Entry save reads machine and phenomenon with `HOLDLOCK` inside a master transaction held until the entry insert commits; delete takes an exclusive row lock **before** its usage check; deactivation's UPDATE waits the same way | `ConcurrencyTests.Q9_delete_waits_for_an_in_flight_entry_save…` (deterministic interleaving: the delete is shown to wait, then refuses with `machine_in_use`); `Q9_random_races…` (15 real HTTP races, no orphan entries, exactly one side wins each time); mutation-checked | **Closed** |

## Other problems from the analysis

| Problem | Fix | Evidence |
|---|---|---|
| No stale-screen protection (only phenomena had a version; the app never sent it; shop-only user edits did not change the user row) | `RowVer` on Users and Machines (db/08), `Version` in admin DTOs and requests, 409 `concurrent_edit`; user edits always touch `ModifiedAt`; the app sends the version | `EntryTests.Saving_from_a_stale_screen_is_refused` (machines, users incl. shop-only change, phenomena); unit test of the app request |
| History capped at 200, no paging; inclusive end date; time zones not normalised; app fetched only 50 | Paged endpoint with stable cursor, exclusive `toUtc`, UTC normalisation of `Z`/offset input; app "Load more" | `EntryTests.Paging_returns_every_entry_once…` (equal timestamps in two shops sharing a table), `Date_filters_accept_utc_and_offset_times`; app unit test |
| Multi-shop history looked complete when a store failed | Paged endpoint lists `unavailableShops`; the app shows an "incomplete" notice | `EntryTests.Paged_history_discloses_an_unreachable_store…`; app unit test |
| First-admin creation queried SQL at start-up, so the API crashed when SQL was down | Background service with retries | Integration fixture (host starts before the admin exists) |
| `/api/health` never checked the databases | `/api/health/ready` probes the master and all four stores | `SecurityTests.Readiness_*` (200 when healthy; 503 with only the broken store marked) |
| Malformed token → 500 | Claims parsed with `TryParse`; failure → 401 | `SecurityTests.Malformed_or_foreign_tokens_get_401_not_500` |
| Constraint/truncation errors shown as 503 "database unavailable" | SQL error classification (409/400/503 by error number) | unit test `Error_mapping…`; HTTP tests receive 409s, not 503s |
| Inactive machine reported like a wrong-shop machine | 409 `machine_inactive`; the app alerts and returns to a refreshed machine list | `EntryTests.Inactive_machine_gets_its_own_code…`; app unit test |
| No index on `MachineId` (full scan for "machine used?") | `IX_QRE_Machine` (db/09) | db/09 runs in every test database |
| Session expiry during save lost the form | Draft kept with its SubmissionId and reopened after the same user signs in | app unit tests `Session_expiry_during_save…`, `A_different_user…` |
| Quantity migration sets old entries to 1 | Unchanged, documented as an **assumption to confirm** | [DATABASE_RUNBOOK.md §2](DATABASE_RUNBOOK.md), [BUSINESS_DECISIONS.md](BUSINESS_DECISIONS.md) |
| Upgrade text said "re-run 03 and 05" (05 = fake machines) | Corrected in 06 and the runbook; controlled machine load `10_load_production_machines.sql` | script tested: invalid lists refused, valid list applied with audit, re-run no-op |
| Seed re-run failed once administrators had added phenomena | Seed check verifies seeded codes, not row counts | `ReferenceDataTests.Rerunning_the_seed…` |

## Defects found during this work (not in the review)

| Defect | Fix |
|---|---|
| EF Core refuses to save to a table with a trigger unless told about it (`OUTPUT` without `INTO`). Found by the integration tests as soon as the Q3 trigger existed. | `Users` mapping declares `HasTrigger(...)`. The runbook warns that the **old** API cannot save users on a database with the trigger. |
| The supplied seed check `CAST(RIGHT(code,3) AS int)` failed on non-numeric codes when SQL Server evaluated it before the `LIKE` filter | `TRY_CAST` in the generated check |


## Round 2: independent review

The round-2 diff (`484f52d..459f3d0`) was reviewed read-only by a separate Claude Fable reviewer that was given only the code and the feature description. It reported **no critical defects and no authorization hole**. Every finding was checked against the code; all held.

| # | Severity | Finding | Fix | Regression test |
|---|---|---|---|---|
| 1 | High | An approval could be committed in the entry database but not the master (failure between the two). A later **reject or withdraw** would then win, leaving the entry reversed while the request said Rejected or Withdrawn. A re-approval could also be blocked if the proposed machine had been deactivated in the meantime. | Decide and withdraw first check whether this request already reversed the entry. If so they complete it as **Approved** (no re-validation) and return 409 `correction_already_applied` to a reject or withdraw. | `Half_applied_approval_is_completed_as_approved_not_rejected_or_withdrawn` |
| 2 | Medium | The app's summary and corrections screens had no guard against overlapping loads, so a slow earlier response could show another day's totals. | A sequence number on each load; only the latest result is applied. | `Summary_ignores_a_slow_response_from_an_earlier_tap` (fails without the fix) |
| 3 | Medium | Renamed or retired shift codes vanished from the dashboard trend and could not be queried. | Known codes include inactive shifts; the dashboard adds a "(retired)" series for codes found in the data. | unit `Custom_two_shift_pattern_and_validation` |
| 4 | Medium | Entries saved by the old API during an upgrade have no production day and are not counted. | Runbook step 7b: re-run db/12 after the switch (it fills only empty rows). | documented |
| 5 | Medium | The db/12 backfill hard-codes the IST offset and shift times. | The script prints its parameters and row count, and in QualityMasterDB refuses (50013) if they differ from `qa.ShiftDefinitions`. | run with `sqlcmd`: mismatch refused, match applied |
| 6 | Low-medium | The replacement entry's SubmissionId was derived from the sequential request id, so an inspector could pre-claim it with an ordinary entry. Approval would then adopt the wrong row. | Random id stored on the request at request time and never sent to clients; the entry store refuses any existing row that is not this correction's replacement. | `A_pre_claimed_replacement_id_is_refused_and_nothing_is_reversed` |
| 7 | Low | The dashboard loaded up to 200 requests (with entry look-ups) just to show a count. | `GET /api/corrections/count`. | `Count_endpoint_matches_the_list_in_scope` |
| 8 | Low | A read after a committed decision could turn a success into a 500. | The read is wrapped. Reject still reads the entry database on purpose, to detect #1; if that database is down it answers 503. | — |
| 9 | Low | Summary "today" went stale after 06:00; the default shift was matched by display name; a failed calendar call led to a query for year 1. | The calendar is re-read on every appearance, the shift is matched by code, and there is no query before the calendar has loaded. | covered by summary tests |
| 10 | Low | The default shift calendar was used silently when `qa.ShiftDefinitions` could not be read. | `/api/health/ready` reports `shifts: unavailable` in that case. | readiness test |
| 11 | Low | Minor items. | Accepted and documented: the correction list's entry badge reflects that row; a replacement's `ServerCreatedAt` is the approval time while its production day is the original's. | — |

**Also found in round 2 by my own checks:**
* `sqlcmd` + `QUOTED_IDENTIFIER OFF` broke db/11's filtered indexes. Fixed; covered by a static unit test.
* The Q9 race test's orphan check was too broad once other tests inserted synthetic rows. Scoped to its own machines; the product was not affected.


## Full application review (Sonnet 5.5)

A separate Claude Sonnet 5.5 reviewer read the **whole application** at `453f148`: server, database scripts, app, dashboard and tests. It worked read-only and was given only the code and the system description. It found no role or shop-scope bypass and no dashboard XSS. Every finding was checked against the code.

| # | Severity | Finding | Fix | Evidence |
|---|---|---|---|---|
| 1 | **High** | Anyone could lock out any account (even the only administrator): the per-login failure bucket refused everybody, at a cost of ~10 requests per 15 min. The Q4 closure's "an abusive client can only lock itself out" was therefore wrong. | The per-login bucket now applies only to clients that have not signed in successfully as that login in the last 30 days. Per-client and per-client+login limits still apply to everyone. | unit `Attackers_cannot_lock_the_real_user_out_of_a_known_device` |
| 2 | Medium | A parallel burst passed the throttle check before any failure was recorded. | Check and reserve are one atomic step; success and "never reached the password" give the reservation back. | unit `Parallel_burst_cannot_exceed_the_per_client_and_login_limit` (200 parallel → exactly 5 allowed) |
| 3 | Medium | Behind a proxy without `ReverseProxy:KnownProxies`, every device shared one throttle bucket, silently. | A warning is logged when forwarded requests arrive and no proxies are configured; documented. | — |
| 4 | Medium | If db/03 was missing, the first-admin start-up threw outside the retry filter and stopped the API. | Every failure except shutdown is logged and retried. | — |
| 5 | Medium | Entries without a production day were silently left out of reports. | `/api/health/ready` lists them as a warning naming db/12. | integration `Readiness_warns_about_entries_without_a_production_day` |
| 6 | Low-medium | No self-service password change. | **Not changed:** outside the documented scope (administrators reset passwords). Logged as decision #19. | — |
| 7 | Low | Completing a half-applied approval recorded the current caller (even the requesting inspector) as the approver. | `DecidedBy` stays empty; audit action `CORRECTION_APPROVAL_COMPLETED`. | existing half-applied test |
| 8 | Low | Unknown or inactive users answered faster than wrong passwords (account enumeration by timing). | The short paths verify a real dummy hash of the same cost. | — |
| 9 | Low | An unknown plant time zone failed on the first request instead of at start-up. | Checked at start-up with a clear message. | — |
| 10 | Low | The app kept sending its token after the server address was changed to another host. | Changing to a different host ends the session. | — |
| 11 | Low | The QR scan screen could stay frozen after an exception. | Scanning resumes in a `finally` unless the page was left. | — |
| 12 | Low | The summary's shift list was built once and could map to the wrong shift after an edit. | Rebuilt when the shift list changes. | — |
| 13 | Low | The dashboard's correction count ignored the shop filter; lists are capped at 200. | Count takes `shopId`; the 200 cap is documented. | — |
| 14 | Low | A stale test name, a stale spec sentence, and a script check that ignored order. | Renamed, corrected; the check now requires `SET QUOTED_IDENTIFIER ON` **before** the first filtered index. | unit |
| 15 | Low | db/08 could stop half-way on a legacy 1-character employee code; db/10 re-activated machines an admin had deactivated. | db/08 lists them and stops with 50081 first; db/10 keeps existing active flags unless `@UpdateActiveFlagOfExisting = 1`. | both run with `sqlcmd` |
| — | unverified | Rare deadlock between entry saves and admin edits; 50+ character login suffixes creating new throttle buckets. | The deadlock would map to 409 `concurrent_change` (retryable). Throttle keys are now cut to 50 characters. | unit `Long_login_names_share_one_bucket` |
