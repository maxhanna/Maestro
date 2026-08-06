<!-- jira: not-configured -->
# Edit-strategy routing, resolution, and validation hardening

## 1. User story
As a benchmark operator, I want Weaver to route each edit task to the required edit strategy, resolve and apply only valid targeted changes, and reject false completion, so that benchmark scores reflect the agent's actual artifact changes rather than optimistic planner or verifier output.

## 2. Acceptance criteria
- For an existing C# method insertion, Weaver selects structural insertion, inserts the requested method exactly once at the requested location, preserves existing methods, and rejects/reverts any edit that introduces syntax errors.
- For an existing-property update, Weaver derives or validates the target symbol from the step and applies a replacement to the existing property rather than adding a duplicate or asking the model to replace unrelated file content.
- For HTML edits, Weaver routes edits requiring section-aware selection to the supported HTML/DOM strategy and does not repeatedly send rejected `oldString/newString` payloads to the HTML resolver.
- If planning returns zero executable steps while required benchmark checks are unsatisfied, Weaver marks the run incomplete and does not report completion.
- For CSS, Python, YAML, JSON, TypeScript, and C# edits, Weaver validates that unrelated content, indentation, encoding, and syntax remain intact; collateral changes or no-op edits are rejected and reverted.
- A successful edit is recorded only after the intended artifact change is present and post-edit validation passes; an attempted, reverted, malformed, or no-op edit is not counted as progress.
- Failed edit attempts do not synchronize artifacts or advance the plan, and the run exposes a deterministic failure reason identifying routing, resolution, validation, or preservation failure.
- Benchmark evaluation remains server-authoritative: it scores the prepared run artifacts and cannot be made successful by client-supplied `complete`, strategy, or step metadata.
- Normal non-benchmark execution retains its existing terminal, approval, and context-review behavior; the hardened benchmark routing rules are scoped to the relevant edit strategy and do not grant additional filesystem access.
- Regression tests cover levels 6–15 or equivalent fixtures for: targeted C# replacement, C# method insertion, property update, ambiguous HTML section, multi-file propagation, CSS, TypeScript, Python, YAML, and JSON.

## 3. Gherkin scenarios

### Scenario: C# method insertion uses structural insertion
Given a prepared benchmark fixture containing `ApplyTax` and `ApplyDiscount`
And the requested change is to add `ClampToZero` after `ApplyTax`
When Weaver executes the edit step
Then it uses the structural insertion strategy
And `ClampToZero` occurs exactly once after `ApplyTax`
And both existing methods remain unchanged
And the file passes C# syntax validation

### Scenario: Invalid structural insertion is reverted
Given a structural insertion candidate introduces C# syntax errors
When Weaver validates the candidate
Then Weaver restores the pre-edit file
And the step is marked failed
And the benchmark run is incomplete
And no artifact synchronization occurs

### Scenario: Existing property update uses an exact target
Given a fixture containing one `MaxEntries` property with value `100`
And the requested change is to set `MaxEntries` to `250`
When Weaver resolves the edit
Then it targets the existing property line
And the file contains exactly one `MaxEntries` property with value `250`
And unrelated properties remain unchanged

### Scenario: Ambiguous HTML edit selects the requested section
Given an HTML fixture containing identical `Save` buttons in `general` and `users` sections
When Weaver changes the users-section button to `Save Users`
Then only the users-section button changes
And the general-section button remains `Save`
And the edit is not retried with a strategy that the HTML router has already rejected

### Scenario: Planner returns no executable steps
Given a benchmark request whose required checks are not yet satisfied
When the planner returns zero executable steps
Then Weaver marks the run incomplete
And `editsApplied` is false unless a previously applied artifact satisfies the checks
And the run is not reported as successfully complete

### Scenario: Collateral CSS changes are rejected
Given a CSS fixture with `color`, `background`, and `font-family` declarations
When the requested change modifies only `background`
And the candidate also changes or removes `color` or `font-family`
Then Weaver rejects and reverts the candidate
And the final artifact retains the unrelated declarations

### Scenario: No-op or malformed cross-language edit is rejected
Given a Python, YAML, JSON, or TypeScript fixture
When the resolver returns an unchanged file, malformed edit payload, or a replacement that fails syntax/encoding validation
Then Weaver does not count the step as successful
And restores the original artifact
And records the failure category and reason

### Scenario: Benchmark score is based on artifacts
Given a client submits completion and strategy metadata that claims success
When the server evaluates the prepared benchmark run
Then the server recomputes all acceptance checks from the run artifacts
And a missing, duplicated, collateral, or no-op change fails regardless of client metadata

### Scenario: Normal execution is unchanged
Given a non-benchmark task
When Weaver performs an edit or terminal operation
Then existing approval, context-review, and terminal behavior remains in effect
And benchmark-only routing and scoring rules are not applied

## 4. Out of scope
- Replacing the existing LLM or adding a new model provider.
- General-purpose compiler, linter, or Tree-sitter support for every language beyond the validators needed by these benchmark fixtures.
- Rewriting the planner architecture or adding parallel plan execution.
- Improving model prompts unrelated to routing, edit payload resolution, or validation failures demonstrated by levels 6–15.
- Changing benchmark acceptance criteria, weights, or expected strategies to make failed artifacts score higher.
- Adding new filesystem permissions, host terminal access, or benchmark sandbox exceptions.
- Frontend redesign of benchmark controls or score presentation.

## 5. Open questions
- **Should routing failures retry with a different strategy automatically?** Recommended default: allow at most one deterministic fallback when the requested strategy is unambiguous; otherwise fail closed and record the routing error.
- **Which syntax/encoding validators are mandatory per language?** Recommended default: use the existing C# validator, Python syntax check, JSON parser with BOM-tolerant input, and lightweight structural checks for CSS, YAML, TypeScript, and HTML; do not require full project builds for these isolated fixtures.
- **Should deterministic simple-value replacement be available outside benchmark mode?** Recommended default: yes, when the target symbol and exact current line are unambiguous; preserve normal validation and rollback behavior.
- **What is the maximum retry budget for a strategy mismatch?** Recommended default: three resolution attempts total, followed by one explicit fallback or a failed step; do not enter repeated replan loops for the same unchanged request.
- **Should UTF-8 BOMs be preserved or removed?** Recommended default: preserve the original BOM on write and make validators parse UTF-8 with or without BOM.
- **Do these changes require a feature flag or rollback switch?** Recommended default: no user-facing flag; keep benchmark strategy selection behind the existing benchmark-mode boundary and roll back via the normal deployment commit if regression tests fail.
