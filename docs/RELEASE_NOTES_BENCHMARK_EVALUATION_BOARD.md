# Weaver benchmark-evaluation-board release notes

**Release:** `benchmark-evaluation-board`  
**Release commit:** `9ffcdca`  
**Base:** synchronized with `origin/main` (`bf7da3f`)  
**Status:** ready for review and benchmark execution

## Overview

This feature branch turns Weaver's edit benchmark from a client-directed workflow into a server-authoritative, replay-resistant evaluation platform. It also hardens the agent's edit-strategy routing and resolution so benchmark tasks receive deterministic handling where a safe exact edit is possible, while normal non-benchmark execution continues to use the existing interactive workflow.

The branch includes the main branch's controller and service refactor, together with the benchmark platform, Docker isolation, artifact validation, Lemonade configuration, workflow reliability fixes, and regression coverage developed on the benchmark branch.

## Highlights

### 1. Server-authoritative benchmark lifecycle

- Added server-issued benchmark run IDs. A client cannot select an arbitrary benchmark root during evaluation.
- Added benchmark preparation that creates an isolated run workspace from the selected fixture.
- Added run-level validation for level, root, and evaluation state.
- Recomputed scores on the server from the prepared artifact rather than trusting client-submitted scores or strategy claims.
- Added replay protection: an evaluated run cannot be scored a second time.
- Added benchmark history summaries, model comparisons, baseline comparison, JSON export, score deletion, system information, and routing-calibration endpoints.
- Preserved run metadata such as model, elapsed time, level, and authoritative evaluation results.

### 2. Sandboxed benchmark command execution

Benchmark verification commands and benchmark-agent terminal commands are isolated in a disposable Docker workspace using the digest-pinned image:

```text
python:3.12-alpine@sha256:236173eb74001afe2f60862de935b74fcbd00adfca247b2c27051a70a6a39a2d
```

The runner:

- does not fall back to running benchmark commands as the Weaver process user;
- disables container networking;
- drops Linux capabilities and enables `no-new-privileges`;
- uses a read-only container root filesystem;
- applies CPU, memory, process, temporary-storage, output, and execution-time limits;
- stages files into a disposable workspace instead of bind-mounting the benchmark fixture directly;
- validates reparse points, symbolic links, hard links, and file-size changes before copying results back;
- copies verification inputs into a temporary read-only staging directory;
- permits only commands declared by the benchmark policy;
- fails closed when Docker, the pinned image, the workspace, or command policy is unavailable.

See [`docs/BENCHMARK_SANDBOX.md`](BENCHMARK_SANDBOX.md) for the operational security model.

### 3. Deterministic edit-strategy routing and resolution

The agent now performs deterministic routing before asking the model to construct an edit payload when the requested change can be identified safely. Coverage includes:

- C#;
- TypeScript and JavaScript;
- CSS;
- YAML;
- JSON;
- Python;
- HTML;
- multi-file signature edits.

Resolution improvements include:

- exact simple-property and property-value edits;
- C# method insertion;
- inference and reuse of existing properties instead of creating duplicates;
- section-aware HTML edits;
- safer signature and member insertion;
- BOM-safe file handling while keeping user-file encoding intact;
- zero-step fallback behavior for incomplete model plans;
- fatal-step and completion guards;
- validation that the expected artifact actually changed.

The deterministic path precedes LLM payload generation. If it cannot prove a safe localized edit, the workflow falls back to the model and subsequent validation rather than guessing.

### 4. Fail-closed artifact and progress validation

Successful progress now requires evidence of a valid change. The workflow rejects or does not count:

- no-op edits;
- malformed edit payloads;
- edits to the wrong file;
- collateral formatting or unrelated changes;
- reverted changes;
- fatal command or edit steps;
- incomplete cards presented as complete;
- retry attempts that do not produce a validated edit.

Benchmark-only synthetic follow-up edits are disabled, preventing unrelated method wiring or other collateral changes from being introduced merely to make a plan appear complete.

### 5. More reliable agent workflow

- Explicit approval is required before context continuation or task queuing.
- Iteration exhaustion is surfaced explicitly instead of being reported as success.
- Successful modified edit steps are recognized consistently by the progress model.
- Benchmark progress is model-aware and does not advance on attempts without edits.
- Concurrent settings saves are protected against configuration races.
- Normal terminal behavior remains unchanged outside benchmark mode.
- Benchmark context review is bypassed only for the server-controlled benchmark workflow; interactive user runs retain their normal review behavior.

### 6. Local Lemonade configuration

The branch is configured for the local Lemonade endpoint:

```text
URL:   http://localhost:8080
Model: lfm2.5-it-1.2b-FLM
```

The default frontend configuration now uses `lfm2.5-it-1.2b-FLM`, while endpoint selection remains available through Weaver's normal configuration and endpoint picker.

## Benchmark API surface

The benchmark controller exposes the following workflow:

| Endpoint | Purpose |
|---|---|
| `POST /api/benchmark/prepare/{level}` | Prepare a fixture and receive a server-issued `runId` and run root. |
| `POST /api/benchmark/evaluate` | Evaluate a prepared run using server-owned artifacts. |
| `POST /api/benchmark/save-score` | Evaluate and persist a score for a prepared run. |
| `GET /api/benchmark/scores` | List stored benchmark scores. |
| `GET /api/benchmark/summary` | Return score history summaries, optionally filtered by level or model. |
| `GET /api/benchmark/plans` | List available benchmark plans. |
| `GET /api/benchmark/info` | Return detected system information. |
| `GET /api/benchmark/system-info` | Return detected and custom benchmark system information. |
| `POST /api/benchmark/system-info` | Save custom system information. |
| `GET /api/benchmark/routing-calibration` | Run planner-routing calibration checks. |
| `GET /api/benchmark/compare/{currentId}/{baselineId}` | Compare two scores for the same level. |
| `GET /api/benchmark/export/{id}` | Download a score as JSON. |
| `DELETE /api/benchmark/scores/{id}` | Delete a stored score. |

Benchmark agent requests must carry the server-issued run ID. The server resolves that ID to the canonical sandbox; callers must not rely on arbitrary client-selected roots.

## Operational requirements

### Docker image

Pull the exact image before running benchmark command or Docker integration tests:

```bash
docker pull python:3.12-alpine@sha256:236173eb74001afe2f60862de935b74fcbd00adfca247b2c27051a70a6a39a2d
```

The runner uses `--pull=never`, so an unavailable local copy causes a controlled failure rather than an unpinned image pull or host fallback.

### Tests

The merged project test suite was run successfully:

```bash
dotnet test Weaver.csproj --no-restore -v minimal
```

Result at release commit:

```text
Passed: 655
Failed: 0
Skipped: 0
```

Docker integration coverage can be enabled with:

```bash
WEAVER_RUN_DOCKER_TESTS=1 dotnet test Weaver.csproj
```

The Docker tests require Docker and the pinned image to be available locally.

## Observed benchmark results

In the targeted benchmark regression runs used during development:

- Levels 7–15 reached 100%.
- Level 6 reached 90.3%.
- Level 6 correctness and preservation were both 100%; the remaining gap was in the overall score components outside those two dimensions.

These figures are development-run results, not a guarantee for every model, machine, or future fixture revision. Scores remain server-generated and should be reproduced using the prepared-run workflow.

## Compatibility and behavior changes

- Benchmark evaluation now requires a valid server-issued `runId`.
- Arbitrary client-selected benchmark roots are no longer trusted as the canonical evaluation source.
- A benchmark command cannot execute on the host as a fallback when Docker isolation is unavailable.
- A client-supplied strategy list is informational only; authoritative scoring is derived from the prepared artifact and benchmark policy.
- Normal, non-benchmark terminal and interactive agent execution is intentionally unchanged.
- The project now uses the merged main-branch test layout; the authoritative test command is `dotnet test Weaver.csproj`.

## Files and components of note

- `Controllers/BenchmarkController.cs` — benchmark lifecycle and score APIs.
- `Services/BenchmarkService.cs` — preparation, run resolution, authoritative evaluation, scoring, history, and replay checks.
- `Services/BenchmarkCommandRunner.cs` — policy-controlled verification command execution.
- `Services/DockerBenchmarkTerminalRunner.cs` — isolated benchmark-agent terminal execution.
- `Controllers/AgentController.cs` and partial controller files — benchmark integration, deterministic routing, edit resolution, validation, and normal agent execution.
- `Services/EditStrategyResolver.cs` — strategy classification and deterministic routing.
- `Services/ConfigFileService.cs` — configuration persistence and Lemonade defaults.
- `tests/UnitTests/` — routing, resolution, sandbox, benchmark lifecycle, encoding, and workflow regression coverage.

## Follow-up work

- Add CI/deployment documentation and pipeline configuration that explicitly installs Docker and the pinned image before benchmark tests.
- Expand the full benchmark regression matrix across all levels 6–15 on each supported model.
- Continue monitoring level 6 scoring and add targeted cases for the remaining score gap.
- Keep generated `.agents/`, `.claude/`, and `tmp/` artifacts excluded from commits.
