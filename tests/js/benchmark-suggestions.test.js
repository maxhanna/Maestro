// benchmark-suggestions.test.js
// Benchmark cards must never get improvement suggestions attached — not when the run
// finishes, not from the idle Done-card loop, and not from "More like this". The runner
// creates cards with _benchmark: true, but a benchmark prompt pasted into a normal card
// in the "Weaver Benchmarks" project carries NO flag — its filePath IS the benchmark
// root. vm.isBenchmarkCard treats either as a benchmark card; every suggestion entry
// point (vm.suggestImprovements, the idle picker, both completion call sites), the
// kanban.html suggestions section, and the meeting gossip all gate on it.
// Dependency-free Node test runner:  node tests/js/benchmark-suggestions.test.js
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

// ── Extract vm.isBenchmarkCard from the live kanban.js ───────────────────
const kanbanSrc = fs.readFileSync(path.join(__dirname, '../../wwwroot/kanban.js'), 'utf8').replace(/\r\n/g, '\n');
const fnMatch = /vm\.isBenchmarkCard = function \(card\) \{[\s\S]*?\n      \}/.exec(kanbanSrc);
assert(fnMatch, 'vm.isBenchmarkCard not found in wwwroot/kanban.js — benchmark-card helper may have drifted');
function makeIsBenchmarkCard(vmState) {
  const vm = Object.assign({
    _benchmarkProjectPath: '',
    systemInfoCustom: {},
    defaultBenchmarkRoot: '',
    benchmarkEffectiveRoot: null
  }, vmState);
  eval('(function () { var vm2 = vm; ' + fnMatch[0].replace('vm.isBenchmarkCard', 'vm2.isBenchmarkCard') + '; vm.isBenchmarkCard = vm2.isBenchmarkCard; })()');
  return function (card) { return vm.isBenchmarkCard(card); };
}

// ── isBenchmarkCard behavior ─────────────────────────────────────────────
test('runner-created card (_benchmark: true) is a benchmark card', () => {
  const isBench = makeIsBenchmarkCard({});
  assert.strictEqual(isBench({ _benchmark: true, filePath: 'C:/anything' }), true);
});

test('card living in the benchmark project (no flag) is a benchmark card', () => {
  const root = 'C:/Users/Saint/Desktop/benchmark_sandbox';
  const isBench = makeIsBenchmarkCard({ defaultBenchmarkRoot: root });
  assert.strictEqual(isBench({ filePath: root }), true);
  // Backslash form + trailing separator still match (normalized).
  assert.strictEqual(isBench({ filePath: root.replace(/\//g, '\\') + '\\' }), true);
});

test('custom benchmark root from system info also matches', () => {
  const isBench = makeIsBenchmarkCard({ systemInfoCustom: { benchmarkProjectRoot: 'D:/bench-root' } });
  assert.strictEqual(isBench({ filePath: 'd:/bench-root' }), true);
});

test('benchmarkEffectiveRoot() is consulted when the other sources are empty', () => {
  const isBench = makeIsBenchmarkCard({ benchmarkEffectiveRoot: function () { return 'E:/custom-sandbox'; } });
  assert.strictEqual(isBench({ filePath: 'E:/custom-sandbox' }), true);
});

test('normal project card is NOT a benchmark card', () => {
  const isBench = makeIsBenchmarkCard({ defaultBenchmarkRoot: 'C:/sandbox' });
  assert.strictEqual(isBench({ filePath: 'C:/Users/someone/code/maxhanna' }), false);
  assert.strictEqual(isBench({}), false);
  assert.strictEqual(isBench(null), false);
  assert.strictEqual(isBench(undefined), false);
});

// ── Template / entry-point contract ──────────────────────────────────────
const agentSrc = fs.readFileSync(path.join(__dirname, '../../wwwroot/agent.js'), 'utf8').replace(/\r\n/g, '\n');
test('suggestImprovements refuses benchmark cards before anything else', () => {
  assert.ok(/if \(vm\.isBenchmarkCard && vm\.isBenchmarkCard\(card\)\) return false;/.test(agentSrc),
    'vm.suggestImprovements must bail out for benchmark cards');
});
test('idle Done-card picker excludes benchmark cards', () => {
  assert.ok(/if \(vm\.isBenchmarkCard && vm\.isBenchmarkCard\(c\)\) return;/.test(agentSrc),
    'the idle suggestion picker must skip benchmark cards');
});
test('both completion call sites gate on isBenchmarkCard', () => {
  const matches = agentSrc.match(/!\(vm\.isBenchmarkCard && vm\.isBenchmarkCard\((mvCard|card)\)\)/g) || [];
  assert.ok(matches.length >= 2, 'both post-run suggestImprovements call sites must gate benchmark cards');
});

const kanbanHtml = fs.readFileSync(path.join(__dirname, '../../wwwroot/kanban.html'), 'utf8').replace(/\r\n/g, '\n');
test('kanban.html hides the suggestions UI on benchmark cards', () => {
  const gates = kanbanHtml.match(/!vm\.isBenchmarkCard\(card\)/g) || [];
  assert.ok(gates.length >= 3,
    'generating/error/section suggestion UI must all be gated off for benchmark cards');
});

const meetingSrc = fs.readFileSync(path.join(__dirname, '../../wwwroot/meeting.js'), 'utf8').replace(/\r\n/g, '\n');
test('meeting gossip skips benchmark-card suggestions', () => {
  assert.ok(/if \(vm\.isBenchmarkCard && vm\.isBenchmarkCard\(card\)\) return;/.test(meetingSrc),
    'collectCardSuggestions must skip benchmark cards');
});

test('benchmark root is prefetched at startup so project matching works early', () => {
  assert.ok(/vm\.refreshBenchmarkRoot = function \(\) \{/.test(agentSrc),
    'agent.js must define vm.refreshBenchmarkRoot');
  const appSrc = fs.readFileSync(path.join(__dirname, '../../wwwroot/app.js'), 'utf8').replace(/\r\n/g, '\n');
  assert.ok(/if \(vm\.refreshBenchmarkRoot\) vm\.refreshBenchmarkRoot\(\);/.test(appSrc),
    'app.js must prefetch the benchmark root at startup');
});

// ── Summary ──────────────────────────────────────────────────────────────
console.log('\n' + passed + ' passed / ' + failed + ' failed');
if (failed > 0) process.exit(1);
