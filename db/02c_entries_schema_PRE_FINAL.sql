/* ============================================================================
   02c_entries_schema_PRE_FINAL.sql  -  Rejection ENTRIES table for the
   Pre-treatment Inspection (PRE) and FINAL Inspection (FINAL) shops.

   DECIDED: Pre-treatment (PRE) and FINAL entries are stored in QualityMasterDB. Run this script ONCE in QualityMasterDB and
   set ConnectionStrings:PreEntries and ConnectionStrings:FinalEntries to the same connection string as ConnectionStrings:Master.
   (GDC entries stay in GDC_AndonDB and CNC entries in MachineShopDB.) Entries of the two shops share this one table and are kept
   apart by ShopId/ShopCode. To use a different database later, run this script there and change the two connection strings.
   REVIEW BEFORE RUNNING. Creates only schema [qa] (if missing) and ONE table.
   A CHECK constraint accepts only ShopCode 'PRE' or 'FINAL'.
   NOT YET EXECUTED against SQL Server.
   ============================================================================ */
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
    CONSTRAINT CK_QRE_ShopCode CHECK (ShopCode IN (N'PRE', N'FINAL')),
    CONSTRAINT CK_QRE_Quantity CHECK (Quantity BETWEEN 1 AND 9999),
    CONSTRAINT CK_QRE_Remarks  CHECK (Remarks IS NULL OR LEN(LTRIM(RTRIM(Remarks))) > 0)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QRE_Inspector_Time' AND object_id = OBJECT_ID(N'qa.QualityRejectionEntries'))
    CREATE INDEX IX_QRE_Inspector_Time ON qa.QualityRejectionEntries (InspectorId, ServerCreatedAt DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QRE_Shop_Time' AND object_id = OBJECT_ID(N'qa.QualityRejectionEntries'))
    CREATE INDEX IX_QRE_Shop_Time ON qa.QualityRejectionEntries (ShopId, ServerCreatedAt DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_QRE_Phenomenon' AND object_id = OBJECT_ID(N'qa.QualityRejectionEntries'))
    CREATE INDEX IX_QRE_Phenomenon ON qa.QualityRejectionEntries (RejectionPhenomenonId);   -- used by the "never used -> may hard-delete" check
GO
PRINT N'02c_entries_schema_PRE_FINAL.sql complete.';
