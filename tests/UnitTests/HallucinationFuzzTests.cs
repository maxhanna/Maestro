using System.Reflection;
using System.Text;
using Xunit;
using Weaver.Controllers;

namespace Weaver.UnitTests;

/// <summary>
/// Seeded-fuzz corpus for <c>AgentController.DetectHallucination</c> (wall-of-text +
/// semantic repetition) and its streaming cousin <c>CheckStreamingHallucination</c>.
///
/// The detector's two branches are threshold-heavy: the wall-of-text branch fires only
/// when <c>len &gt; 2000</c> (streaming: &gt; 2500) AND newline ratio &lt; 0.001, and the
/// semantic-repetition branch fires only when a 120-char trimmed substring appears 3+
/// times on a 40-char sampling grid. Both thresholds were hand-tuned after a false
/// positive on a web-search task, so a regression that nudges any of them (gate sizes,
/// the 0.001 ratio, the 40-char step, the 120-char window, the count-3 threshold) must
/// be caught across a spread of random sizes and densities — not just the handful of
/// hand-picked shapes in HallucinationDetectionTests.
///
/// Every doc derives from <see cref="FuzzHarness.SeededRng"/> so the corpus is
/// byte-identical across runs and machines, and follows the shared guard discipline:
/// exact doc counts (AssertAllDocsChecked), aperiodic filler (a wall doc can never trip
/// the repetition branch — that would make the density assertions vacuous), branch-hit
/// tallies (both detector branches must actually fire), and a seedability assert.
/// </summary>
public class HallucinationFuzzTests
{
    // Must stay a multiple of 3: the density sweep cycles 3 bands (i % 3) and the branch
    // counter's tolerance is docCount/3 - 1, so a non-multiple count can spuriously fail a band.
    private const int WallSeed = 90_113;
    private const int WallPrime = 104_743;
    private const int WallDocCount = 120;

    private const int RepSeed = 45_307;
    private const int RepPrime = 104_729;
    private const int RepDocCount = 80;

    private const int StreamSeed = 71_917;
    private const int StreamPrime = 104_701;
    private const int StreamDocCount = 80;

    private const double MinNewlineRatio = 0.001; // must mirror WallOfTextMinNewlineRatio

    private static readonly MethodInfo DetectMethod = typeof(AgentController).GetMethod(
        "DetectHallucination", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo DetectStreamingMethod = typeof(AgentController).GetMethod(
        "CheckStreamingHallucination", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static string? Detect(string text) =>
        (string?)DetectMethod.Invoke(null, new object[] { text });

    private static string? DetectStreaming(string text) =>
        (string?)DetectStreamingMethod.Invoke(null, new object[] { new StringBuilder(text) });

    // ── Wall-of-text: density sweep across random sizes ─────────────────────

    [Fact]
    public void WallOfText_DensitySweep_MatchesThresholdEverywhere()
    {
        var branches = new BranchHitCounter<string>(
            new[] { "wall-fired", "wall-safe", "gate-safe" }, "hallucination wall");
        var checkedDocs = 0;

        for (var i = 0; i < WallDocCount; i++)
        {
            var rng = FuzzHarness.SeededRng(WallSeed, i, WallPrime);
            var band = i % 3;
            if (band == 0)
            {
                // Sub-threshold wall: ratio strictly below 0.001 → MUST fire.
                var size = 2100 + rng.Next(28_000);
                var ratio = 0.0002 + rng.NextDouble() * (MinNewlineRatio - 0.0004); // [0.0002, 0.0008)
                var newlines = Math.Max(0, (int)Math.Floor(size * ratio));
                var doc = FuzzHarness.BuildHallucinationProse(rng, size, newlines);
                Assert.True(size > 2000, $"wall doc #{i} below the 2000-char gate ({size})");
                var err = Detect(doc);
                Assert.NotNull(err);
                Assert.Contains("wall of text", err);
                branches.Hit("wall-fired");
            }
            else if (band == 1)
            {
                // Above-threshold dense prose: ratio ≥ 0.001 + margin → MUST NOT fire.
                var size = 2100 + rng.Next(28_000);
                var ratio = MinNewlineRatio + 0.0002 + rng.NextDouble() * 0.02;
                var newlines = Math.Max(1, (int)Math.Ceiling(size * ratio));
                var doc = FuzzHarness.BuildHallucinationProse(rng, size, newlines);
                var err = Detect(doc);
                Assert.Null(err);
                branches.Hit("wall-safe");
            }
            else
            {
                // Below the 2000-char gate: never fires regardless of density (0..1 newline).
                var size = 1010 + rng.Next(980); // [1010, 1989] — over the 1000 early gate, under 2000
                var newlines = rng.Next(2);
                var doc = FuzzHarness.BuildHallucinationProse(rng, size, newlines);
                Assert.Null(Detect(doc));
                branches.Hit("gate-safe");
            }
            checkedDocs++;
        }

        FuzzHarness.AssertAllDocsChecked(checkedDocs, WallDocCount, "wall-of-text sweep");
        branches.AssertAllExercised(WallDocCount, 3);
    }

    [Fact]
    public void WallOfText_ExactlyAtThreshold_IsNotFlagged()
    {
        // ratio exactly 0.001 (3000 chars, 3 newlines) is the strict-< boundary — locks the
        // strictness across a few sizes so a <= refactor flipping it gets caught deterministically.
        foreach (var (size, newlines) in new[] { (3000, 3), (5000, 5), (7000, 7) })
        {
            Assert.Null(Detect(FuzzHarness.BuildHallucinationProse(
                FuzzHarness.SeededRng(size, 0, 3), size, newlines)));
        }
    }

    // ── Semantic repetition: block sweep across sizes/repeats ───────────────

    [Fact]
    public void SemanticRepetition_BlockSweep_FiresEverywhere()
    {
        var branches = new BranchHitCounter<string>(
            new[] { "rep-fired" }, "hallucination repetition");
        var checkedDocs = 0;

        for (var i = 0; i < RepDocCount; i++)
        {
            var rng = FuzzHarness.SeededRng(RepSeed, i, RepPrime);
            // Block length is a multiple of the 40-char sampling step (120..640) so every
            // block boundary lands on a sampling window and the same 120-char substring is
            // seen 3+ times. Total length stays in [1000, 1980]: over the 1000-char early
            // gate and under the 2000-char wall gate (which runs FIRST and would otherwise
            // mask the repetition branch — the doc has no newlines, so ratio = 0).
            var blockLen = 120 + rng.Next(14) * 40;
            var minRepeats = Math.Max(3, (int)Math.Ceiling(1000.0 / blockLen));
            var maxRepeats = 1980 / blockLen;
            var repeats = minRepeats + rng.Next(Math.Max(1, maxRepeats - minRepeats + 1));
            var doc = FuzzHarness.BuildHallucinationRepetition(rng, blockLen, repeats);
            Assert.True(doc.Length >= 1000 && doc.Length <= 1980,
                $"rep doc #{i} outside safe band: blockLen={blockLen} repeats={repeats} len={doc.Length}");
            var err = Detect(doc);
            Assert.NotNull(err);
            Assert.Contains("semantic repetition", err);
            branches.Hit("rep-fired");
            checkedDocs++;
        }

        FuzzHarness.AssertAllDocsChecked(checkedDocs, RepDocCount, "semantic-repetition sweep");
        branches.AssertAllExercised(RepDocCount, 1);
    }

    [Fact]
    public void SemanticRepetition_WithEmbeddedWhitespace_ExercisesTrimPath()
    {
        // The no-whitespace sweep never exercises the detector's Trim() / trimmed-length
        // accounting. Space every 3-4 chars (block first 120 chars still trim to >= 60),
        // blockLen multiple of 40 so boundary windows align — repetition must still fire.
        for (var i = 0; i < 20; i++)
        {
            var rng = FuzzHarness.SeededRng(RepSeed + 3, i, RepPrime);
            var blockLen = 120 + rng.Next(10) * 40; // 120..480
            var spaceEvery = 3 + rng.Next(2); // 3 or 4
            var minRepeats = Math.Max(3, (int)Math.Ceiling(1000.0 / blockLen));
            var maxRepeats = 1980 / blockLen;
            var repeats = minRepeats + rng.Next(Math.Max(1, maxRepeats - minRepeats + 1));
            var doc = FuzzHarness.BuildHallucinationRepetitionWithWhitespace(
                rng, blockLen, repeats, spaceEvery);
            Assert.True(doc.Length >= 1000 && doc.Length <= 1980,
                $"ws rep doc #{i} outside safe band: blockLen={blockLen} repeats={repeats} len={doc.Length}");
            var err = Detect(doc);
            Assert.NotNull(err);
            Assert.Contains("semantic repetition", err);
        }
    }

    // ── Gate boundaries: the strict > / < semantics on both size gates ──────

    [Fact]
    public void WallGate_ExactlyAtBoundaries_IsConsistent()
    {
        // DetectHallucination wall check is len > 2000 (strict). 2000 must NOT fire even at
        // ratio 0 (zero newlines); 2001 with a sub-threshold ratio MUST fire.
        Assert.Null(Detect(FuzzHarness.BuildHallucinationProse(
            FuzzHarness.SeededRng(2000, 1, 3), 2000, 0)));
        var err = Detect(FuzzHarness.BuildHallucinationProse(
            FuzzHarness.SeededRng(2001, 2, 3), 2001, 1));
        Assert.NotNull(err);
        Assert.Contains("wall of text", err);
    }

    [Fact]
    public void StreamingGate_ExactlyAtBoundaries_IsConsistent()
    {
        // CheckStreamingHallucination gate is len < 2500 → null. 2500 proceeds to the check:
        // sub-threshold ratio fires, above-threshold is safe.
        var fired = DetectStreaming(FuzzHarness.BuildHallucinationProse(
            FuzzHarness.SeededRng(2500, 1, 3), 2500, 1));
        Assert.NotNull(fired);
        Assert.Contains("wall of text", fired);
        Assert.Null(DetectStreaming(FuzzHarness.BuildHallucinationProse(
            FuzzHarness.SeededRng(2500, 2, 3), 2500, 4)));
    }

    // ── Streaming cousin shares the 0.001 ratio constant ────────────────────

    [Fact]
    public void Streaming_SharedRatioConstant_HoldsAcrossSizes()
    {
        var branches = new BranchHitCounter<string>(
            new[] { "stream-fired", "stream-safe" }, "hallucination streaming");
        var checkedDocs = 0;

        for (var i = 0; i < StreamDocCount; i++)
        {
            var rng = FuzzHarness.SeededRng(StreamSeed, i, StreamPrime);
            var size = 2600 + rng.Next(28_000);
            if (i % 2 == 0)
            {
                var ratio = 0.0002 + rng.NextDouble() * (MinNewlineRatio - 0.0004);
                var newlines = Math.Max(0, (int)Math.Floor(size * ratio));
                var err = DetectStreaming(FuzzHarness.BuildHallucinationProse(rng, size, newlines));
                Assert.NotNull(err);
                Assert.Contains("wall of text", err);
                branches.Hit("stream-fired");
            }
            else
            {
                var ratio = MinNewlineRatio + 0.0002 + rng.NextDouble() * 0.02;
                var newlines = Math.Max(1, (int)Math.Ceiling(size * ratio));
                Assert.Null(DetectStreaming(FuzzHarness.BuildHallucinationProse(rng, size, newlines)));
                branches.Hit("stream-safe");
            }
            checkedDocs++;
        }

        FuzzHarness.AssertAllDocsChecked(checkedDocs, StreamDocCount, "streaming sweep");
        branches.AssertAllExercised(StreamDocCount, 2);
    }

    [Fact]
    public void Streaming_Below2500Gate_NeverFires()
    {
        for (var i = 0; i < 20; i++)
        {
            var rng = FuzzHarness.SeededRng(StreamSeed + 1, i, StreamPrime);
            var size = 1000 + rng.Next(1490); // [1000, 2489] — under the 2500 streaming gate
            var doc = FuzzHarness.BuildHallucinationProse(rng, size, rng.Next(3));
            Assert.Null(DetectStreaming(doc));
        }
    }

    // ── Corpus integrity: seedable and branch-covering ──────────────────────

    [Fact]
    public void Corpus_IsSeedable_ByteIdenticalAcrossRebuilds()
    {
        FuzzHarness.AssertSeedableDeterminism(
            rng => FuzzHarness.BuildHallucinationProse(rng, 4000 + rng.Next(6000), rng.Next(8)),
            WallSeed, 3, WallPrime, "wall corpus");
        FuzzHarness.AssertSeedableDeterminism(
            rng => FuzzHarness.BuildHallucinationRepetition(rng, 160, 8),
            RepSeed, 5, RepPrime, "repetition corpus");
        FuzzHarness.AssertSeedableDeterminism(
            rng => FuzzHarness.BuildHallucinationProse(rng, 5000, rng.Next(10)),
            StreamSeed, 7, StreamPrime, "streaming corpus");
    }

    [Fact]
    public void WallProse_IsAperiodic_DoesNotTripRepetitionBranch()
    {
        // The wall corpus's prose docs must be aperiodic: if the seeded filler ever repeated a
        // 120-char window 3+ times, a density doc would be flagged for SEMANTIC REPETITION
        // instead of passing cleanly, and the density assertions would go vacuous. Sample a
        // spread of large wall-safe docs and assert NONE trips the repetition branch.
        for (var i = 0; i < 30; i++)
        {
            var rng = FuzzHarness.SeededRng(WallSeed + 2, i, WallPrime);
            var size = 3000 + rng.Next(25_000);
            var ratio = MinNewlineRatio + 0.0002 + rng.NextDouble() * 0.02;
            var newlines = Math.Max(1, (int)Math.Ceiling(size * ratio));
            var err = Detect(FuzzHarness.BuildHallucinationProse(rng, size, newlines));
            Assert.Null(err);
        }
    }
}
