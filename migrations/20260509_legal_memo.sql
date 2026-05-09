-- Slice 20: Legal Advisor Agent (premium, corporate-counsel posture).
--
-- A "Counsel's Memo" is the fourth pass on each document, distinct from the
-- Document Intelligence Brief. The brief describes what a contract says.
-- The memo takes an adversarial corporate-counsel posture: where are the
-- landmines, what would I redline, what would I demand, what risks rise to
-- material disclosure. Output: structured markdown memo + JSON issue list +
-- a 0-100 risk score for portfolio-level triage.
--
-- A "Counsel's Brief" is a per-tenant cross-document synthesis at the same
-- counsel posture (kept separate from portfolio_briefs, which is the
-- partner-friendly executive view).
--
-- Idempotent.

ALTER TABLE doc.document_assets
  ADD COLUMN IF NOT EXISTS legal_memo_md            text,
  ADD COLUMN IF NOT EXISTS legal_memo_json          jsonb,
  ADD COLUMN IF NOT EXISTS legal_memo_model         varchar(120),
  ADD COLUMN IF NOT EXISTS legal_memo_tokens_in     int,
  ADD COLUMN IF NOT EXISTS legal_memo_tokens_out    int,
  ADD COLUMN IF NOT EXISTS legal_memo_extracted_at  timestamptz,
  ADD COLUMN IF NOT EXISTS legal_memo_status        varchar(40),
  ADD COLUMN IF NOT EXISTS legal_memo_latency_ms    int,
  ADD COLUMN IF NOT EXISTS legal_memo_risk_score    numeric(5,2);

CREATE INDEX IF NOT EXISTS ix_document_assets_legal_memo_status
  ON doc.document_assets (tenant_id, legal_memo_status)
  WHERE legal_memo_status IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_document_assets_legal_memo_risk
  ON doc.document_assets (tenant_id, legal_memo_risk_score DESC NULLS LAST)
  WHERE legal_memo_risk_score IS NOT NULL;

CREATE TABLE IF NOT EXISTS doc.counsel_briefs (
    tenant_id        uuid        PRIMARY KEY,
    brief_md         text        NOT NULL,
    model            varchar(120),
    tokens_in        int,
    tokens_out       int,
    source_doc_count int         NOT NULL DEFAULT 0,
    latency_ms       int,
    generated_at     timestamptz NOT NULL DEFAULT now(),
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now()
);
