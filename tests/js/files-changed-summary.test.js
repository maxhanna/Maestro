// files-changed-summary.test.js
// At the end of a card's execution the AI Analysis section shows a git-style
// "files changed" summary: a header with the file count + total +/- line stats, and
// one row per file (status letter A/M/D/R, path, per-file counts) with an expandable
// diff preview. vm.fileActionLetter maps the edit action to the status letter and
// vm.filesEditedTotals sums the +/- counts for the header badge. Entries arrive with
// either `action` (server-side ExtractFilesEdited) or `editAction` (client-side live
// accumulation) — both are honored.
// Dependency-free Node test runner:  node tests/js/files-changed-summary.test.js
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

// ── Extract the helpers from the live kanban.js ──────────────────────────
const kanbanSrc = fs.readFileSync(path.join(__dirname, '../../wwwroot/kanban.js'), 'utf8').replace(/\r\n/g, '\n');
const letterMatch = /vm\.fileActionLetter = function \(f\) \{[\s\S]*?\n      \}/.exec(kanbanSrc);
assert(letterMatch, 'vm.fileActionLetter not found in wwwroot/kanban.js — summary helper may have drifted');
const fileActionLetter = eval('(function () { var vm = {}; ' + letterMatch[0] + '; return vm.fileActionLetter; })()');

const totalsMatch = /vm\.filesEditedTotals = function \(files\) \{[\s\S]*?\n      \}/.exec(kanbanSrc);
assert(totalsMatch, 'vm.filesEditedTotals not found in wwwroot/kanban.js — summary helper may have drifted');
const filesEditedTotals = eval('(function () { var vm = {}; ' + totalsMatch[0] + '; return vm.filesEditedTotals; })()');

// ── fileActionLetter ─────────────────────────────────────────────────────
test('defaults to M (modified)', () => {
  assert.strictEqual(fileActionLetter({}), 'M');
  assert.strictEqual(fileActionLetter({ action: 'modified' }), 'M');
  assert.strictEqual(fileActionLetter({ editAction: 'modified' }), 'M');
  assert.strictEqual(fileActionLetter(null), 'M');
  assert.strictEqual(fileActionLetter(undefined), 'M');
});

test('created → A', () => {
  assert.strictEqual(fileActionLetter({ action: 'created' }), 'A');
  assert.strictEqual(fileActionLetter({ editAction: 'created' }), 'A');
});

test('deleted → D', () => {
  assert.strictEqual(fileActionLetter({ action: 'deleted' }), 'D');
  assert.strictEqual(fileActionLetter({ editAction: 'deleted' }), 'D');
});

test('renamed → R (action or arrow form)', () => {
  assert.strictEqual(fileActionLetter({ action: 'renamed' }), 'R');
  assert.strictEqual(fileActionLetter({ editAction: 'renamed → old.ts' }), 'R');
});

test('action wins over editAction when both present', () => {
  assert.strictEqual(fileActionLetter({ action: 'created', editAction: 'modified' }), 'A');
});

// ── filesEditedTotals ────────────────────────────────────────────────────
test('sums added/removed across entries, missing counts count as 0', () => {
  const files = [
    { path: 'a.html', linesAdded: 1, linesRemoved: 0 },
    { path: 'b.css', linesAdded: 2, linesRemoved: 5 },
    { path: 'c.ts', linesAdded: 4 }
  ];
  const t = filesEditedTotals(files);
  assert.strictEqual(t.added, 7);
  assert.strictEqual(t.removed, 5);
});

test('handles string counts, empty list, and non-list input', () => {
  assert.deepStrictEqual(filesEditedTotals([{ linesAdded: '3', linesRemoved: '1' }]), { added: 3, removed: 1 });
  assert.deepStrictEqual(filesEditedTotals([]), { added: 0, removed: 0 });
  assert.deepStrictEqual(filesEditedTotals(null), { added: 0, removed: 0 });
  assert.deepStrictEqual(filesEditedTotals(undefined), { added: 0, removed: 0 });
});

// ── Template contract ────────────────────────────────────────────────────
const kanbanHtml = fs.readFileSync(path.join(__dirname, '../../wwwroot/kanban.html'), 'utf8').replace(/\r\n/g, '\n');
test('AI Analysis sections render the git-style header with file count and totals', () => {
  const matches = kanbanHtml.match(/Agent changed \{\{card\.agentAnalysis\.filesEdited\.length\}\}/g) || [];
  assert.ok(matches.length >= 1, 'the "Agent changed N file(s)" header must be rendered');
  assert.ok(kanbanHtml.indexOf('vm.filesEditedTotals(card.agentAnalysis.filesEdited)') !== -1,
    'the header totals must call vm.filesEditedTotals');
});
test('each file row shows the status letter, path, per-file counts, and an expandable preview', () => {
  assert.ok(kanbanHtml.indexOf('vm.fileActionLetter(f)') !== -1, 'rows must bind vm.fileActionLetter(f)');
  assert.ok(kanbanHtml.indexOf('class="diff-file-preview"') !== -1, 'a diff preview <pre> must exist');
  assert.ok(kanbanHtml.indexOf('ng-if="f.preview"') !== -1, 'the expander must only render when a preview exists');
});

const indexHtml = fs.readFileSync(path.join(__dirname, '../../wwwroot/index.html'), 'utf8').replace(/\r\n/g, '\n');
test('live agent panel files-changed list uses the same summary helpers', () => {
  assert.ok(indexHtml.indexOf('vm.filesEditedTotals(vm.streamingFilesEdited)') !== -1,
    'the live panel must show total +/- stats via vm.filesEditedTotals');
  assert.ok(indexHtml.indexOf('vm.fileActionLetter(f)') !== -1,
    'the live panel rows must bind vm.fileActionLetter(f)');
});
test('live panel file rows are clickable to show the diff inline', () => {
  assert.ok(indexHtml.indexOf('<details class="diff-file-details" ng-if="f.preview">') !== -1,
    'rows with a diff must be expandable details');
  assert.ok(indexHtml.indexOf('<pre class="diff-file-preview">{{f.preview}}</pre>') !== -1,
    'expanding a row must reveal the inline diff preview');
});

const agentSrc2 = fs.readFileSync(path.join(__dirname, '../../wwwroot/agent.js'), 'utf8').replace(/\r\n/g, '\n');
test('live file entries carry the step diff so clicking works', () => {
  assert.ok(/preview: s\.diffPreview/.test(agentSrc2),
    'refreshFilesEditedFromSteps must copy diffPreview from the edit step');
});

// ── Summary ──────────────────────────────────────────────────────────────
console.log('\n' + passed + ' passed / ' + failed + ' failed');
if (failed > 0) process.exit(1);
