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

// The "it opens a tab" half: the SSE handler for a `webtest` event must (a) switch the
// agent panel to the Browser tab and (b) stage the snapshot so the tab's <img>/headings
// render what the browser saw. Extracted verbatim from the EventSource handler.
const webtestBodyMatch = /case 'webtest':\n([\s\S]*?)\n\s+break;/.exec(src);
assert(webtestBodyMatch, "case 'webtest': block not found in wwwroot/agent.js - marker format may have drifted");
// Capture group 1 is the case BODY (the `case` label / `break` are switch syntax and
// cannot be evaled standalone). Direct eval sees the enclosing function's `parsed`/`vm`.
function handleWebtest(parsed, vm) {
  eval('(function () { ' + webtestBodyMatch[1] + ' })()');
}

test('webtest event auto-opens the browser tab and stages the snapshot', () => {
  var vm = { webtestEvents: [], webtestCurrent: null, agentPanelTab: 'activity' };
  var parsed = { phase: 'snapshot', url: 'http://127.0.0.1:3000/', message: 'Rendered',
    snapshot: { title: 'Benchmark 22', headings: ['Benchmark22'], body: 'Score: 0', imageDataUrl: 'data:image/jpeg;base64,xxx' } };
  handleWebtest(parsed, vm);
  assert.strictEqual(vm.agentPanelTab, 'browser');
  assert.strictEqual(vm.webtestCurrent.snapshot.title, 'Benchmark 22');
  assert.strictEqual(vm.webtestCurrent.snapshot.imageDataUrl, 'data:image/jpeg;base64,xxx');
  assert.strictEqual(vm.webtestCurrent.url, 'http://127.0.0.1:3000/');
  assert.strictEqual(vm.webtestEvents.length, 1);
});

test('webtest event without a snapshot still opens the browser tab (phase only)', () => {
  var vm = { webtestEvents: [], webtestCurrent: null, agentPanelTab: 'activity' };
  handleWebtest({ phase: 'server', url: 'http://127.0.0.1:3000/', message: 'Server started' }, vm);
  assert.strictEqual(vm.agentPanelTab, 'browser');
  assert.strictEqual(vm.webtestCurrent.snapshot, null);
  assert.strictEqual(vm.webtestEvents.length, 1);
});

console.log('\nagent-browser-tab.test.js: ' + passed + ' passed / ' + failed + ' failed / ' + (passed + failed) + ' tests');
process.exit(failed ? 1 : 0);
