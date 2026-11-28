/* ============================================================================
   12_upgrade_entries_shift_reversal.sql  -  EVERY entry database
   (GDC_AndonDB, MachineShopDB, and QualityMasterDB for PRE/FINAL). Run AFTER 09 (and, in QualityMasterDB, after 11). Idempotent.
   Touches only the Quality table qa.QualityRejectionEntries.

     ProductionDate / ShiftCode   production day and shift the entry belongs to (set by the API on save)
     ReversedAt / ReversedByCorrectionId
                                  set ONCE when a supervisor approves a correction; reversed entries stay
                                  visible in history and are excluded from totals. No other column is ever changed.
     CorrectsEntryId              on a replacement entry: the entry it corrects

   BACKFILL of ProductionDate/ShiftCode for existing rows uses the plant offset and shift start times below.
   They must match qa.ShiftDefinitions and Plant:TimeZoneId (India Standard Time = UTC+05:30, no daylight saving).
   ============================================================================ */
-- STRING_AGG needs SQL Server 2017 (version 14) or later, on any database compatibility level; the database's level is
-- left alone (GDC_AndonDB and MachineShopDB belong to other systems). (Fable review: an earlier compatibility-level check
-- here was wrong and could block the upgrade of those databases.)
IF CAST(SERVERPROPERTY('ProductMajorVersion') AS int) < 14
    THROW 50015, N'SQL Server 2017 or later is required.', 1;
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
-- In the master (qa.Users exists) the shift table MUST exist: run 11 first. A missing table or no active shifts must not
-- silently skip the check (third full review).
IF OBJECT_ID(N'qa.Users', N'U') IS NOT NULL AND OBJECT_ID(N'qa.ShiftDefinitions', N'U') IS NULL
    THROW 50014, N'qa.ShiftDefinitions does not exist in this master database. Run 11_upgrade_master_shifts_corrections.sql first.', 1;
IF OBJECT_ID(N'qa.ShiftDefinitions', N'U') IS NOT NULL AND EXISTS (SELECT 1 FROM qa.QualityRejectionEntries WHERE ProductionDate IS NULL)
    EXEC sp_executesql N'
        IF ISNULL((SELECT STRING_AGG(ShiftCode + ''='' + CONVERT(varchar(5), StartTime, 108), '','') WITHIN GROUP (ORDER BY SortOrder) FROM qa.ShiftDefinitions WHERE IsActive = 1), '''')
           <> CONCAT(''A='', CONVERT(varchar(5), @A, 108), '',B='', CONVERT(varchar(5), @B, 108), '',C='', CONVERT(varchar(5), @C, 108))
            THROW 50013, N''The backfill shift times in this script differ from qa.ShiftDefinitions. Edit @A/@B/@C (and the codes) to match, then run again.'', 1;',
        N'@A time(0), @B time(0), @C time(0)', @A = @A, @B = @B, @C = @C;
DECLARE @msg nvarchar(200) = CONCAT(N'Backfill parameters: UTC offset ', @OffsetMinutes, N' min; A ', CONVERT(varchar(5), @A, 108), N', B ', CONVERT(varchar(5), @B, 108), N', C ', CONVERT(varchar(5), @C, 108),
    N'; rows to fill: ', (SELECT COUNT(*) FROM qa.QualityRejectionEntries WHERE ProductionDate IS NULL));
PRINT @msg;
-- Walk the clustered key in ranges of 1,500 ids: each batch seeks instead of rescanning the filled rows, and stays below
-- the 5,000-lock escalation threshold even when the two production-day indexes are updated too (a re-run after the
-- indexes exist), so qa.QualityRejectionEntries is not locked table-wide.
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
    WHERE e.Id >= @lo AND e.Id < @lo + 1500 AND e.ProductionDate IS NULL;
    SET @lo += 1500;
END;
GO

-- Covers the report aggregate (which also reads MachineName). An index from an earlier version without it is rebuilt.
IF EXISTS (SELECT 1 FROM sys.indexes i WHERE i.name = N'IX_QRE_Shop_ProductionDate' AND i.object_id = OBJECT_ID(N'qa.QualityRejectionEntries'))
   AND NOT EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                   WHERE i.name = N'IX_QRE_Shop_ProductionDate' AND i.object_id = OBJECT_ID(N'qa.QualityRejectionEntries')
                     AND COL_NAME(ic.object_id, ic.column_id) = N'MachineName')
    DROP INDEX IX_QRE_Shop_ProductionDate ON qa.QualityRejectionEntries;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QRE_Shop_ProductionDate' AND object_id = OBJECT_ID(N'qa.QualityRejectionEntries'))
    CREATE INDEX IX_QRE_Shop_ProductionDate ON qa.QualityRejectionEntries (ShopId, ProductionDate, ShiftCode)
        INCLUDE (Quantity, MachineId, MachineCode, MachineName, RejectionPhenomenonId, PhenomenonNameSnapshot, ReversedAt);
GO
-- Rows still without a production day (normally none): lets the readiness check find them without a table scan.
-- NO filtered indexes in the entry databases (Fable review): GDC_AndonDB and MachineShopDB belong to other systems, and once a
-- table has a filtered index every write or index rebuild from a session with QUOTED_IDENTIFIER OFF (e.g. a SQL Agent
-- maintenance job) fails with error 1934. A plain index on ProductionDate lets the readiness check find rows without one.
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QRE_NoProductionDate' AND object_id = OBJECT_ID(N'qa.QualityRejectionEntries'))
    DROP INDEX IX_QRE_NoProductionDate ON qa.QualityRejectionEntries;   -- filtered version from an earlier draft
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QRE_ProductionDate' AND object_id = OBJECT_ID(N'qa.QualityRejectionEntries'))
    CREATE INDEX IX_QRE_ProductionDate ON qa.QualityRejectionEntries (ProductionDate);
GO
-- Production day and shift are set together (the API and the backfill always set both).
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_QRE_ProductionDay_Shift' AND parent_object_id = OBJECT_ID(N'qa.QualityRejectionEntries'))
    ALTER TABLE qa.QualityRejectionEntries WITH CHECK ADD CONSTRAINT CK_QRE_ProductionDay_Shift
        CHECK ((ProductionDate IS NULL AND ShiftCode IS NULL) OR (ProductionDate IS NOT NULL AND ShiftCode IS NOT NULL));
-- A replacement points to an existing entry of the same table.
-- Existing rows that point to a missing entry are kept (constraint added WITH NOCHECK and reported), not a failed upgrade.
IF OBJECT_ID(N'qa.FK_QRE_CorrectsEntry', N'F') IS NULL
BEGIN
    IF EXISTS (SELECT 1 FROM qa.QualityRejectionEntries r WHERE r.CorrectsEntryId IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM qa.QualityRejectionEntries o WHERE o.Id = r.CorrectsEntryId))
    BEGIN
        PRINT N'WARNING: some replacement entries refer to an entry that does not exist; the foreign key is added without checking them.';
        ALTER TABLE qa.QualityRejectionEntries WITH NOCHECK ADD CONSTRAINT FK_QRE_CorrectsEntry FOREIGN KEY (CorrectsEntryId) REFERENCES qa.QualityRejectionEntries(Id);
    END
    ELSE
        ALTER TABLE qa.QualityRejectionEntries WITH CHECK ADD CONSTRAINT FK_QRE_CorrectsEntry FOREIGN KEY (CorrectsEntryId) REFERENCES qa.QualityRejectionEntries(Id);
END;
GO
-- No unique filtered index on CorrectsEntryId (see above): one replacement per correction is guaranteed by the unique
-- SubmissionId (the replacement's id is fixed per correction request) and one approved correction per entry by the
-- filtered unique index on qa.EntryCorrections in the master. An index from an earlier draft is removed.
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_QRE_CorrectsEntryId' AND object_id = OBJECT_ID(N'qa.QualityRejectionEntries'))
    DROP INDEX UX_QRE_CorrectsEntryId ON qa.QualityRejectionEntries;
GO
PRINT N'12_upgrade_entries_shift_reversal.sql complete.';
