// kanban-cron-doing.test.js
// Unit tests for the fixes behind "when a scheduled calendar event fires via cron, the
// card isn't added to the Doing column": the board's project filter (cardsForProject)
// only showed cards whose filePath equals the currently selected project. A cron card
// for a DIFFERENT project was still pushed to Doing and the agent ran (output streamed
// in the right-hand panel), but the card was invisible on the board. Now _fromCron
// cards in Doing are surfaced regardless of the selected project — while running AND
// after a stop (the stopped card sits in Doing awaiting cleanup and blocks the schedule
// until removed, so hiding it again would strand it). The same surfacing applies to To
// Do: a cron fire that landed while the endpoint was busy is parked there (marked
// _endpointQueued, queued to start when the current run clears) — a queued scheduled
// job must never be invisible either.
//
// The helper is extracted from the live source (meeting-ticker/board-heal pattern);
// a marker assert fails loudly if the format drifts.
// Dependency-free Node test runner:  node tests/js/kanban-cron-doing.test.js
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

// ── Extract cardsForProject from the live kanban.js ──────────────────────
// The method closes over _cardsCache/_cardsVersion; the eval wrapper re-creates
// those so the extracted body runs against a fake vm unmodified.
const kanbanSrc = fs.readFileSync(path.join(__dirname, '../../wwwroot/kanban.js'), 'utf8').replace(/\r\n/g, '\n');
const fnMatch = /vm\.cardsForProject = function \(col\) \{[\s\S]*?\n        return result;\n      \};/.exec(kanbanSrc);
assert(fnMatch, 'cardsForProject not found in wwwroot/kanban.js — marker format may have drifted');
// The extracted body reads `vm`, `_cardsCache` and `_cardsVersion` from the eval
// closure — expose a setVm so each test can swap in a fresh fake board (and reset
// the cache, which would otherwise carry results across tests).
const extracted = eval('(function () { var _cardsCache = {}; var _cardsVersion = 0; var vm = {};\n' +
  fnMatch[0] + '\nreturn { setVm: function (v) { _cardsCache = {}; _cardsVersion = 0; vm = v; }, cardsForProject: vm.cardsForProject }; })()');
const cardsForProject = function (col) { return extracted.cardsForProject(col); };
function setVm(v) { extracted.setVm(v); }

const Weaver = 'C:/Users/Saint/Desktop/Repos/Weaver';
const BugHosted = 'C:/Users/Saint/Desktop/Repos/BugHosted';

function card(id, filePath, extra) {
  return Object.assign({ id: id, text: 'task ' + id, filePath: filePath }, extra || {});
}

// A fake vm with just enough surface for cardsForProject. filterCards returns
// the list unchanged (the search filter is not under test here).
function makeVm(state, selectedProject) {
  return {
    state: state,
    selectedProject: selectedProject,
    searchFilter: '',
    isInFileSearch: false,
    fileSearchFilter: '',
    filterCards: function (cards) { return cards; }
  };
}

test('cron card in Doing for ANOTHER project is visible (the fix)', () => {
  setVm(makeVm({ doing: [card('c1', Weaver), card('c2', BugHosted, { _fromCron: true })] }, Weaver));
  const shown = cardsForProject('doing').map(function (c) { return c.id; });
  assert.deepStrictEqual(shown, ['c1', 'c2']);
});

test('non-cron card in Doing for another project stays hidden', () => {
  setVm(makeVm({ doing: [card('c1', Weaver), card('c2', BugHosted)] }, Weaver));
  const shown = cardsForProject('doing').map(function (c) { return c.id; });
  assert.deepStrictEqual(shown, ['c1']);
});

test('stopped cron card in Doing for another project is still visible (cleanup)', () => {
  // Stop does NOT delete the cron card — it stays in Doing awaiting cleanup and
  // blocks the schedule (hasLiveCalendarInstance) until removed. It must stay visible.
  setVm(makeVm({ doing: [card('c1', Weaver), card('c2', BugHosted, { _fromCron: true })] }, Weaver));
  const shown = cardsForProject('doing').map(function (c) { return c.id; });
  assert.deepStrictEqual(shown, ['c1', 'c2']);
});

test('cron card for the SELECTED project is shown by the normal filter', () => {
  setVm(makeVm({ doing: [card('c1', BugHosted, { _fromCron: true }), card('c2', BugHosted)] }, BugHosted));
  const shown = cardsForProject('doing').map(function (c) { return c.id; });
  assert.deepStrictEqual(shown, ['c1', 'c2']);
});

test('parked cron card in To Do for ANOTHER project is visible (queued fire)', () => {
  // A cron fire that landed while the endpoint was busy parks in To Do (the fire path
  // marks it _endpointQueued so the queue drain starts it when the run clears). The
  // strict project filter must not hide it — a queued scheduled job is never invisible.
  setVm(makeVm({ todo: [card('c1', Weaver), card('c2', BugHosted, { _fromCron: true, _endpointQueued: true })] }, Weaver));
  const shown = cardsForProject('todo').map(function (c) { return c.id; });
  assert.deepStrictEqual(shown, ['c1', 'c2']);
});

test('non-cron card in To Do for another project stays hidden', () => {
  setVm(makeVm({ todo: [card('c1', Weaver), card('c2', BugHosted)] }, Weaver));
  const shown = cardsForProject('todo').map(function (c) { return c.id; });
  assert.deepStrictEqual(shown, ['c1']);
});

test('cron card in To Do for the SELECTED project is shown by the normal filter', () => {
  setVm(makeVm({ todo: [card('c1', BugHosted, { _fromCron: true }), card('c2', BugHosted)] }, BugHosted));
  const shown = cardsForProject('todo').map(function (c) { return c.id; });
  assert.deepStrictEqual(shown, ['c1', 'c2']);
});

test('no project selected returns every card in the column', () => {
  setVm(makeVm({ doing: [card('c1', Weaver), card('c2', BugHosted, { _fromCron: true })] }, ''));
  const shown = cardsForProject('doing').map(function (c) { return c.id; });
  assert.deepStrictEqual(shown, ['c1', 'c2']);
});

test('cache does not serve a stale list after the column changes (length bump)', () => {
  // First call caches the list with only the Weaver card; when a cron card from
  // another project arrives in Doing (a saveCards POST follows in the real app),
  // the column length changes — the cached list must be recomputed and include it.
  const shared = { doing: [card('c1', Weaver)] };
  setVm(makeVm(shared, Weaver));
  assert.deepStrictEqual(cardsForProject('doing').map(function (c) { return c.id; }), ['c1']);
  shared.doing.push(card('c2', BugHosted, { _fromCron: true }));
  const shown = cardsForProject('doing').map(function (c) { return c.id; });
  assert.deepStrictEqual(shown, ['c1', 'c2']);
});

test('cache does not serve a stale To Do list after a parked cron card arrives', () => {
  const shared = { todo: [card('c1', Weaver)] };
  setVm(makeVm(shared, Weaver));
  assert.deepStrictEqual(cardsForProject('todo').map(function (c) { return c.id; }), ['c1']);
  shared.todo.push(card('c2', BugHosted, { _fromCron: true, _endpointQueued: true }));
  const shown = cardsForProject('todo').map(function (c) { return c.id; });
  assert.deepStrictEqual(shown, ['c1', 'c2']);
});

console.log('\nkanban-cron-doing.test.js: ' + passed + ' passed / ' + failed + ' failed / ' + (passed + failed) + ' tests');
process.exit(failed ? 1 : 0);
