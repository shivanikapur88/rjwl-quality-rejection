/* NOTE (third full review): when you fill this in, process machines in ascending MachineId order (e.g. stage the rows
   in a table and MERGE ORDER BY MachineId), the same order the API's upload locks them in, and retry on error 1205.
*/
/* ============================================================================
   14_production_counts_feed_TEMPLATE.sql  -  TEMPLATE, NOT RUN BY ANY INSTALL STEP

   An automatic feed of production counts into qa.ProductionCounts (QualityMasterDB) from the plant's production
   systems (CNC production collector / ShopMonitor). The source table and column names below are PLACEHOLDERS:
   they were not available when this was written. Fill them in with the owners of those systems.

   Rules:
   * READ-ONLY on the source: this only SELECTs from the production databases; it never changes them.
   * The feed upserts by (MachineId, ProductionDate, ShiftCode); machine codes must match qa.Machines for the shop.
   * Production day and shift must follow qa.ShiftDefinitions (A 06:00, B 14:00, C 22:00 IST by default). If the source
     counts per hour or per its own shift (e.g. the CNC 05:59 reset), aggregate to these shifts first.
   * Run it as a SQL Agent job (e.g. every 15 minutes) under a login with SELECT on the source and
     SELECT/INSERT/UPDATE on qa.ProductionCounts only.
   ============================================================================ */
SET NOCOUNT ON; SET XACT_ABORT ON; SET QUOTED_IDENTIFIER ON;
/*
DECLARE @From date = DATEADD(DAY, -2, CAST(SYSUTCDATETIME() AS date));

MERGE qa.ProductionCounts WITH (HOLDLOCK) AS t
USING (
    SELECT m.ShopId, m.Id AS MachineId, src.ProductionDate, src.ShiftCode, SUM(src.GoodPlusRejected) AS Produced
    FROM [<SOURCE_DATABASE>].[<schema>].[<production_table>] AS src          -- PLACEHOLDER
    JOIN qa.Machines m ON m.MachineCode = src.<machine_code_column>           -- PLACEHOLDER
    JOIN qa.Shops s ON s.Id = m.ShopId AND s.ShopCode = N'CNC'                -- one MERGE per source system/shop
    WHERE src.ProductionDate >= @From
    GROUP BY m.ShopId, m.Id, src.ProductionDate, src.ShiftCode
) AS f
ON t.MachineId = f.MachineId AND t.ProductionDate = f.ProductionDate AND t.ShiftCode = f.ShiftCode
WHEN MATCHED AND t.ProducedQuantity <> f.Produced THEN
    UPDATE SET ProducedQuantity = f.Produced, Source = N'feed:CNC', UpdatedAt = SYSUTCDATETIME(), UpdatedBy = NULL
WHEN NOT MATCHED THEN
    INSERT (ShopId, MachineId, ProductionDate, ShiftCode, ProducedQuantity, Source) VALUES (f.ShopId, f.MachineId, f.ProductionDate, f.ShiftCode, f.Produced, N'feed:CNC');
*/
PRINT N'Template only - fill in the source names and remove the comment markers.';
