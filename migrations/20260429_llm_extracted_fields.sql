-- Slice 13: LLM-refined field extraction. Stored alongside the regex output
-- so we can A/B and fall back when the LLM response can't be parsed. Idempotent.

ALTER TABLE doc.document_assets
  ADD COLUMN IF NOT EXISTS llm_extracted_fields_json jsonb,
  ADD COLUMN IF NOT EXISTS llm_extractor_model       varchar(120),
  ADD COLUMN IF NOT EXISTS llm_extracted_at          timestamptz,
  ADD COLUMN IF NOT EXISTS llm_tokens_in             int,
  ADD COLUMN IF NOT EXISTS llm_tokens_out            int,
  ADD COLUMN IF NOT EXISTS llm_extraction_status     varchar(40);

CREATE INDEX IF NOT EXISTS ix_document_assets_llm_extraction_status
  ON doc.document_assets (tenant_id, llm_extraction_status)
  WHERE llm_extraction_status IS NOT NULL;
