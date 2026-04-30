# PracticeX Command Center — System Architecture

High-level architecture as of slice 15 (commit `5e739b4`, 2026-04-29).
Companion document: [`workflow.md`](./workflow.md) — document processing pipeline.

---

## Deployment topology

```mermaid
flowchart LR
    subgraph User["End user"]
        Browser["Browser<br/>(Cloudflare Access whitelist)"]
    end

    subgraph CF["Cloudflare edge"]
        Pages["Pages<br/>app.practicex.ai<br/>(static SPA)"]
        Proxy["Pages Function<br/>/api/* same-origin proxy"]
        Tunnel["cloudflared Tunnel<br/>api.practicex.ai"]
        Access["Access policy<br/>(email whitelist)"]
    end

    subgraph Local["Local PC (on-prem backend)"]
        Api["PracticeX.Api<br/>(ASP.NET minimal API)"]
        Pg[("Postgres<br/>doc.* + audit.*")]
        FS[("Local file store<br/>SHA-256 content-addressed")]
    end

    subgraph Azure["Azure (BAA-covered)"]
        DocIntel["Document Intelligence<br/>eastus, S0"]
    end

    subgraph LLM["LLM (no BAA today)"]
        OR["OpenRouter →<br/>Claude Sonnet 4.6"]
    end

    Browser -->|HTTPS| Access
    Access --> Pages
    Pages --> Proxy
    Proxy -->|same-origin| Tunnel
    Tunnel --> Api
    Api --> Pg
    Api --> FS
    Api -->|scanned PDFs| DocIntel
    Api -->|field extraction| OR

    classDef warn fill:#fff4e6,stroke:#ff8c00,color:#000
    class OR warn
```

Notes:
- `app.practicex.ai` is gated by Cloudflare Access; `api.practicex.ai` is publicly reachable today (service-token harden pending — see roadmap polish item 5).
- Source PDFs never leave the local disk. Only extracted text + structured fields traverse the LLM boundary.
- OpenRouter path is the compliance gap; must move to Azure-OpenAI BAA or Anthropic-direct BAA before paid customers.

---

## Solution layout

```mermaid
flowchart TB
    subgraph FE["apps/command-center (React + Vite)"]
        Portfolio["PortfolioPage"]
        DocDetail["DocumentDetailPage"]
        Review["Review queue"]
        Cmd["Command Center KPIs"]
        ProxyFn["functions/api/[[path]].ts<br/>(Cloudflare Pages Function)"]
    end

    subgraph API["src/PracticeX.Api"]
        AnalysisEP["Analysis/AnalysisEndpoints.cs"]
        LlmEP["LlmExtractionEndpoint.cs"]
        SrcEP["SourceDiscovery/SourceDiscoveryEndpoints.cs"]
    end

    subgraph App["src/PracticeX.Application"]
        AppSvc["Use cases / DTOs"]
    end

    subgraph Disc["src/PracticeX.Discovery"]
        Schemas["Schemas/*SchemaV1.cs<br/>lease · nda · employment · corporate · call_coverage"]
        Extractors["FieldExtraction/*Extractor.cs<br/>regex extractors"]
        Classifier["rule_v2 classifier"]
    end

    subgraph Infra["src/PracticeX.Infrastructure"]
        Orchestrator["SourceDiscovery/Ingestion/<br/>IngestionOrchestrator.cs"]
        DocIntelClient["Azure Document Intelligence client"]
        LlmProvider["Llm/OpenRouterDocumentLanguageModel.cs"]
        Repo["EF Core repositories"]
    end

    subgraph Domain["src/PracticeX.Domain"]
        Entities["DocumentAsset · AuditEvent · Tenant"]
    end

    subgraph Agents["src/PracticeX.Agent.*"]
        AgentCli["Agent.Cli (desktop crawler)"]
        AgentUi["Agent.Ui"]
    end

    Portfolio --> ProxyFn
    DocDetail --> ProxyFn
    Review --> ProxyFn
    Cmd --> ProxyFn
    ProxyFn --> AnalysisEP
    ProxyFn --> LlmEP
    ProxyFn --> SrcEP

    AnalysisEP --> AppSvc
    LlmEP --> AppSvc
    SrcEP --> AppSvc
    AppSvc --> Orchestrator
    AppSvc --> Repo

    Orchestrator --> Classifier
    Orchestrator --> Extractors
    Orchestrator --> DocIntelClient
    Orchestrator --> LlmProvider
    Extractors --> Schemas
    LlmProvider --> Schemas

    Repo --> Entities
    AgentCli -.->|future| SrcEP
```

---

## Data spine

```mermaid
erDiagram
    DOCUMENT_ASSETS ||--o{ AUDIT_EVENTS : "emits"
    DOCUMENT_ASSETS {
        uuid id PK
        string sha256 "content hash (dedupe key)"
        string validity_status
        string extraction_route
        bool has_text_layer
        bool is_encrypted
        string complexity_tier "S/M/L/X"
        json complexity_factors_json
        json complexity_blockers_json
        json layout_json "Doc Intel output"
        string layout_provider
        string layout_model
        int layout_page_count
        text extracted_full_text "cap 256KB"
        string extracted_subtype "lease/nda/..."
        string extractor_name
        string extraction_status
        json extracted_fields_json "regex output"
        json llm_extracted_fields_json "LLM output (preferred)"
        string llm_extractor_model
        int llm_tokens_in
        int llm_tokens_out
        timestamp llm_extracted_at
        string llm_extraction_status
    }
    AUDIT_EVENTS {
        uuid id PK
        uuid document_asset_id FK
        string event_type "ingest/layout/extract/llm/batch"
        int tokens_in
        int tokens_out
        int latency_ms
        timestamp occurred_at
    }
```

`doc.document_assets` is the single read model for the Portfolio + Document Detail surfaces. Cross-document insights (amendment chains, counterparty graph, address registry, total sqft) are computed on read by aggregating across rows — no materialized view yet.

---

## Trust boundaries & compliance

| Boundary | Data crossing | BAA status |
|---|---|---|
| Browser → Cloudflare Access → Pages | UI traffic only | N/A (no PHI in transit) |
| Pages → Tunnel → local API | API calls (tenant-scoped) | Cloudflare zero-trust; service-token harden pending |
| API → Postgres / local FS | Full PHI (extracted text, source PDFs) | Local — never leaves disk |
| API → Azure Document Intelligence | Scanned PDF bytes + extracted layout | ✅ Microsoft Azure BAA (eastus, S0) |
| API → OpenRouter → Anthropic | Extracted text + JSON schema | ⚠️ No BAA — Eagle GI demo only |

See roadmap "Compliance posture" + Phase 4 "Compliance hardening" for the migration plan.

---

## Where to find things in code

- API endpoints: `src/PracticeX.Api/Analysis/AnalysisEndpoints.cs`, `LlmExtractionEndpoint.cs`, `SourceDiscovery/SourceDiscoveryEndpoints.cs`
- Pipeline orchestration: `src/PracticeX.Infrastructure/SourceDiscovery/Ingestion/IngestionOrchestrator.cs`
- Extractors: `src/PracticeX.Discovery/FieldExtraction/*Extractor.cs`
- Schemas: `src/PracticeX.Discovery/Schemas/*SchemaV1.cs`
- LLM provider: `src/PracticeX.Infrastructure/SourceDiscovery/Llm/OpenRouterDocumentLanguageModel.cs`
- Frontend: `apps/command-center/src/views/PortfolioPage.tsx`, `DocumentDetailPage.tsx`
- Cloudflare proxy: `apps/command-center/functions/api/[[path]].ts`
