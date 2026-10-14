import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api, ApiError, type Board, type PlanSummary, type Project, type Task } from './api';
import { COLUMNS, ago, label } from './format';
import { TaskCard } from './TaskCard';
import { TaskDrawer } from './TaskDrawer';
import { Activity } from './Activity';

const POLL_MS = 4000;

function readHash(): { project?: string; plan?: string; task?: string } {
  const p = new URLSearchParams(location.hash.slice(1));
  return { project: p.get('project') ?? undefined, plan: p.get('plan') ?? undefined, task: p.get('task') ?? undefined };
}

function writeHash(v: { project?: string; plan?: string; task?: string }) {
  const p = new URLSearchParams();
  if (v.project) p.set('project', v.project);
  if (v.plan) p.set('plan', v.plan);
  if (v.task) p.set('task', v.task);
  history.replaceState(null, '', `#${p.toString()}`);
}

function useNow(intervalMs: number) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(id);
  }, [intervalMs]);
  return now;
}

export function App() {
  const initial = useMemo(readHash, []);
  const [projects, setProjects] = useState<Project[] | null>(null);
  const [projectId, setProjectId] = useState<string | undefined>(initial.project);
  const [plans, setPlans] = useState<PlanSummary[]>([]);
  const [planId, setPlanId] = useState<string | undefined>(initial.plan);
  const [board, setBoard] = useState<Board | null>(null);
  const [taskId, setTaskId] = useState<string | undefined>(initial.task);
  const [error, setError] = useState<string | null>(null);
  const [lastSync, setLastSync] = useState<number | null>(null);
  const [changed, setChanged] = useState<Set<string>>(new Set());
  const prevStatus = useRef<Map<string, string>>(new Map());
  const clockSkew = useRef(0);
  const now = useNow(1000) + clockSkew.current;

  useEffect(() => {
    api.projects()
      .then((ps) => {
        setProjects(ps);
        setProjectId((cur) => cur ?? ps[0]?.id);
      })
      .catch((e: ApiError) => setError(e.message));
  }, []);

  useEffect(() => {
    if (!projectId) return;
    api.plans(projectId)
      .then((ps) => {
        setPlans(ps);
        setPlanId((cur) => (cur && ps.some((p) => p.id === cur) ? cur : (ps.find((p) => p.status === 'active') ?? ps[0])?.id));
      })
      .catch((e: ApiError) => setError(e.message));
  }, [projectId]);

  const load = useCallback(async () => {
    if (!planId) return;
    try {
      const b = await api.board(planId);
      clockSkew.current = Date.parse(b.serverTime) - Date.now();
      const moved = new Set<string>();
      for (const t of b.tasks) {
        const before = prevStatus.current.get(t.id);
        if (before !== undefined && before !== t.status) moved.add(t.id);
      }
      prevStatus.current = new Map(b.tasks.map((t) => [t.id, t.status]));
      if (moved.size) setChanged(moved);
      setBoard(b);
      setLastSync(Date.now());
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    }
  }, [planId]);

  useEffect(() => {
    prevStatus.current = new Map();
    setBoard(null);
    load();
    const id = setInterval(() => {
      if (document.visibilityState === 'visible') load();
    }, POLL_MS);
    return () => clearInterval(id);
  }, [load]);

  useEffect(() => {
    if (!changed.size) return;
    const id = setTimeout(() => setChanged(new Set()), 2500);
    return () => clearTimeout(id);
  }, [changed]);

  useEffect(() => writeHash({ project: projectId, plan: planId, task: taskId }), [projectId, planId, taskId]);

  const findingsByTask = useMemo(() => {
    const m = new Map<string, number>();
    for (const f of board?.findings ?? []) m.set(f.taskId, (m.get(f.taskId) ?? 0) + 1);
    return m;
  }, [board]);

  const tasksById = useMemo(() => new Map((board?.tasks ?? []).map((t) => [t.id, t])), [board]);
  const stuckIds = useMemo(() => new Set((board?.stuckWork ?? []).map((s) => s.taskId)), [board]);
  const activeClaims = (board?.tasks ?? []).filter((t) => t.claimedUntil && Date.parse(t.claimedUntil) > now).length;
  const agents = new Set((board?.tasks ?? []).filter((t) => t.claimActor).map((t) => t.claimActor)).size;
  const total = board?.tasks.length ?? 0;
  const done = (board?.tasks ?? []).filter((t) => t.status === 'done').length;
  const selected = taskId ? tasksById.get(taskId) : undefined;

  const columns = COLUMNS.map((c) => ({
    ...c,
    tasks: (board?.tasks ?? []).filter((t) => c.statuses.includes(t.status)),
  })).filter((c) => !c.optional || c.tasks.length > 0);

  const live = lastSync !== null && Date.now() - lastSync < POLL_MS * 3 && !error;

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <span className="logo" aria-hidden>
            <svg viewBox="0 0 32 32"><rect width="32" height="32" rx="7" /><path d="M8 11h16M8 16h11M8 21h7" /></svg>
          </span>
          Workstream
        </div>
        <div className="pickers">
          <select aria-label="Project" value={projectId ?? ''} onChange={(e) => { setProjectId(e.target.value); setPlanId(undefined); setTaskId(undefined); }}>
            {(projects ?? []).map((p) => <option key={p.id} value={p.id}>{p.displayName}</option>)}
          </select>
          <span className="sep">/</span>
          <select aria-label="Plan" value={planId ?? ''} onChange={(e) => { setPlanId(e.target.value); setTaskId(undefined); }}>
            {plans.map((p) => <option key={p.id} value={p.id}>{p.name} · {label(p.status)}</option>)}
          </select>
        </div>
        <div className={`sync ${live ? 'on' : ''}`} title={lastSync ? `Last update ${ago(new Date(lastSync).toISOString(), Date.now())}` : ''}>
          <span className="dot" /> {live ? 'Live' : error ? 'Offline' : 'Connecting'}
        </div>
      </header>

      {error && <div className="banner">{error}</div>}

      {projects && projects.length === 0 && (
        <div className="empty-state">
          <h2>No projects yet</h2>
          <p>Create one with the <code>create_project</code> MCP tool, or start the server with <code>WORKSTREAM_DEMO=true</code> to get a demo plan.</p>
        </div>
      )}

      {board && (
        <>
          <section className="summary">
            <div className="plan-title">
              <h1>{board.plan.name}</h1>
              {board.plan.objective && <p>{board.plan.objective}</p>}
            </div>
            <div className="stats">
              <Stat value={`${done}/${total}`} label="done" />
              <Stat value={activeClaims} label="live claims" />
              <Stat value={agents} label="actors" />
              <Stat value={board.stuckWork.length} label="stuck" tone={board.stuckWork.length ? 'warn' : undefined} />
              <Stat value={board.findings.length} label="findings" />
            </div>
            <div className="progress" aria-label={`${done} of ${total} tasks done`}>
              <span style={{ width: total ? `${(done / total) * 100}%` : 0 }} />
            </div>
          </section>

          <main className="layout">
            <div className="board">
              {columns.map((c) => (
                <section key={c.key} className={`column col-${c.key}`}>
                  <h2>
                    {c.label} <span className="count">{c.tasks.length}</span>
                  </h2>
                  <div className="cards">
                    {c.tasks.map((t: Task) => (
                      <TaskCard
                        key={t.id}
                        task={t}
                        now={now}
                        findings={findingsByTask.get(t.id) ?? 0}
                        stuck={stuckIds.has(t.id)}
                        flash={changed.has(t.id)}
                        blockedBy={t.dependsOn.map((d) => tasksById.get(d)).filter((d): d is Task => !!d && d.status !== 'done')}
                        selected={t.id === taskId}
                        onOpen={() => setTaskId(t.id)}
                      />
                    ))}
                    {c.tasks.length === 0 && <div className="column-empty">Nothing here</div>}
                  </div>
                </section>
              ))}
            </div>
            <Activity board={board} tasksById={tasksById} now={now} onOpen={setTaskId} />
          </main>
        </>
      )}

      {selected && board && (
        <TaskDrawer
          task={selected}
          findings={board.findings.filter((f) => f.taskId === selected.id)}
          tasksById={tasksById}
          now={now}
          version={board.serverTime}
          onClose={() => setTaskId(undefined)}
        />
      )}
    </div>
  );
}

function Stat({ value, label, tone }: { value: number | string; label: string; tone?: 'warn' }) {
  return (
    <div className={`stat ${tone ?? ''}`}>
      <strong>{value}</strong>
      <span>{label}</span>
    </div>
  );
}
