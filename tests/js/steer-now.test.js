// steer-now.test.js
// Unit tests for wwwroot/agent.js's vm.steerNow — the live "steer now" helper that POSTs
// the steering input's current value to POST api/agent/steer mid-run (vs run-start
// steeringContext, which is fixed when the run begins). Extracted from the AgentMixin
// factory closure source and eval'd with mocked fetch/pushAgentLog/$scope, mirroring the
// suggestion-cancel test's approach.
// Dependency-free Node test runner:  node tests/js/steer-now.test.js
'use strict';

const assert = require('assert');
const fs = require('fs');
const path = require('path');

let passed = 0;
let failed = 0;

async function run(name, fn) {
  try {
    await fn();
    passed++;
    console.log('  ✓ ' + name);
  } catch (e) {
    failed++;
    console.error('  ✗ ' + name);
    console.error('      ' + (e && e.message));
  }
}

// ── Extract the helper from the live source ────────────────────────────────
const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/agent.js'), 'utf8');
const match = /vm\.steerNow = function \(\) \{[\s\S]*?\n                \};/.exec(src);
assert(match, 'vm.steerNow not found in wwwroot/agent.js — marker format may have drifted');
const assignment = match[0]; // 'vm.steerNow = function () { ... };' — evaluated to define it on vm

function makeSteerNow({ activeCardId = 'card-1', steeringContext = 'stop adding helpers', fetchImpl = null, logImpl = null } = {}) {
  const calls = [];
  const logEntries = [];
  let currentMsg = steeringContext;
  const vm = { activeCardId };
  Object.defineProperty(vm, 'steeringContext', {
    get() { return currentMsg; },
    set(v) { currentMsg = v; },
    configurable: true,
  });
  const $scope = { $applyAsync() { calls.push('applyAsync'); } };
  const pushAgentLog = logImpl || ((vm2, level, message) => logEntries.push({ level, message }));
  // eslint-disable-next-line no-new-func
  const steerNow = new Function('vm', '$scope', 'fetch', 'pushAgentLog', assignment + '\nreturn vm.steerNow;')
    (vm, $scope, fetchImpl || (() => Promise.reject(new Error('unexpected fetch'))), pushAgentLog);
  return { steerNow, logEntries, calls, vm };
}

(async function () {
  const test = run;

  // ── Guards: nothing to send / no active run ────────────────────────────────

  await test('empty message → no fetch, no log', async function () {
    let fetched = false;
    const { steerNow, logEntries } = makeSteerNow({
      steeringContext: '   ',
      fetchImpl: () => { fetched = true; return Promise.resolve(); },
    });
    await steerNow();
    assert.strictEqual(fetched, false);
    assert.strictEqual(logEntries.length, 0);
  });

  await test('no active card → warns, no fetch', async function () {
    let fetched = false;
    const { steerNow, logEntries } = makeSteerNow({
      activeCardId: null,
      steeringContext: 'steer this',
      fetchImpl: () => { fetched = true; return Promise.resolve(); },
    });
    await steerNow();
    assert.strictEqual(fetched, false);
    assert.strictEqual(logEntries.length, 1);
    assert.strictEqual(logEntries[0].level, 'warn');
    assert.ok(logEntries[0].message.includes('no active card'));
  });

  // ── POST shape ─────────────────────────────────────────────────────────────

  await test('sends {cardId, message} to POST /api/agent/steer', async function () {
    const sent = [];
    const { steerNow } = makeSteerNow({
      activeCardId: 'card-9',
      steeringContext: 'rename it to load()',
      fetchImpl: (url, opts) => {
        sent.push({ url, opts });
        return Promise.resolve({
          ok: true,
          json: () => Promise.resolve({ status: 'steered', cardId: 'card-9', active: true }),
        });
      },
    });
    await steerNow();
    assert.strictEqual(sent.length, 1);
    assert.strictEqual(sent[0].url, '/api/agent/steer');
    assert.strictEqual(sent[0].opts.method, 'POST');
    const body = JSON.parse(sent[0].opts.body);
    assert.deepStrictEqual(body, { cardId: 'card-9', message: 'rename it to load()' });
  });

  // ── Feedback + clearing ────────────────────────────────────────────────────

  await test('active:true → info log and clears the input', async function () {
    const { steerNow, logEntries } = makeSteerNow({
      steeringContext: 'make it async',
      fetchImpl: () => Promise.resolve({ ok: true, json: () => Promise.resolve({ active: true }) }),
    });
    await steerNow();
    assert.strictEqual(logEntries.length, 1);
    assert.strictEqual(logEntries[0].level, 'info');
    assert.ok(logEntries[0].message.includes('next planner turn'));
  });

  await test('active:false → warn log (queued but not executing) and clears the input', async function () {
    const { steerNow, logEntries } = makeSteerNow({
      steeringContext: 'queued steer',
      fetchImpl: () => Promise.resolve({ ok: true, json: () => Promise.resolve({ active: false }) }),
    });
    await steerNow();
    assert.strictEqual(logEntries.length, 1);
    assert.strictEqual(logEntries[0].level, 'warn');
    assert.ok(logEntries[0].message.includes('not executing'));
  });

  await test('clears the input after a successful POST so it cannot leak into the next run', async function () {
    const { steerNow, vm } = makeSteerNow({
      steeringContext: 'one-time steer',
      fetchImpl: () => Promise.resolve({ ok: true, json: () => Promise.resolve({ active: true }) }),
    });
    await steerNow();
    assert.strictEqual(vm.steeringContext, '');
  });

  await test('HTTP error → warn log with status', async function () {
    const { steerNow, logEntries } = makeSteerNow({
      fetchImpl: () => Promise.resolve({ ok: false, status: 400, text: () => Promise.resolve('cardId is required') }),
    });
    await steerNow();
    assert.strictEqual(logEntries.length, 1);
    assert.strictEqual(logEntries[0].level, 'warn');
    assert.ok(logEntries[0].message.includes('HTTP 400'));
    assert.ok(logEntries[0].message.includes('cardId is required'));
  });

  await test('network failure → warn log with message', async function () {
    const { steerNow, logEntries } = makeSteerNow({
      fetchImpl: () => Promise.reject(new Error('network down')),
    });
    await steerNow();
    assert.strictEqual(logEntries.length, 1);
    assert.strictEqual(logEntries[0].level, 'warn');
    assert.ok(logEntries[0].message.includes('network down'));
  });

  console.log('\nsteer-now helper tests: ' + passed + ' passed, ' + failed + ' failed');
  if (failed > 0) process.exit(1);
})();
