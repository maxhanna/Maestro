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

      // Auto-load notes when the panel is first opened
      $scope.$watch('vm.showNotes', function (newVal) {
        if (newVal && !vm.notesLastSaved) {
          vm.notesProject = vm.selectedProject || '';
          vm.loadNotes();
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
          vm.notes.left = Math.max(0, e.clientX - vm.notes.dragStartX);
          vm.notes.top = Math.max(0, e.clientY - vm.notes.dragStartY);
          $scope.$apply();
        };
        var onUp = function () {
          vm.notes.dragging = false;
          document.removeEventListener('mousemove', onMove);
          document.removeEventListener('mouseup', onUp);
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
