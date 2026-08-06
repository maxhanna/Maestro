// agent.mixin.js
angular.module('kanbanApp')
    .factory('AgentMixin', ['$http', '$timeout', '$interval', '$window', function ($http, $timeout, $interval, $window) {
        var _lastLogKey = '';

        function uid() { return Math.random().toString(36).slice(2, 9); }

        function normalizeStepStatus(status) {
            if (status === 'written' || status === 'ok' || status === 'created' || status === 'modified') return 'done';
            if (status === 'proposing' || status === 'rejected' || status === 'exploring' || status === 'failed') return status;
            return status || 'pending';
        }
        function normalizeStep(step) { if (!step) return step; step.status = normalizeStepStatus(step.status); return step; }

        function pushAgentLog(vm, level, message, detail) {
            if (!message || level === 'status') return;
            try {
                function normalise(s) { return (s || '').replace(/\d+/g, '#'); }
                var recentDupe = vm.agentActivityLog.length > 0 && vm.agentActivityLog.slice(-3).some(function (e) { return e.level === level && normalise(e.message) === normalise(message); });
                if (recentDupe && level !== 'error' && level !== 'warn' && level !== 'bypass' && level !== 'metric' && level !== 'rejected' && level !== 'recovering') return;
                var entry = { ts: new Date().toLocaleTimeString(), level: level || 'info', message: message, detail: detail };
                vm.agentActivityLog.push(entry); vm.agentActivityLogLength = vm.agentActivityLog.length;
                if (vm.agentActivityLogLength > 100) vm.agentActivityLog.shift();
                if (vm.currentRun && vm.currentRun.log) { vm.currentRun.log.push(entry); if (vm.currentRun.log.length > 200) vm.currentRun.log.shift(); }
                if (vm.scrollToBottom) vm.scrollToBottom();
            } catch (e) { }
        }

        function refreshFilesEditedFromSteps(vm) {
            var seen = {};
            vm.streamingFilesEdited = vm.streamingSteps.filter(function (s) { return (s.type === 'edit' || s.type === 'create' || s.type === 'rename') && s.status === 'done' && s.path; }).filter(function (s) { var already = seen[s.path]; seen[s.path] = true; return !already; }).map(function (s) { var info = { path: s.path, editAction: s.editAction, linesAdded: s.linesAdded, linesRemoved: s.linesRemoved }; if (s.type === 'rename') info.editAction = 'renamed → ' + (s.toPath || ''); else if (s.type === 'create') info.editAction = 'created'; return info; });
        }

        function reconcilePlanItems(vm, $scope, $timeout) {
            if (!vm.planItems || !vm.planItems.length) return;
            var changed = false;
            vm.planItems.forEach(function (item) {
                if (item.done) return;
                var doneSteps = vm.streamingSteps.filter(function (s) {
                    if (s.status !== 'done' && s.status !== 'skipped' && s.status !== 'error') return false;
                    if (s.planItemIndex !== undefined && s.planItemIndex !== null) return s.planItemIndex === item.index;
                    if (s.type === 'command' && item.file && item.file.startsWith('_')) return true;
                    return (s.type === 'edit' || s.type === 'create' || s.type === 'rename') && s.path && item.file && s.path.replace(/\\/g, '/').toLowerCase() === item.file.toLowerCase();
                });
                if (doneSteps.length > 0) { item.done = true; changed = true; }
            });
            vm.verifyDiffs(vm.planItems);
            var activeCard = vm.findCardById ? vm.findCardById(vm.activeCardId) : null;
            if (activeCard && activeCard._plan && changed) activeCard._plan.items = angular.copy(vm.planItems);
            if (changed && vm.saveCards) {
                if (vm._saveCardsTimer) $timeout.cancel(vm._saveCardsTimer);
                vm._saveCardsTimer = $timeout(function () { if (vm.saveCards) vm.saveCards(); }, 500);
            }
        }

        function upsertStreamingStep(vm, parsed, $scope, $timeout) {
            normalizeStep(parsed);
            var existing = vm.streamingSteps.find(function (s) { return s.index === parsed.index; });
            if (existing) angular.extend(existing, parsed); else vm.streamingSteps.push(parsed);
            vm.streamingSteps.sort(function (a, b) { return (a.index || 0) - (b.index || 0); });
            if (parsed.status === 'running') vm.activeStepIndex = parsed.index;
            else { var running = vm.streamingSteps.find(function (s) { return s.status === 'running'; }); vm.activeStepIndex = running ? running.index : null; }
            refreshFilesEditedFromSteps(vm);
        }

        return {
            init: function (vm, $scope) {
                // State
                vm.aiPrompt = ''; vm.aiResponse = ''; vm.activeCardText = ''; vm.activeCardId = null;
                vm.activeCardIds = new Set(); vm.aiChatMessages = []; vm.aiChatInput = ''; vm.aiChatLoading = false; vm.chatMode = 'ask';
                vm.streamingActive = false; vm.streamingThinking = ''; vm.streamingSummary = ''; vm._agentStopped = false; vm.streamingPhase = '';
                vm.streamingContextSize = 0; vm.streamingSteps = []; vm.streamingFilesEdited = []; vm.streamingTokenBuffer = '';
                vm.streamingStableCount = 0; vm.activeStepIndex = null; vm.agentResult = null; vm.steeringContext = ''; vm.clarificationReply = '';
                vm.abortController = new AbortController(); vm.planItems = []; vm.cohesionIssues = []; vm.cohesionFile = '';
                vm.agentRuns = []; vm.currentRun = null;
                vm.refreshStreamingActive = function () {
                    var activeNow = vm.agentRuns.filter(function (r) { return r.active; }).length;
                    var wasActive = vm._lastActiveRunCount || 0;
                    vm._lastActiveRunCount = activeNow;
                    vm.streamingActive = activeNow > 0;
                    if (!vm.streamingActive) { vm.resumeTerminalPolling(); }
                    // A run just ended (active count dropped) -> drain the Ready
                    // queue so parked cards on now-free endpoints start. Deferred
                    // so the finished card's completion bookkeeping runs first.
                    if (activeNow < wasActive) {
                        $timeout(function () { if (vm.processQueuedCards) { vm.processQueuedCards(); } }, 100);
                    }
                };
                // True when the given LLM endpoint ('' = the default endpoint)
                // already has a run in flight. Each endpoint runs one card at a time.
                vm.isEndpointBusy = function (endpointId) {
                    var ep = endpointId || '';
                    return vm.agentRuns.some(function (r) { return r.active && (r.endpointId || '') === ep; });
                };
                // True when a non-self-improving card (todo/doing/done) currently has an
                // active run. Self-improving cards yield to regular work — they only start
                // while this is false.
                vm.regularAgentActive = function () {
                    return vm.agentRuns.some(function (r) { return r.active && !r.selfImproving; });
                };
                // Number of runs currently active. The agent view only splits into
                // side-by-side sections when this is > 1 (multiple endpoints working
                // simultaneously).
                vm.activeRunCount = function () {
                    return vm.agentRuns.filter(function (r) { return r.active; }).length;
                };
                // Starts the next Ready card(s) whose LLM endpoint is now free.
                // Cards parked behind a busy endpoint (_endpointQueued) always start;
                // generally-ready cards start only when the auto-queue is enabled.
                vm._drainingQueue = false;
                vm.processQueuedCards = function () {
                    if (vm._drainingQueue || !vm.state) return;
                    vm._drainingQueue = true;
                    try {
                        // Self-improving cards only drain once the user has physically armed
                        // the cycle (started a self-improving card) AND no regular card
                        // (todo/doing/done) is currently active — regular work always wins.
                        var selfImprovingArmed = vm.selfImprovingAgentActive === true && !vm.regularAgentActive();
                        var candidates = [];
                        (vm.state.todo || []).forEach(function (c) {
                            // _endpointQueued cards were explicitly started by the user,
                            // so drain them even if they belong to a different project.
                            if (c.ready && !c.selfImproving && (c._endpointQueued || c.filePath === vm.selectedProject)) candidates.push(c);
                        });
                        if (vm.state.selfImproving && selfImprovingArmed) {
                            vm.state.selfImproving.forEach(function (c) {
                                if (c.ready && c.selfImproving && (c._endpointQueued || c.filePath === vm.selectedProject)) candidates.push(c);
                            });
                        }
                        for (var i = 0; i < candidates.length; i++) {
                            var card = candidates[i];
                            var ep = card.llmEndpointId || '';
                            if (vm.isEndpointBusy(ep)) continue;
                            // Armed self-improving cards cycle even without autoQueue — the
                            // user physically started them; regular cards still need autoQueue.
                            var isArmedSelfImproving = card.selfImproving && vm.selfImprovingAgentActive === true;
                            if (!card._endpointQueued && !vm.autoQueue && !isArmedSelfImproving) continue;
                            vm.moveCardToDoing(card.id);
                            vm.executeAgent(card);
                        }
                    } finally {
                        vm._drainingQueue = false;
                    }
                };
                vm.pendingContextReview = null; vm.contextReviewCountdown = 0; vm.contextReviewTimer = null;
                vm._agentStartTime = null;
                vm.agentTimer = null;
                vm.cancelAgentTimer = function () { if (vm.agentTimer) { $interval.cancel(vm.agentTimer); vm.agentTimer = null; } };
                vm.formatLogDetail = function (detail) {
                    if (detail === undefined || detail === null) return '';
                    if (typeof detail === 'string') return detail;
                    if (typeof detail === 'object') {
                        if (detail.text) return detail.text;
                        try { return JSON.stringify(detail, null, 2); } catch (e) { return String(detail); }
                    }
                    return String(detail);
                };
                vm.buildTools = [
                    { name: 'Ping', icon: '📡', desc: 'Check host connectivity (TCP/ping/HTTP)', hint: 'ping google.com -n 4' },
                    { name: 'Install Package', icon: '📦', desc: 'Install a NuGet/npm/pip package', hint: 'install package SonarAnalyzer.CSharp' },
                    { name: 'Build', icon: '🔨', desc: 'Run build verification', hint: 'build the project' },
                    { name: 'Full Agent', icon: '🤖', desc: 'Run the full agent pipeline', hint: 'refactor the login page' }
                ];

                // Benchmarks State
                vm.benchmarkScores = []; vm.serverBenchmarks = []; vm.benchmarkPlans = []; vm.benchmarkRunning = false; vm.benchmarkLevel = null; vm.selectedBenchmarkScore = null; vm.selectedServerBenchmark = null; vm.benchmarkPlanNames = {}; vm.fetchingBenchmarks = false;
                vm.benchmarkAllActive = false; vm.benchmarkAllResults = []; vm.benchmarkAllResult = null; vm._benchmarkQueue = [];

                // Methods
                vm.useToolHint = function (hint) { vm.aiChatInput = hint; var el = document.querySelector('.ai-chat-body input'); if (el) el.focus(); };
                vm.toggleChatMode = function () { vm.chatMode = vm.chatMode === 'ask' ? 'build' : 'ask'; };
                vm.logFileSizeAndTokens = function (filePath, content) {
                    if (!filePath || !content) return; const fileSize = content.length; const tokenCount = Math.ceil(fileSize / 4);
                    if (vm.addLogEntry) vm.addLogEntry({ type: 'debug', message: `File: ${filePath} | Size: ${fileSize} chars | Tokens: ~${tokenCount}` });
                };

                vm.executeAgent = function (card, isAutoRestart) {
                    if (!card || !card.text) return;
                    if (vm.agentRuns.some(function (r) { return r.cardId === card.id && r.active; })) return;

                    // Physically starting a self-improving card arms the infinite cycle. It
                    // stays armed (persisted) until the user disables it, but only actually
                    // runs while no regular card (todo/doing/done) is active.
                    if (card.selfImproving && vm.selfImprovingAgentActive !== true) {
                        vm.selfImprovingAgentActive = true;
                        if (vm.persistSelfImprovingAgent) vm.persistSelfImprovingAgent();
                    }

                    // ── Per-endpoint serialization ──
                    // Only one card may run per LLM endpoint at a time ('' = the
                    // default endpoint). If this card's endpoint already has an
                    // active run, keep the card in the Ready state and queue it —
                    // processQueuedCards starts it when the busy run finishes.
                    var cardEndpoint = card.llmEndpointId || '';
                    if (vm.isEndpointBusy(cardEndpoint)) {
                        card.ready = true;
                        card._endpointQueued = true;
                        // The caller may have already moved the card to Doing —
                        // park it back in its source column in the Ready state.
                        var parkCol = card.selfImproving ? 'selfImproving' : 'todo';
                        if (vm.state) {
                            var doingIdx = (vm.state.doing || []).findIndex(function (c) { return c.id === card.id; });
                            if (doingIdx !== -1) {
                                var parkedCard = vm.state.doing.splice(doingIdx, 1)[0];
                                if (!vm.state[parkCol]) vm.state[parkCol] = [];
                                vm.state[parkCol].push(parkedCard);
                            } else {
                                // Card not found in Doing — make sure it stays visible
                                // in its source column so it can be drained later instead
                                // of silently disappearing (reconstructed-object calls).
                                var inCol = (vm.state[parkCol] || []).some(function (c) { return c.id === card.id; });
                                if (!inCol) {
                                    if (!vm.state[parkCol]) vm.state[parkCol] = [];
                                    vm.state[parkCol].push(card);
                                }
                            }
                        }
                        pushAgentLog(vm, 'info', '⏳ ' + (vm.endpointLabel ? vm.endpointLabel(card.llmEndpointId) : 'Default') + ' endpoint is busy — card stays Ready until the current card finishes.');
                        vm.saveCards();
                        $scope.$applyAsync();
                        return;
                    }
                    delete card._endpointQueued;

                    var proj = card.filePath || vm.selectedProject; if (!proj) return $window.alert('No project assigned');
                    try {
                        if (!isAutoRestart) card._agentIteration = 0;
                        delete card.agentAnalysis; delete card.agentLog;

                        var run = {
                            runId: uid() + '-' + Date.now(),
                            cardId: card.id,
                            cardText: card.text,
                            selfImproving: card.selfImproving || false,
                            endpointId: card.llmEndpointId || '',
                            endpointName: vm.endpointLabel ? vm.endpointLabel(card.llmEndpointId) : 'Default',
                            endpointUrl: '', endpointModel: '',
                            log: [], active: true, status: 'running', llmProgressPercent: null,
                            startedAt: Date.now(), elapsed: 0,
                            abortController: new AbortController()
                        };
                        vm.agentRuns.push(run);
                        if (vm.agentRuns.length > 10) {
                            var inactiveIdx = vm.agentRuns.findIndex(function (r) { return !r.active; });
                            if (inactiveIdx !== -1) vm.agentRuns.splice(inactiveIdx, 1);
                        }
                        vm.currentRun = run;
                        vm.refreshStreamingActive();

                        function startAgent() {
                            run._doneProcessed = false; vm.agentResult = null; vm._agentStopped = false; vm.aiResponse = ''; vm.streamingThinking = ''; vm.streamingSummary = '';
                            vm.streamingPhase = ''; vm.streamingContextSize = 0; vm.streamingTokenBuffer = ''; vm.streamingStableCount = 0;
                            vm.complexityScore = null; vm.complexityLabel = ''; vm.complexityTokenCap = null; vm.complexityMaxTokens = null; vm.complexityAtomicSteps = null;
                            vm.cohesionIssues = []; vm.cohesionFile = '';
                            vm.llmProgress = null; vm.llmProgressPercent = null; vm.llmProgressState = '';
                            vm.activeStepIndex = null; vm.streamingActive = true; vm.pauseTerminalPolling();
                            vm._agentStartTime = Date.now();
                            if (vm.agentTimer) { $interval.cancel(vm.agentTimer); vm.agentTimer = null; }
                            vm.agentTimer = $interval(function () {
                                if (vm.streamingActive) {
                                    vm.agentElapsed = (vm._agentStartTime ? Date.now() - vm._agentStartTime : 0);
                                }
                                vm.agentRuns.forEach(function (r) { if (r.active) r.elapsed = Date.now() - r.startedAt; });
                            }, 1000);

                            if (!isAutoRestart) {
                                vm.streamingSteps = [];
                                vm.streamingFilesEdited = [];
                                vm.planItems = [];
                                vm.agentActivityLog = [];
                            }

                            pushAgentLog(vm, 'info', isAutoRestart ? 'Agent restarting (' + (card._agentIteration || 0) + '/5)' : 'Agent started', { project: proj, task: card.text });
                            vm.activeCardText = card.text; vm._agentStartTime = Date.now();
                            var files = Array.isArray(card.attached) ? card.attached : (card.attached ? [card.attached] : []);
                            var payload = { prompt: card.text, project: proj, files: files, maxIterations: 5, maxStepsPerBatch: 8, steeringContext: vm.steeringContext || '', selfImproving: card.selfImproving || false, isDecomposing: card.isDecomposing || false, createTests: card.createTests || false, cardId: card.id, isBenchmark: card._benchmark || false, buildCommands: vm.getProjectBuildCommands(proj) || null, endpointId: card.llmEndpointId || '', runId: run.runId };

                            vm.moveCardToDoing(card.id); vm.activeCardId = card.id; vm.activeCardIds.add(card.id);
                            var localAbortController = run.abortController;

                            fetch('/api/agent/execute-stream', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload), signal: localAbortController.signal })
                                .then(function (response) {
                                    if (!response.ok) { run.active = false; run.status = 'error'; vm.currentRun = null; vm.refreshStreamingActive(); vm.agentResult = { error: 'Server error: ' + response.status }; $scope.$applyAsync(); return; }
                                    var reader = response.body.getReader(); var decoder = new TextDecoder(); var buffer = '';
                                    function readNext() {
                                        reader.read().then(function (result) {
                                            if (result.done) { if (!run.active && !vm.streamingActive) return; run.active = false; if (run.status === 'running') run.status = 'done'; vm.currentRun = null; vm.refreshStreamingActive(); $scope.$applyAsync(); return; }
                                            buffer += decoder.decode(result.value, { stream: true }); var parts = buffer.split('\n\n'); buffer = parts.pop();

                                            $scope.$applyAsync(function () {
                                                vm.currentRun = run;
                                                for (var p = 0; p < parts.length; p++) {
                                                    var lines = parts[p].split('\n'); var eventName = ''; var data = ''; var eventLineFound = false;
                                                    for (var l = 0; l < lines.length; l++) {
                                                        if (!eventLineFound && lines[l].startsWith('event: ')) { eventName = lines[l].slice(7).trim(); eventLineFound = true; }
                                                        else if (lines[l].startsWith('data: ')) { if (data) data += '\n'; data += lines[l].slice(6); }
                                                    }
                                                    data = data.trimEnd ? data.trimEnd() : data.replace(/\s+$/, '');

                                                    if (eventName) {
                                                        var parsed = null; try { parsed = JSON.parse(data); } catch (e) { }
                                                        switch (eventName) {
                                                            case 'log':
                                                                if (parsed) pushAgentLog(vm, parsed.level, parsed.message, parsed.detail);
                                                                break;
                                                            case 'run-start':
                                                                if (parsed && run) {
                                                                    run.endpointName = parsed.endpointName || run.endpointName;
                                                                    run.endpointUrl = parsed.endpointUrl || '';
                                                                    run.endpointModel = parsed.endpointModel || '';
                                                                    if (vm.loadEndpointHealth) vm.loadEndpointHealth();
                                                                    pushAgentLog(vm, 'info', '⚡ Running on endpoint: ' + run.endpointName + (run.endpointModel ? ' (' + run.endpointModel + ')' : ''));
                                                                }
                                                                break;
                                                            case 'complexity':
                                                                if (parsed) {
                                                                    vm.complexityScore = parsed.score;
                                                                    vm.complexityLabel = parsed.label;
                                                                    vm.complexityTokenCap = parsed.tokenCap;
                                                                    vm.complexityMaxTokens = parsed.maxTokens;
                                                                    vm.complexityAtomicSteps = parsed.atomicSteps;
                                                                    pushAgentLog(vm, 'info', '🧠 Complexity: ' + parsed.score + '/100 (' + parsed.label + ') — planning/editing thinking capped at ' + parsed.tokenCap + ' tokens (overall thinking max ' + parsed.maxTokens + ')' + (parsed.atomicSteps ? ', ~' + parsed.atomicSteps + ' atomic step(s) estimated' : ''));
                                                                }
                                                                break;
                                                            case 'phase':
                                                                if (parsed && parsed.message) {
                                                                    vm.streamingPhase = parsed.message;
                                                                    if (parsed.message !== vm.lastPhaseLogged) { vm.lastPhaseLogged = parsed.message; pushAgentLog(vm, 'phase', parsed.message); }
                                                                } else if (parsed && parsed.phase) { vm.streamingPhase = parsed.phase; }
                                                                if (parsed && parsed.contextSize) { vm.streamingContextSize = parsed.contextSize; }
                                                                break;
                                                            case 'status':
                                                                if (parsed && parsed.message) vm.streamingPhase = parsed.message;
                                                                break;
                                                            case 'progress':
                                                                if (parsed) {
                                                                    vm.llmProgress = parsed.progress != null ? parsed.progress : (parsed.percent != null ? parsed.percent / 100 : null);
                                                                    vm.llmProgressPercent = parsed.percent != null ? parsed.percent : (parsed.progress != null ? Math.round(parsed.progress * 100) : 0);
                                                                    vm.llmProgressState = parsed.state || '';
                                                                    if (run) run.llmProgressPercent = vm.llmProgressPercent;
                                                                }
                                                                break;
                                                            case 'token':
                                                                if (parsed && parsed.token) {
                                                                    vm.streamingTokenBuffer += parsed.token;
                                                                    if (vm._streamingLengthTimer) { $timeout.cancel(vm._streamingLengthTimer); }
                                                                    vm._streamingLengthTimer = $timeout(function () { vm.streamingStableCount = vm.streamingTokenBuffer.length; }, 100);
                                                                    if (vm.resolveStreams) { var buf = vm.resolveStreams; if (buf && buf.length) buf[buf.length - 1].content += parsed.token; }
                                                                }
                                                                break;
                                                            case 'thinking':
                                                                if (parsed && parsed.text) {
                                                                    vm.streamingThinking = parsed.text;
                                                                    pushAgentLog(vm, 'think', 'Plan updated (Plan length: ' + parsed.text.length + ' chars)', { text: parsed.text });
                                                                }
                                                                break;
                                                            case 'step-thinking':
                                                                if (parsed && parsed.text) {
                                                                    var stepNum = (parsed.stepIndex !== undefined && parsed.stepIndex !== null) ? parsed.stepIndex + 1 : '?';
                                                                    var label = parsed.phase === 'verify' ? 'Verification thinking — after step ' : 'Extended thinking — step ';
                                                                    // Surface the pre-plan / verify reasoning live in the Thinking panel too.
                                                                    vm.streamingThinking = parsed.text;
                                                                    pushAgentLog(vm, 'think', '🧠 ' + label + stepNum + (parsed.description ? ' — ' + parsed.description : ''), { text: parsed.text });
                                                                }
                                                                break;
                                                            case 'summary':
                                                                if (parsed && parsed.text) { vm.streamingSummary = parsed.text; pushAgentLog(vm, 'summary', parsed.text); }
                                                                break;
                                                            case 'meta-plan':
                                                                if (parsed) {
                                                                    vm.streamingMetaPlan = { summary: parsed.summary, complexity: parsed.complexity, subPlans: parsed.subPlans.map(function (sp) { return { id: sp.id, title: sp.title, description: sp.description, files: sp.files || [], contextNote: sp.contextNote, done: sp.done || false }; }) };
                                                                    pushAgentLog(vm, 'info', '🧠 Meta-Plan: ' + parsed.summary + ' (Complexity: ' + parsed.complexity + '/10)');
                                                                }
                                                                break;
                                                            case 'meta-plan-step-updated':
                                                                if (parsed && vm.streamingMetaPlan && vm.streamingMetaPlan.subPlans) {
                                                                    var sp = vm.streamingMetaPlan.subPlans.find(function (s) { return s.id === parsed.subPlanId; });
                                                                    if (sp) sp.done = parsed.done;
                                                                }
                                                                break;
                                                            case 'plan':
                                                                if (parsed && parsed.items && parsed.items.length) {
                                                                    var existingState = {};
                                                                    if (vm.planItems) vm.planItems.forEach(function (pi) { existingState[pi.file + '|' + pi.change] = { done: pi.done, diffs: pi.diffs, _diffApplied: pi._diffApplied, _diffStepStatus: pi._diffStepStatus }; });
                                                                    vm.planItems = parsed.items.map(function (item, i) {
                                                                        var file = item.File || item.file || '?';
                                                                        var change = item.Change || item.change || '';
                                                                        var key = file + '|' + change;
                                                                        var prev = existingState[key] || {};
                                                                        return { index: i, file: file, change: change, priority: item.Priority || item.priority || i + 1, line: item.Line || item.line || 0, done: prev.done || item.done || false, oldString: item.OldString || item.oldString || '', newString: item.NewString || item.newString || '', diffs: prev.diffs || [], _diffApplied: prev._diffApplied || false, _diffStepStatus: prev._diffStepStatus || '' };
                                                                    });
                                                                    vm.verifyDiffs(vm.planItems);
                                                                    reconcilePlanItems(vm, $scope, $timeout);
                                                                    if (parsed.thinking) vm.streamingThinking = parsed.thinking;
                                                                    if (parsed.summary && !parsed.live) vm.streamingSummary = parsed.summary;
                                                                    if (!parsed.live) pushAgentLog(vm, 'info', '📋 Plan: ' + parsed.summary + ' (' + parsed.items.length + ' steps)', { itemCount: parsed.items.length, score: parsed.score });

                                                                    var activeCard = vm.findCardById(vm.activeCardId);
                                                                    if (activeCard) {
                                                                        activeCard._plan = { items: angular.copy(vm.planItems), summary: parsed.summary, score: parsed.score };
                                                                        vm.saveCards();
                                                                    }
                                                                }
                                                                break;
                                                            case 'edit-resolve':
                                                                if (vm.resolveStreams) vm.resolveStreams.push({ content: '' });
                                                                break;
                                                            case 'show':
                                                                if (parsed && parsed.text) { vm.aiResponse = parsed.text; pushAgentLog(vm, 'info', '📄 ' + parsed.text); }
                                                                break;
                                                            case 'clarification':
                                                                if (parsed && parsed.question) { vm.aiResponse = parsed.question; pushAgentLog(vm, 'warn', 'Clarification needed', { question: parsed.question }); }
                                                                break;
                                                            case 'refresh':
                                                                if (parsed && parsed.target === 'boarddata' && vm.refreshBoardData) vm.refreshBoardData(parsed);
                                                                break;
                                                            case 'step':
                                                                if (parsed) {
                                                                    upsertStreamingStep(vm, parsed, $scope, $timeout);
                                                                    reconcilePlanItems(vm, $scope, $timeout);
                                                                    if (parsed.diffs && parsed.diffs.length && parsed.planItemIndex !== undefined && vm.planItems) {
                                                                        var pi = vm.planItems.find(function (x) { return x.index === parsed.planItemIndex; });
                                                                        if (pi && (!pi.diffs || pi.diffs.length !== parsed.diffs.length)) { pi.diffs = parsed.diffs; pi._diffStepStatus = parsed.status; }
                                                                    }
                                                                    if (parsed.message === 'Cancelled by user' && parsed.planItemIndex !== undefined && vm.planItems) {
                                                                        var cancelledItem = vm.planItems.find(function (pi) { return pi.index === parsed.planItemIndex; });
                                                                        if (cancelledItem) cancelledItem.cancelled = true;
                                                                    }
                                                                    if (parsed.status === 'running') {
                                                                        pushAgentLog(vm, 'step', '▶ ' + parsed.type + ': ' + (parsed.description || parsed.path || parsed.command || ''));
                                                                    } else if (parsed.status === 'error') {
                                                                        pushAgentLog(vm, 'error', '✕ ' + parsed.type + ': ' + (parsed.error || parsed.description || ''));
                                                                    } else if (parsed.skipped) {
                                                                        pushAgentLog(vm, 'info', '⏭ ' + parsed.type + ': ' + (parsed.description || parsed.path || '') + ' (already done)');
                                                                    } else if (parsed.status === 'proposing') {
                                                                        pushAgentLog(vm, 'info', '💡 Proposing: ' + parsed.description + (parsed.thinking ? ' — ' + parsed.thinking : ''));
                                                                    } else if (parsed.status === 'rejected') {
                                                                        pushAgentLog(vm, 'warn', '✗ Rejected: ' + parsed.description + (parsed.error ? ' — ' + parsed.error : ''));
                                                                    } else if (parsed.status === 'exploring') {
                                                                        pushAgentLog(vm, 'info', '🔍 Exploring: ' + parsed.path);
                                                                    }
                                                                }
                                                                break;
                                                            case 'context-review':
                                                                try {
                                                                    if (parsed && parsed.id && parsed.files) {
                                                                        const ctx = parsed; ctx.files.forEach(function (f) { f.keep = true; });
                                                                        vm.pendingContextReview = ctx; vm._contextReviewSubmitted = false; vm.contextReviewCountdown = 5;
                                                                        pushAgentLog(vm, 'phase', '📋 Context review — ' + ctx.files.length + ' file(s) discovered, auto-confirm in 5s');
                                                                        if (vm.contextReviewTimer) $interval.cancel(vm.contextReviewTimer);
                                                                        if (vm.contextReviewAutoConfirm) $timeout.cancel(vm.contextReviewAutoConfirm);
                                                                        vm.contextReviewTimer = $interval(function () { vm.contextReviewCountdown--; if (vm.contextReviewCountdown < 0) vm.contextReviewCountdown = 0; }, 1000, 5);
                                                                        vm.contextReviewAutoConfirm = $timeout(function () { if (!vm._contextReviewSubmitted && vm.pendingContextReview) vm.confirmContextReview(); }, 5000);
                                                                    }
                                                                } catch (e) { pushAgentLog(vm, 'error', 'Context review error: ' + (e.message || e)); }
                                                                break;
                                                            case 'ask-question':
                                                                try {
                                                                    if (parsed && parsed.id && parsed.question) {
                                                                        vm.pendingQuestion = parsed; vm.questionAnswers = {}; vm.questionError = ''; vm.showQuestionModal = true;
                                                                        pushAgentLog(vm, 'info', '❓ Question from agent: ' + parsed.question);
                                                                        if (vm.questionTimeout) $timeout.cancel(vm.questionTimeout);
                                                                        vm.questionTimeout = $timeout(function () { if (vm.showQuestionModal) vm.cancelQuestion(); }, 55000);
                                                                    }
                                                                } catch (e) { pushAgentLog(vm, 'error', 'Question error: ' + (e.message || e)); }
                                                                break;
                                                            case 'cohesion':
                                                                if (parsed && parsed.issues && parsed.issues.length) {
                                                                    vm.cohesionIssues = parsed.issues; vm.cohesionFile = parsed.file || '';
                                                                    pushAgentLog(vm, 'info', '🔍 Cohesion: ' + parsed.issues.length + ' issue(s) found' + (vm.cohesionFile ? ' in ' + vm.cohesionFile : ''));
                                                                    angular.forEach(parsed.issues, function (issue) { pushAgentLog(vm, 'info', '  ⚠ ' + issue); });
                                                                    var activeCardC = vm.findCardById(vm.activeCardId);
                                                                    if (activeCardC) { activeCardC._cohesion = { file: vm.cohesionFile, issues: angular.copy(vm.cohesionIssues) }; vm.saveCards(); }
                                                                } else { pushAgentLog(vm, 'info', '🔍 Cohesion: no issues found'); }
                                                                break;
                                                            case 'done':
                                                                if (run._doneProcessed) { pushAgentLog(vm, 'warn', 'Duplicate done event ignored'); break; }
                                                                run._doneProcessed = true;
                                                                vm.llmProgress = null; vm.llmProgressPercent = 0; vm.llmProgressState = '';
                                                                vm.sendSystemToast(); vm.steeringContext = '';
                                                                var elapsed = vm._agentStartTime ? Date.now() - vm._agentStartTime : 0;
                                                                run.active = false; run.status = 'done'; run.elapsed = Date.now() - run.startedAt; vm.refreshStreamingActive();
                                                                if (vm.loadEndpointHealth) vm.loadEndpointHealth();
                                                                var editsApplied = parsed && parsed.editsApplied;
                                                                var incomplete = parsed && parsed.incomplete;
                                                                if (card.id !== vm.activeCardId) {
                                                                    // A concurrently-running agent finished — complete its card but keep its section.
                                                                    var concMsg = editsApplied ? 'Agent finished (concurrent)' : 'Agent finished without file edits (concurrent)';
                                                                    pushAgentLog(vm, editsApplied ? 'info' : 'warn', concMsg);
                                                                    var concAnalysis = {
                                                                        summary: (parsed && parsed.summary) || vm.streamingSummary,
                                                                        thinking: (parsed && parsed.thinking) || vm.streamingThinking,
                                                                        steps: (parsed && parsed.steps) ? parsed.steps.map(normalizeStep) : angular.copy(vm.streamingSteps),
                                                                        filesEdited: (parsed && parsed.filesEdited) || vm.streamingFilesEdited,
                                                                        planItems: angular.copy(vm.planItems),
                                                                        warning: parsed && parsed.warning,
                                                                        incomplete: !!incomplete
                                                                    };
                                                                    var mvIdx = vm.state.doing.findIndex(function (c) { return c.id === card.id; });
                                                                    var mvCol = 'doing';
                                                                    if (mvIdx === -1 && vm.state.selfImproving) { mvIdx = vm.state.selfImproving.findIndex(function (c) { return c.id === card.id; }); mvCol = 'selfImproving'; }
                                                                    if (mvIdx !== -1) {
                                                                        var mvCard = vm.state[mvCol].splice(mvIdx, 1)[0];
                                                                        mvCard.agentAnalysis = concAnalysis;
                                                                        mvCard.agentLog = angular.copy(run.log);
                                                                        vm.state.done.push(mvCard);
                                                                        vm.saveCards();
                                                                        // Concurrently-finished regular card → suggest improvements too.
                                                                        if (vm.suggestImprovements && mvCol === 'doing' && !mvCard.selfImproving) vm.suggestImprovements(mvCard, concAnalysis.summary, proj);
                                                                    } else if (vm.saveCards) { vm.saveCards(); }
                                                                    $scope.$applyAsync(); return;
                                                                }
                                                                var elapsedStr = elapsed > 0 ? (elapsed >= 60000 ? Math.floor(elapsed / 60000) + 'm ' + (elapsed % 60000) / 1000 + 's' : Math.floor(elapsed / 1000) + 's') : '';

                                                                var editsApplied = parsed && parsed.editsApplied;
                                                                if (!editsApplied && !vm.activeCardId) vm.stopAgent();
                                                                var incomplete = parsed && parsed.incomplete;
                                                                if (parsed && parsed.warning) vm.aiResponse = parsed.warning;

                                                                pushAgentLog(vm, editsApplied ? 'info' : 'warn', editsApplied ? 'Agent finished' : 'Agent finished without file edits', { filesEdited: (parsed && parsed.filesEdited) ? parsed.filesEdited.length : 0, warning: parsed && parsed.warning, duration: elapsedStr || undefined });
                                                                pushAgentLog(vm, 'info', '⏱ ' + (elapsedStr || elapsed + 'ms'));

                                                                var finalThinking = (parsed && parsed.thinking) || vm.streamingThinking;
                                                                var finalSummary = (parsed && parsed.summary) || vm.streamingSummary;
                                                                var finalSteps = (parsed && parsed.steps) ? parsed.steps.map(normalizeStep) : angular.copy(vm.streamingSteps);

                                                                if (parsed && parsed.filesEdited && parsed.filesEdited.length) vm.streamingFilesEdited = parsed.filesEdited;
                                                                else refreshFilesEditedFromSteps(vm);

                                                                vm.agentResult = { summary: finalSummary, thinking: finalThinking, filesEdited: vm.streamingFilesEdited, steps: finalSteps, planItems: angular.copy(vm.planItems), warning: parsed && parsed.warning, incomplete: incomplete, needsClarification: parsed && parsed.needsClarification, question: parsed && (parsed.question || parsed.warning || finalSummary) };
                                                                 vm.aiResponse = (parsed && parsed.warning) || finalSummary || 'Agent completed.';

                                                                var analysis = { summary: finalSummary, thinking: finalThinking, steps: finalSteps, filesEdited: vm.streamingFilesEdited, planItems: angular.copy(vm.planItems), warning: parsed && parsed.warning, incomplete: incomplete, needsClarification: parsed && parsed.needsClarification, question: parsed && (parsed.question || parsed.warning || finalSummary) };
                                                                var doIdx = vm.state.doing.findIndex(function (c) { return c.id === card.id; });
                                                                if (doIdx !== -1) { vm.state.doing[doIdx].agentAnalysis = analysis; vm.state.doing[doIdx].agentLog = angular.copy(vm.agentActivityLog); }

                                                                if (vm._agentStopped || card.id !== vm.activeCardId) { $scope.$applyAsync(); return; }

                                                                if (vm.planItems && vm.planItems.length) {
                                                                    var allDone = vm.planItems.every(function (pi) { return pi.done; });
                                                                    if (!allDone) { incomplete = true; pushAgentLog(vm, 'warn', 'Plan has ' + vm.planItems.filter(function (pi) { return !pi.done; }).length + ' unchecked step(s) — card stays in Doing'); }
                                                                }

                                                                function recordBenchmarkScore() {
                                                                    if (!card._benchmark) return;
                                                                    vm.benchmarkRunning = false; vm.benchmarkLevel = null;
                                                                    var stepsForScoring = (parsed && parsed.steps) ? parsed.steps.map(normalizeStep) : angular.copy(vm.streamingSteps);
                                                                    var editCounts = countEditsFromSteps(stepsForScoring);
                                                                    var successful = editCounts.successful;
                                                                    var failed = editCounts.failed;
                                                                    var totalAttempts = successful + failed;
                                                                    var points = successful + (totalAttempts > 0 && failed === 0 ? successful : 0);
                                                                    var scorePercent = totalAttempts > 0 ? Math.round((successful / totalAttempts) * 1000) / 10 : 0;
                                                                    var status = totalAttempts === 0 ? 'failed' : failed === 0 ? 'completed' : successful > 0 ? 'partial' : 'failed';
                                                                    var bmElapsed = vm._agentStartTime ? Date.now() - vm._agentStartTime : 0;
                                                                    $http.post('/api/benchmark/save-score',
                                                                        {
                                                                            level: card._benchmarkLevel != null ? card._benchmarkLevel : 1,
                                                                            successfulEdits: successful,
                                                                            failedEdits: failed,
                                                                            points: points,
                                                                            scorePercent: scorePercent,
                                                                            status: status,
                                                                            modelUsed: (vm.systemInfoCustom && vm.systemInfoCustom.model) || '',
                                                                            durationMs: bmElapsed,
                                                                            errorReason: vm.agentResult && (vm.agentResult.error || vm.agentResult.warning) || '',
                                                                            edits: collectBenchmarkEdits(stepsForScoring)
                                                                        }
                                                                    );
                                                                    vm._advanceBenchmarkAll(card._benchmarkLevel != null ? card._benchmarkLevel : 1, successful, failed, totalAttempts, status, points, scorePercent);
                                                                    var bIdx = vm.state.todo.indexOf(card);
                                                                    if (bIdx < 0) { bIdx = vm.state.doing.indexOf(card); }
                                                                    if (bIdx < 0) { bIdx = vm.state.done.indexOf(card); }
                                                                    if (bIdx >= 0) {
                                                                        var col = vm.state.todo.indexOf(card) >= 0
                                                                            ? 'todo'
                                                                            : vm.state.doing.indexOf(card) >= 0
                                                                                ? 'doing'
                                                                                : 'done';
                                                                        vm.state[col].splice(bIdx, 1);
                                                                        vm.saveCards();
                                                                    }
                                                                }

                                                                function finishCard() {
                                                                    if (card._benchmark && !incomplete) { recordBenchmarkScore(); vm._agentStartTime = null; if (!vm.agentRuns.some(function (r) { return r.active; })) vm.cancelAgentTimer(); return; }
                                                                    vm._agentStartTime = null;
                                                                    if (!vm.agentRuns.some(function (r) { return r.active; })) vm.cancelAgentTimer();
                                                                    if (!incomplete) {
                                                                        pushAgentLog(vm, 'log', `Plan completed — moving card to ${card.selfImproving ? 'Self-Improving' : 'Done'} column.`);
                                                                        vm.moveCardToDone(card);
                                                                        // Successfully finished → kick off improvement-suggestion generation
                                                                        // for the card while it sits in the Done column. Suggestions are a
                                                                        // Done-column feature, so skip self-improving cards (they round-robin
                                                                        // back to the Self-Improving column, which never renders them).
                                                                        if (vm.suggestImprovements && !card.selfImproving) vm.suggestImprovements(card, finalSummary, proj);
                                                                        $timeout(function () {
                                                                            if (!vm.autoQueue) return;
                                                                            vm.processQueuedCards();
                                                                        }, 500);
                                                                        return;
                                                                    }
                                                                    if (incomplete && card.id === vm.activeCardId) {
                                                                        card._agentIteration = (card._agentIteration || 0) + 1; var MAX_ITERATIONS = 5;
                                                                        if (card._agentIteration >= MAX_ITERATIONS) { pushAgentLog(vm, 'warn', 'Max iterations reached — stopping'); incomplete = false; if (card._benchmark) { recordBenchmarkScore(); return; } }
                                                                        else { pushAgentLog(vm, 'info', 'Re-starting agent (' + card._agentIteration + '/' + MAX_ITERATIONS + ') — ' + (vm.planItems ? vm.planItems.filter(function (pi) { return !pi.done; }).length : 'quality') + ' issue(s) remain'); $timeout(function () { vm.executeAgent(card, true); }, 1000); return; }
                                                                    }
                                                                    $timeout(function () {
                                                                        if (!vm.autoQueue) return;
                                                                        vm.processQueuedCards();
                                                                    }, 500);
                                                                }

                                                                if (!incomplete && card.autoPr && card.prStatus && card.prStatus.branch) {
                                                                    card.prStatus.status = 'creating-pr'; pushAgentLog(vm, 'info', 'Creating PR for branch ' + card.prStatus.branch + '...');
                                                                    $http.post('/api/pr/finish', { projectPath: proj, cardId: card.id, cardText: card.text, branchName: card.prStatus.branch, summary: finalSummary, originalBranch: card.prStatus.originalBranch }).then(function (prResp) {
                                                                        if (prResp.data && prResp.data.success) { card.prStatus = { status: 'pr-created', branch: card.prStatus.branch, prUrl: prResp.data.prUrl }; pushAgentLog(vm, 'info', 'PR created: ' + (prResp.data.prUrl || 'Check your repository')); }
                                                                        else { card.prStatus = { status: 'error', error: (prResp.data && prResp.data.error) || 'PR creation failed', branch: card.prStatus.branch }; pushAgentLog(vm, 'warn', 'PR creation: ' + card.prStatus.error); }
                                                                        finishCard();
                                                                    }, function (err) { card.prStatus = { status: 'error', error: err.statusText || 'PR failed', branch: card.prStatus.branch }; pushAgentLog(vm, 'warn', 'PR creation failed: ' + card.prStatus.error); finishCard(); });
                                                                } else { if (incomplete) pushAgentLog(vm, 'warn', 'Card kept in Doing — no files were modified'); finishCard(); }
                                                                break;
                                                            case 'error':
                                                                run.active = false; run.status = 'error'; vm.currentRun = null; vm.refreshStreamingActive();
                                                                vm._agentStartTime = null;
                                                                if (!vm.agentRuns.some(function (r) { return r.active; })) vm.cancelAgentTimer();
                                                                pushAgentLog(vm, 'error', parsed ? parsed.message : data);
                                                                vm.agentResult = { error: parsed ? parsed.message : data };
                                                                vm.activeCardId = null;
                                                                vm.activeCardIds = new Set();

                                                                if (card._benchmark) {
                                                                    $http.post('/api/benchmark/save-score', {
                                                                        level: card._benchmarkLevel != null ? card._benchmarkLevel : 1,
                                                                        successfulEdits: 0, failedEdits: 0, points: 0,
                                                                        scorePercent: 0, status: 'error',
                                                                        modelUsed: (vm.systemInfoCustom && vm.systemInfoCustom.model) || '',
                                                                        durationMs: vm._agentStartTime ? Date.now() - vm._agentStartTime : 0,
                                                                        errorReason: parsed ? parsed.message : data
                                                                    });
                                                                    vm.benchmarkRunning = false; vm.benchmarkLevel = null;
                                                                    vm._advanceBenchmarkAll(card._benchmarkLevel != null ? card._benchmarkLevel : 1, 0, 0, 0, 'error', 0, 0);
                                                                    var errIdx = vm.state.doing.indexOf(card);
                                                                    if (errIdx >= 0) {
                                                                        vm.state.doing.splice(errIdx, 1);
                                                                        vm.saveCards();
                                                                    }
                                                                }
                                                                break;
                                                        }
                                                    }
                                                }
                                            });
                                            try { $scope.$applyAsync(); } catch (e) { }
                                            readNext();
                                        }).catch(function (readErr) {
                                            if (readErr && readErr.name === 'AbortError') return;
                                            run.active = false; run.status = 'error'; vm.currentRun = null; vm.refreshStreamingActive(); vm.agentResult = { error: 'Stream read error: ' + (readErr && readErr.message || readErr) }; $scope.$applyAsync();
                                            if (vm.loadEndpointHealth) vm.loadEndpointHealth();
                                        });
                                    }
                                    readNext();
                                }).catch(function (err) { run.active = false; run.status = 'error'; vm.currentRun = null; vm.refreshStreamingActive(); });
                        }

                        if (card.autoPr && proj) {
                            pushAgentLog(vm, 'info', 'Creating PR branch...');
                            $http.post('/api/pr/start', { projectPath: proj, cardId: card.id, cardText: card.text }).then(function (resp) {
                                if (resp.data && resp.data.success) { card.prStatus = { status: 'branch-created', branch: resp.data.branchName, originalBranch: resp.data.originalBranch }; pushAgentLog(vm, 'info', 'PR branch: ' + card.prStatus.branch); }
                                else { card.prStatus = { status: 'error', error: 'Branch creation failed' }; pushAgentLog(vm, 'warn', 'PR branch failed'); }
                                startAgent();
                            }, function () { startAgent(); });
                        } else { startAgent(); }
                    } catch (e) { console.log("executeAgent error", e); }
                };

                vm.stopAgent = function (card) {
                    vm._agentStartTime = null;
                    vm.agentElapsed = 0;
                    vm._agentStopped = true;
                    var targetRun = null;
                    if (card) targetRun = vm.agentRuns.find(function (r) { return r.cardId === card.id && r.active; });
                    if (!targetRun) targetRun = vm.agentRuns.find(function (r) { return r.active; });
                    var wasCurrent = targetRun && vm.currentRun === targetRun;
                    if (targetRun) {
                        if (targetRun.abortController) targetRun.abortController.abort();
                        targetRun.active = false; targetRun.status = 'stopped';
                        if (wasCurrent) vm.currentRun = null;
                    }
                    if (vm.abortController) { vm.abortController.abort(); }
                    vm.abortController = new AbortController();
                    if (!vm.agentRuns.some(function (r) { return r.active; })) vm.cancelAgentTimer();
                    vm.refreshStreamingActive();
                    const message = 'Agent stopped by user.';
                    if (wasCurrent) {
                        vm.agentResult = { warning: message };
                        pushAgentLog(vm, 'warn', message);
                        vm.activeCardId = null;
                        vm.activeCardIds = new Set();
                    }
                    if (targetRun && targetRun.log) targetRun.log.push({ ts: new Date().toLocaleTimeString(), level: 'warn', message: message, detail: undefined });
                    vm.showNotification(message);
                };

                vm.stopRun = function (run) {
                    if (!run) return;
                    if (run.abortController) run.abortController.abort();
                    run.active = false; run.status = 'stopped';
                    var wasCurrent = vm.currentRun === run;
                    if (wasCurrent) vm.currentRun = null;
                    if (!vm.agentRuns.some(function (r) { return r.active; })) vm.cancelAgentTimer();
                    vm.refreshStreamingActive();
                    if (wasCurrent) pushAgentLog(vm, 'warn', 'Agent run stopped by user.');
                    if (run && run.log) run.log.push({ ts: new Date().toLocaleTimeString(), level: 'warn', message: 'Agent run stopped by user.', detail: undefined });
                };

                // ── Improvement suggestions for completed cards ─────────────
                // Called when a card finishes successfully (moved to Done). Sets a
                // pulsing "Suggesting improvements…" flag on the card, asks the
                // backend to generate up to 3 LLM suggestions (with file
                // attachments), then persists them onto the card as a
                // "Suggestions" section. Suggestions never become new cards.
                vm.suggestImprovements = function (card, summary, project, opts) {
                    if (!card) return;
                    var topup = !!(opts && opts.topup);
                    if (topup) {
                        // "More like this": tops up only while the card is under the
                        // 3-suggestion cap, and never while a generation is already
                        // in flight.
                        if (!Array.isArray(card._suggestions) || card._suggestions.length >= 3 || card._suggestionsGenerating) return;
                    } else if (card._suggestions || card._suggestionsRequested) {
                        return;
                    }
                    var proj = project || card.filePath || vm.selectedProject;
                    if (!proj) return;
                    card._suggestionsRequested = true;
                    card._suggestionsGenerating = true;
                    card._suggestionsError = null;
                    vm.saveCards();
                    pushAgentLog(vm, 'info', topup ? '💡 Topping up suggestions (More like this)…' : '💡 Suggesting improvements for completed card…');
                    // Ground the suggestions in what the agent actually did and
                    // thought: the stored analysis carries the thinking log, the
                    // executed steps, and the plan items.
                    var analysis = card.agentAnalysis || {};
                    var filesEdited = ((analysis.filesEdited && analysis.filesEdited.length)
                        ? analysis.filesEdited
                        : (vm.streamingFilesEdited || []))
                        .map(function (f) { return f && f.path ? f.path : null; }).filter(function (p) { return !!p; });
                    var stepLog = (analysis.steps || [])
                        .filter(function (s) { return s && s.change; })
                        .slice(0, 40)
                        .map(function (s) { return (s.path ? s.path + ' — ' : '') + s.change; });
                    var planLog = (analysis.planItems || [])
                        .filter(function (p) { return p && p.text; })
                        .slice(0, 25)
                        .map(function (p) { return (p.done ? '✓ ' : '○ ') + p.text; });
                    var payload = {
                        project: proj,
                        cardId: card.id,
                        cardText: card.text,
                        summary: summary || analysis.summary || '',
                        // Thinking logs can run 5-20k chars; cap what we send so the
                        // suggestion prompt stays within the endpoint's input budget.
                        thinking: (analysis.thinking || '').slice(0, 6000),
                        steps: stepLog,
                        planItems: planLog,
                        filesEdited: filesEdited
                    };
                    // "More like this": hand over the existing set so the backend can
                    // extend it (deduped, capped at 3) instead of regenerating from
                    // scratch.
                    if (topup) { payload.topup = true; payload.existing = card._suggestions; }
                    $http.post('/api/agent/suggest-improvements', payload).then(function (resp) {
                        var suggestions = (resp.data && resp.data.suggestions) || [];
                        card._suggestionsGenerating = false;
                        card._suggestions = suggestions;
                        vm.saveCards();
                        if (suggestions.length) {
                            pushAgentLog(vm, 'success', topup ? '💡 Topped up to ' + suggestions.length + ' suggestion(s) on the card.' : '💡 ' + suggestions.length + ' improvement suggestion(s) added to the card.');
                            if (vm.showSideToast) vm.showSideToast(topup ? '💡 Topped up to ' + suggestions.length + ' suggestion(s) on the card' : '💡 ' + suggestions.length + ' improvement suggestion(s) added to the card');
                        } else {
                            pushAgentLog(vm, 'info', '💡 No improvement suggestions generated for this card.');
                        }
                    }, function (err) {
                        card._suggestionsGenerating = false;
                        card._suggestionsError = (err && (err.data && err.data.error || err.statusText)) || 'Suggestion generation failed';
                        card._suggestions = card._suggestions || [];
                        vm.saveCards();
                        pushAgentLog(vm, 'warn', '💡 Suggestion generation failed: ' + card._suggestionsError);
                    });
                };

                // "More like this": tops up the card's Suggestions section toward the
                // 3-suggestion cap with new, distinct ideas in the same vein, grounded
                // in the same card context (thinking / steps / files).
                vm.moreLikeThis = function (card) {
                    if (!card) return;
                    vm.suggestImprovements(card, null, card.filePath || vm.selectedProject, { topup: true });
                };

                // The context that grounded the Suggestions generation — either the
                // persisted _suggestionsContext (survives reloads) or the live card
                // analysis from the run that just finished. Powers the "why these
                // were proposed" explainer on the Suggestions section.
                vm.suggestionContext = function (card) {
                    if (!card) return null;
                    if (card._suggestionsContext) return card._suggestionsContext;
                    var analysis = card.agentAnalysis || {};
                    if (!analysis.summary && !analysis.thinking && !analysis.steps) return null;
                    return {
                        summary: analysis.summary || '',
                        thinking: (analysis.thinking || '').slice(0, 6000),
                        steps: (analysis.steps || []).filter(function (s) { return s && s.change; }).slice(0, 40).map(function (s) { return (s.path ? s.path + ' — ' : '') + s.change; }),
                        planItems: (analysis.planItems || []).filter(function (p) { return p && p.text; }).slice(0, 25).map(function (p) { return (p.done ? '✓ ' : '○ ') + p.text; }),
                        filesEdited: (analysis.filesEdited || []).map(function (f) { return f && f.path ? f.path : null; }).filter(function (p) { return !!p; }),
                        generatedAt: ''
                    };
                };
                vm.toggleSuggestionContext = function (card) {
                    if (!card) return;
                    card._showSuggestionContext = !card._showSuggestionContext;
                };

                vm.askAI = function () {
                    if (!vm.aiPrompt) return $window.alert('Enter a prompt');
                    vm.aiResponse = 'Thinking...';
                    $http.post('/api/ai/generate', { prompt: vm.aiPrompt }).then(function (resp) { vm.aiResponse = typeof resp.data === 'string' ? resp.data : JSON.stringify(resp.data, null, 2); }, function (err) { vm.aiResponse = 'Error: ' + (err.statusText || err); });
                };

                vm.sendAiChat = function () {
                    if (!vm.aiChatInput || vm.aiChatLoading) return;
                    var userMsg = vm.aiChatInput; vm.aiChatInput = ''; vm.aiChatMessages.push({ role: 'user', content: userMsg });
                    if (vm.chatMode === 'build') {
                        var tempCard = { id: 'chat-' + Date.now(), text: userMsg, filePath: vm.selectedProject, attached: [], ready: true, selfImproving: false, isDecomposing: false };
                        vm.aiChatMessages.push({ role: 'assistant', content: '🤖 Starting agent pipeline...', _progress: true });
                        vm.executeAgent(tempCard);
                        var unwatch = $scope.$watch(function () { return vm.agentResult; }, function (newVal) { if (newVal) { var lastMsg = vm.aiChatMessages[vm.aiChatMessages.length - 1]; if (lastMsg && lastMsg._progress) { lastMsg.content = newVal.error ? '❌ ' + newVal.error : '✅ ' + (newVal.summary || 'Agent completed'); delete lastMsg._progress; } unwatch(); } });
                        return;
                    }
                    vm.aiChatLoading = true;
                    var messages = vm.aiChatMessages.map(function (m) { return { role: m.role, content: m.content }; });
                    $http.post('/api/ai/generate', { messages: messages }).then(function (resp) {
                        var content = ''; if (resp.data && resp.data.choices && resp.data.choices[0]) content = resp.data.choices[0].message.content; else if (typeof resp.data === 'string') content = resp.data;
                        vm.aiChatMessages.push({ role: 'assistant', content: content }); vm.aiChatLoading = false;
                    }, function (err) { vm.aiChatMessages.push({ role: 'assistant', content: 'Error: ' + (err.statusText || err) }); vm.aiChatLoading = false; });
                };

                vm.clearAiChat = function () { vm.aiChatMessages = []; vm.aiChatInput = ''; };
                vm.submitClarification = function () {
                    var reply = (vm.clarificationReply || '').trim(); if (!reply) return;
                    var card = vm.findCardById ? vm.findCardById(vm.activeCardId) : null;
                    if (card) { card.text = (card.text || '') + '\n\nClarification requested: ' + (vm.agentResult && vm.agentResult.question || 'Clarification') + '\nUser answer: ' + reply; delete card.agentAnalysis; delete card.agentLog; vm.saveCards(); vm.agentResult = null; vm.executeAgent(card); }
                    vm.clarificationReply = '';
                };

                vm.dismissContextReview = function () { if (vm.contextReviewTimer) { $interval.cancel(vm.contextReviewTimer); vm.contextReviewTimer = null; } vm.pendingContextReview = null; };
                vm.confirmContextReview = function () {
                    if (!vm.pendingContextReview) return;
                    var selected = []; vm.pendingContextReview.files.forEach(function (f) { if (f.keep !== false) selected.push(f.path); });
                    $http.post('/api/agent/context-review/confirm', { id: vm.pendingContextReview.id, files: selected }).then(function () {
                        var card = vm.findCardById ? vm.findCardById(vm.activeCardId) : null;
                        if (card && selected.length > 0) { card.confirmedContextFiles = selected; var existing = Array.isArray(card.attached) ? card.attached : []; selected.forEach(function (f) { if (existing.indexOf(f) === -1) existing.push(f); }); card.attached = existing; vm.saveCards(); }
                        vm.pendingContextReview = null;
                    });
                };

                vm.submitQuestion = function () {
                    if (!vm.pendingQuestion) return;
                    if (vm.questionTimeout) { $timeout.cancel(vm.questionTimeout); vm.questionTimeout = null; }
                    var answers = {}; vm.pendingQuestion.fields.forEach(function (f) { answers[f.key] = (vm.questionAnswers[f.key] || '').trim(); });
                    $http.post('/api/agent/questions/answer', { id: vm.pendingQuestion.id, answers: answers }).then(function () { vm.showQuestionModal = false; vm.pendingQuestion = null; }, function (err) { vm.questionError = 'Failed to submit: ' + (err.data || err.statusText || err); });
                };
                vm.cancelQuestion = function () {
                    if (!vm.pendingQuestion) return;
                    if (vm.questionTimeout) { $timeout.cancel(vm.questionTimeout); vm.questionTimeout = null; }
                    $http.post('/api/agent/questions/answer', { id: vm.pendingQuestion.id, answers: {} }).then(function () { vm.showQuestionModal = false; vm.pendingQuestion = null; });
                };

                vm.applyDiff = function (diffPath, step) {
                    if (!diffPath || !vm.selectedProject) return;
                    var wasRunning = vm.streamingActive;
                    var savedCardId = vm.activeCardId;
                    var savedPrompt = vm.activeCardText;
                    if (wasRunning) {
                        vm.stopAgent();
                    }
                    step._applyingDiff = true;
                    $http.post('/api/agent/apply-diff', { project: vm.selectedProject, diffPath: diffPath }).then(function (resp) {
                        step._applyingDiff = false;
                        if (resp.data && resp.data.success) {
                            step._diffApplied = true;
                            step.status = 'done';
                            step._diffPath = diffPath;
                            if (vm.addLogEntry) vm.addLogEntry({ type: 'info', message: '✓ Diff applied: ' + diffPath + ' — halting agent' });
                            if (wasRunning && savedCardId && savedPrompt) {
                                vm.activeCardId = savedCardId;
                                if (vm.addLogEntry) vm.addLogEntry({ type: 'info', message: '↻ Restarting agent to continue plan…' });
                                $timeout(function () { vm.executeAgent({ id: savedCardId, text: savedPrompt }, true); }, 500);
                            }
                        } else {
                            step._diffError = (resp.data && resp.data.error) || 'Apply failed';
                            if (vm.addLogEntry) vm.addLogEntry({ type: 'error', message: '✕ Diff apply failed: ' + (step._diffError) });
                        }
                    }, function (err) {
                        step._applyingDiff = false;
                        step._diffError = 'HTTP error: ' + (err.statusText || err);
                        if (vm.addLogEntry) vm.addLogEntry({ type: 'error', message: '✕ Diff HTTP error: ' + (step._diffError) });
                    });
                };
                vm.previewDiff = function (diffPath, step) {
                    if (!diffPath || !vm.selectedProject) return;
                    step._previews = step._previews || {};
                    var p = step._previews[diffPath] = step._previews[diffPath] || {};
                    if (p.content) { p.show = !p.show; return; }
                    p.loading = true;
                    $http.get('/api/agent/diff-content', { params: { project: vm.selectedProject, diffPath: diffPath } }).then(function (resp) {
                        p.loading = false;
                        if (resp.data && resp.data.success) {
                            p.content = resp.data.content;
                            p.show = true;
                        } else {
                            p.error = (resp.data && resp.data.error) || 'Preview failed';
                        }
                    }, function () {
                        p.loading = false;
                        p.error = 'HTTP error';
                    });
                };
                vm.deleteDiff = function (diffPath, step, $event) {
                    if ($event) $event.stopPropagation();
                    if (!diffPath || !vm.selectedProject) return;
                    step._previews = step._previews || {};
                    var p = step._previews[diffPath] = step._previews[diffPath] || {};
                    p.deleting = true;
                    $http.post('/api/agent/delete-diff', { project: vm.selectedProject, diffPath: diffPath }).then(function (resp) {
                        p.deleting = false;
                        if (resp.data && resp.data.success) {
                            p.deleted = true;
                            if (step.diffs) {
                                var idx = step.diffs.indexOf(diffPath);
                                if (idx >= 0) step.diffs.splice(idx, 1);
                            }
                            if (vm.addLogEntry) vm.addLogEntry({ type: 'info', message: '🗑 Diff deleted: ' + diffPath });
                        } else {
                            p.deleteError = (resp.data && resp.data.error) || 'Delete failed';
                            if (vm.addLogEntry) vm.addLogEntry({ type: 'error', message: '✕ Diff delete failed: ' + (p.deleteError) });
                        }
                    }, function () {
                        p.deleting = false;
                        p.deleteError = 'HTTP error';
                    });
                };
                vm.openDiffInIde = function (diffPath, step, $event) {
                    if ($event) $event.stopPropagation();
                    if (!diffPath || !vm.selectedProject) return;
                    vm.showIDE = true;
                    if (vm.openFile) {
                        vm.openFile(diffPath);
                    } else if (vm.ide && vm.ide.openFile) {
                        vm.ide.openFile(diffPath);
                    }
                };
                vm.verifyDiffs = function (planItems) {
                    if (!planItems || !planItems.length || !vm.selectedProject) return;
                    var allDiffs = [];
                    var diffToItems = {};
                    planItems.forEach(function (item) {
                        if (item.diffs && item.diffs.length) {
                            item.diffs.forEach(function (d) {
                                if (!diffToItems[d]) diffToItems[d] = [];
                                diffToItems[d].push(item);
                            });
                            allDiffs.push.apply(allDiffs, item.diffs);
                        }
                    });
                    if (!allDiffs.length) return;
                    var seen = {}; allDiffs = allDiffs.filter(function (d) { var k = d.toLowerCase(); var dup = seen[k]; seen[k] = true; return !dup; });
                    $http.post('/api/agent/verify-diffs', { project: vm.selectedProject, diffPaths: allDiffs }).then(function (resp) {
                        if (resp.data && resp.data.missing && resp.data.missing.length) {
                            resp.data.missing.forEach(function (d) {
                                var items = diffToItems[d];
                                if (items) {
                                    items.forEach(function (item) {
                                        var idx = item.diffs.indexOf(d);
                                        if (idx >= 0) item.diffs.splice(idx, 1);
                                    });
                                }
                            });
                        }
                    });
                };

                vm.openBenchmarksPanel = function () { vm.showBenchmarksPanel = true; vm.compareMode = false; vm.compareA = null; vm.compareB = null; vm.compareResult = null; vm.checkLlmReachable(); $http.get('/api/benchmark/scores').then(function (resp) { vm.benchmarkScores = resp.data || []; }); $http.get('/api/benchmark/plans').then(function (resp) { vm.benchmarkPlans = resp.data || []; if (vm._hydrateRestoredBenchmarkQueue) vm._hydrateRestoredBenchmarkQueue(); }); $http.get('/api/benchmark/system-info').then(function (resp) { vm.systemInfoCustom = resp.data.custom || {}; }); };
                vm.closeBenchmarksPanel = function () { vm.showBenchmarksPanel = false; };
                // Collapsible benchmark sections — headers expand/collapse their
                // bodies, and everything starts collapsed so the panel opens
                // compact and the user can expand what they need.
                vm.benchSectionOpen = { run: false, local: false, specs: false };
                vm.toggleBenchSection = function (key) {
                    if (!vm.benchSectionOpen) vm.benchSectionOpen = {};
                    vm.benchSectionOpen[key] = !vm.benchSectionOpen[key];
                    // Re-probe reachability when the run section is expanded, so a
                    // model server that came up (or died) while the panel stayed
                    // open doesn't leave the buttons with stale state.
                    if (key === 'run' && vm.benchSectionOpen.run && vm.checkLlmReachable) vm.checkLlmReachable();
                };
                vm.benchmarkLevelName = function (level) {
                    if (level === undefined || level === null) return '';
                    var p = (vm.benchmarkPlans || []).find(function (x) { return x.level === level; });
                    return p ? p.name : 'Benchmark ' + level;
                };
                // True while benchmark runs (or any agent run) are in flight.
                // Sources the truth from the BOARD (a _benchmark card sitting in
                // Doing, or a ready benchmark card queued in Todo) rather than
                // only the in-session JS flags — benchmark cards keep running
                // server-side across page reloads, so the flags alone would let
                // the Run All / Run buttons wrongly re-enable mid-run. Mirrors
                // the startBenchmark/startBenchmarkAll guards exactly (session
                // flags + streamingActive), then adds the reload-safe board check.
                vm.benchmarksRunning = function () {
                    if (vm.benchmarkRunning || vm.streamingActive || vm.benchmarkAllActive) return true;
                    var doing = (vm.state && vm.state.doing) || [];
                    var todo = (vm.state && vm.state.todo) || [];
                    return doing.some(function (c) { return c && c._benchmark; })
                        || todo.some(function (c) { return c && c._benchmark && c.ready; });
                };
                // LLM reachability for the benchmark buttons: null while unknown
                // (don't disable until we've probed), true/false once checked.
                vm.llmReachable = null;
                vm.checkLlmReachable = function () {
                    $http.get('/api/agent/llm-reachable').then(function (resp) {
                        vm.llmReachable = !!(resp.data && resp.data.reachable);
                    }, function () { vm.llmReachable = false; });
                };
                // Disable benchmark run buttons while a run is in flight OR the
                // configured LLM endpoint is unreachable (checked on panel open).
                vm.benchmarkRunDisabled = function () {
                    return vm.benchmarksRunning() || vm.llmReachable === false;
                };
                vm.benchmarkRunTitle = function () {
                    if (vm.llmReachable === false) return '⚠ LLM endpoint unreachable — start the model server (or fix the endpoint in Settings) to run benchmarks.';
                    if (vm.benchmarksRunning()) return 'A benchmark is already running — wait for it to finish.';
                    return '';
                };
                // Persists the run-all batch state (queue + completed results)
                // onto the board so a page reload can restore accurate progress.
                // The benchmark cards themselves already live in board data — this
                // captures the batch wrapper around them (how many levels remain,
                // which completed with what score) that is otherwise in-memory only.
                vm._persistBenchmarkRun = function () {
                    if (!vm.state) return;
                    var run = {
                        active: !!vm.benchmarkAllActive,
                        queue: (vm._benchmarkQueue || []).map(function (p) { return p.level; }),
                        results: vm.benchmarkAllResults || []
                    };
                    // Keep a stable id so a re-opened panel can tell this batch
                    // apart from an older persisted one.
                    run.id = vm._benchmarkRunId || (vm._benchmarkRunId = 'br_' + Date.now().toString(36));
                    vm.state._benchmarkRun = run;
                    vm.saveCards();
                };
                // Called after board data loads (or refreshes) to bring the
                // benchmark panel state back in line with what is actually on the
                // board: a _benchmark card in Doing = a benchmark is running, and
                // a persisted _benchmarkRun restores the run-all queue + results.
                vm._restoreBenchmarkState = function () {
                    if (!vm.state) return;
                    var doing = (vm.state.doing || []).filter(function (c) { return c && c._benchmark; });
                    var todo = (vm.state.todo || []).filter(function (c) { return c && c._benchmark && c.ready; });
                    var liveCard = doing[0] || todo[0];
                    // Single benchmark (no batch): running flag comes from the card.
                    if (liveCard && !vm.benchmarkAllActive) {
                        vm.benchmarkRunning = true;
                        vm.benchmarkLevel = liveCard._benchmarkLevel != null ? liveCard._benchmarkLevel : 1;
                        if (vm.benchSectionOpen) vm.benchSectionOpen.run = true;
                    }
                    var run = vm.state._benchmarkRun;
                    if (run && run.active && !vm.benchmarkAllActive) {
                        // A persisted in-progress batch and no live in-memory one
                        // (page reload). Restore the queue + results so the panel
                        // shows accurate progress. Guarded on !vm.benchmarkAllActive
                        // so a mid-session board refresh can't clobber a batch this
                        // tab is already tracking in memory.
                        vm.benchmarkAllActive = true;
                        vm._benchmarkRunId = run.id;
                        // Auto-expand the run section so the restored progress and
                        // the Resume control are visible without a manual click.
                        if (vm.benchSectionOpen) vm.benchSectionOpen.run = true;
                        vm.benchmarkAllResults = (run.results || []).slice();
                        // Rebuild the queue from the plans list (already fetched for
                        // the panel); levels we can't resolve stay as {level} stubs so
                        // the count and progression remain correct even if plans
                        // loaded after this restore.
                        var plans = vm.benchmarkPlans || [];
                        vm._benchmarkQueue = (run.queue || []).map(function (lv) {
                            return plans.find(function (p) { return p.level === lv; }) || { level: lv, name: 'Benchmark ' + lv, description: '' };
                        });
                    if (vm.benchmarkRunning === false) {
                        // Batch active but no visible card in Doing/Todo — the
                        // previous run died with the tab. Surface that the batch
                        // is incomplete rather than showing idle.
                        vm.benchmarkRunning = true;
                        // Prefer the stuck Doing card's level for accurate
                        // progress; fall back to the next queued level.
                        var stuckDoing = (vm.state.doing || []).find(function (c) { return c && c._benchmark; });
                        vm.benchmarkLevel = (stuckDoing && stuckDoing._benchmarkLevel != null)
                            ? stuckDoing._benchmarkLevel
                            : (vm._benchmarkQueue.length ? vm._benchmarkQueue[0].level : (vm.benchmarkAllResults.length ? vm.benchmarkAllResults[vm.benchmarkAllResults.length - 1].level : null));
                    }
                    } else if (run && !run.active) {
                        // A finished batch: keep the final summary visible.
                        vm.benchmarkAllActive = false;
                        vm.benchmarkAllResults = (run.results || []).slice();
                        vm.benchmarkAllResult = vm._summarizeBenchmarkAll(vm.benchmarkAllResults);
                    } else if (!run && !liveCard) {
                        vm.benchmarkAllActive = false;
                    }
                    // A batch flagged active with no benchmark card left on the
                    // board is stale (the run died with its tab, or the stuck
                    // card was deleted) — finalize it so the run buttons unlock.
                    vm._finalizeStaleBenchmarkIfNeeded();
                };
                // Ends a run-all batch that is marked active but no longer has any
                // benchmark card in Todo/Doing (reload mid-run + deleted card, or a
                // run that died with its tab). Keeps whatever results completed so
                // the final-score section stays visible.
                vm._finalizeStaleBenchmarkIfNeeded = function () {
                    if (!vm.state || !vm.benchmarkAllActive) return;
                    var anyBenchCard = (vm.state.todo || []).some(function (c) { return c && c._benchmark; })
                        || (vm.state.doing || []).some(function (c) { return c && c._benchmark; });
                    if (!anyBenchCard) {
                        vm.benchmarkAllActive = false;
                        vm._benchmarkQueue = [];
                        vm.benchmarkAllResult = vm._summarizeBenchmarkAll(vm.benchmarkAllResults);
                        vm.benchmarkRunning = false; vm.benchmarkLevel = null;
                        vm._persistBenchmarkRun();
                    }
                };
                // A batch restored from the board is rebuilt as {level} stubs when
                // it loads before /api/benchmark/plans has answered. Once the plans
                // arrive (openBenchmarksPanel), swap the stubs for the real plan
                // objects so names/descriptions render and Resume can run them.
                vm._hydrateRestoredBenchmarkQueue = function () {
                    if (!vm._benchmarkQueue || !vm._benchmarkQueue.length || !vm.benchmarkPlans || !vm.benchmarkPlans.length) return;
                    vm._benchmarkQueue = vm._benchmarkQueue.map(function (p) {
                        var real = vm.benchmarkPlans.find(function (x) { return x.level === p.level; });
                        return real || p;
                    });
                };
                // True when a run-all batch is flagged active but no run in this
                // tab is driving it — the SSE stream that advances the batch died
                // with the previous page, so it is stuck and needs a manual resume.
                vm.benchmarkInterrupted = function () {
                    if (!vm.benchmarkAllActive) return false;
                    return !vm.agentRuns.some(function (r) { return r.active; });
                };
                // Resumes a run-all batch interrupted by a page reload. The reload
                // killed the SSE stream that drives the batch, so the benchmark
                // card that was in Doing is stuck (its run died with the tab and
                // never recorded a result). Resume removes that dead card, puts
                // its level back at the front of the queue, and restarts the
                // remaining batch from this tab.
                vm.resumeBenchmarkAll = function () {
                    if (!vm.benchmarkAllActive || !vm.state) return;
                    // The button is gated by benchmarkInterrupted(), but a stale
                    // digest or an endpoint that just freed up can start a run
                    // between render and click — re-running the stuck level then
                    // would duplicate execution.
                    if (vm.agentRuns.some(function (r) { return r.active; })) return;
                    if (!vm.benchmarkPlans || !vm.benchmarkPlans.length) {
                        // Panel opens before the plans fetch resolves: load the
                        // plans first so the stuck level resolves to a real plan
                        // instead of being dropped as an un-runnable stub.
                        $http.get('/api/benchmark/plans').then(function (resp) {
                            vm.benchmarkPlans = resp.data || [];
                            vm._hydrateRestoredBenchmarkQueue();
                            vm._resumeBenchmarkAllNow();
                        }).catch(function () { vm._resumeBenchmarkAllNow(); });
                        return;
                    }
                    vm._resumeBenchmarkAllNow();
                };
                vm._resumeBenchmarkAllNow = function () {
                    if (!vm.benchmarkAllActive || !vm.state) return;
                    var doing = (vm.state.doing || []).filter(function (c) { return c && c._benchmark; });
                    var todo = (vm.state.todo || []).filter(function (c) { return c && c._benchmark; });
                    var stuckLevel = doing.length ? (doing[0]._benchmarkLevel != null ? doing[0]._benchmarkLevel : null) : null;
                    if (doing.length) vm.state.doing = vm.state.doing.filter(function (c) { return !c._benchmark; });
                    if (todo.length) vm.state.todo = vm.state.todo.filter(function (c) { return !c._benchmark; });
                    // The stuck level never recorded a result — re-run it first.
                    if (stuckLevel != null && !vm.benchmarkAllResults.some(function (r) { return r.level === stuckLevel; })) {
                        var plan = (vm.benchmarkPlans || []).find(function (p) { return p.level === stuckLevel; })
                            || { level: stuckLevel, name: vm.benchmarkLevelName(stuckLevel), description: '' };
                        vm._benchmarkQueue.unshift(plan);
                    }
                    pushAgentLog(vm, 'info', '▶ Resuming interrupted benchmark run…');
                    vm._persistBenchmarkRun();
                    vm._runNextBenchmarkFromQueue();
                };
                vm._summarizeBenchmarkAll = function (results) {
                    var r = results || [];
                    var failed = r.find(function (x) { return x.failed > 0 || x.status === 'failed' || x.status === 'error'; });
                    return {
                        completedLevels: r.length,
                        failedLevel: failed ? failed.level : null,
                        levels: r,
                        totalPoints: r.reduce(function (s, x) { return s + (x.points || 0); }, 0),
                        totalSuccessful: r.reduce(function (s, x) { return s + (x.successful || 0); }, 0),
                        totalFailed: r.reduce(function (s, x) { return s + (x.failed || 0); }, 0)
                    };
                };
                vm._runBenchmarkLevel = function (plan) {
                    vm.benchmarkRunning = true; vm.benchmarkLevel = plan.level;
                    // A running benchmark auto-expands the run section so the
                    // live progress stays visible even though it starts collapsed.
                    if (!vm.benchSectionOpen) vm.benchSectionOpen = {};
                    vm.benchSectionOpen.run = true;
                    var card = { id: 'benchmark_' + plan.level + '_' + Date.now(), text: plan.description, filePath: vm.selectedProject, priority: 'high', _benchmark: true, _benchmarkLevel: plan.level, ready: true };
                    vm.state.todo.push(card); vm.saveCards(); vm.executeAgent(card);
                };
                vm.startBenchmark = function (level) {
                    if (vm.benchmarkRunning || vm.streamingActive || vm.benchmarkAllActive) return;
                    $http.get('/api/benchmark/plans').then(function (resp) {
                        var plan = (resp.data || []).find(function (p) { return p.level === level; });
                        if (!plan) return;
                        vm._runBenchmarkLevel(plan); vm.closeBenchmarksPanel();
                    }).catch(function () { vm.benchmarkRunning = false; });
                };
                vm._finishBenchmarkAll = function () {
                    vm.benchmarkAllActive = false;
                    vm._benchmarkQueue = [];
                    vm.benchmarkAllResult = vm._summarizeBenchmarkAll(vm.benchmarkAllResults);
                    vm.benchmarkRunning = false; vm.benchmarkLevel = null;
                    // Persist the finished summary so a reload still shows the
                    // final score instead of losing it.
                    vm._persistBenchmarkRun();
                    var failed = vm.benchmarkAllResult.failedLevel;
                    pushAgentLog(vm, 'info', '📊 Benchmark All finished — completed ' + vm.benchmarkAllResult.completedLevels + ' benchmark(s), ' +
                        (failed != null ? 'stopped at ' + vm.benchmarkLevelName(failed) + ' due to error steps' : 'all benchmarks passed') +
                        ' (' + vm.benchmarkAllResult.totalPoints + ' pts)');
                };
                vm._runNextBenchmarkFromQueue = function () {
                    if (!vm._benchmarkQueue || !vm._benchmarkQueue.length) { vm._finishBenchmarkAll(); return; }
                    var plan = vm._benchmarkQueue.shift();
                    // A queue entry that never resolved to a real plan (restored
                    // from the board before /api/benchmark/plans answered) has an
                    // empty task text — starting it would create a card that
                    // executeAgent silently skips and the batch would deadlock.
                    // Skip such levels (logged, no result recorded) rather than
                    // stalling the whole run.
                    while (plan && !plan.description) {
                        pushAgentLog(vm, 'warn', '⏭ Skipping benchmark ' + vm.benchmarkLevelName(plan.level) + ' — plan unavailable (open the panel once plans load, or run it manually).');
                        if (!vm._benchmarkQueue.length) { vm._finishBenchmarkAll(); return; }
                        plan = vm._benchmarkQueue.shift();
                    }
                    vm._runBenchmarkLevel(plan);
                };
                vm.startBenchmarkAll = function () {
                    if (vm.benchmarkRunning || vm.streamingActive || vm.benchmarkAllActive) return;
                    $http.get('/api/benchmark/plans').then(function (resp) {
                        var plans = (resp.data || []).slice().sort(function (a, b) { return a.level - b.level; });
                        if (!plans.length) return;
                        vm.benchmarkAllActive = true;
                        vm.benchmarkAllResults = [];
                        vm.benchmarkAllResult = null;
                        vm._benchmarkQueue = plans;
                        // Record the batch on the board before the first card
                        // starts so a reload mid-batch can restore the queue.
                        vm._persistBenchmarkRun();
                        vm._runNextBenchmarkFromQueue();
                    }).catch(function () { vm.benchmarkAllActive = false; });
                };
                vm._advanceBenchmarkAll = function (level, successful, failed, totalAttempts, status, points, scorePercent) {
                    if (!vm.benchmarkAllActive) return;
                    vm.benchmarkAllResults.push({ level: level, successful: successful, failed: failed, status: status, points: points, scorePercent: scorePercent });
                    // Keep the board in sync as results land so a mid-batch reload
                    // sees the correct completed/remaining split.
                    vm._persistBenchmarkRun();
                    if (failed > 0 || totalAttempts === 0 || status === 'error' || status === 'failed') { vm._finishBenchmarkAll(); }
                    else { vm._runNextBenchmarkFromQueue(); }
                };

                function countEditsFromSteps(steps) {
                    if (!steps || !steps.length) return { successful: 0, failed: 0 };
                    var successful = 0, failed = 0;
                    steps.forEach(function (s) {
                        if (s.type === 'edit' || s.type === 'create' || s.type === 'rename' || s.type === 'plan_step') {
                            if (s.status === 'done' || s.status === 'applied' || s.status === 'created') successful++;
                            else if (s.status === 'error' || s.status === 'rejected' || s.status === 'failed') failed++;
                        }
                    });
                    return { successful: successful, failed: failed };
                }

                vm.formatBenchmarkDuration = function (durMs) {
                    if (durMs === null || durMs === undefined) return '';
                    var seconds = Math.floor(durMs / 1000); var minutes = Math.floor(seconds / 60); seconds = seconds % 60; var hours = Math.floor(minutes / 60); minutes = minutes % 60;
                    return (hours > 0 ? hours + 'h ' : '') + (minutes > 0 ? minutes + 'm ' : '') + seconds + 's';
                }

                // Null-safe percentage helper (server scores may arrive as strings).
                // Compact edit records captured when a benchmark run finishes, so
                // the saved score can show what the agent actually changed.
                function collectBenchmarkEdits(steps) {
                    if (!steps || !steps.length) return [];
                    var out = [];
                    var seen = {};
                    steps.forEach(function (s) {
                        if (s.type !== 'edit' && s.type !== 'create' && s.type !== 'rename') return;
                        if (!s.path) return;
                        // Keep every distinct step: key on index when present, else path+type+status.
                        var key = (s.index != null && s.index !== undefined ? s.index + '|' : '') + s.path + '|' + s.type + '|' + (s.type === 'rename' ? (s.toPath || '') : s.status);
                        if (seen[key]) return;
                        seen[key] = true;
                        var action = s.editAction || (s.type === 'create' ? 'created' : (s.type === 'rename' ? 'renamed → ' + (s.toPath || '') : 'modified'));
                        var rec = {
                            path: s.path,
                            type: s.type,
                            status: s.status || 'pending',
                            editAction: action,
                            linesAdded: s.linesAdded || 0,
                            linesRemoved: s.linesRemoved || 0,
                            toPath: s.toPath || '',
                            error: s.error || '',
                            index: s.index != null && s.index !== undefined ? s.index : null
                        };
                        // Best-effort line-level diff: prefer structured diffLines, else old/new arrays, else raw strings.
                        var diff = [];
                        if (Array.isArray(s.diffLines) && s.diffLines.length) {
                            s.diffLines.forEach(function (l) {
                                if (l && (l.oldLine != null || l.newLine != null)) diff.push({ old: l.oldLine || '', new: l.newLine || '' });
                            });
                        } else if (Array.isArray(s.oldLines) || Array.isArray(s.newLines)) {
                            var oldA = s.oldLines || [], newA = s.newLines || [];
                            var m = Math.max(oldA.length, newA.length);
                            for (var i = 0; i < m; i++) diff.push({ old: i < oldA.length ? oldA[i] : '', new: i < newA.length ? newA[i] : '' });
                        } else if (s.oldString || s.newString) {
                            var o = (s.oldString || '').replace(/\r\n/g, '\n').split('\n');
                            var n = (s.newString || '').replace(/\r\n/g, '\n').split('\n');
                            var mm = Math.max(o.length, n.length);
                            for (var j = 0; j < mm; j++) diff.push({ old: j < o.length ? o[j] : '', new: j < n.length ? n[j] : '' });
                        }
                        rec.diff = diff.slice(0, 60);
                        out.push(rec);
                    });
                    return out;
                }

                vm.benchmarkPct = function (scorePercent) {
                    if (scorePercent === null || scorePercent === undefined || scorePercent === '') return '—';
                    var n = Number(scorePercent);
                    return isNaN(n) ? '—' : n.toFixed(1) + '%';
                };
                vm.benchmarkPctNum = function (scorePercent) {
                    if (scorePercent === null || scorePercent === undefined) return 0;
                    var n = Number(scorePercent);
                    return isNaN(n) ? 0 : n;
                };

                // Friendly label + CSS class for a benchmark score's status.
                vm.benchmarkStatusInfo = function (status) {
                    var s = String(status || '').toLowerCase();
                    if (s === 'completed' || s === 'passed' || s === 'ok' || s === 'success') return { label: 'Passed', cls: 'good' };
                    if (s === 'partial') return { label: 'Partial', cls: 'warn' };
                    if (s === 'failed' || s === 'error' || s === 'fail') return { label: 'Failed', cls: 'bad' };
                    return { label: status || '—', cls: 'neutral' };
                };

                // SVG line-chart data for score% trend across runs (oldest → newest).
                // Returns null when there aren't enough points, else { w, h, pts, line, area, last, best }.
                vm.benchmarkTrendData = function () {
                    var scores = (vm.benchmarkScores || []).slice()
                        .sort(function (a, b) { return new Date(a.timestamp || 0) - new Date(b.timestamp || 0); });
                    var pts = [];
                    scores.forEach(function (s) {
                        var p = s.scorePercent;
                        if (p === null || p === undefined || p === '') return;
                        var n = Number(p);
                        if (!isNaN(n)) pts.push(n);
                    });
                    if (pts.length < 2) return null;
                    var w = 300, h = 80, padX = 6, padY = 6, minY = 0, maxY = 100;
                    var stepX = (w - padX * 2) / (pts.length - 1);
                    var coords = pts.map(function (p, i) {
                        var x = padX + i * stepX;
                        var y = h - padY - ((p - minY) / (maxY - minY)) * (h - padY * 2);
                        return { x: Math.round(x * 10) / 10, y: Math.round(y * 10) / 10, pct: p };
                    });
                    var line = coords.map(function (c) { return c.x + ',' + c.y; }).join(' ');
                    var area = line + ' ' + (w - padX) + ',' + (h - padY) + ' ' + padX + ',' + (h - padY);
                    var best = Math.max.apply(null, pts);
                    return { w: w, h: h, pts: coords, line: line, area: area, last: pts[pts.length - 1], best: best };
                };

                // Lightweight aggregates over local scores for the summary strip.
                vm.benchmarkStats = function () {
                    var scores = vm.benchmarkScores || [];
                    if (!scores.length) return null;
                    var ok = 0, fail = 0, pts = 0, total = 0;
                    scores.forEach(function (s) {
                        ok += (s.successfulEdits || 0);
                        fail += (s.failedEdits || 0);
                        pts += (s.points || 0);
                        total++;
                    });
                    var avg = total ? Math.round((ok / (ok + fail || 1)) * 100) : 0;
                    return { runs: total, ok: ok, fail: fail, points: pts, avgPass: avg };
                };

                // Count of populated spec rows, for the System specs header badge.
                vm.systemSpecCount = function () {
                    var custom = vm.systemInfoCustom || {};
                    var det = vm.systemInfoDetected || {};
                    var vals = [
                        custom.os || det.os,
                        custom.cpu || det.cpu,
                        custom.ramGb || det.ramBytes,
                        custom.gpu || det.gpu,
                        custom.model
                    ];
                    return vals.filter(function (v) { return v !== undefined && v !== null && v !== ''; }).length;
                };

                // ── Compare two local scores side-by-side ────────────────────────
                vm.compareMode = false;
                vm.compareA = null;
                vm.compareB = null;
                vm.compareResult = null;

                vm.startCompareMode = function () {
                    vm.compareMode = true;
                    vm.compareA = null;
                    vm.compareB = null;
                    vm.compareResult = null;
                    vm.selectedBenchmarkScore = null;
                };
                vm.exitCompareMode = function () {
                    vm.compareMode = false;
                    vm.compareA = null;
                    vm.compareB = null;
                    vm.compareResult = null;
                };

                vm.toggleCompareScore = function (s) {
                    if (vm.compareA === s) { vm.compareA = null; }
                    else if (vm.compareB === s) { vm.compareB = null; }
                    else if (!vm.compareA) { vm.compareA = s; }
                    else if (!vm.compareB) { vm.compareB = s; }
                    else { vm.compareA = s; }
                    vm.compareResult = vm.compareData();
                };

                vm.benchEditOk = function (e) { return !!(e && (e.status === 'done' || e.status === 'created' || e.status === 'applied')); };
                vm.benchEditFail = function (e) { return !!(e && (e.status === 'error' || e.status === 'failed' || e.status === 'rejected')); };
                vm.benchEditStatusClass = function (e) {
                    if (vm.benchEditOk(e)) return 'ok';
                    if (vm.benchEditFail(e)) return 'bad';
                    return 'neutral';
                };
                vm.benchEditGlyph = function (e) {
                    if (vm.benchEditOk(e)) return '✓';
                    if (vm.benchEditFail(e)) return '✕';
                    return '○';
                };

                // Matches the two scores' edit records by normalized path + type
                // (+ step index when present, so distinct edits to the same file
                // — e.g. create then append — surface as separate rows) and
                // classifies each row so the UI can highlight where they differ.
                vm.compareData = function () {
                    var a = vm.compareA, b = vm.compareB;
                    if (!a || !b) return null;
                    var norm = function (p) { return String(p || '').replace(/\\/g, '/').toLowerCase(); };
                    var editKey = function (e) {
                        var k = norm(e.path);
                        if (e.index != null && e.index !== undefined && e.index !== null) return k + '|' + (e.type || 'edit') + '|' + e.index;
                        return k + '|' + (e.type || 'edit') + '|' + (e.status || '');
                    };
                    var byKey = {};
                    (a.edits || []).forEach(function (e) {
                        var k = editKey(e);
                        byKey[k] = byKey[k] || { a: null, b: null };
                        byKey[k].a = e;
                    });
                    (b.edits || []).forEach(function (e) {
                        var k = editKey(e);
                        byKey[k] = byKey[k] || { a: null, b: null };
                        byKey[k].b = e;
                    });
                    var rows = Object.keys(byKey).map(function (k) {
                        var pair = byKey[k];
                        var ea = pair.a, eb = pair.b;
                        var okA = vm.benchEditOk(ea), okB = vm.benchEditOk(eb);
                        var failA = vm.benchEditFail(ea), failB = vm.benchEditFail(eb);
                        var state;
                        if (ea && eb) {
                            if (okA && okB) state = 'both-ok';
                            else if (failA && failB) state = 'both-fail';
                            else if (okA && failB) state = 'a-ok-b-fail';
                            else if (failA && okB) state = 'a-fail-b-ok';
                            else if (okA) state = 'a-ok-b-mixed';
                            else if (okB) state = 'a-mixed-b-ok';
                            else state = 'both-mixed';
                        } else if (ea) {
                            state = okA ? 'only-a-ok' : (failA ? 'only-a-fail' : 'only-a');
                        } else {
                            state = okB ? 'only-b-ok' : (failB ? 'only-b-fail' : 'only-b');
                        }
                        var label = (ea && ea.path) || (eb && eb.path) || k;
                        return { path: label, a: ea, b: eb, okA: okA, okB: okB, failA: failA, failB: failB, state: state };
                    }).sort(function (x, y) { return x.path.localeCompare(y.path); });
                    var differs = rows.filter(function (r) { return r.state === 'a-ok-b-fail' || r.state === 'a-fail-b-ok'; });
                    return { a: a, b: b, rows: rows, differs: differs.length };
                };

                vm.sendBenchmarkToServer = function (s) {
                    if (!s || !s.id || vm._sendingBenchmarkIds && vm._sendingBenchmarkIds[s.id]) return;
                    vm._sendingBenchmarkIds = vm._sendingBenchmarkIds || {};
                    vm._sendingBenchmarkIds[s.id] = true;
                    var benchmarkDto = {
                        ClientId: vm.bughostedClientId,
                        Token: vm.bughostedClientId,
                        Date: s.date,
                        Benchmark: String(s.level ?? ''),
                        Steps: String(s.successfulEdits ?? '') + "/" + String((s.successfulEdits || 0) + (s.failedEdits || 0)),
                        Score: String(s.scorePercent ?? '0'),
                        Status: String(s.status ?? ''),
                        Duration: s.durationMs ? String(s.durationMs) : '0',
                        Model: String(s.modelUsed ?? ''),
                        OS: String(vm.systemInfoCustom.os || vm.systemInfoDetected.os || ''),
                        CPU: String(vm.systemInfoCustom.cpu || vm.systemInfoDetected.cpu || ''),
                        RAM: String(vm.systemInfoCustom.ramGb || vm.systemInfoDetected.ramBytes || ''),
                        GPU: String(vm.systemInfoCustom.gpu || vm.systemInfoDetected.gpu || '')
                    };

                    $http.post('/api/bughosted/addbenchmark', benchmarkDto)
                        .then(function (response) {
                            console.log('Successfully sent benchmark to server:', response.data);
                            alert('Benchmark successfully sent to BugHosted!');
                        })
                        .catch(function (error) {
                            console.error('Error sending benchmark to server:', error);
                            if (error && error.message) {
                                alert('Failed to send benchmark. Error details:\n' + error.message);
                            } else {
                                alert('Failed to send benchmark due to an unknown error.');
                            }
                        })
                        .finally(function () {
                            delete vm._sendingBenchmarkIds[s.id];
                        });
                };
                vm.msToDigitalTime = function (ms) {
                    if (!ms || isNaN(ms)) return '00:00:00';
                    return new Date(ms).toISOString().slice(11, 19);
                }
                vm.fetchBenchmarksFromServer = function () {
                    if (!vm.bughostedClientId) {
                        alert('Not connected to BugHosted. Login first.');
                        return;
                    }
                    vm.fetchingBenchmarks = true;
                    $http.get('/api/bughosted/benchmarks?token=' + encodeURIComponent(vm.bughostedClientId))
                        .then(function (resp) {
                            vm.serverBenchmarks = resp.data || [];
                            vm.fetchingBenchmarks = false;
                        })
                        .catch(function (error) {
                            vm.fetchingBenchmarks = false;
                            console.error('Error fetching benchmarks:', error);
                            var msg = error.data && (error.data.detail || error.data.error || error.data.title) || error.message || 'Unknown error';
                            alert('Failed to fetch benchmarks:\n' + msg);
                        });
                };
                vm.saveSystemInfo = function () { $http.post('/api/benchmark/system-info', vm.systemInfoCustom).then(function () { vm.systemInfoSaved = true; $timeout(function () { vm.systemInfoSaved = false; }, 2000); }); };
                vm.resetSystemInfo = function () { vm.systemInfoCustom = { os: '', cpu: '', ramGb: null, gpu: '', model: '', benchmarkProjectRoot: '' }; vm.saveSystemInfo(); };
                vm.deleteBenchmarkScore = function (score) { $http.delete('/api/benchmark/scores/' + encodeURIComponent(score.id)).then(function () { var idx = vm.benchmarkScores.indexOf(score); if (idx >= 0) vm.benchmarkScores.splice(idx, 1); if (vm.compareA === score) vm.compareA = null; if (vm.compareB === score) vm.compareB = null; vm.compareResult = vm.compareData(); }).catch(function () { }); };
            }
        };
    }]);