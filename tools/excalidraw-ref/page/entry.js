// Browser-side entry, bundled by esbuild (render.mjs) together with @excalidraw/excalidraw 0.18.1.
// Exposes window.excalRef.{render, syncText, contactSheet}.
import { exportToSvg, exportToCanvas, restoreElements } from "@excalidraw/excalidraw";

const EXPORT_PADDING = 10;
const PNG_SCALE = 2;

const exportOpts = (scene) => ({
  elements: scene.elements,
  appState: {
    ...(scene.appState || {}),
    exportBackground: true,
    viewBackgroundColor: "#ffffff",
    exportWithDarkMode: false,
    exportScale: 1,
  },
  files: scene.files || {},
  exportPadding: EXPORT_PADDING,
});

function loadedFonts() {
  return [...document.fonts]
    .filter((f) => f.status === "loaded")
    .map((f) => `${f.family} [${f.unicodeRange}]`);
}

async function render(scene) {
  // canvas first: exportToCanvas awaits Fonts.loadElementsFonts(), so fonts are in document.fonts
  const canvas = await exportToCanvas({
    ...exportOpts(scene),
    getDimensions: (w, h) => ({ width: w * PNG_SCALE, height: h * PNG_SCALE, scale: PNG_SCALE }),
  });
  const png = canvas.toDataURL("image/png");
  const svgEl = await exportToSvg(exportOpts(scene));
  const svg = svgEl.outerHTML;
  // what the editor does when a file is opened (App -> restore(..., {repairBindings: true, refreshDimensions: false}))
  const restored = restoreElements(scene.elements, null, { repairBindings: true, refreshDimensions: false });
  // what text dims Excalidraw would measure now (fonts loaded)
  const refreshed = restoreElements(scene.elements, null, { repairBindings: true, refreshDimensions: true });
  return { png, svg, restored, refreshed, fonts: loadedFonts(), pngSize: [canvas.width, canvas.height] };
}

// ---- --sync-text support: recompute text width/height/(wrapped) text and bound text position
// using Excalidraw's own measurement (same formulas as element/textElement.ts computeBoundTextPosition)
const BOUND_TEXT_PADDING = 5;
function containerCoords(c) {
  let ox = BOUND_TEXT_PADDING;
  let oy = BOUND_TEXT_PADDING;
  if (c.type === "ellipse") {
    ox += (c.width / 2) * (1 - Math.sqrt(2) / 2);
    oy += (c.height / 2) * (1 - Math.sqrt(2) / 2);
  }
  if (c.type === "diamond") {
    ox += c.width / 4;
    oy += c.height / 4;
  }
  return { x: c.x + ox, y: c.y + oy };
}
function maxW(c) {
  if (c.type === "ellipse") return Math.round((c.width / 2) * Math.sqrt(2)) - BOUND_TEXT_PADDING * 2;
  if (c.type === "diamond") return Math.round(c.width / 2) - BOUND_TEXT_PADDING * 2;
  return c.width - BOUND_TEXT_PADDING * 2;
}
function maxH(c) {
  if (c.type === "ellipse") return Math.round((c.height / 2) * Math.sqrt(2)) - BOUND_TEXT_PADDING * 2;
  if (c.type === "diamond") return Math.round(c.height / 2) - BOUND_TEXT_PADDING * 2;
  return c.height - BOUND_TEXT_PADDING * 2;
}
function boundTextPos(c, t) {
  if (c.type === "arrow") {
    // LinearElementEditor.getBoundTextElementPosition: odd #points -> middle point,
    // 2 points -> segment midpoint (other even counts not supported here)
    const n = c.points.length;
    let mx, my;
    if (n % 2 === 1) {
      const p = c.points[Math.floor(n / 2)];
      mx = c.x + p[0];
      my = c.y + p[1];
    } else if (n === 2) {
      mx = c.x + (c.points[0][0] + c.points[1][0]) / 2;
      my = c.y + (c.points[0][1] + c.points[1][1]) / 2;
    } else {
      throw new Error(`arrow label on ${n}-point arrow not supported by sync-text`);
    }
    return { x: mx - t.width / 2, y: my - t.height / 2 };
  }
  const cc = containerCoords(c);
  let x, y;
  if (t.verticalAlign === "top") y = cc.y;
  else if (t.verticalAlign === "bottom") y = cc.y + (maxH(c) - t.height);
  else y = cc.y + (maxH(c) / 2 - t.height / 2);
  if (t.textAlign === "left") x = cc.x;
  else if (t.textAlign === "right") x = cc.x + (maxW(c) - t.width);
  else x = cc.x + (maxW(c) / 2 - t.width / 2);
  return { x, y };
}

async function syncText(scene) {
  await exportToCanvas({ ...exportOpts(scene) }); // loads the fonts
  // re-wrap from originalText
  const input = scene.elements.map((e) => (e.type === "text" ? { ...e, text: e.originalText ?? e.text } : e));
  const refreshed = restoreElements(input, null, { repairBindings: true, refreshDimensions: true });
  const byId = new Map(refreshed.map((e) => [e.id, e]));
  const changes = [];
  const out = scene.elements.map((e) => {
    if (e.type !== "text") return e;
    const r = byId.get(e.id);
    const n = { ...e, text: r.text, width: r.width, height: r.height };
    if (e.containerId) {
      const c = byId.get(e.containerId);
      Object.assign(n, boundTextPos(c, n));
      if (c.type !== "arrow" && n.height > maxH(c)) {
        changes.push(`WARNING ${e.id}: text height ${n.height} exceeds container max ${maxH(c)} (Excalidraw would grow the container)`);
      }
    } else {
      n.x = r.x;
      n.y = r.y;
    }
    for (const k of ["x", "y", "width", "height", "text"]) {
      if (n[k] !== e[k]) changes.push(`${e.id}.${k}: ${JSON.stringify(e[k])} -> ${JSON.stringify(n[k])}`);
    }
    return n;
  });
  return { scene: { ...scene, elements: out }, changes };
}

// ---- contact sheet
async function contactSheet(items, { cellW = 560, cellH = 400, cols = 3, label = 28 } = {}) {
  const rows = Math.ceil(items.length / cols);
  const c = document.createElement("canvas");
  c.width = cols * cellW;
  c.height = rows * (cellH + label);
  const ctx = c.getContext("2d");
  ctx.fillStyle = "#f1f3f5";
  ctx.fillRect(0, 0, c.width, c.height);
  for (let i = 0; i < items.length; i++) {
    const img = new Image();
    img.src = items[i].png;
    await img.decode();
    const cx = (i % cols) * cellW;
    const cy = Math.floor(i / cols) * (cellH + label);
    const s = Math.min((cellW - 16) / img.width, (cellH - 16) / img.height, 1);
    const w = img.width * s;
    const h = img.height * s;
    ctx.fillStyle = "#ffffff";
    ctx.fillRect(cx + 4, cy + label, cellW - 8, cellH - 4);
    ctx.drawImage(img, cx + (cellW - w) / 2, cy + label + (cellH - h) / 2, w, h);
    ctx.fillStyle = "#212529";
    ctx.font = "bold 18px sans-serif";
    ctx.fillText(items[i].name, cx + 8, cy + 20);
    ctx.strokeStyle = "#adb5bd";
    ctx.strokeRect(cx + 4.5, cy + label + 0.5, cellW - 9, cellH - 5);
  }
  return c.toDataURL("image/png");
}

window.excalRef = { render, syncText, contactSheet };
window.excalRefReady = true;
