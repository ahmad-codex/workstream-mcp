import type { Task } from './api';
import { actorHue, initials, remaining, ttlFraction } from './format';

interface Props {
  task: Task;
  now: number;
  findings: number;
  stuck: boolean;
  flash: boolean;
  blockedBy: Task[];
  selected: boolean;
  onOpen: () => void;
}

export function TaskCard({ task, now, findings, stuck, flash, blockedBy, selected, onOpen }: Props) {
  const claim = task.claimActor && task.claimedUntil ? remaining(task.claimedUntil, now) : null;
  const frac = task.claimedUntil ? ttlFraction(task.claimedAt, task.claimedUntil, now) : 0;

  return (
    <button
      type="button"
      className={`card ${flash ? 'flash' : ''} ${stuck ? 'stuck' : ''} ${selected ? 'selected' : ''}`}
      onClick={onOpen}
    >
      <div className="card-head">
        <span className="key">{task.externalKey}</span>
        <span className={`prio p${Math.min(task.priority, 3)}`} title={`Priority ${task.priority}`}>P{task.priority}</span>
      </div>
      <div className="card-title">{task.title}</div>

      {(task.phase || findings > 0 || blockedBy.length > 0 || stuck) && (
        <div className="tags">
          {task.phase && <span className="tag">{task.phase}</span>}
          {findings > 0 && <span className="tag finding">{findings} finding{findings > 1 ? 's' : ''}</span>}
          {blockedBy.length > 0 && (
            <span className="tag wait" title={blockedBy.map((b) => b.externalKey).join(', ')}>
              waits on {blockedBy.map((b) => b.externalKey).join(', ')}
            </span>
          )}
          {stuck && <span className="tag warn">stuck</span>}
        </div>
      )}

      {claim && task.claimActor && (
        <div className={`claim ${claim.expired ? 'expired' : ''}`}>
          <span className="avatar" style={{ ['--h' as string]: actorHue(task.claimActor) }}>{initials(task.claimActor)}</span>
          <span className="who">
            {task.claimActor}
            {task.claimRole && <em> · {task.claimRole}</em>}
          </span>
          <span className="ttl">{claim.text}</span>
          <span className="ttl-bar"><span style={{ width: `${frac * 100}%` }} /></span>
        </div>
      )}
    </button>
  );
}
