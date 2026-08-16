// log-follow.test.js
// Live logs auto-follow: while the log is already pinned to the bottom, new entries
// keep scrolling it down; if the user scrolled UP to read older entries, new arrivals
// must NOT yank the view away. vm.scrollToBottom (wwwroot/app.js) snaps each live log
// container (agent 📋 Log / 💬 LLM Streaming) to the bottom UNLESS its __logFollow flag
// is false — a flag a capture-phase document scroll listener updates (leaving the
// bottom disables follow, returning re-enables it) and the ▲/▼ buttons keep in sync
// (▼ = follow, ▲ = not).
// Dependency-free Node test runner:  node tests/js/log-follow.test.js
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

// ── Extract vm.scrollToBottom + the scroll listener from the live app.js ──
const appSrc = fs.readFileSync(path.join(__dirname, '../../wwwroot/app.js'), 'utf8').replace(/\r\n/g, '\n');
const sbMatch = /vm\.scrollToBottom = function \(\) \{[\s\S]*?\n      \};/.exec(appSrc);
assert(sbMatch, 'vm.scrollToBottom not found in wwwroot/app.js — marker format may have drifted');
const listenerMatch = /document\.addEventListener\('scroll', function \(e\) \{[\s\S]*?\n        \}, true\);/g.exec(appSrc);
assert(listenerMatch, 'the capture-phase scroll listener not found in wwwroot/app.js — marker format may have drifted');

function makeScrollToBottom(deps) {
  const vm = {};
  const $timeout = deps.timeout;
  const document = deps.document;
  eval(sbMatch[0]);
  return { vm, fn: vm.scrollToBottom };
}
function makeScrollListener(deps) {
  let captured = null;
  const document = { addEventListener: function (ev, handler, capture) { captured = { ev, handler, capture }; } };
  eval(listenerMatch[0]);
  return captured;
}
function fakeEl(scrollTop, scrollHeight, clientHeight) {
  return { nodeType: 1, scrollTop: scrollTop, scrollHeight: scrollHeight, clientHeight: clientHeight, classList: { contains: function (c) { return c === 'log-entries' || c === 'streaming-tokens'; } } };
}

// ── vm.scrollToBottom follows only when the user hasn't scrolled up ──────
test('log at the bottom (flag unset) → new entry scrolls to the bottom', () => {
  const el = fakeEl(0, 200, 100);
  const { fn } = makeScrollToBottom({ timeout: function (f) { f(); }, document: { querySelectorAll: function () { return [el]; } } });
  fn();
  assert.strictEqual(el.scrollTop, 200);
});

test('user scrolled up (__logFollow false) → new entry does NOT yank the view', () => {
  const el = fakeEl(40, 200, 100);
  el.__logFollow = false;
  const { fn } = makeScrollToBottom({ timeout: function (f) { f(); }, document: { querySelectorAll: function () { return [el]; } } });
  fn();
  assert.strictEqual(el.scrollTop, 40); // untouched
});

test('user back at the bottom (__logFollow true) → follow resumes', () => {
  const el = fakeEl(90, 200, 100);
  el.__logFollow = true;
  const { fn } = makeScrollToBottom({ timeout: function (f) { f(); }, document: { querySelectorAll: function () { return [el]; } } });
  fn();
  assert.strictEqual(el.scrollTop, 200);
});

test('multiple live containers followed independently', () => {
  const pinned = fakeEl(0, 300, 100);          // at bottom → snap
  const reading = fakeEl(50, 300, 100);        // scrolled up → leave
  reading.__logFollow = false;
  const { fn } = makeScrollToBottom({ timeout: function (f) { f(); }, document: { querySelectorAll: function () { return [pinned, reading]; } } });
  fn();
  assert.strictEqual(pinned.scrollTop, 300);
  assert.strictEqual(reading.scrollTop, 50);
});

test('throttled — a pending follow coalesces bursts (tokens arrive per-chunk)', () => {
  const el = fakeEl(0, 100, 50);
  let runs = 0;
  const pending = [];
  const { vm, fn } = makeScrollToBottom({
    timeout: function (f) { runs++; pending.push(f); }, // async like real $timeout
    document: { querySelectorAll: function () { return [el]; } }
  });
  fn(); fn(); fn();
  assert.strictEqual(runs, 1, 'only the first call schedules the follow while one is pending');
  assert.strictEqual(vm._scrollFollowPending, true, 'pending flag held while the follow is scheduled');
  // the scheduled follow fires once, snaps, and clears the pending flag
  pending.forEach(function (f) { f(); });
  assert.strictEqual(el.scrollTop, 100);
  assert.strictEqual(vm._scrollFollowPending, false);
});

// ── The capture-phase scroll listener tracks the user's intent ───────────
test('scrolling away from the bottom disables follow; returning re-enables it', () => {
  const captured = makeScrollListener({});
  assert.strictEqual(captured.ev, 'scroll');
  assert.strictEqual(captured.capture, true, 'capture phase required — scroll events do not bubble');
  // near the bottom: 200-88-100 = 12 < 24 → follow stays on
  const near = fakeEl(88, 200, 100);
  captured.handler({ target: near });
  assert.strictEqual(near.__logFollow, true);
  // scrolled well up: 200-30-100 = 70 ≥ 24 → follow off
  const up = fakeEl(30, 200, 100);
  captured.handler({ target: up });
  assert.strictEqual(up.__logFollow, false);
  // back to the bottom → follow re-enabled
  const back = fakeEl(95, 200, 100); // 200-95-100 = 5 < 24
  captured.handler({ target: back });
  assert.strictEqual(back.__logFollow, true);
});
test('scrolled well above the bottom → follow off', () => {
  const captured = makeScrollListener({});
  const el = fakeEl(30, 200, 100); // 200-30-100 = 70 ≥ 24 → not near bottom
  captured.handler({ target: el });
  assert.strictEqual(el.__logFollow, false);
});
test('non-log element scrolls are ignored', () => {
  const captured = makeScrollListener({});
  const el = { classList: { contains: function () { return false; } } };
  captured.handler({ target: el });
  assert.strictEqual(el.__logFollow, undefined);
});

// ── Template / wiring contract ────────────────────────────────────────────
const agentSrc = fs.readFileSync(path.join(__dirname, '../../wwwroot/agent.js'), 'utf8').replace(/\r\n/g, '\n');
test('streaming tokens keep the view pinned via vm.scrollToBottom', () => {
  assert.ok(/if \(vm\.scrollToBottom\) vm\.scrollToBottom\(\);\s*\n\s*\}\s*\n\s*break;/.test(agentSrc),
    'the token case must call vm.scrollToBottom after appending');
  assert.ok(/if \(vm\.scrollToBottom\) vm\.scrollToBottom\(\);/.test(agentSrc),
    'pushAgentLog must keep calling vm.scrollToBottom for new log entries');
});

const kanbanSrc = fs.readFileSync(path.join(__dirname, '../../wwwroot/kanban.js'), 'utf8').replace(/\r\n/g, '\n');
test('scroll buttons keep the follow flag in sync (▲ off, ▼ on)', () => {
  assert.ok(/container\.__logFollow = direction !== 'top';/.test(kanbanSrc),
    'vm.scrollLog must set __logFollow: false for top, true for bottom');
});

// ── Summary ──────────────────────────────────────────────────────────────
console.log('\n' + passed + ' passed / ' + failed + ' failed');
if (failed > 0) process.exit(1);
