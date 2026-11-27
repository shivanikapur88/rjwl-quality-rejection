# Quality Rejection: reconciled specification

This document replaces the scattered statements in the supplied README, `Phase2_Design.md`, `TEST_RESULTS.md` and the short report. Where they disagreed, the position below is the one the code now implements. It describes **behaviour**; evidence for each claim is in [TEST_EVIDENCE.md](TEST_EVIDENCE.md).

## 1. Reconciled facts

| Topic | Supplied documents said | Position (and evidence) |
|---|---|---|
| Mobile pages | Short report: 11 pages | **14 pages** as recovered (round 2 added 4: entry detail, corrections, shift summary, QR scan, so 18 now): Login, Home, Stage, Machine, Entry form, Confirmation, My entries, Settings, Admin phenomena, Phenomenon editor, Admin machines, Machine editor, Admin users, User editor. Registered in `MauiProgram.cs` and present in the supplied build. |
| Framework | ".NET 8, upgrade pending"; mobile already `net10.0-android` | **Everything on .NET 10** (LTS, supported to Nov 2028). .NET 8 left support on 10 Nov 2026. MAUI 10 support ends earlier than .NET 10's; plan the move to MAUI 11 (see [BUSINESS_DECISIONS.md](BUSINESS_DECISIONS.md)). |
| User management | "No user-management screen" **and** in-app user management | **In-app user management exists** (Admin users + User editor) and is part of the scope. |
| PRE/FINAL entries | Early design: undecided | **Both in `QualityMasterDB`**, one shared table `qa.QualityRejectionEntries`, separated by `ShopId`/`ShopCode` (CHECK constraint allows only PRE/FINAL there). |
| Compile status | "Data/API not compiled" | Out of date: compiled in October (build output). Now compiled on .NET 10 together with the mobile projects. |
| Test counts | 22 / 23 / 30 / 62 from different phases | **None carried forward.** The test source was missing; the counts could not be reproduced. Current counts come only from tests in this repository, run against this source ([TEST_EVIDENCE.md](TEST_EVIDENCE.md)). |
| Upgrade instructions | "re-run 03 **and 05**" | **Wrong:** 05 loads fake machines. Production never runs 05; real machines come from `10_load_production_machines.sql` or the admin screen ([DATABASE_RUNBOOK.md](DATABASE_RUNBOOK.md)). |

## 2. Scope

**In scope (supplied design, completed):** the Android entry workflow for four shops, role-based history, in-app administration of phenomena, machines and users, the API, and the SQL scripts.

**Added in round 2 (approved by the owner, 26 Nov 2026):** entry corrections, shift and production date, the app's shift summary, machine QR codes, and a read-only web dashboard. See section 12.

**Added in round 3 (approved by the owner, 26 Nov 2026):** dashboard drill-down, comparison with the previous period, Pareto, and a rejection rate where production counts exist (administrators upload them on the dashboard). See section 13.

**Not added (outside the documented scope; listed so nobody assumes they exist):** offline entry or a local queue, a web *administration* portal, an audit-viewing screen, photos, dashboard functions beyond those in sections 12-13, exports, and PLC integration. Each needs a business decision first ([BUSINESS_DECISIONS.md](BUSINESS_DECISIONS.md)).

**Outside this application:** the CNC production collector and the ShopMonitor dashboard. The scripts add only schema `qa` and the table `qa.QualityRejectionEntries` to `GDC_AndonDB` and `MachineShopDB`; nothing else in those databases is touched.

## 3. Architecture

```
Android app (Quality.Mobile + Quality.Mobile.Core)
    │  HTTPS, JWT bearer token
    ▼
ASP.NET Core API (Quality.Api)        controllers, authentication, throttling, error mapping
    ▼
Business services (Quality.Core)      all rules and authorization decisions
    ▼
EF Core + routing (Quality.Data)      ├─ QualityMasterDB : users, roles, shops, stages, machines, phenomena, audit,
                                      │                    login identifiers + PRE and FINAL entries
                                      ├─ GDC_AndonDB      : GDC entries   (qa.QualityRejectionEntries)
                                      └─ MachineShopDB    : CNC entries   (qa.QualityRejectionEntries)
```
* The app never receives SQL credentials and never chooses a database. The API picks the entry database from the **authorised** shop (`Shops.EntryStoreKey` → `ConnectionStrings:GdcEntries|CncEntries|PreEntries|FinalEntries`).
* The SQL scripts own the schema; EF Core only maps it (no EF migrations).

## 4. Shops, stages, machines, phenomena

| Shop | Code | Stage | Entry database | Seeded phenomena |
|---|---|---|---|---:|
| GDC Shop | GDC | GDC Shop (`GDC_SHOP`) | GDC_AndonDB | 25 |
| CNC Shop | CNC | CNC Shop (`CNC_SHOP`) | MachineShopDB | 52 |
| Pre-treatment Inspection | PRE | Pre-treatment Inspection (`PRE_TREATMENT`) | QualityMasterDB | 34 |
| FINAL Inspection | FINAL | FINAL Inspection (`FINAL`) | QualityMasterDB | 34 |

* Shops and stages are linked through `qa.ShopStages`. The app skips the stage screen **only when a shop has exactly one active stage**; nothing assumes one stage forever.
* Machines belong to a shop. Phenomena belong to a stage. PRE and FINAL have separate phenomenon rows, even though the initial lists match.
* Phenomenon names keep their original text (including double spaces). Uniqueness within a stage uses a normalized key: trim, collapse whitespace, upper case.
* The seed counts are starting values, not limits: administrators add phenomena later.

## 5. Roles

| Capability | Quality Inspector | Supervisor | Administrator |
|---|---|---|---|
| See shops | assigned, active | assigned, active | all active |
| Create rejection entries | **yes** (assigned shops) | no | no |
| History | own entries, assigned shops | all entries of assigned shops | all shops |
| Manage phenomena, machines, users | no | no | yes |

Enforced on the server on every request, whatever IDs a caller sends. A deactivated user, a changed role, a password reset, a removed shop assignment and a deactivated shop, stage or machine all take effect on the **next request**.

## 6. Rejection entry

Request (`POST /api/rejection-entries`): `SubmissionId, ShopId, StageId, MachineId, PhenomenonId, Quantity, Remarks`.

| Rule | Detail |
|---|---|
| SubmissionId | non-empty GUID, generated by the app once per intended entry and **reused for every retry** of that entry |
| Quantity | whole number 1-9,999 (also a CHECK constraint) |
| Remarks | optional; trimmed; blank becomes "none"; max 500 characters |
| Shop | assigned to the user (administrators: any active shop) |
| Stage | active and mapped to the shop |
| Machine | belongs to the shop; **inactive → 409 `machine_inactive`** (the app returns to a refreshed machine list) |
| Phenomenon | belongs to the stage; inactive → 409 `phenomenon_inactive` (the app reloads the list) |
| Server-owned values | inspector id and employee code (from the token), server UTC time, entry database, snapshots of shop/stage/machine/phenomenon names |

**Idempotency:** a request whose SubmissionId already exists returns the original entry with **200** (`alreadySubmitted: true`) if shop, stage, machine, phenomenon, quantity **and cleaned-up remarks** all match. Otherwise it is refused with 409 `submission_payload_mismatch`. A new entry returns **201**. The unique index on `SubmissionId` in each entry database is the final guard. An id used by another inspector gives 409 `submission_id_in_use`.

**Consistency across databases:** while an entry is being saved, the API holds shared locks on the machine and phenomenon rows (master database) until the entry insert has committed. Deleting or deactivating either waits for that, so an entry can never point at a machine or phenomenon that was deleted in the same moment.

## 7. History

| Endpoint | Behaviour |
|---|---|
| `GET /api/rejection-entries/page?shopId&stageId&fromUtc&toUtc&limit&cursor` (**new**) | Newest first, ordered by `ServerCreatedAt DESC, ShopId DESC, Id DESC`, which is stable even when timestamps are equal across databases. `limit` 1-200 (default 50). Returns `{items, nextCursor, unavailableShops, isComplete}`. Send `nextCursor` back as `cursor` for the next page; `null` means the last page. If a shop's entry database cannot be read, its code is in `unavailableShops` and the app says the list is **incomplete**. |
| `GET /api/rejection-entries?…&limit` (supplied, kept for compatibility) | At most `limit` newest entries (max 200); **not** a complete history. Fails with 503 if any store is unreachable. |
| `GET /api/rejection-entries/{shopId}/{id}` | One entry. The shop is part of the key (ids are only unique per database, and PRE/FINAL share a table). |

**Time:** stored as UTC (`datetime2(3)`, server clock). `fromUtc` is inclusive; `toUtc` is **exclusive** in the paged endpoint (inclusive in the legacy one). Query times may carry `Z` or an offset (`+05:30`) and are converted to UTC; times without a zone are taken as UTC. The app shows times in the device's local time zone. From round 2 every entry also gets a production day and shift from the shift calendar (section 12.1); the CNC collector's 05:59 reset is not used.

## 8. Administration and audit

| Area | Create | Edit | Deactivate | Delete |
|---|---|---|---|---|
| Phenomena | yes (code `<STAGE>-NNN`, allocated safely) | rename | yes | only if never used (otherwise 409 `phenomenon_in_use`) |
| Machines | yes (per shop; code unique per shop - case-insensitive with SQL Server's default collation) | code and name | yes | only if never used (409 `machine_in_use`) |
| Users | yes (role, shops, initial password) | name, role, shops, reset password | yes | never (history refers to them) |

Safeguards: no self-deactivation or self-demotion; at least one active administrator, guarded in the service **and** by a database trigger; username and employee code share one namespace across all users; employee code 2-30 characters, username 2-50, password 10-128.

**Stale screens:** list and detail responses carry `version`. Edit and status requests may send it back. If the record changed since the screen was loaded, the save is refused with 409 `concurrent_edit`. The app sends it on every edit. Older clients that send no version are not checked.

**Audit:** every create, edit, (de)activation, delete and password reset writes `qa.AuditLog` (actor, action, entity, id, before/after JSON, UTC time) **in the same transaction** as the change. Passwords and hashes are never written. There is no audit-viewing screen (not in scope).

## 9. API

| Method and path | Who | Notes |
|---|---|---|
| `GET /api/health` | anyone | process is up (liveness) |
| `GET /api/health/ready` (**new**) | anyone (rate-limited, cached 10 s) | 200 if the master and all four entry stores are reachable, otherwise 503; reports only `ok`/`unavailable` per component |
| `POST /api/auth/login` | anyone | username **or** employee code; 429 `too_many_attempts` with `Retry-After` after repeated failures |
| `GET /api/shops`, `/shops/{id}/stages`, `/shops/{id}/stages/{id}/machines`, `/…/phenomena` | signed in | only assigned shops; active items only |
| `POST /api/rejection-entries` | inspector | 201 new / 200 replay |
| `GET /api/rejection-entries`, `/page`, `/{shopId}/{id}` | signed in | role-scoped (section 7) |
| Round 2/3: `POST /api/rejection-entries/{shopId}/{id}/corrections`, `/api/corrections` (+ `/count`, `{id}/approve`, `/reject`, `/withdraw`), `/api/reports/calendar`, `/totals`, `/entries`, `/api/machines/lookup`, `/api/admin/machines/{id}/qr.svg`, `POST /api/admin/production-counts` | by role | see sections 12 and 13 |
| `/api/admin/phenomena`, `/api/admin/machines`, `/api/admin/users` (+ `PATCH {id}/status`, `POST users/{id}/reset-password`, `DELETE`) | administrator | section 8 |

Errors are RFC 7807 problem documents with a stable `code`: `validation` 400, `unauthenticated`/`token_expired` 401, `forbidden`/`shop_not_assigned` 403, `*_not_found` 404, conflicts 409 (`concurrent_edit`, `concurrent_change`, `duplicate*`, `last_admin`, `*_in_use`, `*_inactive`, `submission_*`), `value_too_long` 400, `too_many_attempts` 429, `database_unavailable`/`store_not_configured` 503, `server_error` 500. **Only real connectivity problems** are reported as 503 (previously every constraint error was "database unavailable"). Bodies never contain SQL text, stack traces or secrets.

## 10. Security

* **Tokens:** HS256 JWT, 480 minutes by default, claims `sub`, `emp`, `role`, `tv` (token version). Every request re-checks: user exists, is active, still has that role, and `tv` equals `Users.TokenVersion`. Password reset, deactivation and role change increase `TokenVersion`, so **all earlier tokens stop working immediately**, and reactivating or changing the role back does not revive them. Tokens with missing or malformed claims get 401, not 500.
* **Login throttling:**
  * Failed attempts are counted per client address + login (5 in 5 min) and per client address (30 in 15 min). A login that has already signed in successfully from that address (within 30 days) is exempt from the per-address cap, so one faulty tablet behind the plant's shared address cannot block everybody.
  * Login keys ignore case, accents and full-width forms. Identifiers longer than 50 characters, passwords longer than 128 and request bodies over 8 KB are refused without a database lookup.
  * Per login (10 in 15 min), counted **only for clients that have not signed in as that login before** (within 30 days). Someone guessing from elsewhere therefore cannot lock the real user out of their usual device.
  * Checking and counting are one atomic step, so a parallel burst cannot slip past.
  * A success clears that login's counters.
  * There is also a generous raw cap of 300 login requests/minute per address (configurable).
  * Counters are in memory, per API instance. Behind a reverse proxy, set `ReverseProxy:KnownProxies` so the real client address is used; otherwise a warning is logged.
* **Passwords:** ASP.NET Core `PasswordHasher` (PBKDF2, salted). One generic message for unknown user, wrong password and inactive account.
* **Device:** token in Android Keystore-backed storage, backup disabled, cleartext HTTP disabled; debug builds skip certificate validation for `10.0.2.2`/`localhost` only (any certificate there, not just the development one); release builds always validate. A certificate failure is reported as "The server's security certificate is not trusted on this device", not as a network error.

## 11. App behaviour on network problems (online-only, as designed)

* The form is kept after any recoverable failure. Double taps send one request.
* The same SubmissionId is reused after an uncertain failure (timeout, connection loss, 5xx); the message says the entry **may not have been saved** and that Save again will not create a duplicate. Validation errors are shown as validation errors, not as an unknown outcome.
* **Session expiry while saving (changed):** the entry, with its SubmissionId, is kept in memory. After the same user signs in again the form reopens with it, and Save again is safe. Another user signing in does not see it.
* **Closing the app** with an unsaved entry discards it (no offline storage, by design). Anything already sent with that SubmissionId was either saved once or not at all; check **My entries**.


## 12. Round 2 additions (approved 26 Nov 2026)

### 12.1 Shift and production date (#2)
* `qa.ShiftDefinitions` holds the shifts in plant local time. Seed: **A 06:00–14:00, B 14:00–22:00, C 22:00–06:00**. Plant time zone: `Plant:TimeZoneId`, default `Asia/Kolkata`.
* Every entry stores `ProductionDate` and `ShiftCode`, calculated by the server from its receipt time. The production day starts at the start of the first shift, so **an entry at 02:00 on 27 Nov belongs to shift C of production day 26 Nov**.
* Existing entries are backfilled by db/12. Changing shift times later does not move entries already saved.
* `GET /api/reports/calendar` returns today's production date, the current shift and the shift list.

### 12.2 Entry corrections (#1)
* Entries are still never edited or deleted.
* The **inspector who recorded an entry** asks for a correction: `POST /api/rejection-entries/{shopId}/{id}/corrections` with a reason (required, 500 characters max) and either nothing (= remove the entry) or the corrected phenomenon, machine, quantity and/or remarks. Only the stage's phenomena and the shop's machines are allowed. Entries older than `Corrections:MaxAgeDays` (default 30 production days) cannot be corrected from the app.
* A **supervisor of that shop or an administrator** approves (`/api/corrections/{id}/approve`, optional note) or rejects (`/reject`, note required). The inspector can withdraw a pending request (`/withdraw`). `GET /api/corrections?status=Pending|Approved|Rejected|Withdrawn|all`: inspectors see their own, supervisors their shops', administrators all.
* **Approval:** in one transaction of the shop's entry database, the original gets `ReversedAt`/`ReversedByCorrectionId`, and a replacement entry is inserted if values were proposed. The replacement has `CorrectsEntryId` = original, the **original's inspector, production day and shift**, and current machine/phenomenon names. This step is idempotent: the replacement's SubmissionId is a random value stored on the request when it is made and never sent to clients. A retry after a failure between the two databases therefore completes the same correction rather than repeating it. A later decide or withdraw on such a half-applied request completes it as Approved.
* **Guards:** an update lock on the request row; unique indexes allow one pending and one approved request per entry; proposed machine/phenomenon are re-validated at approval under the same locks as a normal save; every step is audited (`CORRECTION_*`).
* History shows reversed entries with a badge and keeps them visible. Totals exclude them and include replacements.

### 12.3 Reports and the app's shift summary (#4)
* `GET /api/reports/totals?fromDate&toDate&shopId&shiftCode&top` (supervisor: assigned shops; administrator: all; inspectors: 403). Production days are inclusive, at most 92 per request.
* Returns totals, per shop, per day+shift, top phenomena and top machines, plus `unavailableShops` (the result is incomplete if any are listed). Reversed entries are excluded.
* These are **counts of rejected parts**. From round 3 a rejection rate is added only where production counts exist (§13.4); its counting rules still need sign-off (decision #13).
* The app's **Shift summary** shows one production day (today and the current shift by default; previous/next day; shift picker).

### 12.4 Machine QR codes (#8)
* Label content `RJWLQR:M:<machine id>`; the id is stable when a machine is renamed.
* Administrators print labels at `/dashboard/labels.html` (SVG from `GET /api/admin/machines/{id}/qr.svg`).
* Inspectors tap **Scan machine QR**. `GET /api/machines/lookup?code=…` checks shop assignment (403), active machine (409 `machine_inactive`) and label format (400 `qr_invalid`), then the entry form opens for that machine. The camera permission is requested the first time the scan screen opens.

### 12.5 Web dashboard (#6)

Read-only for supervisors. From round 3, administrators can also upload production counts on it (§13.4).

**Phone layout:** below 600 px wide, "Top machines" shows code, parts, change and rate only; entries are shown as one card per entry with labelled fields (still exposed as a table to screen readers); the Pareto shows rank, name, parts, share and cumulative share; the page never scrolls sideways. The chart redraws when the width changes (rotation). Rates of machines and shops based on less than 80% of their rejected parts carry "⚠ N%".
* Served by the API at `/dashboard/`. Supervisors and administrators sign in with their normal credentials; same roles and shop scope as the API.
* Shows: rejected parts and entries, waiting correction requests, a stacked bar chart per production day by shift (with a table view), top phenomena with share of rejected parts, top machines, and totals by shop.
* Plain HTML/JS with no external libraries, so it works on an offline plant network. Strict Content-Security-Policy: own scripts only, no inline script, no framing. The token is kept in the browser tab's session storage and cleared on sign-out or expiry.


## 13. Round 3: dashboard insights and rejection rate (approved 26 Nov 2026)

### 13.1 Drill-down
* Click a **day** in the chart, a **phenomenon** in the Pareto, a **machine** or a **shop** to filter the whole dashboard to it.
* Active filters appear as chips; click × to remove one. The selection is kept in the page address, so a link can be shared.
* API: `GET /api/reports/totals` accepts `machineId` and `phenomenonId`. `GET /api/reports/entries` (same filters, `limit` ≤ 200) lists the entries behind a selection, newest first. Reversed entries are excluded, as in the totals.
* Scope is unchanged: a machine or phenomenon outside the caller's shops simply matches nothing.

### 13.2 Comparison with the previous period
* `compare=true` adds the previous period of the same length, ending the day before `fromDate`.
* It covers totals (`previousQuantity`, `previousEntries`, `previousFromDate`/`previousToDate`) and each phenomenon's and machine's `previousQuantity`.
* The dashboard shows "▲ 12% more rejected parts than 13–19 Nov (913)", and ▲/▼ per phenomenon and machine.

### 13.3 Pareto
* Phenomena sorted by rejected parts (`top` up to 50; `phenomenaCount` is the total), with share and cumulative share of rejected parts as text.
* A line marks where 80% is reached, and a readout says "16 of 37 phenomena cause 82% of rejected parts". It is a single-axis chart: the cumulative share is a column, not a second axis.

### 13.4 Rejection rate (#8)
* **This system does not measure production.** Parts produced per machine, production day and shift are stored in `qa.ProductionCounts` (db/13), from either:
  * an administrator's **CSV upload** on the dashboard (`shop,machine,date,shift,produced`). `POST /api/admin/production-counts` checks the whole file first (unknown shop, machine not in the shop, unknown shift, future date, quantity 0–1,000,000, duplicate rows; at most 5,000 rows; dates from 400 days ago to today; shift codes as configured, which the dashboard upper-cases). Problems are reported by the **line of the file**. It saves nothing if any row is wrong, offers a dry run first, upserts by machine+day+shift, and is audited;
  * an **automatic feed** from the production systems, using the read-only `db/14_production_counts_feed_TEMPLATE.sql`. The source names must be filled in with the owners of those systems.
* If a shop's entries cannot be read in **either** period, that shop is left out of **both** periods and of every total, and the dashboard says so. Report dates start at 2000-01-01.
* `ProductionCounts.ShopId` is stored with each row; the upload checks that the machine belongs to that shop. A feed built from `db/14` must keep the same rule (the template joins on the machine's shop).
* **Rate** = rejected parts on machine+day+shift combinations that have a production count ÷ produced parts in scope.
* Each rate comes with its **coverage**: the share of rejected parts it is based on. With no production data, the dashboard says so instead of showing 0%.
* Rates are shown for the total, each shop and each machine.
* **Counting rules still need RJWL's sign-off (decision #13).** For example: does a part rejected for two phenomena count twice? Is rework counted as produced? Until then the figure is labelled "rejected ÷ produced".
