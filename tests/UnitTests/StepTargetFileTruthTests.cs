using System.Reflection;
using System.Threading;
using Xunit;
using Weaver;
using Weaver.Controllers;
using Weaver.Services;

namespace Weaver.UnitTests;

/// <summary>
/// Locks StepTargetExistsInFileAsync — the file-truth check the duplicate-step guards
/// (interleaved token-overlap, replan "too similar", extra-step queueing) now consult
/// BEFORE rejecting a step. The live navigation.component.ts moviesTodoCount/getMoviesInfo
/// run exposed the brittleness it fixes: step 1's description promised a property AND a
/// method, but the deterministic member synthesis landed only the property — so the
/// token-overlap proxy rejected the still-missing getMoviesInfo() step as a duplicate of
/// the completed one, and the method never landed. The guards now ask "is the target
/// actually in the file?" and only reject when it is.
/// </summary>
public class StepTargetFileTruthTests
{
    private const string FileContent =
        "export class NavigationComponent {\n" +
        "  moviesTodoCount: number | null = null;\n" +
        "  musicTodoCount: number | null = null;\n" +
        "\n" +
        "  private async getMusicInfo() {\n" +
        "    this.musicTodoCount = 42;\n" +
        "  }\n" +
        "}\n";

    private static string WriteTempFile(string content)
    {
        // The steps reference src/app/navigation/navigation.component.ts — write the file at
        // that exact nested path under the temp root so the helper's path resolution finds it.
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "weaver-ft-" + Guid.NewGuid().ToString("N"));
        var nested = System.IO.Path.Combine(dir, "src", "app", "navigation");
        System.IO.Directory.CreateDirectory(nested);
        var path = System.IO.Path.Combine(nested, "navigation.component.ts");
        System.IO.File.WriteAllText(path, content);
        return dir;
    }

    private static bool Invoke(PlanStep step, string projectRoot)
    {
        var method = typeof(AgentController).GetMethod(
            "StepTargetExistsInFileAsync", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("StepTargetExistsInFileAsync not found");
        var task = (Task<bool>)method.Invoke(null, new object[] { step, projectRoot, CancellationToken.None })!;
        return task.GetAwaiter().GetResult();
    }

    private static PlanStep Step(string change, string? newString = null) => new()
    {
        File = "src/app/navigation/navigation.component.ts",
        Change = change,
        NewString = newString,
    };

    [Fact]
    public void MethodNamedInChange_ButNotInFile_IsNotImplemented()
    {
        // The live regression: the property landed but the method never did — the duplicate
        // guard must NOT treat the follow-up step as done.
        var root = WriteTempFile(FileContent);
        try
        {
            Assert.False(Invoke(Step("Add getMoviesInfo() method following same pattern as other info getters"), root));
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MethodNamedInChange_DeclaredInFile_IsImplemented()
    {
        var withMethod = FileContent.Replace(
            "  private async getMusicInfo() {",
            "  private async getMusicInfo() {\n    this.moviesTodoCount = 1;\n  }\n\n  private async getMoviesInfo() {");
        var root = WriteTempFile(withMethod);
        try
        {
            Assert.True(Invoke(Step("Add getMoviesInfo() method following same pattern as other info getters"), root));
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void NewStringCode_PresentInFile_IsImplemented()
    {
        var newString = "  private async getMoviesInfo() { this.moviesTodoCount = 1; }";
        var root = WriteTempFile(FileContent + "\n" + newString + "\n");
        try
        {
            Assert.True(Invoke(Step("Add getMoviesInfo() method", newString), root));
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PropertyNamedInChange_PresentInFile_IsImplemented()
    {
        var root = WriteTempFile(FileContent);
        try
        {
            Assert.True(Invoke(Step("Add moviesTodoCount property for tracking saved movie counts"), root));
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PropertyNamedInChange_AbsentFromFile_IsNotImplemented()
    {
        var root = WriteTempFile(FileContent.Replace("  moviesTodoCount: number | null = null;\n", ""));
        try
        {
            Assert.False(Invoke(Step("Add moviesTodoCount property for tracking saved movie counts"), root));
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MissingFile_IsNotImplemented()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "weaver-ft-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            Assert.False(Invoke(Step("Add getMoviesInfo() method"), dir));
        }
        finally { System.IO.Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void SpecialMarker_IsNotImplemented()
    {
        var root = WriteTempFile(FileContent);
        try
        {
            var marker = new PlanStep { File = "_command", Change = "run npm build" };
            Assert.False(Invoke(marker, root));
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }

    // ── FileTruthOverrideForRejectionAsync ──────────────────────────────────

    private static bool InvokeOverride(string reason, PlanStep step, string projectRoot)
    {
        var method = typeof(AgentController).GetMethod(
            "FileTruthOverrideForRejectionAsync", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("FileTruthOverrideForRejectionAsync not found");
        var task = (Task<bool>)method.Invoke(null, new object[] { reason, step, projectRoot, CancellationToken.None })!;
        return task.GetAwaiter().GetResult();
    }

    [Fact]
    public void Override_ReasonClaimsMissing_ButFileContainsIt_ReturnsTrue()
    {
        // The live regression verbatim: the validator claimed moviesTodoCount doesn't exist
        // even though step 1 had just added it — the stale-context rejection must be overridden.
        var root = WriteTempFile(FileContent);
        try
        {
            const string reason =
                "The proposed next step asks to add getMoviesInfo() method following the same pattern " +
                "as other info getters, but there's no existing moviesTodoCount property initialization " +
                "or usage shown in the code for movie counts like there is for music.";
            Assert.True(InvokeOverride(reason, Step("Add getMoviesInfo() method"), root));
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Override_ReasonClaimsMissing_AndFileReallyLacksIt_ReturnsFalse()
    {
        // The claim is TRUE — the symbol is genuinely absent — so no override.
        var root = WriteTempFile(FileContent.Replace("  moviesTodoCount: number | null = null;\n", ""));
        try
        {
            const string reason =
                "The step references moviesTodoCount but there is no existing moviesTodoCount property in the code.";
            Assert.False(InvokeOverride(reason, Step("Add getMoviesInfo() method"), root));
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Override_NoMissingPhrasing_ReturnsFalse()
    {
        // Even with an identifier the file contains, a reason that does NOT claim something
        // is missing must not trigger the override.
        var root = WriteTempFile(FileContent);
        try
        {
            Assert.False(InvokeOverride(
                "This step is scope creep and duplicates existing functionality.",
                Step("Add getMoviesInfo() method"), root));
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Override_DoesNotHave_Phrasing_OverrideTriggers()
    {
        // The "does not have" phrasing: moviesTodoCount does not have a getter —
        // the step is trying to ADD it. The file contains moviesTodoCount, so the
        // "does not have" claim about it is about a property that EXISTS. The override
        // should fire because the file-truth disproves the rejection premise.
        var root = WriteTempFile(FileContent);
        try
        {
            Assert.True(InvokeOverride(
                "moviesTodoCount does not have number pipe formatting applied to it.",
                Step("Format moviesTodoCount using number pipe"), root));
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Override_SpecialMarker_ReturnsFalse()
    {
        var root = WriteTempFile(FileContent);
        try
        {
            Assert.False(InvokeOverride(
                "No existing npm executable is missing from the code.",
                new PlanStep { File = "_command", Change = "run npm build" }, root));
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }
}
