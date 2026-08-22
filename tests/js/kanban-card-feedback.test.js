// kanban-card-feedback.test.js
// The card 👍/👎 rating widget: vm.cardHasRun gates the "💬 Feedback" section on a card that
// has produced a run result (previous analysis, agent log, or a verification verdict), and
// vm.rateCard / vm.submitCardFeedback / vm.cancelCardFeedback drive the rating + the feedback
// prompt revealed on a thumbs-down. Extracted from the live wwwroot/kanban.js source and
// eval'd with a mocked vm.saveCards, mirroring the kanban-done-verdict test's approach.
// Thumbs-up and thumbs-down+text now POST to /api/bughosted/feedback when connected.
// Dependency-free Node test runner:  node tests/js/kanban-card-feedback.test.js
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

const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/kanban.js'), 'utf8').replace(/\r\n/g, '\n');

// Extract a vm.Xxx = function (params) { ... } block from kanban.js.
// The closing brace is on its own line with 6 spaces of indentation.
function extract(name, params) {
  // Build regex pattern: vm\.name = function \(params\) \{[\s\S]*?\n      \}
  var pattern = 'vm\\.' + name + ' = function \\(' + params + '\\) \\{[\\s\\S]*?\\n      \\}';
  var m = new RegExp(pattern).exec(src);
  assert(m, name + ' not found in wwwroot/kanban.js — marker format may have drifted');
  return m[0];
}

// Extract the private _postRatingToBughosted helper (defined before rateCard in kanban.js).
// It is called by rateCard and submitCardFeedback to POST to bughosted.
function extractPrivate(name) {
  var pattern = 'function ' + name + '\\([^)]*\\) \\{[\\s\\S]*?\\n      \\}';
  var m = new RegExp(pattern).exec(src);
  assert(m, name + ' not found in wwwroot/kanban.js');
  return m[0];
}

const cardHasRunSrc = extract('cardHasRun', 'card');
const rateCardSrc = extract('rateCard', 'card, rating');
const submitSrc = extract('submitCardFeedback', 'card');
const cancelSrc = extract('cancelCardFeedback', 'card');
const postRatingSrc = extractPrivate('_postRatingToBughosted');

function makeVm(saveCalls, opts) {
  opts = opts || {};
  var posts = opts.posts || []; // recorded $http.post calls
  var vm = {
    saveCards: function () { saveCalls.push('save'); },
    bughostedClientId: opts.bughostedClientId || '',
    bughostedStatus: opts.bughostedStatus || 'disconnected',
    addLogEntry: opts.addLogEntry || function () {},
    findCardById: opts.findCardById || function () { return null; },
    $http: {
      post: function (url, payload) {
        posts.push({ url: url, payload: payload });
        return { then: function (cb) { cb({}); } };
      }
    }
  };
  // Evaluate the extracted sources in order — cardHasRun, _postRatingToBughosted
  // (private, must come before callers), then the vm.Xxx assignments that call it.
  new Function('vm', '$http', [cardHasRunSrc, postRatingSrc, rateCardSrc, submitSrc, cancelSrc].join('\n'))(vm, vm.$http);
  return vm;
}

// ── cardHasRun gate ──────────────────────────────────────────────────────────

test('cardHasRun true for agentAnalysis / agentLog / verification', () => {
  const vm = makeVm([]);
  assert.strictEqual(vm.cardHasRun({ agentAnalysis: {} }), true);
  assert.strictEqual(vm.cardHasRun({ agentLog: [{}] }), true);
  assert.strictEqual(vm.cardHasRun({ _verification: { complete: true } }), true);
});

test('cardHasRun false for an empty or un-run card', () => {
  const vm = makeVm([]);
  assert.strictEqual(vm.cardHasRun(null), false);
  assert.strictEqual(vm.cardHasRun({}), false);
  assert.strictEqual(vm.cardHasRun({ agentLog: [] }), false);
});

// ── rateCard ─────────────────────────────────────────────────────────────────

test('thumbs-up records rating and clears a pending draft', () => {
  const calls = [];
  const vm = makeVm(calls);
  const card = { _feedback: { draft: 'stale' } };
  vm.rateCard(card, 'up');
  assert.strictEqual(card._feedback.rating, 'up');
  assert.strictEqual('draft' in card._feedback, false);
  assert.deepStrictEqual(calls, ['save']);
});

test('thumbs-down records rating and leaves the prompt open', () => {
  const calls = [];
  const vm = makeVm(calls);
  const card = {};
  vm.rateCard(card, 'down');
  assert.strictEqual(card._feedback.rating, 'down');
  assert.deepStrictEqual(calls, ['save']);
});

test('rateCard on a null card is a no-op', () => {
  const calls = [];
  const vm = makeVm(calls);
  vm.rateCard(null, 'up');
  assert.deepStrictEqual(calls, []);
});

test('thumbs-up does not POST when not connected to bughosted', () => {
  const calls = [];
  const posts = [];
  const vm = makeVm(calls, { posts: posts, bughostedStatus: 'disconnected' });
  const card = { id: 'c1', text: 'test', _feedback: {} };
  vm.rateCard(card, 'up');
  assert.strictEqual(posts.length, 0);
});

test('thumbs-up POSTs to bughosted when connected', () => {
  const calls = [];
  const posts = [];
  const vm = makeVm(calls, {
    posts: posts,
    bughostedClientId: 'tok123',
    bughostedStatus: 'connected',
    findCardById: function (id) { return { id: id, _feedbackSent: [] }; }
  });
  const card = { id: 'c1', text: 'fix the bug', _feedback: {}, agentAnalysis: { summary: 'plan' } };
  vm.rateCard(card, 'up');
  assert.strictEqual(posts.length, 1);
  assert.strictEqual(posts[0].url, '/api/bughosted/feedback');
  assert.strictEqual(posts[0].payload.clientId, 'tok123');
  assert.strictEqual(posts[0].payload.cardId, 'c1');
  assert.strictEqual(posts[0].payload.message, '\u{1F44D} Thumbs up \u2014 this run was helpful');
});

// ── submitCardFeedback ───────────────────────────────────────────────────────

test('submit stores trimmed text, clears the draft, and saves', () => {
  const calls = [];
  const vm = makeVm(calls);
  const card = { _feedback: { rating: 'down', draft: '  the edit missed a file  ' } };
  vm.submitCardFeedback(card);
  assert.strictEqual(card._feedback.text, 'the edit missed a file');
  assert.strictEqual('draft' in card._feedback, false);
  assert.deepStrictEqual(calls, ['save']);
});

test('submit with an empty draft sends default thumbs-down message to bughosted', () => {
  const calls = [];
  const posts = [];
  const vm = makeVm(calls, {
    posts: posts,
    bughostedClientId: 'tok123',
    bughostedStatus: 'connected',
    findCardById: function (id) { return { id: id, _feedbackSent: [] }; }
  });
  const card = { id: 'c1', text: 'x', _feedback: { rating: 'down', draft: '   ' }, agentAnalysis: { summary: 'run' } };
  vm.submitCardFeedback(card);
  assert.strictEqual('text' in card._feedback, false);
  assert.strictEqual('draft' in card._feedback, false);
  assert.ok(calls.includes('save'), 'saveCards called');
  assert.strictEqual(posts.length, 1, 'empty text still POSTs default thumbs-down message');
  assert.strictEqual(posts[0].payload.message, '\u{1F44E} Thumbs down \u2014 this run needs work');
});

test('submit POSTs thumbs-down feedback to bughosted when connected', () => {
  const calls = [];
  const posts = [];
  const vm = makeVm(calls, {
    posts: posts,
    bughostedClientId: 'tok123',
    bughostedStatus: 'connected',
    findCardById: function (id) { return { id: id, _feedbackSent: [] }; }
  });
  const card = { id: 'c2', text: 'task', _feedback: { rating: 'down', draft: ' missed file ' }, agentAnalysis: { summary: 'run' } };
  vm.submitCardFeedback(card);
  assert.strictEqual(card._feedback.text, 'missed file');
  assert.strictEqual(posts.length, 1);
  assert.strictEqual(posts[0].url, '/api/bughosted/feedback');
  assert.strictEqual(posts[0].payload.clientId, 'tok123');
  assert.strictEqual(posts[0].payload.cardId, 'c2');
  assert.strictEqual(posts[0].payload.message, '\u{1F44E} missed file');
});

test('submit does not POST when not connected to bughosted', () => {
  const calls = [];
  const posts = [];
  const vm = makeVm(calls, { posts: posts, bughostedStatus: 'disconnected' });
  const card = { _feedback: { rating: 'down', draft: 'broke it' } };
  vm.submitCardFeedback(card);
  assert.strictEqual(posts.length, 0);
});

test('POST includes filesEdited and steps from agentAnalysis', () => {
  const calls = [];
  const posts = [];
  const vm = makeVm(calls, {
    posts: posts,
    bughostedClientId: 'tok',
    bughostedStatus: 'connected',
    findCardById: function (id) { return { id: id, _feedbackSent: [] }; }
  });
  const card = {
    id: 'c3', text: 'x',
    _feedback: { rating: 'down', draft: 'bad' },
    agentAnalysis: {
      summary: 'plan',
      filesEdited: ['a.ts', { path: 'b.ts' }],
      steps: [{ type: 'edit', description: 'fix', status: 'done' }]
    }
  };
  vm.submitCardFeedback(card);
  assert.strictEqual(posts.length, 1);
  assert.deepStrictEqual(posts[0].payload.filesEdited, ['a.ts', 'b.ts']);
  assert.deepStrictEqual(posts[0].payload.steps, [{ type: 'edit', change: 'fix', status: 'done' }]);
});

test('POST appends to _feedbackSent on the re-found card', () => {
  const calls = [];
  const posts = [];
  const sentEntries = [];
  const vm = makeVm(calls, {
    posts: posts,
    bughostedClientId: 'tok',
    bughostedStatus: 'connected',
    findCardById: function () { return { id: 'c4', _feedbackSent: sentEntries }; }
  });
  const card = { id: 'c4', text: 'x', _feedback: { rating: 'down', draft: 'oops' } };
  vm.submitCardFeedback(card);
  assert.strictEqual(sentEntries.length, 1);
  assert.ok(sentEntries[0].at);
  assert.strictEqual(sentEntries[0].message, '\u{1F44E} oops');
});

// ── cancelCardFeedback ───────────────────────────────────────────────────────

test('cancel clears the draft but keeps the rating', () => {
  const calls = [];
  const vm = makeVm(calls);
  const card = { _feedback: { rating: 'down', draft: 'unfinished' } };
  vm.cancelCardFeedback(card);
  assert.strictEqual(card._feedback.rating, 'down');
  assert.strictEqual('draft' in card._feedback, false);
  assert.deepStrictEqual(calls, ['save']);
});

// ── HTML wiring ──────────────────────────────────────────────────────────────

test('kanban.html wires the rating buttons to the helpers', () => {
  const html = fs.readFileSync(path.join(__dirname, '../../wwwroot/kanban.html'), 'utf8');
  assert.ok(html.includes('vm.cardHasRun(card)'), 'feedback section must be gated on vm.cardHasRun');
  assert.ok(html.includes("vm.rateCard(card, 'up')"), 'thumbs-up must call vm.rateCard');
  assert.ok(html.includes("vm.rateCard(card, 'down')"), 'thumbs-down must call vm.rateCard');
  assert.ok(html.includes('vm.submitCardFeedback(card)'), 'submit must call vm.submitCardFeedback');
  assert.ok(html.includes("card._feedback.rating === 'down' && !card._feedback.text"), 'feedback prompt must appear on thumbs-down before submit');
});

console.log('\n# ' + passed + ' passed, ' + failed + ' failed');
process.exitCode = failed ? 1 : 0;
