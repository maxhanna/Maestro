using System.Runtime.InteropServices;

namespace Weaver;

/// <summary>
/// Snapshot of the machine a benchmark test run executed on. Captured so that
/// scores can be compared fairly across different hardware / OS configurations.
/// </summary>
public class EnvironmentMetadata
{
    public string Os { get; set; } = "";
    public string OsArchitecture { get; set; } = "";
    public int CpuCores { get; set; }
    public double RamGb { get; set; }
    public string MachineName { get; set; } = "";
    public string Runtime { get; set; } = "";

    /// <summary>WMI-detected CPU model string(s), e.g. "Intel Core i7-12700K (12 cores, 20 threads)".
    /// Windows-only; null elsewhere or if WMI is unavailable. Richer than <see cref="CpuCores"/>,
    /// which is portable but anonymous.</summary>
    public string? Cpu { get; set; }
    /// <summary>WMI-detected GPU model string(s). Windows-only.</summary>
    public string? Gpu { get; set; }
    /// <summary>WMI-detected physical RAM in bytes, when available — more precise than the
    /// GC-derived <see cref="RamGb"/> estimate.</summary>
    public long? RamBytes { get; set; }

    /// <summary>
    /// Collects machine metadata in a cross-platform way. RAM is reported from the
    /// GC's view of total available memory, which is a reasonable portable proxy for
    /// physical RAM (and respects container limits when running inside one).
    /// </summary>
    public static EnvironmentMetadata Collect()
    {
        double ramGb = 0;
        try
        {
            var totalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (totalBytes > 0)
                ramGb = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 1);
        }
        catch { /* best-effort */ }

        var metadata = new EnvironmentMetadata
        {
            Os = RuntimeInformation.OSDescription,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            CpuCores = Environment.ProcessorCount,
            RamGb = ramGb,
            MachineName = SafeMachineName(),
            Runtime = RuntimeInformation.FrameworkDescription
        };

        PopulateWindowsHardwareInfo(metadata);

        return metadata;
    }

    static string SafeMachineName()
    {
        try { return Environment.MachineName; }
        catch { return ""; }
    }

    /// <summary>Enriches with real CPU/GPU model names and exact RAM via WMI. Windows-only;
    /// a no-op elsewhere or if WMI is unavailable (e.g. non-admin, WMI service down).</summary>
    static void PopulateWindowsHardwareInfo(EnvironmentMetadata info)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_Processor");
            var cpus = new List<string>();
            foreach (var o in searcher.Get())
            {
                using var obj = o;
                var name = obj["Name"]?.ToString() ?? "";
                var cores = obj["NumberOfCores"]?.ToString() ?? "";
                var threads = obj["NumberOfLogicalProcessors"]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(name))
                    cpus.Add($"{name} ({cores} cores, {threads} threads)");
            }
            info.Cpu = cpus.Count > 0 ? string.Join("; ", cpus) : null;
        }
        catch { /* WMI not available */ }

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem");
            foreach (var o in searcher.Get())
            {
                using var obj = o;
                var ram = obj["TotalPhysicalMemory"]?.ToString();
                if (long.TryParse(ram, out var bytes))
                    info.RamBytes = bytes;
                break;
            }
        }
        catch { /* WMI not available */ }

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
            var gpus = new List<string>();
            foreach (var o in searcher.Get())
            {
                using var obj = o;
                var name = obj["Name"]?.ToString() ?? "";
                var ram = obj["AdapterRAM"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var entry = name;
                    if (long.TryParse(ram, out var ramBytes) && ramBytes > 0)
                        entry += $" ({ramBytes / 1024 / 1024} MB)";
                    gpus.Add(entry);
                }
            }
            info.Gpu = gpus.Count > 0 ? string.Join("; ", gpus) : null;
        }
        catch { /* WMI not available */ }
    }
}
