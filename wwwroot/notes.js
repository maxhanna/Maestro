angular.module('kanbanApp').factory('NotesMixin', function ($http, $timeout) {
  return {
    init: function (vm, $scope) {
      // Notes panel state
      vm.notes = {
        left: 100,
        top: 100,
        width: 500,
        height: 400,
        dragging: false,
        dragStartX: 0,
        dragStartY: 0,
        resizing: false,
        resizeDir: '',
        resizeStartX: 0,
        resizeStartY: 0,
        resizeStartW: 0,
        resizeStartH: 0
      };
      // Restore a persisted panel position/size (stashed by SettingsMixin) so the
      // notes open exactly where the user left them.
      if (vm._savedNotesPanel) {
        var _np = vm._savedNotesPanel;
        if (typeof _np.left === 'number') vm.notes.left = _np.left;
        if (typeof _np.top === 'number') vm.notes.top = _np.top;
        if (typeof _np.width === 'number') vm.notes.width = _np.width;
        if (typeof _np.height === 'number') vm.notes.height = _np.height;
      }

      vm.notesProject = '';
      vm.notesContent = '';
      vm.notesDirty = false;
      vm.notesLoading = false;
      vm.notesLastSaved = null;
      vm.notesError = '';

      // Load notes when project changes or panel opens
      vm.loadNotes = function () {
        if (!vm.notesProject) {
          vm.notesProject = vm.selectedProject || '';
        }
        if (!vm.notesProject) return;

        vm.notesLoading = true;
        vm.notesError = '';
        $http.get('/api/notes', { params: { project: vm.notesProject } })
          .then(function (resp) {
            vm.notesContent = (resp.data && resp.data.content) || '';
            vm.notesDirty = false;
            vm.notesLastSaved = new Date();
            vm.notesLoading = false;
          })
          .catch(function (err) {
            vm.notesError = 'Failed to load notes: ' + (err.data || err.statusText || 'Unknown error');
            vm.notesLoading = false;
          });
      };

      // Save notes to the backend
      vm.saveNotes = function () {
        if (!vm.notesProject) return;
        vm.notesLoading = true;
        vm.notesError = '';
        $http.post('/api/notes', {
          project: vm.notesProject,
          content: vm.notesContent || ''
        }).then(function () {
          vm.notesDirty = false;
          vm.notesLastSaved = new Date();
          vm.notesLoading = false;
        }).catch(function (err) {
          vm.notesError = 'Failed to save notes: ' + (err.data || err.statusText || 'Unknown error');
          vm.notesLoading = false;
        });
      };

      // Auto-load notes whenever the panel is opened (always uses the
      // currently selected project so switching projects while closed
      // can't leave stale notes visible)
      $scope.$watch('vm.showNotes', function (newVal) {
        if (newVal) {
          vm.notesProject = vm.selectedProject || '';
          vm.loadNotes();
          // Auto-dodge: keep the panel off the Agent panel / panel columns
          // (notes defaults to 100,100 — right on top of them).
          $timeout(function () {
            if (vm._dodgeFloatingPanel) vm._dodgeFloatingPanel(vm.notes, { selfCls: 'notes-floating-panel', margin: 10 });
          }, 0);
        }
      });

      // Reload notes when project changes (if panel is open)
      $scope.$watch('vm.selectedProject', function (newVal, oldVal) {
        if (newVal && newVal !== oldVal && vm.showNotes) {
          vm.notesProject = newVal;
          vm.loadNotes();
        }
      });

      // ── Dragging ──
      vm.startNotesDrag = function (event) {
        event.preventDefault();
        vm.notes.dragging = true;
        vm.notes.dragStartX = event.clientX - vm.notes.left;
        vm.notes.dragStartY = event.clientY - vm.notes.top;

        var onMove = function (e) {
          if (!vm.notes.dragging) return;
          vm.notes.left = e.clientX - vm.notes.dragStartX;
          vm.notes.top = e.clientY - vm.notes.dragStartY;
          if (vm._clampFloatingPanel) vm._clampFloatingPanel(vm.notes);
          else { vm.notes.left = Math.max(0, vm.notes.left); vm.notes.top = Math.max(0, vm.notes.top); }
          $scope.$apply();
        };
        var onUp = function () {
          vm.notes.dragging = false;
          document.removeEventListener('mousemove', onMove);
          document.removeEventListener('mouseup', onUp);
          if (vm.persistWorkspaceLayout) vm.persistWorkspaceLayout(); // persist the new position
        };
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
      };

      // ── Resizing ──
      vm.startNotesResize = function (dir, event) {
        event.preventDefault();
        event.stopPropagation();
        vm.notes.resizing = true;
        vm.notes.resizeDir = dir;
        vm.notes.resizeStartX = event.clientX;
        vm.notes.resizeStartY = event.clientY;
        vm.notes.resizeStartW = vm.notes.width;
        vm.notes.resizeStartH = vm.notes.height;

        var onMove = function (e) {
          if (!vm.notes.resizing) return;
          var dx = e.clientX - vm.notes.resizeStartX;
          var dy = e.clientY - vm.notes.resizeStartY;
          if (vm.notes.resizeDir.indexOf('e') >= 0) vm.notes.width = Math.max(300, vm.notes.resizeStartW + dx);
          if (vm.notes.resizeDir.indexOf('s') >= 0) vm.notes.height = Math.max(200, vm.notes.resizeStartH + dy);
          $scope.$apply();
        };
        var onUp = function () {
          vm.notes.resizing = false;
          document.removeEventListener('mousemove', onMove);
          document.removeEventListener('mouseup', onUp);
          if (vm.persistWorkspaceLayout) vm.persistWorkspaceLayout(); // persist the new size
        };
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
      };

      // Close notes panel (auto-saves if dirty)
      vm.closeNotes = function () {
        if (vm.notesDirty) {
          vm.saveNotes();
        }
        vm.showNotes = false;
        vm.saveSettings();
      };

      // Keyboard shortcut: Ctrl+Shift+S to save notes (avoids conflict with IDE Ctrl+S)
      document.addEventListener('keydown', function (e) {
        if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key === 'S' && vm.showNotes && vm.notesDirty) {
          e.preventDefault();
          vm.saveNotes();
          $scope.$apply();
        }
      });
    }
  };
});
