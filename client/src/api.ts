// Types mirror the server's JSON contracts (camelCased by ASP.NET Core).

export type ConnectorType = 'ItemService' | 'Simulated';
export type DiffStatus = 'New' | 'Modified' | 'Deleted' | 'Conflict' | 'Unchanged';
export type ConflictResolution = 'Unresolved' | 'UseSource' | 'Skip';
export type JobStatus =
  | 'Queued' | 'Running' | 'AwaitingReview' | 'Applying' | 'Completed' | 'Failed' | 'Cancelled';
export type SyncPhase =
  | 'Connecting' | 'DiscoveringSource' | 'DiscoveringTarget' | 'Comparing' | 'Applying' | 'Snapshotting' | 'Done';

export interface Environment {
  id: string;
  name: string;
  baseUrl: string;
  username: string;
  domain: string;
  databases: string[];
  connectorType: ConnectorType;
  color: string;
  hasPassword: boolean;
}

export interface SyncScope {
  content: boolean;
  media: boolean;
  templates: boolean;
  layout: boolean;
  rootPath: string;
  deleteOrphans: boolean;
}

export interface SyncJobRequest {
  sourceEnvironmentId: string;
  sourceDatabase: string;
  targetEnvironmentId: string;
  targetDatabase: string;
  scope: SyncScope;
}

export interface SyncCounters {
  sourceItems: number;
  targetItems: number;
  new: number;
  modified: number;
  deleted: number;
  conflicts: number;
  unchanged: number;
  applied: number;
  skipped: number;
  errors: number;
}

export interface JobSummary {
  id: string;
  sourceEnvironmentName: string;
  sourceDatabase: string;
  targetEnvironmentName: string;
  targetDatabase: string;
  rootPath: string;
  status: JobStatus;
  phase: SyncPhase;
  processed: number;
  total: number;
  currentItem?: string | null;
  counters: SyncCounters;
  error?: string | null;
  created: string;
  started?: string | null;
  finished?: string | null;
}

export interface FieldDiff {
  fieldName: string;
  sourceValue?: string | null;
  targetValue?: string | null;
}

export interface ItemDiff {
  itemId: string;
  name: string;
  path: string;
  templateName: string;
  isMedia: boolean;
  status: DiffStatus;
  resolution: ConflictResolution;
  fieldDiffs: FieldDiff[];
  sourceUpdated?: string | null;
  targetUpdated?: string | null;
}

export interface LogEntry {
  time: string;
  level: 'info' | 'warn' | 'error' | 'success';
  message: string;
}

async function http<T>(url: string, init?: RequestInit): Promise<T> {
  const resp = await fetch(url, {
    headers: { 'Content-Type': 'application/json' },
    ...init
  });
  if (!resp.ok) {
    let message = `${resp.status} ${resp.statusText}`;
    try {
      const body = await resp.json();
      if (body?.error) message = body.error;
    } catch { /* not json */ }
    throw new Error(message);
  }
  if (resp.status === 204) return undefined as T;
  return resp.json();
}

export const api = {
  environments: {
    list: () => http<Environment[]>('/api/environments'),
    create: (env: Partial<Environment> & { password?: string }) =>
      http<Environment>('/api/environments', { method: 'POST', body: JSON.stringify(env) }),
    update: (id: string, env: Partial<Environment> & { password?: string }) =>
      http<Environment>(`/api/environments/${id}`, { method: 'PUT', body: JSON.stringify(env) }),
    remove: (id: string) => http<void>(`/api/environments/${id}`, { method: 'DELETE' }),
    test: (id: string) =>
      http<{ success: boolean; message: string }>(`/api/environments/${id}/test`, { method: 'POST' })
  },
  sync: {
    preview: (request: SyncJobRequest) =>
      http<JobSummary>('/api/sync/preview', { method: 'POST', body: JSON.stringify(request) }),
    apply: (jobId: string, resolutions: Record<string, ConflictResolution>) =>
      http<JobSummary>(`/api/sync/${jobId}/apply`, {
        method: 'POST',
        body: JSON.stringify({ resolutions })
      }),
    cancel: (jobId: string) => http<void>(`/api/sync/${jobId}/cancel`, { method: 'POST' })
  },
  jobs: {
    list: () => http<JobSummary[]>('/api/jobs'),
    get: (id: string) => http<JobSummary>(`/api/jobs/${id}`),
    diff: (id: string, includeUnchanged = false) =>
      http<ItemDiff[]>(`/api/jobs/${id}/diff?includeUnchanged=${includeUnchanged}`),
    log: (id: string) => http<LogEntry[]>(`/api/jobs/${id}/log`)
  }
};
