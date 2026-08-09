// files-edited-dedupe.test.js
// Unit tests for wwwroot/agent.js's dedupeFilesEdited — the pure dedupe behind the
// "files changed" chips. A file edited across multiple steps appears once per step, and
// the server's finish event can carry duplicates, which crashed the streaming panel's
// ng-repeat ('track by f.path'). The LAST edit per path wins (its preview is the final
// state); order is otherwise preserved.
// Dependency-free Node test runner:  node tests/js/files-edited-dedupe.test.js
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

// ── Extract the helper from the live source ─────────────────────────────────
const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/agent.js'), 'utf8');
const match = /function dedupeFilesEdited\(files\) \{[\s\S]*?\n        \}/.exec(src);
assert(match, 'dedupeFilesEdited not found in wwwroot/agent.js — marker format may have drifted');

const dedupeFilesEdited = eval('(function dedupeFilesEdited(files) {' +
  match[0].replace(/^function dedupeFilesEdited\(files\) \{/, '').replace(/\n        \}$/, '') + '})');

// ── Tests ───────────────────────────────────────────────────────────────────

test('duplicate paths collapse to one entry', () => {
  const files = [
    { path: 'a/b.css', action: 'modified', preview: 'first' },
    { path: 'a/b.css', action: 'modified', preview: 'second' }
  ];
  const out = dedupeFilesEdited(files);
  assert.strictEqual(out.length, 1);
  assert.strictEqual(out[0].preview, 'second'); // last edit wins
});

test('order preserved for distinct paths', () => {
  const files = [
    { path: 'a.css', preview: 'a' },
    { path: 'b.ts', preview: 'b' },
    { path: 'c.html', preview: 'c' }
  ];
  const out = dedupeFilesEdited(files);
  assert.deepStrictEqual(out.map(f => f.path), ['a.css', 'b.ts', 'c.html']);
});

test('path matching is case-insensitive and slash-normalized', () => {
  const files = [
    { path: 'maxhanna.client\\Src\\App\\X.ts', preview: 'backslash-upper' },
    { path: 'maxhanna.client/src/app/x.ts', preview: 'forward-lower' }
  ];
  const out = dedupeFilesEdited(files);
  assert.strictEqual(out.length, 1);
  assert.strictEqual(out[0].preview, 'forward-lower');
});

test('entries without a path are dropped', () => {
  const files = [{ preview: 'no path' }, { path: 'ok.ts', preview: 'keep' }];
  const out = dedupeFilesEdited(files);
  assert.strictEqual(out.length, 1);
  assert.strictEqual(out[0].path, 'ok.ts');
});

test('non-array input returns empty array', () => {
  assert.deepStrictEqual(dedupeFilesEdited(null), []);
  assert.deepStrictEqual(dedupeFilesEdited('nope'), []);
  assert.deepStrictEqual(dedupeFilesEdited(undefined), []);
});

test('empty array stays empty', () => {
  assert.deepStrictEqual(dedupeFilesEdited([]), []);
});

// ── Summary ─────────────────────────────────────────────────────────────────
console.log(`\n# ${passed} passed, ${failed} failed`);
process.exitCode = failed ? 1 : 0;
