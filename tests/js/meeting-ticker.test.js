// meeting-ticker.test.js
// Unit tests for wwwroot/meeting.js's batchTickerInfo — the client-side parser for the
// deterministic-batch marker "(deterministic batch: N edits, applied N/M units)". The helper
// lives inside the Angular MeetingMixin factory closure, so we extract its source text and eval
// it with a stubbed `basename`, mirroring how the agent uses it.
// Dependency-free Node test runner:  node tests/js/meeting-ticker.test.js
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

// ── Extract batchTickerInfo from the live source ──────────────────────────
const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/meeting.js'), 'utf8');
const infoMatch = /function batchTickerInfo\(st\) \{[\s\S]*?\n        \}/.exec(src);
assert(infoMatch, 'batchTickerInfo not found in wwwroot/meeting.js — marker format may have drifted');

// Rebuild the function with a `basename` stub (same semantics as meeting.js: backslashes → /, last segment).
function basename(p) {
  var s = String(p || '').replace(/\\/g, '/');
  return s.split('/').pop() || s;
}
const batchTickerInfo = eval('(function batchTickerInfo(st) {' +
  infoMatch[0].replace(/^function batchTickerInfo\(st\) \{/, '').replace(/\n        \}$/, '') + '})');

// Label shorthand mirroring the step watcher's usage: `info ? info.label : null`.
function labelFor(st) {
  const info = batchTickerInfo(st);
  return info ? info.label : null;
}

console.log('batchTickerInfo ticker tests\n');

// ── Parsing the enriched marker ───────────────────────────────────────────
test('full batch: "applied 5/5 occurrences" → "5/5 occurrences updated · config.ts"', function () {
  const st = { newStringPreview: '(deterministic batch: 5 edits, applied 5/5 occurrences)', path: 'src/config.ts' };
  assert.strictEqual(labelFor(st), '5/5 occurrences updated · config.ts');
});

test('partial batch: "applied 2/4 occurrences" → "2/4 occurrences updated"', function () {
  const st = { newStringPreview: '(deterministic batch: 2 edits, applied 2/4 occurrences)', path: 'config.ts' };
  assert.strictEqual(labelFor(st), '2/4 occurrences updated · config.ts');
});

test('member batch: "applied 2/2 classes" → "2/2 classes updated"', function () {
  const st = { newStringPreview: '(deterministic batch: 2 edits, applied 2/2 classes)', path: 'Models/Dtos.cs' };
  assert.strictEqual(labelFor(st), '2/2 classes updated · Dtos.cs');
});

test('single-match batch: "applied 1/1 class" keeps singular unit', function () {
  const st = { newStringPreview: '(deterministic batch: 1 edits, applied 1/1 class)', path: 'Dtos.cs' };
  assert.strictEqual(labelFor(st), '1/1 class updated · Dtos.cs');
});

test('windows path: backslashes normalize to basename', function () {
  const st = { newStringPreview: '(deterministic batch: 3 edits, applied 3/3 occurrences)', path: 'src\\models\\User.ts' };
  assert.strictEqual(labelFor(st), '3/3 occurrences updated · User.ts');
});

test('no path: label without the file suffix', function () {
  const st = { newStringPreview: '(deterministic batch: 5 edits, applied 5/5 occurrences)' };
  assert.strictEqual(labelFor(st), '5/5 occurrences updated');
});

test('long path truncates to 40 chars + ellipsis', function () {
  // The BASENAME is what matters — a genuinely long final segment must truncate.
  const long = 'src/' + 'Segment'.repeat(12) + '.config.ts';
  const st = { newStringPreview: '(deterministic batch: 5 edits, applied 5/5 occurrences)', path: long };
  const label = labelFor(st);
  assert.ok(label.startsWith('5/5 occurrences updated · '));
  assert.ok(label.length <= 40 + '5/5 occurrences updated · '.length + 1, 'label too long: ' + label);
  assert.ok(/…$/.test(label), 'long basename should end with ellipsis');
});

// ── Non-batch steps must NOT be hijacked ──────────────────────────────────
test('regular edit: no marker → null (falls back to generic label)', function () {
  assert.strictEqual(batchTickerInfo({ newStringPreview: 'public string Email { get; set; }', path: 'User.cs' }), null);
});

test('LLM batch marker "(batch:" → null (never matched)', function () {
  assert.strictEqual(batchTickerInfo({ newStringPreview: '(batch: 3 edits)', path: 'a.ts' }), null);
});

test('step with no newStringPreview → null', function () {
  assert.strictEqual(batchTickerInfo({ path: 'a.ts' }), null);
  assert.strictEqual(batchTickerInfo(null), null);
});

// ── Partial-batch detection ───────────────────────────────────────────────
test('batchTickerInfo: partial when applied < total', function () {
  const info = batchTickerInfo({ newStringPreview: '(deterministic batch: 2 edits, applied 2/4 occurrences)', path: 'config.ts' });
  assert.ok(info, 'expected info');
  assert.strictEqual(info.partial, true);
  assert.strictEqual(info.applied, 2);
  assert.strictEqual(info.total, 4);
  assert.strictEqual(info.label, '2/4 occurrences updated · config.ts');
});

test('batchTickerInfo: full batch (applied === total) is not partial', function () {
  const info = batchTickerInfo({ newStringPreview: '(deterministic batch: 5 edits, applied 5/5 occurrences)', path: 'config.ts' });
  assert.ok(info, 'expected info');
  assert.strictEqual(info.partial, false);
  assert.strictEqual(info.applied, 5);
  assert.strictEqual(info.total, 5);
});

test('batchTickerInfo: member batches report partial too', function () {
  const info = batchTickerInfo({ newStringPreview: '(deterministic batch: 1 edits, applied 1/3 classes)', path: 'Dtos.cs' });
  assert.ok(info, 'expected info');
  assert.strictEqual(info.partial, true);
  assert.strictEqual(info.unit, 'classes');
});

test('batchTickerInfo: null on non-batch / missing steps', function () {
  assert.strictEqual(batchTickerInfo({ newStringPreview: 'public string Email { get; set; }', path: 'User.cs' }), null);
  assert.strictEqual(batchTickerInfo({ newStringPreview: '(batch: 3 edits)', path: 'a.ts' }), null);
  assert.strictEqual(batchTickerInfo({ path: 'a.ts' }), null);
  assert.strictEqual(batchTickerInfo(null), null);
});

// ── Format-drift guard: the generator emits exactly what this parser reads ─
test('marker format matches the C# generator emission (multi-swap)', function () {
  // DeterministicEditGenerator: "(deterministic batch: {N} edits, applied {N}/{M} occurrences)"
  const marker = '(deterministic batch: 5 edits, applied 5/5 occurrences)';
  assert.strictEqual(labelFor({ newStringPreview: marker, path: 'config.ts' }), '5/5 occurrences updated · config.ts');
});

test('marker format matches the C# generator emission (multi-member)', function () {
  // DeterministicEditGenerator: "(deterministic batch: {N} edits, applied {N}/{M} {kindLabel})"
  const marker = '(deterministic batch: 2 edits, applied 2/2 interfaces)';
  assert.strictEqual(labelFor({ newStringPreview: marker, path: 'api.ts' }), '2/2 interfaces updated · api.ts');
});

console.log('\n' + (failed ? failed + ' FAILED, ' + passed + ' passed' : 'All ' + passed + ' tests passed') + '\n');
process.exit(failed ? 1 : 0);
