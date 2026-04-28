-- Slice 7: Azure Document Intelligence layout persistence.
--
-- Adds columns to doc.document_assets so layout output (text + structure +
-- tables + key-value pairs) from a Doc Intel pass can be stored alongside the
-- asset. layout_json is the canonical surface that downstream extractors read
-- when local PdfPig/DocX text extraction was insufficient (scanned PDFs, etc).
--
-- Idempotent — safe to re-run.

ALTER TABLE doc.document_assets
  ADD COLUMN IF NOT EXISTS layout_json         jsonb,
  ADD COLUMN IF NOT EXISTS layout_provider     varchar(40),
  ADD COLUMN IF NOT EXISTS layout_model        varchar(80),
  ADD COLUMN IF NOT EXISTS layout_extracted_at timestamptz,
  ADD COLUMN IF NOT EXISTS layout_page_count   int;

CREATE INDEX IF NOT EXISTS ix_document_assets_tenant_layout_provider
  ON doc.document_assets (tenant_id, layout_provider)
  WHERE layout_provider IS NOT NULL;
