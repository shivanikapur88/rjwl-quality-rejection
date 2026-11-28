# RJWL Quality Rejection Entry

An Android app (.NET MAUI), an ASP.NET Core Web API and SQL Server, used to record quality rejections in **four independent shops**: GDC Shop, CNC Shop, Pre-treatment Inspection and FINAL Inspection. Each shop has its own inspection stage, machines and list of rejection phenomena. Entries are stored in three databases: GDC entries in `GDC_AndonDB`, CNC entries in `MachineShopDB`, and PRE and FINAL entries in `QualityMasterDB`.

## Status (read first)

| | |
|---|---|
| Source | Complete and editable. The mobile source missing from the supplied package was **recovered from its compiled DLLs** ([docs/RECOVERY.md](docs/RECOVERY.md)). |
| Framework | **.NET 10** for every project (the API and libraries were on .NET 8, which left support on 10 Nov 2026) |
| Review findings Q1-Q9 | Fixed and tested on SQL Server 2022 ([docs/FINDINGS_CLOSURE.md](docs/FINDINGS_CLOSURE.md)) |
| Round 2 | Entry corrections, shift and production date, app shift summary, machine QR codes, web dashboard at `/dashboard/` (read-only; from round 3 administrators also upload production counts there) ([SPECIFICATION.md §12](docs/SPECIFICATION.md)), checked by an independent review |
| Round 3 | Dashboard drill-down, previous-period comparison, Pareto, and rejection rate from uploaded production counts ([SPECIFICATION.md §13](docs/SPECIFICATION.md)), checked by an independent review |
| Reviews | Independent reviews by Claude Fable (round 2, round 3, a complete review and a UI/UX review) and four full-application reviews by Claude Sonnet 5.5; findings and fixes in [docs/FINDINGS_CLOSURE.md](docs/FINDINGS_CLOSURE.md) |
| Tests | 166 unit and 57 SQL Server integration tests, all passing on a clean checkout; the API runs as a least-privilege login in the tests ([docs/TEST_EVIDENCE.md](docs/TEST_EVIDENCE.md)) |
| Android | Debug APK builds. **Not yet tested on an emulator or device**; no signed release APK yet. |
| Design | Dashboard and app redesigned after a Fable UI/UX review (2,416 displayed numbers recomputed, 0 mismatches); before/after images in docs/evidence/design |
| iOS | iOS project parts added (iPhone and iPad, iOS 15+). **Not built yet**: Apple allows iOS builds only on a Mac ([BUILD_AND_DEPLOY.md §6](docs/BUILD_AND_DEPLOY.md)). |
| Desktop app | The dashboard installs as a Windows desktop app from Edge or Chrome for supervisors and administrators (own window and taskbar icon, needs HTTPS; IT can push it by policy). Never stores rejection data on the PC ([BUILD_AND_DEPLOY.md §4a](docs/BUILD_AND_DEPLOY.md)) |
| Production readiness | **Not yet.** See [docs/READINESS.md](docs/READINESS.md) for what is blocked: device acceptance, the real machine list, package id, keystore and certificate, and business acceptance. |

## Documents

| Document | Contents |
|---|---|
| [SPECIFICATION.md](docs/SPECIFICATION.md) | Reconciled functional and technical specification: shops, roles, entry rules, history, administration, API, security |
| [RECOVERY.md](docs/RECOVERY.md) | What was in the package, what was missing, how it was restored |
| [FINDINGS_CLOSURE.md](docs/FINDINGS_CLOSURE.md) | Q1-Q9 and the other problems: fix and evidence for each |
| [DATABASE_RUNBOOK.md](docs/DATABASE_RUNBOOK.md) | Fresh install, upgrade, loading real machines, recovery, backup/restore |
| [BUILD_AND_DEPLOY.md](docs/BUILD_AND_DEPLOY.md) | Toolchain, build and test, API configuration, deployment and rollback, APK signing, certificates |
| [USER_GUIDE.md](docs/USER_GUIDE.md) | Inspector, supervisor and administrator instructions |
| [TEST_EVIDENCE.md](docs/TEST_EVIDENCE.md) / [evidence/](docs/evidence/) | What ran, where, the results, and what is not tested |
| [READINESS.md](docs/READINESS.md) | Implemented / built / tested / accepted / blocked, per area |
| [BUSINESS_DECISIONS.md](docs/BUSINESS_DECISIONS.md) | Open questions for RJWL |
| [API_EXAMPLES.md](docs/API_EXAMPLES.md), [import_report.md](docs/import_report.md) | Supplied, extended |
| [Phase2_Design.md](docs/Phase2_Design.md), [TEST_RESULTS.md](docs/TEST_RESULTS.md) | Supplied, kept for the record (superseded/historical) |

## Layout

```
incoming/                 the supplied ZIP, unchanged (on main)
db/                       SQL scripts 01-16 (see the runbook for which database each belongs to) + seed/ (workbook, JSON)
tools/                    import_excel.py, create-test-databases.sh, test-upgrade-path.sh
docs/                     documentation and evidence
src/
  QualityRejection.sln    Directory.Build.props, Directory.Packages.props (central package versions)
  Quality.Core/           entities, DTOs, all business rules and authorization decisions
  Quality.Data/           EF Core mappings, locking, entry-database routing
  Quality.Api/            HTTP endpoints, JWT, login throttling, error mapping, readiness
  Quality.Mobile.Core/    API client, session, navigation abstractions, 18 view-models (14 recovered, 4 added in round 2)
  Quality.Mobile/         Android app: 18 pages (14 recovered, 4 added), styles, platform services, manifest
  Quality.Api/wwwroot/dashboard/  web dashboard (production-count upload for administrators) and QR label printing (no external libraries)
                                  also installable as a desktop app: manifest.webmanifest, sw.js, icons/
  Quality.Tests.Unit/     166 tests (rules, shifts, throttling, SQL-script checks, app logic through the real API client)
  Quality.Tests.Integration/  56 tests against real SQL Server (HTTP, concurrency, corrections, reports, scripts, reference data)
```

## Quick start (development)

```bash
docker run -d --name qr-sql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<strong pwd>' -p 14333:1433 mcr.microsoft.com/mssql/server:2022-latest
```
```bash
SQLCMD="sqlcmd -S localhost,14333 -U sa -P <pwd> -C" tools/create-test-databases.sh QRDEV OFF
```
That command creates `QRDEV_Master`, `QRDEV_Gdc` and `QRDEV_Cnc` with all scripts. For development machines, also run `db/05_DEV_ONLY_placeholder_machines.sql` in `QRDEV_Master`. Then set the API settings ([BUILD_AND_DEPLOY.md §3](docs/BUILD_AND_DEPLOY.md)) and run it:
```bash
dotnet run --project src/Quality.Api --launch-profile https
```
Android: `dotnet build src/Quality.Mobile -f net10.0-android`, deploy to an emulator, and set the server address `https://10.0.2.2:7218`.

Tests:
```bash
dotnet test --project src/Quality.Tests.Unit
```
```bash
QR_TEST_SQL="Server=localhost,14333;User Id=sa;Password=<pwd>;TrustServerCertificate=True" dotnet test --project src/Quality.Tests.Integration
```

## Not included (outside the documented scope)

There is no offline mode, web *administration* portal, audit-viewing screen, photo capture, export or PLC integration. The rejection rate needs production counts to be uploaded (or a feed agreed), and its counting rules confirmed (decision #13). Each needs a decision first ([BUSINESS_DECISIONS.md](docs/BUSINESS_DECISIONS.md)). The CNC production collector and ShopMonitor dashboard are separate systems and are not changed.
