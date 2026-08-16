// log-scroll-buttons.test.js
// Tests for the ▲/▼ scroll buttons in the agent log sections: kanban.html wires every
// 📋 Log / 📋 Activity Log section to vm.scrollLog, and vm.scrollLog (extracted from
// wwwroot/kanban.js) scrolls the log-entries container of the section the button lives
// in — section-scoped, so a multi-section board never scrolls the wrong log.
// Dependency-free Node test runner:  node tests/js/log-scroll-buttons.test.js
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

// ── Extract vm.scrollLog from the live controller source ───────────────────
const kanbanSrc = fs.readFileSync(path.join(__dirname, '../../wwwroot/kanban.js'), 'utf8');
const fnStart = kanbanSrc.indexOf('vm.scrollLog = function (direction, $event) {');
assert(fnStart !== -1, 'vm.scrollLog not found in wwwroot/kanban.js — marker format may have drifted');
const fnEnd = kanbanSrc.indexOf('};', fnStart);
assert(fnEnd !== -1 && fnEnd > fnStart, 'vm.scrollLog body not closed');
const body = kanbanSrc.slice(fnStart, fnEnd + 2).replace('vm.scrollLog = ', 'var scrollLog = ');

const api = eval('(function () { ' + body + '\n return { scrollLog: scrollLog }; })()');
const scrollLog = api.scrollLog;

// A fake button + section: closest('.card-section') → querySelector('.log-entries').
function fakeButton(container) {
  return {
    closest: function (sel) {
      assert.strictEqual(sel, '.card-section');
      return { querySelector: function (q) { assert.strictEqual(q, '.log-entries'); return container; } };
    }
  };
}

test('scrollLog bottom sets scrollTop to the full height', () => {
  const container = { scrollTop: 0, scrollHeight: 1000 };
  scrollLog('bottom', { currentTarget: fakeButton(container), stopPropagation: () => {}, preventDefault: () => {} });
  assert.strictEqual(container.scrollTop, 1000);
});

test('scrollLog top resets scrollTop to 0', () => {
  const container = { scrollTop: 500, scrollHeight: 1000 };
  scrollLog('top', { currentTarget: fakeButton(container), stopPropagation: () => {}, preventDefault: () => {} });
  assert.strictEqual(container.scrollTop, 0);
});

test('scrollLog ignores a button with no section (never throws)', () => {
  scrollLog('bottom', { currentTarget: { closest: () => null }, stopPropagation: () => {}, preventDefault: () => {} });
  scrollLog('top', { currentTarget: null, stopPropagation: () => {}, preventDefault: () => {} });
  scrollLog('bottom', null);
});

// ── The HTML wires every log section to vm.scrollLog ────────────────────────
const html = fs.readFileSync(path.join(__dirname, '../../wwwroot/kanban.html'), 'utf8');
const scrollMarkup = `ng-click="vm.scrollLog('top', $event)"`;
const scrollDownMarkup = `ng-click="vm.scrollLog('bottom', $event)"`;

test('every log section (📋 Log + 📋 Activity Log) has the ▲/▼ buttons wired to vm.scrollLog', () => {
  const total = countLogSections(html);
  assert.ok(total >= 1, 'at least one log section exists');
  assert.strictEqual(html.split(scrollMarkup).length - 1, total,
    'each log section must have a scroll-to-top button');
  assert.strictEqual(html.split(scrollDownMarkup).length - 1, total,
    'each log section must have a scroll-to-bottom button');
});

test('the Activity Log section specifically has the buttons wired too', () => {
  const actCount = html.split('<span class="section-title">📋 Activity Log</span>').length - 1;
  assert.ok(actCount >= 1, 'at least one Activity Log section exists');
  const actBlock = html.split('<span class="section-title">📋 Activity Log</span>')[1] || '';
  assert.ok(actBlock.includes(scrollMarkup), 'Activity Log section must have a scroll-to-top button');
  assert.ok(actBlock.includes(scrollDownMarkup), 'Activity Log section must have a scroll-to-bottom button');
});

function countLogSections(htmlText) {
  const logs = htmlText.split('<span class="section-title">📋 Log</span>').length - 1;
  const acts = htmlText.split('<span class="section-title">📋 Activity Log</span>').length - 1;
  return logs + acts;
}

console.log('\nlog-scroll-buttons.test.js — ' + passed + ' passed / ' + failed + ' failed / ' + (passed + failed) + ' tests');
process.exit(failed ? 1 : 0);
