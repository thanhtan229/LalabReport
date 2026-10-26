# Cloud production deploy — 27/10/2026

## Scope

- Worker: `lalab-cloud-worker`
- Domain: `https://lalab.tinix.io.vn`
- D1: `lalab-db` (`ab8e3864-3544-4d89-85cb-0afe9ce1f615`)
- R2: `lalab-media`

## Deployed

- Worker version: `9226ad9a-9ef0-408e-aa4a-7c4c23ae6917` (deployed 2026-10-26T23:20:39Z).
- Previous version (rollback candidate): `2dfcf9f4-d461-4a51-91fe-71e45d7164e9`.
- D1 migration `0008_bill_label_snapshots.sql` applied (adds `cloud_bills.phone_snapshot`, `shipping_address_snapshot`, `note`).
- Reason for 0008: production had applied `0007_order_gallery.sql` before the three bill-column ALTERs were added, so 0007 was already recorded while the columns were missing. 0007 now contains gallery schema only; 0008 owns the bill columns for fresh and existing databases.

## Pre-deploy safety

- D1 export (schema + data): `artifacts/cloud-d1-backup-20261027.sql` (80,286 bytes, local only; `artifacts/` is gitignored).
- Worker tests: `npx tsc --noEmit` clean; `npm test` 94 passed.
- .NET solution on .NET 10: build 0 errors; full suite 858 passed (commit `c274598`).

## Verification

- `GET /api/health` → 200.
- `GET /` → 200, PWA contains gallery and label UI.
- `GET /api/sync/gallery/jobs` without sync secret → 403 (route present).
- `POST /api/orders/1/gallery/requests` without session → 401 (route present).
- D1 schema: `cloud_gallery_manifest/jobs/media` present; `cloud_bills` has `phone_snapshot`, `shipping_address_snapshot`, `note`; migrations 0007 and 0008 recorded.
- No secret values printed; existing `JWT_SECRET`, `SYNC_SECRET`, `ADMIN_PIN`, `STAFF_PIN` retained.

## Rollback

- Worker: redeploy previous version via Wrangler deployment/rollback (`2dfcf9f4-d461-4a51-91fe-71e45d7164e9`).
- D1: 0008 is additive; rolling back the worker does not require dropping columns. If data recovery is needed, restore from the D1 export above (D1 Time Travel may also be used) — do not drop tables.
