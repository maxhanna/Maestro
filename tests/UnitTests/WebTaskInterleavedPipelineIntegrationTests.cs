using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Integration coverage for the OS-filesystem web-task path through the REAL interleaved
/// pipeline (Orchestrate → discovery → checklist → incremental plan → execute → verify),
/// driven with a SCRIPTED fake LLM and a fake HTTP client that answers the web search/fetch
/// GETs. This is the regression test for the "Search the web for an interesting and relevant
/// AI article and write the data into a text file on my desktop" class of run:
///
///   1. The planner's step 1 is a _web_search step, which EXECUTES against the fake
///      DuckDuckGo endpoint and its output is harvested into the discovery context
///      (### WEB RESULTS [query] ###).
///   2. The planner's step 2 must be a _web_fetch of a concrete URL FROM those results —
///      NOT an invented edit to a repo file (the pre-fix behavior: the model drifted into
///      writing a Selenium/Python scraper or an application-code edit instead of using the
///      tool surface).
///   3. The step-2 planner turn must actually SEE the harvested results (the injection
///      feature), asserted on the recorded step-2 user prompt.
///   4. When the planner then declares planComplete WITHOUT writing the demanded file (the
///      observed "Now I need only create the final output file" hallucination), the
///      OS-output gate auto-dumps the harvested web results to the target path — the run
///      genuinely completes with the file on disk, zero repo edits, and every LLM call the
///      script accounts for (Unmatched must be empty).
///
/// A second trace covers the steering path: a run that demands an OS output file but has no
/// web content to dump; the premature planComplete is REJECTED with feedback and the planner
/// is steered to plan the _command write itself.
/// </summary>
public class WebTaskInterleavedPipelineIntegrationTests : IDisposable
{
    private const string SearchQuery = "AI research breakthroughs latest";
    private const string LinksRel = "src/links.ts"; // fixture the cross-tool edit arm writes to

    private readonly string _base;
    private readonly string _projectRoot;
    private readonly string _dumpTarget;   // test 1: the file the auto-dump must create
    private readonly string _steerTarget;  // test 2: the file the steered _command must create
    private readonly string _neverWrittenTarget; // test 3: the file NO run may ever create
    private readonly DatabaseService _db;
    private readonly BoardDataService _boardData;
    private readonly WebTaskScriptedClientFactory _clientFactory;

    public WebTaskInterleavedPipelineIntegrationTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "weaver_webtask_" + Guid.NewGuid().ToString("N"));
        _projectRoot = Path.Combine(_base, "proj");
        Directory.CreateDirectory(_projectRoot);
        Directory.CreateDirectory(Path.Combine(_base, "data"));
        Directory.CreateDirectory(Path.Combine(_base, "dump"));
        Directory.CreateDirectory(Path.Combine(_base, "dump2"));
        Directory.CreateDirectory(Path.Combine(_base, "dump3"));
        _dumpTarget = Path.Combine(_base, "dump", "ai_article_data.txt");
        _steerTarget = Path.Combine(_base, "dump2", "report.txt");
        _neverWrittenTarget = Path.Combine(_base, "dump3", "out.txt");

        _db = new DatabaseService(
            Path.Combine(_base, "data", "weaver.db"),
            Path.Combine(_base, "data"),
            Path.Combine(_base, "data", "weaverconfig.json"));
        _boardData = new BoardDataService(_db, NullLogger<BoardDataService>.Instance);
        _clientFactory = new WebTaskScriptedClientFactory(PlannerMode.WebChain, _steerTarget);
    }

    public void Dispose()
    {
        _clientFactory.Dispose();
        try { Directory.Delete(_base, true); } catch { }
    }

    [Fact]
    public async Task WebTask_PlanComplete_WithoutWritingFile_AutoDumpsHarvestedResults()
    {
        var controller = BuildController();
        // The between-steps WEB assessment (new) runs after each successful web step and at
        // the plan-complete recheck. Script it content-aware: incomplete until the demanded
        // dump file exists — so the search/fetch steps keep planning, and only after the
        // auto-dump lands does the recheck accept the planComplete.
        _clientFactory.WebAssessComplete = () => File.Exists(_dumpTarget);
        // The target is an absolute temp path (quoted so spaces are safe) — the auto-dump
        // must write HERE, never to the real desktop.
        var prompt = $"Search the web for an interesting and relevant AI article and write the data into a text file at \"{_dumpTarget}\".";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The run finished complete AND the demanded file exists with the harvested data.
        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}");
        Assert.True(File.Exists(_dumpTarget), $"auto-dump should have written {_dumpTarget}");
        var dumped = File.ReadAllText(_dumpTarget, Encoding.UTF8);
        Assert.Contains("AlphaFold 3", dumped);          // the harvested DuckDuckGo result
        Assert.Contains("### WEB RESULTS", dumped);      // structured section per result
        Assert.Contains("Task: Search the web", dumped); // the header

        // Step 1 actually EXECUTED a web search and its output was harvested: the result
        // carries the full DuckDuckGo-shaped output including the article URLs.
        var searchResult = allSteps.OfType<Dictionary<string, object?>>()
            .Single(r => r.GetValueOrDefault("type")?.ToString() == "_web_search");
        Assert.Equal("done", searchResult.GetValueOrDefault("status")?.ToString());
        var searchOutput = searchResult.GetValueOrDefault("output")?.ToString() ?? "";
        Assert.Contains("https://example.com/alphafold3", searchOutput);
        Assert.Contains("https://example.com/llm-benchmarks", searchOutput);

        // The step-2 planner turn SAW the harvested results — the injection feature. The
        // recorded user prompt must contain the WEB RESULTS section AND the nudge that
        // steers step 2 to _web_fetch a concrete URL from it.
        var step2Prompt = Assert.Single(_clientFactory.Step2PlannerPrompts);
        Assert.Contains("### WEB RESULTS", step2Prompt);
        Assert.Contains("https://example.com/alphafold3", step2Prompt);
        Assert.Contains("### WEB RESULTS ARE IN CONTEXT ###", step2Prompt);
        Assert.Contains("_web_fetch step with THAT exact URL from the results", step2Prompt);

        // The plan is _web_search → _web_fetch → the auto-dumped _command write. The dump
        // step was added deterministically by the OS-output gate, not planned by the LLM.
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search", "_web_fetch", "_command" }, plan!.Plan.Select(s => s.File).ToArray());
        Assert.Equal("AI research breakthroughs latest", plan.Plan[0].Change);
        Assert.Equal("https://example.com/alphafold3", plan.Plan[1].Change);
        Assert.Contains("Auto-dump web results", plan.Plan[2].Change);

        // The auto-dump is recorded as an executed command step result.
        var dumpResult = allSteps.OfType<Dictionary<string, object?>>()
            .Single(r => r.GetValueOrDefault("type")?.ToString() == "command");
        Assert.Equal("done", dumpResult.GetValueOrDefault("status")?.ToString());
        Assert.Equal(_dumpTarget, dumpResult.GetValueOrDefault("path")?.ToString());

        // ZERO repo file edits were applied — the core "not an invented edit" assertion. A
        // pre-fix run would have planned an edit to a repo file (or a _create_file for a
        // scraper script) and applied it.
        var editResults = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("type")?.ToString() is "edit" or "create" &&
                        r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
            .ToList();
        Assert.Empty(editResults);

        // Every LLM call the pipeline made was one the script accounted for.
        Assert.Empty(_clientFactory.Unmatched);

        // Sanity: the scripted LLM saw the expected call kinds (checklist + 3 planner turns;
        // no verify/cohesion/post-verify — the dump is deterministic and no repo edits
        // exist to verify).
        Assert.Contains("checklist", _clientFactory.Calls);
        Assert.Equal(3, _clientFactory.Calls.Count(c => c == "planner-step"));
        Assert.DoesNotContain("verify", _clientFactory.Calls);
        // The WEB-ONLY completion assessment RAN: after the search step, after the fetch
        // step, at the plan-complete recheck, and at the final quality check — the gap this
        // feature closes (IsLastEditVerifiedComplete never fires without an edit).
        Assert.True(_clientFactory.Calls.Count(c => c == "web-assess") >= 3,
            $"the web-only completion assessment should have run after each web step and at the recheck — calls=[{string.Join(",", _clientFactory.Calls)}]");
    }

    // ── Cross-tool state carry (the multi-turn eval extended to web values) ──────────────
    // Turn 1's state is a VALUE from a web search result (the article URL). Turn 2's tool
    // call must consume that EXACT value — a _web_fetch of the same URL (fetch arm) or an
    // edit that embeds it (edit arm). The carry is judged at EXECUTION: the executed fetch
    // hit exactly the harvested URL (never an invented domain), and the landed edit embeds
    // exactly the harvested URL — with a content-aware ground-truth assessor keeping the
    // run incomplete until the carried value actually lands.

    [Fact]
    public async Task WebTask_CrossToolCarry_SearchResultUrlFeedsTurn2Fetch_ExecutesExactUrl()
    {
        _clientFactory.Mode = PlannerMode.WebChain;
        // Computed ground truth: the task is satisfied only once a fetch EXECUTED against
        // the exact URL harvested from the search results — not merely planned, executed.
        _clientFactory.WebAssessComplete = () =>
            _clientFactory.FetchedUrls.Any(u => u.Contains("https://example.com/alphafold3", StringComparison.Ordinal));
        var controller = BuildController();
        // No OS-output demand — a pure search→fetch chain, so the OS-output gate adds no
        // auto-dump step and the plan is exactly the two web steps.
        var prompt = "Search the web for an interesting AI article, then fetch the best article's URL from the results";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        // Turn-1 state (the harvested search results) reached the turn-2 planner prompt,
        // carrying the exact URL the fetch must use.
        var step2Prompt = Assert.Single(_clientFactory.Step2PlannerPrompts);
        Assert.Contains("### WEB RESULTS", step2Prompt);
        Assert.Contains("https://example.com/alphafold3", step2Prompt);
        // Turn 2's tool call consumed the EXACT value from turn 1: the planned step, the
        // executed step result, and the actual HTTP GET all carry the same URL.
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search", "_web_fetch" }, plan!.Plan.Select(s => s.File).ToArray());
        Assert.Equal("https://example.com/alphafold3", plan.Plan[1].Change);
        var doneFetch = Assert.Single(allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("type")?.ToString() == "_web_fetch" &&
                        r.GetValueOrDefault("status")?.ToString() == "done"));
        Assert.Equal("https://example.com/alphafold3", doneFetch.GetValueOrDefault("url")?.ToString());
        Assert.Contains(_clientFactory.FetchedUrls, u => u.Contains("https://example.com/alphafold3", StringComparison.Ordinal));
        // The invented example-domain pattern was never fetched — only the carried URL.
        Assert.DoesNotContain(_clientFactory.FetchedUrls, u => u.Contains("www.example.com", StringComparison.Ordinal));
        // ZERO repo edits — the fetch consumed the value; nothing invented.
        var editResults = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("type")?.ToString() is "edit" or "create" &&
                        r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
            .ToList();
        Assert.Empty(editResults);
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_CrossToolCarry_SearchResultUrlFeedsTurn2Edit_EmbeddedExactUrl()
    {
        _clientFactory.Mode = PlannerMode.SearchThenEdit;
        var linksPath = Path.Combine(_projectRoot, LinksRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(linksPath)!);
        File.WriteAllText(linksPath, "export const links: string[] = [];", Encoding.UTF8);
        // Computed ground truth: complete only once the landed edit embeds the EXACT URL
        // from the search results — an invented/wrong URL must never satisfy it.
        _clientFactory.WebAssessComplete = () => File.Exists(linksPath) &&
            File.ReadAllText(linksPath, Encoding.UTF8).Contains("https://example.com/alphafold3", StringComparison.Ordinal);
        var controller = BuildController();
        // "find … online" is an EXPLICIT web command (TaskExplicitlyCommandsWeb), so the
        // web-step gate lets the search through without a classifier call — the search then
        // produces the value (URL) turn 2's edit must consume.
        var prompt = "Find a recent AI article online and add a link to the best article into src/links.ts";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        // Turn-1 state (the harvested search results) reached the turn-2 planner prompt.
        var step2Prompt = Assert.Single(_clientFactory.Step2PlannerPrompts);
        Assert.Contains("### WEB RESULTS", step2Prompt);
        Assert.Contains("https://example.com/alphafold3", step2Prompt);
        // Turn 2's tool call was an EDIT whose newString embeds the EXACT harvested URL —
        // cross-tool carry (search tool → edit tool) — and it landed on disk.
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search", LinksRel }, plan!.Plan.Select(s => s.File).ToArray());
        Assert.Contains("https://example.com/alphafold3", plan.Plan[1].NewString);
        var landed = File.ReadAllText(linksPath, Encoding.UTF8);
        Assert.Contains("export const links: string[] = ['https://example.com/alphafold3'];", landed);
        // No fetch was needed — the edit consumed the search value directly (no _web_fetch
        // step executed, no example.com GET beyond the search).
        Assert.DoesNotContain(allSteps.OfType<Dictionary<string, object?>>(),
            r => r.GetValueOrDefault("type")?.ToString() == "_web_fetch");
        Assert.DoesNotContain(_clientFactory.FetchedUrls, u => u.Contains("example.com/alphafold3", StringComparison.Ordinal));
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── Web-only completion assessment (no OS-output demand) ─────────────────────────────
    // The between-steps whole-task assessment previously NEVER ran for web-only runs:
    // IsLastEditVerifiedComplete requires an edit result, so a run whose last step was a
    // successful _web_search/_web_fetch skipped the assessment entirely. Now a successful
    // web step opens the same gate — a pure "search the web" task completes THROUGH the
    // assessment right after the search, instead of waiting for the planner to declare
    // planComplete on its own.

    [Fact]
    public async Task WebTask_SearchOnly_NoOutputDemand_AssessmentCompletesAfterSearch()
    {
        _clientFactory.Mode = PlannerMode.SearchOnly;
        _clientFactory.WebAssessComplete = () => true; // the gathered results satisfy the task
        var controller = BuildController();
        var prompt = "Search the web for recent AI breakthroughs and report what you find";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The run completed — and it completed THROUGH the between-steps web assessment: the
        // planner produced exactly ONE step (the search) and never had to declare completion
        // itself (no second planner turn, no Step 2).
        Assert.True(complete, $"run should complete via the web assessment — calls=[{string.Join(",", _clientFactory.Calls)}]; plan={plan?.Summary}");
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search" }, plan!.Plan.Select(s => s.File).ToArray());
        Assert.Equal(1, _clientFactory.Calls.Count(c => c == "planner-step"));
        Assert.Empty(_clientFactory.Step2PlannerPrompts);
        // The web-only assessment ran — the loop-time assessment after the search (the final
        // whole-task quality check is skipped because the pipeline already recorded a
        // verified_complete step for the planCompleteDeclared path).
        Assert.True(_clientFactory.Calls.Count(c => c == "web-assess") >= 1,
            $"the web-only completion assessment should have run after the search — calls=[{string.Join(",", _clientFactory.Calls)}]");
        var searchResult = allSteps.OfType<Dictionary<string, object?>>()
            .Single(r => r.GetValueOrDefault("type")?.ToString() == "_web_search");
        Assert.Equal("done", searchResult.GetValueOrDefault("status")?.ToString());
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_SearchOnly_AssessmentSaysIncomplete_RejectsPlanCompleteWithFeedback()
    {
        // The incomplete arm of the web gate: the assessment says the search alone does NOT
        // satisfy the task. The planner then declares planComplete (the scripted default) —
        // the incomplete-assessment gate REJECTS it with the assessment's reason as feedback,
        // forcing further planner turns instead of silently completing with unmet
        // requirements. When the scripted planner keeps re-declaring completion, the regen
        // budget ends the loop and the pre-existing repair fallback ("replanner proposed no
        // further steps") closes the run — the NEW behavior being locked is the rejection +
        // feedback loop, not the repair fallback.
        _clientFactory.Mode = PlannerMode.SearchOnly;
        _clientFactory.WebAssessComplete = () => false;
        var controller = BuildController();
        var prompt = "Search the web for recent AI breakthroughs and verify each result is a real published paper";

        var (_, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The assessment ran repeatedly and the plan-complete claims were rejected: multiple
        // planner turns (1 search + ≥2 rejected completions) and ≥3 assessment calls (after
        // the search + each plan-complete recheck).
        Assert.True(_clientFactory.Calls.Count(c => c == "planner-step") >= 3,
            $"incomplete assessments should have forced repeated planner turns — calls=[{string.Join(",", _clientFactory.Calls)}]; plan={plan?.Summary}");
        Assert.True(_clientFactory.Calls.Count(c => c == "web-assess") >= 3,
            $"the assessment should have re-run at each plan-complete recheck — calls=[{string.Join(",", _clientFactory.Calls)}]");
        // The rejection feedback (the assessment's unmet-requirement reason) reached a LATER
        // planner turn — after the first planComplete was refused, the next prompt carries the
        // reason WHY the completion claim was rejected.
        Assert.True(_clientFactory.PlannerUserPrompts.Count >= 3,
            $"expected ≥3 planner turns (search + rejected completions) — got {_clientFactory.PlannerUserPrompts.Count}");
        Assert.True(_clientFactory.PlannerUserPrompts.Skip(1).Any(p => p.Contains("NOT complete") && p.Contains("completion assessment")),
            $"the rejection feedback should have reached a later planner prompt:\n{string.Join("\n---\n", _clientFactory.PlannerUserPrompts.Skip(1))}");
        // The run still ENDS (via the repair fallback: the replanner proposed no further
        // steps) — no hang, no churn.
        Assert.True(complete, $"run should close via the repair fallback — calls=[{string.Join(",", _clientFactory.Calls)}]");
        Assert.Empty(_clientFactory.Unmatched);
    }

    [Fact]
    public async Task WebTask_NoWebContent_PrematurePlanComplete_SteeredToCommandWrite()
    {
        _clientFactory.Mode = PlannerMode.SteeringWrite;
        var controller = BuildController();
        var prompt = $"write the data into a text file at \"{_steerTarget}\"";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The premature planComplete (no file, no content to auto-dump) was rejected and the
        // planner was steered to plan the _command write; the run completes with the file.
        Assert.True(complete, $"pipeline should complete after the steered write — plan summary: {plan?.Summary}");
        Assert.True(File.Exists(_steerTarget), $"the steered _command should have written {_steerTarget}");
        var actualContent = File.ReadAllText(_steerTarget, Encoding.UTF8);
        Assert.True(actualContent.Contains("repair data"),
            $"steered file should contain the Set-Content value — got: [{actualContent}]");

        // The plan is the single steered _command write.
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_command" }, plan!.Plan.Select(s => s.File).ToArray());
        Assert.Contains("Set-Content", plan.Plan[0].Change);

        // The _command executed for real and its result landed in the run.
        var cmdResult = allSteps.OfType<Dictionary<string, object?>>()
            .Single(r => r.GetValueOrDefault("type")?.ToString() == "command");
        Assert.Equal("done", cmdResult.GetValueOrDefault("status")?.ToString());

        // Planner call 1 declared complete prematurely (rejected by the OS-output gate),
        // call 2 planned the write, call 3 completed.
        Assert.Equal(3, _clientFactory.Calls.Count(c => c == "planner-step"));

        // Every LLM call was accounted for.
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── Failed web fetch: retried with feedback, never a halting crash ───────────────────
    // The exact field failure: the planner invented "www.example.com/latest-ai-breakthrough"
    // (copying the example-domain pattern from the tool-use example) instead of a real URL
    // from the search results, the fetch failed, and the interleaved loop HALTED — dropping
    // the run into the repair loop, whose replanner (with no web results and no OS-write
    // guidance) invented a Node fs writeArticleToFile() in an Angular service. Now a failed
    // _web_fetch is a retryable step: the loop removes it, feeds the error back to the
    // planner, and lets it retry with a REAL URL from the harvested results.

    [Fact]
    public async Task WebTask_InventedUrlFetchFails_LoopRetriesWithRealUrlAndCompletes()
    {
        _clientFactory.Mode = PlannerMode.FetchRetry;
        // Incomplete until the demanded dump file exists — the between-steps web assessment
        // keeps the chain planning until the OS-output gate auto-dumps the fetched data.
        _clientFactory.WebAssessComplete = () => File.Exists(_dumpTarget);
        var controller = BuildController();
        var prompt = $"Search the web for an interesting and relevant AI article and write the data into a text file at \"{_dumpTarget}\".";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The run completed AND the file was written (the search data auto-dumped).
        Assert.True(complete, $"pipeline should complete after the fetch retry — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        Assert.True(File.Exists(_dumpTarget), $"auto-dump should have written {_dumpTarget}");
        Assert.Contains("AlphaFold 3", File.ReadAllText(_dumpTarget, Encoding.UTF8));

        // The invented-URL fetch EXECUTED and FAILED (status error with the reason recorded).
        var failedFetch = allSteps.OfType<Dictionary<string, object?>>()
            .Single(r => r.GetValueOrDefault("type")?.ToString() == "_web_fetch" &&
                         r.GetValueOrDefault("status")?.ToString() == "error");
        Assert.Equal("https://www.example.com/latest-ai-breakthrough", failedFetch.GetValueOrDefault("url")?.ToString());
        Assert.Contains("invented URL", failedFetch.GetValueOrDefault("error")?.ToString() ?? "");

        // The successful retry used the REAL URL from the search results — and the FINAL plan
        // carries it (the failed step was removed from planSoFar, never repeated).
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search", "_web_fetch", "_command" }, plan!.Plan.Select(s => s.File).ToArray());
        Assert.Equal("https://example.com/alphafold3", plan.Plan[1].Change);
        var successfulFetch = allSteps.OfType<Dictionary<string, object?>>()
            .Single(r => r.GetValueOrDefault("type")?.ToString() == "_web_fetch" &&
                         r.GetValueOrDefault("status")?.ToString() == "done");
        Assert.Equal("https://example.com/alphafold3", successfulFetch.GetValueOrDefault("url")?.ToString());

        // The failure feedback ("Do NOT retry the same URL… use a REAL URL… never invent")
        // reached a LATER planner turn — the planner saw why the fetch failed.
        Assert.True(_clientFactory.PlannerUserPrompts.Skip(1).Any(p =>
                p.Contains("failed", StringComparison.OrdinalIgnoreCase) &&
                p.Contains("www.example.com", StringComparison.Ordinal) &&
                p.Contains("REAL URL", StringComparison.Ordinal)),
            $"the fetch-failure feedback should have reached a later planner prompt:\n{string.Join("\n---\n", _clientFactory.PlannerUserPrompts.Skip(1))}");

        // ZERO repo edits — the run never invented application code to "write" the file.
        var editResults = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("type")?.ToString() is "edit" or "create" &&
                        r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
            .ToList();
        Assert.Empty(editResults);
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── Repair loop: deterministic auto-dump before the replanner ─────────────────────────
    // When the fetch retry budget is exhausted (a persistently broken URL), the run halts
    // into post-execution verification, which reports the missing OS output file. The repair
    // loop must then dump the harvested search results deterministically — WITHOUT ever
    // calling the replanner, which (pre-fix) invented an Angular-service write.

    [Fact]
    public async Task WebTask_FetchAlwaysFails_RepairLoopAutoDumpsWithoutReplanner()
    {
        _clientFactory.Mode = PlannerMode.FetchAlwaysFails;
        _clientFactory.WebAssessComplete = () => File.Exists(_dumpTarget);
        var controller = BuildController();
        var prompt = $"Search the web for an interesting and relevant AI article and write the data into a text file at \"{_dumpTarget}\".";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The run completed and the demanded file exists — the search results were dumped.
        Assert.True(complete, $"pipeline should complete via the repair-loop auto-dump — plan summary: {plan?.Summary}; calls=[{string.Join(",", _clientFactory.Calls)}]; unmatched={string.Join(";", _clientFactory.Unmatched)}");
        Assert.True(File.Exists(_dumpTarget), $"repair auto-dump should have written {_dumpTarget}");
        Assert.Contains("AlphaFold 3", File.ReadAllText(_dumpTarget, Encoding.UTF8));

        // The replanner was NEVER called — the deterministic dump closed the loop before the
        // LLM repair step could invent application code. This is the core regression: a
        // pre-fix run ended with an invented Angular-service edit, an INCOMPLETE verdict, and
        // a churn-breaker trip.
        Assert.Empty(_clientFactory.RepairUserPrompts);

        // ZERO repo edits.
        var editResults = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("type")?.ToString() is "edit" or "create" &&
                        r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
            .ToList();
        Assert.Empty(editResults);

        // The failed fetch attempts are recorded with their error reasons.
        var failedFetches = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("type")?.ToString() == "_web_fetch" &&
                        r.GetValueOrDefault("status")?.ToString() == "error")
            .ToList();
        Assert.True(failedFetches.Count >= 2, $"expected the retry budget to be exhausted with ≥2 failed fetches — got {failedFetches.Count}");
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── Boarddata fixture: the demanded OS output is NEVER written ────────────────────────
    // Mirrors the RejectedPlanStepPersistenceTests harness (a card in the board fixture, a
    // scripted client, the controller driven through the real Orchestrate path) and locks the
    // NEGATIVE of the OS-output guarantee: when a run ends without creating the demanded file
    // and has nothing to auto-dump, the run must STAY INCOMPLETE — the repair loop may attempt
    // but can never falsely complete it (a repair that produces no write step cannot flip the
    // verdict, and the missing-file CONFIRMED issue is persisted on the card).

    [Fact]
    public async Task WebTask_OsFileNeverWritten_RepairCannotFalselyComplete_StaysIncomplete()
    {
        const string cardId = "os-never-written";
        await _boardData.SaveRawAsync(BoardWithCard(cardId, "doing"));
        _clientFactory.Mode = PlannerMode.NeverWrites;
        var controller = BuildController();
        // Absolute temp path (quoted so it parses as a path) — the demanded file that NO run
        // may ever create. Nothing is ever written to the real desktop.
        var prompt = $"write the data into a text file at \"{_neverWrittenTarget}\"";
        Assert.True(AgentOsOutputVerifier.TryGetOsFileOutputDemand(prompt, out _),
            $"the test prompt must demand an OS output file: {prompt}");

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt, cardId);

        // 1) The run STAYS INCOMPLETE — the headline guarantee.
        Assert.False(complete,
            $"a run that never wrote the demanded OS file must not complete — calls=[{string.Join(",", _clientFactory.Calls)}]; plan={plan?.Summary}");
        // 2) The file was genuinely never created.
        Assert.False(File.Exists(_neverWrittenTarget), $"nothing may write {_neverWrittenTarget}");
        // 3) No repo edit was applied as a substitute (the "invented edit" failure mode).
        var editResults = allSteps.OfType<Dictionary<string, object?>>()
            .Where(r => r.GetValueOrDefault("type")?.ToString() is "edit" or "create" &&
                        r.GetValueOrDefault("status")?.ToString() is "done" or "modified" or "created")
            .ToList();
        Assert.Empty(editResults);
        // 4) The repair loop RAN (the plan-fixer was invoked) and was steered at the missing
        //    file — yet its empty plan could not rescue the run into a false completion.
        Assert.NotEmpty(_clientFactory.RepairUserPrompts);
        Assert.Contains("text file", _clientFactory.RepairUserPrompts[0]);
        Assert.Contains(_neverWrittenTarget, _clientFactory.RepairUserPrompts[0]);
        // 5) The card persists the incompleteness: the CONFIRMED OS-output issue is on the
        //    card as ground truth, and the final verification verdict is complete=false.
        var raw = await _boardData.LoadRawAsync();
        var gt = ReadCardGroundTruth(raw, cardId);
        Assert.NotNull(gt);
        Assert.Contains(gt!, e => e.Contains("never created", StringComparison.Ordinal) &&
                                  e.Contains(_neverWrittenTarget, StringComparison.Ordinal));
        var (vComplete, vReason) = ReadCardVerification(raw, cardId);
        Assert.False(vComplete, $"card verification must record complete=false — reason: {vReason}");
        // 6) Every LLM call the run made was accounted for by the script.
        Assert.Empty(_clientFactory.Unmatched);
    }

    private static string BoardWithCard(string cardId, string column)
    {
        var board = new Dictionary<string, object?>
        {
            ["todo"] = new List<object>(),
            ["doing"] = new List<object>(),
            ["done"] = new List<object>(),
            ["archived"] = new List<object>(),
            ["selfImproving"] = new List<object>()
        };
        board[column] = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["id"] = cardId,
                ["text"] = "task",
                ["filePath"] = "C:/x"
            }
        };
        return JsonSerializer.Serialize(board);
    }

    private static List<string>? ReadCardGroundTruth(string? raw, string cardId)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        using var doc = JsonDocument.Parse(raw);
        foreach (var col in new[] { "todo", "doing", "done", "selfImproving" })
        {
            if (!doc.RootElement.TryGetProperty(col, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var card in arr.EnumerateArray())
            {
                if (!card.TryGetProperty("id", out var id) || id.GetString() != cardId) continue;
                if (card.TryGetProperty("_groundTruth", out var gt) && gt.ValueKind == JsonValueKind.Array)
                    return gt.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                return new List<string>(); // card found, no ground truth
            }
        }
        return null;
    }

    private static (bool complete, string? reason) ReadCardVerification(string? raw, string cardId)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (false, null);
        using var doc = JsonDocument.Parse(raw);
        foreach (var col in new[] { "todo", "doing", "done", "selfImproving" })
        {
            if (!doc.RootElement.TryGetProperty(col, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var card in arr.EnumerateArray())
            {
                if (!card.TryGetProperty("id", out var id) || id.GetString() != cardId) continue;
                if (!card.TryGetProperty("_verification", out var v)) return (false, null);
                return (v.TryGetProperty("complete", out var c) && c.GetBoolean(),
                        v.TryGetProperty("reason", out var r) ? r.GetString() : null);
            }
        }
        return (false, null);
    }

    private async Task<(List<object> allSteps, AgentPlan? plan, bool complete)> InvokeOrchestrate(
        AgentController controller, string prompt, string? cardId = null)
    {
        var method = typeof(AgentController).GetMethod("Orchestrate", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Orchestrate not found");
        var task = (Task<(List<object> allSteps, AgentPlan? plan, bool complete)>)method.Invoke(controller, new object?[]
        {
            prompt, _projectRoot, /*emitSse*/ false, CancellationToken.None,
            /*attachedFiles*/ new List<string>(),
            /*skipContextReview*/ false, /*steeringContext*/ null, /*skipQualityCheck*/ false,
            /*existingPlan*/ null, /*completedStepIndices*/ null, /*cardId*/ cardId,
            /*createTests*/ false, /*buildCommands*/ null
        })!;
        return await task;
    }

    private AgentController BuildController()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Editor:WorkspaceRoot"] = _base,
                ["Editor:DisableLLMRetries"] = "true"
            })
            .Build();
        var controller = (AgentController)RuntimeHelpers.GetUninitializedObject(typeof(AgentController));
        SetField(controller, "_clientFactory", _clientFactory);
        SetField(controller, "_config", config);
        SetField(controller, "_env", new FakeWebHostEnvironment(_projectRoot));
        SetField(controller, "_db", _db);
        SetField(controller, "_configFile", new ConfigFileService(_db));
        SetField(controller, "_terminal", new TerminalService(new ConfigFileService(_db)));
        SetField(controller, "_fileHints", new FileHintsManager(_db));
        SetField(controller, "_boardData", _boardData);
        SetField(controller, "_emailService", new EmailService(new ConfigFileService(_db)));
        SetField(controller, "_push", new PushNotificationService(_db));
        SetField(controller, "_editKnowledge", new EditKnowledgeService(_db));
        // Skip the real TCP/HTTP connectivity probe (the run must not depend on the host
        // network): cache the "reachable" verdict directly.
        SetStaticField("_nextConnectivityCheck", DateTime.UtcNow.AddMinutes(5));
        SetField(controller, "_lastConnectionCheckResult", true);
        return controller;
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field {name} not found");
        field.SetValue(target, value);
    }

    private static void SetStaticField(string name, object value)
    {
        var field = typeof(AgentController).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Static field {name} not found");
        field.SetValue(null, value);
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public FakeWebHostEnvironment(string contentRoot) => ContentRootPath = contentRoot;
        public string ApplicationName { get; set; } = "Weaver";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; }
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private enum PlannerMode { WebChain, SteeringWrite, NeverWrites, SearchOnly, FetchRetry, FetchAlwaysFails, SearchThenEdit }

    /// <summary>
    /// An <see cref="IHttpClientFactory"/> whose handler answers every LLM request from a
    /// script routed on stable prompt markers, and answers the web-step GETs the run makes:
    /// DuckDuckGo searches return realistic result JSON (so step 1 produces harvestable
    /// output with article URLs), fetches return a small body. Unmatched LLM calls are
    /// recorded and fail the test.
    /// </summary>
    private sealed class WebTaskScriptedClientFactory : IHttpClientFactory, IDisposable
    {
        public readonly List<string> Calls = new();
        public readonly List<string> Unmatched = new();
        // User prompts of the repair-loop replanner (plan-fixer) — recorded so the test can
        // assert the repair loop RAN and was steered at the missing file even when it can't
        // complete the run.
        public readonly List<string> RepairUserPrompts = new();
        // The user prompts of the planner's SECOND call in WebChain mode — the step that
        // must pick the _web_fetch URL. Recorded so the test can assert the harvested
        // ### WEB RESULTS section actually reached the step-2 planner turn.
        public readonly List<string> Step2PlannerPrompts = new();
        public readonly List<string> PlannerUserPrompts = new();
        // Every GET the run makes (searches, fetches, probes) — recorded so a test can
        // assert the executed _web_fetch hit the EXACT URL harvested from the search
        // results (cross-tool carry judged at execution, not just in the plan).
        public readonly List<string> FetchedUrls = new();
        public readonly string SteerTarget;
        public PlannerMode Mode;
        /// <summary>Deterministic verdict for the between-steps whole-task completion
        /// assessment ("Evaluate the code changes against the ORIGINAL TASK ONLY"). The
        /// web-only assessment is a NEW LLM call the harness must script: content-aware
        /// (e.g. File.Exists on the demanded output) so a web chain stays incomplete until
        /// its output lands, exactly like the AssessComplete pattern in the synthetic suite.
        /// Null defaults to complete=true.</summary>
        public Func<bool>? WebAssessComplete { get; set; }
        private int _plannerCalls;

        public WebTaskScriptedClientFactory(PlannerMode mode, string steerTarget)
        {
            Mode = mode;
            SteerTarget = steerTarget;
        }

        public HttpClient CreateClient(string name) => new(new ScriptedHandler(this));
        public HttpClient CreateClient() => CreateClient("default");
        public void Dispose() { }

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly WebTaskScriptedClientFactory _owner;
            public ScriptedHandler(WebTaskScriptedClientFactory owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var resp = BuildResponse(request);
                return Task.FromResult(resp);
            }

            private HttpResponseMessage BuildResponse(HttpRequestMessage request)
            {
                if (request.Method == HttpMethod.Get)
                {
                    lock (_owner.FetchedUrls) _owner.FetchedUrls.Add(request.RequestUri?.ToString() ?? "");
                    var host = request.RequestUri?.Host ?? "";
                    if (host.Contains("duckduckgo", StringComparison.OrdinalIgnoreCase))
                    {
                        // Realistic DuckDuckGo instant-answer JSON — long enough (well over
                        // 80 chars) to be harvested into ### WEB RESULTS.
                        return Json(new
                        {
                            AbstractText = "A survey of recent AI research breakthroughs covering large language models, multimodal systems and protein-folding advances published this quarter.",
                            AbstractURL = "https://example.com/ai-overview",
                            Answer = "",
                            RelatedTopics = new object[]
                            {
                                new { Text = "AlphaFold 3 predicts protein structures with atom-level accuracy", FirstURL = "https://example.com/alphafold3" },
                                new { Text = "A new open-weight LLM benchmarks above GPT-4 on reasoning tasks", FirstURL = "https://example.com/llm-benchmarks" }
                            }
                        });
                    }
                    if (host.Contains("www.example.com", StringComparison.OrdinalIgnoreCase))
                    {
                        // The invented example-domain URL must genuinely FAIL — the exact field
                        // failure ("www.example.com/latest-ai-breakthrough"). Throwing makes
                        // WebFetchAsync return an error so the web step result carries status
                        // "error" and the interleaved loop exercises its fetch-failure retry.
                        throw new HttpRequestException($"fetch failed: invented URL on {host}");
                    }
                    // Connectivity probes (/api/tags, /slots) and _web_fetch targets: a small
                    // body is enough — the run must not depend on the real network.
                    return Json(new { });
                }
                var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                var system = new StringBuilder();
                var user = new StringBuilder();
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("messages", out var msgs))
                    {
                        foreach (var m in msgs.EnumerateArray())
                        {
                            var role = m.TryGetProperty("role", out var r) ? r.GetString() : "";
                            var msgContent = m.TryGetProperty("content", out var c) ? c.GetString() : "";
                            if (role == "system") system.Append(msgContent).Append('\n');
                            else if (role == "user") user.Append(msgContent).Append('\n');
                        }
                    }
                }
                catch { }
                var streaming = body.Contains("\"stream\":true", StringComparison.Ordinal) ||
                                body.Contains("\"stream\": true", StringComparison.Ordinal);
                var (content, kind) = Route(system.ToString(), user.ToString());
                lock (_owner.Calls) _owner.Calls.Add(kind);
                return streaming ? Sse(content) : Json(new { choices = new[] { new { message = new { content } } } });
            }

            private (string content, string kind) Route(string system, string user)
            {
                if (system.Contains("senior autonomous coding agent building a code-change plan", StringComparison.Ordinal))
                {
                    lock (_owner.PlannerUserPrompts) _owner.PlannerUserPrompts.Add(user);
                    var n = Interlocked.Increment(ref _owner._plannerCalls);
                    return PlannerContent(n, user);
                }
                if (system.Contains("You are a plan-fixer", StringComparison.Ordinal))
                {
                    // The repair loop must run but NEVER produce a write: an empty plan means
                    // "replanner returned no steps" and the loop breaks with the run incomplete.
                    lock (_owner.RepairUserPrompts) _owner.RepairUserPrompts.Add(user);
                    return ("{\"plan\": []}", "replanner");
                }
                if (system.Contains("You are the deep-reasoning engine of an autonomous coding agent", StringComparison.Ordinal))
                    return ("The next step is scripted by the test harness. Keep the task minimal: implement exactly the scripted step.", "deep-reason");
                if (system.Contains("You are a strict plan-coherence validator", StringComparison.Ordinal))
                    return ("{\"valid\": true}", "plan-validator");
                if (system.Contains("You are a task complexity assessor", StringComparison.Ordinal))
                    return ("{\"score\": 20, \"atomicSteps\": 2}", "complexity");
                if (system.Contains("You extract a short checklist of literal, testable requirements", StringComparison.Ordinal))
                {
                    // The checklist is APPENDED to the task prompt (Pipeline.cs), so in the
                    // write-file modes it must not contain web-y phrasing — "Search the web…"
                    // would trip TaskExplicitlyCommandsWeb and hijack the run into the web-need
                    // gate before the OS-output gate gets a chance.
                    var req = _owner.Mode == PlannerMode.WebChain
                        ? "[\"Search the web for an interesting AI article\", \"Write the article data into a text file on the desktop\"]"
                        : _owner.Mode == PlannerMode.SearchThenEdit
                            ? "[\"Add the article link into src/links.ts\"]"
                            : "[\"Write the data into the file\", \"The file must exist after the run\"]";
                    return ($"{{\"requirements\": {req}}}", "checklist");
                }
                if (system.Contains("strict task classifier", StringComparison.Ordinal))
                    return ("{\"needsWeb\": false, \"reason\": \"repo-local write\", \"query\": \"\"}", "web-need");
                if (user.Contains("Evaluate the code changes against the ORIGINAL TASK ONLY", StringComparison.Ordinal))
                {
                    // The between-steps completion assessment for web-only runs. Scripted
                    // content-aware: incomplete until the demanded output exists, then complete.
                    var done = _owner.WebAssessComplete?.Invoke() ?? true;
                    return (done
                        ? "{\"complete\": true, \"reason\": \"scripted web assessment: demanded output exists\", \"issues\": []}"
                        : "{\"complete\": false, \"reason\": \"scripted web assessment: demanded output not yet written\", \"issues\": [\"the demanded OS output file was not written yet\"]}", "web-assess");
                }
                // The cross-tool EDIT arm applies a repo edit, so the per-step verifier,
                // cohesion check, and post-execution verifier can run — script them all
                // (the search/fetch/command modes never edit, so these stayed unscripted).
                if (user.Contains("Decide: keep or abandon", StringComparison.Ordinal))
                    return ("{\"decision\": \"keep\", \"reason\": \"scripted edit\", \"score\": 95, \"needsExtraStep\": false}", "verify");
                if (system.Contains("You detect code cohesion issues after an edit. Output ONLY JSON.", StringComparison.Ordinal))
                    return ("{\"issues\": []}", "cohesion");
                if (system.Contains("meticulous code reviewer verifying if a task is fully complete", StringComparison.Ordinal))
                    return ("{\"complete\": true, \"reason\": \"done\", \"issues\": []}", "post-verify");
                // The discovery-phase architecture one-liner (fires only when the project
                // actually has files — the edit arm's fixture makes it run).
                if (system.Contains("You are a project architect. Given a project file tree", StringComparison.Ordinal))
                    return ("TypeScript fixture project", "architecture");
                lock (_owner.Unmatched) _owner.Unmatched.Add(system.Length > 80 ? system[..80] : system);
                return ("", "unknown");
            }

            private (string content, string kind) PlannerContent(int n, string user)
            {
                if (_owner.Mode == PlannerMode.SteeringWrite)
                {
                    // Call 1 declares complete prematurely (must be rejected by the
                    // OS-output gate); call 2 plans the write the feedback demanded;
                    // call 3 completes.
                    if (n == 1)
                        return ("{\"planComplete\": true, \"completionReason\": \"nothing to do\"}", "planner-step");
                    if (n == 2)
                        return (PlannerStepJson("_command",
                            $"Set-Content -Path \"{_owner.SteerTarget}\" -Value \"repair data\" -Encoding UTF8"), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"wrote the file\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.NeverWrites)
                {
                    // Every planner turn declares the plan complete without ever proposing a
                    // write and without any web step to auto-dump — the OS-output gate rejects
                    // each premature completion until the regen budget is exhausted.
                    return ("{\"planComplete\": true, \"completionReason\": \"nothing to do\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.SearchOnly)
                {
                    // One search step, then planComplete — the run must end after the search
                    // via the between-steps WEB assessment, not by the planner completing.
                    if (n == 1)
                        return (PlannerStepJson("_web_search", SearchQuery), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"gathered the results\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.SearchThenEdit)
                {
                    // Turn 1 searches (the value-producer), turn 2 EDITS a repo file
                    // embedding the EXACT URL harvested from the search results — the
                    // cross-tool carry arm: search tool → edit tool.
                    if (n == 1)
                        return (PlannerStepJson("_web_search", SearchQuery), "planner-step");
                    if (n == 2)
                    {
                        lock (_owner.Step2PlannerPrompts) _owner.Step2PlannerPrompts.Add(user);
                        return (PlannerEditStepJson(LinksRel,
                            "Add a link to the best AI article from the search results into the links array",
                            "export const links: string[] = [];",
                            "export const links: string[] = ['https://example.com/alphafold3'];"
                        ), "planner-step");
                    }
                    return ("{\"planComplete\": true, \"completionReason\": \"added the article link from the search results\"}", "planner-step");
                }
                if (n == 1)
                    return (PlannerStepJson("_web_search", SearchQuery), "planner-step");
                if (_owner.Mode == PlannerMode.FetchRetry)
                {
                    // Step 2 fetches an INVENTED URL (www.example.com — the exact failure from
                    // the field: the model copied the example-domain pattern instead of a real
                    // result URL). The scripted web handler makes that host throw, so the fetch
                    // FAILS. Step 3 must then retry with a REAL URL from the harvested results.
                    lock (_owner.Step2PlannerPrompts) _owner.Step2PlannerPrompts.Add(user);
                    if (n == 2)
                        return (PlannerStepJson("_web_fetch", "https://www.example.com/latest-ai-breakthrough"), "planner-step");
                    if (n == 3)
                        return (PlannerStepJson("_web_fetch", "https://example.com/alphafold3"), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"fetched a real URL after the invented one failed\"}", "planner-step");
                }
                if (_owner.Mode == PlannerMode.FetchAlwaysFails)
                {
                    // Every fetch uses the invented example-domain URL and fails. The retry cap
                    // (2) is exhausted, the run halts into post-execution verification, and the
                    // repair loop's deterministic auto-dump must complete the run from the
                    // harvested search results — the replanner must NEVER be called.
                    lock (_owner.Step2PlannerPrompts) _owner.Step2PlannerPrompts.Add(user);
                    return (PlannerStepJson("_web_fetch", "https://www.example.com/latest-ai-breakthrough"), "planner-step");
                }
                if (n == 2)
                {
                    // Step 2 must pick a CONCRETE URL from the harvested results.
                    lock (_owner.Step2PlannerPrompts) _owner.Step2PlannerPrompts.Add(user);
                    return (PlannerStepJson("_web_fetch", "https://example.com/alphafold3"), "planner-step");
                }
                return ("{\"planComplete\": true, \"completionReason\": \"fetched the article URL from the search results\"}", "planner-step");
            }

            private static string PlannerStepJson(string file, string change)
            {
                var payload = new Dictionary<string, object?>
                {
                    ["thinking"] = $"Step: {file} — {change}",
                    ["planComplete"] = false,
                    ["step"] = new Dictionary<string, object?>
                    {
                        ["file"] = file,
                        ["change"] = change
                    }
                };
                return JsonSerializer.Serialize(payload);
            }

            private static string PlannerEditStepJson(string file, string change, string oldString, string newString)
            {
                var payload = new Dictionary<string, object?>
                {
                    ["thinking"] = $"Edit: {file} — {change}",
                    ["planComplete"] = false,
                    ["step"] = new Dictionary<string, object?>
                    {
                        ["file"] = file,
                        ["change"] = change,
                        ["oldString"] = oldString,
                        ["newString"] = newString
                    }
                };
                return JsonSerializer.Serialize(payload);
            }

            private static HttpResponseMessage Json(object obj)
                => new(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json")
                };

            private static HttpResponseMessage Sse(string content)
            {
                var data = JsonSerializer.Serialize(new
                {
                    choices = new[] { new { delta = new { content }, finish_reason = "stop" } }
                });
                var body = $"data: {data}\n\n\ndata: [DONE]\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
                };
            }
        }
    }
}
