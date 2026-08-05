using System.Diagnostics;
using System.Text;

namespace Weaver.Services;

public interface IBenchmarkTerminalRunner
{
    Task<CommandCheckOutcome> RunAsync(string command, string workingDirectory, CancellationToken ct);
}

/// <summary>
/// Runs benchmark-agent shell commands in a disposable container. The workspace is
/// staged first so links created by a command cannot resolve through the host bind mount.
/// Valid changes are copied back only after link and size validation.
/// </summary>
public sealed class DockerBenchmarkTerminalRunner : IBenchmarkTerminalRunner
{
    private const string DefaultImage = "python:3.12-alpine@sha256:236173eb74001afe2f60862de935b74fcbd00adfca247b2c27051a70a6a39a2d";
    private const int MaxOutputCharacters = 128 * 1024;
    private readonly string _image;

    public DockerBenchmarkTerminalRunner(string image = DefaultImage)
    {
        if (string.IsNullOrWhiteSpace(image) || !image.Contains("@sha256:", StringComparison.Ordinal) ||
            image.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or ':' or '/' or '-' or '_' or '@')))
            throw new ArgumentException("Benchmark sandbox images must use a digest.", nameof(image));
        _image = image;
    }

    public async Task<CommandCheckOutcome> RunAsync(string command, string workingDirectory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command))
            return CommandCheckOutcome.Failed("Benchmark terminal command cannot be empty.");
        if (!Directory.Exists(workingDirectory))
            return CommandCheckOutcome.Failed("Benchmark workspace does not exist.");

        string stagingDirectory;
        try
        {
            stagingDirectory = DockerBenchmarkCommandRunner.CreateStagingCopy(workingDirectory);
        }
        catch (Exception ex)
        {
            return CommandCheckOutcome.Failed($"Benchmark workspace staging failed: {ex.Message}");
        }

        CommandCheckOutcome outcome;
        Exception? cleanupFailure = null;
        try
        {
            outcome = await RunContainerAsync(command, stagingDirectory, ct);
            if (outcome.ExitCode == 0 && !outcome.TimedOut)
            {
                try
                {
                    if (!WorkspaceWithinLimits(stagingDirectory))
                        throw new InvalidOperationException("The staged workspace exceeded file, directory, or byte limits.");
                    var bytes = 0L;
                    var files = 0;
                    DockerBenchmarkCommandRunner.CopyDirectoryWithoutReparsePoints(
                        stagingDirectory, workingDirectory, ref bytes, ref files);
                    RemoveMissingEntries(stagingDirectory, workingDirectory);
                }
                catch (Exception ex)
                {
                    return CommandCheckOutcome.Failed($"Benchmark workspace synchronization failed: {ex.Message}");
                }
            }
        }
        finally
        {
            try { Directory.Delete(stagingDirectory, recursive: true); }
            catch (Exception ex) { cleanupFailure = ex; }
        }

        return cleanupFailure == null
            ? outcome
            : CommandCheckOutcome.Failed($"Benchmark staging cleanup failed: {cleanupFailure.Message}");
    }

    public static IReadOnlyList<string> BuildSandboxArguments(
        string containerName, string workspace, string command, int timeoutSeconds)
    {
        var timeout = Math.Clamp(timeoutSeconds, 1, 300);
        return
        [
            "run", "--rm", "--pull=never", "--name", containerName,
            "--network", "none",
            "--read-only",
            "--tmpfs", "/tmp:rw,noexec,nosuid,size=128m",
            "--pids-limit", "64",
            "--memory", "512m",
            "--cpus", "1",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--init", "--stop-timeout", "2",
            // The workspace is a disposable staging copy. Container root is still confined
            // by the bind mount, dropped capabilities, read-only rootfs, and no network.
            "--user", "0:0",
            "--mount", $"type=bind,source={Path.GetFullPath(workspace)},target=/workspace,readonly=false",
            "--workdir", "/workspace",
            "--env", "HOME=/tmp",
            "--env", "PYTHONDONTWRITEBYTECODE=1",
            DefaultImage,
            "timeout", "-k", "2", timeout.ToString(),
            "sh", "-lc", command
        ];
    }

    private async Task<CommandCheckOutcome> RunContainerAsync(string command, string workspace, CancellationToken ct)
    {
        var containerName = $"weaver-benchmark-agent-{Guid.NewGuid():N}";
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var arguments = BuildSandboxArguments(containerName, workspace, command, 300);
        var imageIndex = arguments.Count - 8;
        for (var index = 0; index < arguments.Count; index++)
            psi.ArgumentList.Add(index == imageIndex ? _image : arguments[index]);

        Process? process;
        try { process = Process.Start(psi); }
        catch (Exception ex) { return CommandCheckOutcome.Failed($"Benchmark terminal sandbox unavailable: {ex.Message}"); }
        if (process == null)
            return CommandCheckOutcome.Failed("Benchmark terminal sandbox unavailable: Docker process could not be started.");

        using (process)
        {
            var outputLimitExceeded = 0;
            async Task StopForOutputLimit()
            {
                if (Interlocked.Exchange(ref outputLimitExceeded, 1) != 0) return;
                await KillContainerAsync(containerName);
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            var stdoutTask = ReadLimitedAsync(process.StandardOutput, MaxOutputCharacters, StopForOutputLimit);
            var stderrTask = ReadLimitedAsync(process.StandardError, MaxOutputCharacters, StopForOutputLimit);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(305));
            var timedOut = false;
            var cleanupConfirmed = true;
            using var monitorCts = new CancellationTokenSource();
            var workspaceLimitTask = MonitorWorkspaceAsync(workspace, containerName, process, monitorCts.Token);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
                cleanupConfirmed = await KillContainerAsync(containerName);
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            }
            catch (OperationCanceledException)
            {
                monitorCts.Cancel();
                await KillContainerAsync(containerName);
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await workspaceLimitTask; } catch { }
                throw;
            }

            monitorCts.Cancel();
            var workspaceLimitExceeded = await workspaceLimitTask;
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            stopwatch.Stop();
            var outputExceeded = Volatile.Read(ref outputLimitExceeded) != 0;
            var exitCode = process.HasExited ? process.ExitCode : -1;
            var message = outputExceeded
                ? "Benchmark terminal command exceeded the output limit and was stopped."
                : workspaceLimitExceeded
                ? "Benchmark terminal command exceeded workspace limits and was stopped."
                : timedOut
                ? cleanupConfirmed
                    ? "Benchmark terminal command timed out inside the sandbox."
                    : "Benchmark terminal command timed out and sandbox cleanup could not be confirmed."
                : exitCode == 0
                    ? "Benchmark terminal command completed inside the sandbox."
                    : $"Sandbox terminal command exited with code {exitCode}: {Truncate(stderr)}";
            return new(outputExceeded || workspaceLimitExceeded ? -1 : exitCode,
                timedOut || outputExceeded || workspaceLimitExceeded,
                stopwatch.Elapsed.TotalMilliseconds, stdout, stderr, message);
        }
    }

    private static async Task<bool> MonitorWorkspaceAsync(
        string workspace, string containerName, Process process, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !process.HasExited)
            {
                if (!WorkspaceWithinLimits(workspace))
                {
                    await KillContainerAsync(containerName);
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return true;
                }
                await Task.Delay(250, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        return false;
    }

    private static bool WorkspaceWithinLimits(string workspace)
    {
        if (!Directory.Exists(workspace) || DockerBenchmarkCommandRunner.IsReparsePoint(workspace)) return false;
        long bytes = 0;
        var files = 0;
        var directories = 0;
        try
        {
            var pending = new Stack<string>();
            pending.Push(workspace);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (DockerBenchmarkCommandRunner.IsReparsePoint(file)) return false;
                    var length = new FileInfo(file).Length;
                    if (++files > DockerBenchmarkCommandRunner.MaxStagingFiles ||
                        length > DockerBenchmarkCommandRunner.MaxStagingBytes - bytes) return false;
                    bytes += length;
                }
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if (DockerBenchmarkCommandRunner.IsReparsePoint(child) ||
                        ++directories > DockerBenchmarkCommandRunner.MaxStagingDirectories) return false;
                    pending.Push(child);
                }
            }
            return true;
        }
        catch { return false; }
    }

    private static void RemoveMissingEntries(string source, string destination)
    {
        if (DockerBenchmarkCommandRunner.IsReparsePoint(destination))
            throw new InvalidOperationException("The benchmark workspace contains an unsafe filesystem link.");

        foreach (var file in Directory.EnumerateFiles(destination))
        {
            if (DockerBenchmarkCommandRunner.IsReparsePoint(file))
                throw new InvalidOperationException("The benchmark workspace contains an unsafe filesystem link.");
            if (!File.Exists(Path.Combine(source, Path.GetFileName(file))))
                File.Delete(file);
        }
        foreach (var directory in Directory.EnumerateDirectories(destination))
        {
            if (DockerBenchmarkCommandRunner.IsReparsePoint(directory))
                throw new InvalidOperationException("The benchmark workspace contains an unsafe filesystem link.");
            var sourceChild = Path.Combine(source, Path.GetFileName(directory));
            if (!Directory.Exists(sourceChild))
                Directory.Delete(directory, recursive: true);
            else
                RemoveMissingEntries(sourceChild, directory);
        }
    }

    private static async Task<bool> KillContainerAsync(string containerName)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await RunDockerControlAsync("kill", containerName);
            if (await RunDockerControlAsync("rm", "-f", containerName)) return true;
            var exists = await InspectContainerAsync(containerName);
            if (exists == false) return true;
            await Task.Delay(100 * (attempt + 1));
        }
        return await InspectContainerAsync(containerName) == false;
    }

    private static async Task<bool> RunDockerControlAsync(params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);
            using var process = Process.Start(psi);
            if (process == null) return false;
            await process.WaitForExitAsync(CancellationToken.None);
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<bool?> InspectContainerAsync(string containerName)
    {
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("inspect");
            psi.ArgumentList.Add(containerName);
            using var process = Process.Start(psi);
            if (process == null) return null;
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(CancellationToken.None);
            if (process.ExitCode == 0) return true;
            if (error.Contains("No such object", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("not found", StringComparison.OrdinalIgnoreCase)) return false;
            return null;
        }
        catch { return null; }
    }

    private static async Task<string> ReadLimitedAsync(
        StreamReader reader, int maxCharacters, Func<Task> onLimit)
    {
        var buffer = new char[4096];
        var output = new StringBuilder(Math.Min(maxCharacters, 4096));
        var limitSignalled = false;
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            if (output.Length < maxCharacters)
            {
                var accepted = Math.Min(read, maxCharacters - output.Length);
                output.Append(buffer, 0, accepted);
                if (!limitSignalled && output.Length >= maxCharacters)
                {
                    limitSignalled = true;
                    await onLimit();
                }
            }
        }
        return output.ToString();
    }

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500] + "…";
}
