// meeting.mixin.js
// ─────────────────────────────────────────────────────────────────────────────
// Meeting View — a floating, draggable, resizable panel (same pattern as the
// Notes panel) that renders a canvas "office" where little block spiders
// (one per agent role) animate the live agent run:
//   * When a task starts, every spider walks from its desk to the conference
//     table next to the whiteboard.
//   * Each log entry is routed to the spider whose role matches (planner,
//     explorer, editor, commander, verifier, reviewer). That spider walks to
//     the whiteboard and "writes" the agent's actual text on the board,
//     character by character, with a speech bubble above it.
//   * When the plan completes, the reviewer writes the final verdict, the
//     spiders celebrate, then walk back to their desks.
// ─────────────────────────────────────────────────────────────────────────────
angular.module('kanbanApp')
  .factory('MeetingMixin', ['$timeout', function ($timeout) {
    return {
      init: function (vm, $scope) {
        // ── Panel state (mirrors NotesMixin) ──────────────────────────────
        vm.meeting = {
          left: 240, top: 120, width: 620, height: 430,
          dragging: false, dragStartX: 0, dragStartY: 0,
          resizing: false, resizeDir: '', resizeStartX: 0, resizeStartY: 0,
          resizeStartW: 0, resizeStartH: 0
        };
        vm.meetingSpeaker = '🕷️ the spiders are resting';
        vm.meetingBoardLines = [];

        // ── Spider cast (role → spider) ────────────────────────────────────
        var ROLES = [
          { key: 'planner',  name: 'Planner',   icon: '🧠', color: '#61afef', desk: { x: 0.10, y: 0.52 }, seat: { x: 0.38, y: 0.63 } },
          { key: 'explorer', name: 'Explorer',  icon: '🔍', color: '#56b6c2', desk: { x: 0.06, y: 0.72 }, seat: { x: 0.44, y: 0.63 } },
          { key: 'editor',   name: 'Editor',    icon: '✏️', color: '#98c379', desk: { x: 0.10, y: 0.90 }, seat: { x: 0.50, y: 0.63 } },
          { key: 'commander',name: 'Commander', icon: '🛠', color: '#e5c07b', desk: { x: 0.90, y: 0.52 }, seat: { x: 0.56, y: 0.63 } },
          { key: 'verifier', name: 'Verifier',  icon: '✅', color: '#c678dd', desk: { x: 0.94, y: 0.72 }, seat: { x: 0.62, y: 0.63 } },
          { key: 'reviewer', name: 'Reviewer',  icon: '🏁', color: '#e06c75', desk: { x: 0.90, y: 0.90 }, seat: { x: 0.68, y: 0.63 } }
        ];
        // Board stand: where the writing spider stands (in front of the board).
        var BOARD_STAND = { x: 0.66, y: 0.86 };
        // Board rectangle on the wall (fractions of W/H).
        var BOARD_RECT = { x: 0.60, y: 0.05, w: 0.34, h: 0.20 };
        // Table rectangle on the floor.
        var TABLE_RECT = { x: 0.30, y: 0.56, w: 0.46, h: 0.10 };
        // Water cooler — the gossip hangout, on the floor left of the table
        // (a different spot from the usual meeting place).
        var COOLER = { x: 0.17, y: 0.60 };
        // Spots where the listeners gather around the cooler.
        var COOLER_SPOTS = [
          { x: 0.11, y: 0.64 },
          { x: 0.23, y: 0.64 }
        ];

        var scene = null;      // { spiders:[], boardLines:[], writer:null, queue:[], meetingOn:false, done:false }
        var raf = null;
        var canvas = null;
        var ctx = null;
        var lastTs = 0;
        var destroyed = false;
        var _recording = null; // events captured during a live run (for replay)
        var _replay = null;    // active replay clock { events, t0, elapsed, idx }
        vm.meetingReplay = null;      // last completed run's events
        vm.meetingReplaying = false;  // true while a replay is running
        vm.meetingReplaySpeed = 1;    // replay playback multiplier (1 / 1.5 / 2)
        vm.meetingTicker = [];        // recent step outcomes ({ kind, label })
        vm.gossipLog = [];            // transcribed jokes + gossip ({ t, who, text })

        function makeScene() {
          var spiders = ROLES.map(function (r) {
            return {
              role: r.key, name: r.name, icon: r.icon, color: r.color,
              x: r.desk.x, y: r.desk.y,
              home: { x: r.desk.x, y: r.desk.y },
              target: { x: r.desk.x, y: r.desk.y },
              seat: { x: r.seat.x, y: r.seat.y },
              state: 'idle',            // idle | walk | write | celebrate
              walkPhase: Math.random() * 6.28,
              speed: 0.5 + Math.random() * 0.3,
              text: '', progress: 0,    // whiteboard write progress
              speech: '', speechTtl: 0,
              celebrateT: 0,
              reactT: 0, reactKind: '', // brief reaction hop / tremble
              waveT: 0, wavePhase: Math.random() * 6.28 // waving at the user
            };
          });
          return {
            spiders: spiders,
            boardLines: [],   // { role, color, text, progress, done }
            writer: null,
            queue: [],        // { role, text }
            meetingOn: false,
            done: false,
            activeRole: 'planner',   // last role that spoke/wrote
            lastLogAt: Date.now(),   // for lull detection
            streamReadCd: 0,         // throttle for reading the LLM stream aloud
            lastStreamLen: 0,        // last stream buffer length we read
            banterCd: 2,             // countdown until the next joke
            gossip: null,            // active water-cooler gossip sequence
            gossipCd: 12,            // countdown until next gossip moment
            reactLockT: 0,           // reactions pause stream-reading/jokes briefly
            _lastStepType: '',       // step type for reaction routing
            confetti: [],            // celebration particles ({ x, y, vx, vy, rot, vr, color, size, life, ttl })
            watching: null,          // active 'the user is watching' skit
            watchingCd: 8            // cooldown before the next watching skit
          };
        }

        function spiderFor(role) {
          if (!scene) return null;
          for (var i = 0; i < scene.spiders.length; i++) {
            if (scene.spiders[i].role === role) return scene.spiders[i];
          }
          return null;
        }

        // ── Office banter (keeps the spiders alive during lulls) ────────────
        // Spiders crack jokes while the LLM is thinking (it is usually slow)
        // and riff on the office when nothing is happening at all.
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
          "I'd tap my foot, but all 8 of them are asleep."
        ];
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
          "I swear this office chair is more cushioned than my ego."
        ];
        // Context-aware banter — templates referencing the LIVE run state:
        // the file being edited, the LLM endpoint in use, and the plan's
        // step progress. {file} {endpoint} {done} {total} {current} {left}
        // are filled in from the view-model at joke time.
        // Context-aware banter is role-specific: each spider only jokes about
        // its own turf. The editor talks files, the commander talks endpoints,
        // the planner talks step counts — so the joke always comes from the
        // spider whose job it actually is.
        var BANTER_FILE = [   // Editor: file-focused
          "Working on {file} right now. It's looking at me. I'm looking at it. Someone blinks first.",
          "The user wants changes in {file}. Bold of them. I respect it.",
          "If {file} had feelings, I think it would be nervous right now.",
          "The plan says we touch {file} next. The file doesn't know yet. Don't tell it.",
          "We've edited {done} file(s). The user keeps asking for more. I respect the ambition.",
          "{file} is the file of the hour. It didn't ask for this fame.",
          "Step {current} of {total}. {file} is next on the chopping block.",
          "I've been staring at {file} for a while now. We're best friends. It doesn't know yet."
        ];
        var BANTER_ENDPOINT = [ // Commander: endpoints / commands
          "Hitting {endpoint} again. That model owes me {done} successful step(s).",
          "{endpoint} is humming along. {done} step(s) done, all the way to step {current}.",
          "{current} step(s) in on {endpoint}. The other spiders owe me lunch.",
          "{endpoint} is thinking about {file} and honestly? Same.",
          "Sending this to {endpoint}. If it comes back broken, it's the model's fault. Always is.",
          "{endpoint} and I have an arrangement: it thinks, I point. Mostly it thinks."
        ];
        var BANTER_STEPS = [   // Planner: step counts
          "We're {done} step(s) deep into a {total}-step plan. The user is watching. No pressure.",
          "{done} down, {left} to go. The whiteboard is starting to look like a real plan.",
          "This is a {total}-step plan and we've done {done}. Math checks out. Mostly.",
          "On step {current} of {total} and nobody has panicked yet. Beautiful.",
          "{done} step(s) done. Only {left} left. We're basically done. Mostly.",
          "I planned {total} step(s). The others execute them. I take full credit. Fair trade."
        ];
        // Water-cooler gossip — one spider brags about the user's stats and
        // the others react (they're VERY impressed by mundane numbers).
        var GOSSIP_OPENERS = [
          "Okay, hot off the press — you won't believe what the user did.",
          "Gather 'round, have I got news about the user.",
          "So I was watching the user and… you need to hear this.",
          "The user just did something legendary. Sit down. You're already sitting. Good."
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
        // Run-context gossip — the bragger also spills what the agent JUST did:
        // steps completed, the file that was just edited, the endpoint it ran
        // on. These are dynamic (string values, not counts), so they carry a
        // fmt fn that slots the live context in.
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
          "I'm calling the web. This is big."
        ];
        var GOSSIP_ENDINGS = [
          "Anyway, that's the user. Absolute legend.",
          "We're basically working for a superstar.",
          "If you need me, I'll be here processing that.",
          "I'm going to need a moment. And maybe a drink.",
          "Just… let that sink in. Deeply."
        ];
        // ── Seasonal office ────────────────────────────────────────────────
        // The office decorates itself and the water-cooler gossip turns into
        // holiday roasts depending on the current date.
        function currentSeason() {
          var now = new Date();
          var m = now.getMonth() + 1; // 1-12
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
        // Step-outcome reactions — spiders respond to what ACTUALLY happened
        // in the run: a cheer when a step lands clean, a worried gulp when an
        // edit fails or gets rejected.
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
          "Chef's kiss."
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
          "I felt that one in my 8 legs."
        ];
        // Fake but fun ranks based on live user stats.
        var RANK_TITLES = [
          { min: 100, title: 'Grand Architect of Everything' },
          { min: 50, title: 'Supreme Code Commander' },
          { min: 25, title: 'Certified Power User' },
          { min: 10, title: 'Respected Contributor' },
          { min: 3, title: 'Rising Star' },
          { min: 0, title: 'Legend in Training' }
        ];

        function pick(arr) { return arr[Math.floor(Math.random() * arr.length)]; }
        function basename(p) {
          var s = String(p || '').replace(/\\/g, '/');
          return s.split('/').pop() || s;
        }
        // ── Live run context for banter ────────────────────────────────────
        // Collects the file being worked on, the LLM endpoint in use, and the
        // plan's step progress straight from the view-model so the jokes are
        // about what is ACTUALLY happening right now.
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
          // Never claim a nonexistent step zero when the plan has real steps.
          if (total > 0 && current < 1) current = 1;
          return { file: file, endpoint: endpoint, done: done, total: total, current: current, left: Math.max(0, total - done) };
        }
        // Replaces {placeholders} in a banter template with live context.
        function fmtBanter(tpl, ctx) {
          return tpl
            .replace(/\{file\}/g, ctx.file || 'this file')
            .replace(/\{endpoint\}/g, ctx.endpoint || 'the endpoint')
            .replace(/\{done\}/g, ctx.done)
            .replace(/\{total\}/g, ctx.total || '?')
            .replace(/\{current\}/g, ctx.current)
            .replace(/\{left\}/g, ctx.left);
        }
        function randomSpider() {
          if (!scene || !scene.spiders.length) return null;
          return scene.spiders[Math.floor(Math.random() * scene.spiders.length)];
        }
        // Puts a speech bubble + status-bar speaker on a spider (outside digest).
        function setSpeech(spider, text, ttl, speaker) {
          if (!spider) return;
          spider.speech = text;
          spider.speechTtl = ttl;
          if (speaker) vm.meetingSpeaker = speaker;
          $scope.$applyAsync();
        }

        // ── Run recording / replay ────────────────────────────────────────
        // While a run is live we capture a timeline of events (start, log
        // entries, stream-reading bubbles, step reactions, finish). After the
        // run ends the timeline is kept in vm.meetingReplay so the ▶ button can
        // rewind the whole run through the same spider animation.
        function recordEvent(ev) {
          if (_recording && !_replay) {
            ev.t = Date.now();
            _recording.push(ev);
          }
        }

        // ── Step-outcome reactions ────────────────────────────────────────
        // Map a step type to the spider whose turf it is, so the editor cheers
        // for a clean edit, the commander for a command, etc.
        function spiderForStepType(type) {
          var t = String(type || '');
          if (/command|terminal|build|test/.test(t)) return spiderFor('commander');
          if (/explore/.test(t)) return spiderFor('explorer');
          if (/plan/.test(t)) return spiderFor('planner');
          if (/verif|review|check/.test(t)) return spiderFor('verifier') || spiderFor('reviewer');
          if (/edit|create|rename|sql|write|delete/.test(t)) return spiderFor('editor');
          return null;
        }

        // React to a step's outcome: cheer on success, worry on failure. The
        // reaction is a short speech bubble + a hop/tremble, distinct from the
        // board-writing flow, and it briefly pauses stream-reading/jokes so it
        // gets the spotlight.
        function fireReaction(kind, text) {
          if (!scene || scene.done) return;
          if (!_replay) recordEvent({ type: 'reaction', kind: kind, text: text });
          if (scene.gossip) endGossipNow();
          var spider = randomSpider();
          // Prefer the spider matching the step, but never stomp the writer.
          var byStep = spiderForStepType(scene._lastStepType);
          if (byStep && byStep !== scene.writer) spider = byStep;
          if (!spider) return;
          var emoji = kind === 'good' ? '🙌' : '😬';
          setSpeech(spider, emoji + ' ' + text, 3.5, spider.icon + ' ' + spider.name + (kind === 'good' ? ' — step landed' : ' — step failed'));
          spider.reactT = 1.2;
          spider.reactKind = kind; // 'good' → hop, 'bad' → tremble
          scene.reactLockT = 3.2;
          scene.lastLogAt = Date.now(); // reactions are "alive" time too
          // Tiny audio cue: a soft ding-pop for wins, a low womp for fails.
          // fireReaction is driven by watches that fire even while the panel is
          // closed, so gate the sound on visibility like playChime.
          if (vm.showMeeting) {
            if (kind === 'good') playDing();
            else playWomp();
          }
        }

        // ── Sound effects (WebAudio synth — no external files) ─────────────
        // Soft tick while a spider writes on the board, gentle whoosh while
        // they walk, and a little chime when the plan completes. All sounds
        // are generated with oscillators/noise buffers, and can be muted via
        // the header button (persisted in localStorage).
        vm.meetingHovered = false;  // true while the mouse is over the panel
        var _audioCtx = null;
        vm.meetingMuted = false;
        try { vm.meetingMuted = window.localStorage.getItem('weaver.meeting.muted') === '1'; } catch (e) { }
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
        function sfx() { return !vm.meetingMuted ? audioCtx() : null; }
        function playTick() {
          var ctx = sfx(); if (!ctx) return;
          try {
            var t = ctx.currentTime;
            var osc = ctx.createOscillator();
            var gain = ctx.createGain();
            osc.type = 'square';
            osc.frequency.value = 1250;
            gain.gain.setValueAtTime(0.025, t);
            gain.gain.exponentialRampToValueAtTime(0.0001, t + 0.05);
            osc.connect(gain); gain.connect(ctx.destination);
            osc.start(t); osc.stop(t + 0.05);
          } catch (e) { }
        }
        function playWhoosh() {
          var ctx = sfx(); if (!ctx) return;
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
            src.connect(filter); filter.connect(gain); gain.connect(ctx.destination);
            src.start(t);
          } catch (e) { }
        }
        function playChime() {
          var ctx = sfx(); if (!ctx) return;
          try {
            var t = ctx.currentTime;
            var notes = [523.25, 659.25, 783.99]; // C5 E5 G5
            for (var i = 0; i < notes.length; i++) {
              var osc = ctx.createOscillator();
              var gain = ctx.createGain();
              osc.type = 'sine';
              osc.frequency.value = notes[i];
              var st = t + i * 0.12;
              gain.gain.setValueAtTime(0, st);
              gain.gain.linearRampToValueAtTime(0.06, st + 0.02);
              gain.gain.exponentialRampToValueAtTime(0.0001, st + 0.6);
              osc.connect(gain); gain.connect(ctx.destination);
              osc.start(st); osc.stop(st + 0.65);
            }
          } catch (e) { }
        }
        function playDing() {
          var ctx = sfx(); if (!ctx) return;
          try {
            var t = ctx.currentTime;
            // Bright pop: two quick sine partials that decay fast.
            [{ freq: 1318.5, gain: 0.07, delay: 0 }, { freq: 1975.5, gain: 0.045, delay: 0.05 }].forEach(function (spec) {
              var osc = ctx.createOscillator();
              var g = ctx.createGain();
              osc.type = 'sine';
              osc.frequency.value = spec.freq;
              var st = t + spec.delay;
              g.gain.setValueAtTime(0, st);
              g.gain.linearRampToValueAtTime(spec.gain, st + 0.012);
              g.gain.exponentialRampToValueAtTime(0.0001, st + 0.18);
              osc.connect(g); g.connect(ctx.destination);
              osc.start(st); osc.stop(st + 0.2);
            });
          } catch (e) { }
        }
        function playWomp() {
          var ctx = sfx(); if (!ctx) return;
          try {
            var t = ctx.currentTime;
            // Low descending wobble: a sine that slides down with a soft thump.
            var osc = ctx.createOscillator();
            var gain = ctx.createGain();
            osc.type = 'sine';
            osc.frequency.setValueAtTime(165, t);
            osc.frequency.exponentialRampToValueAtTime(98, t + 0.28);
            gain.gain.setValueAtTime(0.07, t);
            gain.gain.exponentialRampToValueAtTime(0.0001, t + 0.32);
            osc.connect(gain); gain.connect(ctx.destination);
            osc.start(t); osc.stop(t + 0.34);
          } catch (e) { }
        }
        vm.toggleMeetingMute = function () {
          vm.meetingMuted = !vm.meetingMuted;
          // Instant local cache, plus persist to saved settings so the
          // preference syncs across devices (same pattern as showMeeting).
          try { window.localStorage.setItem('weaver.meeting.muted', vm.meetingMuted ? '1' : '0'); } catch (e) { }
          if (vm.saveSettings) vm.saveSettings(true);
          // Unmuting is a click gesture — wake the context so sounds resume.
          if (!vm.meetingMuted) audioCtx();
        };

        // ── Gossip log ─────────────────────────────────────────────────────
        // A small scrolling feed at the bottom of the panel that transcribes
        // every joke and gossip line, so users catch the funny stuff even when
        // they look away. Capped so the DOM stays light.
        function logGossipEntry(who, text) {
          var clean = String(text || '').replace(/[\u{1F300}-\u{1FAFF}]/gu, '').trim();
          if (!clean) return;
          vm.gossipLog.push({ t: Date.now(), who: who, text: clean });
          if (vm.gossipLog.length > 30) vm.gossipLog.shift();
          $scope.$applyAsync();
          // Auto-scroll the feed so the newest line stays visible even when
          // the user looked away (deferred until the DOM re-renders).
          try {
            $timeout(function () {
              var feed = document.getElementById('meetingGossipFeed');
              if (feed) feed.scrollTop = feed.scrollHeight;
            }, 0);
          } catch (e) { }
        }
        vm.gossipTimeLabel = function (ts) {
          var d = new Date(ts);
          return ('0' + d.getHours()).slice(-2) + ':' + ('0' + d.getMinutes()).slice(-2) + ':' + ('0' + d.getSeconds()).slice(-2);
        };

        // ── Step-outcome ticker ────────────────────────────────────────────
        // A small scrolling strip at the bottom of the panel showing the last
        // few step results ('✓ edit kanban.html', '✗ command failed') so users
        // can read the reaction history at a glance.
        function pushTicker(kind, label) {
          var text = (kind === 'good' ? '✓ ' : '✗ ') + label;
          // Skip exact duplicates in a row (e.g. reconnects re-reporting a step).
          var last = vm.meetingTicker[vm.meetingTicker.length - 1];
          if (last && last.label === text) return;
          vm.meetingTicker.push({ kind: kind, label: text });
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

        // ── Celebration confetti ───────────────────────────────────────────
        // When a task completes, a burst of tiny colored particles rains down
        // across the canvas before the spiders head home. Position/velocity are
        // in normalized 0..1 scene units so the burst looks right at any size.
        var CONFETTI_COLORS = ['#61afef', '#98c379', '#e5c07b', '#c678dd', '#e06c75', '#56b6c2', '#ffd866', '#f97583', '#7ee787', '#79c0ff'];
        function spawnConfetti() {
          if (!scene) return;
          scene.confetti = [];
          var n = 110;
          for (var i = 0; i < n; i++) {
            var fromLeft = Math.random() < 0.5;
            scene.confetti.push({
              // Burst from the table/board region outward.
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

        // ── Live user stats for the water-cooler gossip ────────────────────
        // Real numbers pulled straight off the view-model so the bragging is
        // genuinely about THIS user's actual state — including real agent
        // performance: cards finished this week, benchmark averages, and total
        // points earned.
        function collectUserStats() {
          // Cards finished this week: done + archived cards created within the
          // last 7 days.
          var weekAgo = Date.now() - 7 * 86400000;
          function countThisWeek(list) {
            if (!list) return 0;
            var n = 0;
            for (var i = 0; i < list.length; i++) {
              var c = list[i];
              // Prefer the completion stamp (set when the card moved to done)
              // over the creation date — a card created weeks ago and finished
              // today IS a this-week win.
              var stamp = c && (c.finishedAt || c.doneAt || c.createdAt);
              var t = stamp ? new Date(stamp).getTime() : 0;
              if (t && t >= weekAgo) n++;
            }
            return n;
          }
          var doneThisWeek = countThisWeek(vm.state && vm.state.done) + countThisWeek(vm.state && vm.state.archived);

          // Benchmark aggregates over local scores (falling back to server
          // benchmarks if the local list hasn't been loaded this session).
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

        // Daily streak, tracked locally (this is what the rank is partly based on).
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

        function userRankTitle(st) {
          var score = (st.done || 0) + (st.benchmarks || 0) * 2 + (st.projects || 0) * 3 + (st.archived || 0) * 0.5
            + Math.round((st.bestScore || 0) / 10) + Math.min(20, (st.totalPoints || 0) / 50);
          for (var i = 0; i < RANK_TITLES.length; i++) {
            if (score >= RANK_TITLES[i].min) return RANK_TITLES[i].title;
          }
          return 'Legend in Training';
        }

        // ── Water-cooler gossip skit ───────────────────────────────────────
        // One spider strolls to the cooler and brags about the user's stats
        // while two others gather and react — as impressed by a tab count as
        // by an entire architecture.
        function startGossip() {
          if (!scene || scene.gossip) return;
          var bragger = randomSpider();
          if (!bragger) return;
          var others = scene.spiders.filter(function (s) { return s !== bragger; });
          var a = others[Math.floor(Math.random() * others.length)];
          var rest = others.filter(function (s) { return s !== a; });
          var b = rest[Math.floor(Math.random() * rest.length)];
          var listeners = [a, b];

          // Everyone strolls over to the cooler.
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
          // Run context (steps done, last file, endpoint) so the gossip can
          // reflect what the agent JUST did, not just lifetime user stats.
          var ctx = currentContext();

          // Start with the run's own story (up to 2 run stats, whichever are
          // actually available), then fill with ~2-4 lifetime stats. Run stats
          // only count when a run REALLY happened — currentContext() falls back
          // to the configured default endpoint even with no run, so gate on
          // actual run evidence (currentRun or streaming steps) to avoid
          // inventing a task that never ran.
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
          // Seasonal twist: swap the gossip's openers/reactions/endings for the
          // current holiday's lines and add a holiday roast of the user.
          var season = currentSeason();
          var sOpen = GOSSIP_OPENERS, sReact = GOSSIP_REACTIONS, sEnd = GOSSIP_ENDINGS, sRoast = null;
          if (season && SEASON_GOSSIP[season]) {
            var sg = SEASON_GOSSIP[season];
            sOpen = sg.openers; sReact = sg.reactions; sEnd = sg.endings;
            if (sg.roasts && sg.roasts.length) sRoast = pick(sg.roasts);
          }

          lines.push({ spider: bragger, text: pick(sOpen), ttl: 2.8 });
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
          // The holiday roast lands as the punchline before the rank reveal.
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
          // leave — everyone wanders back to their desks (or seats if a
          // meeting is in progress).
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

        // Real work interrupts the gossip — everyone scrambles away. The
        // freshly-assigned writer must keep heading to the board, so skip it.
        function endGossipNow() {
          if (!scene || !scene.gossip) return;
          var g = scene.gossip;
          var targetGetter = scene.meetingOn
            ? function (s) { return s.seat; }
            : function (s) { return s.home; };
          [g.bragger].concat(g.listeners).forEach(function (s) {
            if (scene.writer === s) return; // never hijack the active writer
            s.state = 'walk';
            s.target = { x: targetGetter(s).x, y: targetGetter(s).y };
            s.speech = ''; s.speechTtl = 0;
          });
          scene.gossip = null;
          scene.gossipCd = 30 + Math.random() * 20;
        }

        // ── 'The user is watching' skit ───────────────────────────────────
        // When the mouse is over the panel, the spiders notice: they drop the
        // gossip, wave, look toward the user, and compliment them directly.
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
          if (scene.writer || scene.queue.length) return; // never interrupt real work
          if (scene.gossip) endGossipNow(); // drop the gossip — they're watching!
          var star = randomSpider();
          if (!star) return;
          // Everyone notices and waves.
          scene.spiders.forEach(function (s) {
            if (s === scene.writer) return;
            s.waveT = 2.2 + Math.random() * 1.2;
            s.wavePhase = Math.random() * 6.28;
            if (s.state === 'idle') {
              s.speech = pick(WATCHING_WAVES);
              s.speechTtl = 1.5 + Math.random() * 1;
            }
          });
          // The star strolls toward the front (bottom-center) to address the user.
          star.state = 'walk';
          star.target = { x: 0.5, y: 0.92 };
          star.speech = ''; star.speechTtl = 0;
          // Pick 3 distinct compliments so the skit never repeats itself.
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
          // Keep the audience waving throughout.
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
          // leave — the star heads back to its spot.
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
          // Clear the audience's wave bubbles too — no lingering emoji after
          // the user looks away.
          scene.spiders.forEach(function (s) {
            if (s !== scene.writer && /^(👋|🖐️|🙋|🙆|☝️)/.test(s.speech || '')) {
              s.speech = ''; s.speechTtl = 0;
            }
          });
          scene.watching = null;
          scene.watchingCd = 15;
          $scope.$applyAsync();
        }

        // ── Public methods ─────────────────────────────────────────────────
        vm.openMeeting = function () {
          vm.showMeeting = true; vm.saveSettings(true);
          // Prime the AudioContext from a real click gesture so browser
          // autoplay policy doesn't suspend it (rAF-created contexts start
          // suspended and resume() fails outside a gesture).
          audioCtx();
          startLoop();
        };
        vm.closeMeeting = function () { vm.showMeeting = false; vm.saveSettings(true); stopLoop(); };
        vm.setMeetingHovered = function (on) {
          vm.meetingHovered = !!on;
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
            vm.meeting.left = Math.max(0, e.clientX - vm.meeting.dragStartX);
            vm.meeting.top = Math.max(0, e.clientY - vm.meeting.dragStartY);
            $scope.$apply();
          };
          var onUp = function () {
            vm.meeting.dragging = false;
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
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
          };
          document.addEventListener('mousemove', onMove);
          document.addEventListener('mouseup', onUp);
        };

        vm.resetMeeting = function () {
          scene = makeScene();
          vm.meetingSpeaker = '🕷️ the spiders are back at their desks';
          if (canvas && ctx) drawFrame();
        };

        // ── Replay the last agent run ─────────────────────────────────────
        // Plays the captured timeline through the same animation: spiders walk
        // to the table, write each log entry on the board in order, react to
        // step outcomes, and celebrate when the finish event lands.
        vm.cycleMeetingReplaySpeed = function () {
          var speeds = [1, 1.5, 2];
          var i = speeds.indexOf(vm.meetingReplaySpeed);
          if (i < 0) i = speeds.length - 1;
          vm.meetingReplaySpeed = speeds[(i + 1) % speeds.length];
          $scope.$applyAsync();
        };

        vm.replayMeeting = function () {
          if (vm.meetingReplaying) { stopReplay(); return; }
          if (!vm.meetingReplay || !vm.meetingReplay.length) return;
          if (vm.streamingActive) return; // don't replay over a live run
          vm.meetingReplaySpeed = 1; // each replay starts at normal speed
          scene = makeScene();
          scene.meetingOn = true;
          scene.boardLines = [];
          scene.queue = [];
          scene.writer = null;
          scene.spiders.forEach(function (s) {
            s.state = 'walk';
            s.target = { x: s.seat.x, y: s.seat.y };
            s.speechTtl = 0; s.text = ''; s.progress = 0; s.reactT = 0;
          });
          _replay = {
            events: vm.meetingReplay,
            t0: vm.meetingReplay[0].t,
            elapsed: 0,
            idx: 0,
            total: vm.meetingReplay[vm.meetingReplay.length - 1].t - vm.meetingReplay[0].t
          };
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

        // ── Replay scrubber ────────────────────────────────────────────────
        // A progress bar under the canvas showing replay position. Clicking
        // anywhere on it seeks: the office is reset, the board is rebuilt
        // instantly up to the target time, and the timeline clock jumps there
        // so the feed resumes from that moment.
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
          if (vm.streamingActive) return; // never seek over a live run
          if (!vm.meetingReplay || !vm.meetingReplay.length) return;
          // Clicking the scrubber when idle starts a replay from that position.
          if (!_replay) {
            vm.replayMeeting();
            if (!_replay) return;
          }
          var rect = $event.currentTarget.getBoundingClientRect();
          if (!rect.width) return;
          var frac = Math.max(0, Math.min(1, ($event.clientX - rect.left) / rect.width));
          var target = frac * _replay.total;

          // Reset the office to a fresh run state so the board and spiders
          // reflect the seek point rather than the old position.
          scene = makeScene();
          scene.meetingOn = true;
          scene.done = false;
          scene.boardLines = [];
          scene.queue = [];
          scene.writer = null;
          scene.confetti = []; // clear leftover confetti from a previous run
          scene.activeRole = 'planner';
          scene.lastLogAt = Date.now();
          scene.spiders.forEach(function (s) {
            s.state = 'walk';
            s.target = { x: s.seat.x, y: s.seat.y };
            s.speechTtl = 0; s.text = ''; s.progress = 0; s.reactT = 0;
          });

          // Rebuild board state instantly: replay all events up to the target
          // time, writing completed board lines directly (no animation).
          var evs = _replay.events;
          var idx = 0;
          for (; idx < evs.length; idx++) {
            var ev = evs[idx];
            if (ev.t - _replay.t0 > target) break;
            if (ev.type === 'start') {
              // no-op: scene is already freshly started
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
            // reaction / stream events are transient bubbles — skipped on seek
          }

          _replay.elapsed = target;
          _replay.idx = idx;
          vm.meetingSpeaker = '⏩ seeking — ' + fmtDuration(target) + ' / ' + fmtDuration(_replay.total);
          updateScrubber();
          $scope.$applyAsync();
        };

        // ── Agent-log feed ─────────────────────────────────────────────────
        // The agent pipeline funnels every event through vm.agentActivityLog
        // (via pushAgentLog). We watch it and route each entry to a spider.
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
            return 'editor'; // edit / create / rename / sql
          }
          if (level === 'think') return /verif|verification/.test(m) ? 'verifier' : 'planner';
          if (level === 'summary') return 'verifier';
          if (level === 'log') return 'reviewer';
          if (level === 'error') return 'reviewer';
          if (level === 'warn') return /reject/.test(m) ? 'planner' : 'reviewer';
          // info / metric / bypass / status fall through to keyword scan
          if (/proposing|meta-plan|complexity|plan/.test(m)) return 'planner';
          if (/exploring|context review/.test(m)) return 'explorer';
          if (/cohesion/.test(m)) return 'verifier';
          if (/running on endpoint|endpoint/.test(m)) return 'commander';
          if (/agent started/.test(m)) return 'planner';
          return null;
        }

        // Derive the { role, text } a log entry would write on the board — used
        // both by the live pipeline and by replay seeking (which rebuilds the
        // board instantly up to the seek point). Returns null when nothing
        // should be written.
        function logBoardText(entry) {
          var msg = entry && entry.message ? String(entry.message) : '';
          var level = entry && entry.level ? String(entry.level) : 'info';
          var role = roleForEntry(level, msg);
          if (!role) return null;
          var text = stripPrefix(msg);
          if (!text) return null;
          // Rich detail (thinking / plan text) is more interesting on the board.
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

        function handleLogEntry(entry, fromReplay) {
          if (!scene) return;
          var msg = entry && entry.message ? String(entry.message) : '';
          var low = msg.toLowerCase();

          // ── Meeting lifecycle markers ──
          // During a replay these are handled by the explicit start/finish
          // events in the timeline, so skip them (startMeeting/finishMeeting
          // would reset the replay state).
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
        }

        function enqueueWrite(role, text) {
          if (!scene) return;
          if (scene.done) return;
          scene.queue.push({ role: role, text: text });
          if (scene.queue.length > 6) scene.queue.shift(); // don't overload
          pumpQueue();
        }

        function pumpQueue() {
          if (!scene || scene.writer) return; // one writer at a time
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
          spider._lastTickChar = 0; // reset write-sound boundary for this write
          spider.speech = spider.icon + ' ' + spider.name + ': ' + job.text;
          spider.speechTtl = 6;
          vm.meetingSpeaker = spider.icon + ' ' + spider.name;
          $scope.$applyAsync();
        }

        // ── Lifecycle: start / finish meeting ──────────────────────────────
        function startMeeting(fromReplay) {
          if (!scene) scene = makeScene();
          // A fresh live run cancels any in-flight replay.
          if (_replay && !fromReplay) {
            _replay = null;
            vm.meetingReplaying = false;
          }
          // Begin a new timeline capture (only once per run).
          if (!fromReplay && !_recording) {
            _recording = [];
            _recording.push({ t: Date.now(), type: 'start' });
          }
          scene.meetingOn = true;
          scene.done = false;
          scene.boardLines = [];
          scene.queue = [];
          scene.writer = null;
          scene.confetti = []; // clear leftover confetti from a previous run
          // Only a fresh LIVE run resets the ticker — replays reuse the last
          // live history so the rewatch keeps the step outcomes visible.
          if (!fromReplay) vm.meetingTicker = [];
          scene.activeRole = 'planner';
          scene.lastLogAt = Date.now();
          scene.streamReadCd = 0;
          scene.banterCd = 2;
          scene.gossip = null;
          scene.gossipCd = 12;
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
          // Close out the timeline capture and keep it for the replay button.
          if (!fromReplay && _recording) {
            _recording.push({ t: Date.now(), type: 'finish' });
            vm.meetingReplay = _recording.slice();
            _recording = null;
          }
          // Little celebration chime when the plan completes. Only play when
          // the panel is actually visible — finishMeeting is driven by the
          // log/streaming watches, which fire even while the panel is closed.
          if (vm.showMeeting) playChime();
          // Let the reviewer write the verdict on the board. enqueueWrite bails
          // once scene.done is true, so flip the flag AFTER enqueueing.
          var reviewer = spiderFor('reviewer');
          if (reviewer) {
            enqueueWrite('reviewer', '✅ Plan looks good — task complete!');
          }
          scene.done = true;
          // Confetti burst across the whole canvas — every spider throws a
          // handful before heading back to their desk.
          spawnConfetti();
          // Everyone celebrates and walks home shortly after.
          scene.spiders.forEach(function (s) {
            if (s.role !== 'reviewer') {
              s.state = 'celebrate';
              s.celebrateT = 0.9 + Math.random() * 0.8;
              s.speech = '🎉';
              s.speechTtl = 2.5;
            }
          });
          vm.meetingSpeaker = '🏁 task complete — confetti! the spiders are heading home';
          $scope.$applyAsync();
        }

        // ── Animation loop ─────────────────────────────────────────────────
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
        }

        function tick(ts) {
          if (destroyed || !vm.showMeeting) { stopLoop(); return; }
          raf = requestAnimationFrame(tick);
          if (!lastTs) { lastTs = ts; return; }
          var dt = Math.min(0.05, (ts - lastTs) / 1000);
          lastTs = ts;
          // During a replay, scale the whole simulation by the chosen speed so
          // the timeline clock, spider walking, and board typing all stay
          // proportionally in sync (fast-forward without desync).
          if (_replay && vm.meetingReplaySpeed > 1) dt *= vm.meetingReplaySpeed;
          updateScene(dt);
          drawFrame();
        }

        function updateScene(dt) {
          if (!scene) return;

          // ── Replay: feed the recorded timeline on a clock ───────────────
          if (_replay) {
            _replay.elapsed += dt;
            updateScrubber();
            var evs = _replay.events;
            while (_replay.idx < evs.length && _replay.elapsed >= (evs[_replay.idx].t - _replay.t0)) {
              var ev = evs[_replay.idx++];
              if (ev.type === 'start') startMeeting(true);
              else if (ev.type === 'finish') finishMeeting(true);
              else if (ev.type === 'reaction') fireReaction(ev.kind, ev.text);
              else if (ev.type === 'stream') {
                // Replay the LLM stream-reading bubble on the same spider role.
                // Skip the active writer so a stream bubble never stomps the
                // board write that's currently on screen.
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

          // Gentle whoosh while spiders walk (throttled so it doesn't
          // spam on every frame — at most ~3 per second).
          scene._whooshCd = (scene._whooshCd === undefined || scene._whooshCd === null) ? 0 : scene._whooshCd - dt;
          var anyWalking = false;
          scene.spiders.forEach(function (s) {
            if (s.state === 'walk') anyWalking = true;
          });
          if (anyWalking && scene._whooshCd <= 0) {
            playWhoosh();
            scene._whooshCd = 0.33;
          }

          // Confetti physics: gentle rise, then gravity pulls the pieces down
          // with a little drift + spin. Fade out at the end of their life.
          if (scene.confetti && scene.confetti.length) {
            for (var ci = scene.confetti.length - 1; ci >= 0; ci--) {
              var p = scene.confetti[ci];
              p.life += dt;
              if (p.life >= p.ttl) { scene.confetti.splice(ci, 1); continue; }
              p.vy += 0.22 * dt;              // gravity
              p.vx *= (1 - 0.6 * dt);         // air drag
              p.x += p.vx * dt;
              p.y += p.vy * dt;
              p.rot += p.vr * dt;
              if (p.y > 1.05 && p.vy > 0) p.vy *= -0.5; // gentle floor bounce
            }
          }

          // Move spiders toward targets.
          scene.spiders.forEach(function (s) {
            if (s.state === 'walk') {
              var dx = s.target.x - s.x;
              var dy = s.target.y - s.y;
              var dist = Math.sqrt(dx * dx + dy * dy);
              if (dist < 0.012) {
                s.x = s.target.x; s.y = s.target.y;
                if (scene.writer === s) { s.state = 'write'; s.progress = 0; }
                else { s.state = 'idle'; }
              } else {
                // Clamp step to the remaining distance so spiders never
                // overshoot and jitter forever around their target — matters
                // at replay speeds >1× where dt (and therefore step) is larger.
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

          // Writer types on the board.
          if (scene.writer && scene.writer.state === 'write') {
            var w = scene.writer;
            w.progress += dt * 42; // chars per second
            // Soft tick as the marker writes — throttled via integer
            // character boundary so it sounds like deliberate typing.
            var wroteChar = Math.floor(w.progress);
            if (wroteChar !== (w._lastTickChar || 0)) {
              if (wroteChar > (w._lastTickChar || 0) && wroteChar % 2 === 0) playTick();
              w._lastTickChar = wroteChar;
            }
            if (w.progress >= w.text.length) {
              w.progress = w.text.length;
              scene.boardLines.push({ role: w.role, color: w.color, text: w.text, progress: w.text.length });
              if (scene.boardLines.length > 8) scene.boardLines.shift();
              scene.writer = null;
              w.state = 'walk';
              w.target = { x: w.seat.x, y: w.seat.y };
              w.speech = '';
              w.speechTtl = 0;
              pumpQueue();
            }
          }

          // Writer is still walking to the board — keep speech alive.
          if (scene.writer && scene.writer.state === 'walk') {
            scene.writer.speechTtl = Math.max(scene.writer.speechTtl, 2);
          }

          // ── Alive time: read the LLM stream aloud + crack jokes ──────────
          if (scene.reactLockT > 0) scene.reactLockT -= dt;
          // During a replay the timeline is driving the action — skip the
          // spontaneous stream-reading/jokes so the rewatch stays faithful.
          if (!_replay && !scene.writer && !scene.watching && scene.reactLockT <= 0) {
            var streaming = !!vm.streamingActive;
            var buf = vm.streamingTokenBuffer || '';

            // While text is streaming, the active spider reads it out loud
            // (throttled, showing the latest chunk in its chat bubble). Only
            // re-read when the buffer actually grew — otherwise a token pause
            // would repeat the same tail over and over.
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
                scene.streamReadCd = 1.1; // ~1.1s per bubble keeps it lively
              }
              scene.lastStreamLen = buf.length;
            }

            // Jokes during lulls: no new log for a while and nothing queued.
            // While streaming, the stream-reader takes priority, so jokes wait
            // until the stream stalls for a bit — avoids stomping the bubble.
            scene.banterCd = (scene.banterCd === undefined || scene.banterCd === null) ? 2 : scene.banterCd;
            scene.banterCd -= dt;
            if (scene.banterCd <= 0 && !scene.gossip) {
              var idleFor = (Date.now() - (scene.lastLogAt || 0)) / 1000;
              var streamStalled = !streaming || !newStream;
              if (idleFor > 5 && streamStalled && !scene.queue.length) {
                var ctx = currentContext();
                // Role-specific context banter: pick the spider whose turf the
                // context belongs to, then a joke from that spider's own array.
                // File > endpoint > steps priority, so the most concrete bit of
                // context gets the spotlight.
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
                } else {
                  joker = randomSpider();
                  joke = streaming ? pick(BANTER_STREAM) : pick(BANTER_IDLE);
                }
                if (joker && joke) {
                  setSpeech(joker, joke, 4.5, joker.icon + ' ' + joker.name);
                  logGossipEntry(joker.icon + ' ' + joker.name, joke);
                }
                // While streaming (LLM thinking) jokes come faster.
                scene.banterCd = streaming ? 6 + Math.random() * 6 : 12 + Math.random() * 10;
              } else {
                scene.banterCd = 2.5; // re-check shortly
              }
            }
          }

          // ── Water-cooler gossip: idle skit about the user's stats ────────
          if (_replay) {
            // no gossip during replay
          } else if (scene.gossip) {
            // Real work interrupts the gossip — everyone scrambles away.
            if (scene.writer || scene.queue.length || vm.streamingActive) {
              endGossipNow();
            } else {
              advanceGossip(dt);
            }
          } else if (!scene.writer && !scene.queue.length && !vm.streamingActive && !vm.meetingHovered) {
            // Gossip yields to the watching skit — when the user is hovering,
            // the office greets them instead of gossiping about them.
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

          // ── 'The user is watching' skit ──────────────────────────────────
          // When the mouse is over the panel the spiders notice and perform.
          // Hover beats gossip (drops it); real work beats hover (interrupts).
          if (!_replay && vm.meetingHovered) {
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
            endWatchingNow(); // mouse left the panel
          }

          // Wave timers decay.
          scene.spiders.forEach(function (s) {
            if (s.waveT > 0) s.waveT -= dt;
          });
        }

        // ── Drawing ────────────────────────────────────────────────────────
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
          drawDesks(W, H);
          drawSpiders(W, H);
          drawConfetti(W, H);
        }

        // ── Seasonal office decorations ────────────────────────────────────
        // The spiders decorate the office for the current holiday: holiday
        // lights, banners, pumpkins, hearts, stars, shamrocks.
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
          // pennant triangles
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
            ctx.font = 'bold ' + Math.round(bh * 0.42) + 'px sans-serif';
            ctx.textAlign = 'center';
            ctx.fillStyle = 'rgba(255,255,255,0.92)';
            ctx.fillText(text, W / 2, by + bh + bh * 0.75);
            ctx.textAlign = 'left';
          }
        }
        function drawStringLights(W, H) {
          // A sagging string of colored bulbs across the top of the wall.
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
          // Tiny floating emoji drifting across the wall — cheap and cheerful.
          var wallH = H * 0.52;
          var t = Date.now() / 1000;
          ctx.font = Math.round(H * 0.03) + 'px sans-serif';
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
          // A tiny tree on the open floor (bottom center-right, clear of desks).
          var tx = W * 0.70, ty = H * 0.54, th = H * 0.16;
          ctx.fillStyle = '#2d6a4f';
          // Layered tiers: each tier's baseline rises so they stack into a tree.
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
          // Jack-o-lantern on the open floor (bottom left-center, clear of desks).
          var px = W * 0.18, py = H * 0.56, pr = H * 0.055;
          ctx.fillStyle = '#e8871e';
          ctx.beginPath(); ctx.arc(px, py, pr, 0, 6.283); ctx.fill();
          ctx.strokeStyle = 'rgba(0,0,0,0.4)'; ctx.lineWidth = 1.5; ctx.stroke();
          ctx.fillStyle = '#2b1a05';
          // eyes
          ctx.beginPath(); ctx.moveTo(px - pr * 0.45, py - pr * 0.25); ctx.lineTo(px - pr * 0.15, py - pr * 0.25); ctx.lineTo(px - pr * 0.3, py - pr * 0.55); ctx.closePath(); ctx.fill();
          ctx.beginPath(); ctx.moveTo(px + pr * 0.45, py - pr * 0.25); ctx.lineTo(px + pr * 0.15, py - pr * 0.25); ctx.lineTo(px + pr * 0.3, py - pr * 0.55); ctx.closePath(); ctx.fill();
          // jagged mouth
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
            // Tiny rounded rectangle with a slight aspect twist for flutter.
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
          // Back wall
          var wallH = H * 0.52;
          var grad = ctx.createLinearGradient(0, 0, 0, wallH);
          grad.addColorStop(0, '#101a30');
          grad.addColorStop(1, '#16223c');
          ctx.fillStyle = grad;
          ctx.fillRect(0, 0, W, wallH);
          // Wainscot line
          ctx.strokeStyle = 'rgba(255,255,255,0.06)';
          ctx.lineWidth = 1;
          ctx.beginPath(); ctx.moveTo(0, wallH); ctx.lineTo(W, wallH); ctx.stroke();
          // Floor
          var fg = ctx.createLinearGradient(0, wallH, 0, H);
          fg.addColorStop(0, '#22304e');
          fg.addColorStop(1, '#0d1424');
          ctx.fillStyle = fg;
          ctx.fillRect(0, wallH, W, H - wallH);
          // Floor perspective planks
          ctx.strokeStyle = 'rgba(255,255,255,0.035)';
          ctx.lineWidth = 1;
          for (var i = 1; i < 6; i++) {
            var yy = wallH + (H - wallH) * (i / 6);
            ctx.beginPath(); ctx.moveTo(0, yy); ctx.lineTo(W, yy); ctx.stroke();
          }
          // Window (left wall)
          var wx = W * 0.04, wy = H * 0.10, ww = W * 0.16, wh = H * 0.26;
          rr(wx, wy, ww, wh, 6); ctx.fillStyle = 'rgba(110,180,255,0.12)'; ctx.fill();
          ctx.strokeStyle = 'rgba(255,255,255,0.18)'; ctx.lineWidth = 3; ctx.stroke();
          ctx.beginPath(); ctx.moveTo(wx + ww / 2, wy); ctx.lineTo(wx + ww / 2, wy + wh); ctx.stroke();
          ctx.beginPath(); ctx.moveTo(wx, wy + wh / 2); ctx.lineTo(wx + ww, wy + wh / 2); ctx.stroke();
          // Wall clock
          var cx = W * 0.46, cy = H * 0.14, cr = H * 0.05;
          ctx.beginPath(); ctx.arc(cx, cy, cr, 0, 6.283); ctx.fillStyle = '#e8e8e8'; ctx.fill();
          ctx.strokeStyle = '#888'; ctx.lineWidth = 2; ctx.stroke();
          ctx.beginPath(); ctx.moveTo(cx, cy); ctx.lineTo(cx, cy - cr * 0.6); ctx.stroke();
          ctx.beginPath(); ctx.moveTo(cx, cy); ctx.lineTo(cx + cr * 0.5, cy); ctx.stroke();
        }

        function drawTable(W, H) {
          var t = TABLE_RECT;
          var tx = t.x * W, ty = t.y * H, tw = t.w * W, th = t.h * H;
          // Shadow
          ctx.fillStyle = 'rgba(0,0,0,0.35)';
          rr(tx - 4, ty + 4, tw + 8, th + 6, 10); ctx.fill();
          // Table top (wood-ish)
          var g = ctx.createLinearGradient(tx, ty, tx, ty + th);
          g.addColorStop(0, '#4a3a2a'); g.addColorStop(1, '#382c20');
          ctx.fillStyle = g;
          rr(tx, ty, tw, th, 10); ctx.fill();
          ctx.strokeStyle = 'rgba(255,255,255,0.15)'; ctx.lineWidth = 1.5; ctx.stroke();
          // Legs
          ctx.fillStyle = '#2a2118';
          ctx.fillRect(tx + 8, ty + th - 2, 8, H * 0.05);
          ctx.fillRect(tx + tw - 16, ty + th - 2, 8, H * 0.05);
          // Coffee cups / papers on table
          ctx.fillStyle = 'rgba(255,255,255,0.14)';
          rr(tx + tw * 0.15, ty + th * 0.22, tw * 0.18, th * 0.45, 3); ctx.fill();
          rr(tx + tw * 0.55, ty + th * 0.22, tw * 0.18, th * 0.45, 3); ctx.fill();
        }

        function drawCooler(W, H) {
          var cx = COOLER.x * W, cy = COOLER.y * H;
          var s = Math.min(W, H) / 520;
          // Water jug on top
          ctx.fillStyle = 'rgba(120,190,255,0.85)';
          rr(cx - 7 * s, cy - 20 * s, 14 * s, 16 * s, 3 * s); ctx.fill();
          ctx.strokeStyle = 'rgba(255,255,255,0.5)'; ctx.lineWidth = 1; ctx.stroke();
          // water level
          ctx.fillStyle = 'rgba(150,215,255,0.9)';
          rr(cx - 5 * s, cy - 12 * s, 10 * s, 7 * s, 2 * s); ctx.fill();
          // cooler body
          ctx.fillStyle = '#d9e2ef';
          rr(cx - 9 * s, cy - 4 * s, 18 * s, 12 * s, 3 * s); ctx.fill();
          ctx.strokeStyle = 'rgba(0,0,0,0.15)'; ctx.stroke();
          // spigot
          ctx.fillStyle = '#3b82c4';
          rr(cx + 6 * s, cy - 1 * s, 4 * s, 5 * s, 1 * s); ctx.fill();
          // little cup below
          ctx.fillStyle = '#ffffff';
          rr(cx + 3 * s, cy + 9 * s, 5 * s, 6 * s, 1 * s); ctx.fill();
          ctx.strokeStyle = 'rgba(0,0,0,0.2)'; ctx.stroke();
        }

        function drawBoard(W, H) {
          var b = BOARD_RECT;
          var bx = b.x * W, by = b.y * H, bw = b.w * W, bh = b.h * H;
          // Frame shadow
          ctx.fillStyle = 'rgba(0,0,0,0.4)';
          rr(bx + 4, by + 4, bw, bh, 8); ctx.fill();
          // Board surface
          ctx.fillStyle = '#dfe6e0';
          rr(bx, by, bw, bh, 8); ctx.fill();
          ctx.strokeStyle = '#5c6a5e'; ctx.lineWidth = 3; ctx.stroke();
          // Tray
          ctx.fillStyle = 'rgba(0,0,0,0.25)';
          rr(bx + bw * 0.1, by + bh - 12, bw * 0.35, 7, 3); ctx.fill();

          // Board text
          var padX = 10, padY = 12;
          var maxChars = Math.floor((bw - padX * 2) / 9);
          var lines = scene ? scene.boardLines.slice(-6) : [];
          ctx.font = 'bold 11px monospace';
          ctx.textBaseline = 'top';
          // Writing spider's in-progress line
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
            // checkmark for finished lines
            if (!ln.writing && ln.done !== false && i < lines.length - 1) {
              // subtle tick after each completed line
            }
          }
          ctx.textBaseline = 'alphabetic';
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

        function drawDesks(W, H) {
          if (!scene) return;
          scene.spiders.forEach(function (s) {
            var dx = s.home.x * W, dy = s.home.y * H;
            // Desk base
            ctx.fillStyle = 'rgba(0,0,0,0.3)';
            rr(dx - 24, dy - 6, 48, 18, 4); ctx.fill();
            ctx.fillStyle = '#2b2117';
            rr(dx - 26, dy - 8, 52, 18, 4); ctx.fill();
            ctx.strokeStyle = 'rgba(255,255,255,0.1)'; ctx.lineWidth = 1; ctx.stroke();
            // Monitor
            ctx.fillStyle = '#0d1424';
            rr(dx - 10, dy - 24, 20, 14, 2); ctx.fill();
            ctx.strokeStyle = 'rgba(255,255,255,0.2)'; ctx.stroke();
            // tiny screen glow
            ctx.fillStyle = s.color;
            ctx.globalAlpha = 0.5;
            ctx.fillRect(dx - 7, dy - 21, 14, 2);
            ctx.globalAlpha = 1;
            // Name tag
            ctx.font = '9px sans-serif';
            ctx.fillStyle = 'rgba(255,255,255,0.55)';
            ctx.textAlign = 'center';
            ctx.fillText(s.icon, dx, dy - 30);
            ctx.textAlign = 'left';
          });
        }

        function drawSpiders(W, H) {
          if (!scene) return;
          // Sort so spiders lower on screen draw on top.
          var sorted = scene.spiders.slice().sort(function (a, b) { return a.y - b.y; });
          sorted.forEach(function (s) { drawSpider(W, H, s); });
        }

        function drawSpider(W, H, s) {
          var py = s.y * H;
          var scale = Math.min(W, H) / 520;
          var bodyW = 26 * scale, bodyH = 20 * scale;
          var bob = 0;
          var tremble = 0;
          if (s.state === 'walk') bob = Math.sin(s.walkPhase * 2) * 1.5 * scale;
          else if (s.state === 'celebrate') bob = -Math.abs(Math.sin(s.celebrateT * 9)) * 6 * scale;
          else bob = Math.sin(s.walkPhase) * 1.2 * scale;
          // Reaction: happy hop for a landed step, worried tremble for a fail.
          if (s.reactT > 0) {
            if (s.reactKind === 'good') {
              bob -= Math.abs(Math.sin(s.reactT * 16)) * 5 * scale;
            } else {
              tremble = Math.sin(s.reactT * 40) * 1.6 * scale;
            }
          }
          var px = s.x * W + tremble;
          var cy = py + bob;

          // Shadow
          ctx.fillStyle = 'rgba(0,0,0,0.3)';
          ctx.beginPath();
          ctx.ellipse(px, py + bodyH * 0.7, bodyW * 0.9, bodyH * 0.28, 0, 0, 6.283);
          ctx.fill();

          // Legs (8 small legs, 4 per side) — wiggle while walking
          ctx.strokeStyle = s.color;
          ctx.lineWidth = Math.max(1.5, 2 * scale);
          ctx.lineCap = 'round';
          var legSwing = s.state === 'walk' ? Math.sin(s.walkPhase) : Math.sin(s.walkPhase * 0.6) * 0.35;
          for (var i = 0; i < 4; i++) {
            var attachY = cy - bodyH * 0.3 + (i / 3) * bodyH * 0.7;
            var off = 8 + i * 3;
            var sway = legSwing * (i % 2 === 0 ? 1 : -1) * 4 * scale;
            // left leg
            ctx.beginPath();
            ctx.moveTo(px - bodyW * 0.35, attachY);
            ctx.lineTo(px - bodyW * 0.35 - off * scale, attachY + 8 * scale + sway);
            ctx.stroke();
            // right leg
            ctx.beginPath();
            ctx.moveTo(px + bodyW * 0.35, attachY);
            ctx.lineTo(px + bodyW * 0.35 + off * scale, attachY + 8 * scale - sway);
            ctx.stroke();
          }

          // Waving arm — one raised front leg that waves when the user watches.
          if (s.waveT > 0) {
            var waveSwing = Math.sin(s.waveT * 14 + s.wavePhase) * 7 * scale;
            ctx.strokeStyle = s.color;
            ctx.lineWidth = Math.max(1.5, 2 * scale);
            ctx.lineCap = 'round';
            // raised arm (front, toward the user)
            ctx.beginPath();
            ctx.moveTo(px + bodyW * 0.3, cy - bodyH * 0.2);
            ctx.lineTo(px + bodyW * 0.55, cy - bodyH * 1.15 + waveSwing);
            ctx.stroke();
            // little hand
            ctx.fillStyle = s.color;
            ctx.beginPath();
            ctx.arc(px + bodyW * 0.55, cy - bodyH * 1.15 + waveSwing, 2.4 * scale, 0, 6.283);
            ctx.fill();
          }

          // Body: one big block
          ctx.fillStyle = s.color;
          rr(px - bodyW / 2, cy - bodyH / 2, bodyW, bodyH, 6 * scale);
          ctx.fill();
          ctx.strokeStyle = 'rgba(0,0,0,0.35)';
          ctx.lineWidth = 1;
          ctx.stroke();
          // Shine
          ctx.fillStyle = 'rgba(255,255,255,0.28)';
          rr(px - bodyW / 2 + 3 * scale, cy - bodyH / 2 + 2 * scale, bodyW * 0.4, bodyH * 0.28, 3 * scale);
          ctx.fill();
          // Eyes (look toward target, or UP at the user when waving)
          var look = s.state === 'walk' ? 1 : 0;
          var ex = px + (s.target.x > s.x ? 3 : s.target.x < s.x ? -3 : 0) * scale;
          var lookUp = (s.waveT > 0 || (scene.watching && scene.watching.star === s)) ? 1.4 * scale : 0;
          var ey = cy - bodyH * 0.1 - lookUp;
          ctx.fillStyle = '#fff';
          ctx.beginPath(); ctx.arc(px - bodyW * 0.18, ey, 3.2 * scale, 0, 6.283); ctx.fill();
          ctx.beginPath(); ctx.arc(px + bodyW * 0.18, ey, 3.2 * scale, 0, 6.283); ctx.fill();
          ctx.fillStyle = '#111';
          ctx.beginPath(); ctx.arc(px - bodyW * 0.18 + ex * 0.4, ey - lookUp * 0.4, 1.6 * scale, 0, 6.283); ctx.fill();
          ctx.beginPath(); ctx.arc(px + bodyW * 0.18 + ex * 0.4, ey - lookUp * 0.4, 1.6 * scale, 0, 6.283); ctx.fill();

          // Marker pen when writing
          if (scene.writer === s && s.state === 'write') {
            ctx.strokeStyle = '#222';
            ctx.lineWidth = 2.5 * scale;
            ctx.beginPath();
            ctx.moveTo(px + bodyW * 0.3, cy);
            ctx.lineTo(px + bodyW * 0.7, cy + 6 * scale);
            ctx.stroke();
          }

          // Speech bubble
          if (s.speech && s.speechTtl > 0) {
            drawSpeechBubble(W, H, px, cy - bodyH, s.speech);
          }
        }

        function drawSpeechBubble(W, H, px, py, text) {
          ctx.font = '10px sans-serif';
          var maxW = Math.min(W * 0.32, 220);
          var words = text.split(' ');
          var lines = []; var cur = '';
          for (var i = 0; i < words.length; i++) {
            var test = cur ? cur + ' ' + words[i] : words[i];
            if (ctx.measureText(test).width > maxW && cur) { lines.push(cur); cur = words[i]; }
            else cur = test;
          }
          if (cur) lines.push(cur);
          if (lines.length > 3) { lines = lines.slice(0, 3); lines[2] += '…'; }
          var bh = lines.length * 13 + 10;
          var bw = maxW + 12;
          var bx = Math.max(4, Math.min(W - bw - 4, px - bw / 2));
          var by = Math.max(2, py - bh - 6);
          ctx.fillStyle = 'rgba(15,20,35,0.92)';
          rr(bx, by, bw, bh, 6); ctx.fill();
          ctx.strokeStyle = 'rgba(255,255,255,0.25)'; ctx.lineWidth = 1; ctx.stroke();
          // tail
          ctx.beginPath();
          ctx.moveTo(px - 4, by + bh);
          ctx.lineTo(px, by + bh + 7);
          ctx.lineTo(px + 4, by + bh);
          ctx.closePath();
          ctx.fillStyle = 'rgba(15,20,35,0.92)';
          ctx.fill();
          ctx.fillStyle = '#e8eef6';
          ctx.textBaseline = 'top';
          for (var j = 0; j < lines.length; j++) {
            ctx.fillText(lines[j], bx + 6, by + 5 + j * 13);
          }
          ctx.textBaseline = 'alphabetic';
        }

        // ── Wiring ─────────────────────────────────────────────────────────
        // Watch the step stream for status transitions. When a step flips to
        // done/applied/created → cheer; to error/rejected/failed → worry.
        // Keeps a cache of last-seen status per step index so only NEW
        // transitions fire (reconnects/replans don't re-react to old steps).
        var _stepStatusCache = {};
        $scope.$watch(function () {
          var s = vm.streamingSteps;
          if (!s) return 0;
          for (var i = 0; i < s.length; i++) {
            var st = s[i];
            var key = st.index !== undefined && st.index !== null ? st.index : i;
            var status = st.status || 'pending';
            // Treat an unseen step as 'pending' so one that first appears in a
            // terminal state (immediate error, batched done) still reacts.
            var prev = _stepStatusCache[key] !== undefined ? _stepStatusCache[key] : 'pending';
            if (prev !== status) {
              if (scene) scene._lastStepType = st.type;
              if (status === 'done' || status === 'applied' || status === 'created' || status === 'ok' || status === 'skipped') {
                if (scene && scene.meetingOn && prev !== 'done') {
                  fireReaction('good', pick(REACT_SUCCESS));
                  pushTicker('good', tickerLabelForStep(st));
                }
              } else if (status === 'error' || status === 'rejected' || status === 'failed') {
                if (scene && scene.meetingOn) {
                  fireReaction('bad', pick(REACT_FAIL));
                  pushTicker('bad', tickerLabelForStep(st));
                }
              }
            }
            _stepStatusCache[key] = status;
          }
          return s.length;
        });

        // A fresh run resets the step stream — forget old statuses so the
        // first step of the new run is treated as a real transition.
        $scope.$watch(function () { return vm.streamingSteps ? vm.streamingSteps.length : 0; }, function (len, prev) {
          if (len === 0 && prev > 0) _stepStatusCache = {};
        });

        $scope.$watch(function () { return vm.agentActivityLog ? vm.agentActivityLog.length : 0; }, function (len, prev) {
          if (len === undefined) return;
          // New entries appeared.
          if (len > (prev || 0)) {
            var log = vm.agentActivityLog;
            for (var i = (prev || 0); i < len; i++) {
              // Treat an entry as replay-driven only when a replay is actually
              // playing AND no live run is in flight. If the user starts a new
              // card mid-replay, vm.streamingActive flips true in this same
              // digest — the log watch runs before the streamingActive watch,
              // so checking the live flag here lets startMeeting() cancel the
              // replay and record the new run's first entry properly instead
              // of silently dropping it.
              var fromReplay = !!_replay && !vm.streamingActive;
              if (_recording && !_replay) recordEvent({ type: 'log', entry: log[i] });
              handleLogEntry(log[i], fromReplay);
            }
          }
        });

        $scope.$watch('vm.showMeeting', function (val) {
          if (val) startLoop(); else stopLoop();
        });

        // Also trigger meeting start/end from the streaming state (belt & braces).
        $scope.$watch('vm.streamingActive', function (val, prev) {
          if (!scene) return;
          if (val && !prev) startMeeting();
          if (!val && prev) finishMeeting();
        });

        $scope.$on('$destroy', function () {
          destroyed = true;
          stopLoop();
        });

        // Kick off the scene even if the panel opens later.
        scene = makeScene();
        if (vm.showMeeting) startLoop();
      }
    };
  }]);
