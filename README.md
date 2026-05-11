# PracticeX

**PracticeX** is the company. **Practice Command Center (PCC)** is its flagship product — an enterprise healthcare contract intelligence platform that turns a practice's disorganized inventory of payer, vendor, lease, employment, and processor agreements into a structured, evidence-backed system of record, with visibility into renewal risk, notice deadlines, and (later) rate benchmarking and negotiation support.

The early product is shaped for independent GI groups, ambulatory surgery centers, and specialty practices, with the practice/ASC administrator as the primary user. See [`docs/revised_prd.md`](docs/revised_prd.md) for the full product vision and [`docs/architecture-decisions.md`](docs/architecture-decisions.md) for the foundational ADRs.

## Repository layout

```
apps/command-center            React + Vite frontend (Practice Command Center UI)
packages/design-system         Shared design tokens and components
src/PracticeX.Api              ASP.NET Core 9 Web API (entry point)
src/PracticeX.Application      Application layer — connectors, orchestration, interfaces
src/PracticeX.Domain           Domain entities and rules
src/PracticeX.Infrastructure   EF Core, PostgreSQL persistence, external integrations
tests/PracticeX.Tests          xUnit tests (includes Source Discovery tests)
migrations/                    Idempotent SQL migrations (canonical source of truth)
docs/                          PRD, architecture decisions, module notes
```

## What has been implemented so far

The codebase is in its enterprise-foundation phase. The current commits land:

- **Enterprise foundation schema.** PostgreSQL with explicit schemas (`org`, `doc`, `contract`, `evidence`, `rate`, `workflow`, `audit`; `ref` reserved). Snake-case-only identifiers, EF Core naming convention translation. See `migrations/practicex_initial_enterprise_foundation.sql`.
- **Source Discovery module** (the entry point of the ingestion pipeline). Documented in [`docs/source-discovery.md`](docs/source-discovery.md). Includes:
  - `ISourceConnector` abstraction and an `IIngestionOrchestrator` that persists to `doc.source_objects`, `doc.ingestion_batches`, `doc.ingestion_jobs`, `doc.document_assets`, `doc.document_candidates`, `workflow.review_tasks`, and `audit.audit_events`.
  - **Folder upload connector** — multipart upload that preserves relative folder paths (`paths[i]` form fields, `webkitRelativePath` fallback) and uses the parent folder as a classification hint.
  - **Outlook (Microsoft Graph) connector** — OAuth authorize/callback flow, contract-likely message and attachment pull. Tokens held by `IMicrosoftGraphTokenStore` (in-memory in dev).
  - **Rule-based classifier** that emits explainable reason codes (`likely_contract`, `filename_amendment`, `folder_hint_payer`, `outlook_subject_keywords`, …) persisted on `doc.document_candidates.reason_codes_json`.
  - REST surface under `/api/sources/...` (connectors, connections, scans, batches, candidates, queue-review, retry, OAuth start/callback).
- **Frontend shell** for the Command Center UI: React 19 + Vite 8, React Router 7, the `@practicex/design-system` workspace package, and a dev proxy that forwards `/api` to the backend.

What is **not** implemented yet (per the PRD): rate intelligence, benchmarking, negotiation copilot, Gmail/Drive/SFTP connectors, outbound notice generation, e-signature, EHR/PMS integration, and self-service onboarding. The data model and architecture do not preclude these — they are sequenced after the pilot.

## Prerequisites

- **.NET 9 SDK** (the API targets `net9.0`).
- **Node.js ≥ 24** and **npm ≥ 11** (enforced by `package.json` `engines`).
- **PostgreSQL 14+** reachable on `localhost:5432` with a `practicex` database (override via `ConnectionStrings__PracticeX`).
- **`dotnet ef` tool** for migration commands: `dotnet tool install --global dotnet-ef`.
- **Microsoft Entra (Azure AD) app registration** if you plan to exercise the Outlook connector (see below).

## Database setup and migration order

PracticeX **does not** auto-apply migrations at startup (ADR 0005). Apply the SQL scripts in this exact order against your `practicex` database:

1. `migrations/practicex_initial_enterprise_foundation.sql` — canonical schemas and tables.
2. `migrations/20260425_source_discovery_extensions.sql` — Source Discovery additions (`source_objects`, `ingestion_batches`, candidate metadata, etc.).
3. `migrations/20260426_manifest_phase_extensions.sql`
4. `migrations/20260427_complexity_profiling.sql`
5. `migrations/20260427_doc_intel_layout.sql`
6. `migrations/20260428_extracted_fields.sql`
7. `migrations/20260429_extracted_full_text.sql`
8. `migrations/20260429_llm_extracted_fields.sql`
9. `migrations/20260430_llm_narrative_brief.sql`
10. `migrations/20260430_portfolio_brief.sql`
11. `migrations/20260509_legal_memo.sql`
12. `migrations/20260509_portfolio_brief_per_facility.sql`
13. `migrations/20260509_rbac_phase1.sql`
14. `migrations/20260510_tenant_split.sql`
15. `migrations/20260510_rbac_identity_alignment.sql`

All scripts are idempotent and safe to re-run. Example:

```bash
psql "postgres://postgres:postgres@localhost:5432/practicex" \
  -f migrations/practicex_initial_enterprise_foundation.sql
psql "postgres://postgres:postgres@localhost:5432/practicex" \
  -f migrations/20260425_source_discovery_extensions.sql
```

To generate a new EF Core migration or a deployment script, see [`docs/database-migrations.md`](docs/database-migrations.md). The history table is `audit.__ef_migrations_history`.

## Microsoft Graph OAuth setup

Required only if you want to exercise the Outlook connector. Full details in [`docs/source-discovery.md`](docs/source-discovery.md).

1. Register an application in Microsoft Entra (Azure AD).
2. Add the redirect URI matching `MicrosoftGraph:RedirectUri` — the default is `https://localhost:7100/api/sources/outlook/oauth/callback`.
3. Add the **delegated** API permissions: `offline_access`, `Mail.Read`, `Mail.ReadBasic` (read-only).
4. Create a client secret and copy its value.
5. Provide configuration via environment variables (do not commit secrets):

   ```bash
   export MicrosoftGraph__ClientId="<app-registration-client-id>"
   export MicrosoftGraph__ClientSecret="<app-registration-client-secret>"
   export MicrosoftGraph__TenantId="common"   # or your AAD tenant id
   export MICROSOFT_GRAPH_REDIRECT_URI="https://localhost:7100/api/sources/outlook/oauth/callback"
   ```

6. Start the API, open the Source Discovery UI, click **Connect Outlook**, and consent. Tokens are stored via `IMicrosoftGraphTokenStore` (in-memory in dev; replace with Azure Key Vault for production).

If `MicrosoftGraph:ClientId` is unset the connector reports `configuration_required` and the start endpoint returns HTTP 400.

## Running the app locally

Two processes — backend API and frontend dev server.

### Backend (ASP.NET Core API)

```bash
# From repo root
dotnet restore
dotnet build

# Apply the migrations above before first run.

# Run the API. The frontend Vite proxy expects https://localhost:7100,
# so launch the API on that port:
dotnet run --project src/PracticeX.Api --urls "https://localhost:7100;http://localhost:5057"
```

### Frontend (React + Vite)

```bash
# From repo root — npm workspaces resolve the design-system package automatically.
npm install
npm run dev    # runs the command-center app on http://localhost:5173
```

Open <http://localhost:5173>. Calls to `/api/...` are proxied to the API per `apps/command-center/vite.config.ts`.

## Build, typecheck, and test commands

Frontend (run from repo root — these target the npm workspaces):

```bash
npm run build       # builds @practicex/design-system, then @practicex/command-center
npm run typecheck   # tsc on both workspaces
```

Backend:

```bash
dotnet build PracticeX.sln
dotnet test  tests/PracticeX.Tests/PracticeX.Tests.csproj
```

## Known development caveats

- **Migrations are not auto-applied.** Per ADR 0005, application startup never runs schema migrations in any environment. Apply the SQL scripts manually in the order listed above before the API will function correctly.
- **Vite proxy assumes API on `https://localhost:7100`.** `apps/command-center/vite.config.ts` forwards `/api` there with `secure: false` to accept the dev cert. The default `https` profile in `src/PracticeX.Api/Properties/launchSettings.json` uses `https://localhost:7138`; override the URL when running (`--urls "https://localhost:7100;http://localhost:5057"`) or update one of the two to match.
- **Microsoft Graph OAuth requires HTTPS.** The redirect URI is HTTPS by default; trust the ASP.NET Core dev certificate (`dotnet dev-certs https --trust`) before testing the Outlook flow.
- **Microsoft Graph token store is in-memory in dev.** Restarting the API drops connected Outlook sessions. A durable store (Azure Key Vault or equivalent) is required before production.
- **PostgreSQL identifiers are snake_case only** (ADR 0004). When writing SQL or EF Core mappings, never quote identifiers — let the naming convention translate `PascalCase` C# names to `snake_case`.
- **Connectors never mutate canonical contract records.** The ingestion pipeline only writes to source/document/candidate/review tables. Promotion to canonical contract data happens through the review queue.
- **Demo tenant seeding is enabled in dev.** `Seeding:DemoTenant` is `true` in `appsettings.json`; turn it off for environments where you do not want seed data.
- **Secrets must come from environment variables**, not committed config. The `MicrosoftGraph` block in `appsettings.json` ships empty for that reason.
- **Node 24 / npm 11 are required.** Older toolchains will fail the `engines` check.

## Further reading

- [`docs/revised_prd.md`](docs/revised_prd.md) — product vision, principles, pilot scope, full surface map.
- [`docs/architecture-decisions.md`](docs/architecture-decisions.md) — accepted ADRs (enterprise-first, React, ASP.NET + PostgreSQL, snake_case, idempotent migrations, single ingestion pipeline).
- [`docs/source-discovery.md`](docs/source-discovery.md) — Source Discovery module reference, REST surface, reason codes.
- [`docs/database-migrations.md`](docs/database-migrations.md) — migration policy and EF Core commands.
