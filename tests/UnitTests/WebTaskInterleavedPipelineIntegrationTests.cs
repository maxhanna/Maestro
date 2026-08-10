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
///   4. Zero file edits are applied, and every LLM call is one the script accounts for
///      (Unmatched must be empty — an unexpected call fails the test loudly).
/// </summary>
public class WebTaskInterleavedPipelineIntegrationTests : IDisposable
{
    private const string SearchQuery = "AI research breakthroughs latest";

    private readonly string _base;
    private readonly string _projectRoot;
    private readonly DatabaseService _db;
    private readonly WebTaskScriptedClientFactory _clientFactory = new();

    public WebTaskInterleavedPipelineIntegrationTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "weaver_webtask_" + Guid.NewGuid().ToString("N"));
        _projectRoot = Path.Combine(_base, "proj");
        Directory.CreateDirectory(_projectRoot);
        Directory.CreateDirectory(Path.Combine(_base, "data"));

        _db = new DatabaseService(
            Path.Combine(_base, "data", "weaver.db"),
            Path.Combine(_base, "data"),
            Path.Combine(_base, "data", "weaverconfig.json"));
    }

    public void Dispose()
    {
        _clientFactory.Dispose();
        try { Directory.Delete(_base, true); } catch { }
    }

    [Fact]
    public async Task WebTask_Step2IsWebFetchOfResultUrl_NotInventedEdit()
    {
        var controller = BuildController();
        var prompt = "Search the web for an interesting and relevant AI article and write the data into a text file on my desktop.";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        // The run finished complete.
        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}");

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

        // The plan is EXACTLY: _web_search → _web_fetch (the URL from the results). No
        // invented edit steps, no re-search, no drift into writing scraping code.
        Assert.NotNull(plan);
        Assert.Equal(new[] { "_web_search", "_web_fetch" }, plan!.Plan.Select(s => s.File).ToArray());
        Assert.Equal("AI research breakthroughs latest", plan.Plan[0].Change);
        Assert.Equal("https://example.com/alphafold3", plan.Plan[1].Change);

        // ZERO file edits were applied — the core "not an invented edit" assertion. A
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
        // no verify/assess/cohesion/post-verify — no edits exist to verify).
        Assert.Contains("checklist", _clientFactory.Calls);
        Assert.Equal(3, _clientFactory.Calls.Count(c => c == "planner-step"));
        Assert.DoesNotContain("verify", _clientFactory.Calls);
        Assert.DoesNotContain("post-verify", _clientFactory.Calls);
    }

    private async Task<(List<object> allSteps, AgentPlan? plan, bool complete)> InvokeOrchestrate(
        AgentController controller, string prompt)
    {
        var method = typeof(AgentController).GetMethod("Orchestrate", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Orchestrate not found");
        var task = (Task<(List<object> allSteps, AgentPlan? plan, bool complete)>)method.Invoke(controller, new object?[]
        {
            prompt, _projectRoot, /*emitSse*/ false, CancellationToken.None,
            /*attachedFiles*/ new List<string>(),
            /*skipContextReview*/ false, /*steeringContext*/ null, /*skipQualityCheck*/ false,
            /*existingPlan*/ null, /*completedStepIndices*/ null, /*cardId*/ null,
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
        SetField(controller, "_boardData", new BoardDataService(_db, NullLogger<BoardDataService>.Instance));
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
        // The user prompts of the planner's SECOND call — the step that must pick the
        // _web_fetch URL. Recorded so the test can assert the harvested ### WEB RESULTS
        // section actually reached the step-2 planner turn.
        public readonly List<string> Step2PlannerPrompts = new();
        private int _plannerCalls;

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
                    var n = Interlocked.Increment(ref _owner._plannerCalls);
                    if (n == 1)
                        return (PlannerStepJson("_web_search", SearchQuery), "planner-step");
                    if (n == 2)
                    {
                        // Step 2 must pick a CONCRETE URL from the harvested results.
                        lock (_owner.Step2PlannerPrompts) _owner.Step2PlannerPrompts.Add(user);
                        return (PlannerStepJson("_web_fetch", "https://example.com/alphafold3"), "planner-step");
                    }
                    return ("{\"planComplete\": true, \"completionReason\": \"fetched the article URL from the search results\"}", "planner-step");
                }
                if (system.Contains("You extract a short checklist of literal, testable requirements", StringComparison.Ordinal))
                    return ("{\"requirements\": [\"Search the web for an interesting AI article\", \"Write the article data into a text file on the desktop\"]}", "checklist");
                lock (_owner.Unmatched) _owner.Unmatched.Add(system.Length > 80 ? system[..80] : system);
                return ("", "unknown");
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
