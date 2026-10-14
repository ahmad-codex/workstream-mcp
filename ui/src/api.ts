export interface Project {
  id: string;
  slug: string;
  displayName: string;
  activePlanCount: number;
  myOpenClaims: number;
}

export interface PlanSummary {
  id: string;
  name: string;
  planType: string;
  status: string;
  taskCounts: Record<string, number>;
  boardUrl: string | null;
}

export interface Task {
  id: string;
  externalKey: string;
  title: string;
  status: string;
  priority: number;
  phase: string | null;
  claimActorId: string | null;
  claimActor: string | null;
  claimRole: string | null;
  claimedAt: string | null;
  claimedUntil: string | null;
  dependsOn: string[];
  githubIssue: number | null;
  updatedAt: string;
}

export interface Finding {
  id: string;
  taskId: string;
  externalKey: string;
  severity: string | null;
  symptom: string | null;
  status: string;
  claimActor: string | null;
  claimRole: string | null;
  claimedUntil: string | null;
  updatedAt: string;
}

export interface PlanEvent {
  id: number;
  at: string;
  entityType: string;
  entityId: string;
  eventType: string;
  fromState: string | null;
  toState: string | null;
}

export interface StuckItem {
  taskId: string;
  since: string;
  status: string;
  claimHolder: string | null;
}

export interface Board {
  serverTime: string;
  plan: { id: string; name: string; status: string; objective: string | null };
  byStatus: Record<string, number>;
  stuckWork: StuckItem[];
  recentEvents: PlanEvent[];
  tasks: Task[];
  findings: Finding[];
}

export interface TimelineEntry {
  id: number;
  at: string;
  actor: string | null;
  eventType: string;
  fromState: string | null;
  toState: string | null;
  payload: string;
}

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

// Paths are relative to /{token}/ui/, so "../api" is /{token}/api. In dev the Vite proxy
// serves /api with the token added.
const base = import.meta.env.DEV ? '/api' : '../api';

async function get<T>(path: string): Promise<T> {
  const res = await fetch(`${base}${path}`, { headers: { Accept: 'application/json' } });
  if (res.status === 404) throw new ApiError(404, 'Not found. Check the token in the URL.');
  const body = await res.json().catch(() => null);
  if (!res.ok || !body?.ok) throw new ApiError(res.status, body?.error?.message ?? `Request failed (${res.status})`);
  return body.data as T;
}

export const api = {
  projects: () => get<{ projects: Project[] }>('/projects').then((d) => d.projects),
  plans: (projectId: string) => get<{ plans: PlanSummary[] }>(`/projects/${projectId}/plans`).then((d) => d.plans),
  board: (planId: string) => get<Board>(`/plans/${planId}/board`),
  events: (entityType: string, entityId: string) => get<TimelineEntry[]>(`/events/${entityType}/${entityId}`),
};
