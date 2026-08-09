// pr-finish-guard.test.js
// Unit tests for wwwroot/agent.js's applyPrFinishOutcome helper — the pure guard on the
// pr/finish completion path. If BRANCH was toggled off while the card was running, a
// pr/finish response that lands afterwards must NOT re-record any PR state: the card's
// in-flight prStatus is cleared instead, so no stale "PR: weaver/xxx" tag can come back.
// Dependency-free Node test runner:  node tests/js/pr-finish-guard.test.js
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
const match = /function applyPrFinishOutcome\(card, prResp, err\) \{[\s\S]*?\n        \}/.exec(src);
assert(match, 'applyPrFinishOutcome not found in wwwroot/agent.js — marker format may have drifted');

const applyPrFinishOutcome = eval('(function applyPrFinishOutcome(card, prResp, err) {' +
  match[0].replace(/^function applyPrFinishOutcome\(card, prResp, err\) \{/, '').replace(/\n        \}$/, '') + '})');

console.log('pr/finish completion guard tests\n');

// ── BRANCH toggled off while running — the finish response must not stick ──

test('finish response landing after BRANCH off → prStatus cleared, outcome skipped', function () {
  const card = { autoPr: false, prStatus: { status: 'creating-pr', branch: 'weaver/TestCard1', originalBranch: 'master' } };
  const prResp = { data: { success: true, prUrl: 'https://github.com/o/r/pull/9' } };
  assert.strictEqual(applyPrFinishOutcome(card, prResp, null), 'skipped');
  assert.strictEqual(card.prStatus, undefined);
  assert.ok(!('prStatus' in card), 'prStatus key must be fully removed so it is not persisted');
});

test('finish error response landing after BRANCH off → prStatus cleared too', function () {
  const card = { autoPr: false, prStatus: { status: 'creating-pr', branch: 'weaver/x', originalBranch: 'master' } };
  assert.strictEqual(applyPrFinishOutcome(card, { data: { success: false, error: 'push failed' } }, null), 'skipped');
  assert.ok(!('prStatus' in card));
});

test('finish HTTP error landing after BRANCH off → prStatus cleared too', function () {
  const card = { autoPr: false, prStatus: { status: 'creating-pr', branch: 'weaver/x', originalBranch: 'master' } };
  assert.strictEqual(applyPrFinishOutcome(card, null, { statusText: 'Server Error' }), 'skipped');
  assert.ok(!('prStatus' in card));
});

// ── BRANCH still on — normal outcomes unchanged ────────────────────────────

test('successful finish → pr-created with prUrl', function () {
  const card = { autoPr: true, prStatus: { status: 'creating-pr', branch: 'weaver/TestCard1', originalBranch: 'master' } };
  const prResp = { data: { success: true, prUrl: 'https://github.com/o/r/pull/9' } };
  assert.strictEqual(applyPrFinishOutcome(card, prResp, null), 'pr-created');
  assert.strictEqual(card.prStatus.status, 'pr-created');
  assert.strictEqual(card.prStatus.branch, 'weaver/TestCard1');
  assert.strictEqual(card.prStatus.prUrl, 'https://github.com/o/r/pull/9');
});

test('successful finish with no prUrl → prUrl null (client shows fallback)', function () {
  const card = { autoPr: true, prStatus: { status: 'creating-pr', branch: 'weaver/x', originalBranch: 'master' } };
  assert.strictEqual(applyPrFinishOutcome(card, { data: { success: true } }, null), 'pr-created');
  assert.strictEqual(card.prStatus.prUrl, undefined);
});

test('failed finish → error with server message', function () {
  const card = { autoPr: true, prStatus: { status: 'creating-pr', branch: 'weaver/x', originalBranch: 'master' } };
  const prResp = { data: { success: false, error: 'push rejected' } };
  assert.strictEqual(applyPrFinishOutcome(card, prResp, null), 'error');
  assert.strictEqual(card.prStatus.status, 'error');
  assert.strictEqual(card.prStatus.error, 'push rejected');
  assert.strictEqual(card.prStatus.branch, 'weaver/x');
});

test('failed finish with no server message → default error text', function () {
  const card = { autoPr: true, prStatus: { status: 'creating-pr', branch: 'weaver/x', originalBranch: 'master' } };
  assert.strictEqual(applyPrFinishOutcome(card, { data: { success: false } }, null), 'error');
  assert.strictEqual(card.prStatus.error, 'PR creation failed');
});

test('HTTP error while BRANCH on → error with statusText', function () {
  const card = { autoPr: true, prStatus: { status: 'creating-pr', branch: 'weaver/x', originalBranch: 'master' } };
  assert.strictEqual(applyPrFinishOutcome(card, null, { statusText: 'Server Error' }), 'error');
  assert.strictEqual(card.prStatus.status, 'error');
  assert.strictEqual(card.prStatus.error, 'Server Error');
});

// ── Edge cases ─────────────────────────────────────────────────────────────

test('no branch state at all → noop, nothing touched', function () {
  const card = { autoPr: true };
  assert.strictEqual(applyPrFinishOutcome(card, { data: { success: true } }, null), 'noop');
  assert.strictEqual(card.prStatus, undefined);
});

test('null card → noop', function () {
  assert.strictEqual(applyPrFinishOutcome(null, { data: { success: true } }, null), 'noop');
});

console.log('\n' + passed + ' passed, ' + failed + ' failed');
if (failed > 0) process.exit(1);
