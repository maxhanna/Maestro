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

