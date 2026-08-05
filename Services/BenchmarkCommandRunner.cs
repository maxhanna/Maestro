using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Weaver.Services;

public interface IBenchmarkCommandRunner
{
    Task<CommandCheckOutcome> RunAsync(string command, string workingDirectory, int timeoutSeconds, CancellationToken ct);
}

/// <summary>
/// Executes the fixed benchmark verification commands in a disposable Docker container.
/// The source directory is copied to a temporary staging directory before mounting, so
/// commands never receive a host benchmark path and cannot modify the benchmark run.
/// </summary>
public sealed class DockerBenchmarkCommandRunner : IBenchmarkCommandRunner
{
    private const string DefaultImage = "python:3.12-alpine@sha256:236173eb74001afe2f60862de935b74fcbd00adfca247b2c27051a70a6a39a2d";
    private const int MaxOutputCharacters = 64 * 1024;
    internal const long MaxStagingBytes = 64L * 1024 * 1024;
    internal const int MaxStagingFiles = 4096;
    internal const int MaxStagingDirectories = 4096;
    private readonly string _image;

    public DockerBenchmarkCommandRunner(string image = DefaultImage)
    {
        if (string.IsNullOrWhiteSpace(image) || !image.Contains("@sha256:", StringComparison.Ordinal) ||
            image.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or ':' or '/' or '-' or '_' or '@')))
            throw new ArgumentException("Benchmark sandbox images must use a digest.", nameof(image));
        _image = image;
    }

    public string Image => _image;

    public static bool TryBuildPythonArguments(string command, out IReadOnlyList<string> arguments)
    {
        arguments = command switch
        {
            "python -m unittest discover" => ["-m", "unittest", "discover"],
            "python -m py_compile formatter.py" => ["-m", "py_compile", "formatter.py"],
            "python -m json.tool settings.json" => ["-m", "json.tool", "settings.json"],
            _ => Array.Empty<string>()
        };
        return arguments.Count > 0;
    }

    public async Task<CommandCheckOutcome> RunAsync(string command, string workingDirectory, int timeoutSeconds, CancellationToken ct)
    {
        if (!TryBuildPythonArguments(command, out var pythonArguments))
            return CommandCheckOutcome.Failed("Verification command is not allowed by the benchmark sandbox policy.");
        if (!Directory.Exists(workingDirectory))
            return CommandCheckOutcome.Failed("Verification working directory does not exist.");

        string stagingDirectory;
        try
        {
            stagingDirectory = CreateStagingCopy(workingDirectory);
        }
        catch (Exception ex)
        {
            return CommandCheckOutcome.Failed($"Benchmark sandbox staging failed: {ex.Message}");
        }

        try
        {
            return await RunContainerAsync(pythonArguments, stagingDirectory, Math.Clamp(timeoutSeconds, 1, 120), ct);
        }
        finally
        {
            try { Directory.Delete(stagingDirectory, recursive: true); } catch { }
        }
    }

    private async Task<CommandCheckOutcome> RunContainerAsync(
        IReadOnlyList<string> pythonArguments, string stagingDirectory, int timeoutSeconds, CancellationToken ct)
    {
        var containerName = $"weaver-benchmark-{Guid.NewGuid():N}";
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in BuildDockerArguments(containerName, stagingDirectory, pythonArguments, timeoutSeconds))
            psi.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return CommandCheckOutcome.Failed($"Benchmark sandbox unavailable: {ex.Message}");
        }
        if (process == null)
            return CommandCheckOutcome.Failed("Benchmark sandbox unavailable: Docker process could not be started.");

        using (process)
        {
        var stopwatch = Stopwatch.StartNew();
        var stdoutTask = ReadLimitedAsync(process.StandardOutput, MaxOutputCharacters);
        var stderrTask = ReadLimitedAsync(process.StandardError, MaxOutputCharacters);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 120)));
        var timedOut = false;
        var cleanupConfirmed = true;
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
            await KillContainerAsync(containerName);
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        stopwatch.Stop();
        var exitCode = process.HasExited ? process.ExitCode : -1;
        var message = timedOut
            ? cleanupConfirmed
                ? "Verification command timed out inside the benchmark sandbox."
                : "Verification command timed out and sandbox cleanup could not be confirmed."
            : exitCode == 0
                ? "Command succeeded inside the benchmark sandbox."
                : $"Sandbox command exited with code {exitCode}: {Truncate(stderr)}";
        return new(exitCode, timedOut, stopwatch.Elapsed.TotalMilliseconds, stdout, stderr, message);
        }
    }

    internal static string CreateStagingCopy(string sourceDirectory)
    {
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "weaver-benchmark-command", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            long bytes = 0;
            var files = 0;
            CopyDirectoryWithoutReparsePoints(sourceDirectory, stagingDirectory, ref bytes, ref files);
            return stagingDirectory;
        }
        catch
        {
            try { Directory.Delete(stagingDirectory, recursive: true); } catch { }
            throw;
        }
    }

    internal static void CopyDirectoryWithoutReparsePoints(string source, string destination, ref long bytes, ref int files)
    {
        if (IsReparsePoint(source) || (Directory.Exists(destination) && IsReparsePoint(destination)))
            throw new InvalidOperationException("The verification directory contains an unsafe filesystem link.");
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if (IsReparsePoint(file) || (File.Exists(Path.Combine(destination, Path.GetFileName(file))) &&
                    IsReparsePoint(Path.Combine(destination, Path.GetFileName(file)))))
                throw new InvalidOperationException("The verification directory contains an unsafe filesystem link.");
            var length = new FileInfo(file).Length;
            if (++files > MaxStagingFiles || length > MaxStagingBytes - bytes)
                throw new InvalidOperationException("The verification directory exceeds the sandbox staging limits.");
            CopyFileWithoutReparsePoint(file, Path.Combine(destination, Path.GetFileName(file)));
            bytes += length;
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if (IsReparsePoint(directory))
                throw new InvalidOperationException("The verification directory contains an unsafe filesystem link.");
            var child = Path.Combine(destination, Path.GetFileName(directory));
            if (Directory.Exists(child) && IsReparsePoint(child))
                throw new InvalidOperationException("The verification directory contains an unsafe filesystem link.");
            Directory.CreateDirectory(child);
            CopyDirectoryWithoutReparsePoints(directory, child, ref bytes, ref files);
        }
    }

    internal static void CopyFileWithoutReparsePoint(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        if (IsReparsePoint(source))
            throw new InvalidOperationException("The verification directory contains an unsafe filesystem link.");
        if (HasMultipleHardLinks(input.SafeFileHandle))
            throw new InvalidOperationException("The verification directory contains a hard-linked file.");
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
        input.CopyTo(output);
    }

    internal static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool HasMultipleHardLinks(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!GetFileInformationByHandle(handle, out var info)) return true;
        return info.NumberOfLinks > 1;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private IEnumerable<string> BuildDockerArguments(string containerName, string stagingDirectory, IReadOnlyList<string> pythonArguments, int timeoutSeconds)
    {
        yield return "run";
        yield return "--rm";
        yield return "--pull=never";
        yield return "--name";
        yield return containerName;
        yield return "--network";
        yield return "none";
        yield return "--read-only";
        yield return "--tmpfs";
        yield return "/tmp:rw,noexec,nosuid,size=64m";
        yield return "--pids-limit";
        yield return "64";
        yield return "--memory";
        yield return "256m";
        yield return "--cpus";
        yield return "1";
        yield return "--cap-drop";
        yield return "ALL";
        yield return "--security-opt";
        yield return "no-new-privileges";
        yield return "--init";
        yield return "--stop-timeout";
        yield return "2";
        yield return "--user";
        yield return "65532:65532";
        yield return "--mount";
        yield return $"type=bind,source={Path.GetFullPath(stagingDirectory)},target=/workspace,readonly";
        yield return "--workdir";
        yield return "/workspace";
        yield return "--env";
        yield return "PYTHONDONTWRITEBYTECODE=1";
        yield return "--env";
        yield return "PYTHONPYCACHEPREFIX=/tmp/pycache";
        yield return _image;
        yield return "timeout";
        yield return "-k";
        yield return "2";
        yield return (timeoutSeconds + 1).ToString();
        yield return "python";
        foreach (var argument in pythonArguments)
            yield return argument;
    }

    private static async Task<bool> KillContainerAsync(string containerName)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await RunDockerControlAsync("kill", containerName)) return true;
            if (await RunDockerControlAsync("rm", "-f", containerName)) return true;
            try { await Task.Delay(100 * (attempt + 1)); } catch { }
        }
        return false;
    }

    private static async Task<bool> RunDockerControlAsync(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("docker")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process == null) return false;
            await process.WaitForExitAsync(CancellationToken.None);
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, int maxCharacters)
    {
        var buffer = new char[4096];
        var output = new StringBuilder(Math.Min(maxCharacters, 4096));
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            if (output.Length < maxCharacters)
                output.Append(buffer, 0, Math.Min(read, maxCharacters - output.Length));
        }
        return output.ToString();
    }

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500] + "…";
}
