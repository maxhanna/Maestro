// agent-restore-card-steps.test.js
// Regression tests for wwwroot/agent.js's vm.restoreCardSteps — the helper that
// rehydrates the agent panel's "💻 Commands" list from a completed card's persisted
// history (card._steps) so the full executed-command list survives a reload.
// vm.streamingSteps is otherwise transient (cleared at run start), so without this a
// reload leaves the panel empty even though the card is Done. It also re-keys the
// persisted steps with sequential indices, because the server's step index restarts
// per pipeline phase (0,0,0,…) and would otherwise trip ngRepeat `track by s.index`.
// Dependency-free Node test runner:  node tests/js/agent-restore-card-steps.test.js
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

// ── Extract vm.restoreCardSteps from the live source ───────────────────────
const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/agent.js'), 'utf8');

// The persist sites must stay wired, or the restore helper has nothing to read from.
assert(src.indexOf('card._steps = persistStepPhases(finalSteps);') !== -1,
  'normal done path no longer persists card._steps — marker drifted');
assert(src.indexOf('mvCard._steps = persistStepPhases(concAnalysis.steps);') !== -1,
  'concurrent done path no longer persists mvCard._steps — marker drifted');

const fnStart = src.indexOf('vm.restoreCardSteps = function (card) {');
assert(fnStart !== -1, 'vm.restoreCardSteps not found in wwwroot/agent.js — marker format may have drifted');
const bodyStart = fnStart + 'vm.restoreCardSteps = function (card) {'.length;
const bodyEnd = src.indexOf('\n                };', bodyStart);
assert(bodyEnd !== -1 && bodyEnd > bodyStart,
  'vm.restoreCardSteps closing brace not found — indentation may have drifted');
const body = src.slice(bodyStart, bodyEnd);

// The helper's free variables (vm / angular / normalizeStep / refreshFilesEditedFromSteps)
// are bound explicitly so the extracted body runs standalone.
const restoreCardSteps = eval('(function (card, vm, angular, normalizeStep, refreshFilesEditedFromSteps) {' + body + '\n})');

const angularStub = { copy: function (x) { return JSON.parse(JSON.stringify(x)); } };
const normalizeStepStub = function (s) { if (s && !s.status) s.status = 'pending'; };
const refreshStub = function () { };

function freshVm(overrides) {
  return Object.assign({ streamingSteps: [], activeStepIndex: 7, streamingActive: false }, overrides || {});
}

// ── Tests ───────────────────────────────────────────────────────────────────

test('restores persisted steps and re-keys colliding server indices sequentially', () => {
  const vm = freshVm();
  const card = {
    _steps: [
      { index: 0, type: 'list', description: 'list root', status: 'done' },
      { index: 0, type: 'command', command: 'mkdir bench', status: 'done' },
      { index: 0, type: 'command', command: 'node server.js', status: 'done' }
    ]
  };
  const ok = restoreCardSteps(card, vm, angularStub, normalizeStepStub, refreshStub);
  assert.strictEqual(ok, true);
  assert.strictEqual(vm.streamingSteps.length, 3);
  assert.deepStrictEqual(vm.streamingSteps.map(s => s.index), [0, 1, 2]);
  assert.deepStrictEqual(vm.streamingSteps.map(s => s.command), [undefined, 'mkdir bench', 'node server.js']);
  assert.strictEqual(vm.activeStepIndex, null);
});

test('does not clobber the live stream while a run is active', () => {
  const vm = freshVm({ streamingActive: true, streamingSteps: [{ index: 0, type: 'command', status: 'running' }] });
  const card = { _steps: [{ index: 0, type: 'list', status: 'done' }] };
  const ok = restoreCardSteps(card, vm, angularStub, normalizeStepStub, refreshStub);
  assert.strictEqual(ok, false);
  assert.strictEqual(vm.streamingSteps.length, 1);
  assert.strictEqual(vm.streamingSteps[0].type, 'command');
});

test('returns false for a card with no persisted steps', () => {
  const vm = freshVm();
  const ok = restoreCardSteps({}, vm, angularStub, normalizeStepStub, refreshStub);
  assert.strictEqual(ok, false);
  assert.deepStrictEqual(vm.streamingSteps, []);
});

test('returns false for a null card', () => {
  const vm = freshVm();
  assert.strictEqual(restoreCardSteps(null, vm, angularStub, normalizeStepStub, refreshStub), false);
  assert.strictEqual(restoreCardSteps(undefined, vm, angularStub, normalizeStepStub, refreshStub), false);
});

test('does not mutate the persisted card._steps array', () => {
  const vm = freshVm();
  const persisted = [{ index: 0, type: 'command', command: 'x', status: 'done' }];
  const card = { _steps: persisted };
  restoreCardSteps(card, vm, angularStub, normalizeStepStub, refreshStub);
  assert.strictEqual(persisted[0].index, 0, 'source step index must stay untouched');
  assert.strictEqual(vm.streamingSteps[0].index, 0);
  assert.notStrictEqual(vm.streamingSteps[0], persisted[0], 'panel gets a copy, not the card object');
});

// ── Summary ─────────────────────────────────────────────────────────────────
console.log(`\n# ${passed} passed, ${failed} failed`);
process.exitCode = failed ? 1 : 0;
