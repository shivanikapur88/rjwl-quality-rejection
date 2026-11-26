# API request / response examples

Field names below were produced by serialising the real DTO classes (`Quality.Core/Dtos`), so they match the code. Values are illustrative.
JSON is camelCase. Dates are UTC (`...Z`). Base URL: `https://<server>:<port>`. All endpoints except `login` and `health` need `Authorization: Bearer <token>`.
Errors use RFC 7807 problem JSON with an extra `code` field, for example:

```json
{ "status": 409, "title": "The selected rejection phenomenon is no longer active. Refresh the list and choose again.", "code": "phenomenon_inactive" }
```

| HTTP | Meaning | Typical `code` values |
|---|---|---|
| 400 | Validation / wrong selection | `quantity_invalid`, `stage_not_in_shop`, `machine_not_in_shop`, `phenomenon_not_found`, `phenomenon_not_in_stage`, `remarks_too_long`, `submission_id_required`, `name_invalid`, `credentials_required` |
| 401 | Not signed in / bad login | `invalid_credentials`, `token_expired`, `unauthenticated` |
| 403 | Role or shop not allowed | `forbidden`, `shop_not_assigned` |
| 404 | Not found | `shop_not_found`, `entry_not_found`, `phenomenon_not_found` |
| 409 | Conflict | `phenomenon_inactive`, `duplicate_name`, `duplicate_code`, `duplicate_user`, `phenomenon_in_use`, `machine_in_use`, `last_admin`, `cannot_deactivate_self`, `cannot_change_own_role`, `submission_id_in_use`, `submission_payload_mismatch`, `concurrent_edit` |
| 429 | Too many login attempts | |
| 503 | Database unavailable | `database_unavailable`, `store_not_configured` |

## Endpoints

| Method and path | Role | Notes |
|---|---|---|
| GET `/api/health` | anonymous | `{ "status": "ok" }` |
| POST `/api/auth/login` | anonymous | rate limited (10/min) |
| GET `/api/shops` | any signed-in | inspectors/supervisors: assigned shops only |
| GET `/api/shops/{shopId}/stages` | any signed-in | stages mapped to the shop |
| GET `/api/shops/{shopId}/stages/{stageId}/machines` | any signed-in | active machines of that shop |
| GET `/api/shops/{shopId}/stages/{stageId}/phenomena` | any signed-in | **active** phenomena of exactly that stage |
| POST `/api/rejection-entries` | QualityInspector | 201 new, 200 if the same `submissionId` was already saved |
| GET `/api/rejection-entries?shopId&stageId&fromUtc&toUtc&limit` | any signed-in | inspector: own; supervisor: assigned shops; admin: all |
| GET `/api/rejection-entries/{shopId}/{id}` | any signed-in | ids are unique per shop database, so the shop is in the route |
| GET `/api/admin/phenomena?shopId&stageId&isActive&search` | Administrator | active and inactive |
| POST `/api/admin/phenomena` | Administrator | 201 |
| PUT `/api/admin/phenomena/{id}` | Administrator | rename |
| PATCH `/api/admin/phenomena/{id}/status` | Administrator | activate / deactivate |
| DELETE `/api/admin/phenomena/{id}` | Administrator | 204, or 409 `phenomenon_in_use` |
| GET `/api/admin/machines?shopId&isActive&search` | Administrator | active and inactive |
| POST `/api/admin/machines` | Administrator | 201 |
| PUT `/api/admin/machines/{id}` | Administrator | change code and name |
| PATCH `/api/admin/machines/{id}/status` | Administrator | activate / deactivate |
| DELETE `/api/admin/machines/{id}` | Administrator | 204, or 409 `machine_in_use` |
| GET `/api/admin/users?search&isActive` | Administrator | never returns passwords or hashes |
| POST `/api/admin/users` | Administrator | 201 |
| PUT `/api/admin/users/{id}` | Administrator | name, role, shops |
| PATCH `/api/admin/users/{id}/status` | Administrator | activate / deactivate (no hard delete) |
| POST `/api/admin/users/{id}/reset-password` | Administrator | 204 |

(The shop id is part of the stage/machine/phenomena routes because the server must verify the stage belongs to the shop; this refines the paths in the original brief.)

## Login
`POST /api/auth/login`
```json
{ "username": "E002", "password": "<password>" }
```
200:
```json
{ "token": "<jwt>", "expiresAtUtc": "2026-10-19T14:00:00Z", "userId": 2, "employeeCode": "E002", "displayName": "Inspector One", "role": "QualityInspector" }
```

## Workflow lists
`GET /api/shops` → `[ { "id": 1, "shopCode": "GDC", "shopName": "GDC Shop" } ]`

`GET /api/shops` (administrator; inspectors see only their assigned shops)
```json
[ { "id": 1, "shopCode": "GDC",   "shopName": "GDC Shop" },
  { "id": 2, "shopCode": "CNC",   "shopName": "CNC Shop" },
  { "id": 3, "shopCode": "PRE",   "shopName": "Pre-treatment Inspection" },
  { "id": 4, "shopCode": "FINAL", "shopName": "FINAL Inspection" } ]
```
`GET /api/shops/3/stages` → each shop has exactly one stage: `[ { "id": 3, "stageCode": "PRE_TREATMENT", "stageName": "Pre-treatment Inspection" } ]`

`GET /api/shops/1/stages/1/machines` → `[ { "id": 1, "machineCode": "DEV-GDC-01", "machineName": "PLACEHOLDER GDC machine 1" } ]`

`GET /api/shops/1/stages/1/phenomena` → `[ { "id": 1, "phenomenonCode": "GDC_SHOP-001", "phenomenonName": "Bore B/H" }, ... ]` (25 items for shop 1 / GDC Shop stage with the seeded data; 52 for CNC; 34 each for Pre-treatment and FINAL)

## Save a rejection entry
`POST /api/rejection-entries` (no inspector or timestamp is accepted from the client)
```json
{ "submissionId": "3f2b8c1e-6d4a-4b7e-9a21-5c0d8e7f1a42", "shopId": 1, "stageId": 1, "machineId": 1, "phenomenonId": 5, "quantity": 12, "remarks": "left side" }
```
201 (or 200 with `"alreadySubmitted": true` on a retry of the same `submissionId`):
```json
{
  "entry": {
    "id": 101, "submissionId": "3f2b8c1e-6d4a-4b7e-9a21-5c0d8e7f1a42",
    "shopId": 1, "shopCode": "GDC", "stageId": 1, "stageCode": "GDC_SHOP", "stageName": "GDC Shop",
    "machineId": 1, "machineCode": "DEV-GDC-01", "machineName": "PLACEHOLDER GDC machine 1",
    "phenomenonId": 5, "phenomenonName": "Dent", "quantity": 12,
    "inspectorId": 2, "inspectorEmployeeCode": "E002", "remarks": "left side",
    "serverCreatedAtUtc": "2026-10-19T06:42:10.123Z"
  },
  "alreadySubmitted": false
}
```
`phenomenonName` is the snapshot taken at save time; later renames or deactivation never change it.

## Administrator
`POST /api/admin/phenomena`
```json
{ "shopId": 1, "stageId": 1, "name": "Test Defect", "isActive": true }
```
201:
```json
{ "id": 26, "phenomenonCode": "GDC_SHOP-026", "phenomenonName": "Test Defect", "stageId": 1, "stageCode": "GDC_SHOP", "stageName": "GDC Shop",
  "shopCodes": ["GDC"], "isActive": true, "createdBy": 1, "createdAtUtc": "2026-10-19T06:42:10.123Z", "modifiedBy": null, "modifiedAtUtc": null }
```
`PUT /api/admin/phenomena/26` → `{ "name": "Test Defect 2" }`  ·  `PATCH /api/admin/phenomena/26/status` → `{ "isActive": false }`

Duplicate name (409): `{ "status": 409, "title": "'Dent' already exists for CNC Shop.", "code": "duplicate_name" }`

## Machines (administrator)
`POST /api/admin/machines`
```json
{ "shopId": 3, "machineCode": "PRE-X1", "machineName": "Dip tank 1", "isActive": true }
```
201:
```json
{ "id": 9, "machineCode": "PRE-X1", "machineName": "Dip tank 1", "shopId": 3, "shopCode": "PRE", "shopName": "Pre-treatment Inspection", "isActive": true }
```
`PUT /api/admin/machines/9` → `{ "machineCode": "PRE-X1A", "machineName": "Dip tank 1 (renamed)" }` · `PATCH .../status` → `{ "isActive": false }`
Duplicate code in the same shop (409): `{ "status": 409, "title": "Machine code 'PRE-X1' already exists in Pre-treatment Inspection.", "code": "duplicate_code" }`
Used machine (409 on DELETE): `{ "status": 409, "title": "This machine is used in saved rejection entries and cannot be deleted. Deactivate it instead.", "code": "machine_in_use" }`
Machine code rules: 1-30 characters, letters, digits, space and `. _ / -`; unique per shop (case-insensitive). Past entries keep the machine code and name they were saved with.

## Users (administrator)
`POST /api/admin/users`
```json
{ "employeeCode": "E2001", "username": "pre.insp", "displayName": "Pre Inspector", "role": "QualityInspector", "password": "<password, min 10 chars>", "shopIds": [3, 4] }
```
201 (no password or hash is ever returned):
```json
{ "id": 7, "employeeCode": "E2001", "username": "pre.insp", "displayName": "Pre Inspector", "role": "QualityInspector",
  "shopIds": [3, 4], "shopCodes": ["PRE", "FINAL"], "isActive": true, "createdAtUtc": "2026-10-19T06:42:10.123Z" }
```
`PUT /api/admin/users/7` → `{ "displayName": "Pre Inspector", "role": "Supervisor", "shopIds": [3] }`
`POST /api/admin/users/7/reset-password` → `{ "newPassword": "<new password, min 10 chars>" }` (204)
Roles: `Administrator` (sees all shops; `shopIds` is empty), `QualityInspector`, `Supervisor` (both need at least one shop).
Refusals (409): `duplicate_user` (the employee code or username is already used as either), `last_admin`, `cannot_deactivate_self`, `cannot_change_own_role`.
