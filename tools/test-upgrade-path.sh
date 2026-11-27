#!/usr/bin/env bash
# Upgrade-path test: supplied schema (01-04, entries table without Quantity, users created the old way, including a
# username/employee-code collision) -> 07, 09, 08. Creates and drops nothing but the database QRUP. NEVER run against production.
#   SQLCMD="sqlcmd -S localhost,14333 -U sa -P ... -C" DB_DIR=db tools/test-upgrade-path.sh
set -e
TMP="$(mktemp)"; trap 'rm -f "$TMP"' EXIT
fail() { echo "   FAILED: $*"; exit 1; }
: "${SQLCMD:?set SQLCMD}"; DB_DIR="${DB_DIR:-$(cd "$(dirname "$0")/../db" && pwd)}"
Q="$SQLCMD -b"
$Q -Q "IF DB_ID('QRUP') IS NOT NULL BEGIN ALTER DATABASE QRUP SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE QRUP; END; CREATE DATABASE QRUP;" >/dev/null
for f in 01_master_schema 02c_entries_schema_PRE_FINAL 03_seed_reference 04_seed_phenomena; do $Q -d QRUP -i $DB_DIR/$f.sql >/dev/null; done
echo "1. 'before' state = supplied schema (01-04, no 08) with users created the old way, incl. a cross-column collision"
$Q -d QRUP -Q "SET NOCOUNT ON; INSERT qa.Users (EmployeeCode, Username, DisplayName, PasswordHash, RoleId) VALUES ('E100','admin1','Admin','x',1),('E200','e100','Clash','x',2),('E300','insp3','Insp','x',2);"
echo "   simulate an entries table from before 07 (no Quantity) with one old entry"
$Q -d QRUP -Q "SET NOCOUNT ON; ALTER TABLE qa.QualityRejectionEntries DROP CONSTRAINT CK_QRE_Quantity, DF_QRE_Quantity; ALTER TABLE qa.QualityRejectionEntries DROP COLUMN Quantity; INSERT qa.QualityRejectionEntries (SubmissionId, ShopId, ShopCode, InspectionStageId, StageCode, StageName, MachineId, MachineCode, MachineName, RejectionPhenomenonId, PhenomenonNameSnapshot, InspectorId, InspectorEmployeeCode) VALUES (NEWID(),3,'PRE',3,'PRE_TREATMENT','Pre',1,'M','M',1,'P',3,'E300');"
echo "2. run 07 (quantity), as in the runbook before 08"
$Q -d QRUP -i $DB_DIR/07_migration_add_quantity.sql >/dev/null
$Q -d QRUP -h -1 -Q "SET NOCOUNT ON; SELECT '   old entry quantity = ' + CAST(Quantity AS varchar) FROM qa.QualityRejectionEntries"
echo "3. run 08 with the collision present -> must stop and change nothing"
if $Q -d QRUP -i $DB_DIR/08_upgrade_master_review_fixes.sql > "$TMP" 2>&1; then fail "08 succeeded despite the collision"; else grep -E "Msg 50080|E100" "$TMP" | head -3 | sed 's/^/   /'; fi
$Q -d QRUP -h -1 -Q "SET NOCOUNT ON; SELECT '   LoginIdentifiers rows after refused run: ' + CAST(COUNT(*) AS varchar) FROM qa.LoginIdentifiers"
echo "4. resolve the collision (rename one account) and re-run 08"
$Q -d QRUP -Q "UPDATE qa.Users SET Username = 'clash.user' WHERE EmployeeCode = 'E200';" >/dev/null
$Q -d QRUP -i $DB_DIR/08_upgrade_master_review_fixes.sql >/dev/null && echo "   08 ok"
$Q -d QRUP -h -1 -Q "SET NOCOUNT ON; SELECT '   identifiers: ' + STRING_AGG(NormalizedIdentifier + ':' + Kind, ', ') WITHIN GROUP (ORDER BY NormalizedIdentifier) FROM qa.LoginIdentifiers; SELECT '   TokenVersion for all = ' + CAST(MAX(TokenVersion) AS varchar) FROM qa.Users"
echo "5. re-run 08 again (idempotent), then 09 (runbook order: 08 before 09)"; $Q -d QRUP -i $DB_DIR/08_upgrade_master_review_fixes.sql >/dev/null && $Q -d QRUP -i $DB_DIR/09_upgrade_entries_indexes.sql >/dev/null && echo "   ok"
echo "6. trigger: deactivating the only admin is refused"
$Q -d QRUP -Q "UPDATE qa.Users SET IsActive = 0 WHERE Username = 'admin1'" 2>&1 | grep -o "At least one active administrator must remain" | sed 's/^/   /'
echo "7. round 2/3 upgrades on the same database: 11, 12 (backfills the old entry), 13; then all three again"
for f in 11_upgrade_master_shifts_corrections 12_upgrade_entries_shift_reversal 13_upgrade_master_production_counts; do $Q -d QRUP -i $DB_DIR/$f.sql >/dev/null; done
for f in 11_upgrade_master_shifts_corrections 12_upgrade_entries_shift_reversal 13_upgrade_master_production_counts; do $Q -d QRUP -i $DB_DIR/$f.sql >/dev/null; done
n=$($Q -d QRUP -h -1 -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM qa.QualityRejectionEntries WHERE ProductionDate IS NULL OR ShiftCode IS NULL" | tr -d '[:space:]')
[ "$n" = "0" ] || fail "entries without production day after 12: $n"; echo "   old entry has a production day and shift"
$Q -d QRUP -h -1 -Q "SET NOCOUNT ON; IF OBJECT_ID('qa.ProductionCounts') IS NULL THROW 50000, 'no ProductionCounts', 1; SELECT '   production counts table present'"

echo "8. backfill values: a row saved at 05:59:59.700 IST stays in shift C of the previous day"
$Q -d QRUP -Q "SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON; INSERT qa.QualityRejectionEntries (SubmissionId, ShopId, ShopCode, InspectionStageId, StageCode, StageName, MachineId, MachineCode, MachineName, RejectionPhenomenonId, PhenomenonNameSnapshot, InspectorId, InspectorEmployeeCode, Quantity, ServerCreatedAt) VALUES ('6B2B7C4E-0000-4000-8000-000000000001',3,'PRE',3,'PRE_TREATMENT','Pre',1,'M','M',1,'P',3,'E300',1,'2026-11-27T00:29:59.700');" >/dev/null
$Q -d QRUP -i $DB_DIR/12_upgrade_entries_shift_reversal.sql >/dev/null
v=$($Q -d QRUP -h -1 -Q "SET NOCOUNT ON; SELECT CONVERT(varchar(10), ProductionDate, 23) + '|' + ShiftCode FROM qa.QualityRejectionEntries WHERE SubmissionId = '6B2B7C4E-0000-4000-8000-000000000001'" | tr -d '[:space:]')
[ "$v" = "2026-11-26|C" ] || fail "05:59:59.700 backfilled as $v"; echo "   $v"
echo "9. db/12 refuses to backfill when its shift times differ from qa.ShiftDefinitions (error 50013)"
$Q -d QRUP -Q "SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON; UPDATE qa.QualityRejectionEntries SET ProductionDate = NULL, ShiftCode = NULL WHERE SubmissionId = '6B2B7C4E-0000-4000-8000-000000000001'; UPDATE qa.ShiftDefinitions SET StartTime = '07:00' WHERE ShiftCode = 'A';" >/dev/null
if $Q -d QRUP -i $DB_DIR/12_upgrade_entries_shift_reversal.sql > "$TMP" 2>&1; then fail "12 ran with mismatching shift times"; else grep -o "Msg 50013" "$TMP" | head -1 | sed 's/^/   refused: /'; fi
$Q -d QRUP -Q "SET NOCOUNT ON; UPDATE qa.ShiftDefinitions SET StartTime = '06:00' WHERE ShiftCode = 'A';" >/dev/null
$Q -d QRUP -i $DB_DIR/12_upgrade_entries_shift_reversal.sql >/dev/null && echo "   ok after the times match again"
echo "10. db/15 on a database that still has the old schema-wide grants; run twice"
$Q -Q "IF SUSER_ID('qrup_api') IS NULL CREATE LOGIN qrup_api WITH PASSWORD = 'Up-$(date +%s%N)-aA1!', CHECK_POLICY = OFF;" >/dev/null
$Q -d QRUP -Q "IF DATABASE_PRINCIPAL_ID('qrup_api') IS NULL CREATE USER qrup_api FOR LOGIN qrup_api; GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::qa TO qrup_api;" >/dev/null
sed "s/N'qr_api'/N'qrup_api'/" $DB_DIR/15_api_permissions.sql > "$TMP.15.sql"
$Q -d QRUP -i "$TMP.15.sql" >/dev/null && $Q -d QRUP -i "$TMP.15.sql" >/dev/null || fail "15 failed"
n=$($Q -d QRUP -h -1 -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id = DATABASE_PRINCIPAL_ID('qrup_api') AND class = 3" | tr -d '[:space:]')
[ "$n" = "0" ] || fail "schema-wide grants left: $n"
a=$($Q -d QRUP -h -1 -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id = DATABASE_PRINCIPAL_ID('qrup_api') AND major_id = OBJECT_ID('qa.AuditLog') AND permission_name IN ('UPDATE','DELETE')" | tr -d '[:space:]')
[ "$a" = "0" ] || fail "audit log is not append-only"; echo "   schema-wide grants removed; audit log append-only"
rm -f "$TMP.15.sql"
$Q -Q "IF DB_ID('QRUP') IS NOT NULL BEGIN ALTER DATABASE QRUP SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE QRUP; END; IF SUSER_ID('qrup_api') IS NOT NULL DROP LOGIN qrup_api;" >/dev/null
echo "Upgrade path complete."
