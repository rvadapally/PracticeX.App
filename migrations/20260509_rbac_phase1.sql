-- Slice 21 (Phase 1): RBAC + facility-level isolation.
--
-- Adds super_admin / org_admin / facility_user role primitives. Hierarchy:
--   SuperAdmin   → unrestricted across all tenants + facilities
--   OrgAdmin     → all facilities within their home tenant
--   FacilityUser → only facilities listed in their role_assignments
--
-- This is the precondition for Phase 2 (full tenant split). For now the
-- isolation primitive is facility_id; tenant remains the umbrella PracticeX
-- demo tenant until the split lands.
--
-- Idempotent.

-- 1. Super-admin flag on users.
ALTER TABLE org.users
  ADD COLUMN IF NOT EXISTS is_super_admin boolean NOT NULL DEFAULT false;

-- 2. Standard role names. Permissions json describes intent; enforcement
--    lives in code keyed off the role name token (super_admin / org_admin
--    / facility_user). Codes are stable and case-sensitive.
INSERT INTO org.roles (id, tenant_id, name, permissions, created_at)
VALUES
  ('a0000001-0000-0000-0000-000000000001'::uuid,
   '11111111-1111-1111-1111-111111111111'::uuid,
   'super_admin',
   '{"description":"Cross-tenant administrator. Bypasses all access checks."}'::jsonb,
   now()),
  ('a0000001-0000-0000-0000-000000000002'::uuid,
   '11111111-1111-1111-1111-111111111111'::uuid,
   'org_admin',
   '{"description":"All facilities within the home tenant."}'::jsonb,
   now()),
  ('a0000001-0000-0000-0000-000000000003'::uuid,
   '11111111-1111-1111-1111-111111111111'::uuid,
   'facility_user',
   '{"description":"Specific facility (or facilities) listed in role_assignments."}'::jsonb,
   now())
ON CONFLICT (id) DO NOTHING;

-- 3. Promote Raghu to super-admin.
UPDATE org.users
SET is_super_admin = true,
    updated_at = now()
WHERE email IN ('rvadapally@practicex.ai', 'rvadapally@synexar.ai');

-- 4. Seed Parag user, scoped to Eagle facility only.
--    Email matches the Cloudflare Access whitelist entry that will land in
--    Cf-Access-Authenticated-User-Email when he logs in.
INSERT INTO org.users (id, tenant_id, email, name, status, is_super_admin, created_at)
VALUES (
  'a0000002-0000-0000-0000-000000000001'::uuid,
  '11111111-1111-1111-1111-111111111111'::uuid,
  'parag@eaglephysicians.com',
  'Parag Brahmbhatt',
  'active',
  false,
  now()
)
ON CONFLICT (id) DO UPDATE
SET name = EXCLUDED.name,
    status = EXCLUDED.status,
    updated_at = now();

-- 5. Grant Parag facility_user on Eagle Physicians of Greensboro.
INSERT INTO org.role_assignments (id, tenant_id, user_id, facility_id, role_id, status, created_at)
VALUES (
  'a0000003-0000-0000-0000-000000000001'::uuid,
  '11111111-1111-1111-1111-111111111111'::uuid,
  'a0000002-0000-0000-0000-000000000001'::uuid,
  '22222222-2222-2222-2222-222222222222'::uuid,  -- Eagle Physicians of Greensboro
  'a0000001-0000-0000-0000-000000000003'::uuid,  -- facility_user role
  'active',
  now()
)
ON CONFLICT (id) DO NOTHING;

-- 6. Index for fast access-set lookups (per user, active assignments).
CREATE INDEX IF NOT EXISTS ix_role_assignments_user_active
  ON org.role_assignments (user_id, status)
  WHERE status = 'active';
