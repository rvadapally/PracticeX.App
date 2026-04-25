# PracticeX Architecture Decisions

## ADR 0001: Enterprise-first, data-first foundation

Status: accepted

PracticeX will be built as a production-shaped enterprise application from the first implementation slice. The pilot/demo path can be narrow, but core architecture must include durable tenancy, facility scoping, source ingestion, evidence, review, audit, and workflow concepts.

Implications:

- No throwaway mock backend.
- Demo features should write to production-shaped tables and APIs.
- Automation can be introduced gradually behind stable typed interfaces.

## ADR 0002: React frontend

Status: accepted

The frontend will use React with TypeScript and Vite. React is selected because the highest-risk UI surface is the PDF/evidence workstation: split panes, PDF rendering, page overlays, evidence bounding boxes, dense review tables, and command-style interactions.

Angular remains viable, but the React ecosystem is a better fit for PDF-centered custom UI and the FCC mock direction.

## ADR 0003: ASP.NET Core and PostgreSQL backend

Status: accepted

The backend will use ASP.NET Core with PostgreSQL. The backend should begin as a modular monolith with clear module boundaries rather than distributed services.

Initial modules:

- organization and tenancy
- source connections
- document ingestion
- contract records
- evidence
- review workflow
- renewals and tasks
- audit

## ADR 0004: PostgreSQL snake_case only

Status: accepted

All PostgreSQL identifiers must be unquoted snake_case. This avoids case-sensitive quoting issues and keeps direct SQL work clean.

Entity Framework Core mappings must translate idiomatic C# names to snake_case database names.

## ADR 0005: Idempotent database migrations

Status: accepted

Runtime application startup must not apply schema migrations automatically in production. Deployment should generate and run idempotent SQL migration scripts.

The database should use a dedicated migration history table with snake_case naming. Migration scripts must be safe to apply repeatedly in deployment pipelines.

## ADR 0006: Connectors feed one ingestion pipeline

Status: accepted

Local folder discovery, Outlook, Gmail, Drive/SharePoint, Zoom transcripts, and SFTP all feed the same ingestion model. Each connector produces source objects and candidate documents. Human review decides what becomes canonical contract data.
