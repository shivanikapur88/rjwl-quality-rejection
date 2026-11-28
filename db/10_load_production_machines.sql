/* ============================================================================
   10_load_production_machines.sql  -  master database (QualityMasterDB)

   Controlled load of the APPROVED machine list. Edit only the VALUES block below
   (one row per machine: shop code, machine code, machine name, active 1/0), then run.

   The script validates the WHOLE list first and changes nothing if any row is wrong:
     * unknown shop code (must be GDC, CNC, PRE or FINAL)
     * empty / too long / invalid machine code (letters, digits, space . _ / -, max 30)
     * empty / too long machine name (max 100)
     * the same machine code twice for one shop in the list
     * the EXAMPLE rows still present, or a code starting with DEV- (placeholder machines)
   Machines that already exist (same shop + code) get the new NAME, not a duplicate row; their active flag is kept
   unless @UpdateActiveFlagOfExisting = 1 (so a machine deactivated in the app is not silently reactivated).
   Every insert/update is written to qa.AuditLog (UserId NULL = loaded by script).
   Set @DeactivatePlaceholders = 1 to deactivate the DEV-* placeholder machines from script 05 at the same time.
   Run it first with @DryRun = 1: it reports what it WOULD do and rolls back.
   ============================================================================ */
SET QUOTED_IDENTIFIER ON;   -- sqlcmd starts with it OFF; filtered indexes on these tables need it for any write
SET ANSI_NULLS ON;
-- FOR JSON needs SQL Server 2016; the scripts as a whole need SQL Server 2017 (version 14) or later, on any database compatibility level; the database's level is
-- left alone (GDC_AndonDB and MachineShopDB belong to other systems). (Fable review: an earlier compatibility-level check
-- here was wrong and could block the upgrade of those databases.)
IF CAST(SERVERPROPERTY('ProductMajorVersion') AS int) < 14
    THROW 50015, N'SQL Server 2017 or later is required.', 1;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @DryRun bit = 1;                    -- 1 = validate and report only; 0 = apply
DECLARE @DeactivatePlaceholders bit = 0;    -- 1 = also deactivate DEV-* placeholder machines
-- 0 (default) = machines that already exist keep their current active flag (an administrator may have deactivated one in the
-- app); only new machines take the flag from the list. 1 = the list's flag overwrites existing machines too.
DECLARE @UpdateActiveFlagOfExisting bit = 0;

DECLARE @in TABLE (RowNo int IDENTITY(1,1), ShopCode nvarchar(20), MachineCode nvarchar(200), MachineName nvarchar(400), IsActive bit);
INSERT INTO @in (ShopCode, MachineCode, MachineName, IsActive) VALUES
    -- ShopCode, MachineCode,  MachineName,                IsActive       (replace these EXAMPLE rows with the approved list)
    (N'GDC',     N'EXAMPLE-1', N'EXAMPLE - replace me',    1),
    (N'CNC',     N'EXAMPLE-2', N'EXAMPLE - replace me',    1);

/* ---------- validation (nothing is changed if any problem is found) ---------- */
DECLARE @problems TABLE (RowNo int, Problem nvarchar(200));
INSERT INTO @problems
SELECT i.RowNo, N'Unknown shop code ' + ISNULL(i.ShopCode, N'(empty)') FROM @in i WHERE NOT EXISTS (SELECT 1 FROM qa.Shops s WHERE s.ShopCode = i.ShopCode)
UNION ALL SELECT i.RowNo, N'Machine code is empty' FROM @in i WHERE LEN(LTRIM(RTRIM(ISNULL(i.MachineCode, N'')))) = 0
UNION ALL SELECT i.RowNo, N'Machine code longer than 30 characters' FROM @in i WHERE LEN(LTRIM(RTRIM(i.MachineCode))) > 30
UNION ALL SELECT i.RowNo, N'Machine code has characters other than letters, digits, space . _ / -'
          FROM @in i WHERE LTRIM(RTRIM(i.MachineCode)) COLLATE Latin1_General_BIN2 LIKE N'%[^A-Za-z0-9 ._/-]%' OR LTRIM(RTRIM(i.MachineCode)) COLLATE Latin1_General_BIN2 LIKE N'[^A-Za-z0-9]%'
UNION ALL SELECT i.RowNo, N'Machine name is empty' FROM @in i WHERE LEN(LTRIM(RTRIM(ISNULL(i.MachineName, N'')))) = 0
UNION ALL SELECT i.RowNo, N'Machine name longer than 100 characters' FROM @in i WHERE LEN(LTRIM(RTRIM(i.MachineName))) > 100
UNION ALL SELECT i.RowNo, N'Active flag must be 1 or 0' FROM @in i WHERE i.IsActive IS NULL
UNION ALL SELECT i.RowNo, N'EXAMPLE row still present - replace the example rows with the approved list' FROM @in i WHERE LTRIM(RTRIM(i.MachineCode)) LIKE N'EXAMPLE-%'
UNION ALL SELECT i.RowNo, N'DEV- codes are development placeholders and may not be loaded as production machines' FROM @in i WHERE LTRIM(RTRIM(i.MachineCode)) LIKE N'DEV-%'
UNION ALL SELECT i.RowNo, N'Same machine code appears more than once for this shop in the list'
          FROM @in i WHERE EXISTS (SELECT 1 FROM @in j WHERE j.RowNo <> i.RowNo AND j.ShopCode = i.ShopCode AND UPPER(LTRIM(RTRIM(j.MachineCode))) = UPPER(LTRIM(RTRIM(i.MachineCode))) COLLATE Latin1_General_CI_AI);

IF EXISTS (SELECT 1 FROM @problems)
BEGIN
    SELECT p.RowNo, i.ShopCode, i.MachineCode, p.Problem FROM @problems p JOIN @in i ON i.RowNo = p.RowNo ORDER BY p.RowNo;
    THROW 50100, N'The machine list has problems (listed above). Nothing was changed.', 1;
END;

/* ---------- apply ---------- */
BEGIN TRANSACTION;
    DECLARE @changes TABLE (Action nvarchar(10), MachineId int, ShopCode nvarchar(20), MachineCode nvarchar(30), MachineName nvarchar(100), IsActive bit);

    MERGE qa.Machines WITH (HOLDLOCK) AS t
    USING (SELECT s.Id AS ShopId, s.ShopCode, LTRIM(RTRIM(i.MachineCode)) AS MachineCode, LTRIM(RTRIM(i.MachineName)) AS MachineName, i.IsActive
           FROM @in i JOIN qa.Shops s ON s.ShopCode = i.ShopCode) AS src
       ON t.ShopId = src.ShopId AND t.MachineCode = src.MachineCode
    -- Binary comparison: a change of letter case only (e.g. 'press' -> 'Press') is a rename too.
    WHEN MATCHED AND (t.MachineName COLLATE Latin1_General_BIN2 <> src.MachineName COLLATE Latin1_General_BIN2 OR (@UpdateActiveFlagOfExisting = 1 AND t.IsActive <> src.IsActive)) THEN
        UPDATE SET MachineName = src.MachineName,
                   IsActive = CASE WHEN @UpdateActiveFlagOfExisting = 1 THEN src.IsActive ELSE t.IsActive END,
                   ModifiedAt = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (MachineCode, MachineName, ShopId, IsActive) VALUES (src.MachineCode, src.MachineName, src.ShopId, src.IsActive)
    OUTPUT $action, inserted.Id, src.ShopCode, inserted.MachineCode, inserted.MachineName, inserted.IsActive INTO @changes;

    IF @DeactivatePlaceholders = 1
        UPDATE m SET IsActive = 0, ModifiedAt = SYSUTCDATETIME()
        OUTPUT N'DEACTIVATE', inserted.Id, NULL, inserted.MachineCode, inserted.MachineName, inserted.IsActive INTO @changes
        FROM qa.Machines m WHERE m.MachineCode LIKE N'DEV-%' AND m.IsActive = 1;

    INSERT INTO qa.AuditLog (UserId, Action, EntityType, EntityId, NewValue)
    SELECT NULL, CASE c.Action WHEN N'INSERT' THEN N'MACHINE_CREATED' WHEN N'DEACTIVATE' THEN N'MACHINE_DEACTIVATED' ELSE N'MACHINE_EDITED' END,
           N'Machine', CAST(c.MachineId AS nvarchar(50)),
           (SELECT c.MachineCode, c.MachineName, c.IsActive, N'10_load_production_machines.sql' AS Source FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
    FROM @changes c;

    SELECT Action, ShopCode, MachineCode, MachineName, IsActive FROM @changes ORDER BY Action, ShopCode, MachineCode;

IF @DryRun = 1
BEGIN
    ROLLBACK TRANSACTION;
    PRINT N'DRY RUN: the changes listed above were NOT applied. Set @DryRun = 0 to apply.';
END
ELSE
BEGIN
    COMMIT TRANSACTION;
    PRINT N'Machine list applied.';
END
