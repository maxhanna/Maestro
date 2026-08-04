'use strict';

angular.module('kanbanApp').factory('IDEMixin', function($http, $timeout, $interval) {
  return {
    init: function(vm, $scope) {
      vm.ide = {
        showSidebar: true,
        openTabs: [],
        currentFile: null,
        currentTab: null,
        dirty: false,
        syncing: false,
        filePickerPath: '',
        filePickerEntries: [],
        filePickerError: '',
        filePickerLoading: false,
        searchFilter: '',
        lastSavedContent: null,
        pendingFileListing: null,
        pendingFileContent: null,
        sharedEditorActive: false,
        sharedFiles: [],
        conflictFiles: {},
        searchQuery: '',
        searchMatches: [],
        searchCurrentIdx: -1,
        searchVisible: false,
        // File-tree context menu + rename + drag-move state
        treeMenu: { visible: false, x: 0, y: 0, entry: null },
        renamingPath: null,
        renameDraft: '',
        // Restored from the settings localStorage store (stashed by SettingsMixin)
        // so the on/off state survives a page reload; defaults to visible.
        minimapVisible: vm._savedIdeMinimapVisible === undefined ? true : vm._savedIdeMinimapVisible,
        gitDiffVisible: false,
        gitDiffLoading: false,
        gitDiffData: null,
        gitDiffError: '',
        gitDiffView: 'list',
        gitDiffFilePath: '',
        gitDiffRows: [],
        gitCommitMessage: '',
        gitCommitBusy: false,
        gitCommitStatus: '',
        gitCommitResult: '',
        gitCommitError: '',
        gitPrUrl: '',
        left: 60,
        top: 60,
        width: 600,
        height: 400
      };
      var _searchMarks = [];
      var _searchDebounce = null;
      var _minimapEl = null;
      var _minimapCanvas = null;
      var _minimapBgCanvas = null;
      var _minimapScheduled = false;
      var _minimapOverlayOnly = false;
      var _minimapBgDirty = true;
      var _minimapDragging = false;
      var _minimapWindowBound = false;
      var _minimapResizeObs = null;
      var _minimapTipEl = null;
      // Git diff markers: [{ line: 0-based new-file line, kind: 'add'|'remove'|'modify' }]
      var _minimapDiffMarkers = null;
      var _minimapDiffPath = null;   // file the current markers were computed for
      var _minimapDiffCache = {};    // path → markers (avoid refetching on tab switches)


      var _contentSyncDebounce = null;

      // ── File-change polling ──────────────────────────────────────────────
      var _pollInterval = null;
      function startFileChangePolling() {
        if (_pollInterval) return;
        _pollInterval = $interval(function () {
          if (vm.shuttingDown || !vm.ide.openTabs || vm.ide.openTabs.length === 0) return;
          vm.ide.openTabs.forEach(function (tab) {
            if (!tab.path || !tab.lastModified) return;
            $http.get('/api/editor/check-modified', {
              params: { project: vm.selectedProject || '', path: tab.path, since: tab.lastModified }
            }).then(function (resp) {
              var data = resp.data;
              if (!data || !data.exists) return;
              if (!data.modified) {
                tab.lastModified = data.lastModified;
                return;
              }
              // File was modified externally
              if (tab.dirty) {
                // User has unsaved changes — flag conflict, don't overwrite
                tab.externalModified = true;
                return;
              }
              // No unsaved changes — reload silently
              $http.get('/api/editor/content', {
                params: { project: vm.selectedProject || '', path: tab.path }
              }).then(function (cr) {
                var newContent = cr.data && cr.data.content !== undefined ? cr.data.content : (cr.data || '');
                var wasCurrent = vm.ide.currentFile === tab.path;
                tab.content = newContent;
                tab.savedContent = newContent;
                tab.dirty = false;
                tab.lastModified = cr.data.lastModified || data.lastModified;
                tab.lineCount = (newContent.match(/\n/g) || []).length + 1;
                tab.externalModified = false;
                if (wasCurrent) {
                  vm.ide.dirty = false;
                  if (vm._editor) {
                    vm._editorIgnoreChange = true;
                    var cursor = vm._editor.getCursor();
                    vm._editor.setValue(newContent);
                    vm._editor.setCursor(cursor);
                    vm._editorIgnoreChange = false;
                  }
                }
              });
            });
          });
        }, 3000);
      }
      function stopFileChangePolling() {
        if (_pollInterval) {
          $interval.cancel(_pollInterval);
          _pollInterval = null;
        }
      }

      vm.toggleSidebar = function() {
        vm.ide.showSidebar = !vm.ide.showSidebar;
      };

      vm.openFileBrowser = function() {
        vm.toggleSidebar();
        // Always reload when opening — fixes empty directory on startup
        if (vm.selectedProject) {
          vm.loadFilePickerEntries();
        } else {
          // selectedProject not loaded yet — retry after a short delay
          $timeout(function() {
            if (vm.selectedProject) vm.loadFilePickerEntries();
          }, 500);
        }
      };

      vm.loadFilePickerEntries = function(search) {
        var params = { project: vm.selectedProject || '' };
        if (search) {
          params.search = search;
        } else {
          // Load entire tree recursively so folders can be expanded without replacing the list
          params.recursive = true;
        }
        if (!search && vm.ide.filePickerPath) {
          params.path = vm.ide.filePickerPath;
        }
        console.log('loadFilePickerEntries', params);
        vm.ide.filePickerLoading = true;
        $http.get('/api/editor/list', { params: params }).then(function(resp) {
          console.log('loadFilePickerEntries response', resp.data);
          var entries = (resp.data && resp.data.entries) || [];
          if (params.recursive && !search) {
            vm._buildFileTree(entries);
          } else {
            // Search results stay flat
            vm.ide.filePickerEntries = entries;
            vm.ide._treeRoot = null;
          }
          vm.ide.filePickerError = '';
          vm.ide.filePickerLoading = false;
        }, function(err) {
          console.log('loadFilePickerEntries error', err);
          vm.ide.filePickerError = (err.data && typeof err.data === 'string' ? err.data : (err.statusText || 'Failed to load files'));
          vm.ide.filePickerLoading = false;
        });
      };

      // Build tree from flat recursive entries and compute display list
      vm._expandedDirs = {};
      vm._buildFileTree = function(entries) {
        var root = { name: '', path: '', isDirectory: true, children: [], depth: 0 };
        var map = { '': root };
        // Build tree nodes
        entries.forEach(function(e) {
          var parts = e.path.split('/');
          var parentPath = '';
          for (var i = 0; i < parts.length - 1; i++) {
            parentPath = parentPath ? parentPath + '/' + parts[i] : parts[i];
            if (!map[parentPath]) {
              map[parentPath] = { name: parts[i], path: parentPath, isDirectory: true, children: [], depth: 0 };
            }
          }
          var node = { name: e.name, path: e.path, isDirectory: e.isDirectory, children: e.isDirectory ? [] : null, depth: 0 };
          map[parentPath].children.push(node);
          if (e.isDirectory) map[e.path] = node;
        });
        vm.ide._treeRoot = root;
        vm._rebuildTreeDisplay();
      };

      vm._rebuildTreeDisplay = function() {
        var result = [];
        function walk(node, depth) {
          node.depth = depth;
          if (node.path) result.push(node);
          if (node.children && vm._expandedDirs[node.path || '__root__']) {
            node.children.forEach(function(child) { walk(child, depth + 1); });
          }
        }
        // Always show root children (top-level dirs/files)
        if (vm.ide._treeRoot) {
          vm._expandedDirs['__root__'] = true;
          walk(vm.ide._treeRoot, -1);
        }
        vm.ide.filePickerEntries = result;
      };

      vm.toggleTreeDir = function(node) {
        if (!node.isDirectory) {
          vm.openFile(node.path);
          return;
        }
        var key = node.path || '__root__';
        if (vm._expandedDirs[key]) {
          delete vm._expandedDirs[key];
        } else {
          vm._expandedDirs[key] = true;
        }
        vm._rebuildTreeDisplay();
      };

      vm.refreshFileTree = function() {
        vm._expandedDirs = {};
        vm.ide.filePickerPath = '';
        vm.loadFilePickerEntries();
      };

      vm.idePickerEnterDir = function(path) {
        // Legacy: expand the directory in the tree
        vm._expandedDirs[path] = true;
        vm._rebuildTreeDisplay();
      };

      vm.idePickerUpDir = function() {
        vm.refreshFileTree();
      };

      // ── File-tree context menu (right-click) ───────────────────────────────
      vm._treeDragPath = null;   // path being dragged
      vm._treeDropPath = null;   // directory currently hovered as drop target

      vm.onTreeEntryMouseDown = function($event, e) {
        if ($event.button !== 2) return; // left/middle click: normal behavior
        $event.preventDefault();
        $event.stopPropagation();
        vm._treeDragPath = null;
        vm._treeDropPath = null;
        vm.ide.treeMenu.visible = false;
        // Position menu clamped to viewport
        var menuW = 190, menuH = 210;
        var x = Math.min($event.clientX, window.innerWidth - menuW);
        var y = Math.min($event.clientY, window.innerHeight - menuH);
        vm.ide.treeMenu = { visible: true, x: Math.max(0, x), y: Math.max(0, y), entry: e };
      };

      vm.closeTreeMenu = function() {
        vm.ide.treeMenu.visible = false;
      };

      vm.treeMenuAction = function(action) {
        var entry = vm.ide.treeMenu.entry;
        var menuPath = entry ? entry.path : '';
        var isDir = entry ? entry.isDirectory : false;
        vm.ide.treeMenu.visible = false;
        if (action === 'refresh') { vm.refreshFileTree(); return; }
        if (action === 'edit') {
          if (entry) { if (isDir) vm.toggleTreeDir(entry); else vm.openFile(entry.path); }
          return;
        }
        if (action === 'copy') {
          if (!menuPath) return;
          var text = menuPath;
          if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(text).then(function () {
              if (vm.showSideToast) vm.showSideToast('Copied: ' + text);
            });
          } else {
            var ta = document.createElement('textarea');
            ta.value = text; document.body.appendChild(ta); ta.select();
            try { document.execCommand('copy'); } catch (e) {}
            document.body.removeChild(ta);
          }
          return;
        }
        if (action === 'delete') {
          if (!menuPath) return;
          if (!confirm('Delete ' + (isDir ? 'folder' : 'file') + ' "' + menuPath + '"' + (isDir ? ' and all its contents?' : '?'))) return;
          $http.post('/api/editor/delete', { project: vm.selectedProject || '', path: menuPath }).then(function() {
            // Close any open tabs under the deleted path
            var prefix = isDir ? menuPath + '/' : menuPath;
            var removed = false;
            for (var i = vm.ide.openTabs.length - 1; i >= 0; i--) {
              var t = vm.ide.openTabs[i];
              if (t.path === menuPath || (isDir && t.path.indexOf(prefix) === 0)) {
                vm.ide.openTabs.splice(i, 1); removed = true;
              }
            }
            if (removed && vm.ide.openTabs.length === 0) {
              vm.ide.currentFile = null; vm.ide.currentTab = null; vm.ide.dirty = false;
            } else if (removed && vm.ide.currentFile && vm.ide.currentFile.indexOf(prefix) === 0) {
              vm.switchTab(vm.ide.openTabs[0].path);
            }
            vm.refreshFileTree();
          }, function(err) {
            alert((err.data && err.data.error) || 'Delete failed');
          });
          return;
        }
        if (action === 'rename') {
          if (!menuPath) return;
          vm.ide.renamingPath = menuPath;
          vm.ide.renameDraft = (entry && entry.name) || menuPath.split('/').pop() || '';
          vm.ide.treeMenu.visible = false;
          return;
        }
        if (action === 'newfile' || action === 'newfolder') {
          var base = isDir ? menuPath : '';
          var isFile = action === 'newfile';
          var name = prompt((isFile ? 'New file name' : 'New folder name') + (base ? ' in ' + base : ' (project root)') + ':');
          if (!name) return;
          name = name.trim();
          if (!name) return;
          var fullPath = base ? base + '/' + name : name;
          if (isFile) {
            $http.post('/api/editor/write', { project: vm.selectedProject || '', path: fullPath, content: '', createIfMissing: true }).then(function() {
              vm.refreshFileTree();
              vm.openFile(fullPath);
            }, function(err) {
              alert((err.data && err.data.error) || 'Create file failed');
            });
          } else {
            $http.post('/api/editor/mkdir', { project: vm.selectedProject || '', path: fullPath }).then(function() {
              vm.refreshFileTree();
            }, function(err) {
              alert((err.data && err.data.error) || 'Create folder failed');
            });
          }
          return;
        }
      };

      // ── Inline rename ───────────────────────────────────────────────────────
      vm.onTreeRenameKey = function($event, e) {
        if ($event.key === 'Enter') {
          $event.preventDefault();
          vm.commitTreeRename(e);
        } else if ($event.key === 'Escape') {
          $event.preventDefault();
          vm.ide.renamingPath = null;
          vm.ide.renameDraft = '';
        }
      };

      vm.commitTreeRename = function(e) {
        var oldPath = vm.ide.renamingPath;
        if (!oldPath) return;
        var draft = (vm.ide.renameDraft || '').trim();
        vm.ide.renamingPath = null;
        vm.ide.renameDraft = '';
        if (!draft || draft === (e && e.name)) return;
        var parent = oldPath.indexOf('/') >= 0 ? oldPath.slice(0, oldPath.lastIndexOf('/') + 1) : '';
        var newPath = parent + draft;
        var isDir = e ? e.isDirectory : false;
        $http.post('/api/editor/rename', { project: vm.selectedProject || '', path: oldPath, newName: draft }).then(function(resp) {
          var finalPath = (resp.data && resp.data.path) || newPath;
          // Update open tabs that referenced the old path
          for (var i = 0; i < vm.ide.openTabs.length; i++) {
            var t = vm.ide.openTabs[i];
            if (t.path === oldPath) {
              t.path = finalPath;
              t.displayName = draft;
            } else if (isDir && t.path.indexOf(oldPath + '/') === 0) {
              t.path = finalPath + t.path.slice(oldPath.length);
            }
          }
          if (vm.ide.currentFile === oldPath) vm.ide.currentFile = finalPath;
          if (vm.ide.currentFile && isDir && vm.ide.currentFile.indexOf(oldPath + '/') === 0) {
            vm.ide.currentFile = finalPath + vm.ide.currentFile.slice(oldPath.length);
          }
          vm.refreshFileTree();
        }, function(err) {
          alert((err.data && err.data.error) || 'Rename failed');
          vm.refreshFileTree();
        });
      };

      // ── Drag & drop move ────────────────────────────────────────────────────
      vm.onTreeDragStart = function($event, e) {
        if (vm.ide.renamingPath === e.path) { $event.preventDefault(); return; }
        vm._treeDragPath = e.path;
        vm._treeDropPath = null;
        if ($event.dataTransfer) {
          $event.dataTransfer.effectAllowed = 'move';
          $event.dataTransfer.setData('text/plain', e.path);
        }
      };

      vm.onTreeDragOver = function($event, e) {
        if (!vm._treeDragPath) return;
        if (!e.isDirectory) {
          if (vm._treeDropPath) { vm._treeDropPath = null; }
          return;
        }
        // Reject dropping a folder into itself or a descendant
        if (vm._treeDragPath === e.path ||
            (vm._treeDragPath && e.path.indexOf(vm._treeDragPath + '/') === 0)) {
          if (vm._treeDropPath) { vm._treeDropPath = null; }
          return;
        }
        $event.preventDefault();
        if ($event.dataTransfer) $event.dataTransfer.dropEffect = 'move';
        vm._treeDropPath = e.path;
      };

      vm.onTreeDrop = function($event, e) {
        var src = vm._treeDragPath;
        vm._treeDragPath = null;
        vm._treeDropPath = null;
        if (!src) return;
        if (!e || !e.isDirectory) return;
        if (src === e.path || e.path.indexOf(src + '/') === 0) return;
        $event.preventDefault();
        $event.stopPropagation();
        var dest = e.path || '';
        $http.post('/api/editor/move', { project: vm.selectedProject || '', path: src, targetPath: dest }).then(function(resp) {
          var finalPath = (resp.data && resp.data.path) || (dest ? dest + '/' + src.split('/').pop() : src);
          var isDir = !!(resp.data && resp.data.isDirectory);
          // Update open tabs for moved paths
          for (var i = 0; i < vm.ide.openTabs.length; i++) {
            var t = vm.ide.openTabs[i];
            if (t.path === src) {
              t.path = finalPath;
            } else if (t.path.indexOf(src + '/') === 0) {
              t.path = finalPath + t.path.slice(src.length);
            }
          }
          if (vm.ide.currentFile === src) vm.ide.currentFile = finalPath;
          else if (vm.ide.currentFile && vm.ide.currentFile.indexOf(src + '/') === 0) {
            vm.ide.currentFile = finalPath + vm.ide.currentFile.slice(src.length);
          }
          vm.refreshFileTree();
        }, function(err) {
          alert((err.data && err.data.error) || 'Move failed');
          vm.refreshFileTree();
        });
      };

      vm.onTreeDragEnd = function($event) {
        vm._treeDragPath = null;
        vm._treeDropPath = null;
      };

      // Close the context menu when clicking anywhere else
      document.addEventListener('mousedown', function(ev) {
        if (vm.ide.treeMenu && vm.ide.treeMenu.visible) {
          var menuEl = ev.target && ev.target.closest ? ev.target.closest('.ide-context-menu') : null;
          if (!menuEl) {
            vm.ide.treeMenu.visible = false;
            $scope.$digest();
          }
        }
      });
      document.addEventListener('keydown', function(ev) {
        if (ev.key === 'Escape' && vm.ide.treeMenu && vm.ide.treeMenu.visible) {
          vm.ide.treeMenu.visible = false;
          $scope.$digest();
        }
      });

      // Navigate file explorer to show a file's parent directory if sidebar is open
      function _navigateExplorerToFile(path) {
        if (!vm.ide.showSidebar || !vm.ide._treeRoot) return;
        // Expand all ancestor directories so the file is visible in the tree
        var parts = path.split('/');
        var ancestor = '';
        for (var i = 0; i < parts.length - 1; i++) {
          ancestor = ancestor ? ancestor + '/' + parts[i] : parts[i];
          vm._expandedDirs[ancestor] = true;
        }
        vm._rebuildTreeDisplay();
      }

      vm.openFile = function(path) {
        vm.ide.sharedEditorActive = false;
        var existing = vm.findTab(path);
        if (existing) {
          vm.switchTab(path);
          _navigateExplorerToFile(path);
          return;
        }
        var displayName = path.split('/').pop() || path;
        var tab = {
          path: path,
          displayName: displayName,
          content: '',
          savedContent: '',
          dirty: false,
          lineCount: 1,
          remoteEditing: false,
          remoteContent: null,
          fileVersion: 0,
          conflict: false,
          conflictContent: null,
          lastModified: null,
          externalModified: false
        };
        vm.ide.openTabs.push(tab);
        vm.ide.currentFile = path;
        vm.ide.currentTab = tab;
        vm.loadFileContent(path, tab);
        _navigateExplorerToFile(path);
      };

      vm.findTab = function(path) {
        for (var i = 0; i < vm.ide.openTabs.length; i++) {
          if (vm.ide.openTabs[i].path === path) return vm.ide.openTabs[i];
        }
        return null;
      };

      vm.switchTab = function(path) {
        var tab = vm.findTab(path);
        if (tab) {
          vm.ide.currentFile = path;
          vm.ide.currentTab = tab;
          vm.ide.dirty = tab.dirty;
          vm.ide.sharedEditorActive = tab.remoteEditing;
          if (vm.bughostedStatus === 'connected') {
            vm.syncEditorState();
          }
        }
      };

      vm.closeTab = function(path, $event) {
        if ($event) $event.stopPropagation();
        var idx = -1;
        for (var i = 0; i < vm.ide.openTabs.length; i++) {
          if (vm.ide.openTabs[i].path === path) { idx = i; break; }
        }
        if (idx === -1) return;
        if (vm.ide.openTabs[idx].dirty) {
          if (!confirm('Unsaved changes to ' + vm.ide.openTabs[idx].displayName + '. Discard?')) return;
        }
        vm.ide.openTabs.splice(idx, 1);
        if (vm.ide.currentFile === path) {
          if (vm.ide.openTabs.length > 0) {
            var newIdx = Math.min(idx, vm.ide.openTabs.length - 1);
            vm.switchTab(vm.ide.openTabs[newIdx].path);
          } else {
            vm.ide.currentFile = null;
            vm.ide.currentTab = null;
            vm.ide.dirty = false;
            vm.ide.sharedEditorActive = false;
            _searchMarks = [];
            vm.ide.searchVisible = false;
            vm.ide.searchQuery = '';
            vm.ide.searchMatches = [];
            vm.ide.searchCurrentIdx = -1;
            // Destroy CodeMirror when last tab closes
            if (vm._editor) {
              var wrapper = vm._editor.getWrapperElement();
              if (wrapper && wrapper.parentNode) wrapper.parentNode.removeChild(wrapper);
              vm._editor = null;
              _destroyMinimap();
            }
          }
        }
      };

      // ── CodeMirror syntax highlighting ───────────────────────────────
      var MODE_BY_EXT = {
        '.cs': 'text/x-csharp', '.java': 'text/x-java', '.c': 'text/x-csrc',
        '.cpp': 'text/x-c++src', '.h': 'text/x-csrc', '.hpp': 'text/x-c++src',
        '.js': 'text/javascript', '.ts': 'text/typescript', '.jsx': 'text/jsx', '.tsx': 'text/typescript',
        '.html': 'text/html', '.htm': 'text/html', '.xml': 'application/xml', '.svg': 'application/xml',
        '.css': 'text/css', '.scss': 'text/x-scss', '.less': 'text/x-less',
        '.json': 'application/json', '.sql': 'text/x-sql',
        '.py': 'text/x-python', '.rb': 'text/x-ruby', '.php': 'text/x-php',
        '.go': 'text/x-go', '.rs': 'text/x-rust', '.swift': 'text/x-swift',
        '.md': 'text/x-markdown', '.yaml': 'text/x-yaml', '.yml': 'text/x-yaml',
        '.sh': 'text/x-sh', '.bash': 'text/x-sh', '.ps1': 'text/x-sh',
        '.kt': 'text/x-kotlin', '.kts': 'text/x-kotlin',
        '.diff': 'text/x-diff', '.patch': 'text/x-diff'
      };
      function detectMode(path) {
        if (!path) return null;
        var dot = path.lastIndexOf('.');
        if (dot < 0) return null;
        var ext = path.slice(dot).toLowerCase();
        return MODE_BY_EXT[ext] || null;
      }

      vm._editor = null;
      vm._editorIgnoreChange = false;

      function initEditor() {
        var container = document.querySelector('.ide-codemirror-container');
        if (!container) return;
        if (vm._editor) {
          var wrapper = vm._editor.getWrapperElement();
          if (wrapper && wrapper.parentNode) wrapper.parentNode.removeChild(wrapper);
          vm._editor = null;
          _destroyMinimap();
        }
        var savedTheme = vm.ideTheme || 'weaver-dark';
        if (savedTheme !== 'weaver-dark') {
          var linkId = 'cm-ide-theme';
          if (!document.getElementById(linkId)) {
            var link = document.createElement('link');
            link.id = linkId;
            link.rel = 'stylesheet';
            link.href = 'https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.17/theme/' + savedTheme + '.min.css';
            document.head.appendChild(link);
          }
        }
        vm._editor = CodeMirror(container, {
          value: vm.ide.currentTab ? vm.ide.currentTab.content : '',
          mode: detectMode(vm.ide.currentFile),
          theme: savedTheme,
          lineNumbers: true,
          indentUnit: 2,
          tabSize: 2,
          indentWithTabs: false,
          lineWrapping: false,
          matchBrackets: true,
          autoCloseBrackets: true,
          highlightSelectionMatches: {showToken: false, annotateScrollbar: false},
          extraKeys: {
            'Ctrl-S': function () { vm.saveFile(); },
            'Ctrl-F': function () { if (vm && vm.openSearch) vm.openSearch(); },
            'Cmd-F': function () { if (vm && vm.openSearch) vm.openSearch(); }
          }
        });
        vm._editor.on('change', function () {
          if (vm._editorIgnoreChange) return;
          if (!vm.ide.currentTab) return;
          var val = vm._editor.getValue();
          vm.ide.currentTab.content = val;
          var isDirty = val !== vm.ide.currentTab.savedContent;
          vm.ide.currentTab.dirty = isDirty;
          vm.ide.currentTab.lineCount = (val.match(/\n/g) || []).length + 1;
          vm.ide.dirty = isDirty;
          if (_contentSyncDebounce) { $timeout.cancel(_contentSyncDebounce); }
          _contentSyncDebounce = $timeout(function () {
            if (vm.bughostedStatus === 'connected' && vm.bughostedClientId) {
              vm.syncEditorState();
            }
          }, 500, false);
          if (vm.ide.searchVisible && vm.ide.searchQuery) {
            if (_searchDebounce) $timeout.cancel(_searchDebounce);
            _searchDebounce = $timeout(function () {
              if (vm.ide.searchVisible && vm.ide.searchQuery) {
                vm.doSearch();
              }
            }, 300, false);
          }
        });
        vm._editor.setSize('100%', '100%');
        vm._editor.refresh();
        _ensureMinimap();
        vm._editor.on('change', _scheduleMinimapDraw);
        vm._editor.on('cursorActivity', _scheduleMinimapOverlay);
        vm._editor.on('scroll', _scheduleMinimapOverlay);
        _scheduleMinimapDraw();
      }

      function setEditorContent(content, path) {
        if (!vm._editor) return;
        _searchMarks.forEach(function (m) { m.clear(); });
        _searchMarks = [];
        vm.ide.searchVisible = false;
        vm.ide.searchQuery = '';
        vm.ide.searchMatches = [];
        vm.ide.searchCurrentIdx = -1;
        vm._editorIgnoreChange = true;
        vm._editor.setValue(content || '');
        var mode = detectMode(path);
        if (mode) vm._editor.setOption('mode', mode);
        vm._editor.clearHistory();
        vm._editorIgnoreChange = false;
        _scheduleMinimapDraw();
      }

      vm.highlightSyntax = function (tab) {
        // CodeMirror handles highlighting natively — this is kept for compatibility
      };

      vm.loadFileContent = function(path, tab) {
        $http.get('/api/editor/content', { params: { project: vm.selectedProject, path: path } }).then(function(resp) {
          var content = resp.data && resp.data.content !== undefined ? resp.data.content : (resp.data || '');
          tab.content = content;
          tab.savedContent = content;
          tab.dirty = false;
          tab.lineCount = (content.match(/\n/g) || []).length + 1;
          tab.fileVersion = 0;
          tab.conflict = false;
          tab.conflictContent = null;
          tab.lastModified = resp.data.lastModified || null;
          tab.externalModified = false;
          vm.ide.dirty = false;
          vm.ide.lastSavedContent = content;
          vm.broadcastFileOpen(path, content);
          _refreshMinimapDiff(path);
          // Initialize or update CodeMirror
          $timeout(function () {
            if (!vm._editor) {
              initEditor();
            } else {
              setEditorContent(content, path);
            }
          }, 50);
          if (vm.bughostedStatus === 'connected') {
            $timeout(function() { vm.syncEditorState(); }, 50);
          }
          startFileChangePolling();
        }, function(err) {
          tab.content = '// Error loading file: ' + (err.statusText || 'Unknown error');
          tab.savedContent = '';
          tab.dirty = false;
          tab.lineCount = 1;
          vm.ide.dirty = false;
          $timeout(function () {
            if (!vm._editor) {
              initEditor();
            } else {
              setEditorContent(tab.content, path);
            }
          }, 50);
        });
      };

      vm.onContentChange = function() {
        // Content changes are now handled by CodeMirror's change event
      };

      // Re-init editor when tab switches (ng-if may recreate DOM)
      var _origSwitchTab = vm.switchTab;
      vm.switchTab = function (path) {
        _origSwitchTab(path);
        var tab = vm.ide.currentTab;
        if (tab && (tab.type === 'file' || !tab.type)) {
          $timeout(function () {
            if (vm.ide.currentTab) {
              if (!vm._editor) {
                initEditor();
              } else {
                setEditorContent(vm.ide.currentTab.content, path);
              }
              // Deferred into the same digest as the content swap so a cache hit
              // can't draw the new file's markers over the still-old editor.
              _refreshMinimapDiff(path);
            }
          }, 50);
        } else if (tab) {
          // Non-file tab — destroy CodeMirror if it exists to free resources
          if (vm._editor) {
            var wrapper = vm._editor.getWrapperElement();
            if (wrapper && wrapper.parentNode) wrapper.parentNode.removeChild(wrapper);
            vm._editor = null;
            _destroyMinimap();
          }
        }
      };

      vm.saveFile = function() {
        if (!vm.ide.currentFile || !vm.ide.currentTab) return;
        var tab = vm.ide.currentTab;
        var content = tab.content;

        if (tab.conflict) {
          if (!confirm('This file has been edited remotely while you had unsaved changes. Saving will overwrite the remote version. Continue?')) return;
        }

        var payload = {
          project: vm.selectedProject,
          path: vm.ide.currentFile,
          content: content
        };
        $http.post('/api/editor/save', payload).then(function() {
          tab.fileVersion = (tab.fileVersion || 0) + 1;
          tab.savedContent = content;
          tab.dirty = false;
          tab.conflict = false;
          tab.conflictContent = null;
          vm.ide.dirty = false;
          vm.ide.lastSavedContent = content;
          vm.broadcastFileSave(vm.ide.currentFile, content);
          // Diff changes after a save — refresh the minimap markers.
          _refreshMinimapDiff(vm.ide.currentFile);
        }, function(err) {
          console.error('Failed to save file:', err);
        });
      };

      vm.newFile = function() {
        var fileName = prompt('Enter new file name (relative to project root):');
        if (!fileName) return;
        var fullPath = vm.ide.filePickerPath ? vm.ide.filePickerPath + '/' + fileName : fileName;
        var existing = vm.findTab(fullPath);
        if (existing) {
          vm.switchTab(fullPath);
          return;
        }
        var displayName = fileName.split('/').pop() || fileName;
        var tab = {
          path: fullPath,
          displayName: displayName,
          content: '',
          savedContent: '',
          dirty: true,
          lineCount: 1,
          remoteEditing: false,
          remoteContent: null,
          fileVersion: 0,
          conflict: false,
          conflictContent: null,
          lastModified: null,
          externalModified: false
        };
        vm.ide.openTabs.push(tab);
        vm.ide.currentFile = fullPath;
        vm.ide.currentTab = tab;
        vm.ide.dirty = true;
        $timeout(function () {
          if (!vm._editor) {
            initEditor();
          } else {
            setEditorContent('', fullPath);
          }
          _refreshMinimapDiff(fullPath);
        }, 50);
      };

      vm.closeFile = function() {
        if (vm.ide.currentTab && vm.ide.currentTab.dirty) {
          if (!confirm('Unsaved changes to ' + vm.ide.currentTab.displayName + '. Discard?')) return;
        }
        vm.closeTab(vm.ide.currentFile);
      };

      vm.clearSearch = function() {
        vm.ide.searchFilter = '';
        vm.loadFilePickerEntries();
      };

      vm.onIdeSearchChange = function() {
        console.log("onIdeSearchChange", vm.ide.searchFilter);

        if (vm.ide.searchFilter && vm.ide.searchFilter.trim()) {
          vm.loadFilePickerEntries(vm.ide.searchFilter);
        } else {
          vm.loadFilePickerEntries();
        }
      };

      vm.closeIDE = function() {
        var hasDirty = false;
        for (var i = 0; i < vm.ide.openTabs.length; i++) {
          if (vm.ide.openTabs[i].dirty) { hasDirty = true; break; }
        }
        if (hasDirty && !confirm('You have unsaved changes. Close anyway?')) return;
        if (vm._editor) {
          var wrapper = vm._editor.getWrapperElement();
          if (wrapper && wrapper.parentNode) wrapper.parentNode.removeChild(wrapper);
          vm._editor = null;
          _destroyMinimap();
        }
        vm.ide.openTabs = [];
        vm.ide.currentFile = null;
        vm.ide.currentTab = null;
        vm.ide.dirty = false;
        vm.ide.filePickerPath = '';
        vm.ide.filePickerEntries = [];
        vm.ide.searchFilter = '';
        vm.ide.sharedEditorActive = false;
        vm.ide.sharedFiles = [];
        _searchMarks = [];
        vm.ide.searchVisible = false;
        vm.ide.searchQuery = '';
        vm.ide.searchMatches = [];
        vm.ide.searchCurrentIdx = -1;
        stopFileChangePolling();
        vm.showIDE = false;
      };
      vm.stopIdePolling = stopFileChangePolling;

      // ── Search ─────────────────────────────────────────────────────────
      vm.openSearch = function () {
        vm.ide.searchVisible = true;
        vm.ide.searchQuery = '';
        vm.ide.searchMatches = [];
        vm.ide.searchCurrentIdx = -1;
        $timeout(function () {
          var input = document.querySelector('.ide-search-input');
          if (input) { input.focus(); input.select(); }
          if (vm._editor) {
            var sel = vm._editor.getSelection();
            if (sel) {
              vm.ide.searchQuery = sel;
              vm.doSearch();
            }
          }
        });
        // Force a second focus attempt after ng-if renders the DOM
        $timeout(function () {
          var input = document.querySelector('.ide-search-input');
          if (input) input.focus();
        }, 100);
      };

      vm.closeSearch = function () {
        vm.ide.searchVisible = false;
        _searchMarks.forEach(function (m) { m.clear(); });
        _searchMarks = [];
        vm.ide.searchMatches = [];
        vm.ide.searchCurrentIdx = -1;
        vm.ide.searchQuery = '';
        if (vm._editor) vm._editor.focus();
        _scheduleMinimapOverlay();
      };

      vm.doSearch = function () {
        _searchMarks.forEach(function (m) { m.clear(); });
        _searchMarks = [];
        vm.ide.searchMatches = [];
        vm.ide.searchCurrentIdx = -1;
        var query = vm.ide.searchQuery;
        if (!query || !vm._editor) return;
        try {
          var cur = vm._editor.getSearchCursor(query, { line: 0, ch: 0 });
          while (cur.findNext()) {
            vm.ide.searchMatches.push({ from: cur.from(), to: cur.to() });
            var mark = vm._editor.markText(cur.from(), cur.to(), { className: 'cm-search-match' });
            _searchMarks.push(mark);
          }
        } catch (e) {
          return;
        }
        if (vm.ide.searchMatches.length > 0) {
          vm.ide.searchCurrentIdx = 0;
          vm._editor.setSelection(vm.ide.searchMatches[0].from, vm.ide.searchMatches[0].to);
          vm._editor.scrollIntoView({ from: vm.ide.searchMatches[0].from, to: vm.ide.searchMatches[0].to });
        }
        _scheduleMinimapOverlay();
      };

      vm.searchNext = function () {
        if (vm.ide.searchMatches.length === 0) return;
        var idx = vm.ide.searchCurrentIdx + 1;
        if (idx >= vm.ide.searchMatches.length) idx = 0;
        vm.ide.searchCurrentIdx = idx;
        var match = vm.ide.searchMatches[idx];
        vm._editor.setSelection(match.from, match.to);
        vm._editor.scrollIntoView({ from: match.from, to: match.to });
        _scheduleMinimapOverlay();
      };

      vm.searchPrev = function () {
        if (vm.ide.searchMatches.length === 0) return;
        var idx = vm.ide.searchCurrentIdx - 1;
        if (idx < 0) idx = vm.ide.searchMatches.length - 1;
        vm.ide.searchCurrentIdx = idx;
        var match = vm.ide.searchMatches[idx];
        vm._editor.setSelection(match.from, match.to);
        vm._editor.scrollIntoView({ from: match.from, to: match.to });
        _scheduleMinimapOverlay();
      };

      vm.onSearchKeydown = function ($event) {
        if ($event.key === 'Enter') {
          if ($event.shiftKey) {
            vm.searchPrev();
          } else {
            vm.searchNext();
          }
          $event.preventDefault();
        } else if ($event.key === 'Escape') {
          vm.closeSearch();
          $event.preventDefault();
        }
      };

      // ── Minimap (VS Code-style overview) ─────────────────────────────
      var MINIMAP_TOKEN_COLORS = {
        'keyword': '#c792ea', 'atom': '#f78c6c', 'number': '#f78c6c',
        'def': '#82aaff', 'variable': '#e6edf3', 'variable-2': '#e6edf3',
        'variable-3': '#e6edf3', 'property': '#b392f0', 'operator': '#89ddff',
        'comment': '#637777', 'string': '#89ddff', 'string-2': '#89ddff',
        'meta': '#ffcb6b', 'qualifier': '#ffcb6b', 'builtin': '#f78c6c',
        'bracket': '#89ddff', 'tag': '#f07178', 'attribute': '#ffcb6b',
        'link': '#82aaff', 'error': '#ff5370', 'type': '#c792ea'
      };

      var MINIMAP_DIFF_COLORS = {
        'add': 'rgba(74, 222, 128, 0.95)',
        'remove': 'rgba(248, 113, 113, 0.95)',
        'modify': 'rgba(251, 191, 36, 0.95)'
      };

      vm.toggleMinimap = function () {
        vm.ide.minimapVisible = !vm.ide.minimapVisible;
        if (_minimapEl) _minimapEl.classList.toggle('ide-minimap--hidden', !vm.ide.minimapVisible);
        _scheduleMinimapDraw();
        // Persist so the toggle survives a reload (localStorage store + config endpoint).
        if (vm.saveSettings) vm.saveSettings(true);
      };

      function _ensureMinimap() {
        var container = document.querySelector('.ide-codemirror-container');
        if (!container || !vm._editor) return;
        if (_minimapEl && _minimapEl.parentNode === container) return;
        if (_minimapEl && _minimapEl.parentNode) _minimapEl.parentNode.removeChild(_minimapEl);
        _minimapEl = document.createElement('div');
        _minimapEl.className = 'ide-minimap' + (vm.ide.minimapVisible ? '' : ' ide-minimap--hidden');
        _minimapCanvas = document.createElement('canvas');
        _minimapEl.appendChild(_minimapCanvas);
        container.appendChild(_minimapEl);
        _minimapEl.addEventListener('mousedown', function (e) {
          _minimapDragging = true;
          _minimapHideTip();
          _minimapScrollToEvent(e, !!e.altKey);
          e.preventDefault();
        });
        _minimapEl.addEventListener('wheel', function (e) {
          if (!vm._editor) return;
          var si = vm._editor.getScrollInfo();
          vm._editor.scrollTo(null, si.top + (e.deltaY || 0) * 2.5);
          e.preventDefault();
        }, { passive: false });
        _minimapEl.addEventListener('mousemove', function (e) {
          _minimapTipAt(e);
        });
        _minimapEl.addEventListener('mouseleave', function () {
          _minimapHideTip();
        });
        if (!_minimapWindowBound) {
          _minimapWindowBound = true;
          window.addEventListener('mousemove', _minimapWindowMove);
          window.addEventListener('mouseup', function () { _minimapDragging = false; });
        }
        if (_minimapResizeObs) { _minimapResizeObs.disconnect(); _minimapResizeObs = null; }
        if (typeof ResizeObserver !== 'undefined') {
          _minimapResizeObs = new ResizeObserver(function () { _scheduleMinimapDraw(); });
          _minimapResizeObs.observe(container);
        }
      }

      function _destroyMinimap() {
        _minimapDragging = false;
        if (_minimapEl && _minimapEl.parentNode) _minimapEl.parentNode.removeChild(_minimapEl);
        _minimapEl = null;
        _minimapCanvas = null;
        _minimapBgCanvas = null;
        if (_minimapTipEl && _minimapTipEl.parentNode) _minimapTipEl.parentNode.removeChild(_minimapTipEl);
        _minimapTipEl = null;
        if (_minimapResizeObs) { _minimapResizeObs.disconnect(); _minimapResizeObs = null; }
        _minimapDiffMarkers = null;
        _minimapDiffPath = null;
        _minimapDiffCache = {};
      }

      function _minimapWindowMove(e) {
        if (!_minimapDragging) return;
        _minimapScrollToEvent(e, !!e.altKey);
      }

      // Shared: map a mouse position over the minimap to a clamped 0-based
      // line index, so hover tooltips and click/drag navigation always agree.
      function _minimapLineAt(e) {
        if (!_minimapEl || !vm._editor) return -1;
        var rect = _minimapEl.getBoundingClientRect();
        var y = e.clientY - rect.top;
        var H = rect.height || 1;
        var lineCount = vm._editor.lineCount();
        if (!lineCount) return -1;
        return Math.max(0, Math.min(lineCount - 1, Math.floor((y / H) * lineCount)));
      }

      // Alt-click/drag centers the clicked line in the viewport instead of
      // top-aligning it.
      function _minimapScrollToEvent(e, centerLine) {
        if (!_minimapEl || !vm._editor) return;
        var cm = vm._editor;
        var line = _minimapLineAt(e);
        if (line < 0) return;
        var lineCount = cm.lineCount();
        var si = cm.getScrollInfo();
        if (centerLine) {
          var target = cm.heightAtLine(line, 'local') - si.clientHeight / 2;
          target = Math.max(0, Math.min(target, Math.max(0, si.height - si.clientHeight)));
          cm.scrollTo(null, target);
        } else {
          cm.scrollTo(null, (line / lineCount) * si.height);
        }
        cm.focus();
      }

      function _minimapHideTip() {
        if (_minimapTipEl) _minimapTipEl.hidden = true;
      }

      // Hover tooltip: line number + first 40 chars of the line under the mouse.
      function _minimapTipAt(e) {
        if (!_minimapEl || !vm._editor) return;
        var line = _minimapLineAt(e);
        if (line < 0) return;
        if (!_minimapTipEl) {
          _minimapTipEl = document.createElement('div');
          _minimapTipEl.className = 'ide-minimap-tip';
          document.body.appendChild(_minimapTipEl);
        }
        var rect = _minimapEl.getBoundingClientRect();
        var cm = vm._editor;
        var text = (cm.getLine(line) || '').replace(/\s+/g, ' ').trim();
        if (text.length > 40) text = text.slice(0, 40) + '…';
        _minimapTipEl.textContent = (line + 1) + ': ' + (text || '·');
        _minimapTipEl.hidden = false;
        var tipW = _minimapTipEl.offsetWidth || 180;
        var tipH = _minimapTipEl.offsetHeight || 20;
        var left = rect.left - tipW - 8;
        if (left < 8) left = rect.right + 8;
        var top = Math.max(8, Math.min(e.clientY - tipH / 2, window.innerHeight - tipH - 8));
        _minimapTipEl.style.left = left + 'px';
        _minimapTipEl.style.top = top + 'px';
      }

      // ── Git diff markers on the minimap ────────────────────────────────
      // Map LCS diff rows (from vm.computeLineDiff) onto 0-based new-file line
      // numbers: added lines → green, removed lines → red (anchored where the
      // deletion happened), a removal directly replaced by an addition → yellow.
      function _computeMinimapDiffMarkers(rows, newLineCount) {
        var markers = [];
        var cursor = 1; // 1-based index of the next new line yet to be consumed
        var i = 0;
        while (i < rows.length) {
          var row = rows[i];
          if (row.type === 'equal') {
            cursor = (row.newNum || cursor) + 1;
            i++;
            continue;
          }
          if (row.type === 'add') {
            var isModify = i > 0 && rows[i - 1].type === 'remove';
            markers.push({ line: (row.newNum || cursor) - 1, kind: isModify ? 'modify' : 'add' });
            cursor = (row.newNum || cursor) + 1;
            i++;
            continue;
          }
          // Remove run — skip past it to inspect what follows
          var j = i;
          while (j < rows.length && rows[j].type === 'remove') j++;
          var next = j < rows.length ? rows[j] : null;
          if (next && next.type === 'add') {
            // Replacement — the following add rows get the yellow marker; no red.
            i = j;
            continue;
          }
          // Pure deletion — red bar anchored at the line the deletion collapsed into.
          var pos = Math.max(0, Math.min(cursor - 1, newLineCount - 1));
          for (var k = i; k < j; k++) markers.push({ line: pos, kind: 'remove' });
          i = j;
        }
        return markers;
      }

      function _refreshMinimapDiff(path) {
        if (!path || path.indexOf('_git:') === 0) return;
        if (_minimapDiffCache[path]) {
          _minimapDiffMarkers = _minimapDiffCache[path];
          _minimapDiffPath = path;
          _scheduleMinimapOverlay();
          return;
        }
        // Fetching a different file — don't draw the old file's markers meanwhile.
        if (_minimapDiffPath !== path) {
          _minimapDiffMarkers = null;
          _minimapDiffPath = path;
        }
        $http.get('/api/editor/git-diff-file', { params: { project: vm.selectedProject, path: path } }).then(function (resp) {
          var data = resp.data || {};
          if (!data.isGitRepo) {
            _minimapDiffCache[path] = null;
            if (vm.ide.currentFile === path) _minimapDiffMarkers = null;
            return;
          }
          // Count real lines (CodeMirror semantics — no phantom trailing ''), so
          // an EOF deletion clamps to the last visible line instead of a dropped one.
          var newContent = data.newContent || '';
          var realLineCount = newContent.replace(/\n$/, '').split('\n').length;
          var markers = _computeMinimapDiffMarkers(
            vm.computeLineDiff(data.oldContent || '', newContent),
            realLineCount
          );
          _minimapDiffCache[path] = markers;
          if (vm.ide.currentFile === path) {
            _minimapDiffMarkers = markers;
            _minimapDiffPath = path;
            _scheduleMinimapOverlay();
          }
        }, function () {
          _minimapDiffCache[path] = null;
          if (vm.ide.currentFile === path) _minimapDiffMarkers = null;
        });
      }

      function _scheduleMinimapDraw() {
        _minimapBgDirty = true;
        _scheduleMinimapRaf(false);
      }

      function _scheduleMinimapOverlay() {
        _scheduleMinimapRaf(true);
      }

      function _scheduleMinimapRaf(overlayOnly) {
        if (_minimapScheduled) { _minimapOverlayOnly = _minimapOverlayOnly && overlayOnly; return; }
        _minimapScheduled = true;
        _minimapOverlayOnly = overlayOnly;
        requestAnimationFrame(function () {
          _minimapScheduled = false;
          if (vm._editor && _minimapEl && vm.ide.minimapVisible) _drawMinimap(_minimapOverlayOnly);
        });
      }

      function _drawMinimap(overlayOnly) {
        var cm = vm._editor;
        if (!cm || !_minimapEl || !_minimapCanvas) return;
        var W = _minimapEl.clientWidth, H = _minimapEl.clientHeight;
        if (W < 8 || H < 8) return;
        var dpr = window.devicePixelRatio || 1;
        var canvas = _minimapCanvas;
        var ctx = canvas.getContext('2d');
        var lineCount = cm.lineCount();
        var lineH = lineCount ? H / lineCount : 1;
        // Background (token-colored preview) — rendered to an offscreen canvas,
        // only rebuilt on content change; overlays never stack on top of stale pixels.
        if (_minimapBgDirty) {
          if (!_minimapBgCanvas) _minimapBgCanvas = document.createElement('canvas');
          _minimapBgCanvas.width = Math.round(W * dpr);
          _minimapBgCanvas.height = Math.round(H * dpr);
          var bctx = _minimapBgCanvas.getContext('2d');
          bctx.setTransform(dpr, 0, 0, dpr, 0, 0);
          bctx.fillStyle = 'rgba(13, 17, 23, 0.85)';
          bctx.fillRect(0, 0, W, H);
          if (lineCount) {
            var last = cm.lastLine();
            var maxChars = 1;
            var step = Math.max(1, Math.floor((last + 1) / 400));
            for (var l = 0; l <= last; l += step) {
              var len = cm.getLine(l).length;
              if (len > maxChars) maxChars = len;
            }
            var charW = Math.max(1, (W - 4) / Math.max(maxChars, 80));
            var base = 'rgba(230, 237, 243, 0.55)';
            for (var i = 0; i <= last; i++) {
              var y = i * lineH;
              var h = Math.max(1, lineH - 0.4);
              var tokens = cm.getLineTokens(i, 0);
              if (tokens && tokens.length) {
                for (var t = 0; t < tokens.length; t++) {
                  var tok = tokens[t];
                  var color = base;
                  if (tok.type) {
                    var cls = tok.type.split(' ')[0];
                    color = MINIMAP_TOKEN_COLORS[cls] || base;
                  }
                  var x = 2 + tok.start * charW;
                  var tw = Math.max(1, (tok.end - tok.start) * charW);
                  bctx.fillStyle = color;
                  bctx.fillRect(x, y, tw, h);
                }
              } else {
                bctx.fillStyle = 'rgba(230, 237, 243, 0.35)';
                bctx.fillRect(2, y, 1, h);
              }
            }
          }
          _minimapBgDirty = false;
        }
        // Clear the visible canvas every frame, blit the cached background, then overlays
        canvas.width = Math.round(W * dpr);
        canvas.height = Math.round(H * dpr);
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        if (_minimapBgCanvas) ctx.drawImage(_minimapBgCanvas, 0, 0, W, H);
        if (!lineCount) return;
        // Selection
        var sels = cm.listSelections();
        for (var s = 0; s < sels.length; s++) {
          var a = Math.min(sels[s].anchor.line, sels[s].head.line);
          var b = Math.max(sels[s].anchor.line, sels[s].head.line);
          ctx.fillStyle = 'rgba(56, 139, 253, 0.45)';
          ctx.fillRect(0, a * lineH, W, Math.max(1, (b - a + 1) * lineH));
        }
        // Ctrl+F search hits
        if (vm.ide.searchMatches && vm.ide.searchMatches.length) {
          for (var m = 0; m < vm.ide.searchMatches.length; m++) {
            var ml = vm.ide.searchMatches[m].from.line;
            var isCurrent = m === vm.ide.searchCurrentIdx;
            ctx.fillStyle = isCurrent ? 'rgba(88, 166, 255, 0.95)' : 'rgba(210, 153, 34, 0.8)';
            ctx.fillRect(0, ml * lineH, W, Math.max(1, lineH));
          }
        }
        // Viewport indicator
        var si = cm.getScrollInfo();
        var vh = Math.max(1, (si.clientHeight / si.height) * H);
        var vt = (si.top / si.height) * H;
        ctx.fillStyle = 'rgba(255, 255, 255, 0.10)';
        ctx.fillRect(0, vt, W, vh);
        ctx.strokeStyle = 'rgba(255, 255, 255, 0.35)';
        ctx.strokeRect(0.5, vt + 0.5, W - 1, vh - 1);
        // Cursor line markers (glow) — one per active cursor/selection head
        var sels2 = cm.listSelections();
        for (var s2 = 0; s2 < sels2.length; s2++) {
          var cl = sels2[s2].head.line;
          if (cl == null || cl < 0 || cl >= lineCount) continue;
          var cy = cl * lineH;
          ctx.save();
          ctx.shadowColor = 'rgba(56, 139, 253, 0.9)';
          ctx.shadowBlur = 6;
          ctx.fillStyle = 'rgba(56, 139, 253, 0.5)';
          ctx.fillRect(0, cy, W, Math.max(1, lineH));
          ctx.restore();
          ctx.fillStyle = 'rgba(56, 139, 253, 0.95)';
          ctx.fillRect(0, cy, 2.5, Math.max(1, lineH));
        }
        // Git diff markers — drawn last on the right edge so the translucent
        // selection/search overlays can't wash them out, and they don't fight
        // the left-edge cursor bar.
        if (_minimapDiffMarkers && _minimapDiffPath === vm.ide.currentFile) {
          for (var g = 0; g < _minimapDiffMarkers.length; g++) {
            var gd = _minimapDiffMarkers[g];
            if (gd.line < 0 || gd.line >= lineCount) continue;
            var gy = gd.line * lineH;
            ctx.fillStyle = MINIMAP_DIFF_COLORS[gd.kind] || 'rgba(248, 113, 113, 0.95)';
            ctx.fillRect(W - 3, gy, 3, Math.max(1, lineH));
          }
        }
      }

      // ── Git Diff as IDE tabs ──────────────────────────────────────────
      function _openGitTab(type, displayName, pathKey) {
        // Reuse existing git tab with same pathKey, or create one
        var existing = null;
        for (var i = 0; i < vm.ide.openTabs.length; i++) {
          if (vm.ide.openTabs[i].gitTabKey === pathKey) { existing = vm.ide.openTabs[i]; break; }
        }
        if (existing) {
          vm.switchTab(existing.path);
          return existing;
        }
        var tab = {
          type: type,
          path: pathKey || '_git:' + type,
          gitTabKey: pathKey || '_git:' + type,
          displayName: displayName,
          content: '',
          savedContent: '',
          dirty: false,
          lineCount: 1,
          remoteEditing: false,
          gitData: null,
          gitLoading: false,
          gitError: '',
          gitFilePath: '',
          gitRows: [],
          gitCommitMessage: '',
          gitCommitResult: '',
          gitCommitError: '',
          gitCommitBusy: false,
          gitCommitStatus: '',
          gitPrUrl: '',
          lastModified: null,
          externalModified: false
        };
        vm.ide.openTabs.push(tab);
        vm.ide.currentFile = tab.path;
        vm.ide.currentTab = tab;
        vm.ide.dirty = false;
        return tab;
      }

      vm.showGitDiff = function () {
        var tab = _openGitTab('git-list', 'Source Control', '_git:list');
        tab.gitLoading = true;
        tab.gitData = null;
        tab.gitError = '';
        tab.gitCommitMessage = '';
        tab.gitCommitResult = '';
        tab.gitCommitError = '';
        tab.gitPrUrl = '';
        $http.get('/api/editor/git-diff', { params: { project: vm.selectedProject } }).then(function (resp) {
          tab.gitLoading = false;
          tab.gitData = resp.data;
        }, function (err) {
          tab.gitLoading = false;
          tab.gitError = (err.data && err.data.error) || err.statusText || 'Failed to load git diff';
        });
      };

      vm.showFileDiff = function (path) {
        var displayName = 'Diff: ' + (path.split('/').pop() || path);
        var tab = _openGitTab('git-diff', displayName, '_git:diff:' + path);
        tab.gitFilePath = path;
        tab.gitLoading = true;
        tab.gitRows = [];
        tab.gitError = '';
        $http.get('/api/editor/git-diff-file', { params: { project: vm.selectedProject, path: path } }).then(function (resp) {
          tab.gitLoading = false;
          var data = resp.data;
          tab.gitRows = vm.computeLineDiff(data.oldContent || '', data.newContent || '');
        }, function (err) {
          tab.gitLoading = false;
          tab.gitError = (err.data && err.data.error) || err.statusText || 'Failed to load file diff';
        });
      };

      vm.backToSourceControl = function () {
        // Close current diff-content tab if present, then open/reuse git-list tab
        var curTab = vm.ide.currentTab;
        if (curTab && curTab.type === 'git-diff') {
          vm.closeTab(curTab.path);
        }
        vm.showGitDiff();
      };

      // ── Line diff algorithm (LCS-based) ───────────────────────────────
      vm.computeLineDiff = function (oldText, newText) {
        var oldLines = oldText.replace(/\r\n/g, '\n').split('\n');
        var newLines = newText.replace(/\r\n/g, '\n').split('\n');

        // Build LCS table
        var m = oldLines.length, n = newLines.length;
        var dp = [];
        for (var i = 0; i <= m; i++) {
          dp[i] = new Array(n + 1).fill(0);
        }
        for (var i = 1; i <= m; i++) {
          for (var j = 1; j <= n; j++) {
            if (oldLines[i - 1] === newLines[j - 1]) {
              dp[i][j] = dp[i - 1][j - 1] + 1;
            } else {
              dp[i][j] = Math.max(dp[i - 1][j], dp[i][j - 1]);
            }
          }
        }

        // Backtrack to build diff rows
        var rows = [];
        var i = m, j = n;
        var tempRows = [];
        while (i > 0 || j > 0) {
          if (i > 0 && j > 0 && oldLines[i - 1] === newLines[j - 1]) {
            tempRows.push({ type: 'equal', oldNum: i, oldContent: oldLines[i - 1], newNum: j, newContent: newLines[j - 1] });
            i--; j--;
          } else if (j > 0 && (i === 0 || dp[i][j - 1] >= dp[i - 1][j])) {
            tempRows.push({ type: 'add', oldNum: null, oldContent: '', newNum: j, newContent: newLines[j - 1] });
            j--;
          } else if (i > 0) {
            tempRows.push({ type: 'remove', oldNum: i, oldContent: oldLines[i - 1], newNum: null, newContent: '' });
            i--;
          }
        }
        // Reverse to get chronological order
        for (var k = tempRows.length - 1; k >= 0; k--) {
          rows.push(tempRows[k]);
        }
        return rows;
      };

      vm.backToSourceControl = function () {
        vm.ide.gitDiffView = 'list';
        vm.ide.gitDiffFilePath = '';
        vm.ide.gitDiffRows = [];
        vm.showGitDiff();
      };

      function _findGitListTab() {
        for (var i = 0; i < vm.ide.openTabs.length; i++) {
          if (vm.ide.openTabs[i].type === 'git-list') return vm.ide.openTabs[i];
        }
        return null;
      }

      function _doGitCommit(pushAfter, createPr) {
        var tab = _findGitListTab() || vm.ide.currentTab;
        tab.gitCommitResult = '';
        tab.gitCommitError = '';
        tab.gitPrUrl = '';
        tab.gitCommitBusy = true;
        tab.gitCommitStatus = 'Committing';
        var payload = { project: vm.selectedProject, message: tab.gitCommitMessage };
        $http.post('/api/editor/git-commit', payload).then(function (resp) {
          if (resp.data.nothingToCommit) {
            tab.gitCommitBusy = false;
            tab.gitCommitError = 'Nothing to commit — no changes staged or unstaged';
            return;
          }
          if (!resp.data.success) {
            tab.gitCommitBusy = false;
            tab.gitCommitError = resp.data.commitOutput || resp.data.error || 'Commit failed';
            return;
          }
          if (pushAfter || createPr) {
            tab.gitCommitStatus = 'Pushing';
            $http.post('/api/editor/git-push', payload).then(function (pushResp) {
              if (createPr) {
                tab.gitCommitStatus = 'Creating PR';
                $http.post('/api/editor/git-pr', payload).then(function (prResp) {
                  tab.gitCommitBusy = false;
                  if (prResp.data.success) {
                    tab.gitCommitResult = 'PR created successfully';
                    tab.gitPrUrl = prResp.data.prUrl || '';
                    tab.gitCommitMessage = '';
                    vm.showGitDiff();
                  } else {
                    tab.gitCommitError = prResp.data.prUrl || prResp.data.error || 'PR creation failed';
                  }
                }, function (err) {
                  tab.gitCommitBusy = false;
                  tab.gitCommitError = 'PR creation failed: ' + (err.statusText || '');
                });
              } else {
                tab.gitCommitBusy = false;
                tab.gitCommitResult = 'Committed and pushed successfully';
                tab.gitCommitMessage = '';
                vm.showGitDiff();
              }
            }, function (err) {
              tab.gitCommitBusy = false;
              tab.gitCommitError = 'Push failed: ' + (err.statusText || '');
            });
          } else {
            tab.gitCommitBusy = false;
            tab.gitCommitResult = 'Committed successfully';
            tab.gitCommitMessage = '';
            vm.showGitDiff();
          }
        }, function (err) {
          tab.gitCommitBusy = false;
          tab.gitCommitError = (err.data && err.data.error) || err.statusText || 'Commit failed';
        });
      }

      vm.gitCommit = function () { _doGitCommit(false, false); };
      vm.gitCommitAndPush = function () { _doGitCommit(true, false); };
      vm.gitCreatePr = function () { _doGitCommit(true, true); };

      // ===== Shared editing via BugHosted =====
      vm.broadcastFileOpen = function(path, content) {
        if (vm.bughostedStatus !== 'connected' || !vm.bughostedClientId) return;
        vm.ide.lastSharedFile = path;
        vm.ide.syncing = true;
      };

      vm.broadcastFileSave = function(path, content) {
        if (vm.bughostedStatus !== 'connected' || !vm.bughostedClientId) return;
        vm.ide.syncing = true;
        $http.post('/api/bughosted/fileEdit', {
          clientId: vm.bughostedClientId,
          path: path,
          content: content
        }).then(function() {
          vm.ide.syncing = false;
        }, function() {
          vm.ide.syncing = false;
        });
      };

      vm.handleRemoteFileEdit = function(params) {
        if (!params || !params.path || params.content === undefined) return;
        var tab = vm.findTab(params.path);
        if (!tab) return;
        if (tab.dirty && tab.content !== params.content) {
          tab.conflict = true;
          tab.conflictContent = params.content;
          vm.ide.dirty = true;
          if (vm.ide.currentFile === tab.path) {
            vm.ide.sharedEditorActive = true;
          }
          return;
        }
        tab.content = params.content;
        tab.savedContent = params.content;
        tab.dirty = false;
        tab.fileVersion = (tab.fileVersion || 0) + 1;
        tab.conflict = false;
        tab.conflictContent = null;
        tab.remoteEditing = true;
        tab.lineCount = (params.content.match(/\n/g) || []).length + 1;
        if (vm.ide.currentFile === params.path) {
          vm.ide.dirty = false;
          vm.ide.sharedEditorActive = true;
        }
        vm.ide.syncing = false;
      };

      vm.applyRemoteContent = function(path, content) {
        var tab = vm.findTab(path);
        if (!tab) return;
        if (tab.dirty) {
          tab.conflict = true;
          tab.conflictContent = content;
          return;
        }
        tab.content = content;
        tab.savedContent = content;
        tab.dirty = false;
        tab.fileVersion = (tab.fileVersion || 0) + 1;
        tab.lineCount = (content.match(/\n/g) || []).length + 1;
        if (vm.ide.currentFile === path) {
          vm.ide.dirty = false;
        }
      };

      vm.reloadExternalFile = function(path) {
        var tab = vm.findTab(path);
        if (!tab) return;
        $http.get('/api/editor/content', { params: { project: vm.selectedProject || '', path: path } }).then(function(resp) {
          var content = resp.data && resp.data.content !== undefined ? resp.data.content : (resp.data || '');
          var wasCurrent = vm.ide.currentFile === path;
          tab.content = content;
          tab.savedContent = content;
          tab.dirty = false;
          tab.lastModified = resp.data.lastModified || null;
          tab.externalModified = false;
          tab.lineCount = (content.match(/\n/g) || []).length + 1;
          if (wasCurrent) {
            vm.ide.dirty = false;
            if (vm._editor) {
              vm._editorIgnoreChange = true;
              var cursor = vm._editor.getCursor();
              vm._editor.setValue(content);
              vm._editor.setCursor(cursor);
              vm._editorIgnoreChange = false;
            }
          }
        });
      };

      vm.resolveConflict = function(path) {
        var tab = vm.findTab(path);
        if (!tab || !tab.conflict) return;
        var choice = confirm('Use local version? Click Cancel to use remote version.');
        if (choice) {
          tab.conflict = false;
          tab.conflictContent = null;
        } else {
          tab.content = tab.conflictContent;
          tab.savedContent = tab.conflictContent;
          tab.dirty = false;
          tab.conflict = false;
          tab.conflictContent = null;
          tab.fileVersion = (tab.fileVersion || 0) + 1;
          if (vm.ide.currentFile === path) {
            vm.ide.dirty = false;
          }
        }
      };

      vm.resolveAllConflicts = function() {
        for (var i = 0; i < vm.ide.openTabs.length; i++) {
          if (vm.ide.openTabs[i].conflict) {
            vm.resolveConflict(vm.ide.openTabs[i].path);
          }
        }
      };

      vm.startDrag = function($event) {
        // Only drag on the header itself, not buttons/inputs inside it
        if ($event.target.tagName === 'BUTTON' || $event.target.tagName === 'INPUT' ||
            $event.target.tagName === 'TEXTAREA' || $event.target.closest('button')) return;
        $event.preventDefault();
        var startX = $event.clientX;
        var startY = $event.clientY;
        var startLeft = vm.ide.left;
        var startTop = vm.ide.top;
        var viewW = window.innerWidth;
        var viewH = window.innerHeight;
        function onMove(e) {
          vm.ide.left = Math.max(0, Math.min(viewW - 100, startLeft + (e.clientX - startX)));
          vm.ide.top = Math.max(0, Math.min(viewH - 60, startTop + (e.clientY - startY)));
          $scope.$digest();
        }
        function onUp() {
          document.removeEventListener('mousemove', onMove);
          document.removeEventListener('mouseup', onUp);
        }
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
      };

      vm.startResize = function(dir, $event) {
        $event.preventDefault();
        var startX = $event.clientX;
        var startY = $event.clientY;
        var startW = vm.ide.width;
        var startH = vm.ide.height;
        var startLeft = vm.ide.left;
        var startTop = vm.ide.top;
        var minW = 300, minH = 200;
        var viewW = window.innerWidth;
        var viewH = window.innerHeight;
        function onMove(e) {
          var dx = e.clientX - startX;
          var dy = e.clientY - startY;
          if (dir === 'e' || dir === 'se') {
            vm.ide.width = Math.max(minW, Math.min(viewW - vm.ide.left, startW + dx));
          }
          if (dir === 's' || dir === 'se') {
            vm.ide.height = Math.max(minH, Math.min(viewH - vm.ide.top, startH + dy));
          }
          $scope.$digest();
        }
        function onUp() {
          document.removeEventListener('mousemove', onMove);
          document.removeEventListener('mouseup', onUp);
        }
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
      };
    }
  };
});

// Select the full text of an input when it gains focus (used by the inline
// file-tree rename box so the basename is ready to be typed over).
angular.module('kanbanApp').directive('selectOnFocus', function () {
  return {
    restrict: 'A',
    link: function (scope, el) {
      el.on('focus', function () { el[0].select(); });
    }
  };
});
