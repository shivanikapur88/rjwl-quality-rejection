# Package inventory and source recovery

Phase 1 of the implementation brief: what was supplied, what was missing, and how the missing parts were restored.

## 1. What was supplied

| Item | Value |
|---|---|
| File | `incoming/QualityRejectionApp1-20261126T085519Z-1-001.zip` (kept unchanged on `main`) |
| SHA-256 | `16b603e4b6b9311923b6ca1895e67656fd98a248410a96478c6930a450000429` |
| Files | 1,879 in total: **57** source/document files, **1,822** build output files (`bin/`, `obj/`, `.vs/`) |
| Baseline commit | `c78ddf5`: the 57 source/document files exactly as received. Every later change is a reviewable diff against it. |

The brief named `…085724Z…`; the file actually supplied is `…085519Z…`. Its file count (1,879) matches the brief's description of the package.

### Projects
The solution file listed 9 projects; only 4 had source.

| Project | Supplied | Now |
|---|---|---|
| `Quality.Core` | source | source, .NET 10 |
| `Quality.Data` | source | source, .NET 10 |
| `Quality.Api` | source | source, .NET 10 |
| `Quality.Mobile` | `.csproj`, `App`, `AppShell`, `MauiProgram`, `Controls/StatusBar` only | complete at recovery (14 pages, styles, platform code, Android entry points, manifest); 18 pages now (round 2 added 4) |
| `Quality.Mobile.Core` | **missing** (only the compiled DLL) | recovered source (section 3) |
| `Quality.ServiceTests`, `Quality.Mobile.Tests`, `Quality.Verify`, `Quality.Mobile.XamlCheck` | **missing** | replaced by `Quality.Tests.Unit` (27 tests at recovery; see TEST_EVIDENCE for current counts) and `Quality.Tests.Integration` (37 tests at recovery on SQL Server) |
| `Quality.UserSqlGen` | **missing** | not rebuilt: the first administrator comes from `Bootstrap:*` settings and all others are created in the app. See [BUSINESS_DECISIONS.md](BUSINESS_DECISIONS.md) if a scripted path is still wanted. |
| `tools/import_excel.py`, `tools/run_checks.ps1` | **missing** (described in README) | `import_excel.py` rebuilt and proven against the supplied output (section 4); checks are in [TEST_EVIDENCE.md](TEST_EVIDENCE.md) |

## 2. Build output: evidence, not source

Following the brief, build output and logs are used as **evidence** only; no binary is part of the delivered build.

| Evidence | What it shows |
|---|---|
| `src/bin/Debug/net8.0/Quality.Api.dll` (23 Oct) and `obj/project.assets.json` | The API **was** compiled, with EF Core SqlServer 8.0.32 and JwtBearer 8.0.32. The documents saying "not compiled" were out of date. |
| `src/Quality.Mobile/bin/Debug/net10.0-android/*` (7 Nov), `android_debug_keystore.flag`, `upload.flag`, `devices.cache` | A `net10.0-android` **debug** build was made and deployed to an x86_64 emulator on the developer's PC (`C:\Users\MK_VERMA\…`). No APK was included. |
| `obj/Debug/net10.0-android/AndroidManifest.xml` | The generated manifest, including the comment and settings of the original `Platforms/Android/AndroidManifest.xml`. |

## 3. How the mobile source was recovered

| Part | Source of truth | Method | Fidelity |
|---|---|---|---|
| 14 page XAML files, `Colors.xaml`, `Styles.xaml` | `Quality.Mobile.dll` (7 Nov 2026, SHA-256 `e343cfc7…`), embedded XAML resources | Extracted byte-for-byte with ILSpy 11.2 (`ilspycmd`). The App, AppShell and StatusBar XAML extracted from the same DLL are **identical** to the supplied files, so the extraction is exact. | exact |
| Page code-behind, `AppPage`, `Platform/*` services, `MainApplication` | same DLL | Decompiled, then re-typed by hand as normal source (the generated `InitializeComponent` was removed; MAUI generates it again). | equivalent; file comments say "Recovered from…" |
| `MainActivity` attributes | generated `AndroidManifest.xml` | The decompiler could not decode the attribute; values taken from the manifest's activity entry. | equivalent |
| `Platforms/Android/AndroidManifest.xml` | generated manifest | Permissions, `allowBackup=false`, `usesCleartextTraffic=false`, icon names and comment restored. | equivalent |
| `Quality.Mobile.Core` (API client, session, routes, 14 view-models, helper types) | `Quality.Mobile.Core.dll` (23 Oct 2026, SHA-256 `d91da941…`) with its `.pdb` (variable names) | Decompiled with ILSpy into `src/Quality.Mobile.Core`; every file starts with "Recovered by decompiling…". User-facing messages, routes and API paths are exactly the compiled ones. | behaviourally exact; style is decompiler output (see below) |
| App icon (`appicon.svg`, `appiconfg.svg`) | not present anywhere | **New placeholders** (dark blue square with a white tick), clearly commented. | **placeholder: replace with RJWL artwork** |

**Was the 23 Oct Mobile.Core the one the 7 Nov pages were written against?** Yes, as far as the compiler can tell. All 14 pages use compiled bindings (`x:DataType`). The Android build compiles every binding in every page against the recovered view-models and fails on any missing or mistyped property. It builds with no binding errors.

**Known cosmetic debt:** decompiled C# has redundant casts, `if (true)` blocks and lost nullable annotations, which give about 135 nullable warnings in `Quality.Mobile.Core`. They do not affect behaviour. Files changed in this round (`MyEntriesViewModel`, parts of `EntryFormViewModel`, `LoginViewModel`, `MachineViewModel`, `ApiClient`) are partly cleaned up. A full tidy-up is a separate, low-risk task.

## 4. Reference-data tooling

`tools/import_excel.py` was rebuilt from the rules in `docs/import_report.md`. Running it against `db/seed/Rejection_Phinomina.xlsx` reproduces the supplied `db/seed/phenomena_seed.json` **exactly**: all 146 rows, each with stage, cell, original text, normalized key, code, status and duplicate reference, plus the per-stage counts. `python3 -I tools/import_excel.py --check` verifies this at any time.

The generated `db/04_seed_phenomena.sql` keeps the supplied INSERT statements unchanged (same 145 codes and texts). Two parts changed, on purpose:
* The end-of-script check now confirms that **every seeded code** is present, instead of "the stage has exactly N rows". The old check made a re-run fail as soon as an administrator had added a phenomenon.
* The import audit row is written only when the run actually inserted something.

## 5. Documentation contradictions resolved
See [SPECIFICATION.md §1](SPECIFICATION.md#1-reconciled-facts) for the reconciled positions on page count, framework, user management, PRE/FINAL placement, compile status and test counts.
