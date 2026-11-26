> **Historical (supplied package, October-November 2026).** These results came from test projects whose source was **not** in the
> package, so none of the counts below (22 / 23 / 30 / 62) can be reproduced. They are kept only as a record. Current, reproducible
> results: [TEST_EVIDENCE.md](TEST_EVIDENCE.md).

# Test results (as of Phase 7)

**Read this first.** "PASS" below means the test was actually executed in this environment, at the level stated.
Everything that needs SQL Server, the running API over HTTP, or an Android build is marked **NOT RUN**.
The ASP.NET Core API (`Quality.Api`) and EF Core layer (`Quality.Data`) have **not been compiled** (no NuGet access here).

Executed with: .NET SDK 8.0.131, `src/Quality.ServiceTests` (raw output: `service_tests_output.txt`).
These tests exercise the real business-rule code in `Quality.Core` with in-memory fakes for the databases, loaded with the real
145 Excel-derived phenomena. They prove the rules; they do not prove the SQL/EF/HTTP plumbing.
Suite sanity check: two rules were deliberately broken (phenomenon-stage check, duplicate-name check) and the suite failed 2 tests (T09, T16) as expected, then passed again after restoring.

| # | Required test | Status | Level / evidence |
|---|---|---|---|
| 1 | Successful login | PASS | service level (T01), fake hasher/token. Real PBKDF2 + JWT: NOT RUN |
| 2 | Invalid login | PASS | service level (T02): wrong password, unknown, inactive, blank |
| 3 | Shop-stage filtering | PASS | service level (T03) |
| 4 | Stage-wise phenomenon filtering | PASS | service level (T04): 25 / 52 / 34 / 34, Pre-treatment and FINAL separate |
| 5 | Machine filtering | PASS | service level (T05), using DEV placeholder machines |
| 6 | Successful entry save | PASS | service level (T06) |
| 7 | Entry saved to the correct database | PASS (routing only) | T07 verifies GDC->GDC store, CNC->CNC store with fake stores. Real GDC_AndonDB / MachineShopDB: **NOT RUN** |
| 8 | Invalid phenomenon | PASS | T08, T08b (inactive) |
| 9 | Phenomenon from another stage | PASS | T09 |
| 10 | Machine from another shop | PASS | T10 (also stage-from-other-shop, unassigned shop) |
| 11 | Admin adds phenomenon | PASS | T11 (service level; audit written) |
| 12 | Admin edits phenomenon | PASS | T12 |
| 13 | Admin deactivates phenomenon | PASS | T13 (+ reactivate, search, active/inactive filter) |
| 14 | Inspector cannot modify master | PASS (service level) | T14 (inspector and supervisor). HTTP 403 via `[Authorize(Roles)]` attribute: **NOT RUN** |
| 15 | History intact after deactivation | PASS | T15 (snapshot unchanged after rename + deactivate) |
| 16 | Duplicate master-name validation | PASS (service level) | T16. DB unique index `UQ_RejectionPhenomena_Stage_Name`: **NOT RUN** |
| 17 | Network / API failure handling | PASS (app logic level) | Mobile suite M03-M07, M13, M24: no connection, timeout, 503, HTML 500/502, 429, expired token, unconfigured server, safe messages with no leaks. Behaviour on a real phone/network: **NOT RUN** |
| 18 | Repeated submission protection | PASS (service level) | T18 incl. simulated race. DB unique index `UQ_QRE_SubmissionId` and real concurrency: **NOT RUN** |
| 19 | Excel import count and data verification | PASS | Phase 2: SQL seed vs fresh Excel read (145 rows, exact text/order); T19; normalizer parity 146/146 (`normalizer_check_output.txt`). Running the seed on SQL Server: **NOT RUN** |
| 20 | Android build and API connectivity | **NOT RUN** | Android source now exists but cannot be built here (no MAUI workload, no NuGet). Static XAML check passed (below). Connectivity needs your server |

Additional executed tests: T21 delete policy (used vs never-used, both shop databases), T22 entry visibility by role,
T23 remarks length / blank.

## Other checks performed
| Check | Result |
|---|---|
| `Quality.Core` build, warnings as errors | PASS |
| T-SQL syntax of db/01-05 scripts (sqlglot) | 0 parse errors (syntax only) |
| `Quality.Data` / `Quality.Api` compile | **NOT RUN** (needs NuGet). Stripped-reference build showed 0 syntax errors; only missing-package types remain. Semantic errors cannot be ruled out. |
| SQL scripts executed on SQL Server | **NOT RUN** |
| Swagger / HTTP endpoints exercised | **NOT RUN** |

## First things to do on your machine
1. Open `src/QualityRejection.sln` in Visual Studio, restore, build. Fix anything the compiler reports in `Quality.Data` / `Quality.Api` and tell me.
2. Run the SQL scripts on a development SQL Server (after approval), then start the API and try Swagger.
3. `dotnet run --project src/Quality.ServiceTests -- db/seed/phenomena_seed.json` should still print 22 passed.

## Phase 4/5 additions (Android app + admin screens)

Executed: `src/Quality.Mobile.Tests` (24 tests, raw output `mobile_tests_output.txt`). They run the real `Quality.Mobile.Core` code
(API client, session, all view-models) against a scripted fake HTTP server that uses the real DTO types, so JSON contract parity is exercised.
Mutation check: breaking (a) reuse of the submission id and (b) the double-tap guard made 3 tests fail (M12, M13, M14); restored -> 24/24.

| Area | Tests |
|---|---|
| Login, session store, restore, expiry | M01, M02, M06, M08 |
| Network / timeout / 5xx / 429 / not-configured, no leakage | M03, M04, M05, M07, M24 |
| Role-based home; dynamic shop/stage/machine/phenomena lists; search | M09, M10, M11 |
| Submit: payload (no inspector/time), retry keeps same id, one request for repeated taps, validation, inactive phenomenon, replay, form reset | M12-M18 |
| My entries | M19 |
| Settings: HTTPS-only server address, connection test, logout | M20 |
| Admin: filters -> query, confirm before deactivate, delete refusal, editor create/duplicate/edit | M21-M23 |

Static check (`src/Quality.Mobile.XamlCheck`, output `xaml_check_output.txt`): 11 XAML files are well-formed; 100 bindings resolve against the real
view-model/DTO types (including ICommand types, public setters on two-way bindings, template item types), 6 event handlers exist, 115 style/colour keys exist.
It already caught one real bug (StatusBar pointed at the wrong namespace), now fixed. Negative control confirmed it fails on a wrong binding name / missing handler.
This is NOT a substitute for the MAUI build: layout, rendering, navigation behaviour and Android services are **NOT RUN**.

Syntax-only pass over `Quality.Mobile/*.cs`: 0 syntax errors; semantic compilation **NOT RUN** (needs the MAUI workload).

Still NOT RUN anywhere: MAUI/Android build, emulator or phone, real HTTPS to the API, SecureStorage, Shell navigation and back-stack behaviour.

## Phase 7 addition: user-creation tool (`src/Quality.UserSqlGen`)
Executed manually (not an automated suite): generated SQL for a test user; the hash verified with ASP.NET Core's `PasswordHasher` for the
right password (OK) and failed for a wrong one; the generated SQL contains no plaintext password; SQL parses as T-SQL (syntax only);
rejected: an employee code containing SQL metacharacters, an inspector without `--shops`, and a password shorter than 10 characters.
Running the generated SQL on SQL Server and logging in with that account through the API: **NOT RUN**.

## Summary of what is still NOT RUN (needs your environment)
SQL scripts on SQL Server; compile of `Quality.Data` and `Quality.Api`; MAUI/Android build and APK; API over HTTP (Swagger); app <-> API connectivity;
real database routing (GDC_AndonDB / MachineShopDB); emulator and phone behaviour; signed release build; `tools/run_checks.ps1`.

## First build in Visual Studio (reported by you) and fixes
Result of your first build: `Quality.Core`, `Quality.Data` and the rest of `Quality.Api` compiled; 3 errors remained.
| Error | Cause | Fix | Verification |
|---|---|---|---|
| CS1061 `AddFixedWindowLimiter` (Quality.Api/Program.cs:70) | missing `using Microsoft.AspNetCore.RateLimiting;` | using added | Reproduced the exact error in a minimal project against the real ASP.NET Core framework, and confirmed it builds with the using |
| NETSDK `SupportedOSPlatformVersion 24.0 cannot be higher than TargetPlatformVersion 21.0` (Quality.Mobile) | `net8.0-android` is no longer supported by current SDKs | `Quality.Mobile` retargeted to `net10.0-android` | **NOT RUN** (the .NET 10 SDK and MAUI workload are not available here). Needs your rebuild |
| NETSDK `workload 'net8.0-android' is out of support` (Quality.Mobile) | .NET MAUI 8 ended support on 14 May 2025 | same retarget | **NOT RUN** (as above) |
Further errors may appear once these three are cleared, because the MAUI project could not be compiled before.

## Change: four independent shops (design correction)
GDC, CNC, Pre-treatment Inspection and FINAL Inspection are now four independent shops (one stage each, own machines, own entry store key).
Re-executed after the change (raw outputs refreshed in `service_tests_output.txt` / `mobile_tests_output.txt`):
* Business rules: **23 of 23 pass** (T03/T04/T05/T07/T09/T10/T16 rewritten for four shops; new **T24** proves Pre-treatment and FINAL entries sharing one table cannot leak into each other). Mutation check: removing the shop check from the by-id lookup makes T24 fail; restored -> 23/23.
* App logic: **24 of 24 pass** (M09 rewritten: four shops listed, a shop with one stage goes straight to machines, several stages -> stage screen, no stage -> message). Mutation check: the old always-stage-screen behaviour makes M09 fail; restored -> 24/24.
* XAML static check and normalizer check: PASS. SQL scripts 01-06: 0 syntax errors (syntax only).
Still **NOT RUN**: scripts 02c, 03, 06 and the EF Core shop filter on SQL Server; compile of `Quality.Data`/`Quality.Api` after this change; the MAUI build.

## Change: rejected quantity, machine management, user management
Re-executed after the change (raw outputs refreshed: `service_tests_output.txt`, `mobile_tests_output.txt`, `xaml_check_output.txt`):
* **Business rules: 30 of 30 pass** (new T25-T31). T25 quantity 1..9999 stored/returned, 0/negative/10000/int.Max rejected, replay with another quantity refused. T26 machines: add, trim, case-insensitive duplicate per shop (same code in another shop allowed), unsafe characters, edit (history keeps the old code), deactivate (hidden; cannot be used in an entry), delete only if never used, audit rows, non-admins refused with nothing changed. T27-T31 users: create with shops, password stored via the hasher and never in audit, new user can sign in and sees only assigned shops, password/role/shop/duplicate validation (including username == someone's employee code), edit, deactivate/reactivate (deactivated user cannot sign in), password reset (old credential stops working), self-lockout and last-administrator protection, non-admins refused, DTO exposes no password/hash.
* **Mutation checks:** breaking the last-administrator rule, the cross-identifier duplicate rule, the machine-in-use rule and the quantity upper bound made T25, T26, T28 and T30 fail; restored -> 30/30. In the app, weakening the quantity check and removing the password-match check made M25 and M30 fail; restored -> 32/32.
* **App logic: 32 of 32 pass** (new M25-M32): quantity default/typed/+/-/limits/invalid text/reset/retry keeps id and quantity; machine list filters and confirm-before-deactivate/delete; machine editor create/duplicate/edit; user list; user editor create (client checks, shops, role value, password handling), edit (prefilled shops, no password in the update, separate reset call, partial-failure message, server rules shown); admin Home navigation.
* **XAML static check: PASS** (15 files, 168 bindings, 11 handlers, 172 style refs). SQL scripts 01-07: 0 syntax errors (syntax only). MAUI C# files: 0 syntax errors.
Still **NOT RUN**: SQL scripts (02a/b/c with Quantity, 07), EF Core queries for the new machine/user methods, the new controllers over HTTP, compile of `Quality.Data`/`Quality.Api`, the MAUI build and the new screens on a device.
