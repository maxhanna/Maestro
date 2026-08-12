// run-all.js — the JS client-logic test aggregator.
//
// Each tests/js/*.test.js is a self-contained Node script with its own mini-runner
// (test(name, fn) + a non-zero exit on failure). This aggregator spawns every one of
// them as a child process and reports a combined pass/fail count, so the whole JS
// suite can be run with a single command instead of a hand-rolled shell loop that
// nobody remembers to run:
//
//     node tests/js/run-all.js
//
// Exit code 0 = all files green, 1 = at least one file had failing tests, 2 = a
// runner crashed or produced no parsable summary. Pass/fail counts come from the
// per-test result lines each runner prints ('  ✓ name' / '  ✗ name'), which are
// uniform across every file (the trailing summary lines are not).
'use strict';

const { spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const dir = __dirname;
const files = fs.readdirSync(dir)
  .filter(f => /\.test\.js$/.test(f))
  .sort();

if (files.length === 0) {
  console.error('run-all: no tests/js/*.test.js files found');
  process.exit(2);
}

let totalTests = 0;
let totalPassed = 0;
let totalFailed = 0;
let crashed = 0;

console.log('── JS client-logic suite ──────────────────────────────');
for (const file of files) {
  const res = spawnSync(process.execPath, [path.join(dir, file)], {
    encoding: 'utf8',
    timeout: 120000,
  });
  const ok = res.status === 0 && !res.error;

  // Count the per-test result lines the runner printed ('  ✓ ' / '  ✗ ' prefixes).
  const out = (res.stdout || '') + '\n' + (res.stderr || '');
  const passLines = (out.match(/^  ✓ /gm) || []).length;
  const failLines = (out.match(/^  ✗ /gm) || []).length;
  if (passLines + failLines > 0) {
    totalPassed += passLines;
    totalFailed += failLines;
    totalTests += passLines + failLines;
  } else {
    crashed++;
  }

  const status = ok ? '✓' : '✗';
  const detail = `${passLines} passed / ${failLines} failed` + (passLines + failLines === 0 ? ` (no test lines; ${res.error ? res.error.code : 'exit ' + res.status})` : '');
  console.log(`  ${status} ${file} — ${detail}`);

  if (!ok) {
    // Surface the failing file's output so the failure is actionable without
    // re-running the file by hand.
    const trimmed = out.trim();
    if (trimmed) {
      console.log(trimmed.split('\n').map(l => '      ' + l).join('\n'));
    }
  }
}
console.log('───────────────────────────────────────────────────────');

const totals = totalTests > 0
  ? `${totalPassed} passed / ${totalFailed} failed / ${totalTests} tests across ${files.length} files`
  : `${files.length} files, no test summaries found`;
console.log(`JS suite: ${totals}${crashed ? ` (${crashed} crashed, no summary)` : ''}`);

if (totalFailed > 0 || crashed > 0) process.exit(1);
process.exit(0);
