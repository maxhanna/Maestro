using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Weaver.Controllers;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

public sealed class AgentControllerBenchmarkRoutingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weaver-agent-routing-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BenchmarkCommand_UsesSandboxRunnerWithoutStartingHostTerminal()
    {
        Directory.CreateDirectory(_root);
        var environment = new FakeEnvironment(_root);
        var configFile = new ConfigFileService(environment);
        using var terminal = new TerminalService(configFile);
        var runner = new RecordingBenchmarkTerminalRunner();
        var controller = new AgentController(
            null!, null!, environment, terminal, new FileHintsManager(_root), configFile,
            new EmailService(configFile), new BoardDataService(Path.Combine(_root, "board.json"), null!), runner);

        typeof(AgentController).GetField("_isBenchmark", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(controller, true);
        var method = typeof(AgentController).GetMethod("RunAgentCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task<CommandCheckOutcome>)method.Invoke(
            controller, ["touch /workspace/result.txt", "/workspace", 30, CancellationToken.None])!;

        var outcome = await task;

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("touch /workspace/result.txt", runner.Command);
        Assert.Equal("/workspace", runner.WorkingDirectory);
        Assert.False(terminal.IsRunning);
    }

    [Fact]
    public async Task BenchmarkApply_UsesSandboxRunnerForSubmittedCommands()
    {
        Directory.CreateDirectory(_root);
        var benchmarkService = new BenchmarkService(Path.Combine(_root, "data"));
        var prepared = await benchmarkService.PrepareAsync(1, benchmarkService.SandboxRoot);
        var environment = new FakeEnvironment(_root);
        var configFile = new ConfigFileService(environment);
        using var terminal = new TerminalService(configFile);
        var runner = new RecordingBenchmarkTerminalRunner();
        var controller = new AgentController(
            null!, new ConfigurationBuilder().Build(), environment, terminal, new FileHintsManager(_root), configFile,
            new EmailService(configFile), new BoardDataService(Path.Combine(_root, "board.json"), null!), runner)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var response = await controller.ApplyEdits(new ApplyEditsRequest
        {
            IsBenchmark = true,
            BenchmarkRunId = prepared.RunId,
            Edits = [new EditAction { Path = "agent.txt", NewString = "created" }],
            Commands = [new CommandAction { Command = "touch /workspace/command.txt" }]
        });

        var ok = Assert.IsType<OkObjectResult>(response);
        Assert.NotNull(ok.Value);
        Assert.Equal("touch /workspace/command.txt", runner.Command);
        Assert.Equal(prepared.RunRoot, runner.WorkingDirectory);
        Assert.False(terminal.IsRunning);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingBenchmarkTerminalRunner : IBenchmarkTerminalRunner
    {
        public string? Command { get; private set; }
        public string? WorkingDirectory { get; private set; }

        public Task<CommandCheckOutcome> RunAsync(string command, string workingDirectory, CancellationToken ct)
        {
            Command = command;
            WorkingDirectory = workingDirectory;
            return Task.FromResult(new CommandCheckOutcome(0, false, 1, "sandbox output", "", "ok"));
        }
    }

    private sealed class FakeEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Weaver.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(root);
    }
}
