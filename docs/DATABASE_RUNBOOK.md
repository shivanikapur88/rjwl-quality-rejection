# Database runbook

**Do not run every script in filename order against every database.** Each script says which database it belongs to. All scripts are idempotent unless stated otherwise, and none deletes business data. Use `sqlcmd -b` so a script stops at its first error. Review every script before running it in production, and take a backup first (section 5).

`<I>` = SQL Server instance. Use `-E` for Windows authentication, or `-U … -P …`.

## Script map

| Script | Database | Fresh install | Upgrade | Notes |
|---|---|:---:|:---:|---|
| `01_master_schema.sql` | QualityMasterDB | yes | (re-run harmless) | schema `qa`, master tables |
| `02a_entries_schema_GDC_AndonDB.sql` | GDC_AndonDB | yes | no | adds schema `qa` + one table; nothing else in GDC_AndonDB |
| `02b_entries_schema_MachineShopDB.sql` | MachineShopDB | yes | no | same, for CNC |
| `02c_entries_schema_PRE_FINAL.sql` | QualityMasterDB | yes | no | PRE + FINAL entries table |
| `03_seed_reference.sql` | QualityMasterDB | yes | (re-run harmless) | roles, 4 shops, 4 stages, mappings |
| `04_seed_phenomena.sql` | QualityMasterDB | yes | **only with a plan** | 145 phenomena; inserts only missing ones, never renames |
| `05_DEV_ONLY_placeholder_machines.sql` | QualityMasterDB | **development only** | **never** | fake `DEV-*` machines |
| `06_migration_four_shops.sql` | QualityMasterDB | no | only from the old two-shop layout | then re-run 03 (**not** 05) |
| `07_migration_add_quantity.sql` | every entry DB with an old table | no | only if `Quantity` is missing | old rows get quantity **1: an assumption to confirm** |
| `08_upgrade_master_review_fixes.sql` | QualityMasterDB | **yes** | **yes** | review fixes Q2/Q3/Q5/Q6, version columns |
| `09_upgrade_entries_indexes.sql` | **each** entry DB (GDC_AndonDB, MachineShopDB, QualityMasterDB) | **yes** | **yes** | indexes for machine checks and paging |
| `10_load_production_machines.sql` | QualityMasterDB | when the list is approved | when the list changes | validated machine load with dry run (section 3) |
| `11_upgrade_master_shifts_corrections.sql` | QualityMasterDB | **yes** | **yes** | shift definitions (A 06:00, B 14:00, C 22:00), correction requests |
| `12_upgrade_entries_shift_reversal.sql` | **each** entry DB | **yes** | **yes** | production date + shift, reversal markers; **backfills existing rows** (IST, A/B/C times written in the script) |
| `13_upgrade_master_production_counts.sql` | QualityMasterDB | **yes** | **yes** | `qa.ProductionCounts` for the rejection rate |
| `14_production_counts_feed_TEMPLATE.sql` | QualityMasterDB | no | when a feed is agreed | **template**: read-only feed of production counts from the production systems; fill in the source names first |

All scripts set `QUOTED_IDENTIFIER ON` themselves where needed. `sqlcmd` starts with it OFF, and SQL Server then refuses the filtered indexes in 11; a unit test now checks this for every script.

## 1. Fresh installation

1. Create `QualityMasterDB` (only after approval): `CREATE DATABASE QualityMasterDB;`
2. Master:
```bash
sqlcmd -S <I> -E -b -d QualityMasterDB -i db/01_master_schema.sql
sqlcmd -S <I> -E -b -d QualityMasterDB -i db/02c_entries_schema_PRE_FINAL.sql
sqlcmd -S <I> -E -b -d QualityMasterDB -i db/03_seed_reference.sql
sqlcmd -S <I> -E -b -d QualityMasterDB -i db/04_seed_phenomena.sql
sqlcmd -S <I> -E -b -d QualityMasterDB -i db/08_upgrade_master_review_fixes.sql
sqlcmd -S <I> -E -b -d QualityMasterDB -i db/09_upgrade_entries_indexes.sql
sqlcmd -S <I> -E -b -d QualityMasterDB -i db/11_upgrade_master_shifts_corrections.sql
sqlcmd -S <I> -E -b -d QualityMasterDB -i db/12_upgrade_entries_shift_reversal.sql
sqlcmd -S <I> -E -b -d QualityMasterDB -i db/13_upgrade_master_production_counts.sql
```
3. Existing entry databases (check first that schema `qa` and `qa.QualityRejectionEntries` do not already exist there for something else):
```bash
sqlcmd -S <I> -E -b -d GDC_AndonDB   -i db/02a_entries_schema_GDC_AndonDB.sql
sqlcmd -S <I> -E -b -d GDC_AndonDB   -i db/09_upgrade_entries_indexes.sql
sqlcmd -S <I> -E -b -d GDC_AndonDB   -i db/12_upgrade_entries_shift_reversal.sql
sqlcmd -S <I> -E -b -d MachineShopDB -i db/02b_entries_schema_MachineShopDB.sql
sqlcmd -S <I> -E -b -d MachineShopDB -i db/09_upgrade_entries_indexes.sql
sqlcmd -S <I> -E -b -d MachineShopDB -i db/12_upgrade_entries_shift_reversal.sql
```
4. Machines: production → section 3. Development/test only → `05_DEV_ONLY_placeholder_machines.sql`.
5. Verify:
```sql
SELECT s.StageCode, COUNT(*) FROM qa.RejectionPhenomena p JOIN qa.InspectionStages s ON s.Id = p.InspectionStageId GROUP BY s.StageCode;
-- GDC_SHOP 25, CNC_SHOP 52, PRE_TREATMENT 34, FINAL 34
SELECT Script, AppliedAt FROM qa.SchemaVersions;   -- 08_upgrade_master_review_fixes
```
6. API database login (least privilege; the API never needs DDL rights):
```sql
-- QualityMasterDB
CREATE USER [qr_api] FOR LOGIN [qr_api];
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::qa TO [qr_api];
-- GDC_AndonDB and MachineShopDB
CREATE USER [qr_api] FOR LOGIN [qr_api];
GRANT SELECT, INSERT ON SCHEMA::qa TO [qr_api];
```
7. Start the API with the `Bootstrap:*` settings to create the first administrator, then remove them ([BUILD_AND_DEPLOY.md](BUILD_AND_DEPLOY.md)).

This exact order is what `tools/create-test-databases.sh` and the integration-test fixture run (on SQL Server 2022), followed by a second run of every idempotent script.

## 2. Upgrade of an existing installation

Plan a short maintenance window. **Every user must sign in again once** after the upgrade from the supplied version, because tokens issued before it carry no token version.

1. Back up all three databases (section 5).
2. Stop the API (application pool / service).
3. If the master still has the old two-shop layout: `06_migration_four_shops.sql`, then `03_seed_reference.sql`. **Never 05.**
4. If an entries table has no `Quantity` column: `07_migration_add_quantity.sql` in that database. Old rows get quantity **1**. That is an assumption, not a recovered fact: confirm it with the business, or correct the rows afterwards.
5. `08_upgrade_master_review_fixes.sql` in QualityMasterDB.
   * If it stops with **error 50080**, two users share a username/employee code (possible with the old schema). It lists them. Rename one account of each pair in `qa.Users`, then run 08 again. The column additions from the first run are kept; that is harmless.
6. `09_upgrade_entries_indexes.sql` in **each** entry database.
6b. `11_upgrade_master_shifts_corrections.sql` in QualityMasterDB, then `12_upgrade_entries_shift_reversal.sql` in **each** entry database. 12 fills `ProductionDate`/`ShiftCode` for existing entries from their server time, using IST (UTC+05:30) and the A/B/C start times written at the top of the script. If the plant's shifts differ, edit those values **and** `qa.ShiftDefinitions` to match before running it. In QualityMasterDB the script refuses to run if they differ (error 50013), and it prints the parameters and row count it uses. On large tables it works in batches of 5,000 rows.
7b. **After the new API is running, run 12 once more in each entry database.** It fills only rows that are still empty, i.e. any saved by the old API during the switch. Reports count only rows with a production day.
7. Deploy the new API, start it, check `GET /api/health/ready` returns 200, then sign in.
8. `04_seed_phenomena.sql`: re-run **only** if the workbook was intentionally changed. It inserts missing (stage, name) pairs and never renames or deletes. Phenomena maintained by administrators stay as they are.

`tools/test-upgrade-path.sh` repeats this upgrade on a scratch database: old schema, old users including a collision, an entries table without `Quantity`.

## 3. Loading the approved machine list

1. Get the approved list: shop code (GDC/CNC/PRE/FINAL), machine code (max 30; letters, digits, space `. _ / -`), machine name (max 100), active 1/0.
2. Copy `db/10_load_production_machines.sql`, replace the two `EXAMPLE` rows with the list, keep `@DryRun = 1` and run it. It validates the **whole** list first and changes nothing if any row has an unknown shop, a bad or duplicate code, a `DEV-` code, or a leftover EXAMPLE row. The problems are listed by row.
3. Read the dry-run output (what would be inserted or updated), then set `@DryRun = 0` and run again. Set `@DeactivatePlaceholders = 1` if the `DEV-*` machines from script 05 exist and should be switched off.
4. Re-running the same list changes nothing. Changing a name or the active flag updates the machine. Every change is written to `qa.AuditLog`.

Administrators can also add or edit machines in the app (Admin → Machines) instead of using the script.

## 4. Recovery from a failed deployment

| Situation | Action |
|---|---|
| A script stopped with an error | Scripts use `SET XACT_ABORT ON`; data-changing batches roll back as a whole. Read the message, fix the cause, run the **same** script again (idempotent). Do not continue with the next script. |
| 08 error 50080 | Section 2, step 5. |
| 08 error 50090 ("At least one active administrator must remain") | A statement tried to remove the last active administrator. Reactivate or create an administrator first. |
| Seed check 50001 | Some seeded codes are missing (03 not run, or rows were renamed/deleted by hand). Compare with `db/seed/phenomena_seed.json`. |
| New API misbehaves after the upgrade | Stop it. Preferred rollback: restore the backups (section 5) and redeploy the old API. |
| Must run the **old** API on the upgraded database (no restore) | The 08/09 changes are additive (columns, a table, a trigger, indexes). One part breaks the old API: EF Core writes with an `OUTPUT` clause, which SQL Server refuses on a table with a trigger, so **every** user save fails while `qa.TR_Users_KeepActiveAdministrator` exists (this was seen in testing before the new API declared the trigger). Run `DROP TRIGGER qa.TR_Users_KeepActiveAdministrator;` first. Users created by the old API get no `qa.LoginIdentifiers` rows. Before returning to the new API, re-run 08: it recreates the trigger and backfills the identifiers. |

## 4b. Changing shift times later

Edit `qa.ShiftDefinitions` (code, name, start time in plant local time, sort order; the first shift's start is the production-day start). The API picks the change up within 5 minutes. Entries already saved keep the production day and shift they were saved with; historical reports do not move. Set `Plant:TimeZoneId` (default `Asia/Kolkata`) if the plant is elsewhere.

## 5. Backup, restore and retention

* **Before every deployment:** a full backup of QualityMasterDB, GDC_AndonDB and MachineShopDB, e.g. `BACKUP DATABASE QualityMasterDB TO DISK = N'…\QualityMasterDB_YYYYMMDD.bak' WITH COPY_ONLY, CHECKSUM;`. GDC_AndonDB and MachineShopDB belong to other systems: coordinate with their owners and use COPY_ONLY so their backup chains are unaffected.
* **Restore:** `RESTORE DATABASE … FROM DISK = … WITH REPLACE`, with the API stopped. Restore the master and the entry databases **to the same point in time**; otherwise entries can reference masters that the restored master no longer has. (The entries keep their own name snapshots, so history still displays.)
* **Routine schedule and retention:** not defined in the supplied documents. This is a business decision ([BUSINESS_DECISIONS.md](BUSINESS_DECISIONS.md)). Suggested minimum: daily full backups and log backups (FULL recovery model) of QualityMasterDB, and the entry databases on their owners' existing schedule.
* Test a restore at least once before go-live.
