/* ============================================================================
   02b_entries_schema_MachineShopDB.sql  -  Rejection ENTRIES table for CNC Shop (target DB: MachineShopDB)

   REVIEW BEFORE RUNNING. Connect to the EXISTING MachineShopDB database, then run.
   * Does not create a database. Creates only schema [qa] and ONE new table
     ([qa].[QualityRejectionEntries]) - no existing object is altered or read.
     Confirm [qa] and that table name do not clash with existing objects first.
   * A CHECK constraint pins every row to ShopCode = 'CNC', so an entry for
     the wrong shop cannot be stored in this database even if the backend routes badly.
   * Cross-database foreign keys are impossible in SQL Server, so master ids are stored
     together with immutable snapshots (names/codes). Master edits never change history.
   Executed against SQL Server 2022 by the automated tests (docs/TEST_EVIDENCE.md).
   ============================================================================ */
SET QUOTED_IDENTIFIER ON;   -- sqlcmd starts with it OFF; filtered indexes on these tables need it for any write
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO
IF SCHEMA_ID(N'qa') IS NULL EXEC (N'CREATE SCHEMA qa AUTHORIZATION dbo;');
GO
IF OBJECT_ID(N'qa.QualityRejectionEntries', N'U') IS NULL
CREATE TABLE qa.QualityRejectionEntries (
    Id                       bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityRejectionEntries PRIMARY KEY,
    SubmissionId             uniqueidentifier NOT NULL,           -- client-generated; idempotency key
    ShopId                   int           NOT NULL,
    ShopCode                 nvarchar(20)  NOT NULL,
    InspectionStageId        int           NOT NULL,
    StageCode                nvarchar(30)  NOT NULL,              -- snapshot
    StageName                nvarchar(100) NOT NULL,              -- snapshot
    MachineId                int           NOT NULL,
    MachineCode              nvarchar(30)  NOT NULL,              -- snapshot
    MachineName              nvarchar(100) NOT NULL,              -- snapshot
    RejectionPhenomenonId    int           NOT NULL,
    PhenomenonNameSnapshot   nvarchar(200) NOT NULL,              -- name at time of entry
    Quantity                 int           NOT NULL CONSTRAINT DF_QRE_Quantity DEFAULT (1),   -- number of rejected parts
    InspectorId              int           NOT NULL,              -- from authenticated token, never client input
    InspectorEmployeeCode    nvarchar(30)  NOT NULL,              -- snapshot
    Remarks                  nvarchar(500) NULL,
    ServerCreatedAt          datetime2(3)  NOT NULL CONSTRAINT DF_QRE_ServerCreatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_QRE_SubmissionId UNIQUE (SubmissionId),
    CONSTRAINT CK_QRE_ShopCode CHECK (ShopCode = N'CNC'),
    CONSTRAINT CK_QRE_Quantity CHECK (Quantity BETWEEN 1 AND 9999),
    CONSTRAINT CK_QRE_Remarks  CHECK (Remarks IS NULL OR LEN(LTRIM(RTRIM(Remarks))) > 0)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QRE_Inspector_Time' AND object_id = OBJECT_ID(N'qa.QualityRejectionEntries'))
    CREATE INDEX IX_QRE_Inspector_Time ON qa.QualityRejectionEntries (InspectorId, ServerCreatedAt DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QRE_Stage_Time' AND object_id = OBJECT_ID(N'qa.QualityRejectionEntries'))
    CREATE INDEX IX_QRE_Stage_Time ON qa.QualityRejectionEntries (InspectionStageId, ServerCreatedAt DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QRE_Phenomenon' AND object_id = OBJECT_ID(N'qa.QualityRejectionEntries'))
    CREATE INDEX IX_QRE_Phenomenon ON qa.QualityRejectionEntries (RejectionPhenomenonId);   -- used by the "never used -> may hard-delete" check
GO
PRINT N'02b_entries_schema_MachineShopDB.sql complete.';
