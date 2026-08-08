// bughosted.mixin.js
angular.module('kanbanApp')
    .factory('BugHostedMixin', ['$http', '$interval', '$timeout', function ($http, $interval, $timeout) {
        var _bhHeartbeatFailCount = 0, _bhHeartbeatTimer = null, _bhEditorSyncTimer = null, _bhEventSource = null, _bhCommandTimer = null, _bhTimerRunning = false, _lastSyncedEditorState = null, _isSyncingData = false;

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
                vm.bughostedClientId = ''; vm.bughostedStatus = 'disconnected'; vm.bughostedTesting = false;
                vm.bughostedTestResult = ''; vm.bughostedTestError = ''; vm.remoteCommands = [];

                function collectRankPayload() {
                    var out = { userScore: 0, rankTitle: '' };
                    try {
                        if (typeof vm.userStats !== 'function' || typeof vm.userRankProgress !== 'function') return out;
                        var stats = vm.userStats();
                        var prog = vm.userRankProgress(stats);
                        if (prog && prog.score) out.userScore = Math.round(prog.score);
                        if (typeof vm.userRankTitle === 'function') out.rankTitle = vm.userRankTitle(stats) || '';
                    } catch (e) { }
                    return out;
                }
                function buildHeartbeatPayload() {
                    var rank = collectRankPayload();
                    return {
                        clientId: vm.bughostedClientId,
                        kanbanData: JSON.stringify(
                            { 
                                projects: (vm.projects || []).map(function (p) { 
                                    return { 
                                        Name: p.Name, 
                                        Path: p.Path, 
                                        Description: p.Description, 
                                        BuildCommands: p.BuildCommands 
                                    }; 
                                }), 
                                state: vm.state, 
                                agentActive: vm.streamingActive || false, 
                                agentPhase: vm.streamingPhase || '',
                                agentThinking: vm.streamingThinking || '', 
                                agentSummary: vm.streamingSummary || '', 
                                activeCardId: vm.activeCardId || null, 
                                activeCardText: vm.activeCardText || '', 
                                calendarCards: vm.calCards || [],
                                userScore: rank.userScore,
                                rankTitle: rank.rankTitle
                            }
                        ),
                        settings: JSON.stringify({ llamaUrl: vm.llamaUrl, llamaModel: vm.llamaModel, terminalApprovalMode: vm.terminalApprovalMode, defaultProject: vm.defaultProject || vm.selectedProject, showTerminal: vm.showTerminal, showAI: vm.showAI, showIDE: vm.showIDE, showKanban: vm.showKanban, showCalendar: vm.showCalendar, bughostedHeartbeatEnabled: vm.bughostedHeartbeatEnabled, bughostedUsername: vm.bughostedUsername, bughostedPassword: vm.bughostedPassword, autoQueue: vm.autoQueue, prByDefault: vm.prByDefault, maxFileContextChars: vm.maxFileContextChars, maxFullFileTokens: vm.maxFullFileTokens, maxContextChars: vm.maxContextChars, fileBodyTruncationChars: vm.fileBodyTruncationChars, buildOutputTailChars: vm.buildOutputTailChars, defaultMaxTokens: vm.defaultMaxTokens, includeProjectSkeleton: vm.includeProjectSkeleton, includeEditKnowledge: vm.includeEditKnowledge, compactThinkingContext: vm.compactThinkingContext, summarizeDiffContext: vm.summarizeDiffContext, diffContextSummaryChars: vm.diffContextSummaryChars, llmTimeoutMinutes: vm.llmInfiniteTimeout ? 0 : (vm.llmTimeoutMinutes || 0), approvedTerminalRoots: vm.approvedTerminalRoots, disallowedTerminalRoots: vm.disallowedTerminalRoots, buildCommands: vm.buildCommands })
                    };
                }

                vm.bughostedLogin = function () {
                    if (!vm.bughostedUsername || !vm.bughostedPassword) return;
                    vm.bughostedStatus = 'connecting';
                    $http.post('/api/bughosted/login', { Username: vm.bughostedUsername, Password: vm.bughostedPassword }).then(function (resp) {
                        vm.bughostedClientId = resp.data.clientId; vm.bughostedStatus = 'connected'; startBughostedHeartbeat(); startBughostedCommandPolling();
                    }, function () { vm.bughostedStatus = 'error'; vm.bughostedClientId = ''; });
                };

                vm.bughostedLogout = function () {
                    if (vm.bughostedClientId) $http.post('/api/bughosted/logout', { clientId: vm.bughostedClientId });
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
                    stopBughostedHeartbeat();
                    _bhHeartbeatTimer = $interval(function () {
                        if (!vm.bughostedClientId || vm.bughostedStatus !== 'connected' || vm.shuttingDown) return;
                        _lastSyncedEditorState = null; var data = buildHeartbeatPayload();
                        $http.post('/api/bughosted/heartbeat', data).then(function () { if (vm.shuttingDown) return; _bhHeartbeatFailCount = 0; vm.bughostedStatus = 'connected'; }, function () { if (vm.shuttingDown) return; _bhHeartbeatFailCount++; if (_bhHeartbeatFailCount >= 3) vm.bughostedStatus = 'error'; });
                    }, 30000, 0, false);
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