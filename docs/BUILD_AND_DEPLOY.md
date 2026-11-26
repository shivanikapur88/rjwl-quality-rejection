# Build, configure and deploy

## 1. Toolchain

| Item | Version used for the evidence in this repository |
|---|---|
| .NET SDK | 10.0.404 (`global.json`: 10.0.100 or a later 10.0 feature band) |
| Target frameworks | `net10.0` (Core, Data, Api, Mobile.Core, tests), `net10.0-android` (Mobile) |
| MAUI workload | `maui-android` 10.0.110 manifest, `Microsoft.Maui.Controls` 10.0.130 |
| Android | SDK platform 36, build-tools 36.0.0, JDK 21; minimum device Android 7.0 (API 24) |
| Packages | EF Core SqlServer 10.0.15, JwtBearer 10.0.15, Swashbuckle 10.3.1, xUnit v3 4.0.3 (central versions in `src/Directory.Packages.props`) |
| SQL Server | tests run on SQL Server 2022 (16.0.4325, CU28) |

Windows: Visual Studio 2026 (or 2022 17.14+) with *ASP.NET and web development* and *.NET Multi-platform App UI development*, or the .NET 10 SDK plus `dotnet workload install maui-android`. Run builds and tests only where IT has approved them: earlier test runs on the developer PC were blocked by Windows Application Control, which must not be bypassed.

## 2. Build and test

```bash
dotnet build src/QualityRejection.sln
```
The Android project needs the workload; without it, build the other projects individually.
```bash
dotnet test --project src/Quality.Tests.Unit
```
Integration tests need a SQL Server where the test login may create and drop databases. They create `QRIT_<timestamp>_*` databases and drop them afterwards. Never point them at production.
```bash
export QR_TEST_SQL="Server=localhost,14333;User Id=sa;Password=<pwd>;TrustServerCertificate=True"
dotnet test --project src/Quality.Tests.Integration
```
PowerShell: `$env:QR_TEST_SQL="…"`. Optional: `QR_TEST_RCSI=OFF` (default ON), `QR_TEST_KEEP=1` (keep the databases).

A throw-away SQL Server for testing (Docker):
```bash
docker run -d --name qr-sql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<strong pwd>' -p 14333:1433 mcr.microsoft.com/mssql/server:2022-latest
```

Reference data check (Python 3 + `openpyxl`):
```bash
python3 -I tools/import_excel.py --check
```

## 3. API configuration

No secrets are in source. The API refuses to start without these settings (user-secrets in development, environment variables with `__` in production, e.g. `ConnectionStrings__Master`):

| Setting | Value |
|---|---|
| `ConnectionStrings:Master` | QualityMasterDB |
| `ConnectionStrings:GdcEntries` / `CncEntries` | GDC_AndonDB / MachineShopDB |
| `ConnectionStrings:PreEntries` / `FinalEntries` | **same as Master** (PRE/FINAL entries live there) |
| `Jwt:Key` | random, at least 32 bytes. Changing it signs everybody out. |
| `Jwt:Issuer` / `Audience` / `ExpiryMinutes` | defaults in `appsettings.json` (480 min) |
| `Bootstrap:AdminEmployeeCode` / `AdminUsername` / `AdminPassword` / `AdminDisplayName` | only for the very first start (empty Users table); password at least 12 characters; **remove after the first start** |
| `LoginThrottle:RequestsPerMinutePerClient` | optional, default 300 |
| `ReverseProxy:KnownProxies` | optional, list of proxy IP addresses; set it when behind IIS ARR / nginx / a load balancer so login throttling sees the real client address |
| `Swagger:Enabled` | optional, default off outside Development |

Template (no secrets): `src/Quality.Api/appsettings.json`. Development example:
```bash
dotnet user-secrets set "ConnectionStrings:Master" "Server=<I>;Database=QualityMasterDB;Trusted_Connection=True;TrustServerCertificate=True" --project src/Quality.Api
```
Set the other connection strings, `Jwt:Key` and the `Bootstrap:*` values the same way.

## 4. Deploy the API

1. Database: [DATABASE_RUNBOOK.md](DATABASE_RUNBOOK.md) (fresh install or upgrade), with backups first.
2. Publish:
```bash
dotnet publish src/Quality.Api -c Release -o out/api
```
Host behind HTTPS (IIS with the ASP.NET Core Hosting Bundle **10.0**, or Kestrel as a Windows service). Set `ASPNETCORE_ENVIRONMENT=Production`. `Directory.Build.props` sets `UseAppHost=false`, so there is no `.exe`; IIS and `dotnet Quality.Api.dll` work, or remove that line.
3. Check: `GET /api/health` → 200; `GET /api/health/ready` → 200 with every component `ok`.
4. Sign in as the bootstrap administrator in the app, create the real users, then remove the `Bootstrap:*` settings and restart.
5. **Run one instance.** The login throttle counters are in memory per instance. With several instances behind a load balancer the limits apply per instance; move them to a shared store first if that matters.

**Rollback:** stop the new API, restore the database backups taken in step 1 and redeploy the previous build. To run the old API on the upgraded database without a restore, see the trigger warning in [DATABASE_RUNBOOK.md §4](DATABASE_RUNBOOK.md).

## 5. Android app

### Debug APK (testing)
```bash
dotnet build src/Quality.Mobile -f net10.0-android -c Debug
```
Output: `src/Quality.Mobile/bin/Debug/net10.0-android/com.yourcompany.qualityrejection-Signed.apk`, signed with the **debug** key. For testing only: it cannot be upgraded by a release-signed build, and it accepts the development certificate for `10.0.2.2`/`localhost`.

### Signed release APK
1. Decide the package id ([BUSINESS_DECISIONS.md](BUSINESS_DECISIONS.md) #3) and set `ApplicationId` in `src/Quality.Mobile/Quality.Mobile.csproj`. Bump `ApplicationVersion` for every release.
2. Create a keystore once and **back it up**. Losing it means installed apps can never be updated.
```bash
keytool -genkey -v -keystore qualityrejection.keystore -alias qr -keyalg RSA -keysize 2048 -validity 10000
```
3. Set `QR_KEYSTORE_PATH`, `QR_KEY_ALIAS`, `QR_STORE_PASS` and `QR_KEY_PASS` as environment variables (never commit them), then publish:
```bash
dotnet publish src/Quality.Mobile -f net10.0-android -c Release
```
The signed APK is under `bin/Release/net10.0-android/`.
4. Replace the placeholder icon SVGs in `src/Quality.Mobile/Resources/AppIcon/` first.

### Server address and certificates
The app has no built-in server address. On first launch: **Server settings** → `https://<host>:<port>` → **Test connection**. `http://` is refused. Release builds validate certificates normally; Android does not trust user-installed CAs. Use a public-CA certificate, an internal CA with an Android network-security configuration (a small code change), or MDM-installed CAs. Never disable certificate checks.

### Emulator and phone (development)
Emulator: API address `https://10.0.2.2:<port>`. Phone over USB: `adb reverse tcp:<port> tcp:<port>`, then `https://localhost:<port>`.
