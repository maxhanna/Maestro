// context-sse.test.js
// Unit tests for wwwroot/agent.js's SSE `case 'context':` handler — the live context
// counter that updates vm.streamingContextSize / streamingContextChars /
// streamingContextBreakdown DURING orchestration without touching the phase or the
// log, and (on the final run-end event) persists the PEAK size onto the card as
// card._context via the same _field + saveCards pattern as _groundTruth/_verification
// so completed cards keep showing it after the live section closes.
// The handler body is extracted from the live source and eval'd with a mock `vm`,
// mirroring the steer-now test's approach.
// Dependency-free Node test runner:  node tests/js/context-sse.test.js
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

// ── Extract the handler body from the live source ───────────────────────────
const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/agent.js'), 'utf8');
const match = /case 'context':([\s\S]*?)\n\s*case 'groundTruth':/.exec(src);
assert(match, "case 'context': handler not found in wwwroot/agent.js — marker format may have drifted");

// Strip the comment lines, blank lines, and the switch's trailing `break;`; the
// remainder is the handler's pure statements (guarded `if (parsed && ...)` blocks)
// referencing `parsed` and `vm`.
const statements = match[1]
  .split('\n')
  .map(l => l.trim())
  .filter(l => l && !l.startsWith('//') && l !== 'break;')
  .join('\n');

// eslint-disable-next-line no-new-func
const handleContext = new Function('parsed', 'vm', statements);

function makeVm({ activeCardId = null, card = null } = {}) {
  const saveCalls = [];
  const vm = {
    streamingContextSize: 0,
    streamingContextChars: 0,
    streamingContextBreakdown: [],
    streamingPhase: 'phase-doing',
    activeCardId,
    findCardById: (id) => (id === activeCardId ? card : null),
    saveCards: () => saveCalls.push('save'),
  };
  return { vm, saveCalls };
}

// ── Live counter updates (no phase/log side effects) ────────────────────────

test('contextSize/contextChars/breakdown update the live counter', () => {
  const { vm } = makeVm();
  handleContext({ contextSize: 1234, contextChars: 4321, contextBreakdown: [{ name: 'skeleton', tokens: 10 }] }, vm);
  assert.strictEqual(vm.streamingContextSize, 1234);
  assert.strictEqual(vm.streamingContextChars, 4321);
  assert.deepStrictEqual(vm.streamingContextBreakdown, [{ name: 'skeleton', tokens: 10 }]);
});

test('phase is never touched by a context event', () => {
  const { vm } = makeVm();
  handleContext({ contextSize: 99 }, vm);
  assert.strictEqual(vm.streamingPhase, 'phase-doing');
});

test('partial events leave untouched fields as-is', () => {
  const { vm } = makeVm();
  handleContext({ contextChars: 50 }, vm);
  assert.strictEqual(vm.streamingContextChars, 50);
  assert.strictEqual(vm.streamingContextSize, 0);
  assert.deepStrictEqual(vm.streamingContextBreakdown, []);
});

test('non-array breakdown is ignored (never clobbers)', () => {
  const { vm } = makeVm();
  vm.streamingContextBreakdown = [{ name: 'old', tokens: 1 }];
  handleContext({ contextSize: 5, contextBreakdown: 'nope' }, vm);
  assert.deepStrictEqual(vm.streamingContextBreakdown, [{ name: 'old', tokens: 1 }]);
});

test('empty/undefined event is a no-op', () => {
  const { vm } = makeVm();
  handleContext({}, vm);
  assert.strictEqual(vm.streamingContextSize, 0);
  assert.strictEqual(vm.streamingContextChars, 0);
});

// ── Final run-end persistence (card._context + saveCards) ───────────────────

test('final=true persists the peak onto the card and saves', () => {
  const card = { id: 'card-1' };
  const { vm, saveCalls } = makeVm({ activeCardId: 'card-1', card });
  vm.streamingContextSize = 2048;
  vm.streamingContextBreakdown = [{ name: 'skeleton', tokens: 50 }];
  handleContext({ contextSize: 2048, contextChars: 9000, contextBreakdown: [{ name: 'skeleton', tokens: 50 }], final: true }, vm);
  assert.deepStrictEqual(card._context, { size: 2048, chars: 9000, breakdown: [{ name: 'skeleton', tokens: 50 }] });
  assert.deepStrictEqual(saveCalls, ['save']);
});

test('final=true with no matching card is a silent no-op', () => {
  const { vm, saveCalls } = makeVm({ activeCardId: 'card-9', card: null });
  handleContext({ contextSize: 10, final: true }, vm);
  assert.strictEqual(saveCalls.length, 0);
});

test('final=false never persists', () => {
  const card = { id: 'card-1' };
  const { vm, saveCalls } = makeVm({ activeCardId: 'card-1', card });
  handleContext({ contextSize: 777, final: false }, vm);
  assert.strictEqual(card._context, undefined);
  assert.strictEqual(saveCalls.length, 0);
});

test('missing findCardById helper never crashes the handler', () => {
  const vm = {
    streamingContextSize: 0,
    streamingContextChars: 0,
    streamingContextBreakdown: [],
    streamingPhase: 'phase-doing',
    activeCardId: 'card-1',
  };
  handleContext({ contextSize: 5, final: true }, vm);
  assert.strictEqual(vm.streamingContextSize, 5);
});

// ── Summary ─────────────────────────────────────────────────────────────────
console.log(`\n# ${passed} passed, ${failed} failed`);
process.exitCode = failed ? 1 : 0;
