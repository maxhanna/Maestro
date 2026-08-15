// agent-step-phase.test.js
// Unit tests for wwwroot/agent.js's stepPhaseOf / annotateStepPhases — the bucketing
// behind the "💻 Commands" panel's collapsible phase sections (discover / plan / execute /
// verify). Each step is tagged with the pipeline phase it belongs to, and the panel renders
// one collapsible header per phase boundary so a long run is scannable.
// Dependency-free Node test runner:  node tests/js/agent-step-phase.test.js
'use strict';

const assert = require('assert');
const fs = require('fs');
const path = require('path');

let passed = 0;
let failed = 0;

function test(name, fn) {
  try {
    fn();
    passed++;
    console.log('  ✓ ' + name);
  } catch (e) {
    failed++;
    console.error('  ✗ ' + name);
    console.error('      ' + (e && e.message));
  }
}

// ── Extract the helpers from the live source ───────────────────────────────
const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/agent.js'), 'utf8');
const start = src.indexOf('var COMMAND_PHASE_LABELS = {');
const end = src.indexOf('function pushAgentLog(vm, level, message, detail) {');
assert(start !== -1 && end !== -1 && end > start,
  'stepPhaseOf/annotateStepPhases block not found in wwwroot/agent.js — marker format may have drifted');
const block = src.slice(start, end);

const api = eval('(function () { ' + block + '\n return { stepPhaseOf: stepPhaseOf, annotateStepPhases: annotateStepPhases, persistStepPhases: persistStepPhases }; })()');
const stepPhaseOf = api.stepPhaseOf;
const annotateStepPhases = api.annotateStepPhases;
const persistStepPhases = api.persistStepPhases;

// ── Tests ───────────────────────────────────────────────────────────────────

test('discovery steps bucket to discover', () => {
  for (const t of ['list', 'read', 'grep', 'glob', 'explore']) {
    assert.strictEqual(stepPhaseOf({ type: t, status: 'done' }), 'discover', t);
  }
});

test('plan and pending plan_step bucket to plan', () => {
  assert.strictEqual(stepPhaseOf({ type: 'plan', status: 'pending' }), 'plan');
  assert.strictEqual(stepPhaseOf({ type: 'plan', status: 'done' }), 'plan');
  assert.strictEqual(stepPhaseOf({ type: 'plan_step', status: 'pending' }), 'plan');
  assert.strictEqual(stepPhaseOf({ type: 'plan_step', status: 'proposing' }), 'plan');
});

test('executed steps bucket to execute', () => {
  for (const t of ['command', 'edit', 'create', 'rename', 'delete', 'web_search', 'web_fetch', 'scraper']) {
    assert.strictEqual(stepPhaseOf({ type: t, status: 'done' }), 'execute', t);
  }
  // A plan_step that actually executed is an execute-phase step.
  assert.strictEqual(stepPhaseOf({ type: 'plan_step', status: 'done' }), 'execute');
});

test('verification steps bucket to verify', () => {
  for (const t of ['verify', 'verified_complete', 'checkpoint', 'browse', 'test', 'assess']) {
    assert.strictEqual(stepPhaseOf({ type: t, status: 'done' }), 'verify', t);
  }
});

test('unknown types default to execute', () => {
  assert.strictEqual(stepPhaseOf({ type: 'mystery', status: 'done' }), 'execute');
  assert.strictEqual(stepPhaseOf({}), 'execute');
  assert.strictEqual(stepPhaseOf(null), 'execute');
});

test('annotateStepPhases tags phase, boundary flag, and counts', () => {
  const steps = [
    { index: 0, type: 'list', status: 'done' },
    { index: 1, type: 'read', status: 'done' },
    { index: 2, type: 'plan', status: 'pending' },
    { index: 3, type: 'command', status: 'done' },
    { index: 4, type: 'edit', status: 'done' },
    { index: 5, type: 'verified_complete', status: 'done' }
  ];
  annotateStepPhases(steps);

  assert.deepStrictEqual(steps.map(s => s._phase), ['discover', 'discover', 'plan', 'execute', 'execute', 'verify']);
  assert.deepStrictEqual(steps.map(s => s._phaseFirst), [true, false, true, true, false, true]);
  assert.strictEqual(steps[0]._phaseCount, 2); // discover
  assert.strictEqual(steps[2]._phaseCount, 1); // plan
  assert.strictEqual(steps[3]._phaseCount, 2); // execute
  assert.strictEqual(steps[5]._phaseCount, 1); // verify
});

test('annotateStepPhases returns the same array (in-place tagging)', () => {
  const steps = [{ index: 0, type: 'command', status: 'done' }];
  assert.strictEqual(annotateStepPhases(steps), steps);
  assert.strictEqual(steps[0]._phase, 'execute');
});

test('persistStepPhases stamps each step with its pipeline phase', () => {
  const steps = [
    { type: 'list', status: 'done' },
    { type: 'plan_step', status: 'pending' },
    { type: 'command', status: 'done' },
    { type: 'verified_complete', status: 'done' }
  ];
  const out = persistStepPhases(steps);
  assert.strictEqual(out, steps, 'stamps in place and returns the same array');
  assert.deepStrictEqual(steps.map(s => s._phase), ['discover', 'plan', 'execute', 'verify']);
});

test('persistStepPhases only stamps _phase — render-derived fields stay clean', () => {
  const steps = [{ type: 'command', status: 'done', _phaseFirst: true, _phaseCount: 3 }];
  persistStepPhases(steps);
  assert.strictEqual(steps[0]._phase, 'execute');
  assert.strictEqual(steps[0]._phaseFirst, true, 'persistStepPhases must not mutate _phaseFirst');
  assert.strictEqual(steps[0]._phaseCount, 3, 'persistStepPhases must not mutate _phaseCount');
});

test('persistStepPhases tolerates non-arrays and null steps', () => {
  assert.strictEqual(persistStepPhases(null), null);
  assert.strictEqual(persistStepPhases(undefined), undefined);
  assert.deepStrictEqual(persistStepPhases([null, undefined, { type: 'read', status: 'done' }]),
    [null, undefined, { type: 'read', status: 'done', _phase: 'discover' }]);
});

test('preferPersisted honors a stamped _phase even when rules would differ', () => {
  // The persisted step claims 'verify' — with preferPersisted the annotation must keep it
  // even though stepPhaseOf would bucket the type to 'execute' (simulating a classifier
  // rule change AFTER the run was persisted). The live path (no opts) still recomputes.
  const steps = [{ index: 0, type: 'command', status: 'done', _phase: 'verify' }];
  annotateStepPhases(steps, { preferPersisted: true });
  assert.strictEqual(steps[0]._phase, 'verify');
  assert.strictEqual(steps[0]._phaseFirst, true);
  assert.strictEqual(steps[0]._phaseCount, 1);

  annotateStepPhases(steps); // live path: recompute
  assert.strictEqual(steps[0]._phase, 'execute');
});

test('preferPersisted falls back to stepPhaseOf when no _phase is stamped', () => {
  const steps = [
    { index: 0, type: 'list', status: 'done' },
    { index: 1, type: 'command', status: 'done' }
  ];
  annotateStepPhases(steps, { preferPersisted: true });
  assert.deepStrictEqual(steps.map(s => s._phase), ['discover', 'execute']);
  assert.deepStrictEqual(steps.map(s => s._phaseFirst), [true, true]);
});

test('preferPersisted groups contiguous persisted buckets with correct boundaries/counts', () => {
  const steps = [
    { index: 0, type: 'x', status: 'done', _phase: 'discover' },
    { index: 1, type: 'x', status: 'done', _phase: 'discover' },
    { index: 2, type: 'x', status: 'done', _phase: 'plan' },
    { index: 3, type: 'x', status: 'done', _phase: 'execute' },
    { index: 4, type: 'x', status: 'done', _phase: 'execute' },
    { index: 5, type: 'x', status: 'done', _phase: 'execute' },
    { index: 6, type: 'x', status: 'done', _phase: 'verify' }
  ];
  annotateStepPhases(steps, { preferPersisted: true });
  assert.deepStrictEqual(steps.map(s => s._phaseFirst), [true, false, true, true, false, false, true]);
  assert.deepStrictEqual(steps.map(s => s._phaseCount), [2, 2, 1, 3, 3, 3, 1]);
});

test('annotateStepPhases tolerates empty / non-array input', () => {
  assert.deepStrictEqual(annotateStepPhases([]), []);
  assert.deepStrictEqual(annotateStepPhases(null), []);
  assert.deepStrictEqual(annotateStepPhases(undefined), []);
  assert.deepStrictEqual(annotateStepPhases('nope'), []);
});

// ── Summary ─────────────────────────────────────────────────────────────────
console.log(`\n# ${passed} passed, ${failed} failed`);
process.exitCode = failed ? 1 : 0;
