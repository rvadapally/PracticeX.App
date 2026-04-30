-- Slice 16.6: Portfolio Intelligence Brief — stage 3 of the LLM pipeline.
-- Per-tenant rollup synthesized from all per-document briefs into a single
-- executive summary. One row per tenant; latest generation wins (upsert).
-- Idempotent.

CREATE TABLE IF NOT EXISTS doc.portfolio_briefs (
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
