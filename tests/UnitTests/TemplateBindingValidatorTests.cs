using System.IO;
using Weaver.Services;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Tests for the deterministic post-execution template-binding validator (Check A: bindings
/// must reference symbols the sibling component exposes; Check B: component logic wired
/// under a UI task whose template was in scope but never edited). Regex-based, no LLM —
/// the exact failure mode from the 'group benchmarks in the panel' runs, where the agent
/// added a getter to the component (or a binding referencing one) and the verifier missed
/// that the template never rendered the new logic.
/// </summary>
public class TemplateBindingValidatorTests
{
    private const string Component = """
        @Component({ selector: 'app-weaver' })
        export class WeaverComponent {
          benchmarks: BenchmarkEntry[] = [];
          title = 'x';
          isActive = false;
          trackById(index: number, item: any) { return item.id; }
          get groupedBenchmarks() { return {}; }
          constructor(private svc: SomeService) {}
          @Input() userId?: number;
          load() {}
        }
        """;

    // ─── Check A: template bindings must reference component members ──────────

    [Fact]
    public void ValidBindings_AllSymbolsResolve_NoIssues()
    {
        var html = """
            <div *ngFor="let g of groupedBenchmarks | keyvalue">
              <span>{{ g.key }} — {{ title }}</span>
              <button (click)="load()">Go</button>
            </div>
            """;
        Assert.Empty(TemplateBindingValidator.ValidateTemplateBindings("x.component.html", html, Component));
    }

    [Fact]
    public void MissingSymbol_ReferencedByBinding_IssueReported()
    {
        var html = """<div *ngFor="let g of groupdBenchmarks | keyvalue"></div>""";
        var issues = TemplateBindingValidator.ValidateTemplateBindings("x.component.html", html, Component);
        Assert.Single(issues);
        Assert.Contains("groupdBenchmarks", issues[0]);
        Assert.Contains("missing", issues[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoopVarAndSubProperties_NotFlagged()
    {
        // 'b' is the loop variable, 'b.benchmark'/'b.id' are data fields — neither is a member.
        var html = """
            <div *ngFor="let b of benchmarks">
              <span>{{ b.benchmark }}</span>
              <span>{{ b.id }}</span>
            </div>
            """;
        Assert.Empty(TemplateBindingValidator.ValidateTemplateBindings("x.component.html", html, Component));
    }

    [Fact]
    public void PipeName_NotFlagged()
    {
        var html = """<div *ngFor="let g of groupedBenchmarks | keyvalue">{{ g | json }}</div>""";
        Assert.Empty(TemplateBindingValidator.ValidateTemplateBindings("x.component.html", html, Component));
    }

    [Fact]
    public void AngularJsVmPrefix_ResolvesToControllerMember()
    {
        var html = """<input ng-model="vm.title" />""";
        Assert.Empty(TemplateBindingValidator.ValidateTemplateBindings("x.component.html", html, Component));

        var htmlBad = """<input ng-model="vm.missingThing" />""";
        var issues = TemplateBindingValidator.ValidateTemplateBindings("x.component.html", htmlBad, Component);
        Assert.Single(issues);
        Assert.Contains("missingThing", issues[0]);
    }

    [Fact]
    public void ObjectLiteralKeys_NotFlagged_ValuesAre()
    {
        var html = """<div ng-class="{active: vm.isActive, hidden: true}">{{ vm.title }}</div>""";
        Assert.Empty(TemplateBindingValidator.ValidateTemplateBindings("x.component.html", html, Component));

        var htmlBad = """<div ng-class="{active: vm.noSuchProp}"></div>""";
        var issues = TemplateBindingValidator.ValidateTemplateBindings("x.component.html", htmlBad, Component);
        Assert.Single(issues);
        Assert.Contains("noSuchProp", issues[0]);
        Assert.DoesNotContain(issues, i => i.Contains("'active'"));
    }

    [Fact]
    public void DollarLocalsAndStringLiterals_NotFlagged()
    {
        var html = """
            <div *ngFor="let c of benchmarks; let i = $index">
              <span>{{ $event }}</span>
              <span>{{ 'active' }}</span>
              <span>{{ c.id }}</span>
            </div>
            """;
        Assert.Empty(TemplateBindingValidator.ValidateTemplateBindings("x.component.html", html, Component));
    }

    [Fact]
    public void TrackByExpression_CheckedAsSymbol()
    {
        var html = """<div *ngFor="let c of benchmarks; trackBy: trackById"></div>""";
        Assert.Empty(TemplateBindingValidator.ValidateTemplateBindings("x.component.html", html, Component));

        var htmlBad = """<div *ngFor="let c of benchmarks; trackBy: noSuchTracker"></div>""";
        var issues = TemplateBindingValidator.ValidateTemplateBindings("x.component.html", htmlBad, Component);
        Assert.Contains(issues, i => i.Contains("noSuchTracker"));
    }

    [Fact]
    public void Angular17ControlFlow_Parsed()
    {
        var html = """
            @for (let b of benchmarks; track b.id) {
              <span>{{ b.benchmark }}</span>
            } @if (isActive) { <b>{{ title }}</b> }
            """;
        Assert.Empty(TemplateBindingValidator.ValidateTemplateBindings("x.component.html", html, Component));

        var htmlBad = """
            @for (let b of noSuchList; track b.id) {
              <span>{{ b.benchmark }}</span>
            }
            """;
        var issues = TemplateBindingValidator.ValidateTemplateBindings("x.component.html", htmlBad, Component);
        Assert.Contains(issues, i => i.Contains("noSuchList"));
    }

    [Fact]
    public void GetterAndDecoratedInput_CollectedAsMembers()
    {
        var html = """
            <span>{{ groupedBenchmarks }}</span>
            <app-child [userId]="userId"></app-child>
            """;
        Assert.Empty(TemplateBindingValidator.ValidateTemplateBindings("x.component.html", html, Component));
    }

    [Fact]
    public void NonComponentTs_SkipsCheck()
    {
        // No @Component decorator (plain module / static site entry) — never flag.
        var ts = "export function helper() { return 1; }\nexport const x = 5;";
        var html = """<div>{{ helper }}</div><div *ngFor="let a of anything"></div>""";
        Assert.Empty(TemplateBindingValidator.ValidateTemplateBindings("index.html", html, ts));
    }

    [Fact]
    public void ComponentMemberExtractor_SkipsMethodBodyLocals()
    {
        var members = TemplateBindingValidator.ExtractComponentMembers(Component);
        Assert.Contains("benchmarks", members);
        Assert.Contains("groupedBenchmarks", members);
        Assert.Contains("trackById", members);
        Assert.Contains("userId", members);
        Assert.Contains("load", members);
        Assert.DoesNotContain("constructor", members);
        // Method-body locals / statements never leak in.
        Assert.DoesNotContain("index", members);
        Assert.DoesNotContain("item", members);
        Assert.DoesNotContain("svc", members);
    }

    // ─── Check A: hardened extraction (patterns the old regexes missed) ──────
    // Members declared with definite-assignment assertions (keywordsInput!:), inline or
    // multi-line @ViewChild decorators, and members declared AFTER methods containing braces
    // inside string/template literals — a "}" in a backtick string used to knock depth
    // tracking negative and silently drop every subsequent member.

    private const string HardenedComponent = """
        import { Component, ViewChild, ElementRef } from '@angular/core';

        @Component({ selector: 'app-crawler' })
        export class CrawlerComponent {
          load() {
            const label = "weird ( brace }";
            const css = `padding: ${this.indexCount}px }`;
            return 1;
          }
          parentRef!: ElementRef<HTMLDivElement> | null;
          onMobile = window.innerWidth < 768;
          noFavourites = false;
          @ViewChild('keywordsInput') keywordsInput!: ElementRef<HTMLInputElement>;
          searchResults: any[] = [];
          indexCount = 0;
        }
        """;

    [Fact]
    public void ComponentMemberExtractor_BracesInStringsDoNotBreakDepthTracking()
    {
        // The regression: a "}" inside a template literal (and "(" / "}" inside a quoted
        // string) inside a method used to knock class-body depth negative, exiting the class
        // early and dropping EVERY member declared after it — the all-five-bindings-missing
        // crawler false positive.
        var members = TemplateBindingValidator.ExtractComponentMembers(HardenedComponent);
        Assert.Contains("parentRef", members);      // !: definite-assignment assertion
        Assert.Contains("onMobile", members);       // plain assignment AFTER the string-brace method
        Assert.Contains("noFavourites", members);
        Assert.Contains("keywordsInput", members);  // @ViewChild(...) + !:
        Assert.Contains("searchResults", members);
        Assert.Contains("indexCount", members);
        Assert.Contains("load", members);
    }

    [Fact]
    public void ComponentMemberExtractor_MultiLineDecoratorThenMember_Collected()
    {
        var ts = """
            @Component({ selector: 'app-x' })
            export class XComponent {
              @ViewChild('input')
              inputRef!: ElementRef<HTMLInputElement>;
              title = 'x';
            }
            """;
        var members = TemplateBindingValidator.ExtractComponentMembers(ts);
        Assert.Contains("inputRef", members);
        Assert.Contains("title", members);
    }

    [Fact]
    public void ComponentMemberExtractor_ComparisonsAndArrowsNotMembers()
    {
        var ts = """
            @Component({ selector: 'app-x' })
            export class XComponent {
              run() {
                if (a != b) { return; }
                const fn = (x: number) => x * 2;
                return a === b;
              }
            }
            """;
        var members = TemplateBindingValidator.ExtractComponentMembers(ts);
        Assert.Contains("run", members);
        Assert.DoesNotContain("a", members);
        Assert.DoesNotContain("b", members);
        Assert.DoesNotContain("fn", members);
        Assert.DoesNotContain("x", members);
    }

    [Fact]
    public void TemplateSymbols_OptionalChaining_TreatedAsSingleChain()
    {
        // keywordsInput?.nativeElement?.value?.length used to extract nativeElement / value /
        // length as standalone "missing" symbols — exactly how 'length' got flagged.
        var html = """<div *ngIf="keywordsInput?.nativeElement?.value?.length > 0">{{ keywordsInput?.nativeElement?.value }}</div>""";
        var symbols = TemplateBindingValidator.ExtractTemplateSymbols(html);
        Assert.Contains("keywordsInput", symbols);
        Assert.DoesNotContain("nativeElement", symbols);
        Assert.DoesNotContain("value", symbols);
        Assert.DoesNotContain("length", symbols);
    }

    [Fact]
    public void ValidateTemplateBindings_HardenedPatterns_NoFalsePositives()
    {
        // Whole-template fallback (no snapshot): the crawler component's pre-existing bindings
        // — #keywordsInput template ref, parentRef / noFavourites / onMobile members, and the
        // optional-chained keywordsInput?.nativeElement?.value?.length — must ALL resolve.
        var html = """
            <div class="notificationContainer">
              <input #keywordsInput />
              <div *ngIf="!searchResults.length && indexCount" class="nbDiv">Total indexes: {{ indexCount }}</div>
              <div *ngIf="parentRef">{{ parentRef.nativeElement }}</div>
              <div *ngIf="noFavourites">None</div>
              <div *ngIf="onMobile && keywordsInput?.nativeElement?.value?.length">mobile</div>
            </div>
            """;
        Assert.Empty(TemplateBindingValidator.ValidateTemplateBindings("crawler.component.html", html, HardenedComponent));
    }

    // ─── Check A: binding-attribute shapes ([(ngModel)], [class], (event)) ────

    [Fact]
    public void BananaBoxNgModel_DeclaredMember_NoIssue()
    {
        // [(ngModel)]="title" — the two-way binding attribute name ([(...)]) must parse
        // and its value must resolve like any other binding.
        var html = """<input [(ngModel)]="title" />""";
        Assert.Empty(TemplateBindingValidator.ValidateTemplateBindings("x.component.html", html, Component));
    }

    [Fact]
    public void BananaBoxNgModel_UndeclaredMember_IssueReported()
    {
        var html = """<input [(ngModel)]="modelTitle" />""";
        var issues = TemplateBindingValidator.ValidateTemplateBindings("x.component.html", html, Component);
        Assert.Single(issues);
        Assert.Contains("modelTitle", issues[0]);
    }

    [Fact]
    public void ClassBinding_DeclaredMember_NoIssue()
    {
        // [class.active]="isActive" — class-property binding on a declared member.
        var html = """<div [class.active]="isActive"></div>""";
        Assert.Empty(TemplateBindingValidator.ValidateTemplateBindings("x.component.html", html, Component));
    }

    [Fact]
    public void ClassBinding_UndeclaredMember_IssueReported()
    {
        var html = """<div [class.active]="isActve"></div>""";
        var issues = TemplateBindingValidator.ValidateTemplateBindings("x.component.html", html, Component);
        Assert.Single(issues);
        Assert.Contains("isActve", issues[0]);
    }

    [Fact]
    public void StyleAndEventBindings_Resolve_NoIssue()
    {
        // [style.color] on a declared member and (click) handler on a declared method.
        var html = """
            <div [style.color]="isActive ? 'green' : 'red'" (click)="load()">Go</div>
            """;
        Assert.Empty(TemplateBindingValidator.ValidateTemplateBindings("x.component.html", html, Component));
    }

    // ─── Check A: snapshot-based validation (only symbols the edit introduced) ─
    // The crawler-component false-positive regression: whole-template validation flags
    // PRE-EXISTING bindings that the regex extractor can't resolve (template refs like
    // #keywordsInput, array properties like searchResults.length, or members defined in
    // ways the extractor misses) — which drove the repair loop to add garbage steps. With
    // the pre-edit snapshot supplied, only symbols INTRODUCED by the edit are validated.

    private const string CrawlerComponent = """
        @Component({ selector: 'app-crawler' })
        export class CrawlerComponent {
          indexCount = 0;
          isLoading = false;
          constructor(private crawlerService: any) {}
        }
        """;

    private const string CrawlerHtmlBefore = """
        <div class="notificationContainer">
          <input #keywordsInput />
          <div *ngIf="!searchResults.length && indexCount" class="nbDiv">Total indexes: {{ indexCount }}</div>
          <div *ngIf="noFavourites">No favourites yet</div>
          <div *ngIf="onMobile">On mobile</div>
          <div *ngIf="parentRef">Parent</div>
        </div>
        """;

    private static string TempProjectRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "weaver-tbv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void CheckModifiedTemplates_WithPreEditSnapshot_PreexistingBindingsNotFlagged()
    {
        // The edit only adds a pipe to an EXISTING binding — every other binding (some not
        // even resolvable: #keywordsInput template ref, searchResults.length array property,
        // onMobile/noFavourites/parentRef absent from the component) predates the run.
        var dir = TempProjectRoot();
        try
        {
            var newHtml = CrawlerHtmlBefore.Replace("{{ indexCount }}", "{{ indexCount | number:'1.0-0' }}");
            File.WriteAllText(Path.Combine(dir, "crawler.component.html"), newHtml);
            File.WriteAllText(Path.Combine(dir, "crawler.component.ts"), CrawlerComponent);
            var snapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["crawler.component.html"] = CrawlerHtmlBefore
            };
            var issues = TemplateBindingValidator.CheckModifiedTemplates(
                dir, new[] { "crawler.component.html" }, snapshots);
            Assert.Empty(issues);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void CheckModifiedTemplates_WithPreEditSnapshot_NewlyIntroducedMissingSymbolFlagged()
    {
        // Pre-edit template is fully valid; the edit introduces a binding to a symbol the
        // component does not expose — the snapshot path must STILL flag it.
        var dir = TempProjectRoot();
        try
        {
            var before = """<div *ngIf="indexCount">Total: {{ indexCount }}</div>""";
            var after = before + "\n<span>{{ totallyMadeUp }}</span>";
            File.WriteAllText(Path.Combine(dir, "crawler.component.html"), after);
            File.WriteAllText(Path.Combine(dir, "crawler.component.ts"), CrawlerComponent);
            var snapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["crawler.component.html"] = before
            };
            var issues = TemplateBindingValidator.CheckModifiedTemplates(
                dir, new[] { "crawler.component.html" }, snapshots);
            Assert.Contains(issues, i => i.Contains("totallyMadeUp"));
            Assert.DoesNotContain(issues, i => i.Contains("indexCount"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void CheckModifiedTemplates_WithoutSnapshot_FallsBackToWholeTemplateValidation()
    {
        // Documents the pre-fix contract: with no pre-edit snapshot (the interleaved loop
        // before this fix supplied none), every pre-existing unresolved binding is flagged —
        // the false-positive that spawned the garbage repair steps.
        var dir = TempProjectRoot();
        try
        {
            File.WriteAllText(Path.Combine(dir, "crawler.component.html"), CrawlerHtmlBefore);
            File.WriteAllText(Path.Combine(dir, "crawler.component.ts"), CrawlerComponent);
            var issues = TemplateBindingValidator.CheckModifiedTemplates(
                dir, new[] { "crawler.component.html" }, null);
            Assert.Contains(issues, i => i.Contains("onMobile"));
            Assert.Contains(issues, i => i.Contains("noFavourites"));
            Assert.DoesNotContain(issues, i => i.Contains("keywordsInput")); // template ref — never a symbol
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ─── Check B: component wired but never rendered ──────────────────────────

    private static List<object> ReadStep(string path)
        => new() { new Dictionary<string, object?> { ["type"] = "read", ["path"] = path, ["status"] = "done" } };

    private sealed class TempProject : IDisposable
    {
        public string Root;
        public TempProject(params string[] relFiles)
        {
            Root = Path.Combine(Path.GetTempPath(), "weaver-tpl-tests-" + Guid.NewGuid().ToString("N")[..8]);
            foreach (var rel in relFiles)
            {
                var full = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "<div></div>");
            }
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
        }
    }

    [Fact]
    public void ComponentEdited_TemplateInScopeButNotEdited_FailsWhenTaskTargetsUi()
    {
        // The exact 'group benchmarks in the panel' failure: getter added to the .ts, the
        // .html was attached/read this run, but the template loop was never switched to it.
        using var proj = new TempProject("maxhanna.client/src/app/weaver/weaver.component.html");
        var issues = TemplateBindingValidator.CheckUnrenderedComponentLogic(
            "In the benchmarks panel in weaver component, group benchmarks by benchmark name",
            proj.Root,
            new[] { "maxhanna.client/src/app/weaver/weaver.component.ts" },
            ReadStep("maxhanna.client/src/app/weaver/weaver.component.html"));
        Assert.Single(issues);
        Assert.Contains("weaver.component.html", issues[0]);
    }

    [Fact]
    public void ComponentEdited_TemplateAlsoEdited_NoIssue()
    {
        using var proj = new TempProject("maxhanna.client/src/app/weaver/weaver.component.html");
        var issues = TemplateBindingValidator.CheckUnrenderedComponentLogic(
            "In the benchmarks panel, group benchmarks by name",
            proj.Root,
            new[]
            {
                "maxhanna.client/src/app/weaver/weaver.component.ts",
                "maxhanna.client/src/app/weaver/weaver.component.html"
            },
            ReadStep("maxhanna.client/src/app/weaver/weaver.component.html"));
        Assert.Empty(issues);
    }

    [Fact]
    public void PromptWithoutUiTarget_NoIssue()
    {
        using var proj = new TempProject("src/app/profile/profile.component.html");
        var issues = TemplateBindingValidator.CheckUnrenderedComponentLogic(
            "Fix the data loading bug in the component's service call",
            proj.Root,
            new[] { "src/app/profile/profile.component.ts" },
            ReadStep("src/app/profile/profile.component.html"));
        Assert.Empty(issues);
    }

    [Fact]
    public void TemplateNeverInScopeAndNotNamed_NoIssue()
    {
        using var proj = new TempProject("src/app/settings/settings.component.html");
        var issues = TemplateBindingValidator.CheckUnrenderedComponentLogic(
            "In the settings panel, persist the checkbox state",
            proj.Root,
            new[] { "src/app/settings/settings.component.ts" },
            new List<object>());
        Assert.Empty(issues);
    }
}

