# Excel import report

Source file: `Rejection_Phinomina.xlsx` - sheet `Sheet1` (A1:D54)

| Stage | Excel header | Raw rows | Imported | Skipped (duplicate) |
|---|---|---:|---:|---:|
| GDC Shop | GDC SHOP | 25 | 25 | 0 |
| CNC Shop | CNC SHOP | 53 | 52 | 1 |
| Pre-treatment Inspection | Pre-treatment Inspection | 34 | 34 | 0 |
| FINAL Inspection | FINAL Inspection | 34 | 34 | 0 |
| **Total** | | **146** | **145** | **1** |

## Skipped duplicates (reported before deduplication, per approved policy)

- CNC_SHOP: `DENT` at B42 duplicates B8 (not imported)

## Names containing extra whitespace (original text preserved in DB)

- `LUG HEIGHT  O/S /UNCLEAN`: CNC_SHOP!B31, PRE_TREATMENT!C12, FINAL!D12
