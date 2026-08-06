using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Weaver.Services;

using static Weaver.Services.AgentTokenMetrics;
using static Weaver.Services.AgentEditHeuristics;
using static Weaver.Services.AgentPlanParsing;
using static Weaver.Services.AgentMethodInventory;
using static Weaver.Services.AgentProjectUtilities;
using static Weaver.Services.AgentDiscovery;
using static Weaver.Services.AgentTextUtilities;
using static Weaver.Services.AgentCodeFormatting;
using static Weaver.Services.AgentSkeleton;
using static Weaver.Services.AgentDiffUtilities;
using static Weaver.Services.AgentJsonUtilities;

/// <summary>Part of the split of the former AgentUtilities monolith.</summary>
public static class AgentMethodInventory
{
    public static readonly HashSet<string> _builtInTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "string", "int", "long", "double", "float", "decimal", "bool", "char", "byte",
        "short", "uint", "ulong", "ushort", "sbyte", "object", "dynamic", "void",
        "Task", "ValueTask", "Task<T>", "ValueTask<T>", "IActionResult", "ActionResult",
        "OkResult", "OkObjectResult", "BadRequestResult", "BadRequestObjectResult",
        "NotFoundResult", "StatusCodeResult", "ObjectResult", "RedirectResult",
        "FileResult", "ContentResult", "JsonResult", "IEnumerable<T>", "IQueryable<T>",
        "List<T>", "Dictionary<TKey,TValue>", "HashSet<T>", "IList<T>", "ICollection<T>",
        "HttpResponse", "HttpRequest", "CancellationToken", "DateTime", "TimeSpan",
        "Guid", "Uri", "Exception", "InvalidOperationException", "ArgumentNullException",
        "ArgumentException", "NotSupportedException", "MySqlConnection", "MySqlCommand",
        "MySqlDataReader", "MySqlParameter", "MySqlDbType", "DbConnection", "DbCommand",
        "HttpClient", "HttpContent", "HttpMethod", "HttpStatusCode",
        "IHttpResponseBodyFeature", "IHttpRequestLifetimeFeature", "IHttpConnectionFeature",
        "IHttpWebSocketFeature", "Stream", "PipeWriter", "PipeReader",
        "HttpProtocol", "HttpVersion", "HttpContext", "HttpRequest", "HttpResponse",
        "Model", "ViewDataDictionary", "ViewData", "TempData", "ViewBag", "RouteData"
    };

    public static readonly Regex MethodDeclRegex = new(
        @"(?:(?:public|private|protected|internal)\s+)?(?:(?:static|virtual|override|abstract|sealed|new|partial|async|unsafe)\s+)*(?:\w+(?:\[\])?(?:<[^>]*>)?)\s+(\w+)\s*\(([^)]*)\)",
        RegexOptions.Compiled);

    public static readonly HashSet<string> skipTypes = new(StringComparer.Ordinal)
    {
        "string", "int", "bool", "long", "double", "float", "decimal", "char",
        "byte", "short", "uint", "ulong", "ushort", "sbyte", "object", "void",
        "Task", "ValueTask", "IEnumerable", "ICollection", "IList", "List",
        "Dictionary", "HashSet", "Queue", "Stack", "Tuple", "Nullable",
        "StringBuilder", "StringReader", "StringWriter",
        "HttpResponseMessage", "HttpRequestMessage",
        "ActionResult", "IActionResult", "OkResult", "OkObjectResult",
        "BadRequestResult", "NotFoundResult", "StatusCodeResult",
        "JsonResult", "FileResult", "ContentResult", "RedirectResult",
        "ViewResult", "PartialViewResult", "IQueryable",
        "Thread", "TaskCompletionSource", "CancellationToken",
        "HttpClient", "HttpContext", "HttpRequest", "HttpResponse",
        "Stream", "StreamReader", "StreamWriter", "MemoryStream",
        "FileStream", "BinaryReader", "BinaryWriter", "TextReader", "TextWriter",
        "DateTime", "DateTimeOffset", "TimeSpan", "Guid", "Uri", "Version",
        "Regex", "Match", "Group", "Capture", "StringComparison",
        "Encoding", "UTF8", "Unicode", "ASCII", "Declare", "TryParse",
         "Parse", "Convert", "Math", "Random",
        "Exception", "InvalidOperationException", "ArgumentNullException",
        "ArgumentException", "IOException", "FormatException",
        "Response", "Request", "Delegate", "Func", "Action", "Predicate",
        "NameValueCollection", "IOrderedEnumerable",
        "IServiceProvider", "IDisposable", "IAsyncDisposable",
        "Startup", "Program", "MySqlConnection", "MySqlCommand", "MySqlDataReader",
        "MySqlParameter", "MySqlTransaction", "MySqlException",
        "SqlConnection", "SqlCommand", "SqlDataReader",
        "NpgsqlConnection", "NpgsqlCommand", "NpgsqlDataReader", "?",
        "IConfiguration", "Log", "JsonDocument", "JsonNode", "JsonObject",
        "JsonArray", "JsonValue", "JsonSerializer", "JsonSerializerOptions"
    };

    public static readonly string[] serviceSuffixes = {
        "Service", "Controller", "Handler", "Manager",
        "Provider", "Factory", "Repository", "Helper", "Util", "Extension",
        "Middleware", "Filter", "Attribute", "Converter", "Mapper", "Builder",
        "Adapter", "Proxy", "Facade", "Strategy", "Observer", "Configuration",
        "Options", "Settings"
    };

    public static int DetectIndentWidth(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return 4;
        var lines = source.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var hasTabIndent = false;
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            if (line[0] == '\t') { hasTabIndent = true; break; }
        }
        if (hasTabIndent)
        {
            var spaceIndents = new HashSet<int>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length > 0 && line[0] == '\t') continue;
                var n = 0;
                while (n < line.Length && line[n] == ' ') n++;
                if (n > 0) spaceIndents.Add(n);
            }
            if (spaceIndents.Count == 0) return 4;
            return DetectIndentWidthFromIndents(spaceIndents);
        }
        var indentSet = new HashSet<int>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var n = 0;
            while (n < line.Length && line[n] == ' ') n++;
            if (n > 0) indentSet.Add(n);
        }
        if (indentSet.Count == 0) return 4;
        return DetectIndentWidthFromIndents(indentSet);
    }

    public static string? ExtractJsMethodNameFromChange(string change)
    {
        if (string.IsNullOrWhiteSpace(change)) return null;
        var m = Regex.Match(change,
            @"\b(?:add|create|insert|define|implement)\s+(?:a\s+)?(?:new\s+)?(?:method|function|handler)\s+(?:named\s+|called\s+)?([A-Za-z_$][A-Za-z0-9_$]*)",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(change,
            @"\b(?:add|create|insert|define|implement)\s+(?:a\s+)?(?:new\s+)?([A-Za-z_$][A-Za-z0-9_$]*)\s+(?:method|function|handler)\b",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(change,
            @"\b(?:add|create|insert|define|implement)\s+(?:the\s+)?([A-Za-z_$][A-Za-z0-9_$]*)\s*\(\s*\)",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(change,
            @"\b(?:add|create|insert|define|implement)\s+(?:the\s+)?(?:vm|this|self|that)\.([A-Za-z_$][A-Za-z0-9_$]*)",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(change,
            @"\b(?:add|create|insert)\s+(?:a\s+)?(?:new\s+)?([A-Za-z_$][A-Za-z0-9_$]*)\b");
        if (m.Success)
        {
            var candidate = m.Groups[1].Value;
            var stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "new", "code", "logic", "feature", "support",
            "validation", "test", "tests", "file", "block", "section", "comment"
        };
            if (!stopwords.Contains(candidate) && candidate.Length >= 3) return candidate;
        }
        return null;
    }

    public static bool JsMethodExistsInContent(string content, string methodName)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(methodName))
            return false;
        if (methodName.Length < 2) return false;
        var name = Regex.Escape(methodName);
        if (Regex.IsMatch(content, $@"\bfunction\s+{name}\s*\(", RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(content,
            $@"\b(?:const|let|var)\s+{name}\s*=\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(content,
            $@"\b(?:const|let|var)\s+{name}\s*=\s*(?:async\s+)?\([^)]*\)\s*=>",
            RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(content,
            $@"\b{name}\s*:\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(content,
            $@"\b{name}\s*:\s*(?:async\s+)?\([^)]*\)\s*=>",
            RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(content,
            $@"(?m)^\s*(?:static\s+|async\s+|get\s+|set\s+)?{name}\s*\(",
            RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(content,
            $@"\b(?:vm|this|self|that)\.{name}\s*=\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(content,
            $@"\b(?:vm|this|self|that)\.{name}\s*=\s*(?:async\s+)?\([^)]*\)\s*=>",
            RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(content,
            $@"\.prototype\.{name}\s*=\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(content,
            $@"\bexport\s+(?:async\s+)?function\s+{name}\s*\(",
            RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(content,
            $@"\bexport\s+(?:const|let|var)\s+{name}\s*=",
            RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(content,
            $@"Object\.defineProperty\s*\([^,]+,\s*['""]{name}['""]",
            RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(content,
            $@"(?m)^\s*(?:public\s+|private\s+|protected\s+|static\s+|async\s+|get\s+|set\s+|readonly\s+)*{name}\s*\(",
            RegexOptions.IgnoreCase))
            return true;
        return false;
    }

    public static string? ExtractJsMethodNameFromCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var m = Regex.Match(code, @"\bfunction\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*\(", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(code,
            @"\b(?:const|let|var)\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(code,
            @"\b(?:const|let|var)\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s+)?\([^)]*\)\s*=>",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(code,
            @"\b(?:vm|this|self|that)\.([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(code,
            @"\b(?:vm|this|self|that)\.([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s+)?\([^)]*\)\s*=>",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(code,
            @"\b([A-Za-z_$][A-Za-z0-9_$]*)\s*:\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(code,
            @"\b([A-Za-z_$][A-Za-z0-9_$]*)\s*:\s*(?:async\s+)?\([^)]*\)\s*=>",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(code,
            @"(?m)^\s*(?:static\s+|async\s+|get\s+|set\s+)?([A-Za-z_$][A-Za-z0-9_$]*)\s*\(");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(code,
            @"\.prototype\.([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s+)?function\s*\(",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(code,
            @"\bexport\s+(?:async\s+)?function\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*\(",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(code,
            @"\bexport\s+(?:const|let|var)\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*=",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        return null;
    }

    internal static int DetectIndentWidthFromIndents(HashSet<int> indents)
    {
        var list = indents.ToList();
        var gcd = list[0];
        for (var i = 1; i < list.Count; i++)
        {
            gcd = Gcd(gcd, list[i]);
            if (gcd == 1) break;
        }
        if (gcd is >= 1 and <= 8) return gcd;
        var min = list.Min();
        return (min > 0 && min <= 8) ? min : 4;
    }

    internal static int Gcd(int a, int b)
    {
        while (b > 0)
        {
            var t = b;
            b = a % b;
            a = t;
        }
        return a;
    }

    public static string? ExtractLocationTag(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"\b(todo|doing|done|selfimproving|self-improving)\b", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant().Replace("-", "") : null;
    }

    public static string? FindLastReturnLine(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        var lines = code.Split('\n', StringSplitOptions.None);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("return ") && trimmed.EndsWith(";"))
                return lines[i];
        }
        return null;
    }

    public static bool IsBuiltinIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        var keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if","for","while","switch","return","using","lock","catch","throw",
            "function","typeof","instanceof","in","of","do","else","try","finally",
            "await","async","yield","new","delete","void","this","super","extends",
            "implements","interface","class","struct","enum","namespace","import",
            "export","from","as","is","out","ref","params","var","let","const",
        };
        if (keywords.Contains(name)) return true;
        var builtins = new HashSet<string>(StringComparer.Ordinal)
        {
            "Math","JSON","Object","Array","String","Number","Boolean","Date",
            "Promise","Map","Set","WeakMap","WeakSet","Symbol","Reflect","Proxy",
            "Error","TypeError","RangeError","SyntaxError","RegExp","Function",
            "Console","console","window","document","globalThis","global",
            "Number","BigInt","Intl","WebAssembly","process","Buffer",
            "Task","List","Dictionary","HashSet","Enumerable","Action","Func",
            "Tuple","ValueTuple","KeyValuePair","Nullable","Convert","Console",
            "Exception","InvalidOperationException","ArgumentException","Guid",
            "DateTime","TimeSpan","StringBuilder","Regex","Encoding","JsonSerializer",
            "Path","File","Directory","Environment","Math","Random","CancellationToken",
            "length",
        };
        if (builtins.Contains(name)) return true;
        var standardMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "ToString", "Trim", "TrimStart", "TrimEnd", "Substring", "Split",
            "Replace", "Contains", "StartsWith", "EndsWith", "IndexOf", "LastIndexOf",
            "ToUpper", "ToLower", "Equals", "Compare", "CompareTo", "Concat", "Join",
            "IsNullOrEmpty", "IsNullOrWhiteSpace", "Format", "PadLeft", "PadRight",
            "Select", "Where", "FirstOrDefault", "First", "Last", "LastOrDefault",
            "Any", "All", "Count", "Sum", "Min", "Max", "Average", "ToList",
            "ToArray", "ToDictionary", "ToHashSet", "Distinct", "GroupBy",
            "OrderBy", "OrderByDescending", "ThenBy", "Skip", "Take", "Single",
            "SingleOrDefault", "ElementAt", "Reverse", "Add", "AddRange", "Remove",
            "RemoveAt", "Clear", "ContainsKey", "ContainsValue", "TryGetValue",
            "map", "filter", "reduce", "forEach", "find", "findIndex", "includes",
            "join", "concat", "flat", "flatMap", "some", "every", "sort", "push",
            "pop", "shift", "unshift", "splice", "slice", "stringify", "parse",
            "floor", "ceil", "round", "abs", "min", "max", "pow", "sqrt", "toFixed"
        };
        if (standardMethods.Contains(name)) return true;
        return false;
    }

    public static (string family, bool supportsFormatC, string llmHint) GetLanguageProfile(string ext)
    {
        return ext.ToLowerInvariant() switch
        {
            ".cs" => ("brace", true,
                "⚠ C# FILE: " +
                "USE FORMAT C (targetType/targetName/newCode) for FULL METHOD replacements or to ADD a new method (via insertAfter:true). " +
                "For SMALL targeted edits (1-5 lines, e.g. adding a field/property, changing a return value): " +
                "USE oldString/newString. This is the ONLY safe way to add properties/fields. " +
                "Do NOT use targetType='class' to add properties/fields. " +
                "INDENTATION: method signature at class-member level, body indented 4 spaces more."),
            ".ts" or ".tsx" => ("brace", true,
                "⚠ TS FILE: preserve ALL indentation exactly. Methods inside a class MUST be indented. " +
                "Preserve inline formatting: keep a space after colons in object literals ({key: value}) " +
                "and after commas in arrays/objects. " +
                "EDIT MODE DECISION GUIDE:\n" +
                "  • To MODIFY code within an existing method (change/add a few lines): use oldString/newString.\n" +
                "  • To REPLACE an entire existing method body: use FORMAT C (targetType='method', targetName='name') WITHOUT insertAfter.\n" +
                "  • To ADD a NEW method that does NOT exist in the file: use FORMAT C with insertAfter:true (targetType='method', targetName=existing method name).\n" +
                "  • To add properties/fields: use oldString/newString.\n" +
                "  • Do NOT use insertAfter:true when the method ALREADY EXISTS — that creates a DUPLICATE." +
                " Do NOT use targetType='class' — class REPLACE is blocked for .ts files."),
            ".js" or ".jsx" => ("brace", true,
                "⚠ JS FILE: preserve ALL indentation exactly. " +
                "Preserve inline formatting: keep a space after colons in object literals ({key: value}) " +
                "and after commas in arrays/objects. " +
                "FORMAT C supported (targetType='function'/'method', targetName='name'). " +
                "For small edits prefer oldString/newString."),
            ".java" => ("brace", true,
                "⚠ JAVA FILE: brace-based, similar to C#. " +
                "FORMAT C supported: targetType='method'/'class'/'interface'. " +
                "Preserve ALL annotations (@Override, @Autowired, etc.) exactly. " +
                "NEVER alter generic type parameters or throws clauses."),
            ".kt" or ".kts" => ("brace", true,
                "⚠ KOTLIN FILE: brace-based. FORMAT C supported: targetType='function'/'class'. " +
                "Preserve data class properties, suspend/inline/override modifiers, and lambda syntax exactly."),
            ".scala" => ("brace", true,
                "⚠ SCALA FILE: brace-based. FORMAT C supported: targetType='method'/'class'/'object'. " +
                "Preserve implicits, case class syntax, and for-comprehension indentation exactly."),
            ".go" => ("brace", true,
                "⚠ GO FILE: brace-based, uses TABS (not spaces) for indentation — never convert tabs to spaces. " +
                "FORMAT C supported: targetType='function', targetName='FunctionName'. " +
                "Preserve ALL error-handling idioms (if err != nil), defer statements, and goroutine patterns."),
            ".rs" => ("brace", true,
                "⚠ RUST FILE: brace-based. FORMAT C supported: targetType='function'/'impl'. " +
                "Preserve ALL lifetime annotations ('a), borrow markers (&, &mut), " +
                "ownership semantics, trait bounds, and match arm patterns EXACTLY."),
            ".c" or ".cpp" or ".cc" or ".cxx" or ".h" or ".hpp" => ("brace", false,
                "⚠ C/C++ FILE: brace-based, preprocessor directives must stay on their own line. " +
                "Use oldString/newString. Preserve #include order, extern \"C\", " +
                "template parameters, and pointer/reference syntax exactly."),
            ".swift" => ("brace", true,
                "⚠ SWIFT FILE: brace-based. FORMAT C supported: targetType='function'/'class'/'struct'. " +
                "Preserve access modifiers (open/public/internal/fileprivate/private), " +
                "property wrappers (@State, @Binding), and optional chaining exactly."),
            ".php" => ("brace", true,
                "⚠ PHP FILE: brace-based. FORMAT C supported: targetType='function'/'method'/'class'. " +
                "Preserve $ sigils on all variables, type hints, and nullable ? modifiers exactly."),
            ".dart" => ("brace", true,
                "⚠ DART FILE: brace-based. FORMAT C supported: targetType='function'/'class'. " +
                "Preserve async/await, null-safety operators (?., ??, ??=, !), and Widget tree indentation."),
            ".groovy" => ("brace", true,
                "⚠ GROOVY FILE: brace-based (Gradle/Groovy DSL). FORMAT C supported: targetType='method'. " +
                "Preserve closure syntax { ... }, GString interpolation, and Gradle DSL patterns."),
            ".rb" => ("end-keyword", true,
                "⚠ RUBY FILE: uses def/end, do/end, class/end block terminators — NOT braces. " +
                "FORMAT C supported: targetType='method', targetName='method_name' (snake_case). " +
                "Use oldString/newString for small edits. " +
                "Preserve Ruby idioms: ||=, &., symbol literals, and block/proc/lambda syntax."),
            ".lua" => ("end-keyword", false,
                "⚠ LUA FILE: uses function/end, if/end, for/end block terminators — NOT braces. " +
                "Use oldString/newString only. Preserve Lua table syntax, colon-method calls (:), " +
                "and global vs local variable scoping."),
            ".ex" or ".exs" => ("end-keyword", false,
                "⚠ ELIXIR FILE: uses do/end block terminators and pipe operators |>. " +
                "Use oldString/newString. Preserve pattern matching, atoms (:name), " +
                "and module attribute syntax (@doc, @spec)."),
            ".sh" or ".bash" or ".zsh" or ".fish" => ("end-keyword", false,
                "⚠ SHELL SCRIPT: uses if/fi, for/done, while/done, case/esac terminators. " +
                "Use oldString/newString. Preserve $() vs ``, quoting rules, " +
                "and test [ ] vs [[ ]] distinctions exactly."),
            ".ps1" or ".psm1" or ".psd1" => ("brace", false,
                "⚠ POWERSHELL FILE: brace-based, $Variables, -Flags syntax. " +
                "Use oldString/newString. Preserve $_ pipeline variable, " +
                "cmdlet verb-noun naming, and parameter attribute syntax."),
            ".py" or ".pyi" => ("indent", true,
                "⚠ PYTHON FILE: indentation IS the syntax — do NOT alter indent levels. " +
                "FORMAT C supported: targetType='function'/'class', targetName='name'. " +
                "For small edits prefer oldString/newString. " +
                "Copy every leading space/tab from the file exactly into oldString and newString. " +
                "Preserve type hints, decorators (@), and docstring quotes."),
            ".yaml" or ".yml" => ("indent", false,
                "⚠ YAML FILE: whitespace-significant. Use oldString/newString only. " +
                "NEVER change indentation levels — copy exactly. " +
                "Preserve anchors (&), aliases (*), and multiline block styles (|, >)."),
            ".fs" or ".fsx" or ".fsi" => ("indent", false,
                "⚠ F# FILE: whitespace-significant (offside rule). Use oldString/newString only. " +
                "Preserve pipeline |> operators, computation expressions, and discriminated union syntax."),
            ".hs" or ".lhs" => ("indent", false,
                "⚠ HASKELL FILE: whitespace-significant. Use oldString/newString only. " +
                "Preserve do-notation alignment, type class instances, and where-clause indentation."),
            ".coffee" => ("indent", false,
                "⚠ COFFEESCRIPT FILE: whitespace-significant, no braces. Use oldString/newString only."),
            ".html" or ".htm" => ("tag", false,
                "⚠ HTML FILE: tag-based indentation — child elements MUST be indented more than parent. " +
                "Use oldString/newString. Preserve attribute quoting, void element self-closing, " +
                "and Angular/Vue directive syntax exactly."),
            ".xml" or ".xaml" or ".axaml" => ("tag", false,
                "⚠ XML FILE: tag-based. Use oldString/newString. " +
                "Preserve namespace prefixes (xmlns:), attribute order, and CDATA sections."),
            ".cshtml" or ".razor" => ("tag", false,
                "⚠ RAZOR FILE: HTML with @C# expressions. Use oldString/newString. " +
                "Preserve @model, @inject, @Html.* helpers, and @{ } code blocks exactly."),
            ".vue" => ("tag", true,
                "⚠ VUE FILE: <template>/<script>/<style> sections. " +
                "FORMAT C supported for methods inside <script>. " +
                "For template changes use oldString/newString. Preserve v-bind/:, v-on/@, v-model directives."),
            ".svelte" => ("tag", false,
                "⚠ SVELTE FILE: <script>/<style>/template sections. Use oldString/newString. " +
                "Preserve $: reactive declarations, {#if}, {#each} blocks, and slot syntax."),
            ".svg" => ("tag", false,
                "⚠ SVG FILE: XML tag-based. Use oldString/newString. " +
                "Preserve viewBox, transform attributes, and path d= values exactly."),
            ".css" or ".scss" or ".less" => ("brace", false,
                "⚠ CSS/SCSS/LESS FILE: brace-based selectors. Use oldString/newString. " +
                "CRITICAL: oldString MUST be at most 4 lines — never replace an entire CSS block. " +
                "To change a CSS property value, set oldString to the ONE line containing that property " +
                "(copied verbatim from the file), and newString to that line with the new value. " +
                "Example: if changing `flex-direction: row;` to `flex-direction: column;`, " +
                "oldString = \"  flex-direction: row;\" (exact whitespace), newString = \"  flex-direction: column;\". " +
                "Preserve ALL whitespace in property values (e.g. '0 1px 2px rgba(0,0,0,0.5)' — " +
                "every space and comma is significant). Preserve SCSS variables ($var), mixins, and nesting."),
            ".json" => ("config", false,
                "⚠ JSON FILE: strict syntax — use oldString/newString only. " +
                "NO trailing commas, NO comments. Preserve ALL nested object structure exactly. " +
                "When editing arrays, include the full surrounding element for uniqueness."),
            ".toml" => ("config", false,
                "⚠ TOML FILE: use oldString/newString. Preserve [section] headers, " +
                "[[array-of-tables]], and inline table {key=val} syntax exactly."),
            ".env" or ".ini" => ("config", false,
                "⚠ CONFIG FILE: key=value pairs. Use oldString/newString. " +
                "Preserve comment lines (#) and section headers ([section]) exactly."),
            ".proto" => ("brace", false,
                "⚠ PROTOBUF FILE: brace-based. Use oldString/newString. " +
                "Preserve field numbers, oneof blocks, and option statements exactly."),
            ".sql" => ("plain", false,
                "⚠ SQL FILE: use oldString/newString. Preserve ALL whitespace in multi-line queries. " +
                "Match exact keyword casing (uppercase SQL keywords are conventional). " +
                "Preserve semicolons and comment styles (-- vs)."),
            ".graphql" or ".gql" => ("plain", false,
                "⚠ GRAPHQL FILE: use oldString/newString. Preserve type definitions, " +
                "field arguments, and directive (@deprecated, @skip) syntax exactly."),
            ".md" or ".mdx" => ("plain", false,
                "⚠ MARKDOWN FILE: use oldString/newString. " +
                "Preserve heading levels (# vs ##), list markers (-, *, 1.), " +
                "and fenced code block language tags exactly."),
            ".rst" => ("indent", false,
                "⚠ RST FILE: indentation-significant section underlines. Use oldString/newString. " +
                "Preserve directive syntax (.. directive::) and role syntax (:role:`text`)."),
            _ => ("plain", false,
                "⚠ Preserve ALL indentation and whitespace exactly as shown in the file. " +
                "Use oldString/newString. Copy every leading space/tab character-for-character.")
        };
    }

    internal static bool LooksLikeCodeIdentifier(string word)
    {
        if (string.IsNullOrWhiteSpace(word) || word.Length < 2) return false;
        // PascalCase or camelCase with uppercase transitions
        if (word.Any(char.IsUpper)) return true;
        // Single-word lowercase like "login", "render", "parse" — accept if > 2 chars and not a common English word
        return word.Length >= 3 && !CommonEnglishWords.Contains(word) && !CommonEnglishWords.Contains(word.ToLowerInvariant());
    }

    internal static readonly HashSet<string> CommonEnglishWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "but", "not", "you", "all", "any", "can", "had", "her", "was", "one", "our", "out",
        "has", "have", "him", "his", "how", "its", "may", "per", "say", "she", "two", "way", "who", "why", "yes",
        "about", "above", "after", "again", "below", "between", "could", "every", "first", "following",
        "going", "great", "half", "hand", "head", "home", "house", "hours", "just", "keep", "kind", "know",
        "large", "last", "leave", "left", "less", "life", "line", "list", "long", "look", "love", "made",
        "make", "many", "might", "more", "most", "much", "must", "name", "need", "never", "next", "number",
        "often", "once", "only", "open", "order", "other", "over", "own", "part", "place", "play",
        "point", "power", "press", "price", "print", "process", "product", "project",
        "public", "purpose", "quality", "question", "quick", "quite", "race", "range", "rate", "rather",
        "reach", "read", "ready", "real", "reason", "record", "region", "report", "result", "handle",
        "return", "right", "rise", "risk", "road", "role", "room", "rule", "run", "safe", "sale",
        "same", "save", "scale", "school", "science", "score", "screen", "search",
        "season", "second", "section", "security", "select", "sense", "series", "serious", "service",
        "session", "set", "seven", "several", "short", "show", "side", "sign", "significant", "similar",
        "simple", "since", "single", "site", "situation", "six", "size", "skill", "small", "social",
        "society", "some", "sort", "sound", "source", "south", "space", "speak", "special", "specific",
        "spend", "sport", "spring", "staff", "stage", "stand", "standard", "start", "state", "step",
        "still", "stock", "stop", "store", "story", "straight", "strategy", "street", "strength",
        "strong", "structure", "student", "study", "subject", "success", "such", "sudden", "suggest",
        "summer", "support", "sure", "surface", "system", "table", "take", "talk", "task", "teach",
        "team", "technology", "tell", "term", "test", "than", "thank", "that", "their", "them",
        "themselves", "then", "there", "these", "they", "thing", "think", "third", "this",
        "those", "though", "thought", "three", "through", "throughout", "throw", "thus", "time",
        "together", "top", "total", "touch", "toward", "town", "trade", "train", "travel",
        "trouble", "true", "trust", "truth", "try", "turn", "type", "under", "understand", "unit",
        "until", "upon", "use", "usual", "value", "various", "very", "view", "visit", "voice",
        "wait", "walk", "wall", "want", "war", "watch", "water", "way", "wear", "week", "weight",
        "well", "west", "western", "what", "whatever", "when", "where", "whether", "which", "while",
        "white", "who", "whole", "whom", "whose", "why", "wide", "will", "win", "wind", "window",
        "wish", "within", "without", "woman", "wonder", "word", "work", "worker", "world", "worry",
        "would", "write", "writer", "wrong", "year", "young", "your", "yourself", "handle",
        "before", "during", "including", "according", "regarding", "following", "existing",
        "current", "previous", "various", "different", "another", "example", "purpose", "result"
    };

    public static string? ExtractTargetSymbolFromChange(string change)
    {
        if (string.IsNullOrWhiteSpace(change)) return null;
        var m = Regex.Match(change, @"\b([A-Za-z_]\w*)\s*\(", RegexOptions.IgnoreCase);
        if (m.Success && LooksLikeCodeIdentifier(m.Groups[1].Value)) return m.Groups[1].Value;
        m = Regex.Match(change, @"\b(?:class|struct|interface|record)\s+([A-Za-z_]\w*)", RegexOptions.IgnoreCase);
        if (m.Success && LooksLikeCodeIdentifier(m.Groups[1].Value)) return m.Groups[1].Value;
        m = Regex.Match(change, @"\b([A-Z]\w*(?:DTO|Dto|Model|Request|Response|Controller|Service))\b");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(change, @"\bmethod\s+([A-Za-z_]\w*)", RegexOptions.IgnoreCase);
        if (m.Success && LooksLikeCodeIdentifier(m.Groups[1].Value)) return m.Groups[1].Value;
        m = Regex.Match(change, @"\b(?:in|inside)\s+(?:the\s+)?([A-Z]\w+)\b");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(change, @"\b(?:to|inside|in)\s+([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.IgnoreCase);
        if (m.Success && LooksLikeCodeIdentifier(m.Groups[1].Value)) return m.Groups[1].Value;
        // Generic camelCase with at least one uppercase transition (e.g. showNotification, getData, sendEmail)
        // Check BEFORE "symbol call" pattern to avoid matching stopwords like "with call"
        m = Regex.Match(change, @"\b([a-z][a-zA-Z0-9]{2,}(?:[A-Z][a-z0-9]+)+)\b");
        if (m.Success)
        {
            var candidate = m.Groups[1].Value;
            if (LooksLikeCodeIdentifier(candidate)) return candidate;
        }
        // "symbol call" or "symbol method" or "symbol function" — lowercase-starting camelCase
        m = Regex.Match(change, @"\b([a-z]\w*[A-Z]\w*)\s+(?:call|method|function)\b", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var candidate = m.Groups[1].Value;
            if (LooksLikeCodeIdentifier(candidate)) return candidate;
        }
        // "method symbol" or "function symbol" pattern
        m = Regex.Match(change, @"\b(?:method|function)\s+([A-Za-z_]\w*)\b", RegexOptions.IgnoreCase);
        if (m.Success && LooksLikeCodeIdentifier(m.Groups[1].Value)) return m.Groups[1].Value;
        return null;
    }

    public static string ExtractMethodBodiesByKeywords(string content, string taskDesc)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";
        var targetSymbol = ExtractTargetSymbolFromChange(taskDesc);
        var keywords = ExtractMeaningfulKeywords(taskDesc.ToLowerInvariant());
        var lines = content.Split('\n');
        var methods = new List<(string body, int score)>();
        var currentMethod = new List<string>();
        var inMethod = false;
        var braceDepth = 0;
        var methodScore = 0;
        var methodName = "";
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            // Detect method/function signatures across C#, TS, JS, Python, etc.
            // Skip lines that are clearly expressions or assignments rather than declarations.
            if (trimmed.Contains('=') || trimmed.Contains("=>"))
            {
                if (inMethod)
                {
                    currentMethod.Add(line);
                    braceDepth += line.Count(c => c == '{') - line.Count(c => c == '}');
                    methodScore += keywords.Count(k => line.Contains(k, StringComparison.OrdinalIgnoreCase));
                    if (braceDepth <= 0)
                    {
                        inMethod = false;
                        var body = string.Join("\n", currentMethod);
                        if (methodScore > 0 || (currentMethod.Count > 3 && currentMethod.Count < 60))
                        {
                            methods.Add((body, methodScore));
                        }
                        if (!string.IsNullOrWhiteSpace(targetSymbol) &&
                            string.Equals(methodName, targetSymbol, StringComparison.OrdinalIgnoreCase))
                        {
                            methods.Add((body, 1000));
                        }
                        methodName = "";
                        currentMethod.Clear();
                    }
                }
                continue;
            }
            var signatureMatch = Regex.Match(trimmed,
                @"^\s*(?:async\s+)?(?:public|private|protected|internal|static|override|virtual|export\s+)?(?:function|def|func)?\s*([A-Za-z_][A-Za-z0-9_]*)\s*\(",
                RegexOptions.IgnoreCase);
            if (!inMethod && signatureMatch.Success)
            {
                inMethod = true;
                methodName = signatureMatch.Groups[1].Value;
                braceDepth = line.Count(c => c == '{') - line.Count(c => c == '}');
                currentMethod.Clear();
                currentMethod.Add(line);
                methodScore = keywords.Count(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)) * 5;
                if (!string.IsNullOrWhiteSpace(targetSymbol) &&
                    string.Equals(methodName, targetSymbol, StringComparison.OrdinalIgnoreCase))
                {
                    methodScore += 50;
                }
                continue;
            }
            if (inMethod)
            {
                currentMethod.Add(line);
                braceDepth += line.Count(c => c == '{') - line.Count(c => c == '}');
                methodScore += keywords.Count(k => line.Contains(k, StringComparison.OrdinalIgnoreCase));
                if (braceDepth <= 0)
                {
                    inMethod = false;
                    var body = string.Join("\n", currentMethod);
                    if (methodScore > 0 || (currentMethod.Count > 3 && currentMethod.Count < 60))
                    {
                        methods.Add((body, methodScore));
                    }
                    if (!string.IsNullOrWhiteSpace(targetSymbol) &&
                        string.Equals(methodName, targetSymbol, StringComparison.OrdinalIgnoreCase))
                    {
                        methods.Add((body, 1000));
                    }
                    methodName = "";
                    currentMethod.Clear();
                }
            }
        }
        var topMethods = methods
            .OrderByDescending(m => m.score)
            .Take(8)
            .Select(m => m.body)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (topMethods.Count == 0 && !string.IsNullOrWhiteSpace(targetSymbol))
        {
            var symbolMatch = Regex.Match(content, $@"\b{Regex.Escape(targetSymbol)}\b.*?\{{", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (symbolMatch.Success)
            {
                var startIdx = symbolMatch.Index;
                var openBrace = content.IndexOf('{', startIdx);
                var depth = 0;
                var endIdx = -1;
                for (var idx = openBrace; idx < content.Length; idx++)
                {
                    if (content[idx] == '{') depth++;
                    else if (content[idx] == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            endIdx = idx;
                            break;
                        }
                    }
                }
                if (endIdx >= 0)
                {
                    topMethods.Add(content[startIdx..(endIdx + 1)]);
                }
            }
        }
        return string.Join("\n\n---\n\n", topMethods);
    }

    public static string? FindTypeDefinitionInContext(string typeName, string context)
    {
        if (string.IsNullOrWhiteSpace(context) || string.IsNullOrWhiteSpace(typeName))
        { return null; }
        var declPattern = @"(class|record|struct)\s+" + Regex.Escape(typeName) + @"\b";
        var decl = Regex.Match(context, declPattern);
        if (!decl.Success) { return null; }
        var startIdx = decl.Index;
        var braceStart = context.IndexOf('{', startIdx);
        if (braceStart < 0) return null;
        var depth = 0;
        var endIdx = -1;
        for (var i = braceStart; i < context.Length; i++)
        {
            if (context[i] == '{') depth++;
            else if (context[i] == '}') { depth--; if (depth == 0) { endIdx = i; break; } }
        }
        if (endIdx < 0) return null;
        return context[startIdx..(endIdx + 1)].Trim();
    }

    public static string ExtractTypeNameForLog(string classDef)
    {
        var m = Regex.Match(classDef, @"\b(class|record|struct)\s+([A-Za-z_][A-Za-z0-9_]*)");
        return m.Success ? m.Groups[2].Value : "?";
    }

    public static int CountRoslynErrors(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return 0;
        try
        {
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(content);
            return tree.GetDiagnostics().Count(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        }
        catch
        {
            return 0;
        }
    }
}
