> **Superseded** by [SPECIFICATION.md](SPECIFICATION.md) where they differ (e.g. PRE/FINAL placement is decided: QualityMasterDB).
> Kept as the original design record.

# Phase 2 - Database design, shop-stage mapping, import process

Status legend: **Executed** = actually run during authoring; **Not executed** = written but not run.

## 1. Database layout (approval items marked ⚠)

| Database | Contents | Scripts |
|---|---|---|
| ⚠ `QualityMasterDB` (proposed name, new) | Users, Roles, Shops, InspectionStages, ShopStages, UserShops, Machines, RejectionPhenomena, AuditLog | 01, 03, 04, 05(dev) |
| `GDC_AndonDB` (existing, name per your brief) | `qa.QualityRejectionEntries` for GDC Shop | 02a |
| ⚠ database of your choice | `qa.QualityRejectionEntries` for Pre-treatment and FINAL | 02c |
| `MachineShopDB` (existing, name per your brief) | `qa.QualityRejectionEntries` for CNC Shop | 02b |

⚠ **Nothing has been run against any database.** The SQL Server instance, existing schemas and tables are unknown to me and not inspected.
Before running: (a) confirm/choose the master database (creating it is your decision, scripts do not do it),
(b) confirm that schema `qa` and table `qa.QualityRejectionEntries` do not already exist in the two shop databases
(scripts only create new objects, guarded with existence checks), (c) confirm the instance name for connection strings.

Rationale for a shared master DB: users, stages and phenomena must be one source of truth across both shops, and
SQL Server cannot enforce cross-database foreign keys. Entries therefore store master ids **plus immutable snapshots**
(phenomenon name, stage, machine, inspector code). The backend validates all master references before writing.
Consequence: the master check and the entry insert are in two databases, so they are not one atomic transaction.
The window is small and harmless (a phenomenon deactivated in the same instant as a submission); the entry still carries its snapshot.

## 2. Shops and stages (UPDATED: four independent shops)

**Correction after your clarification:** GDC Shop, CNC Shop, Pre-treatment Inspection and FINAL Inspection are four independent shops.
They are not nested (the earlier design wrongly offered Pre-treatment and FINAL inside both GDC and CNC).

```
Shop  GDC   -> stage GDC Shop                 entries -> GDC_AndonDB
Shop  CNC   -> stage CNC Shop                 entries -> MachineShopDB
Shop  PRE   -> stage Pre-treatment Inspection entries -> database of your choice (02c)
Shop  FINAL -> stage FINAL Inspection         entries -> database of your choice (02c)
```
* `qa.ShopStages` is kept as an explicit one-to-one mapping (4 rows), so each phenomenon list (25 / 52 / 34 / 34) belongs to exactly one shop and one stage, as in your original brief.
* Machines belong to a shop, so every shop has its own machine list (none was provided; dev placeholders only).
* Pre-treatment and FINAL keep separate lists even though their initial content is identical.
* Because PRE and FINAL may share one entry table, every entry query filters by `ShopId`, and fetching an entry by id also checks its shop.
* The app skips the stage screen when a shop has exactly one stage.
* Open decision: which database stores PRE and FINAL entries (`02c`, `ConnectionStrings:PreEntries` / `FinalEntries`).

## 3. Import process (Executed)

`python3 tools/import_excel.py [path-to-xlsx]` reads the workbook, stops with an error on any unexpected structure
(extra/missing header, several sheets, non-text cell), and regenerates `db/seed/phenomena_seed.json`, `db/04_seed_phenomena.sql`, `docs/import_report.md`.
* Original names are stored untouched in `PhenomenonName` (including the double space in `LUG HEIGHT  O/S /UNCLEAN`).
* Uniqueness key `NormalizedName` = trim, collapse whitespace, upper-case. Unique per stage.
* Approved duplicate policy: first occurrence kept; CNC `DENT` at B42 skipped and reported (see import_report.md).
* Seed SQL is idempotent, runs in one transaction, and `THROW`s (rolling back) if per-stage counts differ from 25 / 52 / 34 / 34.
* `PhenomenonCode` (e.g. `GDC_SHOP-001`) is a generated identifier in Excel row order, not a name.

Results: GDC 25, CNC 52 (53 raw), Pre-treatment 34, FINAL 34 = 145 imported of 146 raw rows.

## 4. Tables (refinements to your suggested schema)
* `ShopStages` (explicit mapping), `UserShops` (assigned shops), `Shops.EntryStoreKey` (logical key; connection strings live only in server config).
* `RejectionPhenomena.NormalizedName`, `RowVer rowversion` (optimistic concurrency); no shop column (see section 2).
* `QualityRejectionEntries`: `SubmissionId` UNIQUE (idempotency), snapshots, `CHECK (ShopCode = 'GDC'|'CNC')` per database, indexes for "my entries" and "used?" lookups.
* Hard delete of a phenomenon: backend must confirm zero rows in **both** entry databases (`IX_QRE_Phenomenon`), then delete and audit. Otherwise deactivate only.

## 5. Verification actually performed in this phase

| Check | Method | Result |
|---|---|---|
| Per-stage Excel counts (25/53/34/34 raw) | independent openpyxl read | Executed - PASS |
| SQL seed text equals Excel distinct values, exact text and order, all 4 stages | parsed `04_seed_phenomena.sql` and compared with a fresh Excel read | Executed - PASS (145 rows) |
| C# `TextNormalizer` equals Python normalizer on all 146 values | `Quality.Verify` console app | Executed - PASS (0 mismatches) |
| `Quality.Core` compiles | `dotnet build` (.NET 8.0.131), 0 warnings as errors | Executed - PASS |
| T-SQL syntax of scripts 01-05 | `sqlglot` tsql parser | Executed - 0 parse errors (syntax only) |
| Scripts run on SQL Server (constraints, FKs, THROW checks, idempotency) | - | **NOT RUN** (no SQL Server available) |

Known tooling limit: no NuGet access in the authoring environment, so EF Core / JWT / Swagger / MAUI packages can't be restored here.
Phase 3+ code can therefore be written but not compiled in this environment; it will be labelled accordingly.
