using System.Text.RegularExpressions;
using TreeSitter;
namespace Weaver.Services;
public static class AstCodeEditorService
{
    private static readonly HashSet<string> JsLikeLanguages = new(StringComparer.OrdinalIgnoreCase)
        { "JavaScript", "TypeScript", "TSX" };

    /// <summary>
    /// After an edit is applied to a .ts/.js file, parse with Tree-sitter and
    /// insert missing tokens (commas, semicolons) that the parser expected.
    /// Re-parses in a loop until clean or no more fixable errors.
    /// </summary>
    public static string AutoFixSyntaxErrors(string content, string fileExtension)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;
        if (!LanguageMap.TryGetValue(fileExtension, out var langName)) return content;
        if (!JsLikeLanguages.Contains(langName)) return content;

        for (var iter = 0; iter < 10; iter++)
        {
            Language language;
            Parser parser;
            Tree? tree;
            try
            {
                language = new Language(langName);
                parser = new Parser(language);
                tree = parser.Parse(content);
            }
            catch
            {
                return content;
            }
            using (language) using (parser) using (tree)
            {
                if (tree == null || !tree.RootNode.HasError) return content;

                var missingNodes = new List<(string Type, int Pos)>();
                CollectMissingNodes(tree.RootNode, missingNodes);
                if (missingNodes.Count == 0) return content;

                missingNodes.Sort((a, b) => b.Pos.CompareTo(a.Pos));
                var changed = false;
                foreach (var (type, pos) in missingNodes)
                {
                    var insert = type switch
                    {
                        "," => ",",
                        ";" => ";",
                        _ => null
                    };
                    if (insert == null) continue;
                    if (pos >= 0 && pos <= content.Length)
                    {
                        content = content.Insert(pos, insert);
                        changed = true;
                    }
                }
                if (!changed) return content;
            }
        }
        return content;
    }

    private static void CollectMissingNodes(Node node, List<(string Type, int Pos)> results)
    {
        if (node.IsMissing)
            results.Add((node.Type, node.EndIndex));
        if (node.Children != null)
        {
            foreach (var child in node.Children)
                CollectMissingNodes(child, results);
        }
    }
    // Maps file extension -> TreeSitter grammar name (for new Language(name))
    private static readonly Dictionary<string, string> LanguageMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".ts", "TypeScript" },
        { ".tsx", "TSX" },
        { ".js", "JavaScript" },
        { ".jsx", "JavaScript" },
        { ".mjs", "JavaScript" },
        { ".cjs", "JavaScript" },
        { ".cs", "c-sharp" }, // NB: native lib is tree-sitter-c-sharp.dll — "c_sharp" fails to load
        { ".py", "python" },
        { ".rb", "ruby" },
        { ".go", "go" },
        { ".rs", "rust" },
        { ".java", "java" },
        { ".php", "php" },
        { ".c", "c" },
        { ".h", "c" },
        { ".cpp", "cpp" },
        { ".cc", "cpp" },
        { ".cxx", "cpp" },
        { ".hpp", "cpp" },
        { ".css", "css" },
        { ".swift", "swift" },
        { ".scala", "scala" },
        { ".hs", "haskell" },
        { ".jl", "julia" },
        { ".sh", "bash" },
        { ".bash", "bash" },
        { ".zsh", "bash" },
        { ".toml", "toml" },
        { ".ql", "ql" },
        { ".razor", "razor" },
    };
    // TreeSitter query patterns to find named declarations, grouped by grammar name.
    // Each pattern captures @name (the declaration name) and @target (the full node).
    private static readonly Dictionary<string, string[]> QueryPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TypeScript"] =
        [
            "(property_definition name: (property_identifier) @name) @target",
            "(method_definition name: (property_identifier) @name) @target",
            "(function_declaration name: (identifier) @name) @target",
            "(method_signature name: (property_identifier) @name) @target",
            "(function_signature name: (identifier) @name) @target",
            "(generator_method name: (property_identifier) @name) @target",
            "(generator_declaration name: (identifier) @name) @target",
            "(class_declaration name: (type_identifier) @name) @target",
            "(interface_declaration name: (type_identifier) @name) @target",
            "(enum_declaration name: (identifier) @name) @target",
        ],
        ["TSX"] =
        [
            "(property_definition name: (property_identifier) @name) @target",
            "(method_definition name: (property_identifier) @name) @target",
            "(function_declaration name: (identifier) @name) @target",
            "(method_signature name: (property_identifier) @name) @target",
            "(function_signature name: (identifier) @name) @target",
            "(generator_method name: (property_identifier) @name) @target",
            "(generator_declaration name: (identifier) @name) @target",
            "(class_declaration name: (type_identifier) @name) @target",
            "(interface_declaration name: (type_identifier) @name) @target",
        ],
        ["JavaScript"] =
        [
            "(method_definition name: (property_identifier) @name) @target",
            "(function_declaration name: (identifier) @name) @target",
            "(generator_method name: (property_identifier) @name) @target",
            "(generator_declaration name: (identifier) @name) @target",
            "(class_declaration name: (identifier) @name) @target",
            "(export_statement declaration: (function_declaration name: (identifier) @name)) @target",
        ],
        ["c-sharp"] =
        [
            "(method_declaration name: (identifier) @name) @target",
            "(local_function_statement name: (identifier) @name) @target",
            "(constructor_declaration name: (identifier) @name) @target",
            "(class_declaration name: (identifier) @name) @target",
            "(struct_declaration name: (identifier) @name) @target",
            "(interface_declaration name: (identifier) @name) @target",
            "(record_declaration name: (identifier) @name) @target",
            "(enum_declaration name: (identifier) @name) @target",
            "(property_declaration name: (identifier) @name) @target",
        ],
        ["python"] =
        [
            "(function_definition name: (identifier) @name) @target",
            "(class_definition name: (identifier) @name) @target",
            "(decorated_definition definition: (function_definition name: (identifier) @name)) @target",
            "(decorated_definition definition: (class_definition name: (identifier) @name)) @target",
        ],
        ["ruby"] =
        [
            "(method name: (identifier) @name) @target",
            "(singleton_method name: (identifier) @name) @target",
            "(class name: (constant) @name) @target",
            "(module name: (constant) @name) @target",
        ],
        ["go"] =
        [
            "(function_declaration name: (identifier) @name) @target",
            "(method_declaration name: (field_identifier) @name) @target",
            "(type_declaration (type_spec name: (type_identifier) @name)) @target",
        ],
        ["rust"] =
        [
            "(function_item name: (identifier) @name) @target",
            "(struct_item name: (type_identifier) @name) @target",
            "(enum_item name: (type_identifier) @name) @target",
            "(trait_item name: (type_identifier) @name) @target",
            "(impl_item trait: (type_identifier) @name) @target",
            "(impl_item type: (type_identifier) @name) @target",
            "(type_item name: (type_identifier) @name) @target",
            "(constant_item name: (identifier) @name) @target",
            "(static_item name: (identifier) @name) @target",
        ],
        ["java"] =
        [
            "(method_declaration name: (identifier) @name) @target",
            "(class_declaration name: (identifier) @name) @target",
            "(interface_declaration name: (identifier) @name) @target",
            "(enum_declaration name: (identifier) @name) @target",
            "(constructor_declaration name: (identifier) @name) @target",
            "(record_declaration name: (identifier) @name) @target",
            "(annotation_type_declaration name: (identifier) @name) @target",
        ],
        ["php"] =
        [
            "(method_declaration name: (name) @name) @target",
            "(function_definition name: (name) @name) @target",
            "(class_declaration name: (name) @name) @target",
            "(interface_declaration name: (name) @name) @target",
            "(trait_declaration name: (name) @name) @target",
            "(enum_declaration name: (name) @name) @target",
        ],
        ["c"] =
        [
            "(function_definition declarator: (function_declarator declarator: (identifier) @name)) @target",
        ],
        ["cpp"] =
        [
            "(function_definition declarator: (function_declarator declarator: (identifier) @name)) @target",
            "(class_specifier name: (type_identifier) @name) @target",
            "(struct_specifier name: (type_identifier) @name) @target",
            "(enum_specifier name: (type_identifier) @name) @target",
            "(template_declaration declaration: (function_definition declarator: (function_declarator declarator: (identifier) @name))) @target",
            "(template_declaration declaration: (class_specifier name: (type_identifier) @name)) @target",
        ],
        ["css"] =
        [
            "(rule_set (selectors) @name) @target",
        ],
        ["swift"] =
        [
            "(function_declaration name: (identifier) @name) @target",
            "(method_declaration name: (identifier) @name) @target",
            "(class_declaration name: (identifier) @name) @target",
            "(struct_declaration name: (identifier) @name) @target",
            "(enum_declaration name: (identifier) @name) @target",
            "(protocol_declaration name: (identifier) @name) @target",
            "(extension_declaration name: (identifier) @name) @target",
            "(constructor_declaration name: (identifier) @name) @target",
            "(destructor_declaration name: (identifier) @name) @target",
        ],
        ["scala"] =
        [
            "(function_definition name: (identifier) @name) @target",
            "(class_definition name: (identifier) @name) @target",
            "(trait_definition name: (identifier) @name) @target",
            "(object_definition name: (identifier) @name) @target",
            "(enum_definition name: (identifier) @name) @target",
        ],
        ["haskell"] =
        [
            "(function name: (variable) @name) @target",
            "(class name: (type) @name) @target",
            "(instance name: (type) @name) @target",
            "(data name: (type) @name) @target",
            "(type name: (type) @name) @target",
        ],
        ["julia"] =
        [
            "(function_definition name: (identifier) @name) @target",
            "(macro_definition name: (identifier) @name) @target",
            "(struct_definition name: (identifier) @name) @target",
            "(abstract_definition name: (identifier) @name) @target",
            "(primitive_definition name: (identifier) @name) @target",
            "(module_definition name: (identifier) @name) @target",
        ],
        ["bash"] =
        [
            "(function_definition name: (word) @name) @target",
        ],
    }; 
    public static bool IsSupportedExtension(string fileExt) => LanguageMap.ContainsKey(fileExt.ToLowerInvariant());
    public static List<(string name, string source, int startLine)> FindAllFunctions(
        string fileContent, string fileExtension)
    {
        var results = new List<(string name, string source, int startLine)>();
        if (!LanguageMap.TryGetValue(fileExtension.ToLowerInvariant(), out var langName))
            return results;
        var patterns = QueryPatterns.GetValueOrDefault(langName);
        if (patterns == null || patterns.Length == 0)
            return results;
        try
        {
            using var language = new Language(langName);
            using var parser = new Parser(language);
            using var tree = parser.Parse(fileContent);
            if (tree == null) return results;
            foreach (var pattern in patterns)
            {
                Query query;
                try { query = new Query(language, pattern); }
                catch { continue; }
                using (query)
                {
                    var allCaptures = query.Execute(tree.RootNode).Captures.ToList();
                    var nameByStart = new Dictionary<int, string>();
                    foreach (var c in allCaptures)
                    {
                        if (c.Name == "name")
                            nameByStart[c.Node.StartIndex] = c.Node.Text;
                    }
                    foreach (var capture in allCaptures)
                    {
                        if (capture.Name != "method" && capture.Name != "target" && capture.Name != "func")
                            continue;
                        var targetStart = capture.Node.StartIndex;
                        var targetEnd = capture.Node.EndIndex;
                        var resolvedName = nameByStart
                            .Where(kvp => kvp.Key >= targetStart && kvp.Key < targetEnd)
                            .OrderBy(kvp => kvp.Key)
                            .Select(kvp => kvp.Value)
                            .FirstOrDefault();
                        if (string.IsNullOrWhiteSpace(resolvedName))
                            continue;
                        var startIndex = capture.Node.StartIndex;
                        var endIndex = capture.Node.EndIndex;
                        var lineStart = fileContent.LastIndexOf('\n', startIndex) + 1;
                        if (lineStart < 0) lineStart = 0;
                        var fullOldStr = fileContent[lineStart..endIndex]
                            .Replace("\r\n", "\n").Replace("\r", "\n");
                        var startLine = capture.Node.StartPosition.Row + 1;
                        results.Add((resolvedName, fullOldStr, startLine));
                    }
                }
            }
            return results;
        }
        catch
        {
            return results;
        }
    }
    // Node types that represent a CLASS-like declaration across the supported grammars.
    // Used to decide whether a resolved target is a whole class (so we can narrow it to
    // an inner method when the change description references `Class.method`).
    private static readonly HashSet<string> ClassLikeNodeTypes = new(StringComparer.Ordinal)
    {
        "class_definition",
        "class_declaration",
        "class_specifier",
        "struct_declaration",
        "struct_specifier",
        "interface_declaration",
        "record_declaration",
        "trait_definition",
        "object_definition",
        "enum_declaration",
        "enum_definition",
        "module",
    };

    /// <summary>
    /// Extracts the `method` part of a `Class.method` reference in a change description,
    /// e.g. "In MyHTTPRequestHandler.do_GET method (around line 23)" → "do_GET".
    /// Only dotted references anchored to the given class name are accepted, so a generic
    /// "method X" mention in a class-scoped step never narrows to the wrong member.
    /// </summary>
    public static string? ExtractInnerMethodFromChange(string? changeDescription, string className)
    {
        if (string.IsNullOrWhiteSpace(changeDescription) || string.IsNullOrWhiteSpace(className))
            return null;
        var m = Regex.Match(changeDescription,
            $@"\b{Regex.Escape(className)}\s*\.\s*([A-Za-z_]\w*)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Resolves <paramref name="targetSymbol"/> to its exact source text. Supports dotted
    /// `Class.method` symbols (resolves the method inside the class), and — when the symbol
    /// resolves to a whole CLASS — narrows the oldString to the inner method referenced by
    /// <paramref name="changeDescription"/> (e.g. "In MyHTTPRequestHandler.do_GET method"
    /// resolves just the <c>do_GET</c> method instead of the entire class, which previously
    /// made the focused replacement replace the whole class with a bare method → SyntaxError).
    /// </summary>
    public static (string? oldString, int startLine, string? error) FindFunctionSource(
        string fileContent, string targetSymbol, string fileExtension, string? changeDescription = null)
    {
        if (!LanguageMap.TryGetValue(fileExtension.ToLowerInvariant(), out var langName))
            return (null, 0, $"Unsupported extension: {fileExtension}");
        var patterns = QueryPatterns.GetValueOrDefault(langName);
        if (patterns == null || patterns.Length == 0)
            return (null, 0, $"No query patterns for {langName}");
        try
        {
            using var language = new Language(langName);
            using var parser = new Parser(language);
            using var tree = parser.Parse(fileContent);
            if (tree == null)
                return (null, 0, "Failed to parse file");
            // Execute ALL query patterns and collect every declaration target (class and
            // function/method nodes) with its name into one list — the dotted and class-narrowing
            // logic needs to see methods AND their enclosing class across patterns.
            var classTargets = new List<(Node Node, string Name)>();
            var declTargets = new List<(Node Node, string Name)>();
            foreach (var pattern in patterns)
            {
                Query q2;
                try { q2 = new Query(language, pattern); }
                catch { continue; }
                using (q2)
                {
                    var allCaptures = q2.Execute(tree.RootNode).Captures.ToList();
                    // Build a map: name-node start-index → name text for all @name captures
                    var nameByStart = new Dictionary<int, string>();
                    foreach (var c in allCaptures)
                    {
                        if (c.Name == "name")
                            nameByStart[c.Node.StartIndex] = c.Node.Text;
                    }
                    foreach (var capture in allCaptures)
                    {
                        if (capture.Name != "method" && capture.Name != "target" && capture.Name != "func")
                            continue;
                        var resolvedName = ResolveNameForTarget(nameByStart, capture.Node);
                        if (string.IsNullOrWhiteSpace(resolvedName))
                            continue;
                        declTargets.Add((capture.Node, resolvedName));
                        if (ClassLikeNodeTypes.Contains(capture.Node.Type))
                            classTargets.Add((capture.Node, resolvedName));
                    }
                }
            }
            // ── Dotted symbol: `Class.method` → resolve the method inside the class ──
            var dotIdx = targetSymbol.LastIndexOf('.');
            if (dotIdx > 0 && dotIdx < targetSymbol.Length - 1)
            {
                var clsName = targetSymbol[..dotIdx];
                var methName = targetSymbol[(dotIdx + 1)..];
                foreach (var (clsNode, clsMatch) in classTargets)
                {
                    if (!string.Equals(clsMatch, clsName, StringComparison.Ordinal))
                        continue;
                    foreach (var (fnNode, fnName) in declTargets)
                    {
                        if (string.Equals(fnName, methName, StringComparison.Ordinal)
                            && fnNode.StartIndex >= clsNode.StartIndex && fnNode.EndIndex <= clsNode.EndIndex)
                            return BuildSource(fileContent, fnNode);
                    }
                    return (null, 0, $"'{methName}' not found inside class '{clsName}' in {langName} file");
                }
            }
            // ── Exact symbol match ──
            foreach (var (node, name) in declTargets)
            {
                if (!string.Equals(name, targetSymbol, StringComparison.Ordinal))
                    continue;
                // If the target is a whole CLASS but the change description names a member
                // (`Class.method`), narrow the oldString to just that method so the focused
                // replacement rewrites the method — not the entire class.
                if (ClassLikeNodeTypes.Contains(node.Type))
                {
                    var inner = ExtractInnerMethodFromChange(changeDescription, targetSymbol);
                    if (inner != null)
                    {
                        foreach (var (fnNode, fnName) in declTargets)
                        {
                            if (string.Equals(fnName, inner, StringComparison.Ordinal)
                                && fnNode.StartIndex >= node.StartIndex && fnNode.EndIndex <= node.EndIndex)
                                return BuildSource(fileContent, fnNode);
                        }
                    }
                }
                return BuildSource(fileContent, node);
            }
            return (null, 0, $"'{targetSymbol}' not found in {langName} file");
        }
        catch (Exception ex)
        {
            return (null, 0, $"Tree-sitter error: {ex.Message}");
        }
    }

    private static string? ResolveNameForTarget(Dictionary<int, string> nameByStart, Node targetNode)
    {
        return nameByStart
            .Where(kvp => kvp.Key >= targetNode.StartIndex && kvp.Key < targetNode.EndIndex)
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => kvp.Value)
            .FirstOrDefault();
    }

    private static (string oldString, int startLine, string? error) BuildSource(string fileContent, Node node)
    {
        var startIndex = node.StartIndex;
        var endIndex = node.EndIndex;
        var lineStart = fileContent.LastIndexOf('\n', startIndex) + 1;
        if (lineStart < 0) lineStart = 0;
        var fullOldStr = fileContent[lineStart..endIndex];
        fullOldStr = fullOldStr.Replace("\r\n", "\n").Replace("\r", "\n");
        var startLine = node.StartPosition.Row + 1;
        return (fullOldStr, startLine, null);
    }
}
