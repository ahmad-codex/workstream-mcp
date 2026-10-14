export const COLUMNS: { key: string; label: string; statuses: string[]; optional?: boolean }[] = [
  { key: 'pending', label: 'Pending', statuses: ['pending'] },
  { key: 'claimed', label: 'Claimed', statuses: ['claimed'] },
  { key: 'in_progress', label: 'In progress', statuses: ['in_progress'] },
  { key: 'review', label: 'Review', statuses: ['review'] },
  { key: 'needs_human_review', label: 'Needs a human', statuses: ['needs_human_review'], optional: true },
  { key: 'parked', label: 'Blocked or deferred', statuses: ['blocked', 'deferred'], optional: true },
  { key: 'done', label: 'Done', statuses: ['done', 'skipped', 'out_of_scope'] },
];

export function label(state: string | null | undefined): string {
  if (!state) return '';
  return state.replace(/_/g, ' ');
}

export function ago(iso: string, now: number): string {
  const s = Math.max(0, Math.round((now - Date.parse(iso)) / 1000));
  if (s < 60) return `${s}s ago`;
  const m = Math.round(s / 60);
  if (m < 60) return `${m}m ago`;
  const h = Math.round(m / 60);
  if (h < 48) return `${h}h ago`;
  return `${Math.round(h / 24)}d ago`;
}

export function remaining(untilIso: string, now: number): { text: string; expired: boolean } {
  const s = Math.round((Date.parse(untilIso) - now) / 1000);
  if (s <= 0) return { text: 'expired', expired: true };
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  const text = h > 0 ? `${h}h ${m}m` : m > 0 ? `${m}m ${String(sec).padStart(2, '0')}s` : `${sec}s`;
  return { text, expired: false };
}

/** Share of the claim window still left, 0..1. */
export function ttlFraction(fromIso: string | null, untilIso: string, now: number): number {
  const until = Date.parse(untilIso);
  const from = fromIso ? Date.parse(fromIso) : until - 3600_000;
  const total = until - from;
  if (total <= 0) return 0;
  return Math.min(1, Math.max(0, (until - now) / total));
}

export function initials(name: string): string {
  const parts = name.split(/[\s._-]+/).filter(Boolean);
  return (parts.length > 1 ? parts[0][0] + parts[1][0] : name.slice(0, 2)).toUpperCase();
}

// Stable colour per actor, so the same agent looks the same everywhere on the page.
export function actorHue(name: string): number {
  let h = 0;
  for (const c of name) h = (h * 31 + c.charCodeAt(0)) % 360;
  return h;
}

// One action writes several events (for example a review decision, the override that applies
// it and the status change). Lists show one row per action: the most specific event type.
const GENERIC_EVENTS = ['status_changed', 'override'];

interface EventLike { at: string; eventType: string; fromState: string | null; toState: string | null }

export function collapseEvents<T extends EventLike>(events: T[], entityOf: (e: T) => string = () => ''): T[] {
  const out: T[] = [];
  for (const e of events) {
    const prev = out[out.length - 1];
    const sameAction = prev && entityOf(prev) === entityOf(e) && Math.abs(Date.parse(prev.at) - Date.parse(e.at)) < 2000;
    if (!sameAction) {
      out.push(e);
    } else if (GENERIC_EVENTS.includes(prev.eventType) && !GENERIC_EVENTS.includes(e.eventType)) {
      out[out.length - 1] = { ...e, fromState: e.fromState ?? prev.fromState, toState: e.toState ?? prev.toState };
    } else if (!prev.toState && e.toState) {
      out[out.length - 1] = { ...prev, fromState: e.fromState, toState: e.toState };
    }
  }
  return out;
}
