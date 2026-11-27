/* ============================================================================
   15_api_permissions.sql  -  run in EACH of the three databases (QualityMasterDB, GDC_AndonDB, MachineShopDB)

   Gives the API's database login exactly the rights the API uses, table by table (least privilege):
     * QualityMasterDB: read every qa table; write only the tables the API maintains; the audit log is append-only
       (SELECT, INSERT); PRE/FINAL entries can be inserted and only their two reversal columns updated (corrections).
     * GDC_AndonDB / MachineShopDB: ONLY qa.QualityRejectionEntries (SELECT, INSERT, UPDATE of the two reversal
       columns). Nothing else in these shared databases is granted.
   Earlier versions of the runbook granted whole-schema rights; those are revoked here, so re-running this script
   moves an existing installation to least privilege. Idempotent.

   Set @ApiUser to the database user / login name of the API (default qr_api). The login must already exist on the
   server (CREATE LOGIN ... is a server-level step for the DBA, see docs/DATABASE_RUNBOOK.md).
   ============================================================================ */
SET QUOTED_IDENTIFIER ON;   -- sqlcmd starts with it OFF; filtered indexes on these tables need it for any write
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @ApiUser sysname = N'qr_api';

IF SUSER_ID(@ApiUser) IS NULL AND DATABASE_PRINCIPAL_ID(@ApiUser) IS NULL
    THROW 50150, N'The API login does not exist on this server. Create it first (CREATE LOGIN), then run this script.', 1;

DECLARE @u nvarchar(300) = QUOTENAME(@ApiUser), @sql nvarchar(max);
IF DATABASE_PRINCIPAL_ID(@ApiUser) IS NULL
BEGIN
    SET @sql = N'CREATE USER ' + @u + N' FOR LOGIN ' + @u + N';';
    EXEC sys.sp_executesql @sql;
END
-- After a restore onto another server the database user can be "orphaned" (its SID differs from the login's): re-map it.
ELSE IF SUSER_SID(@ApiUser) IS NOT NULL
     AND (SELECT sid FROM sys.database_principals WHERE name = @ApiUser) <> SUSER_SID(@ApiUser)
BEGIN
    SET @sql = N'ALTER USER ' + @u + N' WITH LOGIN = ' + @u + N';';
    EXEC sys.sp_executesql @sql;
    PRINT N'Re-mapped the orphaned database user ' + @ApiUser + N' to its login.';
END;

-- The REVOKE and the GRANTs below are one transaction: a failure part-way leaves the previous rights in place.
BEGIN TRANSACTION;

-- Remove the broad schema-level rights of earlier runbook versions.
IF SCHEMA_ID(N'qa') IS NOT NULL
BEGIN
    SET @sql = N'REVOKE SELECT, INSERT, UPDATE, DELETE ON SCHEMA::qa FROM ' + @u + N';';
    EXEC sys.sp_executesql @sql;
END;

DECLARE @grants TABLE (Obj sysname, Perm nvarchar(200));
IF OBJECT_ID(N'qa.Users', N'U') IS NOT NULL   -- the master database
    INSERT @grants VALUES
        (N'qa.Roles',               N'SELECT'),
        (N'qa.Shops',               N'SELECT'),
        (N'qa.InspectionStages',    N'SELECT'),            -- the Q8 code allocation lock is SELECT ... WITH (UPDLOCK, HOLDLOCK)
        (N'qa.ShopStages',          N'SELECT'),
        (N'qa.ShiftDefinitions',    N'SELECT'),
        (N'qa.SchemaVersions',      N'SELECT'),
        (N'qa.Users',               N'SELECT, INSERT, UPDATE'),
        (N'qa.LoginIdentifiers',    N'SELECT, INSERT'),
        (N'qa.UserShops',           N'SELECT, INSERT, DELETE'),
        (N'qa.Machines',            N'SELECT, INSERT, UPDATE, DELETE'),
        (N'qa.RejectionPhenomena',  N'SELECT, INSERT, UPDATE, DELETE'),
        (N'qa.EntryCorrections',    N'SELECT, INSERT, UPDATE'),
        (N'qa.ProductionCounts',    N'SELECT, INSERT, UPDATE'),
        (N'qa.AuditLog',            N'SELECT, INSERT');    -- append-only: no UPDATE, no DELETE

IF OBJECT_ID(N'qa.QualityRejectionEntries', N'U') IS NOT NULL   -- every entry database (and PRE/FINAL in the master)
    INSERT @grants VALUES
        (N'qa.QualityRejectionEntries', N'SELECT, INSERT'),
        (N'qa.QualityRejectionEntries', N'UPDATE (ReversedAt, ReversedByCorrectionId)');   -- approved corrections only

DECLARE @obj sysname, @perm nvarchar(200);
DECLARE g CURSOR LOCAL FAST_FORWARD FOR SELECT Obj, Perm FROM @grants WHERE OBJECT_ID(Obj, N'U') IS NOT NULL;
OPEN g;
FETCH NEXT FROM g INTO @obj, @perm;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF @perm LIKE N'UPDATE (%'
        SET @sql = N'GRANT UPDATE ON ' + QUOTENAME(PARSENAME(@obj, 2)) + N'.' + QUOTENAME(PARSENAME(@obj, 1))
                 + N' ' + SUBSTRING(@perm, 8, 200) + N' TO ' + @u + N';';
    ELSE
        SET @sql = N'GRANT ' + @perm + N' ON ' + QUOTENAME(PARSENAME(@obj, 2)) + N'.' + QUOTENAME(PARSENAME(@obj, 1)) + N' TO ' + @u + N';';
    EXEC sys.sp_executesql @sql;
    FETCH NEXT FROM g INTO @obj, @perm;
END;
CLOSE g; DEALLOCATE g;

-- Whole-table UPDATE on entries would allow editing saved entries: make sure only the column grant remains.
IF OBJECT_ID(N'qa.QualityRejectionEntries', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.database_permissions p
               WHERE p.major_id = OBJECT_ID(N'qa.QualityRejectionEntries') AND p.minor_id = 0
                 AND p.grantee_principal_id = DATABASE_PRINCIPAL_ID(@ApiUser) AND p.permission_name IN (N'UPDATE', N'DELETE') AND p.state = N'G')
BEGIN
    SET @sql = N'REVOKE UPDATE, DELETE ON qa.QualityRejectionEntries FROM ' + @u + N';
                GRANT UPDATE ON qa.QualityRejectionEntries (ReversedAt, ReversedByCorrectionId) TO ' + @u + N';';
    EXEC sys.sp_executesql @sql;
END;

COMMIT TRANSACTION;

-- Role memberships grant rights this script cannot see table by table (e.g. db_datawriter can UPDATE the audit log).
IF EXISTS (SELECT 1 FROM sys.database_role_members rm WHERE rm.member_principal_id = DATABASE_PRINCIPAL_ID(@ApiUser))
BEGIN
    SELECT r.name AS [Role the API user is a member of] FROM sys.database_role_members rm
    JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id WHERE rm.member_principal_id = DATABASE_PRINCIPAL_ID(@ApiUser);
    PRINT N'WARNING: the API user is a member of the database roles listed above. They grant more than this script (for example';
    PRINT N'db_datawriter allows editing the audit log and saved entries). Remove the memberships unless another system needs them.';
END;

SELECT OBJECT_SCHEMA_NAME(p.major_id) + N'.' + OBJECT_NAME(p.major_id) AS [Object],
       ISNULL(COL_NAME(p.major_id, p.minor_id), N'') AS [Column], p.permission_name AS [Permission]
FROM sys.database_permissions p
WHERE p.grantee_principal_id = DATABASE_PRINCIPAL_ID(@ApiUser) AND p.class = 1 AND p.state = N'G'
ORDER BY 1, 3, 2;
PRINT N'API permissions applied for ' + @ApiUser + N' in ' + DB_NAME() + N'.';
