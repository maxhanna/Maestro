// board-heal.test.js
// Unit tests for the two ngRepeat:dupes defenses added across the app:
//   1. wwwroot/bughosted.js — upsertRemoteCard: idempotent remote card delivery
//      (a re-delivered executeTask/addCard updates the existing card in ANY
//      column instead of pushing a same-id twin).
//   2. wwwroot/kanban.js loadBoardData — the heal: per-column dedupe, then a
//      cross-column pass keeping the most-advanced copy (archived/done > doing
//      > selfImproving > todo) so no id ever exists in two columns.
// The upsert helpers are extracted from the live source (meeting-ticker pattern);
// the heal lives inline in loadBoardData, so this file mirrors it and guards
// against drift by asserting the source markers still exist.
// Dependency-free Node test runner:  node tests/js/board-heal.test.js
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

// ── Extract upsertRemoteCard + findCardColumn from the live bughosted.js ──
const bhSrc = fs.readFileSync(path.join(__dirname, '../../wwwroot/bughosted.js'), 'utf8');
const findMatch = /function findCardColumn\(vm, cardId\) \{[\s\S]*?\n        \}/.exec(bhSrc);
const upsertMatch = /function upsertRemoteCard\(vm, card\) \{[\s\S]*?\n        \}/.exec(bhSrc);
assert(findMatch, 'findCardColumn not found in wwwroot/bughosted.js — marker format may have drifted');
assert(upsertMatch, 'upsertRemoteCard not found in wwwroot/bughosted.js — marker format may have drifted');

// Both are function declarations inside the Angular factory's init() — currently
// 8-space indented, so the closing brace is matched as "\n        }". The module-
// level asserts below fail loudly if the format drifts (e.g. a reformat or a
// move to module scope changes the indentation). Eval them together so
// upsertRemoteCard can resolve findCardColumn (hoisting).
const remoteHelpers = eval('(function () { ' + findMatch[0] + '\n' + upsertMatch[0] +
  '\n return { findCardColumn: findCardColumn, upsertRemoteCard: upsertRemoteCard }; })()');
const { upsertRemoteCard } = remoteHelpers;

// ── Mirror of the kanban loadBoardData heal ───────────────────────────────
// The real heal is inline inside loadBoardData's $http callback. Mirror its
// exact algorithm (per-column dedupe, then cross-column most-advanced-wins)
// and keep the mirror honest with the drift guards below.
function healBoardState(state) {
  const cols = ['todo', 'doing', 'done', 'archived', 'selfImproving'];
  const out = {};
  cols.forEach((col) => {
    if (!Array.isArray(state[col])) { out[col] = state[col]; return; }
    const seen = {};
    out[col] = state[col].filter((c) => {
      if (!c || c.id == null) return true;
      if (seen[c.id]) return false;
      seen[c.id] = true;
      return true;
    });
  });
  // Cross-column: keep the most-advanced copy, drop later duplicates.
  const seenGlobally = {};
  ['archived', 'done', 'doing', 'selfImproving', 'todo'].forEach((col) => {
    if (!Array.isArray(out[col])) return;
    out[col] = out[col].filter((c) => {
      if (!c || c.id == null) return true;
      if (seenGlobally[c.id]) return false;
      seenGlobally[c.id] = true;
      return true;
    });
  });
  return out;
}

// Drift guards (module scope, like the extraction asserts): if the real heal
// changes shape, the whole file fails loudly instead of silently testing a
// stale mirror with a passing exit code.
const kanbanSrc = fs.readFileSync(path.join(__dirname, '../../wwwroot/kanban.js'), 'utf8');
assert(kanbanSrc.includes('seenGlobally'), 'kanban.js cross-column marker "seenGlobally" missing — update the mirror');
assert(kanbanSrc.includes("['archived', 'done', 'doing', 'selfImproving', 'todo']"),
  'kanban.js cross-column order changed — update the mirror');
assert(kanbanSrc.includes('droppedIds'), 'kanban.js heal-report marker "droppedIds" missing — update the mirror');

// ── Invariant helpers ─────────────────────────────────────────────────────
function idsByColumn(state) {
  const cols = ['todo', 'doing', 'done', 'archived', 'selfImproving'];
  const map = {};
  cols.forEach((col) => {
    (state[col] || []).forEach((c) => {
      if (!c || c.id == null) return;
      if (!map[c.id]) map[c.id] = [];
      map[c.id].push(col);
    });
  });
  return map;
}

function assertNoCrossColumn(state) {
  const map = idsByColumn(state);
  const violators = Object.keys(map).filter((id) => map[id].length > 1);
  assert.deepStrictEqual(violators, [],
    'same id survives in multiple columns: ' + violators.map((id) => id + '[' + map[id].join(',') + ']').join(', '));
}

console.log('board heal + remote upsert tests\n');

// ── upsertRemoteCard: idempotent delivery ────────────────────────────────
test('upsert: fresh card lands in todo', function () {
  const state = { todo: [], doing: [], done: [] };
  upsertRemoteCard({ state: state }, { id: 'a1', text: 'first' });
  assert.strictEqual(state.todo.length, 1);
  assert.strictEqual(state.todo[0].id, 'a1');
  assert.strictEqual(state.doing.length, 0);
});

test('upsert: re-delivery while card is in doing updates in place (no twin)', function () {
  const state = { todo: [], doing: [{ id: 'a1', text: 'old', priority: 'low' }], done: [] };
  upsertRemoteCard({ state: state }, { id: 'a1', text: 'new', priority: 'high' });
  assert.strictEqual(state.doing.length, 1);
  assert.strictEqual(state.doing[0].text, 'new');
  assert.strictEqual(state.doing[0].priority, 'high');
  assert.strictEqual(state.todo.length, 0);
  assertNoCrossColumn(state);
});

test('upsert: same-id card in todo is updated, not duplicated', function () {
  const state = { todo: [{ id: 'a1', text: 'old' }], doing: [], done: [] };
  upsertRemoteCard({ state: state }, { id: 'a1', text: 'new' });
  assert.strictEqual(state.todo.length, 1);
  assert.strictEqual(state.todo[0].text, 'new');
  assertNoCrossColumn(state);
});

test('upsert: metadata-only re-delivery preserves existing text', function () {
  const state = { todo: [{ id: 'a1', text: 'keep' }], doing: [], done: [] };
  upsertRemoteCard({ state: state }, { id: 'a1', priority: 'high' });
  assert.strictEqual(state.todo[0].text, 'keep');
  assert.strictEqual(state.todo[0].priority, 'high');
});

test('upsert: re-delivery to a card in done stays in done', function () {
  const state = { todo: [], doing: [], done: [{ id: 'z9', text: 'finished' }] };
  upsertRemoteCard({ state: state }, { id: 'z9', text: 'finished' });
  assert.strictEqual(state.done.length, 1);
  assert.strictEqual(state.todo.length, 0);
  assertNoCrossColumn(state);
});

// ── healBoardState: within-column dedupe ─────────────────────────────────
test('heal: within-column duplicate is removed', function () {
  const out = healBoardState({ todo: [{ id: 'a' }, { id: 'a' }, { id: 'b' }], doing: [], done: [] });
  assert.strictEqual(out.todo.length, 2);
  assertNoCrossColumn(out);
});

test('heal: three copies collapse to one', function () {
  const out = healBoardState({ todo: [{ id: 'x' }, { id: 'x' }, { id: 'x' }], doing: [], done: [] });
  assert.strictEqual(out.todo.length, 1);
  assertNoCrossColumn(out);
});

// ── healBoardState: cross-column most-advanced-wins ──────────────────────
test('heal: doing copy outranks a stale todo twin', function () {
  const out = healBoardState({ todo: [{ id: 'a1', text: 'stale-dup' }], doing: [{ id: 'a1', text: 'running' }], done: [] });
  assert.strictEqual(out.doing.length, 1);
  assert.strictEqual(out.doing[0].text, 'running');
  assert.strictEqual(out.todo.length, 0);
  assertNoCrossColumn(out);
});

test('heal: done copy outranks a todo twin', function () {
  const out = healBoardState({ todo: [{ id: 'b2' }], doing: [], done: [{ id: 'b2' }] });
  assert.strictEqual(out.done.length, 1);
  assert.strictEqual(out.todo.length, 0);
  assertNoCrossColumn(out);
});

test('heal: archived copy outranks all', function () {
  const out = healBoardState({ todo: [{ id: 'c3' }], doing: [{ id: 'c3' }], done: [], archived: [{ id: 'c3' }] });
  assert.strictEqual(out.archived.length, 1);
  assert.strictEqual(out.todo.length, 0);
  assert.strictEqual(out.doing.length, 0);
  assertNoCrossColumn(out);
});

// ── healBoardState: tolerance + no-op cases ──────────────────────────────
test('heal: distinct ids are untouched', function () {
  const state = { todo: [{ id: 'x' }, { id: 'y' }], doing: [{ id: 'z' }], done: [] };
  const out = healBoardState(state);
  assert.strictEqual(out.todo.length, 2);
  assert.strictEqual(out.doing.length, 1);
  assertNoCrossColumn(out);
});

test('heal: null-id and null entries are preserved', function () {
  const out = healBoardState({ todo: [{ text: 'no-id' }, { id: null }, null, { id: 'k' }], doing: [], done: [] });
  assert.strictEqual(out.todo.length, 4);
  assertNoCrossColumn(out);
});

// ── THE INVARIANT the suite exists for ───────────────────────────────────
test('INVARIANT: after heal, no id survives in two columns (bug-shaped boards)', function () {
  // The exact shapes this bug produced: original in doing, stale twin pushed to
  // todo (and archived/done variants), plus same-column doubles.
  const boards = [
    { todo: [{ id: 'uo14lb6', text: 'dup' }, { id: 'uo14lb6', text: 'dup' }], doing: [{ id: 'uo14lb6', text: 'running' }], done: [] },
    { todo: [{ id: 'p1' }, { id: 'p2' }], doing: [{ id: 'p1' }], done: [{ id: 'p2' }] },
    { todo: [{ id: 'q' }, { id: 'q' }, { id: 'q' }], doing: [{ id: 'q' }], done: [{ id: 'q' }] },
    { todo: [{ id: 'r' }], doing: [], done: [], archived: [{ id: 'r' }, { id: 'r' }] },
    { todo: [], doing: [], done: [], archived: [], selfImproving: [{ id: 's' }, { id: 's' }] },
  ];
  boards.forEach((board, i) => {
    const out = healBoardState(board);
    assertNoCrossColumn(out);
    const before = idsByColumn(board);
    const after = idsByColumn(out);
    // Every id that survived must appear exactly once; ids may only disappear
    // (deduped), never move or multiply.
    Object.keys(after).forEach((id) => {
      assert(after[id].length === 1, 'id ' + id + ' survives ' + after[id].length + '× after heal');
      assert(before[id] && before[id].length >= after[id].length, 'id ' + id + ' appeared post-heal');
    });
  });
});

console.log('\n' + passed + ' passed, ' + failed + ' failed');
process.exit(failed ? 1 : 0);
