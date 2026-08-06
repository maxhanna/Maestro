using System.Text;
using Weaver;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Corpus for the CreateFile path — the deterministic chain that fires when a plan
/// step targets a file that does not exist yet. Mirrors the real pipeline exactly:
/// <c>EditClassifier.Classify(fileExists:false)</c> → <c>EditStrategy.CreateFile</c>,
/// then <c>EditStrategyResolver.Decide(fileExists:false)</c> → <c>CreateFile</c>, then
/// <c>AgentController.ApplyFullFile</c>'s new-file byte path via
/// <c>FuzzHarness.ApplyCreateFileMirror</c> (fence strip + CRLF→LF + edge-newline trim
/// + the parse-time no-ops + the CSS-only Clean pass — see the mirror's docs for the
/// deliberately-unmirrored pieces: external formatters and the LLM continuation).
///
/// The claim locked here: for a non-existent file, the classifier and resolver must
/// ALWAYS pick CreateFile, and the written content must equal the pure fullFile bytes
/// after exactly the sanctioned fence/CRLF/edge-newline transforms — no other drift,
/// fence-stripping surprises, or line-ending surprises. Idempotency (re-running the
/// apply) must be byte-identical, so no pass can corrupt on a second visit.
/// </summary>
public class CreateFileCorpusTests
{
    private static readonly string[] Extensions = { ".ts", ".js", ".cs", ".html", ".css" };
    private static readonly string[] FenceTags = { "typescript", "javascript", "csharp", "html", "css" };

    // ── Generators ─────────────────────────────────────────────────────────────
    // Each body embeds its doc index so a cross-doc byte drift is self-diagnosing,
    // and none contain SQL verbatim strings or .py content — the passes that could
    // legitimately transform (CleanVerbatimStringEscapes / AutoFixPythonStatements)
    // must prove themselves no-ops on the corpus.

    private static string GenerateFile(int idx, string ext, Random rng)
    {
        var sb = new StringBuilder();
        var n = rng.Next(2, 4);
        switch (ext)
        {
            case ".ts":
                sb.AppendLine($"export class GenWidget{idx} {{");
                for (var m = 0; m < n; m++)
                {
                    sb.AppendLine($"  async handle{m}{idx}(payload: string): Promise<string> {{");
                    sb.AppendLine($"    const result = payload.trim() + \":gen{idx}\";");
                    sb.AppendLine("    return result;");
                    sb.AppendLine("  }");
                }
                sb.Append('}');
                break;
            case ".js":
                sb.AppendLine($"export function buildGen{idx}() {{");
                sb.AppendLine("  const items = [1, 2, 3];");
                sb.AppendLine($"  return items.map(x => x * {rng.Next(2, 9)});");
                sb.Append('}');
                break;
            case ".cs":
                sb.AppendLine($"public class GenHandler{idx} {{");
                sb.AppendLine("    public int Compute(int value)");
                sb.AppendLine("    {");
                sb.AppendLine($"        var scale = {rng.Next(2, 9)};");
                sb.AppendLine($"        return value * scale + {idx};");
                sb.AppendLine("    }");
                sb.Append('}');
                break;
            case ".html":
                sb.AppendLine("<!DOCTYPE html>");
                sb.AppendLine("<html>");
                sb.AppendLine("<head><title>Gen Page</title></head>");
                sb.AppendLine("<body>");
                sb.AppendLine($"  <div id=\"card-{idx}\" class=\"card\">");
                sb.AppendLine($"    <button onclick=\"runGen{idx}()\">Run</button>");
                sb.AppendLine("  </div>");
                sb.AppendLine("  <script>");
                sb.AppendLine($"    function runGen{idx}() {{ return {idx}; }}");
                sb.AppendLine("  </script>");
                sb.AppendLine("</body>");
                sb.Append("</html>");
                break;
            case ".css":
                sb.AppendLine($"/* gen{idx} */");
                sb.AppendLine($".card-{idx} {{");
                sb.AppendLine($"  color: #{rng.Next(0x100000, 0xFFFFFF):x6};");
                sb.AppendLine($"  margin: {rng.Next(1, 5)}px;");
                sb.AppendLine("  padding: 8px 12px;");
                sb.AppendLine("}");
                sb.AppendLine($".card-{idx}:hover {{");
                sb.AppendLine("  background: #fff;");
                sb.AppendLine("}");
                sb.AppendLine($".card-{idx}::before {{");
                sb.AppendLine("  content: \"\\2014\";");
                sb.AppendLine("}");
                sb.AppendLine("@media (max-width: 600px) {");
                sb.AppendLine($"  .card-{idx} {{ width: 100%; }}");
                sb.AppendLine("}");
                sb.AppendLine($"@keyframes fadeGen{idx} {{");
                sb.AppendLine("  from { opacity: 0; }");
                sb.AppendLine("  to { opacity: 1; }");
                sb.Append('}');
                break;
            default:
                sb.Append($"// unhandled extension {ext} for doc {idx}");
                break;
        }
        return sb.ToString();
    }

    // ── Full-chain corpus ──────────────────────────────────────────────────────

    /// <summary>
    /// 30 seeded docs across .ts/.js/.cs/.html/.css: assert Classify and Decide both
    /// yield CreateFile on a non-existent file, the mirror output equals the pure
    /// fullFile bytes (after only the sanctioned fence/CRLF/edge-newline transforms),
    /// re-application is a byte-identical no-op, and the CSS Clean pass never corrupts.
    /// Fence wrapping fires every 3rd doc and CRLF line endings every 4th doc so both
    /// normalization branches are provably exercised (guarded by AssertExercised).
    /// </summary>
    [Fact]
    public void Fuzz_CreateFile_FullChain_WritesPureBytes()
    {
        const int docCount = 30;
        const int seed = 90210;
        const int prime = 100003;
        var checkedCount = 0;
        var fenceDocs = 0;
        var crlfDocs = 0;
        var cssDocs = 0;

        for (var i = 0; i < docCount; i++)
        {
            var rng = FuzzHarness.SeededRng(seed, i, prime);
            var ext = Extensions[i % Extensions.Length];
            var relPath = $"gen/CreateFileCorpus/gen_doc_{i:D2}{ext}";
            var body = GenerateFile(i, ext, rng); // no trailing newline — pure bytes
            // StringBuilder.AppendLine emits CRLF on Windows; the corpus's canonical
            // form is LF, and the CRLF branch below deliberately re-introduces \r\n
            // so the normalization path is exercised, not masked by the platform.
            body = body.Replace("\r\n", "\n");
            var fullFile = body;

            // Fence-wrapped every 3rd doc → StripFullFileFence must strip it back to body.
            if (i % 3 == 0)
            {
                var tag = FenceTags[i % FenceTags.Length];
                fullFile = "```" + tag + "\n" + body + "\n```";
                fenceDocs++;
            }
            // CRLF line endings every 4th doc → CRLF→LF normalization must land on body.
            if (i % 4 == 1)
            {
                fullFile = fullFile.Replace("\n", "\r\n");
                crlfDocs++;
            }

            // 1. Classifier — a non-existent file is ALWAYS CreateFile, regardless of ext.
            var step = new PlanStep
            {
                File = relPath,
                Change = $"Create a new {ext.TrimStart('.')} file for generated corpus doc {i}",
                OldString = "",
                NewString = fullFile,
            };
            var classified = EditClassifier.Classify(step, fileExists: false, ext);
            Assert.Equal(EditStrategy.CreateFile, classified);

            // 2. Resolver — same gate at the Decide stage, before any AST work.
            var intent = new EditIntent(EditIntentKind.TargetedEdit, null, null);
            var decision = EditStrategyResolver.Decide(relPath, fileExists: false, "", step.Change ?? "", intent);
            Assert.Equal(EditStrategy.CreateFile, decision.Strategy);

            // 3. Apply — written content must equal the pure body bytes (sanctioned
            //    fence/CRLF/edge-newline transforms only; everything else byte-identical).
            var written = FuzzHarness.ApplyCreateFileMirror(fullFile, relPath);
            FuzzHarness.AssertByteIdenticalNoOp(body, written, "CreateFile apply", i, "written");

            // 4. Idempotency — re-applying the written bytes changes nothing (this also
            //    re-runs the CSS Clean pass on .css docs, so a second-visit corruption
            //    would fail here).
            FuzzHarness.AssertByteIdenticalNoOp(written, FuzzHarness.ApplyCreateFileMirror(written, relPath),
                "CreateFile apply (idempotent)", i, "re-apply");

            // 5. CSS only — name the Clean no-op explicitly so a corruption is diagnosed
            //    as the cleaner, not as the generic apply.
            if (ext == ".css")
            {
                cssDocs++;
                FuzzHarness.AssertByteIdenticalNoOp(written, LlmCssCleaner.Clean(written),
                    "LlmCssCleaner.Clean", i, "cleaned");
            }

            checkedCount++;
        }

        FuzzHarness.AssertAllDocsChecked(checkedCount, docCount, "CreateFile full-chain corpus");
        FuzzHarness.AssertExercised(fenceDocs, "no fuzz doc exercised the markdown-fence strip path");
        FuzzHarness.AssertExercised(crlfDocs, "no fuzz doc exercised the CRLF→LF normalization path");
        FuzzHarness.AssertExercised(cssDocs, "no fuzz doc exercised the CSS Clean no-op path");
    }

    // ── Deterministic transform semantics ──────────────────────────────────────

    /// <summary>
    /// Locks the exact byte transforms of <c>ApplyCreateFileMirror</c> (mirroring
    /// <c>StripFullFileFence</c> + the no-op passes): fence strip (with/without language
    /// tag, with/without surrounding blank lines), CRLF→LF normalization, the fence+CRLF
    /// combination, and the empty/whitespace fallback.
    /// </summary>
    [Fact]
    public void ApplyCreateFileMirror_FenceCrlfAndEdgeNewlineSemantics()
    {
        const string body = "export class GenWidget {\n  id = 1;\n}";

        // Unfenced, LF — pure bytes preserved.
        Assert.Equal(body, FuzzHarness.ApplyCreateFileMirror(body, "gen/a.ts"));

        // Fence wrapped — opening/closing fence stripped, body bytes preserved.
        Assert.Equal(body, FuzzHarness.ApplyCreateFileMirror("```ts\n" + body + "```", "gen/a.ts"));

        // Fence with a language tag + blank lines around — edge newlines trimmed.
        Assert.Equal(body, FuzzHarness.ApplyCreateFileMirror("```typescript\n\n\n" + body + "\n\n\n```", "gen/a.ts"));

        // CRLF line endings normalized to LF.
        Assert.Equal(body, FuzzHarness.ApplyCreateFileMirror(body.Replace("\n", "\r\n"), "gen/a.ts"));

        // Fence + CRLF together — both transforms compose to the same pure bytes.
        Assert.Equal(body, FuzzHarness.ApplyCreateFileMirror(("```ts\n" + body + "```").Replace("\n", "\r\n"), "gen/a.ts"));

        // Empty and whitespace-only input → empty output (never a crash, never a fence artifact).
        Assert.Equal("", FuzzHarness.ApplyCreateFileMirror("", "gen/a.ts"));
        Assert.Equal("", FuzzHarness.ApplyCreateFileMirror("   \n \n  ", "gen/a.ts"));

        // The no-op passes never fire on this content: no SQL verbatim strings (escape
        // pass early-returns) and no .py content (the Python pass may run for .py but is
        // a no-op on this TypeScript body — no strings with trailing spaces, no
        // keyword-after-`)`/`;`/`]`/`}` constructs).
        Assert.Equal(body, FuzzHarness.ApplyCreateFileMirror(body, "gen/a.py"));
        Assert.Equal(body, FuzzHarness.ApplyCreateFileMirror(body, "gen/a.txt"));
    }
}
