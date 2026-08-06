// error-core.test.js
// Unit tests for wwwroot/error-core.js (stack parser, error filter, dedupe).
// Dependency-free Node test runner:  node tests/js/error-core.test.js
'use strict';

const assert = require('assert');
const ErrorCore = require('../../wwwroot/error-core.js');

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

console.log('WeaverErrorCore tests\n');

// ── parseStack: Chrome ────────────────────────────────────────────────
test('Chrome: "at fn (agent.js:657:80)"', function () {
  const loc = ErrorCore.parseStack('ReferenceError: x\n    at vm.stopAgent (agent.js:657:80)\n    at fn (eval at compile (angular.js:16548:15), <anonymous>:4:232)');
  assert.deepStrictEqual(loc, { file: 'agent.js', line: '657', col: '80', full: 'agent.js' });
});

test('Chrome: nested path in parens', function () {
  const loc = ErrorCore.parseStack('TypeError: boom\n    at load (http://localhost:8080/wwwroot/ide.js:12:34)');
  assert.deepStrictEqual(loc, { file: 'ide.js', line: '12', col: '34', full: 'http://localhost:8080/wwwroot/ide.js' });
});

test('Chrome: backslash windows path', function () {
  const loc = ErrorCore.parseStack('Error: boom\n    at fn (C:\\Users\\Saint\\Desktop\\app.js:99:1)');
  assert.deepStrictEqual(loc, { file: 'app.js', line: '99', col: '1', full: 'C:\\Users\\Saint\\Desktop\\app.js' });
});

// ── parseStack: Firefox ───────────────────────────────────────────────
test('Firefox (legacy @-form): first app frame wins', function () {
  const loc = ErrorCore.parseStack('ReferenceError: x\nfn@http://localhost:8080/wwwroot/agent.js:657:80\nfn2@http://localhost:8080/wwwroot/other.js:1:1');
  assert.deepStrictEqual(loc, { file: 'agent.js', line: '657', col: '80', full: 'http://localhost:8080/wwwroot/agent.js' });
});

test('Firefox (legacy): "fn@file:///...:line:col"', function () {
  const loc = ErrorCore.parseStack('TypeError: boom\nstopAgent@file:///C:/Users/Saint/Desktop/Repos/Weaver/wwwroot/agent.js:657:80');
  assert.deepStrictEqual(loc, { file: 'agent.js', line: '657', col: '80', full: 'file:///C:/Users/Saint/Desktop/Repos/Weaver/wwwroot/agent.js' });
});

// ── parseStack: Safari ────────────────────────────────────────────────
test('Safari: "fn@http://...:line:col"', function () {
  const loc = ErrorCore.parseStack('ReferenceError: x\nvm.stopAgent@http://localhost:8080/wwwroot/agent.js:657:80');
  assert.deepStrictEqual(loc, { file: 'agent.js', line: '657', col: '80', full: 'http://localhost:8080/wwwroot/agent.js' });
});

// ── parseStack: edge cases ────────────────────────────────────────────
test('Bare "agent.js:657:80" (no function name)', function () {
  const loc = ErrorCore.parseStack('agent.js:657:80');
  assert.deepStrictEqual(loc, { file: 'agent.js', line: '657', col: '80', full: 'agent.js' });
});

test('Non-string / empty / null stack → null', function () {
  assert.strictEqual(ErrorCore.parseStack(null), null);
  assert.strictEqual(ErrorCore.parseStack(undefined), null);
  assert.strictEqual(ErrorCore.parseStack(''), null);
  assert.strictEqual(ErrorCore.parseStack(42), null);
});

test('Stack with no .js frame → null', function () {
  assert.strictEqual(ErrorCore.parseStack('Error: nope\n    at fn (<anonymous>:1:1)'), null);
});

// ── shouldFilter ──────────────────────────────────────────────────────
test('shouldFilter: null / non-object / empty message', function () {
  assert.strictEqual(ErrorCore.shouldFilter(null), true);
  assert.strictEqual(ErrorCore.shouldFilter(undefined), true);
  assert.strictEqual(ErrorCore.shouldFilter({}), true);
  assert.strictEqual(ErrorCore.shouldFilter({ message: '' }), true);
  assert.strictEqual(ErrorCore.shouldFilter({ message: 42 }), true);
});

test('shouldFilter: AbortError and cross-origin Script error', function () {
  assert.strictEqual(ErrorCore.shouldFilter({ name: 'AbortError', message: 'The operation was aborted.' }), true);
  assert.strictEqual(ErrorCore.shouldFilter({ name: 'Error', message: 'Script error.' }), true);
});

test('shouldFilter: real errors pass through', function () {
  assert.strictEqual(ErrorCore.shouldFilter({ name: 'ReferenceError', message: 'cancelAgentTimer is not defined' }), false);
  assert.strictEqual(ErrorCore.shouldFilter({ name: 'TypeError', message: 'x is not a function' }), false);
});

// ── makeErrorKey ──────────────────────────────────────────────────────
test('makeErrorKey: message + file:line when loc present', function () {
  assert.strictEqual(ErrorCore.makeErrorKey({ message: 'boom' }, { file: 'agent.js', line: '657' }), 'boom|agent.js:657');
});

test('makeErrorKey: message only when no loc', function () {
  assert.strictEqual(ErrorCore.makeErrorKey({ message: 'boom' }, null), 'boom|');
});

test('makeErrorKey: fallback "?" when message missing', function () {
  assert.strictEqual(ErrorCore.makeErrorKey({}, null), '?|');
});

// ── createDedupe: burst suppression ───────────────────────────────────
test('dedupe: first occurrence not a burst; repeat within window is', function () {
  const d = ErrorCore.createDedupe({ windowMs: 3000 });
  d.hit('k');
  assert.strictEqual(d.isBurst('k', 1000), false); // never recorded yet
  d.record('k', 1000);
  assert.strictEqual(d.isBurst('k', 1500), true);  // 500ms later → burst
  assert.strictEqual(d.isBurst('k', 2500), true);  // still inside window
});

test('dedupe: burst expires after windowMs', function () {
  const d = ErrorCore.createDedupe({ windowMs: 3000 });
  d.record('k', 1000);
  assert.strictEqual(d.isBurst('k', 3999), true);
  assert.strictEqual(d.isBurst('k', 4000), false); // exactly window → not burst
  assert.strictEqual(d.isBurst('k', 5000), false);
});

test('dedupe: keys are independent', function () {
  const d = ErrorCore.createDedupe({ windowMs: 3000 });
  d.record('a', 1000);
  assert.strictEqual(d.isBurst('a', 1500), true);
  assert.strictEqual(d.isBurst('b', 1500), false); // different key unaffected
});

test('dedupe: hit counts every occurrence, hitsOf reports', function () {
  const d = ErrorCore.createDedupe({ windowMs: 3000 });
  d.hit('k'); d.hit('k'); d.hit('k');
  assert.strictEqual(d.hitsOf('k'), 3);
  assert.strictEqual(d.hitsOf('missing'), 0);
});

test('dedupe: hit during burst window still counts', function () {
  const d = ErrorCore.createDedupe({ windowMs: 3000 });
  d.record('k', 1000);
  d.hit('k'); // burst-suppressed occurrence
  assert.strictEqual(d.hitsOf('k'), 1);
  d.hit('k');
  assert.strictEqual(d.hitsOf('k'), 2);
});

test('dedupe: maxKeys cap resets the map when over the cap', function () {
  const d = ErrorCore.createDedupe({ windowMs: 3000, maxKeys: 2 });
  d.record('a', 1000); // map {a}
  d.record('b', 1000); // map {a,b} — still ≤ cap
  d.record('c', 1000); // map {a,b,c} — still ≤ cap at add time
  assert.strictEqual(d.isBurst('a', 1500), true); // all still tracked
  d.record('d', 1000); // length 3 > cap 2 → map reset, then 'd' recorded
  assert.strictEqual(d.isBurst('a', 1500), false); // lost in the reset
  assert.strictEqual(d.isBurst('d', 1500), true);
});

test('dedupe: reset clears hits and lastSeen', function () {
  const d = ErrorCore.createDedupe({ windowMs: 3000 });
  d.record('k', 1000);
  d.hit('k');
  d.reset();
  assert.strictEqual(d.hitsOf('k'), 0);
  assert.strictEqual(d.isBurst('k', 1500), false);
});

test('dedupe: default window is 3000ms', function () {
  const d = ErrorCore.createDedupe();
  d.record('k', 1000);
  assert.strictEqual(d.isBurst('k', 1000 + 2999), true);
  assert.strictEqual(d.isBurst('k', 1000 + 3000), false);
});

console.log('\n' + passed + ' passed, ' + failed + ' failed');
process.exit(failed ? 1 : 0);
