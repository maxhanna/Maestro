// suggestion-cancel.test.js
// Unit tests for wwwroot/agent.js's suggestion start/cancel/invalidate helpers — the pure
// logic behind vm.suggestImprovements's entry guard, vm.cancelCardSuggestions's abort path,
// and vm.invalidateCardSuggestions's stale-suggestion drop (text edits). All helpers live
// inside the Angular AgentMixin factory closure, so we extract their source text and eval
// it, mirroring the meeting-ticker test's approach.
// Dependency-free Node test runner:  node tests/js/suggestion-cancel.test.js
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
const startMatch = /function shouldStartSuggestions\(card, maxSuggestions, topup, refresh\) \{[\s\S]*?\n        \}/.exec(src);
const abortMatch = /function abortSuggestionGeneration\(card\) \{[\s\S]*?\n        \}/.exec(src);
const staleMatch = /function clearStaleSuggestions\(card\) \{[\s\S]*?\n        \}/.exec(src);
const busyMatch = /function suggestionSystemBlocked\(state\) \{[\s\S]*?\n        \}/.exec(src);
const doneMatch = /vm\._doneCardsNeedingSuggestions = function \(\) \{[\s\S]*?\n                \};/.exec(src);assert(startMatch, 'shouldStartSuggestions not found in wwwroot/agent.js — marker format may have drifted');
assert(abortMatch, 'abortSuggestionGeneration not found in wwwroot/agent.js — marker format may have drifted');
assert(staleMatch, 'clearStaleSuggestions not found in wwwroot/agent.js — marker format may have drifted');
assert(busyMatch, 'suggestionSystemBlocked not found in wwwroot/agent.js — marker format may have drifted');
assert(doneMatch, '_doneCardsNeedingSuggestions not found in wwwroot/agent.js — marker format may have drifted');

const shouldStartSuggestions = eval('(function shouldStartSuggestions(card, maxSuggestions, topup, refresh) {' +
  startMatch[0].replace(/^function shouldStartSuggestions\(card, maxSuggestions, topup, refresh\) \{/, '').replace(/\n        \}$/, '') + '})');
const abortSuggestionGeneration = eval('(function abortSuggestionGeneration(card) {' +
  abortMatch[0].replace(/^function abortSuggestionGeneration\(card\) \{/, '').replace(/\n        \}$/, '') + '})');
const clearStaleSuggestions = eval('(function clearStaleSuggestions(card) {' +
  staleMatch[0].replace(/^function clearStaleSuggestions\(card\) \{/, '').replace(/\n        \}$/, '') + '})');
const suggestionSystemBlocked = eval('(function suggestionSystemBlocked(state) {' +
  busyMatch[0].replace(/^function suggestionSystemBlocked\(state\) \{/, '').replace(/\n        \}$/, '') + '})');
const doneCardsNeedingSuggestions = eval('(function (vm) {' +
  doneMatch[0].replace(/^vm\._doneCardsNeedingSuggestions = function \(\) \{/, '').replace(/\n                \};$/, '') + '})');

console.log('suggestion start/cancel helper tests\n');

// ── shouldStartSuggestions: fresh-run guard ────────────────────────────────
test('fresh card with cap 3 → starts', function () {
  assert.strictEqual(shouldStartSuggestions({}, 3, false), true);
});

test('card that already has suggestions (fresh run) → does not start', function () {
  assert.strictEqual(shouldStartSuggestions({ _suggestions: [{ text: 'x' }] }, 3, false), false);
});

test('card already requested → does not start', function () {
  assert.strictEqual(shouldStartSuggestions({ _suggestionsRequested: true }, 3, false), false);
});

test('no card → does not start', function () {
  assert.strictEqual(shouldStartSuggestions(null, 3, false), false);
});

test('cap 0 → does not start', function () {
  assert.strictEqual(shouldStartSuggestions({}, 0, false), false);
});

test('negative cap → does not start', function () {
  assert.strictEqual(shouldStartSuggestions({}, -1, false), false);
});

test('benchmark card → does not start (fresh run)', function () {
  assert.strictEqual(shouldStartSuggestions({ _benchmark: true }, 3, false), false);
});

test('benchmark card → does not start (topup)', function () {
  assert.strictEqual(shouldStartSuggestions({ _benchmark: true, _suggestions: [{ text: 'a' }] }, 3, true), false);
});

test('non-benchmark card (_benchmark: false) → starts normally', function () {
  assert.strictEqual(shouldStartSuggestions({ _benchmark: false }, 3, false), true);
});

// ── shouldStartSuggestions: topup (More like this) guard ───────────────────
test('topup with existing suggestions below cap → starts', function () {
  assert.strictEqual(shouldStartSuggestions({ _suggestions: [{ text: 'a' }, { text: 'b' }] }, 3, true), true);
});

test('topup at cap → does not start', function () {
  assert.strictEqual(shouldStartSuggestions({ _suggestions: [{ text: 'a' }, { text: 'b' }, { text: 'c' }] }, 3, true), false);
});

test('topup with no suggestions array → does not start', function () {
  assert.strictEqual(shouldStartSuggestions({}, 3, true), false);
});

test('topup while already generating → does not start', function () {
  assert.strictEqual(shouldStartSuggestions({ _suggestions: [{ text: 'a' }], _suggestionsGenerating: true }, 3, true), false);
});

// ── shouldStartSuggestions: refresh (↻ Refresh) guard ──────────────────────
test('refresh with existing suggestions → starts (regenerates from scratch)', function () {
  assert.strictEqual(shouldStartSuggestions({ _suggestions: [{ text: 'a' }, { text: 'b' }] }, 3, false, true), true);
});

test('refresh at cap → still starts (cap applies to the new set, not the old)', function () {
  assert.strictEqual(shouldStartSuggestions({ _suggestions: [{ text: 'a' }, { text: 'b' }, { text: 'c' }] }, 3, false, true), true);
});

test('refresh with no suggestions → starts', function () {
  assert.strictEqual(shouldStartSuggestions({}, 3, false, true), true);
});

test('refresh while already generating → does not start', function () {
  assert.strictEqual(shouldStartSuggestions({ _suggestions: [{ text: 'a' }], _suggestionsGenerating: true }, 3, false, true), false);
});

test('refresh on benchmark card → does not start', function () {
  assert.strictEqual(shouldStartSuggestions({ _benchmark: true, _suggestions: [{ text: 'a' }] }, 3, false, true), false);
});

test('refresh with cap 0 → does not start', function () {
  assert.strictEqual(shouldStartSuggestions({ _suggestions: [{ text: 'a' }] }, 0, false, true), false);
});

// ── abortSuggestionGeneration: cancel-state transition ─────────────────────
test('generating card (flag + deferred) → wasGenerating, flags reset, deferred resolved', function () {
  let resolved = 0;
  const card = { _suggestionsGenerating: true, _suggestionsRequested: true, _suggestionsError: 'boom', _suggestionCancel: { resolve: function () { resolved++; } } };
  const wasGenerating = abortSuggestionGeneration(card);
  assert.strictEqual(wasGenerating, true);
  assert.strictEqual(resolved, 1);
  assert.strictEqual(card._suggestionsCancelled, true);
  assert.strictEqual(card._suggestionsGenerating, false);
  assert.strictEqual(card._suggestionsRequested, false);
  assert.strictEqual(card._suggestionsError, null);
});

test('card with only a deferred (no generating flag) → still counts as generating', function () {
  let resolved = 0;
  const card = { _suggestionCancel: { resolve: function () { resolved++; } } };
  assert.strictEqual(abortSuggestionGeneration(card), true);
  assert.strictEqual(resolved, 1);
  assert.strictEqual(card._suggestionsCancelled, true);
});

test('quiet card (no flags) → not generating, but still marked cancelled + cleared', function () {
  const card = { _suggestionsRequested: true, _suggestionsError: 'stale' };
  assert.strictEqual(abortSuggestionGeneration(card), false);
  assert.strictEqual(card._suggestionsCancelled, true);
  assert.strictEqual(card._suggestionsRequested, false);
  assert.strictEqual(card._suggestionsError, null);
});

test('deferred resolve throwing → does not propagate, flags still reset', function () {
  const card = { _suggestionsGenerating: true, _suggestionCancel: { resolve: function () { throw new Error('boom'); } } };
  assert.doesNotThrow(function () { abortSuggestionGeneration(card); });
  assert.strictEqual(card._suggestionsCancelled, true);
  assert.strictEqual(card._suggestionsGenerating, false);
});

// ── Cancel-path contract the production promise handlers rely on ───────────
test('after abort, _suggestionsCancelled is true and _suggestionCancel survives for the handler', function () {
  const deferred = { resolve: function () { } };
  const card = { _suggestionsGenerating: true, _suggestionCancel: deferred };
  abortSuggestionGeneration(card);
  // The success/error handlers check `card._suggestionsCancelled` first and bail; the
  // deferred is deleted by that handler (or overwritten by the next generation), not by
  // the cancel itself — so a late response can never assign suggestions.
  assert.strictEqual(card._suggestionsCancelled, true);
  assert.strictEqual(card._suggestionCancel, deferred);
});

// ── clearStaleSuggestions: dropping suggestions generated against the old text ──
test('card with suggestions → returns true, suggestions + display flags dropped', function () {
  const card = { _suggestions: [{ id: 1, description: 'old text work' }], _suggestionsNone: true, _suggestionsSaturated: true };
  assert.strictEqual(clearStaleSuggestions(card), true);
  assert.strictEqual(card._suggestions, undefined);
  assert.strictEqual(card._suggestionsNone, false);
  assert.strictEqual(card._suggestionsSaturated, false);
});

test('card with only a request flag → returns true and clears it', function () {
  const card = { _suggestionsRequested: true };
  assert.strictEqual(clearStaleSuggestions(card), true);
  assert.strictEqual(card._suggestionsRequested, true); // flag clearing is cancel's job
  assert.strictEqual(card._suggestions, undefined);
});

test('card with only an in-flight generation → returns true', function () {
  const card = { _suggestionsGenerating: true };
  assert.strictEqual(clearStaleSuggestions(card), true);
  assert.strictEqual(card._suggestions, undefined);
});

test('quiet card → returns false and stays untouched', function () {
  const card = {};
  assert.strictEqual(clearStaleSuggestions(card), false);
  assert.deepStrictEqual(Object.keys(card), []);
});

// ── Full invalidate path (vm.invalidateCardSuggestions = cancel + clear) ────
test('text edit on a completed card: in-flight generation aborted AND stale suggestions dropped', function () {
  let resolved = 0;
  const card = {
    _suggestions: [{ id: 1, description: 'follow-up for the OLD text' }],
    _suggestionsGenerating: true,
    _suggestionsRequested: true,
    _suggestionsError: 'in flight',
    _suggestionCancel: { resolve: function () { resolved++; } }
  };
  const wasGenerating = abortSuggestionGeneration(card);
  const hadState = clearStaleSuggestions(card);
  assert.strictEqual(wasGenerating, true);
  assert.strictEqual(hadState, true);
  assert.strictEqual(resolved, 1);
  assert.strictEqual(card._suggestionsCancelled, true);
  assert.strictEqual(card._suggestions, undefined);
  assert.strictEqual(card._suggestionsGenerating, false);
  assert.strictEqual(card._suggestionsRequested, false);
  assert.strictEqual(card._suggestionsError, null);
});

test('text edit on a card with nothing to invalidate → no-op, no new keys', function () {
  // Mirrors vm.invalidateCardSuggestions: guard first, and only cancel when there was
  // state — so a quiet card is left completely untouched (no stray flags).
  const card = { id: 'x', text: 'plain todo card' };
  if (clearStaleSuggestions(card)) abortSuggestionGeneration(card);
  assert.deepStrictEqual(Object.keys(card).sort(), ['id', 'text']);
  assert.strictEqual(card._suggestionsCancelled, undefined);
});

// ── suggestionSystemBlocked: idle-only enforcement ─────────────────────────
test('completely idle board → suggestions allowed', function () {
  assert.strictEqual(suggestionSystemBlocked({}), false);
});

test('null state → not blocked', function () {
  assert.strictEqual(suggestionSystemBlocked(null), false);
});

test('card executing (streamingActive) → blocked', function () {
  assert.strictEqual(suggestionSystemBlocked({ streamingActive: true }), true);
});

test('benchmark running → blocked', function () {
  assert.strictEqual(suggestionSystemBlocked({ benchmarkRunning: true }), true);
});

test('benchmark-all active → blocked', function () {
  assert.strictEqual(suggestionSystemBlocked({ benchmarkAllActive: true }), true);
});

test('self-improving cycle armed → blocked', function () {
  assert.strictEqual(suggestionSystemBlocked({ selfImprovingAgentActive: true }), true);
});

test('self-improving flag not literally true → not blocked', function () {
  assert.strictEqual(suggestionSystemBlocked({ selfImprovingAgentActive: 'x' }), false);
});

test('idle flags present but nothing executing → not blocked', function () {
  assert.strictEqual(suggestionSystemBlocked({ streamingActive: false, benchmarkRunning: false, _suggestionIdlePaused: true }), false);
});

// ── _doneCardsNeedingSuggestions: benchmark cards are never eligible ──────

test('benchmark card in Done → never counted as needing suggestions', function () {
  // vm.isBenchmarkCard is provided by the real vm (KanbanMixin) — mirror its flag check.
  const vm = {
    state: { done: [{ id: 'b1', _benchmark: true, text: 'benchmark card' }] },
    projectMaxSuggestions: function () { return 3; },
    isBenchmarkCard: function (c) { return !!(c && c._benchmark); }
  };
  assert.deepStrictEqual(doneCardsNeedingSuggestions(vm), []);
});

test('benchmark card mixed with regular Done cards → only the regular card is counted', function () {
  const vm = {
    state: { done: [
      { id: 'b1', _benchmark: true, text: 'benchmark card' },
      { id: 'c1', text: 'finished work', _suggestions: [] }
    ] },
    projectMaxSuggestions: function () { return 3; },
    isBenchmarkCard: function (c) { return !!(c && c._benchmark); }
  };
  const need = doneCardsNeedingSuggestions(vm);
  assert.strictEqual(need.length, 1);
  assert.strictEqual(need[0].id, 'c1');
});

test('regular Done card without suggestions → counted', function () {
  const vm = {
    state: { done: [{ id: 'c1', text: 'finished work' }] },
    projectMaxSuggestions: function () { return 3; }
  };
  const need = doneCardsNeedingSuggestions(vm);
  assert.strictEqual(need.length, 1);
  assert.strictEqual(need[0].id, 'c1');
});

console.log('\n' + passed + ' passed, ' + failed + ' failed');
if (failed > 0) process.exit(1);

