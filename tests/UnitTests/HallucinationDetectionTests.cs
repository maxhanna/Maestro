using System.Reflection;
using System.Text;
using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the hallucination detector's false-positive behavior. A real run
/// ("Search the web for an interesting and recent AI article…") had useful pre-plan
/// reasoning wrongly skipped as "Hallucination (wall of text)" — e.g. 2249 chars with
/// 8 line breaks (ratio 0.0036) and 2175 chars with 4 line breaks (ratio 0.0018) —
/// because the wall-of-text check tripped at fewer than 1 newline per 200 chars.
/// Dense single-paragraph prose is a normal output style for smaller models, so the
/// bar is now extreme (fewer than 1 newline per 1000 chars) and only genuinely
/// break-free walls plus semantic repetition abort.
/// </summary>
public class HallucinationDetectionTests
{
    private static readonly MethodInfo DetectWallMethod = typeof(AgentController).GetMethod(
        "DetectHallucination", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo DetectStreamingMethod = typeof(AgentController).GetMethod(
        "CheckStreamingHallucination", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static string? Detect(string text) =>
        (string?)DetectWallMethod.Invoke(null, new object[] { text });

    private static string? DetectStreaming(string text) =>
        (string?)DetectStreamingMethod.Invoke(null, new object[] { new StringBuilder(text) });

    /// <summary>
    /// Builds a dense non-repeating wall of the requested size with newlines sprinkled in.
    /// Content is pseudo-random (aperiodic) so these tests exercise ONLY the newline-density
    /// check — a periodic filler would also trip the semantic-repetition detector. Characters
    /// are replaced (not appended) so the total length is exactly `chars` and the ratio each
    /// test claims is the ratio the detector computes.
    /// </summary>
    private static string Prose(int chars, int newlines)
    {
        var sb = new StringBuilder(chars);
        var step = newlines > 0 ? chars / (newlines + 1) : int.MaxValue;
        for (var i = 0; i < chars; i++)
        {
            var c = (char)(33 + (i * 31 + (i / 7) * 13) % 90);
            if (newlines > 0 && i > 0 && i < chars - 1 && i % step == 0) c = '\n';
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ── The exact false-positive shapes from the web-search run ──────────────

    [Fact]
    public void DenseProse_2249Chars_8Breaks_IsNotFlagged() // ratio 0.0036 — was wrongly aborted
        => Assert.Null(Detect(Prose(2249, 8)));

    [Fact]
    public void DenseProse_2175Chars_4Breaks_IsNotFlagged() // ratio 0.0018 — was wrongly aborted
        => Assert.Null(Detect(Prose(2175, 4)));

    [Fact]
    public void DenseProse_2500Chars_12Breaks_IsNotFlagged() // ratio 0.0048 — upper prose band
        => Assert.Null(Detect(Prose(2500, 12)));

    [Fact]
    public void DenseProse_4000Chars_6Breaks_IsNotFlagged() // int division gives 7 breaks, ratio 0.00175 — above the bar
        => Assert.Null(Detect(Prose(4000, 6)));

    // ── The genuine stuck-ramble class still fires ───────────────────────────

    [Fact]
    public void ExtremeWall_3200Chars_2Breaks_IsFlagged() // ratio 0.0006 — genuinely break-free
    {
        var err = Detect(Prose(3200, 2));
        Assert.NotNull(err);
        Assert.Contains("Hallucination (wall of text)", err);
    }

    [Fact]
    public void ExtremeWall_Streaming_IsFlagged()
    {
        var err = DetectStreaming(Prose(3500, 1)); // ratio ~0.0003
        Assert.NotNull(err);
        Assert.Contains("Hallucination (wall of text)", err);
    }

    [Fact]
    public void DenseProse_Streaming_IsNotFlagged()
        => Assert.Null(DetectStreaming(Prose(3000, 8))); // ratio 0.0027

    // ── Semantic repetition remains a hard hallucination signal ──────────────

    [Fact]
    public void SemanticRepetition_RepeatedBlock_IsFlagged()
    {
        var block = new string('x', 130) + "\n\n";
        var text = string.Concat(Enumerable.Repeat(block, 8)); // 8×132 = 1056 chars → over the 1000-char early gate
        var err = Detect(text);
        Assert.NotNull(err);
        Assert.Contains("semantic repetition", err);
    }

    // ── Sanity guards ────────────────────────────────────────────────────────

    [Fact]
    public void ShortOrBlank_IsNeverFlagged()
    {
        Assert.Null(Detect(""));
        Assert.Null(Detect(null!));
        Assert.Null(Detect(Prose(900, 2)));
    }

    [Fact]
    public void Boundary_ExactlyAtThreshold_IsNotFlagged()
    {
        // 3000 chars with 3 newlines = ratio 0.001 exactly — the check is strict (< 0.001),
        // so exactly-at-threshold must NOT fire. Locks the strictness so a future <= refactor
        // silently flipping it gets caught.
        Assert.Null(Detect(Prose(3000, 3)));
        Assert.Null(DetectStreaming(Prose(3000, 3)));
    }
}
