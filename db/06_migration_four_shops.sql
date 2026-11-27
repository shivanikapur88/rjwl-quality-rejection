/* ============================================================================
   06_migration_four_shops.sql  -  ONLY if you already ran the EARLIER version of 03_seed_reference.sql
   (two shops, with Pre-treatment and FINAL nested inside both). Skip this on a fresh setup.

   Converts the master data to four independent shops with one stage each. Run in QualityMasterDB,
   then re-run the new 03_seed_reference.sql. Do NOT run 05_DEV_ONLY_placeholder_machines.sql on a production
   database (it adds fake machines); load the real list with 10_load_production_machines.sql instead.
   Corrected 2026-11-26: earlier text said to re-run 03 AND 05. See docs/DATABASE_RUNBOOK.md.
   Existing entries in GDC_AndonDB / MachineShopDB are NOT touched (their ShopId values stay valid).
   Review before running. NOT covered by the automated tests (it needs an old two-shop database): test on a copy first.
   ============================================================================ */
SET QUOTED_IDENTIFIER ON;   -- sqlcmd starts with it OFF; filtered indexes on these tables need it for any write
SET ANSI_NULLS ON;
SET NOCOUNT ON; SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- 1. add the two new shops
INSERT INTO qa.Shops (ShopCode, ShopName, EntryStoreKey)
SELECT v.ShopCode, v.ShopName, v.StoreKey FROM (VALUES
    (N'PRE',   N'Pre-treatment Inspection', N'PRE'),
    (N'FINAL', N'FINAL Inspection',         N'FINAL')) v(ShopCode, ShopName, StoreKey)
WHERE NOT EXISTS (SELECT 1 FROM qa.Shops s WHERE s.ShopCode = v.ShopCode);

-- 2. remove the old nested mappings (GDC/CNC -> Pre-treatment/FINAL)
DELETE ss FROM qa.ShopStages ss
JOIN qa.Shops sh ON sh.Id = ss.ShopId
JOIN qa.InspectionStages st ON st.Id = ss.InspectionStageId
WHERE st.StageCode IN (N'PRE_TREATMENT', N'FINAL') AND sh.ShopCode IN (N'GDC', N'CNC');

-- 3. users assigned to GDC or CNC keep their assignment; assign PRE/FINAL explicitly where wanted, e.g.:
--    INSERT INTO qa.UserShops (UserId, ShopId) SELECT u.Id, s.Id FROM qa.Users u JOIN qa.Shops s ON s.ShopCode IN (N'PRE', N'FINAL') WHERE u.EmployeeCode = N'E1001';

COMMIT TRANSACTION;
PRINT N'Migration done. Now run 03_seed_reference.sql (adds the new 1:1 mappings). Production: load real machines with 10, never 05.';
