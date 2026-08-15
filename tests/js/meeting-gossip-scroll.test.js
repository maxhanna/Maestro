// meeting-gossip-scroll.test.js
// Unit tests for wwwroot/meeting.js's gossipScrollState — the pure helper that
// decides which chat jump button shows over the OFFICE CHAT feed: a ↓ "jump to
// latest" chip while scrolled up, an ↑ "jump to first" chip at the bottom of a
// scrollable log, and neither when the log fits without scrolling. It lives
// inside the Angular MeetingMixin factory closure, so we extract its source text
// and eval it, mirroring the meeting-ticker.test.js approach.
// Dependency-free Node test runner:  node tests/js/meeting-gossip-scroll.test.js
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

// ── Extract gossipScrollState from the live source ──────────────────────────
const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/meeting.js'), 'utf8');
const m = /function gossipScrollState\(sh, st, ch\) \{[\s\S]*?\n        \}/.exec(src);
assert(m, 'gossipScrollState not found in wwwroot/meeting.js — marker format may have drifted');
const gossipScrollState = eval('(function gossipScrollState(sh, st, ch) {' +
  m[0].replace(/^function gossipScrollState\(sh, st, ch\) \{/, '').replace(/\n        \}$/, '') + '})');

console.log('gossipScrollState jump-button tests\n');

// ── Scrolled to the bottom of a long log → ↑ jump-to-top chip ───────────────
test('at the bottom of a long log: nearBottom + canScroll', function () {
  const s = gossipScrollState(600, 552, 48);
  assert.strictEqual(s.nearBottom, true);
  assert.strictEqual(s.canScroll, true);
});

test('within the 48px stickiness window counts as at the bottom', function () {
  const s = gossipScrollState(600, 600 - 48 - 40, 48); // 40px shy of the end
  assert.strictEqual(s.nearBottom, true);
});

// ── Scrolled up → ↓ jump-to-bottom chip ─────────────────────────────────────
test('scrolled up a long log: not near the bottom', function () {
  const s = gossipScrollState(600, 0, 48);
  assert.strictEqual(s.nearBottom, false);
  assert.strictEqual(s.canScroll, true);
});

test('exactly 48px from the end counts as scrolled up (strict threshold)', function () {
  const s = gossipScrollState(600, 600 - 48 - 48, 48);
  assert.strictEqual(s.nearBottom, false);
});

// ── Log fits without scrolling → no chip at all ─────────────────────────────
test('log shorter than the feed: cannot scroll, no chip', function () {
  const s = gossipScrollState(100, 0, 200);
  assert.strictEqual(s.canScroll, false);
  assert.strictEqual(s.nearBottom, true);
});

test('exactly the feed height: canScroll needs real overflow', function () {
  const s = gossipScrollState(200, 0, 196); // within the 4px slack
  assert.strictEqual(s.canScroll, false);
  const s2 = gossipScrollState(200, 0, 180); // 20px of real overflow
  assert.strictEqual(s2.canScroll, true);
});

test('empty feed: bottom, no overflow, no chip', function () {
  const s = gossipScrollState(0, 0, 48);
  assert.strictEqual(s.nearBottom, true);
  assert.strictEqual(s.canScroll, false);
});

// ── Degenerate inputs never crash the chip state ────────────────────────────
test('negative scrollTop is treated as scrolled up, never crashes', function () {
  const s = gossipScrollState(600, -10, 48);
  assert.strictEqual(s.nearBottom, false);
  assert.strictEqual(s.canScroll, true);
});

console.log('\n' + (failed ? failed + ' FAILED, ' + passed + ' passed' : 'All ' + passed + ' tests passed') + '\n');
process.exit(failed ? 1 : 0);
