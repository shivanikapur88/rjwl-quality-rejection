/* ============================================================================
   13_upgrade_master_production_counts.sql  -  master database (QualityMasterDB)

   Round 3, dashboard #8 (rejection rate). Run AFTER 11, on a fresh install and when upgrading. Idempotent.

   qa.ProductionCounts holds the number of parts PRODUCED per machine, production day and shift. The rejection rate is
   rejected parts / produced parts and is shown only where these counts exist. This system does not measure production:
   the counts come from
     (a) an administrator's CSV upload on the dashboard (POST /api/admin/production-counts), or
     (b) an automatic feed from the production systems - see 14_production_counts_feed_TEMPLATE.sql (read-only on their side).
   Counting rules (what "produced" means, rework, scrap at other stages) must be agreed by RJWL before the rate is used
   for decisions (docs/BUSINESS_DECISIONS.md #13).
   ============================================================================ */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO
IF OBJECT_ID(N'qa.ProductionCounts', N'U') IS NULL
CREATE TABLE qa.ProductionCounts (
    Id                bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ProductionCounts PRIMARY KEY,
    ShopId            int           NOT NULL CONSTRAINT FK_ProductionCounts_Shops REFERENCES qa.Shops(Id),
    MachineId         int           NOT NULL CONSTRAINT FK_ProductionCounts_Machines REFERENCES qa.Machines(Id),
    ProductionDate    date          NOT NULL,
    ShiftCode         nvarchar(10)  NOT NULL,
    ProducedQuantity  int           NOT NULL CONSTRAINT CK_ProductionCounts_Qty CHECK (ProducedQuantity BETWEEN 0 AND 1000000),
    Source            nvarchar(50)  NOT NULL,             -- 'upload' or the feed name
    UpdatedAt         datetime2(3)  NOT NULL CONSTRAINT DF_ProductionCounts_UpdatedAt DEFAULT (SYSUTCDATETIME()),
    UpdatedBy         int           NULL CONSTRAINT FK_ProductionCounts_Users REFERENCES qa.Users(Id),   -- NULL = automatic feed
    CONSTRAINT UQ_ProductionCounts_Machine_Day_Shift UNIQUE (MachineId, ProductionDate, ShiftCode)
);
GO
-- The stored shop must be the machine's shop (third full review): enforced by the database, not only by the upload and the
-- feed template. Machines(Id, ShopId) is unique because Id is; the index lets the foreign key reference the pair.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Machines_Id_Shop' AND object_id = OBJECT_ID(N'qa.Machines'))
    CREATE UNIQUE INDEX UX_Machines_Id_Shop ON qa.Machines (Id, ShopId);
IF OBJECT_ID(N'qa.FK_ProductionCounts_MachineShop', N'F') IS NULL
    ALTER TABLE qa.ProductionCounts WITH CHECK ADD CONSTRAINT FK_ProductionCounts_MachineShop
        FOREIGN KEY (MachineId, ShopId) REFERENCES qa.Machines (Id, ShopId);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProductionCounts_Shop_Day' AND object_id = OBJECT_ID(N'qa.ProductionCounts'))
    CREATE INDEX IX_ProductionCounts_Shop_Day ON qa.ProductionCounts (ShopId, ProductionDate, ShiftCode) INCLUDE (MachineId, ProducedQuantity);
GO
IF NOT EXISTS (SELECT 1 FROM qa.SchemaVersions WHERE Script = N'13_upgrade_master_production_counts')
    INSERT INTO qa.SchemaVersions (Script) VALUES (N'13_upgrade_master_production_counts');
GO
PRINT N'13_upgrade_master_production_counts.sql complete.';
