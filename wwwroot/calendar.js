'use strict';

angular.module('kanbanApp').factory('CalendarMixin', function ($http, $window, $timeout, $interval) {
  var _calTimer = null;
  var _vm = null; // controller instance captured in init()
  var _scope = null; // scope captured in init()
  var _cronNext = {}; // card.id -> next-fire Date (or null), rebuilt in calBuildDays

  function uid() { return Math.random().toString(36).slice(2, 9); }

  // ── Cron run log ──────────────────────────────────────────────────────
  // Lightweight audit trail for scheduled jobs. Each calendar card keeps a
  // small capped list of its runs (fire time, outcome, duration) so one-shot
  // cron jobs leave a trace even though the board card is deleted when the
  // job completes. Stored in board data (vm.state._cronRunLog) so it survives
  // reloads; the log is cleared when the calendar card itself is deleted.
  function cronRunLogEnsure(vm) {
    if (!vm.state) vm.state = {};
    if (!Array.isArray(vm.state._cronRunLog)) vm.state._cronRunLog = [];
    return vm.state._cronRunLog;
  }
  // Keys a run-log entry back to the calendar card it came from. Board cards
  // created by the scheduler carry _cronSourceId (the calendar card id); when
  // that's missing (older cards) fall back to matching task text + schedule.
  function cronRunLogKey(calCard) {
    if (!calCard) return '';
    if (calCard.id) return 'id:' + calCard.id;
    return 'text:' + String(calCard.text || '').trim() + '|' + String(calCard.cronExpression || calCard.time || '').trim();
  }
  // Records a run entry. `outcome` is one of 'ran', 'success', 'error',
  // 'stopped' or 'skipped'; durationMs is the run length (0 for fire/skip).
  function cronRunLogAdd(vm, key, entry) {
    if (!key) return;
    try {
      var log = cronRunLogEnsure(vm);
      var base = {
        key: key,
        firedAt: entry.firedAt || new Date().toISOString(),
        outcome: entry.outcome || 'ran',
        durationMs: entry.durationMs || 0,
        summary: entry.summary || '',
        cardId: entry.cardId || ''
      };
      log.unshift(base);
      if (log.length > 100) log.length = 100;
      if (vm.saveCards) vm.saveCards();
    } catch (e) { console.log('cronRunLogAdd error', e); }
  }

  // Keep one card per id — a stale save or double-delivered push can persist two
  // copies of a calendar card, and duplicate ids inside a day's cards crash the
  // calendar's ng-repeat (ngRepeat:dupes), same as the kanban board.
  function dedupeById(arr) {
    var seen = {};
    return arr.filter(function (c) {
      if (!c || c.id == null) return true;
      if (seen[c.id]) return false;
      seen[c.id] = true;
      return true;
    });
  }

  // ── Live-instance idempotency guard ───────────────────────────────────
  // A scheduled (cron) calendar card re-fires on every matching window, and each
  // fire pushes a FRESH To Do card (new uid) and starts it. So stopping a running
  // calendar card does NOT stop the schedule — the next window spawns a look-alike
  // duplicate (new id, same text) and auto-starts it, which reads exactly like
  // "I pressed stop and it duplicated and started again". Before pushing a new
  // card, check whether the same calendar card (same text + same schedule key)
  // already has a live instance in To Do or Doing; if so, the fire is suppressed
  // and the schedule's lastFired/processed is advanced so it doesn't retry every
  // tick. The next card is only created once the current instance leaves the
  // board (Done/archive/delete).
  function hasLiveCalendarInstance(boardState, cal) {
    if (!boardState || !cal || !cal.text) return false;
    var textKey = String(cal.text).trim();
    var cronKey = cal.cronExpression || '';
    var cols = ['todo', 'doing'];
    for (var c = 0; c < cols.length; c++) {
      var cards = boardState[cols[c]] || [];
      for (var i = 0; i < cards.length; i++) {
        var b = cards[i];
        if (!b || !b._fromCron) continue;
        if (cronKey) {
          // Cron/dated cards: match on schedule key AND text so two different
          // tasks on the same schedule never block each other.
          if ((b._cronExpression || '') === cronKey && (b.text || '').trim() === textKey) return true;
        } else if ((b.text || '').trim() === textKey) {
          // Date-only one-off: match on text.
          return true;
        }
      }
    }
    return false;
  }

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
        // Heal duplicates before firing — two copies of the same cron card would
        // double-create To Do cards (and the save below would persist both).
        data = dedupeById(data);
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
            // Idempotency guard — never push a second card for a calendar card
            // that already has a live instance on the board. Stops the
            // "I pressed stop and it duplicated and started again" loop: the
            // schedule keeps firing, but while the stopped card still sits in
            // To Do/Doing no new card is created or auto-started.
            if (hasLiveCalendarInstance(_vm.state, cal)) {
              // Suppress this fire — the same calendar card already has a live
              // card on the board, so firing again would duplicate it.
              console.log('[calendar] suppressed fire for "' + (cal.text || '').slice(0, 40) + '" — a live instance is already on the board; the schedule resumes once it leaves To Do/Doing');
              // Audit trail: note the suppressed fire so skipped runs show up
              // in the card's run history instead of silently vanishing.
              cronRunLogAdd(_vm, cronRunLogKey(cal), { outcome: 'skipped', summary: 'Schedule fired while a live card was still on the board — no duplicate created.' });
              if (cal.cronExpression) cal.lastFired = now.toISOString(); else cal.processed = true;
              changed = true;
              continue;
            }
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
              _cronExpression: cal.cronExpression || (cal.time ? cal.date + ' ' + cal.time : ''),
              _cronLabel: cal.label || '',
              // Link the board card back to the calendar card it came from so
              // the cron run log can attribute outcomes to the right schedule.
              _cronSourceId: cal.id
            };
            _vm.state.todo.push(newCard);
            _vm.saveCards();
            changed = true;
            // Audit trail: this fire produced a board card — record it now (the
            // outcome/duration get filled in when the card finishes or is deleted).
            cronRunLogAdd(_vm, cronRunLogKey(cal), { outcome: 'ran', cardId: newCard.id, summary: 'Fired on schedule — card pushed to To Do.' });
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
      // Default to closed; loadConfig restores the persisted showCalendar value
      // (saved by open/close) once the config arrives, so the calendar reopens
      // on reload exactly as the user left it.
      vm.showCalendar = false;
      vm.calCards = [];
      vm.calDays = [];
      vm.calYear = new Date().getFullYear();
      vm.calMonth = new Date().getMonth();
      vm.calWeekdays = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
      vm.calEditCardData = null;
      vm.calShowHistory = null; // calendar card id whose run-history popup is open

      // ── Cron run log (audit trail for scheduled jobs) ───────────────────
      // Entries live in board data (vm.state._cronRunLog) so they survive
      // reloads; recorded when a job fires, when it finishes/deletes, and when
      // a fire is suppressed because a live instance is already on the board.
      vm.cronRunLogFor = function (card) {
        var key = cronRunLogKey(card);
        if (!key) return [];
        return (cronRunLogEnsure(vm) || []).filter(function (e) { return e && e.key === key; });
      };
      vm.cronRunCount = function (card) { return vm.cronRunLogFor(card).length; };
      vm.cronRunOutcomeClass = function (e) {
        if (!e || !e.outcome) return 'cron-run--ran';
        return 'cron-run--' + String(e.outcome);
      };
      vm.cronRunDuration = function (e) {
        if (!e || !e.durationMs) return '';
        var s = Math.round(e.durationMs / 1000);
        if (s < 60) return s + 's';
        return Math.floor(s / 60) + 'm ' + (s % 60) + 's';
      };
      vm.cronRunLabel = function (e) {
        if (!e) return '';
        if (e.outcome === 'success') return '✅ Done';
        if (e.outcome === 'error') return '❌ Error';
        if (e.outcome === 'stopped') return '⏹ Stopped';
        if (e.outcome === 'skipped') return '⏭ Skipped (already running)';
        return '▶ Fired';
      };
      vm.calOpenHistory = function (card, $event) {
        if ($event) $event.stopPropagation();
        vm.calShowHistory = card ? card.id : null;
        scheduleUpdate();
      };
      vm.calHistoryCard = function () {
        var id = vm.calShowHistory;
        if (!id) return null;
        for (var ci = 0; ci < vm.calCards.length; ci++) {
          if (vm.calCards[ci] && vm.calCards[ci].id === id) return vm.calCards[ci];
        }
        return null;
      };
      vm.calCloseHistory = function (event) {
        if (event) event.stopPropagation();
        vm.calShowHistory = null;
        scheduleUpdate();
      };
      vm.calClearHistory = function (card, $event) {
        if ($event) $event.stopPropagation();
        if (!card || !card.id) return;
        if (!$window.confirm('Clear the run history for this scheduled card?')) return;
        var key = cronRunLogKey(card);
        if (!key) return;
        cronRunLogEnsure(vm);
        vm.state._cronRunLog = (vm.state._cronRunLog || []).filter(function (e) { return e && e.key !== key; });
        if (vm.saveCards) vm.saveCards();
      };

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
      // The add/edit form binds native date/time pickers, which require Date
      // objects — while the calendar's persisted model uses "YYYY-MM-DD" and
      // "HH:MM" strings. These bridge the two using LOCAL components only, so
      // no timezone can shift the day or hour.
      function dateToLocal(date) {
        if (!date) return null;
        if (date instanceof Date) return new Date(date.getFullYear(), date.getMonth(), date.getDate());
        var m = String(date).match(/^(\d{4,})-(\d{2})-(\d{2})/);
        if (m) return new Date(parseInt(m[1], 10), parseInt(m[2], 10) - 1, parseInt(m[3], 10));
        var p = new Date(date);
        return isNaN(p.getTime()) ? null : new Date(p.getFullYear(), p.getMonth(), p.getDate());
      }
      function timeToDate(time) {
        if (!time) return null;
        if (time instanceof Date) return new Date(2000, 0, 1, time.getHours(), time.getMinutes());
        var m = String(time).match(/^(\d{1,2}):(\d{2})/);
        return m ? new Date(2000, 0, 1, parseInt(m[1], 10), parseInt(m[2], 10)) : null;
      }
      function timeStr(d) { return d ? pad2(d.getHours()) + ':' + pad2(d.getMinutes()) : ''; }

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
              // Heal duplicate ids so a persisted double-delivery can never crash
              // the calendar's ng-repeat. In-memory only; the next save persists.
              vm.calCards = dedupeById(data);
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
          // Last-resort render guard: never hand the ng-repeat a duplicate id.
          return dedupeById(result);
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

      // Shared relative-countdown label for a fire datetime (minutes from now),
      // used by both the card tooltip and the live cron-input preview.
      function fireCountdownText(mins) {
        if (mins < 1) return 'now';
        if (mins < 60) return 'in ' + mins + 'm';
        if (mins < 1440) return 'in ' + Math.floor(mins / 60) + 'h ' + (mins % 60) + 'm';
        return 'in ' + Math.floor(mins / 1440) + 'd ' + Math.floor((mins % 1440) / 60) + 'h';
      }

      // Tooltip for a cron card's ⏰ icon: the expression plus when it fires next.
      // Uses the next-fire map computed in calBuildDays (free on every digest); only
      // falls back to a live scan before the first build.
      vm.calNextFireText = function (card) {
        if (!card || !card.cronExpression) return '';
        var nf = _cronNext[card.id];
        if (nf === undefined) nf = nextCronFire(card.cronExpression, new Date());
        var base = (card.label ? card.label + ' · ' : '') + 'Cron: ' + card.cronExpression;
        if (!nf) return base + ' · no fire within ~3 years';
        var rel = fireCountdownText(Math.round((nf.getTime() - Date.now()) / 60000));
        return base + ' · next: ' + formatFireDateTime(nf) + ' (' + rel + ')';
      };

      // Live preview under the cron input in the add/edit popup: resolves the
      // expression the user is typing with nextCronFire and shows when it will
      // fire next. Memoized on expr+minute so repeated digests are free (the
      // template calls it twice — text and class — the second hits the cache).
      var _cronPreviewCache = { key: null, out: { text: '', cls: '' } };
      vm.calCronPreview = function () {
        var d = vm.calEditCardData;
        if (!d) return _cronPreviewCache.out;
        var expr = (d.cronExpression || '').trim();
        var now = new Date();
        var minuteKey = now.getFullYear() + '-' + now.getMonth() + '-' + now.getDate() + '-' + now.getHours() + '-' + now.getMinutes();
        var dateStr = d.date instanceof Date ? localDateStr(d.date) : (d.date || '');
        var timeV = d.time instanceof Date ? timeStr(d.time) : (d.time || '00:00');
        var key = expr + '|' + dateStr + '|' + timeV + '|' + minuteKey;
        if (_cronPreviewCache.key === key) return _cronPreviewCache.out;
        var out;
        if (!expr) {
          // Date-only / date+time cards fire once (no recurring schedule). The
          // one-off moment is the date at its time, or midnight when no time.
          var oneOff = null;
          if (dateStr) {
            var fd = new Date(dateStr + 'T' + timeV);
            if (!isNaN(fd.getTime())) oneOff = fd;
          }
          if (oneOff && oneOff.getTime() <= now.getTime()) {
            out = { text: 'Fires once: ' + formatFireDateTime(oneOff) + ' — already due', cls: 'cron-preview--wait' };
          } else if (oneOff) {
            out = { text: 'Fires once: ' + formatFireDateTime(oneOff) + ' — ' + fireCountdownText(Math.round((oneOff.getTime() - now.getTime()) / 60000)), cls: 'cron-preview--valid' };
          } else {
            out = { text: 'No schedule — fires once on its date', cls: 'cron-preview--none' };
          }
        } else {
          var parts = expr.split(/\s+/);
          var nf = parts.length === 5 ? nextCronFire(expr, now) : null;
          if (parts.length !== 5) {
            out = { text: 'Invalid cron — needs 5 fields: minute hour day-of-month month weekday', cls: 'cron-preview--invalid' };
          } else if (!nf) {
            out = {
              text: 'Valid, but no fire within ~3 years — check field ranges',
              hint: 'Ranges: minute 0-59 · hour 0-23 · day-of-month 1-31 · month 1-12 · weekday 0-6',
              cls: 'cron-preview--wait'
            };
          } else {
            out = { text: 'Next fire: ' + formatFireDateTime(nf) + ' — ' + fireCountdownText(Math.round((nf.getTime() - Date.now()) / 60000)), cls: 'cron-preview--valid' };
          }
        }
        _cronPreviewCache = { key: key, out: out };
        return out;
      };

      vm.calAddCard = function () {
        var now = new Date();
        var d = new Date();
        d.setMinutes(d.getMinutes() + 5);
        vm.calEditCardData = {
          id: null,
          date: new Date(now.getFullYear(), now.getMonth(), now.getDate()),
          time: d,
          text: '',
          priority: 'medium',
          cronExpression: '',
          label: '',
          filePath: vm.selectedProject || ''
        };
        scheduleUpdate();
      };

      // "Today" quick-fill next to the date picker: resets the form's date to
      // the current local day (the form holds Date objects for the native input).
      vm.calSetDateToday = function () {
        if (!vm.calEditCardData) return;
        var n = new Date();
        vm.calEditCardData.date = new Date(n.getFullYear(), n.getMonth(), n.getDate());
        scheduleUpdate();
      };

      // ✕ on the time field: clears it so a card becomes date-only (fires at
      // midnight). null renders empty in the native input and normalizes to ''
      // on save; the cron preview falls back to 00:00.
      vm.calClearTime = function () {
        if (!vm.calEditCardData) return;
        vm.calEditCardData.time = null;
        scheduleUpdate();
      };

      // Extracts "HH:MM" from a cron preset's first two fields, or null when
      // they aren't plain numbers (e.g. */15) — interval presets stay recurring.
      function cronPresetTime(expr) {
        try {
          var parts = expr.trim().split(/\s+/);
          if (parts.length !== 5) return null;
          var m = parseInt(parts[0], 10), h = parseInt(parts[1], 10);
          if (isNaN(m) || isNaN(h)) return null;
          if (m < 0 || m > 59 || h < 0 || h > 23) return null;
          return String(h).padStart(2, '0') + ':' + String(m).padStart(2, '0');
        } catch (e) { return null; }
      }

      vm.setCronExpression = function (expr) {
        if (!vm.calEditCardData) return;
        var d = vm.calEditCardData;
        if (!expr) {
          // "No schedule" → plain one-off card (fires once on its date).
          d.cronExpression = '';
          return;
        }
        var hm = cronPresetTime(expr);
        if (hm && d.date && !d.cronExpression) {
          // The card already has a date and no recurring cron: apply the
          // preset's time as a one-off fire on that date instead of installing
          // a recurring schedule. Clear the Date field to make it recurring.
          d.cronExpression = '';
          d.time = timeToDate(hm);
          var dStr = d.date instanceof Date ? localDateStr(d.date) : d.date;
          if (_vm.showSideToast) _vm.showSideToast('⏰ One-off: fires once at ' + hm + ' on ' + dStr + ' — clear the Date field to make it recurring');
        } else {
          d.cronExpression = expr;
        }
      };

      vm.calEditCard = function (card, $event) {
        if ($event && $event.target.classList.contains('cal-card-del')) return;
        try {
          vm.calEditCardData = JSON.parse(JSON.stringify(card));
        } catch (e) {
          vm.calEditCardData = angular.copy(card);
        }
        // The form's native date/time pickers require Date objects — convert
        // the stored "YYYY-MM-DD"/"HH:MM" strings (calSaveCard converts back).
        // Read from the ORIGINAL card: the JSON round-trip would have turned a
        // Date into a UTC ISO string, shifting the day for positive offsets.
        var ce = vm.calEditCardData;
        if (ce) {
          ce.date = dateToLocal(card && card.date !== undefined ? card.date : ce.date);
          ce.time = timeToDate(card && card.time !== undefined ? card.time : ce.time);
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

          // The form holds Date objects for the native pickers; JSON.stringify
          // would turn them into UTC ISO strings and shift the day/time. Convert
          // to the calendar's string model FIRST, then round-trip. (This also
          // fixes calRunNow, which reads data.date/time after calSaveCard.)
          if (data.date instanceof Date) data.date = localDateStr(data.date);
          if (data.time instanceof Date) data.time = timeStr(data.time);
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
            _cronExpression: data.cronExpression || (data.time ? (data.date instanceof Date ? localDateStr(data.date) : data.date) + ' ' + (data.time instanceof Date ? timeStr(data.time) : data.time) : ''),
            _cronLabel: data.label || '',
            _cronSourceId: data.id
          };
          if (!_vm.state.todo) _vm.state.todo = [];
          _vm.state.todo.push(newCard);
          _vm.saveCards();
          // Audit trail: 'Run now' is a manual fire — record it so the calendar
          // card's history shows when it was fired by hand vs on schedule.
          cronRunLogAdd(_vm, cronRunLogKey(data), { outcome: 'ran', cardId: newCard.id, summary: 'Fired manually (Run now) — card pushed to To Do.' });
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
        // Persist the open state so the calendar comes back open after a reload
        // (closeCalendarPanel already saves on close).
        if (vm.saveSettings) vm.saveSettings(true);
        scheduleUpdate();
      };

      vm.closeCalendarPanel = function () {
        vm.showCalendar = false;
        if (vm.saveSettings) vm.saveSettings(true);
        scheduleUpdate();
      };

      // Options-menu checkbox toggle. ng-change runs AFTER the model has already
      // flipped, so branch on the NEW state: a now-checked box means open (the
      // panel shows via ng-if; refresh cards since openCalendarPanel early-returns
      // once showCalendar is already true), a now-unchecked box means close.
      vm.toggleCalendarPanel = function () {
        if (vm.showCalendar) {
          if (vm.loadCalendarCards) vm.loadCalendarCards();
          // ng-model already flipped the checkbox — persist the new open state.
          if (vm.saveSettings) vm.saveSettings(true);
        } else {
          if (vm.closeCalendarPanel) vm.closeCalendarPanel();
        }
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
