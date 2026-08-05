namespace Weaver;

public class MinimalEditDto
{
    public string Path { get; set; } = "";
    public string OldString { get; set; } = "";
    public string NewString { get; set; } = "";
}

public class MinimalEditsEnvelope
{
    public List<MinimalEditDto> Edits { get; set; } = new();
}

public class ApplyEditsRequest
{
    public string Project { get; set; } = "";
    public List<EditAction> Edits { get; set; } = new();
    public List<CommandAction> Commands { get; set; } = new();
    public bool IsBenchmark { get; set; }
    public string? BenchmarkRunId { get; set; }
    public string? BenchmarkProjectRoot { get; set; }
}