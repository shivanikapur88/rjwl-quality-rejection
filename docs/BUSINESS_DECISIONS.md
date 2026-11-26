# Decisions needed from RJWL

Work that does not depend on these answers is complete. Items marked ⚠ block go-live.

| # | Decision | Why it matters | Current state |
|---|---|---|---|
| 1 ⚠ | **Actual machine list per shop** (code, name, active) | Production reference data | Only `DEV-*` placeholders exist (development). Load the real list with `db/10_load_production_machines.sql` or the admin screen. |
| 2 ⚠ | **SQL Server instance(s) and database placement**; who owns `GDC_AndonDB` and `MachineShopDB` and approves adding schema `qa` there | Deployment, permissions, backup coordination | Scripts assume one instance with the three databases; `qa` and `qa.QualityRejectionEntries` must not already exist in the entry databases. |
| 3 ⚠ | **Android package id** (currently `com.yourcompany.qualityrejection`) and the **device estate** (models, Android versions, MDM?) | Cannot be changed after the first release; signing; compatibility testing | Placeholder id; minimum Android 7.0 (API 24). |
| 4 ⚠ | **Server host name and TLS certificate** (public CA, internal CA + network-security config, or MDM-installed CA) | Release builds validate certificates normally; Android does not trust user-installed CAs | Not configured. |
| 5 ⚠ | **App icon artwork** | Placeholder icon in the build | Placeholder. |
| 6 | **Historical quantity**: old entries migrated by 07 get quantity 1 | Rejection totals for old entries | Assumption; confirm or correct. |
| 7 | ~~How incorrect entries are corrected~~ | Data quality | **Decided 26 Nov 2026:** the inspector requests, a supervisor approves (reversal + optional replacement). Still open: is 30 days the right limit (`Corrections:MaxAgeDays`)? |
| 8 | Is one rejected part ever recorded under **several phenomena**? | Whether quantities count parts or defect occurrences | Not defined; affects any future totals. |
| 9 | Are **part number, batch or work order** fields needed? | Not in the current entry | Not in scope. |
| 10 | ~~Shift / production-date reporting~~ | | **Decided 26 Nov 2026:** A 06:00, B 14:00, C 22:00 IST. Still to confirm: these are the real times for all four shops (one table for all shops today), and whether the CNC 05:59 reset needs to match. |
| 11 | Is **offline entry** needed? | Changes storage, timing and synchronisation design | Online-only, as designed. Estimate separately if wanted. |
| 12 | **Retention and backup** of QualityMasterDB and the entry tables | Operations | Suggested minimum in [DATABASE_RUNBOOK.md §5](DATABASE_RUNBOOK.md). |
| 13 ⚠ | **Rejection rate**: what counts as "produced" (good + rejected? rework?), whether a part rejected for two phenomena counts once or twice, and the **source of production counts** (who uploads, or which production-system tables feed `db/14`, and the owners' approval) | The dashboard now computes rejected ÷ produced where counts exist (round 3) | Rate shown with its coverage and labelled "rejected ÷ produced"; rejections on a machine/day/shift with 0 produced are left outside the rate (they lower the coverage instead); below 80% coverage the dashboard warns. Confirm the rules before using it for decisions. |
| 14 | **MAUI 11 timing**: MAUI 10 leaves support before .NET 10 does (the earlier analysis gives 11 May 2027; verify current dates) | Supported mobile stack | App on MAUI 10.0.130; plan the move before that date. |
| 15 | Is a scripted user-creation tool (`Quality.UserSqlGen`) still wanted? | Its source was missing | Not rebuilt; the bootstrap admin plus in-app user management cover the documented needs. |
| 16 | Are **photos, exports, PLC integration, a web admin portal, an audit-viewing screen** wanted? | Outside the documented scope | None added. QR scanning and a read-only dashboard were added in round 2. |
| 17 | The **review PDF** (`RJWL-Andon-Review-With-Deep-Dive-2026-11-17.pdf`) | Q1-Q9 were worked from the brief's summary | Not provided in this session; check [FINDINGS_CLOSURE.md](FINDINGS_CLOSURE.md) against it. |
| 18 | **QR labels:** label stock and printer, who prints and replaces them | Shop-floor rollout | Labels print from `/dashboard/labels.html` on any printer; durable stock recommended. |
| 19 | Should users be able to **change their own password**, and must they change the administrator-set initial password at first sign-in? | Today administrators know every initial password and nothing forces a change (raised by the full review) | Not in the documented scope; administrators reset passwords. Small addition if wanted. |
