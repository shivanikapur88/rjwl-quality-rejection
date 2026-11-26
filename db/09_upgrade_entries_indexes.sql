/* ============================================================================
   09_upgrade_entries_indexes.sql  -  EVERY entry database
   (GDC_AndonDB, MachineShopDB, and the database holding PRE/FINAL entries - QualityMasterDB)

   Run AFTER 02a / 02b / 02c (and 07 if you are upgrading). Idempotent.
   Creates only an index on the Quality table qa.QualityRejectionEntries; it does not
   touch any other table in GDC_AndonDB or MachineShopDB.

   IX_QRE_Machine: the "is this machine used?" check before a machine is deleted
   previously scanned the whole table.
   ============================================================================ */
SET NOCOUNT ON;
GO
IF OBJECT_ID(N'qa.QualityRejectionEntries', N'U') IS NULL
    THROW 50011, N'qa.QualityRejectionEntries does not exist in this database. Run 02a/02b/02c first.', 1;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QRE_Machine' AND object_id = OBJECT_ID(N'qa.QualityRejectionEntries'))
    CREATE INDEX IX_QRE_Machine ON qa.QualityRejectionEntries (MachineId);
GO
-- Supports stable history paging: ORDER BY ServerCreatedAt DESC, Id DESC within a shop.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QRE_Shop_Time_Id' AND object_id = OBJECT_ID(N'qa.QualityRejectionEntries'))
    CREATE INDEX IX_QRE_Shop_Time_Id ON qa.QualityRejectionEntries (ShopId, ServerCreatedAt DESC, Id DESC);
GO
PRINT N'09_upgrade_entries_indexes.sql complete.';
