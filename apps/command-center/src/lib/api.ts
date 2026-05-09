const API_BASE = (import.meta.env.VITE_API_BASE as string | undefined) ?? '/api';

export interface ApiError {
  status: number;
  title?: string;
  detail?: string;
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, {
    headers: {
      Accept: 'application/json',
      ...(init.body && !(init.body instanceof FormData) ? { 'Content-Type': 'application/json' } : {}),
      ...(init.headers ?? {}),
    },
    // Must be 'include' — Cloudflare Access redirects unauthenticated
    // requests cross-origin to truwit.cloudflareaccess.com/login. 'include'
    // is the only mode where the browser carries cookies through that
    // redirect so re-auth can complete; 'same-origin' breaks the OAuth
    // flow and produces CORS errors on every call.
    credentials: 'include',
    ...init,
  });

  // Cloudflare Access "session expired" path: the redirect to
  // truwit.cloudflareaccess.com/login lands on a 200 OK HTML page (the
  // OTP sign-in screen). Without this guard, res.ok is true, .json()
  // throws on the HTML, and every page latches onto MaintenancePage —
  // auto-retry fires forever against the same redirect. Detect the
  // content-type mismatch and force a top-level navigation so Access
  // can refresh the cookie inline; the SPA reloads against a fresh
  // session afterwards.
  const contentType = res.headers.get('content-type') ?? '';
  if (
    res.ok &&
    res.status !== 204 &&
    !contentType.toLowerCase().includes('json') &&
    typeof window !== 'undefined'
  ) {
    window.location.reload();
    // Give the navigation a tick to take effect before throwing.
    return new Promise<T>(() => {});
  }

  if (!res.ok) {
    let body: unknown = undefined;
    try {
      body = await res.json();
    } catch {
      // ignore
    }
    const err: ApiError = {
      status: res.status,
      title: (body as { title?: string } | undefined)?.title,
      detail: (body as { detail?: string } | undefined)?.detail,
    };
    throw err;
  }
  if (res.status === 204) {
    return undefined as T;
  }
  return (await res.json()) as T;
}

export interface ConnectorDescriptor {
  sourceType: string;
  displayName: string;
  summary: string;
  authMode: 'none' | 'oauth' | 'apikey';
  isReadOnly: boolean;
  status: string;
  supportedMimeTypes: string[];
}

export interface SourceConnection {
  id: string;
  sourceType: string;
  status: 'draft' | 'awaiting_auth' | 'connected' | 'error' | 'disabled';
  displayName: string | null;
  oauthSubject: string | null;
  lastSyncAt: string | null;
  createdAt: string;
  lastError: string | null;
}

export interface IngestionItem {
  sourceObjectId: string;
  documentAssetId: string | null;
  documentCandidateId: string | null;
  name: string;
  candidateType: string;
  confidence: number;
  reasonCodes: string[];
  status: string;
  relativePath: string | null;
}

export interface IngestionBatchSummary {
  batchId: string;
  fileCount: number;
  candidateCount: number;
  skippedCount: number;
  errorCount: number;
  status: string;
  items: IngestionItem[];
}

export interface IngestionBatch {
  id: string;
  sourceType: string;
  sourceConnectionId: string | null;
  status: string;
  fileCount: number;
  candidateCount: number;
  skippedCount: number;
  errorCount: number;
  createdAt: string;
  completedAt: string | null;
  notes: string | null;
}

export interface DocumentCandidate {
  id: string;
  sourceObjectId: string | null;
  documentAssetId: string;
  candidateType: string;
  confidence: number;
  status: string;
  reasonCodes: string[];
  classifierVersion: string;
  originFilename: string | null;
  relativePath: string | null;
  counterpartyHint: string | null;
  createdAt: string;
}

export interface OutlookOAuthStartResponse {
  authorizeUrl: string;
  state: string;
}

export interface ManifestItem {
  relativePath: string;
  name: string;
  sizeBytes: number;
  lastModifiedUtc: string;
  mimeType?: string | null;
}

export interface ManifestScoredItem {
  manifestItemId: string;
  relativePath: string;
  name: string;
  sizeBytes: number;
  candidateType: string;
  confidence: number;
  reasonCodes: string[];
  recommendedAction: 'select' | 'optional' | 'skip';
  band: 'strong' | 'likely' | 'possible' | 'skipped';
  counterpartyHint: string | null;
}

export interface ManifestScanResponse {
  batchId: string;
  phase: string;
  totalItems: number;
  strongCount: number;
  likelyCount: number;
  possibleCount: number;
  skippedCount: number;
  items: ManifestScoredItem[];
}

export interface QueuedFile {
  file: File;
  relativePath: string;
}

export interface SelectedManifestFile extends QueuedFile {
  manifestItemId: string;
}

export const sourcesApi = {
  listConnectors: () => request<ConnectorDescriptor[]>('/sources/connectors'),
  listConnections: () => request<SourceConnection[]>('/sources/connections'),
  createConnection: (sourceType: string, displayName?: string) =>
    request<SourceConnection>('/sources/connections', {
      method: 'POST',
      body: JSON.stringify({ sourceType, displayName }),
    }),
  deleteConnection: (id: string) =>
    request<void>(`/sources/connections/${id}`, { method: 'DELETE' }),

  uploadFolder: (connectionId: string, files: { file: File; relativePath: string }[], notes?: string) => {
    const form = new FormData();
    files.forEach((entry, i) => {
      form.append('file', entry.file, entry.file.name);
      form.append(`paths[${i}]`, entry.relativePath);
    });
    if (notes) {
      form.append('notes', notes);
    }
    return request<IngestionBatchSummary>(`/sources/connections/${connectionId}/folder/scan`, {
      method: 'POST',
      body: form,
    });
  },

  scanManifest: (connectionId: string, items: ManifestItem[], notes?: string) =>
    request<ManifestScanResponse>(`/sources/connections/${connectionId}/folder/manifest`, {
      method: 'POST',
      body: JSON.stringify({ items, notes }),
    }),

  uploadBundle: (connectionId: string, batchId: string, files: SelectedManifestFile[], notes?: string) => {
    const form = new FormData();
    files.forEach((entry, i) => {
      form.append('file', entry.file, entry.file.name);
      form.append(`paths[${i}]`, entry.relativePath);
      form.append(`manifestItemIds[${i}]`, entry.manifestItemId);
    });
    if (notes) {
      form.append('notes', notes);
    }
    return request<IngestionBatchSummary>(
      `/sources/connections/${connectionId}/folder/bundles?batchId=${encodeURIComponent(batchId)}`,
      { method: 'POST', body: form },
    );
  },

  startOutlookOAuth: (connectionId: string) =>
    request<OutlookOAuthStartResponse>(`/sources/connections/${connectionId}/outlook/oauth/start`),

  scanOutlook: (connectionId: string, top = 25, since?: string) =>
    request<IngestionBatchSummary>(`/sources/connections/${connectionId}/outlook/scan`, {
      method: 'POST',
      body: JSON.stringify({ top, since }),
    }),

  listBatches: (limit = 20) => request<IngestionBatch[]>(`/sources/batches?limit=${limit}`),
  deleteBatch: (batchId: string) =>
    request<void>(`/sources/batches/${batchId}`, { method: 'DELETE' }),
  deleteAllBatches: () =>
    request<{ deletedCount: number }>(`/sources/batches`, { method: 'DELETE' }),
  listCandidates: (params: { status?: string; batchId?: string; limit?: number } = {}) => {
    const q = new URLSearchParams();
    if (params.status) q.append('status', params.status);
    if (params.batchId) q.append('batchId', params.batchId);
    if (params.limit) q.append('limit', String(params.limit));
    const suffix = q.toString();
    return request<DocumentCandidate[]>(`/sources/candidates${suffix ? `?${suffix}` : ''}`);
  },
  queueReview: (candidateId: string) =>
    request<void>(`/sources/candidates/${candidateId}/queue-review`, { method: 'POST' }),
  retryCandidate: (candidateId: string) =>
    request<void>(`/sources/candidates/${candidateId}/retry`, { method: 'POST' }),
};

export function readableReason(code: string): string {
  return {
    unsupported_mime_type: 'Unsupported file type',
    duplicate_content: 'Duplicate of an existing document',
    empty_file: 'Empty file',
    exceeds_size_limit: 'Exceeds size limit',
    likely_contract: 'Looks like a contract',
    ambiguous_type: 'Type unclear',
    filename_contract_keywords: 'Filename mentions contract',
    filename_amendment: 'Filename mentions amendment',
    filename_rate_schedule: 'Filename mentions fee schedule',
    folder_hint_payer: 'Folder hint: payer',
    folder_hint_lease: 'Folder hint: lease',
    outlook_subject_keywords: 'Subject mentions contract',
    outlook_sender_domain: 'Sender appears legal/contracts',
  }[code] ?? code;
}

export function readableCandidateType(type: string): string {
  return {
    payer_contract: 'Payer contract',
    vendor_contract: 'Vendor agreement',
    lease: 'Lease',
    lease_amendment: 'Lease amendment',
    lease_loi: 'Lease LOI',
    employee_agreement: 'Employee agreement',
    processor_agreement: 'Processor agreement',
    amendment: 'Amendment',
    fee_schedule: 'Fee schedule',
    nda: 'Non-disclosure agreement',
    bylaws: 'Bylaws',
    call_coverage_agreement: 'Call coverage agreement',
    service_agreement: 'Service agreement',
    operational_data: 'Operational data',
    board_resolution: 'Board resolution',
    equity_grant: 'Equity grant',
    ip_assignment: 'IP assignment',
    corp_formation: 'Corporate formation',
    regulatory_filing: 'Regulatory filing',
    privacy_policy: 'Privacy policy',
    terms_of_service: 'Terms of service',
    term_sheet: 'Term sheet',
    founders_meeting: 'Founders meeting',
    other: 'Other',
    unknown: 'Unclassified',
  }[type] ?? type;
}

// ==========================================================================
// Slice 9: premium analysis surface (/api/analysis/*)
// ==========================================================================

export interface PortfolioFamily {
  family: string;
  documentCount: number;
  activeCount: number;
  expiredCount: number;
  totalPages: number;
  totalSizeMb: number;
  docIntelPagesUsed: number;
  documents: string[];
}

export type ExpirationStatus = 'active' | 'expired' | 'unknown';

export interface PortfolioDocument {
  documentAssetId: string;
  documentCandidateId: string;
  fileName: string;
  candidateType: string;
  family: string;
  extractedSubtype: string | null;
  confidence: number;
  pageCount: number | null;
  sizeBytes: number;
  hasTextLayer: boolean | null;
  usedDocIntelligence: boolean;
  layoutPageCount: number | null;
  extractionStatus: string | null;
  extractionSchemaVersion: string | null;
  isTemplate: boolean | null;
  isExecuted: boolean | null;
  expirationDate: string | null;
  expirationStatus: ExpirationStatus;
  facilityId: string | null;
  propertyAddress: string | null;
  effectiveDate: string | null;
  createdAt: string;
}

export interface Portfolio {
  tenantId: string;
  totalDocuments: number;
  activeDocuments: number;
  expiredDocuments: number;
  unknownDocuments: number;
  totalPages: number;
  totalSizeMb: number;
  docIntelPagesProcessed: number;
  estimatedDocIntelCostUsd: number;
  families: PortfolioFamily[];
  documents: PortfolioDocument[];
}

export interface ExtractedField {
  name: string;
  value: string | null;
  confidence: number;
  sourceCitation: string | null;
}

export interface DocumentDetail {
  documentAssetId: string;
  fileName: string;
  candidateType: string | null;
  confidence: number | null;
  extractedSubtype: string | null;
  extractedSchemaVersion: string | null;
  extractorName: string | null;
  extractionStatus: string | null;
  isTemplate: boolean | null;
  isExecuted: boolean | null;
  pageCount: number | null;
  hasTextLayer: boolean | null;
  layoutProvider: string | null;
  layoutModel: string | null;
  layoutPageCount: number | null;
  layoutSnippet: string | null;
  extractedFields: {
    fields: ExtractedField[];
    reasonCodes: string[];
  } | null;
  llmExtractedFields: {
    fields: ExtractedField[];
    reasonCodes: string[];
  } | null;
  llmModel: string | null;
  llmExtractedAt: string | null;
  headline: Record<string, string | number | boolean | null> | null;
  fieldCitations: Record<string, string> | null;
  narrativeBriefMd: string | null;
  narrativeModel: string | null;
  narrativeExtractedAt: string | null;
  createdAt: string;
}

export interface LlmExtractionResult {
  status: string;
  model: string;
  tokensIn: number;
  tokensOut: number;
  latencyMs: number;
  json: string;
}

export interface AmendmentChain {
  parentDocumentTitle: string;
  amendments: string[];
}

export interface PortfolioInsights {
  totalRentableSqft: number | null;
  uniqueLandlords: string[];
  uniqueTenants: string[];
  uniqueCounterparties: string[];
  amendmentChains: AmendmentChain[];
  documentAddresses: Record<string, string>;
}

export interface DashboardStats {
  tenantId: string;
  documents: number;
  candidates: number;
  contractsTracked: number;
  reviewQueueDepth: number;
  ingestionBatches: number;
  totalSizeMb: number;
  docIntelPagesProcessed: number;
  estimatedDocIntelCostUsd: number;
}

export interface ReviewQueueItem {
  candidateId: string;
  documentAssetId: string;
  fileName: string;
  candidateType: string;
  extractedSubtype: string | null;
  confidence: number;
  usedDocIntelligence: boolean;
  extractionStatus: string | null;
  createdAt: string;
}

export interface CurrentUser {
  userId: string;
  name: string;
  email: string;
  initials: string;
  tenantId: string;
  tenantName: string;
}

export interface Facility {
  id: string;
  code: string;
  name: string;
  status: string;
  documentCount: number;
}

export interface PortfolioBrief {
  briefMd: string;
  model: string | null;
  sourceDocCount: number;
  tokensIn: number;
  tokensOut: number;
  latencyMs: number;
  generatedAt: string;
}

// Slice 19 — Renewal Engine
export interface RenewalAction {
  documentAssetId: string;
  fileName: string;
  family: string;
  counterparty: string | null;
  actionType: string;
  description: string;
  actionDate: string; // yyyy-MM-dd
  daysFromToday: number;
  severity: 'overdue' | 'high' | 'medium' | 'low' | 'info';
}

export interface RenewalBucket {
  key: string;
  label: string;
  items: RenewalAction[];
}

export interface RenewalCounts {
  overdue: number;
  within30: number;
  within90: number;
  within180: number;
  total: number;
}

export interface RenewalsResponse {
  generatedAt: string;
  today: string;
  counts: RenewalCounts;
  buckets: RenewalBucket[];
  actions: RenewalAction[];
}

// Slice 17 — Entity Graph
export interface EntityGraphNode {
  id: string;
  label: string;
  type: 'person' | 'organization' | 'asset' | 'document';
  family?: string | null;
  documentAssetId?: string | null;
  size: number;
}

export interface EntityGraphLink {
  source: string;
  target: string;
  relation: string;
  documentAssetId?: string | null;
  inferred?: boolean;
}

export interface EntityGraph {
  nodes: EntityGraphNode[];
  links: EntityGraphLink[];
}

// Slice 20 — Legal Advisor Agent (premium "Counsel's Memo" surface)
export interface LegalMemoResult {
  status: string;
  model: string;
  riskScore: number | null;
  tokensIn: number;
  tokensOut: number;
  latencyMs: number;
  memoMd: string;
  memoJson: string;
}

export interface LegalAdvisorPortfolioCounts {
  total: number;
  withMemo: number;
  severe: number;
  high: number;
  elevated: number;
  modest: number;
  low: number;
}

export interface LegalAdvisorPortfolioRow {
  documentAssetId: string;
  fileName: string;
  candidateType: string;
  family: string;
  isExecuted: boolean | null;
  memoStatus: string | null;
  riskScore: number | null;
  riskRating: string | null;
  headline: string | null;
  topIssueTitle: string | null;
  memoModel: string | null;
  memoExtractedAt: string | null;
}

export interface LegalAdvisorPortfolio {
  counts: LegalAdvisorPortfolioCounts;
  rows: LegalAdvisorPortfolioRow[];
  disclaimer: string;
}

export interface CounselBrief {
  briefMd: string;
  model: string | null;
  sourceDocCount: number;
  tokensIn: number;
  tokensOut: number;
  latencyMs: number;
  generatedAt: string;
  disclaimer: string;
}

export interface LegalMemoIssue {
  rank: number;
  severity: 'CRITICAL' | 'HIGH' | 'MEDIUM' | 'LOW';
  category: string;
  title: string;
  where: string;
  risk: string;
  non_standard_reason: string;
}

export interface LegalMemoRedline {
  issue_rank: number;
  current_language: string;
  proposed_language: string;
  rationale: string;
}

export interface LegalMemoActionItem {
  rank: number;
  owner: string;
  action: string;
  by: string;
  why_now: string;
  done_looks_like: string;
}

export interface LegalMemoStructured {
  risk_score?: number;
  risk_rating?: string;
  headline?: string;
  issues?: LegalMemoIssue[];
  redlines?: LegalMemoRedline[];
  operational_watch_items?: string[];
  material_disclosures?: {
    board?: string[];
    insurer?: string[];
    lender?: string[];
    ma_due_diligence?: string[];
    regulators?: string[];
  };
  counterparty_posture?: string;
  action_items?: LegalMemoActionItem[];
  plain_english_summary?: string;
}

export const legalAdvisorApi = {
  getPortfolio: () =>
    request<LegalAdvisorPortfolio>(`/legal-advisor/portfolio?_t=${Date.now()}`),
  getMemo: (assetId: string) =>
    request<LegalMemoResult>(`/legal-advisor/memos/${assetId}`),
  generateMemo: (assetId: string) =>
    request<LegalMemoResult>(`/legal-advisor/memos/${assetId}`, { method: 'POST' }),
  batchGenerate: (force = false) =>
    request<BatchExtractionResult>(
      `/legal-advisor/memos-batch${force ? '?force=true' : ''}`,
      { method: 'POST' },
    ),
  getCounselBrief: () =>
    request<CounselBrief>(`/legal-advisor/counsel-brief?_t=${Date.now()}`),
  generateCounselBrief: () =>
    request<CounselBrief>('/legal-advisor/counsel-brief', { method: 'POST' }),
};

export function parseLegalMemoJson(memoJson: string): LegalMemoStructured | null {
  try {
    return JSON.parse(memoJson) as LegalMemoStructured;
  } catch {
    return null;
  }
}

export const LEGAL_ADVISOR_DISCLAIMER =
  'AI-generated legal analysis for informational purposes only. ' +
  'This is not legal advice and does not establish an attorney-client ' +
  'relationship. Engage licensed counsel before relying on any ' +
  'conclusion or taking action based on this output.';

export const analysisApi = {
  getPortfolio: (facilityId?: string) =>
    request<Portfolio>(`/analysis/portfolio${facilityId ? `?facilityId=${encodeURIComponent(facilityId)}` : ''}`),
  getInsights: () => request<PortfolioInsights>('/analysis/insights'),
  getDocument: (assetId: string) => request<DocumentDetail>(`/analysis/documents/${assetId}`),
  getDashboard: () => request<DashboardStats>('/analysis/dashboard'),
  getReviewQueue: () => request<ReviewQueueItem[]>('/analysis/review-queue'),
  getCurrentUser: () => request<CurrentUser>('/analysis/me'),
  getFacilities: () => request<Facility[]>('/analysis/facilities'),
  llmExtract: (assetId: string) =>
    request<LlmExtractionResult>(`/analysis/documents/${assetId}/llm-extract`, { method: 'POST' }),
  llmExtractBatch: (force = false) =>
    request<BatchExtractionResult>(`/analysis/llm-extract-batch${force ? '?force=true' : ''}`, { method: 'POST' }),
  getPortfolioBrief: () =>
    // Cache-buster: iPad Safari ITP / mobile WebKit was returning stuck
    // "Load failed" on this endpoint after earlier transient errors. Adding
    // a per-call timestamp guarantees a fresh URL on every fetch.
    request<PortfolioBrief>(`/analysis/portfolio-brief?_t=${Date.now()}`),
  generatePortfolioBrief: () =>
    request<PortfolioBrief>('/analysis/portfolio-brief', { method: 'POST' }),
  getRenewals: () => request<RenewalsResponse>(`/analysis/renewals?_t=${Date.now()}`),
  getEntityGraph: () => request<EntityGraph>(`/analysis/entity-graph?_t=${Date.now()}`),
};

export interface BatchExtractionResult {
  total: number;
  refined: number;
  skipped: number;
  failed: number;
  totalTokensIn: number;
  totalTokensOut: number;
  latencyMs: number;
  notes: string | null;
}

export function readableRelativeTime(iso: string): string {
  const then = new Date(iso).getTime();
  const now = Date.now();
  const sec = Math.max(0, Math.floor((now - then) / 1000));
  if (sec < 60) return 'just now';
  if (sec < 3600) return `${Math.floor(sec / 60)}m ago`;
  if (sec < 86400) return `${Math.floor(sec / 3600)}h ago`;
  return `${Math.floor(sec / 86400)}d ago`;
}

export function readableFamily(family: string): string {
  return {
    lease: 'Real-estate leases',
    employment_governance: 'Employment & governance',
    nda: 'NDAs',
    governance: 'Corporate governance',
    scheduling: 'Scheduling agreements',
    vendor_services: 'Vendor & services',
    payer: 'Payer contracts',
    compliance: 'Compliance / BAA',
    fee_schedule: 'Fee schedules',
    operational_data: 'Operational records',
    corp_governance: 'Board & corporate governance',
    equity: 'Equity & financing',
    ip: 'IP assignments',
    corp_formation: 'Corporate formation',
    regulatory: 'Regulatory filings',
    policy: 'Public policies',
    unclassified: 'Unclassified',
  }[family] ?? family;
}
