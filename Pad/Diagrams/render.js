"use strict";
// MicaPad's diagram page. The app posts { id, kind, source, dark, fg, bg }; the page answers
// { id, ok: true, svg, png, width, height } or { id, ok: false, error }. kind is "mermaid", "dot",
// "markmap" or "svg" (a picture Kroki drew). Nothing here reaches the network: the page's
// Content-Security-Policy and the app's request filter both refuse it.
(function () {
  const SVG_NS = "http://www.w3.org/2000/svg";
  const SCALE = 2;
  const MAX_SIDE = 4096;
  let counter = 0;
  let viz = null;

  const stage = () => document.getElementById("stage");

  // Lengths in px; Graphviz and Kroki often write pt.
  function length(text) {
    const m = /^\s*([0-9.]+)\s*(px|pt)?\s*$/.exec(text || "");
    if (!m) return 0;
    const n = parseFloat(m[1]);
    return m[2] === "pt" ? n * 4 / 3 : n;
  }

  // Drops what could run where the markup is opened later (Save as SVG writes it): script elements,
  // every on* attribute, and href / xlink:href links to javascript: (spaces and controls ignored).
  function disarm(el) {
    for (const node of [el, ...el.querySelectorAll("*")]) {
      if (node.localName.toLowerCase() === "script") {
        node.remove();
        continue;
      }
      for (const a of Array.from(node.attributes)) {
        const name = a.localName.toLowerCase();
        if (name.startsWith("on")
            || (name === "href" && a.value.replace(/[\u0000-\u0020]/g, "").toLowerCase().startsWith("javascript:"))) {
          node.removeAttributeNode(a);
        }
      }
    }
  }

  // Gives the picture a fixed size in px (the viewBox's, else its width and height) and returns its markup.
  // The markup is parsed, never put into the page, so nothing in a picture runs or loads.
  function finish(svgText) {
    const doc = new DOMParser().parseFromString(svgText, "image/svg+xml");
    const el = doc.documentElement;
    if (!el || el.localName !== "svg" || doc.getElementsByTagName("parsererror").length) {
      throw new Error("The picture could not be read.");
    }
    disarm(el);
    let w = 0, h = 0;
    const box = (el.getAttribute("viewBox") || "").trim().split(/[\s,]+/).map(Number);
    if (box.length === 4 && box[2] > 0 && box[3] > 0) { w = box[2]; h = box[3]; }
    else { w = length(el.getAttribute("width")); h = length(el.getAttribute("height")); }
    if (!(w > 0 && h > 0)) throw new Error("The picture has no size.");
    el.setAttribute("width", String(w));
    el.setAttribute("height", String(h));
    el.style.removeProperty("max-width");
    if (!el.getAttribute("xmlns")) el.setAttribute("xmlns", SVG_NS);
    return { svg: new XMLSerializer().serializeToString(el), width: w, height: h };
  }

  async function drawMermaid(req) {
    mermaid.initialize({
      startOnLoad: false,
      theme: req.dark ? "dark" : "default",
      securityLevel: "strict",
      fontFamily: "\"Segoe UI\", sans-serif"
    });
    const id = "mermaid-" + (++counter);
    try {
      const result = await mermaid.render(id, req.source, stage());
      return finish(result.svg);
    } finally {
      for (const leftover of [document.getElementById(id), document.getElementById("d" + id)]) {
        if (leftover) leftover.remove();
      }
    }
  }

  async function drawDot(req) {
    if (!viz) viz = await Viz.instance();
    const text = viz.renderString(req.source, {
      format: "svg",
      graphAttributes: { bgcolor: "transparent", fontcolor: req.fg, fontname: "Helvetica,Arial,sans-serif" },
      nodeAttributes: { color: req.fg, fontcolor: req.fg, fontname: "Helvetica,Arial,sans-serif" },
      edgeAttributes: { color: req.fg, fontcolor: req.fg, fontname: "Helvetica,Arial,sans-serif" }
    });
    return finish(text.substring(text.indexOf("<svg")));
  }

  // Markmap starts its d3 transitions (duration 0) without waiting for them; let every one finish.
  async function settle(el) {
    for (let i = 0; i < 50; i++) {
      d3.timerFlush();
      let busy = false;
      el.querySelectorAll("*").forEach(n => {
        if (n.__transition && Object.keys(n.__transition).length) busy = true;
      });
      if (!busy) return;
      await new Promise(resolve => setTimeout(resolve, 10));
    }
  }

  async function drawMarkmap(req) {
    const { Transformer, Markmap } = window.markmap;
    const { root } = new Transformer([]).transform(req.source);
    const el = document.createElementNS(SVG_NS, "svg");
    el.setAttribute("width", "1600");
    el.setAttribute("height", "1200");
    el.style.color = req.fg;
    el.style.setProperty("--markmap-text-color", req.fg);
    el.style.setProperty("--markmap-circle-open-bg", req.bg);
    stage().appendChild(el);
    let mm = null;
    try {
      mm = new Markmap(el, { duration: 0, autoFit: false });
      await mm.setData(root);
      await settle(el);
      const r = mm.state.rect;
      const pad = 8;
      const w = r.x2 - r.x1 + 2 * pad, h = r.y2 - r.y1 + 2 * pad;
      el.querySelector("g").removeAttribute("transform");
      el.setAttribute("viewBox", (r.x1 - pad) + " " + (r.y1 - pad) + " " + w + " " + h);
      return finish(new XMLSerializer().serializeToString(el));
    } finally {
      if (mm) mm.destroy();
      el.remove();
    }
  }

  // The picture as a PNG, SCALE times its size, its longest side at most MAX_SIDE pixels.
  async function toPng(svg, width, height) {
    const scale = Math.min(SCALE, MAX_SIDE / Math.max(width, height));
    const pw = Math.max(1, Math.round(width * scale));
    const ph = Math.max(1, Math.round(height * scale));
    const img = new Image();
    await new Promise((resolve, reject) => {
      img.onload = resolve;
      img.onerror = () => reject(new Error("The picture could not be made."));
      img.src = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(svg);
    });
    const canvas = document.createElement("canvas");
    canvas.width = pw;
    canvas.height = ph;
    canvas.getContext("2d").drawImage(img, 0, 0, pw, ph);
    const url = canvas.toDataURL("image/png");
    return url.substring(url.indexOf(",") + 1);
  }

  async function draw(req) {
    const drawn = req.kind === "mermaid" ? await drawMermaid(req)
      : req.kind === "dot" ? await drawDot(req)
      : req.kind === "markmap" ? await drawMarkmap(req)
      : req.kind === "svg" ? finish(req.source)
      : null;
    if (!drawn) throw new Error("Unknown diagram kind.");
    const png = await toPng(drawn.svg, drawn.width, drawn.height);
    return { id: req.id, ok: true, svg: drawn.svg, png, width: drawn.width, height: drawn.height };
  }

  function message(x) {
    const text = String((x && x.message) || x || "The diagram could not be drawn.");
    return text.length > 2000 ? text.substring(0, 2000) : text;
  }

  window.chrome.webview.addEventListener("message", async e => {
    const req = e.data;
    let answer;
    try {
      answer = await draw(req);
    } catch (x) {
      answer = { id: req.id, ok: false, error: message(x) };
    }
    window.chrome.webview.postMessage(answer);
  });
  window.chrome.webview.postMessage({ ready: true });
})();
