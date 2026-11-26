#!/usr/bin/env bash
# Creates three EMPTY test databases on a SQL Server you control and runs the db/ scripts in the documented
# fresh-install order (docs/DATABASE_RUNBOOK.md). Used by the integration tests. NEVER point this at production.
#
#   SQLCMD="sqlcmd -S localhost,14333 -U sa -P '...' -C" tools/create-test-databases.sh [prefix] [rcsi]
#
# prefix  database name prefix (default QRTEST)  ->  <prefix>_Master, <prefix>_Gdc, <prefix>_Cnc
# rcsi    ON|OFF: READ_COMMITTED_SNAPSHOT on all three (default ON - the harder case for the locking fixes)
# The SQLCMD variable must run sqlcmd (v18 tools) and accept -d <db> -i <file> -b.
set -euo pipefail
PREFIX="${1:-QRTEST}"; RCSI="${2:-ON}"
DB_DIR="$(cd "$(dirname "$0")/../db" && pwd)"
: "${SQLCMD:?Set SQLCMD to the sqlcmd command line for the test server}"

run()  { eval "$SQLCMD" -b -d "$1" -i "$2" > /dev/null; echo "  ok  $1 <- $(basename "$2")"; }
q()    { eval "$SQLCMD" -b -Q "\"$1\"" > /dev/null; }

for db in Master Gdc Cnc; do
  name="${PREFIX}_${db}"
  q "IF DB_ID('$name') IS NOT NULL BEGIN ALTER DATABASE [$name] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$name]; END; CREATE DATABASE [$name]; ALTER DATABASE [$name] SET READ_COMMITTED_SNAPSHOT $RCSI;"
done
echo "Created ${PREFIX}_Master, ${PREFIX}_Gdc, ${PREFIX}_Cnc (READ_COMMITTED_SNAPSHOT $RCSI)"

M="${PREFIX}_Master"
run "$M" "$DB_DIR/01_master_schema.sql"
run "$M" "$DB_DIR/02c_entries_schema_PRE_FINAL.sql"      # PRE and FINAL entries live in the master database
run "$M" "$DB_DIR/03_seed_reference.sql"
run "$M" "$DB_DIR/04_seed_phenomena.sql"
run "$M" "$DB_DIR/08_upgrade_master_review_fixes.sql"
run "$M" "$DB_DIR/09_upgrade_entries_indexes.sql"
run "$M" "$DB_DIR/11_upgrade_master_shifts_corrections.sql"
run "$M" "$DB_DIR/12_upgrade_entries_shift_reversal.sql"
for db in Gdc Cnc; do
  if [ "$db" = Gdc ]; then run "${PREFIX}_Gdc" "$DB_DIR/02a_entries_schema_GDC_AndonDB.sql"; else run "${PREFIX}_Cnc" "$DB_DIR/02b_entries_schema_MachineShopDB.sql"; fi
  run "${PREFIX}_$db" "$DB_DIR/09_upgrade_entries_indexes.sql"
  run "${PREFIX}_$db" "$DB_DIR/12_upgrade_entries_shift_reversal.sql"
done
# Re-run everything that claims to be idempotent: must succeed and change nothing.
for f in 01_master_schema 03_seed_reference 04_seed_phenomena 08_upgrade_master_review_fixes 09_upgrade_entries_indexes 11_upgrade_master_shifts_corrections 12_upgrade_entries_shift_reversal; do run "$M" "$DB_DIR/$f.sql"; done
echo "Fresh install + idempotent re-run complete."
