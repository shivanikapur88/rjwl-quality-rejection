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
-- Where the master database is this database (PRE/FINAL), refuse to backfill with values that differ from qa.ShiftDefinitions.
-- (Dynamic SQL: the table does not exist in GDC_AndonDB/MachineShopDB, and SQL Server would resolve the name even inside a skipped IF.)
IF OBJECT_ID(N'qa.ShiftDefinitions', N'U') IS NOT NULL AND EXISTS (SELECT 1 FROM qa.QualityRejectionEntries WHERE ProductionDate IS NULL)
    EXEC sp_executesql N'
        IF (SELECT STRING_AGG(ShiftCode + ''='' + CONVERT(varchar(5), StartTime, 108), '','') WITHIN GROUP (ORDER BY SortOrder) FROM qa.ShiftDefinitions WHERE IsActive = 1)
           <> CONCAT(''A='', CONVERT(varchar(5), @A, 108), '',B='', CONVERT(varchar(5), @B, 108), '',C='', CONVERT(varchar(5), @C, 108))
            THROW 50013, N''The backfill shift times in this script differ from qa.ShiftDefinitions. Edit @A/@B/@C (and the codes) to match, then run again.'', 1;',
        N'@A time(0), @B time(0), @C time(0)', @A = @A, @B = @B, @C = @C;
DECLARE @msg nvarchar(200) = CONCAT(N'Backfill parameters: UTC offset ', @OffsetMinutes, N' min; A ', CONVERT(varchar(5), @A, 108), N', B ', CONVERT(varchar(5), @B, 108), N', C ', CONVERT(varchar(5), @C, 108),
    N'; rows to fill: ', (SELECT COUNT(*) FROM qa.QualityRejectionEntries WHERE ProductionDate IS NULL));
PRINT @msg;
-- Walk the clustered key in ranges of 4,000 ids: each batch seeks instead of rescanning the filled rows, and stays below
-- the 5,000-lock escalation threshold, so other systems' tables in GDC_AndonDB/MachineShopDB are not locked table-wide.
-- Times are compared at millisecond precision (time(3)): a cast to time(0) would ROUND 05:59:59.700 up to 06:00:00.
DECLARE @lo bigint = (SELECT MIN(Id) FROM qa.QualityRejectionEntries WHERE ProductionDate IS NULL),
        @hi bigint = (SELECT MAX(Id) FROM qa.QualityRejectionEntries WHERE ProductionDate IS NULL);
DECLARE @A3 time(3) = @A, @B3 time(3) = @B, @C3 time(3) = @C;
WHILE @lo <= @hi
BEGIN
    UPDATE e
    SET ProductionDate = CAST(DATEADD(MINUTE, -DATEDIFF(MINUTE, CAST('00:00' AS time(0)), @A), l.LocalTime) AS date),
        ShiftCode = CASE WHEN CAST(l.LocalTime AS time(3)) >= @A3 AND CAST(l.LocalTime AS time(3)) < @B3 THEN N'A'
                         WHEN CAST(l.LocalTime AS time(3)) >= @B3 AND CAST(l.LocalTime AS time(3)) < @C3 THEN N'B'
                         ELSE N'C' END
    FROM qa.QualityRejectionEntries e
    CROSS APPLY (SELECT DATEADD(MINUTE, @OffsetMinutes, e.ServerCreatedAt) AS LocalTime) l
    WHERE e.Id >= @lo AND e.Id < @lo + 4000 AND e.ProductionDate IS NULL;
    SET @lo += 4000;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QRE_Shop_ProductionDate' AND object_id = OBJECT_ID(N'qa.QualityRejectionEntries'))
    CREATE INDEX IX_QRE_Shop_ProductionDate ON qa.QualityRejectionEntries (ShopId, ProductionDate, ShiftCode)
        INCLUDE (Quantity, MachineId, MachineCode, RejectionPhenomenonId, PhenomenonNameSnapshot, ReversedAt);
GO
-- Rows still without a production day (normally none): lets the readiness check find them without a table scan.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QRE_NoProductionDate' AND object_id = OBJECT_ID(N'qa.QualityRejectionEntries'))
    CREATE INDEX IX_QRE_NoProductionDate ON qa.QualityRejectionEntries (Id) WHERE ProductionDate IS NULL;
GO
-- Backstop for the cross-database approval: at most one replacement entry per corrected entry.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QRE_CorrectsEntryId' AND object_id = OBJECT_ID(N'qa.QualityRejectionEntries'))
    CREATE UNIQUE INDEX UX_QRE_CorrectsEntryId ON qa.QualityRejectionEntries (CorrectsEntryId) WHERE CorrectsEntryId IS NOT NULL;
GO
PRINT N'12_upgrade_entries_shift_reversal.sql complete.';
