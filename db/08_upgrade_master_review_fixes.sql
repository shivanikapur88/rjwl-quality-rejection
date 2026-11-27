/* ============================================================================
   08_upgrade_master_review_fixes.sql  -  master database (QualityMasterDB)

   Run AFTER 01 and 03 (and 04), on a fresh install AND when upgrading an existing
   database. Idempotent: every change is guarded, so re-running it is safe.
   Does not delete or rewrite any existing business data.

   What it adds (review findings in brackets):
     1. qa.Users.TokenVersion   - bumped on password reset, deactivation and role
                                  change; tokens carrying an older value are refused   [Q2]
     2. qa.Users.ModifiedAt / RowVer, qa.Machines.ModifiedAt / RowVer
                                - lets the API reject saves made from a stale screen   [stale edits]
     3. qa.LoginIdentifiers     - ONE unique namespace for usernames and employee
                                  codes (login accepts either)                          [Q5]
     4. TR_Users_KeepActiveAdministrator
                                - database backstop: no statement may leave the
                                  system without an active administrator                [Q3]
     5. CK_Users_EmployeeCode_Length - employee codes 2..30 characters                  [Q6]

   If step 5 finds an employee code that is not 2-30 characters long, it stops with error 50081 and lists them.
   If step 3 finds two users whose username/employee code collide (possible with
   the earlier schema), it STOPS with error 50080 and lists them. Resolve the
   collision (rename one account in qa.Users) and run the script again. Steps 1-2 (new columns) have then
   already been applied; that is harmless, and the re-run skips them.
   Existing tokens carry no token version, so every user signs in again once after the API update.
   Tested against SQL Server 2022 (see docs/TEST_EVIDENCE.md).
   ============================================================================ */
SET NOCOUNT ON;
SET XACT_ABORT ON;
-- Required for filtered indexes and consistent behaviour; sqlcmd defaults QUOTED_IDENTIFIER to OFF.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ---------- 1. Token version (session revocation) ---------- */
IF COL_LENGTH(N'qa.Users', N'TokenVersion') IS NULL
    ALTER TABLE qa.Users ADD TokenVersion int NOT NULL CONSTRAINT DF_Users_TokenVersion DEFAULT (0);
GO

/* ---------- 2. Version columns for stale-screen protection ---------- */
IF COL_LENGTH(N'qa.Users', N'ModifiedAt') IS NULL
    ALTER TABLE qa.Users ADD ModifiedAt datetime2(0) NULL;
IF COL_LENGTH(N'qa.Users', N'RowVer') IS NULL
    ALTER TABLE qa.Users ADD RowVer rowversion NOT NULL;
IF COL_LENGTH(N'qa.Machines', N'ModifiedAt') IS NULL
    ALTER TABLE qa.Machines ADD ModifiedAt datetime2(0) NULL;
IF COL_LENGTH(N'qa.Machines', N'RowVer') IS NULL
    ALTER TABLE qa.Machines ADD RowVer rowversion NOT NULL;
GO

/* ---------- 3. Shared login-identifier namespace ---------- */
IF OBJECT_ID(N'qa.LoginIdentifiers', N'U') IS NULL
CREATE TABLE qa.LoginIdentifiers (
    -- UPPER(trimmed value): collation-independent uniqueness key across BOTH usernames and employee codes
    NormalizedIdentifier nvarchar(50) NOT NULL CONSTRAINT PK_LoginIdentifiers PRIMARY KEY,
    UserId               int          NOT NULL CONSTRAINT FK_LoginIdentifiers_Users REFERENCES qa.Users(Id),
    -- 'U' = username, 'E' = employee code, 'B' = both (the user's username equals its employee code)
    Kind                 char(1)      NOT NULL CONSTRAINT CK_LoginIdentifiers_Kind CHECK (Kind IN ('U', 'E', 'B'))
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_LoginIdentifiers_User' AND object_id = OBJECT_ID(N'qa.LoginIdentifiers'))
    CREATE INDEX IX_LoginIdentifiers_User ON qa.LoginIdentifiers (UserId);
GO

-- Backfill for users created before this table existed.
BEGIN TRANSACTION;
    DECLARE @ids TABLE (NormalizedIdentifier nvarchar(50) NOT NULL, UserId int NOT NULL, Kind char(1) NOT NULL);
    INSERT INTO @ids (NormalizedIdentifier, UserId, Kind)
    SELECT UPPER(LTRIM(RTRIM(u.Username))), u.Id,
           CASE WHEN UPPER(LTRIM(RTRIM(u.Username))) = UPPER(LTRIM(RTRIM(u.EmployeeCode))) THEN 'B' ELSE 'U' END
    FROM qa.Users u
    UNION ALL
    SELECT UPPER(LTRIM(RTRIM(u.EmployeeCode))), u.Id, 'E'
    FROM qa.Users u
    WHERE UPPER(LTRIM(RTRIM(u.EmployeeCode))) <> UPPER(LTRIM(RTRIM(u.Username)));

    IF EXISTS (SELECT NormalizedIdentifier FROM @ids GROUP BY NormalizedIdentifier HAVING COUNT(DISTINCT UserId) > 1)
    BEGIN
        SELECT i.NormalizedIdentifier AS CollidingIdentifier, u.Id AS UserId, u.Username, u.EmployeeCode, u.IsActive
        FROM @ids i JOIN qa.Users u ON u.Id = i.UserId
        WHERE i.NormalizedIdentifier IN (SELECT NormalizedIdentifier FROM @ids GROUP BY NormalizedIdentifier HAVING COUNT(DISTINCT UserId) > 1)
        ORDER BY i.NormalizedIdentifier, u.Id;
        ROLLBACK TRANSACTION;
        THROW 50080, N'Two or more users share a username/employee code (listed above). Rename one of each pair in qa.Users, then re-run 08.', 1;
    END;

    -- qa.Users is the source of truth: remove identifier rows that no longer match a user's current username/code
    -- (for example after an old API renamed a user on the upgraded database), then add the missing ones.
    DELETE x FROM qa.LoginIdentifiers x
    WHERE NOT EXISTS (SELECT 1 FROM @ids i WHERE i.NormalizedIdentifier = x.NormalizedIdentifier AND i.UserId = x.UserId AND i.Kind = x.Kind);
    INSERT INTO qa.LoginIdentifiers (NormalizedIdentifier, UserId, Kind)
    SELECT i.NormalizedIdentifier, i.UserId, i.Kind
    FROM @ids i
    WHERE NOT EXISTS (SELECT 1 FROM qa.LoginIdentifiers x WHERE x.NormalizedIdentifier = i.NormalizedIdentifier);
COMMIT TRANSACTION;
GO
-- Legacy accounts whose username or employee code has characters the API does not accept (space, tab, accented letters...):
-- SQL and the API may upper-case them differently, so such a user might not be able to sign in. List them for renaming.
IF EXISTS (SELECT 1 FROM qa.Users WHERE Username COLLATE Latin1_General_BIN2 LIKE N'%[^A-Za-z0-9._-]%' OR EmployeeCode COLLATE Latin1_General_BIN2 LIKE N'%[^A-Za-z0-9._-]%')
BEGIN
    SELECT Id, Username, EmployeeCode, IsActive FROM qa.Users
    WHERE Username COLLATE Latin1_General_BIN2 LIKE N'%[^A-Za-z0-9._-]%' OR EmployeeCode COLLATE Latin1_General_BIN2 LIKE N'%[^A-Za-z0-9._-]%';
    PRINT N'WARNING: the users listed above have characters other than letters, digits, . _ - in their username or employee code.';
    PRINT N'They may not be able to sign in. Rename them (Admin -> Users) to letters, digits, . _ - only.';
END;
GO

/* ---------- 4. Database backstop: always keep one active administrator ---------- */
-- The API already serialises role changes/deactivations with locks (see EfMasterData.LockActiveAdministratorsAsync).
-- This trigger catches anything else (manual SQL, a future code path). READCOMMITTEDLOCK makes the check a locking
-- read even if READ_COMMITTED_SNAPSHOT is ON, so two concurrent demotions cannot both see "the other one is still active".
CREATE OR ALTER TRIGGER qa.TR_Users_KeepActiveAdministrator ON qa.Users
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    -- Only statements that REMOVE an active administrator (demotion or deactivation) need the check;
    -- ordinary edits of an administrator row (name, password reset) return here and take no extra locks.
    IF NOT EXISTS (SELECT 1 FROM deleted d JOIN qa.Roles r ON r.Id = d.RoleId
                   WHERE r.RoleName = N'Administrator' AND d.IsActive = 1
                     AND NOT EXISTS (SELECT 1 FROM inserted i JOIN qa.Roles r2 ON r2.Id = i.RoleId
                                     WHERE i.Id = d.Id AND r2.RoleName = N'Administrator' AND i.IsActive = 1))
        RETURN;
    IF NOT EXISTS (SELECT 1 FROM qa.Users u WITH (READCOMMITTEDLOCK) JOIN qa.Roles r ON r.Id = u.RoleId
                   WHERE r.RoleName = N'Administrator' AND u.IsActive = 1)
        THROW 50090, N'At least one active administrator must remain.', 1;
END;
GO

-- Lets the trigger's check (and the API's administrator lock) seek the active administrators instead of scanning and
-- share-locking every user row, which could block or deadlock behind unrelated user edits.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Users_Role_Active' AND object_id = OBJECT_ID(N'qa.Users'))
    CREATE INDEX IX_Users_Role_Active ON qa.Users (RoleId, IsActive);
GO

/* ---------- 5. Employee code length matches validation (2..30) ---------- */
-- Existing users with a 1-character code would make the constraint fail half-way: list them and stop with a clear error instead.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Users_EmployeeCode_Length' AND parent_object_id = OBJECT_ID(N'qa.Users'))
   AND EXISTS (SELECT 1 FROM qa.Users WHERE LEN(EmployeeCode) NOT BETWEEN 2 AND 30)
BEGIN
    SELECT Id, EmployeeCode, Username FROM qa.Users WHERE LEN(EmployeeCode) NOT BETWEEN 2 AND 30;
    THROW 50081, N'Some employee codes are shorter than 2 or longer than 30 characters (listed above). Correct them in qa.Users, then re-run 08.', 1;
END;
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_Users_EmployeeCode_Length' AND parent_object_id = OBJECT_ID(N'qa.Users'))
    ALTER TABLE qa.Users WITH CHECK ADD CONSTRAINT CK_Users_EmployeeCode_Length CHECK (LEN(EmployeeCode) BETWEEN 2 AND 30);
GO

/* ---------- 6. Schema version marker ---------- */
IF OBJECT_ID(N'qa.SchemaVersions', N'U') IS NULL
CREATE TABLE qa.SchemaVersions (
    Script    nvarchar(100) NOT NULL CONSTRAINT PK_SchemaVersions PRIMARY KEY,
    AppliedAt datetime2(0)  NOT NULL CONSTRAINT DF_SchemaVersions_AppliedAt DEFAULT (SYSUTCDATETIME())
);
GO
IF NOT EXISTS (SELECT 1 FROM qa.SchemaVersions WHERE Script = N'08_upgrade_master_review_fixes')
    INSERT INTO qa.SchemaVersions (Script) VALUES (N'08_upgrade_master_review_fixes');
GO
PRINT N'08_upgrade_master_review_fixes.sql complete.';
