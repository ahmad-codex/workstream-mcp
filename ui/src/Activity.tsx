import type { Board, Task } from './api';
import { ago, collapseEvents, label } from './format';

interface Props {
  board: Board;
  tasksById: Map<string, Task>;
  now: number;
  onOpen: (taskId: string) => void;
}

export function Activity({ board, tasksById, now, onOpen }: Props) {
  const findingTask = new Map(board.findings.map((f) => [f.id, f.taskId]));
  const events = collapseEvents(board.recentEvents.filter((e) => e.entityType !== 'attempt'), (e) => e.entityId);

  return (
    <aside className="side">
      {board.stuckWork.length > 0 && (
        <section className="panel warn">
          <h2>Stuck work</h2>
          <ul>
            {board.stuckWork.map((s) => {
              const t = tasksById.get(s.taskId);
              return (
                <li key={s.taskId}>
                  <button type="button" onClick={() => onOpen(s.taskId)}>
                    <span className="key">{t?.externalKey ?? s.taskId.slice(0, 8)}</span> {label(s.status)}
                    <span className="muted"> since {ago(s.since, now)}{s.claimHolder ? ` · ${s.claimHolder}` : ''}</span>
                  </button>
                </li>
              );
            })}
          </ul>
        </section>
      )}

      <section className="panel">
        <h2>Activity</h2>
        {events.length === 0 && <p className="muted">No events yet. Claim a task from an MCP client and it shows up here.</p>}
        <ol className="feed">
          {events.map((e) => {
            const taskId = e.entityType === 'task' ? e.entityId : e.entityType === 'finding' ? findingTask.get(e.entityId) : undefined;
            const t = taskId ? tasksById.get(taskId) : undefined;
            return (
              <li key={e.id}>
                <button type="button" disabled={!taskId} onClick={() => taskId && onOpen(taskId)}>
                  <span className="feed-type">{label(e.eventType)}</span>
                  {t && <span className="key">{t.externalKey}</span>}
                  {e.fromState && e.toState && <span className="t-move">{label(e.fromState)} → {label(e.toState)}</span>}
                  <span className="muted feed-at">{ago(e.at, now)}</span>
                </button>
              </li>
            );
          })}
        </ol>
      </section>
    </aside>
  );
}
