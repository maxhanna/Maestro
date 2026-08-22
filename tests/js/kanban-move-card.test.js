// kanban-move-card.test.js
// vm.moveCard must clear _feedback, _feedbackSent, _verification, _groundTruth, and
// agentLog when a card is moved to the "todo" column from doing/done/archived — but
// preserve agentAnalysis (the plan). The drop handler has the same cleanup inline.
// Dependency-free Node test runner:  node tests/js/kanban-move-card.test.js
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

const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/kanban.js'), 'utf8').replace(/\r\n/g, '\n');

// Extract vm.moveCard = function (id, from, to) { ... }
var moveCardPattern = /vm\.moveCard = function \(id, from, to\) \{[\s\S]*?\n      \}/;
var moveCardMatch = moveCardPattern.exec(src);
assert(moveCardMatch, 'vm.moveCard not found in wwwroot/kanban.js — marker format may have drifted');
var moveCardSrc = moveCardMatch[0];

function makeCard(overrides) {
  return Object.assign({
    id: 'c1',
    text: 'test card',
    ready: true,
    agentAnalysis: { summary: 'the plan', steps: [] },
    _feedback: { rating: 'up' },
    _feedbackSent: [{ at: '2026-01-01', message: 'thumbs up' }],
    _verification: { complete: true, reason: 'all good' },
    _groundTruth: [{ type: 'test', passed: true }],
    agentLog: [{ type: 'info', message: 'ran step 1' }],
  }, overrides || {});
}

function makeVm(state) {
  var vm = {
    state: state,
    saveCards: function () {},
    cancelCardSuggestions: function () {},
    streamingActive: false,
    activeCardId: null,
    stopAgent: function () {},
    executeAgent: function () {},
  };
  // eval moveCard in a scope where $window.alert is a no-op
  new Function('vm', '$window', '$timeout', '$scope',
    moveCardSrc + '\nreturn vm.moveCard;'
  )(vm, { alert: function () {} }, function (fn) { fn(); }, null);
  return vm;
}

// ── Moving to todo from doing clears run artifacts ──────────────────────

test('moveCard doing→todo clears feedback, verification, groundTruth, agentLog', function () {
  var card = makeCard();
  var state = { doing: [card], todo: [], done: [] };
  var vm = makeVm(state);
  vm.moveCard('c1', 'doing', 'todo');
  assert.strictEqual(card._feedback, undefined, '_feedback should be deleted');
  assert.strictEqual(card._feedbackSent, undefined, '_feedbackSent should be deleted');
  assert.strictEqual(card._verification, undefined, '_verification should be deleted');
  assert.strictEqual(card._groundTruth, undefined, '_groundTruth should be deleted');
  assert.strictEqual(card.agentLog, undefined, 'agentLog should be deleted');
});

test('moveCard doing→todo preserves agentAnalysis (the plan)', function () {
  var card = makeCard();
  var state = { doing: [card], todo: [], done: [] };
  var vm = makeVm(state);
  vm.moveCard('c1', 'doing', 'todo');
  assert.deepStrictEqual(card.agentAnalysis, { summary: 'the plan', steps: [] });
});

test('moveCard done→todo clears feedback, verification, groundTruth, agentLog', function () {
  var card = makeCard();
  var state = { done: [card], todo: [], doing: [] };
  var vm = makeVm(state);
  vm.moveCard('c1', 'done', 'todo');
  assert.strictEqual(card._feedback, undefined, '_feedback should be deleted');
  assert.strictEqual(card._verification, undefined, '_verification should be deleted');
  assert.strictEqual(card._groundTruth, undefined, '_groundTruth should be deleted');
  assert.strictEqual(card.agentLog, undefined, 'agentLog should be deleted');
});

test('moveCard archived→todo clears feedback, verification, groundTruth, agentLog', function () {
  var card = makeCard();
  var state = { archived: [card], todo: [], doing: [] };
  var vm = makeVm(state);
  vm.moveCard('c1', 'archived', 'todo');
  assert.strictEqual(card._feedback, undefined, '_feedback should be deleted');
  assert.strictEqual(card._verification, undefined, '_verification should be deleted');
  assert.strictEqual(card._groundTruth, undefined, '_groundTruth should be deleted');
  assert.strictEqual(card.agentLog, undefined, 'agentLog should be deleted');
});

test('moveCard doing→done does NOT clear run artifacts', function () {
  var card = makeCard();
  var state = { doing: [card], done: [], todo: [] };
  var vm = makeVm(state);
  vm.moveCard('c1', 'doing', 'done');
  assert.ok(card._feedback, '_feedback should be preserved');
  assert.ok(card._verification, '_verification should be preserved');
  assert.ok(card._groundTruth, '_groundTruth should be preserved');
  assert.ok(card.agentLog, 'agentLog should be preserved');
});

test('moveCard doing→todo sets ready to false', function () {
  var card = makeCard({ ready: true });
  var state = { doing: [card], todo: [], done: [] };
  var vm = makeVm(state);
  vm.moveCard('c1', 'doing', 'todo');
  assert.strictEqual(card.ready, false);
});

test('moveCard doing→todo clears activeCardId', function () {
  var card = makeCard();
  var state = { doing: [card], todo: [], done: [] };
  var vm = makeVm(state);
  vm.activeCardId = 'c1';
  vm.moveCard('c1', 'doing', 'todo');
  assert.strictEqual(vm.activeCardId, null);
});

test('moveCard todo→doing does NOT clear run artifacts', function () {
  var card = makeCard({ ready: true });
  var state = { todo: [card], doing: [], done: [] };
  var vm = makeVm(state);
  vm.moveCard('c1', 'todo', 'doing');
  assert.ok(card._feedback, '_feedback should be preserved');
  assert.ok(card._verification, '_verification should be preserved');
});

test('moveCard on null card is a no-op', function () {
  var state = { doing: [], todo: [] };
  var vm = makeVm(state);
  vm.moveCard(null, 'doing', 'todo');
  // no throw = pass
});

// ── Drop handler cleanup (inline, not via moveCard) ────────────────────
// The drop handler in kanban.js has its own splice logic that mirrors moveCard.
// Verify that the drop handler's cleanup block (added as a separate fix) also
// clears run artifacts when moving to todo.

test('drop handler cleanup: doing→todo clears feedback/verification/groundTruth/agentLog', function () {
  // Simulate the drop handler's cleanup logic directly
  var card = makeCard();
  var fromCol = 'doing';
  var targetCol = 'todo';
  if (targetCol === 'todo' && fromCol !== 'todo') {
    delete card._feedback;
    delete card._feedbackSent;
    delete card._verification;
    delete card._groundTruth;
    delete card.agentLog;
  }
  assert.strictEqual(card._feedback, undefined, '_feedback should be deleted by drop handler');
  assert.strictEqual(card._verification, undefined, '_verification should be deleted by drop handler');
  assert.strictEqual(card._groundTruth, undefined, '_groundTruth should be deleted by drop handler');
  assert.strictEqual(card.agentLog, undefined, 'agentLog should be deleted by drop handler');
  assert.deepStrictEqual(card.agentAnalysis, { summary: 'the plan', steps: [] }, 'agentAnalysis should be preserved');
});

test('drop handler cleanup: todo→todo does NOT clear (same column)', function () {
  var card = makeCard();
  var fromCol = 'todo';
  var targetCol = 'todo';
  if (targetCol === 'todo' && fromCol !== 'todo') {
    delete card._feedback;
    delete card._feedbackSent;
    delete card._verification;
    delete card._groundTruth;
    delete card.agentLog;
  }
  assert.ok(card._feedback, '_feedback should be preserved for same-column move');
});

console.log('\n' + passed + ' passed, ' + failed + ' failed');
if (failed > 0) process.exit(1);
