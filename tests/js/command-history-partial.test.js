// command-history-partial.test.js
// Regression tests for wwwroot/command-history.html — the shared partial that renders a
// completed card's executed-command list (card._steps) inside each column's "Previous
// Analysis" / "AI Analysis" section. Locks the two things the feature exists for:
// (1) the partial is actually included from every completed-card template (todo / done /
//     archived), so a card whose _steps survived the reload gets its history back, and
// (2) the output is FULL command output — never piped through limitTo or otherwise
//     truncated — with one collapsible <details> entry per step.
// Dependency-free Node test runner:  node tests/js/command-history-partial.test.js
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

const partialPath = path.join(__dirname, '../../wwwroot/command-history.html');
const kanbanPath = path.join(__dirname, '../../wwwroot/kanban.html');

const partial = fs.readFileSync(partialPath, 'utf8');
const kanban = fs.readFileSync(kanbanPath, 'utf8');

// ── Partial contents ────────────────────────────────────────────────────────

test('partial renders one collapsible <details> entry per step', () => {
  assert(/class="command-history-step"[\s\S]*ng-repeat="s in vm\.commandHistorySteps\(card\) track by \$index"/.test(partial),
    'step entries must ng-repeat over vm.commandHistorySteps(card)');
  assert(partial.indexOf('<details') !== -1 && partial.indexOf('<summary') !== -1,
    'each entry must be a collapsible <details>/<summary> pair');
});

test('partial groups steps into collapsible pipeline-phase sections', () => {
  // Mirrors the live Commands panel: a phase header renders at each phase boundary
  // (_phaseFirst) with the label + count and a collapse toggle keyed on the phase.
  assert(partial.indexOf('ng-if="s._phaseFirst"') !== -1,
    'a phase header must render at each phase boundary');
  assert(partial.indexOf('vm.toggleCommandPhase(s._phase)') !== -1,
    'the header must toggle collapse for its phase');
  assert(partial.indexOf('vm.commandPhaseCollapsed(s._phase)') !== -1,
    'the header must reflect / drive the phase collapsed state');
  assert(partial.indexOf('vm.commandPhaseLabel(s._phase)') !== -1,
    'the header must show the phase label');
  assert(partial.indexOf('s._phaseCount') !== -1,
    'the header must show the per-phase step count');
  assert(partial.indexOf('ng-if="!vm.commandPhaseCollapsed(s._phase)"') !== -1,
    'a collapsed phase must hide its steps while keeping the header');
});

test('partial renders FULL command output — never truncated', () => {
  assert(/pre class="step-output command-history-output">\{\{s\.output/.test(partial),
    'output must bind s.output directly with no filter');
  assert(partial.indexOf('limitTo') === -1,
    'command-history.html must never truncate step output with limitTo');
  assert(partial.indexOf('(no output)') !== -1,
    'a step with no output should degrade to a visible placeholder, not vanish');
});

test('partial shows the step descriptor and output size', () => {
  assert(partial.indexOf('s.description || s.path || s.command || s.url') !== -1,
    'summary must label the step by its descriptor');
  assert(partial.indexOf('s.output.length') !== -1,
    'summary must show the output size in chars');
  assert(partial.indexOf("vm.copyStepOutput(s, $event)") !== -1,
    'each entry must offer a copy-output button');
});

test('partial hides itself entirely when the card has no persisted steps', () => {
  assert(partial.indexOf('ng-if="card._steps && card._steps.length"') !== -1,
    'the outer wrapper must gate on card._steps existing and non-empty');
});

// ── Inclusion sites ─────────────────────────────────────────────────────────

test('command-history.html is included from every completed-card column template', () => {
  const includes = kanban.split('ng-include="\'command-history.html\'"').length - 1;
  assert(includes >= 3,
    `expected >= 3 ng-include sites (todo/done/archived), found ${includes}`);
  // Each include must be gated on the same non-empty _steps check so a bare
  // <details> shell never renders for a card with no history.
  const gated = kanban.split('ng-if="card._steps && card._steps.length"').length - 1;
  assert(gated >= 3, `expected >= 3 gated sections, found ${gated}`);
});

test('persist sites stamp phases via persistStepPhases so buckets survive a reload', () => {
  const src = fs.readFileSync(path.join(__dirname, '../../wwwroot/agent.js'), 'utf8');
  assert(src.indexOf('card._steps = persistStepPhases(finalSteps);') !== -1,
    'normal done path must persist phased steps — marker drifted');
  assert(src.indexOf('mvCard._steps = persistStepPhases(concAnalysis.steps);') !== -1,
    'concurrent done path must persist phased steps — marker drifted');
  assert(src.indexOf('vm.commandHistorySteps = function (card)') !== -1,
    'the partial renderer vm.commandHistorySteps is gone — marker drifted');
  assert(src.indexOf('preferPersisted: true') !== -1,
    'the partial renderer must prefer the persisted bucket — marker drifted');
});

test('included sites live in the analysis/history area of the card, not the live run', () => {
  // The include must appear after the Previous/AI analysis section begins — i.e. near
  // card.agentAnalysis, not inside the streaming (doing-column) section that is driven
  // by vm.streamingSteps.
  const analysisIdx = kanban.indexOf('card.agentAnalysis');
  const firstInclude = kanban.indexOf('ng-include="\'command-history.html\'"');
  assert(analysisIdx !== -1 && firstInclude > analysisIdx,
    'command history should render in the analysis area of a completed card');
});

// ── Summary ─────────────────────────────────────────────────────────────────
console.log(`\n# ${passed} passed, ${failed} failed`);
process.exitCode = failed ? 1 : 0;
