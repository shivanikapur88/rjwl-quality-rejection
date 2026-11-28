/* ============================================================================
   05_DEV_ONLY_placeholder_machines.sql
   *** DEVELOPMENT PLACEHOLDERS - NOT REAL MACHINES. DO NOT RUN IN PRODUCTION. ***
   The Excel file contains no machine list and none was provided, so these rows exist
   only so the workflow can be exercised. Replace with your real machine master
   (code + name per shop) before go-live, or deactivate these (IsActive = 0).
   ============================================================================ */
SET QUOTED_IDENTIFIER ON;   -- sqlcmd starts with it OFF; filtered indexes on these tables need it for any write
SET ANSI_NULLS ON;
SET NOCOUNT ON;
INSERT INTO qa.Machines (MachineCode, MachineName, ShopId)
SELECT v.Code, v.Name, s.Id
FROM (VALUES
    (N'GDC',   N'DEV-GDC-01',   N'PLACEHOLDER GDC machine 1'),   (N'GDC',   N'DEV-GDC-02',   N'PLACEHOLDER GDC machine 2'),
    (N'CNC',   N'DEV-CNC-01',   N'PLACEHOLDER CNC machine 1'),   (N'CNC',   N'DEV-CNC-02',   N'PLACEHOLDER CNC machine 2'),
    (N'PRE',   N'DEV-PRE-01',   N'PLACEHOLDER Pre-treatment station 1'), (N'PRE',   N'DEV-PRE-02',   N'PLACEHOLDER Pre-treatment station 2'),
    (N'FINAL', N'DEV-FINAL-01', N'PLACEHOLDER FINAL station 1'), (N'FINAL', N'DEV-FINAL-02', N'PLACEHOLDER FINAL station 2')) v(Shop, Code, Name)
JOIN qa.Shops s ON s.ShopCode = v.Shop
WHERE NOT EXISTS (SELECT 1 FROM qa.Machines m WHERE m.ShopId = s.Id AND m.MachineCode = v.Code);
PRINT N'DEV placeholder machines inserted.';
