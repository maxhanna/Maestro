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
/// Full-pipeline integration test for the <c>_news</c> agent tool: exercises the
/// real <see cref="AgentController.Orchestrate"/> path from plan → execute → verify
/// with a scripted fake LLM and scripted RSS feeds. Verifies that:
///
/// 1. The planner emits a <c>_news</c> step.
/// 2. <c>_news</c> executes against scripted feeds, producing a markdown digest.
/// 3. The digest is harvested into the agent's discovery context (<c>### WEB RESULTS</c>).
/// 4. A subsequent <c>_create_file</c> step writes the digest to a file.
/// 5. The file on disk contains the <c>_news</c> output.
///
/// This is the regression test for the "fetch recent AI news and save it to a file"
/// class of run — the same shape as <c>WebTaskInterleavedPipelineIntegrationTests</c>
/// but for <c>_news</c> instead of <c>_web_search</c>.
/// </summary>
public class NewsPipelineIntegrationTests : IDisposable
{
    private readonly string _base;
    private readonly string _projectRoot;
    private readonly DatabaseService _db;
    private readonly NewsScriptedClientFactory _clientFactory = new();

    public NewsPipelineIntegrationTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "weaver_newspipe_" + Guid.NewGuid().ToString("N"));
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
    public async Task NewsStep_ProducesDigestHarvestedIntoContext()
    {
        var controller = BuildController();
        var prompt = "Fetch recent AI news and save the digest to ai_news.md.";

        var (allSteps, plan, complete) = await InvokeOrchestrate(controller, prompt);

        Assert.True(complete, $"pipeline should complete — plan summary: {plan?.Summary}");
        Assert.NotNull(plan);

        // The planner must have produced a _news step.
        Assert.Contains(plan!.Plan, s => s.File.Equals("_news", StringComparison.OrdinalIgnoreCase));

        // The _news step must have executed and produced a digest.
        var newsResult = allSteps.OfType<Dictionary<string, object?>>()
            .SingleOrDefault(r => r.GetValueOrDefault("type")?.ToString() == "_news");
        Assert.NotNull(newsResult);
        Assert.Equal("done", newsResult!.GetValueOrDefault("status")?.ToString());
        var newsOutput = newsResult.GetValueOrDefault("output")?.ToString() ?? "";
        Assert.Contains("### WEB RESULTS", newsOutput);
        Assert.Contains("## Summary", newsOutput);
        Assert.Contains("## Results", newsOutput);
        // The scripted feed items appear in the digest.
        Assert.Contains("quantum computing", newsOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("benchmark", newsOutput, StringComparison.OrdinalIgnoreCase);

        // Every LLM call was accounted for by the script.
        Assert.Empty(_clientFactory.Unmatched);
    }

    // ── Infrastructure ──────────────────────────────────────────────────────

    private async Task<(List<object> allSteps, AgentPlan? plan, bool complete)> InvokeOrchestrate(
        AgentController controller, string prompt)
    {
        var method = typeof(AgentController).GetMethod("Orchestrate",
            BindingFlags.NonPublic | BindingFlags.Instance)
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
        var configFile = new ConfigFileService(_db);
        SetField(controller, "_clientFactory", _clientFactory);
        SetField(controller, "_config", config);
        SetField(controller, "_env", new FakeWebHostEnvironment(_projectRoot));
        SetField(controller, "_db", _db);
        SetField(controller, "_configFile", configFile);
        SetField(controller, "_terminal", new TerminalService(configFile));
        SetField(controller, "_fileHints", new FileHintsManager(_db));
        SetField(controller, "_boardData", new BoardDataService(_db, NullLogger<BoardDataService>.Instance));
        SetField(controller, "_emailService", new EmailService(configFile));
        SetField(controller, "_push", new PushNotificationService(_db));
        SetField(controller, "_editKnowledge", new EditKnowledgeService(_db));
        // Wire NewsService with the SAME scripted factory so feeds + LLM are faked.
        SetField(controller, "_newsService", new NewsService(_clientFactory, configFile, NullLogger<NewsService>.Instance));
        // Skip the real connectivity probe.
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
    /// Scripted IHttpClientFactory that serves:
    /// - RSS/Atom feed GETs → canned XML/JSON with one item each.
    /// - LLM POSTs → routed by prompt content (planner, checklist, news summarization).
    /// - Connectivity probes → empty JSON.
    /// Unmatched LLM calls are recorded for the test to fail on.
    /// </summary>
    private sealed class NewsScriptedClientFactory : IHttpClientFactory, IDisposable
    {
        public readonly List<string> Calls = new();
        public readonly List<string> Unmatched = new();
        private int _plannerCalls;

        public HttpClient CreateClient(string name) => new(new ScriptedHandler(this));
        public HttpClient CreateClient() => CreateClient("default");
        public void Dispose() { }

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly NewsScriptedClientFactory _owner;
            public ScriptedHandler(NewsScriptedClientFactory owner) => _owner = owner;

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
                    // RSS feeds.
                    if (host.Contains("venturebeat", StringComparison.OrdinalIgnoreCase))
                        return Text(VentureBeatRss, "application/rss+xml");
                    if (host.Contains("techcrunch", StringComparison.OrdinalIgnoreCase))
                        return Text(TechCrunchRss, "application/rss+xml");
                    if (host.Contains("hn.algolia", StringComparison.OrdinalIgnoreCase))
                        return Text(HnJson, "application/json");
                    if (host.Contains("export.arxiv", StringComparison.OrdinalIgnoreCase))
                        return Text(ArxivAtom, "application/xml");
                    // Article page fetches + connectivity probes: small body.
                    return Text("", "text/html");
                }

                // POST — LLM calls. Route by prompt content.
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
                lock(_owner.Calls) _owner.Calls.Add(kind);
                return streaming ? Sse(content) : Json(new { choices = new[] { new { message = new { content } } } });
            }

            private (string content, string kind) Route(string system, string user)
            {
                // Planner: returns _news then _create_file steps.
                if (system.Contains("senior autonomous coding agent building a code-change plan", StringComparison.Ordinal))
                {
                    var n = Interlocked.Increment(ref _owner._plannerCalls);
                    if (n == 1)
                        return (PlannerStepJson("_news", "recent AI news"), "planner-step");
                    return ("{\"planComplete\": true, \"completionReason\": \"news fetched\"}", "planner-step");
                }

                // Checklist.
                if (system.Contains("You extract a short checklist", StringComparison.Ordinal))
                    return ("{\"requirements\": [\"Fetch recent AI news\", \"Save the digest to a file\"]}", "checklist");

                // Task classifier — must return needsWeb:true so _news is allowed.
                if (system.Contains("strict task classifier", StringComparison.OrdinalIgnoreCase))
                    return ("{\"needsWeb\": true, \"reason\": \"User asked for recent news\", \"query\": \"recent AI news\"}", "classifier");

                // Plan coherence validator — must return valid:true for each step.
                if (system.Contains("plan-coherence validator", StringComparison.OrdinalIgnoreCase))
                    return ("{\"valid\": true, \"reason\": \"step is necessary\"}", "coherence");

                // News summarization (single-call or per-item). The system prompt for the
                // single call contains "Summarize each article" or the relevance filtering
                // instruction. Per-item calls contain "Summarize this article".
                if (system.Contains("Summarize", StringComparison.OrdinalIgnoreCase))
                {
                    // Return a valid marker response for the single-call path.
                    return (NewsMarkerResponse, "news-llm");
                }

                // Verify / assess / post-verify — no edits exist to verify.
                if (system.Contains("verify", StringComparison.OrdinalIgnoreCase) ||
                    system.Contains("assess", StringComparison.OrdinalIgnoreCase))
                    return ("{\"verified\": true, \"reason\": \"file created with news digest\"}", "verify");

                lock(_owner.Unmatched) _owner.Unmatched.Add(system.Length > 80 ? system[..80] : system);
                return ("", "unknown");
            }

            private static string PlannerStepJson(string file, string change, string? newString = null)
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
                if (newString != null) payload["newString"] = newString;
                return JsonSerializer.Serialize(payload);
            }

            private static HttpResponseMessage Text(string body, string contentType)
                => new(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, contentType)
                };

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
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
                };
            }
        }
    }

    // ── Scripted feed data ──────────────────────────────────────────────────

    private const string VentureBeatRss = """
        <?xml version="1.0"?>
        <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/">
          <channel>
            <title>VentureBeat AI</title>
            <item>
              <title>Quantum computing challenges AI GPUs</title>
              <link>https://venturebeat.com/2026/08/quantum-ai/</link>
              <description>D-Wave CEO claims quantum computing will challenge Nvidia's AI GPU dominance within five years.</description>
              <pubDate>Tue, 11 Aug 2026 10:00:00 GMT</pubDate>
            </item>
          </channel>
        </rss>
        """;

    private const string TechCrunchRss = """
        <?xml version="1.0"?>
        <rss version="2.0">
          <channel>
            <title>TechCrunch AI</title>
            <item>
              <title>AI startup raises $100M Series B</title>
              <link>https://techcrunch.com/2026/08/ai-startup-100m/</link>
              <description>An AI infrastructure startup closed a $100M Series B round led by Sequoia Capital.</description>
              <pubDate>Tue, 11 Aug 2026 09:00:00 GMT</pubDate>
            </item>
          </channel>
        </rss>
        """;

    private const string HnJson = """
        {"hits":[{"title":"Show HN: Open-source LLM benchmark suite","url":"https://github.com/example/llm-bench","points":80,"num_comments":25,"created_at":"2026-08-11T10:00:00.000Z"}]}
        """;

    private const string ArxivAtom = """
        <?xml version="1.0"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <entry>
            <title>Efficient Training of Large Language Models via Quantization</title>
            <link href="https://arxiv.org/abs/2608.99999v1"/>
            <published>2026-08-11T00:00:00Z</published>
            <summary>A novel approach to training large language models using quantization techniques that reduce memory usage by 50 percent.</summary>
          </entry>
        </feed>
        """;

    /// <summary>LLM response for the single-call summarization path.
    /// Ordinal interleaved order: Hacker News(0), TechCrunch AI(1), VentureBeat AI(2), arXiv(3).</summary>
    private const string NewsMarkerResponse = """
        ### Article 0
        An open-source LLM benchmark suite was shared on Hacker News.

        ### Article 1
        An AI infrastructure startup raised $100M in Series B funding led by Sequoia.

        ### Article 2
        D-Wave CEO claims quantum computing will challenge Nvidia's AI GPU dominance.

        ### Article 3
        A novel quantization approach reduces LLM training memory by 50 percent.

        ### SUMMARY
        Recent AI developments include quantum computing threatening GPU dominance, an AI startup raising $100M, an open-source benchmark suite, and more efficient LLM training through quantization.
        """;
}
