# Quality Rejection Entry

Android app (.NET MAUI) + ASP.NET Core Web API + SQL Server for recording quality rejections in **four independent shops**
(GDC Shop, CNC Shop, Pre-treatment Inspection, FINAL Inspection). Each shop has its own inspection stage, its own machines and its own list of rejection phenomena, which come from your Excel file (one Excel column per shop).

## 0. Honest status – read first

This repository was authored in an environment with a .NET 8 SDK but **no NuGet access, no SQL Server, no MAUI workload and no Android device**.
So some parts were executed and verified, and some were written but never built or run. Nothing marked "not run" should be assumed to work until you have built and run it.

| Part | State |
|---|---|
| Excel import (145 of 146 rows; CNC duplicate `DENT` skipped) and generated seed SQL | **Executed and verified** against the workbook |
| Business rules (`Quality.Core`): login, shop/stage/machine/phenomenon validation, idempotency, roles, admin policy | **Compiled and tested** (30 tests pass, in-memory fakes, real Excel data) |
| App logic (`Quality.Mobile.Core`): API client, session, all view-models, error handling | **Compiled and tested** (32 tests pass, fake HTTP server) |
| XAML bindings of all 11 pages | **Statically checked** (100 bindings resolve against real types) |
| User-creation SQL generator (`Quality.UserSqlGen`) | **Executed**; hash verified with ASP.NET's PasswordHasher |
| SQL scripts `db/*.sql` | Syntax-parsed only. **Never executed on SQL Server** |
| `Quality.Data` (EF Core) and `Quality.Api` (controllers, JWT, Swagger) | **Written, not compiled** |
| `Quality.Mobile` (MAUI project) | **Written, not built.** No APK has been produced |
| Real HTTPS, real database routing, emulator/phone behaviour, signed APK | **Not run** |

Full list of what ran and what did not: `docs/TEST_RESULTS.md`. Expect to fix some compile errors in the uncompiled projects on first build.

## 1. Items that need your confirmation

| # | Item | Current assumption |
|---|---|---|
| 1 | Master database | A **new** database `QualityMasterDB` holds users, roles, shops, stages, machines, phenomena and audit log. You create it; no script does. |
| 2 | Entry databases | `GDC_AndonDB` (GDC entries) and `MachineShopDB` (CNC entries) are existing. Scripts 02a/02b add schema `qa` and one table `qa.QualityRejectionEntries` to each. Check for name clashes first. Instance name, existing tables and schemas were **not inspected**. |
| 3 | Where Pre-treatment and FINAL entries go | **Decided: `QualityMasterDB`** (one database for both). Run `db/02c_entries_schema_PRE_FINAL.sql` in `QualityMasterDB` and set `ConnectionStrings:PreEntries` and `ConnectionStrings:FinalEntries` to the same connection string as `ConnectionStrings:Master`. GDC entries stay in `GDC_AndonDB`, CNC entries in `MachineShopDB`. |
| 3b | Quantity limit | **Decided: 1 to 9999 per entry** (no larger quantity is allowed). Existing rows get 1 when migrated. |
| 4 | Machines | The Excel has none. `db/05_DEV_ONLY_placeholder_machines.sql` adds 4 fake machines for development. **Replace them with your real machine list** and never run script 05 in production. |
| 5 | Android package id | `com.yourcompany.qualityrejection` in `src/Quality.Mobile/Quality.Mobile.csproj`. Change before the first release; it cannot change afterwards. |
| 6 | CNC `DENT` duplicate | Imported once (CNC = 52). The skipped row is in `docs/import_report.md`. |
| 7 | Passwords and users | **Decided:** only an administrator creates users, deactivates them and resets passwords. There is no user deletion and no self-service password change. Passwords: administrator bootstrap min 12 characters, all other users min 10. |
| 8 | Production certificate for phones | See section 8.4. |

## 2. Repository layout

```
QualityRejection/
  README.md
  db/                      SQL scripts 01-05 + seed/ (the Excel file and generated JSON)
  tools/                   import_excel.py (Excel -> seed SQL), run_checks.ps1
  docs/                    TEST_RESULTS.md, import_report.md, Phase2_Design.md, API_EXAMPLES.md, raw test outputs
  src/
    QualityRejection.sln
    Quality.Core/          entities, DTOs, ALL business rules (no external dependencies)
    Quality.Data/          EF Core DbContexts, repositories, entry-database router        [not compiled]
    Quality.Api/           ASP.NET Core Web API, JWT, Swagger                              [not compiled]
    Quality.Mobile.Core/   API client, session, view-models (no MAUI dependency)
    Quality.Mobile/        .NET MAUI Android app (XAML pages, Android services)            [not built]
    Quality.UserSqlGen/    generates "create user" SQL with a valid password hash
    Quality.ServiceTests/  business-rule tests          Quality.Mobile.Tests/  app-logic tests
    Quality.Verify/        normalizer parity check      Quality.Mobile.XamlCheck/  static XAML binding check
```

## 3. Architecture and data flow

```
Android (MAUI) ──HTTPS + JWT──► ASP.NET Core API ──► QualityMasterDB  (users, stages, machines, phenomena, audit)
 pages → view-models → ApiClient     controllers → services (Quality.Core)
 token in Android Keystore           │
                                     ├─ GDC shop entries ──► GDC_AndonDB      (qa.QualityRejectionEntries)
                                     ├─ CNC shop entries ──► MachineShopDB    (qa.QualityRejectionEntries)
                                     └─ Pre-treatment and FINAL entries ──► database of your choice (02c; one for both or one each)
```
* The app never talks to SQL Server and never chooses a database. The API picks the entry database from the validated shop (`Shops.EntryStoreKey`: `GDC` / `CNC`).
* Workflow: login → shop (one of the four) → machine → phenomenon (loaded for that shop) → review → save → confirmation. Each shop has exactly one stage, so the app skips the separate stage screen automatically.
* On save the server verifies: stage belongs to shop, machine belongs to shop, phenomenon belongs to that stage and is active, user may use that shop, and takes the inspector id and the time from the server. Every entry stores a **snapshot** of the phenomenon, stage and machine names, so later edits or deactivation never change history.
* Duplicate protection: the app creates a `submissionId` per entry and keeps it across retries. The server returns the original entry for a repeat, and a unique index in each entry database is the last line of defence.
* Master data and entries live in different databases, so a save is not one atomic transaction across them. The entry carries its own snapshot, so this is harmless.
* Authorisation is enforced on the server (roles + shop assignment); hiding buttons in the app is only a convenience.

Roles: **Administrator** (manages rejection phenomena, machines and users; views all entries; cannot create entries), **QualityInspector** (creates entries for the shops assigned to them; sees own entries), **Supervisor** (views entries of assigned shops).

### What the administrator can do in the app
| Area | Add | Edit | Deactivate | Delete |
|---|---|---|---|---|
| Rejection phenomena | yes | rename | yes | only if never used in an entry |
| **Machines** | yes (per shop) | code and name | yes | **only if never used in an entry** (otherwise deactivate) |
| **Users** | yes (role, shops, initial password) | name, role, shops, **reset password** | yes | not offered: users are deactivated so history stays intact |
Safeguards: you cannot deactivate yourself or change your own role; the last active administrator cannot be deactivated or demoted; a username and an employee code can never collide with another user's (login accepts either); every change is written to the audit log (passwords and hashes never are).

### Rejected quantity
Every entry records the **number of rejected parts** (whole number 1 to 9999, default 1, typed or with +/-). One entry = one phenomenon on one machine with one quantity. Quantity is saved with the entry, shown on the confirmation and in the entries list, and is part of the duplicate-submission check (retrying the same entry with another quantity is refused).

## 4. Prerequisites

* Windows with **Visual Studio 2022 17.14+ or Visual Studio 2026**, the **.NET 10 SDK**, and workloads *ASP.NET and web development* and *.NET Multi-platform App UI development* (includes Android SDK/emulator). The Android app targets `net10.0-android`: .NET MAUI 8 went out of support on 14 May 2025 and no longer builds with current SDKs. The API and libraries still target `net8.0`, which runs on the .NET 8 runtime (see section 12 for the planned move to .NET 10).
* SQL Server (any supported edition) and `sqlcmd` or SSMS.
* Python 3 with `openpyxl` only if you want to regenerate the seed from a changed Excel file (`pip install openpyxl`).

## 5. Database setup

Review every script before running. Replace `<INSTANCE>` with your instance (for example `.\SQLEXPRESS`). Add `-C` to sqlcmd if you get a certificate-trust error on a development server.

1. **Create** the master database yourself (e.g. `CREATE DATABASE QualityMasterDB;`) – after approval.
2. Master database, in this order (`-b` stops on the first error):
```
sqlcmd -S <INSTANCE> -d QualityMasterDB -E -b -i db\01_master_schema.sql
sqlcmd -S <INSTANCE> -d QualityMasterDB -E -b -i db\03_seed_reference.sql
sqlcmd -S <INSTANCE> -d QualityMasterDB -E -b -i db\04_seed_phenomena.sql
sqlcmd -S <INSTANCE> -d QualityMasterDB -E -b -i db\05_DEV_ONLY_placeholder_machines.sql     (development only)
```
3. Entry databases (existing ones; each script adds only schema `qa` and one table, and refuses rows for the wrong shop):
```
sqlcmd -S <INSTANCE> -d GDC_AndonDB   -E -b -i db\02a_entries_schema_GDC_AndonDB.sql
sqlcmd -S <INSTANCE> -d MachineShopDB -E -b -i db\02b_entries_schema_MachineShopDB.sql
```
Pre-treatment and FINAL entries: run once in the database you chose for them (decided: `QualityMasterDB`):
```
sqlcmd -S <INSTANCE> -d QualityMasterDB -E -b -i db\02c_entries_schema_PRE_FINAL.sql
```
**Adding the quantity column to an existing entries table:** if you already ran an earlier `02a`/`02b`/`02c`, run `db\07_migration_add_quantity.sql` once in each entry database that has the table (existing rows get quantity 1, an assumption). On a fresh setup the new `02a`/`02b`/`02c` already include it.

**Upgrading from the earlier version of these scripts** (which treated Pre-treatment and FINAL as part of GDC and CNC): run `db\06_migration_four_shops.sql` in `QualityMasterDB` first, then re-run `03_seed_reference.sql` and `05_DEV_ONLY_placeholder_machines.sql`.
4. **Verify** in `QualityMasterDB`:
```sql
SELECT s.StageName, COUNT(*) AS Phenomena FROM qa.RejectionPhenomena p JOIN qa.InspectionStages s ON s.Id = p.InspectionStageId GROUP BY s.StageName, s.SortOrder ORDER BY s.SortOrder;
-- expected: GDC Shop 25, CNC Shop 52, Pre-treatment Inspection 34, FINAL Inspection 34
```
The seed script also aborts and rolls back by itself if these counts differ.
5. **Least-privilege SQL login** for the API (suggestion – adapt names, set the password yourself):
```sql
-- master database                                  -- every entry database (GDC_AndonDB, MachineShopDB, the PRE/FINAL database)
CREATE USER [qr_api] FOR LOGIN [qr_api];            CREATE USER [qr_api] FOR LOGIN [qr_api];
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::qa TO [qr_api];   GRANT SELECT, INSERT ON SCHEMA::qa TO [qr_api];
```
Do not use `sa` or an administrator login for the API.

**Changing the Excel data later:** replace `db/seed/Rejection_Phinomina.xlsx`, run `python tools/import_excel.py` (it stops with an error on unexpected structure instead of guessing) and review the regenerated `db/04_seed_phenomena.sql` and `docs/import_report.md`. After go-live, change phenomena through the administrator screen, not by re-seeding.

## 6. Create users

The **first administrator** is created from the `Bootstrap` settings (section 7). After that, the administrator adds users **in the app** (Home → *Manage users*). The SQL generator below remains available for scripted or emergency use.

There is no user-management screen (not in the approved scope). Generate reviewable SQL instead; the password is never printed or stored, only its salted hash:
```
set QR_NEW_USER_PASSWORD=<at least 10 characters>
dotnet run --project src/Quality.UserSqlGen -- --employee E1234 --username jsmith --name "J Smith" --role QualityInspector --shops GDC,CNC,PRE,FINAL > create_user.sql
```
Review `create_user.sql`, then run it in `QualityMasterDB`. Roles: `Administrator`, `QualityInspector`, `Supervisor`. Non-admin users need `--shops` with any of `GDC`, `CNC`, `PRE`, `FINAL` (comma separated), or `--shops ALL` for all four shops. Each user only sees the shops assigned to them.
To deactivate a user: `UPDATE qa.Users SET IsActive = 0 WHERE EmployeeCode = N'E1234';` (takes effect on the next request).
The **first administrator** is created by the API at startup from the `Bootstrap` settings (section 7), but only when the Users table is empty.

## 7. Configure and run the API

No secrets are stored in source. `appsettings.json` has empty placeholders and the API **refuses to start** until they are set.

Development (user-secrets; run from the repository root):
```
dotnet user-secrets init --project src/Quality.Api
dotnet user-secrets set "ConnectionStrings:Master"     "Server=<INSTANCE>;Database=QualityMasterDB;Trusted_Connection=True;TrustServerCertificate=True" --project src/Quality.Api
dotnet user-secrets set "ConnectionStrings:GdcEntries" "Server=<INSTANCE>;Database=GDC_AndonDB;Trusted_Connection=True;TrustServerCertificate=True"   --project src/Quality.Api
dotnet user-secrets set "ConnectionStrings:CncEntries" "Server=<INSTANCE>;Database=MachineShopDB;Trusted_Connection=True;TrustServerCertificate=True" --project src/Quality.Api
dotnet user-secrets set "ConnectionStrings:PreEntries"   "Server=<INSTANCE>;Database=QualityMasterDB;Trusted_Connection=True;TrustServerCertificate=True" --project src/Quality.Api
dotnet user-secrets set "ConnectionStrings:FinalEntries" "Server=<INSTANCE>;Database=QualityMasterDB;Trusted_Connection=True;TrustServerCertificate=True" --project src/Quality.Api
dotnet user-secrets set "Jwt:Key" "<random string, at least 32 bytes>" --project src/Quality.Api
dotnet user-secrets set "Bootstrap:AdminEmployeeCode" "<code>"       --project src/Quality.Api
dotnet user-secrets set "Bootstrap:AdminUsername"     "<username>"   --project src/Quality.Api
dotnet user-secrets set "Bootstrap:AdminPassword"     "<min 12 chars>" --project src/Quality.Api
```
(Use SQL authentication in the connection string if that is what you have: `User Id=qr_api;Password=<...>`.)
Generate a JWT key in PowerShell: `$b = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)`.
After the first successful start, **delete the three `Bootstrap:*` settings**.

Run in Visual Studio: set `Quality.Api` as startup project, profile **https** → opens Swagger at `https://localhost:7218/swagger`. (Trust the ASP.NET dev certificate when asked, or run `dotnet dev-certs https --trust`.)
In Swagger: `POST /api/auth/login`, copy the token, click **Authorize**, paste the token. Request/response examples: `docs/API_EXAMPLES.md`.

Production: publish with `dotnet publish src/Quality.Api -c Release`, host behind HTTPS (IIS or Kestrel with a real certificate), set `ASPNETCORE_ENVIRONMENT=Production`, and provide the same settings as **environment variables** with double underscores, e.g. `ConnectionStrings__Master`, `ConnectionStrings__GdcEntries`, `ConnectionStrings__CncEntries`, `ConnectionStrings__PreEntries`, `ConnectionStrings__FinalEntries`, `Jwt__Key`. Swagger is off outside Development unless `Swagger__Enabled=true`. Token lifetime defaults to 480 minutes (`Jwt:ExpiryMinutes`).

## 8. Android app

### 8.1 Server address
There is deliberately no built-in default. On first launch tap **Server settings** on the login screen, enter the **HTTPS** address (for example `https://server:port`) and press **Test connection** (calls `/api/health`). The address is stored on the device. `http://` addresses are rejected by the app.

### 8.2 Android emulator
1. Visual Studio → *Android Device Manager* → create/start an emulator (API 24 or higher).
2. Start the API (section 7), set `Quality.Mobile` as startup project, target the emulator, **Debug**.
3. In the app, server address: `https://10.0.2.2:7218` (the emulator's alias for your PC's localhost). Debug builds accept the development certificate **only** for `10.0.2.2` and `localhost`.

### 8.3 Physical phone (development)
Easiest: USB debugging on the phone, then on the PC `adb reverse tcp:7218 tcp:7218`, and use `https://localhost:7218` in the app (Debug build). `adb` is in the Android SDK `platform-tools` folder.

### 8.4 Production certificate – decision needed
Release builds validate certificates normally. Android 7+ apps **do not trust user-installed CA certificates** by default. Choose one:
* Use a certificate from a publicly trusted CA for the server's hostname (simplest), or
* Use an internal CA and add an Android *network security configuration* that trusts your CA certificate (a small code change; ask me and I will add it), or
* Use a device-management (MDM) system that installs your CA as a system certificate.
Do **not** disable certificate checks in Release builds.

### 8.5 Signed release APK
1. Create a keystore once (JDK `keytool`) and **back it up safely** – losing it means you cannot update the installed app:
```
keytool -genkey -v -keystore qualityrejection.keystore -alias qr -keyalg RSA -keysize 2048 -validity 10000
```
2. Change `ApplicationId` in `src/Quality.Mobile/Quality.Mobile.csproj` (item 5 above) and bump `ApplicationVersion` for each release.
3. Provide the signing values as environment variables (never commit them; `.gitignore` already excludes keystores and APKs):
```
set QR_KEYSTORE_PATH=C:\path\qualityrejection.keystore
set QR_KEY_ALIAS=qr
set QR_STORE_PASS=<keystore password>
set QR_KEY_PASS=<key password>
```
4. Build: `dotnet publish src/Quality.Mobile/Quality.Mobile.csproj -f net10.0-android -c Release`. Look for a `*-Signed.apk` under `src/Quality.Mobile/bin/Release/net10.0-android/`.
5. Install: copy the APK to the phone and allow installs from that source, or `adb install -r <file>.apk`.
This path has not been executed; if the publish step reports a signing error, check that the four variables are set in the same terminal/Visual Studio instance.

## 9. Tests

On your machine (also scripted in `tools/run_checks.ps1`):
```
dotnet run --project src/Quality.ServiceTests -- db/seed/phenomena_seed.json     (expects: 30 passed)
dotnet run --project src/Quality.Mobile.Tests                                    (expects: 32 passed)
dotnet run --project src/Quality.Mobile.XamlCheck -- src/Quality.Mobile          (expects: RESULT: PASS)
dotnet run --project src/Quality.Verify -- db/seed/phenomena_seed.json           (expects: RESULT: PASS)
```
Manual acceptance test once everything runs: log in as an inspector, record one entry per stage, check the row landed in the right database (`SELECT TOP 5 * FROM qa.QualityRejectionEntries ORDER BY Id DESC`; for Pre-treatment/FINAL, in the database you chose), switch the phone to airplane mode and press Save (message, no duplicate after reconnecting and pressing Save again), then as administrator deactivate a phenomenon and confirm inspectors no longer see it while old entries keep their name.

## 10. Security notes
* Passwords: ASP.NET Core PasswordHasher (PBKDF2, salted). Login is rate-limited and always returns one generic message for wrong user/password/inactive account.
* JWT secret, connection strings and bootstrap password come only from configuration. The token is kept in Android Keystore-backed storage; app backup is disabled; cleartext HTTP is disabled.
* A deactivated user or changed role is rejected on the next request, not at token expiry.
* Error responses never contain SQL text, stack traces or secrets; details go to the server log only.
* Rotate `Jwt:Key` to sign everyone out.

## 11. Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| API exits at start: "Missing configuration value ..." | Set the user-secrets / environment variables (section 7). |
| "Jwt:Key must be at least 32 bytes" | Use a longer random key. |
| API starts, login returns "Invalid username or password" for the admin | Bootstrap runs only when `qa.Users` is empty; the roles must exist (script 03). Otherwise create the user with `Quality.UserSqlGen`. |
| 503 `database_unavailable` | SQL Server unreachable, wrong instance, login lacks rights, or TLS trust (`TrustServerCertificate=True` for development). Check the API log. |
| 503 `store_not_configured` | One of the `GdcEntries` / `CncEntries` / `PreEntries` / `FinalEntries` connection strings is missing. |
| Seed script: "Seed count mismatch" | Script 03 not run first, or the phenomena table already holds other rows for a stage. |
| App: "The server address is not set" | Enter it in Settings (section 8.1). |
| App: "Cannot reach the server" | Wrong address/port, API not running, firewall, phone not on the same network, emulator not using `10.0.2.2`, or certificate not trusted (section 8). |
| App: "Your session has expired" | Token lifetime reached or the user was deactivated; sign in again. |
| "No machines are configured" | Script 05 not run (dev) or the real machine list has not been loaded for that shop. |
| "No active rejection phenomena are configured for this stage" | All items for that stage are deactivated; reactivate in the admin screen. |
| An entry shows "Already saved earlier" | A retry of the same entry; no duplicate was created. |
| 409 `phenomenon_inactive` when saving | An administrator deactivated it a moment ago; the app reloads the list. |
| `CS1061 'RateLimiterOptions' does not contain ... 'AddFixedWindowLimiter'` | Fixed (missing `using Microsoft.AspNetCore.RateLimiting;` in `Quality.Api/Program.cs`). Re-download the latest zip. |
| `NETSDK1202 workload 'net8.0-android' is out of support` / `SupportedOSPlatformVersion 24.0 cannot be higher than TargetPlatformVersion 21.0` | Fixed by retargeting `Quality.Mobile` to `net10.0-android`. Needs the .NET 10 SDK and the MAUI workload (`dotnet workload install maui` or the Visual Studio installer). |
| API publishes a `.dll` but no `.exe` | `src/Directory.Build.props` sets `UseAppHost=false` (needed where it was authored). Delete that file, or the line, if you want a native executable; IIS and `dotnet Quality.Api.dll` work either way. |
| Visual Studio build errors in `Quality.Data`, `Quality.Api` or `Quality.Mobile` | These projects were never compiled before delivery; send the error list. |
| MAUI Android build: workload / SDK missing | Visual Studio Installer → *.NET Multi-platform App UI development*, or `dotnet workload install maui-android`. |

## 12. Not included (by design or scope)
* No offline mode or local queue. The app shows clear network errors and a safe retry instead. Offline support is **not** implemented.
* No photos, barcode, PLC integration or dashboards. No data export.
* Users cannot change their own password; an administrator resets it. Users are deactivated, never deleted.
* The administrator list shows creators/modifiers as "user #id" because the API returns ids only.
* **Plan to move the API and libraries from .NET 8 to .NET 10 before go-live:** .NET 8 itself leaves support on 10 November 2026. It is a version bump of the `TargetFramework` and the EF Core / JwtBearer / Swashbuckle packages (Swashbuckle changed its OpenAPI types in recent versions), so it should be done and re-tested rather than rushed. Not done yet.
* No automated tests against a real SQL Server, a running API over HTTP, or a device yet.

## 13. Delivery checklist
- [x] Phase 1 Excel inspection · [x] Phase 2 database and import · [x] Phase 3 API and business rules · [x] Phase 4 Android app · [x] Phase 5 admin screens · [x] Phase 7 documentation
- [ ] **Phase 6 (needs you):** build in Visual Studio, run the SQL scripts on a development server, run the API and app end to end, then fix what breaks. Update `docs/TEST_RESULTS.md` with real results.
