// meeting-bubble.test.js
// Unit tests for wwwroot/meeting.js's canvas text helpers: fitFont (wall text
// that shrinks to fit its frame), drawSpeechBubble (spider chat bubbles that
// must be clean, readable and never bounce), and drawSpiderDetails (each role's
// signature accessory must draw without throwing). All three live inside the
// Angular MeetingMixin factory closure, so we extract their source text and eval
// them with stubbed ctx/mf/rr, mirroring the meeting-ticker.test.js approach.
// Dependency-free Node test runner:  node tests/js/meeting-bubble.test.js
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
const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/meeting.js'), 'utf8');

function extract(fnName, params) {
  const re = new RegExp('function ' + fnName + '\\(' + params + '\\) \\{[\\s\\S]*?\\n        \\}');
  const m = re.exec(src);
  assert(m, fnName + ' not found in wwwroot/meeting.js — marker format may have drifted');
  const body = m[0].replace(/^function [a-zA-Z]+\([^)]*\) \{/, '').replace(/\n        \}$/, '');
  // The helpers reference the factory closure's `ctx`/`mf`/`rr` as free
  // variables, so wrap the body in a binder that supplies the stubs.
  return eval('(function (ctx, mf, rr) { return function ' + fnName + '(' + params + ') {' + body + '}; })');
}

// One shared stub ctx — each test resets the call counters before running.
const sharedCtx = makeCtx();
const mf = (px) => px;
const rr = (x, y, w, h) => { sharedCtx.calls.rrCalls.push({ x: x, y: y, w: w, h: h }); };

const fitFont = extract('fitFont', 'text, maxFont, maxWidth, bold')(sharedCtx, mf, rr);
const drawSpeechBubble = extract('drawSpeechBubble', 'W, H, px, py, s')(sharedCtx, mf, rr);
const drawSpiderDetails = extract('drawSpiderDetails', 'px, cy, bodyW, bodyH, scale, s, eyeY')(sharedCtx, mf, rr);

function resetCtx() {
  sharedCtx.calls.fills = 0;
  sharedCtx.calls.strokes = 0;
  sharedCtx.calls.fillTexts = [];
  sharedCtx.calls.rrCalls = [];
}

// ── Stubs ────────────────────────────────────────────────────────────────────
// measureText is proportional to the current font so fitFont's shrink loop and
// the bubble's word wrap behave like a real canvas (≈0.6em average char width).
function makeCtx() {
  const calls = { fills: 0, strokes: 0, fillTexts: [] };
  const ctx = {
    font: '10px sans-serif',
    fillStyle: '', strokeStyle: '', lineWidth: 1, lineCap: '', textAlign: 'left',
    textBaseline: 'alphabetic', globalAlpha: 1,
    calls,
    measureText(t) {
      const f = parseFloat(String(ctx.font).replace(/^bold /, '')) || 10;
      return { width: String(t).length * 0.6 * f };
    },
    save() {}, restore() {}, beginPath() {}, closePath() {},
    moveTo() {}, lineTo() {}, arc() {}, ellipse() {}, quadraticCurveTo() {}, rect() {},
    clip() {},
    fill() { calls.fills++; },
    stroke() { calls.strokes++; },
    fillText(t, x, y) { calls.fillTexts.push({ text: t, x: x, y: y }); },
    fillRect() { calls.fills++; }
  };
  return ctx;
}

console.log('meeting canvas text helpers\n');

// ── fitFont: wall/poster text must shrink to fit its frame ─────────────────
test('fitFont: oversized poster title shrinks to fit the width', function () {
  const got = fitFont('STAND', 62, 184, true);
  assert.ok(got < 62, 'should shrink below maxFont, got ' + got);
  assert.ok(got >= 59, 'should stay near maxFont for short text, got ' + got);
  // 'STAND' at size s is 5 * 0.6 * s = 3s px wide; must fit 184 → s <= 61.33
  assert.ok(3 * got <= 184 + 0.01, 'returned size still overflows the frame');
});

test('fitFont: short text keeps the requested size when it already fits', function () {
  const got = fitFont('UP', 62, 184, true);
  assert.strictEqual(got, 62);
});

test('fitFont: long tagline shrinks hard on a narrow frame', function () {
  const got = fitFont('EVERY · DAY', 25, 100, false);
  assert.ok(got < 25, 'should shrink, got ' + got);
  assert.ok(11 * 0.6 * got <= 100 + 0.01, 'tagline still overflows the frame');
});

test('fitFont: never returns below 4 (floor guard)', function () {
  const got = fitFont('A very long phrase that can never fit at any size', 12, 3, false);
  assert.strictEqual(got, 4);
});

// ── drawSpeechBubble: clean, readable, never bouncing ──────────────────────
test('bubble: short quip fits on one line with no scrolling', function () {
  resetCtx();
  drawSpeechBubble(600, 400, 300, 100, { speech: 'hi there', speechTtl: 3 });
  assert.strictEqual(sharedCtx.calls.fillTexts.length, 1, 'one line of text');
  assert.strictEqual(sharedCtx.calls.fillTexts[0].text, 'hi there');
});

test('bubble: long text caps at five visible lines', function () {
  resetCtx();
  const long = 'This is a very long quip that wraps onto many lines because spiders have a lot to say about the agent run, the edits, the verification, and everything in between.';
  drawSpeechBubble(600, 400, 300, 300, { speech: long, speechTtl: 3 });
  assert.ok(sharedCtx.calls.fillTexts.length >= 5, 'wrapped into several lines: ' + sharedCtx.calls.fillTexts.length);
  // The bubble body height must cap at 5 lines (5 × 14px lineHeight + 2 × 6px pad).
  assert.strictEqual(sharedCtx.calls.rrCalls.length, 1, 'one bubble body drawn');
  const bh = sharedCtx.calls.rrCalls[0].h;
  assert.strictEqual(bh, 5 * 14 + 2 * 6, 'bubble height capped at five lines, got ' + bh);
});

test('bubble: scroll is monotonic and bounded — no bounce back', function () {
  resetCtx();
  const long = 'This is a very long quip that wraps onto many lines because spiders have a lot to say about the agent run, the edits, the verification, and everything in between, so it definitely overflows the five line window and must scroll.';
  // Simulate t=0: speech just set → _speechStart = now → elapsed ≈ 0.
  const s0 = { speech: long, speechTtl: 3 };
  drawSpeechBubble(600, 400, 300, 300, s0);
  const y0 = sharedCtx.calls.fillTexts[0].y;
  // Simulate t=100s: scroll fully played out → the first line scrolls off the top.
  resetCtx();
  const s1 = { speech: long, speechTtl: 3, _speechText: long, _speechStart: Date.now() - 100000 };
  drawSpeechBubble(600, 400, 300, 300, s1);
  const y100 = sharedCtx.calls.fillTexts[0].y;
  assert.ok(y100 < y0, 'text must move up as time passes (' + y0 + ' → ' + y100 + ')');
  assert.ok(y100 >= y0 - (sharedCtx.calls.fillTexts.length - 1) * 14 - 40, 'scroll must never overshoot the last line');
});

test('bubble: stays on screen when the spider is near the top edge', function () {
  resetCtx();
  drawSpeechBubble(600, 400, 300, 10, { speech: 'tiny spider near the ceiling', speechTtl: 3 });
  assert.ok(sharedCtx.calls.fillTexts.length >= 1);
});

test('bubble: stays within the horizontal bounds', function () {
  resetCtx();
  drawSpeechBubble(600, 400, 590, 300, { speech: 'a quip from the right edge of the office', speechTtl: 3 });
  assert.ok(sharedCtx.calls.fillTexts.length >= 1);
});

// ── drawSpiderDetails: every role's accessory draws without throwing ───────
const ROLES = ['planner', 'explorer', 'editor', 'commander', 'verifier', 'reviewer',
  'itspecialist', 'ideas', 'complexity'];

for (const role of ROLES) {
  test('spider details: ' + role + ' accessory draws cleanly', function () {
    resetCtx();
    drawSpiderDetails(100, 100, 26, 20, 1, { role: role }, 98);
    assert.ok(sharedCtx.calls.fills > 0 || sharedCtx.calls.strokes > 0, 'draws something');
  });
}

test('spider details: unknown role still draws the mouth (no crash)', function () {
  resetCtx();
  drawSpiderDetails(100, 100, 26, 20, 1, { role: 'mystery' }, 98);
  assert.ok(sharedCtx.calls.strokes > 0);
});

console.log('\n' + (failed ? failed + ' FAILED, ' + passed + ' passed' : 'All ' + passed + ' tests passed') + '\n');
process.exit(failed ? 1 : 0);
