using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.IntegrationTests;

/// <summary>
/// Substitutes UnifiedPipeline's own multi-phase LLM machinery (discovery, planning
/// convergence, validation, pre-audit, edit resolution) with a scripted result, so a
/// test can exercise the real Orchestrate() / CommandExecutionPipeline() / chaining
/// code around it without reverse-engineering every internal LLM contract UnifiedPipeline
/// uses. See AgentController.cs — chaining branch (~line 8700) and the "internal virtual"
/// comment on UnifiedPipeline for why this seam exists.
/// </summary>
public class TestableAgentController : AgentController
{
    /// <summary>Set per-test before calling Orchestrate to control what the chained
    /// "second stage" returns.</summary>
    public Func<string, string, List<string>?, (List<object> steps, AgentPlan plan)>? UnifiedPipelineStub { get; set; }

    public TestableAgentController(
        IHttpClientFactory cf, IConfiguration config,
        IWebHostEnvironment env, TerminalService terminal, FileHintsManager fileHints,
        ConfigFileService configFile, EmailService emailService, BoardDataService boardData)
        : base(cf, config, env, terminal, fileHints, configFile, emailService, boardData)
    {
    }

    internal override Task<(List<object> steps, AgentPlan plan)> UnifiedPipeline(
        string prompt, string projectRoot, bool emitSse, CancellationToken ct,
        List<string>? attachedFiles = null,
        bool skipContextReview = false,
        string? steeringContext = null,
        string? cardId = null)
    {
        if (UnifiedPipelineStub == null)
            throw new InvalidOperationException(
                "TestableAgentController.UnifiedPipelineStub was not set before Orchestrate reached the chaining branch.");

        return Task.FromResult(UnifiedPipelineStub(prompt, projectRoot, attachedFiles));
    }
}
