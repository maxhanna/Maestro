// app.js
angular.module('kanbanApp', [])
  .config(['$provide', function ($provide) {
    // ── Global error surfacing ──────────────────────────────────────────
    // Uncaught JS errors (ReferenceError, TypeError, …) normally only land in
    // the devtools console. Route them through $exceptionHandler (Angular's
    // own digest-time catcher) plus window.onerror / unhandledrejection
    // (plain JS / async paths) so they surface as a visible toast with
    // file:line:col for quick diagnosis. The pure logic (stack parsing, burst
    // dedupe, filtering) lives in the testable WeaverErrorCore module.
    var ErrorCore = window.WeaverErrorCore;
    if (!ErrorCore || typeof ErrorCore.createDedupe !== 'function') {
      // error-core.js didn't load (stale cache) — degrade to no-op instead of
      // throwing during Angular config and breaking the whole app.
      console.error('[Weaver] error-core.js not loaded — error toasts/recent-errors disabled');
      ErrorCore = { shouldFilter: function () { return true; }, parseStack: function () { return null; }, makeErrorKey: function () { return ''; }, createDedupe: function () { return { hit: function () {}, hitsOf: function () { return 0; }, isBurst: function () { return false; }, record: function () {}, reset: function () {} }; } };
    }
    var _errorDedupe = ErrorCore.createDedupe({ windowMs: 3000, maxKeys: 300 });
    var _recentErrors = []; // { key, name, message, file, line, col, ts, stack, count }
    var _MAX_RECENT = 50;

    function _showErrorToast(message, loc) {
      try {
        var container = document.getElementById('weaver-toast-container');
        if (!container) return;
        var toast = document.createElement('div');
        toast.className = 'weaver-toast error';
        var glyph = document.createElement('div');
        glyph.className = 'toast-glyph';
        glyph.textContent = '⚠';
        var body = document.createElement('span');
        body.className = 'toast-text';
        var msg = document.createElement('div');
        msg.textContent = message;
        body.appendChild(msg);
        if (loc) {
          var locEl = document.createElement('div');
          locEl.className = 'toast-loc';
          locEl.textContent = loc.file + ':' + loc.line + ':' + loc.col;
          locEl.title = loc.full;
          body.appendChild(locEl);
          _wireSnippetHover(locEl, loc.full || loc.file, loc.line);
        }
        toast.appendChild(glyph);
        toast.appendChild(body);
        container.appendChild(toast);
        void toast.offsetHeight;
        toast.classList.add('show');
        setTimeout(function () {
          toast.classList.remove('show');
          toast.classList.add('hide');
          setTimeout(function () {
            _hideSnippetTip();
            if (toast.parentNode) toast.parentNode.removeChild(toast);
          }, 30000);
        }, 5000);
      } catch (e) { /* the error toast must never throw */ }
    }

    function _handleGlobalError(err, cause, alreadyLogged) {
      if (ErrorCore.shouldFilter(err)) return;
      var loc = ErrorCore.parseStack(err.stack);
      var key = ErrorCore.makeErrorKey(err, loc);
      var now = Date.now();
      _errorDedupe.hit(key); // count every occurrence, even burst-suppressed
      if (_errorDedupe.isBurst(key, now)) {
        // Same incident inside the burst window: no new toast/entry, but keep
        // the badge and any open panel's occurrence count in sync.
        _refreshErrorCounts();
        return;
      }
      _errorDedupe.record(key, now);
      if (!alreadyLogged) console.error('[Weaver] Uncaught ' + (cause || 'error'), err);
      _showErrorToast((err.name ? err.name + ': ' : '') + err.message, loc);
      _recordError(err, loc, key);
    }

    // ── Recent-errors badge + panel ──────────────────────────────────────
    // A small bottom-right badge shows how many errors have been surfaced;
    // clicking it opens a panel with every recent error (message, file:line,
    // timestamp) so users can review or copy details after the toast fades.
    var _errorUi = null;

    function _ensureErrorUi() {
      if (_errorUi) return _errorUi;
      try {
        var badge = document.createElement('button');
        badge.type = 'button';
        badge.className = 'weaver-error-badge';
        badge.title = 'Recent errors — click to view';
        badge.setAttribute('aria-expanded', 'false');
        badge.innerHTML = '⚠ <span class="weaver-error-badge-count">0</span>';
        badge.style.display = 'none';

        var panel = document.createElement('div');
        panel.className = 'weaver-error-panel';
        panel.setAttribute('role', 'dialog');
        panel.setAttribute('aria-label', 'Recent errors');
        panel.hidden = true;

        var head = document.createElement('div');
        head.className = 'weaver-error-panel-head';
        var title = document.createElement('span');
        title.textContent = '🚨 Recent errors';
        var clearBtn = document.createElement('button');
        clearBtn.type = 'button';
        clearBtn.className = 'weaver-error-btn';
        clearBtn.textContent = 'Clear';
        var closeBtn = document.createElement('button');
        closeBtn.type = 'button';
        closeBtn.className = 'weaver-error-btn';
        closeBtn.textContent = '✕';
        head.appendChild(title);
        head.appendChild(clearBtn);
        head.appendChild(closeBtn);

        var list = document.createElement('div');
        list.className = 'weaver-error-list';
        panel.appendChild(head);
        panel.appendChild(list);

        document.body.appendChild(badge);
        document.body.appendChild(panel);

        badge.addEventListener('click', function () { _toggleErrorPanel(); });
        closeBtn.addEventListener('click', function () { _toggleErrorPanel(false); });
        clearBtn.addEventListener('click', function () { _clearRecentErrors(); });

        _errorUi = { badge: badge, panel: panel, list: list, countEl: badge.querySelector('.weaver-error-badge-count') };
        return _errorUi;
      } catch (e) { return null; }
    }

    function _recordError(err, loc, key) {
      try {
        _recentErrors.push({
          key: key || '',
          name: err.name || 'Error',
          message: err.message,
          file: loc ? loc.file : '',
          line: loc ? loc.line : '',
          col: loc ? loc.col : '',
          full: loc ? loc.full : '',
          ts: new Date().toLocaleString(),
          stack: err.stack || '',
          count: key ? (_errorDedupe.hitsOf(key) || 1) : 1
        });
        if (_recentErrors.length > _MAX_RECENT) _recentErrors.shift();
        var ui = _ensureErrorUi();
        if (ui) {
          ui.countEl.textContent = String(_recentErrors.length);
          ui.badge.style.display = '';
          if (!ui.panel.hidden) _renderErrorList();
        }
      } catch (e) { /* recording must never throw */ }
    }

    function _refreshErrorCounts() {
      var ui = _errorUi;
      if (!ui) return;
      ui.countEl.textContent = String(_recentErrors.length);
      if (ui.panel.hidden) return;
      var rows = ui.list.querySelectorAll('.weaver-error-row');
      var needRebuild = false;
      for (var r = 0; r < rows.length; r++) {
        var key = rows[r].getAttribute('data-key');
        if (!key) continue;
        var hits = _errorDedupe.hitsOf(key) || 1;
        var cnt = rows[r].querySelector('.weaver-error-count');
        if (hits > 1 && !cnt) { needRebuild = true; continue; }
        if (cnt) cnt.textContent = '×' + hits;
      }
      if (needRebuild) _renderErrorList();
    }

    function _toggleErrorPanel(open) {
      var ui = _ensureErrorUi();
      if (!ui) return;
      var shouldOpen = open === undefined ? ui.panel.hidden : !!open;
      ui.panel.hidden = !shouldOpen;
      ui.badge.setAttribute('aria-expanded', shouldOpen ? 'true' : 'false');
      if (shouldOpen) _renderErrorList();
    }

    function _clearRecentErrors() {
      _recentErrors = [];
      _errorDedupe.reset(); // start fresh so an identical error can re-surface immediately
      var ui = _ensureErrorUi();
      if (!ui) return;
      ui.countEl.textContent = '0';
      ui.badge.style.display = 'none';
      _renderErrorList();
    }

    function _renderErrorList() {
      var ui = _ensureErrorUi();
      if (!ui) return;
      ui.list.innerHTML = '';
      if (!_recentErrors.length) {
        var empty = document.createElement('div');
        empty.className = 'weaver-error-empty';
        empty.textContent = 'No errors recorded yet.';
        ui.list.appendChild(empty);
        return;
      }
      for (var i = _recentErrors.length - 1; i >= 0; i--) ui.list.appendChild(_buildErrorRow(_recentErrors[i]));
    }

    function _buildErrorRow(err) {
      var row = document.createElement('div');
      row.className = 'weaver-error-row';
      if (err.key) row.setAttribute('data-key', err.key);

      var top = document.createElement('div');
      top.className = 'weaver-error-top';
      var msg = document.createElement('span');
      msg.className = 'weaver-error-msg';
      msg.textContent = (err.name ? err.name + ': ' : '') + err.message;
      var hits = err.key ? (_errorDedupe.hitsOf(err.key) || err.count || 1) : (err.count || 1);
      if (err.key && hits > 1) {
        var countEl = document.createElement('span');
        countEl.className = 'weaver-error-count';
        countEl.textContent = '×' + hits;
        countEl.title = 'Occurred ' + hits + ' times';
        msg.appendChild(countEl);
      }
      var actions = document.createElement('div');
      actions.className = 'weaver-error-actions';
      var copyBtn = document.createElement('button');
      copyBtn.type = 'button';
      copyBtn.className = 'weaver-error-btn';
      copyBtn.textContent = '⧉ Copy';
      copyBtn.title = 'Copy error details for reporting';
      top.appendChild(msg);
      top.appendChild(actions);
      actions.appendChild(copyBtn);

      var meta = document.createElement('div');
      meta.className = 'weaver-error-meta';
      meta.textContent = (err.file ? err.file + ':' + err.line + ':' + err.col + ' · ' : '') + err.ts;
      if (err.file && err.line) _wireSnippetHover(meta, err.full || err.file, err.line);

      var stackWrap = document.createElement('pre');
      stackWrap.className = 'weaver-error-stack';
      stackWrap.textContent = err.stack || 'No stack trace available.';
      stackWrap.hidden = true;

      row.appendChild(top);
      row.appendChild(meta);
      row.appendChild(stackWrap);

      copyBtn.addEventListener('click', function (e) {
        e.stopPropagation();
        var text = (err.name ? err.name + ': ' : '') + err.message + '\n' +
          (err.file ? 'at ' + err.file + ':' + err.line + ':' + err.col + '\n' : '') +
          'at ' + err.ts + '\n\n' + (err.stack || '');
        _copyText(text, copyBtn);
      });
      row.addEventListener('click', function () { stackWrap.hidden = !stackWrap.hidden; });
      return row;
    }

    function _copyText(text, btn) {
      var done = function () {
        if (!btn) return;
        var old = btn.textContent;
        btn.textContent = '✓ Copied';
        setTimeout(function () { btn.textContent = old; }, 1200);
      };
      try {
        if (navigator.clipboard && navigator.clipboard.writeText) {
          navigator.clipboard.writeText(text).then(done, function () { _legacyCopy(text); done(); });
        } else { _legacyCopy(text); done(); }
      } catch (e) { _legacyCopy(text); done(); }
    }

    function _legacyCopy(text) {
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

    // ── Source snippet tooltip ───────────────────────────────────────────
    // Hovering the file:line in an error toast (or a recent-errors row) fetches
    // the offending line's source from the Weaver API and shows it in a small
    // popover, so a stack trace points at the actual code that threw.
    var _snippetCache = {}; // key 'file:line' → snippet response
    var _snippetTipEl = null;
    var _snippetTipAnchor = null;

    function _escapeHtml(s) {
      return String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function _ensureSnippetTip() {
      if (_snippetTipEl) return _snippetTipEl;
      try {
        _snippetTipEl = document.createElement('div');
        _snippetTipEl.className = 'weaver-snippet-tip';
        _snippetTipEl.hidden = true;
        document.body.appendChild(_snippetTipEl);
        return _snippetTipEl;
      } catch (e) { return null; }
    }

    function _positionSnippetTip(anchor) {
      var tip = _ensureSnippetTip();
      if (!tip || !anchor) return;
      var rect = anchor.getBoundingClientRect();
      var pad = 10;
      var left = Math.min(rect.left, window.innerWidth - tip.offsetWidth - pad);
      var top = rect.bottom + 6;
      if (top + tip.offsetHeight > window.innerHeight - pad) top = Math.max(pad, rect.top - tip.offsetHeight - 6);
      tip.style.left = Math.max(pad, left) + 'px';
      tip.style.top = top + 'px';
    }

    function _hideSnippetTip() {
      _snippetTipAnchor = null;
      if (_snippetTipEl) _snippetTipEl.hidden = true;
    }

    function _showSnippetTip(anchor, file, line) {
      if (!file || !line) return;
      var tip = _ensureSnippetTip();
      if (!tip) return;
      _snippetTipAnchor = anchor;
      tip.hidden = false;
      tip.innerHTML = '<div class="weaver-snippet-loading">⏳ loading ' + _escapeHtml(file) + ':' + line + '…</div>';
      _positionSnippetTip(anchor);
      var key = file + ':' + line;
      var render = function (data) {
        if (_snippetTipAnchor !== anchor) return; // user moved away
        if (data && data.lines && data.lines.length) {
          var html = '';
          for (var i = 0; i < data.lines.length; i++) {
            var ln = data.lines[i];
            html += '<div class="weaver-snippet-line' + (ln.isTarget ? ' weaver-snippet-line--target' : '') + '">' +
              '<span class="weaver-snippet-num">' + ln.number + '</span>' +
              '<span class="weaver-snippet-text">' + _escapeHtml(ln.text) + '</span></div>';
          }
          tip.innerHTML = '<div class="weaver-snippet-head">' + _escapeHtml(data.path || file) + '</div>' + html;
        } else {
          tip.innerHTML = '<div class="weaver-snippet-empty">No source snippet found for ' + _escapeHtml(file) + ':' + line + '</div>';
        }
        _positionSnippetTip(anchor);
      };
      if (_snippetCache[key] !== undefined) { render(_snippetCache[key]); return; }
      fetch('/api/editor/snippet?file=' + encodeURIComponent(file) + '&line=' + line)
        .then(function (r) { return r.json(); })
        .then(function (data) { _snippetCache[key] = data; render(data); })
        .catch(function () { _snippetCache[key] = null; render(null); });
    }

    function _wireSnippetHover(el, file, line) {
      if (!el || !file || !line) return;
      el.classList.add('weaver-loc-hover');
      el.addEventListener('mouseenter', function () { _showSnippetTip(el, file, line); });
      el.addEventListener('mouseleave', _hideSnippetTip);
    }

    $provide.decorator('$exceptionHandler', ['$delegate', function ($delegate) {
      return function (exception, cause) {
        // $delegate already logs to the console — we only add the visible toast
        // here, so the error isn't printed twice.
        $delegate(exception, cause);
        _handleGlobalError(exception, cause || 'angular $exceptionHandler', true);
      };
    }]);
    window.addEventListener('error', function (event) {
      if (event && event.error) _handleGlobalError(event.error, 'window.onerror');
    });
    window.addEventListener('unhandledrejection', function (event) {
      var reason = event && event.reason;
      if (reason instanceof Error) _handleGlobalError(reason, 'unhandledrejection');
    });
  }])
  .filter('formatNumber', function () {
    return function (input) {
      if (input === null || input === undefined) return '';
      return input.toString().replace(/\B(?=(\d{3})+(?!\d))/g, ',');
    };
  })
  .controller('MainCtrl', [
    '$http', '$interval', '$window', '$scope', '$timeout',
    'KanbanMixin', 'CalendarMixin', 'IDEMixin', 'NotesMixin',
    'SettingsMixin', 'BugHostedMixin', 'TerminalMixin',
    'AgentMixin', 'FilePickerMixin', 'MeetingMixin',
    function ($http, $interval, $window, $scope, $timeout,
      KanbanMixin, CalendarMixin, IDEMixin, NotesMixin,
      SettingsMixin, BugHostedMixin, TerminalMixin,
      AgentMixin, FilePickerMixin, MeetingMixin) {

      const vm = this;

      // === Global UI State ===
      vm.faqs = [
        { question: 'How do I get started?', answer: 'To get started, simply create a new project and begin adding tasks to your Kanban board.', expanded: false },
        { question: 'Can I collaborate with others?', answer: 'Yes, you can invite team members to collaborate on projects and share Kanban boards.', expanded: false },
        { question: 'How do I export my data?', answer: 'You can export your Kanban data as JSON by clicking the export button in the settings panel.', expanded: false }
      ];

      vm.showKanban = true;
      vm.showCalendar = false;
      vm.showTodo = true;
      vm.showDoing = true;
      vm.showDone = true;
      vm.showArchived = false;
      vm.showSelfImproving = false;
      vm.isSearchResult = false;

      // === Floating-panel z-order ===
      // IDE / Notes / Meeting stack in a shared band (1000–1199) below the
      // dropdown/modal layer (options-menu 1200, popups 1300+). The last panel
      // the user clicked — or opened — sits on top, so the meeting view no
      // longer permanently hovers over the IDE.
      vm.panelZ = { notes: 1001, ide: 1010, meeting: 1020 };
      vm._panelZNext = 1030;
      vm.bringPanelToFront = function (key) {
        if (!Object.prototype.hasOwnProperty.call(vm.panelZ, key)) return;
        // Band is nearly full — rebase while preserving the current order.
        if (vm._panelZNext >= 1195) {
          var order = Object.keys(vm.panelZ).sort(function (a, b) { return vm.panelZ[a] - vm.panelZ[b]; });
          for (var i = 0; i < order.length; i++) vm.panelZ[order[i]] = 1000 + i;
          vm._panelZNext = 1000 + order.length;
        }
        vm.panelZ[key] = vm._panelZNext++;
      };

      // The panel toggles are plain ng-model checkboxes — bring a panel to the
      // front the moment it's opened.
      var _panelFlag = { ide: 'showIDE', notes: 'showNotes', meeting: 'showMeeting' };
      Object.keys(_panelFlag).forEach(function (key) {
        $scope.$watch(function () { return vm[_panelFlag[key]]; }, function (nv) {
          if (nv) vm.bringPanelToFront(key);
        });
      });

      // === Global UI Methods ===
      // Autoplay policy: audio.play() rejects with NotAllowedError unless the user
      // has interacted with the page first. Prime playback on the first gesture so
      // task-completion sounds work after that, and always swallow the rejection
      // (the sound simply doesn't play until the user has engaged with the page).
      var _audioUnlocked = false;
      function unlockAudioOnGesture() {
        if (_audioUnlocked) return;
        _audioUnlocked = true;
        try {
          var silent = new Audio('/wwwroot/gold_skulltula.mp3');
          silent.volume = 0;
          silent.muted = true;
          var sp = silent.play();
          if (sp && sp.catch) sp.catch(function () { });
        } catch (e) { /* ignore */ }
      }
      window.addEventListener('pointerdown', unlockAudioOnGesture, { once: true });
      window.addEventListener('keydown', unlockAudioOnGesture, { once: true });

      vm.playSound = function () {
        try {
          var audio = new Audio('/wwwroot/gold_skulltula.mp3');
          audio.volume = 1;
          var p = audio.play();
          if (p && p.catch) p.catch(function () { /* autoplay blocked before first user gesture */ });
        } catch (e) { /* ignore */ }
      };

      vm.showNotification = async function (message) {
        var granted = window.__weaverRequestPermission ? await window.__weaverRequestPermission() : Notification.permission === 'granted';
        if (granted) {
          if (window.__weaverNotify) {
            window.__weaverNotify('Weaver', message);
          } else if ("Notification" in window) {
            new Notification("Weaver", { body: message, icon: "/weavericon.png" });
          }
        }
        vm.showSideToast(message);
      };

      vm.sendSystemToast = function () {
        if (navigator.userAgent.indexOf('Win') !== -1) {
          vm.showNotification('Task done');
          vm.playSound();
        }
      };

      vm.showSideToast = function (message, duration = 3500) {
        const container = document.getElementById("weaver-toast-container");

        const toast = document.createElement("div");
        toast.className = "weaver-toast";

        const img = document.createElement("img");
        img.className = "toast-icon";
        img.src = "/weavericon.png";
        img.alt = "";

        const span = document.createElement("span");
        span.className = "toast-text";
        span.textContent = message;

        toast.appendChild(img);
        toast.appendChild(span);
        container.appendChild(toast);

        void toast.offsetHeight;
        toast.classList.add("show");

        setTimeout(() => {
          toast.classList.remove("show");
          toast.classList.add("hide");
          setTimeout(() => toast.remove(), 30000);
        }, duration);
      };

      // Error-styled sibling of showSideToast (red border/gradient, warning glyph) for
      // non-blocking failure notifications — mirrors _showErrorToast above.
      vm.showErrorToast = function (message, duration = 5000) {
        const container = document.getElementById("weaver-toast-container");
        if (!container) return;

        const toast = document.createElement("div");
        toast.className = "weaver-toast error";

        const glyph = document.createElement("div");
        glyph.className = "toast-glyph";
        glyph.textContent = "⚠";

        const body = document.createElement("span");
        body.className = "toast-text";
        body.textContent = message;

        toast.appendChild(glyph);
        toast.appendChild(body);
        container.appendChild(toast);

        void toast.offsetHeight;
        toast.classList.add("show");

        setTimeout(() => {
          toast.classList.remove("show");
          toast.classList.add("hide");
          setTimeout(() => toast.remove(), 30000);
        }, duration);
      };


      vm.exportKanbanData = function () {
        const data = JSON.stringify(vm.state);
        alert(data);
        return data;
      };

      // === Global Log & Fonts ===
      vm.agentActivityLog = [];
      vm.agentActivityLogLength = 0;
      vm.logFontSize = 18;
      vm.llmFontSize = 14;
      vm.planFontSize = 14;
      vm.metaPlanFontSize = 12;
      // Font sizes persist across reloads (same pattern as the meeting view's font).
      try {
        var _lf = parseInt(window.localStorage.getItem('weaver.font.log'), 10);
        if (_lf >= 6 && _lf <= 32) vm.logFontSize = _lf;
        var _llf = parseInt(window.localStorage.getItem('weaver.font.llm'), 10);
        if (_llf >= 6 && _llf <= 32) vm.llmFontSize = _llf;
        var _pf = parseInt(window.localStorage.getItem('weaver.font.plan'), 10);
        if (_pf >= 6 && _pf <= 32) vm.planFontSize = _pf;
        var _mpf = parseInt(window.localStorage.getItem('weaver.font.metaplan'), 10);
        if (_mpf >= 6 && _mpf <= 32) vm.metaPlanFontSize = _mpf;
      } catch (e) { }
      function persistFontSizes() {
        try {
          window.localStorage.setItem('weaver.font.log', String(vm.logFontSize));
          window.localStorage.setItem('weaver.font.llm', String(vm.llmFontSize));
          window.localStorage.setItem('weaver.font.plan', String(vm.planFontSize));
          window.localStorage.setItem('weaver.font.metaplan', String(vm.metaPlanFontSize));
        } catch (e) { }
        // Push to the backend config too (like the meeting view) so the sizes
        // sync across browsers/devices, not just this browser profile.
        if (vm.saveSettings) vm.saveSettings(true);
      }

      // Auto-follow for live logs: when a log is already pinned to the bottom, new
      // entries keep it scrolled down; if the user scrolled UP (reading older entries),
      // new arrivals must NOT yank the view away. Each scrollable container carries a
      // __logFollow flag updated by the capture-phase scroll listener below (undefined =
      // never scrolled up → follow). Throttled because streaming tokens arrive many
      // times per second.
      vm.scrollToBottom = function () {
        if (vm._scrollFollowPending) return;
        vm._scrollFollowPending = true;
        $timeout(function () {
          vm._scrollFollowPending = false;
          var els = document.querySelectorAll('.agent-activity-log .log-entries, .agent-streaming-tokens .streaming-tokens');
          for (var i = 0; i < els.length; i++) {
            if (els[i].__logFollow === false) continue;
            els[i].scrollTop = els[i].scrollHeight;
          }
        }, 10, false);
      };

      // Track the user's scroll intent per log container: any scroll that leaves the
      // bottom disables follow (they're reading older entries); returning to the bottom
      // re-enables it. Capture phase is required — scroll events don't bubble.
      if (!vm._logFollowListenerAttached) {
        vm._logFollowListenerAttached = true;
        document.addEventListener('scroll', function (e) {
          var t = e.target;
          if (!t || t.nodeType !== 1 || !t.classList) return;
          if (t.classList.contains('log-entries') || t.classList.contains('streaming-tokens')) {
            t.__logFollow = (t.scrollHeight - t.scrollTop - t.clientHeight) < 24;
          }
        }, true);
      }

      vm.increaseLogFont = function () { vm.logFontSize = Math.min(vm.logFontSize + 2, 32); persistFontSizes(); };
      vm.decreaseLogFont = function () { vm.logFontSize = Math.max(vm.logFontSize - 2, 6); persistFontSizes(); };
      vm.increaseLlmFont = function () { vm.llmFontSize = Math.min(vm.llmFontSize + 2, 32); persistFontSizes(); };
      vm.decreaseLlmFont = function () { vm.llmFontSize = Math.max(vm.llmFontSize - 2, 6); persistFontSizes(); };
      vm.increasePlanFont = function () { vm.planFontSize = Math.min(vm.planFontSize + 2, 32); persistFontSizes(); };
      vm.decreasePlanFont = function () { vm.planFontSize = Math.max(vm.planFontSize - 2, 6); persistFontSizes(); };
      vm.increaseMetaPlanFont = function () { vm.metaPlanFontSize = Math.min(vm.metaPlanFontSize + 2, 32); persistFontSizes(); };
      vm.decreaseMetaPlanFont = function () { vm.metaPlanFontSize = Math.max(vm.metaPlanFontSize - 2, 6); persistFontSizes(); };

      vm.addLogEntry = function (entry) {
        if (vm.agentActivityLog.length > 0) {
          var lastEntry = vm.agentActivityLog[vm.agentActivityLog.length - 1];
          if (lastEntry && ((lastEntry.type === entry.type && lastEntry.message === entry.message) || lastEntry.timestamp === entry.timestamp)) return;
        }
        vm.agentActivityLog.push(entry);
        vm.agentActivityLogLength = vm.agentActivityLog.length;
      };

      // ── Generic floating-panel auto-dodge ───────────────────────────────
      // The IDE / Notes / Meeting panels are position:fixed overlays that can
      // open on top of the Agent panel / Ask AI column (which live in the
      // right panel column) and the kanban columns. On open we check the
      // panel's rect against the live bounding boxes of those regions and
      // nudge it to the first free spot (preferring the current position so a
      // deliberate user drag is respected), always clamped to the viewport.
      vm._clampFloatingPanel = function (panel) {
        if (!panel) return;
        var vw = window.innerWidth || 1280;
        var vh = window.innerHeight || 800;
        var pw = Math.min(panel.width || 600, vw);
        var ph = Math.min(panel.height || 400, vh);
        panel.left = Math.max(0, Math.min(panel.left || 0, vw - pw));
        panel.top = Math.max(0, Math.min(panel.top || 0, vh - ph));
      };
      vm._dodgeFloatingPanel = function (panel, opts) {
        opts = opts || {};
        if (!panel) return;
        var vw = window.innerWidth || 1280;
        var vh = window.innerHeight || 800;
        var pw = Math.min(panel.width || opts.width || 600, vw);
        var ph = Math.min(panel.height || opts.height || 400, vh);
        var margin = opts.margin || 8;

        // Blockers: the panel column (Ask AI + Agent) and its panels, plus any
        // other floating panels that are currently visible (so IDE/Notes/Meeting
        // never stack invisibly on top of each other).
        var blockers = [];
        function add(el) {
          if (!el) return;
          var r = el.getBoundingClientRect();
          if (r.width > 2 && r.height > 2) blockers.push({ l: r.left - margin, t: r.top - margin, r: r.right + margin, b: r.bottom + margin });
        }
        document.querySelectorAll('.right-panel, .right-panel .panel, [data-panel-id="agent-panel"], [data-panel-id="ask-ai-panel"]').forEach(add);
        ['meeting-floating-panel', 'notes-floating-panel', 'ide-floating-panel'].forEach(function (cls) {
          if (cls !== opts.selfCls) {
            var el = document.querySelector('.' + cls);
            if (el) add(el);
          }
        });

        function hit(x, y) {
          var pr = { l: x, t: y, r: x + pw, b: y + ph };
          for (var i = 0; i < blockers.length; i++) {
            var b = blockers[i];
            if (pr.l < b.r && pr.r > b.l && pr.t < b.b && pr.b > b.t) return true;
          }
          return false;
        }
        function fit(c) {
          return { x: Math.max(0, Math.min(c.x, vw - pw)), y: Math.max(0, Math.min(c.y, vh - ph)) };
        }

        // Occupied zone = union of blocker rects (for 'right of' / 'below' moves).
        var zone = null;
        blockers.forEach(function (b) {
          if (!zone) zone = { l: b.l, t: b.t, r: b.r, b: b.b };
          else {
            zone.l = Math.min(zone.l, b.l); zone.t = Math.min(zone.t, b.t);
            zone.r = Math.max(zone.r, b.r); zone.b = Math.max(zone.b, b.b);
          }
        });
        var curX = panel.left || 0, curY = panel.top || 0;
        var candidates = [
          { x: curX, y: curY },                                  // current (respect user drag)
          zone ? { x: curX, y: zone.b + margin } : null,          // below the panel column
          zone ? { x: zone.r + margin, y: curY } : null,          // right of the panel column
          { x: vw - pw - 16, y: vh - ph - 16 },                   // bottom-right corner
          { x: 16, y: vh - ph - 16 },                             // bottom-left (over the board)
          { x: vw - pw - 16, y: 16 },                             // top-right
          { x: 16, y: 16 }                                        // top-left
        ];
        for (var i = 0; i < candidates.length; i++) {
          var c = candidates[i];
          if (!c) continue;
          var f = fit(c);
          if (!hit(f.x, f.y)) { panel.left = f.x; panel.top = f.y; return; }
        }
        vm._clampFloatingPanel(panel); // nothing free — stay on-screen
      };

      // === Initialize Mixins ===
      // Order matters: Settings/State first, then features, then Agent
      SettingsMixin.init(vm, $scope);
      KanbanMixin.init(vm, $scope);
      CalendarMixin.init(vm, $scope);
      IDEMixin.init(vm, $scope);
      NotesMixin.init(vm, $scope);
      TerminalMixin.init(vm, $scope);
      FilePickerMixin.init(vm, $scope);
      AgentMixin.init(vm, $scope);
      MeetingMixin.init(vm, $scope);
      BugHostedMixin.init(vm, $scope);

      // === Global Init Calls ===
      if (vm.emailAccounts.length === 0) vm.addEmailAccount();
      vm.loadConfig().then(function () {
        // Restore a remembered BugHosted login on reload (set by a successful
        // bughostedLogin and cleared by logout), or the legacy heartbeat checkbox.
        if (vm.bughostedUsername && vm.bughostedPassword && (vm.bughostedHeartbeatEnabled || vm.bughostedAutoLogin)) {
          vm.bughostedLogin();
        }
      });
      vm.countArchivedCards();
      vm.startCalendarProcessing();
      // Benchmark root early: the suggestion gates identify benchmark cards by their
      // project path, so vm.defaultBenchmarkRoot must be known before the Benchmarks
      // panel is ever opened (a hand-created benchmark card's suggestions are generated
      // right after its run finishes).
      if (vm.refreshBenchmarkRoot) vm.refreshBenchmarkRoot();

      // Global Keybindings
      document.addEventListener('keydown', function (event) {
        if (event.key === 'Escape' && vm.deleteCardConfirm && vm.deleteCardConfirm.show) {
          if (vm.closeSettingsPanel) vm.closeSettingsPanel();
          if (vm.closeEditProjectsPanel) vm.closeEditProjectsPanel();
          if (vm.closeFilePicker) vm.closeFilePicker();
          if (vm.closeDeleteCardConfirm) vm.closeDeleteCardConfirm();
        }
      });

      $scope.$on('$destroy', function () {
        vm.destroyed = true;
        if (vm.abortController) vm.abortController.abort();
      });
    }
  ]);