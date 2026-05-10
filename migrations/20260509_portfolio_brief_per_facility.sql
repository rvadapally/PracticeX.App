-- Per-facility Practice Intelligence Brief.
--
-- Today portfolio_briefs is keyed on tenant_id alone, so when a viewer
-- switches the facility filter the brief still shows the dominant-facility
-- synthesis. Add facility_id to the PK; use the sentinel
-- 00000000-0000-0000-0000-000000000000 to mean "all facilities" (the
-- previous tenant-wide scope) so existing rows survive untouched.
--
-- Idempotent.

ALTER TABLE doc.portfolio_briefs
  ADD COLUMN IF NOT EXISTS facility_id uuid NOT NULL
    DEFAULT '00000000-0000-0000-0000-000000000000';

ALTER TABLE doc.portfolio_briefs DROP CONSTRAINT IF EXISTS portfolio_briefs_pkey;
ALTER TABLE doc.portfolio_briefs ADD CONSTRAINT portfolio_briefs_pkey
  PRIMARY KEY (tenant_id, facility_id);
