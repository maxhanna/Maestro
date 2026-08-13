// kanban-load-drain.test.js
// Unit tests for the fix behind "when the app loads and no agent is running, drain any
// parked _endpointQueued cron cards in To Do so a queued fire left from a previous
// session still starts." A cron fire that landed while the endpoint was busy is parked
// in To Do with _endpointQueued=true and persisted in boarddata. processQueuedCards (the
// queue drain) only fires when a run FINISHES — but after an app reload there is no
// finishing run, so the parked card would sit forever. loadBoardData now calls the drain
// on load when shouldDrainParkedCardsOnLoad says the board is loaded, nothing is
// streaming, and a READY parked card is waiting.
//
// The helper is extracted from the live source (meeting-ticker/board-heal pattern);
// a marker assert fails loudly if the format drifts.
// Dependency-free Node test runner:  node tests/js/kanban-load-drain.test.js
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

// ── Extract shouldDrainParkedCardsOnLoad from the live kanban.js ──────────
const kanbanSrc = fs.readFileSync(path.join(__dirname, '../../wwwroot/kanban.js'), 'utf8').replace(/\r\n/g, '\n');
const fnMatch = /function shouldDrainParkedCardsOnLoad\(boardState, streamingActive\) \{\n[\s\S]*?\n      \}/.exec(kanbanSrc);
assert(fnMatch, 'shouldDrainParkedCardsOnLoad not found in wwwroot/kanban.js — marker format may have drifted');
const shouldDrainParkedCardsOnLoad = eval('(function () { ' + fnMatch[0] + ' return shouldDrainParkedCardsOnLoad; })()');

function parkedCard(overrides) {
  return Object.assign({ id: 'c1', text: 'queued cron job', filePath: 'P1', _fromCron: true, _endpointQueued: true, ready: true, selfImproving: false }, overrides || {});
}

test('parked READY cron card in To Do + idle board → drain', () => {
  const state = { todo: [parkedCard()] };
  assert.strictEqual(shouldDrainParkedCardsOnLoad(state, false), true);
});

test('board is streaming (a run is active) → never drain', () => {
  const state = { todo: [parkedCard()] };
  assert.strictEqual(shouldDrainParkedCardsOnLoad(state, true), false);
});

test('no parked cards → no drain', () => {
  const state = { todo: [{ id: 'c1', text: 'normal card', filePath: 'P1', ready: true }] };
  assert.strictEqual(shouldDrainParkedCardsOnLoad(state, false), false);
});

test('parked card that was set UNREADY → no drain (user opted out)', () => {
  const state = { todo: [parkedCard({ ready: false })] };
  assert.strictEqual(shouldDrainParkedCardsOnLoad(state, false), false);
});

test('self-improving parked card is not in the To Do drain path → no drain', () => {
  const state = { todo: [parkedCard({ selfImproving: true })] };
  assert.strictEqual(shouldDrainParkedCardsOnLoad(state, false), false);
});

test('missing or unloaded board state → no drain', () => {
  assert.strictEqual(shouldDrainParkedCardsOnLoad(null, false), false);
  assert.strictEqual(shouldDrainParkedCardsOnLoad({}, false), false);
  assert.strictEqual(shouldDrainParkedCardsOnLoad(undefined, false), false);
});

test('a plain _autoQueued suggestion card also drains on load (same parked queue)', () => {
  // _autoQueued suggestion cards ride the same processQueuedCards drain and are
  // equally stranded after a reload — the load hook drains them too.
  const state = { todo: [{ id: 's1', text: 'suggestion', filePath: 'P1', _autoQueued: true, ready: true }] };
  assert.strictEqual(shouldDrainParkedCardsOnLoad(state, false), true);
});

console.log('\nkanban-load-drain.test.js: ' + passed + ' passed / ' + failed + ' failed / ' + (passed + failed) + ' tests');
process.exit(failed ? 1 : 0);
