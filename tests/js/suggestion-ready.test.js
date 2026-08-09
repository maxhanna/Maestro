// suggestion-ready.test.js
// Unit tests for wwwroot/agent.js's suggestion-card auto-ready helpers — the pure logic
// behind clicking a suggestion: the new card comes in "readied", starts immediately when
// the board is idle, and is marked _autoQueued when a card is already running so the queue
// drain starts it the moment the current card finishes. Also covers autoQueueEligible, the
// gate the queue drain uses to decide which ready cards may auto-start (endpoint-parked and
// suggestion-auto cards drain even with the global autoQueue toggle off).
// Dependency-free Node test runner:  node tests/js/suggestion-ready.test.js
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

// ── Extract the helpers from the live source ────────────────────────────────
const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/agent.js'), 'utf8');
const readyMatch = /function prepareSuggestionCardForAutoRun\(card, streamingActive\) \{[\s\S]*?\n        \}/.exec(src);
const gateMatch = /function autoQueueEligible\(card, autoQueue, selfImprovingArmed\) \{[\s\S]*?\n        \}/.exec(src);
const capMatch = /function drainBlockedBySuggestionCap\(card, startedSuggestionCard\) \{[\s\S]*?\n        \}/.exec(src);
assert(readyMatch, 'prepareSuggestionCardForAutoRun not found in wwwroot/agent.js — marker format may have drifted');
assert(gateMatch, 'autoQueueEligible not found in wwwroot/agent.js — marker format may have drifted');
assert(capMatch, 'drainBlockedBySuggestionCap not found in wwwroot/agent.js — marker format may have drifted');

const prepareSuggestionCardForAutoRun = eval('(function prepareSuggestionCardForAutoRun(card, streamingActive) {' +
  readyMatch[0].replace(/^function prepareSuggestionCardForAutoRun\(card, streamingActive\) \{/, '').replace(/\n        \}$/, '') + '})');
const autoQueueEligible = eval('(function autoQueueEligible(card, autoQueue, selfImprovingArmed) {' +
  gateMatch[0].replace(/^function autoQueueEligible\(card, autoQueue, selfImprovingArmed\) \{/, '').replace(/\n        \}$/, '') + '})');
const drainBlockedBySuggestionCap = eval('(function drainBlockedBySuggestionCap(card, startedSuggestionCard) {' +
  capMatch[0].replace(/^function drainBlockedBySuggestionCap\(card, startedSuggestionCard\) \{/, '').replace(/\n        \}$/, '') + '})');

// ── prepareSuggestionCardForAutoRun ─────────────────────────────────────────

test('board idle → card readied, no queue flag, caller starts it now', () => {
  const card = {};
  const queued = prepareSuggestionCardForAutoRun(card, false);
  assert.strictEqual(queued, false);
  assert.strictEqual(card.ready, true);
  assert.strictEqual(card._autoQueued, undefined);
});

test('card running → card readied AND _autoQueued, caller leaves it for the queue', () => {
  const card = {};
  const queued = prepareSuggestionCardForAutoRun(card, true);
  assert.strictEqual(queued, true);
  assert.strictEqual(card.ready, true);
  assert.strictEqual(card._autoQueued, true);
});

test('idle card clears a stale _autoQueued flag', () => {
  const card = { _autoQueued: true };
  prepareSuggestionCardForAutoRun(card, false);
  assert.strictEqual(card._autoQueued, undefined);
  assert.strictEqual(card.ready, true);
});

test('null card → treated as queued (safe no-op)', () => {
  assert.strictEqual(prepareSuggestionCardForAutoRun(null, false), true);
});

// ── autoQueueEligible (the processQueuedCards drain gate) ───────────────────

test('endpoint-parked card always drains', () => {
  assert.strictEqual(autoQueueEligible({ _endpointQueued: true }, false, false), true);
});

test('suggestion-auto card drains even with autoQueue off', () => {
  assert.strictEqual(autoQueueEligible({ _autoQueued: true }, false, false), true);
});

test('autoQueue toggle on drains any ready card', () => {
  assert.strictEqual(autoQueueEligible({}, true, false), true);
});

test('armed self-improving card drains', () => {
  assert.strictEqual(autoQueueEligible({ selfImproving: true }, false, true), true);
});

test('plain ready card with autoQueue off does NOT drain', () => {
  assert.strictEqual(autoQueueEligible({}, false, false), false);
});

test('null card never drains', () => {
  assert.strictEqual(autoQueueEligible(null, true, false), false);
});

// ── drainBlockedBySuggestionCap (one queued suggestion per drain) ────────────

test('first suggestion card of the drain is not blocked', () => {
  assert.strictEqual(drainBlockedBySuggestionCap({ _autoQueued: true }, false), false);
});

test('second suggestion card of the drain is blocked', () => {
  assert.strictEqual(drainBlockedBySuggestionCap({ _autoQueued: true }, true), true);
});

test('non-suggestion cards are never blocked, even after one started', () => {
  assert.strictEqual(drainBlockedBySuggestionCap({}, true), false);
  assert.strictEqual(drainBlockedBySuggestionCap({ _endpointQueued: true }, true), false);
  assert.strictEqual(drainBlockedBySuggestionCap({ selfImproving: true }, true), false);
});

test('null card is never blocked', () => {
  assert.strictEqual(drainBlockedBySuggestionCap(null, true), false);
});

// ── Summary ─────────────────────────────────────────────────────────────────
console.log(`\n# ${passed} passed, ${failed} failed`);
process.exitCode = failed ? 1 : 0;
