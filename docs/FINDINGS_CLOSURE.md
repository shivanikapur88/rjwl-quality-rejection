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
| 4 | Medium | Entries saved by the old API during an upgrade have no production day and are not counted. | Runbook section 2 step 10: re-run db/12 after the switch (it fills only empty rows). | documented |
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

## Round 3: independent review (Claude Fable)

A separate reviewer read the round-3 code (drill-down, comparison, Pareto, production counts and rate, dashboard) without seeing my conclusions. It found **no high-severity defect**. Scope, the exclusion of reversed entries, role checks and the dashboard's XSS/CSP safety were confirmed sound. Every finding was checked against the code.

| # | Severity | Finding | Fix | Evidence |
|---|---|---|---|---|
| 1 | Medium | Clicking a bar in the trend chart did nothing: the click area sat underneath the bars. | The day's click area is drawn on top of the bars. | browser check: clicking the tallest bar filters to that day |
| 2 | Medium | When a shop's database failed in only one of the two periods, the comparison and totals covered different shops. | A shop unreadable in either period is left out of both periods and every total. | integration `A_shop_unreadable_in_one_period_is_left_out_of_both_periods` (fails without the fix) |
| 3 | Medium | An error from an older, slower request could appear above newer, correct data. | Errors of superseded loads are ignored. | — |
| 4 | Medium | Accessibility: focusable rectangles inside an image, table rows turned into buttons, no visible focus, focus lost after drill-down, nothing announced. | Real buttons in the first table cell and on Pareto names; table semantics kept; chart is mouse-only and the "Show as table" day buttons are the keyboard path; visible focus outline; focus moves to the filter chips; result announced in a live region. | browser check (keyboard drill via Enter) |
| 5 | Medium | Upload error row numbers did not match the file's line numbers. | Each row keeps its file line; client and server errors both say "Line N". | browser check: blank line skipped, lines 2 and 4 reported |
| 6 | Medium | An integration test used a fixed date that would be refused after Feb 2027. | The date is relative to today. | — |
| 7 | Low | No change showed as "▲ 0%". | Under 0.5% reads "about the same" / "＝ 0%". | — |
| 8 | Low | `fromDate=0001-01-01&compare=true` gave a 500; produced totals could overflow `int`. | Report dates start at 2000-01-01 (400); produced sums are `long`. | integration `Report_and_upload_limits_are_enforced` |
| 9 | Low | CSV parsing: quoted fields, impossible dates, huge numbers, double click on Save. | Quoted-field parser; real-date and 0–1,000,000 checks per line; buttons disabled while sending. | browser check |
| 10 | Low | The upload's lock covered the whole table for the date range. | Locks only the uploaded machines' key ranges, through the unique index. | integration upload test |
| 11 | Low | A shared link could show a wrong machine label; impossible dates in a link; pasted links ignored in an open tab; removing "Day" always reset to 7 days. | Labels replaced from the report; dates validated; link changes applied; the previous range is restored. | browser check |
| 12 | Low | Docs still said "no rejection percentage" and "read-only"; wrong approval date; limits not listed; glued sentence in the user guide. | Corrected. | — |
| 13 | Low | Missing tests (JavaScript, limits, scope of `entries`, partial availability). | Added the server tests named above (limits, 403 on another shop's entries, partial availability). **Not added:** automated JavaScript tests and concurrent-upload tests; the dashboard was checked in a real browser instead. | — |

Also fixed while checking: the chart's y-axis now uses whole-number steps (ticks read 0, 10, 20, 30, 40 rather than 13 and 38).

## Second full application review (Sonnet 5.5)

After round 3, three independent reviewers (Claude Sonnet 5.5) read the whole application without seeing my conclusions: server code, database scripts, and the Android app with the dashboard and documents. **None found a high-severity defect in the code.** The one high finding was in the database runbook. Every finding was checked against the code; the fixes are below.

### Database (scripts and runbook)

| # | Severity | Finding | Fix | Evidence |
|---|---|---|---|---|
| D1 | **High** | The runbook gave the API only `SELECT, INSERT` in GDC_AndonDB and MachineShopDB, but approving a correction updates the entry there: every GDC/CNC approval would fail with a permission error. The tests ran as `sa`, so they could not see it. | New `db/15_api_permissions.sql`, table by table. The **whole integration suite now runs the API as a login with exactly these rights**. | With the old grants, 6 correction tests fail; with 15, all pass |
| D2 | Medium | The upgrade steps omitted 13 and the PRE/FINAL entries table (02c). | Upgrade section rewritten and renumbered: 02c when missing, 09/12 in all three databases, 13, 15. | `tools/test-upgrade-path.sh` now also runs 11, 12, 13 twice and checks the backfill |
| D3 | Medium | Re-running 04 after a workbook change or an administrator's rename failed on a duplicate code. | A new name takes the next free code of its stage when its workbook code is taken; the post-check verifies names. | integration `Rerunning_the_seed_after_a_seeded_phenomenon_was_renamed_gives_the_old_name_a_new_code` |
| D4 | Medium | Deleting a machine with production counts (or a correction proposal) gave a foreign-key 500. | The delete check covers production counts and correction requests → 409 `machine_in_use` / `phenomenon_in_use`. Foreign keys added for proposed machine and phenomenon (db/11). | integration `Report_and_upload_limits_are_enforced` |
| D5 | Medium | The master grant allowed UPDATE/DELETE on the audit log and saved entries. | db/15: audit log SELECT/INSERT only; entries only the two reversal columns. | — |
| D6 | Low | db/12 rounded 05:59:59.7 up to 06:00 (shift A on the previous day). | Compared at millisecond precision. | the backfill test has the boundary case |
| D7 | Low | db/12 rescanned the table per batch and could escalate to a table lock. | Walks the key in 4,000-row id ranges; filtered index for rows still without a production day. | — |
| D8 | Low | Runbook overstated rollback and "nothing is deleted". | Reworded. | — |
| D9 | Low | Constraint gaps. | Added: proposal foreign keys; one replacement per corrected entry (unique index). Documented, not added: `ProductionCounts.ShopId` vs the machine's shop (checked by the upload and the feed template), shift-code foreign keys, one shop per stage. | — |
| D10 | Low (plausible) | The administrator trigger scanned all users. | Index on `(RoleId, IsActive)`. | — |
| D11 | Low (plausible) | The upload lock could become table-wide. | One query per machine with the predicate inside the hinted query, in machine order. | — |
| D12 | Low | db/10: untrimmed DEV-/EXAMPLE- checks; case-only renames ignored. | Fixed. | — |
| D13 | Low | 08 never removed stale identifier rows. | 08 resynchronises them with `qa.Users`. | upgrade-path script |
| D14 | Low (plausible) | 06 leaves old PRE/FINAL entries under the GDC/CNC shops. | Documented in the runbook; a migration needs a business decision. | — |
| D15 | Low | No `-f 65001` (UTF-8) in the runbook. | Added (all scripts are ASCII today). | — |
| D16 | Low | Test tooling: no assertions, fixed temp path, no production-name guard, incomplete re-run. | Fixed. | — |

### Server

| # | Severity | Finding | Fix | Evidence |
|---|---|---|---|---|
| S1 | Medium | 30 failures from the shared plant address blocked every sign-in from the plant. | Logins already known on that address are exempt from the per-address cap (they keep their own 5-per-5-minutes limit). | unit `One_bad_tablet_on_the_shared_plant_address_does_not_block_known_users` |
| S2 | Medium | Anonymous `/api/health/ready` opened 5 connections per call, scanned for rows without a production day and showed counts and file names. | Rate-limited, cached 10 s, filtered index, generic warning text. | integration readiness test |
| S3 | Medium | Correction list: about 800 queries for 200 requests; a down database stalled every row. | One entry query per shop, one each for users, machines and phenomena; a failing store is tried once. The 200 cap stays documented. | existing correction tests |
| S4 | Low | Accent/width variants of a login had separate throttle buckets. | Keys are folded. | unit `Accent_variants_of_a_login_share_one_bucket` |
| S5 | Low | Two aliases (username, employee code) give two per-login buckets. | **Not changed**: per-client and per-client+login limits still apply; documented. | — |
| S6 | Low | Anonymous 30 MB login bodies. | 8 KB limit; identifiers over 50 and passwords over 128 characters refused before any lookup. | — |
| S7 | Low | Body too large reported as 400. | 413 `request_too_large`. | unit |
| S8 | Low | Rejections on a shift with 0 produced raised the rate. | Such combinations are outside the rate and lower its coverage (decision #13 to confirm). | integration upload test |
| S9 | Low | Report names depended on row order after a rename. | Current master names. | — |
| S10 | Low | Save response time had more precision than the stored value; SubmissionId uniqueness is per entry database. | Times cut to milliseconds. The per-database uniqueness is documented (each id is generated per intended entry). | — |
| S11 | Low | Delete "in use" checks missed production counts and proposals. | = D4. | — |
| S12 | Low | A retry after losing the shop assignment answers 403 although the entry was saved. | **Not changed**: assignment is checked first by design; documented. | — |
| S13 | Low | Lookup distinguishes "other shop" from "not found". | **Not changed**: the inspector needs the clear "this machine belongs to another shop" message; ids are sequential and printed on labels anyway. | — |

### Android app, dashboard and documents

| # | Severity | Finding | Fix | Evidence (unit tests) |
|---|---|---|---|---|
| C1 | Medium | Changing the server address ended the session but left the user on Home with every call failing. | Signs out and goes to sign-in. | `Changing_the_server_host_signs_out_and_returns_to_sign_in` |
| C2 | Medium | Summary showed the previous day's numbers under the new date after a failed load. | Totals cleared when the day or shift changes. | `Summary_failed_load_of_another_day_does_not_show_the_old_numbers` |
| C3 | Medium | An exception in a button command could close the app. | Caught and logged; scan page and navigation guarded. | `An_exception_in_a_command_does_not_escape_Execute` |
| C4 | Medium | Labels page: two quick "Show" clicks mixed two shops' labels; expired session gave a script error. | Sequence guard; sign-in shown on expiry. | browser check |
| C5 | Medium | Certificate problems reported as "no network". | Own message: "The server's security certificate is not trusted on this device. Contact IT." | `Untrusted_certificate_is_not_reported_as_no_connection` |
| C6 | Medium | My entries: refresh and "Load more" could overlap and duplicate rows. | Only the latest load applies. | `Refresh_drops_a_slow_load_more_page_and_owns_the_busy_state` |
| C7 | Low | Draft could be restored twice, or lost after a failed load. | Restored once, after the list loaded. | `Entry_draft_is_restored_once_and_only_after_the_phenomena_loaded` |
| C8 | Low | A stored `http://` address was used as is. | Re-validated before every request; nothing is sent. | `A_stored_http_address_is_refused_before_the_token_is_sent` |
| C9 | Low | Scan of a machine in a shop with several stages lost the machine. | The machine is kept. | `Scan_of_a_machine_in_a_shop_with_several_stages_keeps_the_machine` |
| C10 | Low | Correction request answered "already pending" left the form open. | Form closes and the entry reloads. | `Correction_already_pending_closes_the_form_and_shows_the_pending_entry` |
| C11 | Low | Rate shown at full prominence at low coverage; same-named phenomena of two shops looked identical; template machine codes did not exist. | Warning below 80% coverage; shop shown when several shops are in view; template uses placeholders. | — |
| C12 | Low | Not changed: sign-out leaves the previous user's lists until the next load; chart colours rely on the table view for accessibility; Back does not step through dashboard filters; CSV fields containing line breaks. | Documented here. | — |
| C13 | Docs | Stale statements: shifts "not implemented", 14 pages, 77/49 tests, API table without round 2/3, "Save" vs "Save entry", 30 days vs 30 production days, debug certificate wording, full-review #10 closed too generously. | Corrected (#10 is now really fixed by C1). | — |

## Third full application review (Sonnet 5.5)

After the phone layout was added, three independent reviewers (Claude Sonnet 5.5) read the whole application again: server, database scripts, and the Android app with the dashboard and documents. **None found a high-severity defect.** Every finding was checked against the code.

### Server

| # | Severity | Finding | Fix | Evidence |
|---|---|---|---|---|
| S1 | Medium | A caller that disconnected during the readiness check could cache "unavailable" for everyone for 10 s; slow probes made the cache useless. | Each check has its own 5 s limit, independent of the caller; the result is stamped when the check finishes; waiting callers can leave. | unit `Readiness_cache_is_stamped_after_a_slow_probe` |
| S2 | Medium | Sign-ins that failed on a server error (database down) counted as failed attempts, locking users out after an outage. | Refunded on any error. | — |
| S3 | Medium (plausible) | Throttle memory had no upper bound under a flood of random logins, and every request then scanned it. | Hard cap with eviction of the oldest keys; pruning at most every 30 s; IPv6 clients grouped by /64. | unit `A_flood_of_random_logins_is_capped`, `Ipv6_clients_are_grouped_by_their_64_bit_network` |
| S4 | Low | A fallback shift calendar was kept for 5 minutes. | Retried after 15 s. | — |
| S5 | Low | Torn read of the readiness cache. | Immutable entry. | — |
| S6 | Low | A user assigned to a shop deactivated later could not be edited. | Existing assignments to inactive shops may be kept; new ones are refused with a clear message. | — |
| S7 | Low | Readiness warnings were not in the specification. | Documented as public (they name only the store). | — |
| S8 | Low | Shift times formatted with the server's culture. | Invariant. | — |
| S9 | Low | Success/refund after a parallel sign-in could settle the wrong buckets. | The "known" state of the attempt is carried through. | — |
| S10 | Low | Model-binding errors had no `code`. | `validation` problem documents. | — |
| S11 | Low | 30 MB bodies accepted on every authenticated endpoint. | 64 KB default (Kestrel and IIS); the upload keeps 2 MB. | — |

### Database

| # | Severity | Finding | Fix | Evidence |
|---|---|---|---|---|
| D1 | Medium | The 04 seed check passed when the stages were missing (03 not run). | Fails then. | — |
| D2 | Medium | db/15 was not atomic, did not handle an orphaned user after a restore, and could not see role memberships such as `db_datawriter`. | One transaction; orphaned user re-mapped; role memberships listed with a warning. | upgrade-path step 10 (from the old schema-wide grants, run twice) |
| D3 | Medium | The db/12 guard could be skipped (no active shifts, 11 not run); GDC/CNC cannot be compared; no compatibility-level requirement. | Guard cannot be skipped (50013/50014); runbook says to set the values for GDC/CNC; compatibility level ≥ 130 checked (50015) and documented. | upgrade-path steps 8 and 9 (05:59:59.700 value; refusal on mismatch) |
| D4 | Medium (plausible) | An admin delete could queue behind slow entry saves and stall new saves; pool exhaustion under many PRE/FINAL saves. | Deletes give up after 3 s with 409; pool size guidance in BUILD_AND_DEPLOY. | — |
| D5 | Low | Report index did not cover `MachineName`. | Included (rebuilt if needed). | — |
| D6 | Low | Legacy usernames with unusual characters might not match the login lookup. | 08 lists them with a warning. | — |
| D7 | Low | 03 refused to re-run once a shop or stage was added. | Checks the four required rows exist; rejects leftover old-layout mappings. | — |
| D8 | Low | Scripts without `SET QUOTED_IDENTIFIER ON` fail under sqlcmd once filtered indexes exist. | Every script sets it; unit test per script; runbook: `sqlcmd -I` for ad-hoc SQL. | unit `Every_script_sets_quoted_identifier_on` |
| D9 | Low | Runbook: missing error codes, wrong 07 wording, SchemaVersions note, version requirement; stale "NOT YET EXECUTED" headers. | Corrected. | — |
| D10 | Low | Test tooling did not check values, refusal, db/15 or runbook order. | Added (see D2/D3). The fixture runs scripts through SqlClient; the sqlcmd path is covered by the two shell scripts. | — |
| D11 | Low | Constraints. | Added: production day/shift set together; replacement self-reference; production-count shop must be the machine's shop (composite key); indexes for the proposal keys. Not added: blank-code checks and an `EntryStoreKey` check (validated by the API). | — |
| D12 | Low (plausible) | A future feed could deadlock with an upload. | Template note: process machines in ascending id order, retry on 1205. | — |
| D13 | Low | Python and SQL normalise non-ASCII names differently. | Documented; the workbook is ASCII. | — |

### Dashboard and documents

| # | Severity | Finding | Fix | Evidence |
|---|---|---|---|---|
| W1 | Medium | The chart was not redrawn on resize or rotation. | Redrawn when its width changes. | browser check (1280 → 700 px) |
| W2 | Medium (plausible) | Card layout on phones could drop table semantics. | Explicit table/row/cell roles. | browser check |
| W3 | Medium | Machine and shop rates were shown without their coverage. | "⚠ N%" below 80% coverage, explained on the page. | — |
| W4 | Low | `hidden` ignored where CSS sets `display` (nav on the sign-in screen). | `[hidden]` always hides. | browser check |
| W5 | Low | Phone: Pareto numbers and filter selects could overflow; long machine codes. | Wider number columns, shrinkable filters, codes wrap on phones. | no horizontal overflow at Pixel 7 width |
| W6 | Low | "▼ 100% fewer" while parts remained. | Shown as 99%. | — |
| W7 | Low | Changing the upload file during a check. | File input locked while sending. | — |
| W8 | Low | CR-only line endings. | Supported. | — |
| W9 | Low | Labels from a shared link shown when unconfirmed. | Shown as `#id`. | browser check |
| W10 | Low | Print before the QR images loaded. | Print enabled when all images settled. | — |
| W11 | Docs | API examples (login limit, missing routes), README script range and dashboard wording, RECOVERY counts, "table semantics kept", phone layout undocumented. | Corrected; phone layout in the specification and user guide. | — |

### Android app

| # | Severity | Finding | Fix | Evidence (unit tests) |
|---|---|---|---|---|
| A1 | Medium (plausible) | Scan → Back reopened the form at once while the camera still saw the label. | The same label is ignored for 3 s after returning; a different one is handled at once. | `Scan_back_does_not_reopen_the_same_label_at_once` |
| A2 | Medium | User editor: a retry after a failed password reset sent a stale version (409). | The returned user replaces the edited one; a retry only repeats the password step. | `User_editor_retry_after_a_failed_password_reset_does_not_resend_a_stale_update` |
| A3 | Medium | "Clear" after an unknown save outcome issued a new submission id, so re-entering could create a duplicate. | The id is kept until the outcome is confirmed. | `Clear_after_an_unknown_outcome_keeps_the_submission_id` |
| A4 | Medium | My entries reloaded page 1 on every return, losing loaded pages. | Pages kept; only a corrected row is updated. | `My_entries_keeps_its_pages_after_Back_and_updates_the_corrected_row` |
| A5 | Medium (plausible) | A late 401 for an earlier session ended the new one. | Ignored unless it is for the current token. | `A_late_401_for_an_earlier_session_does_not_end_the_new_one` |
| A6 | Medium (plausible) | A tablet clock hours ahead made every new session "expired". | The expiry is shifted by the difference to the server's `Date` header. | `A_device_clock_hours_ahead_does_not_expire_a_new_session` |
| A7 | Low | An entry draft survived an explicit sign-out. | Discarded on sign-out. | `Explicit_sign_out_discards_the_entry_draft` |
| A8 | Low | A certificate failure said "the entry may not have been saved". | Not for certificate or address errors (nothing was sent). | `Certificate_failure_is_not_reported_as_a_possibly_saved_entry` |
| A9 | Low | Admin phenomena: a quick second shop change could be ignored or show the first shop's stages. | Sequence guard. | `Admin_phenomena_slow_stages_of_an_earlier_shop_are_not_added_to_the_newer_shop` |
| A10 | Low | Changing only the server path kept the session. | Scheme, host, port and path are compared. | `Changing_only_the_server_path_signs_out` |

All fixes are verified by unit tests through the real API client with a scripted server; none has been tried on a device yet (no emulator in this environment).

## Complete review by Claude Fable (after the third Sonnet review)

Three independent Fable reviewers read the whole application: server, database scripts, and the app with the dashboard and documents. They found **two high-severity defects**, both introduced or missed by earlier rounds, and both now fixed. Every finding was checked against the code.

### High

| # | Finding | Fix | Evidence |
|---|---|---|---|
| H-DB | The "compatibility level ≥ 130" guard added in the third review was wrong. STRING_AGG and FOR JSON depend on the SQL Server version, not the level. The guard could stop the upgrade of the shared GDC/CNC databases, and it told operators to change another system's database setting. | Replaced by a SQL Server 2017+ version check. Database levels are never touched. Runbook corrected. | sqlcmd fresh install and upgrade path |
| H-App | My entries: on a device, "Load more" reloaded page 1. The pull-to-refresh control ran its refresh command whenever the busy flag went true. The same coupling existed on Summary and Corrections. | Pull-to-refresh has its own flag, set only by the pull; "Load more" shows its own progress. | unit `Load_more_does_not_set_the_refresh_flag_and_a_pull_refreshes_once`, `Summary_and_corrections_loads_from_code_do_not_set_the_refresh_flag` |

### Server

| # | Severity | Finding | Fix | Evidence |
|---|---|---|---|---|
| S1 | Medium | "Known login" exemptions lived only in memory, so the lock-out protection lapsed after every restart or IIS recycle. | Persisted in `qa.LoginKnownClients` (db/16), looked up when not in memory (at most every 5 min per pair). | unit `Known_logins_survive_a_restart_through_the_store`; integration `Role_refusals_have_a_code_and_sign_ins_are_remembered` |
| S2 | Medium (plausible) | Throttle keys did not match the database's notion of the same login (zero-width characters, ß). | Keys are folded; anything that is not a valid identifier after folding shares one key. | unit `Login_keys_fold_variants_and_never_throw` |
| S3 | Low | Anonymous 500 when the 50-character cut split an emoji. | Cut on a character boundary; never throws. | same |
| S4 | Low | 403 (roles) and limiter 429 had no problem body or code. | `forbidden` / `too_many_attempts` with Retry-After. | integration test above |
| S5 | Low | No proxy warning for a proxy on the same server. | Warns for loopback peers too (not in Development). | — |
| S6 | Low | A stale token made anonymous endpoints depend on the master database. | Revocation check skipped on anonymous endpoints. | — |
| S7 | Low | Connection-pool exhaustion gave 500. | 503 "server is busy". | — |
| S8 | Low | A failed shift-table read was hidden while an older calendar was cached. | Readiness reports it; retried after 15 s. | — |
| S9 | Low (plausible) | Throttle eviction could flush a targeted login's bucket. | Buckets at their limit are never evicted. | unit `Eviction_keeps_buckets_that_are_at_their_limit` |

### Database

| # | Severity | Finding | Fix | Evidence |
|---|---|---|---|---|
| D1 | Medium (plausible) | db/12 added filtered indexes to the shared GDC/CNC databases. Their owners' maintenance jobs running with QUOTED_IDENTIFIER OFF would then fail. | No filtered indexes there: a plain index on ProductionDate; the redundant unique replacement index is removed (one replacement per correction is already guaranteed). | sqlcmd install and upgrade |
| D2 | Medium | New constraints on existing tables had no pre-check. | db/13 lists rows whose shop is wrong (50016); the db/12 self-reference uses WITH NOCHECK and a warning. | — |
| D3 | Low (plausible) | db/04 locked another index than the API. | Same statement as the API (by Id). | — |
| D4 | Low | db/15 granted to an orphaned user without a login; problems with mapped or foreign users; tables were skipped silently. | Refuses with 50150-50153; re-maps only true orphans; warns about missing tables. | sqlcmd: run in all three databases; refuses `master` (50153) |
| D5 | Low | db/10's character check depended on the collation. | Binary comparison. | — |
| D6 | Low | Template 14 could hit a collation conflict. | `COLLATE DATABASE_DEFAULT`. | — |
| D7 | Low | Docs and tooling wording (12 header, "seeded codes", upgrade-test description). | Corrected. | — |

### Android app

| # | Severity | Finding | Fix | Evidence (unit tests) |
|---|---|---|---|---|
| A1 | Medium | A kept entry draft could not be retried after the phenomena list failed to load. | "Try again" button; the draft is kept until restored. | `Failed_phenomena_load_can_be_retried_and_the_draft_is_kept_until_restored`, `A_draft_not_yet_restored_is_resumed_when_the_same_form_is_opened_again` |
| A2 | Medium | The unknown-outcome protection was lost on leaving the form, so a re-entered save could duplicate. | The pending submission id is kept per user, shop, stage and machine until the outcome is known. | `An_unknown_outcome_survives_leaving_the_form_and_its_id_is_reused_for_the_same_selection`, `A_reused_id_with_other_details_gets_a_new_id_from_the_payload_mismatch_answer` |
| A3 | Medium (plausible) | Certificate detection was too broad (any "SSL" text) and too narrow (some exception types). | Trust failures matched specifically, in any exception type; handshake resets and timeouts stay network errors. | `Certificate_trust_failures_are_recognised_in_any_exception_type`, `Handshake_resets_and_timeouts_stay_network_errors` |
| A4 | Low | Unrelated answers cleared the unknown-outcome flag. | Only an answer of the entry endpoint does. | `Answers_not_from_the_entry_endpoint_do_not_settle_an_unknown_outcome` |
| A5 | Low | Phenomenon editor: quick shop changes. | Sequence guard. | `Phenomenon_editor_quick_shop_changes_show_only_the_latest_shops_stages` |
| A6 | Low | Home looked role-less after the token expired locally. | Returns to sign-in. | `Home_returns_to_sign_in_when_the_token_ran_out_while_the_app_was_open` |
| A7 | Low | "Test connection" saved the address and signed out before the result. | Tests the typed address only; Save persists it. | `Test_connection_checks_the_typed_address_without_saving_it_or_signing_out` |

### Dashboard and documents

| # | Severity | Finding | Fix | Evidence |
|---|---|---|---|---|
| W1 | Low | After a failed load the chips and URL showed the new selection over the old numbers. | The previous selection is restored, and the message says so. | browser check |
| W2 | Low | Coverage warnings could read "⚠ 80%". | Rounded down. | — |
| W3 | Low | Focus was lost after removing the last chip. | Moves to the first filter. | — |
| W4 | Low | Too large uploads got a generic 413. | 5,000-row limit checked first. | — |
| W5 | Low | Long remarks widened the table. | Text wraps. | — |
| W6 | Low (plausible) | A PC clock far ahead made sign-in loop silently. | No client-clock expiry check; the server decides. | — |
| W7 | Docs | "All fixed with tests", the stale readiness header, the stale evidence remark. | Corrected. | — |

Also added in this round, at the user's request: **iOS project parts** (iPhone and iPad). They are not built here, because Apple allows iOS builds only on macOS. See BUILD_AND_DEPLOY §6 and decision #20.

**Found during the verification of this round:** in the first clean-clone run, one integration test failed in the READ_COMMITTED_SNAPSHOT **OFF** mode. A plain `GET` of the machine list was chosen as a deadlock victim against a machine delete running in another test, and the user got 409. The extra index on `qa.Machines` (db/13) makes such reader/writer deadlocks slightly more likely when reads take shared locks. **Fix:** GET requests are retried up to twice on a deadlock (they change nothing). Two further runs in that mode passed, but the deadlock did not occur in them, so the retry path itself has **not** been exercised by a test. With READ_COMMITTED_SNAPSHOT ON (the recommended setting for QualityMasterDB), reads take no shared locks and this deadlock cannot occur.

## UI/UX review (Claude Fable) and redesign

A Fable reviewer tested the live dashboard at desktop, tablet and phone sizes, in light and dark mode. It also reviewed the Android layouts statically (no emulator) and recomputed every displayed number from the API: **2,416 comparisons, 0 mismatches**. At the owner's request the dashboard and the app were then redesigned. The findings were fixed as part of that work.

**Dashboard: redesign.**
* One token-based design system: rem sizes, a spacing scale, status colours with icons, and visible focus and disabled states. Dark mode has its own brand and focus colours.
* A hero tile with trend sparklines.
* Change badges coloured by meaning (fewer rejections = good), always with an arrow and words, never colour alone.
* A segmented quick-range control that shows the selected range, and a sticky filter bar on wide screens.
* Cleaner tables and empty states.

**Dashboard: fixes.**

| Severity | Finding | Fix |
|---|---|---|
| High | "Top machines" lost its Produced/Rate columns between 761 and ~1090 px | Single column below 1100 px; words wrap at word boundaries; tables scroll inside their card if needed |
| Medium | Dark mode: focus, chips and primary button almost invisible | Own dark-mode brand and focus colours |
| Medium | Disabled buttons looked enabled | `:disabled` style |
| Medium | Tooltip squeezed at the right edge or on phones | Clamped to the screen |
| Medium | Back did not undo a drill-down | Drill-down steps are added to the browser history |
| Medium | Coverage mark pushed the rate out of alignment | Shown on its own line under the rate |
| Medium | Entry times in the browser's time zone | Plant time zone |
| Medium | Labels page kept the old shop's labels after a shop change | Reloads on change |
| Medium (phones) | Horizontal scroll at ≤ 360 px for administrators; tap targets under 44 px; long ranges overlapping bars | File input shrinks; 44 px targets on touch screens; long ranges scroll inside the chart |
| Medium | **Rate with a phenomenon selected divided by every shop's production** | Only the shops where that phenomenon occurs count (integration test: CNC production does not dilute a GDC phenomenon). When several shops are in view, the tile says "all shops in view" (decision #13 still open). |
| Low | Single-day period text; "today still running"; last day unlabelled; empty chart; "＝ 0%"; "0%" for small shares; pending tile scope; password focus; supervisor on the labels page; "more machines" note; coverage rounding (now 6 decimals, rounded down) | Fixed |

**Android app (static; not run on a device).**

| Severity | Finding | Fix |
|---|---|---|
| High | Entry form: about three list rows visible, none with the keyboard open | Two steps: (1) search and list use the whole screen; (2) chosen phenomenon with "Change", quantity, "+ Add remarks", Clear/Save in a scroll view. One-line context header; compact error banner with "Try again"; the keyboard resizes the page. |
| High | Device dark mode could produce white text on light cards | Light theme locked; implicit styles give every label and input explicit colours |
| Medium | Home did not scroll; Settings/sign-out unreachable on small phones | Whole page scrolls; large "Scan machine QR"; shop list; grouped actions |
| Medium | Admin list filters fixed (~300 dp); three buttons per row clipped | Filters scroll away; Edit + Activate/Deactivate on one row, Delete as a red text button |
| Medium | Secondary/Danger buttons had no disabled state | Added |
| Medium | Shift summary: "›" active on today; "1 entries"; cut lists not marked | Fixed ("Top 5 …" with a note) |
| Low | Checkbox labels not tappable; status colours; confirmation shop name and row order; Clear without confirmation; Reject with empty reason | Fixed |

The shared style system has a named type scale (body never below 15, captions 13), a spacing scale, cards, a button hierarchy in sentence case (48 dp minimum) and status chips. The 18 new unit tests are in `MobileDesignTests.cs`. **Not verified on a device:** rendering, keyboard behaviour and text wrapping need the device checklist in BUILD_AND_DEPLOY.
