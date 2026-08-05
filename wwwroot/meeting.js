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
  .factory('MeetingMixin', ['$timeout', '$http', function ($timeout, $http) {
    return {
      init: function (vm, $scope) {
        // ── Panel state (mirrors NotesMixin) ──────────────────────────────
        // Default the floating panel to the BOTTOM-RIGHT corner of the viewport
        // so it never covers the Agent panel / streaming section (which live in
        // the top-left panel column) on first open. A saved position (persisted
        // via settings) overrides the default, and everything is clamped to the
        // viewport so a stale save can't park it over the agent UI either.
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
        // Restore a persisted panel position/size (saved by SettingsMixin).
        if (vm._meetingPanelCfg) {
          var _mp = vm._meetingPanelCfg;
          if (typeof _mp.left === 'number') vm.meeting.left = _mp.left;
          if (typeof _mp.top === 'number') vm.meeting.top = _mp.top;
          if (typeof _mp.width === 'number') vm.meeting.width = _mp.width;
          if (typeof _mp.height === 'number') vm.meeting.height = _mp.height;
        }
        // Shared clamp so the panel always stays fully on-screen, no matter
        // how it was positioned (fresh default, restored save, or drag/resize).
        // Exposed on vm so SettingsMixin can re-clamp when it applies a saved
        // geometry asynchronously after this mixin has already run.
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
        vm.meetingBoardLines = [];

        // ── Spider cast (role → spider) ────────────────────────────────────
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
        // Board stand: where the writing spider stands (in front of the board).
        var BOARD_STAND = { x: 0.66, y: 0.86 };
        // Where the Verifier stands to stare down the Complexity spider after
        // an overruled verdict — just left of the board stand, face to face.
        var STANDOFF_SPOT = { x: 0.54, y: 0.86 };
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
          { x: 0.23, y: 0.64 },
          { x: 0.17, y: 0.72 }
        ];
        var scene = null;      // { spiders:[], boardLines:[], writer:null, queue:[], meetingOn:false, done:false }
        var raf = null;
        var canvas = null;
        var ctx = null;
        var lastTs = 0;
        var destroyed = false;
        var _recording = null; // events captured during a live run (for replay)
        var _recordingCardId = null; // card whose run is being recorded (persisted for replay)
        var _replay = null;    // active replay clock { events, t0, elapsed, idx }
        vm.meetingReplay = null;      // last completed run's events
        vm.meetingReplaying = false;  // true while a replay is running
        vm.meetingReplaySpeed = 1;    // replay playback multiplier (1 / 1.5 / 2)
        vm.meetingTicker = [];        // recent step outcomes ({ kind, label })
        vm.gossipLog = [];            // transcribed jokes + gossip ({ t, who, text })
        vm.meetingIdeas = [];         // backend improvement suggestions ({ topic, desc, complete, date })

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
              waveT: 0, wavePhase: Math.random() * 6.28, // waving at the user
              rage: 0, // Complexity spider: escalating anger meter (0..100)
              rageAt: Date.now(), // when rage last changed (for idle cooling)
              rageDrainedAt: Date.now(), // last frame cooling was applied
              stomping: false, // exaggerated angry walk back to the desk
              glaringAt: null  // spider this one is locking eyes with (glare skit)
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
            postMortem: null,        // { est, actual, shown } est-vs-actual chart
            verdictOutcome: null,    // 'right' | 'wrong' | 'fail' from the run's verdict
            verdictGossiped: false,  // once the verdict is retold at the cooler, it's done
            _lastStepType: '',       // step type for reaction routing
            confetti: [],            // celebration particles ({ x, y, vx, vy, rot, vr, color, size, life, ttl })
            calmQuipFired: false,    // once-per-run 'calmed down' line (idle drain to 0)
            steam: [],               // rage steam wisps off the Complexity spider ({ x, y, vx, vy, size, sway, swaySpeed, life, ttl })
            watching: null,          // active 'the user is watching' skit
            watchingCd: 8,           // cooldown before the next watching skit
            standoff: null,          // active Verifier-vs-Complexity standoff skit
            standoffCd: 20,          // cooldown before the next standoff can start
            glare: null,             // active Reviewer-vs-Complexity glare skit
            coolerTrip: null,        // Complexity spider storming off to the cooler
            coolerTripCd: 90         // cooldown before the next rage-cooler trip
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
        var BANTER_CONFIG = [  // IT Specialist: settings / configuration — the boring stuff
          "Configuration check: thinking capped at {think} tokens, output at {tokens}. Tune responsibly.",
          "This run is using {model} on {endpoint}. I wrote that down somewhere. Probably.",
          "Thinking context is at {think} tokens. Above 4096 the model gets… creative. I mean that literally.",
          "Output budget is {tokens} tokens. If the plan runs long, that's why. Budgeting. I love budgeting.",
          "Endpoint config verified: {endpoint}. Yes. That is a configured thing. It's configured.",
          "{saved} saved theme(s), plan font at {font}px, {model} on deck. The boring numbers that run the show.",
          "The user set {model} as the worker. I checked the model field three times. It says what it says.",
          "Default max tokens {tokens}, thinking {think}. In my professional opinion? These are numbers. Good numbers."
        ];
        // The Complexity spider is the office's most complicated, most annoyed
        // resident. It mutters about the task's difficulty score, the context
        // budget, and whenever reasoning or diffs get compacted.
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
        var COMPLEXITY_CTX = [   // about the accumulated context budget
          "The context is up to {ctx} tokens now. It grows. It always grows.",
          "{ctx} tokens of context and counting. The whiteboard is practically vibrating.",
          "We're at {ctx} tokens of accumulated context. Someone's going to pay for this later.",
          "{ctx} tokens. Do you understand what that MEANS for my poor spider brain? Probably not."
        ];
        var COMPLEXITY_COMPACT = [ // when reasoning/diffs get compacted
          "COMPACTED. They compacted my context. Everything I knew, summarized by a stranger.",
          "Oh wonderful — the reasoning just got compressed. Goodbye, nuance. We barely knew you.",
          "They compressed the accumulated diffs. My memory is now a Highlights Reel.",
          "COMPACTION. That's their solution to complexity. Add more complexity. Perfect.",
          "The context was too big, so they made it smaller. I am Big Mad about this.",
          "Diff context summarized. Translation: my beautiful, detailed history is now a haiku."
        ];
        var CALMED_QUIPS = [ // rare: the meter fully drains during idle cooling
          "Huh. I'm… relaxed? This is new. Don't get used to it.",
          "Fine. FINE. I will breathe. For now. Temporarily.",
          "The rage is gone. It feels wrong. What do I do with my hands?",
          "A quiet mind. Disgusting. Absolutely disgusting.",
          "Okay, who replaced my rage with calm? HR is going to hear about this."
        ];
        // When the run finishes, the Complexity spider compares its own
        // difficulty estimate to what actually happened. Being right: smug.
        // Being overruled by the verifier: sulking, obviously.
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
        var COMPLEXITY_VERDICT_FAIL = [ // the verifier said the task is NOT complete
          "The verifier says it's NOT complete. On a {label} task. I don't make the rules — I just estimate them.",
          "Verification failed. My {label} estimate was apparently too generous. Or reality is broken.",
          "The verifier found issues. {actual} step(s) and STILL not done. This is exactly why I complain.",
          "Not complete?! The complexity was clearly {label}-and-then-some. But nobody listens to the spider.",
          "That's {record} now, tainted by THIS. The failure column grew. I felt it in my legs.",
          "{record}. And this run is why there's a losses column at all. Unbelievable."
        ];
        // The Complexity spider's water-cooler brag about its historical
        // accuracy once it has a real track record to brag about.
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
        // The reviewer's counter-reaction, delivered from the board right after
        // it writes the final verdict: a grudging concession when the
        // Complexity spider's estimate was right, a smug 'told you so' when it
        // was overruled. Both land in the Office Chat.
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
        var REVIEWER_SMUG_FAIL = [ // verifier said it's not done — own it together
          "Okay, that one's on all of us. The board will have to stay up a little longer.",
          "I wrote 'complete' and the verifier wrote 'no'. Some days the board plays both sides.",
          "{label}, {actual} step(s), and not finished. We'll take the L as a team.",
          "I've seen incomplete plans before. This one's going back to the drawing board. All of us."
        ];
        // When the reviewer's smug 'told you so' lands, the two lock eyes from
        // across the office for a beat before the Complexity spider stomps
        // home. The Complexity spider fires back; the reviewer gets the last
        // word. Both bubbles land in the Office Chat.
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
        // Water-cooler retellings: after a run, a bystander spider recounts the
        // reviewer's verdict moment for the rest of the office — the grudging
        // admission when the estimate was right, the smug 'told you so' when it
        // was overruled, or the shared L when the verifier said it wasn't done.
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
        // When rage crosses 75 the Complexity spider occasionally storms off to
        // the water cooler, grabs a drink, mutters about the unmanageable
        // context, then stalks back to its desk.
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
        // A random nearby spider's double-take when the Complexity spider
        // storms off mid-meeting — with a bubble in the Office Chat.
        var DOUBLE_TAKE = [
          "did… did he just leave mid-meeting?",
          "Is the red one… storming off? Again?",
          "He just LEFT. Mid-sentence. Rude but… honestly, fair.",
          "Wait, did he just go for water during MY turn?",
          "Okay, who's covering his seat while he hydrates?",
          "The Complex one just walked out. Should someone go after him? …No? No one? Cool."
        ];
        // The sarcastic post-mortem the Complexity spider writes on the board
        // LAST, after the reviewer's verdict — summarizing how the task turned
        // out versus how easy the user probably thought it would be.
        var COMPLEXITY_POSTMORTEM = [
          "POST-MORTEM: user expected 'quick tweak', reality was {label} ({actual} step(s)). I saw this coming.",
          "Post-mortem: {label} in {actual} step(s). The user said 'easy'. The whiteboard says otherwise.",
          "Task autopsy: called it {label}. {actual} step(s) later, I remain unimpressed and correct.",
          "The user asked for a 'small change'. The board says {label}, {actual} steps. They always do.",
          "Final analysis: {label}. {actual} steps. The user's expectations and my estimate were never friends.",
          "Post-mortem complete: {label} ({actual} step(s)). I predicted {est} step(s). Close enough. I'm always close."
        ];
        var COMPLEXITY_POSTMORTEM_FAIL = [ // the verifier said it's NOT done
          "POST-MORTEM: {label} in {actual} step(s) and STILL not done. The user said 'simple'. Unbelievable.",
          "Task autopsy: {label}, {actual} steps, verifier unsatisfied. So the user gets MORE complexity. Great.",
          "Post-mortem: user wanted trivial, got {label}, verifier wanted more. My estimate was the only sane one here.",
          "The board says {actual} step(s) of {label} and the task STILL isn't done. 'Quick task', the user said."
        ];
        // The two-spider standoff: when the Complexity spider's estimate is
        // overruled, the Verifier walks over and stares it down with the code
        // as evidence, then the Complexity spider sulks back to its desk.
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
        // Fake but fun ranks based on live user stats. Each rank carries a
        // military-style insignia: chevrons (enlisted stripes, pointing up),
        // stars (officer rank), and a grade color (bronze → silver → gold →
        // platinum). The header rank chip renders the insignia beside the title.
        var RANK_TITLES = [
          { min: 100, title: 'Grand Architect of Everything', chevrons: 5, stars: 2, grade: 'platinum' },
          { min: 50, title: 'Supreme Code Commander', chevrons: 4, stars: 1, grade: 'gold' },
          { min: 25, title: 'Certified Power User', chevrons: 3, stars: 1, grade: 'gold' },
          { min: 10, title: 'Respected Contributor', chevrons: 2, stars: 0, grade: 'silver' },
          { min: 3, title: 'Rising Star', chevrons: 1, stars: 0, grade: 'bronze' },
          { min: 0, title: 'Legend in Training', chevrons: 0, stars: 0, grade: 'recruit' }
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
        // Live settings snapshot for the IT Specialist's config banter.
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
        // Replaces {placeholders} in a banter template with live context.
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
          if (scene.coolerTrip) endCoolerTripNow(); // a step landing breaks the cooler break
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
        vm.meetingHoverSince = null; // when the current hover began (for watched-drain boost)
        var _audioCtx = null;
        vm.meetingMuted = false;
        try { vm.meetingMuted = window.localStorage.getItem('weaver.meeting.muted') === '1'; } catch (e) { }
        // Panel-wide text size — scales the DOM chrome (status bar, ticker,
        // office chat, badges) via the --meeting-font CSS var AND the canvas
        // text via mf() below. Persisted like the mute toggle.
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
        // One-time discoverability tooltip: the first time the rage rumble is
        // actually heard, point users at the header mute toggle. Persisted so
        // it only shows once ever.
        vm.meetingSoundTip = false;
        vm._soundTipShown = false;
        try { vm._soundTipShown = window.localStorage.getItem('weaver.meeting.soundTipSeen') === '1'; } catch (e) { }
        vm.dismissMeetingSoundTip = function () {
          vm.meetingSoundTip = false;
          if (vm._soundTipTimer) { $timeout.cancel(vm._soundTipTimer); vm._soundTipTimer = null; }
        };

        // ── Complexity spider verdict track record ─────────────────────────
        // A persistent win/loss tally of the Complexity spider's estimates
        // against the verifier's verdicts, saved to localStorage so it survives
        // across runs and can brag about its historical accuracy. 'right' is a
        // win; 'wrong' and 'fail' are both losses.
        var _verdictRecord = { right: 0, wrong: 0, fail: 0 };
        try {
          var _vrRaw = window.localStorage.getItem('weaver.meeting.verdicts');
          if (_vrRaw) {
            var _vrParsed = JSON.parse(_vrRaw);
            // Harden against corrupted / legacy shapes: only adopt a real
            // object with numeric counters; anything else keeps the defaults.
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
        // '3-1' style label: wins-losses (wrong + fail both count as losses).
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
        function playStomp() {
          var ctx = sfx(); if (!ctx) return;
          try {
            var t = ctx.currentTime;
            // Heavy angry thud: low square hit with a fast pitch drop.
            var osc = ctx.createOscillator();
            var gain = ctx.createGain();
            osc.type = 'square';
            osc.frequency.setValueAtTime(90, t);
            osc.frequency.exponentialRampToValueAtTime(38, t + 0.18);
            gain.gain.setValueAtTime(0.09, t);
            gain.gain.exponentialRampToValueAtTime(0.0001, t + 0.22);
            osc.connect(gain); gain.connect(ctx.destination);
            osc.start(t); osc.stop(t + 0.24);
          } catch (e) { }
        }
        // ── Rage sound ─────────────────────────────────────────────────────
        // The Complexity spider's anger has a voice: a low continuous rumble
        // that swells as the meter climbs (volume + pitch both track rage),
        // plus a quick one-shot 'steam vent' hiss when it hits 100%. Both
        // honor the existing mute toggle and only sound while the panel is
        // visible. The rumble is a persistent node that must be started and
        // stopped explicitly (unlike the one-shots above).
        // The jitter wave that trembles the spider's body on screen. Shared by
        // drawSpider (pixel shake) and updateRageRumble (filter wobble) so the
        // audio stays in sync with the animation by construction.
        function rageShakeWave(now, walkPhase, rageFactor) {
          return Math.sin(now * (6 + rageFactor * 26) + walkPhase * 3) +
                 Math.sin(now * (9 + rageFactor * 20) * 1.7);
        }
        var _rageRumble = null; // { osc, gain, filter } persistent nodes
        var _steamVentAt = 0;   // last steam-vent time (throttle)
        function playSteamVent() {
          var ctx = sfx(); if (!ctx) return;
          try {
            var t = ctx.currentTime;
            // Short burst of band-passed noise — a pressure-valve hiss.
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
            src.connect(filter); filter.connect(gain); gain.connect(ctx.destination);
            src.start(t);
          } catch (e) { }
        }
        function playRageCreak(bucket) {
          var ctx = sfx(); if (!ctx) return;
          try {
            var t = ctx.currentTime;
            // A short rising tension tone — a 'creak' as the meter clenches a
            // notch tighter. Pitch climbs with the bucket so the escalation is
            // audible even without looking at the counter.
            var base = 140 + bucket * 4;      // 180 Hz at 10, 500 Hz at 90
            var osc = ctx.createOscillator();
            var gain = ctx.createGain();
            osc.type = 'triangle';
            osc.frequency.setValueAtTime(base, t);
            osc.frequency.exponentialRampToValueAtTime(base * 1.35, t + 0.16);
            gain.gain.setValueAtTime(0, t);
            gain.gain.linearRampToValueAtTime(0.05, t + 0.02);
            gain.gain.exponentialRampToValueAtTime(0.0001, t + 0.3);
            osc.connect(gain); gain.connect(ctx.destination);
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
            // Stereo panner so the growl can be positioned toward the spider.
            // Feature-detected: older engines without StereoPanner just skip it.
            var panner = null;
            if (typeof ctx.createStereoPanner === 'function') {
              // pan defaults to 0 on a fresh node.
              panner = ctx.createStereoPanner();
            }
            osc.connect(filter); filter.connect(gain);
            if (panner) { gain.connect(panner); panner.connect(ctx.destination); }
            else { gain.connect(ctx.destination); }
            osc.start();
            _rageRumble = { osc: osc, gain: gain, filter: filter, panner: panner };
          } catch (e) { _rageRumble = null; }
        }
        function updateRageRumble() {
          // Called every frame: scale the persistent rumble to current rage.
          // Muted or hidden → silence; rage 0 → silence; otherwise the rumble
          // gets louder and slightly higher-pitched as the meter climbs.
          var sp = scene ? spiderFor('complexity') : null;
          var rage = sp ? (sp.rage || 0) : 0;
          var audible = !vm.meetingMuted && vm.showMeeting && rage > 0;
          if (!audible) { stopRageRumble(); return; }
          var ctx = audioCtx(); if (!ctx) return;
          if (!_rageRumble) startRageRumble();
          if (!_rageRumble) return;
          // First time the rumble is heard: pop the one-time tooltip so users
          // discover the header mute toggle controls these office sounds.
          // Fired only after the rumble is genuinely playing (startRageRumble
          // succeeded) and never during a replay.
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
          // Smooth targets avoid zipper clicks on every step bump.
          _rageRumble.gain.gain.setTargetAtTime(0.045 * level, t, 0.08);
          _rageRumble.osc.frequency.setTargetAtTime(40 + level * 24, t, 0.1);
            // The lowpass filter wobbles in sync with the on-screen shake: the
            // shared rageShakeWave (same as drawSpider uses) drives the cutoff,
            // so at high rage the rumble physically wobbles as the spider
            // vibrates. sp is guaranteed non-null here (rage > 0 passed the
            // audible gate above, and rage is 0 whenever sp is null).
            var wave = rageShakeWave(Date.now() / 1000, sp.walkPhase, level);
            // wave swings roughly in [-2, 2]; normalize to a wobble whose
            // width grows with rage (tight at calm, wide and shaky at 100%).
            var wobble = (wave / 2) * (18 + level * 70);
            _rageRumble.filter.frequency.setTargetAtTime(95 + wobble, t, 0.05);
            // Spatial: pan the growl toward the spider's current screen
            // position (x is normalized 0..1, so map to a -1..1 balance capped
            // at ±0.85 — near hard-pan at the edge desks but keeps them
            // distinct). Walking to the cooler shifts where the rumble comes
            // from.
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
        vm.toggleMeetingMute = function () {
          vm.meetingMuted = !vm.meetingMuted;
          // Muting kills the ongoing rage rumble immediately (the per-frame
          // updater would also silence it, but stop it now so it never lingers).
          if (vm.meetingMuted) stopRageRumble();
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
        // The Complexity spider slams down at its desk after the post-mortem:
        // a small red particle burst, a heavy tremble, and a thud.
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
          s.reactKind = 'bad'; // tremble = the burst shaking it
          bumpComplexityRage(5); // one last angry pulse
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

        // Full rank object (title + insignia: chevrons, stars, grade) for the
        // header chip so each rank renders its own military insignia.
        function userRank(st) {
          var score = userRankScore(st);
          for (var i = 0; i < RANK_TITLES.length; i++) {
            if (score >= RANK_TITLES[i].min) return RANK_TITLES[i];
          }
          return RANK_TITLES[RANK_TITLES.length - 1];
        }

        // ng-repeat helper: [0..n) for drawing `n` chevron stripes.
        function chevronArray(n) {
          var a = [];
          for (var i = 0; i < (n || 0); i++) a.push(i);
          return a;
        }

        // ── Header user-stats widget ───────────────────────────────────────
        // Surfaces the same live stats the water-cooler gossip brags about as a
        // compact chip row in the top bar (next to the Project picker). Values
        // are recomputed from the live view-model whenever the header renders.
        vm.userStats = collectUserStats;
        vm.userRankTitle = userRankTitle;
        vm.userRank = userRank;
        vm.chevronArray = chevronArray;

        // ── Water-cooler gossip skit ───────────────────────────────────────
        // One spider strolls to the cooler and brags about the user's stats
        // while two others gather and react — as impressed by a tab count as
        // by an entire architecture.
        // Fetches the backend's improvement suggestions for the current project
        // (the "new tickets" the self-improving pipeline spawns) so the Ideas
        // spider can relay them during idle gossip. Throttled to once a minute.
        var _ideasFetchedAt = 0;
        function refreshMeetingIdeas() {
          var proj = vm.selectedProject;
          if (!proj) return;
          var now = Date.now();
          if (now - _ideasFetchedAt < 60000) return;
          _ideasFetchedAt = now;
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
            ideas.sort(function (x, y) { return (y.date || '').localeCompare(x.date || ''); });
            vm.meetingIdeas = ideas.slice(0, 6);
            $scope.$applyAsync();
          }).catch(function () { /* silent — office keeps gossiping about other stuff */ });
        }
        // The Ideas spider cuts in with the freshest backend suggestion.
        var IDEA_LINES = [
          "Fresh from the backend: '{topic}' is now a ticket. I basically invented it.",
          "Hey, the system just spawned an idea — '{topic}'. Filed under: brilliant.",
          "New suggestion dropped from the backend: '{topic}'. You're welcome.",
          "The backend birthed a ticket about '{topic}'. I've already got notes.",
          "Idea alert from the pipeline: '{topic}'. I saw it coming. I always see it coming."
        ];
        var IDEA_REACTIONS = [
          "ANOTHER ticket?! From thin air?!",
          "The backend just THINKS of work now?!",
          "Self-spawning tickets?! Terrifying. I love it.",
          "Where does it get these ideas?! ...Oh. From the user's code."
        ];
        function startGossip() {
          if (!scene || scene.gossip) return;
          refreshMeetingIdeas();
          var freshIdeas = (vm.meetingIdeas || []).filter(function (i) { return !i.complete; });
          var ideasSpider = spiderFor('ideas');
          // Keep the Ideas spider out of the bragger seat when it has something
          // to relay — it's the featured speaker for backend suggestions, not
          // the one bragging about the user's tab count.
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
          // The Ideas spider crashes the gossip to relay a backend suggestion —
          // it walks up to the cooler and joins the circle.
          if (freshIdeas.length && ideasSpider && ideasSpider !== bragger && ideasSpider !== a && ideasSpider !== b) {
            listeners.push(ideasSpider); // walks to COOLER_SPOTS[2] with the others
          }

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
          function fmtIdea(tpl, topic) {
            return tpl.replace(/\{topic\}/g, topic);
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
          // The Ideas spider cuts in with the freshest backend suggestion.
          if (ideasSpider && freshIdeas.length && listeners.indexOf(ideasSpider) !== -1) {
            var idea = freshIdeas[0];
            var topic = (idea.topic || 'a new task').trim();
            if (topic.length > 48) topic = topic.slice(0, 45) + '…';
            var ideaText = fmtIdea(pick(IDEA_LINES), topic);
            // Sprinkle in the actual suggestion description when there is one,
            // so the spider relays the ticket's content, not just its title.
            if (idea.desc) {
              var snippet = idea.desc.trim();
              if (snippet.length > 60) snippet = snippet.slice(0, 57) + '…';
              ideaText = ideaText + ' (' + snippet + ')';
            }
            lines.push({ spider: ideasSpider, text: ideaText, ttl: 3.6 });
            lines.push({ spider: listeners[0], text: pick(IDEA_REACTIONS), ttl: 2.2 });
          }
          // A bystander retells the reviewer's verdict moment from the run that
          // just ended — the grudging admission, the smug 'told you so', or the
          // shared L — with the rest of the cooler circle reacting. Consumed
          // once per verdict so it lands in the first gossip after the run,
          // then the office moves on. The teller is chosen from whoever is
          // already at the cooler (the participants never retell their own
          // drama), so nobody has to leave the circle.
          if (scene.verdictOutcome && !scene.verdictGossiped) {
            var reviewerSp = spiderFor('reviewer');
            var complexitySp = spiderFor('complexity');
            var coolerGroup = [bragger].concat(listeners);
            var tellers = coolerGroup.filter(function (s) { return s !== reviewerSp && s !== complexitySp && s !== ideasSpider; });
            if (tellers.length) {
              // Only consume the retelling when it actually lands — if the
              // circle happens to be all participants, the next gossip session
              // gets another shot with a different group.
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
          // The Complexity spider cuts in with its all-time verdict record
          // once it has something to brag about (only when it's already at the
          // cooler — it won't interrupt its own work to brag).
          if (verdictRecordTotal() >= 2) {
            var cpxSpider = spiderFor('complexity');
            if (cpxSpider && (cpxSpider === bragger || listeners.indexOf(cpxSpider) !== -1)) {
              lines.push({ spider: cpxSpider, text: fmt(pick(VERDICT_BRAG), 0).replace(/\{record\}/g, verdictRecordLabel()), ttl: 3.4 });
              var cpxReact = listeners[0];
              if (cpxReact === cpxSpider) cpxReact = listeners[1] || listeners[0];
              lines.push({ spider: cpxReact, text: pick(VERDICT_BRAG_REACTIONS), ttl: 2.4 });
            }
          }
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
          if (scene.coolerTrip) endCoolerTripNow(); // and the cooler trip too
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

        // ── Verifier vs Complexity standoff ────────────────────────────────
        // After an overruled verdict, the Verifier walks over and stares the
        // Complexity spider down with the code as evidence, then the Complexity
        // spider sulks back to its desk. Same walk → talk → leave flow as the
        // gossip and watching skits.
        function startStandoff() {
          if (!scene || scene.standoff || scene.glare || _replay) return; // glare is busy too
          var verifier = spiderFor('verifier');
          var complexity = spiderFor('complexity');
          if (!verifier || !complexity) return;
          if (scene.writer || scene.queue.length || vm.streamingActive) return; // never interrupt real work
          // The Verifier walks over; the Complexity spider stays at the board.
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
          // leave — verifier heads back to its seat, complexity sulks to its desk
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

        // ── Reviewer vs Complexity glare ───────────────────────────────────
        // When the reviewer's smug 'told you so' lands after an overruled
        // verdict, the two spiders stay at their own desks — same row, far
        // apart — and stare each other down across the office: a silent
        // eye-lock beat, then a couple of back-and-forth bubbles, then the
        // Complexity spider stomps home. Returns true if the glare started
        // (false → the caller falls back to the plain one-liner).
        function startGlare(revLine) {
          if (!scene || scene.glare || _replay || !vm.showMeeting) return false;
          if (scene.standoff || scene.coolerTrip || scene.gossip || scene.watching) return false;
          if (scene.writer || scene.queue.length || vm.streamingActive) return false;
          var reviewer = spiderFor('reviewer');
          var complexity = spiderFor('complexity');
          if (!reviewer || !complexity) return false;
          // Both head to their own desks — the glare happens desk-to-desk
          // across the office (home = desk, not the meeting seat row).
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
              { spider: reviewer, text: revLine, ttl: 4.0 },   // 'told you so'
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
              // Silent eye-contact beat: both spiders lock onto each other.
              g.phase = 'glare';
              g.reviewer.glaringAt = g.complexity;
              g.complexity.glaringAt = g.reviewer;
              // The reviewer trembles a little for staring down an angry
              // spider; the Complexity spider's rage shake carries its side.
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
          // leave — eyes unlock, the Complexity spider stomps back to its desk
          // (already at its desk from the walk phase, so the stomp lands with
          // a red burst in place — the beat has its punctuation).
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

        // ── Rage cooler trip ───────────────────────────────────────────────
        // When the Complexity spider's rage crosses 75 it occasionally storms
        // off to the water cooler, grabs a drink, mutters about the
        // unmanageable context, then stalks back to its desk. Same walk → talk
        // → leave flow as the other skits.
        // A random spider whose current position is close to the Complexity
        // spider's storm-off path — the witness does a double-take as he
        // storms past. Measured from the Complexity spider's desk (passed in
        // explicitly since scene.coolerTrip isn't built yet), and compared
        // against each candidate's LIVE x/y so it feels truly nearby.
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
          if (!rec && _replay) return; // during replay, start only via a recorded event
          if (!vm.showMeeting) return;
          var sp = spiderFor('complexity');
          if (!sp) return;
          if (!rec) {
            if (scene.writer || scene.queue.length || vm.streamingActive) return; // never interrupt real work
            if (scene.gossip || scene.watching || scene.standoff) return;
          }
          scene.lastLogAt = Date.now(); // trips are "alive" time — keeps banter from stomping the bubbles
          sp.state = 'walk';
          sp.target = { x: COOLER.x, y: COOLER.y };
          sp.speech = ''; sp.speechTtl = 0;
          var witness, lines, witnessText;
          if (rec) {
            // Replay: reuse the exact lines, witness, and double-take from the
            // live run so the rewatch is faithful to what actually happened.
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
            // Record the storm-off into the replay timeline so rewatching the
            // run replays the angry walk to the cooler and back.
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
            witness: witness,           // nearby spider doing a double-take
            witnessFired: false,
            witnessText: witnessText,   // pre-picked so replay matches live
            lines: lines,
            li: 0, lineTtl: 0,
            drink: 0 // 0..1 — the cup in the spider's hand fills while sipping
          };
          vm.meetingSpeaker = sp.icon + ' ' + sp.name + ' — storming off to the water cooler';
          $scope.$applyAsync();
        }

        function advanceCoolerTrip(dt) {
          if (!scene || !scene.coolerTrip) return;
          var ct = scene.coolerTrip;
          if (ct.phase === 'walk') {
            ct.ttl -= dt;
            // Mid-storm (about halfway), the nearby witness does a double-take
            // with a bubble in the Office Chat — a reaction to the walk itself.
            if (!ct.witnessFired && ct.witness && ct.ttl < 0.7) {
              ct.witnessFired = true;
              var wText = ct.witnessText || pick(DOUBLE_TAKE);
              setSpeech(ct.witness, '😳 ' + wText, 3.4, ct.witness.icon + ' ' + ct.witness.name + ' — double-take');
              if (!_replay) logGossipEntry(ct.witness.icon + ' ' + ct.witness.name, wText);
              ct.witness.reactT = 1.1;
              ct.witness.reactKind = 'bad'; // worried tremble = the double-take
              scene.lastLogAt = Date.now();
            }
            if (ct.ttl <= 0) { ct.phase = 'talk'; ct.li = 0; ct.lineTtl = 0; }
            return;
          }
          if (ct.phase === 'talk') {
            // Drink state: while the SIP line is up (li === 2 — the index
            // points at the NEXT line to show, so lines[1] is playing now),
            // the cup fills toward full; it drains as the stalk-back line
            // plays (li === 3).
            if (ct.li === 2) {
              ct.drink = Math.min(1, ct.drink + dt / 0.9);
            } else if (ct.li === 3) {
              // Drain over the full stalk-back line so the cup visibly empties
              // as the spider finishes its drink.
              ct.drink = Math.max(0, ct.drink - dt / 2.8);
            }
            ct.lineTtl -= dt;
            if (ct.lineTtl > 0) return;
            if (ct.li < ct.lines.length) {
              var ln = ct.lines[ct.li];
              setSpeech(ln.spider, ln.text, ln.ttl, ln.spider.icon + ' ' + ln.spider.name);
              // During replay the trip's chat lines were already logged live,
              // so skip them to avoid double-posting to the Office Chat.
              if (!_replay) logGossipEntry(ln.spider.icon + ' ' + ln.spider.name, ln.text);
              ct.lineTtl = ln.ttl;
              ct.li++;
            } else {
              ct.phase = 'leave';
            }
            return;
          }
          // leave — stalk back to the desk (or the seat row if a meeting is
          // still in progress, matching how the gossip skit returns home).
          var ctHome = scene.meetingOn ? ct.spider.seat : ct.spider.home;
          ct.spider.state = 'walk';
          ct.spider.target = { x: ctHome.x, y: ctHome.y };
          ct.spider.speech = ''; ct.spider.speechTtl = 0;
          // Finished the drink → one-time rage discount: the storm-off is both
          // a gag and a gameplay beat that visibly calms the spider (the meter
          // drops, the red tint fades, the rumble eases). Only a COMPLETED trip
          // pays out — an interrupted one (endCoolerTripNow) skips the reward.
          // Live only: during replay the meter is rebuilt from scratch, so a
          // negative bump would be a meaningless no-op anyway.
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

        // ── Public methods ─────────────────────────────────────────────────
        vm.openMeeting = function () {
          vm.showMeeting = true; vm.saveSettings(true);
          // Load the last persisted replay timeline (from a past run's card)
          // so the ▶ button works even after a page reload.
          if (vm._restoreMeetingReplayFromCards) vm._restoreMeetingReplayFromCards();
          // Auto-dodge: keep the panel off the Agent panel / panel columns.
          if (vm._dodgeFloatingPanel) vm._dodgeFloatingPanel(vm.meeting, { selfCls: 'meeting-floating-panel', margin: 10 });
          // Prime the AudioContext from a real click gesture so browser
          // autoplay policy doesn't suspend it (rAF-created contexts start
          // suspended and resume() fails outside a gesture).
          audioCtx();
          startLoop();
        };
        vm.closeMeeting = function () { vm.showMeeting = false; vm.saveSettings(true); stopLoop(); };
        vm.setMeetingHovered = function (on) {
          vm.meetingHovered = !!on;
          // Remember when the hover began so coolComplexityRage can scale the
          // cooldown by how long someone has been watching the desk badge.
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
            if (vm.saveSettings) vm.saveSettings(true); // persist the new position
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
            if (vm.saveSettings) vm.saveSettings(true); // persist the new size
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

        // ── Restore a persisted replay timeline (survives page reloads) ──
        // On open, if no live run has been captured this session, load the
        // most recent card that carries a recorded _meetingReplay timeline so
        // the ▶ button replays a past run instead of being empty.
        vm._restoreMeetingReplayFromCards = function () {
          if (!vm.state) return;
          if (_recording || _replay) return; // a live run or replay is in flight
          if (vm.meetingReplay && vm.meetingReplay.length) return; // already loaded
          var cols = ['done', 'doing', 'selfImproving', 'todo'];
          for (var c = 0; c < cols.length; c++) {
            var cards = vm.state[cols[c]] || [];
            for (var i = cards.length - 1; i >= 0; i--) {
              var rc = cards[i];
              if (!rc || !Array.isArray(rc._meetingReplay) || !rc._meetingReplay.length) continue;
              // Only restore timelines from cards in the currently selected
              // project — a project switch must not replay another project's run.
              if (vm.selectedProject && rc.filePath && rc.filePath !== vm.selectedProject) continue;
              vm.meetingReplay = rc._meetingReplay;
              return;
            }
          }
        };

        // Bound what we persist onto the card: the live timeline is kept in
        // full for this session, but the stored copy is capped and its log
        // entry details are trimmed so board data doesn't bloat (the full log
        // already lives in card.agentLog).
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
            s.speechTtl = 0; s.text = ''; s.progress = 0; s.reactT = 0; s.stomping = false;
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
            // reaction / stream / cooler events are transient skits — skipped on seek
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
          // The Complexity spider owns anything about the task's difficulty,
          // the accumulated context budget, or compaction of reasoning/diffs.
          // Meta-plans stay with the planner (they only MENTION a complexity
          // score); a bare 'complexity' line is the Complexity spider's turf.
          if (/meta-plan/.test(m)) return 'planner';
          if (/complexity|compacted|compact(ed|ing|s)? |context (size|accum|budget)|accumulated (reasoning|diffs?|context)|thinking context|context summarized|token cap|atomic step/.test(m)) {
            if (/context review/.test(m)) return 'explorer'; // that's the explorer's job
            return 'complexity';
          }
          if (/proposing|plan/.test(m)) return 'planner';
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

        // The Complexity spider relays the task's difficulty / context budget /
        // compaction events with maximum annoyance. {score} {label} {cap} {ctx}
        // are the live complexity values; {est}/{actual} are the estimated vs
        // executed step counts for the verdict commentary.
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
        // Count how many steps actually ran to completion this run.
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
        // ── Complexity spider anger meter ──────────────────────────────────
        // Every new step, context milestone, or compaction event makes the
        // Complexity spider visibly redder and shakier, with a rising counter
        // over its desk. Caps at 100 and resets when the plan completes.
        // The meter also COOLS: whenever the office goes quiet (no new rage
        // sources for a while), a few points per idle minute drain away, so
        // the meter breathes between bursts instead of staying pinned at 100%.
        // rageAt stamps every change so the idle clock restarts on each bump.
        var RAGE_COOL_PER_SEC = 4 / 60; // ≈4 points per idle minute
        var RAGE_COOL_IDLE_MS = 20000;  // only start cooling after ~20s of quiet
        function bumpComplexityRage(amount) {
          if (!scene) return;
          var sp = spiderFor('complexity');
          if (!sp) return;
          var before = sp.rage || 0;
          sp.rage = Math.max(0, Math.min(100, before + amount));
          sp.rageAt = Date.now();
          sp.rageDrainedAt = Date.now(); // a fresh burst restarts cooling
          // Hitting 100% vents steam — a one-shot hiss. Gated on visibility
          // (bumps also fire from watchers while the panel is closed) and on
          // the existing mute toggle via sfx().
          if (before < 100 && sp.rage >= 100 && !_replay && vm.showMeeting) {
            playSteamVent();
          }
          // Crossing a fresh 10-point bucket (10, 20, … 90) plays a short
          // rising 'creak' so the climb itself is audible — not just the
          // continuous rumble. Each bucket is a slightly higher tension tone.
          if (!_replay && vm.showMeeting) {
            var bucket = Math.floor(sp.rage / 10) * 10;
            var prevBucket = Math.floor(before / 10) * 10;
            if (bucket >= 10 && bucket < 100 && bucket > prevBucket) {
              playRageCreak(bucket);
            }
          }
          // Crossing the 75 threshold occasionally sends the spider storming
          // off to the water cooler for a drink about the unmanageable context.
          if (before < 75 && sp.rage >= 75) maybeStartCoolerTrip();
        }
        // Occasional + cooldown-gated trigger for the cooler trip. Won't
        // interrupt real work or other skits, never fires during a replay, and
        // only about half the crossings actually set off the storm-off.
        function maybeStartCoolerTrip() {
          if (!scene || _replay || !vm.showMeeting) return;
          if (scene.coolerTrip || scene.gossip || scene.watching || scene.standoff) return;
          if (scene.writer || scene.queue.length || vm.streamingActive) return;
          scene.coolerTripCd = (scene.coolerTripCd === undefined || scene.coolerTripCd === null) ? 90 : scene.coolerTripCd;
          if (scene.coolerTripCd > 0) return;
          if (Math.random() < 0.5) {
            startCoolerTrip();
          } else {
            scene.coolerTripCd = 30 + Math.random() * 30; // try again at the next crossing
          }
        }
        function resetComplexityRage() {
          if (!scene) return;
          var sp = spiderFor('complexity');
          if (sp) { sp.rage = 0; sp.rageAt = Date.now(); sp.rageDrainedAt = Date.now(); }
        }
        // Called every frame from updateScene: drain rage during idle stretches
        // (live runs only — a replay replays the run as it happened, so the
        // rewatch shows the meter exactly as it was). The gate uses the idle
        // clock (time since the LAST rage event) so a fresh step/compaction
        // instantly restarts cooling. Drainage is measured in real wall-clock
        // time since the last cooling tick (not the rAF dt, which is clamped
        // to 0.05s and lags after a backgrounded tab), so a "few points per
        // idle minute" stays true no matter what.
        function coolComplexityRage() {
          if (!scene || _replay) return;
          var sp = spiderFor('complexity');
          if (!sp || !sp.rage) return;
          var now = Date.now();
          var idleMs = now - (sp.rageAt || now);
          if (idleMs <= RAGE_COOL_IDLE_MS) {
            sp.rageDrainedAt = now; // keep the drain clock in sync while gated
            return;
          }
          var sinceDrain = now - (sp.rageDrainedAt || now);
          // The spider visibly relaxes faster when someone is actually looking:
          // the longer the mouse has been hovering the panel, the stronger the
          // cooldown — 1x at hover start, ramping to 5x after ~20s of continuous
          // attention, capped so a long idle hover can't wipe the meter in one
          // blink. Interrupting the hover returns the rate to baseline.
          var hoverFactor = 1;
          if (vm.meetingHovered && vm.meetingHoverSince) {
            hoverFactor = 1 + Math.min(4, (now - vm.meetingHoverSince) / 5000);
          }
          var before = sp.rage;
          sp.rage = Math.max(0, sp.rage - RAGE_COOL_PER_SEC * hoverFactor * (sinceDrain / 1000));
          sp.rageDrainedAt = now;
          // Rare 'calmed down' moment: when idle cooling drains the meter all
          // the way to zero, the spider is briefly at a loss. Fires once per
          // run (reset in startMeeting) — a speech bubble plus an Office Chat
          // line — then it's right back to being furious about having relaxed.
          if (before > 0 && sp.rage === 0 && !scene.calmQuipFired) {
            scene.calmQuipFired = true;
            var calm = pick(CALMED_QUIPS);
            setSpeech(sp, '🧘 ' + calm, 4.5, sp.icon + ' ' + sp.name + ' — calmed down');
            logGossipEntry(sp.icon + ' ' + sp.name, calm);
          }
        }

        // ── Rage steam wisps ───────────────────────────────────────────────
        // Tiny particles rising off the Complexity spider's head while its rage
        // is high. Emission rate tracks the meter (sparse at low anger, a steady
        // wisp at full), so as the rage drains the steam visibly thins — the
        // cooling shows even when the desk-badge counter is off-screen. Uses
        // normalized coordinates (fractions of W/H) like the confetti system.
        function updateSteam(dt) {
          if (!scene || !scene.steam) return;
          var sp = spiderFor('complexity');
          var rage = sp ? (sp.rage || 0) : 0;
          var rageFactor = Math.min(1, rage / 100);
          // Emit while angry: ~2 wisps/sec at low anger up to ~12/sec at 100%.
          if (sp && rage > 0) {
            scene._steamAcc = (scene._steamAcc || 0) + dt * (2 + rageFactor * 10);
            while (scene._steamAcc >= 1) {
              scene._steamAcc -= 1;
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
          for (var i = scene.steam.length - 1; i >= 0; i--) {
            var p = scene.steam[i];
            p.life += dt;
            if (p.life >= p.ttl) { scene.steam.splice(i, 1); continue; }
            p.x += p.vx * dt + Math.sin(p.life * p.swaySpeed + p.sway) * 0.003 * dt;
            p.y += p.vy * dt;
          }
        }

        // Make the Complexity spider actually SAY something about a complexity-
        // related log entry (speech bubble + office-chat line), not just write
        // it on the board. Returns true if it spoke.
        function complexityReactToLog(low, fromReplay) {
          if (fromReplay || !scene) return false;
          var sp = spiderFor('complexity');
          if (!sp) return false;
          var vals = complexityVals();
          var text = null;
          if (/complexity|token cap|atomic step/.test(low)) {
            // Only fire the score-aware quip once a REAL complexity rating is
            // in — otherwise a bare 'Complexity' mention (e.g. meta-plan) would
            // render '?/100' and look broken.
            if (typeof vm.complexityScore !== 'number') return false;
            text = fmtComplexity(pick(COMPLEXITY_QUIPS), vals);
            bumpComplexityRage(12); // being forced to care about the rating
          } else if (/compacted|compressed|context summarized|compact summary|compaction/.test(low)) {
            text = pick(COMPLEXITY_COMPACT);
            bumpComplexityRage(25); // compaction is personal
          }
          if (!text) return false;
          setSpeech(sp, text, 5.0, sp.icon + ' ' + sp.name);
          logGossipEntry(sp.icon + ' ' + sp.name, text);
          scene.lastLogAt = Date.now();
          return true;
        }

        // The sarcastic post-mortem line the Complexity spider writes on the
        // board last — estimate vs reality vs the user's (presumed) expectations.
        // Kept short (~100 chars) so it wraps cleanly on the whiteboard.
        function complexityPostMortemText() {
          var actual = complexityActualSteps();
          var vals = complexityVals(); // est already set from complexityAtomicSteps
          vals.actual = actual;
          var fail = !!(vm.agentResult && vm.agentResult.incomplete);
          var text = fmtComplexity(pick(fail ? COMPLEXITY_POSTMORTEM_FAIL : COMPLEXITY_POSTMORTEM), vals);
          return text.length > 100 ? text.slice(0, 97) + '…' : text;
        }

        // At plan finish the Complexity spider compares its own difficulty
        // estimate to what the verifier actually decided: smug when right,
        // sulking when overruled, furious when the task was judged incomplete.
        function complexityVerdictReact(fromReplay) {
          if (fromReplay || !scene) return;
          var sp = spiderFor('complexity');
          var est = (typeof vm.complexityAtomicSteps === 'number' && vm.complexityAtomicSteps) ? vm.complexityAtomicSteps : 0;
          var actual = complexityActualSteps();
          // ── Track the verdict record REGARDLESS of panel visibility ─────
          // The win/loss tally is a persistent cross-run stat, so it must count
          // every finished run — even ones where the Meeting View was closed.
          // Only the visual reaction (speech/rage) is gated on visibility below.
          var outcome;
          var right = false;
          if (vm.agentResult && vm.agentResult.incomplete) {
            outcome = 'fail';
          } else {
            // Right or wrong about the step count: within ~1 step (or no
            // estimate at all) counts as right — the verifier agreed.
            var off = est > 0 ? Math.abs(actual - est) : 0;
            right = est === 0 ? actual <= 4 : off <= 1;
            outcome = right ? 'right' : 'wrong';
          }
          scene.verdictOutcome = outcome;
          scene.verdictGossiped = false; // a fresh verdict is retell-worthy again
          if (outcome === 'fail') _verdictRecord.fail = (_verdictRecord.fail || 0) + 1;
          else if (outcome === 'right') _verdictRecord.right = (_verdictRecord.right || 0) + 1;
          else _verdictRecord.wrong = (_verdictRecord.wrong || 0) + 1;
          saveVerdictRecord();
          // ── Visual reaction (only when the panel is actually visible) ────
          if (!vm.showMeeting || !sp) return;
          var vals = complexityVals();
          vals.actual = actual; // est already set by complexityVals()
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
          bumpComplexityRage(right ? -8 : 18); // being right cools the rage a little
          scene.lastLogAt = Date.now();
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
          // The Complexity spider cuts in (angrily) whenever the task's
          // difficulty or a context compaction shows up in the log. Runs AFTER
          // the write so its speech bubble wins over the writer's "reading"
          // bubble — the board still gets the actual text written.
          if (parsed.role === 'complexity') complexityReactToLog(low, fromReplay);
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
            _recordingCardId = vm.activeCardId || null;
            _recording.push({ t: Date.now(), type: 'start' });
          }
          scene.meetingOn = true;
          scene.done = false;
          scene.boardLines = [];
          scene.queue = [];
          scene.writer = null;
          scene.confetti = []; // clear leftover confetti from a previous run
          scene.postMortem = null; // clear the est-vs-actual chart
          // A fresh run resets the Complexity spider's anger meter — a new
          // task starts with a clean (if skeptical) slate.
          if (!fromReplay) {
            resetComplexityRage();
            scene.calmQuipFired = false; // allow the 'calmed down' line again
          }
          // Only a fresh LIVE run resets the ticker — replays reuse the last
          // live history so the rewatch keeps the step outcomes visible.
          if (!fromReplay) vm.meetingTicker = [];
          scene.activeRole = 'planner';
          scene.lastLogAt = Date.now();
          scene.streamReadCd = 0;
          scene.banterCd = 2;
          scene.gossip = null;
          scene.gossipCd = 12;
          scene.coolerTrip = null; // no storm-offs on a fresh run
          scene.coolerTripCd = 90;
          scene.glare = null;      // no stare-downs on a fresh run
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
            // Persist the timeline onto the card so the spider-meeting replay
            // survives page reloads and works for past runs. Cards are stored
            // as raw JSON board data, so attaching _meetingReplay persists it
            // via vm.saveCards() with no backend change.
            if (_recordingCardId) {
              var repCard = vm.findCardById ? vm.findCardById(_recordingCardId) : null;
              if (repCard) {
                repCard._meetingReplay = boundedReplayForStorage(vm.meetingReplay);
                if (vm.saveCards) vm.saveCards();
              }
            }
            _recordingCardId = null;
          }
          // Little celebration chime when the plan completes. Only play when
          // the panel is actually visible — finishMeeting is driven by the
          // log/streaming watches, which fire even while the panel is closed.
          if (vm.showMeeting) playChime();
          // Let the reviewer write the verdict on the board, then the Complexity
          // spider writes its sarcastic post-mortem LAST (queued behind the
          // reviewer, so it walks to the board after the verdict lands).
          // enqueueWrite bails once scene.done is true, so flip the flag AFTER
          // both writes are queued.
          var reviewer = spiderFor('reviewer');
          if (reviewer) {
            enqueueWrite('reviewer', '✅ Plan looks good — task complete!');
          }
          var complexitySpider = spiderFor('complexity');
          if (complexitySpider && typeof vm.complexityScore === 'number' && vm.complexityScore >= 0) {
            // Remember the estimate vs reality (and the exact composed text) so
            // the whiteboard can draw the est-vs-actual bar chart and trigger
            // the angry stomp only when THIS post-mortem write actually lands —
            // never when some earlier complexity log line completes writing.
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
          // Confetti burst across the whole canvas — every spider throws a
          // handful before heading back to their desk.
          spawnConfetti();
          // Everyone celebrates and walks home shortly after. The Complexity
          // spider's rage meter resets — the plan is done, its suffering ends.
          resetComplexityRage();
          scene.spiders.forEach(function (s) {
            if (s.role !== 'reviewer') {
              s.state = 'celebrate';
              s.celebrateT = 0.9 + Math.random() * 0.8;
              s.speech = '🎉';
              s.speechTtl = 2.5;
            }
          });
          // The Complexity spider sizes itself up against the verifier's verdict
          // AFTER the celebration loop so its speech bubble wins the spotlight.
          complexityVerdictReact(fromReplay);
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
          // No more frames → stop the persistent rage rumble so it can't
          // keep droning after the panel closes or the view is destroyed.
          stopRageRumble();
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
              else if (ev.type === 'cooler') startCoolerTrip(ev); // replay the storm-off
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

          // The Complexity spider's anger slowly cools whenever the office
          // goes quiet — a few points per idle minute, so the meter breathes
          // between bursts instead of staying pinned at 100%.
          coolComplexityRage();
          // Steam wisps off its head while the meter is high — they thin as the
          // rage drains, making the cooling visible at a glance.
          updateSteam(dt);
          // Its rage also has a voice: a low rumble that swells with the meter
          // (silenced when muted, panel hidden, or rage at 0).
          updateRageRumble();

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
                else {
                  // The Complexity spider's angry stomp lands with a burst.
                  if (s.stomping) {
                    s.stomping = false;
                    stompLand(s);
                  }
                  s.state = 'idle';
                }
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
              // The Complexity spider's post-mortem is the last line written —
              // once THIS exact write lands (matched by its composed text, so
              // an earlier complexity log line never triggers it), the chart
              // appears in the corner. When the verifier overruled the
              // estimate, a two-spider standoff follows: the Verifier walks
              // over and stares it down, then the Complexity spider sulks home.
              // Otherwise it just stomps angrily back to its own desk.
              if (w.role === 'complexity' && scene.postMortem && w.text === scene.postMortem.text) {
                scene.postMortem.shown = true;
                if (scene.verdictOutcome === 'wrong' && !_replay) {
                  // Stay at the board — the Verifier comes to us.
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
                // The reviewer reacts to the Complexity spider's verdict from
                // the board, right after its own verdict line lands: grudging
                // 'fine, you were right' when the estimate was right, smug
                // 'told you so' when it was overruled. Both go to the Office
                // Chat. Matches the exact verdict text so an earlier reviewer
                // write (a step verdict during the run) never triggers it.
                if (!_replay && vm.showMeeting && w.role === 'reviewer' && w.text === '✅ Plan looks good — task complete!' && scene.verdictOutcome) {
                  var revVals = complexityVals();
                  revVals.actual = complexityActualSteps();
                  var revLine = fmtComplexity(
                    pick(scene.verdictOutcome === 'right' ? REVIEWER_GRUDGE : (scene.verdictOutcome === 'fail' ? REVIEWER_SMUG_FAIL : REVIEWER_SMUG)),
                    revVals);
                  // A smug 'told you so' escalates into a desk-to-desk glare:
                  // the two lock eyes across the office, trade a couple of
                  // bubbles, then the Complexity spider stomps home. Falls back
                  // to the plain one-liner if the office is busy.
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
                // File > endpoint > steps > config priority, so the most concrete
                // bit of context gets the spotlight; the IT Specialist chimes in
                // with the boring config details when there's nothing more
                // exciting happening.
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
                  // A task is (or was) being rated — the Complexity spider has
                  // opinions about that, and will not be ignored.
                  joker = spiderFor('complexity') || randomSpider();
                  joke = fmtComplexity(pick(COMPLEXITY_QUIPS), complexityVals());
                } else {
                  var cfg = settingsContext();
                  if (cfg.ready) {
                    joker = spiderFor('itspecialist') || randomSpider();
                    joke = fmtBanter(pick(BANTER_CONFIG), cfg);
                  } else {
                    joker = randomSpider();
                    joke = streaming ? pick(BANTER_STREAM) : pick(BANTER_IDLE);
                  }
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

          // ── Rage cooler trip: the Complexity spider storms off for a drink ─
          // Triggered when its rage crosses 75; driven per-frame like the other
          // skits. Real work interrupts it — the spider stalks straight home.
          // Runs in BOTH live and replay modes: a recorded 'cooler' event
          // starts the trip during a rewatch, and the same per-frame driver
          // walks it to the cooler and back (real work from the timeline
          // interrupts it exactly like live).
          if (scene.coolerTrip) {
            if (scene.writer || scene.queue.length || vm.streamingActive) {
              endCoolerTripNow();
            } else {
              advanceCoolerTrip(dt);
            }
          }

          // ── Verifier vs Complexity standoff ──────────────────────────────
          // Driven per-frame like the other skits; real work interrupts it.
          if (scene.standoff) {
            if (scene.writer || scene.queue.length || vm.streamingActive) {
              endStandoffNow();
            } else {
              advanceStandoff(dt);
            }
          }

          // ── Reviewer vs Complexity glare ─────────────────────────────────
          // After the reviewer's 'told you so' the two stare each other down
          // from across the office; the Complexity spider stomps home after.
          if (scene.glare) {
            if (scene.writer || scene.queue.length || vm.streamingActive) {
              endGlareNow();
            } else {
              advanceGlare(dt);
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
          } else if (!scene.writer && !scene.queue.length && !vm.streamingActive && !vm.meetingHovered && !scene.coolerTrip) {
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
        // Scale any base pixel size up/down by the panel-wide text-size setting
        // (relative to the 12px default), so canvas text grows with the chrome.
        var mf = function (px) { return Math.max(6, Math.round(px * (vm.meetingFontSize || 12) / 12)); };
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
          drawSteam(W, H);
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
            ctx.font = 'bold ' + Math.round(bh * 0.42 * (vm.meetingFontSize || 12) / 12) + 'px sans-serif';
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

        function drawSteam(W, H) {
          if (!scene.steam || !scene.steam.length) return;
          for (var i = 0; i < scene.steam.length; i++) {
            var p = scene.steam[i];
            var fadeIn = Math.min(1, p.life * 3);     // quick pop-in
            var fadeOut = Math.max(0, 1 - p.life / p.ttl); // then dissolve
            var grow = 1 + p.life * 0.9;              // wisps expand as they rise
            ctx.globalAlpha = fadeIn * fadeOut * 0.5;
            ctx.fillStyle = '#efe9dc';                // pale steam, no hard edge
            ctx.beginPath();
            ctx.arc(p.x * W, p.y * H, p.size * W * grow, 0, 6.283);
            ctx.fill();
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

          // Steam wisp — a gentle rising curl while the cooler is in use (the
          // Complexity spider is sipping), so the drink is visible on the
          // cooler itself, not just in the spider's hand.
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
          // Reserve the bottom-right corner for the post-mortem chart ONLY while
          // the chart is actually visible — during the run, text keeps full
          // width. Wrapping happens per-frame, so narrowing at chart time
          // re-wraps the existing lines cleanly around it.
          var chartW = 52, chartH = 34;
          var chartShown = !!(scene && scene.postMortem && scene.postMortem.shown);
          var maxChars = Math.max(8, Math.floor((bw - padX * 2 - (chartShown ? chartW + 4 : 0)) / mf(9)));
          var lines = scene ? scene.boardLines.slice(-6) : [];
          ctx.font = 'bold ' + mf(11) + 'px monospace';
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

          // Post-mortem est-vs-actual chart: a tiny two-bar graphic in the
          // bottom-right corner of the board, once the Complexity spider has
          // written its autopsy line. Stays until the next run starts.
          if (chartShown) {
            var pm = scene.postMortem;
            var cx = bx + bw - chartW - 8, cy = by + bh - chartH - 6;
            var maxVal = Math.max(1, pm.est, pm.actual);
            // Panel backdrop (tall enough for title + bars + labels inside).
            ctx.fillStyle = 'rgba(255,255,255,0.55)';
            rr(cx - 4, cy - 16, chartW + 8, chartH + 22, 4); ctx.fill();
            ctx.strokeStyle = 'rgba(0,0,0,0.25)'; ctx.lineWidth = 1; ctx.stroke();
            ctx.font = 'bold ' + mf(8) + 'px sans-serif';
            ctx.fillStyle = '#8b4a4a';
            ctx.textAlign = 'left';
            ctx.fillText('EST vs ACTUAL', cx, cy - 9);
            // Bars (base line above where the labels sit, so nothing clips).
            var bw2 = 16, bh2 = chartH - 14;
            var bx2 = cx + 6, by2 = cy + chartH - 8;
            ctx.fillStyle = '#c0392b';
            ctx.fillRect(bx2, by2 - bh2 * (pm.est / maxVal), bw2, bh2 * (pm.est / maxVal));
            ctx.fillStyle = '#e67e22';
            ctx.fillRect(bx2 + bw2 + 8, by2 - bh2 * (pm.actual / maxVal), bw2, bh2 * (pm.actual / maxVal));
            // Labels inside the panel, under the bars.
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

        // Blend two hex colors by t (0..1) — used to turn the Complexity spider
        // redder as its anger meter climbs.
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
            // Desk base
            ctx.fillStyle = 'rgba(0,0,0,0.3)';
            rr(dx - 24, dy - 6, 48, 18, 4); ctx.fill();
            ctx.fillStyle = '#2b2117';
            rr(dx - 26, dy - 8, 52, 18, 4); ctx.fill();
            ctx.strokeStyle = 'rgba(255,255,255,0.1)'; ctx.lineWidth = 1; ctx.stroke();
            // Monitor
            ctx.fillStyle = '#0d1424';
            rr(dx - mf(10), dy - mf(24), mf(20), mf(14), 2); ctx.fill();
            ctx.strokeStyle = 'rgba(255,255,255,0.2)'; ctx.stroke();
            // tiny screen glow
            ctx.fillStyle = s.color;
            ctx.globalAlpha = 0.5;
            ctx.fillRect(dx - mf(7), dy - mf(21), mf(14), 2);
            ctx.globalAlpha = 1;
            // Name tag — a standing name-plate on every desk: the spider's
            // icon + name, so each desk advertises exactly who sits there.
            var nLabel = s.name.toUpperCase();
            ctx.font = 'bold ' + mf(8) + 'px sans-serif';
            var nW = Math.ceil(ctx.measureText(nLabel).width) + mf(20);
            var nH = mf(13);
            var nY = dy - mf(52);
            // Clamp so edge desks (Explorer, Verifier, …) never push the plate
            // off-screen even at large text sizes.
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
            // Rage counter — a tiny badge over the Complexity spider's desk that
            // climbs with every step / context milestone / compaction event.
            // Sits one row below the name-plate so the two never collide.
            if (s.role === 'complexity' && Math.round(s.rage) > 0) {
              var bw = mf(24), bh = mf(13);
              var flash = (s.rage >= 100 && Math.floor(Date.now() / 300) % 2 === 0);
              ctx.fillStyle = flash ? 'rgba(220,30,30,0.9)' : 'rgba(0,0,0,0.6)';
              rr(dx - bw / 2, dy - mf(38), bw, bh, 4); ctx.fill();
              ctx.strokeStyle = 'rgba(255,255,255,0.35)'; ctx.lineWidth = 1; ctx.stroke();
              ctx.font = 'bold ' + mf(9) + 'px sans-serif';
              ctx.fillStyle = s.rage >= 100 ? '#ffd0d0' : '#ff9a9a';
              ctx.textAlign = 'center';
              ctx.fillText((s.rage >= 100 ? '💢 ' : '🔥 ') + Math.round(s.rage) + '%', dx, dy - mf(27));
              ctx.textAlign = 'left';
            }
            // Sound indicator — a tiny ON/OFF plaque beside the rage counter so
            // users discover the header mute toggle controls the office sounds.
            if (s.role === 'complexity') {
              var sLabel = 'SOUND: ' + (vm.meetingMuted ? 'OFF' : 'ON');
              ctx.font = 'bold ' + mf(8) + 'px sans-serif';
              var sW = Math.ceil(ctx.measureText(sLabel).width) + 12;
              var sH = mf(13);
              // Beside the rage badge when it's up, centered otherwise.
              var sX = Math.round(s.rage) > 0 ? dx + mf(15) : dx - sW / 2;
              ctx.fillStyle = 'rgba(0,0,0,0.6)';
              rr(sX, dy - mf(38), sW, sH, 4); ctx.fill();
              ctx.strokeStyle = vm.meetingMuted ? 'rgba(248,113,113,0.55)' : 'rgba(74,222,128,0.55)';
              ctx.lineWidth = 1; ctx.stroke();
              ctx.fillStyle = vm.meetingMuted ? '#f87171' : '#4ade80';
              ctx.textAlign = 'center';
              ctx.fillText(sLabel, sX + sW / 2, dy - mf(27));
              ctx.textAlign = 'left';
            }
            // Verdict track record — a tiny win/loss plaque pinned above the
            // Complexity spider's desk ('called it: 3-1') that updates after
            // every task so it can brag about its historical accuracy.
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
          // The Complexity spider's angry stomp: heavier, angrier gait — big
          // slamming bobs instead of the gentle walk wiggle.
          if (s.state === 'walk') {
            bob = s.stomping
              ? -Math.abs(Math.sin(s.walkPhase * 2.4)) * 4.2 * scale
              : Math.sin(s.walkPhase * 2) * 1.5 * scale;
          }
          else if (s.state === 'celebrate') bob = -Math.abs(Math.sin(s.celebrateT * 9)) * 6 * scale;
          else bob = Math.sin(s.walkPhase) * 1.2 * scale;
          // Reaction: happy hop for a landed step, worried tremble for a fail.
          // The tremble scales with rage, so only the (angry) Complexity spider
          // shakes hard on landing — other spiders keep the normal tremble.
          if (s.reactT > 0) {
            if (s.reactKind === 'good') {
              bob -= Math.abs(Math.sin(s.reactT * 16)) * 5 * scale;
            } else {
              tremble = Math.sin(s.reactT * 40) * (1.6 + (s.rage || 0) / 40) * scale;
            }
          }
          // Anger meter: the Complexity spider reddens and shakes as its rage
          // climbs. rageFactor 0..1 → blend toward a hot red and add a jittery
          // shake that gets faster and wider with every point of anger.
          var rage = s.rage || 0;
          var rageFactor = Math.min(1, rage / 100);
          var rageShake = 0;
          if (rageFactor > 0) {
            // Shared with updateRageRumble so the audio wobble always matches
            // the on-screen tremble. Pixel amplitude scales the wave here.
            rageShake = rageShakeWave(Date.now() / 1000, s.walkPhase, rageFactor) * (0.6 + rageFactor * 2.6) * scale;
          }
          var px = s.x * W + tremble + rageShake;
          var cy = py + bob;
          var bodyColor = rageFactor > 0 ? blendHex(s.color, '#ff3b30', rageFactor) : s.color;

          // Shadow
          ctx.fillStyle = 'rgba(0,0,0,0.3)';
          ctx.beginPath();
          ctx.ellipse(px, py + bodyH * 0.7, bodyW * 0.9, bodyH * 0.28, 0, 0, 6.283);
          ctx.fill();

          // Legs (8 small legs, 4 per side) — wiggle while walking
          ctx.strokeStyle = bodyColor;
          ctx.lineWidth = Math.max(1.5, 2 * scale);
          ctx.lineCap = 'round';
          var legSwing = s.state === 'walk' ? Math.sin(s.walkPhase) : Math.sin(s.walkPhase * 0.6) * 0.35;
          for (var i = 0; i < 4; i++) {
            var attachY = cy - bodyH * 0.3 + (i / 3) * bodyH * 0.7;
            var off = 8 + i * 3;
            // Stomping legs stamp wider and more erratically — pure anger.
            var stompLegs = s.stomping ? 7 * scale : 4 * scale;
            var sway = legSwing * (i % 2 === 0 ? 1 : -1) * stompLegs;
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
            ctx.strokeStyle = bodyColor;
            ctx.lineWidth = Math.max(1.5, 2 * scale);
            ctx.lineCap = 'round';
            // raised arm (front, toward the user)
            ctx.beginPath();
            ctx.moveTo(px + bodyW * 0.3, cy - bodyH * 0.2);
            ctx.lineTo(px + bodyW * 0.55, cy - bodyH * 1.15 + waveSwing);
            ctx.stroke();
            // little hand
            ctx.fillStyle = bodyColor;
            ctx.beginPath();
            ctx.arc(px + bodyW * 0.55, cy - bodyH * 1.15 + waveSwing, 2.4 * scale, 0, 6.283);
            ctx.fill();
          }

          // Rage aura — a faint red glow that grows with the anger meter. Drawn
          // BEFORE the body so it reads as a halo behind the spider rather than
          // a tint over it.
          if (rageFactor > 0) {
            ctx.globalAlpha = 0.12 + rageFactor * 0.18;
            ctx.fillStyle = '#ff3b30';
            ctx.beginPath();
            ctx.arc(px, cy, (bodyW * 0.9) * (1 + rageFactor * 0.5), 0, 6.283);
            ctx.fill();
            ctx.globalAlpha = 1;
          }

          // Body: one big block
          ctx.fillStyle = bodyColor;
          rr(px - bodyW / 2, cy - bodyH / 2, bodyW, bodyH, 6 * scale);
          ctx.fill();
          ctx.strokeStyle = 'rgba(0,0,0,0.35)';
          ctx.lineWidth = 1;
          ctx.stroke();
          // Shine
          ctx.fillStyle = 'rgba(255,255,255,0.28)';
          rr(px - bodyW / 2 + 3 * scale, cy - bodyH / 2 + 2 * scale, bodyW * 0.4, bodyH * 0.28, 3 * scale);
          ctx.fill();
          // Eyes (look toward target, or UP at the user when waving, or lock
          // onto the other spider during a glare — narrowed for the stare-down)
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

          // Marker pen when writing
          if (scene.writer === s && s.state === 'write') {
            ctx.strokeStyle = '#222';
            ctx.lineWidth = 2.5 * scale;
            ctx.beginPath();
            ctx.moveTo(px + bodyW * 0.3, cy);
            ctx.lineTo(px + bodyW * 0.7, cy + 6 * scale);
            ctx.stroke();
          }

          // Drink state: the Complexity spider holds a little cup while at the
          // cooler, and the liquid fills as it sips (drink 0..1). Drawn in the
          // front 'hand' so the sip is part of the animation, not just dialog.
          var ct = scene ? scene.coolerTrip : null;
          if (s.role === 'complexity' && ct && ct.spider === s && ct.drink > 0) {
            var cupW = 7 * scale, cupH = 9 * scale;
            var cupX = px + bodyW * 0.55, cupY = cy + bodyH * 0.12;
            // arm to the cup
            ctx.strokeStyle = bodyColor;
            ctx.lineWidth = Math.max(1.5, 2 * scale);
            ctx.lineCap = 'round';
            ctx.beginPath();
            ctx.moveTo(px + bodyW * 0.3, cy - bodyH * 0.1);
            ctx.lineTo(cupX + cupW / 2, cupY - cupH * 0.5);
            ctx.stroke();
            // cup body
            ctx.fillStyle = '#ffffff';
            rr(cupX, cupY - cupH, cupW, cupH, 2 * scale); ctx.fill();
            ctx.strokeStyle = 'rgba(0,0,0,0.35)'; ctx.lineWidth = 1; ctx.stroke();
            // liquid fill (rises with drink)
            var fillH = cupH * 0.8 * ct.drink;
            if (fillH > 0.5) {
              ctx.fillStyle = '#7dd3fc';
              rr(cupX + 1.2 * scale, cupY - cupH * 0.2 - fillH, cupW - 2.4 * scale, fillH, 1 * scale);
              ctx.fill();
            }
          }

          // Speech bubble
          if (s.speech && s.speechTtl > 0) {
            drawSpeechBubble(W, H, px, cy - bodyH, s.speech);
          }
        }

        function drawSpeechBubble(W, H, px, py, text) {
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
              if (status === 'done' || status === 'applied' || status === 'created' || status === 'ok') {
                if (scene && scene.meetingOn && prev !== 'done') {
                  fireReaction('good', pick(REACT_SUCCESS));
                  pushTicker('good', tickerLabelForStep(st));
                }
                // Skipped steps don't add complexity (and a replay of a finished
                // run must not re-angrify the spider — live runs only).
                if (!_replay) bumpComplexityRage(3); // another step done
              } else if (status === 'error' || status === 'rejected' || status === 'failed') {
                if (scene && scene.meetingOn) {
                  fireReaction('bad', pick(REACT_FAIL));
                  pushTicker('bad', tickerLabelForStep(st));
                }
                if (!_replay) bumpComplexityRage(10); // a failed step is complex
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

        // The Complexity spider gripes as the accumulated context grows past
        // round thresholds — one grumble per 10k-token bucket so it never
        // spams. It's allowed to talk while other spiders write (setSpeech is
        // per-spider); it only stays quiet when it's the active writer itself,
        // when the user is hovering (watching skit wins), or during a replay.
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
          bumpComplexityRage(10); // every 10k tokens of context, more rage
        });
        // Reset the griped-bucket tracker on a fresh run (context resets to 0).
        $scope.$watch(function () { return vm.streamingActive; }, function (val, prev) {
          if (val && !prev) _ctxGripedAt = 0;
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
          if (val) { startLoop(); if (vm._restoreMeetingReplayFromCards) vm._restoreMeetingReplayFromCards(); }
          else stopLoop();
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
