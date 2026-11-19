// Generates samples/*.excalidraw with fixed seeds / ids.
// Text width/height/x/y are first estimated here, then corrected with Excalidraw's own
// text measurement by `node render.mjs --sync-text` (see README).
//
// Usage: node make-samples.mjs        (overwrites samples/*.excalidraw)
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const OUT = path.join(HERE, "samples");

// ---------- deterministic helpers ----------
let counter = 0;
const nextSeed = () => {
  counter++;
  // simple LCG -> stable, distinct 31-bit seeds
  return (Math.imul(counter, 1103515245) + 12345) >>> 1;
};
const UPDATED = 1730000000000;

function base(type, id, x, y, width, height, o = {}) {
  return {
    id,
    type,
    x,
    y,
    width,
    height,
    angle: 0,
    strokeColor: "#1e1e1e",
    backgroundColor: "transparent",
    fillStyle: "solid",
    strokeWidth: 2,
    strokeStyle: "solid",
    roughness: 1,
    opacity: 100,
    groupIds: [],
    frameId: null,
    roundness: null,
    seed: nextSeed(),
    version: 1,
    versionNonce: nextSeed(),
    isDeleted: false,
    boundElements: [],
    updated: UPDATED,
    link: null,
    locked: false,
    ...o,
  };
}

const rect = (id, x, y, w, h, o) => base("rectangle", id, x, y, w, h, o);
const diamond = (id, x, y, w, h, o) => base("diamond", id, x, y, w, h, o);
const ellipse = (id, x, y, w, h, o) => base("ellipse", id, x, y, w, h, o);

function bounds(points) {
  const xs = points.map((p) => p[0]);
  const ys = points.map((p) => p[1]);
  return [Math.max(...xs) - Math.min(...xs), Math.max(...ys) - Math.min(...ys)];
}

function arrow(id, x, y, points, o = {}) {
  const [w, h] = bounds(points);
  return base("arrow", id, x, y, w, h, {
    roundness: { type: 2 },
    points,
    lastCommittedPoint: null,
    startBinding: null,
    endBinding: null,
    startArrowhead: null,
    endArrowhead: "arrow",
    elbowed: false,
    ...o,
  });
}

function line(id, x, y, points, o = {}) {
  const [w, h] = bounds(points);
  return base("line", id, x, y, w, h, {
    roundness: null,
    points,
    lastCommittedPoint: null,
    startBinding: null,
    endBinding: null,
    startArrowhead: null,
    endArrowhead: null,
    ...o,
  });
}

function freedraw(id, x, y, points, pressures, simulatePressure, o = {}) {
  const [w, h] = bounds(points);
  return base("freedraw", id, x, y, w, h, {
    points,
    pressures,
    simulatePressure,
    lastCommittedPoint: null,
    ...o,
  });
}

// Rough estimate; corrected by `render.mjs --sync-text`.
function text(id, x, y, str, o = {}) {
  const fontSize = o.fontSize ?? 20;
  const lineHeight = o.lineHeight ?? 1.25;
  const lines = str.split("\n");
  const w = Math.max(...lines.map((l) => l.length)) * fontSize * 0.55;
  const h = lines.length * fontSize * lineHeight;
  return base("text", id, x, y, w, h, {
    text: str,
    fontSize,
    fontFamily: 5,
    textAlign: "left",
    verticalAlign: "top",
    containerId: null,
    originalText: str,
    autoResize: true,
    lineHeight,
    ...o,
  });
}

// text bound inside a container; links both ways
function boundText(container, id, str, o = {}) {
  const t = text(id, container.x, container.y, str, {
    textAlign: "center",
    verticalAlign: "middle",
    containerId: container.id,
    ...o,
  });
  // place roughly at the center; --sync-text computes the exact position
  t.x = container.x + container.width / 2 - t.width / 2;
  t.y = container.y + container.height / 2 - t.height / 2;
  addBound(container, "text", id);
  return t;
}

function addBound(el, type, id) {
  el.boundElements = [...(el.boundElements ?? []), { type, id }];
}

// bind an arrow's start/end to shapes (focus/gap as Excalidraw stores them)
function bind(a, start, end, { sf = 0, ef = 0, gap = 8 } = {}) {
  if (start) {
    a.startBinding = { elementId: start.id, focus: sf, gap };
    addBound(start, "arrow", a.id);
  }
  if (end) {
    a.endBinding = { elementId: end.id, focus: ef, gap };
    addBound(end, "arrow", a.id);
  }
  return a;
}

const r2 = (v) => Math.round(v * 100) / 100;

// fractional indices as Excalidraw 0.18 writes them ("a0".."a9","aA".."aZ","aa".."az")
const B62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
function scene(elements) {
  if (elements.length > 62) throw new Error("too many elements for simple index scheme");
  elements = elements.map((e, i) => {
    const { id, type, ...rest } = e;
    return { id, type, index: "a" + B62[i], ...rest };
  });
  return {
    type: "excalidraw",
    version: 2,
    source: "https://excalidraw.com",
    elements,
    appState: { gridSize: 20, gridStep: 5, gridModeEnabled: false, viewBackgroundColor: "#ffffff" },
    files: {},
  };
}

const samples = {};

// 01 basic shapes -------------------------------------------------------------
counter = 100;
samples["01-shapes"] = scene([
  rect("rect-sharp", 0, 0, 160, 100),
  rect("rect-round", 200, 0, 160, 100, { roundness: { type: 3 } }),
  diamond("diamond-sharp", 400, 0, 160, 100),
  diamond("diamond-round", 600, 0, 160, 100, { roundness: { type: 2 } }),
  ellipse("ellipse", 800, 0, 160, 100),
  rect("rect-small-round", 0, 140, 60, 40, { roundness: { type: 3 } }),
  rect("rect-big-round", 100, 140, 300, 160, { roundness: { type: 3 } }),
  ellipse("circle", 440, 140, 120, 120),
  diamond("diamond-tall", 620, 140, 80, 160, { roundness: { type: 2 } }),
]);

// 02 fills --------------------------------------------------------------------
counter = 200;
samples["02-fills"] = scene([
  rect("fill-hachure", 0, 0, 160, 110, { fillStyle: "hachure", backgroundColor: "#a5d8ff" }),
  rect("fill-crosshatch", 200, 0, 160, 110, { fillStyle: "cross-hatch", backgroundColor: "#ffc9c9" }),
  rect("fill-solid", 400, 0, 160, 110, { fillStyle: "solid", backgroundColor: "#b2f2bb" }),
  rect("fill-zigzag", 600, 0, 160, 110, { fillStyle: "zigzag", backgroundColor: "#ffec99" }),
  ellipse("ell-hachure", 0, 150, 160, 110, { fillStyle: "hachure", backgroundColor: "#e599f7" }),
  ellipse("ell-crosshatch", 200, 150, 160, 110, { fillStyle: "cross-hatch", backgroundColor: "#a5d8ff" }),
  ellipse("ell-solid", 400, 150, 160, 110, { fillStyle: "solid", backgroundColor: "#ffd8a8" }),
  ellipse("ell-zigzag", 600, 150, 160, 110, { fillStyle: "zigzag", backgroundColor: "#b2f2bb" }),
  diamond("dia-hachure", 0, 300, 160, 110, { fillStyle: "hachure", backgroundColor: "#ffc9c9", roundness: { type: 2 } }),
  diamond("dia-crosshatch", 200, 300, 160, 110, { fillStyle: "cross-hatch", backgroundColor: "#ffec99" }),
  rect("rr-solid", 400, 300, 160, 110, { fillStyle: "solid", backgroundColor: "#a5d8ff", roundness: { type: 3 } }),
  rect("rr-hachure-thick", 600, 300, 160, 110, {
    fillStyle: "hachure",
    backgroundColor: "#1971c2",
    strokeColor: "#1971c2",
    strokeWidth: 4,
    roundness: { type: 3 },
  }),
]);

// 03 stroke styles x widths ----------------------------------------------------
counter = 300;
{
  const els = [];
  const styles = ["solid", "dashed", "dotted"];
  const widths = [1, 2, 4];
  styles.forEach((s, i) =>
    widths.forEach((w, j) => {
      els.push(rect(`r-${s}-${w}`, j * 180, i * 130, 150, 100, { strokeStyle: s, strokeWidth: w }));
    }),
  );
  styles.forEach((s, i) => {
    els.push(ellipse(`e-${s}`, 560, i * 130, 140, 100, { strokeStyle: s, strokeWidth: 2 }));
    els.push(diamond(`d-${s}`, 730, i * 130, 140, 100, { strokeStyle: s, strokeWidth: 2, roundness: { type: 2 } }));
  });
  samples["03-stroke-styles"] = scene(els);
}

// 04 roughness 0/1/2 ------------------------------------------------------------
counter = 400;
{
  const els = [];
  [0, 1, 2].forEach((r, i) => {
    const y = i * 140;
    els.push(rect(`rect-r${r}`, 0, y, 160, 110, { roughness: r, fillStyle: "hachure", backgroundColor: "#a5d8ff" }));
    els.push(rect(`rrect-r${r}`, 200, y, 160, 110, { roughness: r, roundness: { type: 3 } }));
    els.push(ellipse(`ell-r${r}`, 400, y, 160, 110, { roughness: r, fillStyle: "cross-hatch", backgroundColor: "#ffc9c9" }));
    els.push(diamond(`dia-r${r}`, 600, y, 160, 110, { roughness: r }));
    els.push(arrow(`arr-r${r}`, 800, y + 20, [[0, 0], [80, 40], [160, 70]], { roughness: r }));
  });
  samples["04-roughness"] = scene(els);
}

// 05 opacity + angle -------------------------------------------------------------
counter = 500;
{
  const rotRect = rect("rot-rect", 300, 20, 160, 90, { angle: 0.5235987755982988, fillStyle: "hachure", backgroundColor: "#ffec99" });
  samples["05-opacity-angle"] = scene([
    rect("opaque-under", 0, 0, 140, 140, { fillStyle: "solid", backgroundColor: "#1971c2" }),
    rect("opacity-50", 60, 60, 160, 120, { opacity: 50, fillStyle: "solid", backgroundColor: "#ffc9c9", strokeColor: "#e03131" }),
    rotRect,
    ellipse("rot-ellipse", 520, 20, 180, 90, { angle: 1.0471975511965976, fillStyle: "cross-hatch", backgroundColor: "#b2f2bb" }),
    diamond("rot-diamond", 740, 20, 160, 110, { angle: 5.497787143782138, roundness: { type: 2 }, fillStyle: "solid", backgroundColor: "#e599f7" }),
    rect("rot-rrect-op", 0, 240, 200, 100, { angle: 2.792526803190927, roundness: { type: 3 }, opacity: 50, strokeWidth: 4 }),
    text("rot-text", 300, 260, "Rotated text\n회전된 텍스트", { angle: 0.3490658503988659, fontSize: 24 }),
    arrow("rot-arrow-op", 560, 260, [[0, 0], [120, -40], [220, 30]], { opacity: 50, strokeColor: "#2f9e44", strokeWidth: 4 }),
  ]);
}

// 06 arrows ------------------------------------------------------------------------
counter = 600;
samples["06-arrows"] = scene([
  arrow("straight-2pt", 0, 0, [[0, 0], [240, 0]]),
  arrow("straight-diag", 0, 40, [[0, 0], [240, 80]], { roundness: null }),
  arrow("multi-sharp", 300, 0, [[0, 0], [100, 80], [200, 0], [300, 80]], { roundness: null }),
  arrow("multi-curved", 300, 140, [[0, 0], [100, 80], [200, 0], [300, 80]], { roundness: { type: 2 } }),
  arrow("curved-3pt", 0, 160, [[0, 0], [120, 100], [240, 0]], { roundness: { type: 2 } }),
  arrow("dashed-curved", 0, 300, [[0, 0], [80, -40], [160, 40], [240, 0]], { strokeStyle: "dashed" }),
  arrow("dotted-sharp", 300, 300, [[0, 0], [150, 0], [150, 80], [300, 80]], { strokeStyle: "dotted", roundness: null }),
  arrow("thick-r0", 0, 420, [[0, 0], [120, 60], [240, 0]], { strokeWidth: 4, roughness: 0, strokeColor: "#e03131" }),
  arrow("thin-r2", 300, 420, [[0, 0], [300, 60]], { strokeWidth: 1, roughness: 2, strokeColor: "#1971c2" }),
  arrow("backwards", 600, 0, [[0, 0], [-60, 200], [60, 380]], { roundness: { type: 2 }, startArrowhead: "arrow" }),
]);

// 07 arrowheads ----------------------------------------------------------------------
counter = 700;
{
  const heads = ["arrow", "triangle", "triangle_outline", "bar", "dot", "circle", "circle_outline", "diamond", "diamond_outline"];
  const els = [];
  heads.forEach((h, i) => {
    const y = i * 50;
    els.push(arrow(`end-${h}`, 0, y, [[0, 0], [200, 0]], { endArrowhead: h, roundness: null }));
    els.push(arrow(`both-${h}`, 260, y, [[0, 0], [200, 0]], { startArrowhead: h, endArrowhead: h, roundness: null, strokeWidth: 1 }));
    els.push(arrow(`start-${h}`, 520, y, [[0, 0], [200, 20]], { startArrowhead: h, endArrowhead: null, strokeWidth: 4 }));
    els.push(text(`label-${h}`, 760, y - 12, h, { fontSize: 16, fontFamily: 3 }));
  });
  // curved arrow with heads at both ends (arrowhead direction from curve tangent)
  els.push(arrow("curved-heads", 0, 470, [[0, 0], [150, 70], [300, 0], [460, 60]], { startArrowhead: "triangle", endArrowhead: "arrow" }));
  // no arrowheads at all
  els.push(arrow("no-heads", 520, 470, [[0, 0], [200, 40]], { startArrowhead: null, endArrowhead: null }));
  samples["07-arrowheads"] = scene(els);
}

// 08 bindings ---------------------------------------------------------------------------
counter = 800;
{
  const A = rect("shape-a", 0, 0, 160, 80, { roundness: { type: 3 } });
  const B = rect("shape-b", 320, 0, 160, 80);
  const C = ellipse("shape-c", 320, 180, 160, 100);
  const D = diamond("shape-d", 0, 200, 160, 120);
  const ab = bind(arrow("a-to-b", 168, 40, [[0, 0], [144, 0]]), A, B);
  const ab2 = bind(arrow("a-to-b-focus", 168, 66, [[0, 0], [144, -48]], { roundness: null, strokeColor: "#1971c2" }), A, B, {
    sf: -0.6,
    ef: 0.6,
    gap: 6,
  });
  const bc = bind(arrow("b-to-c", 400, 88, [[0, 0], [0, 84]]), B, C);
  const cd = bind(arrow("c-to-d", 312, 230, [[0, 0], [-144, 30]], { startArrowhead: "dot" }), C, D, { gap: 8 });
  const da = bind(arrow("d-to-a", 80, 192, [[0, 0], [-30, -50], [0, -104]], { strokeStyle: "dashed" }), D, A, {
    sf: 0.1,
    ef: -0.1,
  });
  // half-bound arrow (only start bound)
  const free = bind(arrow("c-free", 488, 230, [[0, 0], [120, 0], [160, 60]]), C, null);
  samples["08-bindings"] = scene([A, B, C, D, ab, ab2, bc, cd, da, free]);
}

// 09 lines ------------------------------------------------------------------------------
counter = 900;
samples["09-lines"] = scene([
  line("polyline-open", 0, 0, [[0, 0], [80, 60], [160, 0], [240, 60], [320, 0]]),
  line("polyline-curved", 0, 100, [[0, 0], [80, 60], [160, 0], [240, 60], [320, 0]], { roundness: { type: 2 } }),
  line("single-segment", 380, 0, [[0, 0], [200, 100]], { strokeWidth: 4, strokeColor: "#e03131" }),
  line("closed-hachure", 0, 220, [[0, 0], [160, -20], [200, 120], [40, 160], [0, 0]], {
    backgroundColor: "#a5d8ff",
    fillStyle: "hachure",
  }),
  line("closed-solid-curved", 260, 220, [[0, 0], [160, 0], [180, 140], [20, 160], [0, 0]], {
    backgroundColor: "#ffc9c9",
    fillStyle: "solid",
    roundness: { type: 2 },
  }),
  line("closed-crosshatch-dashed", 500, 220, [[0, 0], [100, 60], [200, 0], [160, 160], [40, 160], [0, 0]], {
    backgroundColor: "#b2f2bb",
    fillStyle: "cross-hatch",
    strokeStyle: "dashed",
  }),
  line("closed-zigzag-r0", 760, 220, [[0, 0], [140, 0], [70, 130], [0, 0]], {
    backgroundColor: "#ffec99",
    fillStyle: "zigzag",
    roughness: 0,
  }),
]);

// 10 freedraw ----------------------------------------------------------------------------
counter = 1000;
{
  const wave = [];
  const pres = [];
  for (let i = 0; i <= 60; i++) {
    wave.push([r2(i * 5), r2(25 * Math.sin(i / 5) + 6 * Math.sin(i / 1.7) - 6 * Math.sin(0))]);
    pres.push(r2(0.15 + 0.7 * Math.sin((Math.PI * i) / 60)));
  }
  const spiral = [];
  for (let i = 0; i <= 80; i++) {
    const t = i / 8;
    spiral.push([r2(Math.cos(t) * t * 6 - 6 * 0), r2(Math.sin(t) * t * 6)]);
  }
  // normalize spiral so first point is [0,0]
  const s0 = spiral[0];
  const spiralN = spiral.map(([x, y]) => [r2(x - s0[0]), r2(y - s0[1])]);
  const scribble = [[0, 0], [10, 4], [22, 12], [30, 26], [28, 40], [16, 46], [4, 38], [6, 24], [20, 16], [40, 18], [58, 28], [70, 44], [84, 50], [100, 46], [110, 30], [104, 14], [92, 10], [86, 22], [96, 36], [120, 44], [150, 40]];
  samples["10-freedraw"] = scene([
    freedraw("fd-pressure", 0, 40, wave, pres, false, { strokeWidth: 2 }),
    freedraw("fd-simulated", 0, 160, wave, [], true, { strokeWidth: 2, strokeColor: "#1971c2" }),
    freedraw("fd-thick-pressure", 360, 40, wave, pres, false, { strokeWidth: 4, strokeColor: "#e03131" }),
    freedraw("fd-thin-simulated", 360, 160, wave, [], true, { strokeWidth: 1 }),
    freedraw("fd-spiral", 120, 320, spiralN, [], true, { strokeWidth: 2, strokeColor: "#2f9e44" }),
    freedraw("fd-scribble-pressure", 400, 300, scribble, scribble.map((_, i) => r2(0.3 + 0.03 * i)), false, {
      strokeWidth: 4,
      opacity: 50,
    }),
    freedraw("fd-closed-fill", 640, 300, [[0, 0], [60, -10], [100, 30], [80, 90], [20, 100], [-10, 50], [0, 2]], [], true, {
      backgroundColor: "#ffec99",
      fillStyle: "solid",
    }),
  ]);
}

// 11 free text --------------------------------------------------------------------------
counter = 1100;
samples["11-text"] = scene([
  text("t-virgil", 0, 0, "Virgil (fontFamily 1)\nsecond line", { fontFamily: 1, fontSize: 20 }),
  text("t-helvetica", 0, 80, "Helvetica (fontFamily 2)\nsecond line", { fontFamily: 2, fontSize: 20 }),
  text("t-cascadia", 0, 160, "Cascadia (fontFamily 3)\nsecond line", { fontFamily: 3, fontSize: 20 }),
  text("t-excalifont", 0, 240, "Excalifont (fontFamily 5)\nsecond line", { fontFamily: 5, fontSize: 20 }),
  text("t-nunito", 0, 320, "Nunito (fontFamily 6)\nsecond line", { fontFamily: 6, fontSize: 20 }),
  text("t-lilita", 0, 400, "Lilita One (7)", { fontFamily: 7, fontSize: 20 }),
  text("t-comic", 0, 450, "Comic Shanns (8)", { fontFamily: 8, fontSize: 20 }),
  text("t-left", 420, 0, "Left aligned\nmulti-line text\nthird", { textAlign: "left", fontSize: 20 }),
  text("t-center", 420, 120, "Center aligned\nmulti-line\ntext block", { textAlign: "center", fontSize: 20 }),
  text("t-right", 420, 240, "Right aligned\nmulti-line\ntext", { textAlign: "right", fontSize: 20 }),
  text("t-small", 420, 360, "small 16px", { fontSize: 16, strokeColor: "#1971c2" }),
  text("t-large", 420, 390, "Large 36px", { fontSize: 36, strokeColor: "#e03131" }),
  text("t-korean", 0, 520, "한글 텍스트: 시작 → 검토 → 완료\n두 번째 줄 (Excalifont)", { fontFamily: 5, fontSize: 20 }),
  text("t-korean-virgil", 0, 590, "한글 Virgil 폰트", { fontFamily: 1, fontSize: 24 }),
  text("t-fixed-width", 420, 470, "autoResize false: this long text wraps inside a fixed width box", {
    autoResize: false,
    fontSize: 20,
    width: 220,
  }),
]);

// 12 bound text --------------------------------------------------------------------------
counter = 1200;
{
  const r = rect("box", 0, 0, 200, 100, { roundness: { type: 3 }, backgroundColor: "#a5d8ff", fillStyle: "hachure" });
  const rt = boundText(r, "box-text", "Text in a\nrectangle");
  const d = diamond("dia", 260, 0, 200, 140, { backgroundColor: "#ffec99", fillStyle: "solid" });
  const dt = boundText(d, "dia-text", "Diamond");
  const e = ellipse("ell", 520, 0, 200, 120, { backgroundColor: "#b2f2bb", fillStyle: "cross-hatch" });
  const et = boundText(e, "ell-text", "Ellipse text");
  const r2b = rect("box-top-left", 0, 180, 200, 120, {});
  const r2t = boundText(r2b, "box-top-left-text", "top-left\naligned", { textAlign: "left", verticalAlign: "top", fontFamily: 1 });
  const r3 = rect("box-bottom-right", 260, 180, 200, 120, {});
  const r3t = boundText(r3, "box-bottom-right-text", "bottom-right", { textAlign: "right", verticalAlign: "bottom", fontFamily: 6 });
  const r4 = rect("box-wrap", 520, 180, 160, 120, { roundness: { type: 3 } });
  const r4t = boundText(r4, "box-wrap-text", "This long label gets wrapped by Excalidraw", { fontSize: 16 });
  const a = arrow("label-arrow", 0, 380, [[0, 0], [400, 0]]);
  const at = boundText(a, "label-arrow-text", "arrow label", { fontSize: 20 });
  const a2 = arrow("label-arrow-curved", 460, 360, [[0, 0], [130, 60], [260, 0]], { roundness: { type: 2 } });
  const a2t = boundText(a2, "label-arrow-curved-text", "곡선 라벨", { fontSize: 20 });
  samples["12-bound-text"] = scene([r, rt, d, dt, e, et, r2b, r2t, r3, r3t, r4, r4t, a, at, a2, a2t]);
}

// 13 workflow ------------------------------------------------------------------------------
counter = 1300;
{
  const title = text("wf-title", 0, -80, "결재 워크플로우 (Approval workflow)", { fontSize: 28 });
  const start = ellipse("wf-start", 180, 0, 160, 70, { backgroundColor: "#b2f2bb", fillStyle: "solid" });
  const startT = boundText(start, "wf-start-text", "시작");
  const step1 = rect("wf-step1", 160, 140, 200, 80, { roundness: { type: 3 }, backgroundColor: "#a5d8ff", fillStyle: "hachure" });
  const step1T = boundText(step1, "wf-step1-text", "요청 접수\nReceive request");
  const dec = diamond("wf-decision", 160, 290, 200, 130, { roundness: { type: 2 }, backgroundColor: "#ffec99", fillStyle: "cross-hatch" });
  const decT = boundText(dec, "wf-decision-text", "검토\n승인?");
  const yes = rect("wf-yes", 0, 510, 180, 80, { roundness: { type: 3 }, backgroundColor: "#a5d8ff", fillStyle: "solid" });
  const yesT = boundText(yes, "wf-yes-text", "처리 (Process)");
  const no = rect("wf-no", 340, 510, 180, 80, { roundness: { type: 3 }, backgroundColor: "#ffc9c9", fillStyle: "solid" });
  const noT = boundText(no, "wf-no-text", "반려 (Reject)");
  const end = ellipse("wf-end", 180, 670, 160, 70, { backgroundColor: "#b2f2bb", fillStyle: "solid" });
  const endT = boundText(end, "wf-end-text", "완료");

  const a1 = bind(arrow("wf-a1", 260, 78, [[0, 0], [0, 54]]), start, step1);
  const a2 = bind(arrow("wf-a2", 260, 228, [[0, 0], [0, 54]]), step1, dec);
  const a3 = bind(arrow("wf-a3", 152, 355, [[0, 0], [-62, 35], [-62, 147]], { strokeColor: "#2f9e44" }), dec, yes, { sf: 0, ef: 0 });
  const a3T = boundText(a3, "wf-a3-text", "예", { strokeColor: "#2f9e44" });
  const a4 = bind(arrow("wf-a4", 368, 355, [[0, 0], [62, 35], [62, 147]], { strokeColor: "#e03131" }), dec, no, { sf: 0, ef: 0 });
  const a4T = boundText(a4, "wf-a4-text", "아니오", { strokeColor: "#e03131" });
  const a5 = bind(arrow("wf-a5", 90, 598, [[0, 0], [20, 80], [82, 107]]), yes, end, { sf: 0.2, ef: -0.1 });
  const a6 = bind(arrow("wf-a6", 430, 598, [[0, 0], [-20, 80], [-82, 107]], { strokeStyle: "dashed" }), no, end, { sf: -0.2, ef: 0.1 });
  const note = text("wf-note", 560, 520, "반려 시 요청자에게\n알림 발송", { fontSize: 16, strokeColor: "#868e96", fontFamily: 5 });
  samples["13-workflow"] = scene([
    title, start, startT, step1, step1T, dec, decT, yes, yesT, no, noT, end, endT,
    a1, a2, a3, a3T, a4, a4T, a5, a6, note,
  ]);
}

// 14 grouped + mixed colors (groupIds, stroke colors, transparent + strokeWidth 1 roughness 2) --
counter = 1400;
samples["14-groups-colors"] = scene([
  rect("g-rect", 0, 0, 180, 100, { groupIds: ["grp1"], strokeColor: "#1971c2", backgroundColor: "#e7f5ff", fillStyle: "solid" }),
  ellipse("g-ell", 40, 20, 100, 60, { groupIds: ["grp1"], strokeColor: "#e03131", strokeWidth: 1, roughness: 2 }),
  rect("c-1", 240, 0, 100, 100, { strokeColor: "#f08c00", backgroundColor: "#fff9db", fillStyle: "hachure", strokeWidth: 1 }),
  rect("c-2", 360, 0, 100, 100, { strokeColor: "#9c36b5", backgroundColor: "#f3d9fa", fillStyle: "cross-hatch", strokeWidth: 4, roughness: 2 }),
  diamond("c-3", 480, 0, 100, 100, { strokeColor: "#0c8599", backgroundColor: "#c5f6fa", fillStyle: "zigzag", roughness: 0 }),
  ellipse("c-4", 600, 0, 100, 100, { strokeColor: "#2f9e44", backgroundColor: "#d3f9d8", fillStyle: "solid", strokeStyle: "dotted", strokeWidth: 4 }),
]);

fs.mkdirSync(OUT, { recursive: true });
for (const [name, data] of Object.entries(samples)) {
  fs.writeFileSync(path.join(OUT, `${name}.excalidraw`), JSON.stringify(data, null, 2) + "\n");
}
console.log(`wrote ${Object.keys(samples).length} samples to ${OUT}`);
