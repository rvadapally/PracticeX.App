-- Slice 21 (Phase 2): Tenant split.
--
-- Promotes Eagle Physicians of Greensboro and Synexar Inc from facilities
-- under the umbrella PracticeX tenant to their own first-class tenants.
-- Every facility-scoped row (assets, candidates, source_objects, ingestion_jobs,
-- portfolio_briefs, counsel_briefs) is moved to the appropriate new tenant
-- based on its facility_hint_id (or its document's candidate's facility_hint_id
-- when not directly facility-scoped).
--
-- New tenant ids:
--   e1111111-1111-1111-1111-111111111111  Eagle Physicians of Greensboro
--   51111111-1111-1111-1111-111111111111  Synexar Inc
--
-- The original umbrella tenant 11111111-1111-1111-1111-111111111111 (PracticeX)
-- becomes the platform tenant: holds super-admin user(s), shared role
-- definitions, and historical audit events. Source_connections and
-- ingestion_batches stay in the umbrella because they were created when
-- both facilities shared a tenant and don't have a clean retroactive split.
-- New ingestion goes through per-tenant connections in Phase 3.
--
-- Wrapped in a transaction so it's all-or-nothing. Idempotent on re-run
-- because every UPDATE is conditional and every INSERT uses ON CONFLICT.

BEGIN;

-- ---------------------------------------------------------------
-- 1. Create new tenant rows.
-- ---------------------------------------------------------------
INSERT INTO org.tenants (id, name, status, data_region, baa_status, created_at)
VALUES
  ('e1111111-1111-1111-1111-111111111111'::uuid,
   'Eagle Physicians of Greensboro', 'active', 'us', 'signed', now()),
  ('51111111-1111-1111-1111-111111111111'::uuid,
   'Synexar Inc', 'active', 'us', 'signed', now())
ON CONFLICT (id) DO NOTHING;

-- ---------------------------------------------------------------
-- 2. Move facility rows to their new tenants.
-- ---------------------------------------------------------------
UPDATE org.facilities
SET tenant_id = 'e1111111-1111-1111-1111-111111111111'::uuid,
    updated_at = now()
WHERE id = '22222222-2222-2222-2222-222222222222'::uuid
  AND tenant_id != 'e1111111-1111-1111-1111-111111111111'::uuid;

UPDATE org.facilities
SET tenant_id = '51111111-1111-1111-1111-111111111111'::uuid,
    updated_at = now()
WHERE id = '33333333-3333-3333-3333-333333333333'::uuid
  AND tenant_id != '51111111-1111-1111-1111-111111111111'::uuid;

-- ---------------------------------------------------------------
-- 3. Update document_candidates based on facility_hint_id.
-- ---------------------------------------------------------------
UPDATE doc.document_candidates
SET tenant_id = 'e1111111-1111-1111-1111-111111111111'::uuid,
    updated_at = now()
WHERE facility_hint_id = '22222222-2222-2222-2222-222222222222'::uuid
  AND tenant_id != 'e1111111-1111-1111-1111-111111111111'::uuid;

UPDATE doc.document_candidates
SET tenant_id = '51111111-1111-1111-1111-111111111111'::uuid,
    updated_at = now()
WHERE facility_hint_id = '33333333-3333-3333-3333-333333333333'::uuid
  AND tenant_id != '51111111-1111-1111-1111-111111111111'::uuid;

-- ---------------------------------------------------------------
-- 4. Update document_assets via candidate join (asset's tenant follows
--    its candidate's tenant). Each asset has 1+ candidate rows; pick the
--    most recent. In practice every asset has exactly 1 candidate today.
-- ---------------------------------------------------------------
UPDATE doc.document_assets a
SET tenant_id = c.tenant_id,
    updated_at = now()
FROM (
  SELECT DISTINCT ON (document_asset_id) document_asset_id, tenant_id
  FROM doc.document_candidates
  ORDER BY document_asset_id, created_at DESC
) c
WHERE c.document_asset_id = a.id
  AND a.tenant_id != c.tenant_id;

-- ---------------------------------------------------------------
-- 5. Update source_objects via document_assets.
-- ---------------------------------------------------------------
UPDATE doc.source_objects so
SET tenant_id = a.tenant_id,
    updated_at = now()
FROM doc.document_assets a
WHERE a.source_object_id = so.id
  AND so.tenant_id != a.tenant_id;

-- ---------------------------------------------------------------
-- 6. Update ingestion_jobs via document_assets.
-- ---------------------------------------------------------------
UPDATE doc.ingestion_jobs ij
SET tenant_id = a.tenant_id,
    updated_at = now()
FROM doc.document_assets a
WHERE ij.document_asset_id = a.id
  AND ij.tenant_id != a.tenant_id;

-- ---------------------------------------------------------------
-- 7. Update portfolio_briefs (already has facility_id).
--    The all-facilities sentinel row (facility_id = '00000000-...')
--    stays in umbrella — it's a cross-facility view that no per-tenant
--    user should see anyway after the split.
-- ---------------------------------------------------------------
UPDATE doc.portfolio_briefs pb
SET tenant_id = f.tenant_id,
    updated_at = now()
FROM org.facilities f
WHERE pb.facility_id = f.id
  AND pb.tenant_id != f.tenant_id;

-- ---------------------------------------------------------------
-- 8. counsel_briefs: today there is only 1 row (the Synexar synthesis
--    from the recent batch). Move it explicitly to Synexar.
-- ---------------------------------------------------------------
UPDATE doc.counsel_briefs
SET tenant_id = '51111111-1111-1111-1111-111111111111'::uuid,
    updated_at = now()
WHERE tenant_id = '11111111-1111-1111-1111-111111111111'::uuid;

-- ---------------------------------------------------------------
-- 9. Move Parag's user + role_assignment to Eagle tenant.
--    (Super-admin Raghu stays on the umbrella tenant — that is the
--    "platform" context.)
-- ---------------------------------------------------------------
UPDATE org.users
SET tenant_id = 'e1111111-1111-1111-1111-111111111111'::uuid,
    updated_at = now()
WHERE email = 'parag@eaglephysicians.com'
  AND tenant_id != 'e1111111-1111-1111-1111-111111111111'::uuid;

UPDATE org.role_assignments
SET tenant_id = 'e1111111-1111-1111-1111-111111111111'::uuid,
    updated_at = now()
WHERE user_id = (SELECT id FROM org.users WHERE email = 'parag@eaglephysicians.com')
  AND tenant_id != 'e1111111-1111-1111-1111-111111111111'::uuid;

-- ---------------------------------------------------------------
-- 10. Rename the umbrella tenant to make its new role explicit.
-- ---------------------------------------------------------------
UPDATE org.tenants
SET name = 'PracticeX Platform',
    updated_at = now()
WHERE id = '11111111-1111-1111-1111-111111111111'::uuid
  AND name = 'PracticeX';

COMMIT;
