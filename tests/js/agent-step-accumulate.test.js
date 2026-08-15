// agent-step-accumulate.test.js
// Regression tests for wwwroot/agent.js's upsertStreamingStep. The backend now remaps
// every 'step' SSE event onto ONE run-unique monotonic counter, so the frontend dedupes
// on the STABLE `index` key alone: an update (running→done) carries the SAME index and
// extends in place; a new step carries a fresh index. Missing indices (legacy paths) are
// re-keyed defensively. This replaced the old per-phase 0,0,0,… indices that used to
// overwrite earlier commands in the "💻 Commands" list.
// Dependency-free Node test runner:  node tests/js/agent-step-accumulate.test.js
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

// ── Extract upsertStreamingStep from the live source ───────────────────────
const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/agent.js'), 'utf8');
const start = src.indexOf('function upsertStreamingStep(vm, parsed, $scope, $timeout) {');
const end = src.indexOf('function normalizeAgentPanelTab(tab, webtestEvents) {');
assert(start !== -1 && end !== -1 && end > start,
  'upsertStreamingStep block not found in wwwroot/agent.js — marker format may have drifted');
const block = src.slice(start, end);

// upsertStreamingStep depends on normalizeStep / refreshFilesEditedFromSteps / angular.
global.normalizeStep = function (s) { return s; };
global.refreshFilesEditedFromSteps = function () { };
global.angular = { extend: function (target, source) { return Object.assign(target, source); } };

const upsertStreamingStep = eval('(function () { ' + block + '\n return upsertStreamingStep; })()');

function freshVm() {
  return { streamingSteps: [], activeStepIndex: null };
}

// ── Tests ───────────────────────────────────────────────────────────────────

test('a running→done pair with the same index merges in place', () => {
  const vm = freshVm();
  upsertStreamingStep(vm, { index: 0, type: 'command', description: 'mkdir bench', status: 'running' });
  upsertStreamingStep(vm, { index: 0, type: 'command', description: 'mkdir bench', command: 'mkdir bench', status: 'done', output: 'ok' });
  assert.strictEqual(vm.streamingSteps.length, 1);
  assert.strictEqual(vm.streamingSteps[0].status, 'done');
  assert.strictEqual(vm.streamingSteps[0].command, 'mkdir bench');
});

test('distinct steps with unique indices accumulate', () => {
  const vm = freshVm();
  upsertStreamingStep(vm, { index: 0, type: 'list', description: 'list root', status: 'done' });
  upsertStreamingStep(vm, { index: 1, type: 'command', command: 'mkdir bench', status: 'done' });
  upsertStreamingStep(vm, { index: 2, type: 'command', command: 'node server.js', status: 'done' });
  assert.strictEqual(vm.streamingSteps.length, 3);
  assert.deepStrictEqual(vm.streamingSteps.map(s => s.index), [0, 1, 2]);
});

test('a missing index is assigned a fresh one', () => {
  const vm = freshVm();
  upsertStreamingStep(vm, { type: 'command', command: 'npm test', status: 'done' });
  upsertStreamingStep(vm, { type: 'command', command: 'npm run build', status: 'done' });
  assert.strictEqual(vm.streamingSteps.length, 2);
  assert.deepStrictEqual(vm.streamingSteps.map(s => s.index), [0, 1]);
});

test('steps keep their assigned index across status updates', () => {
  const vm = freshVm();
  upsertStreamingStep(vm, { index: 3, type: 'command', description: 'x', status: 'running' });
  upsertStreamingStep(vm, { index: 3, type: 'command', description: 'x', status: 'done' });
  assert.strictEqual(vm.streamingSteps.length, 1);
  assert.strictEqual(vm.streamingSteps[0].index, 3);
});

test('steps are sorted by their stable index', () => {
  const vm = freshVm();
  upsertStreamingStep(vm, { index: 2, type: 'command', command: 'c', status: 'done' });
  upsertStreamingStep(vm, { index: 0, type: 'list', description: 'list', status: 'done' });
  upsertStreamingStep(vm, { index: 1, type: 'command', command: 'b', status: 'done' });
  assert.deepStrictEqual(vm.streamingSteps.map(s => s.index), [0, 1, 2]);
  assert.deepStrictEqual(vm.streamingSteps.map(s => s.type), ['list', 'command', 'command']);
});

// ── Summary ─────────────────────────────────────────────────────────────────
console.log(`\n# ${passed} passed, ${failed} failed`);
process.exitCode = failed ? 1 : 0;
