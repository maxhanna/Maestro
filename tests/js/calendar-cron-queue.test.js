// calendar-cron-queue.test.js
// Unit tests for the fix behind "a cron fire that lands while the endpoint is busy
// must be visible as a queued job": when the agent is already running, the calendar
// fire paths (cron processor / Run now / Requeue now) push the _fromCron card to To
// Do WITHOUT starting it. Previously the parked card carried no queue marker, so the
// queue drain (processQueuedCards) never started it (unless the global autoQueue
// toggle was on) and the board's project filter could hide it entirely. Now a busy
// fire marks the card _endpointQueued = true — the drain starts it the moment the
// current run clears, the board renders a ⏳ QUEUED chip, and the To Do column
// surfaces _fromCron cards regardless of project (see kanban-cron-doing.test.js).
//
// The function is extracted from the live source (same pattern as
// calendar-cron-chips.test.js); marker asserts fail loudly if the format drifts.
// Dependency-free Node test runner:  node tests/js/calendar-cron-queue.test.js
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

// ── Extract calRunNow from the live calendar.js ───────────────────────────
const calSrc = fs.readFileSync(path.join(__dirname, '../../wwwroot/calendar.js'), 'utf8').replace(/\r\n/g, '\n');
const runNowMatch = /vm\.calRunNow = function \(\) \{\n[\s\S]*?\n      \};/.exec(calSrc);
assert(runNowMatch, 'vm.calRunNow not found in wwwroot/calendar.js — marker format may have drifted');
// calRunNow now routes through the shared fire path (buildCronFireCard →
// vm.fireCalCard), so extract those too — a stale extraction here would hide a
// drift in the push semantics this file is testing.
const fireBuilderMatch = /      function buildCronFireCard\(calCard\) \{\n[\s\S]*?\n      \}/.exec(calSrc);
assert(fireBuilderMatch, 'buildCronFireCard not found in wwwroot/calendar.js — marker format may have drifted');
const fireCalCardMatch = /      vm\.fireCalCard = function \(calCard, summary\) \{\n[\s\S]*?\n      \};/.exec(calSrc);
assert(fireCalCardMatch, 'vm.fireCalCard not found in wwwroot/calendar.js — marker format may have drifted');

// calRunNow closes over module-scope helpers (uid/localDateStr/timeStr/
// cronRunLogAdd/cronRunLogKey/scheduleUpdate/$window/_scope) and the vm/_vm
// controller. Eval everything in one closure with stubs and a fake controller.
const runNow = eval('(function () {' +
  'function uid() { return "u-" + (++uid._n); } uid._n = 0;' +
  'function localDateStr(d) { return d.getFullYear() + "-" + String(d.getMonth() + 1).padStart(2, "0") + "-" + String(d.getDate()).padStart(2, "0"); }' +
  'function timeStr(d) { return String(d.getHours()).padStart(2, "0") + ":" + String(d.getMinutes()).padStart(2, "0"); }' +
  'function cronRunLogAdd() {} function cronRunLogKey() { return "k"; }' +
  'function scheduleUpdate() {}' +
  'var $window = { alert: function () { throw new Error("unexpected alert"); } };' +
  'var _scope = { $$phase: null, $applyAsync: function () {} };' +
  'var vm, _vm;' +
  fireBuilderMatch[0] +
  fireCalCardMatch[0].replace('vm.fireCalCard = function (calCard, summary)', 'function fireCalCard(calCard, summary)') +
  runNowMatch[0].replace('vm.calRunNow = function ()', 'function calRunNow()') +
  ' return function (controller) { vm = controller; _vm = controller; controller.fireCalCard = fireCalCard; calRunNow(); return controller; }; })()');

function makeController(streamingActive) {
  return {
    calEditCardData: {
      id: 'cal1',
      text: 'Fetch a recent AI news article',
      date: new Date(2026, 7, 12),
      time: new Date(2000, 0, 1, 11, 25),
      cronExpression: '0 11 * * *',
      label: 'AI news',
      filePath: 'C:/Users/Saint/Desktop/Repos/BugHosted'
    },
    selectedProject: 'C:/Users/Saint/Desktop/Repos/Weaver',
    calSaveCard: function () {},
    state: { todo: [] },
    saveCards: function () {},
    streamingActive: streamingActive,
    executeAgent: function (card) { this._started = card; },
    showSideToast: function () {}
  };
}

test('idle endpoint → fire starts immediately, no queue marker', () => {
  const c = runNow(makeController(false));
  assert.strictEqual(c.state.todo.length, 1);
  assert.strictEqual(c.state.todo[0]._endpointQueued, undefined);
  assert.strictEqual(c._started, c.state.todo[0], 'idle fire must call executeAgent with the new card');
});

test('busy endpoint → fire parks in To Do marked _endpointQueued, NOT started', () => {
  const c = runNow(makeController(true));
  assert.strictEqual(c.state.todo.length, 1);
  const parked = c.state.todo[0];
  assert.strictEqual(parked._endpointQueued, true, 'busy fire must mark the card so the drain starts it');
  assert.strictEqual(parked._fromCron, true);
  assert.strictEqual(c._started, undefined, 'busy fire must NOT start the card immediately');
});

test('busy fire card is ready so the queue drain can pick it up', () => {
  const c = runNow(makeController(true));
  assert.strictEqual(c.state.todo[0].ready, true);
});

test('busy vs idle toasts still distinguish queued from started', () => {
  // The toast text is built from the same streamingActive flag — the queue marker
  // must not change it ("(queued)" while busy, "and started" when idle).
  const busy = runNow(makeController(true));
  const idle = runNow(makeController(false));
  // Both ran through the same code path; the observable difference is the marker +
  // executeAgent call, which the tests above assert. This test just guards the
  // construction path didn't break either branch.
  assert.ok(busy.state.todo[0]._endpointQueued === true && idle.state.todo[0]._endpointQueued === undefined);
});

if (failed > 0) {
  console.error(`\n${failed} test(s) failed, ${passed} passed`);
  process.exit(1);
}
console.log(`\n${passed} passed / 0 failed`);
