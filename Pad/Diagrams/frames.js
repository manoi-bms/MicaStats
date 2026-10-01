"use strict";
// A hidden page never gets animation frames; d3 (Markmap's transitions) waits for them.
window.requestAnimationFrame = cb => setTimeout(() => cb(performance.now()), 0);
window.cancelAnimationFrame = id => clearTimeout(id);
