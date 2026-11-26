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
echo "2. run 07 (quantity) and 09 (indexes)"
$Q -d QRUP -i $DB_DIR/07_migration_add_quantity.sql >/dev/null; $Q -d QRUP -i $DB_DIR/09_upgrade_entries_indexes.sql >/dev/null
$Q -d QRUP -h -1 -Q "SET NOCOUNT ON; SELECT '   old entry quantity = ' + CAST(Quantity AS varchar) FROM qa.QualityRejectionEntries"
echo "3. run 08 with the collision present -> must stop and change nothing"
if $Q -d QRUP -i $DB_DIR/08_upgrade_master_review_fixes.sql > "$TMP" 2>&1; then fail "08 succeeded despite the collision"; else grep -E "Msg 50080|E100" "$TMP" | head -3 | sed 's/^/   /'; fi
$Q -d QRUP -h -1 -Q "SET NOCOUNT ON; SELECT '   LoginIdentifiers rows after refused run: ' + CAST(COUNT(*) AS varchar) FROM qa.LoginIdentifiers"
echo "4. resolve the collision (rename one account) and re-run 08"
$Q -d QRUP -Q "UPDATE qa.Users SET Username = 'clash.user' WHERE EmployeeCode = 'E200';" >/dev/null
$Q -d QRUP -i $DB_DIR/08_upgrade_master_review_fixes.sql >/dev/null && echo "   08 ok"
$Q -d QRUP -h -1 -Q "SET NOCOUNT ON; SELECT '   identifiers: ' + STRING_AGG(NormalizedIdentifier + ':' + Kind, ', ') WITHIN GROUP (ORDER BY NormalizedIdentifier) FROM qa.LoginIdentifiers; SELECT '   TokenVersion for all = ' + CAST(MAX(TokenVersion) AS varchar) FROM qa.Users"
echo "5. re-run 08 again (idempotent)"; $Q -d QRUP -i $DB_DIR/08_upgrade_master_review_fixes.sql >/dev/null && echo "   ok"
echo "6. trigger: deactivating the only admin is refused"
$Q -d QRUP -Q "UPDATE qa.Users SET IsActive = 0 WHERE Username = 'admin1'" 2>&1 | grep -o "At least one active administrator must remain" | sed 's/^/   /'
echo "7. round 2/3 upgrades on the same database: 11, 12 (backfills the old entry), 13; then all three again"
for f in 11_upgrade_master_shifts_corrections 12_upgrade_entries_shift_reversal 13_upgrade_master_production_counts; do $Q -d QRUP -i $DB_DIR/$f.sql >/dev/null; done
for f in 11_upgrade_master_shifts_corrections 12_upgrade_entries_shift_reversal 13_upgrade_master_production_counts; do $Q -d QRUP -i $DB_DIR/$f.sql >/dev/null; done
n=$($Q -d QRUP -h -1 -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM qa.QualityRejectionEntries WHERE ProductionDate IS NULL OR ShiftCode IS NULL" | tr -d '[:space:]')
[ "$n" = "0" ] || fail "entries without production day after 12: $n"; echo "   old entry has a production day and shift"
$Q -d QRUP -h -1 -Q "SET NOCOUNT ON; IF OBJECT_ID('qa.ProductionCounts') IS NULL THROW 50000, 'no ProductionCounts', 1; SELECT '   production counts table present'"
echo "Upgrade path complete."
