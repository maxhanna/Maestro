# Weaver Refactoring Analysis: God Classes & Code Smells

*Analysed: 2026-08-25 — branch `docs/refactoring-analysis` off `main` (`0ae23d6`)*

This report identifies the codebase's worst maintainability hotspots using **measured**
metrics (line counts, dependency counts, field counts, static-state counts), not opinion.
It mirrors the format of `docs/CODING_PIPELINE_REPORT.md` and proposes a sequenced,
low-risk decomposition roadmap. No code is changed here — this is the analysis a
refactor will be planned against.

---

## 1. Methodology

Every figure below was gathered directly from the source on `main`:

- Aggregate line counts across `AgentController` partial files (`Get-Content | Measure-Object -Line`).
- Method-signature counts per partial (`regex` against `^\s*(public|private|…)\s*…\w+\s*\(`).
- Injected-dependency and mutable-field counts from the constructor/field declarations in `AgentController.cs`.
- Static-state counts (`private static`/`ConcurrentDictionary` fields) across all partials.
- Front-end file sizes (`wwwroot/*.js`) and largest-service public-method counts.

God-class thresholds cited are the commonly-cited heuristics (e.g. LCOM, number of
collaborators > 7, methods > ~20–40, file > ~1000 lines). Weaver exceeds all of these
on its primary controller by an order of magnitude.

---

## 2. Findings (worst-first)

### 🟥 God class #1 — `AgentController` (the dominant problem)

The controller is sharded across **20 `partial` files** that share one class, one
`private` scope, and one set of instance fields. Sharding is not decomposition.

| Metric | Measured | Typical god-class threshold |
| --- | --- | --- |
| Aggregate lines (20 partials) | **29,305** | > ~1,000 |
| Partial files | 20 | (cosmetic — same class) |
| Injected + field collaborators | **25** | > ~7 |
| Mutable instance fields | **46** | > ~10 |
| Static mutable fields | **13** | > 0 in a per-request controller |
| Largest partial | `AgentController.Planning.cs` — 5,333 lines | single file > ~1,000 |

Per-partial line/method distribution (top offenders):

```text
5333  AgentController.Planning.cs        (~1,071 sig matches)
2554  AgentController.EditResolution.cs  (~553)
2441  AgentController.Pipeline.cs        (~448)
2386  AgentController.ApplyEdit.cs        (~485)
2054  AgentController.Discovery.cs       (~384)
2002  AgentController.Execution.cs       (~391)
1928  AgentController.Formatting.cs      (~453)
1806  AgentController.cs                 (~357)
1736  Services/AgentEditHeuristics.cs    (23 public methods)
1475  AgentController.StepExploration.cs (~287)
1427  AgentController.Prompts.cs         (~122)
1252  AgentController.Steps.cs           (~250)
1020  AgentController.Llm.cs             (~192)
```

**The specific code smells, with evidence:**

1. **Shredded god class, not decomposition.** The 20 `partial` files are cosmetic:
   `AgentController.Planning.cs` reads `private` fields declared in
   `AgentController.Pipeline.cs`. A real decomposition would be separate classes with
   explicit interfaces; partials here only silence the compiler's file-size warning.

2. **Violates Single Responsibility (~22 responsibilities in one type).** The class
   performs: HTTP/SSE hosting, LLM orchestration, prompt construction, plan parsing,
   edit resolution, AST/text editing, formatting, terminal commands, browser testing,
   web scraping, runtime probing, OS-output verification, repair loop, token accounting,
   complexity scoring, context-review gating, steering, changelog, and benchmark wiring.

3. **Excessive collaborators (25).** `IHttpClientFactory`, `IConfiguration`,
   `IWebHostEnvironment`, `TerminalService`, `FileHintsManager`, `ConfigFileService`,
   `EmailService`, `BoardDataService`, `EditKnowledgeService`, `PushNotificationService`,
   `DatabaseService`, `AiServerDiscoveryService`, plus `_scraperService`,
   `_runtimeProbe`, `_cfgCache`, etc. Tests must build a 12-field harness via
   `RuntimeHelpers.GetUninitializedObject` — observed firsthand while wiring
   `StrictVerifierTests`.

4. **Mutable static state in a controller (13 static fields).** `_pendingQuestions`,
   `_pendingContextReviews`, `_cancelledSteps`, `_liveSteer`, `_stepThinkingStore`,
   `_complexityScores`, `_atomicStepEstimates`, `_infiniteTimeout`,
   `_nextConnectivityCheck`. Static/`ConcurrentDictionary` state in an ASP.NET controller
   is a cross-request correctness hazard and a testability hazard (every test resets
   statics via reflection — e.g. `SetStaticField("_nextConnectivityCheck", …)`).

5. **Run-scoped state living on a singleton-shaped controller.** `_gracefulStop`,
   `_requirementChecklist`, `_stepLlmPromptTokens`, `_runLlmPromptTokens`,
   `_skeletonContextChars`, `_taskPromptContextChars` are per-run state sitting as
   instance fields. The code comments "reset at run start in StepResolutionPipeline" —
   that reset is the smell: the state should be a `RunContext` object passed by
   reference, not fields wiped each run.

**Secondary smells evidenced by the god class:**

- **Reflection-based test coupling.** 7 test files reach into `AgentController` via
  `GetMethod("Orchestrate"/"PostExecuteVerify")`. Adding one parameter required updating
  all 7 (a `TargetParameterCountException` regression hit during the StrictVerifier work).
  This coupling exists *because* the god class can't be constructed normally.
- **Controller performs I/O directly.** Reads files, runs processes, launches browsers,
  scrapes the web — inline. A controller should orchestrate, not perform.
- **Hand-assembled config save drops fields.** While wiring StrictVerifier UI I found
  `prByDefault` was never explicitly written in `saveSettings` — a toggle persisted only
  incidentally. Smell: the settings save path assembles a giant cfg object by hand and
  forgets fields.

### 🟧 God class #2 — `wwwroot/meeting.js` (7,756 lines)

The single largest file in the repo. For contrast `agent.js` (next largest) is 3,165.
A 7.7k-line vanilla-JS file bundling the entire meeting/ticker/spider feature surface
with no module boundary is a god module by size alone. Front-end, no compile risk, but
highest DX-pain-per-line (no navigability, no encapsulation). Candidate split:
`meeting-ui.js`, `meeting-ticker.js`, `meeting-spider.js`, `meeting-state.js`.

### 🟧 God class #3 — `Services/AgentEditHeuristics.cs` (1,736 lines, 23 public methods)

Below the controller but over threshold. A "heuristics" bag with 23 public entry points
strongly suggests multiple heuristic families (rename, CSS, template-binding,
hallucinated-property, brace-matching…) lumped into one service. Likely cohesive *per
family* but not cohesive *as a whole*. Candidate: one class per family behind a common
`IEditHeuristic` interface.

---

## 3. Sequenced Decomposition Roadmap

Ordered lowest-risk-first. Each step keeps `dotnet test` + `node tests/js/run-all.js`
green and is one reviewable commit (the standard held for the StrictVerifier branch).
**No behavior change** — pure extract/move.

### Phase 0 — Safe quick wins (≈1–2 days each)

- [ ] **Extract `AgentRunContext`.** Move the 12 run-scoped mutable fields
  (`_gracefulStop`, `_requirementChecklist`, `_stepLlm*`, `_runLlm*`,
  `_skeletonContextChars`, `_taskPromptContextChars`, `_discoverySteps`) into a
  `RunContext` record passed through `StepResolutionPipeline`/`PostExecuteVerify`.
  Kills the "reset on run start" smell; makes runs trivially testable. No behavior change.
- [ ] **Split `AgentEditHeuristics` by family** behind an `IEditHeuristic` interface
  (one class per family). Small, safe, mechanical.

### Phase 1 — Extract services from the controller partials (the real fix)

Each partial's private methods become a service the controller delegates to. The
`private` cross-partial access is replaced with explicit service interfaces — which
is exactly what retires the reflection-test coupling.

- [ ] `AgentController.Llm.cs` → `LlmClient` (natural seam; already wraps `IHttpClientFactory`)
- [ ] `AgentController.Terminal.cs` → `CommandRunner`
- [ ] `AgentController.BrowserTest.cs` → `BrowserTestRunner`
- [ ] `AgentController.Formatting.cs` → `CodeFormatter`
- [ ] `AgentController.Pipeline.cs` → `AgentPipeline` (the verify/repair loop)
- [ ] `AgentController.EditResolution.cs` → `EditResolver`
- [ ] `AgentController.Planning.cs` → `AgentPlanner` (the 5,333-line monster — last)

After Phase 1, `AgentController` shrinks to ~HTTP + SSE wiring that delegates; the
controller's dependency count drops from 25 to the handful of services it orchestrates.

### Phase 2 — Kill the static state (correctness)

- [ ] Move the 13 `static`/`ConcurrentDictionary` fields into a scoped `RunRegistry`
  (per-run) or `CardSessionStore` (per-card), injected. Fixes the cross-request hazard
  and removes the test `SetStaticField` dance.

### Phase 3 — Front-end

- [ ] **Split `meeting.js`** into `meeting-ui.js`, `meeting-ticker.js`,
  `meeting-spider.js`, `meeting-state.js`. No compile risk, big DX win.

---

## 4. Non-goals (deliberately out of scope for this analysis)

- No behavior changes, no API changes, no config-schema changes.
- No new dependencies.
- No changes to the public HTTP surface (`api/agent/*`, `api/config/*`, etc.).
- Does not touch `feature/test-benchmark-gates` (in flight).

## 5. Next steps

1. Pick Phase 0's `AgentRunContext` extract as the first concrete commit — it's the
   safest, highest-leverage move and unblocks cleaner tests for everything that follows.
2. Re-measure the controller aggregate after each phase to confirm the line/dependency
   counts actually drop (the metric is the proof the refactor worked).
3. Keep the "one extract per commit, tests green" discipline that held for the
   StrictVerifier + CI work.
