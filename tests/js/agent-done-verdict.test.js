// agent-done-verdict.test.js
// Unit tests for the fix behind "completed cards restart instead of stopping": the 'done'
// SSE event's plan-items check forced incomplete=true whenever a stale plan item was
// unchecked — a no-op repair edit that never emitted a step event, or a preserved rejected
// step — even when the server VERIFIED the run complete (parsed.complete=true, zero
// remaining issues). The card then auto-restarted (Re-starting agent 1/5) instead of
// finishing. applyDoneVerdict makes the server's verdict authoritative: complete=true marks
// every non-rejected plan item done (rejected steps keep their rejected marker); the
// unchecked-step "stays in Doing" gate only applies when the server did NOT verify completion.
//
// The helper is extracted from the live source (meeting-ticker/board-heal pattern);
// a marker assert fails loudly if the format drifts.
// Dependency-free Node test runner:  node tests/js/agent-done-verdict.test.js
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

// ── Extract applyDoneVerdict from the live agent.js ───────────────────────
const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/agent.js'), 'utf8').replace(/\r\n/g, '\n');
const fnMatch = /function applyDoneVerdict\(parsed, planItems\) \{\n[\s\S]*?\n        \}/.exec(src);
assert(fnMatch, 'applyDoneVerdict not found in wwwroot/agent.js — marker format may have drifted');
const applyDoneVerdict = eval('(function () { ' + fnMatch[0] + ' return applyDoneVerdict; })()');

function item(id, done, extra) {
  return Object.assign({ index: id, file: 'f' + id, change: 'c' + id, done: !!done }, extra || {});
}

test('server verified complete → stale unchecked item does NOT force a restart', () => {
  // The benchmark-run shape: the last repair edit was a no-op that never emitted a step
  // event, so its plan item is still unchecked — but the server says complete=true.
  const items = [item(0, true), item(1, true), item(2, false)];
  const verdict = applyDoneVerdict({ complete: true, incomplete: false }, items);
  assert.strictEqual(verdict.incomplete, false);
  assert.strictEqual(verdict.unchecked, undefined);
  // Every real item is now marked done so the board shows a complete plan.
  assert.strictEqual(items[2].done, true);
});

test('server verified complete → rejected steps keep their rejected marker', () => {
  const items = [item(0, true), item(1, false, { status: 'rejected' })];
  const verdict = applyDoneVerdict({ complete: true, incomplete: false }, items);
  assert.strictEqual(verdict.incomplete, false);
  assert.strictEqual(items[1].done, false, 'a rejected step must not flip to done');
  assert.strictEqual(items[1].status, 'rejected');
});

test('server did NOT verify complete + unchecked steps → stays in Doing', () => {
  const items = [item(0, true), item(1, false)];
  const verdict = applyDoneVerdict({ complete: false, incomplete: false }, items);
  assert.strictEqual(verdict.incomplete, true);
  assert.strictEqual(verdict.unchecked, 1);
});

test('server did NOT verify complete + all steps done → server incomplete flag wins', () => {
  const items = [item(0, true), item(1, true)];
  assert.strictEqual(applyDoneVerdict({ complete: false, incomplete: true }, items).incomplete, true);
  assert.strictEqual(applyDoneVerdict({ complete: false, incomplete: false }, items).incomplete, false);
});

test('no plan items → server flag is the verdict', () => {
  assert.strictEqual(applyDoneVerdict({ complete: true, incomplete: false }, []).incomplete, false);
  assert.strictEqual(applyDoneVerdict({ complete: false, incomplete: true }, null).incomplete, true);
  assert.strictEqual(applyDoneVerdict(undefined, []).incomplete, false);
});

console.log('\nagent-done-verdict.test.js: ' + passed + ' passed / ' + failed + ' failed / ' + (passed + failed) + ' tests');
process.exit(failed ? 1 : 0);
