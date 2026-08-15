// agent-plan-marker.test.js
// Unit tests for the fix behind "planning thinking is marked as a step and checked off
// while being produced": the backend used to append the transient activity row
// ("Deep thinking for plan — Step 3…", "Proposing step 2…", "Applying edits — Step 2 — …")
// into the 'plan' SSE items array with a done flag, so it rendered as a checkable plan
// step (✅) and counted toward the plan gate. Now the marker travels separately (`marker`)
// and is rendered as a bottom-of-plan status line while the current step is produced.
// planPayloadParts splits the payload; the backend shape (marker field, no marker rows in
// items) is asserted by tests/UnitTests/WebTaskInterleavedPipelineIntegrationTests.cs.
//
// The helper is extracted from the live source (meeting-ticker/board-heal pattern);
// a marker assert fails loudly if the format drifts.
// Dependency-free Node test runner:  node tests/js/agent-plan-marker.test.js
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

// ── Extract planPayloadParts from the live agent.js ────────────────────────
const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/agent.js'), 'utf8').replace(/\r\n/g, '\n');
const fnMatch = /function planPayloadParts\(parsed\) \{\n[\s\S]*?\n        \}/.exec(src);
assert(fnMatch, 'planPayloadParts not found in wwwroot/agent.js — marker format may have drifted');
const planPayloadParts = eval('(function () { ' + fnMatch[0] + ' return planPayloadParts; })()');

test('marker-only event (initial discovery phase) → marker set, zero plan items', () => {
  const parts = planPayloadParts({ items: [], marker: { File: '_planning', Change: 'Reading task & discovery context…' } });
  assert.strictEqual(parts.items.length, 0, 'empty items must stay empty — the marker is not a step');
  assert.deepStrictEqual(parts.marker, { file: '_planning', change: 'Reading task & discovery context…' });
});

test('committed steps + thinking marker → marker never appears in the items list', () => {
  const payload = {
    items: [
      { File: '_command', Change: 'mkdir "benchmark_test_22"', done: true },
      { File: 'benchmark_test_22/index.html', Change: 'Create index.html', done: false }
    ],
    marker: { File: '_planning', Change: 'Deep thinking for plan — Step 3…' }
  };
  const parts = planPayloadParts(payload);
  assert.strictEqual(parts.items.length, 2, 'only the two committed steps are plan items');
  assert.ok(!parts.items.some(function (i) { return i.File === '_planning'; }), 'no _planning row may leak into items');
  assert.ok(!parts.items.some(function (i) { return i.File === '_executing'; }), 'no _executing row may leak into items');
  assert.deepStrictEqual(parts.marker, { file: '_planning', change: 'Deep thinking for plan — Step 3…' });
});

test('executing marker stays separate while the step being produced is NOT done', () => {
  const payload = {
    items: [
      { File: '_command', Change: 'mkdir "benchmark_test_22"', done: true },
      { File: 'benchmark_test_22/index.html', Change: 'Create index.html', done: false }
    ],
    marker: { File: '_executing', Change: 'Applying edits — Step 2 — Create index.html' }
  };
  const parts = planPayloadParts(payload);
  assert.strictEqual(parts.items.length, 2);
  assert.strictEqual(parts.items[1].done, false, 'the step currently being produced must not be marked done');
  assert.deepStrictEqual(parts.marker, { file: '_executing', change: 'Applying edits — Step 2 — Create index.html' });
});

test('no marker in payload → marker is null (final persisted plan)', () => {
  const parts = planPayloadParts({ items: [{ File: 'a.ts', Change: 'x', done: true }] });
  assert.strictEqual(parts.items.length, 1);
  assert.strictEqual(parts.marker, null);
});

test('malformed payload → safe defaults (no throw, no marker)', () => {
  assert.deepStrictEqual(planPayloadParts(undefined), { items: [], marker: null });
  assert.deepStrictEqual(planPayloadParts(null), { items: [], marker: null });
  assert.deepStrictEqual(planPayloadParts({}), { items: [], marker: null });
});

// ── Template contract: the live plan panel renders the marker as a status row ─
const html = fs.readFileSync(path.join(__dirname, '../../wwwroot/index.html'), 'utf8').replace(/\r\n/g, '\n');
test('index.html plan section shows when only the marker exists (initial phase)', () => {
  assert.ok(/ng-if="vm\.agentPanelTabIs\('activity'\) && \(vm\.streamingThinking \|\| vm\.planItems\.length \|\| vm\.streamingSteps\.length \|\| vm\.planMarker\)"/.test(html),
    'plan section must render for a marker-only event so the panel does not freeze during discovery');
});
test('index.html renders the marker as a bottom-of-plan row with a spinner, no checkbox', () => {
  assert.ok(/class="plan-item-row plan-marker-row"/.test(html), 'marker row class must exist');
  assert.ok(/vm\.planMarkerIcon\(vm\.planMarker\.file\)/.test(html), 'marker row must show the phase icon');
  assert.ok(/vm\.planMarkerLabel\(vm\.planMarker\.file, vm\.planMarker\.change\)/.test(html), 'marker row must show the activity text');
});

// ── SSE reader scope: the plan handler must NEVER declare `var parts` ──────
// The reader splits each chunk with `var parts = buffer.split('\n\n')` and iterates
// `for (var p = 0; p < parts.length; p++)`. The plan case lives in the SAME
// $applyAsync function scope, so `var parts = planPayloadParts(parsed)` there
// hoists to the top of that function and shadows the chunk array with `undefined`
// — crashing the panel with "TypeError: Cannot read properties of undefined
// (reading 'length')" at the for loop on every chunk whose first event is not a
// plan event. The split local must keep a distinct name (planParts).
const readerMatch = /var parts = buffer\.split\('\\n\\n'\); buffer = parts\.pop\(\);\n\s*\$scope\.\$applyAsync\(function \(\) \{([\s\S]*?)\n\s*\}\);\n\s*try \{ \$scope\.\$applyAsync\(\); \} catch \(e\) \{ \}\n\s*readNext\(\);/;
const readerExec = readerMatch.exec(src);
assert(readerExec, 'SSE reader pattern not found in wwwroot/agent.js — chunk loop may have drifted');
const applyBody = readerExec[1];
const chunkArrayDecls = (applyBody.match(/\bvar parts\b/g) || []).length;

test('plan handler must not declare `var parts` inside the SSE $applyAsync scope (var-hoisting crash)', () => {
  assert.strictEqual(chunkArrayDecls, 0,
    'found `var parts` inside the SSE reader scope — it hoists over the for-loop and crashes `parts.length` (TypeError: Cannot read properties of undefined)');
});

test('plan handler names its split-payload local `planParts` (distinct from the chunk array)', () => {
  assert.ok(/\bvar planParts = planPayloadParts\(parsed\)/.test(applyBody),
    'plan case must assign planPayloadParts(parsed) to a distinct local named planParts');
  assert.ok(/vm\.planMarker = planParts\.marker/.test(applyBody), 'marker must be read from planParts');
  assert.ok(/vm\.planItems = planParts\.items\.map/.test(applyBody), 'plan items must be mapped from planParts');
});

console.log('\nagent-plan-marker.test.js: ' + passed + ' passed / ' + failed + ' failed / ' + (passed + failed) + ' tests');
process.exit(failed > 0 ? 1 : 0);
