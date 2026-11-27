/* ============================================================================
   03_seed_reference.sql - roles, the FOUR independent shops, their stages and mapping.
   Run in the master database after 01_master_schema.sql. Idempotent.
  Executed against SQL Server 2022 by the automated tests (docs/TEST_EVIDENCE.md).

   Four independent shops, each with exactly one inspection stage of the same name:
     GDC   -> GDC Shop                 (entries: GDC_AndonDB)
     CNC   -> CNC Shop                 (entries: MachineShopDB)
     PRE   -> Pre-treatment Inspection (entries: database of your choice - see 02c)
     FINAL -> FINAL Inspection         (entries: database of your choice - see 02c)
   No users are seeded here: password hashes must come from the backend (see Quality.UserSqlGen).
   ============================================================================ */
SET QUOTED_IDENTIFIER ON;   -- sqlcmd starts with it OFF; filtered indexes on these tables need it for any write
SET ANSI_NULLS ON;
SET NOCOUNT ON; SET XACT_ABORT ON;
BEGIN TRANSACTION;

INSERT INTO qa.Roles (Id, RoleName)
SELECT v.Id, v.RoleName FROM (VALUES (1, N'Administrator'), (2, N'QualityInspector'), (3, N'Supervisor')) v(Id, RoleName)
WHERE NOT EXISTS (SELECT 1 FROM qa.Roles r WHERE r.Id = v.Id);

-- EntryStoreKey is resolved to a connection string in server configuration
-- (ConnectionStrings:GdcEntries / CncEntries / PreEntries / FinalEntries).
INSERT INTO qa.Shops (ShopCode, ShopName, EntryStoreKey)
SELECT v.ShopCode, v.ShopName, v.StoreKey FROM (VALUES
    (N'GDC',   N'GDC Shop',                 N'GDC'),
    (N'CNC',   N'CNC Shop',                 N'CNC'),
    (N'PRE',   N'Pre-treatment Inspection', N'PRE'),
    (N'FINAL', N'FINAL Inspection',         N'FINAL')) v(ShopCode, ShopName, StoreKey)
WHERE NOT EXISTS (SELECT 1 FROM qa.Shops s WHERE s.ShopCode = v.ShopCode);

INSERT INTO qa.InspectionStages (StageCode, StageName, SortOrder)
SELECT v.StageCode, v.StageName, v.SortOrder FROM (VALUES
    (N'GDC_SHOP',      N'GDC Shop',                1),
    (N'CNC_SHOP',      N'CNC Shop',                2),
    (N'PRE_TREATMENT', N'Pre-treatment Inspection',3),
    (N'FINAL',         N'FINAL Inspection',        4)) v(StageCode, StageName, SortOrder)
WHERE NOT EXISTS (SELECT 1 FROM qa.InspectionStages s WHERE s.StageCode = v.StageCode);

-- One-to-one: each shop has exactly its own stage
INSERT INTO qa.ShopStages (ShopId, InspectionStageId)
SELECT sh.Id, st.Id
FROM (VALUES (N'GDC', N'GDC_SHOP'), (N'CNC', N'CNC_SHOP'), (N'PRE', N'PRE_TREATMENT'), (N'FINAL', N'FINAL')) m(ShopCode, StageCode)
JOIN qa.Shops sh ON sh.ShopCode = m.ShopCode
JOIN qa.InspectionStages st ON st.StageCode = m.StageCode
WHERE NOT EXISTS (SELECT 1 FROM qa.ShopStages x WHERE x.ShopId = sh.Id AND x.InspectionStageId = st.Id);

-- The four required rows must exist; more (a shop or stage added later) is allowed, so a re-run stays harmless.
DECLARE @stageCount int, @shopCount int, @mapCount int;
SELECT @stageCount = COUNT(*) FROM qa.InspectionStages WHERE StageCode IN (N'GDC_SHOP', N'CNC_SHOP', N'PRE_TREATMENT', N'FINAL');
SELECT @shopCount  = COUNT(*) FROM qa.Shops WHERE ShopCode IN (N'GDC', N'CNC', N'PRE', N'FINAL');
SELECT @mapCount   = COUNT(*) FROM qa.ShopStages x JOIN qa.Shops sh ON sh.Id = x.ShopId JOIN qa.InspectionStages st ON st.Id = x.InspectionStageId
WHERE (sh.ShopCode = N'GDC' AND st.StageCode = N'GDC_SHOP') OR (sh.ShopCode = N'CNC' AND st.StageCode = N'CNC_SHOP')
   OR (sh.ShopCode = N'PRE' AND st.StageCode = N'PRE_TREATMENT') OR (sh.ShopCode = N'FINAL' AND st.StageCode = N'FINAL');
IF @stageCount <> 4 THROW 50002, N'The 4 inspection stages are not all present.', 1;
IF @shopCount  <> 4 THROW 50003, N'The 4 shops are not all present. If you ran an older version of this script, run 06_migration_four_shops.sql first.', 1;
IF @mapCount   <> 4 THROW 50004, N'The 4 shop-stage mappings (one per shop) are not all present. Run 06_migration_four_shops.sql.', 1;
-- The old two-shop layout mapped PRE/FINAL stages to GDC/CNC: it must have been migrated by 06.
IF EXISTS (SELECT 1 FROM qa.ShopStages x JOIN qa.Shops sh ON sh.Id = x.ShopId JOIN qa.InspectionStages st ON st.Id = x.InspectionStageId
           WHERE sh.ShopCode IN (N'GDC', N'CNC') AND st.StageCode IN (N'PRE_TREATMENT', N'FINAL'))
    THROW 50004, N'GDC/CNC still have PRE/FINAL stage mappings from the old layout. Run 06_migration_four_shops.sql.', 1;
COMMIT TRANSACTION;
PRINT N'03_seed_reference.sql complete.';
