// Pure, order-independent file-tree builder for the IDE file explorer.
//
// Extracted from wwwroot/ide.js (the old inline _buildFileTree) into a standalone
// module so the exact same code runs in the browser AND can be exercised by the
// seeded-fuzz corpus in tests/UnitTests/FileTreeOrderIndependenceTests.cs (which
// spawns `node wwwroot/filetree.js` with a JSON listing on stdin and asserts the
// tree invariants against dozens of randomly-shuffled orderings).
//
// Order-independence: the backend (FileEditController.List with recursive=true)
// interleaves directories and files sorted by path, so a folder's entry is NOT
// guaranteed to arrive before its children. Every node is attached to its parent
// exactly once through a path->node map (ensureDir), which prevents phantom
// folders, duplicate folders, and orphaned subtrees regardless of entry order.
//
// UMD: browser global `WeaverFileTree.buildFileTree(entries)`; Node
// `require('./filetree.js')` returns the same; run as `node filetree.js` it reads
// a JSON array of {path, isDirectory, name} entries from stdin and prints the
// flattened tree (array of {path, isDirectory}) to stdout for the unit corpus.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory();
  else root.WeaverFileTree = factory();
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  function buildFileTree(entries) {
    var root = { name: '', path: '', isDirectory: true, children: [], depth: 0 };
    var map = { '': root };
    // Ensure a directory node exists for path and is attached to its parent.
    function ensureDir(path) {
      if (map[path]) return map[path];
      var node = { name: path.split('/').pop(), path: path, isDirectory: true, children: [], depth: 0 };
      map[path] = node;
      var slash = path.lastIndexOf('/');
      var parentPath = slash <= 0 ? '' : path.slice(0, slash);
      ensureDir(parentPath).children.push(node);
      return node;
    }
    (entries || []).forEach(function (e) {
      if (!e || !e.path) return;
      var path = e.path.replace(/\\/g, '/');
      var slash = path.lastIndexOf('/');
      var parentPath = slash <= 0 ? '' : path.slice(0, slash);
      var parent = ensureDir(parentPath);
      // If the entry is the real directory record, reuse (don't duplicate)
      // the node the children may have already created implicitly, and
      // adopt the backend's authoritative display name.
      if (e.isDirectory) {
        var dn = ensureDir(path);
        if (e.name) dn.name = e.name;
      } else {
        var node = { name: e.name || path.split('/').pop(), path: path, isDirectory: false, children: null, depth: 0 };
        if (!map[path]) map[path] = node;
        // Dedupe: a file path can appear once per listing, but guard anyway.
        if (parent.children.indexOf(map[path]) === -1) parent.children.push(map[path]);
      }
    });
    // Stable ordering: directories first, then files, both alphabetical.
    function sortChildren(node) {
      if (!node.children) return;
      node.children.sort(function (a, b) {
        if (a.isDirectory !== b.isDirectory) return a.isDirectory ? -1 : 1;
        return (a.name || '').localeCompare(b.name || '');
      });
      node.children.forEach(sortChildren);
    }
    sortChildren(root);
    return root;
  }

  // ── Node CLI mode (used by the unit-test corpus) ──────────────────────────
  // Reads a JSON array of entries from stdin, prints the flattened tree to stdout.
  if (typeof process !== 'undefined' && process.stdin &&
      typeof require !== 'undefined' && require.main === module) {
    var chunks = [];
    process.stdin.on('data', function (c) { chunks.push(c); });
    process.stdin.on('end', function () {
      var entries;
      try {
        entries = JSON.parse(Buffer.concat(chunks).toString('utf8'));
      } catch (e) {
        process.stderr.write('Bad JSON input: ' + e.message + '\n');
        process.exit(1);
      }
      var tree = buildFileTree(entries);
      var flat = [];
      (function walk(n, parentPath) {
        if (n.path) flat.push({ path: n.path, isDirectory: !!n.isDirectory, parent: parentPath || '' });
        if (n.children) n.children.forEach(function (c) { walk(c, n.path); });
      })(tree, '');
      process.stdout.write(JSON.stringify(flat));
    });
  }

  return { buildFileTree: buildFileTree };
});
