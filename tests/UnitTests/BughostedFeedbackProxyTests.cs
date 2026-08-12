using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Weaver.Controllers;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Tests for the POST api/bughosted/feedback proxy — the card-feedback loop between
/// weaver and bughosted (weaver admins review these). The audit flagged the whole
/// feedback loop as shipped with zero tests; ConfigControllerSaveTests only covers the
/// config save. These drive the real Feedback endpoint: session auth, message
/// validation, the forwarded payload (token/cardId/cardText/message/planSummary/
/// filesEdited/steps), upstream error surfacing, and upstream-throw behavior.
/// </summary>
public class BughostedFeedbackProxyTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public readonly List<(string url, string body)> Posts = new();
        public Func<string, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ok\"}", System.Text.Encoding.UTF8, "application/json") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
            {
                var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                lock (Posts) Posts.Add((request.RequestUri?.ToString() ?? "", body));
            }
            return Task.FromResult(Responder(request.RequestUri?.ToString() ?? ""));
        }
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler);
        public HttpClient CreateClient() => CreateClient("default");
    }

    private const string TestClientId = "test-client-1";
    private const string UpstreamUrl = "http://bughosted.test";

    private static BughostedController Build(RecordingHandler handler)
    {
        var controller = (BughostedController)RuntimeHelpers.GetUninitializedObject(typeof(BughostedController));
        var field = typeof(BughostedController).GetField("_clientFactory", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_clientFactory field not found");
        field.SetValue(controller, new FakeHttpClientFactory(handler));
        var sessions = (Dictionary<string, BughostedSession>)typeof(BughostedController)
            .GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        sessions[TestClientId] = new BughostedSession
        {
            Token = "tok-123",
            ClientId = TestClientId,
            Url = UpstreamUrl
        };
        return controller;
    }

    private static BughostedFeedbackRequest Req(string? message = "this run went wrong", string clientId = TestClientId)
        => new()
        {
            ClientId = clientId,
            CardId = "card-42",
            CardText = "Fix the schedule popup",
            Message = message ?? "",
            PlanSummary = "Add max-height + overflow:auto to the schedules container",
            FilesEdited = new List<string> { "maxhanna.client/src/app/globe/globe.component.css", "globe.component.html" },
            Steps = new List<BughostedFeedbackStep>
            {
                new() { Type = "edit", Change = "Add CSS rules for the schedules container", Status = "done" },
                new() { Type = "_web_search", Change = "latest release notes", Status = "done" },
                new() { Type = "verified_complete", Change = "Final verification", Status = "pending" }
            }
        };

    // ── Auth + validation ─────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownClientId_Unauthorized()
    {
        var controller = Build(new RecordingHandler());
        var result = await controller.Feedback(Req(clientId: "no-such-session"));
        var obj = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(401, obj.StatusCode);
    }

    [Fact]
    public async Task EmptyMessage_BadRequest()
    {
        var controller = Build(new RecordingHandler());
        var result = await controller.Feedback(Req(message: "   "));
        var obj = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, obj.StatusCode);
    }

    // ── Forwarding payload ────────────────────────────────────────────────────

    [Fact]
    public async Task ValidFeedback_ForwardsTokenAndFields_ToUpstream()
    {
        var handler = new RecordingHandler();
        var controller = Build(handler);
        var result = await controller.Feedback(Req());

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/json", content.ContentType);

        var post = Assert.Single(handler.Posts);
        Assert.Equal(UpstreamUrl + "/weaver/feedback", post.url);
        using var doc = JsonDocument.Parse(post.body);
        var root = doc.RootElement;
        Assert.Equal("tok-123", root.GetProperty("token").GetString());
        Assert.Equal("card-42", root.GetProperty("cardId").GetString());
        Assert.Equal("Fix the schedule popup", root.GetProperty("cardText").GetString());
        Assert.Equal("this run went wrong", root.GetProperty("message").GetString());
        Assert.Equal("Add max-height + overflow:auto to the schedules container", root.GetProperty("planSummary").GetString());
        var files = root.GetProperty("filesEdited").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Equal(new[] { "maxhanna.client/src/app/globe/globe.component.css", "globe.component.html" }, files);
        var steps = root.GetProperty("steps").EnumerateArray().Select(s => new
        {
            type = s.GetProperty("type").GetString(),
            change = s.GetProperty("change").GetString(),
            status = s.GetProperty("status").GetString()
        }).ToList();
        Assert.Equal(3, steps.Count);
        Assert.Equal("edit", steps[0].type);
        Assert.Equal("Add CSS rules for the schedules container", steps[0].change);
        Assert.Equal("done", steps[0].status);
        Assert.Equal("_web_search", steps[1].type);
        Assert.Equal("latest release notes", steps[1].change);
        Assert.Equal("pending", steps[2].status);
    }

    [Fact]
    public async Task OversizedPlanSummary_TruncatedBeforeForwarding()
    {
        var handler = new RecordingHandler();
        var controller = Build(handler);
        var huge = new string('x', 5000);
        var req = Req();
        req.PlanSummary = huge;
        await controller.Feedback(req);
        var post = Assert.Single(handler.Posts);
        using var doc = JsonDocument.Parse(post.body);
        var summary = doc.RootElement.GetProperty("planSummary").GetString();
        Assert.Equal(2000, summary!.Length);
        Assert.StartsWith(huge[..2000], summary);
    }

    [Fact]
    public async Task OversizedFilesEdited_TrimmedToCap_WithPathTruncation()
    {
        var handler = new RecordingHandler();
        var controller = Build(handler);
        var req = Req();
        var longPath = new string('p', 1000);
        req.FilesEdited = Enumerable.Range(0, 150)
            .Select(i => i == 0 ? longPath : $"path/{i}.ts")
            .ToList();
        await controller.Feedback(req);
        var post = Assert.Single(handler.Posts);
        using var doc = JsonDocument.Parse(post.body);
        var files = doc.RootElement.GetProperty("filesEdited").EnumerateArray().Select(x => x.GetString()).ToList();
        // List capped to the first 100 entries, run order preserved.
        Assert.Equal(100, files.Count);
        Assert.Equal("path/1.ts", files[1]);
        Assert.Equal("path/99.ts", files[99]);
        // The single over-long entry was truncated to the per-path cap.
        Assert.Equal(300, files[0]!.Length);
        Assert.StartsWith(new string('p', 300), files[0]);
    }

    [Fact]
    public async Task UpstreamResponse_PassedThroughVerbatim()
    {
        var handler = new RecordingHandler();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"received\":\"card-42\"}", System.Text.Encoding.UTF8, "application/json")
        };
        var controller = Build(handler);
        var result = await controller.Feedback(Req());
        var content = Assert.IsType<ContentResult>(result);
        Assert.Contains("received", content.Content);
    }

    // ── Upstream failures ─────────────────────────────────────────────────────

    [Fact]
    public async Task UpstreamNonSuccess_SurfacesStatusAndBody()
    {
        var handler = new RecordingHandler();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream exploded", System.Text.Encoding.UTF8)
        };
        var controller = Build(handler);
        var result = await controller.Feedback(Req());
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, obj.StatusCode);
        Assert.Equal("upstream exploded", obj.Value);
    }

    [Fact]
    public async Task UpstreamThrows_Returns500()
    {
        var handler = new RecordingHandler();
        handler.Responder = _ => throw new HttpRequestException("connection refused");
        var controller = Build(handler);
        var result = await controller.Feedback(Req());
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, obj.StatusCode);
    }
}
