/* ============================================================================
   12_upgrade_entries_shift_reversal.sql  -  EVERY entry database
   (GDC_AndonDB, MachineShopDB, and QualityMasterDB for PRE/FINAL). Run AFTER 09. Idempotent.
   Touches only the Quality table qa.QualityRejectionEntries.

     ProductionDate / ShiftCode   production day and shift the entry belongs to (set by the API on save)
     ReversedAt / ReversedByCorrectionId
                                  set ONCE when a supervisor approves a correction; reversed entries stay
                                  visible in history and are excluded from totals. No other column is ever changed.
     CorrectsEntryId              on a replacement entry: the entry it corrects

   BACKFILL of ProductionDate/ShiftCode for existing rows uses the plant offset and shift start times below.
   They must match qa.ShiftDefinitions and Plant:TimeZoneId (India Standard Time = UTC+05:30, no daylight saving).
   ============================================================================ */
SET NOCOUNT ON;
SET XACT_ABORT ON;
-- Required for filtered indexes and consistent behaviour; sqlcmd defaults QUOTED_IDENTIFIER to OFF.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO
IF OBJECT_ID(N'qa.QualityRejectionEntries', N'U') IS NULL
    THROW 50012, N'qa.QualityRejectionEntries does not exist in this database. Run 02a/02b/02c first.', 1;
GO
IF COL_LENGTH(N'qa.QualityRejectionEntries', N'ProductionDate') IS NULL
    ALTER TABLE qa.QualityRejectionEntries ADD ProductionDate date NULL;
IF COL_LENGTH(N'qa.QualityRejectionEntries', N'ShiftCode') IS NULL
    ALTER TABLE qa.QualityRejectionEntries ADD ShiftCode nvarchar(10) NULL;
IF COL_LENGTH(N'qa.QualityRejectionEntries', N'ReversedAt') IS NULL
    ALTER TABLE qa.QualityRejectionEntries ADD ReversedAt datetime2(3) NULL;
IF COL_LENGTH(N'qa.QualityRejectionEntries', N'ReversedByCorrectionId') IS NULL
    ALTER TABLE qa.QualityRejectionEntries ADD ReversedByCorrectionId bigint NULL;
IF COL_LENGTH(N'qa.QualityRejectionEntries', N'CorrectsEntryId') IS NULL
    ALTER TABLE qa.QualityRejectionEntries ADD CorrectsEntryId bigint NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_QRE_Reversal' AND parent_object_id = OBJECT_ID(N'qa.QualityRejectionEntries'))
    ALTER TABLE qa.QualityRejectionEntries ADD CONSTRAINT CK_QRE_Reversal
        CHECK ((ReversedAt IS NULL AND ReversedByCorrectionId IS NULL) OR (ReversedAt IS NOT NULL AND ReversedByCorrectionId IS NOT NULL));
GO

/* ---------- backfill existing rows (plant time = UTC + @OffsetMinutes) ---------- */
DECLARE @OffsetMinutes int = 330;                         -- India Standard Time (UTC+05:30)
DECLARE @A time(0) = '06:00', @B time(0) = '14:00', @C time(0) = '22:00';   -- = qa.ShiftDefinitions seed
WHILE 1 = 1
BEGIN
    UPDATE TOP (5000) e
    SET ProductionDate = CAST(DATEADD(MINUTE, -DATEDIFF(MINUTE, CAST('00:00' AS time(0)), @A), l.LocalTime) AS date),
        ShiftCode = CASE WHEN CAST(l.LocalTime AS time(0)) >= @A AND CAST(l.LocalTime AS time(0)) < @B THEN N'A'
                         WHEN CAST(l.LocalTime AS time(0)) >= @B AND CAST(l.LocalTime AS time(0)) < @C THEN N'B'
                         ELSE N'C' END
    FROM qa.QualityRejectionEntries e
    CROSS APPLY (SELECT DATEADD(MINUTE, @OffsetMinutes, e.ServerCreatedAt) AS LocalTime) l
    WHERE e.ProductionDate IS NULL;
    IF @@ROWCOUNT = 0 BREAK;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QRE_Shop_ProductionDate' AND object_id = OBJECT_ID(N'qa.QualityRejectionEntries'))
    CREATE INDEX IX_QRE_Shop_ProductionDate ON qa.QualityRejectionEntries (ShopId, ProductionDate, ShiftCode)
        INCLUDE (Quantity, MachineId, MachineCode, RejectionPhenomenonId, PhenomenonNameSnapshot, ReversedAt);
GO
PRINT N'12_upgrade_entries_shift_reversal.sql complete.';
