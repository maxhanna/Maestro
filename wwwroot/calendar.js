'use strict';

angular.module('kanbanApp').factory('CalendarMixin', function ($http, $window, $timeout, $interval) {
  var _calTimer = null;
  var _vm = null; // controller instance captured in init()
  var _scope = null; // scope captured in init()
  var _cronNext = {}; // card.id -> next-fire Date (or null), rebuilt in calBuildDays

  function uid() { return Math.random().toString(36).slice(2, 9); }

  // ── Cron matching (5-field: minute hour day-of-month month day-of-week) ──
  function cronMatches(expr, date) {
    try {
      var parts = expr.trim().split(/\s+/);
      if (parts.length !== 5) return false;
      function matchField(field, val) {
        if (field === '*') return true;
        if (field.indexOf('*/') === 0) {
          var interval = parseInt(field.slice(2), 10);
          return interval > 0 && val % interval === 0;
        }
        var vals = field.split(',');
        for (var i = 0; i < vals.length; i++) {
          var v = vals[i];
          if (v.indexOf('-') > 0) {
            var range = v.split('-');
            var lo = parseInt(range[0], 10);
            var hi = parseInt(range[1], 10);
            if (val >= lo && val <= hi) return true;
          } else if (parseInt(v, 10) === val) {
            return true;
          }
        }
        return false;
      }
      return matchField(parts[0], date.getMinutes()) &&
        matchField(parts[1], date.getHours()) &&
        matchField(parts[2], date.getDate()) &&
        matchField(parts[3], date.getMonth() + 1) &&
        matchField(parts[4], date.getDay());
    } catch (e) { return false; }
  }

  // ── Next-fire computation (same 5-field semantics as cronMatches) ─────
  // Returns the next Date (strictly after `fromDate`) at which the cron will
  // fire, or null when the expression is invalid / has no upcoming match.
  // Day-of-month AND day-of-week must BOTH match (matching the app's actual
  // firing behavior), so the hint always reflects when the task really runs.
  function nextCronFire(expr, fromDate) {
    try {
      var parts = expr.trim().split(/\s+/);
      if (parts.length !== 5) return null;
      function parseField(field, lo, hi) {
        var out = [];
        if (field === '*') {
          for (var i = lo; i <= hi; i++) out.push(i);
          return out;
        }
        var vals = field.split(',');
        for (var vi = 0; vi < vals.length; vi++) {
          var v = vals[vi];
          if (v.indexOf('*/') === 0) {
            var interval = parseInt(v.slice(2), 10);
            if (interval > 0) for (var i = lo; i <= hi; i++) if (i % interval === 0) out.push(i);
          } else if (v.indexOf('-') > 0) {
            var range = v.split('-');
            var rlo = parseInt(range[0], 10), rhi = parseInt(range[1], 10);
            for (var i = Math.max(lo, rlo); i <= Math.min(hi, rhi); i++) out.push(i);
          } else {
            var n = parseInt(v, 10);
            if (!isNaN(n) && n >= lo && n <= hi) out.push(n);
          }
        }
        out.sort(function (a, b) { return a - b; });
        return out;
      }
      var minutes = parseField(parts[0], 0, 59);
      var hours = parseField(parts[1], 0, 23);
      var doms = parseField(parts[2], 1, 31);
      var months = parseField(parts[3], 1, 12);
      var dows = parseField(parts[4], 0, 6);
      if (!minutes.length || !hours.length || !doms.length || !months.length || !dows.length) return null;
      var from = new Date(fromDate.getTime());
      from.setSeconds(0, 0);
      from.setMinutes(from.getMinutes() + 1); // strictly after now
      // Scan day-by-day (bounded to ~3 years) for the first day whose date fields
      // match, then the first matching hour:minute on that day at/after `from`.
      for (var day = 0; day <= 366 * 3; day++) {
        var d = new Date(from.getFullYear(), from.getMonth(), from.getDate() + day);
        var dm = d.getMonth() + 1, dd = d.getDate(), dw = d.getDay();
        if (months.indexOf(dm) === -1 || doms.indexOf(dd) === -1 || dows.indexOf(dw) === -1) continue;
        for (var hi = 0; hi < hours.length; hi++) {
          var h = hours[hi];
          for (var mi = 0; mi < minutes.length; mi++) {
            var m = minutes[mi];
            var cand = new Date(d.getFullYear(), d.getMonth(), d.getDate(), h, m, 0, 0);
            if (cand < from) continue;
            return cand;
          }
        }
      }
      return null;
    } catch (e) { return null; }
  }

  // Compact "when" label for a fire datetime: "Today 09:00", "Mon, Sep 8 09:00"...
  function formatFireDateTime(d) {
    var now = new Date();
    var sameDay = d.getFullYear() === now.getFullYear() && d.getMonth() === now.getMonth() && d.getDate() === now.getDate();
    var hm = String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
    if (sameDay) return 'Today ' + hm;
    return d.toLocaleDateString('default', { weekday: 'short', month: 'short', day: 'numeric' }) + ' ' + hm;
  }

  // ── Cron background processor ──────────────────────────────────────────
  // Every 60s: load calendar cards, fire any that are due (cron schedule
  // matched, or a one-off date/time reached), create a To Do card from the
  // task text and start it automatically when nothing else is streaming.
  function processCalendarEvents() {
    $http.get('/api/calendar/load').then(function (resp) {
      try {
        var data = resp.data;
        if (typeof data === 'string') data = JSON.parse(data);
        if (!Array.isArray(data)) return;
        var now = new Date();
        var todayStr = now.getFullYear() + '-' + String(now.getMonth() + 1).padStart(2, '0') + '-' + String(now.getDate()).padStart(2, '0');
        var currentMinutes = now.getHours() * 60 + now.getMinutes();
        var changed = false;

        for (var ci = 0; ci < data.length; ci++) {
          var cal = data[ci];
          if (!cal.date || !cal.text) continue;
          var shouldFire = false;
          if (cal.cronExpression) {
            if (cronMatches(cal.cronExpression, now)) {
              var lastFired = cal.lastFired ? new Date(cal.lastFired).getTime() : 0;
              if (now.getTime() - lastFired > 60000) shouldFire = true;
            }
          } else {
            if (cal.processed) continue;
            var calMinute = 0;
            if (cal.time) {
              var tp = cal.time.split(':');
              calMinute = parseInt(tp[0], 10) * 60 + parseInt(tp[1], 10);
            }
            if (cal.date < todayStr || (cal.date === todayStr && calMinute <= currentMinutes)) shouldFire = true;
          }

          if (shouldFire) {
            var newCard = {
              id: uid(),
              text: cal.text,
              filePath: cal.filePath || cal.project || _vm.selectedProject,
              createdAt: now.toISOString(),
              priority: cal.priority || 'medium',
              ready: true,
              attached: [],
              selfImproving: false,
              isDecomposing: false,
              // Marks the card as created by a calendar schedule so the board can
              // render a ⏰ chip on it (and keep it recognizable through Doing/Done).
              _fromCron: true,
              _cronExpression: cal.cronExpression || (cal.time ? cal.date + ' ' + cal.time : '')
            };
            _vm.state.todo.push(newCard);
            _vm.saveCards();
            changed = true;
            if (cal.cronExpression) cal.lastFired = now.toISOString(); else cal.processed = true;
            // The interval runs outside a digest — apply so the new card shows
            // even when the agent stays idle (no SSE stream to trigger a digest).
            try { if (_scope && !_scope.$$phase) _scope.$applyAsync(); } catch (e) {}
            if (!_vm.streamingActive && _vm.executeAgent) _vm.executeAgent(newCard);
          }
        }
        if (changed) $http.post('/api/calendar/save', data).catch(function () { });
      } catch (e) { console.log("processCalendarEvents error ", e); }
    }, function () { });
  }

  function stopCalendarProcessing() {
    if (_calTimer) { $interval.cancel(_calTimer); _calTimer = null; }
  }

  return {
    init: function (vm, $scope) {
      _vm = vm;
      _scope = $scope;
      // The calendar is a popup the user opens explicitly — never auto-open it
      // on load, even if a previous session persisted showCalendar=true.
      vm.showCalendar = false;
      vm.calCards = [];
      vm.calDays = [];
      vm.calYear = new Date().getFullYear();
      vm.calMonth = new Date().getMonth();
      vm.calWeekdays = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
      vm.calEditCardData = null;

      vm.calMonthName = (function () {
        var m = new Date(vm.calYear, vm.calMonth, 1);
        return m.toLocaleString('default', { month: 'long' });
      })();

      function localDateStr(date) {
        var y = date.getFullYear();
        var m = String(date.getMonth() + 1).padStart(2, '0');
        var d = String(date.getDate()).padStart(2, '0');
        return y + '-' + m + '-' + d;
      }

      function scheduleUpdate() {
        try { if (!$scope.$$phase) $scope.$applyAsync(); } catch (e) {}
      }

      function normalizeCalCard(c) {
        if (typeof c.date !== 'string' || c.date.length > 10) {
          if (c.date && typeof c.date === 'object' && typeof c.date.getFullYear === 'function') {
            c.date = localDateStr(c.date);
          } else {
            c.date = c.date ? localDateStr(new Date(c.date)) : '';
          }
        }
        if (typeof c.time !== 'string' || c.time.length > 5) {
          if (c.time && typeof c.time === 'object' && typeof c.time.getHours === 'function') {
            c.time = pad2(c.time.getHours()) + ':' + pad2(c.time.getMinutes());
          } else if (c.time && String(c.time).length > 10) {
            var t = new Date(c.time);
            c.time = pad2(t.getHours()) + ':' + pad2(t.getMinutes());
          } else {
            c.time = '';
          }
        }
        // Unify legacy "project" field onto "filePath" (what the card and
        // cron processor use).
        if (c.project && !c.filePath) c.filePath = c.project;
        return c;
      }
      function pad2(n) { return String(n).padStart(2, '0'); }

      vm.projectName = function (path) {
        if (!path) return '';
        if (vm.projects) {
          for (var pi = 0; pi < vm.projects.length; pi++) {
            var p = vm.projects[pi];
            if ((p.Path || p.path) === path) return p.Name || p.name || path;
          }
        }
        return path.split(/[\\/]/).pop() || path;
      };

      vm.loadCalendarCards = function () {
        $http.get('/api/calendar/load').then(function (resp) {
          try {
            var data = resp.data;
            if (typeof data === 'string') data = JSON.parse(data);
            if (Array.isArray(data)) {
              for (var ci = 0; ci < data.length; ci++) normalizeCalCard(data[ci]);
              vm.calCards = data;
            }
          } catch (e) {
            console.warn('Failed to parse calendar data');
          }
          vm.calBuildDays();
        }, function () {
          vm.calBuildDays();
        });
      };

      vm.saveCalendarCards = function () {
        $http.post('/api/calendar/save', vm.calCards).catch(function (err) {
          console.error('Failed to save calendar data:', err);
        });
      };

      vm.calBuildDays = function () {
        var year = vm.calYear;
        var month = vm.calMonth;
        var first = new Date(year, month, 1);
        var last = new Date(year, month + 1, 0);
        var startPad = first.getDay();
        var daysInMonth = last.getDate();
        var today = new Date();
        var todayStr = localDateStr(today);
        var days = [];
        var cards = vm.calCards;
        var project = vm.selectedProject;

        function baseName(path) {
          if (!path) return '';
          var parts = String(path).split(/[\\/]/);
          return parts[parts.length - 1] || '';
        }

        function cardsForDate(dateStr) {
          var result = [];
          var projectBase = baseName(project);
          for (var ci = 0; ci < cards.length; ci++) {
            var c = cards[ci];
            if (c.date !== dateStr) continue;
            if (!project) { result.push(c); continue; }
            // Match full path, or basename (legacy cards stored only the folder name).
            if (c.filePath === project || baseName(c.filePath) === projectBase) {
              result.push(c);
            }
          }
          return result;
        }

        function isWeekend(d) {
          var day = d.getDay();
          return day === 0 || day === 6;
        }

        // Next-fire hints: compute each cron card's next fire ONCE, bucket by fire
        // date, and reuse the map for both the day-cell hints and the card tooltips
        // (so tooltips never re-run the scan on every digest).
        var nowForCron = new Date();
        _cronNext = {};
        var firesByDate = {}; // 'YYYY-MM-DD' -> [{ card, fire }]
        for (var cn = 0; cn < cards.length; cn++) {
          var cc = cards[cn];
          if (!cc.cronExpression) continue;
          var nf = nextCronFire(cc.cronExpression, nowForCron);
          _cronNext[cc.id] = nf;
          if (nf) {
            var dsKey = localDateStr(nf);
            if (!firesByDate[dsKey]) firesByDate[dsKey] = [];
            firesByDate[dsKey].push({ card: cc, fire: nf });
          }
        }
        function nextFiresTitle(list) {
          if (!list || !list.length) return '';
          var lines = ['Next fire:'];
          for (var li = 0; li < list.length; li++) {
            lines.push(formatFireDateTime(list[li].fire) + ' — ' + String(list[li].card.text || '').slice(0, 40));
          }
          return lines.join('\n');
        }
        function makeDay(num, dateStr, inMonth, dt) {
          var nfList = firesByDate[dateStr] || [];
          return { num: num, date: dateStr, inMonth: inMonth, isToday: dateStr === todayStr, isWeekend: isWeekend(dt), cards: cardsForDate(dateStr), nextFires: nfList, nextFiresTitle: nextFiresTitle(nfList) };
        }

        var prevMonthLast = new Date(year, month, 0).getDate();
        for (var p = startPad - 1; p >= 0; p--) {
          var d = prevMonthLast - p;
          var dt = new Date(year, month - 1, d);
          days.push(makeDay(d, localDateStr(dt), false, dt));
        }

        for (var i = 1; i <= daysInMonth; i++) {
          var dt2 = new Date(year, month, i);
          days.push(makeDay(i, localDateStr(dt2), true, dt2));
        }

        var remaining = 7 - (days.length % 7);
        if (remaining < 7) {
          for (var j = 1; j <= remaining; j++) {
            var dt3 = new Date(year, month + 1, j);
            days.push(makeDay(j, localDateStr(dt3), false, dt3));
          }
        }

        vm.calDays = days;
        vm.calMonthName = first.toLocaleString('default', { month: 'long' });
        scheduleUpdate();
      };

      vm.calPrevMonth = function () {
        vm.calMonth--;
        if (vm.calMonth < 0) { vm.calMonth = 11; vm.calYear--; }
        vm.calBuildDays();
      };

      vm.calNextMonth = function () {
        vm.calMonth++;
        if (vm.calMonth > 11) { vm.calMonth = 0; vm.calYear++; }
        vm.calBuildDays();
      };

      vm.calToday = function () {
        var now = new Date();
        vm.calYear = now.getFullYear();
        vm.calMonth = now.getMonth();
        vm.calBuildDays();
      };

      // Tooltip for a cron card's ⏰ icon: the expression plus when it fires next.
      // Uses the next-fire map computed in calBuildDays (free on every digest); only
      // falls back to a live scan before the first build.
      vm.calNextFireText = function (card) {
        if (!card || !card.cronExpression) return '';
        var nf = _cronNext[card.id];
        if (nf === undefined) nf = nextCronFire(card.cronExpression, new Date());
        var base = 'Cron: ' + card.cronExpression;
        if (!nf) return base + ' · no fire within ~3 years';
        var mins = Math.round((nf.getTime() - Date.now()) / 60000);
        var rel;
        if (mins < 1) rel = 'now';
        else if (mins < 60) rel = 'in ' + mins + 'm';
        else if (mins < 1440) rel = 'in ' + Math.floor(mins / 60) + 'h ' + (mins % 60) + 'm';
        else rel = 'in ' + Math.floor(mins / 1440) + 'd ' + Math.floor((mins % 1440) / 60) + 'h';
        return base + ' · next: ' + formatFireDateTime(nf) + ' (' + rel + ')';
      };

      vm.calAddCard = function () {
        var now = new Date();
        var d = new Date();
        d.setMinutes(d.getMinutes() + 5);
        vm.calEditCardData = {
          id: null,
          date: localDateStr(now),
          time: pad2(d.getHours()) + ':' + pad2(d.getMinutes()),
          text: '',
          priority: 'medium',
          cronExpression: '',
          filePath: vm.selectedProject || ''
        };
        scheduleUpdate();
      };

      vm.setCronExpression = function (expr) {
        if (vm.calEditCardData) {
          vm.calEditCardData.cronExpression = expr;
        }
      };

      vm.calEditCard = function (card, $event) {
        if ($event && $event.target.classList.contains('cal-card-del')) return;
        try {
          vm.calEditCardData = JSON.parse(JSON.stringify(card));
        } catch (e) {
          vm.calEditCardData = angular.copy(card);
        }
        scheduleUpdate();
      };

      vm.calCloseEdit = function (event) {
        if (event) event.stopPropagation();
        vm.calEditCardData = null;
        scheduleUpdate();
      };

      vm.calSaveCard = function () {
        try {
          var data = vm.calEditCardData;
          if (!data || !data.text || !data.date) return;

          var saved = normalizeCalCard(JSON.parse(JSON.stringify(data)));
          if (saved.id) {
            var idx = -1;
            for (var ci = 0; ci < vm.calCards.length; ci++) {
              if (vm.calCards[ci].id === saved.id) { idx = ci; break; }
            }
            if (idx !== -1) {
              vm.calCards[idx] = saved;
            }
          } else {
            saved.id = uid();
            saved.createdAt = new Date().toISOString();
            vm.calCards.push(saved);
          }
          vm.calEditCardData = null;
          vm.saveCalendarCards();
          vm.calBuildDays();
        } catch (e) {
          console.error('Error saving calendar card:', e);
        }
        scheduleUpdate();
      };

      // ── "Run once now" — fire the card immediately ─────────────────────
      // Mirrors what the cron processor does on schedule: persist the calendar
      // entry, then push a To Do card (marked _fromCron) and start it if the
      // agent is idle. Lets users test a schedule without waiting for it.
      vm.calRunNow = function () {
        try {
          var data = vm.calEditCardData;
          if (!data || !data.text) return $window.alert('Enter a task first');
          if (!data.date) return $window.alert('A date is required');
          // Persist first so an unsaved (new) card isn't lost when we close the editor.
          vm.calSaveCard();
          var now = new Date();
          var newCard = {
            id: uid(),
            text: data.text,
            filePath: data.filePath || data.project || _vm.selectedProject,
            createdAt: now.toISOString(),
            priority: data.priority || 'medium',
            ready: true,
            attached: [],
            selfImproving: false,
            isDecomposing: false,
            _fromCron: true,
            _cronExpression: data.cronExpression || (data.time ? data.date + ' ' + data.time : '')
          };
          if (!_vm.state.todo) _vm.state.todo = [];
          _vm.state.todo.push(newCard);
          _vm.saveCards();
          // The interval runs outside a digest — apply so the card shows immediately.
          try { if (_scope && !_scope.$$phase) _scope.$applyAsync(); } catch (e) {}
          if (_vm.showSideToast) _vm.showSideToast('⏰ Calendar card fired now — added to To Do' + (_vm.streamingActive ? ' (queued)' : ' and started'));
          if (!_vm.streamingActive && _vm.executeAgent) _vm.executeAgent(newCard);
        } catch (e) {
          console.error('Error running calendar card now:', e);
        }
      };

      vm.calDeleteCard = function (card, $event) {
        try {
          if ($event) $event.stopPropagation();
          if (!$window.confirm('Delete this calendar card?')) return;
          var id = card.id || (vm.calEditCardData && vm.calEditCardData.id);
          if (!id) return;
          var filtered = [];
          for (var ci = 0; ci < vm.calCards.length; ci++) {
            if (vm.calCards[ci].id !== id) filtered.push(vm.calCards[ci]);
          }
          vm.calCards = filtered;
          vm.calEditCardData = null;
          vm.saveCalendarCards();
          vm.calBuildDays();
        } catch (e) {
          console.error('Error deleting calendar card:', e);
        }
        scheduleUpdate();
      };

      // ── Popup panel open/close ─────────────────────────────────────────
      vm.openCalendarPanel = function () {
        if (vm.showCalendar) return;
        vm.showCalendar = true;
        vm.loadCalendarCards();
        scheduleUpdate();
      };

      vm.closeCalendarPanel = function () {
        vm.showCalendar = false;
        if (vm.saveSettings) vm.saveSettings(true);
        scheduleUpdate();
      };

      // ── Cron processing lifecycle (started by app.js) ──────────────────
      vm.startCalendarProcessing = function () {
        stopCalendarProcessing();
        _calTimer = $interval(processCalendarEvents, 60000, 0, false);
        processCalendarEvents();
      };

      $scope.$on('$destroy', stopCalendarProcessing);

      vm.loadCalendarCards();
    }
  };
});
