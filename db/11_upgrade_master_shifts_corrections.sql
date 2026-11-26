/* ============================================================================
   11_upgrade_master_shifts_corrections.sql  -  master database (QualityMasterDB)

   Run AFTER 08, on a fresh install and when upgrading. Idempotent.
     1. qa.ShiftDefinitions   - plant shifts (local plant time). The production day starts at the start of the
                                shift with the lowest SortOrder. Seeded: A 06:00-14:00, B 14:00-22:00, C 22:00-06:00.
                                Edit the rows (not this script) to change shift times; the API reads them on start-up
                                and every 5 minutes. Existing entries keep the shift they were saved with.
     2. qa.EntryCorrections   - correction requests (inspector requests, supervisor/administrator decides).
                                Entries are never edited: an approved request REVERSES the entry (marker in the entry
                                database) and optionally adds a corrected replacement entry.
   ============================================================================ */
SET NOCOUNT ON;
SET XACT_ABORT ON;
-- Required for filtered indexes and consistent behaviour; sqlcmd defaults QUOTED_IDENTIFIER to OFF.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'qa.ShiftDefinitions', N'U') IS NULL
CREATE TABLE qa.ShiftDefinitions (
    Id         int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ShiftDefinitions PRIMARY KEY,
    ShiftCode  nvarchar(10)  NOT NULL CONSTRAINT UQ_ShiftDefinitions_Code UNIQUE,
    ShiftName  nvarchar(50)  NOT NULL,
    StartTime  time(0)       NOT NULL CONSTRAINT UQ_ShiftDefinitions_Start UNIQUE,   -- plant local time; a shift ends where the next one starts
    SortOrder  tinyint       NOT NULL CONSTRAINT UQ_ShiftDefinitions_Sort UNIQUE,
    IsActive   bit           NOT NULL CONSTRAINT DF_ShiftDefinitions_IsActive DEFAULT (1)
);
GO
INSERT INTO qa.ShiftDefinitions (ShiftCode, ShiftName, StartTime, SortOrder)
SELECT v.Code, v.Name, v.StartTime, v.SortOrder
FROM (VALUES (N'A', N'Shift A', CAST('06:00' AS time(0)), 1),
             (N'B', N'Shift B', CAST('14:00' AS time(0)), 2),
             (N'C', N'Shift C', CAST('22:00' AS time(0)), 3)) v(Code, Name, StartTime, SortOrder)
WHERE NOT EXISTS (SELECT 1 FROM qa.ShiftDefinitions);   -- seed only an empty table; never overwrite edited shifts
GO

IF OBJECT_ID(N'qa.EntryCorrections', N'U') IS NULL
CREATE TABLE qa.EntryCorrections (
    Id                    bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_EntryCorrections PRIMARY KEY,
    ShopId                int            NOT NULL CONSTRAINT FK_EntryCorrections_Shops REFERENCES qa.Shops(Id),
    EntryId               bigint         NOT NULL,           -- id in the shop's entry database (not unique across shops)
    EntrySubmissionId     uniqueidentifier NOT NULL,
    RequestedBy           int            NOT NULL CONSTRAINT FK_EntryCorrections_RequestedBy REFERENCES qa.Users(Id),
    RequestedAt           datetime2(3)   NOT NULL,
    Reason                nvarchar(500)  NOT NULL,
    -- NULL proposal = reverse only (the entry should not exist). Any proposal = reverse and replace.
    ProposedMachineId     int            NULL,
    ProposedPhenomenonId  int            NULL,
    ProposedQuantity      int            NULL CONSTRAINT CK_EntryCorrections_Qty CHECK (ProposedQuantity IS NULL OR ProposedQuantity BETWEEN 1 AND 9999),
    ProposedRemarks       nvarchar(500)  NULL,
    Status                nvarchar(10)   NOT NULL CONSTRAINT CK_EntryCorrections_Status CHECK (Status IN (N'Pending', N'Approved', N'Rejected', N'Withdrawn')),
    DecidedBy             int            NULL CONSTRAINT FK_EntryCorrections_DecidedBy REFERENCES qa.Users(Id),
    DecidedAt             datetime2(3)   NULL,
    DecisionNote          nvarchar(500)  NULL,
    ReplacementEntryId    bigint         NULL,
    RowVer                rowversion     NOT NULL,
    CONSTRAINT CK_EntryCorrections_Reason CHECK (LEN(LTRIM(RTRIM(Reason))) > 0),
    CONSTRAINT CK_EntryCorrections_Decision CHECK ((Status = N'Pending' AND DecidedBy IS NULL AND DecidedAt IS NULL)
                                                OR (Status <> N'Pending' AND DecidedAt IS NOT NULL))
);
GO
-- SubmissionId of the replacement entry: random, fixed when the request is made, never exposed to clients
-- (a predictable value could be pre-claimed by an ordinary entry; found in the independent review).
IF COL_LENGTH(N'qa.EntryCorrections', N'ReplacementSubmissionId') IS NULL
    ALTER TABLE qa.EntryCorrections ADD ReplacementSubmissionId uniqueidentifier NOT NULL CONSTRAINT DF_EntryCorrections_ReplacementSubmissionId DEFAULT (NEWID());
GO
-- At most one OPEN request and at most one APPROVED correction per entry.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_EntryCorrections_OnePending' AND object_id = OBJECT_ID(N'qa.EntryCorrections'))
    CREATE UNIQUE INDEX UX_EntryCorrections_OnePending ON qa.EntryCorrections (ShopId, EntryId) WHERE Status = N'Pending';
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_EntryCorrections_OneApproved' AND object_id = OBJECT_ID(N'qa.EntryCorrections'))
    CREATE UNIQUE INDEX UX_EntryCorrections_OneApproved ON qa.EntryCorrections (ShopId, EntryId) WHERE Status = N'Approved';
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EntryCorrections_Shop_Status' AND object_id = OBJECT_ID(N'qa.EntryCorrections'))
    CREATE INDEX IX_EntryCorrections_Shop_Status ON qa.EntryCorrections (ShopId, Status, RequestedAt DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EntryCorrections_RequestedBy' AND object_id = OBJECT_ID(N'qa.EntryCorrections'))
    CREATE INDEX IX_EntryCorrections_RequestedBy ON qa.EntryCorrections (RequestedBy, RequestedAt DESC);
GO

IF NOT EXISTS (SELECT 1 FROM qa.SchemaVersions WHERE Script = N'11_upgrade_master_shifts_corrections')
    INSERT INTO qa.SchemaVersions (Script) VALUES (N'11_upgrade_master_shifts_corrections');
GO
PRINT N'11_upgrade_master_shifts_corrections.sql complete.';
