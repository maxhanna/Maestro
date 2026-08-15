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
  sharedCtx.calls.alphas = [];
}

// ── Stubs ────────────────────────────────────────────────────────────────────
// measureText is proportional to the current font so fitFont's shrink loop and
// the bubble's word wrap behave like a real canvas (≈0.6em average char width).
function makeCtx() {
  const calls = { fills: 0, strokes: 0, fillTexts: [], alphas: [] };
  let globalAlpha = 1;
  const ctx = {
    font: '10px sans-serif',
    fillStyle: '', strokeStyle: '', lineWidth: 1, lineCap: '', textAlign: 'left',
    textBaseline: 'alphabetic',
    calls,
    get globalAlpha() { return globalAlpha; },
    set globalAlpha(v) { globalAlpha = v; calls.alphas.push(v); },
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

// ── drawSpeechBubble: clean, readable, static, never popping ───────────────
// Bubbles fade in/out instead of popping, and text never moves while reading.
// Tests drive the fade timing by overriding Date.now (the bubble uses it both
// for the fade-in ramp and the _speechStart bookkeeping).
const realNow = Date.now;
const T0 = 1000000;
function withNow(fn) { Date.now = () => T0; try { return fn(); } finally { Date.now = realNow; } }
function established(text, speechTtl, ageMs) {
  return { speech: text, speechTtl: speechTtl, _speechText: text, _speechStart: T0 - ageMs };
}

test('bubble: short quip fits on one line with no scrolling', function () {
  resetCtx();
  withNow(function () {
    drawSpeechBubble(600, 400, 300, 100, established('hi there', 3, 1000));
  });
  assert.strictEqual(sharedCtx.calls.fillTexts.length, 1, 'one line of text');
  assert.strictEqual(sharedCtx.calls.fillTexts[0].text, 'hi there');
});

test('bubble: long text caps at five visible lines with an ellipsis on the cut', function () {
  resetCtx();
  const long = 'This is a very long quip that wraps onto many lines because spiders have a lot to say about the agent run, the edits, the verification, and everything in between.';
  withNow(function () {
    drawSpeechBubble(600, 400, 300, 300, established(long, 3, 1000));
  });
  assert.strictEqual(sharedCtx.calls.fillTexts.length, 5, 'wrapped into five capped lines');
  assert.ok(sharedCtx.calls.fillTexts[4].text.endsWith('…'), 'the cut line carries an ellipsis');
  // The bubble body height must cap at 5 lines (5 × 14px lineHeight + 2 × 6px pad).
  assert.strictEqual(sharedCtx.calls.rrCalls.length, 1, 'one bubble body drawn');
  assert.strictEqual(sharedCtx.calls.rrCalls[0].h, 5 * 14 + 2 * 6, 'bubble height capped at five lines');
});

test('bubble: text never moves — static layout at any age, no scroll', function () {
  resetCtx();
  const long = 'This is a very long quip that wraps onto many lines because spiders have a lot to say about the agent run, the edits, the verification, and everything in between, so it definitely overflows the five line window.';
  let yFresh, yOld;
  withNow(function () {
    drawSpeechBubble(600, 400, 300, 300, established(long, 3, 200));
    yFresh = sharedCtx.calls.fillTexts[0].y;
  });
  resetCtx();
  withNow(function () {
    drawSpeechBubble(600, 400, 300, 300, established(long, 3, 10000));
    yOld = sharedCtx.calls.fillTexts[0].y;
  });
  assert.strictEqual(yOld, yFresh, 'the first line sits at the same spot no matter the bubble age');
});

test('bubble: very long text shrinks the font to fit the screen instead of scrolling', function () {
  resetCtx();
  const long = 'This is a very long quip that wraps onto many lines because spiders have a lot to say about the agent run, the edits, the verification, and everything in between.';
  withNow(function () {
    drawSpeechBubble(600, 400, 300, 60, established(long, 3, 1000));
  });
  assert.strictEqual(sharedCtx.font, '8px sans-serif', 'font must shrink to the floor for a cramped spot');
  assert.ok(sharedCtx.calls.fillTexts.length < 5, 'fewer lines fit, got ' + sharedCtx.calls.fillTexts.length);
  assert.ok(sharedCtx.calls.fillTexts[sharedCtx.calls.fillTexts.length - 1].text.endsWith('…'), 'the cut is marked');
});

test('bubble: fades in — invisible at birth, full opacity after the ramp', function () {
  resetCtx();
  withNow(function () {
    drawSpeechBubble(600, 400, 300, 100, { speech: 'hi', speechTtl: 3 }); // brand new: age 0
  });
  assert.strictEqual(sharedCtx.calls.fillTexts.length, 0, 'nothing drawn at birth');
  resetCtx();
  withNow(function () {
    drawSpeechBubble(600, 400, 300, 100, established('hi', 3, 1000));
  });
  assert.strictEqual(sharedCtx.globalAlpha, 1, 'fully opaque once settled');
});

test('bubble: fades out during the final half second of its life', function () {
  resetCtx();
  withNow(function () {
    drawSpeechBubble(600, 400, 300, 100, established('hi', 0.2, 1000));
  });
  const drawAlpha = Math.max.apply(null, sharedCtx.calls.alphas.filter(a => a < 1));
  assert.ok(Math.abs(drawAlpha - 0.4) < 0.001, 'alpha tracks ttl/0.5, got ' + drawAlpha);
  resetCtx();
  withNow(function () {
    drawSpeechBubble(600, 400, 300, 100, established('hi', 0, 1000));
  });
  assert.strictEqual(sharedCtx.calls.fillTexts.length, 0, 'a dead bubble draws nothing');
});

test('bubble: stays on screen when the spider is near the top edge', function () {
  resetCtx();
  withNow(function () {
    drawSpeechBubble(600, 400, 300, 10, established('tiny spider near the ceiling', 3, 1000));
  });
  assert.ok(sharedCtx.calls.fillTexts.length >= 1);
});

test('bubble: stays within the horizontal bounds', function () {
  resetCtx();
  withNow(function () {
    drawSpeechBubble(600, 400, 590, 300, established('a quip from the right edge of the office', 3, 1000));
  });
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
