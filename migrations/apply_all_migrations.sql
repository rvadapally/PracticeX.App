-- =============================================================================
-- PracticeX: Apply All Migrations (Consolidated — Idempotent)
-- =============================================================================
-- Run this script to bring any PracticeX database fully up to date from any
-- point in the migration history. Every statement uses IF NOT EXISTS / ADD
-- COLUMN IF NOT EXISTS so this is safe to run repeatedly.
--
-- Usage:
--   psql "postgres://postgres:postgres@localhost:5432/practicex" \
--     -f migrations/apply_all_migrations.sql
--
-- Migration order (scripts applied inline below):
--   1. practicex_initial_enterprise_foundation.sql
--   2. 20260425_source_discovery_extensions.sql
--   3. 20260426_manifest_phase_extensions.sql
--   4. 20260427_doc_intel_layout.sql
--   5. 20260427_complexity_profiling.sql
--   6. 20260428_extracted_fields.sql
--   7. 20260429_extracted_full_text.sql
--   8. 20260429_llm_extracted_fields.sql
--   9. 20260430_llm_narrative_brief.sql
--  10. 20260430_portfolio_brief.sql
-- =============================================================================

\echo '==> Step 1: Initial enterprise foundation'
\i migrations/practicex_initial_enterprise_foundation.sql

\echo '==> Step 2: Source discovery extensions'
\i migrations/20260425_source_discovery_extensions.sql

\echo '==> Step 3: Manifest phase extensions'
\i migrations/20260426_manifest_phase_extensions.sql

\echo '==> Step 4: Doc Intel layout columns'
\i migrations/20260427_doc_intel_layout.sql

\echo '==> Step 5: Complexity profiling columns'
\i migrations/20260427_complexity_profiling.sql

\echo '==> Step 6: Extracted fields columns'
\i migrations/20260428_extracted_fields.sql

\echo '==> Step 7: Extracted full text column'
\i migrations/20260429_extracted_full_text.sql

\echo '==> Step 8: LLM extracted fields columns'
\i migrations/20260429_llm_extracted_fields.sql

\echo '==> Step 9: LLM narrative brief columns'
\i migrations/20260430_llm_narrative_brief.sql

\echo '==> Step 10: Portfolio briefs table'
\i migrations/20260430_portfolio_brief.sql

\echo '==> All migrations applied successfully.'
