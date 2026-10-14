import { useEffect, useState } from 'react';
import { api, type Finding, type Task, type TimelineEntry } from './api';
import { actorHue, ago, collapseEvents, initials, label, remaining } from './format';

interface Props {
  task: Task;
  findings: Finding[];
  tasksById: Map<string, Task>;
  now: number;
  version: string;
  onClose: () => void;
}

export function TaskDrawer({ task, findings, tasksById, now, version, onClose }: Props) {
  const [events, setEvents] = useState<TimelineEntry[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Reload the timeline when the task changes or the board moves on (status or claim changes).
  const key = `${task.id}|${task.status}|${task.claimActor ?? ''}|${task.updatedAt}`;
  useEffect(() => {
    let cancelled = false;
    api.events('task', task.id)
      .then((e) => { if (!cancelled) { setEvents(collapseEvents(e)); setError(null); } })
      .catch((e: Error) => { if (!cancelled) setError(e.message); });
    return () => { cancelled = true; };
  }, [key]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const claim = task.claimedUntil ? remaining(task.claimedUntil, now) : null;

  return (
    <>
      <div className="scrim" onClick={onClose} />
      <aside className="drawer" aria-label={`Task ${task.externalKey}`} data-version={version}>
        <header>
          <div>
            <span className="key">{task.externalKey}</span>
            <span className={`status s-${task.status}`}>{label(task.status)}</span>
          </div>
          <button type="button" className="close" onClick={onClose} aria-label="Close">×</button>
        </header>
        <h2>{task.title}</h2>

        <dl className="facts">
          <dt>Priority</dt><dd>P{task.priority}</dd>
          {task.phase && (<><dt>Phase</dt><dd>{task.phase}</dd></>)}
          <dt>Claim</dt>
          <dd>
            {task.claimActor && claim
              ? <>{task.claimActor}{task.claimRole ? ` (${task.claimRole})` : ''}, {claim.expired ? 'expired' : `${claim.text} left`}</>
              : 'none'}
          </dd>
          {task.dependsOn.length > 0 && (
            <>
              <dt>Depends on</dt>
              <dd>{task.dependsOn.map((d) => { const t = tasksById.get(d); return t ? `${t.externalKey} (${label(t.status)})` : d.slice(0, 8); }).join(', ')}</dd>
            </>
          )}
          {task.githubIssue && (<><dt>GitHub</dt><dd>#{task.githubIssue}</dd></>)}
          <dt>Updated</dt><dd>{ago(task.updatedAt, now)}</dd>
        </dl>

        {findings.length > 0 && (
          <section>
            <h3>Findings</h3>
            <ul className="findings">
              {findings.map((f) => (
                <li key={f.id}>
                  <span className={`sev sev-${(f.severity ?? 'none').toLowerCase()}`}>{f.severity ?? 'n/a'}</span>
                  <span className="f-key">{f.externalKey}</span>
                  <span className="f-text">{f.symptom ?? ''}</span>
                  <span className="f-status">{label(f.status)}</span>
                </li>
              ))}
            </ul>
          </section>
        )}

        <section>
          <h3>Timeline</h3>
          {error && <p className="muted">{error}</p>}
          {!events && !error && <p className="muted">Loading…</p>}
          {events && events.length === 0 && <p className="muted">No events yet.</p>}
          {events && (
            <ol className="timeline">
              {events.map((e) => (
                <li key={e.id}>
                  {e.actor
                    ? <span className="avatar sm" style={{ ['--h' as string]: actorHue(e.actor) }}>{initials(e.actor)}</span>
                    : <span className="avatar sm system">·</span>}
                  <div>
                    <div className="t-line">
                      <strong>{label(e.eventType)}</strong>
                      {e.fromState && e.toState && <span className="t-move">{label(e.fromState)} → {label(e.toState)}</span>}
                    </div>
                    <div className="muted">{e.actor ?? 'system'} · {ago(e.at, now)}</div>
                  </div>
                </li>
              ))}
            </ol>
          )}
        </section>
      </aside>
    </>
  );
}
