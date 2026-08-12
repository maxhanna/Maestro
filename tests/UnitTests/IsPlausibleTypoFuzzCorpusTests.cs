using System.Reflection;
using System.Text;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Seeded fuzz corpus for <c>AgentEditHeuristics.IsPlausibleTypo</c> — the conservative
/// letter-dropping typo check that anchors the hallucinated-property guard ('ested' vs
/// 'estimated'). Every doc is a REAL property name as it would appear in a file; the corpus
/// asserts the guard's two halves with opposite strictness:
///   • GENUINE names must NEVER be flagged (zero false positives): the word itself, plural /
///     Array extensions (items / itemArray next to item), longer names, different-first-letter
///     names, and same-length names carrying a letter absent from the word.
///   • DROPPED-LETTER variants must ALL be caught: every subsequence that drops 1–4 letters
///     (keeping the first character, ≥ 4 chars) is a plausible typo; dropping 5+ letters or
///     dropping the first character is outside the guard's contract and must NOT fire.
/// Following the FuzzHarness discipline: a fixed (seed, prime) drives a per-doc RNG so the
/// corpus is byte-identical across runs and machines, and the AssertAllDocsChecked /
/// AssertExercised guards fail loudly if a doc or a bucket is silently skipped.
/// </summary>
public class IsPlausibleTypoFuzzCorpusTests
{
    // Unique (seed, prime) for this corpus — no other corpus shares this doc stream.
    private const int Seed = 0x7A5C;
    private const int Prime = 97;

    // The end-to-end corpora below drive DetectHallucinatedProperties — the guard the apply
    // pipeline actually calls — instead of the raw heuristic, so they get their OWN (seed, prime)
    // streams: no two corpora share an RNG doc stream.
    private const int EndToEndSeed = 0x7A5D;
    private const int EndToEndPrime = 101;
    private const int HtmlSeed = 0x7A5E;
    private const int HtmlPrime = 103;

    /// <summary>Each doc is one "word already present in the file" — camelCase property names
    /// long enough (≥ 5 chars) to carry dropped-letter variants under the ≥ 4/≤ 4 contract.</summary>
    private static readonly string[] SeedWords =
    {
        "estimated",     // the real 'ested' vs 'estimated' hallucination
        "retryCount",
        "maxAttempts",
        "deliveryStatus",
        "isLoading",
        "componentName",
        "createdAt",
        "sessionToken"
    };

    /// <summary>Plural / collection / variant suffixes — all LONGER than the word, so the
    /// guard must never treat them as typos of it.</summary>
    private static readonly string[] GenuineSuffixes =
    {
        "s", "es", "ies", "Array", "List", "Count", "Map", "Value", "Model", "Dto"
    };

    /// <summary>Realistic property names with diverse first letters, used as unrelated
    /// genuine names (first letter != the word's first letter guarantees no same-first-char
    /// subsequence can fire).</summary>
    private static readonly string[] UnrelatedPool =
    {
        "total", "value", "message", "status", "label", "count", "error", "updated",
        "visible", "enabled", "active", "complete", "valid", "pending", "source",
        "target", "payload", "request", "response", "header", "body", "offset",
        "limit", "totalCount", "hasError", "user", "session", "token", "record", "row"
    };

    private static readonly MethodInfo TypoMethod = typeof(AgentEditHeuristics).GetMethod(
        "IsPlausibleTypo", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("IsPlausibleTypo not found");

    private static bool IsPlausibleTypo(string prop, string word) =>
        (bool)TypoMethod.Invoke(null, new object[] { prop, word })!;

    /// <summary>The guard's public entry point — the exact call the apply pipeline makes
    /// (AgentController.ApplyEdit passes oldStr/newStr/fileContent/relPath plus the resolved
    /// sibling .ts for HTML edits).</summary>
    private static string? Guard(string oldStr, string newStr, string fileContent, string relPath,
        string? relatedFileContent = null) =>
        AgentEditHeuristics.DetectHallucinatedProperties(oldStr, newStr, fileContent, relPath, relatedFileContent);

    /// <summary>A realistic model file whose declared members are <paramref name="word"/> and
    /// <c>ready</c> — so the guard's known-word set contains the real name exactly as it would
    /// after a genuine edit to a file that already uses the property.</summary>
    private static string TsDocBody(string word, bool withPlural = false, bool withArray = false)
    {
        var plural = withPlural ? $"\n    public {word}s: string[] = [];" : "";
        var array = withArray ? $"\n    public {word}Array: string[] = [];" : "";
        return $"export class Doc {{\n    public {word}: string = \"\";\n    public ready: boolean = false;{plural}{array}\n}}\n";
    }

    /// <summary>oldString for a read-position edit: only the real member is referenced, so the
    /// real name is NOT introduced by the edit.</summary>
    private static string ReadOld(string word) =>
        $"public get value(): string {{ return this.{word}; }}";

    /// <summary>newString for a read-position edit: the real member PLUS an introduced prop — the
    /// introduced prop must be judged against the file's known words.</summary>
    private static string ReadNew(string word, string prop) =>
        $"public get value(): string {{ return this.{word} + this.{prop}; }}";

    [Fact]
    public void Corpus_ZeroFalsePositivesOnGenuineNames_AndEveryDroppedLetterVariantCaught()
    {
        var docsChecked = 0;
        var falsePositives = 0;   // genuine names — must be 0 hits
        var typoCases = 0;        // dropped-letter variants — must all fire
        var overDropCases = 0;    // > 4 dropped letters — must NOT fire

        for (var docIdx = 0; docIdx < SeedWords.Length; docIdx++)
        {
            var word = SeedWords[docIdx];
            var rng = FuzzHarness.SeededRng(Seed, docIdx, Prime);

            // ── False-positive bucket: genuine property names ──────────────────────────
            Assert.False(IsPlausibleTypo(word, word),
                $"identity must not be a typo of itself (doc #{docIdx}, word '{word}')");
            falsePositives++;

            foreach (var suffix in GenuineSuffixes)
            {
                var longer = word + suffix;
                Assert.False(IsPlausibleTypo(longer, word),
                    $"plural/Array variant '{longer}' must not be flagged as a typo of '{word}'");
                falsePositives++;
            }

            var differentFirstLetter = "x" + word[1..];
            Assert.False(IsPlausibleTypo(differentFirstLetter, word),
                $"different-first-letter name '{differentFirstLetter}' must not be flagged against '{word}'");
            falsePositives++;

            // 20 unrelated names, each starting with a different first letter than the word
            // (guaranteed outside the same-first-char contract, so a miss can never be a
            // coincidental subsequence).
            var unrelated = UnrelatedPool.Where(n => n[0] != word[0]).ToArray();
            for (var i = 0; i < 20; i++)
            {
                var name = unrelated[rng.Next(unrelated.Length)];
                Assert.False(IsPlausibleTypo(name, word),
                    $"unrelated name '{name}' must not be flagged as a typo of '{word}'");
                falsePositives++;
            }

            // 5 same-length names with the SAME first char but one middle letter replaced by
            // a letter ABSENT from the word — a genuine name can never be a subsequence of a
            // word it doesn't contain, so this is a guaranteed non-typo by construction.
            var absentLetters = "abcdefghijklmnopqrstuvwxyz"
                .Where(c => !word.Contains(c)).ToArray();
            Assert.NotEmpty(absentLetters);
            for (var i = 0; i < 5; i++)
            {
                var replaceAt = rng.Next(1, word.Length - 1);
                var replaced = word[..replaceAt] + absentLetters[rng.Next(absentLetters.Length)] + word[(replaceAt + 1)..];
                Assert.False(IsPlausibleTypo(replaced, word),
                    $"same-length name '{replaced}' carries a letter absent from '{word}' — must not be flagged");
                falsePositives++;
            }

            // ── True-positive bucket: EVERY dropped-letter variant (drop 1..min(4, len-4)) ──
            var variants = DropLetterVariants(word).ToList();
            foreach (var variant in variants)
            {
                Assert.True(IsPlausibleTypo(variant, word),
                    $"dropped-letter variant '{variant}' of '{word}' must be caught");
                typoCases++;
            }

            // ── Boundary bucket: dropping 5+ letters (still ≥ 4 chars) must NOT fire — the
            //    ≤ 4-dropped-letters bound is part of the guard's conservatism contract. ──
            foreach (var overDrop in OverDropVariants(word))
            {
                Assert.False(IsPlausibleTypo(overDrop, word),
                    $"dropping 5+ letters from '{word}' gives '{overDrop}' — outside the guard's typo contract");
                overDropCases++;
            }

            docsChecked++;
        }

        FuzzHarness.AssertAllDocsChecked(docsChecked, SeedWords.Length, "IsPlausibleTypo corpus");
        FuzzHarness.AssertExercised(typoCases,
            "the corpus must actually exercise dropped-letter typo detection — seed words too short?");
        FuzzHarness.AssertExercised(overDropCases,
            "the corpus must exercise the >4-drop boundary — seed words too short?");
        FuzzHarness.AssertExercised(falsePositives,
            "the corpus must exercise the genuine-name buckets");
    }

    /// <summary>
    /// End-to-end half of the corpus: the SAME seed words and buckets, but driven through
    /// <c>AgentEditHeuristics.DetectHallucinatedProperties</c> — the guard the apply pipeline
    /// actually calls — instead of the raw heuristic. This locks the guarantees at the point
    /// where a hallucinated edit gets wiped:
    ///   • GENUINE names must never be wiped: an existing member referenced as-is, a new member
    ///     explicitly declared in the same edit, plural/Array variants that ALREADY exist in the
    ///     file, longer non-similar names, different-first-letter names, and same-length names
    ///     carrying a letter absent from the word all return null (the edit proceeds).
    ///   • DROPPED-LETTER variants must ALL be wiped: every subsequence dropping 1–4 letters
    ///     (same first char, ≥ 4 chars) introduced in a read position is rejected with
    ///     "did you mean", because the real name sits in the file's known-word set.
    ///   • The guard's OTHER similarity rule — plural/Array/List EXTENSIONS of an existing word
    ///     introduced WITHOUT being present in the file or declared in the edit — must ALSO be
    ///     rejected. That is the guard's 'pluralizing' hallucination class, which the raw
    ///     IsPlausibleTypo corpus deliberately does NOT assert (IsPlausibleTypo alone never flags
    ///     a LONGER name); the end-to-end guard's contract is stricter and is locked here.
    ///   • The &gt; 4-drop boundary must still NOT fire end-to-end.
    /// </summary>
    [Fact]
    public void Corpus_DetectHallucinatedPropertiesEndToEnd_ZeroFalsePositives_EveryTypoCaught()
    {
        var docsChecked = 0;
        var falsePositives = 0;       // genuine names — must be 0 rejections
        var typoCases = 0;            // dropped-letter variants — must ALL be rejected
        var pluralExtensionCases = 0; // plural/Array/List extensions absent from file — must be rejected
        var overDropCases = 0;        // > 4 dropped letters — must NOT be rejected
        const string relPath = "src/app/doc.model.ts";

        for (var docIdx = 0; docIdx < SeedWords.Length; docIdx++)
        {
            var word = SeedWords[docIdx];
            var rng = FuzzHarness.SeededRng(EndToEndSeed, docIdx, EndToEndPrime);
            var fileContent = TsDocBody(word);

            // ── False-positive bucket: genuine names must never be wiped ─────────────────
            // (1) Referencing a member that already exists in the file.
            var result = Guard(ReadOld("ready"), $"public get value(): string {{ return this.ready + this.{word}; }}",
                fileContent, relPath);
            Assert.True(result is null,
                $"existing member '{word}' referenced as-is must pass (doc #{docIdx}) — got: {result}");
            falsePositives++;

            // (2) Explicitly declaring the new member in the same edit — the guard's documented
            //     escape hatch for genuinely-new collections.
            result = Guard(
                "constructor() { this.ready = false; }",
                $"constructor() {{ this.ready = false; this.{word}s = []; }}",
                fileContent, relPath);
            Assert.True(result is null,
                $"new '{word}s' collection declared in the same edit must pass (doc #{docIdx}) — got: {result}");
            falsePositives++;

            // (3) Plural / Array variants that ALREADY exist in the file are real names.
            result = Guard(ReadOld(word), ReadNew(word, word + "s"), TsDocBody(word, withPlural: true), relPath);
            Assert.True(result is null,
                $"plural '{word}s' present in the file must pass (doc #{docIdx}) — got: {result}");
            result = Guard(ReadOld(word), ReadNew(word, word + "Array"), TsDocBody(word, withArray: true), relPath);
            Assert.True(result is null,
                $"Array variant '{word}Array' present in the file must pass (doc #{docIdx}) — got: {result}");
            falsePositives += 2;

            // (4) Longer names with suffixes OUTSIDE the guard's similarity rules (a longer name
            //     can never be a dropped-letter variant, and these are not s/es/Array/List
            //     extensions) must pass even when absent from the file.
            foreach (var suffix in new[] { "Value", "Model", "Dto", "Map", "Count" })
            {
                result = Guard(
                    "constructor() { this.ready = false; }",
                    $"constructor() {{ this.ready = this.{word}{suffix}; }}",
                    fileContent, relPath);
                Assert.True(result is null,
                    $"longer genuine name '{word}{suffix}' must pass (doc #{docIdx}) — got: {result}");
                falsePositives++;
            }

            // (5) Different-first-letter name — outside the same-first-char typo contract.
            var differentFirst = "x" + word[1..];
            result = Guard(
                "constructor() { this.ready = false; }",
                $"constructor() {{ this.ready = this.{differentFirst}; }}",
                fileContent, relPath);
            Assert.True(result is null,
                $"different-first-letter name '{differentFirst}' must pass (doc #{docIdx}) — got: {result}");
            falsePositives++;

            // (6) Same-length name carrying a letter ABSENT from the word — never a subsequence,
            //     so it can never be a plausible typo.
            var absentLetters = "abcdefghijklmnopqrstuvwxyz".Where(c => !word.Contains(c)).ToArray();
            Assert.NotEmpty(absentLetters);
            var replaceAt = rng.Next(1, word.Length - 1);
            var replaced = word[..replaceAt] + absentLetters[rng.Next(absentLetters.Length)] + word[(replaceAt + 1)..];
            result = Guard(
                "constructor() { this.ready = false; }",
                $"constructor() {{ this.ready = this.{replaced}; }}",
                fileContent, relPath);
            Assert.True(result is null,
                $"same-length name '{replaced}' carries a letter absent from '{word}' — must pass (doc #{docIdx}) — got: {result}");
            falsePositives++;

            // ── True-positive bucket: EVERY dropped-letter variant must be wiped ───────────
            foreach (var variant in DropLetterVariants(word))
            {
                var rejection = Guard(ReadOld(word), ReadNew(word, variant), fileContent, relPath);
                Assert.NotNull(rejection);
                Assert.Contains(variant, rejection);
                Assert.Contains("did you mean", rejection);
                typoCases++;
            }

            // ── True-positive bucket: plural/Array/List extensions ABSENT from the file are
            //    the guard's 'pluralizing' hallucination class — introduced without presence or
            //    declaration they must be wiped, naming the real singular word. ─────────────
            foreach (var suffix in new[] { "s", "es", "Array", "List" })
            {
                var plural = word + suffix;
                var rejection = Guard(ReadOld(word), ReadNew(word, plural), fileContent, relPath);
                Assert.NotNull(rejection);
                Assert.Contains(plural, rejection);
                pluralExtensionCases++;
            }

            // ── Boundary bucket: dropping 5+ letters must NOT fire end-to-end ──────────────
            foreach (var overDrop in OverDropVariants(word))
            {
                result = Guard(ReadOld(word), ReadNew(word, overDrop), fileContent, relPath);
                Assert.True(result is null,
                    $"dropping 5+ letters from '{word}' gives '{overDrop}' — outside the guard's typo contract (doc #{docIdx}) — got: {result}");
                overDropCases++;
            }

            docsChecked++;
        }

        FuzzHarness.AssertAllDocsChecked(docsChecked, SeedWords.Length, "DetectHallucinatedProperties end-to-end corpus");
        FuzzHarness.AssertExercised(typoCases,
            "the corpus must exercise dropped-letter detection end-to-end — seed words too short?");
        FuzzHarness.AssertExercised(pluralExtensionCases,
            "the corpus must exercise the plural-extension rejection — plural suffixes not produced?");
        FuzzHarness.AssertExercised(overDropCases,
            "the corpus must exercise the >4-drop boundary end-to-end — seed words too short?");
        FuzzHarness.AssertExercised(falsePositives,
            "the corpus must exercise the genuine-name buckets end-to-end");
    }

    /// <summary>
    /// The HTML surface of the guard, driven exactly as the apply pipeline calls it: the sibling
    /// component `.ts` is resolved and merged into the scan context. The REAL member name lives
    /// ONLY in the .ts (never in the template), so the whole-template word set depends on the
    /// cross-file merge — a binding referencing the declared member must pass, and every
    /// dropped-letter variant of it (seeded sample per doc) must be wiped with "did you mean".
    /// </summary>
    [Fact]
    public void Corpus_DetectHallucinatedPropertiesEndToEnd_HtmlSurfaceResolvesSiblingTs()
    {
        var docsChecked = 0;
        var declaredMemberPasses = 0;
        var variantRejections = 0;
        const string relPath = "src/app/comp.component.html";
        const string htmlFile = "<div>{{ vm.ready }}</div>";
        const string oldHtml = "<div>{{ vm.ready }}</div>";

        for (var docIdx = 0; docIdx < SeedWords.Length; docIdx++)
        {
            var word = SeedWords[docIdx];
            var rng = FuzzHarness.SeededRng(HtmlSeed, docIdx, HtmlPrime);
            var tsFile = $"export class Comp {{\n    public {word}: string = \"\";\n    public ready = false;\n}}";

            // Genuine: referencing the member declared in the sibling .ts must pass — the real
            // name never appears in the template, so only the merged .ts word set can exempt it.
            var result = Guard(oldHtml, $"<div>{{{{ vm.ready }}}} {{{{ vm.{word} }}}}</div>", htmlFile, relPath, tsFile);
            Assert.True(result is null,
                $"member '{word}' declared in the sibling .ts must pass when referenced in the template (doc #{docIdx}) — got: {result}");
            declaredMemberPasses++;

            // Every dropped-letter variant must be wiped — the real name exists ONLY in the .ts.
            foreach (var variant in DropLetterVariants(word).Take(3 + rng.Next(4)))
            {
                var rejection = Guard(oldHtml, $"<div>{{{{ vm.ready }}}} {{{{ vm.{variant} }}}}</div>", htmlFile, relPath, tsFile);
                Assert.NotNull(rejection);
                Assert.Contains(variant, rejection);
                Assert.Contains("did you mean", rejection);
                variantRejections++;
            }

            docsChecked++;
        }

        FuzzHarness.AssertAllDocsChecked(docsChecked, SeedWords.Length, "HTML-surface end-to-end corpus");
        FuzzHarness.AssertExercised(declaredMemberPasses,
            "the corpus must exercise the sibling-.ts genuine-member pass");
        FuzzHarness.AssertExercised(variantRejections,
            "the corpus must exercise cross-file dropped-letter detection on the HTML surface");
    }

    /// <summary>All subsequences of <paramref name="word"/> that keep the first character and
    /// drop 1–4 letters (result ≥ 4 chars) — every one of them is a plausible typo. Deduped:
    /// distinct drop-sets can yield the same string (repeated letters), and a duplicate
    /// assertion would inflate the exercised count.</summary>
    private static IEnumerable<string> DropLetterVariants(string word)
    {
        var rest = word.Substring(1);
        var seen = new HashSet<string>();
        var maxDrop = Math.Min(4, word.Length - 4);
        for (var drop = 1; drop <= maxDrop; drop++)
        {
            foreach (var dropIndices in Combinations(rest.Length, drop))
            {
                var sb = new StringBuilder(word.Length - drop);
                sb.Append(word[0]);
                for (var i = 0; i < rest.Length; i++)
                {
                    if (!dropIndices.Contains(i)) sb.Append(rest[i]);
                }
                if (sb.Length >= 4) seen.Add(sb.ToString());
            }
        }
        return seen;
    }

    /// <summary>Variants that drop MORE than 4 letters while still keeping ≥ 4 chars — the
    /// guard must refuse them (they are no longer a conservative letter-dropping typo).</summary>
    private static IEnumerable<string> OverDropVariants(string word)
    {
        var rest = word.Substring(1);
        for (var drop = 5; drop <= rest.Length - 3; drop++)
        {
            foreach (var dropIndices in Combinations(rest.Length, drop))
            {
                var sb = new StringBuilder(word.Length - drop);
                sb.Append(word[0]);
                for (var i = 0; i < rest.Length; i++)
                {
                    if (!dropIndices.Contains(i)) sb.Append(rest[i]);
                }
                yield return sb.ToString();
            }
        }
    }

    /// <summary>All k-combinations of indices 0..n-1 (the drop positions).</summary>
    private static IEnumerable<HashSet<int>> Combinations(int n, int k)
    {
        var current = new int[k];
        return Combine(0, 0);

        IEnumerable<HashSet<int>> Combine(int start, int depth)
        {
            if (depth == k)
            {
                yield return new HashSet<int>(current);
                yield break;
            }
            for (var i = start; i <= n - (k - depth); i++)
            {
                current[depth] = i;
                foreach (var combo in Combine(i + 1, depth + 1))
                    yield return combo;
            }
        }
    }
}
