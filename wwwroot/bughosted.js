// bughosted.mixin.js
angular.module('kanbanApp')
    .factory('BugHostedMixin', ['$http', '$interval', '$timeout', '$window', function ($http, $interval, $timeout, $window) {
        var _bhHeartbeatFailCount = 0, _bhHeartbeatTimer = null, _bhEditorSyncTimer = null, _bhEventSource = null, _bhCommandTimer = null, _bhTimerRunning = false, _lastSyncedEditorState = null, _isSyncingData = false;
        var BH_AUTOLOGIN_KEY = 'weaver.bughosted.autoLogin';

        // "Remember me" marker: set when a login succeeds, cleared on logout. It lives in
        // localStorage so a page reload restores the session automatically (the app
        // auto-logs-in with the saved credentials instead of showing the rank name until
        // the user re-enters them).
        function readAutoLoginFlag() {
            try {
                var raw = $window.localStorage.getItem(BH_AUTOLOGIN_KEY);
                if (!raw) return false;
                var s = JSON.parse(raw);
                return !!(s && s.enabled);
            } catch (e) { return false; }
        }
        function writeAutoLoginFlag(enabled) {
            try { $window.localStorage.setItem(BH_AUTOLOGIN_KEY, JSON.stringify({ enabled: enabled ? true : false, savedAt: Date.now() })); } catch (e) { }
        }

        function uid() { return Math.random().toString(36).slice(2, 9); }
        // Idempotent remote card delivery: a command can arrive twice (SSE + polling
        // fallback, or a re-delivery after a failed ack). If a card with this id
        // already exists ANYWHERE on the board (a re-delivered executeTask may have
        // already moved it to doing/done), update it in place instead of pushing a
        // duplicate — same-id cards in different columns corrupt findCardById and
        // same-column dupes crash ng-repeat (ngRepeat:dupes).
        function upsertRemoteCard(vm, card) {
            var col = findCardColumn(vm, card.id);
            if (col) {
                var existing = (vm.state[col] || []).find(function (c) { return c.id === card.id; });
                if (existing) {
                    if (card.text) existing.text = card.text;
                    if (card.priority !== undefined && card.priority !== null) existing.priority = card.priority;
                    if (card.attached !== undefined) existing.attached = card.attached;
                }
                return;
            }
            vm.state.todo.push(card);
        }
        function findCardColumn(vm, cardId) {
            if (!cardId || !vm.state) return null;
            var cols = ['todo', 'doing', 'done', 'archived', 'selfImproving'];
            for (var i = 0; i < cols.length; i++) {
                var cards = vm.state[cols[i]] || [];
                for (var j = 0; j < cards.length; j++) { if (cards[j].id === cardId) return cols[i]; }
            }
            return null;
        }

        return {
            init: function (vm, $scope) {
                vm.bughostedUsername = ''; vm.bughostedPassword = ''; vm.bughostedHeartbeatEnabled = false;
                vm.bughostedShareRank = false;
                vm.bughostedShareSkeleton = false;
                vm.bughostedClientId = ''; vm.bughostedStatus = 'disconnected'; vm.bughostedTesting = false;
                vm.bughostedTestResult = ''; vm.bughostedTestError = ''; vm.remoteCommands = [];
                vm.bughostedAutoLogin = readAutoLoginFlag();

                // Rank is only sent to the bughosted server when the user opts in
                // (vm.bughostedShareRank). Off = zeros, so nothing leaks by default.
                function collectRankPayload() {
                    var out = { userScore: 0, rankTitle: '' };
                    if (!vm.bughostedShareRank) return out;
                    try {
                        if (typeof vm.userStats !== 'function' || typeof vm.userRankProgress !== 'function') return out;
                        var stats = vm.userStats();
                        var prog = vm.userRankProgress(stats);
                        if (prog && prog.score) out.userScore = Math.round(prog.score);
                        if (typeof vm.userRankTitle === 'function') out.rankTitle = vm.userRankTitle(stats) || '';
                    } catch (e) { }
                    return out;
                }
                function capRemoteString(s, n) {
                    if (typeof s === 'string' && s.length > n) return s.slice(0, n) + '…';
                    return s;
                }
                function slimCardForRemote(card) {
                    if (!card || typeof card !== 'object') return card;
                    var c = {};
                    for (var k in card) { if (Object.prototype.hasOwnProperty.call(card, k)) c[k] = card[k]; }
                    delete c._meetingReplay;
                    delete c._appliedDiffs;
                    delete c.confirmedContextFiles;
                    delete c._cohesion;
                    if (Array.isArray(c.agentLog)) {
                        c.agentLog = c.agentLog.slice(-15).map(function (e) {
                            if (!e || typeof e !== 'object') return e;
                            var o = {};
                            for (var k2 in e) { if (Object.prototype.hasOwnProperty.call(e, k2)) o[k2] = e[k2]; }
                            o.detail = capRemoteString(o.detail, 2000);
                            o.message = capRemoteString(o.message, 2000);
                            return o;
                        });
                    }
                    if (c.agentAnalysis && typeof c.agentAnalysis === 'object') {
                        var a = {};
                        for (var k3 in c.agentAnalysis) { if (Object.prototype.hasOwnProperty.call(c.agentAnalysis, k3)) a[k3] = c.agentAnalysis[k3]; }
                        a.thinking = capRemoteString(a.thinking, 15000);
                        a.summary = capRemoteString(a.summary, 15000);
                        a.question = capRemoteString(a.question, 15000);
                        if (Array.isArray(a.steps)) {
                            a.steps = a.steps.slice(-20).map(function (s) {
                                if (!s || typeof s !== 'object') return s;
                                var o = {};
                                for (var k4 in s) { if (Object.prototype.hasOwnProperty.call(s, k4)) o[k4] = s[k4]; }
                                o.output = capRemoteString(o.output, 2000);
                                return o;
                            });
                        }
                        c.agentAnalysis = a;
                    }
                    return c;
                }
                function slimStateForRemote(state) {
                    var out = {};
                    for (var k5 in state) { if (Object.prototype.hasOwnProperty.call(state, k5)) out[k5] = state[k5]; }
                    ['todo', 'doing', 'done', 'archived', 'selfImproving'].forEach(function (col) {
                        if (Array.isArray(out[col])) out[col] = out[col].map(slimCardForRemote);
                    });
                    return out;
                }
                // Cap a handful of named string fields on an object in place.
                function capFields(o, limits) {
                    if (!o || typeof o !== 'object') return o;
                    for (var k in limits) {
                        if (Object.prototype.hasOwnProperty.call(o, k)) o[k] = capRemoteString(o[k], limits[k]);
                    }
                    return o;
                }
                // Deep-cap every string nested in an object/array tree (in place).
                function capAllStrings(o, n) {
                    if (!o || typeof o !== 'object') return;
                    for (var k in o) {
                        if (!Object.prototype.hasOwnProperty.call(o, k)) continue;
                        var v = o[k];
                        if (typeof v === 'string') o[k] = capRemoteString(v, n);
                        else if (Array.isArray(v)) { for (var i = 0; i < v.length; i++) capAllStrings(v[i], n); }
                        else if (v && typeof v === 'object') capAllStrings(v, n);
                    }
                }
                // Run a callback over every card in the state columns.
                function walkStateColumns(kanban, fn) {
                    var st = kanban && kanban.state;
                    if (!st || typeof st !== 'object') return;
                    ['todo', 'doing', 'done', 'archived', 'selfImproving'].forEach(function (col) {
                        if (Array.isArray(st[col])) st[col].forEach(fn);
                    });
                }
                // Keep the kanban payload under the server's storage budget by
                // trimming progressively — the cheapest cuts first. Normal boards
                // (under budget) are sent exactly as slimStateForRemote produced
                // them; only oversized ones get the deeper passes.
                function capKanbanToBudget(kanban, budget) {
                    if (!kanban || typeof kanban !== 'object') return kanban;
                    var len = function () { return JSON.stringify(kanban).length; };
                    if (len() <= budget) return kanban;

                    // Pass 1: transient streaming blobs + calendar cards (the
                    // remote dashboard never renders these in full).
                    kanban.agentThinking = capRemoteString(kanban.agentThinking, 5000);
                    kanban.agentSummary = capRemoteString(kanban.agentSummary, 5000);
                    kanban.activeCardText = capRemoteString(kanban.activeCardText, 5000);
                    if (Array.isArray(kanban.calendarCards)) {
                        kanban.calendarCards = kanban.calendarCards.slice(0, 100).map(function (cc) {
                            if (!cc || typeof cc !== 'object') return cc;
                            var o = {};
                            for (var k in cc) { if (Object.prototype.hasOwnProperty.call(cc, k)) o[k] = capRemoteString(cc[k], 500); }
                            return o;
                        });
                    }
                    if (len() <= budget) return kanban;

                    // Pass 2: shrink per-card agent ephemera harder than the
                    // base slim already did.
                    walkStateColumns(kanban, function (card) {
                        if (!card || typeof card !== 'object') return;
                        if (Array.isArray(card.agentLog)) {
                            card.agentLog = card.agentLog.slice(-8).map(function (e) {
                                return e && typeof e === 'object' ? capFields(e, { detail: 1000, message: 1000 }) : e;
                            });
                        }
                        if (card.agentAnalysis && typeof card.agentAnalysis === 'object') {
                            card.agentAnalysis = capFields(card.agentAnalysis, { thinking: 4000, summary: 4000, question: 4000 });
                            if (Array.isArray(card.agentAnalysis.steps)) {
                                card.agentAnalysis.steps = card.agentAnalysis.steps.slice(-10).map(function (s) {
                                    return s && typeof s === 'object' ? capFields(s, { output: 1000 }) : s;
                                });
                            }
                        }
                    });
                    if (len() <= budget) return kanban;

                    // Pass 3 (last resort): cap every remaining card string so
                    // the dashboard still renders (truncated) instead of the
                    // heartbeat being flagged oversized.
                    walkStateColumns(kanban, function (card) { capAllStrings(card, 2000); });
                    if (len() <= budget) return kanban;

                    // Pass 4: drop the bulkiest ephemeral lists outright.
                    walkStateColumns(kanban, function (card) { if (card && typeof card === 'object') { delete card.agentLog; delete card.agentAnalysis; } });
                    return kanban;
                }
                function buildHeartbeatPayload() {
                    var rank = collectRankPayload();
                    // Trim the kanban payload client-side so it stays under the
                    // server's storage budget (1,000,000 chars) — the server only
                    // slims as a safety net and never has to flag an oversized
                    // heartbeat.
                    var kanban = capKanbanToBudget({
                        projects: (vm.projects || []).map(function (p) {
                            return {
                                Name: p.Name,
                                Path: p.Path,
                                Description: p.Description,
                                BuildCommands: p.BuildCommands
                            };
                        }),
                        state: slimStateForRemote(vm.state),
                        agentActive: vm.streamingActive || false,
                        agentPhase: vm.streamingPhase || '',
                        agentThinking: vm.streamingThinking || '',
                        agentSummary: vm.streamingSummary || '',
                        activeCardId: vm.activeCardId || null,
                        activeCardText: vm.activeCardText || '',
                        calendarCards: vm.calCards || [],
                        userScore: rank.userScore,
                        rankTitle: rank.rankTitle
                    }, 950000);
                    return {
                        clientId: vm.bughostedClientId,
                        kanbanData: JSON.stringify(kanban),
                        // Opt-in project skeleton sharing: the local bridge attaches the
                        // cached skeleton to the forwarded heartbeat when this is on.
                        shareSkeleton: vm.bughostedShareSkeleton === true,
                        projectPath: vm.selectedProject || vm.defaultProject || '',
                        settings: JSON.stringify({ llamaUrl: vm.llamaUrl, llamaModel: vm.llamaModel, terminalApprovalMode: vm.terminalApprovalMode, defaultProject: vm.defaultProject || vm.selectedProject, showTerminal: vm.showTerminal, showAI: vm.showAI, showIDE: vm.showIDE, showKanban: vm.showKanban, showCalendar: vm.showCalendar,                        bughostedHeartbeatEnabled: vm.bughostedHeartbeatEnabled, bughostedShareRank: vm.bughostedShareRank, bughostedShareSkeleton: vm.bughostedShareSkeleton, bughostedUsername: vm.bughostedUsername, bughostedPassword: vm.bughostedPassword, autoQueue: vm.autoQueue, prByDefault: vm.prByDefault, maxFileContextChars: vm.maxFileContextChars, maxFullFileTokens: vm.maxFullFileTokens, maxContextChars: vm.maxContextChars, fileBodyTruncationChars: vm.fileBodyTruncationChars, buildOutputTailChars: vm.buildOutputTailChars, defaultMaxTokens: vm.defaultMaxTokens, includeProjectSkeleton: vm.includeProjectSkeleton, includeEditKnowledge: vm.includeEditKnowledge, compactThinkingContext: vm.compactThinkingContext, summarizeDiffContext: vm.summarizeDiffContext, diffContextSummaryChars: vm.diffContextSummaryChars, llmTimeoutMinutes: vm.llmInfiniteTimeout ? 0 : (vm.llmTimeoutMinutes || 0), approvedTerminalRoots: vm.approvedTerminalRoots, disallowedTerminalRoots: vm.disallowedTerminalRoots, buildCommands: vm.buildCommands })
                    };
                }

                vm.bughostedLogin = function () {
                    if (!vm.bughostedUsername || !vm.bughostedPassword) return;
                    vm.bughostedStatus = 'connecting';
                    $http.post('/api/bughosted/login', { Username: vm.bughostedUsername, Password: vm.bughostedPassword }).then(function (resp) {
                        // Use the server's canonical username (the login response carries the
                        // remote account as `user` — a plain string or an object with
                        // username/name — with `username` as a forward-compat fallback) so
                        // the header rank chip shows the real casing, not whatever was typed.
                        var d = resp.data || {};
                        var canonical = (typeof d.user === 'string') ? d.user
                            : (d.user && (d.user.username || d.user.name)) || d.username || '';
                        if (canonical) vm.bughostedUsername = canonical;
                        vm.bughostedClientId = d.clientId; vm.bughostedStatus = 'connected'; startBughostedHeartbeat(); startBughostedCommandPolling();
                        // Remember the login so a page reload restores the session without
                        // re-entering credentials. saveSettings(true) persists the canonical
                        // username + password to the server config (and never closes an open
                        // settings panel).
                        vm.bughostedAutoLogin = true; writeAutoLoginFlag(true);
                        if (typeof vm.saveSettings === 'function') vm.saveSettings(true);
                    }, function () { vm.bughostedStatus = 'error'; vm.bughostedClientId = ''; });
                };

                vm.bughostedLogout = function () {
                    if (vm.bughostedClientId) $http.post('/api/bughosted/logout', { clientId: vm.bughostedClientId });
                    // Explicit logout means "don't auto-connect next load" — the saved
                    // credentials stay prefilled, but the session is not restored.
                    vm.bughostedAutoLogin = false; writeAutoLoginFlag(false);
                    vm.bughostedClientId = ''; vm.bughostedStatus = 'disconnected'; stopBughostedHeartbeat(); stopBughostedCommandPolling();
                };

                vm.bughostedToggle = function () { (vm.bughostedStatus === 'connected' || vm.bughostedClientId) ? vm.bughostedLogout() : vm.bughostedLogin(); };
                vm.bughostedTestConnection = function () {
                    if (!vm.bughostedUsername || !vm.bughostedPassword) return; vm.bughostedTesting = true; vm.bughostedTestResult = '';
                    $http.post('/api/bughosted/test', { Username: vm.bughostedUsername, Password: vm.bughostedPassword }).then(function (resp) {
                        if (resp.data.success) vm.bughostedTestResult = 'ok'; else { vm.bughostedTestResult = 'fail'; vm.bughostedTestError = resp.data.error || 'HTTP ' + resp.data.statusCode; }
                        vm.bughostedTesting = false;
                    }, function () { vm.bughostedTestResult = 'fail'; vm.bughostedTestError = 'Cannot reach server'; vm.bughostedTesting = false; });
                };
                vm.bughostedForceReconnect = function () { vm.bughostedLogout(); _bhHeartbeatFailCount = 0; $timeout(function () { vm.bughostedLogin(); }, 300); };

                vm.syncEditorState = function () {
                    if (!vm.bughostedClientId || vm.bughostedStatus !== 'connected' || _isSyncingData || vm.shuttingDown) return;
                    _isSyncingData = true;
                    var data = buildHeartbeatPayload();
                    $http.post('/api/bughosted/heartbeat', data).then(function () { _bhHeartbeatFailCount = 0; vm.bughostedStatus = 'connected'; _isSyncingData = false; }, function () { _bhHeartbeatFailCount++; if (_bhHeartbeatFailCount >= 3) vm.bughostedStatus = 'error'; _isSyncingData = false; });
                }; 

                function startBughostedHeartbeat() {
                    if (vm.destroyed) return; stopBughostedHeartbeat();
                    _bhHeartbeatTimer = $interval(function () {
                        if (vm.destroyed || !vm.bughostedClientId || vm.bughostedStatus !== 'connected' || vm.shuttingDown) return;
                        vm.syncEditorState();
                    }, 15000, 0, false);
                    // Keep the editor state fresher than the heartbeat alone: sync
                    // every few seconds while connected so the web dashboard mirrors
                    // the local board without waiting for the 15s heartbeat.
                    _bhEditorSyncTimer = $interval(function () { if (vm.bughostedClientId && vm.bughostedStatus === 'connected' && !vm.shuttingDown) vm.syncEditorState(); }, 3000, 0, false);
                }
                function stopBughostedHeartbeat() {
                    if (_bhHeartbeatTimer) { $interval.cancel(_bhHeartbeatTimer); _bhHeartbeatTimer = null; }
                    if (_bhEditorSyncTimer) { $interval.cancel(_bhEditorSyncTimer); _bhEditorSyncTimer = null; }
                }

                function receiveCommand(cmd) {
                    if (cmd.parameters && !cmd.params) { try { cmd.params = JSON.parse(cmd.parameters); } catch (e) { cmd.params = {}; } }
                    if (!vm.remoteCommands) vm.remoteCommands = [];
                    var existing = vm.remoteCommands.find(function (c) { return c.id === cmd.id; });
                    if (!existing && cmd.command) { vm.remoteCommands.push(cmd); vm.executeRemoteCommand(cmd); }
                }

                function startBughostedCommandPolling() {
                    if (vm.destroyed) return; stopBughostedCommandPolling();
                    var clientId = vm.bughostedClientId; if (!clientId || vm.bughostedStatus !== 'connected') return;
                    try {
                        var es = new EventSource('/api/bughosted/events?clientId=' + encodeURIComponent(clientId));
                        es.addEventListener('command', function (e) { try { var cmd = JSON.parse(e.data); $timeout(function () { receiveCommand(cmd); }, 0); } catch (ex) { } });
                        es.onerror = function () { es.close(); _bhEventSource = null; startPollingFallback(); };
                        _bhEventSource = es;
                    } catch (e) { startPollingFallback(); }
                }
                function startPollingFallback() {
                    if (vm.destroyed || _bhCommandTimer) return;
                    _bhCommandTimer = $interval(function () {
                        if (vm.destroyed || _bhTimerRunning || !vm.bughostedClientId || vm.bughostedStatus !== 'connected' || vm.shuttingDown) return;
                        _bhTimerRunning = true;
                        $http.get('/api/bughosted/commands?clientId=' + encodeURIComponent(vm.bughostedClientId)).then(function (resp) {
                            _bhTimerRunning = false;
                            if (resp && resp.data && resp.data.length > 0) resp.data.forEach(function (cmd) { $timeout(function () { receiveCommand(cmd); }, 0); });
                        }).catch(function () { _bhTimerRunning = false; });
                    }, 5000, 0, false);
                }
                function stopBughostedCommandPolling() {
                    if (_bhEventSource) { _bhEventSource.close(); _bhEventSource = null; }
                    if (_bhCommandTimer) { $interval.cancel(_bhCommandTimer); _bhCommandTimer = null; }
                }

                vm.executeRemoteCommand = function (cmd) {
                    // [Kept identical to original logic, utilizing vm.* methods like vm.moveCard, vm.saveCards, vm.executeAgent]
                    if (cmd.command === 'executeTask' && cmd.params && cmd.params.text) {
                        var card = { id: cmd.params.cardId || uid(), text: cmd.params.text, filePath: cmd.params.project || vm.selectedProject, createdAt: new Date().toISOString(), priority: cmd.params.priority || 'medium', attached: [], selfImproving: false, isDecomposing: false };
                        upsertRemoteCard(vm, card);
                        vm.saveCards();
                    } else if (cmd.command === 'addCard') {
                        var card = { id: cmd.params.cardId || uid(), text: cmd.params.text || cmd.params.title || '', filePath: cmd.params.project || vm.selectedProject, createdAt: new Date().toISOString(), priority: cmd.params.priority || 'medium', attached: [], selfImproving: false, isDecomposing: false };
                        upsertRemoteCard(vm, card);
                        vm.saveCards();
                    } else if (cmd.command === 'changeCardText' && cmd.params && cmd.params.cardId) {
                        // Card text edited on the web dashboard after the card was
                        // created (addCard carries the initial text; later edits come
                        // as changeCardText so the text lands even if the agent had
                        // already fetched the addCard with its original params).
                        var tc = cmd.params.text || '';
                        var c = vm.findCardById ? vm.findCardById(cmd.params.cardId) : null;
                        if (c) { c.text = tc; if (vm.invalidateCardSuggestions) vm.invalidateCardSuggestions(c); vm.saveCards(); }
                        else {
                            upsertRemoteCard(vm, { id: cmd.params.cardId, text: tc, filePath: cmd.params.project || vm.selectedProject, createdAt: new Date().toISOString(), priority: 'medium', attached: [], selfImproving: false, isDecomposing: false });
                            vm.saveCards();
                        }
                    } else if (cmd.command === 'moveCard' && cmd.params) {
                        var fromCol = findCardColumn(vm, cmd.params.cardId); if (fromCol && cmd.params.status && fromCol !== cmd.params.status) vm.moveCard(cmd.params.cardId, fromCol, cmd.params.status);
                    } else if (cmd.command === 'updateCard' && cmd.params) {
                        var c = vm.findCardById ? vm.findCardById(cmd.params.cardId) : null; if (c) { if (cmd.params.text) c.text = cmd.params.text; if (cmd.params.priority) c.priority = cmd.params.priority; if (cmd.params.attached !== undefined) c.attached = cmd.params.attached; if (cmd.params.autoPr !== undefined) c.autoPr = cmd.params.autoPr; vm.saveCards(); }
                    } else if (cmd.command === 'suggestMore' && cmd.params && cmd.params.cardId) {
                        // Remote 'More like this' — top up a card's improvement suggestions
                        // to the cap of 3 by re-running the generator with topup context.
                        var c = vm.findCardById ? vm.findCardById(cmd.params.cardId) : null;
                        if (c && vm.moreLikeThis) { vm.moreLikeThis(c); }
                    } else if (cmd.command === 'archiveCard' && cmd.params) {
                        var col = findCardColumn(vm, cmd.params.cardId) || 'done'; vm.archiveCard(cmd.params.cardId, col);
                    } else if (cmd.command === 'startAgent' && cmd.params) {
                        var c = vm.findCardById ? vm.findCardById(cmd.params.cardId) : null; if (c && !vm.streamingActive) vm.executeAgent(c);
                    } else if (cmd.command === 'stopAgent') {
                        var activeCard = vm.findCardById ? vm.findCardById(vm.activeCardId) : null; vm.stopAgent && vm.stopAgent(activeCard);
                    } else if (cmd.command === 'updateSettings' && cmd.params) {
                        if (cmd.params.llamaUrl !== undefined) vm.llamaUrl = cmd.params.llamaUrl;
                        if (cmd.params.llamaModel !== undefined) vm.llamaModel = cmd.params.llamaModel;
                        if (cmd.params.terminalApprovalMode !== undefined) vm.terminalApprovalMode = cmd.params.terminalApprovalMode;
                        if (cmd.params.defaultProject !== undefined) vm.defaultProject = cmd.params.defaultProject;
                        if (cmd.params.showTerminal !== undefined) vm.showTerminal = cmd.params.showTerminal;
                        if (cmd.params.showAI !== undefined) vm.showAI = cmd.params.showAI;
                        if (cmd.params.showIDE !== undefined) vm.showIDE = cmd.params.showIDE;
                        if (cmd.params.showKanban !== undefined) vm.showKanban = cmd.params.showKanban;
                        if (cmd.params.showCalendar !== undefined) vm.showCalendar = cmd.params.showCalendar;
                        if (cmd.params.autoQueue !== undefined) vm.autoQueue = cmd.params.autoQueue;
                        if (cmd.params.bughostedShareRank !== undefined) vm.bughostedShareRank = cmd.params.bughostedShareRank;
                        if (cmd.params.bughostedShareSkeleton !== undefined) vm.bughostedShareSkeleton = cmd.params.bughostedShareSkeleton;
                        if (cmd.params.prByDefault !== undefined) vm.prByDefault = cmd.params.prByDefault;
                        if (cmd.params.maxFileContextChars !== undefined) vm.maxFileContextChars = cmd.params.maxFileContextChars;
                        if (cmd.params.maxFullFileTokens !== undefined) vm.maxFullFileTokens = cmd.params.maxFullFileTokens;
                        if (cmd.params.maxContextChars !== undefined) vm.maxContextChars = cmd.params.maxContextChars;
                        if (cmd.params.fileBodyTruncationChars !== undefined) vm.fileBodyTruncationChars = cmd.params.fileBodyTruncationChars;
                        if (cmd.params.buildOutputTailChars !== undefined) vm.buildOutputTailChars = cmd.params.buildOutputTailChars;
                        if (cmd.params.defaultMaxTokens !== undefined) vm.defaultMaxTokens = cmd.params.defaultMaxTokens;
                        if (cmd.params.includeProjectSkeleton !== undefined) vm.includeProjectSkeleton = cmd.params.includeProjectSkeleton;
                        if (cmd.params.includeEditKnowledge !== undefined) vm.includeEditKnowledge = cmd.params.includeEditKnowledge;
                        if (cmd.params.compactThinkingContext !== undefined) vm.compactThinkingContext = cmd.params.compactThinkingContext;
                        if (cmd.params.summarizeDiffContext !== undefined) vm.summarizeDiffContext = cmd.params.summarizeDiffContext;
                        if (cmd.params.diffContextSummaryChars !== undefined) vm.diffContextSummaryChars = cmd.params.diffContextSummaryChars;
                        if (cmd.params.approvedTerminalRoots !== undefined) vm.approvedTerminalRoots = cmd.params.approvedTerminalRoots;
                        if (cmd.params.disallowedTerminalRoots !== undefined) vm.disallowedTerminalRoots = cmd.params.disallowedTerminalRoots;
                        if (cmd.params.buildCommands !== undefined) vm.buildCommands = cmd.params.buildCommands;
                        vm.saveSettings();
                    } else if (cmd.command === 'startAllBenchmarks') {
                        // Remote 'Start All Benchmarks' — same flow as the UI Run-All
                        // button: queues every benchmark plan and runs them
                        // back-to-back (guard inside startBenchmarkAll handles a
                        // run already in progress / unreachable LLM).
                        if (vm.startBenchmarkAll) vm.startBenchmarkAll();
                        else if (vm.startBenchmarkAllNow) vm.startBenchmarkAllNow();
                    }
                    $http.post('/api/bughosted/commands/ack', { clientId: vm.bughostedClientId, commandId: cmd.id, status: 'executed', result: 'ok' });
                };

                vm.stopBughostedTimers = function () { stopBughostedHeartbeat(); stopBughostedCommandPolling(); };
                $scope.$on('$destroy', function () { stopBughostedHeartbeat(); stopBughostedCommandPolling(); });
            }
        };
    }]);
