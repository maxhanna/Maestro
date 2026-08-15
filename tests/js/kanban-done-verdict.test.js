// kanban-done-verdict.test.js
// The Done / Done & Delete buttons used to be always green (Done) or gold/amber (Done &
// Delete for benchmark/cron) regardless of the verification verdict. Now they mirror the
// verification header above them: green when the card verified complete, yellow when it
// verified incomplete. vm.cardDoneVerdict returns 'ok' | 'fail' | null (null = no verdict
// on the card → the templates fall back to the type-based styling), and the templates +
// CSS wire the verdict classes (verdict-ok / verdict-fail) onto the buttons.
// Dependency-free Node test runner:  node tests/js/kanban-done-verdict.test.js
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

// ── Extract vm.cardDoneVerdict from the live kanban.js ────────────────────
const kanbanSrc = fs.readFileSync(path.join(__dirname, '../../wwwroot/kanban.js'), 'utf8').replace(/\r\n/g, '\n');
const fnMatch = /vm\.cardDoneVerdict = function \(card\) \{[\s\S]*?\n      \}/.exec(kanbanSrc);
assert(fnMatch, 'vm.cardDoneVerdict not found in wwwroot/kanban.js — verdict helper may have drifted');
const cardDoneVerdict = eval('(function () { var vm = {}; ' + fnMatch[0] + '; return vm.cardDoneVerdict; })()');

test('verified complete → ok (green)', () => {
  assert.strictEqual(cardDoneVerdict({ id: 1, _verification: { complete: true, reason: 'all good' } }), 'ok');
});

test('verified incomplete → fail (yellow)', () => {
  assert.strictEqual(cardDoneVerdict({ id: 1, _verification: { complete: false, reason: 'one step failed' } }), 'fail');
});

test('no verification on the card → null (fall back to type-based styling)', () => {
  assert.strictEqual(cardDoneVerdict({ id: 1 }), null);
  assert.strictEqual(cardDoneVerdict({ id: 1, _verification: undefined }), null);
  assert.strictEqual(cardDoneVerdict(null), null);
  assert.strictEqual(cardDoneVerdict(undefined), null);
});

test('verification present but complete missing/undefined → null', () => {
  assert.strictEqual(cardDoneVerdict({ id: 1, _verification: {} }), null);
  assert.strictEqual(cardDoneVerdict({ id: 1, _verification: { complete: undefined } }), null);
});

// ── Template contract: the buttons bind the verdict to verdict-ok/verdict-fail ──
const html = fs.readFileSync(path.join(__dirname, '../../wwwroot/kanban.html'), 'utf8').replace(/\r\n/g, '\n');
test('Done button turns verdict-fail when the card verified incomplete', () => {
  assert.ok(/vm\.cardDoneVerdict\(card\) === 'fail' \? 'verdict-fail' : 'success'/.test(html),
    'the ✔ Done button must swap success → verdict-fail when the verification verdict is fail');
});
test('Done & Delete button binds verdict-ok / verdict-fail and clears the type-based color', () => {
  assert.ok(/vm\.cardDoneVerdict\(card\) === 'fail' \? 'verdict-fail' : \(vm\.cardDoneVerdict\(card\) === 'ok' \? 'verdict-ok'/.test(html),
    'the Done & Delete button must use verdict-fail/verdict-ok classes for the verdict colors');
  assert.ok(/vm\.isCardActive\(card\.id\) \|\| vm\.cardDoneVerdict\(card\) \? '' : \(card\._fromCron \? '#fbbf24' : '#e5c07b'\)/.test(html),
    'the type-based inline color must be suppressed whenever a verdict exists');
});
test('kanban.css defines verdict-ok / verdict-fail button colors', () => {
  const css = fs.readFileSync(path.join(__dirname, '../../wwwroot/kanban.css'), 'utf8').replace(/\r\n/g, '\n');
  assert.ok(/button\.verdict-fail/.test(css), '.verdict-fail button style must exist');
  assert.ok(/button\.verdict-ok/.test(css), '.verdict-ok button style must exist');
});

console.log('\nkanban-done-verdict.test.js: ' + passed + ' passed / ' + failed + ' failed / ' + (passed + failed) + ' tests');
process.exit(failed > 0 ? 1 : 0);
