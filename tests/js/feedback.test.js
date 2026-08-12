// feedback.test.js
// Unit tests for wwwroot/agent.js's vm.sendFeedback — the card-feedback submission to
// the bughosted proxy (POST /api/bughosted/feedback). Extracted from the AgentMixin
// factory closure source and eval'd with mocked $http / vm (findCardById, saveCards,
// addLogEntry), mirroring the steer-now test's approach.
// Covers the payload shape (cardId/cardText/message/planSummary/filesEdited), the
// not-connected guard, the success path (✓ indicator + saveCards + log), and the
// failure path (feedbackError surfaced).
// Dependency-free Node test runner:  node tests/js/feedback.test.js
'use strict';

const assert = require('assert');
const fs = require('fs');
const path = require('path');

let passed = 0;
let failed = 0;

async function run(name, fn) {
  try {
    await fn();
    passed++;
    console.log('  ✓ ' + name);
  } catch (e) {
    failed++;
    console.error('  ✗ ' + name);
    console.error('      ' + (e && e.message));
  }
}

// ── Extract sendFeedback from the live source ───────────────────────────────
const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/agent.js'), 'utf8');
const match = /vm\.sendFeedback = function \(\) \{[\s\S]*?\n                \};/.exec(src);
assert(match, 'vm.sendFeedback not found in wwwroot/agent.js — marker format may have drifted');
const assignment = match[0]; // 'vm.sendFeedback = function () { ... };'

function makeSendFeedback({
  card = null,
  feedbackText = 'this run went wrong',
  connected = true,
  findCardById = null,
  httpImpl = null,
} = {}) {
  const logEntries = [];
  const saveCalls = [];
  const vm = {
    feedbackCard: card,
    feedbackSending: false,
    feedbackText,
    feedbackError: '',
    bughostedClientId: connected ? 'client-42' : '',
    bughostedStatus: connected ? 'connected' : 'disconnected',
    findCardById: findCardById || (() => card),
    saveCards: () => saveCalls.push('save'),
    addLogEntry: (entry) => logEntries.push(entry),
  };
  // The live code calls $http.post(url, body) — mock $http as an object exposing .post.
  const $http = httpImpl
    ? { post: httpImpl }
    : { post: () => Promise.reject(new Error('unexpected $http.post')) };
  // eslint-disable-next-line no-new-func
  const sendFeedback = new Function('vm', '$http', assignment + '\nreturn vm.sendFeedback;')
    (vm, $http);
  return { sendFeedback, vm, logEntries, saveCalls };
}

// Extract openFeedback / closeFeedback the same way (they populate the popup's
// previous-messages preview from the card's _feedbackSent array).
const NL = String.fromCharCode(10);
function extractAssignment(name, params) {
  const marker = 'vm.' + name + ' = function ' + params + ' {';
  const start = src.indexOf(marker);
  assert(start !== -1, marker + ' not found in wwwroot/agent.js — marker format may have drifted');
  const closeMarker = NL + '                };';
  const end = src.indexOf(closeMarker, start);
  assert(end !== -1, name + ' closing marker not found in wwwroot/agent.js');
  return src.slice(start, end + closeMarker.length);
}
const openAssignment = extractAssignment('openFeedback', '(card)');
const closeAssignment = extractAssignment('closeFeedback', '()');

function makeOpenClose(vmOverrides = {}) {
  const vm = Object.assign({
    feedbackCard: null,
    feedbackText: 'stale',
    feedbackError: 'stale',
    feedbackPrevious: [],
  }, vmOverrides);
  // eslint-disable-next-line no-new-func
  const openFeedback = new Function('vm', openAssignment + '\nreturn vm.openFeedback;')(vm);
  // eslint-disable-next-line no-new-func
  const closeFeedback = new Function('vm', closeAssignment + '\nreturn vm.closeFeedback;')(vm);
  return { openFeedback, closeFeedback, vm };
}

function okHttp(body) {
  return { then: (ok) => Promise.resolve(ok(body)) };
}
function failHttp(status, error) {
  return {
    then: (ok, fail) => Promise.resolve(fail({ status, data: error ? { error } : undefined })),
  };
}

const baseCard = {
  id: 'card-7',
  text: 'Fix the schedule popup',
  agentAnalysis: {
    summary: 'Add max-height + overflow:auto to the schedules container',
    filesEdited: [
      { path: 'maxhanna.client/src/app/globe/globe.component.css' },
      'maxhanna.client/src/app/globe/globe.component.html',
      '',
    ],
    steps: [
      { type: 'edit', description: 'Add CSS rules for the schedules container', status: 'done' },
      { type: '_web_search', command: 'latest release notes', status: 'done' },
      { type: 'verified_complete', description: 'Final verification', status: 'pending' },
    ],
  },
};

(async function () {
  const test = run;

  // ── Guards: nothing to send / already sending / not connected ───────────────

  await test('no feedback card → no POST', async function () {
    let posted = false;
    const { sendFeedback } = makeSendFeedback({
      card: null,
      httpImpl: () => { posted = true; return okHttp({}); },
    });
    await sendFeedback();
    assert.strictEqual(posted, false);
  });

  await test('empty message → no POST', async function () {
    let posted = false;
    const { sendFeedback } = makeSendFeedback({
      feedbackText: '   ',
      httpImpl: () => { posted = true; return okHttp({}); },
    });
    await sendFeedback();
    assert.strictEqual(posted, false);
  });

  await test('already sending → no POST', async function () {
    let posted = false;
    const { sendFeedback, vm } = makeSendFeedback({ httpImpl: () => { posted = true; return okHttp({}); } });
    vm.feedbackSending = true;
    await sendFeedback();
    assert.strictEqual(posted, false);
  });

  await test('not connected to bughosted → error message, no POST', async function () {
    let posted = false;
    const { sendFeedback, vm } = makeSendFeedback({
      card: baseCard,
      connected: false,
      httpImpl: () => { posted = true; return okHttp({}); },
    });
    await sendFeedback();
    assert.strictEqual(posted, false);
    assert.ok(vm.feedbackError.includes('Not connected to bughosted'));
  });

  // ── Payload shape ───────────────────────────────────────────────────────────

  await test('POSTs the full payload to /api/bughosted/feedback', async function () {
    let sent = null;
    const { sendFeedback } = makeSendFeedback({
      card: baseCard,
      httpImpl: (url, body) => { sent = { url, body }; return okHttp({}); },
    });
    await sendFeedback();
    assert.strictEqual(sent.url, '/api/bughosted/feedback');
    assert.deepStrictEqual(sent.body, {
      clientId: 'client-42',
      cardId: 'card-7',
      cardText: 'Fix the schedule popup',
      message: 'this run went wrong',
      planSummary: 'Add max-height + overflow:auto to the schedules container',
      filesEdited: [
        'maxhanna.client/src/app/globe/globe.component.css',
        'maxhanna.client/src/app/globe/globe.component.html',
      ],
      steps: [
        { type: 'edit', change: 'Add CSS rules for the schedules container', status: 'done' },
        { type: '_web_search', change: 'latest release notes', status: 'done' },
        { type: 'verified_complete', change: 'Final verification', status: 'pending' },
      ],
    });
  });

  await test('message is trimmed before sending', async function () {
    let sent = null;
    const { sendFeedback } = makeSendFeedback({
      card: baseCard,
      feedbackText: '  trailing spaces here  ',
      httpImpl: (url, body) => { sent = body; return okHttp({}); },
    });
    await sendFeedback();
    assert.strictEqual(sent.message, 'trailing spaces here');
  });

  await test('missing agentAnalysis degrades to empty planSummary/filesEdited/steps', async function () {
    let sent = null;
    const { sendFeedback } = makeSendFeedback({
      card: { id: 'card-1', text: 'x' },
      httpImpl: (url, body) => { sent = body; return okHttp({}); },
    });
    await sendFeedback();
    assert.strictEqual(sent.planSummary, '');
    assert.deepStrictEqual(sent.filesEdited, []);
    assert.deepStrictEqual(sent.steps, []);
  });

  await test('step change falls back description → path → command → url, junk steps skipped', async function () {
    let sent = null;
    const { sendFeedback } = makeSendFeedback({
      card: {
        id: 'card-3',
        text: 'x',
        agentAnalysis: {
          steps: [
            null,
            { type: 'edit', path: 'app/foo.ts', status: 'error' },
            { type: '_web_fetch', url: 'https://example.com/a', status: 'done' },
            { type: 'run', command: 'pwd', status: 'done' },
            { type: 'think' },
          ],
        },
      },
      httpImpl: (url, body) => { sent = body; return okHttp({}); },
    });
    await sendFeedback();
    assert.deepStrictEqual(sent.steps, [
      { type: 'edit', change: 'app/foo.ts', status: 'error' },
      { type: '_web_fetch', change: 'https://example.com/a', status: 'done' },
      { type: 'run', change: 'pwd', status: 'done' },
      { type: 'think', change: '', status: '' },
    ]);
  });

  // ── Success path: ✓ indicator + saveCards + log ─────────────────────────────

  await test('success stamps _feedbackSent on the card, saves, clears the popup, logs', async function () {
    const card = { id: 'card-7', text: 'Fix the schedule popup', agentAnalysis: { summary: 'x' } };
    const { sendFeedback, vm, saveCalls, logEntries } = makeSendFeedback({
      card,
      httpImpl: () => okHttp({}),
    });
    await sendFeedback();
    assert.ok(card._feedbackSent, 'card must carry _feedbackSent');
    assert.ok(Array.isArray(card._feedbackSent), '_feedbackSent must be an array');
    assert.strictEqual(card._feedbackSent.length, 1);
    assert.strictEqual(card._feedbackSent[0].message, 'this run went wrong');
    assert.ok(card._feedbackSent[0].at, 'entry must carry a timestamp');
    assert.deepStrictEqual(saveCalls, ['save']);
    assert.strictEqual(vm.feedbackCard, null);
    assert.strictEqual(vm.feedbackText, '');
    assert.strictEqual(logEntries.length, 1);
    assert.ok(logEntries[0].message.includes('Feedback sent for card #card-7'));
  });

  await test('second submission appends to _feedbackSent instead of replacing', async function () {
    const card = { id: 'card-7', text: 'x', _feedbackSent: [{ at: '2026-08-01T00:00:00.000Z', message: 'first' }] };
    const { sendFeedback, vm, saveCalls } = makeSendFeedback({
      card,
      httpImpl: () => okHttp({}),
    });
    await sendFeedback();
    // The success path clears the popup (feedbackCard) and the text box — the app
    // re-opens them via openFeedback() for a second submission, so restore here.
    vm.feedbackCard = card;
    vm.feedbackText = 'second report';
    await sendFeedback();
    assert.strictEqual(card._feedbackSent.length, 3);
    assert.strictEqual(card._feedbackSent[0].message, 'first');
    assert.strictEqual(card._feedbackSent[1].message, 'this run went wrong');
    assert.strictEqual(card._feedbackSent[2].message, 'second report');
    assert.strictEqual(saveCalls.length, 2);
  });

  await test('legacy single-object _feedbackSent is converted to an array on next send', async function () {
    const card = { id: 'card-7', text: 'x', _feedbackSent: { at: '2026-08-01T00:00:00.000Z', message: 'legacy' } };
    const { sendFeedback } = makeSendFeedback({
      card,
      httpImpl: () => okHttp({}),
    });
    await sendFeedback();
    assert.ok(Array.isArray(card._feedbackSent), 'legacy object must be normalized to an array');
    assert.strictEqual(card._feedbackSent.length, 2);
    assert.strictEqual(card._feedbackSent[0].message, 'legacy');
  });

  await test('success re-finds the card by id (popup card may be gone)', async function () {
    const realCard = { id: 'card-7', text: 'x' };
    const { sendFeedback, vm, saveCalls } = makeSendFeedback({
      card: baseCard,
      findCardById: (id) => (id === 'card-7' ? realCard : null),
      httpImpl: () => okHttp({}),
    });
    await sendFeedback();
    assert.ok(realCard._feedbackSent, 'the re-found card must be stamped');
    assert.strictEqual(saveCalls.length, 1);
    assert.strictEqual(vm.feedbackCard, null);
  });

  // ── Failure path ────────────────────────────────────────────────────────────

  await test('failure surfaces the server error and keeps the popup open', async function () {
    const { sendFeedback, vm } = makeSendFeedback({
      card: baseCard,
      httpImpl: () => failHttp(500, 'Upstream exploded'),
    });
    await sendFeedback();
    assert.strictEqual(vm.feedbackError, 'Upstream exploded');
    assert.ok(vm.feedbackCard, 'card stays open on failure');
    assert.strictEqual(vm.feedbackSending, false);
  });

  await test('failure without a data.error falls back to HTTP status', async function () {
    const { sendFeedback, vm } = makeSendFeedback({
      card: baseCard,
      httpImpl: () => failHttp(502),
    });
    await sendFeedback();
    assert.ok(vm.feedbackError.includes('HTTP 502'));
  });

  // ── ✓ Sent chip helpers (count / label / last) ──────────────────────────────

  function extractHelper(name) {
    const m = new RegExp('vm\\.' + name + ' = function \\(card\\) \\{[\\s\\S]*?\\n                \\};').exec(src);
    assert(m, name + ' not found in wwwroot/agent.js — marker format may have drifted');
    return m[0];
  }

  function makeFeedbackHelpers() {
    const vm = {};
    const block = ['feedbackSentEntries', 'feedbackSentCount', 'feedbackSentLast', 'feedbackSentLabel']
      .map(extractHelper)
      .join('\n');
    // eslint-disable-next-line no-new-func
    new Function('vm', block)(vm);
    return vm;
  }

  await test('helpers: single entry renders ✓ Sent, several render ✓ N sent', async function () {
    const vm = makeFeedbackHelpers();
    const one = { _feedbackSent: [{ at: '2026-08-01T00:00:00.000Z', message: 'a' }] };
    const many = { _feedbackSent: [{ at: '2026-08-01T00:00:00.000Z', message: 'a' }, { at: '2026-08-02T00:00:00.000Z', message: 'b' }, { at: '2026-08-03T00:00:00.000Z', message: 'c' }] };
    assert.strictEqual(vm.feedbackSentCount(one), 1);
    assert.strictEqual(vm.feedbackSentLabel(one), '✓ Sent');
    assert.strictEqual(vm.feedbackSentCount(many), 3);
    assert.strictEqual(vm.feedbackSentLabel(many), '✓ 3 sent');
  });

  await test('helpers: legacy single-object _feedbackSent counts as one and last is the object', async function () {
    const vm = makeFeedbackHelpers();
    const legacy = { _feedbackSent: { at: '2026-08-01T00:00:00.000Z', message: 'legacy' } };
    assert.strictEqual(vm.feedbackSentCount(legacy), 1);
    assert.strictEqual(vm.feedbackSentLabel(legacy), '✓ Sent');
    assert.strictEqual(vm.feedbackSentLast(legacy).message, 'legacy');
  });

  await test('helpers: last() returns the newest entry; empty cards return 0/null', async function () {
    const vm = makeFeedbackHelpers();
    const many = { _feedbackSent: [{ at: '2026-08-01T00:00:00.000Z', message: 'a' }, { at: '2026-08-02T00:00:00.000Z', message: 'b' }] };
    assert.strictEqual(vm.feedbackSentLast(many).message, 'b');
    assert.strictEqual(vm.feedbackSentCount(null), 0);
    assert.strictEqual(vm.feedbackSentCount({}), 0);
    assert.strictEqual(vm.feedbackSentLast({}), null);
    assert.strictEqual(vm.feedbackSentLabel({}), '✓ Sent');
  });

  // ── openFeedback / closeFeedback: previous-message preview ──────────────────

  await test('openFeedback shows previously submitted messages in feedbackPrevious', async function () {
    const card = {
      id: 'card-7',
      text: 'x',
      _feedbackSent: [
        { at: '2026-08-01T00:00:00.000Z', message: 'first report' },
        { at: '2026-08-02T00:00:00.000Z', message: 'second report' },
      ],
    };
    const { openFeedback, vm } = makeOpenClose();
    openFeedback(card);
    assert.strictEqual(vm.feedbackCard, card);
    assert.strictEqual(vm.feedbackPrevious.length, 2);
    assert.strictEqual(vm.feedbackPrevious[0].message, 'first report');
    assert.strictEqual(vm.feedbackPrevious[1].message, 'second report');
    assert.strictEqual(vm.feedbackText, '', 'text box must start empty');
  });

  await test('openFeedback normalizes a legacy single-object _feedbackSent to a preview array', async function () {
    const card = { id: 'card-7', text: 'x', _feedbackSent: { at: '2026-08-01T00:00:00.000Z', message: 'legacy report' } };
    const { openFeedback, vm } = makeOpenClose();
    openFeedback(card);
    assert.ok(Array.isArray(vm.feedbackPrevious));
    assert.strictEqual(vm.feedbackPrevious.length, 1);
    assert.strictEqual(vm.feedbackPrevious[0].message, 'legacy report');
  });

  await test('openFeedback with no prior feedback leaves an empty preview; closeFeedback resets it', async function () {
    const { openFeedback, closeFeedback, vm } = makeOpenClose();
    openFeedback({ id: 'card-8', text: 'y' });
    assert.deepStrictEqual(vm.feedbackPrevious, []);
    vm.feedbackPrevious = [{ at: 'x', message: 'stale' }];
    closeFeedback();
    assert.strictEqual(vm.feedbackCard, null);
    assert.deepStrictEqual(vm.feedbackPrevious, []);
  });

  await test('kanban.html feedback popup is wired to vm.feedbackPrevious', async function () {
    const html = fs.readFileSync(path.join(__dirname, '../../wwwroot/kanban.html'), 'utf8');
    const popup = html.split('CARD FEEDBACK POPUP')[1] || '';
    assert.ok(popup.includes('vm.feedbackPrevious'), 'popup must render the previous-messages preview');
    assert.ok(popup.includes('ng-repeat="entry in vm.feedbackPrevious"'), 'popup must iterate previous entries');
  });

  console.log(`\n# ${passed} passed, ${failed} failed`);
  process.exitCode = failed ? 1 : 0;
})();
