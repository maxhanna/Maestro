// suggestion-signals.test.js
// Unit tests for wwwroot/agent.js's collectRunSignals helper — the pure log→problem-signal
// extraction that feeds the suggestion LLM's RUN OUTCOME block so suggestions can fix what
// actually went wrong (verification failures, repair passes, rejected steps) instead of
// building only on the rosy summary. Extracted from the live source and eval'd, mirroring
// the suggestion-cancel.test.js approach.
// Dependency-free Node test runner:  node tests/js/suggestion-signals.test.js
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

// ── Extract the helper from the live source ────────────────────────────────
const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/agent.js'), 'utf8');
const signalMatch = /function collectRunSignals\(log\) \{[\s\S]*?\n                \}/.exec(src);
assert(signalMatch, 'collectRunSignals not found in wwwroot/agent.js — marker format may have drifted');
const collectRunSignals = eval('(function collectRunSignals(log) {' +
  signalMatch[0].replace(/^function collectRunSignals\(log\) \{/, '').replace(/\n                \}$/, '') + '})');

// ── Core behavior ──────────────────────────────────────────────────────────
test('warn/error/rejected entries become signals', function () {
  const out = collectRunSignals([
    { level: 'info', message: 'Agent started' },
    { level: 'warn', message: 'Post-execution verification incomplete (repair pass 1/3): the file was not written' },
    { level: 'error', message: 'Step failed' },
    { level: 'rejected', message: 'Step rejected — invented path' }
  ]);
  assert.strictEqual(out.length, 3, 'warn + error + rejected, info without a marker is dropped');
  assert.ok(out.some(s => s.includes('verification incomplete')));
  assert.ok(out.some(s => s === 'Step failed'));
  assert.ok(out.some(s => s.includes('invented path')));
});

test('info entries mentioning failure markers are captured', function () {
  const out = collectRunSignals([
    { level: 'info', message: 'Agent started' },
    { level: 'info', message: '🔧 Deterministic checks: 1 CONFIRMED issue(s): the OS file was never created' },
    { level: 'info', message: 'Plan completed — moving card to Done' }
  ]);
  assert.strictEqual(out.length, 1, 'only the info entry with a failure marker');
  assert.ok(out[0].includes('Deterministic checks'));
});

test('quiet runs produce no signals', function () {
  const out = collectRunSignals([
    { level: 'info', message: 'Agent started' },
    { level: 'info', message: 'Plan completed — moving card to Done' },
    { level: 'status', message: 'skipped status entries' }
  ]);
  assert.deepStrictEqual(out, []);
  assert.deepStrictEqual(collectRunSignals(null), []);
  assert.deepStrictEqual(collectRunSignals([]), []);
});

test('near-duplicate signals are deduped (numbers normalized)', function () {
  const out = collectRunSignals([
    { level: 'warn', message: 'repair pass 1/3 did not land' },
    { level: 'warn', message: 'repair pass 2/3 did not land' },
    { level: 'warn', message: 'repair pass 3/3 did not land' }
  ]);
  assert.strictEqual(out.length, 1, 'numeric variants collapse to one signal');
});

test('long messages are capped at 220 chars', function () {
  const out = collectRunSignals([{ level: 'warn', message: 'x'.repeat(500) }]);
  assert.strictEqual(out[0].length, 221, '220 chars + ellipsis');
  assert.ok(out[0].endsWith('…'));
});

test('signal count is capped at 30', function () {
  const log = [];
  for (let i = 0; i < 50; i++) {
    // Distinct non-numeric tails so dedupe (numbers normalized) does not collapse them.
    const tag = String.fromCharCode(97 + (i % 26)) + String.fromCharCode(97 + Math.floor(i / 26));
    log.push({ level: 'warn', message: 'signal ' + tag });
  }
  const out = collectRunSignals(log);
  assert.strictEqual(out.length, 30, 'capped at 30 distinct signals');
});

console.log('\n' + passed + ' passed, ' + failed + ' failed');
if (failed > 0) process.exit(1);
