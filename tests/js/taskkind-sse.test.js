// taskkind-sse.test.js
// Unit tests for wwwroot/agent.js's SSE `case 'taskKind':` handler — the live
// dump-vs-build badge that lands on the card when a run starts: card._taskKind is
// set from parsed.taskKind ('dump' / 'build') and persisted via saveCards, and a
// null taskKind clears the badge. Extracted from the live source and eval'd with a
// mock `vm`, mirroring the context-sse test's approach.
// Dependency-free Node test runner:  node tests/js/taskkind-sse.test.js
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

// ── Extract the handler body from the live source ───────────────────────────
const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/agent.js'), 'utf8');
const match = /case 'taskKind':([\s\S]*?)\n\s*case 'steerDelivered':/.exec(src);
assert(match, "case 'taskKind': handler not found in wwwroot/agent.js — marker format may have drifted");

// Strip comment lines, blank lines, and the trailing `break;`.
const statements = match[1]
  .split('\n')
  .map(l => l.trim())
  .filter(l => l && !l.startsWith('//') && l !== 'break;')
  .join('\n');

// eslint-disable-next-line no-new-func
const handleTaskKind = new Function('parsed', 'vm', statements);

function makeVm(card) {
  const saveCalls = [];
  const vm = {
    findCardById: (id) => (card && id === card.id ? card : null),
    saveCards: () => saveCalls.push('save'),
  };
  return { vm, saveCalls };
}

// ── Handler behavior ────────────────────────────────────────────────────────

test("sets card._taskKind to the dump badge and saves", () => {
  const card = { id: 'card-1' };
  const { vm, saveCalls } = makeVm(card);
  handleTaskKind({ cardId: 'card-1', taskKind: 'dump' }, vm);
  assert.strictEqual(card._taskKind, 'dump');
  assert.deepStrictEqual(saveCalls, ['save']);
});

test("sets card._taskKind to the build badge", () => {
  const card = { id: 'card-2' };
  const { vm } = makeVm(card);
  handleTaskKind({ cardId: 'card-2', taskKind: 'build' }, vm);
  assert.strictEqual(card._taskKind, 'build');
});

test("null taskKind clears the badge and saves", () => {
  const card = { id: 'card-3', _taskKind: 'dump' };
  const { vm, saveCalls } = makeVm(card);
  handleTaskKind({ cardId: 'card-3', taskKind: null }, vm);
  assert.strictEqual(card._taskKind, null);
  assert.deepStrictEqual(saveCalls, ['save']);
});

test("no matching card is a silent no-op", () => {
  const { vm, saveCalls } = makeVm(null);
  handleTaskKind({ cardId: 'card-missing', taskKind: 'dump' }, vm);
  assert.strictEqual(saveCalls.length, 0);
});

test("missing findCardById helper never crashes the handler", () => {
  const vm = {};
  handleTaskKind({ cardId: 'card-1', taskKind: 'dump' }, vm);
  assert.strictEqual(vm._taskKind, undefined);
});

// ── Summary ─────────────────────────────────────────────────────────────────
console.log(`\n# ${passed} passed, ${failed} failed`);
if (failed > 0) process.exit(1);
