"use strict";
// MathJax reads this before it loads: no menus, no accessibility extras (they would fetch from a CDN), no typesetting of the page.
window.MathJax = {
  startup: { typeset: false },
  options: { enableMenu: false, enableEnrichment: false, enableComplexity: false, enableExplorer: false, enableAssistiveMml: false },
  svg: { fontCache: "local" }
};
