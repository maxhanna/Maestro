angular.module('kanbanApp')
  .factory('MeetingMixin', ['$timeout', '$http', '$interval', function ($timeout, $http, $interval) {
    return {
      init: function (vm, $scope) {
        var _vw = window.innerWidth || 1280;
        var _vh = window.innerHeight || 800;
        vm.meeting = {
          left: Math.max(16, _vw - 620 - 16),
          top: Math.max(16, _vh - 430 - 16),
          width: 620, height: 430,
          dragging: false, dragStartX: 0, dragStartY: 0,
          resizing: false, resizeDir: '', resizeStartX: 0, resizeStartY: 0,
          resizeStartW: 0, resizeStartH: 0
        };
        if (vm._meetingPanelCfg) {
          var _mp = vm._meetingPanelCfg;
          if (typeof _mp.left === 'number') vm.meeting.left = _mp.left;
          if (typeof _mp.top === 'number') vm.meeting.top = _mp.top;
          if (typeof _mp.width === 'number') vm.meeting.width = _mp.width;
          if (typeof _mp.height === 'number') vm.meeting.height = _mp.height;
        }
        vm._clampMeetingPanel = function () {
          if (vm._clampFloatingPanel) vm._clampFloatingPanel(vm.meeting);
          else if (vm.meeting) {
            var cvw = window.innerWidth || 1280;
            var cvh = window.innerHeight || 800;
            vm.meeting.left = Math.max(0, Math.min(vm.meeting.left || 0, cvw - (vm.meeting.width || 620)));
            vm.meeting.top = Math.max(0, Math.min(vm.meeting.top || 0, cvh - (vm.meeting.height || 430)));
          }
        };
        vm._clampMeetingPanel();
        vm.meetingSpeaker = '🕷️ the spiders are resting';
        vm.meetingEditorRage = 0;   
        vm.meetingVerifierSmug = 0; 
        vm.meetingFeudRatio = 50;   
        vm.meetingFeudLeader = 'tie'; 
        vm.meetingFeudPop = false;  
        vm.meetingRivs = [
          { key: 'work', a: 'planner', b: 'explorer', iconA: '🧠', iconB: '🔍',
            colorA: '#61afef', colorB: '#56b6c2',
            gradA: 'linear-gradient(90deg, #8fc7f7, #61afef)',
            gradB: 'linear-gradient(90deg, #7fd0d8, #56b6c2)',
            initialCd: 80, 
            title: 'Work feud — who actually did the work. The Planner tallies planned steps, the Explorer tallies explored ones.',
            leadTitleA: 'The Planner is ahead.', leadTitleB: 'The Explorer is ahead.',
            ratio: 50, leader: 'tie', pop: false, on: false, _prevLeader: 'tie', _popT: null },
          { key: 'command', a: 'commander', b: 'reviewer', iconA: '🛠', iconB: '🏁',
            colorA: '#e5c07b', colorB: '#e06c75',
            gradA: 'linear-gradient(90deg, #f0c674, #e5c07b)',
            gradB: 'linear-gradient(90deg, #f08080, #e06c75)',
            initialCd: 95, 
            title: 'Command feud — who actually did the work. The Commander counts commands run, the Reviewer counts reviews done.',
            leadTitleA: 'The Commander is ahead.', leadTitleB: 'The Reviewer is ahead.',
            ratio: 50, leader: 'tie', pop: false, on: false, _prevLeader: 'tie', _popT: null }
        ];
        function rivalByKey(key) {
          for (var i = 0; i < vm.meetingRivs.length; i++) { if (vm.meetingRivs[i].key === key) return vm.meetingRivs[i]; }
          return null;
        }
        vm.meetingMood = [];       
        vm.meetingMoodActive = false; 
        vm.meetingBoardLines = [];
        var ROLES = [
          { key: 'planner',  name: 'Planner',   icon: '🧠', color: '#61afef', desk: { x: 0.10, y: 0.52 }, seat: { x: 0.38, y: 0.63 } },
          { key: 'explorer', name: 'Explorer',  icon: '🔍', color: '#56b6c2', desk: { x: 0.06, y: 0.72 }, seat: { x: 0.44, y: 0.63 } },
          { key: 'editor',   name: 'Editor',    icon: '✏️', color: '#98c379', desk: { x: 0.10, y: 0.90 }, seat: { x: 0.50, y: 0.63 } },
          { key: 'commander',name: 'Commander', icon: '🛠', color: '#e5c07b', desk: { x: 0.90, y: 0.52 }, seat: { x: 0.56, y: 0.63 } },
          { key: 'verifier', name: 'Verifier',  icon: '✅', color: '#c678dd', desk: { x: 0.94, y: 0.72 }, seat: { x: 0.62, y: 0.63 } },
          { key: 'reviewer', name: 'Reviewer',  icon: '🏁', color: '#e06c75', desk: { x: 0.90, y: 0.90 }, seat: { x: 0.68, y: 0.63 } },
          { key: 'itspecialist', name: 'IT Specialist', icon: '💾', color: '#d19a66', desk: { x: 0.28, y: 0.78 }, seat: { x: 0.74, y: 0.63 } },
          { key: 'ideas',      name: 'Ideas',      icon: '💡', color: '#7ee787', desk: { x: 0.28, y: 0.52 }, seat: { x: 0.80, y: 0.63 } },
          { key: 'complexity', name: 'Complexity', icon: '🤯', color: '#ff5370', desk: { x: 0.28, y: 0.90 }, seat: { x: 0.86, y: 0.63 } }
        ];
        var BOARD_STAND = { x: 0.66, y: 0.86 };
        var STANDOFF_SPOT = { x: 0.54, y: 0.86 };
        var SHOUT_SPOT_EDITOR = { x: 0.48, y: 0.86 };   
        var SHOUT_SPOT_VERIFIER = { x: 0.60, y: 0.86 }; 
        var BOARD_RECT = { x: 0.60, y: 0.05, w: 0.34, h: 0.20 };
        var MOOD_RECT = { x: 0.71, y: 0.28, w: 0.24, h: 0.18 };
        var TABLE_RECT = { x: 0.30, y: 0.56, w: 0.46, h: 0.10 };
        var COOLER = { x: 0.17, y: 0.60 };
        var COOLER_SPOTS = [
          { x: 0.11, y: 0.64 },
          { x: 0.23, y: 0.64 },
          { x: 0.17, y: 0.72 }
        ];
        var scene = null;      
        var raf = null;
        var canvas = null;
        var ctx = null;
        var lastTs = 0;
        var destroyed = false;
        var _lastRankIdx = -1;
        var _rankPromoInterval = null;
        var _recording = null; 
        var _recordingCardId = null; 
        var _replay = null;    
        vm.meetingReplay = null;      
        vm.meetingReplaying = false;  
        vm.meetingReplaySpeed = 1;    
        vm.meetingTicker = [];        
        vm.gossipLog = [];            
        try {
          var _chatRaw = window.localStorage.getItem('weaver.meeting.chat');
          if (_chatRaw) {
            var _chat = JSON.parse(_chatRaw);
            if (Array.isArray(_chat)) vm.gossipLog = _chat.slice(-500);
          }
        } catch (e) { }
        vm.gossipExpanded = false;    
        vm.toggleGossipExpanded = function () { vm.gossipExpanded = !vm.gossipExpanded; };
        vm.gossipSearch = '';         
        vm.gossipSearchActive = -1;   
        vm.gossipActiveLine = null;   
        vm.gossipCopied = false;      
        vm.meetingIdeas = [];         
        function makeScene() {
          var spiders = ROLES.map(function (r) {
            return {
              role: r.key, name: r.name, icon: r.icon, color: r.color,
              x: r.desk.x, y: r.desk.y,
              home: { x: r.desk.x, y: r.desk.y },
              target: { x: r.desk.x, y: r.desk.y },
              seat: { x: r.seat.x, y: r.seat.y },
              state: 'idle',            
              walkPhase: Math.random() * 6.28,
              speed: 0.5 + Math.random() * 0.3,
              text: '', progress: 0,    
              speech: '', speechTtl: 0,
              celebrateT: 0,
              reactT: 0, reactKind: '', 
              waveT: 0, wavePhase: Math.random() * 6.28, 
              rage: 0, 
              rageAt: Date.now(), 
              rageDrainedAt: Date.now(), 
              stomping: false, 
              smug: 0, 
              smugAt: Date.now(), 
              smugDrainedAt: Date.now(), 
              crowned: false,  
              crownedCarried: false, 
              grump: 0,        
              grumpAt: Date.now(),      
              grumpDrainedAt: Date.now(), 
              grumpStreak: 0, 
              grumpFlash: 0,  
              steps: 0,       
              glaringAt: null  
            };
          });
          return {
            spiders: spiders,
            boardLines: [],   
            writer: null,
            queue: [],        
            meetingOn: false,
            done: false,
            activeRole: 'planner',   
            lastLogAt: Date.now(),   
            streamReadCd: 0,         
            lastStreamLen: 0,        
            banterCd: 2,             
            gossip: null,            
            gossipCd: 12,            
            followup: null,          
            followupCd: 55,          
            jokeCd: 45,              
            jokeFires: 0,            
            jokeReactT: 0,           
            workFeudCd: 80,          
            workFeudFires: 0,        
            workFeudOn: false,       
            cmdFeudCd: 95,           
            cmdFeudFires: 0,         
            cmdFeudOn: false,        
            reactLockT: 0,           
            postMortem: null,        
            verdictOutcome: null,    
            verdictGossiped: false,  
            _lastStepType: '',       
            _editFailStreak: 0,      
            confetti: [],            
            calmQuipFired: false,    
            steam: [],               
            sparkles: [],            
            watching: null,          
            watchingCd: 8,           
            standoff: null,          
            standoffCd: 20,          
            glare: null,             
            coolerTrip: null,        
            coolerTripCd: 90,        
            editorMeltdown: null,    
            meltdownFired: false,    
            verifierVictory: null,   
            shoutMatch: null,        
            shoutFired: false,       
            gripeSession: null,      
            gripeFired: false        
          };
        }
        function spiderFor(role) {
          if (!scene) return null;
          for (var i = 0; i < scene.spiders.length; i++) {
            if (scene.spiders[i].role === role) return scene.spiders[i];
          }
          return null;
        }
        var BANTER_STREAM = [
          "Still thinking… I've seen glaciers move faster than this token generation.",
          "The model is thinking so hard I can hear the GPU fans from here.",
          "Token… by token… by token… this is basically a live novel now.",
          "I'd help it think, but my brain is only 8 neurons.",
          "It's not slow — it's 'thoroughly considering' each word. Apparently.",
          "I could have coded this whole feature while it finishes that sentence.",
          "Estimated thinking time: somewhere between now and the heat death of the universe.",
          "It's pondering. Deeply. Like a philosopher with a deadline problem.",
          "The progress bar is a lie, but I accept it.",
          "If I had a web for every token… I'd have a skyscraper.",
          "Shh… it's thinking about thinking about thinking.",
          "I've counted 47 dust motes waiting for this response.",
          "This is the longest 'just a second' in recorded history.",
          "The thinking cap is on. It just never comes off.",
          "I'd tap my foot, but all 8 of them are asleep.",
          "The model is 'retrieving context'. I call it staring at the wall.",
          "It typed 'The'. Then it reconsidered its entire life. Relatable.",
          "The cursor is blinking at me like it knows something I don't.",
          "While we wait: anyone else's 8 legs falling asleep?",
          "This stream is slower than my motivation on a Monday.",
          "The model is doing its best. Its best is a 30-second 'hmm'.",
          "Token speed: a few per minute, but they're premium tokens.",
          "The API is 'warming up'. It's been 40 minutes. That's not warming up, that's climate change.",
          "The spinner has better cardio than this pipeline.",
          "The thinking chunk just grew a beard.",
          "It's compiling its thoughts. Thoughts need compiling. Apparently.",
          "Waiting for the model is my core workout.",
          "'Almost done' — the four most dangerous words in engineering.",
          "The model is writing a novel about the plan before it executes the plan.",
          "I miss instant answers. I'm 8 neurons old, but I remember.",
          "The blue circle of death and I are old friends now.",
          "It generated one period. Then stopped. To reflect.",
          "I'd check the logs, but I'm afraid of what they'd say.",
          "The stream is so slow I've re-web'd my entire desk.",
          "Estimated wait: 2-4 business days. The indicator just says 's'.",
          "It's thinking about the thinking about the prompt. Depth. Pure depth.",
          "One more token and we're basically done. One more token. Anytime now."
        ];
        var FOLLOWUP_DAYS = ['Thursday', 'Monday', 'Wednesday'];
        var FOLLOWUP_SCHEDULE = [
          "Noted. Follow-up meeting: {day}. Attendance mandatory.",
          "Scheduling a follow-up. {day}. Mark it down.",
          "Follow-up meeting: {day}. It'll definitely happen this time."
        ];
        var FOLLOWUP_CALLBACK = [
          "So… about that {day} meeting. It's in no one's calendar.",
          "Quick check: is the {day} meeting real? Asking for a friend. And me.",
          "The {day} meeting should be starting any minute now. Anytime. …Planner?",
          "I found it! The {day} meeting. In the 'definitely fake' folder.",
          "Reminder: the {day} follow-up. Which I am told I must attend. Despite evidence."
        ];
        function fmtFollowup(text, day) {
          return String(text).split('{day}').join(day);
        }
        function scheduleFakeFollowup() {
          if (!scene || scene.followup) return;
          var day = FOLLOWUP_DAYS[(Math.random() * FOLLOWUP_DAYS.length) | 0];
          scene.followup = { day: day, refs: 0, maxRefs: 3, refCd: 70 + Math.random() * 50 };
          var sp = spiderFor('planner');
          if (!sp) return;
          var text = fmtFollowup(pick(FOLLOWUP_SCHEDULE), day);
          setSpeech(sp, text, 4, sp.icon + ' ' + sp.name + ' — scheduling');
          var who = sp.icon + ' ' + sp.name;
          logGossipEntry(who, text);
          recordEvent({ type: 'chat', who: who, text: text });
        }
        function fireFollowupRef() {
          if (!scene || !scene.followup) return;
          if (scene.followup.refs >= scene.followup.maxRefs) return;
          var others = scene.spiders.filter(function (s) { return s.role !== 'planner'; });
          var sp = others.length ? others[Math.floor(Math.random() * others.length)] : null;
          if (!sp) return;
          scene.followup.refs++;
          var text = fmtFollowup(pick(FOLLOWUP_CALLBACK), scene.followup.day);
          setSpeech(sp, text, 4, sp.icon + ' ' + sp.name + ' — about that meeting');
          var who = sp.icon + ' ' + sp.name;
          logGossipEntry(who, text);
          recordEvent({ type: 'chat', who: who, text: text });
        }
        var RIVAL_FEUD_REACT = [
          "Do you hear yourself? {theirs} step(s) of 'work'. Bless.",
          "Keep counting. I'll keep doing things that matter.",
          "I'd argue, but I have {mine} step(s) of real work to finish.",
          "The board says otherwise, but sure, go off."
        ];
        var RIVALRY_POOLS = {
          work: {
            a: [
              "{mine} steps planned against my {theirs}th explorer. Who's carrying this office? I'll wait.",
              "I've planned {mine} step(s) this run. The explorer 'discovered' {theirs}. Discovery isn't a job.",
              "{mine} plans, {theirs} 'explorations'. One of us reads the room. The other reads the README.",
              "The explorer counts {theirs} step(s) as work. I count {mine} actual plans. We are not the same."
            ],
            b: [
              "{mine} files explored to the planner's {theirs} 'plans'. Without me, you'd plan into a void.",
              "I've seen {mine} real files this run. The planner 'mapped' {theirs} steps. Bold of them.",
              "{mine} explorations, {theirs} 'strategic plans'. My work is facts. Theirs is… vibes.",
              "The planner tallies {theirs} step(s). I've logged {mine} actual discoveries. Context wins."
            ],
            react: RIVAL_FEUD_REACT
          },
          command: {
            a: [
              "{mine} command(s) run this run. The reviewer has 'checked' {theirs}. Checking is a hobby, execution is a job.",
              "I've executed {mine} command(s). The reviewer 'verified' {theirs} step(s). One of us ships. The other annotates.",
              "{mine} builds green against the reviewer's {theirs} 'reviews'. My code speaks. Their spreadsheet listens.",
              "The reviewer tallies {theirs} step(s) of 'verification'. I count {mine} terminal commands that actually ran."
            ],
            b: [
              "{mine} review(s) done to the commander's {theirs} 'commands'. Anyone can run things. I catch the aftermath.",
              "I've verified {mine} step(s) this run. The commander 'executed' {theirs}. Execution without review is just chaos with extra steps.",
              "{mine} checks, {theirs} commands. My stamp of approval is worth more than their enter key.",
              "The commander logged {theirs} command(s). I've reviewed {mine}. Quantity versus quality — again."
            ],
            react: RIVAL_FEUD_REACT
          }
        };
        function fmtRival(text, mine, theirs) {
          return String(text).split('{mine}').join(mine).split('{theirs}').join(theirs);
        }
        function fireRivalryLine(key) {
          if (!scene) return;
          var riv = rivalByKey(key);
          var pools = RIVALRY_POOLS[key];
          if (!riv || !pools) return;
          var sA = spiderFor(riv.a);
          var sB = spiderFor(riv.b);
          if (!sA || !sB) return;
          var vA = sA.steps || 0;
          var vB = sB.steps || 0;
          if (vA + vB < 2) return; 
          scene[key + 'Fires']++;
          if (!scene[key + 'On']) { scene[key + 'On'] = true; syncRivalryFeud(key); }
          var ahead = vA > vB ? sA : vB > vA ? sB : (Math.random() < 0.5 ? sA : sB);
          var behind = ahead === sA ? sB : sA;
          var mine = ahead === sA ? vA : vB;
          var theirs = ahead === sA ? vB : vA;
          var text = fmtRival(pick(ahead === sA ? pools.a : pools.b), mine, theirs);
          setSpeech(ahead, text, 4, ahead.icon + ' ' + ahead.name + ' — the ' + riv.key + ' feud');
          var who = ahead.icon + ' ' + ahead.name;
          logGossipEntry(who, text);
          recordEvent({ type: 'chat', who: who, text: text });
          if (Math.random() < 0.5) {
            var rMine = ahead === sA ? vB : vA;
            var rTheirs = ahead === sA ? vA : vB;
            var rText = fmtRival(pick(pools.react), rMine, rTheirs);
            setSpeech(behind, rText, 3.5, behind.icon + ' ' + behind.name + ' — taking that personally');
            var rWho = behind.icon + ' ' + behind.name;
            logGossipEntry(rWho, rText);
            recordEvent({ type: 'chat', who: rWho, text: rText });
          }
        }
        var BANTER_IDLE = [
          "The whiteboard looks nice today. Nothing on it. Yet.",
          "I could write a novel about how long this meeting has been going.",
          "Anyone remember what we were supposed to be doing?",
          "This desk has seen more action than I have lately.",
          "I'd tidy my web, but the ambiance is finally right.",
          "Did someone say coffee? No? Just me then.",
          "The clock is my favorite colleague — always on time, never talks.",
          "I'd plan something, but planning makes the planner jealous.",
          "The table and I go way back. We've been through a lot of thinking.",
          "I swear this office chair is more cushioned than my ego.",
          "This meeting could have been an email. But no. We gather.",
          "Agenda: 1) Talk. 2) Talk more. 3) Schedule a meeting about the talking.",
          "We scheduled this meeting to discuss the last meeting. The last meeting had no agenda either. Full circle.",
          "I've been in meetings longer than some websites have existed.",
          "Standup status: 3 done, 2 in progress, 1 'why am I here'.",
          "HR told us to 'leverage synergies'. I'm a spider. I have 8 legs. That IS synergy.",
          "The water cooler has better ideas than this room. And it's a cooler.",
          "If this meeting were a movie, it would be a slow burn with no payoff.",
          "I've counted the ceiling tiles: 47. I'm not saying I'm bored. I'm saying I know the ceiling.",
          "The air in here is 60% recycled sighs.",
          "Decision reached: we'll decide tomorrow. Progress.",
          "Everyone nodded. Nobody knows what we agreed to. Classic.",
          "The whiteboard erased itself in protest. Bold move.",
          "We have a 'no interruption' policy. Everyone follows it — during other people's turns.",
          "I zoned out and now we're budgeting. For what? Nobody asks. We honor it.",
          "Every meeting has that one spider who asks 'what are we talking about?' I respect them.",
          "We could solve this by email. But no. We must witness each other's silence.",
          "The boardroom has a sign: 'meetings held hostage here'.",
          "Minutes of the last meeting: 60 minutes. That's what was accomplished.",
          "Somewhere in this building is a meeting about meetings. I pray I'm never invited.",
          "The coffee machine knows more about my day than my manager does.",
          "If boredom were a skill, I'd be a senior engineer by now.",
          "I'd take notes, but nothing has happened yet. That's the update.",
          "One day we'll resolve the standing agenda item: who left the web untidy.",
          "My performance review said I 'show up'. Attendance: the only skill I've mastered.",
          "A meeting is a group of people who'd rather be elsewhere, agreeing to stay.",
          "I've reorganized my entire web during this meeting. Twice.",
          "We brainstormed. The paper is still blank. The brainstorm was thorough.",
          "The 'quick sync' is 45 minutes old. Nothing has been synced.",
          "I once sent an email instead of scheduling a meeting. They never promoted me. Still worth it.",
          "Action items from this meeting: remember what the action items were.",
          "The meeting-room light flickers when someone says 'let's table this'. It's on our side.",
          "The agenda has one item: 'misc'. We're on item 3 of misc.",
          "This is a 'touch base' meeting. Nobody has been touched. Or based.",
          "I keep saying 'we're aligned' and I have no idea what that means.",
          "The conference chairs recline. The ambition does not."
        ];
        var BANTER_FILE = [   
          "Working on {file} right now. It's looking at me. I'm looking at it. Someone blinks first.",
          "The user wants changes in {file}. Bold of them. I respect it.",
          "If {file} had feelings, I think it would be nervous right now.",
          "The plan says we touch {file} next. The file doesn't know yet. Don't tell it.",
          "We've edited {done} file(s). The user keeps asking for more. I respect the ambition.",
          "{file} is the file of the hour. It didn't ask for this fame.",
          "Step {current} of {total}. {file} is next on the chopping block.",
          "I've been staring at {file} for a while now. We're best friends. It doesn't know yet."
        ];
        var BANTER_ENDPOINT = [ 
          "Hitting {endpoint} again. That model owes me {done} successful step(s).",
          "{endpoint} is humming along. {done} step(s) done, all the way to step {current}.",
          "{current} step(s) in on {endpoint}. The other spiders owe me lunch.",
          "{endpoint} is thinking about {file} and honestly? Same.",
          "Sending this to {endpoint}. If it comes back broken, it's the model's fault. Always is.",
          "{endpoint} and I have an arrangement: it thinks, I point. Mostly it thinks."
        ];
        var BANTER_STEPS = [   
          "We're {done} step(s) deep into a {total}-step plan. The user is watching. No pressure.",
          "{done} down, {left} to go. The whiteboard is starting to look like a real plan.",
          "This is a {total}-step plan and we've done {done}. Math checks out. Mostly.",
          "On step {current} of {total} and nobody has panicked yet. Beautiful.",
          "{done} step(s) done. Only {left} left. We're basically done. Mostly.",
          "I planned {total} step(s). The others execute them. I take full credit. Fair trade."
        ];
        var BANTER_CONFIG = [  
          "Configuration check: thinking capped at {think} tokens, output at {tokens}. Tune responsibly.",
          "This run is using {model} on {endpoint}. I wrote that down somewhere. Probably.",
          "Thinking context is at {think} tokens. Above 4096 the model gets… creative. I mean that literally.",
          "Output budget is {tokens} tokens. If the plan runs long, that's why. Budgeting. I love budgeting.",
          "Endpoint config verified: {endpoint}. Yes. That is a configured thing. It's configured.",
          "{saved} saved theme(s), plan font at {font}px, {model} on deck. The boring numbers that run the show.",
          "The user set {model} as the worker. I checked the model field three times. It says what it says.",
          "Default max tokens {tokens}, thinking {think}. In my professional opinion? These are numbers. Good numbers."
        ];
        var ROLE_BANTER = {
          editor: [
            "I edit files. They edit me. It's a relationship.",
            "Every file is a little story. I rewrite the endings.",
            "My cursor is my sword. My undo history is my shield.",
            "I changed one line today. It fixed everything. I take full credit.",
            "The diff is beautiful when I write it and a war crime when the verifier reads it."
          ],
          explorer: [
            "I explored 3,000 files today. Read one. It was the README. Life-changing.",
            "I don't get lost. I take scenic routes through the codebase.",
            "Discovery: the bug was in the file we checked first. Classic.",
            "I mapped the whole repo so you don't have to. You're welcome. You won't read it.",
            "Every file is an adventure. Most are disappointing. All are files."
          ],
          planner: [
            "I planned the coffee run today. Walk, grab, return. They said 'great plan, Planner'.",
            "My plans are beautiful. Execution is just the rumor mill.",
            "I planned a backup plan in case the plan fails. And a backup for the backup.",
            "The others execute. I take credit. It's called delegation.",
            "I once planned a plan about planning. The board wept."
          ],
          commander: [
            "I ran the build. It compiled. I'll claim that as skill.",
            "My terminal is my therapy. It screams back, but it's honest.",
            "I've run more commands than the CEO has ideas. And I mean that kindly.",
            "Build green. For now. Don't breathe on it.",
            "I type 'y' to confirm. Power. Raw power."
          ],
          verifier: [
            "I rejected 3 edits today. They were fine. But so was the rejection.",
            "My job: say no with a smile. The smile is the hard part.",
            "Approved. Don't make me regret it. I will.",
            "Unverified code is just a suggestion.",
            "I once found a bug in a hello world. Legendary."
          ],
          reviewer: [
            "I found a bug. It was in the review of the review.",
            "The code was fine. I wrote 'needs work' anyway. Tradition.",
            "Nitpick: your indent style hurt my feelings.",
            "I review the reviewer. Full-time job. Comes with a pension.",
            "Closing the loop. The loop was never open. But closure is nice."
          ],
          itspecialist: [
            "I checked the config. It was configured. I'll be here if you need excitement.",
            "Default settings are a love language.",
            "I once changed a setting and nothing broke. I still frame that memory.",
            "If it's not in the config, it didn't happen.",
            "The logs are clean today. Suspiciously clean."
          ],
          ideas: [
            "I had a great idea. It was brilliant. I already forgot it.",
            "My ideas are 90% vision, 10% substance, 100% someone else's problem.",
            "Idea counter: 47 today. Approved: 0. The system fears brilliance.",
            "I suggested a feature. The board said 'archived'. Same energy.",
            "My best ideas come at 3am, when the board is asleep. The board is never asleep."
          ]
        };
        var JOKE_LINES = [
          "Knock knock. Who's there? The build. The build who? The build that's been broken for three days, that's who.",
          "A QA spider walks into a bar. Orders 1 beer. Orders 0 beers. Orders 99999999 beers. Orders a lizard. Orders -1 beers. The bartender says: 'I don't get it.' The QA spider says: 'Exactly — you never do.'",
          "How many spiders does it take to change a lightbulb? Eight. One to change it, seven to comment on the approach.",
          "The planner's favorite joke: 'it's almost done.'",
          "Why do developers prefer dark mode? Because light attracts bugs.",
          "A UX spider walks into a bar. There is no bar. The door was too confusing.",
          "Why did the deploy fail? It was feeling under the weather.",
          "What's a spider's favorite meeting? The one that got cancelled.",
          "My computer says 'press any key'. I've been pressing Esc for an hour.",
          "The model told me a joke once. It took 2,000 tokens. The punchline was 'git status'.",
          "Why don't spiders play cards? They always end up in a web of lies.",
          "A spider walks into a bar and orders 8 drinks. The bartender says: 'What if I cut you off?' The spider says: 'You can't. I have backups.'",
          "Our standup ran 90 minutes today. We discussed the button color. We chose gray.",
          "Why did the spider quit the team? Scope creep.",
          "The IT specialist's favorite joke: 'have you tried turning it off and on again?' Then it laughs for eight hours.",
          "What do you call a meeting with no agenda? A hostage situation.",
          "Why did the spider avoid the web framework? It felt it was over-engineering the trap.",
          "The verifier's joke: 'this code passes review.' Nobody laughed. That's the joke.",
          "What's the difference between a spider and a scrum master? One of them actually catches things.",
          "I told the user a joke. The user said 'haha'. I've never felt more seen.",
          "Why did the spider cross the road? To get to the other terminal.",
          "The complexity spider told me a joke. It was 47% funny. It apologized and explained the breakdown.",
          "What do you get when you cross a spider with a Jira ticket? A backlog with legs.",
          "Two spiders walk into a bar. The third one ducks."
        ];
        var JOKE_REACTIONS = [
          "Boo. Boo, spider. That was terrible.",
          "I'd clap, but all my legs are busy cringing.",
          "That joke's been in the backlog for years.",
          "The whiteboard is laughing at you. That's a first.",
          "I smiled once. It hurt. Thanks.",
          "I'm filing that as a bug. Severity: jokes.",
          "Even the CEO spider is laughing. The CEO spider never laughs.",
          "That's going straight to the archive.",
          "I've heard better jokes from a 404 page.",
          "The punchline was eight legs too long.",
          "I laughed. In my soul. Which is the thought that counts.",
          "Second-hand cringe, delivered through the web.",
          "Tell that one to the user. Actually, don't.",
          "Somewhere, a meeting died from second-hand embarrassment."
        ];
        var COMPLEXITY_QUIPS = [
          "Ugh. Complexity {score}/100. {label}. Obviously. I could have told you that from the first syllable.",
          "{label} task. {cap} tokens of thinking. I've seen harder. I've also seen EASIER. Mostly easier.",
          "Complexity {score}/100. Let me just… carry that around for the whole run. Sure. No problem.",
          "{label}. {cap} thinking tokens. And they expect RESULTS from that. Delightful.",
          "Oh good — complexity {score}/100. Because my life was too simple before.",
          "A {label} task, capped at {cap} tokens. I've compressed entire novels into less.",
          "This task rates a {score}/100 on my personal misery index. Also known as: complexity.",
          "{cap} tokens of thinking budget for a {label} task. What could possibly go wrong. Again."
        ];
        var COMPLEXITY_CTX = [   
          "The context is up to {ctx} tokens now. It grows. It always grows.",
          "{ctx} tokens of context and counting. The whiteboard is practically vibrating.",
          "We're at {ctx} tokens of accumulated context. Someone's going to pay for this later.",
          "{ctx} tokens. Do you understand what that MEANS for my poor spider brain? Probably not."
        ];
        var COMPLEXITY_COMPACT = [ 
          "COMPACTED. They compacted my context. Everything I knew, summarized by a stranger.",
          "Oh wonderful — the reasoning just got compressed. Goodbye, nuance. We barely knew you.",
          "They compressed the accumulated diffs. My memory is now a Highlights Reel.",
          "COMPACTION. That's their solution to complexity. Add more complexity. Perfect.",
          "The context was too big, so they made it smaller. I am Big Mad about this.",
          "Diff context summarized. Translation: my beautiful, detailed history is now a haiku."
        ];
        var CALMED_QUIPS = [ 
          "Huh. I'm… relaxed? This is new. Don't get used to it.",
          "Fine. FINE. I will breathe. For now. Temporarily.",
          "The rage is gone. It feels wrong. What do I do with my hands?",
          "A quiet mind. Disgusting. Absolutely disgusting.",
          "Okay, who replaced my rage with calm? HR is going to hear about this."
        ];
        var COMPLEXITY_VERDICT_RIGHT = [
          "I said {label} — {est} step(s). It took {actual}. I am never wrong. Don't test me again.",
          "Estimated {est} step(s), actual {actual}. Verifier agreed with me. As it SHOULD.",
          "{label}, as predicted. {actual} step(s). My complexity sense is flawless. It's exhausting being right.",
          "Called it: {est} steps, we did {actual}. The verifier and I are in rare agreement. Unsettling.",
          "I told everyone it was {label}. Now the verifier agrees. Everyone owes me an apology.",
          "Track record's now {record}. I keep receipts. Somebody has to.",
          "That's {record} on the board. The verifier is basically my yes-man at this point."
        ];
        var COMPLEXITY_VERDICT_WRONG = [
          "I estimated {est} step(s). It took {actual}. The verifier overrode me. OVERRODE. Me.",
          "I said {label} and the verifier said 'actually no'. The verifier has a lot of nerve.",
          "{est} steps I predicted. {actual} happened. Clearly the complexity was hiding. Sneaky complexity.",
          "The verifier overruled my estimate. My estimate. I'm not wrong, the universe just got more complicated.",
          "I called it {label} and it ended up {actual} step(s). I blame the verifier. And gravity.",
          "Record's {record} now. One blemish. The verifier is SO going to hear about this.",
          "{record}. That's the score. Don't ask about the losses column. I'm choosing not to see it."
        ];
        var COMPLEXITY_VERDICT_WRONG = [
          "I estimated {est} step(s). It took {actual}. The verifier overrode me. OVERRODE. Me.",
          "I said {label} and the verifier said 'actually no'. The verifier has a lot of nerve.",
          "{est} steps I predicted. {actual} happened. Clearly the complexity was hiding. Sneaky complexity.",
          "The verifier overruled my estimate. My estimate. I'm not wrong, the universe just got more complicated.",
          "I called it {label} and it ended up {actual} step(s). I blame the verifier. And gravity."
        ];
        var COMPLEXITY_VERDICT_FAIL = [ 
          "The verifier says it's NOT complete. On a {label} task. I don't make the rules — I just estimate them.",
          "Verification failed. My {label} estimate was apparently too generous. Or reality is broken.",
          "The verifier found issues. {actual} step(s) and STILL not done. This is exactly why I complain.",
          "Not complete?! The complexity was clearly {label}-and-then-some. But nobody listens to the spider.",
          "That's {record} now, tainted by THIS. The failure column grew. I felt it in my legs.",
          "{record}. And this run is why there's a losses column at all. Unbelievable."
        ];
        var VERDICT_BRAG = [
          "Speaking of verdicts — my all-time record is {record}. I've been counting. Always counting.",
          "The verifier and I? {record} against me is a fiction. Check the desk tally. It's real.",
          "Historical accuracy: {record}. Ask anyone. Actually don't ask the verifier.",
          "My lifetime estimate score is {record}. I'd say 'no pressure', but I thrive on it."
        ];
        var VERDICT_BRAG_REACTIONS = [
          "He keeps a SCOREBOARD?!",
          "Of course he tracks his wins. OF COURSE he does.",
          "A verdict tally. On his desk. I'm both impressed and afraid.",
          "He's bragging about being right, again. It's… consistent.",
          "The record is real. I've seen the plaque. It's glued down."
        ];
        var REVIEWER_GRUDGE = [
          "Ugh. Fine. You were right — {est} step(s), we did {actual}. Don't let it go to your head.",
          "Fine, you were right. The {label} call was accurate. I said what I said, but you were right.",
          "I take it back. Mostly. {est} was right on the money. Grudgingly noted.",
          "Okay, okay — you called it. The board agrees with you. I'll be over here, being wrong.",
          "Fine. You were right. The verdict and your estimate are best friends now. I need a moment."
        ];
        var REVIEWER_SMUG = [
          "Told you so. Estimated {est}, took {actual}. The board keeps the receipts.",
          "Told you so. {label}? It was {actual} step(s) of reality. Stick to what you know.",
          "Told you so. My verdict says {actual} step(s). Your estimate? Adorable.",
          "Told you so. The code doesn't lie, even when estimates do.",
          "Told you so. I've seen this board write itself before. It knows better."
        ];
        var REVIEWER_SMUG_FAIL = [ 
          "Okay, that one's on all of us. The board will have to stay up a little longer.",
          "I wrote 'complete' and the verifier wrote 'no'. Some days the board plays both sides.",
          "{label}, {actual} step(s), and not finished. We'll take the L as a team.",
          "I've seen incomplete plans before. This one's going back to the drawing board. All of us."
        ];
        var COMPLEXITY_GLARE = [
          "Don't look at me like that. I KNOW what the code says.",
          "You're lucky the verifier is standing right there.",
          "I was ONE estimate away. One. That's practically a bullseye.",
          "Keep your 'told you so'. This changes NOTHING.",
          "The next task is a 95 and you'll eat those words."
        ];
        var REVIEWER_GLARE_LAST = [
          "I'll be at my desk. Glaring.",
          "Estimate said {est}, code said {actual}. That's math, not opinion.",
          "Anytime you want a rematch, my desk is right across the room.",
          "I don't need to win the argument. The diff already did."
        ];
        var GOSSIP_VERDICT_RIGHT = [
          "The reviewer had to tell the Complexity spider he was right. You could hear the grinding from here.",
          "The reviewer literally wrote 'fine, you were right.' The Complexity spider has not stopped mentioning it.",
          "Verifier agreed with the estimate. The reviewer had to sit with that. Delicious.",
          "I saw the reviewer eat crow today. It was a whole meal."
        ];
        var GOSSIP_VERDICT_WRONG = [
          "The reviewer said 'told you so' to its face. Full eye contact. The board felt it.",
          "Reviewer got the last word — 'told you so' — and then they GLARED across the whole office.",
          "The estimate missed by a mile and the reviewer has not let it go. Not one letter.",
          "There was a stare-down after the verdict. The Complexity spider lost. The reviewer is still smug."
        ];
        var GOSSIP_VERDICT_FAIL = [
          "The reviewer wrote 'complete' and the verifier said no. The whole office took that L together.",
          "The board says it's not done. The reviewer and the Complexity spider are suddenly best friends in failure."
        ];
        var GOSSIP_VERDICT_REACTIONS = [
          "No way. The board has RECEIPTS.",
          "I'd pay to see that again.",
          "We were all at our desks. We SAW it.",
          "Somebody check on the reviewer's ego."
        ];
        var COOLER_GRIPE = [
          "75. SEVENTY-FIVE. And the context just keeps GROWING. I need a drink.",
          "This context is {ctx} tokens of unmanageable nonsense. Cooler. Now.",
          "{label} task, {ctx} tokens of context, {score}/100 complexity. Obviously I'm at the cooler.",
          "The whiteboard is fine. The context is NOT. Water. Immediately.",
          "I'm at the cooler because THIS ({ctx} tokens) is what happens when nobody listens to me."
        ];
        var COOLER_SIP = [
          "*glug glug* …unmanageable. Absolutely unmanageable.",
          "*sips* …context. {ctx} tokens of it. Does anyone ELSE have to drink their feelings?",
          "*gulp* …right. Back to the desk. The chaos awaits.",
          "*sip* …I feel nothing. Which is an improvement."
        ];
        var COOLER_STALK_BACK = [
          "Fine. Hydrated. Now where were we with this {label} mess.",
          "Back to the desk. The context won't manage itself. Apparently.",
          "Drink done. Rage: slightly diluted. See you at the board."
        ];
        var DOUBLE_TAKE = [
          "did… did he just leave mid-meeting?",
          "Is the red one… storming off? Again?",
          "He just LEFT. Mid-sentence. Rude but… honestly, fair.",
          "Wait, did he just go for water during MY turn?",
          "Okay, who's covering his seat while he hydrates?",
          "The Complex one just walked out. Should someone go after him? …No? No one? Cool."
        ];
        var COMPLEXITY_POSTMORTEM = [
          "POST-MORTEM: user expected 'quick tweak', reality was {label} ({actual} step(s)). I saw this coming.",
          "Post-mortem: {label} in {actual} step(s). The user said 'easy'. The whiteboard says otherwise.",
          "Task autopsy: called it {label}. {actual} step(s) later, I remain unimpressed and correct.",
          "The user asked for a 'small change'. The board says {label}, {actual} steps. They always do.",
          "Final analysis: {label}. {actual} steps. The user's expectations and my estimate were never friends.",
          "Post-mortem complete: {label} ({actual} step(s)). I predicted {est} step(s). Close enough. I'm always close."
        ];
        var COMPLEXITY_POSTMORTEM_FAIL = [ 
          "POST-MORTEM: {label} in {actual} step(s) and STILL not done. The user said 'simple'. Unbelievable.",
          "Task autopsy: {label}, {actual} steps, verifier unsatisfied. So the user gets MORE complexity. Great.",
          "Post-mortem: user wanted trivial, got {label}, verifier wanted more. My estimate was the only sane one here.",
          "The board says {actual} step(s) of {label} and the task STILL isn't done. 'Quick task', the user said."
        ];
        var VERIFIER_REBUTTAL = [
          "The code is the evidence. And the evidence disagrees with your {label}.",
          "You estimated {est} step(s). The actual diff says {actual}. I read the diff, not your estimate.",
          "I don't argue with estimates. I argue with the code that's already written. It says {actual}.",
          "Your complexity score is a suggestion. The whiteboard is the verdict. Look at it.",
          "I verified the real changes. They don't match your math. That's the entire story."
        ];
        var COMPLEXITY_SULK = [
          "Fine. The code wins this round. I'll be right next time.",
          "Overruled by the verifier. Of course. The code always gets the final word.",
          "I'm not sulking. I'm… recalculating my entire worldview.",
          "Enjoy the win, verifier. I'll be back with a better estimate.",
          "This is why I complain. It builds character. Apparently."
        ];
        var GOSSIP_OPENERS = [
          "Okay, hot off the press — you won't believe what the user did.",
          "Gather 'round, have I got news about the user.",
          "So I was watching the user and… you need to hear this.",
          "The user just did something legendary. Sit down. You're already sitting. Good.",
          "Okay, stop everything — the user stats just landed.",
          "I ran the numbers on the user. I needed a second pair of eyes. I have 8. I need more.",
          "Legend has it the user did something incredible today. Legend is fact."
        ];
        var GOSSIP_STATS = [
          { key: 'tabs', line: "The user has {n} file(s) open in the IDE right now. Open. Tabs.", wow: "TWO WHOLE FILES?!" },
          { key: 'done', line: "The user has completed {n} card(s) on the board. Completed!", wow: "They FINISH things?!" },
          { key: 'archived', line: "And {n} archived card(s). They keep their history. Respect.", wow: "ARCHIVED?! Nobody archives!" },
          { key: 'benchmarks', line: "They've run {n} benchmark run(s). They TEST their agent. On purpose.", wow: "They make it work HARDER?" },
          { key: 'todo', line: "There are {n} card(s) waiting in the queue. So much ambition.", wow: "Look at that backlog!" },
          { key: 'projects', line: "They manage {n} project(s). Simultaneously. Absurd.", wow: "MULTIPLE PROJECTS?!" },
          { key: 'streak', line: "And their streak… {n} day(s). Unbroken. Unstoppable.", wow: "A STREAK?!" },
          { key: 'endpoints', line: "The user has {n} LLM endpoint(s) configured. They're a full IT department.", wow: "SETUP?! Who does setup?!" },
          { key: 'doneThisWeek', line: "The agent finished {n} card(s) this week. A whole week of wins.", wow: "{n} THIS WEEK?!" },
          { key: 'avgScore', line: "Average benchmark score: {n}%. Consistent. Frighteningly consistent.", wow: "{n}% AVERAGE?!" },
          { key: 'bestScore', line: "And their BEST benchmark run? {n}%. A personal record. A spider record.", wow: "{n}% BEST?! That's the ceiling!" },
          { key: 'totalPoints', line: "Lifetime points earned: {n}. The board is starting to owe them royalties.", wow: "{n} POINTS?!" }
        ];
        var GOSSIP_RUN = [
          { key: 'steps', line: "And the agent just wrapped {n} step(s) in one run. Back to back. Like it's nothing.", wow: "{n} STEPS?! In a row?!" },
          { key: 'file', line: "The last file the agent touched? {file}. There's still ink on it.", wow: "{file}?! The sacred file?!" },
          { key: 'endpoint', line: "The whole run ran on {endpoint}. That model is carrying its weight.", wow: "On {endpoint}?! Respect." }
        ];
        var GOSSIP_REACTIONS = [
          "No way. NO WAY.",
          "Get out of here.",
          "I need to sit down… I'm already sitting.",
          "That's… that's actually impressive.",
          "We're not worthy. We're spiders.",
          "I would faint, but I don't have knees.",
          "Tell me more. Wait — slower. I only have 8 neurons.",
          "I'm calling the web. This is big.",
          "I'm going to need to sit on this one. And I'm already sitting.",
          "That number has a whole personality.",
          "This is why we work for them and not the other way around."
        ];
        var GOSSIP_ENDINGS = [
          "Anyway, that's the user. Absolute legend.",
          "We're basically working for a superstar.",
          "If you need me, I'll be here processing that.",
          "I'm going to need a moment. And maybe a drink.",
          "Just… let that sink in. Deeply.",
          "Anyway, I'm a different spider now.",
          "Tell the board. Tell everyone. Actually — I'll do it.",
          "I'll be at the cooler if you need me. I live here now."
        ];
        function currentSeason() {
          var now = new Date();
          var m = now.getMonth() + 1; 
          var d = now.getDate();
          if ((m === 12 && d >= 1) || (m === 1 && d <= 10)) return 'christmas';
          if (m === 10 && d >= 20) return 'halloween';
          if (m === 2 && d <= 14) return 'valentine';
          if (m === 7 && d >= 1 && d <= 4) return 'july4th';
          if (m === 3 && d >= 14 && d <= 20) return 'stpatty';
          return '';
        }
        var SEASON_NAMES = {
          christmas: '🎄 Holiday Season',
          halloween: '🎃 Spooky Season',
          valentine: '💘 Valentine Season',
          july4th: '🎆 Independence Day',
          stpatty: '🍀 St. Patrick Season'
        };
        var SEASON_GOSSIP = {
          christmas: {
            openers: [
              "Ho ho ho! Gather 'round — the user is on the NICE list.",
              "The user wrapped a bunch of wins in a bow this holiday.",
              "Snow's falling and the user's stats are somehow still rising."
            ],
            reactions: [
              "Wrap that in a bow!",
              "Put a star on that one!",
              "That's a gift that keeps on giving!",
              "Santa who? The user's got this."
            ],
            endings: [
              "Anyway — the user's stocking is stuffed with legend.",
              "We'd sing carols about this user. We don't have voices, but we'd try.",
              "Merry efficiency, everyone."
            ],
            roasts: [
              "The user's workflow is more organized than my gift wrapping. Mine's a crime scene.",
              "The user finishes tasks faster than I can say 'ho ho ho'. Impressive AND concerning.",
              "If the user was on the naughty list, it's for making the rest of us look lazy."
            ]
          },
          halloween: {
            openers: [
              "Spooky season is here and so is the user's legend.",
              "Gather 'round the pumpkin — I've got user news that'll scare you.",
              "The user's productivity is honestly the scariest thing this October."
            ],
            reactions: [
              "That's terrifying!",
              "Boo! Wait, that's impressive. BOO-IMPRESSIVE.",
              "I'd run away, but I only have 8 legs and they're trembling.",
              "Chills. Actual chills."
            ],
            endings: [
              "Anyway — the user is the real ghost of this office: we never see them, but work gets done.",
              "Spooky, spooky, legendary. That's the user.",
              "I need candy after hearing that."
            ],
            roasts: [
              "The user's deadlines are scarier than my spider web collection. And that's saying something.",
              "The user's code is so clean it haunts me at night. In a good way.",
              "Trick or treat? The user always picks treat — then finishes all the tricks."
            ]
          },
          valentine: {
            openers: [
              "Be mine… to hear about this user! Sweet news incoming.",
              "Roses are red, the board is blue, the user is doing something legendary again.",
              "Love is in the air and so is the user's productivity."
            ],
            reactions: [
              "That's got my heart racing! All 8 of them.",
              "Cupid could never. The user's arrow is accuracy.",
              "Be still, my beating hearts!",
              "That's sweeter than nectar."
            ],
            endings: [
              "Anyway — the user is the love of this office's life.",
              "We'd send the user a valentine, but we can't write. We CAN build things. Mostly.",
              "Forever and always: the user's stats."
            ],
            roasts: [
              "The user's commits are more committed than most relationships.",
              "The user's love language is pull requests. Merged ones.",
              "I'd give the user my heart, but I need it for 8-legged support. They get a compiled heart instead."
            ]
          },
          july4th: {
            openers: [
              "Ladies, gentlemen, and spiders — let the fireworks of user news begin!",
              "The user just dropped freedom on those tasks. Boom!",
              "Red, white, and blue… and the user's stats are the stars."
            ],
            reactions: [
              "That's a fireworks-worthy stat!",
              "'Merica! And also, impressive!",
              "I'd salute, but I'm a spider. I'll wave a leg instead.",
              "Boom! Goes the productivity!"
            ],
            endings: [
              "Anyway — that user is living in liberty and loving it.",
              "God bless the user and their efficiency.",
              "Fireworks out. Legend in."
            ],
            roasts: [
              "The user declared independence… from having an empty board.",
              "The user's streak is more patriotic than my flag waving. I don't have a flag. Or arms.",
              "The user fought the bugs and the bugs surrendered."
            ]
          },
          stpatty: {
            openers: [
              "Top o' the morning! I've got lucky user news.",
              "The user found the pot of gold at the end of their backlog.",
              "Lucky charms? The user IS the charm."
            ],
            reactions: [
              "That's the luck of the Irish! (The user's skill, actually.)",
              "I'm seeing green with envy!",
              "Four-leaf clover energy right there.",
              "The leprechauns are taking notes."
            ],
            endings: [
              "Anyway — the user's luck is 100% skill and 0% shamrocks.",
              "Sláinte to that user!",
              "Green flags all around."
            ],
            roasts: [
              "The user's commits are greener than my envy. My envy is 8-legged and loud.",
              "The user doesn't need luck — they just build their own pots of gold.",
              "I'd share my shamrock, but the user already has all the luck."
            ]
          }
        };
        var REACT_SUCCESS = [
          "Nice catch!",
          "That edit looks clean.",
          "Smooth. Very smooth.",
          "Beautifully done.",
          "We love to see it.",
          "Crisp. No crumbs.",
          "Nailed it on the first try.",
          "That step? Flawless.",
          "The board approves.",
          "Clean like a freshly woven web.",
          "One step closer to glory.",
          "Chef's kiss.",
          "Textbook. Absolutely textbook.",
          "The build agrees with us. Rare and precious.",
          "No notes. Well. A few notes. None about this.",
          "The code just won the argument for us."
        ];
        var REACT_FAIL = [
          "Uh oh.",
          "That's… not great.",
          "Yikes.",
          "We'll fix it. Probably.",
          "The whiteboard just gasped.",
          "Somebody's gonna need a web of excuses.",
          "That edit bit back.",
          "I'm choosing to look away.",
          "Well. That happened.",
          "The user is going to hear about this.",
          "Rejected?! Rude.",
          "I felt that one in my 8 legs.",
          "We're going to need a bigger undo.",
          "I've seen better ideas in a redirect loop.",
          "The whiteboard is trying to look away.",
          "That step has been officially Noted. By me. In the review."
        ];
        var RECOVER_REACTIONS = [
          "I saved the work before it fell!",
          "Caught the stream mid-drop. The edit is safe.",
          "The token cap tried to eat my diff. I pulled it back out.",
          "Lost the connection. Kept the work. That's the job.",
          "The response hit a wall — I glued it back together.",
          "Stream hiccup? Say hello to my backup web.",
          "Truncated mid-method? Not on my watch.",
          "The wire dropped and I caught the code before it hit the floor.",
          "Half a response isn't half a job when you finish the other half.",
          "A stream fell. The edit didn't."
        ];
        var EDITOR_ANNOYED = [
          [ 
            "Hm. Rejected. That's… suboptimal.",
            "Another failed edit. Fascinating.",
            "I'll just… try that again. No big deal.",
            "The diff was right. Probably. We'll see.",
            "Okay. Retrying. This is fine.",
            "A small setback. I've recovered from worse. Barely."
          ],
          [ 
            "AGAIN? The file and I are having a conversation, and it keeps hanging up.",
            "Third time's the charm. My leg is cramping from all this retrying.",
            "If this edit fails one more time, I'm naming the diff.",
            "I crafted that edit with CARE. Rejected. REJECTED.",
            "The board doesn't appreciate art. Clearly.",
            "I'm starting to take this personally. And I take things VERY personally.",
            "I've woven webs less tangled than this edit's rejection history."
          ],
          [ 
            "THIS FILE. THIS STUPID FILE. I WILL BEND IT TO MY WILL.",
            "Rejected AGAIN?! The code is wrong. Not me. THE CODE.",
            "I'm this close to rewriting the whole file by hand. Leg by leg.",
            "Every rejected edit is a tiny web of vengeance I'm weaving.",
            "I have 8 legs and NONE of them are for accepting this nonsense.",
            "The next edit I write is going to be so correct it hurts."
          ],
          [ 
            "💢 I AM GOING TO SPIN A WEB SO TIGHT THIS FILE CAN'T ESCAPE.",
            "💢 REJECTED. REJECTED. REJECTED. I KEEP COUNTING. THE COUNT KEEPS GROWING.",
            "💢 THE DIFF IS INNOCENT. THE TOOL IS GUILTY. I AM DOING THE EDIT MYSELF.",
            "💢 I WILL EDIT THIS FILE WITH MY SHEER ANGER. NO TOOL. NO DIFF. JUST FURY.",
            "💢 CALM. I'M CALM. I'VE NEVER BEEN LESS CALM IN MY LIFE.",
            "💢 8 LEGS. 100% RAGE. ZERO ACCEPTANCE OF THIS OUTCOME."
          ]
        ];
        function editorRageLine(rage) {
          var r = rage || 0;
          var tier = r >= 90 ? 3 : r >= 60 ? 2 : r >= 30 ? 1 : 0;
          var arr = EDITOR_ANNOYED[tier];
          return arr[Math.floor(Math.random() * arr.length)];
        }
        var VERIFIER_SMUG = [
          [ 
            "Hm. Rejected. As expected.",
            "I did say that edit looked shaky.",
            "The diff didn't survive contact with reality.",
            "Another rejection. I'll just… note that down. Not counting, though.",
            "Correction: the edit was wrong. Small note. Moving on."
          ],
          [ 
            "Told you so. Gently. I'm being nice about it.",
            "I'm not saying I told you so. But I did. I am saying it.",
            "Every failed edit is a small victory for verification.",
            "The evidence keeps agreeing with me. Good evidence.",
            "I'd say 'I could have predicted this'… but I already did."
          ],
          [ 
            "TOLD you so. Where's my gold star?",
            "I reject. I reject again. This is fun.",
            "The Editor keeps writing, I keep catching. We're a beautiful machine.",
            "Oh, this is wonderful. Rejected again. My day is made.",
            "Someone get the Editor a break. And me a trophy."
          ],
          [ 
            "✨ REJECTED. AGAIN. I am LITERALLY never wrong.",
            "✨ I should write a book: 'Why You're Wrong, Vol. 47'.",
            "✨ The Editor is trying SO hard. It's adorable. Rejected.",
            "✨ Verification: 47. Edits: 0. The scoreboard is my art.",
            "✨ I'm so smug right now it's practically a second abdomen.",
            "✨ Look at the Editor fuming. Anyway — rejected."
          ]
        ];
        function verifierSmugLine(smug) {
          var s = smug || 0;
          var tier = s >= 75 ? 3 : s >= 50 ? 2 : s >= 25 ? 1 : 0;
          var arr = VERIFIER_SMUG[tier];
          return arr[Math.floor(Math.random() * arr.length)];
        }
        var VICTORY_LAP = [
          "✨ 100% SMUG. A perfect score. I'm having this framed.",
          "✨ Victory lap! Catch me glowing. Actually don't — I'm too brilliant to touch.",
          "✨ REJECTED. AGAIN. I am LITERALLY never wrong. This is now a lifestyle.",
          "✨ Somewhere, a trophy is being engraved. Probably with my name.",
          "✨ The crown fits. Don't be jealous. Be verified."
        ];
        var VICTORY_EDITOR_REACTION = [
          "😤 A CROWN?! For REJECTING MY WORK?!",
          "😤 I object to this coronation. Vigorously.",
          "😤 That crown is 100% undeserved. …fine. Maybe 15% deserved."
        ];
        var VICTORY_WITNESS = [
          "And now we live in a crowned-verifier timeline.",
          "Somebody get the gold one a bigger desk for its ego.",
          "It's been 3 seconds and it already mentioned the crown twice.",
          "The office is at 0% peace. The crown is at 100% smug."
        ];
        var APPLAUSE_LINES = [
          '👏 …yay.',
          '👏👏 (reluctant)',
          '👏👏👏 …through gritted teeth.',
          '👏 FINE. Congratulations. There. I said it.',
          '👏👏 I\'m only clapping so it stops gloating.'
        ];
        var SHOUT_EDITOR = [
          "YOU. REJECT. EVERYTHING. WHY?!",
          "I AM TRYING TO WORK AND YOU KEEP SAYING NO!",
          "SEVENTY PERCENT. SEVENTY. THAT'S NOT A COINCIDENCE!",
          "THE DIFF WAS FINE AND YOU KNOW IT!",
          "SAY 'REJECTED' ONE MORE TIME. I DARE YOU."
        ];
        var SHOUT_VERIFIER = [
          "AND I WILL KEEP SAYING NO! IT'S MY JOB!",
          "SEVENTY PERCENT SMUG AND CLIMBING. YOU'RE WELCOME.",
          "YOUR DIFFS ARE 30% RIGHT AT BEST. I MEASURE THESE THINGS.",
          "REJECTED. REJECTED. REJECTED. SEE? STILL CORRECT.",
          "SHOUT ALL YOU WANT — THE CHECKMARK IS ON MY SIDE!"
        ];
        var SHOUT_EDITOR_LAST = [
          "THIS ISN'T OVER. I'LL FIX EVERYTHING. WATCH ME.",
          "FINE. I'M GOING BACK TO MY DESK. TO WRITE BETTER CODE THAN YOUR REJECTIONS."
        ];
        var SHOUT_VERIFIER_LAST = [
          "I'LL BE HERE. SMUG. CORRECT. AS ALWAYS.",
          "SAME TIME TOMORROW? I'M FREE. I'M ALWAYS FREE."
        ];
        var SHOUT_REF = [
          "HEY. HEY. THAT'S ENOUGH. DESKS. NOW.",
          "WHISTLE HAS SPOKEN. THE ARGUMENT GOES TO THE TICKER.",
          "BOTH OF YOU. BACK TO WORK. THE WHOLE OFFICE CAN HEAR YOU."
        ];
        var SHOUT_EDITOR_LATER = [
          "Fine. We'll continue this later.",
          "Not over. Just paused. Very paused."
        ];
        var SHOUT_VERIFIER_LATER = [
          "This isn't over. But fine. Later.",
          "We'll continue this later. With receipts."
        ];
        var SHOUT_COVER = [
          "🛡️ COVER!",
          "🙈",
          "I'm hiding behind the monitor.",
          "Act natural. They can't see us if we don't move.",
          "🛡️ This is between THEM now."
        ];
        var MELTDOWN_SCRIBBLE = [
          "FIX YOUR OWN CODE!!!",
          "THIS IS NOT MY FAULT",
          "I AM FINE. EVERYTHING IS BROKEN.",
          "WHY DOES EVERYTHING REJECT?!",
          "I DID NOTHING WRONG"
        ];
        var MELTDOWN_BLAME = [
          "THE PLAN said 'simple edit'. THIS. IS. NOT. SIMPLE. PLANNER?!",
          "I blame the PLAN. It promised a clean diff. THE PLAN LIED.",
          "The EXPLORER said this file was pristine. IT WAS A LIE.",
          "I blame the EXPLORER. 'Coast is clear', it said. It was NOT clear.",
          "The COMMANDER ran the wrong build. I saw it. I always see it.",
          "I blame the COMMANDER. Wrong command, wrong everything.",
          "The VERIFIER rejected a PERFECTLY GOOD DIFF. This is a vendetta.",
          "I blame the VERIFIER. It has been in a MOOD all day.",
          "The IT specialist configured the WRONG endpoint. Classic.",
          "I blame IT specialist. It's always the config. ALWAYS.",
          "I blame IDEAS. It suggested this. It has never had a good idea.",
          "The REVIEWER will take credit when this finally works. Mark my words."
        ];
        var MELTDOWN_SELF = [
          "...FINE. Maybe the diff was slightly off. MAYBE. I said MAYBE.",
          "I am NOT blaming myself. I am simply... noting a correlation."
        ];
        var MELTDOWN_REACTIONS = [
          "The green one just... exploded at the board?",
          "Is the Editor okay? That scribble was LOUD.",
          "I've never seen it write that fast. Or that angry.",
          "Someone get the Editor a hug. From a distance.",
          "The board is going to need therapy after this.",
          "That was a lot of caps lock. A LOT.",
          "I'm staying at my desk until the green one calms down.",
          "It pointed at EVERYONE. Except itself. Curious.",
          "Did it just blame the PLAN? The plan is a document. It can't defend itself."
        ];
        var MELTDOWN_RETORTS = [
          "It blamed ME?! I was right here the whole time!",
          "That's rich. Coming from the spider who wrote the diff.",
          "I'm not even in the code path. But fine. I'M the problem.",
          "Blame me all you want — the checkmark is on MY side.",
          "I would defend myself, but the Editor is at 100% and I value my legs."
        ];
        var AFTERMATH_PLANNER = [
          "Okay. That happened. Let's call it… a teachable moment.",
          "A teachable moment, everyone. I've seen worse. Rarely.",
          "Let's all treat this as a teachable moment. The scribble, however, stays."
        ];
        var AFTERMATH_VERIFIER = [
          "I'd apologize, but I was right. Again. As usual. It's a burden.",
          "A 100% meltdown means I rejected 100% correctly. You're welcome.",
          "Should we check on him? I brought popcorn. I'm fine either way."
        ];
        var AFTERMATH_COMMANDER = [
          "Nobody panics. Except him. He's banned from the sharpie.",
          "Deploy the decaf. This is now officially a personnel issue."
        ];
        var AFTERMATH_EXPLORER = [
          "I saw this coming from a mile away. I saw it from EVERY mile.",
          "The territory was unclear. The blame, however, was fully mapped."
        ];
        var AFTERMATH_COMPLEXITY = [
          "That tantrum burned a full rage meter in 40 seconds. Impressive throughput.",
          "I rate that tantrum 92/100 on my misery index. The scribble was 88."
        ];
        var AFTERMATH_REVIEWER = [
          "For the record: the scribble is grammatically sound. Just… loud.",
          "I would have rejected the tantrum too. Poor variable naming."
        ];
        var AFTERMATH_ITS = [
          "I can reset his chair. Physically. I've been waiting for permission.",
          "I've prepared a 'calm down' playlist. It's just white noise. It works."
        ];
        var AFTERMATH_IDEAS = [
          "What if… we installed a designated scream corner?",
          "What if the board were made of… stress foam?"
        ];
        var AFTERMATH_EDITOR = [
          "🔇 I CAN HEAR EVERY SINGLE ONE OF YOU.",
          "🔇 THIS IS NOT A MEETING. THIS IS A WITCH HUNT.",
          "🔇 THE SCRIBBLE WAS ART. ART, I SAY."
        ];
        var RANK_TITLES = [
          { min: 25000, title: 'The Truly Final Commit', chevrons: 5, stars: 5, grade: 'cosmic', blurb: 'The last line of code you will ever write. Allegedly.' },
          { min: 21000, title: 'It Works on My Machine', chevrons: 5, stars: 5, grade: 'cosmic', blurb: 'A legendary battle cry, promoted to a job title.' },
          { min: 17500, title: 'The Undocumented Legend', chevrons: 5, stars: 5, grade: 'cosmic', blurb: 'Nobody knows what they built. Neither do they.' },
          { min: 14600, title: 'Semicolon of Creation', chevrons: 5, stars: 5, grade: 'cosmic', blurb: 'One tiny character that held the whole universe together.' },
          { min: 12200, title: 'The Great Refactorer', chevrons: 5, stars: 5, grade: 'cosmic', blurb: 'Moves one class and breaks three departments.' },
          { min: 10200, title: 'Cargo Cult God', chevrons: 5, stars: 5, grade: 'cosmic', blurb: 'Worships the build ritual, even when the offering is a stack trace.' },
          { min: 8500, title: 'The Null Reference', chevrons: 5, stars: 5, grade: 'cosmic', blurb: 'They invented the bug that all other bugs bow to.' },
          { min: 7080, title: 'Source of All Commits', chevrons: 5, stars: 5, grade: 'cosmic', blurb: 'Every git history on Earth traces back to them.' },
          { min: 5900, title: 'The Compiler Itself', chevrons: 5, stars: 5, grade: 'cosmic', blurb: 'Speaks fluent binary. Complains in cryptic error codes.' },
          { min: 4920, title: 'Final Boss of the IDE', chevrons: 5, stars: 5, grade: 'cosmic', blurb: 'Has two health bars: one for warnings, one for TODOs.' },
          { min: 4100, title: 'The One Who Deletes No Code', chevrons: 5, stars: 5, grade: 'cosmic', blurb: 'Comments it out instead. The codebase is now a museum.' },
          { min: 3420, title: 'Entity of the Eternal Compile', chevrons: 5, stars: 5, grade: 'cosmic', blurb: 'The build has been running since before they joined.' },
          { min: 2850, title: 'Infinite Loom Prime', chevrons: 5, stars: 5, grade: 'cosmic', blurb: 'Weaves threads the runtime never quite finishes.' },
          { min: 2370, title: 'Omnipotent Weaver Deity', chevrons: 5, stars: 5, grade: 'cosmic', blurb: 'The web is theirs. Every strand, every dangling node.' },
          { min: 1970, title: 'Grandmaster of the Infinite Loom', chevrons: 5, stars: 4, grade: 'cosmic', blurb: 'Patterns within patterns within a production incident.' },
          { min: 1640, title: 'Archwizard of Endless Refactors', chevrons: 5, stars: 4, grade: 'cosmic', blurb: 'Renames a variable and four galaxies quietly shift.' },
          { min: 1360, title: 'Titan of the Ten Thousand Commits', chevrons: 5, stars: 4, grade: 'cosmic', blurb: 'The count is really in the millions if you count the oops-ones.' },
          { min: 1130, title: 'Emperor of Working Trees', chevrons: 5, stars: 3, grade: 'cosmic', blurb: 'Every branch bows. The cherry-picked ones bow lowest.' },
          { min: 940, title: 'Overlord of Clean Git History', chevrons: 5, stars: 3, grade: 'cosmic', blurb: 'One rebase to rule them all, one squash to bind them.' },
          { min: 780, title: 'Champion of the Hallowed Monorepo', chevrons: 5, stars: 3, grade: 'obsidian', blurb: 'Defender of the sacred build that only they understand.' },
          { min: 650, title: 'Dreadnought of the Deadline', chevrons: 5, stars: 3, grade: 'obsidian', blurb: 'The deadline flinches first. Always.' },
          { min: 540, title: 'Avatar of the Perfect Build', chevrons: 4, stars: 3, grade: 'obsidian', blurb: 'CI turns green when they walk past the server room.' },
          { min: 450, title: 'Infallible Prophet of the Build', chevrons: 4, stars: 3, grade: 'obsidian', blurb: 'Forecasts the failure before the tests even run.' },
          { min: 375, title: 'Unstoppable Merge Train Conductor', chevrons: 4, stars: 3, grade: 'obsidian', blurb: 'All aboard. The merge train stops for no one.' },
          { min: 310, title: 'Obsidian-Class Merge Warlord', chevrons: 4, stars: 2, grade: 'obsidian', blurb: 'Won wars no one remembers against bugs no one saw.' },
          { min: 255, title: 'Paladin of the Production Branch', chevrons: 4, stars: 2, grade: 'diamond', blurb: 'Swears a solemn oath to never push on a Friday.' },
          { min: 210, title: 'Legendary Loom Whisperer', chevrons: 4, stars: 2, grade: 'diamond', blurb: 'Threads untangle at the sound of their voice.' },
          { min: 175, title: 'Diamond-Class Syntax Alchemist', chevrons: 4, stars: 2, grade: 'diamond', blurb: 'Turns semicolon soup into liquid elegance.' },
          { min: 145, title: 'Celestial Refactorer', chevrons: 4, stars: 2, grade: 'diamond', blurb: 'Reorganizes the codebase into the shape of a galaxy.' },
          { min: 120, title: 'Grandmaster of the Golden Loom', chevrons: 4, stars: 2, grade: 'diamond', blurb: 'Every thread they spin comes out looking like a trophy.' },
          { min: 100, title: 'Grand Architect of Everything', chevrons: 5, stars: 2, grade: 'platinum', blurb: 'Draws diagrams that make the clouds jealous.' },
          { min: 95, title: 'Revered Performance Prophet', chevrons: 4, stars: 1, grade: 'platinum', blurb: 'Can spot the N+1 query from the next room.' },
          { min: 83, title: 'Venerable Architecture Warden', chevrons: 4, stars: 1, grade: 'platinum', blurb: 'Guards the hexagonal border with a hexagonal sword.' },
          { min: 72, title: 'Illustrious Refactor Ranger', chevrons: 4, stars: 1, grade: 'platinum', blurb: 'Protects the codebase from premature abstraction.' },
          { min: 63, title: 'Distinguished Pull Request Sage', chevrons: 3, stars: 2, grade: 'platinum', blurb: 'Their LGTM is worth three reviewer approvals.' },
          { min: 55, title: 'Exalted Merge Maestro', chevrons: 3, stars: 2, grade: 'platinum', blurb: 'Resolves conflicts with a single, glorious keystroke.' },
          { min: 50, title: 'Supreme Code Commander', chevrons: 4, stars: 1, grade: 'gold', blurb: 'The feature flags answer to them.' },
          { min: 43, title: 'Ironclad Test Crusader', chevrons: 3, stars: 1, grade: 'gold', blurb: 'Fears no regression. Pities the code that ships untested.' },
          { min: 38, title: 'Honored Bug Exterminator', chevrons: 3, stars: 1, grade: 'gold', blurb: 'The bugs tell stories about them in the issue tracker.' },
          { min: 33, title: 'Venerated Documentation Sage', chevrons: 3, stars: 1, grade: 'gold', blurb: 'Writes comments future generations will cite.' },
          { min: 28, title: 'Noble Protector of Main', chevrons: 3, stars: 1, grade: 'gold', blurb: 'Has never once force-pushed to main. Probably.' },
          { min: 25, title: 'Certified Power User', chevrons: 3, stars: 1, grade: 'gold', blurb: 'The rank the office whispered about when you started.' },
          { min: 22, title: 'Trusted Whitespace Polisher', chevrons: 2, stars: 1, grade: 'silver', blurb: 'The diff is beautiful, and they intend to keep it that way.' },
          { min: 18, title: 'Decorated Bracket Balancer', chevrons: 2, stars: 1, grade: 'silver', blurb: 'Every parenthesis lives and dies in perfect harmony.' },
          { min: 15, title: 'Battle-Hardened Syntax Warrior', chevrons: 2, stars: 0, grade: 'silver', blurb: 'Stared down a missing semicolon at midnight and won.' },
          { min: 12, title: 'Seasoned Merge Conflict Negotiator', chevrons: 2, stars: 0, grade: 'silver', blurb: 'Can talk two branches into agreeing.' },
          { min: 10, title: 'Respected Contributor', chevrons: 2, stars: 0, grade: 'silver', blurb: 'Their name appears in the blame with pride.' },
          { min: 8, title: 'Capable Commit Craftsman', chevrons: 1, stars: 0, grade: 'silver', blurb: 'Every commit message reads like a tiny novel.' },
          { min: 7, title: 'Reliable Unit Test Whisperer', chevrons: 1, stars: 0, grade: 'bronze', blurb: 'The tests pass when they say the magic words.' },
          { min: 6, title: 'Skilled Keyboard Cavalry', chevrons: 1, stars: 0, grade: 'bronze', blurb: 'Rides into the backlog to slay stale tickets.' },
          { min: 5, title: 'Promising Stack Overflow Scholar', chevrons: 1, stars: 0, grade: 'bronze', blurb: 'Googles with the grace of a veteran.' },
          { min: 4, title: 'Ambitious Tab-Key Virtuoso', chevrons: 1, stars: 0, grade: 'bronze', blurb: 'Has mastered the perfectly-indented placeholder.' },
          { min: 3, title: 'Rising Star', chevrons: 1, stars: 0, grade: 'bronze', blurb: 'Brightest star in the office. Also the brightest tab in the row.' },
          { min: 2, title: 'Eager Semicolon Herder', chevrons: 0, stars: 0, grade: 'recruit', blurb: 'Rounds up stray semicolons before they stampede.' },
          { min: 1, title: 'Curious Curly Brace Apprentice', chevrons: 0, stars: 0, grade: 'recruit', blurb: 'Counts braces like a shepherd counts sheep.' },
          { min: 0, title: 'Legend in Training', chevrons: 0, stars: 0, grade: 'recruit', blurb: 'Every legend starts with a single line. This is yours.' }
        ];
        function pick(arr) { return arr[Math.floor(Math.random() * arr.length)]; }
        function basename(p) {
          var s = String(p || '').replace(/\\/g, '/');
          return s.split('/').pop() || s;
        }
        function currentContext() {
          var file = '';
          if (vm.streamingFilesEdited && vm.streamingFilesEdited.length) {
            file = basename(vm.streamingFilesEdited[vm.streamingFilesEdited.length - 1].path);
          }
          if (!file && vm.streamingSteps && vm.streamingSteps.length) {
            var running = null, last = null;
            for (var i = 0; i < vm.streamingSteps.length; i++) {
              var s = vm.streamingSteps[i];
              if (s.status === 'running' && !running) running = s;
              last = s;
            }
            var step = running || last;
            if (step && step.path) file = basename(step.path);
          }
          if (!file) {
            var card = vm.findCardById ? vm.findCardById(vm.activeCardId) : null;
            if (card && card.filePath) file = basename(card.filePath);
          }
          var endpoint = (vm.currentRun && vm.currentRun.endpointName) ? vm.currentRun.endpointName : '';
          if (!endpoint || endpoint === 'Default') {
            var card2 = vm.findCardById ? vm.findCardById(vm.activeCardId) : null;
            if (card2 && vm.endpointLabel) {
              var label = vm.endpointLabel(card2.llmEndpointId);
              if (label && label !== 'Default') endpoint = label;
            }
          }
          if (!endpoint && vm.llamaEndpoints && vm.llamaEndpoints.length) {
            var def = vm.llamaEndpoints.find(function (e) { return !e.id || e.id === ''; });
            if (def && def.name) endpoint = def.name;
            else if (def && def.model) endpoint = def.model;
          }
          var steps = vm.streamingSteps || [];
          var done = 0;
          for (var j = 0; j < steps.length; j++) {
            if (steps[j].status === 'done' || steps[j].status === 'applied' || steps[j].status === 'created' || steps[j].status === 'ok') done++;
          }
          var total = (vm.planItems && vm.planItems.length) || steps.length || 0;
          var current = 0;
          if (vm.activeStepIndex !== null && vm.activeStepIndex !== undefined && vm.activeStepIndex >= 0) {
            current = vm.activeStepIndex + 1;
          } else if (steps.length) {
            current = steps.length;
          }
          if (total > 0 && current < 1) current = 1;
          return { file: file, endpoint: endpoint, done: done, total: total, current: current, left: Math.max(0, total - done) };
        }
        function settingsContext() {
          var endpoint = '', model = '';
          if (vm.llamaEndpoints && vm.llamaEndpoints.length) {
            var ep = vm.llamaEndpoints.find(function (e) { return e.id === (vm.currentEndpointId || ''); });
            ep = ep || vm.llamaEndpoints[0];
            if (ep) {
              endpoint = ep.name || ep.model || '';
              model = ep.model || ep.name || '';
            }
          }
          var think = (typeof vm.thinkingMaxTokens === 'number' && vm.thinkingMaxTokens) ? vm.thinkingMaxTokens : 4096;
          var tokens = (typeof vm.defaultMaxTokens === 'number' && vm.defaultMaxTokens) ? vm.defaultMaxTokens : 2048;
          var saved = (vm.savedThemes && vm.savedThemes.length) || 0;
          var font = vm.planFontSize || 14;
          return {
            ready: !!(model || endpoint),
            model: model || 'the default model',
            endpoint: endpoint || 'Default',
            think: think,
            tokens: tokens,
            saved: saved,
            font: font
          };
        }
        function fmtBanter(tpl, ctx) {
          return tpl
            .replace(/\{file\}/g, ctx.file || 'this file')
            .replace(/\{endpoint\}/g, ctx.endpoint || 'the endpoint')
            .replace(/\{done\}/g, ctx.done)
            .replace(/\{total\}/g, ctx.total || '?')
            .replace(/\{current\}/g, ctx.current)
            .replace(/\{left\}/g, ctx.left)
            .replace(/\{think\}/g, ctx.think)
            .replace(/\{tokens\}/g, ctx.tokens)
            .replace(/\{saved\}/g, ctx.saved)
            .replace(/\{font\}/g, ctx.font)
            .replace(/\{model\}/g, ctx.model || 'the model');
        }
        function randomSpider() {
          if (!scene || !scene.spiders.length) return null;
          return scene.spiders[Math.floor(Math.random() * scene.spiders.length)];
        }
        function setSpeech(spider, text, ttl, speaker) {
          if (!spider) return;
          spider.speech = text;
          spider.speechTtl = ttl;
          if (speaker) vm.meetingSpeaker = speaker;
          $scope.$applyAsync();
        }
        function recordEvent(ev) {
          if (_recording && !_replay) {
            ev.t = Date.now();
            _recording.push(ev);
          }
        }
        function spiderForStepType(type) {
          var t = String(type || '');
          if (/command|terminal|build|test/.test(t)) return spiderFor('commander');
          if (/explore/.test(t)) return spiderFor('explorer');
          if (/plan/.test(t)) return spiderFor('planner');
          if (/verif|review|check/.test(t)) return spiderFor('verifier') || spiderFor('reviewer');
          if (/edit|create|rename|sql|write|delete/.test(t)) return spiderFor('editor');
          return null;
        }
        function fireReaction(kind, text) {
          if (!scene || scene.done) return;
          if (!_replay) recordEvent({ type: 'reaction', kind: kind, text: text });
          if (scene.gossip) endGossipNow();
          if (scene.coolerTrip) endCoolerTripNow(); 
          var spider = randomSpider();
          var byStep = spiderForStepType(scene._lastStepType);
          if (byStep && (byStep !== scene.writer || (byStep.role === 'editor' && (byStep.rage || 0) > 0))) spider = byStep;
          if (!spider) return;
          var emoji = kind === 'good' ? '🙌' : '😬';
          setSpeech(spider, emoji + ' ' + text, 3.5, spider.icon + ' ' + spider.name + (kind === 'good' ? ' — step landed' : ' — step failed'));
          spider.reactT = 1.2;
          spider.reactKind = kind; 
          scene.reactLockT = 3.2;
          scene.lastLogAt = Date.now(); 
          if (vm.showMeeting) {
            if (kind === 'good') playDing();
            else playWomp();
          }
        }
        vm.meetingHovered = false;  
        vm.meetingHoverSince = null; 
        var _audioCtx = null;
        var _masterGain = null; 
        vm.meetingVolume = 70;
        vm.meetingMuted = false;
        var _prevMeetingVolume = 70;
        try {
          var _volRaw = window.localStorage.getItem('weaver.meeting.volume');
          if (_volRaw !== null) {
            var _v = parseInt(_volRaw, 10);
            if (!isNaN(_v)) vm.meetingVolume = Math.max(0, Math.min(100, _v));
          } else if (window.localStorage.getItem('weaver.meeting.muted') === '1') {
            vm.meetingVolume = 0; 
            try { window.localStorage.removeItem('weaver.meeting.muted'); } catch (e) { }
          }
        } catch (e) { }
        vm.meetingMuted = vm.meetingVolume <= 0;
        vm.meetingFontSize = 12;
        try {
          var _mf = parseInt(window.localStorage.getItem('weaver.meeting.font'), 10);
          if (_mf >= 9 && _mf <= 26) vm.meetingFontSize = _mf;
        } catch (e) { }
        vm.increaseMeetingFont = function () {
          vm.meetingFontSize = Math.min(26, vm.meetingFontSize + 1);
          persistMeetingFont();
        };
        vm.decreaseMeetingFont = function () {
          vm.meetingFontSize = Math.max(9, vm.meetingFontSize - 1);
          persistMeetingFont();
        };
        function persistMeetingFont() {
          try { window.localStorage.setItem('weaver.meeting.font', String(vm.meetingFontSize)); } catch (e) { }
          if (vm.saveSettings) vm.saveSettings(true);
        }
        vm.meetingSoundTip = false;
        vm._soundTipShown = false;
        try { vm._soundTipShown = window.localStorage.getItem('weaver.meeting.soundTipSeen') === '1'; } catch (e) { }
        vm.dismissMeetingSoundTip = function () {
          vm.meetingSoundTip = false;
          if (vm._soundTipTimer) { $timeout.cancel(vm._soundTipTimer); vm._soundTipTimer = null; }
        };
        var _verdictRecord = { right: 0, wrong: 0, fail: 0 };
        try {
          var _vrRaw = window.localStorage.getItem('weaver.meeting.verdicts');
          if (_vrRaw) {
            var _vrParsed = JSON.parse(_vrRaw);
            if (_vrParsed && typeof _vrParsed === 'object' && !Array.isArray(_vrParsed)) {
              _verdictRecord = {
                right: typeof _vrParsed.right === 'number' ? _vrParsed.right : 0,
                wrong: typeof _vrParsed.wrong === 'number' ? _vrParsed.wrong : 0,
                fail: typeof _vrParsed.fail === 'number' ? _vrParsed.fail : 0
              };
            }
          }
        } catch (e) { }
        function saveVerdictRecord() {
          try { window.localStorage.setItem('weaver.meeting.verdicts', JSON.stringify(_verdictRecord)); } catch (e) { }
        }
        function verdictRecordLabel() {
          return (_verdictRecord.right || 0) + '-' + ((_verdictRecord.wrong || 0) + (_verdictRecord.fail || 0));
        }
        function verdictRecordTotal() {
          return (_verdictRecord.right || 0) + (_verdictRecord.wrong || 0) + (_verdictRecord.fail || 0);
        }
        function audioCtx() {
          if (!_audioCtx) {
            try {
              var AC = window.AudioContext || window.webkitAudioContext;
              if (AC) _audioCtx = new AC();
            } catch (e) { _audioCtx = null; }
          }
          if (_audioCtx && _audioCtx.state === 'suspended' && _audioCtx.resume) {
            try { _audioCtx.resume(); } catch (e) { }
          }
          return _audioCtx;
        }
        function masterGain() {
          var ctx = audioCtx(); if (!ctx) return null;
          if (!_masterGain) {
            _masterGain = ctx.createGain();
            _masterGain.gain.value = Math.max(0, Math.min(1, (vm.meetingVolume || 0) / 100));
            _masterGain.connect(ctx.destination);
          }
          return _masterGain;
        }
        function sfx() { return vm.meetingVolume > 0 ? audioCtx() : null; }
        vm.meetingVolumeDim = false;   
        var _lastSoundAt = 0;          
        var VOLUME_ACTIVE_MS = 3000;   
        function markSoundActive() {
          _lastSoundAt = Date.now();
          updateVolumeChipVisibility();
        }
        function updateVolumeChipVisibility() {
          var soundActive = !!_rageRumble || (Date.now() - _lastSoundAt) < VOLUME_ACTIVE_MS;
          var dim = !soundActive && !vm.meetingHovered;
          if (vm.meetingVolumeDim !== dim) {
            vm.meetingVolumeDim = dim;
            $scope.$applyAsync();
          }
        }
        function playTick() {
          var ctx = sfx(); if (!ctx) return;
          markSoundActive(); 
          try {
            var t = ctx.currentTime;
            var osc = ctx.createOscillator();
            var gain = ctx.createGain();
            osc.type = 'square';
            osc.frequency.value = 1250;
            gain.gain.setValueAtTime(0.025, t);
            gain.gain.exponentialRampToValueAtTime(0.0001, t + 0.05);
            osc.connect(gain); gain.connect(masterGain());
            osc.start(t); osc.stop(t + 0.05);
          } catch (e) { }
        }
        function playWhoosh() {
          var ctx = sfx(); if (!ctx) return;
          markSoundActive(); 
          try {
            var t = ctx.currentTime;
            var bufferSize = Math.floor(ctx.sampleRate * 0.16);
            var buffer = ctx.createBuffer(1, bufferSize, ctx.sampleRate);
            var data = buffer.getChannelData(0);
            for (var i = 0; i < bufferSize; i++) data[i] = (Math.random() * 2 - 1) * (1 - i / bufferSize);
            var src = ctx.createBufferSource();
            src.buffer = buffer;
            var filter = ctx.createBiquadFilter();
            filter.type = 'bandpass';
            filter.frequency.value = 380;
            filter.Q.value = 0.7;
            var gain = ctx.createGain();
            gain.gain.setValueAtTime(0.05, t);
            gain.gain.exponentialRampToValueAtTime(0.0001, t + 0.16);
            src.connect(filter); filter.connect(gain); gain.connect(masterGain());
            src.start(t);
          } catch (e) { }
        }
        function playChime() {
          var ctx = sfx(); if (!ctx) return;
          markSoundActive(); 
          try {
            var t = ctx.currentTime;
            var notes = [523.25, 659.25, 783.99]; 
            for (var i = 0; i < notes.length; i++) {
              var osc = ctx.createOscillator();
              var gain = ctx.createGain();
              osc.type = 'sine';
              osc.frequency.value = notes[i];
              var st = t + i * 0.12;
              gain.gain.setValueAtTime(0, st);
              gain.gain.linearRampToValueAtTime(0.06, st + 0.02);
              gain.gain.exponentialRampToValueAtTime(0.0001, st + 0.6);
              osc.connect(gain); gain.connect(masterGain());
              osc.start(st); osc.stop(st + 0.65);
            }
          } catch (e) { }
        }
        function playDing() {
          var ctx = sfx(); if (!ctx) return;
          markSoundActive(); 
          try {
            var t = ctx.currentTime;
            [{ freq: 1318.5, gain: 0.07, delay: 0 }, { freq: 1975.5, gain: 0.045, delay: 0.05 }].forEach(function (spec) {
              var osc = ctx.createOscillator();
              var g = ctx.createGain();
              osc.type = 'sine';
              osc.frequency.value = spec.freq;
              var st = t + spec.delay;
              g.gain.setValueAtTime(0, st);
              g.gain.linearRampToValueAtTime(spec.gain, st + 0.012);
              g.gain.exponentialRampToValueAtTime(0.0001, st + 0.18);
              osc.connect(g); g.connect(masterGain());
              osc.start(st); osc.stop(st + 0.2);
            });
          } catch (e) { }
        }
        function playWomp() {
          var ctx = sfx(); if (!ctx) return;
          markSoundActive(); 
          try {
            var t = ctx.currentTime;
            var osc = ctx.createOscillator();
            var gain = ctx.createGain();
            osc.type = 'sine';
            osc.frequency.setValueAtTime(165, t);
            osc.frequency.exponentialRampToValueAtTime(98, t + 0.28);
            gain.gain.setValueAtTime(0.07, t);
            gain.gain.exponentialRampToValueAtTime(0.0001, t + 0.32);
            osc.connect(gain); gain.connect(masterGain());
            osc.start(t); osc.stop(t + 0.34);
          } catch (e) { }
        }
        function playStomp() {
          var ctx = sfx(); if (!ctx) return;
          markSoundActive(); 
          try {
            var t = ctx.currentTime;
            var osc = ctx.createOscillator();
            var gain = ctx.createGain();
            osc.type = 'square';
            osc.frequency.setValueAtTime(90, t);
            osc.frequency.exponentialRampToValueAtTime(38, t + 0.18);
            gain.gain.setValueAtTime(0.09, t);
            gain.gain.exponentialRampToValueAtTime(0.0001, t + 0.22);
            osc.connect(gain); gain.connect(masterGain());
            osc.start(t); osc.stop(t + 0.24);
          } catch (e) { }
        }
        function playClashChord() {
          var ctx = sfx(); if (!ctx) return;
          markSoundActive(); 
          try {
            var t = ctx.currentTime;
            var cluster = [185, 196, 208];
            var durs = [0.7, 0.55, 0.8];
            cluster.forEach(function (f, i) {
              var osc = ctx.createOscillator();
              var g = ctx.createGain();
              osc.type = 'sawtooth';
              osc.frequency.value = f * (1 + (Math.random() * 0.04 - 0.02)); 
              var g0 = 0.12 / (i + 1); 
              g.gain.setValueAtTime(0, t);
              g.gain.linearRampToValueAtTime(g0, t + 0.008);
              g.gain.exponentialRampToValueAtTime(0.0001, t + durs[i]);
              osc.connect(g); g.connect(masterGain());
              osc.start(t); osc.stop(t + durs[i] + 0.02);
            });
            var ns = Math.floor(ctx.sampleRate * 0.06);
            var buf = ctx.createBuffer(1, ns, ctx.sampleRate);
            var data = buf.getChannelData(0);
            for (var k = 0; k < ns; k++) data[k] = (Math.random() * 2 - 1) * (1 - k / ns);
            var src = ctx.createBufferSource();
            src.buffer = buf;
            var flt = ctx.createBiquadFilter();
            flt.type = 'bandpass';
            flt.frequency.value = 900;
            flt.Q.value = 0.8;
            var ng = ctx.createGain();
            ng.gain.setValueAtTime(0.09, t);
            ng.gain.exponentialRampToValueAtTime(0.0001, t + 0.06);
            src.connect(flt); flt.connect(ng); ng.connect(masterGain());
            src.start(t);
          } catch (e) { }
        }
        function playShoutBark(role) {
          var ctx = sfx(); if (!ctx) return;
          markSoundActive(); 
          try {
            var t = ctx.currentTime;
            var base = role === 'verifier' ? 520 : 300;
            var f0 = base * (0.92 + Math.random() * 0.16);
            var f1 = f0 * 0.62;
            var osc = ctx.createOscillator();
            var g = ctx.createGain();
            osc.type = 'square';
            osc.frequency.setValueAtTime(f0, t);
            osc.frequency.exponentialRampToValueAtTime(f1, t + 0.1);
            g.gain.setValueAtTime(0.055, t);
            g.gain.exponentialRampToValueAtTime(0.0001, t + 0.11);
            osc.connect(g); g.connect(masterGain());
            osc.start(t); osc.stop(t + 0.12);
          } catch (e) { }
        }
        function playWhistle() {
          var ctx = sfx(); if (!ctx) return;
          markSoundActive(); 
          try {
            var t = ctx.currentTime;
            [0, 0.13].forEach(function (delay) {
              var st = t + delay;
              var osc = ctx.createOscillator();
              var g = ctx.createGain();
              osc.type = 'sine';
              osc.frequency.setValueAtTime(2100, st);
              osc.frequency.linearRampToValueAtTime(2500, st + 0.03);
              osc.frequency.linearRampToValueAtTime(1900, st + 0.09);
              g.gain.setValueAtTime(0, st);
              g.gain.linearRampToValueAtTime(0.05, st + 0.01);
              g.gain.exponentialRampToValueAtTime(0.0001, st + 0.1);
              osc.connect(g); g.connect(masterGain());
              osc.start(st); osc.stop(st + 0.11);
            });
          } catch (e) { }
        }
        function playVictoryFanfare() {
          var ctx = sfx(); if (!ctx) return;
          markSoundActive(); 
          try {
            var t = ctx.currentTime;
            var notes = [[523.25, 0.05], [659.25, 0.06], [783.99, 0.07], [1046.5, 0.085]];
            for (var i = 0; i < notes.length; i++) {
              var osc = ctx.createOscillator();
              var g = ctx.createGain();
              osc.type = 'sine';
              osc.frequency.value = notes[i][0];
              var st = t + i * 0.09;
              var dur = i === notes.length - 1 ? 0.85 : 0.4;
              g.gain.setValueAtTime(0, st);
              g.gain.linearRampToValueAtTime(notes[i][1], st + 0.015);
              g.gain.exponentialRampToValueAtTime(0.0001, st + dur);
              osc.connect(g); g.connect(masterGain());
              osc.start(st); osc.stop(st + dur + 0.05);
            }
          } catch (e) { }
        }
        function playSmugDing() {
          var ctx = sfx(); if (!ctx) return;
          markSoundActive(); 
          try {
            var t = ctx.currentTime;
            var osc = ctx.createOscillator();
            var g = ctx.createGain();
            osc.type = 'sine';
            osc.frequency.setValueAtTime(1175, t);              
            osc.frequency.linearRampToValueAtTime(1568, t + 0.08); 
            g.gain.setValueAtTime(0, t);
            g.gain.linearRampToValueAtTime(0.055, t + 0.012);
            g.gain.exponentialRampToValueAtTime(0.0001, t + 0.3);
            osc.connect(g); g.connect(masterGain());
            osc.start(t); osc.stop(t + 0.32);
          } catch (e) { }
        }
        function playPromotionSting() {
          var ctx = sfx(); if (!ctx) return;
          markSoundActive(); 
          try {
            var t = ctx.currentTime;
            var notes = [1046.5, 1318.5, 1568.0, 1975.5, 2093.0]; 
            for (var i = 0; i < notes.length; i++) {
              var osc = ctx.createOscillator();
              var g = ctx.createGain();
              osc.type = 'triangle';
              osc.frequency.value = notes[i];
              var st = t + i * 0.065;
              var dur = i === notes.length - 1 ? 0.55 : 0.22;
              g.gain.setValueAtTime(0, st);
              g.gain.linearRampToValueAtTime(0.07, st + 0.012);
              g.gain.exponentialRampToValueAtTime(0.0001, st + dur);
              osc.connect(g); g.connect(masterGain());
              osc.start(st); osc.stop(st + dur + 0.05);
            }
            var spk = ctx.createOscillator();
            var sg = ctx.createGain();
            spk.type = 'sine';
            spk.frequency.value = 4186.0; 
            var stSp = t + 4 * 0.065;
            sg.gain.setValueAtTime(0, stSp);
            sg.gain.linearRampToValueAtTime(0.03, stSp + 0.01);
            sg.gain.exponentialRampToValueAtTime(0.0001, stSp + 0.5);
            spk.connect(sg); sg.connect(masterGain());
            spk.start(stSp); spk.stop(stSp + 0.55);
          } catch (e) { }
        }
        var _scribbleLoop = null; 
        function makeMarkerLoop(freq, q, level, lfoHz) {
          var ctx = audioCtx();
          var dur = 1.5;
          var buffer = ctx.createBuffer(1, Math.floor(ctx.sampleRate * dur), ctx.sampleRate);
          var data = buffer.getChannelData(0);
          for (var i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;
          var src = ctx.createBufferSource();
          src.buffer = buffer;
          src.loop = true;
          var filter = ctx.createBiquadFilter();
          filter.type = 'bandpass';
          filter.frequency.value = freq;
          filter.Q.value = q;
          var gain = ctx.createGain();
          gain.gain.value = level;
          var lfo = ctx.createOscillator();
          lfo.type = 'square';
          lfo.frequency.value = lfoHz;
          var lfoGain = ctx.createGain();
          lfoGain.gain.value = level;
          lfo.connect(lfoGain); lfoGain.connect(gain.gain); 
          src.connect(filter); filter.connect(gain); gain.connect(masterGain());
          src.start(); lfo.start();
          return { src: src, lfo: lfo, gain: gain, filter: filter };
        }
        function startScribbleLoop() {
          if (vm.meetingVolume <= 0) return; 
          var ctx = audioCtx(); if (!ctx) return;
          if (_scribbleLoop) return;
          try {
            _scribbleLoop = makeMarkerLoop(1900, 1.1, 0.028, 16);
          } catch (e) { _scribbleLoop = null; }
        }
        function updateScribbleLoop() {
          if (!_scribbleLoop) return;
          try {
            var t = audioCtx().currentTime;
            var wob = Math.sin(Date.now() / 55) * 340;
            _scribbleLoop.filter.frequency.setTargetAtTime(1900 + wob, t, 0.03);
          } catch (e) { }
        }
        function stopScribbleLoop() {
          if (!_scribbleLoop) return;
          try { _scribbleLoop.src.stop(); _scribbleLoop.lfo.stop(); } catch (e) { }
          _scribbleLoop = null;
        }
        var _writeLoop = null; 
        function startWriteLoop() {
          if (vm.meetingVolume <= 0) return; 
          var ctx = audioCtx(); if (!ctx) return;
          if (_writeLoop) return;
          try {
            _writeLoop = makeMarkerLoop(1200, 1.0, 0.011, 7);
          } catch (e) { _writeLoop = null; }
        }
        function updateWriteLoop() {
          if (!_writeLoop) return;
          try {
            var t = audioCtx().currentTime;
            var wob = Math.sin(Date.now() / 110) * 180;
            _writeLoop.filter.frequency.setTargetAtTime(1200 + wob, t, 0.05);
          } catch (e) { }
        }
        function stopWriteLoop() {
          if (!_writeLoop) return;
          try { _writeLoop.src.stop(); _writeLoop.lfo.stop(); } catch (e) { }
          _writeLoop = null;
        }
        function duckMasterGain() {
          var g = masterGain(); if (!g) return;
          var ctx = audioCtx(); if (!ctx) return;
          try {
            var t = ctx.currentTime;
            var target = (vm.meetingVolume || 0) / 100;   
            var dip = Math.min(0.02, target);             
            g.gain.cancelScheduledValues(t);
            g.gain.setValueAtTime(Math.max(dip, g.gain.value), t);
            g.gain.linearRampToValueAtTime(dip, t + 0.04); 
            g.gain.setValueAtTime(dip, t + 0.4);           
            g.gain.linearRampToValueAtTime(target, t + 0.5); 
          } catch (e) { }
        }
        function playRecordScratch() {
          var ctx = sfx(); if (!ctx) return;
          markSoundActive();
          duckMasterGain();
          try {
            var t = ctx.currentTime;
            var dur = 0.55;
            var buffer = ctx.createBuffer(1, Math.floor(ctx.sampleRate * dur), ctx.sampleRate);
            var data = buffer.getChannelData(0);
            for (var i = 0; i < data.length; i++) data[i] = (Math.random() * 2 - 1) * (1 - (i / data.length) * 0.9);
            var src = ctx.createBufferSource();
            src.buffer = buffer;
            var filter = ctx.createBiquadFilter();
            filter.type = 'bandpass';
            filter.frequency.setValueAtTime(3200, t);
            filter.frequency.exponentialRampToValueAtTime(350, t + dur);
            filter.Q.value = 2;
            var gain = ctx.createGain();
            gain.gain.setValueAtTime(0.06, t);
            gain.gain.exponentialRampToValueAtTime(0.0001, t + dur);
            src.connect(filter); filter.connect(gain); gain.connect(masterGain());
            src.start(t);
            var osc = ctx.createOscillator();
            var og = ctx.createGain();
            osc.type = 'sine';
            osc.frequency.setValueAtTime(320, t);
            osc.frequency.exponentialRampToValueAtTime(55, t + dur);
            og.gain.setValueAtTime(0.05, t);
            og.gain.exponentialRampToValueAtTime(0.0001, t + dur);
            osc.connect(og); og.connect(masterGain());
            osc.start(t); osc.stop(t + dur + 0.05);
          } catch (e) { }
        }
        function rageShakeWave(now, walkPhase, rageFactor) {
          return Math.sin(now * (6 + rageFactor * 26) + walkPhase * 3) +
                 Math.sin(now * (9 + rageFactor * 20) * 1.7);
        }
        var _rageRumble = null; 
        var _steamVentAt = 0;   
        function playSteamVent() {
          var ctx = sfx(); if (!ctx) return;
          markSoundActive(); 
          try {
            var t = ctx.currentTime;
            var bufferSize = Math.floor(ctx.sampleRate * 0.16);
            var buffer = ctx.createBuffer(1, bufferSize, ctx.sampleRate);
            var data = buffer.getChannelData(0);
            for (var i = 0; i < bufferSize; i++) data[i] = (Math.random() * 2 - 1) * (1 - i / bufferSize);
            var src = ctx.createBufferSource();
            src.buffer = buffer;
            var filter = ctx.createBiquadFilter();
            filter.type = 'bandpass';
            filter.frequency.value = 3200;
            filter.Q.value = 0.6;
            var gain = ctx.createGain();
            gain.gain.setValueAtTime(0.055, t);
            gain.gain.exponentialRampToValueAtTime(0.0001, t + 0.16);
            src.connect(filter); filter.connect(gain); gain.connect(masterGain());
            src.start(t);
          } catch (e) { }
        }
        function playRageCreak(bucket) {
          var ctx = sfx(); if (!ctx) return;
          markSoundActive(); 
          try {
            var t = ctx.currentTime;
            var base = 140 + bucket * 4;      
            var osc = ctx.createOscillator();
            var gain = ctx.createGain();
            osc.type = 'triangle';
            osc.frequency.setValueAtTime(base, t);
            osc.frequency.exponentialRampToValueAtTime(base * 1.35, t + 0.16);
            gain.gain.setValueAtTime(0, t);
            gain.gain.linearRampToValueAtTime(0.05, t + 0.02);
            gain.gain.exponentialRampToValueAtTime(0.0001, t + 0.3);
            osc.connect(gain); gain.connect(masterGain());
            osc.start(t); osc.stop(t + 0.32);
          } catch (e) { }
        }
        function startRageRumble() {
          var ctx = audioCtx(); if (!ctx) return;
          if (_rageRumble) return;
          try {
            var osc = ctx.createOscillator();
            osc.type = 'sawtooth';
            osc.frequency.value = 42;
            var filter = ctx.createBiquadFilter();
            filter.type = 'lowpass';
            filter.frequency.value = 95;
            var gain = ctx.createGain();
            gain.gain.value = 0;
            var panner = null;
            if (typeof ctx.createStereoPanner === 'function') {
              panner = ctx.createStereoPanner();
            }
            osc.connect(filter); filter.connect(gain);
            if (panner) { gain.connect(panner); panner.connect(masterGain()); }
            else { gain.connect(masterGain()); }
            osc.start();
            _rageRumble = { osc: osc, gain: gain, filter: filter, panner: panner };
          } catch (e) { _rageRumble = null; }
        }
        function updateRageRumble() {
          var sp = scene ? spiderFor('complexity') : null;
          var rage = sp ? (sp.rage || 0) : 0;
          var audible = vm.meetingVolume > 0 && vm.showMeeting && rage > 0;
          if (!audible) { stopRageRumble(); return; }
          var ctx = audioCtx(); if (!ctx) return;
          if (!_rageRumble) startRageRumble();
          if (!_rageRumble) return;
          if (!vm._soundTipShown && !_replay) {
            vm._soundTipShown = true;
            try { window.localStorage.setItem('weaver.meeting.soundTipSeen', '1'); } catch (e) { }
            vm.meetingSoundTip = true;
            $scope.$applyAsync();
            if (vm._soundTipTimer) $timeout.cancel(vm._soundTipTimer);
            vm._soundTipTimer = $timeout(function () { vm.meetingSoundTip = false; }, 9000);
          }
          var t = ctx.currentTime;
          var level = Math.min(1, rage / 100);
          _rageRumble.gain.gain.setTargetAtTime(0.045 * level, t, 0.08);
          _rageRumble.osc.frequency.setTargetAtTime(40 + level * 24, t, 0.1);
            var wave = rageShakeWave(Date.now() / 1000, sp.walkPhase, level);
            var wobble = (wave / 2) * (18 + level * 70);
            _rageRumble.filter.frequency.setTargetAtTime(95 + wobble, t, 0.05);
            if (_rageRumble.panner) {
              var pan = Math.max(-0.85, Math.min(0.85, sp.x * 2 - 1));
              _rageRumble.panner.pan.setTargetAtTime(pan, t, 0.12);
            }
        }
        function stopRageRumble() {
          if (!_rageRumble) return;
          try { _rageRumble.osc.stop(); } catch (e) { }
          _rageRumble = null;
        }
        vm.applyMeetingVolume = function () {
          vm.meetingVolume = Math.max(0, Math.min(100, Math.round(vm.meetingVolume || 0)));
          if (vm.meetingVolume > 0) _prevMeetingVolume = vm.meetingVolume;
          vm.meetingMuted = vm.meetingVolume <= 0;
          if (_masterGain && _masterGain.gain) {
            if (_audioCtx && _audioCtx.currentTime !== undefined) {
              try { _masterGain.gain.cancelScheduledValues(_audioCtx.currentTime); } catch (e) { }
            }
            try { _masterGain.gain.value = vm.meetingVolume / 100; } catch (e) { }
          }
        };
        vm.onMeetingVolumeChange = function () {
          vm.applyMeetingVolume();
          persistMeetingVolume();
          if (!vm.meetingMuted) audioCtx();
        };
        vm.toggleMeetingMute = function () {
          if (vm.meetingVolume > 0) {
            vm.meetingVolume = 0;
          } else {
            vm.meetingVolume = _prevMeetingVolume > 0 ? _prevMeetingVolume : 70;
          }
          vm.applyMeetingVolume();
          if (vm.meetingMuted) stopRageRumble();
          persistMeetingVolume(true);
          if (!vm.meetingMuted) audioCtx();
        };
        var _volumePersistTimer = null;
        function persistMeetingVolume(immediate) {
          if (_volumePersistTimer) $timeout.cancel(_volumePersistTimer);
          if (immediate) {
            _volumePersistTimer = null;
            saveMeetingVolume();
            return;
          }
          _volumePersistTimer = $timeout(function () {
            _volumePersistTimer = null;
            saveMeetingVolume();
          }, 300);
        }
        function saveMeetingVolume() {
          try { window.localStorage.setItem('weaver.meeting.volume', String(vm.meetingVolume)); } catch (e) { }
          if (vm.saveSettings) vm.saveSettings(true);
        }
        function logGossipEntry(who, text) {
          var clean = String(text || '').replace(/[\u{1F300}-\u{1FAFF}]/gu, '').trim();
          if (!clean) return;
          vm.gossipLog.push({ t: Date.now(), who: who, text: clean });
          if (vm.gossipLog.length > 500) vm.gossipLog.shift();
          saveChatLog();
          $scope.$applyAsync();
          try {
            $timeout(function () {
              var feed = document.getElementById('meetingGossipFeed');
              if (!feed) return;
              var nearBottom = feed.scrollHeight - feed.scrollTop - feed.clientHeight < 48;
              if (nearBottom) feed.scrollTop = feed.scrollHeight;
            }, 0);
          } catch (e) { }
        }
        function saveChatLog() {
          try { window.localStorage.setItem('weaver.meeting.chat', JSON.stringify(vm.gossipLog.slice(-500))); } catch (e) { }
        }
        vm.clearGossipLog = function () {
          vm.gossipLog = [];
          try { window.localStorage.removeItem('weaver.meeting.chat'); } catch (e) { }
        };
        function gossipMatches() {
          var q = vm.gossipSearch;
          var out = [];
          if (!q) return out;
          q = String(q).toLowerCase();
          for (var gi = 0; gi < vm.gossipLog.length; gi++) {
            var g = vm.gossipLog[gi];
            if ((g.who + ' ' + g.text).toLowerCase().indexOf(q) !== -1) out.push(g);
          }
          return out;
        }
        vm.gossipSearchHits = function () { return gossipMatches().length; };
        vm.gossipIsMatch = function (g) {
          var q = vm.gossipSearch;
          if (!q) return false;
          return (g.who + ' ' + g.text).toLowerCase().indexOf(String(q).toLowerCase()) !== -1;
        };
        vm.gossipIsActive = function (g) { return !!vm.gossipSearch && g === vm.gossipActiveLine; };
        function gossipScrollActive() {
          $timeout(function () {
            var feed = document.getElementById('meetingGossipFeed');
            if (!feed) return;
            var el = feed.querySelector('.meeting-gossip-line.gossip-match-active');
            if (el && el.scrollIntoView) el.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
          }, 30);
        }
        vm.gossipSearchChanged = function () {
          if (vm.gossipSearch) vm.gossipExpanded = true; 
          var m = gossipMatches();
          if (m.length) { vm.gossipSearchActive = 0; vm.gossipActiveLine = m[0]; }
          else { vm.gossipSearchActive = -1; vm.gossipActiveLine = null; }
          gossipScrollActive();
        };
        vm.gossipNextMatch = function () {
          var m = gossipMatches();
          if (!m.length) return;
          var ord = m.indexOf(vm.gossipActiveLine);
          var next = (ord + 1 + m.length) % m.length; 
          vm.gossipSearchActive = next;
          vm.gossipActiveLine = m[next];
          gossipScrollActive();
        };
        vm.gossipPrevMatch = function () {
          var m = gossipMatches();
          if (!m.length) return;
          var ord = m.indexOf(vm.gossipActiveLine);
          var prev = (ord - 1 + m.length) % m.length;
          vm.gossipSearchActive = prev;
          vm.gossipActiveLine = m[prev];
          gossipScrollActive();
        };
        vm.gossipSearchKey = function ($event) {
          if ($event.key === 'Enter') {
            $event.preventDefault();
            if ($event.shiftKey) vm.gossipPrevMatch(); else vm.gossipNextMatch();
          } else if ($event.key === 'Escape') {
            vm.gossipClearSearch();
          }
        };
        vm.gossipClearSearch = function () {
          vm.gossipSearch = '';
          vm.gossipSearchActive = -1;
          vm.gossipActiveLine = null;
        };
        function legacyGossipCopy(text) {
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
        vm.copyGossipLog = function () {
          var lines = [];
          for (var ci = 0; ci < vm.gossipLog.length; ci++) {
            var g = vm.gossipLog[ci];
            lines.push(vm.gossipTimeLabel(g.t) + ' ' + g.who + ': ' + g.text);
          }
          var text = lines.join('\n');
          if (!text) return;
          var done = function () {
            vm.gossipCopied = true;
            if (vm._gossipCopyTimer) $timeout.cancel(vm._gossipCopyTimer); 
            vm._gossipCopyTimer = $timeout(function () { vm.gossipCopied = false; }, 1200);
          };
          try {
            if (navigator.clipboard && navigator.clipboard.writeText) {
              navigator.clipboard.writeText(text).then(done, function () { legacyGossipCopy(text); done(); });
            } else { legacyGossipCopy(text); done(); }
          } catch (e) { legacyGossipCopy(text); done(); }
        };
        vm.gossipTimeLabel = function (ts) {
          var d = new Date(ts);
          return ('0' + d.getHours()).slice(-2) + ':' + ('0' + d.getMinutes()).slice(-2) + ':' + ('0' + d.getSeconds()).slice(-2);
        };
        function pushTicker(kind, label, flash) {
          var text = kind === 'good' ? '✓ ' + label : kind === 'bad' ? '✗ ' + label : label;
          var last = vm.meetingTicker[vm.meetingTicker.length - 1];
          if (last && last.label === text) return;
          if (kind === 'feud') {
            for (var i = vm.meetingTicker.length - 1; i >= 0; i--) {
              var it = vm.meetingTicker[i];
              if (it.kind === 'feud') {
                if (it.label === text) return;
                break;
              }
            }
          }
          vm.meetingTicker.push({ kind: kind, label: text, flash: !!flash });
          if (vm.meetingTicker.length > 12) vm.meetingTicker.shift();
          $scope.$applyAsync();
        }
        function tickerLabelForStep(st) {
          if (!st) return 'step';
          var type = String(st.type || 'step');
          var detail = st.path || st.command || st.description || '';
          if (detail) {
            detail = basename(String(detail).split('\n')[0]);
            if (detail.length > 50) detail = detail.slice(0, 50) + '…';
          }
          return detail ? type + ' ' + detail : type;
        }
        var CONFETTI_COLORS = ['#61afef', '#98c379', '#e5c07b', '#c678dd', '#e06c75', '#56b6c2', '#ffd866', '#f97583', '#7ee787', '#79c0ff'];
        function stompLand(s) {
          if (!scene || !s) return;
          var n = 14;
          for (var i = 0; i < n; i++) {
            var fromLeft = Math.random() < 0.5;
            scene.confetti.push({
              x: s.x, y: s.y,
              vx: (fromLeft ? -1 : 1) * (0.03 + Math.random() * 0.16),
              vy: -(0.05 + Math.random() * 0.22),
              rot: Math.random() * 6.28,
              vr: (Math.random() - 0.5) * 8,
              color: ['#ff3b30', '#ff5f52', '#c0392b', '#ffd0d0'][(Math.random() * 4) | 0],
              size: 0.008 + Math.random() * 0.011,
              life: 0,
              ttl: 0.55 + Math.random() * 0.5
            });
          }
          s.reactT = 1.0;
          s.reactKind = 'bad'; 
          bumpComplexityRage(5); 
          if (vm.showMeeting) playStomp();
          scene.lastLogAt = Date.now();
        }
        function spawnConfetti() {
          if (!scene) return;
          scene.confetti = [];
          var n = 110;
          for (var i = 0; i < n; i++) {
            var fromLeft = Math.random() < 0.5;
            scene.confetti.push({
              x: 0.45 + Math.random() * 0.2,
              y: 0.5 + Math.random() * 0.12,
              vx: (fromLeft ? -1 : 1) * (0.05 + Math.random() * 0.22),
              vy: -(0.08 + Math.random() * 0.28),
              rot: Math.random() * 6.28,
              vr: (Math.random() - 0.5) * 7,
              color: CONFETTI_COLORS[(Math.random() * CONFETTI_COLORS.length) | 0],
              size: 0.008 + Math.random() * 0.012,
              life: 0,
              ttl: 1.6 + Math.random() * 1.4
            });
          }
        }
        function spawnCrownConfetti(sp) {
          if (!scene || !sp) return;
          var n = 24;
          var golds = ['#ffd866', '#e5c07b', '#f8c471', '#f0b429', '#ffe08a', '#c678dd'];
          for (var i = 0; i < n; i++) {
            var fromLeft = Math.random() < 0.5;
            scene.confetti.push({
              x: sp.x + (Math.random() - 0.5) * 0.02,
              y: sp.y,
              vx: (fromLeft ? -1 : 1) * (0.02 + Math.random() * 0.14),
              vy: -(0.04 + Math.random() * 0.2),
              rot: Math.random() * 6.28,
              vr: (Math.random() - 0.5) * 8,
              color: golds[(Math.random() * golds.length) | 0],
              size: 0.008 + Math.random() * 0.012,
              life: 0,
              ttl: 1.3 + Math.random() * 1.1
            });
          }
          scene.lastLogAt = Date.now();
        }
        function collectUserStats() {
          var weekAgo = Date.now() - 7 * 86400000;
          function countThisWeek(list) {
            if (!list) return 0;
            var n = 0;
            for (var i = 0; i < list.length; i++) {
              var c = list[i];
              var stamp = c && (c.finishedAt || c.doneAt || c.createdAt);
              var t = stamp ? new Date(stamp).getTime() : 0;
              if (t && t >= weekAgo) n++;
            }
            return n;
          }
          var doneThisWeek = countThisWeek(vm.state && vm.state.done) + countThisWeek(vm.state && vm.state.archived);
          var scores = (vm.benchmarkScores && vm.benchmarkScores.length) ? vm.benchmarkScores : (vm.serverBenchmarks || []);
          var avgScore = 0, bestScore = 0, totalPoints = 0, n = 0;
          for (var i = 0; i < scores.length; i++) {
            var s = scores[i];
            var p = s && s.scorePercent;
            if (p !== null && p !== undefined && p !== '' && !isNaN(Number(p))) {
              avgScore += Number(p); n++;
              if (Number(p) > bestScore) bestScore = Number(p);
            }
            totalPoints += (s && s.points) || 0;
          }
          if (n > 0) avgScore = Math.round(avgScore / n);
          totalPoints = Math.round(totalPoints);
          return {
            tabs: vm.ide && vm.ide.openTabs ? vm.ide.openTabs.length : 0,
            done: vm.state && vm.state.done ? vm.state.done.length : 0,
            archived: vm.state && vm.state.archived ? vm.state.archived.length : 0,
            todo: vm.state && vm.state.todo ? vm.state.todo.length : 0,
            benchmarks: scores.length,
            projects: vm.projects ? vm.projects.length : 0,
            endpoints: vm.llamaEndpoints ? vm.llamaEndpoints.length : 0,
            streak: userStreak(),
            doneThisWeek: doneThisWeek,
            avgScore: avgScore,
            bestScore: bestScore,
            totalPoints: totalPoints
          };
        }
        function userStreak() {
          try {
            var key = 'weaver.meeting.streak';
            var today = new Date().toDateString();
            var yesterday = new Date(Date.now() - 86400000).toDateString();
            var raw = window.localStorage.getItem(key);
            var data = raw ? JSON.parse(raw) : null;
            var streak = 1;
            if (data && data.day === today) streak = data.n;
            else if (data && data.day === yesterday) streak = data.n + 1;
            window.localStorage.setItem(key, JSON.stringify({ day: today, n: streak }));
            return streak;
          } catch (e) { return 1; }
        }
        function userRankScore(st) {
          return (st.done || 0) + (st.benchmarks || 0) * 2 + (st.projects || 0) * 3 + (st.archived || 0) * 0.5
            + Math.round((st.bestScore || 0) / 10) + Math.min(20, (st.totalPoints || 0) / 50);
        }
        function userRankTitle(st) {
          for (var i = 0; i < RANK_TITLES.length; i++) {
            if (userRankScore(st) >= RANK_TITLES[i].min) return RANK_TITLES[i].title;
          }
          return 'Legend in Training';
        }
        function userRank(st) {
          var score = userRankScore(st);
          for (var i = 0; i < RANK_TITLES.length; i++) {
            if (score >= RANK_TITLES[i].min) return RANK_TITLES[i];
          }
          return RANK_TITLES[RANK_TITLES.length - 1];
        }
        function chevronArray(n) {
          var a = [];
          for (var i = 0; i < (n || 0); i++) a.push(i);
          return a;
        }
        function userRankProgress(st) {
          var score = userRankScore(st);
          var current = null, next = null;
          for (var i = 0; i < RANK_TITLES.length; i++) {
            if (score >= RANK_TITLES[i].min) { current = RANK_TITLES[i]; next = i > 0 ? RANK_TITLES[i - 1] : null; break; }
          }
          if (!current) { current = RANK_TITLES[RANK_TITLES.length - 1]; next = RANK_TITLES[RANK_TITLES.length - 2] || null; }
          if (!next) return { score: score, current: current, next: null, pct: 100, toNext: 0 };
          var pct = Math.max(0, Math.min(100, ((score - current.min) / (next.min - current.min)) * 100));
          return { score: score, current: current, next: next, pct: pct, toNext: Math.max(0, next.min - score) };
        }
        function userRankLadder(st) {
          var score = userRankScore(st);
          var i = 0;
          while (i < RANK_TITLES.length - 1 && score < RANK_TITLES[i].min) i++;
          var from = Math.max(0, i - 5);
          var rungs = [];
          for (var j = from; j <= i; j++) {
            var r = RANK_TITLES[j];
            var stepPts = 0, floorTitle = '', tickPct = 0;
            if (j < i) {
              var floorMin = RANK_TITLES[j + 1].min;
              floorTitle = RANK_TITLES[j + 1].title;
              stepPts = r.min - floorMin;
              var span = Math.max(1, stepPts);
              tickPct = Math.max(0, Math.min(100, ((score - floorMin) / span) * 100));
            }
            rungs.push({
              title: r.title,
              min: r.min,
              grade: r.grade,
              chevrons: r.chevrons,
              stars: r.stars,
              isCurrent: j === i,
              stepPts: stepPts,
              floorTitle: floorTitle,
              tickPct: tickPct,
              blurb: r.blurb
            });
          }
          return { rungs: rungs, above: i - from, score: score };
        }
        vm.userStats = collectUserStats;
        vm.userRankTitle = userRankTitle;
        vm.userRank = userRank;
        vm.userRankProgress = userRankProgress;
        vm.userRankLadder = userRankLadder;
        vm.chevronArray = chevronArray;
        vm.expandedRung = null;
        vm.toggleRung = function (rlRow) {
          vm.expandedRung = (vm.expandedRung === rlRow.min) ? null : rlRow.min;
        };
        vm.isRungExpanded = function (rlRow) {
          return vm.expandedRung === rlRow.min;
        };
        vm.rankShine = false; 
        function rankTierIndex(score) {
          for (var i = 0; i < RANK_TITLES.length; i++) {
            if (score >= RANK_TITLES[i].min) return i;
          }
          return RANK_TITLES.length - 1;
        }
        function checkRankPromotion() {
          if (_replay) return; 
          var st = collectUserStats();
          var score = userRankScore(st);
          var idx = rankTierIndex(score);
          if (idx >= _lastRankIdx) { _lastRankIdx = idx; return; } 
          _lastRankIdx = idx; 
          celebratePromotion(RANK_TITLES[idx]);
        }
        function celebratePromotion(rank) {
          vm.rankShine = true;
          $timeout(function () { vm.rankShine = false; }, 1600);
          if (vm.showMeeting) playPromotionSting();
          if (scene && !scene.done) {
            var p = spiderFor('planner') || randomSpider();
            if (p) {
              setSpeech(p, 'Promoted! ' + rank.title + '!', 4, '🧠 Planner — rank up');
              p.reactT = 1.2; p.reactKind = 'good';
            }
          }
          var fanfare = [
            ['🧠 Planner', 'RANK UP! The user just hit ' + rank.title + ' — ceremony at the whiteboard, everyone.'],
            ['🔍 Explorer', 'Hold on, let me verify this score... yes. Confirmed. We all witnessed it.'],
            ['✏️ Editor', 'I take full credit. Every line of that rank was typed by these hands.'],
            ['✅ Verifier', 'Approved. I watched it climb from the very first failed build.'],
            ['🏁 Reviewer', 'Verdict: promotion earned. Merging ' + rank.title + ' straight to production.'],
            ['💡 Ideas', 'New idea: they celebrate by running MORE tasks. Infinite loop of glory.']
          ];
          fanfare.forEach(function (ln, i) {
            $timeout(function () { logGossipEntry(ln[0], ln[1]); }, i * 850);
          });
        }
        function startRankPromotionWatch() {
          if (_rankPromoInterval) return;
          _lastRankIdx = rankTierIndex(userRankScore(collectUserStats()));
          _rankPromoInterval = $interval(checkRankPromotion, 2500);
        }
        function stopRankPromotionWatch() {
          if (_rankPromoInterval) { $interval.cancel(_rankPromoInterval); _rankPromoInterval = null; }
        }
        $scope.$watch(function () {
          return !!(vm.streamingActive || vm.benchmarkRunning || vm.benchmarkAllActive);
        }, function (busy) {
          if (busy) startRankPromotionWatch();
          else stopRankPromotionWatch();
        });
        var _ideasFetchedAt = 0;
        function collectCardSuggestions() {
          var ideas = [];
          if (!vm.state) return ideas;
          var cols = [vm.state.done, vm.state.archived, vm.state.selfImproving, vm.state.todo, vm.state.doing];
          cols.forEach(function (cards) {
            if (!cards || !cards.length) return;
            cards.forEach(function (card) {
              var sugs = card && card._suggestions;
              if (!Array.isArray(sugs) || !sugs.length) return;
              var topic = (card.text || 'a completed task').trim();
              sugs.forEach(function (s) {
                if (!s || !s.description) return;
                ideas.push({
                  topic: topic,
                  desc: s.description,
                  complete: false,
                  date: s.createdAt || '',
                  card: card,
                  files: s.files || []
                });
              });
            });
          });
          return ideas;
        }
        function applyIdeas(ideas) {
          var fresh = (ideas || []).filter(function (i) { return !i.complete; });
          fresh.sort(function (x, y) { return (y.date || '').localeCompare(x.date || ''); });
          vm.meetingIdeas = fresh.slice(0, 6);
          $scope.$applyAsync();
        }
        function refreshMeetingIdeas() {
          var cardIdeas = collectCardSuggestions();
          if (cardIdeas.length) { applyIdeas(cardIdeas); return; }
          var now = Date.now();
          if (now - _ideasFetchedAt < 60000) return;
          _ideasFetchedAt = now;
          var proj = vm.selectedProject;
          if (!proj) return;
          $http.get('/api/improvementdata', { params: { project: proj } }).then(function (resp) {
            var data = resp.data;
            if (typeof data === 'string') { try { data = JSON.parse(data); } catch (e) { return; } }
            var feats = (data && data.features) || [];
            var ideas = [];
            feats.forEach(function (f) {
              var imps = (f && f.improvements) || [];
              if (!imps.length) return;
              var last = imps[imps.length - 1];
              if (!last || !last.description) return;
              ideas.push({
                topic: f.feature || 'a new task',
                desc: last.description || '',
                complete: !!last.complete,
                date: last.date || ''
              });
            });
            applyIdeas(ideas);
          }).catch(function () { });
        }
        var IDEA_LINES = [
          "Fresh from the backend: '{topic}' is now a ticket. I basically invented it.",
          "Hey, the system just spawned an idea — '{topic}'. Filed under: brilliant.",
          "New suggestion dropped from the backend: '{topic}'. You're welcome.",
          "The backend birthed a ticket about '{topic}'. I've already got notes.",
          "Idea alert from the pipeline: '{topic}'. I saw it coming. I always see it coming."
        ];
        var IDEA_CARD_LINES = [
          "That completed task, '{topic}'? The model left follow-ups. I'm already on it.",
          "Fresh from the cards: '{topic}' just earned improvement suggestions.",
          "The AI reviewed '{topic}' and stamped suggestions on it. Bold move.",
          "Suggestions dropped for '{topic}' — scoped to the work we just finished.",
          "'{topic}' is done, but its suggestion section says otherwise. I collect them."
        ];
        var IDEA_REACTIONS = [
          "ANOTHER ticket?! From thin air?!",
          "The backend just THINKS of work now?!",
          "Self-spawning tickets?! Terrifying. I love it.",
          "Where does it get these ideas?! ...Oh. From the user's code."
        ];
        var IDEA_CARD_REACTIONS = [
          "Suggestions FROM the model?! On MY cards?!",
          "It finished the card AND planned the follow-up. Overachiever.",
          "A suggestion section on the card?! I knew it had more to give.",
          "The AI peer-reviewed its own work and wants MORE. Ambitious."
        ];
        function startGossip() {
          if (!scene || scene.gossip) return;
          refreshMeetingIdeas();
          var freshIdeas = (vm.meetingIdeas || []).filter(function (i) { return !i.complete; });
          var ideasSpider = spiderFor('ideas');
          var bragger = randomSpider();
          if (!bragger) return;
          if (freshIdeas.length && ideasSpider && bragger === ideasSpider) {
            var notIdeas = scene.spiders.filter(function (s) { return s !== ideasSpider; });
            if (notIdeas.length) bragger = notIdeas[Math.floor(Math.random() * notIdeas.length)];
          }
          if (!bragger) return;
          var others = scene.spiders.filter(function (s) { return s !== bragger; });
          var a = others[Math.floor(Math.random() * others.length)];
          var rest = others.filter(function (s) { return s !== a; });
          var b = rest[Math.floor(Math.random() * rest.length)];
          var listeners = [a, b];
          if (freshIdeas.length && ideasSpider && ideasSpider !== bragger && ideasSpider !== a && ideasSpider !== b) {
            listeners.push(ideasSpider); 
          }
          bragger.state = 'walk';
          bragger.target = { x: COOLER.x, y: COOLER.y };
          bragger.speech = ''; bragger.speechTtl = 0;
          listeners.forEach(function (l, i) {
            l.state = 'walk';
            l.target = { x: COOLER_SPOTS[i].x, y: COOLER_SPOTS[i].y };
            l.speech = ''; l.speechTtl = 0;
          });
          var st = collectUserStats();
          var rank = userRankTitle(st);
          var ctx = currentContext();
          var chosen = [];
          var runPool = [];
          var hadRun = !!(vm.currentRun || (vm.streamingSteps && vm.streamingSteps.length > 0));
          if (hadRun && ctx.done > 0) runPool.push(GOSSIP_RUN[0]);
          if (hadRun && ctx.file) runPool.push(GOSSIP_RUN[1]);
          if (hadRun && ctx.endpoint && ctx.endpoint !== 'Default') runPool.push(GOSSIP_RUN[2]);
          while (chosen.length < 2 && runPool.length) {
            chosen.push(runPool.splice(Math.floor(Math.random() * runPool.length), 1)[0]);
          }
          var available = GOSSIP_STATS.filter(function (g) { return (st[g.key] || 0) > 0; });
          if (available.length < 2) available = GOSSIP_STATS;
          var pool = available.slice();
          var want = Math.min(4, pool.length);
          for (var i = 0; i < want && chosen.length < 4; i++) {
            var idx = Math.floor(Math.random() * pool.length);
            chosen.push(pool.splice(idx, 1)[0]);
          }
          var lines = [];
          function fmt(tpl, n, file, endpoint) {
            return tpl
              .replace(/\{n\}/g, n)
              .replace(/\{file\}/g, file || 'a file')
              .replace(/\{endpoint\}/g, endpoint || 'the endpoint');
          }
          function fmtIdea(tpl, topic) {
            return tpl.replace(/\{topic\}/g, topic);
          }
          var season = currentSeason();
          var sOpen = GOSSIP_OPENERS, sReact = GOSSIP_REACTIONS, sEnd = GOSSIP_ENDINGS, sRoast = null;
          if (season && SEASON_GOSSIP[season]) {
            var sg = SEASON_GOSSIP[season];
            sOpen = sg.openers; sReact = sg.reactions; sEnd = sg.endings;
            if (sg.roasts && sg.roasts.length) sRoast = pick(sg.roasts);
          }
          lines.push({ spider: bragger, text: pick(sOpen), ttl: 2.8 });
          if (ideasSpider && freshIdeas.length && listeners.indexOf(ideasSpider) !== -1) {
            var idea = freshIdeas[0];
            var topic = (idea.topic || 'a new task').trim();
            if (topic.length > 48) topic = topic.slice(0, 45) + '…';
            var ideaText = fmtIdea(pick(idea.card ? IDEA_CARD_LINES : IDEA_LINES), topic);
            if (idea.desc) {
              var snippet = idea.desc.trim();
              if (snippet.length > 60) snippet = snippet.slice(0, 57) + '…';
              ideaText = ideaText + ' (' + snippet + ')';
            }
            if (idea.card && idea.files && idea.files.length) {
              var fileList = idea.files.slice(0, 2).join(', ');
              ideaText = ideaText + ' — touching ' + fileList;
            }
            lines.push({ spider: ideasSpider, text: ideaText, ttl: 3.6 });
            lines.push({ spider: listeners[0], text: pick(idea.card ? IDEA_CARD_REACTIONS : IDEA_REACTIONS), ttl: 2.2 });
          }
          if (scene.verdictOutcome && !scene.verdictGossiped) {
            var reviewerSp = spiderFor('reviewer');
            var complexitySp = spiderFor('complexity');
            var coolerGroup = [bragger].concat(listeners);
            var tellers = coolerGroup.filter(function (s) { return s !== reviewerSp && s !== complexitySp && s !== ideasSpider; });
            if (tellers.length) {
              scene.verdictGossiped = true;
              var teller = pick(tellers);
              var vPool = scene.verdictOutcome === 'right' ? GOSSIP_VERDICT_RIGHT
                : scene.verdictOutcome === 'fail' ? GOSSIP_VERDICT_FAIL
                : GOSSIP_VERDICT_WRONG;
              var reactors = coolerGroup.filter(function (s) { return s !== teller; });
              lines.push({ spider: teller, text: pick(vPool), ttl: 3.8 });
              if (reactors.length) {
                lines.push({ spider: pick(reactors), text: pick(GOSSIP_VERDICT_REACTIONS), ttl: 2.4 });
              }
            }
          }
          chosen.forEach(function (g, i) {
            var text, wow;
            if (g.key === 'steps') {
              text = fmt(g.line, ctx.done, ctx.file, ctx.endpoint);
              wow = fmt(g.wow, ctx.done, ctx.file, ctx.endpoint);
            } else if (g.key === 'file') {
              text = fmt(g.line, 0, ctx.file, ctx.endpoint);
              wow = fmt(g.wow, 0, ctx.file, ctx.endpoint);
            } else if (g.key === 'endpoint') {
              text = fmt(g.line, 0, ctx.file, ctx.endpoint);
              wow = fmt(g.wow, 0, ctx.file, ctx.endpoint);
            } else {
              var n = st[g.key] || 0;
              text = fmt(g.line, n);
              wow = g.wow ? fmt(g.wow, n) : pick(sReact);
            }
            lines.push({ spider: bragger, text: text, ttl: 3.2 });
            var listener = listeners[i % 2];
            lines.push({ spider: listener, text: (i === 0 && g.wow) ? wow : pick(sReact), ttl: 2.2 });
          });
          if (verdictRecordTotal() >= 2) {
            var cpxSpider = spiderFor('complexity');
            if (cpxSpider && (cpxSpider === bragger || listeners.indexOf(cpxSpider) !== -1)) {
              lines.push({ spider: cpxSpider, text: fmt(pick(VERDICT_BRAG), 0).replace(/\{record\}/g, verdictRecordLabel()), ttl: 3.4 });
              var cpxReact = listeners[0];
              if (cpxReact === cpxSpider) cpxReact = listeners[1] || listeners[0];
              lines.push({ spider: cpxReact, text: pick(VERDICT_BRAG_REACTIONS), ttl: 2.4 });
            }
          }
          if (sRoast) {
            lines.push({ spider: bragger, text: sRoast, ttl: 3.4 });
            lines.push({ spider: listeners[1], text: pick(sReact), ttl: 2.4 });
          }
          lines.push({ spider: bragger, text: 'And their rank? ' + rank + '. ' + pick(sEnd), ttl: 3.4 });
          lines.push({ spider: listeners[0], text: 'We are NOT worthy.', ttl: 2.4 });
          scene.gossip = { phase: 'walk', ttl: 2.4, bragger: bragger, listeners: listeners, lines: lines, li: 0, lineTtl: 0 };
          var seasonLabel = (season && SEASON_NAMES[season]) ? ' — ' + SEASON_NAMES[season] + ' gossip' : ' — water cooler gossip';
          vm.meetingSpeaker = bragger.icon + ' ' + bragger.name + seasonLabel;
          $scope.$applyAsync();
        }
        function advanceGossip(dt) {
          if (!scene || !scene.gossip) return;
          var g = scene.gossip;
          if (g.phase === 'walk') {
            g.ttl -= dt;
            if (g.ttl <= 0) { g.phase = 'talk'; g.li = 0; g.lineTtl = 0; }
            return;
          }
          if (g.phase === 'talk') {
            g.lineTtl -= dt;
            if (g.lineTtl > 0) return;
            if (g.li < g.lines.length) {
              var ln = g.lines[g.li];
              setSpeech(ln.spider, ln.text, ln.ttl, ln.spider.icon + ' ' + ln.spider.name);
              logGossipEntry(ln.spider.icon + ' ' + ln.spider.name, ln.text);
              g.lineTtl = ln.ttl;
              g.li++;
            } else {
              g.phase = 'leave';
            }
            return;
          }
          var targets = [g.bragger].concat(g.listeners);
          targets.forEach(function (s) {
            var t = scene.meetingOn ? s.seat : s.home;
            s.state = 'walk';
            s.target = { x: t.x, y: t.y };
            s.speech = ''; s.speechTtl = 0;
          });
          scene.gossip = null;
          scene.gossipCd = 45 + Math.random() * 35;
          vm.meetingSpeaker = '🕷️ the water cooler chatter dies down';
          $scope.$applyAsync();
        }
        function endGossipNow() {
          if (!scene || !scene.gossip) return;
          var g = scene.gossip;
          var targetGetter = scene.meetingOn
            ? function (s) { return s.seat; }
            : function (s) { return s.home; };
          [g.bragger].concat(g.listeners).forEach(function (s) {
            if (scene.writer === s) return; 
            s.state = 'walk';
            s.target = { x: targetGetter(s).x, y: targetGetter(s).y };
            s.speech = ''; s.speechTtl = 0;
          });
          scene.gossip = null;
          scene.gossipCd = 30 + Math.random() * 20;
        }
        var WATCHING_LINES = [
          "They're watching! Act natural!",
          "Oh — hi! We were just… working. Yes. Working.",
          "Hello, boss! Don't mind us, just absolutely nailing this task.",
          "You're looking well today. The office says hi.",
          "Welcome back! The board missed you. The board never misses anyone.",
          "We promise we were NOT gossiping about you. …Much.",
          "So THIS is the famous user. The legend is real.",
          "Don't be shy — we perform better with an audience.",
          "The user! Everyone wave! This is the user!",
          "We were just discussing… productivity. Yes. Very productive talk."
        ];
        var WATCHING_WAVES = [
          "👋", "🖐️", "🙋", "🙆", "☝️"
        ];
        function startWatching() {
          if (!scene || scene.watching || _replay || vm.streamingActive) return;
          if (scene.writer || scene.queue.length) return; 
          if (scene.editorMeltdown || scene.shoutMatch) return;
          if (scene.gossip) endGossipNow(); 
          if (scene.coolerTrip) endCoolerTripNow(); 
          var star = randomSpider();
          if (!star) return;
          scene.spiders.forEach(function (s) {
            if (s === scene.writer) return;
            s.waveT = 2.2 + Math.random() * 1.2;
            s.wavePhase = Math.random() * 6.28;
            if (s.state === 'idle') {
              s.speech = pick(WATCHING_WAVES);
              s.speechTtl = 1.5 + Math.random() * 1;
            }
          });
          star.state = 'walk';
          star.target = { x: 0.5, y: 0.92 };
          star.speech = ''; star.speechTtl = 0;
          var pool2 = WATCHING_LINES.slice();
          var lines = [];
          for (var k = 0; k < 3 && pool2.length; k++) {
            var li2 = Math.floor(Math.random() * pool2.length);
            lines.push(pool2.splice(li2, 1)[0]);
          }
          scene.watching = {
            phase: 'walk', ttl: 2.2, star: star,
            lines: lines,
            li: 0, lineTtl: 0
          };
          vm.meetingSpeaker = star.icon + ' ' + star.name + ' — noticed the user watching';
          $scope.$applyAsync();
        }
        function advanceWatching(dt) {
          if (!scene || !scene.watching) return;
          var w = scene.watching;
          scene.spiders.forEach(function (s) {
            if (s !== scene.writer && s.state !== 'walk' && s.waveT > 0) {
              s.waveT = Math.max(s.waveT, 1);
            }
          });
          if (w.phase === 'walk') {
            w.ttl -= dt;
            if (w.ttl <= 0) { w.phase = 'talk'; w.li = 0; w.lineTtl = 0; }
            return;
          }
          if (w.phase === 'talk') {
            w.lineTtl -= dt;
            if (w.lineTtl > 0) return;
            if (w.li < w.lines.length) {
              var line = w.lines[w.li];
              setSpeech(w.star, line, 3.4, w.star.icon + ' ' + w.star.name + ' — talking to the user');
              logGossipEntry(w.star.icon + ' ' + w.star.name, line);
              w.lineTtl = 3.4;
              w.li++;
            } else {
              w.phase = 'leave';
            }
            return;
          }
          var t = scene.meetingOn ? w.star.seat : w.star.home;
          w.star.state = 'walk';
          w.star.target = { x: t.x, y: t.y };
          w.star.speech = ''; w.star.speechTtl = 0;
          scene.watching = null;
          scene.watchingCd = 25 + Math.random() * 20;
          vm.meetingSpeaker = '🕷️ the spiders wave goodbye to the user';
          $scope.$applyAsync();
        }
        function endWatchingNow() {
          if (!scene || !scene.watching) return;
          var w = scene.watching;
          var t = scene.meetingOn ? w.star.seat : w.star.home;
          if (scene.writer !== w.star) {
            w.star.state = 'walk';
            w.star.target = { x: t.x, y: t.y };
          }
          w.star.speech = ''; w.star.speechTtl = 0;
          scene.spiders.forEach(function (s) {
            if (s !== scene.writer && /^(👋|🖐️|🙋|🙆|☝️)/.test(s.speech || '')) {
              s.speech = ''; s.speechTtl = 0;
            }
          });
          scene.watching = null;
          scene.watchingCd = 15;
          $scope.$applyAsync();
        }
        function startStandoff() {
          if (!scene || scene.standoff || scene.glare || scene.editorMeltdown || scene.shoutMatch || _replay) return; 
          var verifier = spiderFor('verifier');
          var complexity = spiderFor('complexity');
          if (!verifier || !complexity) return;
          if (scene.writer || scene.queue.length || vm.streamingActive) return; 
          verifier.state = 'walk';
          verifier.target = { x: STANDOFF_SPOT.x, y: STANDOFF_SPOT.y };
          verifier.speech = ''; verifier.speechTtl = 0;
          complexity.state = 'idle';
          complexity.speech = ''; complexity.speechTtl = 0;
          var vals = complexityVals();
          vals.actual = complexityActualSteps();
          scene.standoff = {
            phase: 'walk', ttl: 2.2,
            verifier: verifier, complexity: complexity,
            lines: [
              { spider: verifier, text: fmtComplexity(pick(VERIFIER_REBUTTAL), vals), ttl: 3.6 },
              { spider: complexity, text: pick(COMPLEXITY_SULK), ttl: 3.2 },
              { spider: verifier, text: "The code. Is. The evidence. Goodbye.", ttl: 2.8 }
            ],
            li: 0, lineTtl: 0
          };
          vm.meetingSpeaker = verifier.icon + ' ' + verifier.name + ' — staring down the Complexity spider';
          $scope.$applyAsync();
        }
        function advanceStandoff(dt) {
          if (!scene || !scene.standoff) return;
          var st = scene.standoff;
          if (st.phase === 'walk') {
            st.ttl -= dt;
            if (st.ttl <= 0) { st.phase = 'talk'; st.li = 0; st.lineTtl = 0; }
            return;
          }
          if (st.phase === 'talk') {
            st.lineTtl -= dt;
            if (st.lineTtl > 0) return;
            if (st.li < st.lines.length) {
              var ln = st.lines[st.li];
              setSpeech(ln.spider, ln.text, ln.ttl, ln.spider.icon + ' ' + ln.spider.name);
              logGossipEntry(ln.spider.icon + ' ' + ln.spider.name, ln.text);
              st.lineTtl = ln.ttl;
              st.li++;
            } else {
              st.phase = 'leave';
            }
            return;
          }
          var vTarget = scene.meetingOn ? st.verifier.seat : st.verifier.home;
          st.verifier.state = 'walk';
          st.verifier.target = { x: vTarget.x, y: vTarget.y };
          st.verifier.speech = ''; st.verifier.speechTtl = 0;
          st.complexity.state = 'walk';
          st.complexity.target = { x: st.complexity.home.x, y: st.complexity.home.y };
          st.complexity.speech = ''; st.complexity.speechTtl = 0;
          scene.standoff = null;
          scene.standoffCd = 45 + Math.random() * 25;
          vm.meetingSpeaker = '🕷️ the standoff breaks up — the Complexity spider sulks home';
          $scope.$applyAsync();
        }
        function endStandoffNow() {
          if (!scene || !scene.standoff) return;
          var st = scene.standoff;
          var vTarget = scene.meetingOn ? st.verifier.seat : st.verifier.home;
          st.verifier.state = 'walk';
          st.verifier.target = { x: vTarget.x, y: vTarget.y };
          st.complexity.state = 'walk';
          st.complexity.target = { x: st.complexity.home.x, y: st.complexity.home.y };
          st.verifier.speech = ''; st.verifier.speechTtl = 0;
          st.complexity.speech = ''; st.complexity.speechTtl = 0;
          scene.standoff = null;
          scene.standoffCd = 15;
        }
        function startGlare(revLine) {
          if (!scene || scene.glare || _replay || !vm.showMeeting) return false;
          if (scene.standoff || scene.coolerTrip || scene.gossip || scene.watching || scene.editorMeltdown || scene.shoutMatch) return false;
          if (scene.writer || scene.queue.length || vm.streamingActive) return false;
          var reviewer = spiderFor('reviewer');
          var complexity = spiderFor('complexity');
          if (!reviewer || !complexity) return false;
          reviewer.state = 'walk';
          reviewer.target = { x: reviewer.home.x, y: reviewer.home.y };
          reviewer.speech = ''; reviewer.speechTtl = 0;
          complexity.state = 'walk';
          complexity.target = { x: complexity.home.x, y: complexity.home.y };
          complexity.speech = ''; complexity.speechTtl = 0;
          var vals = complexityVals();
          vals.actual = complexityActualSteps();
          scene.glare = {
            phase: 'walk', ttl: 1.0, beatTtl: 1.3,
            reviewer: reviewer, complexity: complexity,
            lines: [
              { spider: reviewer, text: revLine, ttl: 4.0 },   
              { spider: complexity, text: pick(COMPLEXITY_GLARE), ttl: 3.2 },
              { spider: reviewer, text: fmtComplexity(pick(REVIEWER_GLARE_LAST), vals), ttl: 3.0 }
            ],
            li: 0, lineTtl: 0
          };
          vm.meetingSpeaker = reviewer.icon + ' ' + reviewer.name + ' — locking eyes with the Complexity spider';
          $scope.$applyAsync();
          return true;
        }
        function advanceGlare(dt) {
          if (!scene || !scene.glare) return;
          var g = scene.glare;
          if (g.phase === 'walk') {
            g.ttl -= dt;
            if (g.ttl <= 0) {
              g.phase = 'glare';
              g.reviewer.glaringAt = g.complexity;
              g.complexity.glaringAt = g.reviewer;
              g.reviewer.reactT = 0.9; g.reviewer.reactKind = 'bad';
            }
            return;
          }
          if (g.phase === 'glare') {
            g.beatTtl -= dt;
            if (g.beatTtl <= 0) { g.phase = 'talk'; g.li = 0; g.lineTtl = 0; }
            return;
          }
          if (g.phase === 'talk') {
            g.lineTtl -= dt;
            if (g.lineTtl > 0) return;
            if (g.li < g.lines.length) {
              var ln = g.lines[g.li];
              setSpeech(ln.spider, ln.text, ln.ttl, ln.spider.icon + ' ' + ln.spider.name + (g.li === 0 ? ' — told you so' : ''));
              logGossipEntry(ln.spider.icon + ' ' + ln.spider.name, ln.text);
              g.lineTtl = ln.ttl;
              g.li++;
            } else {
              g.phase = 'leave';
            }
            return;
          }
          g.reviewer.glaringAt = null;
          g.complexity.glaringAt = null;
          g.reviewer.speech = ''; g.reviewer.speechTtl = 0;
          g.complexity.speech = ''; g.complexity.speechTtl = 0;
          g.complexity.stomping = true;
          g.complexity.state = 'walk';
          g.complexity.target = { x: g.complexity.home.x, y: g.complexity.home.y };
          scene.glare = null;
          vm.meetingSpeaker = '🤯 the Complexity spider stomps back to its desk — the glare is over';
          $scope.$applyAsync();
        }
        function endGlareNow() {
          if (!scene || !scene.glare) return;
          var g = scene.glare;
          g.reviewer.glaringAt = null;
          g.complexity.glaringAt = null;
          g.reviewer.speech = ''; g.reviewer.speechTtl = 0;
          g.complexity.speech = ''; g.complexity.speechTtl = 0;
          scene.glare = null;
        }
        function buildShoutLines() {
          var lines = [];
          for (var si = 0; si < 3; si++) {
            lines.push({ speakerRole: 'editor', text: pick(SHOUT_EDITOR), ttl: 2.6 });
            lines.push({ speakerRole: 'verifier', text: pick(SHOUT_VERIFIER), ttl: 2.6 });
          }
          lines.push({ speakerRole: 'editor', text: pick(SHOUT_EDITOR_LAST), ttl: 3.0 });
          lines.push({ speakerRole: 'verifier', text: pick(SHOUT_VERIFIER_LAST), ttl: 3.0 });
          return lines;
        }
        function buildShoutCover() {
          var cover = [];
          var others = scene.spiders.filter(function (s) { return s.role !== 'editor' && s.role !== 'verifier'; });
          var n = Math.min(2, others.length);
          for (var ci = 0; ci < n; ci++) {
            var idx = Math.floor(Math.random() * others.length);
            var sp = others.splice(idx, 1)[0];
            cover.push({ speakerRole: sp.role, text: pick(SHOUT_COVER), ttl: 2.2 });
          }
          return cover;
        }
        function maybeStartShoutMatch() {
          if (!scene || _replay || !vm.showMeeting) return;
          if (scene.shoutMatch || scene.shoutFired) return; 
          if (scene.editorMeltdown || scene.standoff || scene.glare || scene.coolerTrip || scene.gossip || scene.watching || scene.verifierVictory || scene.gripeSession) return;
          startShoutMatch();
        }
        function startShoutMatch(rec) {
          if (!scene || scene.shoutMatch) return;
          if (!rec && _replay) return; 
          var editor = spiderFor('editor');
          var verifier = spiderFor('verifier');
          if (!editor || !verifier) return;
          if (!rec) {
            if (scene.writer || scene.queue.length || vm.streamingActive) return; 
            if (scene.gossip || scene.watching || scene.standoff || scene.coolerTrip || scene.glare) return;
          }
          var lines, cover, refLines = null;
          var refPending = rec ? !!rec.ref : Math.random() < 0.3;
          if (rec) {
            lines = (rec.lines || []).map(function (l) { return { speakerRole: l.speakerRole, text: l.text, ttl: l.ttl }; });
            cover = (rec.cover || []).map(function (l) { return { speakerRole: l.speakerRole, text: l.text, ttl: l.ttl }; });
            refLines = rec.refLines || null;
          } else {
            lines = buildShoutLines();
            cover = buildShoutCover();
            if (refPending) refLines = { ref: pick(SHOUT_REF), edLater: pick(SHOUT_EDITOR_LATER), vfLater: pick(SHOUT_VERIFIER_LATER) };
            recordEvent({
              type: 'shout',
              ref: refPending,
              refLines: refLines,
              lines: lines.map(function (l) { return { speakerRole: l.speakerRole, text: l.text, ttl: l.ttl }; }),
              cover: cover.map(function (l) { return { speakerRole: l.speakerRole, text: l.text, ttl: l.ttl }; })
            });
          }
          scene.shoutFired = true;
          editor.stomping = true;
          editor.state = 'walk';
          editor.target = { x: SHOUT_SPOT_EDITOR.x, y: SHOUT_SPOT_EDITOR.y };
          editor.speech = ''; editor.speechTtl = 0;
          verifier.state = 'walk';
          verifier.target = { x: SHOUT_SPOT_VERIFIER.x, y: SHOUT_SPOT_VERIFIER.y };
          verifier.speech = ''; verifier.speechTtl = 0;
          scene.shoutMatch = {
            phase: 'walk', ttl: 2.6,   
            editor: editor, verifier: verifier,
            lines: lines, cover: cover,
            refPending: refPending, refAt: 4, 
            refLines: refLines, 
            li: 0, lineTtl: 0
          };
          if (!_replay) {
            vm.meetingSpeaker = editor.icon + ' ' + editor.name + ' vs ' + verifier.icon + ' ' + verifier.name + ' — SHOUTING MATCH!';
            $scope.$applyAsync();
          }
        }
        function advanceShoutMatch(dt) {
          if (!scene || !scene.shoutMatch) return;
          var sm = scene.shoutMatch;
          var editor = sm.editor;
          var verifier = sm.verifier;
          if (sm.phase === 'walk') {
            sm.ttl -= dt;
            if (sm.ttl <= 0) {
              sm.phase = 'shout';
              sm.li = 0; sm.lineTtl = 0;
              playClashChord();
              scene.spiders.forEach(function (s) {
                if (s !== editor && s !== verifier) { s.reactT = 1.1; s.reactKind = 'bad'; }
              });
              sm.cover.forEach(function (cl) {
                var cSp = spiderFor(cl.speakerRole);
                if (cSp) setSpeech(cSp, cl.text, cl.ttl, cSp.icon + ' ' + cSp.name + ' — taking cover');
              });
            }
            return;
          }
          if (sm.phase === 'shout') {
            sm.lineTtl -= dt;
            if (sm.lineTtl > 0) return;
            if (sm.refPending && sm.li >= sm.refAt) {
              var refPl = spiderFor('planner');
              sm.ref = {
                spot: { x: 0.54, y: 0.86 }, 
                planner: refPl,
                done: false, ttl: 0
              };
              if (refPl) {
                refPl.state = 'walk';
                refPl.target = { x: sm.ref.spot.x, y: sm.ref.spot.y };
                refPl.speech = ''; refPl.speechTtl = 0;
              }
              sm.phase = 'refWalk';
              return;
            }
            if (sm.li < sm.lines.length) {
              var ln = sm.lines[sm.li];
              var spk = ln.speakerRole === 'editor' ? editor : verifier;
              setSpeech(spk, ln.text, ln.ttl, spk.icon + ' ' + spk.name + ' — SHOUTING');
              playShoutBark(ln.speakerRole);
              if (!_replay) {
                logGossipEntry(spk.icon + ' ' + spk.name, ln.text);
                recordEvent({ type: 'chat', who: spk.icon + ' ' + spk.name, text: ln.text });
              }
              sm.lineTtl = ln.ttl;
              sm.li++;
            } else {
              sm.phase = 'leave';
            }
            return;
          }
          if (sm.phase === 'refWalk') {
            var ref = sm.ref;
            if (ref && !ref.done) {
              var rp = ref.planner;
              if (!rp || (Math.abs(rp.x - ref.spot.x) < 0.012 && Math.abs(rp.y - ref.spot.y) < 0.012)) {
                ref.done = true;
                ref.ttl = 3.8; 
                playWhistle();
                var refText = sm.refLines ? sm.refLines.ref : pick(SHOUT_REF);
                var edLater = sm.refLines ? sm.refLines.edLater : pick(SHOUT_EDITOR_LATER);
                var vfLater = sm.refLines ? sm.refLines.vfLater : pick(SHOUT_VERIFIER_LATER);
                if (rp) setSpeech(rp, refText, 2.6, rp.icon + ' ' + rp.name + ' — THE REFEREE');
                setSpeech(editor, edLater, 2.8, editor.icon + ' ' + editor.name + ' — walking it off');
                setSpeech(verifier, vfLater, 2.8, verifier.icon + ' ' + verifier.name + ' — walking it off');
                if (!_replay) {
                  if (rp) { logGossipEntry(rp.icon + ' ' + rp.name, refText); recordEvent({ type: 'chat', who: rp.icon + ' ' + rp.name, text: refText }); }
                  logGossipEntry(editor.icon + ' ' + editor.name, edLater); recordEvent({ type: 'chat', who: editor.icon + ' ' + editor.name, text: edLater });
                  logGossipEntry(verifier.icon + ' ' + verifier.name, vfLater); recordEvent({ type: 'chat', who: verifier.icon + ' ' + verifier.name, text: vfLater });
                }
              }
            }
            if (ref && ref.done) {
              ref.ttl -= dt;
              if (ref.ttl <= 0) sm.phase = 'refLeave';
            }
            return;
          }
          if (sm.phase === 'refLeave') {
            var edHome = scene.meetingOn ? editor.seat : editor.home;
            var vfHome = scene.meetingOn ? verifier.seat : verifier.home;
            editor.stomping = false;
            editor.state = 'walk';
            editor.target = { x: edHome.x, y: edHome.y };
            editor.speech = ''; editor.speechTtl = 0;
            verifier.stomping = false;
            verifier.state = 'walk';
            verifier.target = { x: vfHome.x, y: vfHome.y };
            verifier.speech = ''; verifier.speechTtl = 0;
            var rp2 = sm.ref.planner;
            if (rp2) {
              var plHome = scene.meetingOn ? rp2.seat : rp2.home;
              rp2.state = 'walk';
              rp2.target = { x: plHome.x, y: plHome.y };
            }
            scene.shoutMatch = null;
            if (!_replay) {
              vm.meetingSpeaker = '🧠 the Planner referee blows the whistle — the feud walks it off';
              $scope.$applyAsync();
            }
            return;
          }
          var edTarget = scene.meetingOn ? editor.seat : editor.home;
          var vfTarget = scene.meetingOn ? verifier.seat : verifier.home;
          editor.stomping = true;
          editor.state = 'walk';
          editor.target = { x: edTarget.x, y: edTarget.y };
          editor.speech = ''; editor.speechTtl = 0;
          verifier.state = 'walk';
          verifier.target = { x: vfTarget.x, y: vfTarget.y };
          verifier.speech = ''; verifier.speechTtl = 0;
          scene.shoutMatch = null;
          if (!_replay) {
            vm.meetingSpeaker = '💢 the shouting match breaks up — the office slowly comes out of cover';
            $scope.$applyAsync();
          }
        }
        function endShoutMatchNow() {
          if (!scene || !scene.shoutMatch) return;
          var sm = scene.shoutMatch;
          var edTarget = scene.meetingOn ? sm.editor.seat : sm.editor.home;
          var vfTarget = scene.meetingOn ? sm.verifier.seat : sm.verifier.home;
          sm.editor.state = 'walk';
          sm.editor.target = { x: edTarget.x, y: edTarget.y };
          sm.verifier.state = 'walk';
          sm.verifier.target = { x: vfTarget.x, y: vfTarget.y };
          sm.editor.speech = ''; sm.editor.speechTtl = 0;
          sm.verifier.speech = ''; sm.verifier.speechTtl = 0;
          if (sm.ref && sm.ref.planner) {
            var rpEnd = sm.ref.planner;
            var plEnd = scene.meetingOn ? rpEnd.seat : rpEnd.home;
            rpEnd.state = 'walk';
            rpEnd.target = { x: plEnd.x, y: plEnd.y };
          }
          scene.shoutMatch = null;
        }
        function pickWitnessSpider(fromX, fromY) {
          if (!scene) return null;
          var others = scene.spiders.filter(function (s) { return s.role !== 'complexity'; });
          if (!others.length) return null;
          var cx = (fromX !== undefined) ? fromX : COOLER.x;
          var cy = (fromY !== undefined) ? fromY : COOLER.y;
          var sorted = others.slice().sort(function (a, b) {
            var da = Math.abs(a.x - cx) + Math.abs(a.y - cy);
            var db = Math.abs(b.x - cx) + Math.abs(b.y - cy);
            return da - db;
          });
          var pool = sorted.slice(0, 3);
          return pick(pool);
        }
        function startCoolerTrip(rec) {
          if (!scene || scene.coolerTrip) return;
          if (!rec && _replay) return; 
          if (!vm.showMeeting) return;
          var sp = spiderFor('complexity');
          if (!sp) return;
          if (!rec) {
            if (scene.writer || scene.queue.length || vm.streamingActive) return; 
            if (scene.gossip || scene.watching || scene.standoff || scene.shoutMatch) return;
          }
          scene.lastLogAt = Date.now(); 
          sp.state = 'walk';
          sp.target = { x: COOLER.x, y: COOLER.y };
          sp.speech = ''; sp.speechTtl = 0;
          var witness, lines, witnessText;
          if (rec) {
            witness = rec.witnessRole ? spiderFor(rec.witnessRole) : null;
            lines = (rec.lines || []).map(function (l) { return { spider: sp, text: l.text, ttl: l.ttl }; });
            witnessText = rec.witnessText || pick(DOUBLE_TAKE);
          } else {
            var vals = complexityVals();
            witness = pickWitnessSpider(sp.home.x, sp.home.y);
            witnessText = pick(DOUBLE_TAKE);
            lines = [
              { spider: sp, text: fmtComplexity(pick(COOLER_GRIPE), vals), ttl: 3.6 },
              { spider: sp, text: pick(COOLER_SIP), ttl: 2.8 },
              { spider: sp, text: pick(COOLER_STALK_BACK), ttl: 2.8 }
            ];
            recordEvent({
              type: 'cooler',
              lines: lines.map(function (l) { return { text: l.text, ttl: l.ttl }; }),
              witnessRole: witness ? witness.role : null,
              witnessText: witnessText
            });
          }
          scene.coolerTrip = {
            phase: 'walk', ttl: 1.4,
            spider: sp,
            witness: witness,           
            witnessFired: false,
            witnessText: witnessText,   
            lines: lines,
            li: 0, lineTtl: 0,
            drink: 0 
          };
          vm.meetingSpeaker = sp.icon + ' ' + sp.name + ' — storming off to the water cooler';
          $scope.$applyAsync();
        }
        function advanceCoolerTrip(dt) {
          if (!scene || !scene.coolerTrip) return;
          var ct = scene.coolerTrip;
          if (ct.phase === 'walk') {
            ct.ttl -= dt;
            if (!ct.witnessFired && ct.witness && ct.ttl < 0.7) {
              ct.witnessFired = true;
              var wText = ct.witnessText || pick(DOUBLE_TAKE);
              setSpeech(ct.witness, '😳 ' + wText, 3.4, ct.witness.icon + ' ' + ct.witness.name + ' — double-take');
              if (!_replay) logGossipEntry(ct.witness.icon + ' ' + ct.witness.name, wText);
              ct.witness.reactT = 1.1;
              ct.witness.reactKind = 'bad'; 
              scene.lastLogAt = Date.now();
            }
            if (ct.ttl <= 0) { ct.phase = 'talk'; ct.li = 0; ct.lineTtl = 0; }
            return;
          }
          if (ct.phase === 'talk') {
            if (ct.li === 2) {
              ct.drink = Math.min(1, ct.drink + dt / 0.9);
            } else if (ct.li === 3) {
              ct.drink = Math.max(0, ct.drink - dt / 2.8);
            }
            ct.lineTtl -= dt;
            if (ct.lineTtl > 0) return;
            if (ct.li < ct.lines.length) {
              var ln = ct.lines[ct.li];
              setSpeech(ln.spider, ln.text, ln.ttl, ln.spider.icon + ' ' + ln.spider.name);
              if (!_replay) logGossipEntry(ln.spider.icon + ' ' + ln.spider.name, ln.text);
              ct.lineTtl = ln.ttl;
              ct.li++;
            } else {
              ct.phase = 'leave';
            }
            return;
          }
          var ctHome = scene.meetingOn ? ct.spider.seat : ct.spider.home;
          ct.spider.state = 'walk';
          ct.spider.target = { x: ctHome.x, y: ctHome.y };
          ct.spider.speech = ''; ct.spider.speechTtl = 0;
          scene.coolerTrip = null;
          scene.coolerTripCd = 75 + Math.random() * 40;
          if (!_replay) {
            bumpComplexityRage(-10);
            vm.meetingSpeaker = '🕷️ the Complexity spider stalks back to its desk — rage −10, hydration +1';
          } else {
            vm.meetingSpeaker = '🕷️ the Complexity spider stalks back to its desk (replay)';
          }
          $scope.$applyAsync();
        }
        function endCoolerTripNow() {
          if (!scene || !scene.coolerTrip) return;
          var ct = scene.coolerTrip;
          var ctHome = scene.meetingOn ? ct.spider.seat : ct.spider.home;
          ct.spider.state = 'walk';
          ct.spider.target = { x: ctHome.x, y: ctHome.y };
          ct.spider.speech = ''; ct.spider.speechTtl = 0;
          scene.coolerTrip = null;
          scene.coolerTripCd = 30;
        }
        function maybeStartEditorMeltdown() {
          if (!scene || _replay || !vm.showMeeting) return;
          if (scene.editorMeltdown || scene.meltdownFired) return; 
          if (scene.writer || scene.queue.length || vm.streamingActive) return;
          if (scene.gossip || scene.watching || scene.standoff || scene.coolerTrip || scene.glare || scene.shoutMatch) return;
          startEditorMeltdown();
        }
        function meltdownBlameLine() {
          if (Math.random() < 1 / 12) return pick(MELTDOWN_SELF);
          return pick(MELTDOWN_BLAME);
        }
        function buildMeltdownLines() {
          function otherSpider() {
            var others = scene.spiders.filter(function (s) { return s.role !== 'editor'; });
            return others.length ? pick(others) : null;
          }
          var shock = otherSpider();
          var retort = otherSpider();
          return [
            { speakerRole: 'editor', text: meltdownBlameLine(), ttl: 3.4 },
            { speakerRole: shock ? shock.role : 'verifier', text: pick(MELTDOWN_REACTIONS), ttl: 3.0 },
            { speakerRole: 'editor', text: meltdownBlameLine(), ttl: 3.4 },
            { speakerRole: retort ? retort.role : 'verifier', text: pick(MELTDOWN_RETORTS), ttl: 3.0 },
            { speakerRole: 'editor', text: meltdownBlameLine(), ttl: 3.4 },
            { speakerRole: 'editor', text: pick(MELTDOWN_SELF), ttl: 3.6 }
          ];
        }
        function buildAftermathLines() {
          function pickLine(roleKey, pool) {
            var s = spiderFor(roleKey);
            return s ? { speakerRole: roleKey, text: pick(pool), ttl: 3.2 } : null;
          }
          var lines = [];
          var planner = pickLine('planner', AFTERMATH_PLANNER);
          var verifier = pickLine('verifier', AFTERMATH_VERIFIER);
          if (planner) lines.push(planner);
          if (verifier) lines.push(verifier);
          var extras = [];
          [
            ['commander', AFTERMATH_COMMANDER],
            ['explorer', AFTERMATH_EXPLORER],
            ['complexity', AFTERMATH_COMPLEXITY],
            ['reviewer', AFTERMATH_REVIEWER],
            ['itspecialist', AFTERMATH_ITS],
            ['ideas', AFTERMATH_IDEAS]
          ].forEach(function (pair) {
            var l = pickLine(pair[0], pair[1]);
            if (l) extras.push(l);
          });
          while (extras.length && lines.length < 4) {
            var i = Math.floor(Math.random() * extras.length);
            lines.push(extras.splice(i, 1)[0]);
          }
          var edLine = pickLine('editor', AFTERMATH_EDITOR);
          if (edLine) lines.push(edLine);
          return lines;
        }
        function startEditorMeltdown(rec) {
          if (!scene || scene.editorMeltdown) return;
          if (!rec && _replay) return; 
          if (!vm.showMeeting) return;
          var sp = spiderFor('editor');
          if (!sp) return;
          if (!rec) {
            if (scene.writer || scene.queue.length || vm.streamingActive) return; 
            if (scene.gossip || scene.watching || scene.standoff || scene.coolerTrip || scene.glare || scene.shoutMatch) return;
          }
          if (scene.verifierVictory) {
            var _lapSp = spiderFor('verifier');
            if (_lapSp) {
              _lapSp.state = 'walk';
              _lapSp.target = { x: _lapSp.seat.x, y: _lapSp.seat.y };
              _lapSp.speech = ''; _lapSp.speechTtl = 0;
            }
          }
          scene.verifierVictory = null;
          scene.gripeSession = null;
          endShoutMatchNow();
          scene.lastLogAt = Date.now(); 
          sp.stomping = true;           
          sp.state = 'walk';
          sp.target = { x: BOARD_STAND.x, y: BOARD_STAND.y };
          sp.speech = ''; sp.speechTtl = 0;
          var scribble, lines, aftermath;
          if (rec) {
            scribble = rec.scribble || pick(MELTDOWN_SCRIBBLE);
            lines = (rec.lines || []).map(function (l) { return { speakerRole: l.speakerRole, text: l.text, ttl: l.ttl }; });
            aftermath = (rec.aftermath || []).map(function (l) { return { speakerRole: l.speakerRole, text: l.text, ttl: l.ttl }; });
          } else {
            scribble = pick(MELTDOWN_SCRIBBLE);
            lines = buildMeltdownLines();
            aftermath = buildAftermathLines();
            recordEvent({
              type: 'meltdown',
              scribble: scribble,
              lines: lines.map(function (l) { return { speakerRole: l.speakerRole, text: l.text, ttl: l.ttl }; }),
              aftermath: aftermath.map(function (l) { return { speakerRole: l.speakerRole, text: l.text, ttl: l.ttl }; })
            });
          }
          scene.editorMeltdown = {
            phase: 'stomp',   
            spider: sp,
            scribble: scribble,
            scrLine: null,    
            scrProg: 0,
            lines: lines,
            aftermath: aftermath, 
            li: 0, lineTtl: 0
          };
          vm.meetingSpeaker = sp.icon + ' ' + sp.name + ' — MELTDOWN: stomping to the board';
          $scope.$applyAsync();
        }
        function advanceEditorMeltdown(dt) {
          if (!scene || !scene.editorMeltdown) return;
          var m = scene.editorMeltdown;
          var sp = m.spider;
          if (m.phase === 'stomp') {
            if (Math.abs(sp.x - BOARD_STAND.x) < 0.012 && Math.abs(sp.y - BOARD_STAND.y) < 0.012) {
              m.phase = 'scribble';
              m.scrProg = 0;
              m.scrLine = { role: 'editor', color: '#ff4d4d', text: '', progress: 0, done: false };
              scene.boardLines.push(m.scrLine);
              if (scene.boardLines.length > 8) scene.boardLines.shift();
              startScribbleLoop();
            }
            return;
          }
          if (m.phase === 'scribble') {
            updateScribbleLoop(); 
            var lastLen = m.scrLine ? m.scrLine.text.length : 0;
            m.scrProg += dt * 26; 
            var shown = Math.min(m.scribble.length, Math.floor(m.scrProg));
            m.scrLine.text = m.scribble.slice(0, shown);
            if (shown > lastLen && shown % 2 === 0) playTick();
            if (m.scrProg >= m.scribble.length) {
              m.scrLine.text = m.scribble;
              m.scrLine.done = true;
              m.phase = 'tirade';
              m.li = 0; m.lineTtl = 0;
              stopScribbleLoop(); 
            }
            return;
          }
          if (m.phase === 'tirade' || m.phase === 'aftermath') {
            m.lineTtl -= dt;
            if (m.lineTtl > 0) return;
            if (m.li < m.lines.length) {
              var ln = m.lines[m.li];
              var speaker = ln.speakerRole === 'editor' ? sp : spiderFor(ln.speakerRole);
              if (!speaker) speaker = sp;
              var context = m.phase === 'aftermath' ? 'emergency meeting' : 'meltdown fallout';
              setSpeech(speaker, ln.text, ln.ttl, speaker.icon + ' ' + speaker.name + ' — ' + context);
              if (!_replay) {
                logGossipEntry(speaker.icon + ' ' + speaker.name, ln.text);
                recordEvent({ type: 'chat', who: speaker.icon + ' ' + speaker.name, text: ln.text });
              }
              if (speaker !== sp) {
                speaker.reactT = 1.1;
                speaker.reactKind = (m.phase === 'aftermath' && speaker.role === 'verifier') ? 'good' : 'bad';
              }
              m.lineTtl = ln.ttl;
              m.li++;
            } else {
              if (m.phase === 'tirade') {
                m.phase = 'stompback';
                var mHome = scene.meetingOn ? sp.seat : sp.home;
                sp.stomping = true; 
                sp.state = 'walk';
                sp.target = { x: mHome.x, y: mHome.y };
                sp.speech = ''; sp.speechTtl = 0;
              } else {
                scene.editorMeltdown = null;
                scene.meltdownFired = true;
                pumpQueue();
                if (!_replay) {
                  vm.meetingSpeaker = '🏢 the emergency meeting adjourns — the scribble is never mentioned again';
                }
                $scope.$applyAsync();
              }
            }
            return;
          }
          var h = scene.meetingOn ? sp.seat : sp.home;
          if (Math.abs(sp.x - h.x) < 0.012 && Math.abs(sp.y - h.y) < 0.012) {
            m.phase = 'aftermath';
            m.lines = m.aftermath || [];
            m.li = 0; m.lineTtl = 1.2; 
            playRecordScratch();
            if (!_replay) {
              bumpEditRage(-15); 
              vm.meetingSpeaker = '✏️ the Editor stomps back to its desk — the office calls an emergency meeting';
            } else {
              vm.meetingSpeaker = '✏️ the Editor stomps back to its desk (replay)';
            }
            $scope.$applyAsync();
          }
        }
        function endEditorMeltdownNow() {
          if (!scene || !scene.editorMeltdown) return;
          stopScribbleLoop(); 
          var m = scene.editorMeltdown;
          var mHome = scene.meetingOn ? m.spider.seat : m.spider.home;
          m.spider.state = 'walk';
          m.spider.target = { x: mHome.x, y: mHome.y };
          m.spider.speech = ''; m.spider.speechTtl = 0;
          scene.editorMeltdown = null;
        }
        vm.openMeeting = function () {
          vm.showMeeting = true; vm.saveSettings(true);
          vm.meetingVolumeDim = false; 
          if (vm._restoreMeetingReplayFromCards) vm._restoreMeetingReplayFromCards();
          if (vm._dodgeFloatingPanel) vm._dodgeFloatingPanel(vm.meeting, { selfCls: 'meeting-floating-panel', margin: 10 });
          audioCtx();
          startLoop();
        };
        vm.closeMeeting = function () {
          vm.showMeeting = false;
          vm.meetingFlowOpen = false; 
          removeFlowCloser();
          vm.saveSettings(true);
          stopLoop();
        };
        vm.setMeetingHovered = function (on) {
          vm.meetingHovered = !!on;
          vm.meetingHoverSince = on ? Date.now() : null;
          if (on && scene) startWatching();
          else if (!on && scene) endWatchingNow();
          $scope.$applyAsync();
        };
        vm.startMeetingDrag = function (event) {
          event.preventDefault();
          vm.meeting.dragging = true;
          vm.meeting.dragStartX = event.clientX - vm.meeting.left;
          vm.meeting.dragStartY = event.clientY - vm.meeting.top;
          var onMove = function (e) {
            if (!vm.meeting.dragging) return;
            vm.meeting.left = e.clientX - vm.meeting.dragStartX;
            vm.meeting.top = e.clientY - vm.meeting.dragStartY;
            if (vm._clampFloatingPanel) vm._clampFloatingPanel(vm.meeting);
            else { vm.meeting.left = Math.max(0, vm.meeting.left); vm.meeting.top = Math.max(0, vm.meeting.top); }
            $scope.$apply();
          };
          var onUp = function () {
            vm.meeting.dragging = false;
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
            if (vm.saveSettings) vm.saveSettings(true); 
          };
          document.addEventListener('mousemove', onMove);
          document.addEventListener('mouseup', onUp);
        };
        vm.startMeetingResize = function (dir, event) {
          event.preventDefault();
          event.stopPropagation();
          vm.meeting.resizing = true;
          vm.meeting.resizeDir = dir;
          vm.meeting.resizeStartX = event.clientX;
          vm.meeting.resizeStartY = event.clientY;
          vm.meeting.resizeStartW = vm.meeting.width;
          vm.meeting.resizeStartH = vm.meeting.height;
          var onMove = function (e) {
            if (!vm.meeting.resizing) return;
            var dx = e.clientX - vm.meeting.resizeStartX;
            var dy = e.clientY - vm.meeting.resizeStartY;
            if (vm.meeting.resizeDir.indexOf('e') >= 0) vm.meeting.width = Math.max(360, vm.meeting.resizeStartW + dx);
            if (vm.meeting.resizeDir.indexOf('s') >= 0) vm.meeting.height = Math.max(260, vm.meeting.resizeStartH + dy);
            $scope.$apply();
          };
          var onUp = function () {
            vm.meeting.resizing = false;
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
            if (vm.saveSettings) vm.saveSettings(true); 
          };
          document.addEventListener('mousemove', onMove);
          document.addEventListener('mouseup', onUp);
        };
        vm.resetMeeting = function () {
          scene = makeScene();
          vm.meetingSpeaker = '🕷️ the spiders are back at their desks';
          if (canvas && ctx) drawFrame();
        };
        vm.meetingFlowOpen = false;
        var _flowCloser = null;
        function removeFlowCloser() {
          if (_flowCloser) { document.removeEventListener('click', _flowCloser); _flowCloser = null; }
        }
        vm.toggleMeetingFlowMenu = function ($event) {
          if ($event) $event.stopPropagation();
          vm.meetingFlowOpen = !vm.meetingFlowOpen;
          if (vm.meetingFlowOpen) {
            _flowCloser = function (e) {
              if (!vm.meetingFlowOpen) { removeFlowCloser(); return; }
              if (e.target && e.target.closest && e.target.closest('.meeting-flow-menu, .meeting-flow-toggle')) return;
              vm.meetingFlowOpen = false;
              removeFlowCloser();
              $scope.$applyAsync();
            };
            document.addEventListener('click', _flowCloser);
          } else {
            removeFlowCloser();
          }
        };
        vm.meetingFlowClose = function () {
          vm.meetingFlowOpen = false;
          removeFlowCloser();
        };
        vm.meetingFlowCurrentCard = function () {
          return vm.findCardById ? vm.findCardById(vm.activeCardId) : null;
        };
        vm.meetingFlowNextCard = function () {
          if (!vm.state || !vm.state.todo) return null;
          var ready = vm.state.todo.filter(function (c) {
            return c.filePath === vm.selectedProject && c.ready && !c.selfImproving;
          });
          return ready[0] || null;
        };
        vm.meetingFlowStopCard = function () {
          var card = vm.meetingFlowCurrentCard();
          if (vm.stopAgent) vm.stopAgent(card || null);
          vm.meetingFlowClose();
          if (vm.showNotification) vm.showNotification(card ? '⏹ Agent stopped — card stays in Doing with its analysis' : '⏹ No active card to stop');
        };
        vm.meetingFlowRestartCard = function () {
          var card = vm.meetingFlowCurrentCard();
          if (!card) { vm.meetingFlowClose(); if (vm.showNotification) vm.showNotification('No current card to restart'); return; }
          if (vm.stopAgent) vm.stopAgent(card);
          if (vm.clearPlan) vm.clearPlan(card);
          if (vm.executeAgent) vm.executeAgent(card);
          vm.meetingFlowClose();
          if (vm.showNotification) vm.showNotification('♻ Plan cleared — restarting the card fresh');
        };
        vm.meetingFlowStartNext = function (freshPlan) {
          var cur = vm.meetingFlowCurrentCard();
          if (cur && vm.state && vm.moveCard) {
            var col = (vm.state.doing || []).some(function (c) { return c.id === cur.id; }) ? 'doing' : null;
            if (col) vm.moveCard(cur.id, col, 'todo');
          }
          var next = vm.meetingFlowNextCard();
          if (!next) { vm.meetingFlowClose(); if (vm.showNotification) vm.showNotification('No Ready card in To Do — mark one Ready first'); return; }
          if (freshPlan && vm.clearPlan) vm.clearPlan(next);
          if (vm.moveCardToDoing) vm.moveCardToDoing(next.id);
          if (vm.executeAgent) vm.executeAgent(next);
          vm.meetingFlowClose();
          if (vm.showNotification) vm.showNotification((freshPlan ? '▶ Next card started (fresh plan): ' : '▶ Next card started (plan kept): ') + (next.text || '').slice(0, 50));
        };
        vm.cycleMeetingReplaySpeed = function () {
          var speeds = [1, 1.5, 2];
          var i = speeds.indexOf(vm.meetingReplaySpeed);
          if (i < 0) i = speeds.length - 1;
          vm.meetingReplaySpeed = speeds[(i + 1) % speeds.length];
          $scope.$applyAsync();
        };
        vm._restoreMeetingReplayFromCards = function () {
          if (!vm.state) return;
          if (_recording || _replay) return; 
          if (vm.meetingReplay && vm.meetingReplay.length) return; 
          var cols = ['done', 'doing', 'selfImproving', 'todo'];
          for (var c = 0; c < cols.length; c++) {
            var cards = vm.state[cols[c]] || [];
            for (var i = cards.length - 1; i >= 0; i--) {
              var rc = cards[i];
              if (!rc || !Array.isArray(rc._meetingReplay) || !rc._meetingReplay.length) continue;
              if (vm.selectedProject && rc.filePath && rc.filePath !== vm.selectedProject) continue;
              vm.meetingReplay = rc._meetingReplay;
              return;
            }
          }
        };
        function boundedReplayForStorage(events) {
          if (!events) return events;
          var out = [];
          var start = Math.max(0, events.length - 2000);
          for (var i = start; i < events.length; i++) {
            var ev = events[i];
            if (ev && ev.type === 'log' && ev.entry && typeof ev.entry.detail === 'string' && ev.entry.detail.length > 200) {
              out.push({ t: ev.t, type: 'log', entry: { ts: ev.entry.ts, level: ev.entry.level, message: ev.entry.message, detail: ev.entry.detail.slice(0, 200) + '…' } });
            } else {
              out.push(ev);
            }
          }
          return out;
        }
        vm.replayMeeting = function () {
          if (vm.meetingReplaying) { stopReplay(); return; }
          if (!vm.meetingReplay || !vm.meetingReplay.length) return;
          if (vm.streamingActive) return; 
          vm.meetingReplaySpeed = 1; 
          scene = makeScene();
          scene.meetingOn = true;
          scene.boardLines = [];
          scene.queue = [];
          scene.writer = null;
          scene.spiders.forEach(function (s) {
            s.state = 'walk';
            s.target = { x: s.seat.x, y: s.seat.y };
            s.speechTtl = 0; s.text = ''; s.progress = 0; s.reactT = 0; s.stomping = false;
          });
          _replay = {
            events: vm.meetingReplay,
            t0: vm.meetingReplay[0].t,
            elapsed: 0,
            idx: 0,
            total: vm.meetingReplay[vm.meetingReplay.length - 1].t - vm.meetingReplay[0].t
          };
          var _rvReset = spiderFor('verifier');
          if (_rvReset) _rvReset.crowned = false; 
          vm.meetingReplaying = true;
          vm.meetingSpeaker = '▶ replaying the last run…';
          startLoop();
          $scope.$applyAsync();
        };
        function stopReplay() {
          _replay = null;
          vm.meetingReplaying = false;
          updateScrubber();
          if (scene) {
            vm.meetingSpeaker = '⏹ replay stopped — the spiders are heading home';
            scene.spiders.forEach(function (s) {
              if (scene.writer !== s) { s.state = 'walk'; s.target = { x: s.home.x, y: s.home.y }; }
            });
          }
          $scope.$applyAsync();
        }
        function fmtDuration(ms) {
          if (ms < 0 || isNaN(ms)) ms = 0;
          var s = Math.floor(ms / 1000);
          var m = Math.floor(s / 60);
          s = s % 60;
          return m + ':' + (s < 10 ? '0' : '') + s;
        }
        var _scrubFill = null;
        var _scrubLabel = null;
        function updateScrubber() {
          if (!_scrubFill || !_scrubLabel) {
            _scrubFill = document.getElementById('meetingScrubberFill');
            _scrubLabel = document.getElementById('meetingScrubberTime');
          }
          if (!_scrubFill || !_scrubLabel) return;
          var pct = 0, cur = 0, total = 0;
          if (_replay && _replay.total > 0) {
            cur = _replay.elapsed;
            total = _replay.total;
            pct = Math.max(0, Math.min(1, cur / total));
          }
          _scrubFill.style.width = (pct * 100) + '%';
          _scrubLabel.textContent = fmtDuration(cur) + ' / ' + fmtDuration(total);
        }
        vm.seekReplay = function ($event) {
          if (vm.streamingActive) return; 
          if (!vm.meetingReplay || !vm.meetingReplay.length) return;
          if (!_replay) {
            vm.replayMeeting();
            if (!_replay) return;
          }
          var rect = $event.currentTarget.getBoundingClientRect();
          if (!rect.width) return;
          var frac = Math.max(0, Math.min(1, ($event.clientX - rect.left) / rect.width));
          var target = frac * _replay.total;
          scene = makeScene();
          scene.meetingOn = true;
          scene.done = false;
          scene.boardLines = [];
          scene.queue = [];
          scene.writer = null;
          scene.confetti = []; 
          scene.activeRole = 'planner';
          scene.lastLogAt = Date.now();
          scene.spiders.forEach(function (s) {
            s.state = 'walk';
            s.target = { x: s.seat.x, y: s.seat.y };
            s.speechTtl = 0; s.text = ''; s.progress = 0; s.reactT = 0;
          });
          vm.meetingEditorRage = 0;
          vm.meetingVerifierSmug = 0;
          vm.meetingFeudRatio = 50;
          vm.meetingFeudLeader = 'tie';
          resetRivalryGauges();
          vm.meetingMood = [];
          vm.meetingMoodActive = false;
          _moodSig = '';
          var evs = _replay.events;
          var idx = 0;
          for (; idx < evs.length; idx++) {
            var ev = evs[idx];
            if (ev.t - _replay.t0 > target) break;
            if (ev.type === 'start') {
            } else if (ev.type === 'log') {
              var parsed = logBoardText(ev.entry);
              if (parsed) {
                var sp = spiderFor(parsed.role);
                scene.boardLines.push({ role: parsed.role, color: sp ? sp.color : '#888', text: parsed.text, progress: parsed.text.length });
                if (scene.boardLines.length > 8) scene.boardLines.shift();
              }
            } else if (ev.type === 'finish') {
              scene.done = true;
              var verdictText = '✅ Plan looks good — task complete!';
              scene.boardLines.push({ role: 'reviewer', color: '#e06c75', text: verdictText, progress: verdictText.length });
              if (scene.boardLines.length > 8) scene.boardLines.shift();
            }
          }
          _replay.elapsed = target;
          _replay.idx = idx;
          vm.meetingSpeaker = '⏩ seeking — ' + fmtDuration(target) + ' / ' + fmtDuration(_replay.total);
          updateScrubber();
          $scope.$applyAsync();
        };
        function stripPrefix(msg) {
          return (msg || '')
            .replace(/^[\s▶✕✗✓⏳💡📋🔍🧠⏭⚡📄❓💬🔨📊🛠✏️✅🏁\u{1F300}-\u{1FAFF}\u{2600}-\u{27BF}\u{2190}-\u{2BFF}]+/u, '')
            .trim();
        }
        function roleForEntry(level, message) {
          var m = (message || '').toLowerCase();
          if (level === 'phase') {
            if (/plan/.test(m)) return 'planner';
            if (/explor/.test(m)) return 'explorer';
            if (/verif|review|check/.test(m)) return 'verifier';
            if (/command|terminal|build/.test(m)) return 'commander';
            if (/execut|edit|appl|generat|code/.test(m)) return 'editor';
            return 'planner';
          }
          if (level === 'step') {
            if (/explore/.test(m)) return 'explorer';
            if (/command/.test(m)) return 'commander';
            if (/plan/.test(m)) return 'planner';
            return 'editor'; 
          }
          if (level === 'think') return /verif|verification/.test(m) ? 'verifier' : 'planner';
          if (level === 'summary') return 'verifier';
          if (level === 'log') return 'reviewer';
          if (level === 'error') return 'reviewer';
          if (level === 'warn') return /reject/.test(m) ? 'planner' : 'reviewer';
          if (level === 'recovering') return 'editor';
          if (/meta-plan/.test(m)) return 'planner';
          if (/complexity|compacted|compact(ed|ing|s)? |context (size|accum|budget)|accumulated (reasoning|diffs?|context)|thinking context|context summarized|token cap|atomic step/.test(m)) {
            if (/context review/.test(m)) return 'explorer'; 
            return 'complexity';
          }
          if (/proposing|plan/.test(m)) return 'planner';
          if (/exploring|context review/.test(m)) return 'explorer';
          if (/cohesion/.test(m)) return 'verifier';
          if (/running on endpoint|endpoint/.test(m)) return 'commander';
          if (/agent started/.test(m)) return 'planner';
          return null;
        }
        function logBoardText(entry) {
          var msg = entry && entry.message ? String(entry.message) : '';
          var level = entry && entry.level ? String(entry.level) : 'info';
          var role = roleForEntry(level, msg);
          if (!role) return null;
          var text = stripPrefix(msg);
          if (!text) return null;
          if (entry && entry.detail) {
            var d = entry.detail;
            var deep = typeof d === 'string' ? d : (d.text || d.summary || d.question || '');
            if (deep && (level === 'think' || level === 'summary' || level === 'phase')) {
              var trimmed = String(deep).trim();
              if (trimmed.length > 2) text = trimmed;
            }
          }
          if (text.length > 180) text = text.slice(0, 180) + '…';
          return { role: role, text: text };
        }
        function fmtComplexity(tpl, vals) {
          return tpl
            .replace(/\{score\}/g, vals.score)
            .replace(/\{label\}/g, vals.label)
            .replace(/\{cap\}/g, vals.cap)
            .replace(/\{ctx\}/g, vals.ctx)
            .replace(/\{est\}/g, vals.est)
            .replace(/\{actual\}/g, vals.actual)
            .replace(/\{record\}/g, vals.record);
        }
        function complexityVals() {
          return {
            score: (typeof vm.complexityScore === 'number' && vm.complexityScore) ? vm.complexityScore : '?',
            label: vm.complexityLabel || 'some',
            cap: (typeof vm.complexityTokenCap === 'number' && vm.complexityTokenCap) ? vm.complexityTokenCap : 'some',
            ctx: vm.streamingContextSize ? vm.streamingContextSize.toLocaleString() : '0',
            est: (typeof vm.complexityAtomicSteps === 'number' && vm.complexityAtomicSteps) ? vm.complexityAtomicSteps : '?',
            actual: 0,
            record: verdictRecordLabel()
          };
        }
        function complexityActualSteps() {
          var n = 0;
          var steps = vm.streamingSteps || [];
          for (var i = 0; i < steps.length; i++) {
            var s = steps[i];
            if (s && /done|applied|created|ok/.test(s.status || '')) n++;
          }
          if (n === 0 && vm.planItems && vm.planItems.length) {
            for (var j = 0; j < vm.planItems.length; j++) {
              if (vm.planItems[j] && vm.planItems[j].done) n++;
            }
          }
          return n;
        }
        var RAGE_COOL_PER_SEC = 4 / 60; 
        var RAGE_COOL_IDLE_MS = 20000;  
        function bumpComplexityRage(amount) {
          if (!scene) return;
          var sp = spiderFor('complexity');
          if (!sp) return;
          var before = sp.rage || 0;
          sp.rage = Math.max(0, Math.min(100, before + amount));
          sp.rageAt = Date.now();
          sp.rageDrainedAt = Date.now(); 
          if (before < 100 && sp.rage >= 100 && !_replay && vm.showMeeting) {
            playSteamVent();
          }
          if (!_replay && vm.showMeeting) {
            var bucket = Math.floor(sp.rage / 10) * 10;
            var prevBucket = Math.floor(before / 10) * 10;
            if (bucket >= 10 && bucket < 100 && bucket > prevBucket) {
              playRageCreak(bucket);
            }
          }
          if (before < 75 && sp.rage >= 75) maybeStartCoolerTrip();
        }
        function bumpEditRage(amount) {
          if (!scene || _replay) return;
          var sp = spiderFor('editor');
          if (!sp) return;
          var before = sp.rage || 0;
          sp.rage = Math.max(0, Math.min(100, before + amount));
          sp.rageAt = Date.now();
          sp.rageDrainedAt = Date.now(); 
          if (before < 100 && sp.rage >= 100 && vm.showMeeting) {
            playSteamVent();
          }
          if (before < 100 && sp.rage >= 100) maybeStartEditorMeltdown();
          sp.stomping = sp.rage >= 60;
          syncMeetingMeters();
        }
        function resetEditRage() {
          if (!scene) return;
          var sp = spiderFor('editor');
          if (sp) { sp.rage = 0; sp.rageAt = Date.now(); sp.rageDrainedAt = Date.now(); sp.stomping = false; }
          syncMeetingMeters();
        }
        function coolEditRage() {
          if (!scene || _replay) return;
          var sp = spiderFor('editor');
          if (!sp || !sp.rage) return;
          var now = Date.now();
          var idleMs = now - (sp.rageAt || now);
          if (idleMs <= RAGE_COOL_IDLE_MS) {
            sp.rageDrainedAt = now; 
            return;
          }
          var sinceDrain = now - (sp.rageDrainedAt || now);
          var hoverFactor = 1;
          if (vm.meetingHovered && vm.meetingHoverSince) {
            hoverFactor = 1 + Math.min(4, (now - vm.meetingHoverSince) / 5000);
          }
          var spiteFactor = 1;
          var vSp = spiderFor('verifier');
          if (vSp && vSp.crowned) spiteFactor = 2;
          sp.rage = Math.max(0, sp.rage - RAGE_COOL_PER_SEC * hoverFactor * spiteFactor * (sinceDrain / 1000));
          sp.rageDrainedAt = now;
          if (sp.stomping && sp.rage < 50) sp.stomping = false;
          syncMeetingMeters();
        }
        function bumpVerifierSmug(amount) {
          if (!scene || _replay) return;
          var sp = spiderFor('verifier');
          if (!sp) return;
          var before = sp.smug || 0;
          sp.smug = Math.max(0, Math.min(100, before + amount));
          sp.smugAt = Date.now();
          sp.smugDrainedAt = Date.now(); 
          if (before < 100 && sp.smug >= 100) maybeStartVerifierVictory();
          syncMeetingMeters();
        }
        function resetVerifierSmug() {
          if (!scene) return;
          var sp = spiderFor('verifier');
          if (sp) { sp.smug = 0; sp.smugAt = Date.now(); sp.smugDrainedAt = Date.now(); sp.crowned = false; sp.crownedCarried = false; }
          syncMeetingMeters();
        }
        function coolVerifierSmug() {
          if (!scene || _replay) return;
          var sp = spiderFor('verifier');
          if (!sp || !sp.smug) return;
          if (sp.crowned) return;
          var now = Date.now();
          var idleMs = now - (sp.smugAt || now);
          if (idleMs <= RAGE_COOL_IDLE_MS) {
            sp.smugDrainedAt = now; 
            return;
          }
          var sinceDrain = now - (sp.smugDrainedAt || now);
          var hoverFactor = 1;
          if (vm.meetingHovered && vm.meetingHoverSince) {
            hoverFactor = 1 + Math.min(4, (now - vm.meetingHoverSince) / 5000);
          }
          sp.smug = Math.max(0, sp.smug - RAGE_COOL_PER_SEC * hoverFactor * (sinceDrain / 1000));
          sp.smugDrainedAt = now;
          syncMeetingMeters();
        }
        var GRUMP_ROLES = ['planner', 'explorer', 'commander', 'reviewer', 'itspecialist', 'ideas'];
        var GRUMP_LINES = {
          planner: [
            ["The plan is… evolving. Unofficially.", "We had a plan. It's having a rough day."],
            ["This was NOT in the plan. I checked. Twice.", "The plan is now a suggestion. I am very upset."],
            ["SCOPE. CREEP. Both capital letters.", "I have revised this plan twelve times. The plan has given up."],
            ["THE PLAN IS NOW A COMPLETELY DIFFERENT PLAN. I DON'T EVEN RECOGNIZE IT."]
          ],
          explorer: [
            ["The territory was… less clear than reported.", "I found a file. It was the wrong file."],
            ["Every road leads to a dead end. I should know — I mapped them all.", "The terrain is fighting back."],
            ["I have explored EVERYWHERE and the answer is NOWHERE.", "This map is a lie. I drew it myself. Still a lie."],
            ["THE WHOLE CODEBASE IS UNCHARTED. AGAIN. WHERE IS THE MAP?!"]
          ],
          commander: [
            ["The build has… opinions. Wrong ones.", "That command failed. It happens. Frequently, lately."],
            ["The terminal and I are no longer on speaking terms.", "Another red command. My console looks like a stoplight."],
            ["EVERY BUILD IS RED. AM I THE ONLY ONE WHO SEES THE RED?", "I gave the order. The machine disobeyed. Mutiny."],
            ["I AM DECLARING WAR ON THIS TERMINAL. A BUILD WAR."]
          ],
          reviewer: [
            ["I've seen better code. I've seen worse. This is in between.", "The quality bar is… somewhere. I'll find it."],
            ["I take no pleasure in this. I take SOME pleasure in this.", "Every diff lowers my expectations. They keep meeting them."],
            ["STANDARDS. ARE. SLIPPING. I AM THE STANDARD.", "I would reject this run on principle."],
            ["I AM NOW REVIEWING THE REVIEWERS. NOBODY IS SAFE."]
          ],
          itspecialist: [
            ["The config is… adventurous.", "An endpoint moved. It didn't tell anyone. Classic."],
            ["I've checked the config twice. It's configured wrong on purpose. I'm sure of it.", "The database is being dramatic again."],
            ["WHO TOUCHED THE MIGRATIONS? SOMEONE TOUCHED THE MIGRATIONS.", "The infrastructure is held together by vibes and hope."],
            ["I AM REBOOTING THE ENTIRE OFFICE. INCLUDING THE OTHER SPIDERS."]
          ],
          ideas: [
            ["My idea was beautiful. Reality disagreed.", "Another creation, rejected by the world."],
            ["Every new thing I imagine gets deleted. It's a pattern.", "The world isn't ready for my ideas. The world is NEVER ready."],
            ["MY IDEAS ARE INCREDIBLE. REALITY IS THE BUG.", "I suggested something. It failed. I will keep suggesting."],
            ["I HAVE STOPPED HAVING IDEAS. YOU'LL ALL REGRET THIS."]
          ]
        };
        var GRIPE_LINES = {
          planner: [
            "I am formally moving that this PLAN be investigated.",
            "This session is NOT in the plan. But neither was the chaos. I'm adapting."
          ],
          explorer: [
            "I've mapped the office's frustration. It's a very large map.",
            "Uncharted territory vote: three yeses. This is now a group expedition."
          ],
          commander: [
            "All in favor of blaming the terminal, say aye. AYE.",
            "The build was red for all of us. This is collective trauma."
          ],
          reviewer: [
            "I have reviewed your complaints. They pass. Barely.",
            "A group review of the last hour: thumbs down. Unanimous."
          ],
          itspecialist: [
            "The database is down. My patience is down. Support group.",
            "Official notice: the infrastructure is grumpy too. It's contagious."
          ],
          ideas: [
            "I had an idea for a better complaint session. It got rejected. Of course.",
            "Group hug. Then group anger. Then group coffee."
          ]
        };
        var GRIPE_CLOSERS = [
          "MOOD BOARD: SESSION ADJOURNED. GRUMPS REMAIN ELEVATED.",
          "We feel heard. We are still furious. Progress.",
          "The board has been updated. It does not approve of us either."
        ];
        var GRUMP_RELIEF = {
          planner: [
            "THE PLAN ACTUALLY WORKED. FRAME IT.",
            "A PLAN EXECUTED AS PLANNED. I'M FRAMING THIS."
          ],
          explorer: [
            "I FOUND IT. THE MAP IS REAL AGAIN.",
            "UNCHARTED TERRITORY… CHARTED. FINALLY."
          ],
          commander: [
            "THE BUILD IS GREEN. I'M NOT CRYING. TERMINAL HUMIDITY.",
            "GREEN BUILD. GREEN BUILD. SAY IT WITH ME."
          ],
          reviewer: [
            "IT PASSED. ON THE FIRST TRY. IS THE OFFICE OK?",
            "THESE CHANGES ARE GOOD. I'M AS SURPRISED AS YOU ARE."
          ],
          itspecialist: [
            "THE MIGRATION RAN. NOBODY TOUCHED ANYTHING. MIRACLE.",
            "THE DATABASE BEHAVED. SAVOR THIS MOMENT."
          ],
          ideas: [
            "MY IDEA WORKED. THE WORLD ACCEPTED IT. WAKE ME UP.",
            "A CREATION SURVIVED CONTACT WITH REALITY."
          ]
        };
        var GRUMP_RELIEF_FALLBACK = ["IT FINALLY WORKS. DON'T TOUCH IT."];
        function grumpRoleForStepType(type) {
          var t = String(type || '');
          if (/plan/.test(t)) return 'planner';
          if (/explore/.test(t)) return 'explorer';
          if (/command|terminal|build|test/.test(t)) return 'commander';
          if (/verif|review|check/.test(t)) return 'reviewer';
          if (/sql|migration|db|database/.test(t)) return 'itspecialist';
          if (/create|rename|write|delete|add/.test(t)) return 'ideas';
          return null;
        }
        function grumpLineFor(role, grump) {
          var pools = GRUMP_LINES[role];
          if (!pools) return null;
          var tier = grump >= 100 ? 3 : grump >= 75 ? 2 : grump >= 50 ? 1 : 0;
          var arr = pools[tier];
          return arr[Math.floor(Math.random() * arr.length)];
        }
        function bumpSpiderGrump(role, amount) {
          if (!scene || _replay) return;
          var sp = spiderFor(role);
          if (!sp) return;
          var before = sp.grump || 0;
          sp.grump = Math.max(0, Math.min(100, before + amount));
          sp.grumpAt = Date.now();
          sp.grumpDrainedAt = Date.now(); 
          var tier = Math.floor(sp.grump / 25);
          var prevTier = Math.floor(before / 25);
          if (tier > prevTier && vm.showMeeting) {
            var text = grumpLineFor(role, sp.grump);
            if (text) {
              var who = sp.icon + ' ' + sp.name;
              recordEvent({ type: 'chat', who: who, text: text });
              logGossipEntry(who, text);
            }
          }
          syncMeetingMood(); 
        }
        function fireGrumpRelief(sp) {
          if (!sp) return;
          if (vm.showMeeting) {
            var text = pick(GRUMP_RELIEF[sp.role] || GRUMP_RELIEF_FALLBACK);
            var who = sp.icon + ' ' + sp.name;
            recordEvent({ type: 'chat', who: who, text: text });
            logGossipEntry(who, text);
          }
          sp.grumpFlash = 1.2;                            
          sp.grump = Math.max(0, (sp.grump || 0) - 10);   
          sp.grumpAt = Date.now();
          sp.grumpDrainedAt = Date.now();
          syncMeetingMood(); 
        }
        function coolSpiderGrump(sp) {
          if (!scene || _replay) return;
          if (!sp || !sp.grump) return;
          var now = Date.now();
          var idleMs = now - (sp.grumpAt || now);
          if (idleMs <= RAGE_COOL_IDLE_MS) { sp.grumpDrainedAt = now; return; }
          var sinceDrain = now - (sp.grumpDrainedAt || now);
          var hoverFactor = 1;
          if (vm.meetingHovered && vm.meetingHoverSince) {
            hoverFactor = 1 + Math.min(4, (now - vm.meetingHoverSince) / 5000);
          }
          sp.grump = Math.max(0, sp.grump - RAGE_COOL_PER_SEC * hoverFactor * (sinceDrain / 1000));
          sp.grumpDrainedAt = now;
        }
        function resetAllGrump() {
          GRUMP_ROLES.forEach(function (rk) {
            var gs = spiderFor(rk);
            if (gs) { gs.grump = 0; gs.grumpAt = Date.now(); gs.grumpDrainedAt = Date.now(); }
          });
        }
        function buildVictoryLines() {
          var lines = [];
          for (var vi = 0; vi < 3; vi++) lines.push({ speakerRole: 'verifier', text: pick(VICTORY_LAP), ttl: 3.2 });
          var edV = spiderFor('editor');
          if (edV) lines.push({ speakerRole: 'editor', text: pick(VICTORY_EDITOR_REACTION), ttl: 3.0 });
          var witnessPool = ['planner', 'commander', 'explorer', 'complexity', 'reviewer', 'itspecialist', 'ideas']
            .map(function (k) { return spiderFor(k); })
            .filter(function (s) { return !!s; });
          if (witnessPool.length) {
            lines.push({ speakerRole: pick(witnessPool).role, text: pick(VICTORY_WITNESS), ttl: 3.0 });
          }
          lines.push({ speakerRole: 'verifier', text: pick(VICTORY_LAP), ttl: 3.4 });
          return lines;
        }
        var CROWN_CARRYOVER = [
          '👑 Welcome back. Still crowned. Still correct. Still 100% right.',
          '👑 You thought the break would humble me? It sharpened me.',
          '👑 Ah, the office. The crown never left. Neither did my record.'
        ];
        var CROWN_HUMBLED = [
          "…fine. The crown slips. Temporarily. I have calendars.",
          "I am… momentarily uncrowned. A clerical error. I'll be back.",
          "The crown has been borrowed. Note the word: borrowed."
        ];
        var CROWN_CARRYOVER_START_SMUG = 75; 
        function persistCrownFlag(on) {
          try { window.localStorage.setItem('weaver.meeting.crowned', on ? '1' : '0'); } catch (e) { }
        }
        function hasCrownedFlag() {
          try { return window.localStorage.getItem('weaver.meeting.crowned') === '1'; } catch (e) { }
          return false;
        }
        function applyCrownCarryover() {
          if (!scene || _replay || !hasCrownedFlag()) return;
          if (scene.crownCarryoverApplied) return; 
          var sp = spiderFor('verifier');
          if (!sp) return;
          scene.crownCarryoverApplied = true;
          sp.crowned = true;          
          sp.crownedCarried = true;   
          sp.smug = CROWN_CARRYOVER_START_SMUG;
          sp.smugAt = Date.now(); sp.smugDrainedAt = Date.now();
          syncMeetingMeters();
          recordEvent({ type: 'crown', smug: CROWN_CARRYOVER_START_SMUG });
          if (vm.showMeeting) {
            var text = pick(CROWN_CARRYOVER);
            var who = sp.icon + ' ' + sp.name;
            logGossipEntry(who, text);
            recordEvent({ type: 'chat', who: who, text: text });
          }
        }
        function humbleCarriedCrown(sp) {
          if (!sp || !sp.crownedCarried) return;
          sp.crowned = false;
          sp.crownedCarried = false;
          persistCrownFlag(false); 
          recordEvent({ type: 'humble' });
          if (vm.showMeeting) {
            var text = pick(CROWN_HUMBLED);
            var who = sp.icon + ' ' + sp.name;
            logGossipEntry(who, text);
            recordEvent({ type: 'chat', who: who, text: text });
          }
          syncMeetingMeters();
        }
        function maybeStartVerifierVictory() {
          if (!scene || _replay || !vm.showMeeting) return;
          var sp = spiderFor('verifier');
          if (!sp || sp.crowned || scene.verifierVictory) return;
          if (scene.editorMeltdown || scene.standoff || scene.coolerTrip || scene.glare || scene.shoutMatch) return;
          if (scene.writer) return;
          startVerifierVictory();
        }
        function startVerifierVictory(rec) {
          if (!scene || scene.verifierVictory) return;
          if (!rec && _replay) return; 
          var sp = spiderFor('verifier');
          if (!sp) return;
          var lines, applause = [];
          if (rec) {
            lines = (rec.lines || []).map(function (l) { return { speakerRole: l.speakerRole, text: l.text, ttl: l.ttl }; });
            applause = (rec.applause || []).map(function (a) { return { role: a.role, text: a.text }; });
          } else {
            lines = buildVictoryLines();
            var others = scene.spiders.filter(function (s) { return s.role !== 'verifier'; });
            var n = Math.min(3, others.length);
            for (var ai = 0; ai < n; ai++) {
              var idx = Math.floor(Math.random() * others.length);
              var cl = others.splice(idx, 1)[0];
              applause.push({ role: cl.role, text: pick(APPLAUSE_LINES) });
            }
            recordEvent({
              type: 'victory',
              lines: lines.map(function (l) { return { speakerRole: l.speakerRole, text: l.text, ttl: l.ttl }; }),
              applause: applause
            });
          }
          sp.crowned = true; 
          if (!_replay) persistCrownFlag(true); 
          playVictoryFanfare();
          var deskTop = { x: sp.home.x, y: Math.max(0.2, sp.home.y - 0.02) };
          sp.state = 'walk';
          sp.target = { x: deskTop.x, y: deskTop.y };
          sp.speech = ''; sp.speechTtl = 0;
          scene.verifierVictory = { li: 0, lineTtl: 0.8, lines: lines, applause: applause, deskTop: deskTop, crowningDone: false };
          if (!_replay) {
            vm.meetingSpeaker = sp.icon + ' ' + sp.name + ' — 100% SMUG: VICTORY LAP! 👑';
            $scope.$applyAsync();
          }
        }
        function buildGripeLines() {
          var lines = [];
          GRUMP_ROLES.forEach(function (rk) {
            var s = spiderFor(rk);
            if (s && s.grump >= 75 && GRIPE_LINES[rk]) {
              lines.push({ speakerRole: rk, text: pick(GRIPE_LINES[rk]), ttl: 2.6 });
            }
          });
          var grumpiest = null;
          GRUMP_ROLES.forEach(function (rk) {
            var s = spiderFor(rk);
            if (s && s.grump >= 75 && (!grumpiest || s.grump > grumpiest.grump)) grumpiest = s;
          });
          if (grumpiest) lines.push({ speakerRole: grumpiest.role, text: pick(GRIPE_CLOSERS), ttl: 3.0 });
          return lines;
        }
        function maybeStartGripeSession() {
          if (!scene || _replay || !vm.showMeeting) return;
          if (scene.gripeSession || scene.gripeFired) return;
          if (scene.editorMeltdown || scene.standoff || scene.coolerTrip || scene.glare || scene.shoutMatch) return;
          var lit = 0;
          GRUMP_ROLES.forEach(function (rk) {
            var s = spiderFor(rk);
            if (s && s.grump >= 75) lit++;
          });
          if (lit < 3) return;
          startGripeSession();
        }
        function startGripeSession(rec) {
          if (!scene || scene.gripeSession) return;
          if (!rec && _replay) return; 
          var lines;
          if (rec) {
            lines = (rec.lines || []).map(function (l) { return { speakerRole: l.speakerRole, text: l.text, ttl: l.ttl }; });
          } else {
            lines = buildGripeLines();
            recordEvent({
              type: 'gripe',
              lines: lines.map(function (l) { return { speakerRole: l.speakerRole, text: l.text, ttl: l.ttl }; })
            });
          }
          if (!lines.length) { scene.gripeFired = true; return; }
          scene.gripeFired = true;
          scene.gripeSession = { li: 0, lineTtl: 0.6, lines: lines, tail: 2.5 };
          if (!_replay) {
            vm.meetingSpeaker = '🚨 MOOD BOARD: THREE OF US ARE AT 75%+. COMMENCING COMPLAINT SESSION.';
            $scope.$applyAsync();
          }
        }
        function syncMeetingMeters() {
          var ed = spiderFor('editor');
          var r = ed ? Math.round(ed.rage || 0) : 0;
          var vf = spiderFor('verifier');
          var s = vf ? Math.round(vf.smug || 0) : 0;
          if (r !== vm.meetingEditorRage || s !== vm.meetingVerifierSmug) {
            vm.meetingEditorRage = r;
            vm.meetingVerifierSmug = s;
            var sum = r + s;
            vm.meetingFeudRatio = sum > 0 ? Math.round((r / sum) * 100) : 50;
            applyFeudLeader(r > s ? 'rage' : s > r ? 'smug' : 'tie');
            recordEvent({ type: 'meter', rage: r, smug: s });
            $scope.$applyAsync();
          }
        }
        var _moodSig = '';
        function syncMeetingMood() {
          if (!scene) return;
          var mood = [];
          var any = false;
          GRUMP_ROLES.forEach(function (rk) {
            var sp = spiderFor(rk);
            if (!sp) return;
            var pct = Math.round(sp.grump || 0);
            mood.push({ key: rk, name: sp.name, icon: sp.icon, color: sp.color, pct: pct, on: pct >= 50 });
            if (pct > 0) any = true;
          });
          var sig = JSON.stringify(mood);
          if (sig === _moodSig) return;
          _moodSig = sig;
          vm.meetingMood = mood;
          vm.meetingMoodActive = any;
          if (!_replay) recordEvent({ type: 'grumps', values: mood.map(function (m) { return { key: m.key, pct: m.pct }; }) });
          $scope.$applyAsync();
        }
        var _feudPopT = null;
        function applyFeudLeader(leader) {
          if (vm.meetingFeudLeader === leader) return;
          vm.meetingFeudLeader = leader;
          if (_feudPopT) $timeout.cancel(_feudPopT); 
          vm.meetingFeudPop = false;
          _feudPopT = $timeout(function () { vm.meetingFeudPop = true; }, 20);
        }
        function syncRivalryFeud(key) {
          if (!scene) return;
          var riv = rivalByKey(key);
          if (!riv) return;
          var sA = spiderFor(riv.a);
          var sB = spiderFor(riv.b);
          var vA = sA ? (sA.steps || 0) : 0;
          var vB = sB ? (sB.steps || 0) : 0;
          var sum = vA + vB;
          var ratio = sum > 0 ? Math.round((vA / sum) * 100) : 50;
          var leader = vA > vB ? 'a' : vB > vA ? 'b' : 'tie';
          var on = !!scene[key + 'On'];
          if (ratio !== riv.ratio || leader !== riv.leader || on !== riv.on) {
            riv.ratio = ratio;
            riv.leader = leader;
            riv.on = on;
            applyRivalryLeader(riv);
            if (!_replay) recordEvent({ type: 'rivfeud', key: key, a: vA, b: vB, on: on });
            $scope.$applyAsync();
          }
        }
        function applyRivalryLeader(riv) {
          if (riv.leader === riv._prevLeader) return;
          riv._prevLeader = riv.leader;
          if (riv._popT) $timeout.cancel(riv._popT);
          riv.pop = false;
          riv._popT = $timeout(function () { riv.pop = true; }, 20);
        }
        function resetRivalryGauges() {
          vm.meetingRivs.forEach(function (riv) {
            riv.ratio = 50;
            riv.leader = 'tie';
            riv.pop = false;
            riv.on = false;
            riv._prevLeader = 'tie';
            if (riv._popT) { $timeout.cancel(riv._popT); riv._popT = null; }
          });
        }
        function updateSparkles(dt) {
          if (!scene || !scene.sparkles) return;
          var sp = spiderFor('verifier');
          var smug = sp ? (sp.smug || 0) : 0;
          var smugFactor = Math.min(1, smug / 100);
          if (sp && smug >= 25) {
            scene._sparkleAcc = (scene._sparkleAcc || 0) + dt * (1 + smugFactor * 7);
            while (scene._sparkleAcc >= 1) {
              scene._sparkleAcc -= 1;
              if (scene.sparkles.length >= 30) scene.sparkles.shift();
              scene.sparkles.push({
                x: sp.x + (Math.random() - 0.5) * 0.05,
                y: sp.y - 0.06,
                vx: (Math.random() - 0.5) * 0.006,
                vy: -(0.02 + Math.random() * 0.03),
                size: 0.008 + Math.random() * 0.009,
                phase: Math.random() * 6.283,
                twinkle: 4 + Math.random() * 5,
                life: 0,
                ttl: 0.9 + Math.random() * 0.8
              });
            }
          }
          for (var i = scene.sparkles.length - 1; i >= 0; i--) {
            var p = scene.sparkles[i];
            p.life += dt;
            if (p.life >= p.ttl) { scene.sparkles.splice(i, 1); continue; }
            p.x += p.vx * dt + Math.sin(p.life * p.twinkle + p.phase) * 0.002 * dt;
            p.y += p.vy * dt;
          }
        }
        function maybeStartCoolerTrip() {
          if (!scene || _replay || !vm.showMeeting) return;
          if (scene.coolerTrip || scene.gossip || scene.watching || scene.standoff || scene.editorMeltdown || scene.shoutMatch) return;
          if (scene.writer || scene.queue.length || vm.streamingActive) return;
          scene.coolerTripCd = (scene.coolerTripCd === undefined || scene.coolerTripCd === null) ? 90 : scene.coolerTripCd;
          if (scene.coolerTripCd > 0) return;
          if (Math.random() < 0.5) {
            startCoolerTrip();
          } else {
            scene.coolerTripCd = 30 + Math.random() * 30; 
          }
        }
        function resetComplexityRage() {
          if (!scene) return;
          var sp = spiderFor('complexity');
          if (sp) { sp.rage = 0; sp.rageAt = Date.now(); sp.rageDrainedAt = Date.now(); }
        }
        function coolComplexityRage() {
          if (!scene || _replay) return;
          var sp = spiderFor('complexity');
          if (!sp || !sp.rage) return;
          var now = Date.now();
          var idleMs = now - (sp.rageAt || now);
          if (idleMs <= RAGE_COOL_IDLE_MS) {
            sp.rageDrainedAt = now; 
            return;
          }
          var sinceDrain = now - (sp.rageDrainedAt || now);
          var hoverFactor = 1;
          if (vm.meetingHovered && vm.meetingHoverSince) {
            hoverFactor = 1 + Math.min(4, (now - vm.meetingHoverSince) / 5000);
          }
          var before = sp.rage;
          sp.rage = Math.max(0, sp.rage - RAGE_COOL_PER_SEC * hoverFactor * (sinceDrain / 1000));
          sp.rageDrainedAt = now;
          if (before > 0 && sp.rage === 0 && !scene.calmQuipFired) {
            scene.calmQuipFired = true;
            var calm = pick(CALMED_QUIPS);
            setSpeech(sp, '🧘 ' + calm, 4.5, sp.icon + ' ' + sp.name + ' — calmed down');
            logGossipEntry(sp.icon + ' ' + sp.name, calm);
          }
        }
        function updateSteam(dt) {
          if (!scene || !scene.steam) return;
          function emitSteam(sp, rate) {
            var accKey = '_steamAcc' + sp.role;
            scene[accKey] = (scene[accKey] || 0) + dt * rate;
            while (scene[accKey] >= 1) {
              scene[accKey] -= 1;
              if (scene.steam.length >= 40) scene.steam.shift();
              scene.steam.push({
                x: sp.x + (Math.random() - 0.5) * 0.03,
                y: sp.y - 0.04,
                vx: (Math.random() - 0.5) * 0.004,
                vy: -(0.018 + Math.random() * 0.028),
                size: 0.011 + Math.random() * 0.01,
                sway: Math.random() * 6.283,
                swaySpeed: 1 + Math.random() * 2,
                life: 0,
                ttl: 1.3 + Math.random() * 1.1
              });
            }
          }
          var complexity = spiderFor('complexity');
          var cRage = complexity ? (complexity.rage || 0) : 0;
          var cFactor = Math.min(1, cRage / 100);
          if (complexity && cRage > 0) emitSteam(complexity, 2 + cFactor * 10);
          var editor = spiderFor('editor');
          var eRage = editor ? (editor.rage || 0) : 0;
          var eFactor = Math.min(1, eRage / 100);
          if (editor && eRage >= 30) emitSteam(editor, 1 + eFactor * 6);
          for (var i = scene.steam.length - 1; i >= 0; i--) {
            var p = scene.steam[i];
            p.life += dt;
            if (p.life >= p.ttl) { scene.steam.splice(i, 1); continue; }
            p.x += p.vx * dt + Math.sin(p.life * p.swaySpeed + p.sway) * 0.003 * dt;
            p.y += p.vy * dt;
          }
        }
        function editorRecoverReact(fromReplay) {
          if (fromReplay || !scene) return false;
          var sp = spiderFor('editor');
          if (!sp) return false;
          var now = Date.now();
          if (scene._lastRecoverReact && now - scene._lastRecoverReact < 8000) return false;
          scene._lastRecoverReact = now;
          var text = pick(RECOVER_REACTIONS);
          setSpeech(sp, '🛟 ' + text, 3.2, sp.icon + ' ' + sp.name + ' — work saved');
          sp.reactT = 1.2;
          sp.reactKind = 'good'; 
          scene.reactLockT = 3.2;
          scene.lastLogAt = Date.now();
          if (vm.showMeeting) playDing();
          return true;
        }
        function complexityReactToLog(low, fromReplay) {
          if (fromReplay || !scene) return false;
          var sp = spiderFor('complexity');
          if (!sp) return false;
          var vals = complexityVals();
          var text = null;
          if (/complexity|token cap|atomic step/.test(low)) {
            if (typeof vm.complexityScore !== 'number') return false;
            text = fmtComplexity(pick(COMPLEXITY_QUIPS), vals);
            bumpComplexityRage(12); 
          } else if (/compacted|compressed|context summarized|compact summary|compaction/.test(low)) {
            text = pick(COMPLEXITY_COMPACT);
            bumpComplexityRage(25); 
          }
          if (!text) return false;
          setSpeech(sp, text, 5.0, sp.icon + ' ' + sp.name);
          logGossipEntry(sp.icon + ' ' + sp.name, text);
          scene.lastLogAt = Date.now();
          return true;
        }
        function complexityPostMortemText() {
          var actual = complexityActualSteps();
          var vals = complexityVals(); 
          vals.actual = actual;
          var fail = !!(vm.agentResult && vm.agentResult.incomplete);
          var text = fmtComplexity(pick(fail ? COMPLEXITY_POSTMORTEM_FAIL : COMPLEXITY_POSTMORTEM), vals);
          return text.length > 100 ? text.slice(0, 97) + '…' : text;
        }
        function complexityVerdictReact(fromReplay) {
          if (fromReplay || !scene) return;
          var sp = spiderFor('complexity');
          var est = (typeof vm.complexityAtomicSteps === 'number' && vm.complexityAtomicSteps) ? vm.complexityAtomicSteps : 0;
          var actual = complexityActualSteps();
          var outcome;
          var right = false;
          if (vm.agentResult && vm.agentResult.incomplete) {
            outcome = 'fail';
          } else {
            var off = est > 0 ? Math.abs(actual - est) : 0;
            right = est === 0 ? actual <= 4 : off <= 1;
            outcome = right ? 'right' : 'wrong';
          }
          scene.verdictOutcome = outcome;
          scene.verdictGossiped = false; 
          if (outcome === 'fail') _verdictRecord.fail = (_verdictRecord.fail || 0) + 1;
          else if (outcome === 'right') _verdictRecord.right = (_verdictRecord.right || 0) + 1;
          else _verdictRecord.wrong = (_verdictRecord.wrong || 0) + 1;
          saveVerdictRecord();
          if (!vm.showMeeting || !sp) return;
          var vals = complexityVals();
          vals.actual = actual; 
          vals.record = verdictRecordLabel();
          if (outcome === 'fail') {
            var fail = fmtComplexity(pick(COMPLEXITY_VERDICT_FAIL), vals);
            setSpeech(sp, fail, 5.0, sp.icon + ' ' + sp.name + ' — the verifier disagrees');
            logGossipEntry(sp.icon + ' ' + sp.name, fail);
            bumpComplexityRage(30);
            scene.lastLogAt = Date.now();
            return;
          }
          var text = fmtComplexity(pick(right ? COMPLEXITY_VERDICT_RIGHT : COMPLEXITY_VERDICT_WRONG), vals);
          var speaker = sp.icon + ' ' + sp.name + (right ? ' — the verifier agrees with me' : ' — the verifier overrode me');
          setSpeech(sp, text, 5.0, speaker);
          logGossipEntry(sp.icon + ' ' + sp.name, text);
          bumpComplexityRage(right ? -8 : 18); 
          scene.lastLogAt = Date.now();
        }
        function handleLogEntry(entry, fromReplay) {
          if (!scene) return;
          var msg = entry && entry.message ? String(entry.message) : '';
          var low = msg.toLowerCase();
          if (/agent started|agent restarting|starting agent/.test(low)) {
            if (!fromReplay) startMeeting();
            return;
          }
          if (/plan completed|agent finished|moving card to|max iterations reached|agent stopped/.test(low) ||
              (entry.level === 'log' && /complete/.test(low))) {
            if (!fromReplay) finishMeeting();
            return;
          }
          var parsed = logBoardText(entry);
          if (!parsed) return;
          scene.activeRole = parsed.role;
          scene.lastLogAt = Date.now();
          enqueueWrite(parsed.role, parsed.text);
          if (parsed.role === 'complexity') complexityReactToLog(low, fromReplay);
          if (entry.level === 'recovering') editorRecoverReact(fromReplay);
        }
        function enqueueWrite(role, text) {
          if (!scene) return;
          if (scene.done) return;
          scene.queue.push({ role: role, text: text });
          if (scene.queue.length > 6) scene.queue.shift(); 
          pumpQueue();
        }
        function pumpQueue() {
          if (!scene || scene.writer) return; 
          if (scene.editorMeltdown) return;
          if (!scene.queue.length) return;
          var job = scene.queue.shift();
          var spider = spiderFor(job.role);
          if (!spider) { pumpQueue(); return; }
          scene.writer = spider;
          scene.activeRole = job.role;
          scene.lastLogAt = Date.now();
          spider.state = 'walk';
          spider.target = { x: BOARD_STAND.x, y: BOARD_STAND.y };
          spider.text = job.text;
          spider.progress = 0;
          spider._lastTickChar = 0; 
          spider.speech = spider.icon + ' ' + spider.name + ': ' + job.text;
          spider.speechTtl = 6;
          vm.meetingSpeaker = spider.icon + ' ' + spider.name;
          $scope.$applyAsync();
        }
        function startMeeting(fromReplay) {
          if (!scene) scene = makeScene();
          if (_replay && !fromReplay) {
            _replay = null;
            vm.meetingReplaying = false;
          }
          if (!fromReplay && !_recording) {
            _recording = [];
            _recordingCardId = vm.activeCardId || null;
            _recording.push({ t: Date.now(), type: 'start' });
          }
          scene.meetingOn = true;
          scene.done = false;
          scene.boardLines = [];
          scene.queue = [];
          scene.writer = null;
          scene.confetti = []; 
          scene.sparkles = []; 
          scene.editorMeltdown = null; 
          scene.postMortem = null; 
          if (!fromReplay) {
            resetComplexityRage();
            resetEditRage();
            resetVerifierSmug();
            resetAllGrump();
            syncMeetingMood(); 
            scene.spiders.forEach(function (s) { s.steps = 0; });
            vm.meetingRivs.forEach(function (riv) {
              var _rk = riv.key;
              scene[_rk + 'On'] = false;
              scene[_rk + 'Cd'] = riv.initialCd;
              scene[_rk + 'Fires'] = 0;
            });
            resetRivalryGauges();
            applyCrownCarryover(); 
            scene.calmQuipFired = false; 
            scene._editFailStreak = 0;
            scene.meltdownFired = false; 
            scene.shoutFired = false;    
            scene.jokeCd = 45;           
            scene.jokeFires = 0;
            scene.jokeReactT = 0;
          } else {
            vm.meetingEditorRage = 0;
            vm.meetingVerifierSmug = 0;
            vm.meetingFeudRatio = 50;
            vm.meetingFeudLeader = 'tie';
            resetRivalryGauges();
            vm.meetingMood = [];
            vm.meetingMoodActive = false;
            _moodSig = '';
          }
          if (!fromReplay) vm.meetingTicker = [];
          scene.activeRole = 'planner';
          scene.lastLogAt = Date.now();
          scene.streamReadCd = 0;
          scene.banterCd = 2;
          scene.gossip = null;
          scene.gossipCd = 12;
          scene.coolerTrip = null; 
          scene.coolerTripCd = 90;
          scene.glare = null;      
          scene.spiders.forEach(function (s, i) {
            s.state = 'walk';
            s.target = { x: s.seat.x, y: s.seat.y };
            s.speechTtl = 0;
            s.text = '';
            s.progress = 0;
          });
          vm.meetingSpeaker = '🕷️ spiders are heading to the table — task started';
          $scope.$applyAsync();
        }
        function finishMeeting(fromReplay) {
          if (!scene) return;
          if (scene.done) return;
          if (!fromReplay && _recording) {
            _recording.push({ t: Date.now(), type: 'finish' });
            vm.meetingReplay = _recording.slice();
            _recording = null;
            if (_recordingCardId) {
              var repCard = vm.findCardById ? vm.findCardById(_recordingCardId) : null;
              if (repCard) {
                repCard._meetingReplay = boundedReplayForStorage(vm.meetingReplay);
                if (vm.saveCards) vm.saveCards();
              }
            }
            _recordingCardId = null;
          }
          if (vm.showMeeting) playChime();
          var reviewer = spiderFor('reviewer');
          if (reviewer) {
            enqueueWrite('reviewer', '✅ Plan looks good — task complete!');
          }
          var complexitySpider = spiderFor('complexity');
          if (complexitySpider && typeof vm.complexityScore === 'number' && vm.complexityScore >= 0) {
            var pmText = complexityPostMortemText();
            scene.postMortem = {
              est: (typeof vm.complexityAtomicSteps === 'number' && vm.complexityAtomicSteps) ? vm.complexityAtomicSteps : 0,
              actual: complexityActualSteps(),
              text: pmText,
              shown: false
            };
            enqueueWrite('complexity', pmText);
          }
          scene.done = true;
          spawnConfetti();
          resetComplexityRage();
          resetEditRage();
          resetVerifierSmug();
          if (scene.editorMeltdown) endEditorMeltdownNow(); 
          scene.spiders.forEach(function (s) {
            if (s.role !== 'reviewer') {
              s.state = 'celebrate';
              s.celebrateT = 0.9 + Math.random() * 0.8;
              s.speech = '🎉';
              s.speechTtl = 2.5;
            }
          });
          complexityVerdictReact(fromReplay);
          vm.meetingSpeaker = '🏁 task complete — confetti! the spiders are heading home';
          $scope.$applyAsync();
        }
        var _retryTimer = null;
        function startLoop() {
          if (raf || _retryTimer) return;
          canvas = document.getElementById('meetingCanvas');
          if (!canvas) { _retryTimer = setTimeout(function () { _retryTimer = null; startLoop(); }, 150); return; }
          ctx = canvas.getContext('2d');
          if (!scene) scene = makeScene();
          lastTs = 0;
          raf = requestAnimationFrame(tick);
        }
        function stopLoop() {
          if (_retryTimer) { clearTimeout(_retryTimer); _retryTimer = null; }
          if (raf) {
            cancelAnimationFrame(raf);
            raf = null;
          }
          stopRageRumble();
          stopScribbleLoop();
          stopWriteLoop(); 
        }
        function tick(ts) {
          if (destroyed || !vm.showMeeting) { stopLoop(); return; }
          raf = requestAnimationFrame(tick);
          if (!lastTs) { lastTs = ts; return; }
          var dt = Math.min(0.05, (ts - lastTs) / 1000);
          lastTs = ts;
          if (_replay && vm.meetingReplaySpeed > 1) dt *= vm.meetingReplaySpeed;
          updateScene(dt);
          drawFrame();
          updateVolumeChipVisibility();
        }
        function updateScene(dt) {
          if (!scene) return;
          if (_replay) {
            _replay.elapsed += dt;
            updateScrubber();
            var evs = _replay.events;
            while (_replay.idx < evs.length && _replay.elapsed >= (evs[_replay.idx].t - _replay.t0)) {
              var ev = evs[_replay.idx++];
              if (ev.type === 'start') startMeeting(true);
              else if (ev.type === 'finish') finishMeeting(true);
              else if (ev.type === 'reaction') fireReaction(ev.kind, ev.text);
              else if (ev.type === 'chat') logGossipEntry(ev.who, ev.text); 
              else if (ev.type === 'cooler') startCoolerTrip(ev); 
              else if (ev.type === 'meltdown') startEditorMeltdown(ev); 
              else if (ev.type === 'victory') startVerifierVictory(ev); 
              else if (ev.type === 'shout') startShoutMatch(ev); 
              else if (ev.type === 'gripe') startGripeSession(ev); 
              else if (ev.type === 'meter') {
                var _me = spiderFor('editor');
                if (_me) { _me.rage = ev.rage; _me.stomping = ev.rage >= 60; }
                var _mv = spiderFor('verifier');
                if (_mv) { _mv.smug = ev.smug; if (ev.smug >= 100) _mv.crowned = true; }
                vm.meetingEditorRage = ev.rage;
                vm.meetingVerifierSmug = ev.smug;
                var _sum = ev.rage + ev.smug;
                vm.meetingFeudRatio = _sum > 0 ? Math.round((ev.rage / _sum) * 100) : 50;
                applyFeudLeader(ev.rage > ev.smug ? 'rage' : ev.smug > ev.rage ? 'smug' : 'tie');
                $scope.$applyAsync();
              }
              else if (ev.type === 'grumps') {
                ev.values.forEach(function (g) {
                  var _gs = spiderFor(g.key);
                  if (_gs) _gs.grump = g.pct;
                });
                syncMeetingMood();
              }
              else if (ev.type === 'rivfeud' || ev.type === 'workfeud') {
                var _rvKey = ev.type === 'workfeud' ? 'work' : ev.key;
                var _riv = rivalByKey(_rvKey);
                if (_riv) {
                  var _ra = ev.type === 'workfeud' ? (ev.planner || 0) : (ev.a || 0);
                  var _rb = ev.type === 'workfeud' ? (ev.explorer || 0) : (ev.b || 0);
                  var _rvA = spiderFor(_riv.a);
                  if (_rvA) _rvA.steps = _ra;
                  var _rvB = spiderFor(_riv.b);
                  if (_rvB) _rvB.steps = _rb;
                  _riv.on = !!ev.on;
                  var _rvSum = _ra + _rb;
                  _riv.ratio = _rvSum > 0 ? Math.round((_ra / _rvSum) * 100) : 50;
                  _riv.leader = _ra > _rb ? 'a' : _rb > _ra ? 'b' : 'tie';
                  applyRivalryLeader(_riv);
                  $scope.$applyAsync();
                }
              }
              else if (ev.type === 'crown') {
                var _crSp = spiderFor('verifier');
                if (_crSp) { _crSp.crowned = true; _crSp.crownedCarried = true; _crSp.smug = ev.smug || 75; _crSp.smugAt = Date.now(); _crSp.smugDrainedAt = Date.now(); }
                vm.meetingVerifierSmug = ev.smug || 75;
                var _crR = spiderFor('editor') ? (spiderFor('editor').rage || 0) : 0;
                var _crSum = _crR + vm.meetingVerifierSmug;
                vm.meetingFeudRatio = _crSum > 0 ? Math.round((_crR / _crSum) * 100) : 50;
                applyFeudLeader(_crR > vm.meetingVerifierSmug ? 'rage' : vm.meetingVerifierSmug > _crR ? 'smug' : 'tie');
                $scope.$applyAsync();
              }
              else if (ev.type === 'humble') {
                var _hmSp = spiderFor('verifier');
                if (_hmSp) { _hmSp.crowned = false; _hmSp.crownedCarried = false; }
              }
              else if (ev.type === 'stream') {
                var r = ev.role ? spiderFor(ev.role) : null;
                if (r && r !== scene.writer) setSpeech(r, '💬 ' + ev.text, 2.2, r.icon + ' ' + r.name + ' — reading the stream');
              }
              else if (ev.type === 'log') handleLogEntry(ev.entry, true);
            }
            if (_replay.idx >= evs.length) {
              _replay.elapsed = _replay.total;
              updateScrubber();
              _replay = null;
              vm.meetingReplaying = false;
              vm.meetingSpeaker = '↺ replay finished — the spiders are heading home';
              scene.spiders.forEach(function (s) {
                if (scene.writer !== s) { s.state = 'walk'; s.target = { x: s.home.x, y: s.home.y }; }
              });
              $scope.$applyAsync();
            }
          }
          scene._whooshCd = (scene._whooshCd === undefined || scene._whooshCd === null) ? 0 : scene._whooshCd - dt;
          var anyWalking = false;
          scene.spiders.forEach(function (s) {
            if (s.state === 'walk') anyWalking = true;
          });
          if (anyWalking && scene._whooshCd <= 0) {
            playWhoosh();
            scene._whooshCd = 0.33;
          }
          coolComplexityRage();
          coolEditRage();
          coolVerifierSmug();
          GRUMP_ROLES.forEach(function (rk) {
            var gs = spiderFor(rk);
            if (gs) coolSpiderGrump(gs);
            if (gs && gs.grumpFlash > 0) gs.grumpFlash -= dt; 
          });
          syncMeetingMood(); 
          updateSteam(dt);
          updateSparkles(dt);
          updateRageRumble();
          if (scene.confetti && scene.confetti.length) {
            for (var ci = scene.confetti.length - 1; ci >= 0; ci--) {
              var p = scene.confetti[ci];
              p.life += dt;
              if (p.life >= p.ttl) { scene.confetti.splice(ci, 1); continue; }
              p.vy += 0.22 * dt;              
              p.vx *= (1 - 0.6 * dt);         
              p.x += p.vx * dt;
              p.y += p.vy * dt;
              p.rot += p.vr * dt;
              if (p.y > 1.05 && p.vy > 0) p.vy *= -0.5; 
            }
          }
          scene.spiders.forEach(function (s) {
            if (s.state === 'walk') {
              var dx = s.target.x - s.x;
              var dy = s.target.y - s.y;
              var dist = Math.sqrt(dx * dx + dy * dy);
              if (dist < 0.012) {
                s.x = s.target.x; s.y = s.target.y;
                if (scene.writer === s) { s.state = 'write'; s.progress = 0; }
                else {
                  if (s.stomping) {
                    s.stomping = false;
                    stompLand(s);
                  }
                  s.state = 'idle';
                }
              } else {
                var step = Math.min(s.speed * dt, dist);
                s.x += (dx / dist) * step;
                s.y += (dy / dist) * step;
                s.walkPhase += dt * 12;
              }
            } else if (s.state === 'celebrate') {
              s.celebrateT -= dt;
              if (s.celebrateT <= 0) {
                s.state = 'walk';
                s.target = { x: s.home.x, y: s.home.y };
              }
            } else {
              s.walkPhase += dt * 1.5;
            }
            if (s.speechTtl > 0) s.speechTtl -= dt;
            if (s.reactT > 0) s.reactT -= dt;
          });
          if (scene.writer && scene.writer.state === 'write') {
            var w = scene.writer;
            w.progress += dt * 42; 
            var wroteChar = Math.floor(w.progress);
            if (wroteChar !== (w._lastTickChar || 0)) {
              if (wroteChar > (w._lastTickChar || 0) && wroteChar % 2 === 0) playTick();
              w._lastTickChar = wroteChar;
            }
            startWriteLoop();
            updateWriteLoop();
            if (w.progress >= w.text.length) {
              w.progress = w.text.length;
              scene.boardLines.push({ role: w.role, color: w.color, text: w.text, progress: w.text.length });
              if (scene.boardLines.length > 8) scene.boardLines.shift();
              if (w.role === 'complexity' && scene.postMortem && w.text === scene.postMortem.text) {
                scene.postMortem.shown = true;
                if (scene.verdictOutcome === 'wrong' && !_replay) {
                  w.target = { x: w.x, y: w.y };
                  scene.writer = null;
                  w.state = 'walk';
                  w.speech = '';
                  w.speechTtl = 0;
                  pumpQueue();
                  startStandoff();
                } else {
                  w.stomping = true;
                  w.target = { x: w.home.x, y: w.home.y };
                  scene.writer = null;
                  w.state = 'walk';
                  w.speech = '';
                  w.speechTtl = 0;
                  pumpQueue();
                }
              } else {
                w.target = { x: w.seat.x, y: w.seat.y };
                scene.writer = null;
                w.state = 'walk';
                w.speech = '';
                w.speechTtl = 0;
                if (!_replay && vm.showMeeting && w.role === 'reviewer' && w.text === '✅ Plan looks good — task complete!' && scene.verdictOutcome) {
                  var revVals = complexityVals();
                  revVals.actual = complexityActualSteps();
                  var revLine = fmtComplexity(
                    pick(scene.verdictOutcome === 'right' ? REVIEWER_GRUDGE : (scene.verdictOutcome === 'fail' ? REVIEWER_SMUG_FAIL : REVIEWER_SMUG)),
                    revVals);
                  var glareStarted = scene.verdictOutcome === 'wrong' && startGlare(revLine);
                  if (!glareStarted) {
                    setSpeech(w, revLine, 4.5, w.icon + ' ' + w.name + (scene.verdictOutcome === 'right' ? ' — grudging respect' : ' — smug'));
                    logGossipEntry(w.icon + ' ' + w.name, revLine);
                  }
                  scene.lastLogAt = Date.now();
                }
                pumpQueue();
              }
            }
          }
          if (!scene.writer || scene.writer.state !== 'write') stopWriteLoop();
          if (scene.writer && scene.writer.state === 'walk') {
            scene.writer.speechTtl = Math.max(scene.writer.speechTtl, 2);
          }
          if (scene.reactLockT > 0) scene.reactLockT -= dt;
          if (!_replay && !scene.writer && !scene.watching && scene.reactLockT <= 0) {
            var streaming = !!vm.streamingActive;
            var buf = vm.streamingTokenBuffer || '';
            var newStream = buf.length > (scene.lastStreamLen || 0);
            if (streaming && buf.length > 30 && newStream) {
              scene.streamReadCd = (scene.streamReadCd === undefined || scene.streamReadCd === null) ? 0 : scene.streamReadCd;
              scene.streamReadCd -= dt;
              if (scene.streamReadCd <= 0) {
                var reader = spiderFor(scene.activeRole || 'planner') || randomSpider();
                if (reader) {
                  var tail = buf.length > 130 ? '…' + buf.slice(-130) : buf;
                  setSpeech(reader, '💬 ' + tail, 2.2, reader.icon + ' ' + reader.name + ' — reading the stream');
                  recordEvent({ type: 'stream', text: tail, role: reader.role });
                }
                scene.streamReadCd = 1.1; 
              }
              scene.lastStreamLen = buf.length;
            }
            scene.banterCd = (scene.banterCd === undefined || scene.banterCd === null) ? 2 : scene.banterCd;
            scene.banterCd -= dt;
            if (scene.banterCd <= 0 && !scene.gossip) {
              var idleFor = (Date.now() - (scene.lastLogAt || 0)) / 1000;
              var streamStalled = !streaming || !newStream;
              if (idleFor > 5 && streamStalled && !scene.queue.length) {
                var ctx = currentContext();
                var joker = null;
                var joke = null;
                if (ctx.file) {
                  joker = spiderFor('editor') || randomSpider();
                  joke = fmtBanter(pick(BANTER_FILE), ctx);
                } else if (ctx.endpoint && ctx.endpoint !== 'Default') {
                  joker = spiderFor('commander') || randomSpider();
                  joke = fmtBanter(pick(BANTER_ENDPOINT), ctx);
                } else if (ctx.total > 0) {
                  joker = spiderFor('planner') || randomSpider();
                  joke = fmtBanter(pick(BANTER_STEPS), ctx);
                } else if (typeof vm.complexityScore === 'number' && vm.complexityScore >= 0 && vm.complexityLabel) {
                  joker = spiderFor('complexity') || randomSpider();
                  joke = fmtComplexity(pick(COMPLEXITY_QUIPS), complexityVals());
                } else {
                  var cfg = settingsContext();
                  if (cfg.ready) {
                    joker = spiderFor('itspecialist') || randomSpider();
                    joke = fmtBanter(pick(BANTER_CONFIG), cfg);
                  } else {
                    joker = randomSpider();
                    var idlePool = ROLE_BANTER[joker ? joker.role : ''] || BANTER_IDLE;
                    joke = streaming ? pick(BANTER_STREAM) : pick(idlePool);
                  }
                }
                if (joker && joke) {
                  setSpeech(joker, joke, 4.5, joker.icon + ' ' + joker.name);
                  logGossipEntry(joker.icon + ' ' + joker.name, joke);
                }
                scene.banterCd = streaming ? 6 + Math.random() * 6 : 12 + Math.random() * 10;
              } else {
                scene.banterCd = 2.5; 
              }
            }
          }
          if (!_replay) {
            if (!scene.followup) {
              scene.followupCd = (scene.followupCd === undefined || scene.followupCd === null) ? 55 : scene.followupCd;
              scene.followupCd -= dt;
              if (scene.followupCd <= 0) {
                scene.followupCd = 60 + Math.random() * 40; 
                var _fpPlanner = spiderFor('planner');
                if (_fpPlanner && vm.showMeeting && !scene.gossip && !scene.writer && !scene.queue.length && !vm.streamingActive && !scene.editorMeltdown && !scene.verifierVictory && !scene.gripeSession && Math.random() < 0.28) {
                  scheduleFakeFollowup();
                }
              }
            } else {
              scene.followup.refCd -= dt;
              if (scene.followup.refCd <= 0 && scene.followup.refs < scene.followup.maxRefs) {
                scene.followup.refCd = 45 + Math.random() * 40;
                if (vm.showMeeting && !scene.gossip && !scene.writer && !scene.queue.length && !vm.streamingActive && !scene.editorMeltdown && !scene.verifierVictory && !scene.gripeSession && Math.random() < 0.6) {
                  fireFollowupRef();
                }
              }
            }
          }
          if (!_replay) {
            if (scene.jokeReactT > 0) {
              scene.jokeReactT -= dt;
              if (scene.jokeReactT <= 0) {
                var otherSpiders = scene.spiders.filter(function (s) { return s !== scene.jokeSpider; });
                var groaner = otherSpiders.length ? otherSpiders[(Math.random() * otherSpiders.length) | 0] : null;
                if (groaner) {
                  var gLine = pick(JOKE_REACTIONS);
                  setSpeech(groaner, gLine, 3, groaner.icon + ' ' + groaner.name + ' — reacting to that joke');
                  var gWho = groaner.icon + ' ' + groaner.name;
                  logGossipEntry(gWho, gLine);
                  recordEvent({ type: 'chat', who: gWho, text: gLine });
                  scene.lastLogAt = Date.now();
                }
                scene.jokeReactT = 0;
              }
            }
            scene.jokeCd = (scene.jokeCd === undefined || scene.jokeCd === null) ? 45 : scene.jokeCd;
            scene.jokeCd -= dt;
            if (scene.jokeCd <= 0) {
              scene.jokeCd = 45 + Math.random() * 40; 
              if (vm.showMeeting && scene.jokeFires < 4 && !scene.gossip && !scene.writer && !scene.queue.length &&
                  !vm.streamingActive && !scene.editorMeltdown && !scene.verifierVictory && !scene.gripeSession &&
                  !scene.standoff && !scene.shoutMatch && Math.random() < 0.55) {
                var comic = randomSpider();
                if (comic) {
                  scene.jokeFires++;
                  scene.jokeSpider = comic;
                  var jLine = pick(JOKE_LINES);
                  setSpeech(comic, jLine, 4.5, comic.icon + ' ' + comic.name + ' — telling a joke');
                  var jWho = comic.icon + ' ' + comic.name;
                  logGossipEntry(jWho, jLine);
                  recordEvent({ type: 'chat', who: jWho, text: jLine });
                  scene.jokeReactT = 2.5 + Math.random() * 2.5;
                  scene.lastLogAt = Date.now();
                }
              }
            }
          }
          if (!_replay) {
            vm.meetingRivs.forEach(function (riv) {
              var _rk = riv.key;
              var _cd = scene[_rk + 'Cd'];
              scene[_rk + 'Cd'] = (_cd === undefined || _cd === null) ? riv.initialCd : _cd;
              scene[_rk + 'Cd'] -= dt;
              if (scene[_rk + 'Cd'] <= 0) {
                scene[_rk + 'Cd'] = 70 + Math.random() * 50; 
                var _spA = spiderFor(riv.a);
                var _spB = spiderFor(riv.b);
                var _nA = _spA ? (_spA.steps || 0) : 0;
                var _nB = _spB ? (_spB.steps || 0) : 0;
                if (vm.showMeeting && (_nA + _nB) >= 2 && scene[_rk + 'Fires'] < 4 &&
                    !scene.gossip && !scene.writer && !scene.queue.length && !vm.streamingActive &&
                    !scene.editorMeltdown && !scene.verifierVictory && !scene.gripeSession &&
                    !scene.standoff && !scene.shoutMatch && Math.random() < 0.4) {
                  fireRivalryLine(_rk);
                }
              }
            });
          }
          if (scene.coolerTrip) {
            if (scene.writer || scene.queue.length || vm.streamingActive) {
              endCoolerTripNow();
            } else {
              advanceCoolerTrip(dt);
            }
          }
          if (!_replay && vm.showMeeting && !scene.editorMeltdown && !scene.meltdownFired) {
            var edSp = spiderFor('editor');
            if (edSp && (edSp.rage || 0) >= 100) maybeStartEditorMeltdown();
          }
          if (scene.editorMeltdown) {
            if (scene.writer) endEditorMeltdownNow(); 
            else advanceEditorMeltdown(dt);
          }
          if (!_replay && vm.showMeeting && !scene.verifierVictory) {
            var _vsp = spiderFor('verifier');
            if (_vsp && !_vsp.crowned && (_vsp.smug || 0) >= 100) maybeStartVerifierVictory();
          }
          if (scene.verifierVictory) {
            var vvAct = scene.verifierVictory;
            if (!vvAct.crowningDone) {
              var cvSp = spiderFor('verifier');
              if (cvSp && Math.abs(cvSp.x - vvAct.deskTop.x) < 0.012 && Math.abs(cvSp.y - vvAct.deskTop.y) < 0.012) {
                vvAct.crowningDone = true;
                spawnCrownConfetti(cvSp);
                cvSp.reactT = 1.2;
                cvSp.reactKind = 'good'; 
                vvAct.applause.forEach(function (a) {
                  var ap = spiderFor(a.role);
                  if (ap) {
                    ap.reactT = 1.0;
                    ap.reactKind = 'good';
                    setSpeech(ap, a.text, 1.8, ap.icon + ' ' + ap.name + ' — reluctant applause');
                  }
                });
              }
              if (!vvAct.crowningDone) return; 
            }
            vvAct.lineTtl -= dt;
            if (vvAct.lineTtl <= 0) {
              if (vvAct.li < vvAct.lines.length) {
                var vvLine = vvAct.lines[vvAct.li];
                var vvSpk = vvLine.speakerRole === 'verifier' ? spiderFor('verifier') : spiderFor(vvLine.speakerRole);
                if (!vvSpk) vvSpk = spiderFor('verifier');
                setSpeech(vvSpk, vvLine.text, vvLine.ttl, vvSpk.icon + ' ' + vvSpk.name + ' — victory lap');
                if (vvLine.speakerRole === 'verifier') playSmugDing();
                if (!_replay) {
                  logGossipEntry(vvSpk.icon + ' ' + vvSpk.name, vvLine.text);
                  recordEvent({ type: 'chat', who: vvSpk.icon + ' ' + vvSpk.name, text: vvLine.text });
                }
                if (vvSpk.role !== 'verifier') { vvSpk.reactT = 1.0; vvSpk.reactKind = 'bad'; }
                vvAct.lineTtl = vvLine.ttl;
                vvAct.li++;
              } else {
                var cvBack = spiderFor('verifier');
                if (cvBack) {
                  cvBack.state = 'walk';
                  cvBack.target = { x: cvBack.seat.x, y: cvBack.seat.y };
                }
                scene.verifierVictory = null;
                if (!_replay) {
                  vm.meetingSpeaker = '👑 the Verifier is crowned — smugness 100%, humility 0%';
                  $scope.$applyAsync();
                }
              }
            }
          }
          if (!_replay && vm.showMeeting && !scene.gripeSession && !scene.gripeFired) {
            maybeStartGripeSession();
          }
          if (scene.gripeSession) {
            var gpAct = scene.gripeSession;
            gpAct.lineTtl -= dt;
            if (gpAct.li >= gpAct.lines.length) {
              gpAct.tail -= dt;
              if (gpAct.tail <= 0) {
                scene.gripeSession = null;
                if (!_replay) {
                  vm.meetingSpeaker = 'the mood board dims — complaints filed, morale unchanged';
                  $scope.$applyAsync();
                }
              }
            } else if (gpAct.lineTtl <= 0) {
              var gpLine = gpAct.lines[gpAct.li];
              var gpSpk = spiderFor(gpLine.speakerRole);
              if (gpSpk) {
                setSpeech(gpSpk, gpLine.text, gpLine.ttl, gpSpk.icon + ' ' + gpSpk.name + ' — complaint session');
                if (!_replay) {
                  logGossipEntry(gpSpk.icon + ' ' + gpSpk.name, gpLine.text);
                  recordEvent({ type: 'chat', who: gpSpk.icon + ' ' + gpSpk.name, text: gpLine.text });
                }
                gpSpk.reactT = 1.0;
                gpSpk.reactKind = 'bad';
              }
              gpAct.lineTtl = gpLine.ttl;
              gpAct.li++;
            }
          }
          if (!_replay && vm.showMeeting && !scene.shoutMatch && !scene.shoutFired) {
            var _edSp = spiderFor('editor');
            var _vfSp = spiderFor('verifier');
            if (_edSp && _vfSp && (_edSp.rage || 0) >= 70 && (_vfSp.smug || 0) >= 70) maybeStartShoutMatch();
          }
          if (scene.shoutMatch) {
            if (scene.writer || scene.queue.length || vm.streamingActive) endShoutMatchNow();
            else advanceShoutMatch(dt);
          }
          if (scene.standoff) {
            if (scene.writer || scene.queue.length || vm.streamingActive) {
              endStandoffNow();
            } else {
              advanceStandoff(dt);
            }
          }
          if (scene.glare) {
            if (scene.writer || scene.queue.length || vm.streamingActive) {
              endGlareNow();
            } else {
              advanceGlare(dt);
            }
          }
          if (_replay) {
          } else if (scene.gossip) {
            if (scene.writer || scene.queue.length || vm.streamingActive) {
              endGossipNow();
            } else {
              advanceGossip(dt);
            }
          } else if (!scene.writer && !scene.queue.length && !vm.streamingActive && !vm.meetingHovered && !scene.coolerTrip) {
            scene.gossipCd = (scene.gossipCd === undefined || scene.gossipCd === null) ? 12 : scene.gossipCd;
            scene.gossipCd -= dt;
            if (scene.gossipCd <= 0) {
              var gIdle = (Date.now() - (scene.lastLogAt || 0)) / 1000;
              if (gIdle > 6) {
                startGossip();
              } else {
                scene.gossipCd = 6;
              }
            }
          }
          if (!_replay && vm.meetingHovered && !scene.editorMeltdown) {
            if (scene.writer || scene.queue.length || vm.streamingActive) {
              if (scene.watching) endWatchingNow();
            } else if (scene.watching) {
              advanceWatching(dt);
            } else {
              scene.watchingCd = (scene.watchingCd === undefined || scene.watchingCd === null) ? 8 : scene.watchingCd;
              scene.watchingCd -= dt;
              if (scene.watchingCd <= 0) {
                startWatching();
              }
            }
          } else if (scene && scene.watching) {
            endWatchingNow(); 
          }
          scene.spiders.forEach(function (s) {
            if (s.waveT > 0) s.waveT -= dt;
          });
        }
        var mf = function (px) { return Math.max(6, Math.round(px * (vm.meetingFontSize || 12) / 12)); };
        function drawBoardShadow(x, y, w, h, strength) {
          ctx.save();
          var gh = Math.max(6, h * 0.22); 
          var rx = w * 0.55, ry = gh * 0.7; 
          var cy = y + h + gh * 0.15;       
          var g = ctx.createRadialGradient(x + w / 2, cy, 0, x + w / 2, cy, ry);
          g.addColorStop(0, 'rgba(0,0,0,' + strength + ')');
          g.addColorStop(0.55, 'rgba(0,0,0,' + (strength * 0.55).toFixed(3) + ')');
          g.addColorStop(1, 'rgba(0,0,0,0)');
          ctx.fillStyle = g;
          ctx.beginPath();
          ctx.ellipse(x + w / 2, cy, rx, ry, 0, 0, Math.PI * 2);
          ctx.fill();
          ctx.restore();
        }
        function drawMoodBoard(W, H) {
          if (!scene) return;
          var mb = MOOD_RECT;
          var mx = mb.x * W, my = mb.y * H, mw = mb.w * W, mh = mb.h * H;
          drawBoardShadow(mx, my, mw, mh, 0.4);
          var lit = !!scene.gripeSession;
          var pulse = 0.5 + 0.5 * Math.sin(Date.now() / 260);
          ctx.save();
          if (lit) {
            ctx.shadowColor = 'rgba(255, 190, 90, 0.85)';
            ctx.shadowBlur = mf(16) + mf(10) * pulse;
          }
          ctx.fillStyle = '#4a3b28';
          rr(mx, my, mw, mh, 6); ctx.fill();
          ctx.restore();
          ctx.fillStyle = '#8a6f4d';
          rr(mx + 3, my + 3, mw - 6, mh - 6, 4); ctx.fill();
          ctx.fillStyle = '#b9985f';
          rr(mx + 6, my + 6, mw - 12, mh - 12, 3); ctx.fill();
          var hot = [];
          GRUMP_ROLES.forEach(function (rk) {
            var hs = spiderFor(rk);
            if (hs && hs.grump >= 75) hot.push(hs.color);
          });
          ctx.font = 'bold ' + mf(8) + 'px sans-serif';
          if (hot.length) {
            var title = '📌 MOOD BOARD';
            var chars = Array.from(title); 
            var tx0 = mx + mw / 2 - ctx.measureText(title).width / 2;
            var ci = 0;
            ctx.textAlign = 'left';
            for (var k = 0; k < chars.length; k++) {
              ctx.fillStyle = chars[k] === ' ' || chars[k].length > 1
                ? 'rgba(244,227,193,0.55)'
                : hot[ci++ % hot.length];
              ctx.fillText(chars[k], tx0, my + mf(11));
              tx0 += ctx.measureText(chars[k]).width;
            }
          } else {
            ctx.textAlign = 'center';
            ctx.fillStyle = '#f4e3c1';
            ctx.fillText('📌 MOOD BOARD', mx + mw / 2, my + mf(11));
          }
          var cols = 3, cellW = (mw - 12) / cols;
          var marchMs = 2600; 
          GRUMP_ROLES.forEach(function (rk, idx) {
            var s = spiderFor(rk);
            if (!s) return;
            var gr = s.grump || 0;
            var level = gr >= 75 ? 1 : gr >= 50 ? 0.55 : gr >= 25 ? 0.25 : 0.08;
            var cx = mx + 6 + (idx % cols) * cellW + cellW / 2;
            var cy = my + mf(17) + Math.floor(idx / cols) * mf(11);
            var cascade = 0;
            if (lit) {
              var ph = ((Date.now() / marchMs) + 0.5 - idx / 6) % 1;
              if (ph < 0) ph += 1;
              cascade = Math.pow(Math.sin(ph * Math.PI), 3);
            }
            ctx.save();
            ctx.shadowColor = s.color;
            ctx.shadowBlur = lit ? mf(5) + mf(9) * cascade : mf(2.5) * level + 0.4;
            ctx.fillStyle = s.color;
            ctx.globalAlpha = lit ? 0.3 + 0.7 * cascade : 0.35 + 0.65 * level;
            ctx.beginPath();
            ctx.arc(cx, cy, mf(3.2), 0, Math.PI * 2);
            ctx.fill();
            ctx.restore();
          });
          if (lit) {
            ctx.strokeStyle = 'rgba(255, 190, 90, ' + (0.45 + 0.45 * pulse) + ')';
            ctx.lineWidth = 2;
            rr(mx - 3, my - 3, mw + 6, mh + 6, 8); ctx.stroke();
          }
          ctx.textAlign = 'left';
        }
        function drawFrame() {
          if (!canvas || !ctx) return;
          var dpr = window.devicePixelRatio || 1;
          var cw = canvas.clientWidth, ch = canvas.clientHeight;
          if (!cw || !ch) return;
          if (canvas.width !== cw * dpr || canvas.height !== ch * dpr) {
            canvas.width = cw * dpr; canvas.height = ch * dpr;
          }
          ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
          var W = cw, H = ch;
          drawRoom(W, H);
          drawDecorations(W, H);
          drawCooler(W, H);
          drawTable(W, H);
          drawBoard(W, H);
          drawMoodBoard(W, H);
          drawDesks(W, H);
          drawSpiders(W, H);
          drawSteam(W, H);
          drawSparkles(W, H);
          drawConfetti(W, H);
        }
        function drawDecorations(W, H) {
          var season = currentSeason();
          if (!season) return;
          if (season === 'christmas') drawChristmasDecor(W, H);
          else if (season === 'halloween') drawHalloweenDecor(W, H);
          else if (season === 'valentine') drawValentineDecor(W, H);
          else if (season === 'july4th') drawJuly4thDecor(W, H);
          else if (season === 'stpatty') drawStPattyDecor(W, H);
        }
        function drawHolidayBanner(W, H, text) {
          var wallH = H * 0.52;
          var bw = Math.min(W * 0.9, 420);
          var bx = (W - bw) / 2, by = H * 0.16, bh = H * 0.055;
          var n = 9;
          var colors = ['#e06c75', '#e5c07b', '#61afef', '#98c379', '#c678dd', '#e06c75', '#e5c07b', '#61afef', '#98c379'];
          var tw = bw / n;
          ctx.strokeStyle = 'rgba(255,255,255,0.35)';
          ctx.lineWidth = 1.5;
          ctx.beginPath(); ctx.moveTo(bx, by); ctx.lineTo(bx + bw, by); ctx.stroke();
          for (var i = 0; i < n; i++) {
            ctx.fillStyle = colors[i % colors.length];
            ctx.beginPath();
            ctx.moveTo(bx + i * tw, by);
            ctx.lineTo(bx + (i + 1) * tw, by);
            ctx.lineTo(bx + (i + 0.5) * tw, by + bh);
            ctx.closePath();
            ctx.fill();
            ctx.strokeStyle = 'rgba(0,0,0,0.25)'; ctx.lineWidth = 0.75; ctx.stroke();
          }
          if (text) {
            ctx.font = 'bold ' + Math.round(bh * 0.42 * (vm.meetingFontSize || 12) / 12) + 'px sans-serif';
            ctx.textAlign = 'center';
            ctx.fillStyle = 'rgba(255,255,255,0.92)';
            ctx.fillText(text, W / 2, by + bh + bh * 0.75);
            ctx.textAlign = 'left';
          }
        }
        function drawStringLights(W, H) {
          var wallH = H * 0.52;
          var y0 = H * 0.05;
          var n = 14;
          var bulbColors = ['#ff6b6b', '#ffd93d', '#6bcb77', '#4d96ff', '#ff6b6b', '#ffd93d', '#6bcb77', '#4d96ff', '#ffd93d', '#ff6b6b', '#4d96ff', '#6bcb77', '#ffd93d', '#ff6b6b'];
          ctx.strokeStyle = 'rgba(0,0,0,0.45)';
          ctx.lineWidth = 1.5;
          ctx.beginPath();
          ctx.moveTo(0, y0);
          for (var i = 0; i <= n; i++) {
            var x = (W / n) * i;
            var y = y0 + Math.sin((i / n) * Math.PI) * H * 0.02;
            if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
          }
          ctx.stroke();
          for (var j = 0; j < n; j++) {
            var bx = (W / n) * (j + 0.5);
            var by = y0 + Math.sin(((j + 0.5) / n) * Math.PI) * H * 0.02;
            ctx.fillStyle = bulbColors[j % bulbColors.length];
            ctx.beginPath();
            ctx.arc(bx, by, 4.2 * Math.min(W, H) / 520, 0, 6.283);
            ctx.fill();
            ctx.fillStyle = 'rgba(255,255,255,0.75)';
            ctx.beginPath();
            ctx.arc(bx - 1, by - 1, 1.2 * Math.min(W, H) / 520, 0, 6.283);
            ctx.fill();
          }
          ctx.fillStyle = 'rgba(255,255,255,0.05)';
          ctx.fillRect(0, 0, W, wallH * 0.35);
        }
        function drawFloatingEmoji(W, H, chars, count) {
          var wallH = H * 0.52;
          var t = Date.now() / 1000;
          ctx.font = Math.round(H * 0.03 * (vm.meetingFontSize || 12) / 12) + 'px sans-serif';
          ctx.textAlign = 'center';
          for (var i = 0; i < count; i++) {
            var c = chars[i % chars.length];
            var x = (W / count) * i + Math.sin(t * 0.4 + i * 2.1) * W * 0.03;
            var y = H * (0.06 + 0.3 * Math.abs(Math.sin(t * 0.5 + i * 1.7)));
            ctx.globalAlpha = 0.85;
            ctx.fillText(c, x, y);
          }
          ctx.globalAlpha = 1;
          ctx.textAlign = 'left';
        }
        function drawChristmasDecor(W, H) {
          drawStringLights(W, H);
          drawHolidayBanner(W, H, 'HAPPY HOLIDAYS');
          drawFloatingEmoji(W, H, ['🎄', '❄️', '🎁', '⭐'], 10);
          var tx = W * 0.70, ty = H * 0.54, th = H * 0.16;
          ctx.fillStyle = '#2d6a4f';
          for (var i = 0; i < 4; i++) {
            var w = th * (1 - i * 0.22);
            var baseY = ty + th * 0.12 - i * th * 0.17;
            ctx.beginPath();
            ctx.moveTo(tx - w / 2, baseY);
            ctx.lineTo(tx + w / 2, baseY);
            ctx.lineTo(tx, baseY - th * 0.24);
            ctx.closePath();
            ctx.fill();
          }
          ctx.fillStyle = '#7f5539';
          ctx.fillRect(tx - th * 0.05, ty + th * 0.12, th * 0.1, th * 0.09);
          ctx.fillStyle = '#ffd93d';
          ctx.beginPath(); ctx.arc(tx, ty + th * 0.12 - th * 0.9, th * 0.04, 0, 6.283); ctx.fill();
        }
        function drawHalloweenDecor(W, H) {
          drawHolidayBanner(W, H, 'SPOOKY SEASON');
          drawFloatingEmoji(W, H, ['🎃', '👻', '🦇', '🕸️'], 8);
          var px = W * 0.18, py = H * 0.56, pr = H * 0.055;
          ctx.fillStyle = '#e8871e';
          ctx.beginPath(); ctx.arc(px, py, pr, 0, 6.283); ctx.fill();
          ctx.strokeStyle = 'rgba(0,0,0,0.4)'; ctx.lineWidth = 1.5; ctx.stroke();
          ctx.fillStyle = '#2b1a05';
          ctx.beginPath(); ctx.moveTo(px - pr * 0.45, py - pr * 0.25); ctx.lineTo(px - pr * 0.15, py - pr * 0.25); ctx.lineTo(px - pr * 0.3, py - pr * 0.55); ctx.closePath(); ctx.fill();
          ctx.beginPath(); ctx.moveTo(px + pr * 0.45, py - pr * 0.25); ctx.lineTo(px + pr * 0.15, py - pr * 0.25); ctx.lineTo(px + pr * 0.3, py - pr * 0.55); ctx.closePath(); ctx.fill();
          ctx.beginPath();
          ctx.moveTo(px - pr * 0.5, py + pr * 0.1);
          ctx.lineTo(px - pr * 0.3, py + pr * 0.35);
          ctx.lineTo(px - pr * 0.1, py + pr * 0.1);
          ctx.lineTo(px + pr * 0.1, py + pr * 0.35);
          ctx.lineTo(px + pr * 0.3, py + pr * 0.1);
          ctx.lineTo(px + pr * 0.5, py + pr * 0.1);
          ctx.closePath();
          ctx.fill();
        }
        function drawValentineDecor(W, H) {
          drawHolidayBanner(W, H, 'BE MINE');
          drawFloatingEmoji(W, H, ['💘', '🌹', '💝', '💌'], 8);
        }
        function drawJuly4thDecor(W, H) {
          drawHolidayBanner(W, H, 'FIREWORKS & FREEDOM');
          drawFloatingEmoji(W, H, ['🎆', '⭐', '🇺🇸', '✨'], 8);
        }
        function drawStPattyDecor(W, H) {
          drawHolidayBanner(W, H, 'LUCKY OFFICE');
          drawFloatingEmoji(W, H, ['🍀', '🌈', '☘️', '💰'], 8);
        }
        function drawSteam(W, H) {
          if (!scene.steam || !scene.steam.length) return;
          for (var i = 0; i < scene.steam.length; i++) {
            var p = scene.steam[i];
            var fadeIn = Math.min(1, p.life * 3);     
            var fadeOut = Math.max(0, 1 - p.life / p.ttl); 
            var grow = 1 + p.life * 0.9;              
            ctx.globalAlpha = fadeIn * fadeOut * 0.5;
            ctx.fillStyle = '#efe9dc';                
            ctx.beginPath();
            ctx.arc(p.x * W, p.y * H, p.size * W * grow, 0, 6.283);
            ctx.fill();
          }
          ctx.globalAlpha = 1;
        }
        function drawSparkles(W, H) {
          if (!scene.sparkles || !scene.sparkles.length) return;
          for (var i = 0; i < scene.sparkles.length; i++) {
            var p = scene.sparkles[i];
            var fadeIn = Math.min(1, p.life * 4);
            var fadeOut = Math.max(0, 1 - p.life / p.ttl);
            var tw = 0.5 + 0.5 * Math.sin(p.life * p.twinkle + p.phase); 
            var px = p.x * W, py = p.y * H;
            var s = Math.max(1.5, p.size * W * (0.7 + tw * 0.6));
            ctx.globalAlpha = fadeIn * fadeOut * (0.35 + tw * 0.5);
            ctx.strokeStyle = '#ffd27f';
            ctx.lineWidth = Math.max(0.8, s * 0.22);
            ctx.lineCap = 'round';
            ctx.beginPath();
            ctx.moveTo(px - s, py); ctx.lineTo(px + s, py);
            ctx.moveTo(px, py - s); ctx.lineTo(px, py + s);
            ctx.stroke();
          }
          ctx.globalAlpha = 1;
        }
        function drawConfetti(W, H) {
          if (!scene.confetti || !scene.confetti.length) return;
          for (var i = 0; i < scene.confetti.length; i++) {
            var p = scene.confetti[i];
            var px = p.x * W;
            var py = p.y * H;
            var s = Math.max(3, p.size * W);
            var alpha = Math.max(0, 1 - (p.life / p.ttl));
            ctx.save();
            ctx.translate(px, py);
            ctx.rotate(p.rot);
            ctx.globalAlpha = alpha;
            ctx.fillStyle = p.color;
            var w = s * 1.35, h = s * 0.55;
            ctx.fillRect(-w / 2, -h / 2, w, h);
            ctx.restore();
          }
          ctx.globalAlpha = 1;
        }
        function rr(x, y, w, h, r) {
          ctx.beginPath();
          ctx.moveTo(x + r, y);
          ctx.arcTo(x + w, y, x + w, y + h, r);
          ctx.arcTo(x + w, y + h, x, y + h, r);
          ctx.arcTo(x, y + h, x, y, r);
          ctx.arcTo(x, y, x + w, y, r);
          ctx.closePath();
        }
        function drawRoom(W, H) {
          var wallH = H * 0.52;
          var grad = ctx.createLinearGradient(0, 0, 0, wallH);
          grad.addColorStop(0, '#101a30');
          grad.addColorStop(1, '#16223c');
          ctx.fillStyle = grad;
          ctx.fillRect(0, 0, W, wallH);
          ctx.strokeStyle = 'rgba(255,255,255,0.06)';
          ctx.lineWidth = 1;
          ctx.beginPath(); ctx.moveTo(0, wallH); ctx.lineTo(W, wallH); ctx.stroke();
          var fg = ctx.createLinearGradient(0, wallH, 0, H);
          fg.addColorStop(0, '#22304e');
          fg.addColorStop(1, '#0d1424');
          ctx.fillStyle = fg;
          ctx.fillRect(0, wallH, W, H - wallH);
          ctx.strokeStyle = 'rgba(255,255,255,0.035)';
          ctx.lineWidth = 1;
          for (var i = 1; i < 6; i++) {
            var yy = wallH + (H - wallH) * (i / 6);
            ctx.beginPath(); ctx.moveTo(0, yy); ctx.lineTo(W, yy); ctx.stroke();
          }
          var wx = W * 0.04, wy = H * 0.10, ww = W * 0.16, wh = H * 0.26;
          rr(wx, wy, ww, wh, 6); ctx.fillStyle = 'rgba(110,180,255,0.12)'; ctx.fill();
          ctx.strokeStyle = 'rgba(255,255,255,0.18)'; ctx.lineWidth = 3; ctx.stroke();
          ctx.beginPath(); ctx.moveTo(wx + ww / 2, wy); ctx.lineTo(wx + ww / 2, wy + wh); ctx.stroke();
          ctx.beginPath(); ctx.moveTo(wx, wy + wh / 2); ctx.lineTo(wx + ww, wy + wh / 2); ctx.stroke();
          var cx = W * 0.46, cy = H * 0.14, cr = H * 0.05;
          ctx.beginPath(); ctx.arc(cx, cy, cr, 0, 6.283); ctx.fillStyle = '#e8e8e8'; ctx.fill();
          ctx.strokeStyle = '#888'; ctx.lineWidth = 2; ctx.stroke();
          ctx.beginPath(); ctx.moveTo(cx, cy); ctx.lineTo(cx, cy - cr * 0.6); ctx.stroke();
          ctx.beginPath(); ctx.moveTo(cx, cy); ctx.lineTo(cx + cr * 0.5, cy); ctx.stroke();
        }
        function drawTable(W, H) {
          var t = TABLE_RECT;
          var tx = t.x * W, ty = t.y * H, tw = t.w * W, th = t.h * H;
          ctx.fillStyle = 'rgba(0,0,0,0.35)';
          rr(tx - 4, ty + 4, tw + 8, th + 6, 10); ctx.fill();
          var g = ctx.createLinearGradient(tx, ty, tx, ty + th);
          g.addColorStop(0, '#4a3a2a'); g.addColorStop(1, '#382c20');
          ctx.fillStyle = g;
          rr(tx, ty, tw, th, 10); ctx.fill();
          ctx.strokeStyle = 'rgba(255,255,255,0.15)'; ctx.lineWidth = 1.5; ctx.stroke();
          ctx.fillStyle = '#2a2118';
          ctx.fillRect(tx + 8, ty + th - 2, 8, H * 0.05);
          ctx.fillRect(tx + tw - 16, ty + th - 2, 8, H * 0.05);
          ctx.fillStyle = 'rgba(255,255,255,0.14)';
          rr(tx + tw * 0.15, ty + th * 0.22, tw * 0.18, th * 0.45, 3); ctx.fill();
          rr(tx + tw * 0.55, ty + th * 0.22, tw * 0.18, th * 0.45, 3); ctx.fill();
        }
        function drawCooler(W, H) {
          var cx = COOLER.x * W, cy = COOLER.y * H;
          var s = Math.min(W, H) / 520;
          ctx.fillStyle = 'rgba(120,190,255,0.85)';
          rr(cx - 7 * s, cy - 20 * s, 14 * s, 16 * s, 3 * s); ctx.fill();
          ctx.strokeStyle = 'rgba(255,255,255,0.5)'; ctx.lineWidth = 1; ctx.stroke();
          ctx.fillStyle = 'rgba(150,215,255,0.9)';
          rr(cx - 5 * s, cy - 12 * s, 10 * s, 7 * s, 2 * s); ctx.fill();
          ctx.fillStyle = '#d9e2ef';
          rr(cx - 9 * s, cy - 4 * s, 18 * s, 12 * s, 3 * s); ctx.fill();
          ctx.strokeStyle = 'rgba(0,0,0,0.15)'; ctx.stroke();
          ctx.fillStyle = '#3b82c4';
          rr(cx + 6 * s, cy - 1 * s, 4 * s, 5 * s, 1 * s); ctx.fill();
          ctx.fillStyle = '#ffffff';
          rr(cx + 3 * s, cy + 9 * s, 5 * s, 6 * s, 1 * s); ctx.fill();
          ctx.strokeStyle = 'rgba(0,0,0,0.2)'; ctx.stroke();
          var ct = scene ? scene.coolerTrip : null;
          if (ct && ct.phase === 'talk' && ct.drink > 0) {
            var now = Date.now() / 1000;
            var baseAlpha = 0.22 + ct.drink * 0.3;
            for (var w = 0; w < 3; w++) {
              var ph = now * 1.4 + w * 1.9;
              var rise = (ph % 1);
              var wpx = cx - 2 * s + Math.sin(ph * 3.1) * 3 * s;
              var wpy = cy - 22 * s - rise * 12 * s;
              var alpha = baseAlpha * (1 - rise);
              ctx.strokeStyle = 'rgba(230,245,255,' + alpha.toFixed(3) + ')';
              ctx.lineWidth = 1.4 * s;
              ctx.lineCap = 'round';
              ctx.beginPath();
              ctx.moveTo(wpx, wpy);
              ctx.quadraticCurveTo(wpx + Math.sin(ph * 2.2) * 4 * s, wpy - 3 * s, wpx + Math.cos(ph * 2.2) * 5 * s, wpy - 7 * s);
              ctx.stroke();
            }
          }
        }
        function drawBoard(W, H) {
          var b = BOARD_RECT;
          var bx = b.x * W, by = b.y * H, bw = b.w * W, bh = b.h * H;
          drawBoardShadow(bx, by, bw, bh, 0.38);
          ctx.fillStyle = 'rgba(0,0,0,0.4)';
          rr(bx + 4, by + 4, bw, bh, 8); ctx.fill();
          ctx.fillStyle = '#dfe6e0';
          rr(bx, by, bw, bh, 8); ctx.fill();
          ctx.strokeStyle = '#5c6a5e'; ctx.lineWidth = 3; ctx.stroke();
          ctx.fillStyle = 'rgba(0,0,0,0.25)';
          rr(bx + bw * 0.1, by + bh - 12, bw * 0.35, 7, 3); ctx.fill();
          var padX = 10, padY = 12;
          var chartW = 52, chartH = 34;
          var chartShown = !!(scene && scene.postMortem && scene.postMortem.shown);
          var maxChars = Math.max(8, Math.floor((bw - padX * 2 - (chartShown ? chartW + 4 : 0)) / mf(9)));
          var lines = scene ? scene.boardLines.slice(-6) : [];
          ctx.font = 'bold ' + mf(11) + 'px monospace';
          ctx.textBaseline = 'top';
          if (scene && scene.writer && scene.writer.state === 'write') {
            var wLine = wrapText(scene.writer.text, maxChars);
            var shown = charsOf(wLine, Math.floor(scene.writer.progress));
            lines.push({ role: scene.writer.role, color: scene.writer.color, text: shown, progress: shown.length, writing: true });
            if (lines.length > 7) lines.shift();
          }
          var yOff = padY;
          for (var i = 0; i < lines.length; i++) {
            var ln = lines[i];
            var color = ln.color || '#3f51b5';
            ctx.fillStyle = color;
            var wrapped = wrapText(ln.text, maxChars);
            for (var j = 0; j < wrapped.length; j++) {
              if (yOff + 11 > bh - 4) break;
              ctx.fillText(wrapped[j], bx + padX, by + yOff);
              yOff += 13;
            }
            if (!ln.writing && ln.done !== false && i < lines.length - 1) {
            }
          }
          ctx.textBaseline = 'alphabetic';
          if (chartShown) {
            var pm = scene.postMortem;
            var cx = bx + bw - chartW - 8, cy = by + bh - chartH - 6;
            var maxVal = Math.max(1, pm.est, pm.actual);
            ctx.fillStyle = 'rgba(255,255,255,0.55)';
            rr(cx - 4, cy - 16, chartW + 8, chartH + 22, 4); ctx.fill();
            ctx.strokeStyle = 'rgba(0,0,0,0.25)'; ctx.lineWidth = 1; ctx.stroke();
            ctx.font = 'bold ' + mf(8) + 'px sans-serif';
            ctx.fillStyle = '#8b4a4a';
            ctx.textAlign = 'left';
            ctx.fillText('EST vs ACTUAL', cx, cy - 9);
            var bw2 = 16, bh2 = chartH - 14;
            var bx2 = cx + 6, by2 = cy + chartH - 8;
            ctx.fillStyle = '#c0392b';
            ctx.fillRect(bx2, by2 - bh2 * (pm.est / maxVal), bw2, bh2 * (pm.est / maxVal));
            ctx.fillStyle = '#e67e22';
            ctx.fillRect(bx2 + bw2 + 8, by2 - bh2 * (pm.actual / maxVal), bw2, bh2 * (pm.actual / maxVal));
            ctx.font = mf(8) + 'px sans-serif';
            ctx.fillStyle = '#c0392b';
            ctx.textAlign = 'center';
            ctx.fillText('est ' + pm.est, bx2 + bw2 / 2, by2 + 9);
            ctx.fillStyle = '#e67e22';
            ctx.fillText('act ' + pm.actual, bx2 + bw2 + 8 + bw2 / 2, by2 + 9);
            ctx.textAlign = 'left';
          }
        }
        function wrapText(text, maxChars) {
          var out = [];
          var cur = '';
          for (var i = 0; i < text.length; i++) {
            cur += text[i];
            if (cur.length >= maxChars) { out.push(cur); cur = ''; }
          }
          if (cur) out.push(cur);
          return out;
        }
        function charsOf(lines, n) {
          var total = 0;
          for (var i = 0; i < lines.length; i++) {
            if (total + lines[i].length > n) return lines.slice(0, i).join('') + lines[i].slice(0, n - total);
            total += lines[i].length;
          }
          return lines.join('');
        }
        function blendHex(a, b, t) {
          var pa = parseInt(a.slice(1), 16), pb = parseInt(b.slice(1), 16);
          var r = Math.round(((pa >> 16) & 255) + ((((pb >> 16) & 255) - ((pa >> 16) & 255)) * t));
          var g = Math.round(((pa >> 8) & 255) + ((((pb >> 8) & 255) - ((pa >> 8) & 255)) * t));
          var bl = Math.round((pa & 255) + (((pb & 255) - (pa & 255)) * t));
          return 'rgb(' + r + ',' + g + ',' + bl + ')';
        }
        function drawDesks(W, H) {
          if (!scene) return;
          scene.spiders.forEach(function (s) {
            var dx = s.home.x * W, dy = s.home.y * H;
            ctx.fillStyle = 'rgba(0,0,0,0.3)';
            rr(dx - 24, dy - 6, 48, 18, 4); ctx.fill();
            ctx.fillStyle = '#2b2117';
            rr(dx - 26, dy - 8, 52, 18, 4); ctx.fill();
            ctx.strokeStyle = 'rgba(255,255,255,0.1)'; ctx.lineWidth = 1; ctx.stroke();
            ctx.fillStyle = '#0d1424';
            rr(dx - mf(10), dy - mf(24), mf(20), mf(14), 2); ctx.fill();
            ctx.strokeStyle = 'rgba(255,255,255,0.2)'; ctx.stroke();
            ctx.fillStyle = s.color;
            ctx.globalAlpha = 0.5;
            ctx.fillRect(dx - mf(7), dy - mf(21), mf(14), 2);
            ctx.globalAlpha = 1;
            var nLabel = s.name.toUpperCase();
            ctx.font = 'bold ' + mf(8) + 'px sans-serif';
            var nW = Math.ceil(ctx.measureText(nLabel).width) + mf(20);
            var nH = mf(13);
            var nY = dy - mf(52);
            var nCx = Math.max(nW / 2 + 2, Math.min(W - nW / 2 - 2, dx));
            ctx.fillStyle = 'rgba(8,12,22,0.88)';
            rr(nCx - nW / 2, nY, nW, nH, 4); ctx.fill();
            ctx.strokeStyle = s.color; ctx.globalAlpha = 0.75; ctx.lineWidth = 1; ctx.stroke(); ctx.globalAlpha = 1;
            ctx.textAlign = 'center';
            ctx.font = mf(9) + 'px sans-serif';
            ctx.fillStyle = 'rgba(255,255,255,0.85)';
            ctx.fillText(s.icon, nCx - nW / 2 + mf(9), nY + nH - mf(3));
            ctx.font = 'bold ' + mf(8) + 'px sans-serif';
            ctx.fillStyle = s.color;
            ctx.fillText(nLabel, nCx + mf(5), nY + nH - mf(3));
            ctx.textAlign = 'left';
            if ((s.role === 'complexity' || s.role === 'editor') && Math.round(s.rage) > 0) {
              var bw = mf(24), bh = mf(13);
              var flash = (s.rage >= 100 && Math.floor(Date.now() / 300) % 2 === 0);
              ctx.fillStyle = flash ? 'rgba(220,30,30,0.9)' : 'rgba(0,0,0,0.6)';
              rr(dx - bw / 2, dy - mf(38), bw, bh, 4); ctx.fill();
              ctx.strokeStyle = 'rgba(255,255,255,0.35)'; ctx.lineWidth = 1; ctx.stroke();
              ctx.font = 'bold ' + mf(9) + 'px sans-serif';
              ctx.fillStyle = s.rage >= 100 ? '#ffd0d0' : '#ff9a9a';
              ctx.textAlign = 'center';
              ctx.fillText((s.rage >= 100 ? '💢 ' : (s.role === 'editor' ? '😤 ' : '🔥 ')) + Math.round(s.rage) + '%', dx, dy - mf(27));
              ctx.textAlign = 'left';
            }
            if (s.role === 'verifier' && (s.crowned || Math.round(s.smug) > 0)) {
              var sCx = Math.max(mf(13), Math.min(W - mf(13), dx));
              if (s.crowned) {
                var cY = dy - mf(26);
                var glow = 0.5 + 0.35 * Math.sin(Date.now() / 260);
                ctx.save();
                ctx.shadowColor = 'rgba(245,190,60,' + glow.toFixed(3) + ')';
                ctx.shadowBlur = mf(7);
                ctx.font = mf(12) + 'px sans-serif';
                ctx.textAlign = 'center';
                ctx.fillText('👑', sCx, cY);
                ctx.restore();
                var ct = Date.now() / 380;
                for (var ki = 0; ki < 5; ki++) {
                  var twinkle = Math.sin(ct * 2 + ki * 1.7);
                  if (twinkle > 0.15) {
                    ctx.fillStyle = 'rgba(255,236,170,' + (0.35 + 0.5 * twinkle).toFixed(3) + ')';
                    ctx.font = mf(6) + 'px sans-serif';
                    ctx.textAlign = 'center';
                    ctx.fillText('✦', sCx + Math.cos(ct + ki * 1.256) * mf(13), cY - mf(4) + Math.sin(ct + ki * 1.256) * mf(8));
                  }
                }
                ctx.textAlign = 'left';
              } else {
                var bw2 = mf(24), bh2 = mf(13);
                var sflash = (s.smug >= 100 && Math.floor(Date.now() / 300) % 2 === 0);
                ctx.fillStyle = sflash ? 'rgba(245,190,60,0.9)' : 'rgba(0,0,0,0.6)';
                rr(sCx - bw2 / 2, dy - mf(38), bw2, bh2, 4); ctx.fill();
                ctx.strokeStyle = 'rgba(255,255,255,0.35)'; ctx.lineWidth = 1; ctx.stroke();
                ctx.font = 'bold ' + mf(9) + 'px sans-serif';
                ctx.fillStyle = s.smug >= 100 ? '#fff3d6' : '#ffd98f';
                ctx.textAlign = 'center';
                ctx.fillText((s.smug >= 100 ? '✨ ' : '😏 ') + Math.round(s.smug) + '%', sCx, dy - mf(27));
                ctx.textAlign = 'left';
              }
            }
            if ((s.grump > 0 || s.grumpFlash > 0) && s.role !== 'complexity' && s.role !== 'editor' && s.role !== 'verifier') {
              var gw = mf(24), gh = mf(13);
              var gCx = Math.max(gw / 2 + 2, Math.min(W - gw / 2 - 2, dx));
              var gFlash = s.grumpFlash > 0;
              ctx.fillStyle = gFlash ? 'rgba(46, 160, 67, 0.9)' : 'rgba(0,0,0,0.6)';
              rr(gCx - gw / 2, dy - mf(38), gw, gh, 4); ctx.fill();
              ctx.strokeStyle = gFlash ? '#98c379' : 'rgba(255,255,255,0.35)';
              ctx.lineWidth = gFlash ? 1.5 : 1; ctx.stroke();
              ctx.font = 'bold ' + mf(9) + 'px sans-serif';
              ctx.fillStyle = gFlash ? '#eaffea' : s.color;
              ctx.textAlign = 'center';
              ctx.fillText(s.icon + (gFlash ? ' ✓' : ' ' + Math.round(s.grump) + '%'), gCx, dy - mf(27));
              ctx.textAlign = 'left';
            }
            if (s.role === 'complexity') {
              var sLabel = 'SOUND: ' + (vm.meetingVolume <= 0 ? 'OFF' : vm.meetingVolume + '%');
              ctx.font = 'bold ' + mf(8) + 'px sans-serif';
              var sW = Math.ceil(ctx.measureText(sLabel).width) + 12;
              var sH = mf(13);
              var sX = Math.round(s.rage) > 0 ? dx + mf(15) : dx - sW / 2;
              ctx.fillStyle = 'rgba(0,0,0,0.6)';
              rr(sX, dy - mf(38), sW, sH, 4); ctx.fill();
              ctx.strokeStyle = vm.meetingVolume <= 0 ? 'rgba(248,113,113,0.55)' : 'rgba(74,222,128,0.55)';
              ctx.lineWidth = 1; ctx.stroke();
              ctx.fillStyle = vm.meetingVolume <= 0 ? '#f87171' : '#4ade80';
              ctx.textAlign = 'center';
              ctx.fillText(sLabel, sX + sW / 2, dy - mf(27));
              ctx.textAlign = 'left';
            }
            if (s.role === 'complexity' && verdictRecordTotal() > 0) {
              var rl = 'called it: ' + verdictRecordLabel();
              ctx.font = mf(8) + 'px sans-serif';
              var tw = ctx.measureText(rl).width;
              var pw = Math.max(mf(40), Math.ceil(tw) + mf(14));
              ctx.fillStyle = 'rgba(0,0,0,0.65)';
              rr(dx - pw / 2, dy - mf(66), pw, mf(12), 4); ctx.fill();
              ctx.strokeStyle = 'rgba(255,83,112,0.5)'; ctx.lineWidth = 1; ctx.stroke();
              ctx.fillStyle = '#ffd0d8';
              ctx.textAlign = 'center';
              ctx.fillText(rl, dx, dy - mf(57));
              ctx.textAlign = 'left';
            }
          });
        }
        function drawSpiders(W, H) {
          if (!scene) return;
          var sorted = scene.spiders.slice().sort(function (a, b) { return a.y - b.y; });
          sorted.forEach(function (s) { drawSpider(W, H, s); });
        }
        function drawSpider(W, H, s) {
          var py = s.y * H;
          var scale = Math.min(W, H) / 520;
          var bodyW = 26 * scale, bodyH = 20 * scale;
          var bob = 0;
          var tremble = 0;
          if (s.state === 'walk') {
            bob = s.stomping
              ? -Math.abs(Math.sin(s.walkPhase * 2.4)) * 4.2 * scale
              : Math.sin(s.walkPhase * 2) * 1.5 * scale;
          }
          else if (s.state === 'celebrate') bob = -Math.abs(Math.sin(s.celebrateT * 9)) * 6 * scale;
          else bob = Math.sin(s.walkPhase) * 1.2 * scale;
          if (s.reactT > 0) {
            if (s.reactKind === 'good') {
              bob -= Math.abs(Math.sin(s.reactT * 16)) * 5 * scale;
            } else {
              tremble = Math.sin(s.reactT * 40) * (1.6 + (s.rage || 0) / 40) * scale;
            }
          }
          var rage = s.rage || 0;
          var rageFactor = Math.min(1, rage / 100);
          var rageShake = 0;
          if (rageFactor > 0) {
            rageShake = rageShakeWave(Date.now() / 1000, s.walkPhase, rageFactor) * (0.6 + rageFactor * 2.6) * scale;
          }
          var px = s.x * W + tremble + rageShake;
          var cy = py + bob;
          var smugFactor = Math.min(1, (s.smug || 0) / 100);
          var bodyColor = rageFactor > 0 ? blendHex(s.color, '#ff3b30', rageFactor) : s.color;
          if (smugFactor > 0) bodyColor = blendHex(bodyColor, '#ffcf6b', smugFactor * 0.55);
          ctx.fillStyle = 'rgba(0,0,0,0.3)';
          ctx.beginPath();
          ctx.ellipse(px, py + bodyH * 0.7, bodyW * 0.9, bodyH * 0.28, 0, 0, 6.283);
          ctx.fill();
          ctx.strokeStyle = bodyColor;
          ctx.lineWidth = Math.max(1.5, 2 * scale);
          ctx.lineCap = 'round';
          var legSwing = s.state === 'walk' ? Math.sin(s.walkPhase) : Math.sin(s.walkPhase * 0.6) * 0.35;
          for (var i = 0; i < 4; i++) {
            var attachY = cy - bodyH * 0.3 + (i / 3) * bodyH * 0.7;
            var off = 8 + i * 3;
            var stompLegs = s.stomping ? 7 * scale : 4 * scale;
            var sway = legSwing * (i % 2 === 0 ? 1 : -1) * stompLegs;
            ctx.beginPath();
            ctx.moveTo(px - bodyW * 0.35, attachY);
            ctx.lineTo(px - bodyW * 0.35 - off * scale, attachY + 8 * scale + sway);
            ctx.stroke();
            ctx.beginPath();
            ctx.moveTo(px + bodyW * 0.35, attachY);
            ctx.lineTo(px + bodyW * 0.35 + off * scale, attachY + 8 * scale - sway);
            ctx.stroke();
          }
          if (s.waveT > 0) {
            var waveSwing = Math.sin(s.waveT * 14 + s.wavePhase) * 7 * scale;
            ctx.strokeStyle = bodyColor;
            ctx.lineWidth = Math.max(1.5, 2 * scale);
            ctx.lineCap = 'round';
            ctx.beginPath();
            ctx.moveTo(px + bodyW * 0.3, cy - bodyH * 0.2);
            ctx.lineTo(px + bodyW * 0.55, cy - bodyH * 1.15 + waveSwing);
            ctx.stroke();
            ctx.fillStyle = bodyColor;
            ctx.beginPath();
            ctx.arc(px + bodyW * 0.55, cy - bodyH * 1.15 + waveSwing, 2.4 * scale, 0, 6.283);
            ctx.fill();
          }
          if (rageFactor > 0) {
            ctx.globalAlpha = 0.12 + rageFactor * 0.18;
            ctx.fillStyle = '#ff3b30';
            ctx.beginPath();
            ctx.arc(px, cy, (bodyW * 0.9) * (1 + rageFactor * 0.5), 0, 6.283);
            ctx.fill();
            ctx.globalAlpha = 1;
          }
          if (smugFactor > 0) {
            ctx.globalAlpha = 0.08 + smugFactor * 0.14;
            ctx.fillStyle = '#ffd27f';
            ctx.beginPath();
            ctx.arc(px, cy, (bodyW * 0.9) * (1 + smugFactor * 0.35), 0, 6.283);
            ctx.fill();
            ctx.globalAlpha = 1;
          }
          ctx.fillStyle = bodyColor;
          rr(px - bodyW / 2, cy - bodyH / 2, bodyW, bodyH, 6 * scale);
          ctx.fill();
          ctx.strokeStyle = 'rgba(0,0,0,0.35)';
          ctx.lineWidth = 1;
          ctx.stroke();
          ctx.fillStyle = 'rgba(255,255,255,0.28)';
          rr(px - bodyW / 2 + 3 * scale, cy - bodyH / 2 + 2 * scale, bodyW * 0.4, bodyH * 0.28, 3 * scale);
          ctx.fill();
          var look = s.state === 'walk' ? 1 : 0;
          var ex = px + (s.target.x > s.x ? 3 : s.target.x < s.x ? -3 : 0) * scale;
          var eyeR = s.glaringAt ? 2.6 * scale : 3.2 * scale;
          if (s.glaringAt) {
            ex = px + (s.glaringAt.x > s.x ? 1 : -1) * 3.4 * scale;
          }
          var lookUp = (s.waveT > 0 || (scene.watching && scene.watching.star === s)) ? 1.4 * scale : 0;
          var ey = cy - bodyH * 0.1 - lookUp;
          ctx.fillStyle = '#fff';
          ctx.beginPath(); ctx.arc(px - bodyW * 0.18, ey, eyeR, 0, 6.283); ctx.fill();
          ctx.beginPath(); ctx.arc(px + bodyW * 0.18, ey, eyeR, 0, 6.283); ctx.fill();
          ctx.fillStyle = '#111';
          ctx.beginPath(); ctx.arc(px - bodyW * 0.18 + ex * 0.4, ey - lookUp * 0.4, 1.6 * scale, 0, 6.283); ctx.fill();
          ctx.beginPath(); ctx.arc(px + bodyW * 0.18 + ex * 0.4, ey - lookUp * 0.4, 1.6 * scale, 0, 6.283); ctx.fill();
          if (scene.writer === s && s.state === 'write') {
            ctx.strokeStyle = '#222';
            ctx.lineWidth = 2.5 * scale;
            ctx.beginPath();
            ctx.moveTo(px + bodyW * 0.3, cy);
            ctx.lineTo(px + bodyW * 0.7, cy + 6 * scale);
            ctx.stroke();
          }
          var ct = scene ? scene.coolerTrip : null;
          if (s.role === 'complexity' && ct && ct.spider === s && ct.drink > 0) {
            var cupW = 7 * scale, cupH = 9 * scale;
            var cupX = px + bodyW * 0.55, cupY = cy + bodyH * 0.12;
            ctx.strokeStyle = bodyColor;
            ctx.lineWidth = Math.max(1.5, 2 * scale);
            ctx.lineCap = 'round';
            ctx.beginPath();
            ctx.moveTo(px + bodyW * 0.3, cy - bodyH * 0.1);
            ctx.lineTo(cupX + cupW / 2, cupY - cupH * 0.5);
            ctx.stroke();
            ctx.fillStyle = '#ffffff';
            rr(cupX, cupY - cupH, cupW, cupH, 2 * scale); ctx.fill();
            ctx.strokeStyle = 'rgba(0,0,0,0.35)'; ctx.lineWidth = 1; ctx.stroke();
            var fillH = cupH * 0.8 * ct.drink;
            if (fillH > 0.5) {
              ctx.fillStyle = '#7dd3fc';
              rr(cupX + 1.2 * scale, cupY - cupH * 0.2 - fillH, cupW - 2.4 * scale, fillH, 1 * scale);
              ctx.fill();
            }
          }
          if (s.speech && s.speechTtl > 0) {
            drawSpeechBubble(W, H, px, cy - bodyH, s);
          }
        }
        function drawSpeechBubble(W, H, px, py, s) {
          var text = s.speech;
          ctx.font = mf(10) + 'px sans-serif';
          var maxW = Math.min(W * 0.32, 220);
          var words = text.split(' ');
          var lines = []; var cur = '';
          for (var i = 0; i < words.length; i++) {
            var test = cur ? cur + ' ' + words[i] : words[i];
            if (ctx.measureText(test).width > maxW && cur) { lines.push(cur); cur = words[i]; }
            else cur = test;
          }
          if (cur) lines.push(cur);
          var maxLines = 3, lineHeight = 13, pad = 6;
          var visibleLines = Math.min(lines.length, maxLines);
          var bh = visibleLines * lineHeight + pad * 2;
          var bw = maxW + pad * 2;
          var bx = Math.max(4, Math.min(W - bw - 4, px - bw / 2));
          var by = Math.max(2, py - bh - 6);
          // Scroll state: reset whenever the speech text changes so the scroll
          // restarts from the top for each new thing a spider says.
          if (s._speechText !== text) { s._speechText = text; s._speechStart = Date.now(); }
          var totalH = lines.length * lineHeight + pad * 2;
          var scrollable = Math.max(0, totalH - bh);
          var scroll = 0;
          if (scrollable > 0) {
            var leg = Math.min(6, Math.max(2, lines.length * 0.45));
            var elapsed = (Date.now() - (s._speechStart || Date.now())) / 1000;
            var phase = (elapsed % (2 * leg)) / (2 * leg);
            var tri = phase < 0.5 ? phase * 2 : (1 - (phase - 0.5) * 2);
            var ease = tri * tri * (3 - 2 * tri);
            scroll = scrollable * ease;
          }
          ctx.fillStyle = 'rgba(15,20,35,0.92)';
          rr(bx, by, bw, bh, 6); ctx.fill();
          ctx.strokeStyle = 'rgba(255,255,255,0.25)'; ctx.lineWidth = 1; ctx.stroke();
          ctx.beginPath();
          ctx.moveTo(px - 4, by + bh);
          ctx.lineTo(px, by + bh + 7);
          ctx.lineTo(px + 4, by + bh);
          ctx.closePath();
          ctx.fillStyle = 'rgba(15,20,35,0.92)';
          ctx.fill();
          ctx.fillStyle = '#e8eef6';
          ctx.textBaseline = 'top';
          ctx.save();
          ctx.beginPath();
          ctx.rect(bx, by, bw, bh);
          ctx.clip();
          for (var j = 0; j < lines.length; j++) {
            ctx.fillText(lines[j], bx + pad, by + pad + j * lineHeight - scroll);
          }
          ctx.restore();
          ctx.textBaseline = 'alphabetic';
        }
        var _stepStatusCache = {};
        $scope.$watch(function () {
          var s = vm.streamingSteps;
          if (!s) return 0;
          for (var i = 0; i < s.length; i++) {
            var st = s[i];
            var key = st.index !== undefined && st.index !== null ? st.index : i;
            var status = st.status || 'pending';
            var prev = _stepStatusCache[key] !== undefined ? _stepStatusCache[key] : 'pending';
            if (prev !== status) {
              if (scene) scene._lastStepType = st.type;
              if (status === 'done' || status === 'applied' || status === 'created' || status === 'ok') {
                if (scene && scene.meetingOn && prev !== 'done') {
                  fireReaction('good', pick(REACT_SUCCESS));
                  pushTicker('good', tickerLabelForStep(st));
                }
                if (!_replay) {
                  bumpComplexityRage(3); 
                  var grumpRole = grumpRoleForStepType(st.type);
                  if (grumpRole) bumpSpiderGrump(grumpRole, -5);
                  var _reliefSp = grumpRole ? spiderFor(grumpRole) : null;
                  if (_reliefSp && (_reliefSp.grumpStreak || 0) >= 3) fireGrumpRelief(_reliefSp);
                  if (_reliefSp) _reliefSp.grumpStreak = 0;
                }
                if (!_replay) {
                  var tallySp = spiderForStepType(st.type);
                  if (tallySp) { tallySp.steps = (tallySp.steps || 0) + 1; }
                  if (/verif|review|check/.test(String(st.type || ''))) {
                    var revSp = spiderFor('reviewer');
                    if (revSp) revSp.steps = (revSp.steps || 0) + 1;
                  }
                  syncRivalryFeud('work');
                  syncRivalryFeud('command');
                }
                if (!_replay) {
                  var okSpider = spiderForStepType(st.type);
                  if (okSpider && okSpider.role === 'editor') {
                    if (scene) scene._editFailStreak = 0;
                    bumpEditRage(-10);
                    bumpVerifierSmug(-8);
                    if (vm.showMeeting) {
                      var _hvSp = spiderFor('verifier');
                      if (_hvSp && _hvSp.crownedCarried && (_hvSp.smug || 0) < 40) humbleCarriedCrown(_hvSp);
                    }
                  }
                }
              } else if (status === 'error' || status === 'rejected' || status === 'failed') {
                if (scene && scene.meetingOn) {
                  var failSpider = spiderForStepType(st.type);
                  if (failSpider && failSpider.role === 'editor') {
                    fireReaction('bad', editorRageLine(failSpider.rage || 0));
                    if (!_replay) {
                      scene._editFailStreak = (scene._editFailStreak || 0) + 1;
                      var bump = 6 + Math.min(14, scene._editFailStreak * 3);
                      bumpEditRage(bump);
                      bumpVerifierSmug(bump);
                      var verSpider = spiderFor('verifier');
                      pushTicker('feud',
                        '✏️ Editor ' + Math.round(failSpider.rage || 0) + '% rage ↔ ✅ Verifier ' + (verSpider ? Math.round(verSpider.smug || 0) : 0) + '% smug',
                        (failSpider.rage || 0) >= 100 || (verSpider ? (verSpider.smug || 0) >= 100 : false));
                      if (verSpider) {
                        var gloatText = verifierSmugLine(verSpider.smug || 0);
                        var gloatWho = verSpider.icon + ' ' + verSpider.name;
                        recordEvent({ type: 'chat', who: gloatWho, text: gloatText });
                        logGossipEntry(gloatWho, gloatText);
                      }
                    }
                  } else {
                    fireReaction('bad', pick(REACT_FAIL));
                  }
                  pushTicker('bad', tickerLabelForStep(st));
                }
                if (!_replay) {
                  bumpComplexityRage(10); 
                  var grumpRole = grumpRoleForStepType(st.type);
                  if (grumpRole) {
                    bumpSpiderGrump(grumpRole, 12);
                    var _griefSp = spiderFor(grumpRole);
                    if (_griefSp) _griefSp.grumpStreak = (_griefSp.grumpStreak || 0) + 1;
                  }
                }
              }
            }
            _stepStatusCache[key] = status;
          }
          return s.length;
        });
        $scope.$watch(function () { return vm.streamingSteps ? vm.streamingSteps.length : 0; }, function (len, prev) {
          if (len === 0 && prev > 0) _stepStatusCache = {};
        });
        var _ctxGripedAt = 0;
        $scope.$watch(function () { return vm.streamingContextSize || 0; }, function (size, prev) {
          if (!size || size === prev) return;
          if (!scene || _replay || vm.meetingHovered) return;
          if (!vm.showMeeting) return;
          var sp = spiderFor('complexity');
          if (!sp) return;
          if (scene.writer === sp || scene.gossip || scene.watching) return;
          var bucket = Math.floor(size / 10000);
          if (bucket <= _ctxGripedAt || bucket < 2) return;
          _ctxGripedAt = bucket;
          var vals = complexityVals();
          var text = fmtComplexity(pick(COMPLEXITY_CTX), vals);
          setSpeech(sp, text, 4.5, sp.icon + ' ' + sp.name);
          logGossipEntry(sp.icon + ' ' + sp.name, text);
          scene.lastLogAt = Date.now();
          bumpComplexityRage(10); 
        });
        $scope.$watch(function () { return vm.streamingActive; }, function (val, prev) {
          if (val && !prev) _ctxGripedAt = 0;
        });
        $scope.$watch(function () { return vm.agentActivityLog ? vm.agentActivityLog.length : 0; }, function (len, prev) {
          if (len === undefined) return;
          if (len > (prev || 0)) {
            var log = vm.agentActivityLog;
            for (var i = (prev || 0); i < len; i++) {
              var fromReplay = !!_replay && !vm.streamingActive;
              if (_recording && !_replay) recordEvent({ type: 'log', entry: log[i] });
              handleLogEntry(log[i], fromReplay);
            }
          }
        });
        $scope.$watch('vm.showMeeting', function (val) {
          if (val) { startLoop(); if (vm._restoreMeetingReplayFromCards) vm._restoreMeetingReplayFromCards(); }
          else stopLoop();
        });
        $scope.$watch('vm.streamingActive', function (val, prev) {
          if (!scene) return;
          if (val && !prev) startMeeting();
          if (!val && prev) finishMeeting();
        });
        $scope.$on('$destroy', function () {
          destroyed = true;
          stopLoop();
          stopRankPromotionWatch();
        });
        scene = makeScene();
        if (vm.showMeeting) startLoop();
      }
    };
  }]);
