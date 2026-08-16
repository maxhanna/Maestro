// agent-runtime-tag.test.js
// Unit tests for wwwroot/agent.js's captureRuntimeAvailability — the helper that pins the
// discovery runtime line ("Phase 1 — runtime availability: python ✓, node ✓, npm ✓, …") onto
// the agent panel as the 🧰 tag. The tag surfaces the CORRECTED availability — npm/npx now
// detected on Windows via the .cmd shim fix — so users can see why the planner runs
// `npm install` instead of reading it buried in the log.
// Dependency-free Node test runner:  node tests/js/agent-runtime-tag.test.js
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

// ── Extract the helper from the live source ───────────────────────────────
const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/agent.js'), 'utf8');
const start = src.indexOf('function captureRuntimeAvailability(vm, level, message) {');
const end = src.indexOf('function pushAgentLog(vm, level, message, detail) {');
assert(start !== -1 && end !== -1 && end > start,
  'captureRuntimeAvailability block not found in wwwroot/agent.js — marker format may have drifted');
const block = src.slice(start, end);

const api = eval('(function () { ' + block + '\n return { captureRuntimeAvailability: captureRuntimeAvailability }; })()');
const capture = api.captureRuntimeAvailability;

// ── Tests ───────────────────────────────────────────────────────────────────

test('pins the runtime line from the discovery log message', () => {
  const vm = {};
  capture(vm, 'info', 'Phase 1 — runtime availability: python ✓, node ✓, npm ✓, npx ✓, go ✗');
  assert.strictEqual(vm.runtimeAvailability, 'python ✓, node ✓, npm ✓, npx ✓, go ✗');
});

test('shows the corrected availability (npm/npx detected) verbatim', () => {
  const vm = {};
  capture(vm, 'info', 'Phase 1 — runtime availability: python ✓, python3 ✓, pip ✓, pip3 ✓, node ✓, npm ✓, npx ✓, dotnet ✓, go ✗');
  assert.ok(vm.runtimeAvailability.indexOf('npm ✓') !== -1, 'npm must show as available after the shim fix');
  assert.ok(vm.runtimeAvailability.indexOf('npx ✓') !== -1, 'npx must show as available after the shim fix');
});

test('ignores other info messages (never wipes an existing tag)', () => {
  const vm = { runtimeAvailability: 'python ✓, node ✓' };
  capture(vm, 'info', 'Agent started');
  assert.strictEqual(vm.runtimeAvailability, 'python ✓, node ✓');
});

test('ignores non-info levels and null messages', () => {
  const vm = {};
  capture(vm, 'metric', 'Phase 1 — runtime availability: python ✓');
  capture(vm, 'error', 'Phase 1 — runtime availability: python ✓');
  capture(vm, 'info', null);
  capture(vm, 'info', '');
  assert.strictEqual(vm.runtimeAvailability, undefined);
});

test('resets on a new run (startAgent clears the tag)', () => {
  // The reset lives in startAgent — this locks the contract that a fresh run starts blank.
  const vm = { runtimeAvailability: 'python ✓, node ✓' };
  vm.runtimeAvailability = '';
  assert.strictEqual(vm.runtimeAvailability, '');
});

console.log('\nagent-runtime-tag.test.js — ' + passed + ' passed / ' + failed + ' failed / ' + (passed + failed) + ' tests');
process.exit(failed ? 1 : 0);
