#!/usr/bin/env node
// Fills a local Workstream server with a plan and three agents working it, so the dashboard
// has something to show. Two developer agents race for tasks and a reviewer approves or sends
// work back. Every call goes through the public MCP endpoint, the same way a real agent works.
//
//   docker run --rm -p 127.0.0.1:8080:8080 -e WORKSTREAM_ADMIN_TOKEN=local-admin \
//     ghcr.io/ahmad-codex/workstream-mcp:demo
//   WORKSTREAM_ADMIN_TOKEN=local-admin node scripts/simulate-agents.mjs
//
// Then open http://localhost:8080/demo-insecure-token/ui/ and watch the board.
// Options: --rounds <n> stops after n tasks are done (default: runs until the plan is done),
//          --speed <x> multiplies every delay (default 1; 0.3 is fast).

const base = (process.env.WORKSTREAM_URL ?? 'http://localhost:8080').replace(/\/$/, '');
const adminToken = process.env.WORKSTREAM_ADMIN_TOKEN;
const demoToken = process.env.WORKSTREAM_DEMO_TOKEN ?? 'demo-insecure-token';
const args = process.argv.slice(2);
const opt = (name, fallback) => {
  const i = args.indexOf(`--${name}`);
  return i >= 0 ? Number(args[i + 1]) : fallback;
};
const rounds = opt('rounds', Infinity);
const speed = opt('speed', 1);

if (!adminToken) {
  console.error('Set WORKSTREAM_ADMIN_TOKEN (the server needs it too) so the script can create agent users.');
  process.exit(1);
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms * speed));
const jitter = (min, max) => min + Math.random() * (max - min);

let rpcId = 0;
async function call(token, name, args) {
  const res = await fetch(`${base}/${token}/mcp`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ jsonrpc: '2.0', id: ++rpcId, method: 'tools/call', params: { name, arguments: args } }),
  });
  const body = await res.json();
  const text = body.result?.content?.[0]?.text;
  const envelope = text ? JSON.parse(text) : body;
  if (!envelope.ok) {
    const err = new Error(`${name}: ${envelope.error?.code ?? res.status} ${envelope.error?.message ?? ''}`);
    err.code = envelope.error?.code;
    throw err;
  }
  return envelope.data;
}

async function createAgent(username, displayName, actorType) {
  const res = await fetch(`${base}/admin/users`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${adminToken}` },
    body: JSON.stringify({ githubUsername: `${username}-${Date.now().toString(36)}`, displayName, actorType }),
  });
  if (!res.ok) throw new Error(`creating ${username}: HTTP ${res.status}`);
  const { url } = await res.json();
  return { name: displayName, token: url.split('/').pop() };
}

async function setupPlan() {
  const { projects } = await call(demoToken, 'list_projects', {});
  const project = projects[0];
  const plan = await call(demoToken, 'create_plan', {
    projectId: project.id,
    planType: 'development',
    name: 'Checkout redesign',
    objective: 'Move checkout to the new payments API and rebuild the cart page.',
  });
  const api = await call(demoToken, 'add_phase', { planId: plan.id, orderIndex: 1, name: 'API' });
  const web = await call(demoToken, 'add_phase', { planId: plan.id, orderIndex: 2, name: 'Web' });
  const t = (externalKey, title, priority, phaseId, dependsOnExternalKeys) =>
    ({ externalKey, title, priority, phaseId, dependsOnExternalKeys });
  await call(demoToken, 'create_tasks', {
    planId: plan.id,
    tasks: [
      t('CHK-1', 'Add the payments client with retries and idempotency keys', 3, api.id),
      t('CHK-2', 'Create orders from a cart in one transaction', 3, api.id, ['CHK-1']),
      t('CHK-3', 'Webhook handler for payment status updates', 2, api.id, ['CHK-1']),
      t('CHK-4', 'Rate limit the checkout endpoints', 1, api.id),
      t('CHK-5', 'Rebuild the cart page with server components', 2, web.id),
      t('CHK-6', 'Address form with validation and autofill', 2, web.id),
      t('CHK-7', 'Payment step using the new client', 3, web.id, ['CHK-2']),
      t('CHK-8', 'Order confirmation page and email', 1, web.id, ['CHK-2']),
      t('CHK-9', 'End-to-end checkout tests', 2, web.id, ['CHK-7']),
      t('CHK-10', 'Remove the old checkout flag and dead code', 0, web.id, ['CHK-9']),
    ],
  });
  await call(demoToken, 'activate_plan', { planId: plan.id });
  return plan.id;
}

let done = 0;
const log = (who, msg) => console.log(`${new Date().toISOString().slice(11, 19)}  ${who.padEnd(12)} ${msg}`);

async function developer(agent, planId) {
  while (done < rounds) {
    let claim;
    try {
      claim = await call(agent.token, 'claim_next_task', { planId, role: 'developer' });
    } catch (e) {
      if (e.code === 'no_work_available' || e.code === 'not_found') {
        if (await planFinished(planId)) return;
        await sleep(jitter(3000, 6000));
        continue;
      }
      throw e;
    }
    log(agent.name, `claimed ${claim.title}`);
    await sleep(jitter(2500, 5000));
    await call(agent.token, 'start_work', { claimToken: claim.claimToken });
    log(agent.name, 'started work');
    await sleep(jitter(6000, 12000));
    await call(agent.token, 'submit_attempt', {
      claimToken: claim.claimToken,
      attempt: { filesChanged: ['src/checkout.ts'], approachSummary: 'Implemented and tested locally', testScenario: 'unit tests pass' },
    });
    log(agent.name, 'submitted for review');
    await sleep(jitter(1500, 3000));
  }
}

async function reviewer(agent, planId) {
  while (done < rounds) {
    const board = await fetch(`${base}/${agent.token}/api/plans/${planId}/board`).then((r) => r.json());
    const inReview = board.data.tasks.filter((t) => t.status === 'review');
    if (inReview.length === 0) {
      if (board.data.tasks.every((t) => t.status === 'done')) return;
      await sleep(2500);
      continue;
    }
    const task = inReview[0];
    await sleep(jitter(4000, 8000));
    await call(agent.token, 'submit_review_decision', {
      taskId: task.id,
      decision: 'approved',
      comments: 'Looks good.',
      as_agent: 'reviewer',
    });
    done++;
    log(agent.name, `approved ${task.externalKey}`);
  }
}

async function planFinished(planId) {
  const { plans } = await call(demoToken, 'list_plans', { projectId: (await call(demoToken, 'list_projects', {})).projects[0].id });
  const p = plans.find((x) => x.id === planId);
  return !p || Object.entries(p.taskCounts).every(([s, n]) => s === 'done' || n === 0);
}

const planId = await setupPlan();
const agents = await Promise.all([
  createAgent('claude-code', 'Claude Code', 'subagent'),
  createAgent('codex', 'Codex', 'subagent'),
  createAgent('gemini', 'Gemini', 'subagent'),
]);
console.log(`Plan ready. Open ${base}/${demoToken}/ui/#plan=${planId}`);
await Promise.all([developer(agents[0], planId), developer(agents[1], planId), reviewer(agents[2], planId)]);
console.log(`Stopped after ${done} approved tasks.`);
