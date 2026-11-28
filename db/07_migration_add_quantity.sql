/* ============================================================================
   07_migration_add_quantity.sql  -  ONLY if you ALREADY created qa.QualityRejectionEntries with an earlier
   version of 02a / 02b / 02c (without the Quantity column). Skip on a fresh setup: the new 02a/02b/02c already include it.

   Run once in EACH entry database that already has the table (GDC_AndonDB, MachineShopDB, and the Pre-treatment/FINAL database).
   Existing rows get Quantity = 1 (the earlier version did not record a quantity; 1 is an ASSUMPTION - correct it if you know better).
   Review before running.Executed against SQL Server 2022 by the automated tests (docs/TEST_EVIDENCE.md).
   ============================================================================ */
SET QUOTED_IDENTIFIER ON;   -- sqlcmd starts with it OFF; filtered indexes on these tables need it for any write
SET ANSI_NULLS ON;
SET NOCOUNT ON;
GO
IF OBJECT_ID(N'qa.QualityRejectionEntries', N'U') IS NULL
BEGIN
    THROW 50010, N'qa.QualityRejectionEntries does not exist in this database. Run 02a/02b/02c instead.', 1;
END
GO
IF COL_LENGTH(N'qa.QualityRejectionEntries', N'Quantity') IS NULL
    ALTER TABLE qa.QualityRejectionEntries ADD Quantity int NOT NULL CONSTRAINT DF_QRE_Quantity DEFAULT (1);
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_QRE_Quantity' AND parent_object_id = OBJECT_ID(N'qa.QualityRejectionEntries'))
    ALTER TABLE qa.QualityRejectionEntries ADD CONSTRAINT CK_QRE_Quantity CHECK (Quantity BETWEEN 1 AND 9999);
GO
PRINT N'07_migration_add_quantity.sql complete.';
