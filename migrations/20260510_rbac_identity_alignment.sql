-- Slice 21 follow-up: align RBAC identities with the live access list.
--
-- Fixes:
--   * adds the fourth canonical role: facility_admin
--   * provisions Raghu's allowed super-admin emails
--   * moves Parag to dr8382@gmail.com and grants org_admin on Eagle
--   * provisions Ashutosh + Sourabh as Synexar facility_user accounts
--
-- Assumes 20260509_rbac_phase1.sql and 20260510_tenant_split.sql have
-- already run. Safe to re-run.

BEGIN;

-- ---------------------------------------------------------------
-- 1. Canonical role set.
-- ---------------------------------------------------------------
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
   now()),
  ('a0000001-0000-0000-0000-000000000004'::uuid,
   '11111111-1111-1111-1111-111111111111'::uuid,
   'facility_admin',
   '{"description":"Facility-scoped administrator limited to assigned facilities."}'::jsonb,
   now())
ON CONFLICT (id) DO UPDATE
SET tenant_id = EXCLUDED.tenant_id,
    name = EXCLUDED.name,
    permissions = EXCLUDED.permissions;

-- ---------------------------------------------------------------
-- 2. Super-admin identities for Raghu.
-- ---------------------------------------------------------------
INSERT INTO org.users (id, tenant_id, email, name, status, is_super_admin, created_at)
VALUES
  ('22222222-2222-2222-2222-222222222222'::uuid,
   '11111111-1111-1111-1111-111111111111'::uuid,
   'rvadapally@practicex.ai',
   'Raghuram Vadapally',
   'active',
   true,
   now()),
  ('a0000002-0000-0000-0000-000000000010'::uuid,
   '11111111-1111-1111-1111-111111111111'::uuid,
   'rvadapally@gmail.com',
   'Raghuram Vadapally',
   'active',
   true,
   now()),
  ('a0000002-0000-0000-0000-000000000011'::uuid,
   '11111111-1111-1111-1111-111111111111'::uuid,
   'rvadapally@synexar.ai',
   'Raghuram Vadapally',
   'active',
   true,
   now())
ON CONFLICT (id) DO UPDATE
SET tenant_id = EXCLUDED.tenant_id,
    email = EXCLUDED.email,
    name = EXCLUDED.name,
    status = EXCLUDED.status,
    is_super_admin = EXCLUDED.is_super_admin,
    updated_at = now();

UPDATE org.users
SET tenant_id = '11111111-1111-1111-1111-111111111111'::uuid,
    name = 'Raghuram Vadapally',
    status = 'active',
    is_super_admin = true,
    updated_at = now()
WHERE email IN ('rvadapally@practicex.ai', 'rvadapally@gmail.com', 'rvadapally@synexar.ai');

-- ---------------------------------------------------------------
-- 3. Parag is the Eagle org admin under his Gmail identity.
-- ---------------------------------------------------------------
INSERT INTO org.users (id, tenant_id, email, name, status, is_super_admin, created_at)
VALUES (
  'a0000002-0000-0000-0000-000000000001'::uuid,
  'e1111111-1111-1111-1111-111111111111'::uuid,
  'dr8382@gmail.com',
  'Dr. Parag',
  'active',
  false,
  now()
)
ON CONFLICT (id) DO UPDATE
SET tenant_id = EXCLUDED.tenant_id,
    email = EXCLUDED.email,
    name = EXCLUDED.name,
    status = EXCLUDED.status,
    is_super_admin = false,
    updated_at = now();

UPDATE org.users
SET tenant_id = 'e1111111-1111-1111-1111-111111111111'::uuid,
    email = 'dr8382@gmail.com',
    name = 'Dr. Parag',
    status = 'active',
    is_super_admin = false,
    updated_at = now()
WHERE id = 'a0000002-0000-0000-0000-000000000001'::uuid
   OR email = 'parag@eaglephysicians.com';

INSERT INTO org.role_assignments (id, tenant_id, user_id, facility_id, role_id, status, created_at)
VALUES (
  'a0000003-0000-0000-0000-000000000001'::uuid,
  'e1111111-1111-1111-1111-111111111111'::uuid,
  'a0000002-0000-0000-0000-000000000001'::uuid,
  null,
  'a0000001-0000-0000-0000-000000000002'::uuid,
  'active',
  now()
)
ON CONFLICT (id) DO UPDATE
SET tenant_id = EXCLUDED.tenant_id,
    user_id = EXCLUDED.user_id,
    facility_id = EXCLUDED.facility_id,
    role_id = EXCLUDED.role_id,
    status = EXCLUDED.status,
    updated_at = now();

-- ---------------------------------------------------------------
-- 4. Synexar facility users.
-- ---------------------------------------------------------------
INSERT INTO org.users (id, tenant_id, email, name, status, is_super_admin, created_at)
VALUES
  ('a0000002-0000-0000-0000-000000000002'::uuid,
   '51111111-1111-1111-1111-111111111111'::uuid,
   'agupta@synexar.ai',
   'Ashutosh Gupta',
   'active',
   false,
   now()),
  ('a0000002-0000-0000-0000-000000000003'::uuid,
   '51111111-1111-1111-1111-111111111111'::uuid,
   'sourabh.sanghi@gmail.com',
   'Sourabh Sanghi',
   'active',
   false,
   now())
ON CONFLICT (id) DO UPDATE
SET tenant_id = EXCLUDED.tenant_id,
    email = EXCLUDED.email,
    name = EXCLUDED.name,
    status = EXCLUDED.status,
    is_super_admin = false,
    updated_at = now();

INSERT INTO org.role_assignments (id, tenant_id, user_id, facility_id, role_id, status, created_at)
VALUES
  ('a0000003-0000-0000-0000-000000000002'::uuid,
   '51111111-1111-1111-1111-111111111111'::uuid,
   'a0000002-0000-0000-0000-000000000002'::uuid,
   '33333333-3333-3333-3333-333333333333'::uuid,
   'a0000001-0000-0000-0000-000000000003'::uuid,
   'active',
   now()),
  ('a0000003-0000-0000-0000-000000000003'::uuid,
   '51111111-1111-1111-1111-111111111111'::uuid,
   'a0000002-0000-0000-0000-000000000003'::uuid,
   '33333333-3333-3333-3333-333333333333'::uuid,
   'a0000001-0000-0000-0000-000000000003'::uuid,
   'active',
   now())
ON CONFLICT (id) DO UPDATE
SET tenant_id = EXCLUDED.tenant_id,
    user_id = EXCLUDED.user_id,
    facility_id = EXCLUDED.facility_id,
    role_id = EXCLUDED.role_id,
    status = EXCLUDED.status,
    updated_at = now();

COMMIT;
