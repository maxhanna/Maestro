using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Seeded fuzz corpus for the plural/Array similarity CHAIN inside
/// <c>AgentEditHeuristics.DetectHallucinatedProperties</c> — the rules that flag an introduced
/// property as a hallucinated collection when it is the s/es/Array/List extension of a word
/// already in the file, or the singular of an existing plural:
///   (w + "s" == prop) || (w + "es" == prop) || (w + "Array" == prop) || (w + "List" == prop) ||
///   (prop + "s" == w) || (prop + "es" == w) || (prop + "Array" == w) || (prop + "List" == w)
///
/// The corpus asserts the GENUINE half of the contract: a collection that genuinely exists —
/// already declared in the file, declared in the same edit, or declared in the sibling component
/// .ts — must NEVER be flagged, even when its singular sibling sits in the file and would
/// otherwise tempt the chain. The recall half is locked too: the SAME extension introduced
/// without presence or declaration IS wiped with "did you mean", and so is a singular introduced
/// next to an existing plural. (Recall for these suffixes is provably chain-driven: the
/// extension is LONGER than its singular, so the typo rule can never fire — only the chain can.)
///
/// Every doc derives from <see cref="FuzzHarness.SeededRng"/> (byte-identical across runs) and
/// follows the shared discipline: AssertAllDocsChecked / AssertExercised so a silently-degraded
/// corpus or a never-exercised bucket fails loudly.
/// </summary>
public class PluralCollectionFuzzCorpusTests
{
    private const int Seed = 0x7B11;
    private const int Prime = 107;
    private const int DocCount = 32;

    // The four similarity rules the chain implements — each doc sweeps ALL of them.
    private static readonly string[] ChainSuffixes = { "s", "es", "Array", "List" };

    // Word factory: a realistic camelCase singular built from a stem + camel tail, long enough
    // that s/es/Array/List extensions are always distinct tokens and never collide with the
    // fixture's own words ('ready', 'export', 'class', 'string', ...).
    private static readonly string[] Stems =
    {
        "retry", "estimat", "deliver", "load", "session", "component", "create",
        "account", "flight", "bench", "user", "order", "pay", "search", "filter",
        "message", "attach", "comment", "total", "active", "pending", "record",
        "batch", "task", "note", "view", "history", "header", "request", "response",
        "settle", "upload", "refresh", "import", "render", "display", "toggle", "queue"
    };

    private static readonly string[] CamelTails =
    {
        "Count", "Status", "Date", "Id", "Name", "Type", "Value", "Time",
        "Key", "State", "Size", "Limit", "Offset", "Total", "Index", "Version"
    };

    /// <summary>The guard's public entry point — the exact call the apply pipeline makes
    /// (AgentController.ApplyEdit passes oldStr/newStr/fileContent/relPath plus the resolved
    /// sibling .ts for HTML edits).</summary>
    private static string? Guard(string oldStr, string newStr, string fileContent, string relPath,
        string? relatedFileContent = null) =>
        AgentEditHeuristics.DetectHallucinatedProperties(oldStr, newStr, fileContent, relPath, relatedFileContent);

    /// <summary>A model file declaring <paramref name="singular"/> (as the singular member),
    /// optionally followed by one collection extension — which extension is in the file is the
    /// point: the chain must not flag a collection that genuinely exists.</summary>
    private static string TsDoc(string singular, string? collection = null)
    {
        var extra = collection == null ? "" : $"\n    public {collection}: string[] = [];";
        return $"export class Doc {{\n    public {singular}: string = \"\";\n    public ready: boolean = false;{extra}\n}}\n";
    }

    /// <summary>oldString for a read-position edit referencing only <paramref name="word"/>.</summary>
    private static string ReadOld(string word) =>
        $"public get value(): string {{ return this.{word}; }}";

    /// <summary>newString for a read-position edit that adds <paramref name="prop"/> next to the
    /// already-present <paramref name="word"/>.</summary>
    private static string ReadNew(string word, string prop) =>
        $"public get value(): string {{ return this.{word} + this.{prop}; }}";

    [Fact]
    public void Corpus_GenuineCollections_NeverFlagged_AllChainRules()
    {
        var docsChecked = 0;
        var genuineInFile = 0;         // collection present in the file next to its singular — must pass
        var genuineDeclared = 0;       // collection declared in the same edit — must pass
        var genuineCollectionOnly = 0; // collection-only file (no singular member) — must pass
        var genuineIdentity = 0;       // singular + collection both present, referencing both — must pass
        var absentCaught = 0;          // extension absent from file/declaration — must be wiped
        var reverseCaught = 0;         // singular introduced next to an existing plural — must be wiped
        const string relPath = "src/app/doc.model.ts";

        for (var docIdx = 0; docIdx < DocCount; docIdx++)
        {
            var rng = FuzzHarness.SeededRng(Seed, docIdx, Prime);
            var singular = Stems[rng.Next(Stems.Length)] + CamelTails[rng.Next(CamelTails.Length)];

            foreach (var sf in ChainSuffixes)
            {
                var collection = singular + sf;

                // (1) Genuine: the collection ALREADY exists in the file next to its singular —
                //     the exact case the chain exists to tempt — referencing it must pass.
                var result = Guard(ReadOld(singular), ReadNew(singular, collection),
                    TsDoc(singular, collection), relPath);
                Assert.True(result is null,
                    $"collection '{collection}' present in the file must pass (doc #{docIdx}, rule '{sf}') — got: {result}");
                genuineInFile++;

                // (2) Genuine: the collection is explicitly DECLARED in the same edit (the
                //     guard's documented escape hatch) — must pass.
                result = Guard(
                    "constructor() { this.ready = false; }",
                    $"constructor() {{ this.ready = false; this.{collection} = []; }}",
                    TsDoc(singular), relPath);
                Assert.True(result is null,
                    $"collection '{collection}' declared in the same edit must pass (doc #{docIdx}, rule '{sf}') — got: {result}");
                genuineDeclared++;

                // (3) Genuine: the file has ONLY the collection (no singular member) — a
                //     legitimate collection-only model — referencing it must pass.
                result = Guard(ReadOld("ready"), $"public get value(): string {{ return this.ready + this.{collection}; }}",
                    TsDoc(collection), relPath);
                Assert.True(result is null,
                    $"collection-only file referencing '{collection}' must pass (doc #{docIdx}, rule '{sf}') — got: {result}");
                genuineCollectionOnly++;

                // (4) Recall: the SAME extension introduced WITHOUT presence or declaration IS
                //     the pluralization hallucination the chain targets — must be wiped, naming
                //     the singular. (The extension is longer than its singular, so only the
                //     chain — never the typo rule — can fire here.)
                var rejection = Guard(ReadOld(singular), ReadNew(singular, collection),
                    TsDoc(singular), relPath);
                Assert.NotNull(rejection);
                Assert.Contains(collection, rejection);
                Assert.Contains($"did you mean '{singular}'", rejection);
                absentCaught++;

                // (5) Recall, reverse direction: introducing the SINGULAR next to an existing
                //     plural is the same hallucination class — must be wiped, naming the
                //     collection.
                rejection = Guard(ReadOld(collection), ReadNew(collection, singular),
                    TsDoc(collection), relPath);
                Assert.NotNull(rejection);
                Assert.Contains(singular, rejection);
                Assert.Contains($"did you mean '{collection}'", rejection);
                reverseCaught++;
            }

            // (6) Genuine identity: with BOTH the singular and one collection present in the
            //     file, a genuine edit referencing both existing members must pass.
            var sf2 = ChainSuffixes[rng.Next(ChainSuffixes.Length)];
            var col2 = singular + sf2;
            var both = $"public get value(): string {{ return this.{singular} + this.{col2}; }}";
            var result2 = Guard(ReadOld("ready"), both, TsDoc(singular, col2), relPath);
            Assert.True(result2 is null,
                $"singular '{singular}' and collection '{col2}' both present must pass (doc #{docIdx}) — got: {result2}");
            genuineIdentity++;

            docsChecked++;
        }

        FuzzHarness.AssertAllDocsChecked(docsChecked, DocCount, "plural/Array similarity chain corpus");
        FuzzHarness.AssertExercised(genuineInFile,
            "no doc exercised the present-in-file genuine collection bucket");
        FuzzHarness.AssertExercised(genuineDeclared,
            "no doc exercised the same-edit declaration bucket");
        FuzzHarness.AssertExercised(genuineCollectionOnly,
            "no doc exercised the collection-only-file bucket");
        FuzzHarness.AssertExercised(genuineIdentity,
            "no doc exercised the singular-identity bucket");
        FuzzHarness.AssertExercised(absentCaught,
            "no doc exercised the absent-collection recall bucket");
        FuzzHarness.AssertExercised(reverseCaught,
            "no doc exercised the singular-of-plural recall bucket");
    }

    /// <summary>
    /// The HTML surface of the chain, driven exactly as the apply pipeline calls it: the sibling
    /// component `.ts` is resolved and merged into the scan context. A collection declared in the
    /// sibling .ts must pass when referenced from the template (its exemption can only come from
    /// the merged cross-file word set — neither name ever appears in the template), and a plural
    /// extension of a .ts-only singular introduced in the template must be wiped.
    /// </summary>
    [Fact]
    public void Corpus_GenuineCollections_NeverFlagged_HtmlSurfaceResolvesSiblingTs()
    {
        var docsChecked = 0;
        var siblingPasses = 0;
        var siblingCaught = 0;
        const string relPath = "src/app/comp.component.html";
        const string htmlFile = "<div>{{ vm.ready }}</div>";
        const string oldHtml = "<div>{{ vm.ready }}</div>";

        for (var docIdx = 0; docIdx < DocCount; docIdx++)
        {
            var rng = FuzzHarness.SeededRng(Seed + 5000, docIdx, Prime + 1);
            var singular = Stems[rng.Next(Stems.Length)] + CamelTails[rng.Next(CamelTails.Length)];
            var sf = ChainSuffixes[rng.Next(ChainSuffixes.Length)];
            var collection = singular + sf;

            // Genuine: both the singular and the collection are declared in the sibling .ts —
            // the template referencing the collection must pass.
            var tsBoth = $"export class Comp {{\n    public {singular}: string = \"\";\n    public {collection}: string[] = [];\n    public ready = false;\n}}";
            var result = Guard(oldHtml, $"<div>{{{{ vm.ready }}}} {{{{ vm.{collection} }}}}</div>", htmlFile, relPath, tsBoth);
            Assert.True(result is null,
                $"collection '{collection}' declared in the sibling .ts must pass when referenced in the template (doc #{docIdx}) — got: {result}");
            siblingPasses++;

            // Recall on the HTML surface: only the singular is in the .ts — introducing its
            // plural extension in the template is wiped via the cross-file word set.
            var tsSingular = $"export class Comp {{\n    public {singular}: string = \"\";\n    public ready = false;\n}}";
            var rejection = Guard(oldHtml, $"<div>{{{{ vm.ready }}}} {{{{ vm.{collection} }}}}</div>", htmlFile, relPath, tsSingular);
            Assert.NotNull(rejection);
            Assert.Contains(collection, rejection);
            Assert.Contains($"did you mean '{singular}'", rejection);
            siblingCaught++;

            docsChecked++;
        }

        FuzzHarness.AssertAllDocsChecked(docsChecked, DocCount, "HTML plural-chain corpus");
        FuzzHarness.AssertExercised(siblingPasses,
            "no doc exercised the sibling-.ts genuine collection pass");
        FuzzHarness.AssertExercised(siblingCaught,
            "no doc exercised the HTML-surface absent-collection recall");
    }
}
