angular.module('kanbanApp')
    .factory('AgentMixin', ['$http', '$timeout', '$interval', '$window', '$q', function ($http, $timeout, $interval, $window, $q) {
        var _lastLogKey = '';
        var _suggestionContextCache = new WeakMap();
        function uid() { return Math.random().toString(36).slice(2, 9); }
        // Pure guard: should a suggestion generation start for this card? Mirrors the
        // entry checks of vm.suggestImprovements (tests/js/suggestion-cancel.test.js
        // extracts these helpers from the live source).
        function shouldStartSuggestions(card, maxSuggestions, topup) {
            if (!card || maxSuggestions <= 0) return false;
            if (topup) {
                return Array.isArray(card._suggestions) && card._suggestions.length < maxSuggestions && !card._suggestionsGenerating;
            }
            return !(card._suggestions || card._suggestionsRequested);
        }
        // Pure cancel-state transition for an in-flight suggestion generation: marks the
        // card cancelled, aborts the pending $http call via its deferred (if any), resets
        // the generation flags, and reports whether a generation was actually in flight so
        // callers can log accordingly. Does NOT delete _suggestionCancel — the promise
        // handler (or the next generation) owns that, matching vm.cancelCardSuggestions.
        function abortSuggestionGeneration(card) {
            var wasGenerating = !!(card._suggestionsGenerating || card._suggestionCancel);
            card._suggestionsCancelled = true;
            if (card._suggestionCancel) {
                try { card._suggestionCancel.resolve(); } catch (e) { }
            }
            card._suggestionsGenerating = false;
            card._suggestionsRequested = false;
            card._suggestionsError = null;
            return wasGenerating;
        }
        // Pure companion to abortSuggestionGeneration for in-place text edits: a card's
        // suggestions were generated against its OLD text, so after the text changes they
        // are stale and must be dropped (cancel alone keeps them for display). Returns
        // whether the card had any suggestion state at all — callers can skip work when
        // there is nothing to invalidate. (tests/js/suggestion-cancel.test.js extracts it.)
        function clearStaleSuggestions(card) {
            var hadState = !!(card._suggestions || card._suggestionsRequested || card._suggestionsGenerating || card._suggestionCancel);
            if (hadState) {
                card._suggestions = undefined;
                card._suggestionsNone = false;
                card._suggestionsSaturated = false;
            }
            return hadState;
        }
        // Pure guard for the pr/finish completion path: if BRANCH was toggled off while
        // the card was running, the finish POST must not re-record any PR state — the
        // card's prStatus is cleared instead ("skipped") so no stale "PR: weaver/xxx"
        // tag can come back once the response lands. Mirrors the pr/start response race
        // guard. Returns the outcome: 'noop' (no branch state to resolve), 'skipped'
        // (BRANCH off — prStatus cleared), 'pr-created', or 'error'.
        // (tests/js/pr-finish-guard.test.js extracts this helper.)
        function applyPrFinishOutcome(card, prResp, err) {
            if (!card || !card.prStatus) return 'noop';
            if (!card.autoPr) { delete card.prStatus; return 'skipped'; }
            if (err) {
                card.prStatus = { status: 'error', error: err.statusText || 'PR failed', branch: card.prStatus.branch };
                return 'error';
            }
            if (prResp && prResp.data && prResp.data.success) {
                card.prStatus = { status: 'pr-created', branch: card.prStatus.branch, prUrl: prResp.data.prUrl };
                return 'pr-created';
            }
            card.prStatus = { status: 'error', error: (prResp && prResp.data && prResp.data.error) || 'PR creation failed', branch: card.prStatus.branch };
            return 'error';
        }
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
                // Focus-stats metrics (bootstrap auto-read, _discover, _explore) aggregate into
                // vm.focusStats for the panel's stat row — run-level totals of files focused and
                // chars saved, plus the effective focus threshold from the bootstrap phase.
                if (level === 'metric' && detail && detail.kind === 'focusStats') {
                    var fs = vm.focusStats || { files: 0, chars: 0, threshold: null };
                    fs.files += (detail.filesFocused || 0);
                    fs.chars += (detail.charsSaved || 0);
                    if (detail.threshold != null) fs.threshold = detail.threshold;
                    vm.focusStats = fs;
                }
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
                vm.aiPrompt = ''; vm.aiResponse = ''; vm.activeCardText = ''; vm.activeCardId = null;
                vm.activeCardIds = new Set(); vm.aiChatMessages = []; vm.aiChatInput = ''; vm.aiChatLoading = false; vm.chatMode = 'ask';
                vm.streamingActive = false; vm.streamingThinking = ''; vm.streamingSummary = ''; vm._agentStopped = false; vm.streamingPhase = '';
                vm.streamingContextSize = 0; vm.streamingContextChars = 0; vm.streamingContextBreakdown = []; vm.streamingSteps = []; vm.streamingFilesEdited = []; vm.streamingTokenBuffer = '';
                vm.streamingStableCount = 0; vm.activeStepIndex = null; vm.agentResult = null; vm.steeringContext = ''; vm.clarificationReply = '';
                vm.abortController = new AbortController(); vm.planItems = []; vm.cohesionIssues = []; vm.cohesionFile = '';
                vm.agentRuns = []; vm.currentRun = null;
                vm.refreshStreamingActive = function () {
                    var activeNow = vm.agentRuns.filter(function (r) { return r.active; }).length;
                    var wasActive = vm._lastActiveRunCount || 0;
                    vm._lastActiveRunCount = activeNow;
                    vm.streamingActive = activeNow > 0;
                    if (!vm.streamingActive) { vm.resumeTerminalPolling(); }
                    if (activeNow === 0 && vm.reconcileBenchmarkRunning) { vm.reconcileBenchmarkRunning(); }
                    if (activeNow < wasActive) {
                        $timeout(function () { if (vm.processQueuedCards) { vm.processQueuedCards(); } }, 100);
                    }
                    // The agent just went fully idle — give the board a moment to
                    // settle, then start topping up Done-card suggestions.
                    if (activeNow === 0 && wasActive > 0) {
                        $timeout(function () { if (vm.kickIdleSuggestions) vm.kickIdleSuggestions(); }, 2500);
                    }
                };
                vm.isEndpointBusy = function (endpointId) {
                    var ep = endpointId || '';
                    return vm.agentRuns.some(function (r) { return r.active && (r.endpointId || '') === ep; });
                };
                vm.regularAgentActive = function () {
                    return vm.agentRuns.some(function (r) { return r.active && !r.selfImproving; });
                };
                vm.activeRunCount = function () {
                    return vm.agentRuns.filter(function (r) { return r.active; }).length;
                };
                vm._drainingQueue = false;
                vm.processQueuedCards = function () {
                    if (vm._drainingQueue || !vm.state) return;
                    vm._drainingQueue = true;
                    try {
                        var selfImprovingArmed = vm.selfImprovingAgentActive === true && !vm.regularAgentActive();
                        var candidates = [];
                        (vm.state.todo || []).forEach(function (c) {
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
                // A step whose output is web data (search results or a fetched page) renders in a
                // dedicated collapsible '🌐 Web results' block instead of the generic output box.
                // Matches both plan-marker types (_web_search/_web_fetch) and the command-pipeline
                // types (web_search/web_fetch).
                vm.isWebStep = function (s) {
                    if (!s) return false;
                    var t = s.type || '';
                    return t === '_web_search' || t === '_web_fetch' || t === 'web_search' || t === 'web_fetch';
                };
                // Copies a step's web results (output, else query/url) to the clipboard,
                // flashing '✓' on the button. Stops propagation so the parent <details>
                // summary toggle and card click don't fire.
                vm.copyStepOutput = function (s, evt, preferFull) {
                    if (evt && evt.stopPropagation) evt.stopPropagation();
                    if (evt && evt.preventDefault) evt.preventDefault();
                    var text = (s && (preferFull ? (s.output || s.focusedOutput || s.query || s.url) : (s.focusedOutput || s.output || s.query || s.url))) || '';
                    var btn = evt && evt.currentTarget;
                    var done = function () {
                        if (!btn) return;
                        btn.classList.add('copied');
                        setTimeout(function () { btn.classList.remove('copied'); }, 1200);
                    };
                    var legacy = function () {
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
                    };
                    try {
                        if (navigator.clipboard && navigator.clipboard.writeText) {
                            navigator.clipboard.writeText(text).then(done, function () { legacy(); done(); });
                        } else { legacy(); done(); }
                    } catch (e) { legacy(); done(); }
                };
                vm.buildTools = [
                    { name: 'Ping', icon: '📡', desc: 'Check host connectivity (TCP/ping/HTTP)', hint: 'ping google.com -n 4' },
                    { name: 'Install Package', icon: '📦', desc: 'Install a NuGet/npm/pip package', hint: 'install package SonarAnalyzer.CSharp' },
                    { name: 'Build', icon: '🔨', desc: 'Run build verification', hint: 'build the project' },
                    { name: 'Full Agent', icon: '🤖', desc: 'Run the full agent pipeline', hint: 'refactor the login page' }
                ];
                vm.benchmarkScores = []; vm.serverBenchmarks = []; vm.benchmarkPlans = []; vm.benchmarkRunning = false; vm.benchmarkLevel = null; vm.selectedBenchmarkScore = null; vm.selectedServerBenchmark = null; vm.benchmarkPlanNames = {}; vm.fetchingBenchmarks = false;
                vm.benchmarkAllActive = false; vm.benchmarkAllResults = []; vm.benchmarkAllResult = null; vm._benchmarkQueue = [];
                // Per-card run heartbeat: each live run stamps run.heartbeat on every
                // stream chunk (the server also emits a keepalive every 15s), and the
                // watchdog below flips any run whose heartbeat goes stale to
                // 'interrupted' instead of leaving it 'running' forever. Cards whose
                // run isn't live in this tab (reload, dropped stream, no done event)
                // then render as interrupted (amber), never as actively running.
                vm._runHeartbeatTimeoutMs = 45000;
                vm._cardLiveRun = function (card) {
                    if (!card) return null;
                    var runs = vm.agentRuns || [];
                    for (var i = 0; i < runs.length; i++) {
                        var r = runs[i];
                        if (r && r.active && r.cardId === card.id) return r;
                    }
                    return null;
                };
                vm._runHeartbeatStale = function (run) {
                    return !!(run && run.heartbeat && (Date.now() - run.heartbeat) > vm._runHeartbeatTimeoutMs);
                };
                vm._benchmarkLiveRun = function () {
                    var runs = vm.agentRuns || [];
                    for (var i = 0; i < runs.length; i++) {
                        var r = runs[i];
                        if (!r || !r.active) continue;
                        if (vm.benchmarkAllActive) return r;
                        var card = vm.findCardById ? vm.findCardById(r.cardId) : null;
                        if (card && card._benchmark) return r;
                    }
                    return null;
                };
                vm.cardRunInterrupted = function (card) {
                    if (!card) return false;
                    if (vm.isCardActive && vm.isCardActive(card.id)) return false;
                    var live = vm._cardLiveRun(card);
                    if (card._benchmark) {
                        var inDoing = (vm.state.doing || []).some(function (c) { return c.id === card.id; });
                        var inTodoReady = (vm.state.todo || []).some(function (c) { return c.id === card.id && c.ready; });
                        if (!inDoing && !inTodoReady) return false;
                        if (card._endpointQueued) return false;
                        if (live) return vm._runHeartbeatStale(live);
                        return true;
                    }
                    return !!card._runInterrupted;
                };
                vm.useToolHint = function (hint) { vm.aiChatInput = hint; var el = document.querySelector('.ai-chat-body input'); if (el) el.focus(); };
                vm.toggleChatMode = function () { vm.chatMode = vm.chatMode === 'ask' ? 'build' : 'ask'; };
                // Token estimate mirroring AgentTokenMetrics.EstimateTokens in the C# server:
                // single spaces free, words split at case boundaries (≤10 chars = 1 token),
                // digit runs ~1/3, punctuation 1 per same-char run, quoted literals ~4 chars/token.
                function partTokens(part) { return part.length <= 10 ? 1 : Math.ceil(part.length / 4); }
                function digitTokens(len) { return len <= 3 ? 1 : Math.ceil(len / 3); }
                function wordTokens(word) {
                    for (var j = 0; j < word.length; j++) if (word.charCodeAt(j) > 127) return word.length;
                    var total = 0, start = 0;
                    for (var k = 1; k < word.length; k++) {
                        if (word[k] >= 'A' && word[k] <= 'Z' && word[k - 1] >= 'a' && word[k - 1] <= 'z') {
                            total += partTokens(word.slice(start, k));
                            start = k;
                        }
                    }
                    total += partTokens(word.slice(start));
                    return Math.max(1, total);
                }
                function estimateTokens(text) {
                    if (!text) return 0;
                    var total = 0, i = 0, n = text.length;
                    while (i < n) {
                        var c = text[i];
                        if (c === '"' || c === "'" || c === '`') {
                            var qStart = i; i++;
                            while (i < n) {
                                if (text[i] === '\\') { i += 2; continue; }
                                if (text[i] === c) { i++; break; }
                                i++;
                            }
                            total += Math.max(1, Math.floor((i - qStart) / 4));
                            continue;
                        }
                        if (/[a-zA-Z0-9_]/.test(c)) {
                            var wStart = i;
                            while (i < n && /[a-zA-Z0-9_]/.test(text[i])) i++;
                            var run = text.slice(wStart, i);
                            total += /^[0-9]/.test(run) ? digitTokens(run.length) : wordTokens(run);
                            continue;
                        }
                        if (/\s/.test(c)) {
                            var sStart = i;
                            while (i < n && /\s/.test(text[i])) i++;
                            var wLen = i - sStart;
                            if (wLen >= 2) total += Math.floor(wLen / 4);
                            continue;
                        }
                        while (i < n && text[i] === c) i++;
                        total++;
                    }
                    return total;
                }
                vm.logFileSizeAndTokens = function (filePath, content) {
                    if (!filePath || !content) return; const fileSize = content.length; const tokenCount = estimateTokens(content);
                    if (vm.addLogEntry) vm.addLogEntry({ type: 'debug', message: `File: ${filePath} | Size: ${fileSize} chars | Tokens: ~${tokenCount}` });
                };
                vm.executeAgent = function (card, isAutoRestart) {
                    if (!card || !card.text) return;
                    // Any card starting means the board is no longer idle — cancel every
                    // in-flight suggestion generation (idle-loop or manual) so they stop
                    // burning the endpoint and can't land on a card that moved back to
                    // To Do. The idle chain resumes on the next idle spell.
                    if (vm.cancelCardSuggestions && vm.state) {
                        ['todo', 'doing', 'done', 'archived', 'selfImproving'].forEach(function (col) {
                            (vm.state[col] || []).forEach(function (c) {
                                if (c && (c._suggestionsGenerating || c._suggestionCancel)) vm.cancelCardSuggestions(c);
                            });
                        });
                    }
                    if (vm.agentRuns.some(function (r) { return r.cardId === card.id && r.active; })) return;
                    if (card.selfImproving && vm.selfImprovingAgentActive !== true) {
                        vm.selfImprovingAgentActive = true;
                        if (vm.persistSelfImprovingAgent) vm.persistSelfImprovingAgent();
                    }
                    var cardEndpoint = card.llmEndpointId || '';
                    if (vm.isEndpointBusy(cardEndpoint)) {
                        card.ready = true;
                        card._endpointQueued = true;
                        var parkCol = card.selfImproving ? 'selfImproving' : 'todo';
                        if (vm.state) {
                            var doingIdx = (vm.state.doing || []).findIndex(function (c) { return c.id === card.id; });
                            if (doingIdx !== -1) {
                                var parkedCard = vm.state.doing.splice(doingIdx, 1)[0];
                                if (!vm.state[parkCol]) vm.state[parkCol] = [];
                                vm.state[parkCol].push(parkedCard);
                            } else {
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
                            startedAt: Date.now(), elapsed: 0, heartbeat: Date.now(),
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
                            vm.streamingPhase = ''; vm.streamingContextSize = 0; vm.streamingContextChars = 0; vm.streamingContextBreakdown = []; vm.streamingTokenBuffer = ''; vm.streamingStableCount = 0;
                            vm.focusStats = null;
                            vm.complexityScore = null; vm.complexityLabel = ''; vm.complexityTokenCap = null; vm.complexityMaxTokens = null; vm.complexityAtomicSteps = null;
                            vm.cohesionIssues = []; vm.cohesionFile = '';
                            vm.llmProgress = null; vm.llmProgressPercent = null; vm.llmProgressState = '';
                            vm.activeStepIndex = null; vm.streamingActive = true; vm.pauseTerminalPolling();
                            vm._agentStartTime = Date.now();
                            run.heartbeat = Date.now();
                            if (vm.agentTimer) { $interval.cancel(vm.agentTimer); vm.agentTimer = null; }
                            vm.agentTimer = $interval(function () {
                                if (vm.streamingActive) {
                                    vm.agentElapsed = (vm._agentStartTime ? Date.now() - vm._agentStartTime : 0);
                                }
                                var hbStale = false;
                                vm.agentRuns.forEach(function (r) {
                                    if (r.active) r.elapsed = Date.now() - r.startedAt;
                                    if (r.active && vm._runHeartbeatStale(r)) {
                                        r.active = false; r.status = 'interrupted';
                                        if (vm.currentRun === r) vm.currentRun = null;
                                        var hbCard = vm.findCardById ? vm.findCardById(r.cardId) : null;
                                        if (hbCard) hbCard._runInterrupted = true;
                                        pushAgentLog(vm, 'warn', '⚠ Run interrupted — stream heartbeat lost, no events for ' + Math.round((Date.now() - r.heartbeat) / 1000) + 's. Card marked as interrupted.');
                                        hbStale = true;
                                    }
                                });
                                if (hbStale) {
                                    vm.refreshStreamingActive();
                                    $scope.$applyAsync();
                                }
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
                            var payload = { prompt: card.text, project: proj, files: files, maxIterations: 5, maxStepsPerBatch: 8, steeringContext: vm.steeringContext || '', selfImproving: card.selfImproving || false, isDecomposing: card.isDecomposing || false, createTests: card.createTests || false, cardId: card.id, isBenchmark: card._benchmark || false, benchmarkProjectRoot: (card._benchmark && vm.systemInfoCustom && vm.systemInfoCustom.benchmarkProjectRoot) || '', buildCommands: vm.getProjectBuildCommands(proj) || null, endpointId: card.llmEndpointId || '', runId: run.runId };
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
                                                run.heartbeat = Date.now();
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
                                                                if (parsed && parsed.contextChars) { vm.streamingContextChars = parsed.contextChars; }
                                                                if (parsed && Array.isArray(parsed.contextBreakdown)) { vm.streamingContextBreakdown = parsed.contextBreakdown; }
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
                                                                     // When the agent finishes a step that produced diffs, those diffs
                                                                     // are applied by the agent during the run — record them so the
                                                                     // Apply button stays hidden across reloads (and other tabs).
                                                                     if (parsed.status === 'done' && parsed.diffs && parsed.diffs.length) {
                                                                         var appliedCard = vm.findCardById ? vm.findCardById(vm.activeCardId) : null;
                                                                         if (appliedCard) {
                                                                             if (!appliedCard._appliedDiffs) appliedCard._appliedDiffs = {};
                                                                             parsed.diffs.forEach(function (d) { appliedCard._appliedDiffs[d] = true; });
                                                                             if (vm.saveCards) vm.saveCards();
                                                                         }
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
                                                                // A lone benchmark (or any single card) chimes on completion, but a
                                                                // Run-All batch defers the gold skulltula to _finishBenchmarkAll so it
                                                                // plays once for the whole batch — not once per level.
                                                                if (!(card._benchmark && vm.benchmarkAllActive)) vm.sendSystemToast();
                                                                vm.steeringContext = '';
                                                                var elapsed = vm._agentStartTime ? Date.now() - vm._agentStartTime : 0;
                                                                run.active = false; run.status = 'done'; run.elapsed = Date.now() - run.startedAt; vm.refreshStreamingActive();
                                                                if (vm.loadEndpointHealth) vm.loadEndpointHealth();
                                                                var editsApplied = parsed && parsed.editsApplied;
                                                                var incomplete = parsed && parsed.incomplete;
                                                                if (card.id !== vm.activeCardId) {
                                                                    if (card._benchmark && recordBenchmarkScore) { recordBenchmarkScore((parsed && parsed.warning) || ''); }
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
                                                                        // Audit trail: scheduled (cron) cards are deleted when done, so record
                                                                        // the outcome + duration BEFORE the card disappears from the board.
                                                                        if (mvCard._fromCron) vm.cronRunLogEnd(mvCard, incomplete ? 'error' : 'success', run.elapsed, concAnalysis.summary);
                                                                        // Scheduled (cron) cards are one-shot jobs — don't keep them on the
                                                                        // board (the card is already spliced out, so just skip the done push).
                                                                        if (!mvCard._fromCron) {
                                                                            vm.state.done.push(mvCard);
                                                                        }
                                                                        vm.saveCards();
                                                                        if (vm.suggestImprovements && mvCol === 'doing' && !mvCard.selfImproving && !mvCard._fromCron) vm.suggestImprovements(mvCard, concAnalysis.summary, proj);
                                                                    } else if (vm.saveCards) { vm.saveCards(); }
                                                                    if (vm.reconcileBenchmarkRunning) vm.reconcileBenchmarkRunning();
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
                                                                function recordBenchmarkScore(errorReason) {
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
                                                                    var bmElapsed = run.elapsed || (vm._agentStartTime ? Date.now() - vm._agentStartTime : 0);
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
                                                                            errorReason: errorReason !== undefined ? errorReason : (vm.agentResult && (vm.agentResult.error || vm.agentResult.warning) || ''),
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
                                                                        if (card._fromCron) {
                                                                            // Scheduled (cron) cards are one-shot jobs: when they finish,
                                                                            // delete the card instead of keeping it on the board.
                                                                            pushAgentLog(vm, 'log', 'Plan completed — deleting scheduled card (one-shot job).');
                                                                            // Audit trail: the card is about to be deleted, so record the run
                                                                            // outcome + duration on the calendar card's run log first.
                                                                            vm.cronRunLogEnd(card, 'success', run.elapsed, finalSummary);
                                                                            vm.deleteCard(card.id, 'doing');
                                                                            $timeout(function () {
                                                                                if (!vm.autoQueue) return;
                                                                                vm.processQueuedCards();
                                                                            }, 500);
                                                                            return;
                                                                        }
                                                                        pushAgentLog(vm, 'log', `Plan completed — moving card to ${card.selfImproving ? 'Self-Improving' : 'Done'} column.`);
                                                                        vm.moveCardToDone(card);
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
                                                                        var outcome = applyPrFinishOutcome(card, prResp, null);
                                                                        if (outcome === 'pr-created') { pushAgentLog(vm, 'info', 'PR created: ' + (card.prStatus.prUrl || 'Check your repository')); }
                                                                        else if (outcome === 'error') { pushAgentLog(vm, 'warn', 'PR creation: ' + card.prStatus.error); }
                                                                        else if (outcome === 'skipped') { pushAgentLog(vm, 'info', 'BRANCH was toggled off — skipping PR creation'); }
                                                                        finishCard();
                                                                    }, function (err) {
                                                                        var outcome = applyPrFinishOutcome(card, null, err);
                                                                        if (outcome === 'error') { pushAgentLog(vm, 'warn', 'PR creation failed: ' + card.prStatus.error); }
                                                                        else if (outcome === 'skipped') { pushAgentLog(vm, 'info', 'BRANCH was toggled off — skipping PR creation'); }
                                                                        finishCard();
                                                                    });
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
                                if (resp.data && resp.data.success) {
                                    if (card.autoPr) { card.prStatus = { status: 'branch-created', branch: resp.data.branchName, originalBranch: resp.data.originalBranch }; pushAgentLog(vm, 'info', 'PR branch: ' + card.prStatus.branch); }
                                    else { pushAgentLog(vm, 'info', 'PR branch was created but BRANCH was toggled off — card continues without a branch'); }
                                }
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
                // ── Cron run log ───────────────────────────────────────────────
                // Scheduled (cron) cards are deleted when they finish, so their run
                // summary would be lost with them. Record the outcome + duration on
                // the calendar card's run log (stored in board data by CalendarMixin
                // as vm.state._cronRunLog) so one-shot jobs leave an audit trail.
                vm.cronRunLogEnd = function (card, outcome, elapsedMs, summary) {
                    if (!card || !card._fromCron) return;
                    try {
                        if (!vm.state) return;
                        if (!Array.isArray(vm.state._cronRunLog)) return;
                        var sourceId = card._cronSourceId || '';
                        var textKey = 'text:' + String(card.text || '').trim() + '|' + String(card._cronExpression || '').trim();
                        // Update the newest matching entry in place (the fire record).
                        var found = false;
                        for (var i = 0; i < vm.state._cronRunLog.length; i++) {
                            var e = vm.state._cronRunLog[i];
                            if (!e) continue;
                            var matches = sourceId ? (e.key === 'id:' + sourceId) : (e.key === textKey);
                            if (!matches || e.cardId !== card.id) continue;
                            e.outcome = outcome;
                            e.durationMs = elapsedMs || e.durationMs || 0;
                            if (summary) e.summary = summary;
                            found = true;
                            break;
                        }
                        if (!found) {
                            // No fire record (e.g. card created before logging) — add one.
                            vm.state._cronRunLog.unshift({
                                key: sourceId ? 'id:' + sourceId : textKey,
                                firedAt: card.createdAt || new Date().toISOString(),
                                outcome: outcome,
                                durationMs: elapsedMs || 0,
                                summary: summary || '',
                                cardId: card.id
                            });
                            if (vm.state._cronRunLog.length > 100) vm.state._cronRunLog.length = 100;
                        }
                        // Mark the card as resolved so a later manual delete of the
                        // same card doesn't overwrite this outcome with 'stopped'.
                        card._cronResolved = true;
                        if (vm.saveCards) vm.saveCards();
                    } catch (e) { console.log('cronRunLogEnd error', e); }
                };

                // Tooltip for a scheduled (cron) card's ⏰ chip: explains the card
                // came from a calendar job, naming the schedule label and expression.
                vm.cronChipTitle = function (card) {
                    if (!card || !card._fromCron) return '';
                    var label = card._cronLabel;
                    var expr = card._cronExpression;
                    var mid = label ? ' — ' + label : (expr ? ' — ' + expr : '');
                    if (label && expr) mid += ' (' + expr + ')';
                    return 'This card was created by a scheduled calendar job' + mid + '.';
                };

                vm.suggestImprovements = function (card, summary, project, opts) {
                    if (!card) return false;
                    var proj = project || card.filePath || vm.selectedProject;
                    if (!proj) return false;
                    var maxSuggestions = vm.projectMaxSuggestions(proj);
                    var topup = !!(opts && opts.topup);
                    if (!shouldStartSuggestions(card, maxSuggestions, topup)) return false;
                    // A fresh generation clears any earlier cancellation so the request can
                    // complete normally; _suggestionCancel aborts the in-flight $http call
                    // when the card is moved back to To Do or another card starts.
                    card._suggestionsCancelled = false;
                    card._suggestionCancel = $q.defer();
                    card._suggestionsRequested = true;
                    card._suggestionsGenerating = true;
                    card._suggestionsError = null;
                    vm.saveCards();
                    pushAgentLog(vm, 'info', topup ? '💡 Topping up suggestions (More like this)…' : '💡 Suggesting improvements for completed card…');
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
                        thinking: (analysis.thinking || '').slice(0, 6000),
                        steps: stepLog,
                        planItems: planLog,
                        filesEdited: filesEdited
                    };
                    payload.maxSuggestions = maxSuggestions;
                    if (topup) { payload.topup = true; payload.existing = card._suggestions; }
                    $http.post('/api/agent/suggest-improvements', payload, { timeout: card._suggestionCancel.promise }).then(function (resp) {
                        if (card._suggestionsCancelled) { delete card._suggestionCancel; return; }
                        delete card._suggestionCancel;
                        var suggestions = (resp.data && resp.data.suggestions) || [];
                        card._suggestionsGenerating = false;
                        card._suggestions = suggestions;
                        vm.saveCards();
                        if (suggestions.length) {
                            pushAgentLog(vm, 'success', topup ? '💡 Topped up to ' + suggestions.length + ' suggestion(s) on the card.' : '💡 ' + suggestions.length + ' improvement suggestion(s) added to the card.');
                            // Background (idle-loop) runs skip the toast — they'd spam
                            // the screen while filling a whole board of Done cards.
                            if (!(opts && opts.idle) && vm.showSideToast) vm.showSideToast(topup ? '💡 Topped up to ' + suggestions.length + ' suggestion(s) on the card' : '💡 ' + suggestions.length + ' improvement suggestion(s) added to the card');
                        } else {
                            pushAgentLog(vm, 'info', '💡 No improvement suggestions generated for this card.');
                        }
                        if (opts && opts.onDone) opts.onDone(true);
                    }, function (err) {
                        if (card._suggestionsCancelled) {
                            // Aborted because the card moved back to To Do or another card
                            // started — leave the card's flags as the cancel set them, and
                            // still release the idle chain so it can resume next idle spell.
                            delete card._suggestionCancel;
                            if (opts && opts.onDone) opts.onDone(false);
                            return;
                        }
                        delete card._suggestionCancel;
                        card._suggestionsGenerating = false;
                        card._suggestionsError = (err && (err.data && err.data.error || err.statusText)) || 'Suggestion generation failed';
                        card._suggestions = card._suggestions || [];
                        vm.saveCards();
                        pushAgentLog(vm, 'warn', '💡 Suggestion generation failed: ' + card._suggestionsError);
                        if (opts && opts.onDone) opts.onDone(false);
                    });
                    return true;
                };
                // Aborts an in-flight suggestion generation for a card and resets its flags
                // so no stale results land on it. Used when a card is sent back to To Do or
                // when ANY card starts (the board is no longer idle — the suggestion process
                // must stop burning the endpoint).
                vm.cancelCardSuggestions = function (card) {
                    if (!card) return;
                    var wasGenerating = abortSuggestionGeneration(card);
                    if (wasGenerating) {
                        // Tell the server to abort the in-flight LLM call for this card so
                        // the endpoint stops burning tokens (fire-and-forget; the client
                        // already discards any late response via _suggestionsCancelled).
                        if (card.id) $http.post('/api/agent/suggest-improvements/cancel', { cardId: card.id }).catch(function () { });
                        pushAgentLog(vm, 'info', '💡 Suggestion generation cancelled — ' + ((card.text || '').slice(0, 80) || 'card') + ' is no longer a completed card.');
                    }
                };
                // A card's text changed while it was a completed card — its suggestions were
                // generated against the old wording. Abort any in-flight generation AND drop
                // the stale suggestions so the card regenerates for its new text. Wired into
                // the in-place edit paths (editCardText, saveCardText, remote changeCardText).
                vm.invalidateCardSuggestions = function (card) {
                    if (!card) return;
                    // Nothing to invalidate — skip entirely so a quiet card is untouched.
                    if (!clearStaleSuggestions(card)) return;
                    vm.cancelCardSuggestions(card);
                };
                vm.moreLikeThis = function (card) {
                    if (!card) return;
                    vm.suggestImprovements(card, null, card.filePath || vm.selectedProject, { topup: true });
                };
                // ── Idle suggestion loop ──────────────────────────────────────
                // When the agent is completely free (no run active, no benchmark,
                // no self-improving cycle), quietly top up Done-column cards that
                // don't yet carry the project's suggestion cap (0-4, default 3). It
                // works through the cards one at a time and keeps going until every
                // Done card is saturated or the user starts the agent again — the
                // armed() check stops the loop the moment anything starts, and it
                // resumes on the next idle spell.
                vm._suggestionIdleTimer = null;
                vm._suggestionIdleBusy = false;
                vm._suggestionIdleChainActive = false;
                vm._suggestionIdlePaused = false; // Session-level pause toggled from the header chip
                vm._idleSuggestionPending = 0; // Done cards still needing suggestions (header chip)

                // Per-project control over the idle suggestion loop. Defaults to ON; a
                // project can disable it so the agent never auto-generates Done-card
                // suggestions while you're working in that project. Resolved by path
                // against vm.projects with the same normalization the backend uses;
                // unknown/unselected projects default to enabled.
                vm.projectIdleSuggestionsEnabled = function () {
                    var proj = vm.selectedProject || '';
                    if (!proj || !Array.isArray(vm.projects)) return true;
                    var norm = function (s) { return String(s || '').replace(/\\/g, '/').replace(/\/+$/g, '').toLowerCase(); };
                    var target = norm(proj);
                    for (var i = 0; i < vm.projects.length; i++) {
                        var p = vm.projects[i];
                        if (!p) continue;
                        if (norm(p.Path || p.path) === target) return p.IdleSuggestions !== false;
                    }
                    return true;
                };
                // Per-project cap on how many suggestions each completed card can get
                // (0-4; default 3). Resolved by path against vm.projects — 0 means no
                // suggestions for that project; unknown/unselected projects default to 3.
                vm.projectMaxSuggestions = function (projPath) {
                    var proj = projPath || vm.selectedProject || '';
                    var fallback = 3;
                    if (!proj || !Array.isArray(vm.projects)) return fallback;
                    var norm = function (s) { return String(s || '').replace(/\\/g, '/').replace(/\/+$/g, '').toLowerCase(); };
                    var target = norm(proj);
                    for (var i = 0; i < vm.projects.length; i++) {
                        var p = vm.projects[i];
                        if (!p) continue;
                        if (norm(p.Path || p.path) === target) {
                            var m = p.MaxSuggestionsPerCard;
                            return (typeof m === 'number' && m >= 0 && m <= 4) ? Math.round(m) : fallback;
                        }
                    }
                    return fallback;
                };

                vm.suggestionIdleArmed = function () {
                    return !vm.streamingActive
                        && !vm.benchmarkRunning
                        && !vm.benchmarkAllActive
                        && vm.selfImprovingAgentActive !== true
                        && vm.projectIdleSuggestionsEnabled()
                        && !vm._suggestionIdlePaused;
                };

                // Pause/resume the idle suggestion loop on the spot from the kanban
                // header chip. Pausing halts any in-flight chain immediately (the
                // armed() gate stops the next step); resuming kicks the loop right
                // away if the agent is idle. The pause is session-level and does not
                // touch the per-project IdleSuggestions setting.
                vm.toggleIdleSuggestions = function () {
                    vm._suggestionIdlePaused = !vm._suggestionIdlePaused;
                    if (vm._suggestionIdlePaused) {
                        if (vm._suggestionIdleChainActive) {
                            vm._suggestionIdleChainActive = false;
                        }
                        pushAgentLog(vm, 'info', '⏸ Idle suggestions paused — click the header chip to resume.');
                    } else {
                        pushAgentLog(vm, 'info', '▶ Idle suggestions resumed.');
                        $timeout(function () { vm._runIdleSuggestions(); }, 500);
                    }
                    // Keep the header indicator accurate in both directions.
                    vm._idleSuggestionPending = vm._doneCardsNeedingSuggestions().length;
                };

                vm._doneCardsNeedingSuggestions = function () {
                    var need = [];
                    if (!vm.state || !Array.isArray(vm.state.done)) return need;
                    (vm.state.done).forEach(function (c) {
                        if (!c) return;
                        if (c._suggestionsSaturated) return;
                        if (c._suggestionsGenerating) return;
                        var maxFor = vm.projectMaxSuggestions(c.filePath || vm.selectedProject);
                        if (maxFor <= 0) return;
                        if (Array.isArray(c._suggestions) && c._suggestions.length >= maxFor) return;
                        need.push(c);
                    });
                    return need;
                };

                vm._nextIdleSuggestionCard = function () {
                    var cards = vm._doneCardsNeedingSuggestions();
                    for (var i = 0; i < cards.length; i++) {
                        var c = cards[i];
                        // Skip cards whose configured LLM endpoint is currently busy
                        // with user-started work (same guard processQueuedCards uses)
                        // so background suggestions never contend with a live run.
                        if (vm.isEndpointBusy(c.llmEndpointId || '')) continue;
                        // A card that already completed a successful generation with
                        // zero suggestions has nothing relevant — mark it so the LLM
                        // is never re-asked for it (re-asking would just hit the
                        // endpoint again for a card that legitimately earned none).
                        if (c._suggestionsRequested && !c._suggestionsError && Array.isArray(c._suggestions) && c._suggestions.length === 0) {
                            c._suggestionsSaturated = true;
                            c._suggestionsNone = true;
                            if (vm.saveCards) vm.saveCards();
                            continue;
                        }
                        // Skip cards that would bail out of suggestImprovements
                        // (requested but never resolved) so we can't spin on them.
                        if (c._suggestionsRequested && !Array.isArray(c._suggestions)) continue;
                        if (!(c.filePath || vm.selectedProject)) continue;
                        return c;
                    }
                    return null;
                };

                vm._runIdleSuggestions = function () {
                    // Keep the header indicator's pending count fresh on every tick
                    // (interval, chain step, or post-completion re-run).
                    vm._idleSuggestionPending = vm._doneCardsNeedingSuggestions().length;
                    if (vm._suggestionIdleBusy) return;
                    if (!vm.suggestionIdleArmed()) return;
                    var card = vm._nextIdleSuggestionCard();
                    if (!card) {
                        if (vm._suggestionIdleChainActive) {
                            vm._suggestionIdleChainActive = false;
                            pushAgentLog(vm, 'info', '💡 Done-card suggestions filled — idle loop complete.');
                        }
                        return;
                    }
                    if (!vm._suggestionIdleChainActive) {
                        vm._suggestionIdleChainActive = true;
                        pushAgentLog(vm, 'info', '💡 Agent idle — topping up Done-card suggestions…');
                    }
                    vm._suggestionIdleBusy = true;
                    var isTopup = Array.isArray(card._suggestions);
                    var beforeCount = Array.isArray(card._suggestions) ? card._suggestions.length : 0;
                    card._suggestionIdleAttempts = (card._suggestionIdleAttempts || 0) + 1;
                    if (vm.saveCards) vm.saveCards();
                    var fired = vm.suggestImprovements(card, null, card.filePath || vm.selectedProject, {
                        topup: isTopup,
                        idle: true,
                        onDone: function (ok) {
                            vm._suggestionIdleBusy = false;
                            var n = Array.isArray(card._suggestions) ? card._suggestions.length : 0;
                            var maxFor = vm.projectMaxSuggestions(card.filePath || vm.selectedProject);
                            if (ok && n === 0) {
                                // The LLM found nothing relevant — note it and never
                                // retry this card, so the loop doesn't keep hammering
                                // the endpoint for a card that legitimately earned none.
                                card._suggestionsSaturated = true;
                                card._suggestionsNone = true;
                                pushAgentLog(vm, 'info', '💡 No relevant suggestions possible for this card — won\u2019t retry.');
                            } else if (n >= maxFor) {
                                card._suggestionsSaturated = true;
                            } else if (ok && beforeCount > 0 && n <= beforeCount) {
                                // A top-up that added nothing new — the LLM couldn't
                                // extend the set, so stop asking for this card.
                                card._suggestionsSaturated = true;
                            } else if (card._suggestionIdleAttempts >= 3) {
                                // Give the LLM a few chances, then move on so the
                                // loop never hammers the endpoint for one card.
                                card._suggestionsSaturated = true;
                            }
                            if (vm.saveCards) vm.saveCards();
                            // Refresh the chip immediately — a card's suggestions just landed.
                            vm._idleSuggestionPending = vm._doneCardsNeedingSuggestions().length;
                            $timeout(function () { vm._runIdleSuggestions(); }, 1500);
                        }
                    });
                    if (!fired) {
                        // Unexpected bail (e.g. a race with manual generation) —
                        // treat as saturated so the loop can't spin on this card.
                        card._suggestionsSaturated = true;
                        vm._suggestionIdleBusy = false;
                        if (vm.saveCards) vm.saveCards();
                        $timeout(function () { vm._runIdleSuggestions(); }, 800);
                    }
                };

                vm.kickIdleSuggestions = function () {
                    if (!vm.suggestionIdleArmed()) return;
                    $timeout(function () { vm._runIdleSuggestions(); }, 500);
                };

                // Safety-net poller: catches app-start, missed transitions and
                // any card that lands in Done while the agent is already idle.
                if (vm._suggestionIdleTimer) $interval.cancel(vm._suggestionIdleTimer);
                vm._suggestionIdleTimer = $interval(function () {
                    if (!vm._suggestionIdleBusy) vm._runIdleSuggestions();
                }, 10000);
                // ── Suggestion → To-Do card ───────────────────────────────────
                // Clicking a suggestion in the Done column creates a new card in the To Do
                // column pre-filled with the suggestion text and its file attachments (a
                // suggestion may reference multiple files — all are attached). The suggestion
                // is marked _queued so it can't be clicked twice, and the board scrolls to
                // the new card.
                vm.suggestionToCard = function (sourceCard, suggestion) {
                    if (!sourceCard || !suggestion) return;
                    if (suggestion._queued) return;
                    if (!vm.state || !vm.state.todo) return;
                    suggestion._queued = true;
                    var files = Array.isArray(suggestion.files) ? suggestion.files.slice() : [];
                    // The suggestion often builds on the finished work, so prepend the
                    // source card's completion summary as context (bounded so the card
                    // text stays readable). The summary lands first — context, then the
                    // actionable suggestion — so the agent sees what was just done.
                    var summary = (sourceCard.agentAnalysis && sourceCard.agentAnalysis.summary) || '';
                    var contextBlock = '';
                    var srcRef = 'Source card #' + ((sourceCard.id || '').slice(0, 6) || '?');
                    if (summary) {
                        var trimmed = summary.length > 2000 ? summary.slice(0, 2000) + '…' : summary;
                        contextBlock = '[CONTEXT — ' + srcRef + ' — completion summary of the source task]\n' +
                            trimmed + '\n[/CONTEXT]\n\n';
                    } else if (sourceCard.text) {
                        // No summary captured — reference the source card instead so the
                        // agent still knows this builds on completed work. Bracketed so the
                        // planner treats it as context, not as task requirements.
                        contextBlock = '[CONTEXT — ' + srcRef + ' — follows up on the completed source task]\n"' +
                            (sourceCard.text.length > 300 ? sourceCard.text.slice(0, 300) + '…' : sourceCard.text) +
                            '"\n[/CONTEXT]\n\n';
                    }
                    // The suggestion itself may name the other card/feature it builds on
                    // (e.g. "notification system built in card #a1b2c3") — carry that into
                    // the new card so the agent knows the wider connection.
                    if (suggestion.connection) {
                        contextBlock += '[CONTEXT — builds on: ' + suggestion.connection + ']\n[/CONTEXT]\n\n';
                    }
                    var newCard = {
                        id: uid(),
                        text: contextBlock + (suggestion.description || ''),
                        filePath: sourceCard.filePath || vm.selectedProject,
                        createdAt: new Date().toISOString(),
                        priority: 'medium',
                        attached: files,
                        autoPr: vm.prByDefault !== false,
                        selfImproving: false,
                        createTests: false,
                        llmEndpointId: sourceCard.llmEndpointId || '',
                        _fromSuggestion: true,
                        _suggestionSourceCardId: sourceCard.id
                    };
                    vm.state.todo.push(newCard);
                    vm.saveCards();
                    pushAgentLog(vm, 'success', '💡 Suggestion queued as a new card: ' + (newCard.text || '').slice(0, 80));
                    if (vm.showSideToast) vm.showSideToast('💡 Added to To Do — ' + (files.length ? files.length + ' file(s) attached' : 'no attachments'));
                    // Scroll the new card into view so the user sees where it landed.
                    $timeout(function () {
                        var el = document.querySelector('[data-card-id="' + newCard.id + '"]');
                        if (el) el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
                    }, 50);
                };
                // ── Jump to the card a suggestion's connection references ─────────
                // Suggestion connections name the other card/feature they build on (e.g.
                // "notification system built in card #a1b2c3"). Clicking the 🔗 chip on a
                // Done-column suggestion resolves the #id prefix against every column,
                // opens the owning column if hidden, scrolls the card into view and
                // flashes it so the user sees exactly what the suggestion builds on.
                vm.jumpToConnectionCard = function (conn) {
                    if (!conn) return;
                    var m = String(conn).match(/#([a-zA-Z0-9]+)/);
                    if (!m) return;
                    var prefix = m[1].toLowerCase();
                    var cols = ['todo', 'doing', 'done', 'archived', 'selfImproving'];
                    var target = null, targetCol = null;
                    for (var i = 0; i < cols.length && !target; i++) {
                        var col = cols[i];
                        (vm.state && vm.state[col] || []).forEach(function (c) {
                            if (!target && c.id && String(c.id).toLowerCase().indexOf(prefix) === 0) { target = c; targetCol = col; }
                        });
                    }
                    if (!target) {
                        if (vm.showSideToast) vm.showSideToast('🔗 The card this suggestion builds on is no longer on the board');
                        return;
                    }
                    // Open the owning column if it's hidden, and ensure the right project.
                    if (target.filePath && vm.selectedProject !== target.filePath) vm.selectedProject = target.filePath;
                    var showKey = 'show' + targetCol.charAt(0).toUpperCase() + targetCol.slice(1);
                    if (vm[showKey] === false) vm[showKey] = true;
                    if (vm.selectCard) vm.selectCard(target);
                    $timeout(function () {
                        var el = document.querySelector('[data-card-id="' + target.id + '"]');
                        if (el) {
                            el.scrollIntoView({ behavior: 'smooth', block: 'center' });
                            el.classList.add('card-flash-highlight');
                            $timeout(function () { el.classList.remove('card-flash-highlight'); }, 1600);
                        }
                    }, 60);
                };
                vm.suggestionContext = function (card) {
                    if (!card) return null;
                    if (card._suggestionsContext) return card._suggestionsContext;
                    var analysis = card.agentAnalysis || {};
                    if (!analysis.summary && !analysis.thinking && !analysis.steps) return null;
                    var cached = _suggestionContextCache.get(card);
                    if (cached && cached.analysis === analysis) return cached.value;
                    var value = {
                        summary: analysis.summary || '',
                        thinking: (analysis.thinking || '').slice(0, 6000),
                        steps: (analysis.steps || []).filter(function (s) { return s && s.change; }).slice(0, 40).map(function (s) { return (s.path ? s.path + ' — ' : '') + s.change; }),
                        planItems: (analysis.planItems || []).filter(function (p) { return p && p.text; }).slice(0, 25).map(function (p) { return (p.done ? '✓ ' : '○ ') + p.text; }),
                        filesEdited: (analysis.filesEdited || []).map(function (f) { return f && f.path ? f.path : null; }).filter(function (p) { return !!p; }),
                        generatedAt: ''
                    };
                    _suggestionContextCache.set(card, { analysis: analysis, value: value });
                    return value;
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
                // Resolve the card that owns a diff. Callers pass it when they have
                // it in scope (per-card columns); otherwise fall back to the active
                // card so the streaming panel can still persist applied state.
                vm._diffResolveCard = function (card) {
                    if (card) return card;
                    return vm.findCardById ? vm.findCardById(vm.activeCardId) : null;
                };
                vm.diffIsApplied = function (holder, diffPath, card) {
                    if (!holder) return false;
                    if (holder._diffApplied) return true;
                    var status = holder.status || holder._diffStepStatus || '';
                    if (status === 'done') return true;
                    if (holder.done === true && status === '') return true;
                    // Real per-diff applied flag persisted in board data — survives
                    // reload and is consistent across tabs (source of truth is the
                    // backend git apply response, recorded in card._appliedDiffs).
                    var c = vm._diffResolveCard(card);
                    if (c && c._appliedDiffs && diffPath && c._appliedDiffs[diffPath]) return true;
                    return false;
                };
                // True when the diff is applied but NOT via an explicit user Apply
                // click in this session — i.e. the agent applied it during the run.
                // Drives the "already applied" tooltip on the ✓ marker.
                vm.diffAppliedByAgent = function (holder, diffPath, card) {
                    if (!holder || !diffPath) return false;
                    if (holder._diffApplied) return false;
                    var status = holder.status || holder._diffStepStatus || '';
                    var looksApplied = (status === 'done') || (holder.done === true && status === '');
                    if (looksApplied) return true;
                    var c = vm._diffResolveCard(card);
                    if (c && c._appliedDiffs && c._appliedDiffs[diffPath]) return true;
                    return false;
                };
                vm.applyDiff = function (diffPath, step, card) {
                    if (!diffPath) return;
                    // Resolve against the CARD's project (falling back to the selected
                    // one) so apply/preview/delete/open all hit the same root — a diff
                    // lives in the card's project, which may be an absolute path outside
                    // the workspace root (e.g. a benchmark sandbox).
                    var proj = (card && card.filePath) || vm.selectedProject;
                    if (!proj) return;
                    if (vm.diffIsApplied(step, diffPath, card)) return;
                    var wasRunning = vm.streamingActive;
                    var savedCardId = vm.activeCardId;
                    var savedPrompt = vm.activeCardText;
                    if (wasRunning) {
                        vm.stopAgent();
                    }
                    step._applyingDiff = true;
                    $http.post('/api/agent/apply-diff', { project: proj, diffPath: diffPath }).then(function (resp) {
                        step._applyingDiff = false;
                        if (resp.data && resp.data.success) {
                            step._diffApplied = true;
                            step.status = 'done';
                            step._diffPath = diffPath;
                            // Persist the applied diff path in board data so Apply stays
                            // correct across reloads and consistent across tabs.
                            var appliedPath = (resp.data && resp.data.diffPath) || diffPath;
                            var c = vm._diffResolveCard(card);
                            if (c && appliedPath) {
                                if (!c._appliedDiffs) c._appliedDiffs = {};
                                c._appliedDiffs[appliedPath] = true;
                                if (vm.saveCards) vm.saveCards();
                            }
                            if (vm.addLogEntry) vm.addLogEntry({ type: 'info', message: '✓ Diff applied: ' + appliedPath + ' — halting agent' });
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
                vm.previewDiff = function (diffPath, step, card) {
                    if (!diffPath) return;
                    var proj = (card && card.filePath) || vm.selectedProject;
                    if (!proj) return;
                    step._previews = step._previews || {};
                    var p = step._previews[diffPath] = step._previews[diffPath] || {};
                    if (p.content) { p.show = !p.show; return; }
                    p.loading = true;
                    $http.get('/api/agent/diff-content', { params: { project: proj, diffPath: diffPath } }).then(function (resp) {
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
                vm.deleteDiff = function (diffPath, step, card, $event) {
                    // Legacy call sites pass ($event) as the third argument.
                    if (card && !card.filePath && !card.id && card.stopPropagation) {
                        $event = card;
                        card = null;
                    }
                    if ($event) $event.stopPropagation();
                    if (!diffPath) return;
                    var proj = (card && card.filePath) || vm.selectedProject;
                    if (!proj) return;
                    step._previews = step._previews || {};
                    var p = step._previews[diffPath] = step._previews[diffPath] || {};
                    p.deleting = true;
                    $http.post('/api/agent/delete-diff', { project: proj, diffPath: diffPath }).then(function (resp) {
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
                // Open an undo diff in the IDE. The diff path is relative to the CARD's
                // project (which for a benchmark is the sandbox folder — an absolute path
                // that may live OUTSIDE the workspace root), so the card's filePath is
                // passed through; without it the editor/content endpoint would resolve the
                // diff against the wrong root and fail with 400 "Path outside workspace
                // root". Backslashes are normalized so the tab shows a clean filename.
                vm.openDiffInIde = function (diffPath, step, card, $event) {
                    // Legacy call sites pass ($event) as the third argument — detect that
                    // shape (an event object, not a card) and shift it into the event slot.
                    if (card && !card.filePath && !card.id && card.stopPropagation) {
                        $event = card;
                        card = null;
                    }
                    if ($event) $event.stopPropagation();
                    if (!diffPath) return;
                    var proj = (card && card.filePath) || vm.selectedProject;
                    if (!proj) return;
                    var normalized = String(diffPath).replace(/\\/g, '/');
                    vm.showIDE = true;
                    if (vm.openFile) {
                        vm.openFile(normalized, proj);
                    } else if (vm.ide && vm.ide.openFile) {
                        vm.ide.openFile(normalized, proj);
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
                vm.openBenchmarksPanel = function () { vm.showBenchmarksPanel = true; vm.compareMode = false; vm.compareA = null; vm.compareB = null; vm.compareResult = null; vm.checkLlmReachable(); $http.get('/api/benchmark/scores').then(function (resp) { vm.benchmarkScores = resp.data || []; }); $http.get('/api/benchmark/plans').then(function (resp) { vm.benchmarkPlans = resp.data || []; if (vm._hydrateRestoredBenchmarkQueue) vm._hydrateRestoredBenchmarkQueue(); }); $http.get('/api/benchmark/system-info').then(function (resp) { vm.systemInfoCustom = resp.data.custom || {}; vm.defaultBenchmarkRoot = resp.data.defaultBenchmarkRoot || vm.defaultBenchmarkRoot || ''; }); };
                vm.closeBenchmarksPanel = function () { vm.showBenchmarksPanel = false; };
                vm.benchSectionOpen = { run: false, local: false, specs: false, server: false };
                vm.toggleBenchSection = function (key) {
                    if (!vm.benchSectionOpen) vm.benchSectionOpen = {};
                    vm.benchSectionOpen[key] = !vm.benchSectionOpen[key];
                    if (key === 'run' && vm.benchSectionOpen.run && vm.checkLlmReachable) vm.checkLlmReachable();
                };
                vm.benchmarkLevelName = function (level) {
                    if (level === undefined || level === null) return '';
                    var p = (vm.benchmarkPlans || []).find(function (x) { return x.level === level; });
                    return p ? p.name : 'Benchmark ' + level;
                };
                vm.reconcileBenchmarkRunning = function () {
                    if (!vm.state) return;
                    if (vm.agentRuns.some(function (r) { return r.active; })) return;
                    if (vm._finalizeStaleBenchmarkIfNeeded) vm._finalizeStaleBenchmarkIfNeeded();
                    var hasCard = (vm.state.doing || []).some(function (c) { return c && c._benchmark; })
                        || (vm.state.todo || []).some(function (c) { return c && c._benchmark && c.ready; });
                    var hasLiveRun = !!vm._benchmarkLiveRun();
                    if (hasCard || vm.benchmarkAllActive) {
                        // A benchmark card exists. It only counts as RUNNING while its
                        // stream is actually live in this tab; a card with no live run
                        // (reload, dropped stream, missing done event) becomes
                        // 'interrupted' so it never shows as actively running.
                        if (!hasLiveRun && (vm.benchmarkRunning || vm.benchmarkLevel != null)) {
                            vm.benchmarkRunning = false;
                            vm.benchmarkLevel = null;
                        }
                        return;
                    }
                    if (vm.benchmarkRunning || vm.benchmarkLevel != null) {
                        vm.benchmarkRunning = false;
                        vm.benchmarkLevel = null;
                    }
                };
                vm.benchmarksRunning = function () {
                    if (vm.streamingActive) return true;
                    if (vm._benchmarkLiveRun()) return true;
                    return false;
                };
                vm.llmReachable = null;
                vm.checkLlmReachable = function () {
                    return $http.get('/api/agent/llm-reachable').then(function (resp) {
                        vm.llmReachable = !!(resp.data && resp.data.reachable);
                        return vm.llmReachable;
                    }, function () { vm.llmReachable = false; return false; });
                };
                vm.showBenchmarkBlocked = function (msg) {
                    msg = msg || 'Benchmark can\'t start right now.';
                    if (vm.showSideToast) { vm.showSideToast(msg); return; }
                    if (vm.showNotification) { vm.showNotification(msg); return; }
                    $window.alert(msg);
                };
                vm._benchmarkClickPending = false;
                vm.benchmarkRunTitle = function () {
                    if (vm.llmReachable === false) return '⚠ LLM endpoint unreachable — start the model server (or fix the endpoint in Settings) to run benchmarks.';
                    if (vm.streamingActive) return 'An agent run is in progress — wait for it to finish.';
                    if (vm.benchmarkInterrupted() != null) return '⚠ A previous benchmark was interrupted — resume it (Run All) or delete the stuck card.';
                    return '';
                };
                vm._persistBenchmarkRun = function () {
                    if (!vm.state) return;
                    var run = {
                        active: !!vm.benchmarkAllActive,
                        queue: (vm._benchmarkQueue || []).map(function (p) { return p.level; }),
                        results: vm.benchmarkAllResults || []
                    };
                    run.id = vm._benchmarkRunId || (vm._benchmarkRunId = 'br_' + Date.now().toString(36));
                    vm.state._benchmarkRun = run;
                    vm.saveCards();
                };
                vm._restoreBenchmarkState = function () {
                    if (!vm.state) return;
                    var doing = (vm.state.doing || []).filter(function (c) { return c && c._benchmark; });
                    var todo = (vm.state.todo || []).filter(function (c) { return c && c._benchmark && c.ready; });
                    var liveCard = doing[0] || todo[0];
                    var hasLiveRun = !!vm._benchmarkLiveRun();
                    if (liveCard && vm.ensureBenchmarkProject) {
                        // A benchmark was left running/interrupted — show its dedicated kanban so
                        // the user sees where the benchmark cards live on reload.
                        vm.ensureBenchmarkProject(function () { });
                    }
                    if (liveCard && !vm.benchmarkAllActive) {
                        // Only claim "running" while a stream for this card is actually
                        // live in this tab. A leftover card (page reload, dropped stream)
                        // is interrupted instead — the panel shows the amber banner, not
                        // a forever spinner.
                        if (hasLiveRun) {
                            vm.benchmarkRunning = true;
                            vm.benchmarkLevel = liveCard._benchmarkLevel != null ? liveCard._benchmarkLevel : 1;
                            if (vm.benchSectionOpen) vm.benchSectionOpen.run = true;
                        } else {
                            vm.benchmarkRunning = false;
                            vm.benchmarkLevel = null;
                        }
                    }
                    var run = vm.state._benchmarkRun;
                    if (run && run.active && !vm.benchmarkAllActive) {
                        vm.benchmarkAllActive = true;
                        vm._benchmarkRunId = run.id;
                        if (vm.benchSectionOpen) vm.benchSectionOpen.run = true;
                        vm.benchmarkAllResults = (run.results || []).slice();
                        var plans = vm.benchmarkPlans || [];
                        vm._benchmarkQueue = (run.queue || []).map(function (lv) {
                            return plans.find(function (p) { return p.level === lv; }) || { level: lv, name: 'Benchmark ' + lv, description: '' };
                        });
                        var stuckDoing = (vm.state.doing || []).find(function (c) { return c && c._benchmark; });
                        vm.benchmarkLevel = (stuckDoing && stuckDoing._benchmarkLevel != null)
                            ? stuckDoing._benchmarkLevel
                            : (vm._benchmarkQueue.length ? vm._benchmarkQueue[0].level : (vm.benchmarkAllResults.length ? vm.benchmarkAllResults[vm.benchmarkAllResults.length - 1].level : null));
                        vm.benchmarkRunning = !!hasLiveRun;
                    } else if (run && !run.active) {
                        vm.benchmarkAllActive = false;
                        vm.benchmarkAllResults = (run.results || []).slice();
                        vm.benchmarkAllResult = vm._summarizeBenchmarkAll(vm.benchmarkAllResults);
                    } else if (!run && !liveCard) {
                        vm.benchmarkAllActive = false;
                    }
                    vm._finalizeStaleBenchmarkIfNeeded();
                    if (vm.reconcileBenchmarkRunning) vm.reconcileBenchmarkRunning();
                };
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
                vm._hydrateRestoredBenchmarkQueue = function () {
                    if (!vm._benchmarkQueue || !vm._benchmarkQueue.length || !vm.benchmarkPlans || !vm.benchmarkPlans.length) return;
                    vm._benchmarkQueue = vm._benchmarkQueue.map(function (p) {
                        var real = vm.benchmarkPlans.find(function (x) { return x.level === p.level; });
                        return real || p;
                    });
                };
                vm.benchmarkInterrupted = function () {
                    if (vm.streamingActive) return null;
                    if (vm._benchmarkLiveRun()) return null;
                    if (!vm.benchmarkAllActive) {
                        var stuckDoing = (vm.state.doing || []).find(function (c) { return c && c._benchmark; });
                        if (stuckDoing) return stuckDoing._benchmarkLevel != null ? stuckDoing._benchmarkLevel : 1;
                        var stuckTodo = (vm.state.todo || []).find(function (c) { return c && c._benchmark && c.ready && !c._endpointQueued; });
                        if (stuckTodo) return stuckTodo._benchmarkLevel != null ? stuckTodo._benchmarkLevel : 1;
                        return null;
                    }
                    if (vm.agentRuns.some(function (r) { return r.active; })) return null;
                    if (vm.benchmarkLevel != null) return vm.benchmarkLevel;
                    if (vm._benchmarkQueue && vm._benchmarkQueue.length) return vm._benchmarkQueue[0].level;
                    if (vm.benchmarkAllResults && vm.benchmarkAllResults.length) return vm.benchmarkAllResults[vm.benchmarkAllResults.length - 1].level;
                    return null;
                };
                vm.benchmarkInterruptedName = function () {
                    var lv = vm.benchmarkInterrupted();
                    return lv != null ? (vm.benchmarkLevelName(lv) || 'Benchmark ' + lv) : '';
                };
                vm.resumeBenchmarkAll = function () {
                    if (!vm.benchmarkAllActive || !vm.state) return;
                    if (vm.agentRuns.some(function (r) { return r.active; })) return;
                    if (!vm.benchmarkPlans || !vm.benchmarkPlans.length) {
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
                    if (stuckLevel != null && !vm.benchmarkAllResults.some(function (r) { return r.level === stuckLevel; })) {
                        var plan = (vm.benchmarkPlans || []).find(function (p) { return p.level === stuckLevel; })
                            || { level: stuckLevel, name: vm.benchmarkLevelName(stuckLevel), description: '' };
                        vm._benchmarkQueue.unshift(plan);
                    }
                    pushAgentLog(vm, 'info', '▶ Resuming interrupted benchmark run…');
                    vm._persistBenchmarkRun();
                    vm.ensureBenchmarkProject(function () { vm._runNextBenchmarkFromQueue(); });
                };
                // Single-benchmark rerun: a stuck card in Doing (stream died with the
                // tab, no live run) gets a one-click retry. Clears the dead benchmark
                // card(s) and re-runs that level fresh — the run-all Resume button in
                // the panel handles run-all batches, so this is gated to single mode.
                vm.rerunBenchmark = function (card) {
                    if (!card || !card._benchmark) return;
                    if (vm.benchmarkAllActive) return;
                    if (vm.benchmarksRunning()) {
                        if (vm.showBenchmarkBlocked) vm.showBenchmarkBlocked('A benchmark is already running — wait for it to finish before rerunning.');
                        return;
                    }
                    var level = card._benchmarkLevel != null ? card._benchmarkLevel : 1;
                    if (vm.state) {
                        vm.state.doing = (vm.state.doing || []).filter(function (c) { return !c._benchmark; });
                        vm.state.todo = (vm.state.todo || []).filter(function (c) { return !c._benchmark; });
                    }
                    var plan = (vm.benchmarkPlans || []).find(function (p) { return p.level === level; });
                    if (plan) {
                        vm.ensureBenchmarkProject(function () { vm._runBenchmarkLevel(plan); });
                    } else {
                        $http.get('/api/benchmark/plans').then(function (resp) {
                            vm.benchmarkPlans = resp.data || [];
                            if (vm._hydrateRestoredBenchmarkQueue) vm._hydrateRestoredBenchmarkQueue();
                            var p = (vm.benchmarkPlans || []).find(function (x) { return x.level === level; });
                            if (p) vm.ensureBenchmarkProject(function () { vm._runBenchmarkLevel(p); });
                            else pushAgentLog(vm, 'warn', '⚠ Cannot rerun benchmark — no plan found for level ' + level + '.');
                        }).catch(function () { pushAgentLog(vm, 'warn', '⚠ Cannot rerun benchmark — failed to load plans.'); });
                    }
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
                // Benchmark cards must land in a dedicated "Weaver Benchmarks" kanban, not
                // whatever project happens to be selected. Ensures the backend project entry
                // exists (created on first run / when missing), then switches the board to it
                // so the cards are visible in the right column view.
                vm.ensureBenchmarkProject = function (cb) {
                    $http.post('/api/benchmark/ensure-project').then(function (resp) {
                        var path = resp.data && resp.data.path;
                        if (!path) { if (cb) cb(); return; }
                        vm._benchmarkProjectPath = path;
                        if (resp.data && resp.data.created) {
                            pushAgentLog(vm, 'success', '📁 Created the "Weaver Benchmarks" project for benchmark cards.');
                            if (vm.showSideToast) vm.showSideToast('📁 Created "Weaver Benchmarks" project for benchmark cards');
                        }
                        if (vm.selectedProject === path) { if (cb) cb(); return; }
                        // Keep the user's chosen default intact — only the selected view switches.
                        var prevDefault = vm.defaultProject;
                        vm.selectedProject = path;
                        if (!vm.loadConfig) { if (cb) cb(); return; }
                        vm.loadConfig(path).then(function () {
                            vm.defaultProject = prevDefault || vm.defaultProject;
                            if (vm.countArchivedCards) vm.countArchivedCards();
                            if (vm.loadFilePickerEntries) vm.loadFilePickerEntries();
                            if (cb) cb();
                        }, function () { if (cb) cb(); });
                    }, function () { if (cb) cb(); });
                };
                // The root the NEXT benchmark run will write to: the custom
                // benchmarkProjectRoot from the system-info panel when set, else the
                // desktop benchmark_sandbox default resolved by the backend. Surfaced in
                // the panel before a run so users aren't surprised where work lands.
                vm.benchmarkEffectiveRoot = function () {
                    var custom = vm.systemInfoCustom && vm.systemInfoCustom.benchmarkProjectRoot;
                    var root = (custom && String(custom).trim()) ? String(custom) : (vm.defaultBenchmarkRoot || '');
                    return root.replace(/\\/g, '/').replace(/\/+$/g, '').trim();
                };
                vm.benchmarkUsesCustomRoot = function () {
                    var custom = vm.systemInfoCustom && vm.systemInfoCustom.benchmarkProjectRoot;
                    return !!(custom && String(custom).trim());
                };
                vm._runBenchmarkLevel = function (plan) {
                    vm.benchmarkRunning = true; vm.benchmarkLevel = plan.level;
                    if (!vm.benchSectionOpen) vm.benchSectionOpen = {};
                    vm.benchSectionOpen.run = true;
                    var benchProj = vm._benchmarkProjectPath || vm.selectedProject;
                    var card = { id: 'benchmark_' + plan.level + '_' + Date.now(), text: plan.description, filePath: benchProj, priority: 'high', _benchmark: true, _benchmarkLevel: plan.level, ready: true };
                    vm._benchmarkProjectPath = benchProj;
                    vm.state.todo.push(card); vm.saveCards();
                    // Match the panel note: log the effective root (custom or default sandbox)
                    // so the transcript and the Benchmarks panel agree on where work lands.
                    pushAgentLog(vm, 'info', '📍 ' + (vm.benchmarkLevelName ? vm.benchmarkLevelName(plan.level) : ('Benchmark ' + plan.level)) +
                        ' writing to ' + (vm.benchmarkEffectiveRoot ? vm.benchmarkEffectiveRoot() : benchProj));
                    vm.executeAgent(card);
                };
                vm.startBenchmark = function (level) {
                    if (vm.benchmarksRunning()) return;
                    if (vm.llmReachable === false) {
                        if (vm._benchmarkClickPending) return;
                        vm._benchmarkClickPending = true;
                        vm.showBenchmarkBlocked(vm.benchmarkRunTitle() || '⚠ LLM endpoint unreachable — start the model server (or fix the endpoint in Settings) to run benchmarks.');
                        var done = function () { vm._benchmarkClickPending = false; };
                        if (vm.checkLlmReachable) vm.checkLlmReachable().then(done, done);
                        else done();
                        return;
                    }
                    vm.startBenchmarkNow(level);
                };
                vm.startBenchmarkNow = function (level) {
                    $http.get('/api/benchmark/plans').then(function (resp) {
                        var plan = (resp.data || []).find(function (p) { return p.level === level; });
                        if (!plan) return;
                        vm.ensureBenchmarkProject(function () {
                            vm._runBenchmarkLevel(plan); vm.closeBenchmarksPanel();
                        });
                    }).catch(function () { vm.benchmarkRunning = false; });
                };
                vm._finishBenchmarkAll = function () {
                    vm.benchmarkAllActive = false;
                    vm._benchmarkQueue = [];
                    vm.benchmarkAllResult = vm._summarizeBenchmarkAll(vm.benchmarkAllResults);
                    vm.benchmarkRunning = false; vm.benchmarkLevel = null;
                    vm._persistBenchmarkRun();
                    var failed = vm.benchmarkAllResult.failedLevel;
                    pushAgentLog(vm, 'info', '📊 Benchmark All finished — completed ' + vm.benchmarkAllResult.completedLevels + ' benchmark(s), ' +
                        (failed != null ? 'stopped at ' + vm.benchmarkLevelName(failed) + ' due to error steps' : 'all benchmarks passed') +
                        ' (' + vm.benchmarkAllResult.totalPoints + ' pts)');
                    // The gold skulltula marks the end of the whole batch — win or lose.
                    // (Same Windows gate as sendSystemToast so the sound behaves
                    // identically to single-run completions.)
                    if (vm.playSound && navigator.userAgent.indexOf('Win') !== -1) vm.playSound();
                };
                vm.stopBenchmarkAll = function () {
                    if (!vm.benchmarkAllActive) return;
                    if (vm.stopAgent) vm.stopAgent();
                    var completed = vm.benchmarkAllResults ? vm.benchmarkAllResults.length : 0;
                    var queued = vm._benchmarkQueue ? vm._benchmarkQueue.length : 0;
                    vm.benchmarkAllActive = false;
                    vm._benchmarkQueue = [];
                    vm.benchmarkAllResult = vm._summarizeBenchmarkAll(vm.benchmarkAllResults);
                    vm.benchmarkRunning = false; vm.benchmarkLevel = null;
                    vm._persistBenchmarkRun();
                    pushAgentLog(vm, 'warn', '⏹ Benchmark All stopped — completed ' + completed + ' benchmark(s), ' + queued + ' remaining.');
                };
                vm._runNextBenchmarkFromQueue = function () {
                    if (!vm._benchmarkQueue || !vm._benchmarkQueue.length) { vm._finishBenchmarkAll(); return; }
                    var plan = vm._benchmarkQueue.shift();
                    while (plan && !plan.description) {
                        pushAgentLog(vm, 'warn', '⏭ Skipping benchmark ' + vm.benchmarkLevelName(plan.level) + ' — plan unavailable (open the panel once plans load, or run it manually).');
                        if (!vm._benchmarkQueue.length) { vm._finishBenchmarkAll(); return; }
                        plan = vm._benchmarkQueue.shift();
                    }
                    vm._runBenchmarkLevel(plan);
                };
                vm.startBenchmarkAll = function () {
                    if (vm.benchmarksRunning()) return;
                    if (vm.llmReachable === false) {
                        if (vm._benchmarkClickPending) return;
                        vm._benchmarkClickPending = true;
                        vm.showBenchmarkBlocked(vm.benchmarkRunTitle() || '⚠ LLM endpoint unreachable — start the model server (or fix the endpoint in Settings) to run benchmarks.');
                        var done = function () { vm._benchmarkClickPending = false; };
                        if (vm.checkLlmReachable) vm.checkLlmReachable().then(done, done);
                        else done();
                        return;
                    }
                    vm.startBenchmarkAllNow();
                };
                vm.startBenchmarkAllNow = function () {
                    $http.get('/api/benchmark/plans').then(function (resp) {
                        var plans = (resp.data || []).slice().sort(function (a, b) { return a.level - b.level; });
                        if (!plans.length) return;
                        vm.benchmarkAllActive = true;
                        vm.benchmarkAllResults = [];
                        vm.benchmarkAllResult = null;
                        vm._benchmarkQueue = plans;
                        vm._persistBenchmarkRun();
                        vm.ensureBenchmarkProject(function () { vm._runNextBenchmarkFromQueue(); });
                    }).catch(function () { vm.benchmarkAllActive = false; });
                };
                vm._advanceBenchmarkAll = function (level, successful, failed, totalAttempts, status, points, scorePercent) {
                    if (!vm.benchmarkAllActive) return;
                    vm.benchmarkAllResults.push({ level: level, successful: successful, failed: failed, status: status, points: points, scorePercent: scorePercent });
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
                function collectBenchmarkEdits(steps) {
                    if (!steps || !steps.length) return [];
                    var out = [];
                    var seen = {};
                    steps.forEach(function (s) {
                        if (s.type !== 'edit' && s.type !== 'create' && s.type !== 'rename') return;
                        if (!s.path) return;
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
                vm.benchmarkStatusInfo = function (status) {
                    var s = String(status || '').toLowerCase();
                    if (s === 'completed' || s === 'passed' || s === 'ok' || s === 'success') return { label: 'Passed', cls: 'good' };
                    if (s === 'partial') return { label: 'Partial', cls: 'warn' };
                    if (s === 'failed' || s === 'error' || s === 'fail') return { label: 'Failed', cls: 'bad' };
                    return { label: status || '—', cls: 'neutral' };
                };
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
                vm.clearAllBenchmarkScores = function () {
                    if (!vm.benchmarkScores || !vm.benchmarkScores.length) return;
                    if (!$window.confirm('Delete all ' + vm.benchmarkScores.length + ' local benchmark score(s)? This cannot be undone.')) return;
                    $http.delete('/api/benchmark/scores').then(function () {
                        vm.benchmarkScores = [];
                        vm.compareA = null;
                        vm.compareB = null;
                        vm.compareResult = vm.compareData();
                        vm.selectedBenchmarkScore = null;
                    }).catch(function () { });
                };
            }
        };
    }]);