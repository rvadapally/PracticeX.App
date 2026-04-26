# PracticeX Facility Discovery Agent — Spec

> Status: Phase 0 (cloud manifest + bundle endpoints) is shipped. **Phase 1 ships in this PR** — a one-shot console CLI (`practicex-agent`) that exercises the cloud endpoints end-to-end against a local folder, with no SQLite cache and no tray UI yet. Phases 2–4 below are spec only.
>
> The Phase 1 CLI lives at `src/PracticeX.Agent.Cli/`. See [Phase 1 CLI](#phase-1-cli-what-ships-in-this-pr) below.

## Why an agent at all?

The browser folder-upload flow is a great onboarding experience for a small practice that wants to drag in a few hundred files. It hits a wall when the real-world target is:

- Network shares (`\\server\share\Contracts`, mapped drives) the browser cannot reach reliably.
- Tens of thousands of files where browser-side enumeration is slow, memory-heavy, and tab-fragile.
- Recurring scans where "what changed since last week?" matters more than a one-shot upload.
- Bandwidth-constrained or after-hours uploads under IT policy.
- High-confidence privacy posture: *files that don't match contract signals never leave the facility.*

The **PracticeX Facility Discovery Agent** is a thin edge client that runs at the customer site, prunes aggressively before bytes leave the building, and uploads only what passed the bar — through the same APIs the browser uses. The cloud remains the source of truth for tenants, source connections, ingestion batches, candidates, review tasks, audit, and contracts.

## Reference test target

The canonical local test root during development is **`C:\Users\harek\SYNEXAR INC`** — a real-world Synexar working folder with a mix of presentations, spreadsheets, scanned PDFs, and contract evidence. Phase 1 verification points the agent CLI at this folder.

## Identity & deployment

| Concern | Decision |
|---|---|
| Name | **PracticeX Facility Discovery Agent** (`practicex-agent`) |
| Stack | **.NET 9 Worker Service** (cross-platform core) + **WinUI 3** tray UI for Windows in v2. Shares C# DTOs with the API via a small `PracticeX.Discovery.Client` project. |
| Distribution | Signed MSI (Windows). WinGet listing once stable. Linux/macOS via `dotnet tool install` for IT engineers. |
| Process model | v1 runs as the logged-in user (so it can read network shares without IT approvals). v2 ships a Windows Service variant with a service account. |
| Auth | OAuth **device-code flow** against the same identity provider as the web app. Refresh token persisted to a DPAPI-encrypted file under `%LOCALAPPDATA%\PracticeX\agent\token.bin`. |
| Tenant binding | Tenant + facility selected at agent registration. Cached in `agent.db`. Re-prompt if API returns a 401 streak. |

## Local state (`agent.db`, SQLite)

```sql
CREATE TABLE scan_targets (
  id            TEXT PRIMARY KEY,         -- uuid
  root_path     TEXT NOT NULL,            -- C:\Contracts or \\server\share\Legal
  filters_json  TEXT,                     -- include/exclude globs, max-size, etc.
  created_at    INTEGER NOT NULL,
  last_scan_at  INTEGER
);

CREATE TABLE local_index (
  scan_target_id     TEXT NOT NULL,
  relative_path      TEXT NOT NULL,
  size               INTEGER NOT NULL,
  modified_at        INTEGER NOT NULL,
  quick_fingerprint  TEXT NOT NULL,       -- sha1(name|size|mtime|first8b|last8b)
  sha256             TEXT,                -- only for selected files
  last_scan_id       TEXT,
  last_status        TEXT,                -- inventoried | selected | uploaded | skipped | failed
  last_extraction_route TEXT,
  PRIMARY KEY (scan_target_id, relative_path)
);

CREATE TABLE scans (
  id                 TEXT PRIMARY KEY,
  scan_target_id     TEXT NOT NULL,
  cloud_batch_id     TEXT,                -- the ingestion_batches.id returned by /folder/manifest
  started_at         INTEGER NOT NULL,
  completed_at       INTEGER,
  status             TEXT NOT NULL,
  files_inventoried  INTEGER NOT NULL DEFAULT 0,
  files_selected     INTEGER NOT NULL DEFAULT 0,
  files_uploaded     INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE bundles (
  id              TEXT PRIMARY KEY,
  scan_id         TEXT NOT NULL,
  sequence        INTEGER NOT NULL,       -- 1, 2, 3 for retry/resume tracking
  sha256          TEXT NOT NULL,
  size_bytes      INTEGER NOT NULL,
  status          TEXT NOT NULL,          -- pending | uploading | uploaded | failed
  last_attempt_at INTEGER,
  retry_count     INTEGER NOT NULL DEFAULT 0
);
```

The **delta-scan superpower** — "Only 17 new or changed files since last week" — comes from `local_index`. Files whose `quick_fingerprint` matches a row with `last_status='uploaded'` are skipped without re-hashing or re-classifying.

## Scanning pipeline

Mirrors the browser pipeline (Inventory → Prune → Package → Process), with extra steps the browser cannot do.

| # | Stage | Notes |
|---|---|---|
| 1 | **Enumerate** | Walk every `scan_target.root_path`. Skip system folders, hidden files, `node_modules`, `__pycache__`, `.git`, recycle bin, OS temp dirs. Apply user `filters_json`. |
| 2 | **Quick fingerprint** | `sha1(relative_path + size + mtime + first8B + last8B)`. ~30-40 ms per file even on spinning disks. |
| 3 | **Delta filter** | Drop files whose `quick_fingerprint` matches `local_index.last_status='uploaded'`. Telemetry: "skipped 8,432 unchanged files." |
| 4 | **Local content sniffing** | Per-format. PDF: `%PDF-` signature, `PdfPig.PdfDocument.Open()` for page count + text-layer probe + `PdfDocumentEncryptedException` → `is_encrypted=true`. DOCX/XLSX: `ZipArchive.Read` + `[Content_Types].xml` presence + first-row inspection. MSG/EML: parse headers + attachments. Plain text: encoding sniff. |
| 5 | **Score** | POST `/api/sources/connections/{id}/folder/manifest` with the metadata-only manifest. Single source of truth for classifier rules — no drift risk. Returns the same `ManifestScoredItem` shape the browser sees, including `band`, `recommendedAction`, `extractionRoute`. Cache `cloud_batch_id` in `scans`. |
| 6 | **Confirm** | Tray UI shows the prune table; user can adjust selection. Default = Strong + Likely auto-selected. Same UX as browser. |
| 7 | **Package** | Bundle selected files into ZIP archives. `max_files_per_bundle=25` (default), `max_bundle_bytes=200 MB`. Each bundle contains `manifest.json` (the scored items in this bundle) + `files/<relative_path>/...` preserving folder structure. SHA-256 the bundle. |
| 8 | **Upload** | POST each bundle to `/api/sources/connections/{id}/folder/bundles?batchId=…`. Resume via per-bundle SHA-256: a HEAD with `If-Match` short-circuits if the cloud already has it. Retry with exponential backoff. |
| 9 | **Reconcile** | On 200 OK: write `local_index.last_status='uploaded'` and `bundles.status='uploaded'`. On 4xx: `failed` + retry. On 5xx: pause 60 s and retry. |

## Optimizations (the differentiated speed story)

These are the ten things that should appear in product copy as "what makes PracticeX fast":

1. **Metadata-first wire format.** The agent never uploads bytes during the inventory phase — only `manifest.json` of `{path, size, mtime, quick_fingerprint, classifier scores}` hits the cloud during scoring. **The headline "PracticeX scanned 200 files without uploading them" is true here.**
2. **Incremental delta scan.** `local_index` makes "scan again next week" return in seconds for unchanged trees.
3. **Partial hashing.** Full SHA-256 is computed only for files selected for upload. The cheap `quick_fingerprint` powers everything before that.
4. **Local PDF text-layer probe.** PdfPig text extraction on the agent side sets `extraction_route='local_text'` definitively, so the cloud never wastes OCR budget on a file that already has text.
5. **First-3-pages classification.** For borderline scanned PDFs, the agent OCRs only pages 1–3 locally (Windows OCR or bundled Tesseract) before deciding whether to send the whole document.
6. **Page-count budgeting.** UI shows "this scan would process ~312 pages, avoided ~2,100" before the user confirms, so cost control is visible.
7. **Confidence-gated OCR.** Scores ≥ 60 → OCR allowed. 35–59 → user approval required. < 35 → never OCR. Prevents burning Document AI budget on receipt scans.
8. **Bandwidth control.** Optional upload windows ("after 6 PM"), per-Mbps cap, pause on metered connection.
9. **Resumable uploads.** Bundle chunking + per-bundle SHA short-circuit. Survives reboots, VPN drops, sleep cycles.
10. **Local privacy filter.** A configurable deny-list (PHI patterns, EHR exports, EOB blobs) stops files leaving the facility regardless of classifier score. Strong enterprise message: *"irrelevant or sensitive files never leave the building."*

Bonus: **priority queue.** Top-N highest-confidence bundles upload first, so reviewers see candidates in the cloud within seconds even on a slow link.

Bonus: **network-share heuristics.** For `\\server\share\…` roots the agent throttles SMB reads, prefers `FileShare.Read` semantics, and checks ACLs before walking a subtree.

## Bundling format

Each ZIP bundle is self-describing:

```
bundle_001.zip
├── manifest.json       # the scored items in this bundle (subset of the cloud manifest)
└── files/
    ├── Payers/BCBS/2024 Amendment 3.pdf
    ├── Vendors/Olympus/Service Renewal.docx
    └── Leases/Northside/Suite 310 Lease.pdf
```

`manifest.json` per bundle:

```json
{
  "agentId": "<guid>",
  "agentVersion": "1.0.0",
  "scanId": "<local guid>",
  "cloudBatchId": "<ingestion_batches.id>",
  "bundleSequence": 1,
  "items": [
    {
      "manifestItemId": "manifest:Payers/BCBS/2024 Amendment 3.pdf|1843221|1736884930",
      "relativePath": "Payers/BCBS/2024 Amendment 3.pdf",
      "sizeBytes": 1843221,
      "sha256": "ab12...",
      "extractionRoute": "local_text",
      "validityStatus": "valid",
      "hasTextLayer": true,
      "isEncrypted": false
    }
  ]
}
```

The cloud bundle endpoint (`POST /folder/bundles`) reuses the existing multipart shape today; the agent additionally sends a `bundle.zip` part plus `bundle.manifest.json` and the cloud unpacks if Content-Type is `application/zip`. Forward-compat — no breaking change to the browser's flat-file flow.

## Cloud-side surface (specced, not built in this PR)

The browser flow uses the existing per-connection routes. The agent gains a thin parallel surface for identity and resumable bundle delivery:

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/agents/register` | Body `{ tenantId, facilityId, deviceFingerprint, agentVersion }` → returns `{ agentId, agentToken }`. One-time per machine. |
| `POST` | `/api/agents/{id}/heartbeat` | Body `{ agentVersion, lastScanAt, pendingBundles }`. Used for "agent online" badge in the cloud UI. |
| `POST` | `/api/agents/{id}/scans` | Body `{ rootPaths, filters, connectionId }`. Returns `scanId`. |
| `POST` | `/api/agents/{id}/bundles?scanId={scanId}` | Multipart `bundle.zip` + `bundle.manifest.json`. Reuses the existing `/folder/bundles` orchestration internally. |
| `GET` | `/api/agents/{id}/scans/{scanId}` | Returns batch status + per-bundle progress. |

All persistence reuses the existing tables. Agent identity is recorded on `audit_events.actor_type='agent'` with `metadata_json.agent_id` and `metadata_json.agent_version` so every batch can be traced to a specific machine + version.

## Security & compliance

- OAuth device-code only — no client secrets shipped in the binary.
- Refresh token in DPAPI-encrypted file under `%LOCALAPPDATA%\PracticeX\agent\`.
- All cloud calls TLS 1.2+ with cert pinning to `*.practicex.app`.
- Agent never reads file *content* unless the file is selected for processing (bytes streamed straight into the bundle ZIP, no in-memory scratch copy).
- All scans audited cloud-side with agent identity.
- Admin can revoke the agent token from the web UI; agent self-disables on a 401 streak.
- Auto-update with signature verification.
- Logs do not contain document content; only file paths, scores, reason codes, and counters.
- Local privacy deny-list ships as a default config (PHI patterns, EHR exports). Customers can extend.

## Phase 1 CLI — what ships in this PR

Project: `src/PracticeX.Agent.Cli/`. Single-file `practicex-agent.exe` (or `practicex-agent` on Linux/macOS) built via `dotnet publish -c Release`. Pure HTTP client — references `PracticeX.Domain` only; no `PracticeX.Infrastructure`, no `PracticeX.Api`.

### Command

```
practicex-agent scan \
  --root "C:\Users\harek\SYNEXAR INC\Fundraising" \
  --connection-id 11111111-2222-3333-4444-555555555555 \
  --api https://localhost:7100 \
  --token $env:PRACTICEX_TOKEN \
  --auto-select strong,likely \
  [--dry-run]
```

| Flag | Default | Purpose |
|---|---|---|
| `--root` | required | Folder to scan recursively. |
| `--connection-id` | required | Existing `source_connections.id` of type `local_folder`. CLI does not auto-create — user creates the connection once via the web app. |
| `--api` | `https://localhost:7100` | API base URL. |
| `--token` | reads `PRACTICEX_TOKEN` env var, optional in dev | Bearer token. Sent as `Authorization: Bearer …` when present. The dev API still uses `DemoCurrentUserContext`, so the value is currently a no-op pass-through; Phase 2 wires real auth without changing the CLI surface. |
| `--auto-select` | `strong,likely` | Bands to upload. `strong,likely,possible` casts wider; `strong` is conservative. |
| `--dry-run` | off | Run the manifest scan and print the report; skip the bundle upload. |
| `--insecure` | off | Skip TLS validation (dev only — local self-signed cert). |

### Flow

1. Walk `--root` recursively. Skip system / hidden / `node_modules` / `.git` / `*.tmp` / `*.log`. Build `List<ManifestItemDto>` of `{relativePath, name, sizeBytes, lastModifiedUtc, mimeType?}`.
2. `POST /api/sources/connections/{id}/folder/manifest` with the list. Parse `ManifestScanResponse`.
3. Print Strong / Likely / Possible / Skipped counts and the auto-selection plan.
4. If `--dry-run`, exit 0. Otherwise stream the selected files as a multipart POST to `/folder/bundles?batchId=…` (per-part `paths[i]` and `manifestItemIds[i]`).
5. Print the final `IngestionBatchSummaryDto` — candidates created, duplicates skipped, errors.

### Exit codes

- `0` — success.
- `1` — partial (any item failed but the batch completed).
- `2` — transport / auth / configuration error.

### Phase 1 acceptance — `C:\Users\harek\SYNEXAR INC\Fundraising`

- CLI inventories the tree without uploading bytes during scan (only JSON hits `/folder/manifest`, no multipart yet).
- A `phase='manifest'` batch appears via `GET /api/sources/batches`.
- After bundle upload, the same batch shows `phase='complete'`; selected files in `GET /api/sources/batches/{id}` carry `extraction_route` and `validity_status` populated.
- A second run reports duplicates via per-tenant SHA dedupe (`ix_document_assets_tenant_id_sha256`), even without a local SQLite cache.

## Roadmap

| Phase | Scope | Verification target |
|---|---|---|
| **0** *(prior PR)* | Cloud-side manifest + bundle endpoints. Browser uses them. | `C:\Users\harek\SYNEXAR INC\Fundraising` via the browser at http://localhost:5173 |
| **1** *(this PR)* | Agent CLI: `practicex-agent scan --root "C:\Users\harek\SYNEXAR INC"`. One-shot scanner; no SQLite cache, no tray UI. Calls existing manifest + bundle endpoints. Validates the wire format end-to-end. | `C:\Users\harek\SYNEXAR INC` via CLI; verify `phase='manifest'` batch then `phase='complete'` batch in `GET /api/sources/batches`. |
| **2** | SQLite cache + delta scans. Quick-fingerprint dedupe. Resumable bundle uploads. Confidence-gated OCR. Local PDF text-layer detection via PdfPig. | Re-run scan against the same root, expect "12 new files of 1,847" telemetry. |
| **3** | WinUI 3 tray UI. Scheduled scans. Network-share support. Bandwidth controls. Local privacy deny-list. | Tray UI shows "Last scan: 17 new files. 12 candidates. 5 minutes ago." |
| **4** | Windows Service variant + signed MSI installer. Auto-update. IT-grade logging. | Customer pilot at a real facility. |

## Browser vs. Facility Agent — capability matrix

| Capability | Browser flow | Facility agent |
|---|:--:|:--:|
| Demo speed (zero install) | ✅ | ❌ |
| Network share access | ⚠️ partial | ✅ |
| Tens of thousands of files | ⚠️ slow | ✅ |
| Local content sniffing (PDF text layer, encrypted) | ❌ (server only) | ✅ (before upload) |
| Background / scheduled scans | ❌ | ✅ |
| Resumable uploads across days | ❌ | ✅ |
| Delta scans (only changed files) | ❌ | ✅ |
| Bandwidth control | ❌ | ✅ |
| Local privacy filter | ❌ | ✅ |
| First-week build cost | low | medium-high |

The right product story is *both*. The browser is for low-friction demo + small practices; the agent is for serious enterprise discovery. Sharing the same manifest + bundle APIs means there is exactly one ingestion pipeline.
