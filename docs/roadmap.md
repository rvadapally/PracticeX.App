# PracticeX Command Center — Roadmap & Workflow

Single-source-of-truth document for "where are we, what's next, why does it matter."
Last updated: 2026-05-10 (Slice 21 Phase 1 — RBAC + facility isolation).

---

## Where we are today

Live demo URL: `https://app.practicex.ai/portfolio` (gated by Cloudflare Access — whitelist email entry).
Other surfaces: `/dashboard` (command center), `/renewals`, `/graph` (entity network), `/portfolio/{id}` (doc detail with citation anchors).

19 slices shipped end-to-end. Eagle GI's 18-document drop is the working test set. Everything below is **demo-ready** — pending the polish list at the bottom.

### What's actually working

- **Manifest-first ingestion** — score files by metadata before uploading bytes (browser flow + desktop agent spec).
- **Local + cloud text extraction** — PdfPig + OpenXml for digital docs, Azure Document Intelligence for scanned PDFs (BAA-covered, eastus, S0 tier).
- **Rule-based classifier (rule_v2)** — 14 contract types: lease / lease_amendment / lease_loi / nda / employment / amendment / call_coverage_agreement / bylaws / service_agreement / processor_agreement / payer_contract / vendor_contract / fee_schedule / unknown.
- **Regex field extractors v1** — Lease, NDA, Employment, Corporate, CallCoverage. Each with a typed schema (`lease_v1`, `nda_v1`, etc).
- **LLM field extraction (Sonnet 4.6 via OpenRouter)** — replaces regex on demand or in batch. ~$0.01-0.02 per doc, 5-10 sec each.
- **Cross-document insights** — amendment chains, counterparty graph, address registry, total rentable sqft. Reads LLM data preferentially, falls back to regex.
- **Premium UI surface** — Portfolio (KPIs + family rollups + insights panel), Document Detail (layout snippet + canonical headline + citation anchors), Review queue, Command Center.
- **Canonical headline fields (Slice 18)** — every contract family has a defined must-extract set (lease/NDA/employment/call-coverage/generic). Stage-1+2 prompts mandate the schema; UI renders a HeadlineGrid first, "structured details" + "risk flags" collapse below.
- **Renewal Engine (Slice 19)** — `/api/analysis/renewals` derives expiration, notice deadline, NDA discussion / survival end from headlines; `/renewals` page buckets by Overdue / 0-30 / 31-90 / 91-180 / 181-365 / >365 with severity coloring; KPI on Command Center.
- **Citation anchors (polish)** — clicking a HeadlineCard scrolls the layout-snippet pane to the matched quote and flashes a highlight on it; falls back to head-match and whitespace-normalized search.
- **Entity Graph (Slice 17)** — `/api/analysis/entity-graph` walks every doc and produces nodes (people / orgs / premises / documents) + relation edges with token-normalized dedupe (M.D./Ph.D./P.A. degree-suffix collapse). `/graph` page renders force-atlas-2 via vis-network with type-color legend, hover tooltips, sidebar inspector for selected node, double-click drill into doc.
- **Cloudflare deploy** — Pages (frontend) + Tunnel (backend on local PC) + Access (whitelist auth) + same-origin Pages Function proxy + service-token gate on `api.practicex.ai`.

### Compliance posture

- ✅ Microsoft Azure BAA covers Document Intelligence (eastus, S0).
- ⚠️ OpenRouter → Anthropic Claude has **no BAA** today. Acceptable for Eagle GI demo (Parag-trusted, pre-revenue), **must move to Azure-OpenAI BAA-covered or Anthropic-direct BAA before paid customers**.
- ✅ Cloudflare Access whitelist gates all customer-data UI. Source PDFs never leave the laptop's local disk.
- ⚠️ `api.practicex.ai` is publicly reachable (no Access in front). Service-token gate is tomorrow's harden.

---

## Document workflow — current state

```
[ INGEST ]
  Browser folder upload  →  POST /api/sources/.../folder/scan
                                                    │
                                                    ▼
  SHA-256 dedupe  ──  same content already on file? skip.
                                                    │
                                                    ▼
[ CLASSIFY ]
  Validity inspector       (PDF text-layer? scanned? encrypted?)
  Signature detector       (Docusign envelope / AcroForm / DOCX)
  Complexity profiler      (S/M/L/X tier — drives pricing later)
  Rule-based classifier    (lease / nda / employment / lease_amendment /
                            call_coverage / bylaws / service_agreement / ...)
                                                    │
                                                    ▼
[ STORAGE ]
  Local file system, content-addressed by SHA-256, per tenant.
  doc.document_assets row created with metadata + routing decision.
                                                    │
                                                    ▼
[ TEXT EXTRACTION ]
  digital PDF/DOCX  ─→  PdfPig / OpenXml      (local, instant, free)
  scanned image-PDF ─→  Azure Document Intel  (BAA-covered, ~$0.001/pg)
                                                    │
                                                    ▼
  → extracted_full_text + layout_json stored on the asset
                                                    │
                                                    ▼
[ FIELD EXTRACTION — Phase 1 ] (automatic, on every ingest)
  Regex extractors per family:
    LeaseExtractor / NdaExtractor / EmploymentExtractor /
    CorporateExtractor / CallCoverageExtractor
  → extracted_fields_json
                                                    │
                                                    ▼
[ FIELD EXTRACTION — Phase 2 ] (on-demand or batch)
  OpenRouter → Claude Sonnet 4.6 with family-specific JSON schema prompt
  ~5-10 sec per doc, ~$0.01-0.02 per doc
  → llm_extracted_fields_json   (overrides regex in UI when present)
                                                    │
                                                    ▼
[ CROSS-DOCUMENT ANALYSIS ] (computed on read)
  Aggregate landlords, tenants, counterparties (LLM data preferred)
  Detect amendment chains by parent_agreement_date
  Sum rentable sqft  ·  Address registry
                                                    │
                                                    ▼
[ SURFACE ]
  Portfolio page   ·   Document detail (PDF + fields)   ·   Review queue
  Command center KPIs   ·   /api/analysis/insights
```

### Storage shape

`doc.document_assets` is the central spine. Per asset:
- `validity_status`, `extraction_route`, `has_text_layer`, `is_encrypted`
- `complexity_tier`, `complexity_factors_json`, `complexity_blockers_json`
- `layout_json`, `layout_provider`, `layout_model`, `layout_page_count` (Doc Intel output)
- `extracted_full_text` (cap 256KB; survives Doc Intel layout for snippet display)
- `extracted_fields_json`, `extracted_subtype`, `extractor_name`, `extraction_status` (regex output)
- `llm_extracted_fields_json`, `llm_extractor_model`, `llm_tokens_in/out`, `llm_extracted_at`, `llm_extraction_status` (LLM output)

Audit trail: `audit.audit_events` captures every ingestion, layout extraction, field extraction, LLM call, and batch run with token counts + latency.

---

## Tonight's polish list (next session — ~1-2 hours total)

These are demo-blockers Parag will notice. Knock out before sharing the URL widely.

1. **Total rentable sqft over-counts.** Currently 78,343 sqft because we sum premises across multiple amendments that all reference the same physical suites at 1002 N. Church. Should dedupe by `(street_address + suite)` tuple. The actual unique footprint is ~23,743 sqft. **30 min.**

2. **Tenant entity normalization.** "Eagle Physicians, P.A." / "Eagle Physicians and Associates, P.A." / "EAGLE PHYSICIANS AND ASSOCIATES, PA" all show as separate tenants. Case-insensitive + punctuation-stripped fuzzy match should collapse to one canonical. **15 min.**

3. **Counterparty filter.** Drop entries that are already shown as landlord or tenant — currently they appear in both lists, inflating the counterparty count. **10 min.**

4. **EmploymentExtractor `phi_agreement` false-positive** — fires on any document that mentions HIPAA. Tighten to require explicit "Business Associate" or "Protected Health Information" structure. **30 min.** (regex side; LLM already gets this right)

5. **Service-token harden on `api.practicex.ai`.** Need user to add `Access: Service Tokens: Edit` permission to the Cloudflare API token, then create a token + add to API app's Allow policy. Removes the public-API exposure. **15 min once token has permission.**

---

## Phase 3 — Premium features

Each of these reads from the same data spine and adds a new operational surface. Order is roughly priority for the board demo and post-Parag rollout.

### ⭐⭐⭐ Legal Advisor Agent — Slice 20 (shipped 2026-05-09)
A dedicated General-Counsel pass over every contract — separate from the
Document Intelligence Brief, premium-tier surface with a non-negotiable
"not legal advice" disclaimer.

- **Per-doc Counsel's Memo** (markdown + structured JSON):
  posture snapshot with 0–100 risk score, issue register (CRITICAL/HIGH/
  MEDIUM/LOW × 17 categories), proposed redlines with concrete language,
  material disclosures (board / insurer / lender / M&A diligence /
  regulators), counterparty posture, action items, plain-English summary.
- **Family overlays**: Lease, NDA, Employment, CallCoverage, Generic —
  each with its own corporate-law issue checklist (M&A reps, governance
  protective provisions, IP assignment, change-of-control, indemnity
  caps, anti-assignment, drag/tag/ROFR, escalators, exclusivity, BAA,
  Stark, AKS).
- **Counsel's Brief** (cross-document): partner-counsel synthesis at
  board-grade — risk heatmap, top 10 cross-document risks, material
  disclosure posture, counterparty concentration, compliance posture,
  negotiation calendar.
- **Endpoints**: `POST/GET /api/legal-advisor/memos/{assetId}`,
  `POST /api/legal-advisor/memos-batch`, `GET /api/legal-advisor/portfolio`,
  `POST/GET /api/legal-advisor/counsel-brief`. Two-stage Sonnet 4.6 via
  OpenRouter (markdown then strict JSON) — same posture as existing
  brief/extract pipeline.
- **UI**: `/legal-advisor` portfolio page (sortable by risk score) +
  "Counsel's Memo" tab on `/portfolio/{id}` + `LegalDisclaimer` banner
  on every memo surface. Sidebar entry under Workspace.
- **DB**: migration `20260509_legal_memo.sql` adds memo columns to
  `doc.document_assets` + new `doc.counsel_briefs` table. Idempotent.
- **Why it differs from existing Stage 1 brief**: the brief *describes*
  what's in the contract (sectioned narrative for a practice owner). The
  memo takes an adversarial GC posture — landmines, redlines, disclosure
  triggers, sortable risk score. Same documents, different audience and
  product tier.
- **Files**: `src/PracticeX.Api/Analysis/LegalAdvisorEndpoint.cs`,
  `src/PracticeX.Api/Analysis/Prompts/LegalMemo_*.md`,
  `apps/command-center/src/views/LegalAdvisorPage.tsx`,
  `apps/command-center/src/components/LegalDisclaimer.tsx`,
  document-detail tab in `DocumentDetailPage.tsx`.

### ⭐⭐⭐ Renewal Engine — board-demo signature feature
- Compute `end_date` from `effective_date + term_months`.
- Subtract `notice_period_days` to get `notice_window_opens`.
- `/alerts` surface: "Lease at 1002 N. Church renews in 90 days, notice window opens Jul 15"
- Daily email digest: "3 renewals in next 60 days, 1 needs CFO action"
- This is the feature that lets Parag say "PracticeX paid for itself in renewal #1"

### ⭐⭐⭐ Contract-Aware Scheduling — the moat
- Reads `call_coverage_v1` schema → physician rotation rules.
- Reads `employment_v1` → on-call obligations + comp formulas.
- Generates Qgenda-compatible monthly schedules.
- Surfaces conflicts: "Dr. Brahmbhatt has Cone Health weekend coverage Apr 18-20 per the May 2024 agreement; this conflicts with the Eagle internal rotation."
- This is the structural differentiator vs Amazon Quick / generic AI assistants — Quick has no scheduling primitive.

### ⭐⭐ Cost Savings Engine
- Cross-tenant rate benchmarking (anonymized).
- Lease $/sqft compared to market median.
- Vendor service rates compared to peer practices.
- Output: "Your AP Labs MDS contract is 18% above peer median, renegotiate at renewal."

### ⭐⭐ Negotiation Copilot
- Identifies non-standard clauses (auto-renew, indemnity, exclusivity).
- Suggests redlines based on prior signed contracts.
- Generates renewal LOI drafts from current terms + market data.
- Output: "Your draft 1002 N. Church renewal has 2.5% escalation; comparable leases in NC averaged 3.2% in Q1 2026 — you can push for 3%."

### ⭐⭐ Counterparty Intelligence
- Builds a graph: who you contract with, who they contract with, M&A signals.
- Detects "asset purchase" / "business restructuring" language in NDAs.
- Output: "Wake Forest's M&A Business Dev VP is on 4 other NC practice NDAs from 2023-2024 — they're consolidating."

### ⭐ Compliance & Risk Dashboard
- BAA inventory (every vendor with PHI access, expiry status).
- Missing-signature surfacing (templates that should be executed).
- Conflict detection (exclusivity in one contract contradicted by another).
- Audit-ready PDF export.

### ⭐ Notifications Layer
- Email alerts (notice windows, missing fields, expirations).
- Slack / Teams webhooks.
- Mobile push (when iOS app ships).

---

## Phase 4 — Plumbing that unlocks scale

### Pre-ingestion (more sources)
- Outlook mailbox connector (already scaffolded; wire post-BAA).
- Desktop facility agent (on-prem network shares; designed in `docs/desktop-discovery-agent.md`).
- Email forward (`forward@practicex.ai` → ingest pipeline).
- SharePoint / Google Drive / Box connectors.

### Post-extraction enrichment
- Entity normalization (canonical entity table; alias mapping).
- Renewal date computation (covered in Phase 3 above).
- Risk scoring (auto-renew, indemnity, termination).
- Conflict detection across docs.
- Rate benchmarking (market data feed).

### Production posture
- OIDC auth (replace `DemoCurrentUserContext`).
- Multi-tenant data isolation (row-level security in Postgres).
- Stripe billing (subscription + usage-based for Doc Intel + LLM costs).
- SOC 2 / HIPAA audit (post-revenue).
- Customer self-onboarding (signup → tenant provisioning).

### Compliance hardening
- Migrate LLM calls off OpenRouter onto Azure-OpenAI (BAA-covered) or Anthropic-direct (with their BAA).
- Service-token-gate `api.practicex.ai` so the public hostname requires auth.
- Move Cloudflare zone from personal Gmail account to a `practicex.ai` corporate identity.
- Provision a dedicated Azure subscription separate from Synexar (already done — `practicex-pcc-docintel` lives under `Rvadapally@gmail.com's Account`; should migrate to `rvadapally@practicex.ai` corporate identity for clean books).

---

## Why this is structurally hard to clone

Three moats that compound:

1. **BAA-covered + healthcare-domain-specific extraction schemas.** `lease_v1`, `call_coverage_v1`, `employment_v1`, etc. — built from real customer samples. AWS Quick + a custom-app builder is generic; the schema-and-prompt work is bespoke per industry.

2. **Cross-document operational insights.** Amendment chains, counterparty graphs, renewal timelines, total sqft. Not a "summarize my files" capability — these are healthcare-practice-operations primitives.

3. **Contract → scheduling bridge.** Physical operations downstream of contracts (Qgenda export, on-call rotations, RVU comp). No generic AI assistant has scheduling as a first-class concept.

Phase 3 priorities concentrate on these three intentionally.

---

## Open strategic decisions

1. **Should we be a Quick custom app eventually, or stay standalone?**
   - Standalone gives us pricing power + brand presence.
   - Quick app gives us AWS distribution.
   - Hybrid: standalone for premium customers, Quick app for self-serve.
   - Recommend: standalone-only for first 3-5 customers; revisit Quick app as distribution play in Q3 2026.

2. **Pricing model.**
   - Per facility ($499/mo as Parag suggested) vs. per provider vs. percentage of savings.
   - Per facility is simplest; per provider scales with practice size; percentage of savings aligns interests.
   - Recommend: per facility for simplicity; layer percentage-of-savings on top of premium tier (Negotiation Copilot, Cost Savings Engine).

3. **Geographic expansion.**
   - Eagle GI is NC. WFUBMC and Cone Health are NC.
   - First 5 customers in NC = compounding referral network + market data density.
   - Then expand to TX, FL, GA — high specialty-group concentration.

---

## Sequencing for the board demo (3-week clock)

Week 1: tonight's polish list + Renewal Engine (Phase 3, ⭐⭐⭐). 
Week 2: Contract-Aware Scheduling MVP — at minimum, render the call coverage rotation rules as a structured calendar view that maps to Qgenda's data model.
Week 3: Negotiation Copilot stub + customer narrative deck for the board meeting.

Demo punch line: "We read Eagle GI's filing cabinet in 30 seconds, surfaced the WFUBMC M&A NDA + Cone Health call coverage + 1002 N. Church renewal posture, and we're generating contract-aware schedules from the same data."

---

## Strategic findings already extracted (do not lose)

See `~/.claude/projects/.../memory/project_eagle_gi_strategic_findings.md` for the customer-confidential summary of what we surfaced from Parag's drop. Highlights:

- October 2023: M&A NDA with Wake Forest University Baptist Medical Center, signed by 5 named partners + WFUBMC's VP of Core Market Growth and Business Development.
- May 2024: $1,800/day GI + ERCP call coverage contract with Cone Health (Moses Cone + Wesley Long).
- April 2026: Lease renewal LOI for 1002 N. Church at $16.83/sqft + 2.5% escalation.
- Eagle GI has been at 1002 N. Church for 27 years across 8+ amendments since the 1999 master lease.

---

## Slice ledger (commits on `main`)

| Slice | Description | Commit |
|---|---|---|
| 1-3 | Initial scaffolding, manifest flow, Discovery library extraction | early |
| 4-6 | Schemas + extractors for Employment, NDA, Corporate | `546360e` |
| 7 | Azure Document Intelligence wiring | `e0bc0b2` / `59d3a7a` |
| 8 | Classifier v2 + LeaseExtractor + premium analysis surface API | `a20ac5a` |
| 9 | Portfolio UI + CallCoverageExtractor | `5596b04` |
| 10 | Strip mock data + wire UI to real APIs | `e526941` |
| 11 | Side-by-side document detail | `6a03676` |
| 12 | Pretty-print fields + inline PDF + extracted_full_text storage | `ae02867` |
| 13 | OpenRouter LLM extraction (Claude) | `7daa64e` |
| 14 | Vertical field-card layout + Sonnet 4.6 | `1252af7` |
| 15 | Batch LLM + same-origin proxy + LLM-cleaned insights | `5e739b4` |
| 16 | Two-stage LLM (narrative brief + JSON extract) | `dc8e638`-ish |
| 17 | Entity Graph (`/graph`) | post-15 |
| 18 | Canonical headline fields + citation anchors | post-15 |
| 19 | Renewal Engine (`/renewals`) | post-15 |
| 20 | Legal Advisor Agent (Counsel's Memo + Brief, premium) | `749a1d7` |
| 20.1 | Synexar / early-stage corporate family overlays (Equity, Governance, IpAssignment, CorpFormation, Regulatory, Policy) | `3fa2bf7` |
| 20.2 | Stage B JSON failure recovery + retry-json endpoint | `4e39188` |
| 21 (Phase 1) | RBAC + facility-level isolation (super_admin / org_admin / facility_user) | `4c25bd1` |
| 21 (Phase 2) | Tenant split: Eagle + Synexar promoted to own tenants; org switcher | next |

---

## Synexar test results (2026-05-09 / 2026-05-10)

End-to-end Legal Advisor batch over Synexar's 53-doc foundation corpus:

- **49 completed + 1 partial** (markdown saved, JSON unparseable — recoverable via `/retry-json`).
- **Risk distribution:** 0 severe / **37 high (61–80)** / 7 elevated / 6 modest / 0 low. Average **61.6 / 100**, max **74** (HIPAA BAA).
- **Counsel's Brief synthesized 47 docs in 168 sec** (286K input tokens / 8K output, ~$0.85). Identified the company-defining risk thesis: HIPAA compliance vacuum (no executed BAA, no policy, no breach procedure) running into a Series A term sheet that requires HIPAA attestation as a hard closing condition. Cross-document linkage no single doc could surface.
- Time-sensitive items the brief surfaced: **FinCEN BOIR update by May 31, 2026** (post share-gift), **Vadapally 83(b) reconstruction** (record missing).
- Validated all 6 new Slice 20.1 family overlays at scale; risk-score calibration is well-distributed (no clustering).

Sample artifacts (gitignored — local only): `synexar_memo_run.log`, `highest_risk_memo.md`, `sample_memo.md`, `synexar_counsel_brief.md`.

---

## Where to find things

- API endpoints: `src/PracticeX.Api/Analysis/AnalysisEndpoints.cs` + `LlmExtractionEndpoint.cs` + `LegalAdvisorEndpoint.cs` + `SourceDiscovery/SourceDiscoveryEndpoints.cs`
- Pipeline orchestration: `src/PracticeX.Infrastructure/SourceDiscovery/Ingestion/IngestionOrchestrator.cs`
- Extractors: `src/PracticeX.Discovery/FieldExtraction/*Extractor.cs`
- Schemas: `src/PracticeX.Discovery/Schemas/*SchemaV1.cs`
- LLM provider: `src/PracticeX.Infrastructure/SourceDiscovery/Llm/OpenRouterDocumentLanguageModel.cs`
- Frontend: `apps/command-center/src/views/PortfolioPage.tsx` + `DocumentDetailPage.tsx`
- Cloudflare Pages proxy: `apps/command-center/functions/api/[[path]].ts`
- Customer data: `data/Eagle GI/` (gitignored)
- Deploy runbook: `docs/deploy-cloudflare.md`
