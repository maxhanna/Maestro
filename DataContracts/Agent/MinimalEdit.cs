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
}

public class ApplyDiffRequest
{
    public string Project { get; set; } = "";
    public string DiffPath { get; set; } = "";
    // When set, the currently-applied diff is reversed FIRST (git apply --reverse)
    // before the target DiffPath is applied — this "swaps" the live edit on the
    // file to a different (e.g. earlier proposed or previously rejected) diff.
    public string SwapFrom { get; set; } = "";
}

public class DeleteDiffsRequest
{
    public string Project { get; set; } = "";
    public List<string> DiffPaths { get; set; } = new();
}

public class VerifyDiffsRequest
{
    public string Project { get; set; } = "";
    public List<string> DiffPaths { get; set; } = new();
}