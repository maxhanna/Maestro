'use strict';

angular.module('kanbanApp').factory('KanbanMixin', function ($window, $timeout, VoiceInput, $http) {
  function uid() { return Math.random().toString(36).slice(2, 9); }

  function loadCards() {
    return { todo: [], doing: [], done: [], archived: [], selfImproving: [] };
  }

  var _cardsCache = {};
  var _cardsVersion = 0;
  var _saveCardTextTimer = null;

  // Fallback copy path for browsers without navigator.clipboard.
  function legacyCopyLog(text) {
    try {
      var ta = document.createElement('textarea');
      ta.value = text;
      ta.style.position = 'fixed';
      ta.style.opacity = '0';
      document.body.appendChild(ta);
      ta.select();
      document.execCommand('copy');
      document.body.removeChild(ta);
    } catch (e) { }
  }

  return {
    init: function (vm, $scope) {
      vm.state = { todo: [], doing: [], done: [], archived: [], selfImproving: [] };
      vm.isCardActive = function (cardId) { return vm.streamingActive && vm.activeCardId === cardId }
      vm.findCardById = function (cardId) {
        if (!cardId || !vm.state) return null;
        try {
          var cols = ['todo', 'doing', 'done', 'selfImproving'];
          for (var c = 0; c < cols.length; c++) {
            var cards = vm.state[cols[c]] || [];
            for (var i = 0; i < cards.length; i++) {
              if (cards[i].id === cardId) return cards[i];
            }
          }
        } catch (e) {
          console.log("findCardById error", e);
        }
        return null;
      };

      function loadBoardData() {
        $http.get('/api/boarddata/load').then(function (resp) {
          try {
            var data = resp.data;
            if (typeof data === 'string') {
              data = JSON.parse(data);
            }
            if (data && (data.todo || data.doing || data.done || data.archived || data.selfImproving)) {
              vm.state = data;

              // Restore benchmark panel state (running flag + run-all queue/results)
              // from the board so a reload shows accurate progress instead of idle.
              if (vm._restoreBenchmarkState) vm._restoreBenchmarkState();

              if (vm.activeCardId && vm.planItems && vm.planItems.length) {
                var activeCard = findCardById(vm.activeCardId);
                if (activeCard) {
                  var serverItems = (activeCard._plan && activeCard._plan.items)
                    ? activeCard._plan.items : [];
                  if (serverItems.length < vm.planItems.length) {
                    var restoredItems = angular.copy(vm.planItems);
                    serverItems.forEach(function (si) {
                      var match = restoredItems.find(function (ri) { return ri.index === si.index; });
                      if (match && si.done) match.done = true;
                    });
                    activeCard._plan = {
                      items: restoredItems,
                      summary: vm.streamingSummary || (activeCard._plan ? activeCard._plan.summary : ''),
                      score: (activeCard._plan ? activeCard._plan.score : 0)
                    };
                  }
                }
              }
            }
          } catch (e) {
            console.warn('Failed to parse boarddata from server, using default state');
          }
          if ($scope) $scope.$applyAsync();
        }, function () { /* ignore load errors, keep default state */ });
      }

      loadBoardData();

      vm.clearMetaPlan = function (card) {
        if (!card) return;
        if (!$window.confirm('Clear meta-plan for this card? This will remove the sub-plan tracking and allow a clean restart.')) return;
        delete card._metaPlan;
        // Also clear the regular plan if it exists
        delete card._plan;
        vm.planItems = [];
        vm.saveCards();
      };

      vm.refreshBoardData = function (detail) {
        loadBoardData();
        if (detail && detail.target === 'boarddata') {
          console.debug('[boarddata] refresh requested', detail);
        }
      };
      _cardsCache = {};
      _cardsVersion = 0;

      vm.saveCards = function () {
        console.log("Saving cards", vm.state);
        // Save to .boarddata file
        try {
          $http.post('/api/boarddata/save', vm.state).catch(function (err) {
            console.error('Failed to save to board.data file:', err);
          });
          _cardsVersion++;
          vm.updateSelfImprovingCount();
        } catch (e) {
          console.log("Save cards error:", e);
        }
      };

      vm.updateSelfImprovingCount = function () {
        try {
          if (vm.countSelfImprovingCards) {
            vm.selfImprovingCardCount = vm.countSelfImprovingCards();
          }
        } catch (e) {
          console.log("Ignoring selfImproveCount errors", e);
        }
      };

      vm.handleFileSearchChange = function () {
        // When search changes in file attachment modal, show all files/folders
        // This bypasses the normal filtering to help users navigate faster
        if (vm.fileSearchFilter) {
          // Reset file search to show all items when search changes
          vm.fileSearchFilter = '';
          // Trigger a refresh of file listing
          if (vm.refreshFileList) {
            vm.refreshFileList();
          }
        }
      };

      vm.filterCards = function (cards) {
        if (!vm.searchFilter) return cards;
        var filter = vm.searchFilter.toLowerCase();
        return cards.filter(function (card) {
          return card.id.toLowerCase().includes(filter) || card.text.toLowerCase().includes(filter);
        });
      };

      vm.cardsForProject = function (col) {
        var all = vm.state[col] || [];
        if (!vm.selectedProject) return all;
        var key = col + '|' + vm.selectedProject + '|' + (vm.searchFilter || '');
        var cached = _cardsCache[key];
        if (cached && cached._version === _cardsVersion && cached._length === all.length) return cached;
        var filtered = all.filter(function (c) { return c.filePath === vm.selectedProject; });
        // If we're in file search context, bypass filtering to show all files/folders
        if (vm.isInFileSearch && vm.fileSearchFilter) {
          return filtered;
        }
        var result = vm.filterCards(filtered);
        result._version = _cardsVersion;
        result._length = all.length;
        _cardsCache[key] = result;
        return result;
      };

      vm.addCard = function (col) {
        vm.state[col].push({
          id: uid(),
          text: '',
          filePath: vm.selectedProject,
          createdAt: new Date().toISOString(),
          priority: 'medium',
          attached: [],
          autoPr: vm.prByDefault !== false,
          selfImproving: false,
          createTests: false,
          llmEndpointId: ''
        });
        vm.saveCards();
        $timeout(function () {
          var cards = vm.state[col];
          if (cards.length) {
            var newCard = cards[cards.length - 1];
            var textarea = document.querySelector('[data-card-id="' + newCard.id + '"] textarea');
            if (textarea) textarea.focus();
          }
        }, 0);
      };

      vm.clearDoneCards = function () {
        if (!$window.confirm('Delete all done tasks?')) return;
        vm.state.done = [];
        vm.saveCards();
      };

      vm.archiveCard = function (id, col) {
        col = col || 'done';
        var idx = vm.state[col].findIndex(function (c) { return c.id === id; });
        if (idx === -1) return;
        var card = vm.state[col].splice(idx, 1)[0];
        vm.state.archived.push(card);
        vm.saveCards();
      };

      vm.clearAllArchivedCards = function () {
        if (!$window.confirm('Delete all archived cards?')) return;
        vm.state.archived = [];
        vm.saveCards();
      };

      vm.archiveAllDone = function () {
        if (!vm.state.done.length) return;
        if (!$window.confirm('Archive all done tasks?')) return;
        Array.prototype.push.apply(vm.state.archived, vm.state.done);
        vm.state.done = [];
        vm.saveCards();
      };

      vm.unarchiveCard = function (id) {
        var idx = vm.state.archived.findIndex(function (c) { return c.id === id; });
        if (idx === -1) return;
        var card = vm.state.archived.splice(idx, 1)[0];
        vm.state.todo.push(card);
        vm.saveCards();
      };

      vm.isInFileSearch = false;
      vm.voiceSupported = VoiceInput.isSupported();
      vm.isRecording = false;

      vm.recordVoice = function (card) {
        if (!card) return;
        if (VoiceInput.isActive()) {
          VoiceInput.stop();
          vm.isRecording = false;
        } else {
          VoiceInput.start(card, $scope);
          vm.isRecording = true;
          // Focus the textarea for the card when starting recording
          $timeout(function () {
            var textarea = document.querySelector('[data-card-id="' + card.id + '"] textarea');
            if (textarea) {
              textarea.focus();
              textarea.scrollIntoView({ behavior: 'smooth', block: 'center' });
            }
          }, 0);
        }
      };

      vm.copyCardText = function (card) {
        if (!card || !card.text) return;
        if (navigator.clipboard) {
          navigator.clipboard.writeText(card.text).then(function () {
            console.log('Card text copied to clipboard');
          }).catch(function (err) {
            console.error('Failed to copy card text: ', err);
          });
        } else {
          var textArea = document.createElement('textarea');
          textArea.value = card.text;
          document.body.appendChild(textArea);
          textArea.select();
          try {
            document.execCommand('copy');
            console.log('Card text copied to clipboard (fallback)');
          } catch (err) {
            console.error('Failed to copy card text (fallback): ', err);
          }
          document.body.removeChild(textArea);
        }
      };

      vm.copyCardLog = function (card, $event) {
        if ($event) { $event.stopPropagation(); $event.preventDefault(); }
        var btn = $event && $event.currentTarget;
        if (!card || !card.agentLog || !card.agentLog.length) return;
        var lines = card.agentLog.map(function (e) {
          var line = (e.ts ? e.ts + '  ' : '') + (e.level || 'info') + ': ' + (e.message || '');
          if (e.detail) {
            var d = vm.formatLogDetail ? vm.formatLogDetail(e.detail) : JSON.stringify(e.detail);
            if (d) line += '\n    ' + String(d).split('\n').join('\n    ');
          }
          return line;
        });
        var text = lines.join('\n');
        function copied() {
          if (!btn) return;
          var old = btn.textContent;
          btn.textContent = '✓ Copied';
          setTimeout(function () { btn.textContent = old; }, 1200);
        }
        if (navigator.clipboard && navigator.clipboard.writeText) {
          navigator.clipboard.writeText(text).then(copied, function () { legacyCopyLog(text); copied(); });
        } else { legacyCopyLog(text); copied(); }
      };

      vm.openDeleteCardConfirm = function (id, col) {
        vm.confirmDeleteCardId = id;
        var col = col || 'done';
        var card = vm.state[col].find(function (c) { return c.id === id; });
        if (!card) {
          alert('Card not found in ' + col + ' column');
          return;
        }
        vm.deleteCardConfirm = {
          id: id,
          col: col,
          show: true,
          dontShowAgain: false
        };
      };

      vm.confirmDeleteCard = function () {
        if (!vm.deleteCardConfirm || !vm.deleteCardConfirm.id) return;
        var id = vm.deleteCardConfirm.id;
        var col = vm.deleteCardConfirm.col;
        var idx = vm.state[col].findIndex(function (c) { return c.id === id; });
        if (idx !== -1) {
          vm.state[col].splice(idx, 1);
          console.log('Deleted card with id', id);
          vm.saveCards();
        }
        // Deleting the last benchmark card of a run-all batch finalizes it
        // (the batch has no live card left), so the run buttons unlock.
        if (vm._finalizeStaleBenchmarkIfNeeded) vm._finalizeStaleBenchmarkIfNeeded();
        // Also clear a latched single-benchmark "running" flag when the deleted
        // card was its only evidence (e.g. a stuck single-benchmark run).
        if (vm.reconcileBenchmarkRunning) vm.reconcileBenchmarkRunning();
        if (vm.deleteCardConfirm.dontShowAgain) {
          try { $window.localStorage.setItem('weaverconfig.deleteCardConfirm', 'false'); } catch (e) { }
        }
        vm.confirmDeleteCardId = null;
        vm.deleteCardConfirm = null;
      };

      vm.closeDeleteCardConfirm = function (event) {
        if (event && event.key === 'Escape') {
          event.stopPropagation();
          event.preventDefault();
          vm.confirmDeleteCardId = null;
          vm.deleteCardConfirm = null;
          return;
        }
        vm.confirmDeleteCardId = null;
        vm.deleteCardConfirm = null;
      };

      vm.deleteCard = function (id, col) {
        col = col || 'todo';
        var idx = vm.state[col].findIndex(function (c) { return c.id === id; });
        if (idx !== -1) {
          vm.state[col].splice(idx, 1);
          vm.saveCards();
        }
        // Deleting the last benchmark card of a run-all batch finalizes it
        // (the batch has no live card left), so the run buttons unlock.
        if (vm._finalizeStaleBenchmarkIfNeeded) vm._finalizeStaleBenchmarkIfNeeded();
        // Also clear a latched single-benchmark "running" flag when the deleted
        // card was its only evidence (e.g. a stuck single-benchmark run).
        if (vm.reconcileBenchmarkRunning) vm.reconcileBenchmarkRunning();
      };

      vm.onSelfImprovingToggle = function (card) {
        if (card.selfImproving) {
          var idx = vm.state.todo.findIndex(function (c) { return c.id === card.id; });
          if (idx !== -1) {
            var c = vm.state.todo.splice(idx, 1)[0];
            c.selfImproving = true;
            c.ready = false;
            if (!vm.state.selfImproving) vm.state.selfImproving = [];
            vm.state.selfImproving.push(c);
            vm.saveCards();
            console.log('Moved card to Self-Improving column:', c);
          }
        } else {
          var idx = vm.state.selfImproving.findIndex(function (c) { return c.id === card.id; });
          if (idx !== -1) {
            if (!vm.state.selfImproving) vm.state.selfImproving = [];
            var c = vm.state.selfImproving.splice(idx, 1)[0];
            c.selfImproving = false;
            vm.state.todo.push(c);
            vm.saveCards();
            console.log('Moved card to To Do column:', c);
          }
        }
      };

      vm.toggleCardReady = function (card) {
        try {
          card.ready = !card.ready;
          if (card.ready && (!vm.streamingActive || !vm.isCardActive(card.id))) {
            vm.startCard(card);
          }
        }
        catch (e) {
          console.log("toggleCardReady error", e);
        }
      };

      vm.planDoneCount = function (items) {
        if (!items || !items.length) return 0;
        return items.filter(function (i) { return i.done; }).length;
      };

      // Number of 'recovering' log entries for this card — the count of times the
      // run healed itself mid-stream (stream drop retry, finish-this continuation).
      // Persisted runs read card.agentLog; the live run reads the streaming log.
      vm.recoveredCount = function (card) {
        try {
          var log = null;
          if (card && card.agentLog && card.agentLog.length) {
            log = card.agentLog;
          } else if (card && vm.isCardActive && vm.isCardActive(card.id) && vm.agentActivityLog) {
            log = vm.agentActivityLog;
          }
          if (!log) return 0;
          var n = 0;
          for (var i = 0; i < log.length; i++) {
            if (log[i] && log[i].level === 'recovering') n++;
          }
          return n;
        } catch (e) { return 0; }
      };

      vm.isPlanMarker = function (file) {
        return file === '_planning' || file === '_executing' || file === '_verifying' || file === '_exploring';
      };

      vm.planMarkerIcon = function (file) {
        if (file === '_planning') return '💭';
        if (file === '_executing') return '⚡';
        if (file === '_verifying') return '🔍';
        if (file === '_exploring') return '🔎';
        return null;
      };

      vm.planMarkerLabel = function (file, change) {
        if (!vm.isPlanMarker(file)) return null;
        if (change) return change;
        if (file === '_planning') return 'Thinking…';
        if (file === '_executing') return 'Working…';
        if (file === '_verifying') return 'Verifying…';
        if (file === '_exploring') return 'Exploring…';
        return 'Working…';
      };

      vm.togglePlanItem = function (card, index) {
        if (!card._plan || !card._plan.items) return;
        var item = card._plan.items.find(function (i) { return i.index === index; });
        if (item) {
          item.done = !item.done;
          vm.saveCards();
        }
      };

      vm.removePlanItem = function (card, index) {
        if (!card._plan || !card._plan.items) return;
        card._plan.items = card._plan.items.filter(function (i) { return i.index !== index; });
        if (card._plan.items.length === 0) {
          delete card._plan;
        }
        vm.saveCards();
      };

      vm.clearPlan = function (card) {
        delete card._plan;
        // Also clear persisted plan data in analysis
        if (card.agentAnalysis) {
          delete card.agentAnalysis.planItems;
        }
        if (card.agentResult) {
          delete card.agentResult.planItems;
        }
        vm.planItems = [];
        vm.saveCards();
      };

      vm.moveCard = function (id, from, to) {
        try {
          var idx = vm.state[from].findIndex(function (c) { return c.id === id; });
          if (idx === -1) return;

          var card = vm.state[from][idx];

          if (from.toLowerCase() === "doing" && to.toLowerCase() === "todo" && vm.streamingActive && vm.activeCardId === card.id) {
            console.log("Back pressed on active card; Stopping agent.");
            vm.stopAgent(card);
          }

          if (!card.selfImproving && to === 'selfImproving') {
            card.selfImproving = true;
            card.ready = false;
          }
          if (card.selfImproving && to !== 'selfImproving' && to !== 'doing') {
            to = 'selfImproving';
          }
          if (from === 'todo' && to === 'doing' && !card.ready) {
            return $window.alert('Mark the card as Ready first (press Start)');
          }

          vm.state[from].splice(idx, 1);
          if (from === 'doing' && to === 'todo') {
            card.ready = false;
            // Preserve agentAnalysis/agentLog for previous-analysis display
            vm.activeCardId = null;
            if (vm.streamingActive && vm.activeCardId === card.id) {
              vm.stopAgent(card);
            }
            // Scroll to the card after moving it back to To Do
            $timeout(function () {
              var cardElement = document.querySelector('[data-card-id="' + card.id + '"]');
              if (cardElement) {
                cardElement.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
              }
            }, 0);
          }
          if (from === 'doing' && to === 'done') {
            // Only clear activeCardId if it's not part of the current project
            if (card.filePath !== vm.selectedProject) {
              vm.activeCardId = null;
            }
          }
          if (from === 'doing' && to === 'selfImproving') {
            card.selfImproving = true;
            card.ready = false;
          }
          if (from === 'selfImproving' && to === 'doing' && !card.ready) {
            vm.state.selfImproving.push(card);
            vm.saveCards();
            return $window.alert('Mark the card as Ready first (press Start)');
          }
          if (from === 'done' && to === 'todo') {
            card.ready = false;
            // Preserve agentAnalysis/agentLog for previous-analysis display
            // Only clear activeCardId if it's not part of the current project
            if (card.filePath !== vm.selectedProject) {
              vm.activeCardId = null;
            }
            // Scroll to the card after moving it back to To Do
            $timeout(function () {
              var cardElement = document.querySelector('[data-card-id="' + card.id + '"]');
              if (cardElement) {
                cardElement.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
              }
            }, 0);
          }
          if (from === 'todo' && to === 'done') {
            card.ready = false;
            delete card.agentAnalysis;
            delete card.agentSteps;
            // Only clear activeCardId if it's not part of the current project
            if (card.filePath !== vm.selectedProject) {
              vm.activeCardId = null;
            }
          }
          vm.state[to].push(card);
          if (from === 'todo' && to === 'doing' && card.ready) {
            // Clear previous analysis when starting a fresh run
            delete card.agentAnalysis;
            delete card.agentLog;
            delete card._meetingReplay; // stale replay — the new run re-records it
            vm.executeAgent(card);
          }
          if (from === 'selfImproving' && to === 'doing' && card.ready) {
            delete card.agentAnalysis;
            delete card.agentLog;
            delete card._meetingReplay; // stale replay — the new run re-records it
            vm.executeAgent(card);
          }
          vm.saveCards();
        } catch (e) {
          console.log("moveCard error.", e);
        }
      };

      vm.reopenCard = function (card) {
        card.ready = false;
        // Preserve agentAnalysis/agentLog for previous-analysis display
        var idx = vm.state.done.findIndex(function (c) { return c.id === card.id; });
        if (idx === -1) return;
        vm.state.done.splice(idx, 1);
        vm.state.todo.push(card);
        vm.saveCards();
        // Scroll to the card after reopening it to To Do
        $timeout(function () {
          var cardElement = document.querySelector('[data-card-id="' + card.id + '"]');
          if (cardElement) {
            cardElement.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
          }
        }, 0);
      };

      vm.getAttachedFiles = function (card) {
        if (Array.isArray(card.attached)) return card.attached;
        if (card.attached) return [card.attached];
        return [];
      };

      vm.removeAttachment = function (cardId, attachmentName, col) {
        var cards = vm.state[col]; // col : 'todo', 'doing', 'done', or 'selfImproving'
        for (var i = 0; i < cards.length; i++) {
          if (cards[i].id === cardId) {
            var attached = cards[i].attached;
            if (Array.isArray(attached)) {
              var index = attached.indexOf(attachmentName);
              if (index !== -1) {
                attached.splice(index, 1);
              }
            }
            break;
          }
        }

        vm.saveCards();
      };

      vm.openInIde = function (filePath) {
        if (vm.useVSCodeInsteadOfIDE) {
          var fullPath = (vm.selectedProject || '') + '/' + filePath;
          $http.post('/api/config/open-in-vscode', { filePath: fullPath }).then(function () {}, function (err) {
            console.error('Failed to open in VS Code', err);
          });
          return;
        }
        vm.showIDE = true;
        if (vm.openFile) vm.openFile(filePath);
      };

      vm.editCardText = function (card) {
        var newText = $window.prompt('Edit task:', card.text);
        if (newText !== null && newText !== card.text) {
          card.text = newText;
          vm.saveCards();
        }
      };

      vm.saveCardText = function (card) {
        // Debounce the save so it only fires 500ms after the user stops typing
        if (_saveCardTextTimer) { $timeout.cancel(_saveCardTextTimer); }
        _saveCardTextTimer = $timeout(function () {
          console.log("saving card text");
          vm.saveCards();
        }, 500);
      };

      vm.todoTextAreaClicked = function (event) {
        event.stopPropagation();
        event.preventDefault();
      }

      vm.splitCardIntoSubtasks = function (card) {
        if (!card || !card.text) return;
        var lines = card.text.split(/\n+/).map(function (l) { return l.trim(); }).filter(Boolean);
        if (lines.length <= 1) {
          var parts = card.text.split(/[.;]\s+/).filter(function (p) { return p.length > 10; });
          if (parts.length <= 1) return $window.alert('Task is already small. Add line breaks or bullet points to split.');
          lines = parts;
        }
        if (!$window.confirm('Split into ' + lines.length + ' smaller Todo cards?')) return;
        var idx = vm.state.todo.findIndex(function (c) { return c.id === card.id; });
        if (idx === -1) {
          ['doing', 'done'].forEach(function (col) {
            var i = vm.state[col].findIndex(function (c) { return c.id === card.id; });
            if (i !== -1) { vm.state[col].splice(i, 1); idx = -2; }
          });
        } else {
          vm.state.todo.splice(idx, 1);
        }
        lines.forEach(function (line, i) {
          vm.state.todo.push({
            id: uid(),
            text: line.charAt(0).toUpperCase() + line.slice(1),
            filePath: card.filePath || vm.selectedProject,
            createdAt: new Date().toISOString(),
            priority: card.priority || 'medium',
            attached: i === 0 ? angular.copy(card.attached || []) : [],
            selfImproving: false,
            llmEndpointId: card.llmEndpointId || ''
          });
        });
        vm.saveCards();
      };

      vm.buildDiffLines = function (oldLines, newLines, startLine) {
        if (!oldLines) oldLines = [];
        if (!newLines) newLines = [];
        startLine = startLine || 0;
        var maxLen = Math.max(oldLines.length, newLines.length);
        var result = [];
        for (var i = 0; i < maxLen; i++) {
          var oldLine = i < oldLines.length ? oldLines[i] : null;
          var newLine = i < newLines.length ? newLines[i] : null;
          var bothExist = oldLine !== null && newLine !== null;
          var changed = bothExist ? oldLine !== newLine : true;
          result.push({
            oldLine: oldLine,
            newLine: newLine,
            changed: changed,
            bothExist: bothExist,
            oldLineNum: startLine + i,
            newLineNum: startLine + i
          });
        }
        return result;
      };

      vm.moveCardToDoing = function (cardId) {
        if (!vm.state.selfImproving) { vm.state.selfImproving = []; }
        var card = undefined;

        var idx = vm.state.todo.findIndex(function (c) { return c.id === cardId; });
        if (idx === -1) {
          idx = vm.state.selfImproving.findIndex(function (c) { return c.id === cardId; });
          if (idx === -1) {
            idx = vm.state.archived.findIndex(function (c) { return c.id === cardId; });
            if (idx === -1) {
              return;
            } else {
              card = vm.state.archived.splice(idx, 1)[0];
            }
          }
          else {
            card = vm.state.selfImproving.splice(idx, 1)[0];
          }
        } else {
          card = vm.state.todo.splice(idx, 1)[0];
        }
        if (card) {
          vm.state.doing.push(card);
          vm.saveCards();
        }
      };

      vm.moveCardToDone = function (card) {
        var cardId = card.id || card._id;
        var targetCol = card.selfImproving ? 'selfImproving' : 'done';
        console.log("Moving card to " + targetCol);
        var idx = vm.state.doing.findIndex(function (c) { return c.id === cardId; });
        if (idx === -1) {
          idx = vm.state.doing.findIndex(function (c) { return (c.id || c._id) == cardId; });
        }
        if (idx === -1) {
          console.log("ERROR: Could not find card in doing column");
          return;
        }
        var moved = vm.state.doing.splice(idx, 1)[0];
        if (moved) {
          // Stamp the completion time so stats like "cards finished this week"
          // reflect when the card actually finished, not when it was created.
          moved.finishedAt = new Date().toISOString();
          console.log("Found card in doing, moving to " + targetCol);
          if (targetCol === 'selfImproving') {
            // Round-robin: a completed self-improving card goes to the BACK of the list
            // so the armed cycle advances 1-by-1 (A → B → C → A …) instead of re-running
            // the same front card forever.
            vm.state[targetCol].push(moved);
          } else {
            vm.state[targetCol].push(moved);
          }
          console.log("card added to " + targetCol + " setting active card to null");
          vm.activeCardId = null;
          if (!vm.activeCardIds) vm.activeCardIds = new Set();
          vm.activeCardIds.delete(cardId);
          console.log("saving cards");
          vm.saveCards();
        } else {
          console.log("ERROR: Could not find card to move in Doing column");
        }
      };

      vm.startCard = function (card) {
        if (!card) return;
        try {
          if (card.ready) {
            // Self-improving cards only run while NO regular card (todo/doing/done) is
            // active — even on a physical start. Arm the cycle and leave the card Ready;
            // processQueuedCards drains it (1-by-1) once regular work clears.
            if (card.selfImproving && vm.regularAgentActive && vm.regularAgentActive()) {
              if (vm.selfImprovingAgentActive !== true) {
                vm.selfImprovingAgentActive = true;
                if (vm.persistSelfImprovingAgent) vm.persistSelfImprovingAgent();
              }
              card.ready = true;
              vm.saveCards();
              return;
            }
            vm.moveCardToDoing(card.id);
            vm.executeAgent(card);
          } else {
            card.ready = true;
            vm.saveCards();
          }
        }
        catch (e) {
          console.log("startCard error", e);
        }
      };



      vm.processQueue = function () {
        if (vm.streamingActive) return;
        var readyCards = vm.state.todo.filter(function (c) { return c.filePath === vm.selectedProject && c.ready && !c.selfImproving; });
        if (!readyCards.length) return;
        var next = readyCards[readyCards.length - 1];
        vm.moveCardToDoing(next.id);
        vm.executeAgent(next);
      };

      vm.processSelfImprovingQueue = function () {
        if (vm.streamingActive) return;
        // Self-improving cards only run when the user has armed the cycle AND no regular
        // card (todo/doing/done) is active.
        if (!vm.selfImprovingAgentActive) return;
        if (vm.regularAgentActive && vm.regularAgentActive()) return;
        var readyCards = vm.state.selfImproving.filter(function (c) { return c.filePath === vm.selectedProject && c.ready && c.selfImproving; });
        if (!readyCards.length) return;
        // Round-robin: completed cards are pushed to the BACK, so pick the front
        // card to advance 1-by-1 through the list.
        var next = readyCards[0];
        vm.moveCardToDoing(next.id);
        vm.executeAgent(next);
      };

      vm.toggleSelfImprovingAgent = function () {
        vm.selfImprovingAgentActive = !vm.selfImprovingAgentActive;
        if (vm.persistSelfImprovingAgent) vm.persistSelfImprovingAgent();
        if (vm.selfImprovingAgentActive && vm.processQueuedCards) vm.processQueuedCards();
      };

      vm.countSelfImprovingCards = function () {
        if (!vm.state || !vm.state.selfImproving) return 0;
        return vm.state.selfImproving.filter(function (c) { return c.filePath === vm.selectedProject; }).length;
      };

      vm.focusCardTextarea = function (card) {
        console.log("focusing on text area ", card);
        if (!card) return;
        var el = document.querySelector('[data-card-id="' + card.id + '"] textarea');
        if (el && !card.text.trim()) {
          $timeout(function () { el.focus(); }, 50);
        }
      };

      // Persistent workspace: per-column widths survive reloads. Seeded from the
      // settings localStorage store (stashed by SettingsMixin before this mixin).
      vm._kanbanColWidths = {};
      if (vm._savedKanbanColWidths && typeof vm._savedKanbanColWidths === 'object') {
        vm._kanbanColWidths = vm._savedKanbanColWidths;
      }

      vm.initColumnResizers = function () {
        try {
          var existing = document.querySelectorAll('.col-resizer');
          existing.forEach(function (el) { el.remove(); });
          var cols = Array.prototype.slice.call(document.querySelectorAll('#board .column'));
          if (!cols.length) {
            // The board template is ng-included asynchronously — retry briefly
            // so the resizers attach as soon as the columns exist.
            if (vm._resizerRetries === undefined) vm._resizerRetries = 0;
            if (vm._resizerRetries < 25) {
              vm._resizerRetries++;
              $timeout(function () { vm.initColumnResizers(); }, 200);
            }
            return;
          }
          vm._resizerRetries = 0;
          // Restore persisted widths (keyed by data-col) so the layout comes
          // back exactly as the user left it.
          var savedW = vm._kanbanColWidths || {};
          cols.forEach(function (c) {
            var key = c.getAttribute('data-col');
            if (key && savedW[key]) c.style.flex = '0 0 ' + Math.round(savedW[key]) + 'px';
          });
          for (var i = 0; i < cols.length - 1; i++) {
            (function (leftCol) {
              var resizer = document.createElement('div');
              resizer.className = 'col-resizer';
              leftCol.appendChild(resizer);
              resizer.addEventListener('pointerdown', function startDrag(e) {
                e.preventDefault();
                var rightCol = leftCol.nextElementSibling;
                if (!rightCol) return;
                var startX = e.clientX;
                var leftRect = leftCol.getBoundingClientRect();
                var rightRect = rightCol.getBoundingClientRect();
                var leftW = leftRect.width;
                var rightW = rightRect.width;
                var min = 200;
                var nl = leftW, nr = rightW;
                document.body.style.userSelect = 'none';
                resizer.classList.add('active');
                function onMove(ev) {
                  var dx = ev.clientX - startX;
                  nl = leftW + dx;
                  nr = rightW - dx;
                  var total = leftW + rightW;
                  if (nl < min) { nl = min; nr = total - min; }
                  if (nr < min) { nr = min; nl = total - min; }
                  leftCol.style.flex = '0 0 ' + Math.round(nl) + 'px';
                  rightCol.style.flex = '0 0 ' + Math.round(nr) + 'px';
                }
                function stopDrag() {
                  document.removeEventListener('pointermove', onMove);
                  document.removeEventListener('pointerup', stopDrag);
                  document.body.style.userSelect = '';
                  resizer.classList.remove('active');
                  // Persist the new widths so the layout survives a reload.
                  var lk = leftCol.getAttribute('data-col');
                  var rk = rightCol.getAttribute('data-col');
                  var saved = vm._kanbanColWidths = vm._kanbanColWidths || {};
                  if (lk) saved[lk] = Math.round(nl);
                  if (rk) saved[rk] = Math.round(nr);
                  if (vm.persistWorkspaceLayout) vm.persistWorkspaceLayout();
                }
                document.addEventListener('pointermove', onMove);
                document.addEventListener('pointerup', stopDrag);
              });
              resizer.addEventListener('dblclick', function () {
                cols.forEach(function (c) {
                  c.style.flex = ''; c.style.width = '';
                  var key = c.getAttribute('data-col');
                  if (key && vm._kanbanColWidths) delete vm._kanbanColWidths[key];
                });
                if (vm.persistWorkspaceLayout) vm.persistWorkspaceLayout();
              });
            })(cols[i]);
          }
        } catch (e) { console.error('resizer error', e); }
      };

      // Toggle a board column's visibility and persist it, re-attaching resizers
      // afterwards since ng-if re-renders the column DOM.
      vm.toggleColumn = function (col) {
        if (col === 'todo') vm.showTodo = !vm.showTodo;
        else if (col === 'doing') vm.showDoing = !vm.showDoing;
        else if (col === 'done') vm.showDone = !vm.showDone;
        else if (col === 'archived') vm.showArchived = !vm.showArchived;
        else if (col === 'selfImproving') vm.showSelfImproving = !vm.showSelfImproving;
        $timeout(function () { vm.initColumnResizers(); }, 0);
        if (vm.persistWorkspaceLayout) vm.persistWorkspaceLayout();
      };

      vm.setupDragDrop = function () {
        try {
          var indicatorEl = null;

          function cleanupDropIndicators(col) {
            if (indicatorEl) { indicatorEl.remove(); indicatorEl = null; }
            if (col) {
              col.querySelectorAll('.card.drop-above, .card.drop-below').forEach(function (c) {
                c.classList.remove('drop-above', 'drop-below');
              });
            }
          }

          function positionDropIndicator(col, cursorY) {
            col.querySelectorAll('.card.drop-above, .card.drop-below').forEach(function (c) {
              c.classList.remove('drop-above', 'drop-below');
            });
            var cardEls = col.querySelectorAll('.card');
            var colRect = col.getBoundingClientRect();
            if (!indicatorEl) {
              indicatorEl = document.createElement('div');
              indicatorEl.className = 'drop-indicator';
              col.appendChild(indicatorEl);
            }
            indicatorEl.style.display = '';
            if (cardEls.length === 0) {
              indicatorEl.style.top = '0px';
              return;
            }
            for (var i = 0; i < cardEls.length; i++) {
              var cardRect = cardEls[i].getBoundingClientRect();
              var cardMid = cardRect.top + cardRect.height / 2;
              if (cursorY < cardMid) {
                cardEls[i].classList.add('drop-above');
                indicatorEl.style.top = (cardRect.top - colRect.top) + 'px';
                return;
              }
              cardEls[i].classList.add('drop-below');
            }
            var lastRect = cardEls[cardEls.length - 1].getBoundingClientRect();
            indicatorEl.style.top = (lastRect.bottom - colRect.top) + 'px';
          }

          // Use document-level event delegation so drag/drop works even after
          // Angular re-renders card elements (e.g., after Stop + Back).
          document.addEventListener('dragstart', function (e) {
            var card = e.target.closest('.card');
            if (!card || !card.closest('#board')) return;
            e.dataTransfer.setData('text/plain', card.id.replace('card-', ''));
            card.classList.add('dragging');
            document.body.classList.add('dragging-active');
          });

          document.addEventListener('dragend', function (e) {
            var card = e.target.closest('.card');
            if (!card || !card.closest('#board')) return;
            card.classList.remove('dragging');
            document.body.classList.remove('dragging-active');
            document.querySelectorAll('.cards').forEach(function (c) {
              c.closest('.column').classList.remove('drop-target');
              cleanupDropIndicators(c);
            });
            _dragOverCol = null;
          });

          var _dragOverCol = null;
          document.addEventListener('dragover', function (e) {
            var col = e.target.closest('.cards');
            if (!col || !col.closest('#board')) return;
            e.preventDefault();
            if (_dragOverCol && _dragOverCol !== col) {
              _dragOverCol.closest('.column').classList.remove('drop-target');
              cleanupDropIndicators(_dragOverCol);
            }
            _dragOverCol = col;
            col.closest('.column').classList.add('drop-target');
            positionDropIndicator(col, e.clientY);
          });

          document.addEventListener('dragleave', function (e) {
            var col = e.target.closest('.cards');
            if (!col || !col.closest('#board')) return;
            var related = e.relatedTarget;
            if (related && col.contains(related)) return;
            col.closest('.column').classList.remove('drop-target');
            cleanupDropIndicators(col);
            if (_dragOverCol === col) _dragOverCol = null;
          });

          document.addEventListener('drop', function (e) {
            e.preventDefault();
            var col = e.target.closest('.cards');
            if (!col || !col.closest('#board')) return;
            col.closest('.column').classList.remove('drop-target');
            cleanupDropIndicators(col);
            _dragOverCol = null;

            var cardId = e.dataTransfer.getData('text/plain');
            var targetCol = col.closest('.column') ? col.closest('.column').getAttribute('data-col') : null;
            if (!cardId || !targetCol) return;
            var fromCol = null;
            ['todo', 'doing', 'done', 'archived', 'selfImproving'].forEach(function (cn) {
              var idx = vm.state[cn].findIndex(function (c) { return c.id === cardId; });
              if (idx !== -1) fromCol = cn;
            });
            if (!fromCol) return;
            var cardObj = vm.state[fromCol].find(function (c) { return c.id === cardId; });
            if (!cardObj) return;

            var colRect = col.getBoundingClientRect();
            var cursorY = e.clientY - colRect.top;
            var cardEls = col.querySelectorAll('.card');
            var dropIndex = vm.state[targetCol].length;

            for (var i = 0; i < cardEls.length; i++) {
              var cardRect = cardEls[i].getBoundingClientRect();
              var cardMid = cardRect.top + cardRect.height / 2;
              if (e.clientY < cardMid) {
                var targetCardId = cardEls[i].id.replace('card-', '');
                dropIndex = vm.state[targetCol].findIndex(function (c) { return c.id === targetCardId; });
                if (dropIndex < 0) dropIndex = i;
                break;
              }
            }

            if (fromCol === targetCol) {
              var fromIndex = vm.state[fromCol].findIndex(function (c) { return c.id === cardId; });
              if (fromIndex === -1) return;
              vm.state[fromCol].splice(fromIndex, 1);
              if (fromIndex < dropIndex) dropIndex--;
              vm.state[targetCol].splice(Math.max(0, dropIndex), 0, cardObj);
            } else {
              if (fromCol === 'todo' && targetCol === 'doing' && !cardObj.ready) {
                alert('Mark the card as Ready first (press Start)');
                return;
              }
              if (fromCol === 'doing' && targetCol === 'todo') {
                cardObj.ready = false;
              }
              if (fromCol === 'done' && targetCol === 'todo') {
                cardObj.ready = false;
              }
              var idx = vm.state[fromCol].findIndex(function (c) { return c.id === cardId; });
              if (idx === -1) return;
              vm.state[fromCol].splice(idx, 1);
              vm.state[targetCol].splice(Math.max(0, dropIndex), 0, cardObj);
              if (fromCol === 'todo' && targetCol === 'doing' && cardObj.ready) {
                delete cardObj.agentAnalysis;
                delete cardObj.agentLog;
                vm.saveCards();
                vm.executeAgent(cardObj);
                return;
              }
            }
            vm.saveCards();
            if ($scope) { $scope.$applyAsync(); }
          });
        } catch (e) { console.error('dragdrop error', e); }
      };

      $timeout(function () { vm.setupDragDrop(); vm.initColumnResizers(); }, 500);
      // The board is ng-if'd on vm.showKanban, so closing it destroys the column
      // DOM (inline widths + attached resizers). Re-attach and re-apply saved
      // widths whenever the board re-opens.
      $scope.$watch(function () { return vm.showKanban; }, function (v) {
        if (v) $timeout(function () { vm.initColumnResizers(); }, 100);
      });
    }
  };
});
