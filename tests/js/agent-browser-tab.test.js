// agent-browser-tab.test.js
// Locks the Agent panel browser-tab guard used by the live web-test surface.
// Dependency-free Node test runner:  node tests/js/agent-browser-tab.test.js
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
    console.log('  \u2713 ' + name);
  } catch (e) {
    failed++;
    console.error('  \u2717 ' + name);
    console.error('      ' + (e && e.message));
  }
}

const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/agent.js'), 'utf8').replace(/\r\n/g, '\n');
const fnMatch = /function normalizeAgentPanelTab\(tab, webtestEvents\) \{\n[\s\S]*?\n        \}/.exec(src);
assert(fnMatch, 'normalizeAgentPanelTab not found in wwwroot/agent.js - marker format may have drifted');
const normalizeAgentPanelTab = eval('(function () { ' + fnMatch[0] + ' return normalizeAgentPanelTab; })()');

test('browser tab is available once webtest events exist', () => {
  assert.strictEqual(normalizeAgentPanelTab('browser', [{ phase: 'snapshot' }]), 'browser');
});

test('browser tab falls back to activity when no webtest events exist', () => {
  assert.strictEqual(normalizeAgentPanelTab('browser', []), 'activity');
  assert.strictEqual(normalizeAgentPanelTab('browser', null), 'activity');
});

test('unknown tabs fall back to activity even with browser events', () => {
  assert.strictEqual(normalizeAgentPanelTab('files', [{ phase: 'snapshot' }]), 'activity');
});

console.log('\nagent-browser-tab.test.js: ' + passed + ' passed / ' + failed + ' failed / ' + (passed + failed) + ' tests');
process.exit(failed ? 1 : 0);
