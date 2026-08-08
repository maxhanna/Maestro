// auto-pr-toggle.test.js
// Unit tests for wwwroot/kanban.js's applyAutoPrToggle helper — the pure logic behind the
// BRANCH checkbox. Turning the feature OFF must drop any prStatus the card picked up from a
// previous run, so the stale "PR: weaver/xxx" tag can't linger after a card is stopped,
// sent back to To Do, and un-branched. Enabling is a no-op (the next run creates the branch).
// Dependency-free Node test runner:  node tests/js/auto-pr-toggle.test.js
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
const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/kanban.js'), 'utf8');
const toggleMatch = /function applyAutoPrToggle\(card\) \{[\s\S]*?\n  \}/.exec(src);
assert(toggleMatch, 'applyAutoPrToggle not found in wwwroot/kanban.js — marker format may have drifted');

const applyAutoPrToggle = eval('(function applyAutoPrToggle(card) {' +
  toggleMatch[0].replace(/^function applyAutoPrToggle\(card\) \{/, '').replace(/\n  \}$/, '') + '})');

console.log('autoPr toggle helper tests\n');

// ── Turning BRANCH off clears stale branch state ───────────────────────────

test('unchecking BRANCH with a stale branch → prStatus cleared', function () {
  const card = { autoPr: false, prStatus: { status: 'branch-created', branch: 'weaver/TestCard1', originalBranch: 'master' } };
  assert.strictEqual(applyAutoPrToggle(card), true);
  assert.strictEqual(card.prStatus, undefined);
  assert.ok(!('prStatus' in card), 'prStatus key must be fully removed so it is not persisted');
});

test('unchecking BRANCH on a card with a created PR → prStatus cleared too', function () {
  const card = { autoPr: false, prStatus: { status: 'pr-created', branch: 'weaver/x', prUrl: 'https://github.com/o/r/pull/5' } };
  assert.strictEqual(applyAutoPrToggle(card), true);
  assert.ok(!('prStatus' in card));
});

// ── Enabling BRANCH is a no-op ─────────────────────────────────────────────

test('checking BRANCH on → existing prStatus kept (next run will refresh it)', function () {
  const card = { autoPr: true, prStatus: { status: 'branch-created', branch: 'weaver/x' } };
  assert.strictEqual(applyAutoPrToggle(card), false);
  assert.strictEqual(card.prStatus.branch, 'weaver/x');
});

test('checking BRANCH on a clean card → no-op', function () {
  const card = { autoPr: true };
  assert.strictEqual(applyAutoPrToggle(card), false);
  assert.strictEqual(card.prStatus, undefined);
});

// ── Edge cases ─────────────────────────────────────────────────────────────

test('unchecking BRANCH with no prStatus → no-op', function () {
  const card = { autoPr: false };
  assert.strictEqual(applyAutoPrToggle(card), false);
});

test('null card → no-op', function () {
  assert.strictEqual(applyAutoPrToggle(null), false);
});

console.log('\n' + passed + ' passed, ' + failed + ' failed');
if (failed > 0) process.exit(1);
