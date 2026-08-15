using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Tests for <c>AgentTextUtilities.BuildVerifierFileView</c> — the windowed view of large
/// files fed to the post-execution verifier. Regression: a 36k-char stylesheet whose edit
/// lands at char ~29k was head-truncated to the first 12k chars, so the verifier honestly
/// reported the freshly added rule as "not found" and spawned a redundant repair step. The
/// windowed view guarantees the edited region (anchored by each applied edit's newString)
/// is always visible, alongside a bounded head and tail.
///
/// Also covers <c>AgentTextUtilities.CheckAppliedEditsPresent</c> — the deterministic
/// ground-truth check that every applied edit's newString is actually present on disk,
/// so the LLM verifier can never claim "the change was not made" for a change that
/// provably landed (the popupUserTagUser?.username title-line case) without a deterministic
/// counter-fact, and so a silently-dropped edit fails verification on its own.
/// </summary>
public class AgentTextUtilitiesTests
{
    private const string Line = "// filler line filler line filler line\n"; // 42 chars

    private static string Fill(int lines) => string.Concat(Enumerable.Repeat(Line, lines));

    [Fact]
    public void BuildVerifierFileView_SmallFile_ReturnedWhole()
    {
        const string content = "a\nb\nc";
        Assert.Equal(content, AgentTextUtilities.BuildVerifierFileView(content, null, 12000));
    }

    [Fact]
    public void BuildVerifierFileView_LargeFileWithAnchor_ShowsEditedRegionAndHeadAndTail()
    {
        // head: 150 lines (6300 chars — beyond the 3000-char head budget, so the head
        // truncation marker appears); mid1: filler before the edit; anchor: the new CSS
        // rule (~char 9k of a ~16k file); mid2: filler after the edit, far from both the
        // anchor window and the tail; tail: last ~3.6k chars (beyond the 2000-char tail
        // budget, so the tail marker appears).
        const string anchor = ".kanban-card .attachments .attachment-item {\n white-space: nowrap !important;\n overflow: hidden !important;\n text-overflow: ellipsis !important;\n}";
        var content = Fill(150) + Fill(300) + anchor + "\n" +
            string.Concat(Enumerable.Repeat("/* gap2 filler */\n", 100)) +
            "/* GAP2_SENTINEL_OMITTED */\n" +
            string.Concat(Enumerable.Repeat("/* gap2 filler */\n", 200)) +
            string.Concat(Enumerable.Repeat("/* tail filler line */\n", 200));
        Assert.True(content.Length > 12000);

        var view = AgentTextUtilities.BuildVerifierFileView(content, new[] { anchor }, 12000);

        // The edited region MUST be visible — this is the regression this helper fixes.
        Assert.Contains(".kanban-card .attachments .attachment-item", view);
        Assert.Contains("text-overflow: ellipsis !important", view);
        Assert.Contains("EDITED REGION", view);
        // Head and tail markers present; the view is bounded.
        Assert.Contains("head of file shown", view);
        Assert.Contains("TAIL", view);
        Assert.Contains("TRUNCATED", view);
        Assert.True(view.Length < content.Length);
        // The middle beyond the anchor's ±400 window (and before the tail) is omitted.
        Assert.DoesNotContain("GAP2_SENTINEL_OMITTED", view);
    }

    [Fact]
    public void BuildVerifierFileView_VerbatimMiss_FallsBackToLongestLine()
    {
        // The edit was reformatted after apply (e.g. a CSS dedupe re-serialized the body),
        // so the verbatim anchor no longer matches — but its selector line still does.
        var content = Fill(700) +
            ".kanban-card .attachments .attachment-item {\n  white-space: nowrap !important;\n}\n" +
            Fill(700);
        var verbatimAnchor = ".kanban-card .attachments .attachment-item {\n white-space: nowrap !important;\n overflow: hidden !important;\n}";
        Assert.True(content.Length > 12000);

        var view = AgentTextUtilities.BuildVerifierFileView(content, new[] { verbatimAnchor }, 12000);

        Assert.Contains(".kanban-card .attachments .attachment-item", view);
        Assert.Contains("EDITED REGION", view);
    }

    [Fact]
    public void BuildVerifierFileView_MultipleAnchors_AllRegionsVisible()
    {
        const string anchorA = ".settings-edit-field {\n display: flex;\n}";
        const string anchorB = ".command-item {\n cursor: pointer;\n}";
        var content = Fill(400) + anchorA + "\n" + Fill(500) + anchorB + "\n" + Fill(400);
        Assert.True(content.Length > 12000);

        var view = AgentTextUtilities.BuildVerifierFileView(content, new[] { anchorA, anchorB }, 12000);

        Assert.Contains(".settings-edit-field", view);
        Assert.Contains(".command-item", view);
        Assert.Equal(2, CountOccurrences(view, "EDITED REGION"));
    }

    [Fact]
    public void BuildVerifierFileView_NoAnchors_HeadAndTailOnlyBounded()
    {
        var content = Fill(400) +
            string.Concat(Enumerable.Repeat("/* MIDDLE_SENTINEL line */\n", 800)) +
            Fill(400);
        Assert.True(content.Length > 12000);

        var view = AgentTextUtilities.BuildVerifierFileView(content, null, 12000);

        Assert.Contains("head of file shown", view);
        Assert.Contains("TAIL", view);
        Assert.DoesNotContain("EDITED REGION", view);
        Assert.DoesNotContain("MIDDLE_SENTINEL", view);
        Assert.True(view.Length < content.Length);
        Assert.True(view.Length <= 12000 + 500); // bounded (markers may push slightly past)
    }

    [Fact]
    public void BuildVerifierFileView_AnchorAtVeryStart_StillBounded()
    {
        const string anchor = ".top-rule {\n color: blue;\n}";
        var content = anchor + "\n" + Fill(1500);
        Assert.True(content.Length > 12000);

        var view = AgentTextUtilities.BuildVerifierFileView(content, new[] { anchor }, 12000);

        Assert.Contains(".top-rule", view);
        Assert.True(view.Length < content.Length);
    }

    // ── CheckAppliedEditsPresent: deterministic applied-edit ground truth ──────────────

    private const string HtmlRel = "app.component.html";

    private static Dictionary<string, object?> DoneEdit(string newString)
        => new() { ["type"] = "edit", ["status"] = "done", ["path"] = HtmlRel, ["newStringPreview"] = newString };

    [Fact]
    public void CheckAppliedEditsPresent_LandedEdit_IsConfirmedAndNotMissing()
    {
        // The exact regression from the popupUserTagUser?.username log: the edit applied and
        // its new text IS on disk, but the LLM verifier claimed "the change was not made".
        // The deterministic check must produce the counter-fact so the verifier prompt can
        // carry it and the card can show it as ground truth.
        var dir = TempDir();
        try
        {
            var newStr = "<span class=\"cursorPointer\" (click)=\"open($event)\"\n [title]=\"'Open profile of ' + popupUserTagUser.username + ' in a new tab'\">";
            File.WriteAllText(Path.Combine(dir, HtmlRel),
                "<div>\n" + newStr + "\n</div>\n" + new string('x', 20000));

            var (confirmed, missing) = AgentTextUtilities.CheckAppliedEditsPresent(dir, new object[] { DoneEdit(newStr) });

            Assert.Single(confirmed);
            Assert.Contains("popupUserTagUser.username", confirmed[0]);
            Assert.Empty(missing);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CheckAppliedEditsPresent_DomNormalizedParenSpacing_StillConfirmed()
    {
        // The HtmlDomEditor FORMAT D path (and the apply pipeline's HTML style self-heal)
        // serialize the changed line with `button(` — no space before the attribute paren.
        // The edit's newStringPreview carries the spaced form, so a verbatim match would
        // false-negative a LANDED edit and fail verification with a phantom "NOT present"
        // issue (the churn that sent the run into the repair circuit breaker with the
        // verifier's reason under a green "Verified complete" card). Both sides must be
        // paren-spacing-normalized before matching — the same normalization the per-step
        // ground-truth check applies.
        var dir = TempDir();
        try
        {
            var newStr = "<button (click)=\"vm.openCard(card.id)\">Details</button> " +
                         "<button (click)=\"vm.openCard(card.id)\">Open</button>";
            // What the DOM serializer actually wrote: `<button(click)=…` (space dropped).
            File.WriteAllText(Path.Combine(dir, HtmlRel),
                "<div class=\"card-list\">\n" +
                "  <div *ngFor=\"let card of vm.items\" class=\"card-item\">\n" +
                "    <button(click)=\"vm.openCard(card.id)\">Details</button> " +
                "<button(click)=\"vm.openCard(card.id)\">Open</button>\n" +
                "  </div>\n</div>\n" + new string('q', 12000));

            var (confirmed, missing) = AgentTextUtilities.CheckAppliedEditsPresent(dir, new object[] { DoneEdit(newStr) });

            Assert.Single(confirmed);
            Assert.Contains("vm.openCard(card.id)", confirmed[0]);
            Assert.Empty(missing);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CheckAppliedEditsPresent_EditNotOnDisk_IsMissingIssue()
    {
        // A step that reports status=done but whose newString is NOT in the current file
        // must fail verification deterministically — the LLM verifier cannot be trusted to
        // notice a silently-dropped edit on its own.
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, HtmlRel), "<div>unchanged content</div>");

            var (confirmed, missing) = AgentTextUtilities.CheckAppliedEditsPresent(
                dir, new object[] { DoneEdit("this edit never landed") });

            Assert.Empty(confirmed);
            Assert.Single(missing);
            Assert.Contains("NOT present", missing[0]);
            Assert.Contains("this edit never landed", missing[0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CheckAppliedEditsPresent_ReformattedEdit_FallsBackToDistinctiveLine()
    {
        // The newString no longer matches verbatim (reformatted/merged) but its distinctive
        // line survives — must still count as confirmed, mirroring BuildVerifierFileView.
        var dir = TempDir();
        try
        {
            // The applied edit's newString was later reformatted: the full block no longer
            // matches verbatim (an attribute was added), but its distinctive line (the
            // class-bearing div) survives — the same fallback BuildVerifierFileView uses.
            var newStr = "<div class=\"flight-schedule-entry\">\n  <div class=\"flight-detail-body\">\n  </div>\n</div>";
            File.WriteAllText(Path.Combine(dir, HtmlRel),
                "<div>\n<div class=\"flight-schedule-entry extra-class\">\n  <div class=\"flight-detail-body\">\n  </div>\n</div>\n</div>\n" + new string('y', 15000));

            var (confirmed, missing) = AgentTextUtilities.CheckAppliedEditsPresent(dir, new object[] { DoneEdit(newStr) });

            Assert.Single(confirmed);
            Assert.Contains("flight-detail-body", confirmed[0]);
            Assert.Empty(missing);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CheckAppliedEditsPresent_CrlfContent_StillConfirmed()
    {
        var dir = TempDir();
        try
        {
            var newStr = "isLoading = false;";
            File.WriteAllText(Path.Combine(dir, HtmlRel), "class A {\r\n" + newStr + "\r\n}\r\n" + new string('z', 12000));

            var (confirmed, missing) = AgentTextUtilities.CheckAppliedEditsPresent(dir, new object[] { DoneEdit(newStr) });

            Assert.Single(confirmed);
            Assert.Empty(missing);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CheckAppliedEditsPresent_NonDoneStatus_Ignored()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, HtmlRel), "<div>unchanged</div>");
            var result = new Dictionary<string, object?>
            {
                ["type"] = "edit", ["status"] = "skipped", ["path"] = HtmlRel, ["newStringPreview"] = "never applied"
            };

            var (confirmed, missing) = AgentTextUtilities.CheckAppliedEditsPresent(dir, new object[] { result });

            Assert.Empty(confirmed);
            Assert.Empty(missing);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CheckAppliedEditsPresent_MultipleEdits_AllConfirmedWhenPresent()
    {
        var dir = TempDir();
        try
        {
            var first = "getItems() { return this.items.slice(); }";
            var second = "getItemsCount() { return this.items.length; }";
            File.WriteAllText(Path.Combine(dir, HtmlRel), first + "\n" + second);

            var (confirmed, missing) = AgentTextUtilities.CheckAppliedEditsPresent(
                dir, new object[] { DoneEdit(first), DoneEdit(second) });

            Assert.Equal(2, confirmed.Count);
            Assert.Empty(missing);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CheckAppliedEditsPresent_LastEditSupersedesEarlier_MissingOnlyFiresOnLastPerPath()
    {
        // A repair pass legitimately rewrites a region an earlier edit touched. The earlier
        // edit's newString is gone from the file, but the MISSING side must only check the
        // LAST applied edit per path — otherwise the check would churn on every repair pass.
        // The confirmed side still lists what's actually present.
        var dir = TempDir();
        try
        {
            var superseded = "oldVersionMarker";
            var final = "finalVersionMarker";
            File.WriteAllText(Path.Combine(dir, HtmlRel), final + "\n" + new string('q', 12000));

            var (confirmed, missing) = AgentTextUtilities.CheckAppliedEditsPresent(
                dir, new object[] { DoneEdit(superseded), DoneEdit(final) });

            Assert.Single(confirmed);
            Assert.Contains("finalVersionMarker", confirmed[0]);
            Assert.Empty(missing);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CheckAppliedEditsPresent_MissingTargetFile_IsMissingIssue()
    {
        var dir = TempDir();
        try
        {
            var (confirmed, missing) = AgentTextUtilities.CheckAppliedEditsPresent(
                dir, new object[] { DoneEdit("anything") });
            Assert.Empty(confirmed);
            Assert.Single(missing);
            Assert.Contains("no longer exists", missing[0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver_applied_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    // ── Per-step computed ground truth (ComputeStepGroundTruth) ───────────────────────────

    [Fact]
    public void ComputeStepGroundTruth_LiteralSwap_YieldsNewContentExpectation()
    {
        // A deterministic literal swap: the step's new content must be present in the file.
        var items = AgentTextUtilities.ComputeStepGroundTruth(
            "card.component.html", "<span>Details</span>", "<span>Open</span>");

        Assert.Single(items);
        Assert.Contains("Expected: \"", items[0].Text);
        Assert.Contains("present in card.component.html", items[0].Text);
        Assert.Equal("card.component.html", items[0].File);
        // The anchor is the last substantial line of the new content — searchable on disk.
        Assert.Equal("<span>Open</span>", items[0].Anchor);
    }

    [Fact]
    public void ComputeStepGroundTruth_TypoFix_YieldsDidYouMeanExpectation()
    {
        // The 'did you mean' shape: the edit replaces a plausible typo with the corrected
        // token — the expected outcome is the corrected token on disk.
        var items = AgentTextUtilities.ComputeStepGroundTruth(
            "card.component.html",
            "<button (click)=\"vm.opnCard(card.id)\">Open</button>",
            "<button (click)=\"vm.openCard(card.id)\">Open</button>");

        var fix = items.FirstOrDefault(i => i.Text.Contains("replaces", StringComparison.Ordinal));
        Assert.NotNull(fix);
        Assert.Contains("openCard", fix!.Text);
        Assert.Contains("opnCard", fix.Text);
        Assert.Equal("openCard", fix.Anchor);
    }

    [Fact]
    public void ComputeStepGroundTruth_PluralVariantSwap_YieldsDidYouMeanExpectation()
    {
        // The plural family: `item` → `items` is the guard's pluralization relation, so a
        // step that fixes the cardinality names the expected token.
        var items = AgentTextUtilities.ComputeStepGroundTruth(
            "config.ts", "const x = this.item;", "const x = this.items;");
        Assert.Contains(items, i => i.Text.Contains("items", StringComparison.Ordinal) &&
                                    i.Text.Contains("item", StringComparison.Ordinal) &&
                                    i.Text.Contains("replaces", StringComparison.Ordinal));
    }

    [Fact]
    public void ComputeStepGroundTruth_MultiLineNewContent_AnchorIsLastSubstantialLine()
    {
        var items = AgentTextUtilities.ComputeStepGroundTruth(
            "metrics.service.ts", "constructor() { }",
            "constructor() {\n    void this.loadWithRetries();\n  }");

        Assert.Single(items);
        Assert.Equal("void this.loadWithRetries();", items[0].Anchor);
    }

    [Fact]
    public void ComputeStepGroundTruth_FullFileForm_YieldsExpectation()
    {
        var items = AgentTextUtilities.ComputeStepGroundTruth(
            "brand-new.ts", null, null, fullFile: "export const A = 1;");
        Assert.Single(items);
        Assert.Contains("present in brand-new.ts", items[0].Text);
        Assert.Equal("export const A = 1;", items[0].Anchor);
    }

    [Fact]
    public void ComputeStepGroundTruth_NoContent_ReturnsEmpty()
    {
        Assert.Empty(AgentTextUtilities.ComputeStepGroundTruth("x.ts", null, null));
        Assert.Empty(AgentTextUtilities.ComputeStepGroundTruth("x.ts", "old", ""));
    }

    [Fact]
    public void NormalizeParenSpacing_MatchesApplyPipelinesSelfHeal()
    {
        // The apply pipeline rewrites the whole changed line (`button (` → `button(`); the
        // verifier normalizes both sides so a landing HTML edit is never false-negative.
        const string raw = "<button (click)=\"vm.openCard(1)\">Go</button>";
        Assert.Equal("<button(click)=\"vm.openCard(1)\">Go</button>", AgentTextUtilities.NormalizeParenSpacing(raw));
        // Idempotent forward: a second pass changes nothing.
        var once = AgentTextUtilities.NormalizeParenSpacing(raw);
        Assert.Equal(once, AgentTextUtilities.NormalizeParenSpacing(once));
    }
}
