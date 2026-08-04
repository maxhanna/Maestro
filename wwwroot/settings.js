// settings.mixin.js
angular.module('kanbanApp')
    .factory('SettingsMixin', ['$http', '$window', '$timeout', function ($http, $window, $timeout) {
        const SETTINGS_KEY = 'weaverconfig.settings';

        var DEFAULT_THEME = {
            '--bg': '#071025', '--surface': '#0b1220', '--panel': '#071322',
            '--muted': '#9fb3c8', '--text': '#e6eef6', '--accent': '#06b6d4',
            '--accent-2': '#7c3aed', '--success': '#4ade80', '--warning': '#fbbf24',
            '--error': '#f87171'
        };

        var PRESET_THEMES = {
            'Dracula': {
                '--bg': '#282a36', '--surface': '#2d2f3e', '--panel': '#21222c',
                '--muted': '#6272a4', '--text': '#f8f8f2', '--accent': '#bd93f9',
                '--accent-2': '#ff79c6', '--success': '#50fa7b', '--warning': '#f1fa8c',
                '--error': '#ff5555'
            },
            'Nord': {
                '--bg': '#2e3440', '--surface': '#3b4252', '--panel': '#434c5e',
                '--muted': '#81a1c1', '--text': '#eceff4', '--accent': '#88c0d0',
                '--accent-2': '#b48ead', '--success': '#a3be8c', '--warning': '#ebcb8b',
                '--error': '#bf616a'
            },
            'Solarized': {
                '--bg': '#002b36', '--surface': '#073642', '--panel': '#0a4a54',
                '--muted': '#657b83', '--text': '#93a1a1', '--accent': '#268bd2',
                '--accent-2': '#d33682', '--success': '#859900', '--warning': '#b58900',
                '--error': '#dc322f'
            },
            'Catppuccin': {
                '--bg': '#1e1e2e', '--surface': '#181825', '--panel': '#11111b',
                '--muted': '#a6adc8', '--text': '#cdd6f4', '--accent': '#89b4fa',
                '--accent-2': '#f5c2e7', '--success': '#a6e3a1', '--warning': '#f9e2af',
                '--error': '#f38ba8'
            },
            'Tokyo Night': {
                '--bg': '#0d0f1c', '--surface': '#13152a', '--panel': '#1a1b2e',
                '--muted': '#565f89', '--text': '#c0caf5', '--accent': '#7aa2f7',
                '--accent-2': '#bb9af7', '--success': '#9ece6a', '--warning': '#e0af68',
                '--error': '#f7768e'
            }
        };

        function mergeTheme(themeColors) {
            var merged = {};
            Object.keys(DEFAULT_THEME).forEach(function (k) { merged[k] = DEFAULT_THEME[k]; });
            if (themeColors) {
                Object.keys(themeColors).forEach(function (k) {
                    if (merged.hasOwnProperty(k) && themeColors[k]) merged[k] = themeColors[k];
                });
            }
            return merged;
        }

        function applyTheme(el, themeColors) {
            if (!el) el = document.documentElement;
            Object.keys(themeColors).forEach(function (k) {
                el.style.setProperty(k, themeColors[k]);
            });
        }

        function normalizeProjects(raw) {
            return raw.map(function (p) {
                return { Name: p.Name || p.name, Path: p.Path || p.path, Description: p.Description || p.description || '', BuildCommands: p.buildCommands || p.BuildCommands || '' };
            });
        }

        return {
            init: function (vm, $scope) {
                // State
                vm.selectedProject = '';
                vm.archiveCardCount = 0;
                vm.selfImprovingCardCount = 0;
                vm.projects = [];
                vm.defaultProject = '';
                vm.settingsDefaultProject = '';
                vm.autoQueue = true;
                // Self-improving cards must be physically started by the user. Once armed
                // (selfImprovingAgentActive), they cycle 1-by-1 forever while no regular
                // card (todo/doing/done) is active.
                vm.selfImprovingAgentActive = false;
                vm.useVSCodeInsteadOfIDE = false;

                // Terminal/Config settings
                vm.llamaUrl = 'http://localhost:8080';
                vm.llamaModel = 'medgemma:4b';
                vm.llamaEndpoints = [];
                vm.terminalApprovalMode = 'approveAll';
                vm.approvedTerminalRoots = [];
                vm.disallowedTerminalRoots = [];
                vm.approvedTerminalRootsText = '';
                vm.disallowedTerminalRootsText = '';
                vm.maxFileContextChars = 24000;
                vm.maxFullFileTokens = 4096;
                vm.maxContextChars = 22000;
                vm.fileBodyTruncationChars = 8000;
                vm.buildOutputTailChars = 8000;
                vm.defaultMaxTokens = 2048;
                vm.includeProjectSkeleton = true;
                vm.includeEditKnowledge = false;
                vm.compactThinkingContext = true;
                vm.summarizeDiffContext = true;
                vm.diffContextSummaryChars = 6000;
                vm.llmTimeoutMinutes = 0;
                vm.llmInfiniteTimeout = true;
                // When the user turns "Infinite" off, default the slider to the 5-minute floor
                // instead of leaving a stale 0 that sits below the slider's min.
                vm.onLlmInfiniteToggle = function () {
                    if (!vm.llmInfiniteTimeout && vm.llmTimeoutMinutes < 5) vm.llmTimeoutMinutes = 5;
                };
                vm.buildCommands = "";
                vm.prByDefault = false;
                vm.themeColors = {};
                vm.presetThemeList = Object.keys(PRESET_THEMES);
                vm.savedThemes = [];
                vm.newThemeName = '';
                vm.ideThemeFilter = '';
                vm.ideTheme = 'weaver-dark';
                vm.ideThemeList = [
                    { name: 'Default (Weaver Dark)', value: 'weaver-dark', preview: { bg: '#0d1117', fg: '#c792ea', accent: '#06b6d4', light: false } },
                    { name: 'Monokai', value: 'monokai', preview: { bg: '#272822', fg: '#a6e22e', accent: '#e6db74', light: false } },
                    { name: 'Dracula', value: 'dracula', preview: { bg: '#282a36', fg: '#ff79c6', accent: '#bd93f9', light: false } },
                    { name: 'Material', value: 'material', preview: { bg: '#263238', fg: '#82aaff', accent: '#80cbc4', light: false } },
                    { name: 'Darcula', value: 'darcula', preview: { bg: '#2b2b2b', fg: '#a9b7c6', accent: '#6897bb', light: false } },
                    { name: 'Seti', value: 'seti', preview: { bg: '#151718', fg: '#55b5db', accent: '#a074c4', light: false } },
                    { name: 'Tomorrow Night Eighties', value: 'tomorrow-night-eighties', preview: { bg: '#2d2d2d', fg: '#f2777a', accent: '#6699cc', light: false } },
                    { name: 'Ambiance', value: 'ambiance', preview: { bg: '#202020', fg: '#ffa500', accent: '#e0e0e0', light: false } },
                    { name: 'Eclipse (light)', value: 'eclipse', preview: { bg: '#ffffff', fg: '#7f0055', accent: '#3f7f5f', light: true } },
                    { name: 'Neo (light)', value: 'neo', preview: { bg: '#ffffff', fg: '#75438a', accent: '#3a6ea5', light: true } }
                ];
                vm.ideThemePreviewStyle = function (t) {
                    return {
                        'background': t.preview.bg,
                        'color': t.preview.fg,
                        'border-color': t.preview.accent
                    };
                };

                // Settings tab state
                vm.settingsTab = 'appearance';

                // UI Panels
                vm.showProjectOptions = false;
                vm.showEditProjectsPanel = false;
                vm.showSettingsPanel = false;
                vm.showDiscordPanel = false;

                // Projects UI
                vm.newProjectName = '';
                vm.newProjectPath = '';
                vm.newProjectDescription = '';
                vm.newProjectBuildCommands = '';

                // File hints
                vm.fileHintsData = [];

                // Email Accounts
                vm.emailAccounts = [];

                // Agent Tools
                vm.toolList = [
                    { key: '_explore', label: 'Explore files for reference', enabled: true },
                    { key: '_discover', label: 'Project-wide context search (BM25 + AI)', enabled: true },
                    { key: '_command', label: 'Run terminal commands', enabled: true },
                    { key: '_create_file', label: 'Create new files', enabled: true },
                    { key: '_sql_migration', label: 'SQL migration files', enabled: true },
                    { key: '_web_search', label: 'Web search', enabled: true },
                    { key: '_web_fetch', label: 'Fetch URLs', enabled: true },
                    { key: '_git', label: 'Git operations', enabled: true },
                    { key: '_rename_file', label: 'Rename files', enabled: true },
                    { key: '_delete_file', label: 'Delete files', enabled: true },
                    { key: '_show', label: 'Display text to user', enabled: true }
                ];

                // Discord/Update
                vm.appVersion = null;
                vm.updating = false;

                // === Methods ===
                function loadLocalSettings() {
                    try {
                        var raw = $window.localStorage.getItem(SETTINGS_KEY);
                        if (raw) {
                            var s = JSON.parse(raw);
                            vm.autoQueue = s.autoQueue !== false;
                            if (typeof s.selfImprovingAgentActive === 'boolean') vm.selfImprovingAgentActive = s.selfImprovingAgentActive;
                            // Stash the saved minimap state so IDEMixin.init (which runs after
                            // this mixin) can restore it synchronously before config loads.
                            if (typeof s.ideMinimapVisible === 'boolean') vm._savedIdeMinimapVisible = s.ideMinimapVisible;
                        }
                    } catch (e) { }
                }
                loadLocalSettings();

                // Lightweight single-key persist (no full config round-trip) so arming the
                // self-improving cycle from the queue code survives a reload.
                vm.persistSelfImprovingAgent = function () {
                    try {
                        var raw = $window.localStorage.getItem(SETTINGS_KEY);
                        var s = raw ? JSON.parse(raw) : {};
                        s.selfImprovingAgentActive = vm.selfImprovingAgentActive === true;
                        $window.localStorage.setItem(SETTINGS_KEY, JSON.stringify(s));
                    } catch (e) { }
                };

                function saveLocalSettings() {
                    try {
                        $window.localStorage.setItem(SETTINGS_KEY, JSON.stringify({
                            autoQueue: vm.autoQueue,
                            selfImprovingAgentActive: vm.selfImprovingAgentActive === true,
                            ideMinimapVisible: vm.ide ? vm.ide.minimapVisible !== false : true
                        }));
                    } catch (e) { }
                }

                vm.countArchivedCards = function () {
                    if (!vm.state || !vm.state.archived) { vm.archiveCardCount = 0; return; }
                    try {
                        if (Array.isArray(vm.state.archived)) {
                            vm.archiveCardCount = vm.state.archived.filter(function (card) { return card.filePath === vm.selectedProject; }).length;
                        } else if (typeof vm.state.archived === 'object') {
                            var archivedData = vm.state.archived[vm.selectedProject];
                            vm.archiveCardCount = Array.isArray(archivedData) ? archivedData.length : 0;
                        } else { vm.archiveCardCount = 0; }
                    } catch (e) { console.log("CountArchivedCards error", e); }
                };

                vm.loadConfig = function (project) {
                    return $http.get('/api/config').then(function (resp) {
                        try {
                            var cfg = resp.data || {};
                            var raw = (cfg.projects && cfg.projects.length) ? cfg.projects : [{ Name: 'Project Alpha', Path: '../project-alpha' }];
                            vm.projects = normalizeProjects(raw);
                            vm.selectedProject = project || cfg.defaultProject || (vm.projects.length ? vm.projects[0].Path : '');
                            vm.defaultProject = project || cfg.defaultProject;

                            if (typeof cfg.showTerminal === 'boolean') vm.showTerminal = cfg.showTerminal;
                            if (typeof cfg.showAI === 'boolean') vm.showAI = cfg.showAI;
                            if (typeof cfg.showIDE === 'boolean') vm.showIDE = cfg.showIDE;
                            if (typeof cfg.showCalendar === 'boolean') vm.showCalendar = cfg.showCalendar;
                            if (typeof cfg.showKanban === 'boolean') vm.showKanban = cfg.showKanban;
                            if (typeof cfg.showNotes === 'boolean') vm.showNotes = cfg.showNotes;
                            if (typeof cfg.useVSCodeInsteadOfIDE === 'boolean') vm.useVSCodeInsteadOfIDE = cfg.useVSCodeInsteadOfIDE;
                            if (typeof cfg.ideTheme === 'string') vm.ideTheme = cfg.ideTheme;
                            if (typeof cfg.ideMinimapVisible === 'boolean' && vm.ide) vm.ide.minimapVisible = cfg.ideMinimapVisible;
                            if (typeof cfg.prByDefault === 'boolean') vm.prByDefault = cfg.prByDefault;
                            vm.llamaUrl = cfg.llamaUrl || "http://localhost:8080";
                            vm.llamaModel = cfg.llamaModel || "medgemma:4b";
                            vm.llamaEndpoints = (cfg.llamaEndpoints || []).map(function (e) { return { id: e.id || ('ep-' + Math.random().toString(36).slice(2, 9)), name: e.name || '', url: e.url || '', model: e.model || '' }; });
                            vm.terminalApprovalMode = cfg.terminalApprovalMode || 'approveAll';
                            vm.approvedTerminalRoots = cfg.approvedTerminalRoots || [];
                            vm.approvedTerminalRootsText = vm.approvedTerminalRoots.join(', ');
                            vm.disallowedTerminalRoots = cfg.disallowedTerminalRoots || [];
                            vm.disallowedTerminalRootsText = vm.disallowedTerminalRoots.join(', ');
                            vm.maxFileContextChars = typeof cfg.maxFileContextChars === 'number' ? cfg.maxFileContextChars : 24000;
                            vm.maxFullFileTokens = typeof cfg.maxFullFileTokens === 'number' ? cfg.maxFullFileTokens : 4096;
                            vm.maxContextChars = typeof cfg.maxContextChars === 'number' ? cfg.maxContextChars : 22000;
                            vm.fileBodyTruncationChars = typeof cfg.fileBodyTruncationChars === 'number' ? cfg.fileBodyTruncationChars : 8000;
                            vm.buildOutputTailChars = typeof cfg.buildOutputTailChars === 'number' ? cfg.buildOutputTailChars : 8000;
                            vm.defaultMaxTokens = typeof cfg.defaultMaxTokens === 'number' ? cfg.defaultMaxTokens : 2048;
                            vm.includeProjectSkeleton = cfg.includeProjectSkeleton === true;
                            vm.includeEditKnowledge = cfg.includeEditKnowledge === true;
                            vm.extendThinking = cfg.extendThinking !== false;
                            vm.thinkingMaxTokens = typeof cfg.thinkingMaxTokens === 'number' ? cfg.thinkingMaxTokens : 4096;
                            vm.compactThinkingContext = cfg.compactThinkingContext !== false;
                            vm.summarizeDiffContext = cfg.summarizeDiffContext !== false;
                            vm.diffContextSummaryChars = typeof cfg.diffContextSummaryChars === 'number' ? cfg.diffContextSummaryChars : 6000;
                            vm.llmTimeoutMinutes = typeof cfg.llmTimeoutMinutes === 'number' && cfg.llmTimeoutMinutes > 0 ? Math.max(5, cfg.llmTimeoutMinutes) : 0;
                            vm.llmInfiniteTimeout = vm.llmTimeoutMinutes <= 0;

                            vm.enabledTools = cfg.enabledTools || [];
                            vm.toolList.forEach(function (t) { t.enabled = vm.enabledTools.length === 0 || vm.enabledTools.indexOf(t.key) !== -1; });

                            vm.emailAccounts = (cfg.emailAccounts || []).map(function (a) {
                                return { imapServer: a.imapServer || '', imapPort: a.imapPort || 993, useSsl: a.useSsl !== false, username: a.username || '', password: a.password || '', label: a.label || '', showAppPasswordInstructions: false, testing: false, testResult: null };
                            });
                            if (vm.emailAccounts.length === 0 && (cfg.emailUsername || cfg.emailImapServer)) {
                                vm.emailAccounts.push({ imapServer: cfg.emailImapServer || '', imapPort: cfg.emailImapPort || 993, useSsl: cfg.emailUseSsl !== false, username: cfg.emailUsername || '', password: cfg.emailPassword || '', label: '', showAppPasswordInstructions: false, testing: false, testResult: null });
                            }
                            vm.bughostedUrl = cfg.bughostedUrl || '';
                            vm.bughostedUsername = cfg.bughostedUsername || '';
                            vm.bughostedPassword = cfg.bughostedPassword || '';
                            vm.bughostedHeartbeatEnabled = cfg.bughostedHeartbeatEnabled || false;
                            vm.themeColors = mergeTheme(cfg.themeColors);
                            vm.savedThemes = (cfg.savedThemes || []).map(function (t) { return { name: t.name || 'Untitled', colors: mergeTheme(t.colors), _editing: false, _editName: '' }; });
                            applyTheme(null, vm.themeColors);
                            // Auto-load file picker if IDE sidebar is already open
                            if (vm.ide && vm.ide.showSidebar && vm.loadFilePickerEntries) {
                                $timeout(function () { vm.loadFilePickerEntries(); }, 100);
                            }
                        } catch (e) { console.log("Loading config error", e); }
                    }, function () {
                        vm.projects = normalizeProjects([{ Name: 'Default', Path: '..' }]);
                        vm.selectedProject = '..'; vm.defaultProject = '..';
                    });
                };

                vm.saveSettings = function (skipCloseSettingsPanel = false) {
                    saveLocalSettings();
                    $http.get('/api/config').then(function (resp) {
                        var cfg = resp.data || { projects: vm.projects };
                        cfg.projects = cfg.projects || vm.projects;
                        cfg.defaultProject = vm.settingsDefaultProject || vm.defaultProject;
                        cfg.llamaUrl = vm.llamaUrl || "http://localhost:8080";
                        cfg.llamaModel = vm.llamaModel || "medgemma:4b";
                        cfg.llamaEndpoints = vm.llamaEndpoints || [];
                        cfg.terminalApprovalMode = vm.terminalApprovalMode || 'approveAll';
                        cfg.approvedTerminalRoots = (vm.approvedTerminalRootsText || '').split(',').map(function (r) { return r.trim().toLowerCase(); }).filter(Boolean);
                        cfg.disallowedTerminalRoots = (vm.disallowedTerminalRootsText || '').split(',').map(function (r) { return r.trim().toLowerCase(); }).filter(Boolean);
                        cfg.maxFileContextChars = vm.maxFileContextChars || 24000;
                        cfg.maxFullFileTokens = vm.maxFullFileTokens || 4096;
                        cfg.maxContextChars = vm.maxContextChars || 22000;
                        cfg.fileBodyTruncationChars = vm.fileBodyTruncationChars || 8000;
                        cfg.buildOutputTailChars = vm.buildOutputTailChars || 8000;
                        cfg.defaultMaxTokens = vm.defaultMaxTokens || 2048;
                        cfg.includeProjectSkeleton = vm.includeProjectSkeleton === true;
                        cfg.includeEditKnowledge = vm.includeEditKnowledge === true;
                        cfg.extendThinking = vm.extendThinking === true;
                        cfg.thinkingMaxTokens = vm.thinkingMaxTokens || 4096;
                        cfg.compactThinkingContext = vm.compactThinkingContext !== false;
                        cfg.summarizeDiffContext = vm.summarizeDiffContext !== false;
                        cfg.diffContextSummaryChars = vm.diffContextSummaryChars || 6000;
                        // LLM request timeout: 0 (or unchecked infinite) = no timeout, otherwise >= 5 minutes.
                        cfg.llmTimeoutMinutes = vm.llmInfiniteTimeout || !vm.llmTimeoutMinutes ? 0 : Math.max(5, vm.llmTimeoutMinutes);
                        cfg.showNotes = vm.showNotes === true;
                        cfg.useVSCodeInsteadOfIDE = vm.useVSCodeInsteadOfIDE === true;
                        cfg.ideTheme = vm.ideTheme || 'weaver-dark';
                        cfg.ideMinimapVisible = !!(vm.ide && vm.ide.minimapVisible);
                        cfg.emailAccounts = vm.emailAccounts.map(function (a) { return { imapServer: a.imapServer, imapPort: a.imapPort, useSsl: a.useSsl, username: a.username, password: a.password, label: a.label }; });
                        cfg.bughostedUrl = vm.bughostedUrl || '';
                        cfg.bughostedUsername = vm.bughostedUsername || '';
                        cfg.bughostedPassword = vm.bughostedPassword || '';
                        cfg.bughostedHeartbeatEnabled = vm.bughostedHeartbeatEnabled || false;
                        cfg.themeColors = vm.themeColors;
                        cfg.savedThemes = vm.savedThemes.map(function (t) { return { name: t.name || 'Untitled', colors: t.colors || {} }; });
                        cfg.enabledTools = vm.toolList.filter(function (t) { return t.enabled; }).map(function (t) { return t.key; });
                        return $http.post('/api/config/save', cfg);
                    }).then(function () {
                        vm.defaultProject = vm.settingsDefaultProject || vm.defaultProject;
                        if (vm.settingsDefaultProject) vm.selectedProject = vm.settingsDefaultProject;
                       // vm.loadConfig(vm.defaultProject);
                        if (!skipCloseSettingsPanel) vm.closeSettingsPanel();
                    }, function (err) { $window.alert('Failed to save settings: ' + (err.data || err.statusText || err)); });
                };

                vm.addEmailAccount = function () { vm.emailAccounts.push({ imapServer: '', imapPort: 993, useSsl: true, username: '', password: '', label: '', showAppPasswordInstructions: false, testing: false, testResult: null }); };
                vm.removeEmailAccount = function (index) { vm.emailAccounts.splice(index, 1); };
                vm.addLlamaEndpoint = function () { vm.llamaEndpoints.push({ id: 'ep-' + Math.random().toString(36).slice(2, 9), name: '', url: 'http://localhost:8080', model: '' }); };
                vm.removeLlamaEndpoint = function (index) { vm.llamaEndpoints.splice(index, 1); };
                vm.endpointLabel = function (id) {
                    if (!id) return 'Default';
                    var ep = (vm.llamaEndpoints || []).find(function (e) { return e.id === id; });
                    return ep ? (ep.name || ep.url || 'Default') : 'Default';
                };
                vm.checkEmailServer = function (index) {
                    var acct = vm.emailAccounts[index]; if (!acct || !acct.imapServer) return acct.showAppPasswordInstructions = false;
                    var lower = acct.imapServer.toLowerCase();
                    acct.showAppPasswordInstructions = (lower.includes('gmail.com') || lower.includes('googlemail.com')) ? 'google' : (lower.includes('outlook.com') || lower.includes('hotmail.com') || lower.includes('live.com') || lower.includes('msn.com')) ? 'microsoft' : false;
                };
                vm.testEmailConnection = function (index) {
                    var acct = vm.emailAccounts[index]; if (!acct || !acct.imapServer || !acct.username || !acct.password) return acct.testResult = { success: false, message: 'Please fill in all fields' };
                    acct.testing = true; acct.testResult = null;
                    $http.post('/api/email/test', { imapServer: acct.imapServer, imapPort: acct.imapPort, useSsl: acct.useSsl, username: acct.username, password: acct.password })
                        .then(function (response) { acct.testing = false; acct.testResult = response.data; })
                        .catch(function (error) { acct.testing = false; acct.testResult = { success: false, message: 'Connection test failed: ' + (error.data || error.statusText || 'Unknown error') }; });
                };

                vm.getProjectBuildCommands = function (projectIndex) {
                    if (!vm.projects || !vm.projects[projectIndex]) return '';
                    return vm.projects[projectIndex].BuildCommands || '';
                };

                vm.loadFileHints = function () {
                    $http.get('/api/filehints').then(function (response) {
                        try {
                            var store = typeof response.data === 'string' ? JSON.parse(response.data) : response.data;
                            if (store && store.Projects && vm.projects) {
                                vm.fileHintsData = vm.projects.map(function (p) {
                                    var proj = store.Projects[p.Path];
                                    return { projectPath: p.Path, hints: proj && proj.Hints ? proj.Hints.map(function (h) { return { keywords: (h.Keywords || []).join(', '), files: (h.Files || []).length > 0 ? h.Files.slice() : [''] }; }) : [] };
                                });
                            } else { vm.fileHintsData = []; }
                        } catch (e) { vm.fileHintsData = []; }
                    }, function () { vm.fileHintsData = vm.projects ? vm.projects.map(function (p) { return { projectPath: p.Path, hints: [] }; }) : []; });
                };

                vm.getProjectHints = function (projectIndex) {
                    if (!vm.fileHintsData) vm.fileHintsData = [];
                    if (!vm.fileHintsData[projectIndex]) {
                        var proj = (vm.projects && vm.projects[projectIndex]) ? vm.projects[projectIndex] : { Path: '' };
                        vm.fileHintsData[projectIndex] = { projectPath: proj.Path, hints: [] };
                    }
                    return vm.fileHintsData[projectIndex].hints;
                };
                vm.addHint = function (projectIndex) { vm.getProjectHints(projectIndex).push({ keywords: '', files: [''] }); };
                vm.removeHint = function (projectIndex, hintIndex) { if (vm.fileHintsData[projectIndex]) vm.fileHintsData[projectIndex].hints.splice(hintIndex, 1); };
                vm.addFileToHint = function (projectIndex, hintIndex) { if (vm.fileHintsData[projectIndex] && vm.fileHintsData[projectIndex].hints[hintIndex]) vm.fileHintsData[projectIndex].hints[hintIndex].files.push(''); };
                vm.removeFileFromHint = function (projectIndex, hintIndex, fileIndex) { if (vm.fileHintsData[projectIndex] && vm.fileHintsData[projectIndex].hints[hintIndex]) vm.fileHintsData[projectIndex].hints[hintIndex].files.splice(fileIndex, 1); };
                vm.saveFileHints = function () {
                    var payload = { Projects: {} };
                    vm.fileHintsData.forEach(function (entry) {
                        var projectKey = entry.projectPath || vm.selectedProject || vm.defaultProject || '__default__';
                        payload.Projects[projectKey] = { Hints: entry.hints.map(function (h) { return { Keywords: h.keywords.split(',').map(function (k) { return k.trim(); }).filter(Boolean), Files: h.files.filter(Boolean) }; }), AutoLearned: [] };
                    });
                    return $http.put('/api/filehints', payload).then(function () { vm.closeSettingsPanel(); }, function (err) { $window.alert('Failed to save file hints: ' + (err.data || err.statusText || err)); });
                };

                vm.toggleProjectOptions = function () { vm.showProjectOptions = !vm.showProjectOptions; };
                vm.closeOptionsOnBlur = function (event) { $timeout(function () { vm.showProjectOptions = false; $timeout(function () { vm.saveSettings(true); }, 300); }, 300); };
                vm.changeProject = function () { vm.loadConfig(vm.selectedProject).then(function () { $timeout(function () { vm.countArchivedCards(); vm.loadFilePickerEntries(); }, 100); }); };
                vm.openEditProjectsPanel = function () { vm.newProjectName = ''; vm.newProjectPath = ''; vm.newProjectDescription = ''; vm.settingsDefaultProject = vm.defaultProject || vm.selectedProject; vm.projects.forEach(function (p) { p._origPath = p.Path; }); vm.showEditProjectsPanel = true; };
                vm.closeEditProjectsPanel = function () { vm.saveSettings(true); vm.showEditProjectsPanel = false; };
                vm.addProjectFromPanel = function () {
                    if (!vm.newProjectName) return $window.alert('Project name is required');
                    if (!vm.newProjectPath) return $window.alert('Project path is required');
                    $http.post('/api/config/projects/add', { Name: vm.newProjectName, Path: vm.newProjectPath.replace(/\\/g, '/'), Description: vm.newProjectDescription || '', BuildCommands: vm.newProjectBuildCommands || '' })
                        .then(function () { vm.loadConfig(); vm.newProjectName = ''; vm.newProjectPath = ''; vm.newProjectDescription = ''; vm.newProjectBuildCommands = ''; }, function (err) { $window.alert('Failed to add project: ' + (err.data || err.statusText)); });
                };
                vm.saveProject = function (p) {
                    if (!p.Name || !p.Path) return $window.alert('Name and Path are required');
                    var originalPath = p._origPath || p.Path;
                    $http.get('/api/config').then(function (resp) {
                        var cfg = resp.data || { projects: [] }; cfg.projects = cfg.projects || [];
                        var idx = cfg.projects.findIndex(function (cp) { return (cp.Path || cp.path) === originalPath; });
                        if (idx === -1) return $window.alert('Project not found in config');
                        var newPath = p.Path.replace(/\\/g, '/');
                        if (newPath !== originalPath && cfg.projects.some(function (cp) { return (cp.Path || cp.path) === newPath; })) return $window.alert('A project with that path already exists');
                        cfg.projects[idx].Name = p.Name; cfg.projects[idx].Path = newPath; cfg.projects[idx].Description = p.Description || ''; cfg.projects[idx].BuildCommands = p.BuildCommands || '';
                        $http.post('/api/config/save', cfg).then(function () { vm.loadConfig(); }, function (err) { $window.alert('Failed to save: ' + (err.data || err.statusText)); });
                    });
                };
                vm.removeProject = function (p, event) {
                    if (event) event.stopPropagation(); if (!p || !p.Path) return;
                    if (!$window.confirm('Remove project "' + (p.Name || '') + '" (' + p.Path + ')?')) return;
                    $http.post('/api/config/projects/remove', { Path: p.Path }).then(function () { vm.loadConfig(); });
                };
                vm.openDiscordPanel = function () { vm.showDiscordPanel = true; vm.loadVersion(); };
                vm.closeDiscordPanel = function () { vm.showDiscordPanel = false; };
                vm.loadVersion = function () { $http.get('/api/bughosted/version', { timeout: 10000 }).then(function (resp) { vm.appVersion = resp.data; }, function () { vm.appVersion = { local: '?', remote: null, updateAvailable: false }; }); };
                vm.triggerUpdate = function () {
                    vm.updating = true;
                    vm.updateProgress = { stage: 'starting', percent: 0, bytesDownloaded: 0, totalBytes: 0 };
                    $http.post('/api/bughosted/update').then(function () {
                        pollUpdateProgress();
                    }, function () {
                        vm.updating = false;
                        vm.updateProgress = null;
                        alert('Update failed.');
                    });
                };

                function pollUpdateProgress() {
                    var poll = function () {
                        $http.get('/api/bughosted/update-progress', { timeout: 3000 }).then(function (resp) {
                            vm.updateProgress = resp.data;
                            if (resp.data.stage === 'failed') {
                                vm.updating = false;
                                alert('Update failed.');
                            } else if (resp.data.stage === 'installing' || resp.data.stage === 'restarting') {
                                vm.updateProgress = { stage: 'restarting', percent: 100, bytesDownloaded: 0, totalBytes: 0 };
                                waitForServer();
                            } else {
                                $timeout(poll, 500);
                            }
                        }, function () {
                            vm.updateProgress = { stage: 'restarting', percent: 100, bytesDownloaded: 0, totalBytes: 0 };
                            waitForServer();
                        });
                    };
                    $timeout(poll, 1000);
                }

                function shutdownBackground() {
                    vm.shuttingDown = true;
                    if (vm.stopBughostedTimers) vm.stopBughostedTimers();
                    if (vm.pauseTerminalPolling) vm.pauseTerminalPolling();
                    if (vm.stopIdePolling) vm.stopIdePolling();
                }

                function waitForServer(fromManual) {
                    shutdownBackground();
                    var started = Date.now();
                    var retry = function () {
                        var elapsed = (Date.now() - started) / 1000;
                        if (elapsed > 15) vm.updateProgress.stuck = true;
                        if (fromManual && elapsed > 60) { vm.updateProgress.stuck = true; return; }
                        $http.get('/api/bughosted/version', { timeout: 5000 }).then(function () {
                            window.location.reload();
                        }, function () {
                            $timeout(retry, fromManual ? 500 : 2000);
                        });
                    };
                    $timeout(retry, fromManual ? 500 : 1000);
                }
                vm.reloadNow = function () { waitForServer(true); };

                vm.openSettingsPanel = function () {
                    vm.settingsDefaultProject = vm.defaultProject || vm.selectedProject; vm.showSettingsPanel = true;
                    vm.loadIdeThemeStylesheets();
                    $timeout(function () { vm.renderThemePreviews(); }, 150);
                    vm.fileHintsData = (vm.projects || []).map(function (p) { return { projectPath: p.Path, hints: [] }; });
                    $http.get('/api/filehints').then(function (resp) {
                        try {
                            var store = typeof resp.data === 'string' ? JSON.parse(resp.data) : resp.data;
                            if (store && store.Projects) {
                                vm.projects.forEach(function (p, i) {
                                    var proj = store.Projects[p.Path];
                                    vm.fileHintsData[i] = { projectPath: p.Path, hints: proj && proj.Hints ? proj.Hints.map(function (h) { return { keywords: (h.Keywords || []).join(', '), files: (h.Files || []).length > 0 ? h.Files.slice() : [''] }; }) : [] };
                                });
                            }
                        } catch (e) { }
                    });
                    var backdrop = document.getElementById('backdrop'); if (backdrop) backdrop.style.display = 'block';
                };
                var _themeSaveDebounce = null;
                vm.applyThemeColors = function (el, colors) {
                    applyTheme(el, colors);
                    if (_themeSaveDebounce) $timeout.cancel(_themeSaveDebounce);
                    _themeSaveDebounce = $timeout(function () {
                        vm.saveSettings(true);
                        _themeSaveDebounce = null;
                    }, 800, false);
                };
                vm.resetThemeColors = function () {
                    vm.themeColors = {};
                    Object.keys(DEFAULT_THEME).forEach(function (k) { vm.themeColors[k] = DEFAULT_THEME[k]; });
                    applyTheme(null, vm.themeColors);
                    vm.saveSettings(true);
                };
                vm.applyPresetTheme = function (name) {
                    var preset = PRESET_THEMES[name];
                    if (!preset) return;
                    Object.keys(preset).forEach(function (k) { vm.themeColors[k] = preset[k]; });
                    applyTheme(null, vm.themeColors);
                    vm.saveSettings(true);
                };

                // ── Named theme presets ───────────────────────────────────────────
                vm.themeEquals = function (a, b) {
                    return Object.keys(DEFAULT_THEME).every(function (k) {
                        return (a[k] || '').toLowerCase() === (b[k] || '').toLowerCase();
                    });
                };
                vm.isPresetActive = function (name) {
                    var preset = PRESET_THEMES[name];
                    return preset ? vm.themeEquals(vm.themeColors, preset) : false;
                };
                vm.isSavedThemeActive = function (t) {
                    return t && t.colors ? vm.themeEquals(vm.themeColors, t.colors) : false;
                };
                vm.themeSwatches = function (colors) {
                    return [colors['--bg'], colors['--surface'], colors['--accent'], colors['--success'], colors['--error']];
                };
                vm.presetSwatches = function (name) {
                    var p = PRESET_THEMES[name];
                    return p ? vm.themeSwatches(p) : [];
                };
                vm.saveCurrentTheme = function () {
                    var name = (vm.newThemeName || '').trim();
                    if (!name) { $window.alert('Enter a name for this theme first.'); return; }
                    var existing = vm.savedThemes.find(function (t) { return t.name.toLowerCase() === name.toLowerCase(); });
                    if (existing) {
                        if (!$window.confirm('A theme named "' + name + '" already exists. Replace it?')) return;
                        existing.colors = angular.copy(vm.themeColors);
                    } else {
                        vm.savedThemes.push({ name: name, colors: angular.copy(vm.themeColors), _editing: false, _editName: '' });
                    }
                    vm.newThemeName = '';
                    vm.saveSettings(true);
                };
                vm.applySavedTheme = function (t) {
                    if (!t || !t.colors) return;
                    Object.keys(t.colors).forEach(function (k) { vm.themeColors[k] = t.colors[k]; });
                    applyTheme(null, vm.themeColors);
                    vm.saveSettings(true);
                };
                vm.startRenameTheme = function (t) {
                    t._editing = true;
                    t._editName = t.name;
                };
                vm.commitRenameTheme = function (t) {
                    var name = (t._editName || '').trim();
                    if (name && name.toLowerCase() !== t.name.toLowerCase()) {
                        var clash = vm.savedThemes.find(function (x) { return x !== t && x.name.toLowerCase() === name.toLowerCase(); });
                        if (clash) {
                            $window.alert('Another theme is already named "' + name + '".');
                            t._editName = t.name; // revert input
                            t._editing = false;   // exit edit mode so blur stops re-alerting
                            return;
                        }
                        t.name = name;
                        vm.saveSettings(true);
                    }
                    t._editing = false;
                };
                vm.cancelRenameTheme = function (t) {
                    t._editName = t.name;
                    t._editing = false;
                };
                vm.deleteSavedTheme = function (t) {
                    if (!t) return;
                    if (!$window.confirm('Delete theme "' + t.name + '"? Your current colors are not changed.')) return;
                    vm.savedThemes = vm.savedThemes.filter(function (x) { return x !== t; });
                    vm.saveSettings(true);
                };
                vm.applyIdeTheme = function (name) {
                    if (!name) name = 'weaver-dark';
                    vm.ideTheme = name;
                    var linkId = 'cm-ide-theme';
                    var existing = document.getElementById(linkId);
                    if (existing) existing.parentNode.removeChild(existing);
                    if (name !== 'weaver-dark') {
                        var link = document.createElement('link');
                        link.id = linkId;
                        link.rel = 'stylesheet';
                        link.href = 'https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.17/theme/' + name + '.min.css';
                        document.head.appendChild(link);
                    }
                    if (vm._editor) {
                        vm._editor.setOption('theme', name);
                    }
                    vm.saveSettings(true);
                };

                // ── Real CodeMirror previews on the IDE theme cards ─────────────────
                var _previewCssLoaded = false;
                var _previewSample =
                    'const fib = (n) => {\n' +
                    '  if (n <= 1) return n; // base case\n' +
                    '  return fib(n - 1) + fib(n - 2);\n' +
                    '};\n' +
                    'console.log(fib(10));\n';

                vm.loadIdeThemeStylesheets = function () {
                    if (_previewCssLoaded || !vm.ideThemeList) return;
                    _previewCssLoaded = true;
                    vm.ideThemeList.forEach(function (t) {
                        if (t.value === 'weaver-dark') return; // built-in styles already loaded
                        var id = 'cm-theme-css-' + t.value;
                        if (document.getElementById(id)) return;
                        var link = document.createElement('link');
                        link.id = id;
                        link.rel = 'stylesheet';
                        link.href = 'https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.17/theme/' + t.value + '.min.css';
                        link.onload = function () { vm.renderThemePreviews(); };
                        document.head.appendChild(link);
                    });
                };

                vm.renderThemePreviews = function () {
                    if (!window.CodeMirror) return;
                    var nodes = document.querySelectorAll('.theme-card-preview[data-theme]');
                    for (var i = 0; i < nodes.length; i++) {
                        (function (el) {
                            var theme = el.getAttribute('data-theme');
                            if (!theme) return;
                            if (el._cmPreview) {
                                if (el._cmPreview.getOption('theme') !== theme) el._cmPreview.setOption('theme', theme);
                                el._cmPreview.refresh();
                                return;
                            }
                            el._cmPreview = CodeMirror(el, {
                                value: _previewSample,
                                mode: 'javascript',
                                theme: theme,
                                readOnly: true,
                                cursorBlinkRate: -1,
                                lineNumbers: false,
                                foldGutter: false,
                                gutters: [],
                                indentUnit: 2,
                                tabSize: 2
                            });
                            el._cmPreview.setOption('readOnly', true);
                            el._cmPreview.refresh();
                        })(nodes[i]);
                    }
                };

                vm.destroyThemePreviews = function () {
                    var nodes = document.querySelectorAll('.theme-card-preview[data-theme]');
                    for (var i = 0; i < nodes.length; i++) {
                        var el = nodes[i];
                        if (el._cmPreview) {
                            var wrapper = el._cmPreview.getWrapperElement();
                            if (wrapper && wrapper.parentNode) wrapper.parentNode.removeChild(wrapper);
                            el._cmPreview = null;
                        }
                        el.innerHTML = '';
                    }
                };
                vm.closeSettingsPanel = function (event) {
                    if (_themeSaveDebounce) { $timeout.cancel(_themeSaveDebounce); _themeSaveDebounce = null; }
                    if (event && event.target.tagName === 'INPUT') return;
                    if (event) event.stopPropagation();
                    vm.showSettingsPanel = false;
                    vm.destroyThemePreviews();
                    var backdrop = document.getElementById('backdrop'); if (backdrop) backdrop.style.display = 'none';
                };

                // Pre-load file hints
                $http.get('/api/filehints').then(function (resp) {
                    try { var store = typeof resp.data === 'string' ? JSON.parse(resp.data) : resp.data; if (store && store.Projects) vm._preloadedFileHints = store.Projects; } catch (e) { }
                });
            }
        };
    }]);