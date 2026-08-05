using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Seeded-fuzz corpus for the IDE file explorer's tree builder
/// (<c>wwwroot/filetree.js</c> — the pure helper extracted from the old inline
/// <c>_buildFileTree</c> in ide.js; the browser wires it via
/// <c>WeaverFileTree.buildFileTree</c>).
///
/// The backend (<c>FileEditController.List</c> with recursive=true) interleaves
/// directories and files sorted by path, so a folder's entry is NOT guaranteed to
/// arrive before its children. The OLD builder assumed parents-first: a file
/// processed before its directory created an implicit node that was never attached
/// (orphaned subtree → lost files), and the real dir entry later replaced the map
/// entry (phantom empty folder). The fix builds every node through a path→node map
/// (<c>ensureDir</c>) so the result is identical for ANY entry order.
///
/// This corpus exercises the REAL helper (not a C# mirror) by spawning
/// <c>node wwwroot/filetree.js</c> with the listing JSON on stdin and parsing the
/// flattened tree it prints. For every seeded random listing it tries MULTIPLE
/// orderings — the natural one, dirs-first, files-first (children before parents,
/// the pathological case), reverse-alphabetical, and a seeded Fisher-Yates shuffle —
/// and asserts the four invariants that locked the fix: every file present exactly
/// once, every real directory present exactly once, zero phantom directories, and
/// zero duplicate nodes.
/// </summary>
public class FileTreeOrderIndependenceTests
{
    private const int DocCount = 40;
    // Fresh (seed, prime) — 60613/104729 is taken by FormatDPayloadCorpusTests;
    // FuzzHarness requires a unique pair per corpus so no two share an RNG stream.
    private const int Seed = 51513;
    private const int Prime = 165901;

    // ── Node runner ────────────────────────────────────────────────────────────

    private static readonly string HelperPath = LocateHelper();

    private static string LocateHelper()
    {
        // Tests run from tests/UnitTests/bin/<cfg>/<tfm>/ — walk up to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "wwwroot", "filetree.js");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Cannot locate wwwroot/filetree.js — the pure tree builder must exist for this corpus.");
    }

    /// <summary>
    /// Locates an executable on PATH (plus the current directory, which
    /// Process.Start also searches) so the corpus can fail with a clear diagnostic
    /// instead of the cryptic Win32Exception "The system cannot find the file
    /// specified" that Process.Start throws when node is missing.
    /// </summary>
    private static string? FindOnPath(string fileName)
    {
        var dirs = new List<string> { Environment.CurrentDirectory };
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        dirs.AddRange(pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in dirs)
        {
            var candidate = Path.Combine(dir.Trim(), fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static void EnsureNodeOnPath()
    {
        // Windows: only node.exe qualifies — with UseShellExecute=false,
        // Process.Start cannot launch .cmd/.bat shims (Win32Exception 193), so
        // accepting node.cmd here would pass the pre-check and then fail at spawn.
        var names = OperatingSystem.IsWindows() ? new[] { "node.exe" } : new[] { "node" };
        foreach (var name in names)
        {
            if (FindOnPath(name) != null) return;
        }
        throw new InvalidOperationException(
            "The file-tree order-independence corpus spawns `node wwwroot/filetree.js` to exercise the REAL " +
            "tree builder, but Node.js was not found on PATH. Install Node.js (or add it to PATH) and re-run " +
            "the tests — this corpus deliberately tests the shipped helper, so it cannot fall back to a C# mirror.");
    }

    /// <summary>
    /// Runs the real helper via Node: writes <paramref name="entriesJson"/> (a JSON
    /// array of {path,isDirectory,name}) to its stdin and returns the parsed
    /// flattened tree ({path,isDirectory,parent} list, root-relative, dirs sorted
    /// first). The <c>parent</c> field lets the corpus assert each node hangs off
    /// exactly the directory its path implies — closing the wrong-but-reachable
    /// parent gap (a node present once but attached under the wrong directory).
    /// </summary>
    private static List<(string path, bool isDirectory, string parent)> RunHelper(string entriesJson)
    {
        EnsureNodeOnPath();
        var psi = new ProcessStartInfo("node")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // ArgumentList quotes/escapes each argument itself, so a HelperPath
        // containing spaces is passed as a single path — the old manual
        // $"\"{HelperPath}\"" quoting relied on the argument parser's re-splitting
        // rules and broke on paths with spaces + certain characters.
        psi.ArgumentList.Add(HelperPath);
        Process proc;
        try
        {
            proc = Process.Start(psi)!;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Belt-and-braces: the pre-check above normally catches a missing
            // node, but a race (PATH change mid-run) or exotic spawn failure
            // lands here — surface it as an actionable message either way.
            throw new InvalidOperationException(
                $"Failed to start `node {HelperPath}` ({ex.Message}). Ensure Node.js is installed and on PATH.", ex);
        }
        using (proc)
        {
            proc.StandardInput.Write(entriesJson);
            proc.StandardInput.Close();
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            Assert.True(proc.WaitForExit(30_000), "node filetree.js timed out");
            Assert.True(proc.ExitCode == 0, $"node filetree.js failed:\n{stderr}");

            using var doc = JsonDocument.Parse(stdout);
            var flat = new List<(string, bool, string)>();
            foreach (var el in doc.RootElement.EnumerateArray())
                flat.Add((el.GetProperty("path").GetString()!,
                          el.GetProperty("isDirectory").GetBoolean(),
                          el.TryGetProperty("parent", out var p) ? p.GetString()! : ""));
            return flat;
        }
    }

    // ── Seeded listing generator ───────────────────────────────────────────────

    /// <summary>Builds a random tree as a flat listing with the given depth/dirs/files
    /// budgets. Returns the listing plus the expected dir/file path sets.</summary>
    private static (string json, HashSet<string> files, HashSet<string> dirs) BuildListing(Random rng, int docIdx)
    {
        var entries = new List<Dictionary<string, object>>();
        var files = new HashSet<string>(StringComparer.Ordinal);
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        var dirNamePool = new[] { "Controllers", "Services", "Models", "wwwroot", "Views", "Data", "Utils", "Core" };
        var fileExts = new[] { ".cs", ".ts", ".js", ".html", ".css", ".json", ".md" };

        // Random depth 1-4, 2-5 top-level dirs.
        var topCount = 2 + rng.Next(4);
        for (var t = 0; t < topCount; t++)
        {
            var depth = 1 + rng.Next(4);
            var path = dirNamePool[rng.Next(dirNamePool.Length)] + docIdx + "_" + rng.Next(100);
            dirs.Add(path);
            entries.Add(Entry(path, true, path.Split('/').Last()));
            for (var d = 1; d < depth; d++)
            {
                path += "/" + dirNamePool[rng.Next(dirNamePool.Length)] + "_" + rng.Next(100);
                if (dirs.Add(path))
                    entries.Add(Entry(path, true, path.Split('/').Last()));
            }
            // 1-4 files in the deepest dir.
            var fileCount = 1 + rng.Next(4);
            for (var f = 0; f < fileCount; f++)
            {
                var filePath = path + "/" + "file" + docIdx + "_" + f + fileExts[rng.Next(fileExts.Length)];
                files.Add(filePath);
                entries.Add(Entry(filePath, false, filePath.Split('/').Last()));
            }
        }
        // A couple of files directly at the root to exercise the '' parent path.
        for (var r = 0; r < 1 + rng.Next(3); r++)
        {
            var rootFile = "root" + docIdx + "_" + r + fileExts[rng.Next(fileExts.Length)];
            files.Add(rootFile);
            entries.Add(Entry(rootFile, false, rootFile));
        }
        // Roughly every 3rd listing: rewrite a random subset of paths with Windows
        // backslashes. The helper normalizes '\\' → '/' before building, so the
        // expected file/dir sets stay forward-slash and must still match the tree.
        if (docIdx % 3 == 0)
        {
            var count = 1 + rng.Next(Math.Max(1, entries.Count / 3));
            for (var b = 0; b < count && entries.Count > 0; b++)
            {
                var idx = rng.Next(entries.Count);
                var raw = entries[idx]["path"]!.ToString()!;
                if (!raw.Contains('/')) continue;
                entries[idx]["path"] = raw.Replace('/', '\\');
            }
        }
        return (JsonSerializer.Serialize(entries), files, dirs);
    }

    private static Dictionary<string, object> Entry(string path, bool isDirectory, string name) => new()
    {
        ["path"] = path,
        ["isDirectory"] = isDirectory,
        ["name"] = name
    };

    /// <summary>Seeded Fisher-Yates shuffle of the raw listing strings.</summary>
    private static List<string> Shuffle(Random rng, List<string> list)
    {
        var copy = new List<string>(list);
        for (var i = copy.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return copy;
    }

    // ── The corpus ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 40 seeded listings × 5 orderings each: natural, dirs-first, files-first
    /// (children before parents — the case that broke the old builder),
    /// reverse-alphabetical, and a seeded shuffle. Every ordering must produce the
    /// same tree: no phantom dirs, no duplicates, every file and real dir exactly
    /// once. The files-first and shuffle passes are guaranteed non-degenerate by
    /// FuzzHarness.AssertExercised (a listing where a file precedes its parent must
    /// actually occur).
    /// </summary>
    [Fact]
    public void Fuzz_BuildFileTree_OrderIndependent_NoPhantomsNoDuplicates()
    {
        var checkedDocs = 0;
        var childBeforeParentPasses = 0;
        var backslashPaths = 0;

        for (var i = 0; i < DocCount; i++)
        {
            var listingRng = FuzzHarness.SeededRng(Seed, i, Prime);
            var (json, files, dirs) = BuildListing(listingRng, i);
            var raw = JsonDocument.Parse(json).RootElement.EnumerateArray()
                .Select(el => el.GetRawText())
                .ToList();

            // 1. Natural order (as generated).
            AssertInvariants(i, "natural", json, files, dirs, ref childBeforeParentPasses, ref backslashPaths);

            // 2. Dirs first, then files.
            var dirsFirst = raw.Where(r => IsDirEntry(r)).Concat(raw.Where(r => !IsDirEntry(r))).ToList();
            AssertInvariants(i, "dirs-first", Serialize(dirsFirst), files, dirs, ref childBeforeParentPasses, ref backslashPaths);

            // 3. Files first, then dirs — children ALWAYS precede their parent dirs.
            //    This is the ordering the old builder corrupted (orphaned subtrees).
            var filesFirst = raw.Where(r => !IsDirEntry(r)).Concat(raw.Where(r => IsDirEntry(r))).ToList();
            AssertInvariants(i, "files-first", Serialize(filesFirst), files, dirs, ref childBeforeParentPasses, ref backslashPaths);

            // 4. Reverse-alphabetical by path (deepest paths first).
            var revAlpha = raw.OrderByDescending(r =>
                JsonDocument.Parse(r).RootElement.GetProperty("path").GetString(), StringComparer.Ordinal).ToList();
            AssertInvariants(i, "reverse-alpha", Serialize(revAlpha), files, dirs, ref childBeforeParentPasses, ref backslashPaths);

            // 5. Seeded Fisher-Yates shuffle.
            var shuffled = Shuffle(FuzzHarness.SeededRng(Seed + 777, i, Prime), raw);
            AssertInvariants(i, "shuffle", Serialize(shuffled), files, dirs, ref childBeforeParentPasses, ref backslashPaths);

            checkedDocs++;
        }

        FuzzHarness.AssertAllDocsChecked(checkedDocs, DocCount, "file-tree order-independence corpus");
        FuzzHarness.AssertExercised(childBeforeParentPasses,
            "no corpus pass ever placed a file BEFORE its parent directory — the pathological case was not exercised");
        FuzzHarness.AssertExercised(backslashPaths,
            "no corpus listing exercised backslash path normalization");
    }

    private static bool IsDirEntry(string raw) =>
        JsonDocument.Parse(raw).RootElement.GetProperty("isDirectory").GetBoolean();

    private static string Serialize(List<string> raws) => "[" + string.Join(",", raws) + "]";

    /// <summary>
    /// Runs the real helper on one ordering and asserts the four invariants that
    /// lock the order-independence fix:
    ///   1. every FILE from the listing is present exactly once;
    ///   2. every real DIRECTORY is present exactly once;
    ///   3. zero phantom directories — every dir node has a real dir entry (no
    ///      implicitly-created dir that never got its own listing entry, and no
    ///      orphaned empty folder);
    ///   4. zero duplicate nodes — no path appears twice.
    /// Also counts a pass as exercising child-before-parent if any file preceded
    /// its parent dir in the input ordering (verified structurally from the tree).
    /// </summary>
    private static void AssertInvariants(int docIdx, string pass, string json,
        HashSet<string> files, HashSet<string> dirs,
        ref int childBeforeParentPasses, ref int backslashPaths)
    {
        var flat = RunHelper(json);

        // All four invariants (plus parent linkage) in one predicate — shared with
        // the deliberately-regressing reference test so the corpus and the old
        // builder are checked against EXACTLY the same rules.
        var violation = InvariantViolation(flat, files, dirs);
        Assert.True(violation == null, $"doc #{docIdx} [{pass}]: {violation}");

        // Did this pass actually put a file before its parent dir in the input?
        // Detect from the flat tree: count files whose parent dir exists as a node
        // AFTER them in the flattened output is unreliable; instead detect from the
        // input ordering: a file path whose parent dir path appears later in the raw.
        if (FileBeforeParentOccurred(json, files, dirs))
            childBeforeParentPasses++;
        if (json.Contains("\\\\"))
            backslashPaths++;
    }

    /// <summary>
    /// Returns a human-readable reason the flat tree violates the corpus
    /// invariants, or null when it satisfies all of them:
    ///   1. every FILE from the listing is present exactly once;
    ///   2. every real DIRECTORY is present exactly once;
    ///   3. zero phantom directories — every dir node has a real dir entry (no
    ///      implicitly-created dir that never got its own listing entry, and no
    ///      orphaned empty folder);
    ///   4. zero duplicate nodes — no path appears twice;
    ///   5. every node hangs off exactly the directory its path implies (parent
    ///      linkage — a node present once but attached under the wrong parent).
    /// </summary>
    private static string? InvariantViolation(
        List<(string path, bool isDirectory, string parent)> flat,
        HashSet<string> files, HashSet<string> dirs)
    {
        // Counts keyed by path, split by node kind.
        var fileCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var dirCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (path, isDir, _) in flat)
            (isDir ? dirCounts : fileCounts)[path] = (isDir ? dirCounts : fileCounts).GetValueOrDefault(path) + 1;

        // 1 + 4. Every file exactly once (a file node can never be a dir node, so
        // the fileCounts dictionary is authoritative per path).
        foreach (var f in files)
            if (fileCounts.GetValueOrDefault(f) != 1)
                return $"file '{f}' has {fileCounts.GetValueOrDefault(f)} node(s) — must be exactly 1";

        // 2. Every real directory exactly once.
        foreach (var d in dirs)
            if (dirCounts.GetValueOrDefault(d) != 1)
                return $"real dir '{d}' has {dirCounts.GetValueOrDefault(d)} node(s) — must be exactly 1";

        // 3. Zero phantom directories — the set of dir nodes must equal the real dirs
        //    exactly (no extras, and no real dir missing — the latter already covered).
        if (dirCounts.Keys.Count != dirs.Count)
            return $"{dirCounts.Keys.Count} dir node(s) but {dirs.Count} real dir(s) — phantom directory present";

        // No duplicates across the whole tree (a path can't be both kinds, and the
        // per-kind counts are exactly 1 above, so total nodes == total paths).
        if (flat.Count != files.Count + dirs.Count)
            return $"tree has {flat.Count} node(s) but listing has {files.Count + dirs.Count} entries";

        // 5. Parent linkage — every node must hang off exactly the directory its
        //    path implies. A node present exactly once but attached under the WRONG
        //    (still-reachable) parent would pass the counts above.
        var parentByPath = flat.ToDictionary(n => n.path, n => n.parent, StringComparer.Ordinal);
        foreach (var (path, isDir, parent) in flat)
        {
            var expectedParent = ParentOf(path) ?? "";
            if (!string.Equals(parent, expectedParent, StringComparison.Ordinal))
                return $"node '{path}' is attached under '{parent}' but its path implies '{expectedParent}'";
            if (parent.Length > 0 && !(parentByPath.ContainsKey(parent) && dirs.Contains(parent)))
                return $"node '{path}' parent '{parent}' is not a real directory node";
            if (isDir && parent.Length > 0 && !dirs.Contains(path))
                return $"'{path}' is a directory node but has no real dir entry (phantom)";
        }
        return null;
    }

    /// <summary>True if any file entry appears in the listing before its parent dir entry.</summary>
    private static bool FileBeforeParentOccurred(string json, HashSet<string> files, HashSet<string> dirs)
    {
        using var doc = JsonDocument.Parse(json);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var path = el.GetProperty("path").GetString()!;
            if (!el.GetProperty("isDirectory").GetBoolean())
            {
                // Does a parent dir of this file still need to arrive?
                var parent = ParentOf(path);
                while (parent != null)
                {
                    if (dirs.Contains(parent) && !seen.Contains(parent))
                        return true;
                    parent = ParentOf(parent);
                }
            }
            seen.Add(path);
        }
        return false;
    }

    private static string? ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? null : path[..slash];
    }

    // ── Deliberately-regressing reference (the OLD algorithm) ──────────────────
    // The corpus above proves the FIXED builder is order-independent. This
    // section proves the corpus itself has teeth: a faithful C# reproduction of
    // the OLD parents-first-only builder must FAIL the very same invariants on
    // the files-first pass. If this test starts passing, the corpus no longer
    // catches the phantom-folder bug it was built to lock in.

    private sealed class OldTreeNode
    {
        public string Path = "";
        public bool IsDirectory;
        public string Parent = "";
        public List<OldTreeNode> Children = new();
    }

    /// <summary>
    /// Faithful reproduction of the old <c>_buildFileTree</c>: it assumed directory
    /// entries always arrived before their children. Two defects, exactly as the
    /// class doc describes:
    ///   • a file processed before its directory created an implicit node that was
    ///     never attached to the tree — the file's subtree became orphaned (lost
    ///     files);
    ///   • when the real dir entry arrived later it REPLACED the map entry with a
    ///     fresh node, so the folder came back phantom-empty.
    /// Only parents-first orderings survive; any ordering where a file precedes its
    /// parent dir loses that file and/or leaves a phantom empty folder.
    /// </summary>
    private static List<(string path, bool isDirectory, string parent)> BuildParentsFirstOnly(
        IReadOnlyList<(string path, bool isDirectory)> entries)
    {
        // Each path maps to the last node created for it. Children are attached
        // under their parent's CURRENT node.
        var map = new Dictionary<string, OldTreeNode>(StringComparer.Ordinal);
        var rootChildren = new List<OldTreeNode>();

        OldTreeNode EnsureNode(string path, bool isDir)
        {
            if (map.TryGetValue(path, out var existing)) return existing;
            var node = new OldTreeNode { Path = path, IsDirectory = isDir };
            map[path] = node;
            return node;
        }

        foreach (var (rawPath, isDir) in entries)
        {
            var path = rawPath.Replace('\\', '/');
            var parentPath = ParentOf(path) ?? "";
            if (isDir)
            {
                // A real dir entry creates a FRESH node and REPLACES any implicit
                // one the map already held — the old implicit node (and whatever
                // files were attached to it) is discarded → phantom empty folder.
                var node = new OldTreeNode { Path = path, IsDirectory = true };
                map[path] = node;
                if (parentPath.Length == 0) rootChildren.Add(node);
                else
                {
                    var parent = EnsureNode(parentPath, true);
                    node.Parent = parentPath;
                    parent.Children.Add(node);
                }
            }
            else
            {
                // A file attaches under its parent's CURRENT map node.
                var node = new OldTreeNode { Path = path, IsDirectory = false };
                if (parentPath.Length == 0) rootChildren.Add(node);
                else
                {
                    var parent = EnsureNode(parentPath, true);
                    node.Parent = parentPath;
                    parent.Children.Add(node);
                }
            }
        }

        // Flatten what is actually reachable from the root — the tree is built by
        // walking from a sentinel root; any implicit node that got REPLACED in the
        // map is no longer reachable (its files vanish), exactly like the old
        // builder. A node is only emitted when it is reachable from the sentinel.
        var sentinel = new OldTreeNode();
        sentinel.Children.AddRange(rootChildren);
        var flat = new List<(string, bool, string)>();
        void Walk(OldTreeNode n)
        {
            if (n.Path.Length > 0) flat.Add((n.Path, n.IsDirectory, n.Parent));
            foreach (var c in n.Children) Walk(c);
        }
        Walk(sentinel);
        return flat;
    }

    /// <summary>
    /// Reference test with teeth: the OLD parents-first-only builder must sail
    /// through the natural (parents-first) pass — proving the reproduction is
    /// faithful to the old happy path — and must FAIL the files-first pass,
    /// because that is the ordering that used to lose files and leave phantom
    /// empty folders. If the corpus ever stops catching the old bug, this test
    /// fails with a loud message instead of silently passing.
    /// </summary>
    [Fact]
    public void Regression_OldParentsFirstBuilder_FailsFilesFirstPass()
    {
        var naturalPassed = 0;
        var filesFirstCaught = 0;
        var filesFirstExercised = 0;
        var docsWithNestedFiles = 0;
        string? firstNaturalViolation = null;

        for (var i = 0; i < DocCount; i++)
        {
            var listingRng = FuzzHarness.SeededRng(Seed, i, Prime);
            var (json, files, dirs) = BuildListing(listingRng, i);
            var entries = JsonDocument.Parse(json).RootElement.EnumerateArray()
                .Select(el => (path: el.GetProperty("path").GetString()!, isDirectory: el.GetProperty("isDirectory").GetBoolean()))
                .ToList();

            // Non-vacuity guard: this doc must actually contain a nested file
            // (depth >= 2) for either verdict below to mean anything — a doc whose
            // files are all at the root loses nothing on a files-first pass, so it
            // can neither prove the happy path nor catch the old builder.
            if (HasNestedFile(files, dirs)) docsWithNestedFiles++;

            // Natural order (parents-first, as generated): the old builder's happy
            // path must pass the exact same invariants the fixed builder passes.
            var natural = BuildParentsFirstOnly(entries);
            var naturalViolation = InvariantViolation(natural, files, dirs);
            if (naturalViolation == null) naturalPassed++;
            else firstNaturalViolation ??= $"doc #{i}: {naturalViolation}";

            // Files-first (children before parents): the pathological ordering. The
            // OLD builder must be caught by the corpus invariants here.
            var filesFirst = entries.Where(e => !e.isDirectory).Concat(entries.Where(e => e.isDirectory)).ToList();
            if (FileBeforeParentOccurred(Serialize(filesFirst.Select(ToRaw).ToList()), files, dirs)) filesFirstExercised++;
            var ffTree = BuildParentsFirstOnly(filesFirst);
            if (InvariantViolation(ffTree, files, dirs) != null) filesFirstCaught++;
        }

        // The two == DocCount verdicts above would be vacuous if the listing
        // generator ever degraded to root-level (or depth-1-only) files: the old
        // builder never orphans a root file, and a files-first pass with zero
        // nesting loses nothing — so both would pass trivially and the regression
        // would silently go stale. Require a proportional share of docs to carry
        // at least one nested file (depth >= 2) so the happy-path/failure verdicts
        // are forced to do real work against real nesting. The bar is 3/4, well
        // below the generator's ~94% floor (each top dir rolls depth 1-4 uniform,
        // so a nested file is virtually guaranteed per doc), so it stays flake-free
        // while still failing loudly the moment the generator flattens.
        Assert.True(docsWithNestedFiles >= DocCount * 3 / 4,
            $"only {docsWithNestedFiles}/{DocCount} corpus docs contain a nested file (depth >= 2) — " +
            "the naturalPassed/filesFirstCaught verdicts are VACUOUS; the listing generator no longer " +
            "exercises real nesting, so this regression test cannot catch the phantom-folder bug");

        Assert.True(naturalPassed == DocCount,
            $"old parents-first-only builder failed {DocCount - naturalPassed}/{DocCount} natural pass(es) — " +
            $"the reproduction is not faithful to the old algorithm's happy path. First violation: {firstNaturalViolation}");
        FuzzHarness.AssertExercised(filesFirstExercised,
            "no files-first pass placed a file before its parent — the regression reference was not exercised");
        // The old builder deterministically loses every nested file on files-first
        // (depth >= 1 in every listing), so it must be caught on ALL of them — not
        // just one. Any doc where it survives means the corpus no longer catches
        // the phantom-folder bug it was built to lock in.
        Assert.True(filesFirstCaught == DocCount,
            $"old parents-first-only builder survived the files-first pass on {DocCount - filesFirstCaught}/{DocCount} " +
            $"listing(s) — the corpus invariants NO LONGER CATCH the phantom-folder bug they were built to detect");
    }

    /// <summary>
    /// True when the listing contains at least one FILE nested at depth >= 2
    /// (e.g. <c>a/b/f.ts</c> — a file whose parent dir has a parent of its own).
    /// Only such files can be orphaned by the old parents-first builder on a
    /// files-first pass, so their presence is what makes a doc's pass/fail
    /// verdict meaningful rather than vacuous.
    /// </summary>
    private static bool HasNestedFile(HashSet<string> files, HashSet<string> dirs)
    {
        foreach (var f in files)
        {
            var parent = ParentOf(f);
            if (parent == null || !dirs.Contains(parent)) continue;
            // Depth >= 2: the file's parent dir sits inside another directory.
            if (ParentOf(parent) != null) return true;
        }
        return false;
    }

    private static string ToRaw((string path, bool isDirectory) e) =>
        JsonSerializer.Serialize(new { path = e.path, isDirectory = e.isDirectory });
}
