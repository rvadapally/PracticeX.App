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
    credentials: 'include',
    ...init,
  });
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
    employee_agreement: 'Employee agreement',
    processor_agreement: 'Processor agreement',
    amendment: 'Amendment',
    fee_schedule: 'Fee schedule',
    other: 'Other',
    unknown: 'Unclassified',
  }[type] ?? type;
}
