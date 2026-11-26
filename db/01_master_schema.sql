/* ============================================================================
   01_master_schema.sql  -  Master data schema  (PROPOSED database: QualityMasterDB)

   REVIEW BEFORE RUNNING.
   * This script does NOT create a database. Create/choose the master database
     yourself (see docs/Phase2_Design.md, "Approval items"), connect to it, then run.
   * It creates only a new schema [qa] and new tables inside it. It does not
     touch any existing object.
   * Re-runnable: every object is guarded with IF NOT EXISTS / OBJECT_ID checks.
   * NOT YET EXECUTED against SQL Server (no SQL Server available during authoring).
   ============================================================================ */
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF SCHEMA_ID(N'qa') IS NULL EXEC (N'CREATE SCHEMA qa AUTHORIZATION dbo;');
GO

/* ---------- Roles ---------- */
IF OBJECT_ID(N'qa.Roles', N'U') IS NULL
CREATE TABLE qa.Roles (
    Id        tinyint       NOT NULL CONSTRAINT PK_Roles PRIMARY KEY,
    RoleName  nvarchar(30)  NOT NULL CONSTRAINT UQ_Roles_RoleName UNIQUE
);
GO

/* ---------- Users ---------- */
IF OBJECT_ID(N'qa.Users', N'U') IS NULL
CREATE TABLE qa.Users (
    Id            int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
    EmployeeCode  nvarchar(30)  NOT NULL,
    Username      nvarchar(50)  NOT NULL,
    DisplayName   nvarchar(100) NOT NULL,
    PasswordHash  nvarchar(500) NOT NULL,           -- ASP.NET Core PasswordHasher output; never plaintext
    RoleId        tinyint       NOT NULL CONSTRAINT FK_Users_Roles REFERENCES qa.Roles(Id),
    IsActive      bit           NOT NULL CONSTRAINT DF_Users_IsActive DEFAULT (1),
    CreatedAt     datetime2(0)  NOT NULL CONSTRAINT DF_Users_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_Users_EmployeeCode UNIQUE (EmployeeCode),
    CONSTRAINT UQ_Users_Username     UNIQUE (Username)
);
GO

/* ---------- Shops ---------- */
IF OBJECT_ID(N'qa.Shops', N'U') IS NULL
CREATE TABLE qa.Shops (
    Id            int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Shops PRIMARY KEY,
    ShopCode      nvarchar(20)  NOT NULL CONSTRAINT UQ_Shops_ShopCode UNIQUE,
    ShopName      nvarchar(100) NOT NULL,
    -- Logical key the backend uses to pick the entry database
    -- (resolved to a connection string in server configuration, never sent to the app).
    EntryStoreKey nvarchar(30)  NOT NULL,
    IsActive      bit           NOT NULL CONSTRAINT DF_Shops_IsActive DEFAULT (1)
);
GO

/* ---------- Inspection stages: exactly four, kept separate ---------- */
IF OBJECT_ID(N'qa.InspectionStages', N'U') IS NULL
CREATE TABLE qa.InspectionStages (
    Id         int IDENTITY(1,1) NOT NULL CONSTRAINT PK_InspectionStages PRIMARY KEY,
    StageCode  nvarchar(30)  NOT NULL CONSTRAINT UQ_InspectionStages_StageCode UNIQUE,
    StageName  nvarchar(100) NOT NULL,
    SortOrder  tinyint       NOT NULL,
    IsActive   bit           NOT NULL CONSTRAINT DF_InspectionStages_IsActive DEFAULT (1)
);
GO

/* ---------- Explicit shop <-> stage mapping ---------- */
IF OBJECT_ID(N'qa.ShopStages', N'U') IS NULL
CREATE TABLE qa.ShopStages (
    ShopId            int NOT NULL CONSTRAINT FK_ShopStages_Shops  REFERENCES qa.Shops(Id),
    InspectionStageId int NOT NULL CONSTRAINT FK_ShopStages_Stages REFERENCES qa.InspectionStages(Id),
    IsActive          bit NOT NULL CONSTRAINT DF_ShopStages_IsActive DEFAULT (1),
    CONSTRAINT PK_ShopStages PRIMARY KEY (ShopId, InspectionStageId)
);
GO

/* ---------- Which shops a user may work in ---------- */
IF OBJECT_ID(N'qa.UserShops', N'U') IS NULL
CREATE TABLE qa.UserShops (
    UserId int NOT NULL CONSTRAINT FK_UserShops_Users REFERENCES qa.Users(Id),
    ShopId int NOT NULL CONSTRAINT FK_UserShops_Shops REFERENCES qa.Shops(Id),
    CONSTRAINT PK_UserShops PRIMARY KEY (UserId, ShopId)
);
GO

/* ---------- Machines (per shop) ---------- */
IF OBJECT_ID(N'qa.Machines', N'U') IS NULL
CREATE TABLE qa.Machines (
    Id           int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Machines PRIMARY KEY,
    MachineCode  nvarchar(30)  NOT NULL,
    MachineName  nvarchar(100) NOT NULL,
    ShopId       int           NOT NULL CONSTRAINT FK_Machines_Shops REFERENCES qa.Shops(Id),
    IsActive     bit           NOT NULL CONSTRAINT DF_Machines_IsActive DEFAULT (1),
    CONSTRAINT UQ_Machines_Shop_Code UNIQUE (ShopId, MachineCode)
);
GO

/* ---------- Rejection phenomena (scoped to a stage; shop access via qa.ShopStages) ---------- */
IF OBJECT_ID(N'qa.RejectionPhenomena', N'U') IS NULL
CREATE TABLE qa.RejectionPhenomena (
    Id                int IDENTITY(1,1) NOT NULL CONSTRAINT PK_RejectionPhenomena PRIMARY KEY,
    PhenomenonCode    nvarchar(40)  NOT NULL CONSTRAINT UQ_RejectionPhenomena_Code UNIQUE,
    PhenomenonName    nvarchar(200) NOT NULL,   -- original text, exactly as entered / imported
    NormalizedName    nvarchar(200) NOT NULL,   -- whitespace-collapsed, upper-case; uniqueness key
    InspectionStageId int           NOT NULL CONSTRAINT FK_RejectionPhenomena_Stages REFERENCES qa.InspectionStages(Id),
    IsActive          bit           NOT NULL CONSTRAINT DF_RejectionPhenomena_IsActive DEFAULT (1),
    CreatedBy         int           NULL CONSTRAINT FK_RejectionPhenomena_CreatedBy  REFERENCES qa.Users(Id),
    CreatedAt         datetime2(0)  NOT NULL CONSTRAINT DF_RejectionPhenomena_CreatedAt DEFAULT (SYSUTCDATETIME()),
    ModifiedBy        int           NULL CONSTRAINT FK_RejectionPhenomena_ModifiedBy REFERENCES qa.Users(Id),
    ModifiedAt        datetime2(0)  NULL,
    RowVer            rowversion    NOT NULL,   -- optimistic concurrency for admin edits
    CONSTRAINT UQ_RejectionPhenomena_Stage_Name UNIQUE (InspectionStageId, NormalizedName),
    CONSTRAINT CK_RejectionPhenomena_NameNotBlank CHECK (LEN(LTRIM(RTRIM(PhenomenonName))) > 0)
);
GO

/* ---------- Audit log (all master changes) ---------- */
IF OBJECT_ID(N'qa.AuditLog', N'U') IS NULL
CREATE TABLE qa.AuditLog (
    Id          bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY,
    UserId      int           NULL CONSTRAINT FK_AuditLog_Users REFERENCES qa.Users(Id),  -- NULL = system/import
    Action      nvarchar(50)  NOT NULL,
    EntityType  nvarchar(50)  NOT NULL,
    EntityId    nvarchar(50)  NULL,
    OldValue    nvarchar(max) NULL,
    NewValue    nvarchar(max) NULL,
    Timestamp   datetime2(3)  NOT NULL CONSTRAINT DF_AuditLog_Timestamp DEFAULT (SYSUTCDATETIME())
);
GO

/* ---------- Supporting indexes ---------- */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RejectionPhenomena_Stage_Active' AND object_id = OBJECT_ID(N'qa.RejectionPhenomena'))
    CREATE INDEX IX_RejectionPhenomena_Stage_Active ON qa.RejectionPhenomena (InspectionStageId, IsActive) INCLUDE (PhenomenonName);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Machines_Shop_Active' AND object_id = OBJECT_ID(N'qa.Machines'))
    CREATE INDEX IX_Machines_Shop_Active ON qa.Machines (ShopId, IsActive);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AuditLog_Entity' AND object_id = OBJECT_ID(N'qa.AuditLog'))
    CREATE INDEX IX_AuditLog_Entity ON qa.AuditLog (EntityType, EntityId, Timestamp DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AuditLog_Timestamp' AND object_id = OBJECT_ID(N'qa.AuditLog'))
    CREATE INDEX IX_AuditLog_Timestamp ON qa.AuditLog (Timestamp DESC);
GO

PRINT N'01_master_schema.sql complete.';
