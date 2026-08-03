// error-core.js
// Pure, dependency-free helpers behind the global error-surfacing feature in
// app.js. Loaded in the browser as window.WeaverErrorCore and required as a
// CommonJS module from Node for unit tests (tests/js/error-core.test.js).
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.WeaverErrorCore = factory();
  }
}(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  var DEFAULT_BURST_WINDOW_MS = 3000;
  var DEFAULT_MAX_KEYS = 300;

  // Parse the first app-frame out of a stack trace.
  // Supports the common browser formats:
  //   Chrome/Edge:  "at vm.stopAgent (agent.js:657:80)"
  //   Firefox:      "at vm.stopAgent (agent.js:657:80)"  or legacy "fn@file:///.../agent.js:657:80"
  //   Safari:       "fn@http://localhost:8080/wwwroot/agent.js:657:80"
  //   Bare:         "agent.js:657:80"
  // Returns { file, line, col, full } or null. `file` is the basename
  // (handles / and \ separators), `full` is the raw path as it appeared in
  // the stack (for the snippet API). Legacy @-prefixed function names are
  // stripped so `full` stays a clean path.
  function parseStack(stack) {
    if (typeof stack !== 'string' || !stack) return null;
    var m = stack.match(/\(([^)\n]*\.js):(\d+):(\d+)\)/);
    if (!m) m = stack.match(/(?:^|\s|@)([^ \n()@]*\.js):(\d+):(\d+)/);
    if (!m) return null;
    return { file: m[1].split(/[\\/]/).pop(), line: m[2], col: m[3], full: m[1] };
  }

  // Should this error be ignored entirely (no toast, no record)?
  // Malformed errors, intentional aborts, and cross-origin "Script error."
  // noise are all filtered out here.
  function shouldFilter(err) {
    if (!err || typeof err.message !== 'string' || !err.message) return true;
    if (err.name === 'AbortError' || err.message === 'Script error.') return true;
    return false;
  }

  // Build the dedupe key for an error — same message at the same file:line
  // is treated as the same incident for burst suppression.
  function makeErrorKey(err, loc) {
    return ((err && err.message) || '?') + '|' + (loc ? loc.file + ':' + loc.line : '');
  }

  // Stateful burst-suppression + occurrence counting. `isBurst` tells the
  // caller whether the same incident already surfaced within `windowMs`;
  // `hit` counts every occurrence (even burst-suppressed ones) so UI can show
  // "×N"; `record` stamps the last-surfaced time and caps the map size.
  function createDedupe(options) {
    options = options || {};
    var windowMs = typeof options.windowMs === 'number' ? options.windowMs : DEFAULT_BURST_WINDOW_MS;
    var maxKeys = typeof options.maxKeys === 'number' ? options.maxKeys : DEFAULT_MAX_KEYS;
    var lastSeen = {}; // key → timestamp of last surfaced occurrence
    var hits = {};     // key → total occurrences seen

    function hit(key) {
      hits[key] = (hits[key] || 0) + 1;
      return hits[key];
    }

    function hitsOf(key) {
      return hits[key] || 0;
    }

    function isBurst(key, now) {
      var t = lastSeen[key];
      return !!(t && now - t < windowMs);
    }

    function record(key, now) {
      if (Object.keys(lastSeen).length > maxKeys) lastSeen = {}; // keep the map small
      lastSeen[key] = now;
    }

    function reset() {
      lastSeen = {};
      hits = {};
    }

    return {
      hit: hit,
      hitsOf: hitsOf,
      isBurst: isBurst,
      record: record,
      reset: reset
    };
  }

  return {
    parseStack: parseStack,
    shouldFilter: shouldFilter,
    makeErrorKey: makeErrorKey,
    createDedupe: createDedupe
  };
}));
