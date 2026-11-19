// Reference renderer: samples/*.excalidraw -> out/<name>.svg / .png / .restored.json (+ contact sheet)
// using the real @excalidraw/excalidraw 0.18.1 in headless Chromium (Playwright).
//
//   node render.mjs                 render all samples
//   node render.mjs 06-arrows ...   render only the given samples (name without extension)
//   node render.mjs --sync-text     first rewrite text x/y/width/height (and wrapped text) in
//                                   samples/*.excalidraw using Excalidraw's own measurement, then render
//   SAMPLES_DIR=... OUT_DIR=...     override input / output folders
import fs from "node:fs";
import path from "node:path";
import http from "node:http";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
const esbuild = require("esbuild");
const { chromium } = require("playwright"); // pre-installed globally (NODE_PATH) or local

const SAMPLES = path.resolve(process.env.SAMPLES_DIR || path.join(HERE, "samples"));
const OUT = path.resolve(process.env.OUT_DIR || path.join(HERE, "out"));
const BUILD = path.join(HERE, "build");
// resolves to .../@excalidraw/excalidraw/dist/prod/index.js (package "exports" hides package.json)
const EXCALIDRAW_DIST = fs.realpathSync(path.dirname(require.resolve("@excalidraw/excalidraw")));

const args = process.argv.slice(2);
const syncTextMode = args.includes("--sync-text");
const only = args.filter((a) => !a.startsWith("--"));

// ---------- 1. bundle page ----------
fs.rmSync(BUILD, { recursive: true, force: true });
await esbuild.build({
  entryPoints: [path.join(HERE, "page/entry.js")],
  bundle: true,
  format: "esm",
  splitting: true,
  outdir: BUILD,
  minify: false,
  logLevel: "warning",
  define: { "process.env.NODE_ENV": '"production"', "process.env.IS_PREACT": '"false"' },
  conditions: ["production"],
});
fs.writeFileSync(
  path.join(BUILD, "index.html"),
  `<!doctype html><html><head><meta charset="utf-8">
<script>window.EXCALIDRAW_ASSET_PATH = location.origin + "/";</script>
<script type="module" src="./entry.js"></script></head><body></body></html>`,
);

// ---------- 2. static server (bundle + Excalidraw's font files) ----------
const MIME = { ".html": "text/html", ".js": "text/javascript", ".woff2": "font/woff2", ".css": "text/css", ".json": "application/json" };
const fontRequests = [];
const server = http.createServer((req, res) => {
  const url = decodeURIComponent(new URL(req.url, "http://x").pathname);
  let file;
  if (url.startsWith("/fonts/")) {
    file = path.join(EXCALIDRAW_DIST, url);
    fontRequests.push(url);
  } else {
    file = path.join(BUILD, url === "/" ? "index.html" : url);
  }
  if (!file.startsWith(BUILD) && !file.startsWith(EXCALIDRAW_DIST)) {
    res.writeHead(403).end();
    return;
  }
  fs.readFile(file, (err, data) => {
    if (err) {
      res.writeHead(404).end();
      console.warn("404", url);
      return;
    }
    res.writeHead(200, { "content-type": MIME[path.extname(file)] || "application/octet-stream" });
    res.end(data);
  });
});
await new Promise((r) => server.listen(0, "127.0.0.1", r));
const origin = `http://127.0.0.1:${server.address().port}`;

// ---------- 3. browser ----------
const browser = await chromium.launch();
const page = await browser.newPage();
const blocked = [];
await page.route("**/*", (route) => {
  const u = route.request().url();
  if (u.startsWith(origin) || u.startsWith("data:") || u.startsWith("blob:")) return route.continue();
  blocked.push(u);
  return route.abort();
});
page.on("console", (m) => {
  if (m.type() === "error" || m.type() === "warning") console.log(`[browser ${m.type()}]`, m.text());
});
page.on("pageerror", (e) => console.log("[pageerror]", e.message));
await page.goto(origin + "/");
await page.waitForFunction(() => window.excalRefReady === true);

// ---------- 4. samples ----------
let files = fs.readdirSync(SAMPLES).filter((f) => f.endsWith(".excalidraw")).sort();
if (only.length) files = files.filter((f) => only.includes(path.basename(f, ".excalidraw")));
fs.mkdirSync(OUT, { recursive: true });

if (syncTextMode) {
  for (const f of files) {
    const p = path.join(SAMPLES, f);
    const scene = JSON.parse(fs.readFileSync(p, "utf8"));
    const { scene: next, changes } = await page.evaluate((s) => window.excalRef.syncText(s), scene);
    fs.writeFileSync(p, JSON.stringify(next, null, 2) + "\n");
    console.log(`sync-text ${f}: ${changes.length} change(s)`);
    for (const c of changes) console.log("   ", c);
  }
}

// deep-compare helper for the restore report
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const report = {};
const sheetItems = [];
const allFonts = new Set();

for (const f of files) {
  const name = path.basename(f, ".excalidraw");
  const scene = JSON.parse(fs.readFileSync(path.join(SAMPLES, f), "utf8"));
  const r = await page.evaluate((s) => window.excalRef.render(s), scene);
  fs.writeFileSync(path.join(OUT, `${name}.svg`), r.svg);
  fs.writeFileSync(path.join(OUT, `${name}.png`), Buffer.from(r.png.split(",")[1], "base64"));
  fs.writeFileSync(path.join(OUT, `${name}.restored.json`), JSON.stringify(r.restored, null, 2) + "\n");
  r.fonts.forEach((x) => allFonts.add(x));
  sheetItems.push({ name, png: r.png });

  // restore diff: input element vs restoreElements() output, field by field
  const inById = new Map(scene.elements.map((e) => [e.id, e]));
  const outIds = new Set(r.restored.map((e) => e.id));
  const diffs = [];
  for (const e of scene.elements) if (!outIds.has(e.id)) diffs.push({ id: e.id, removed: true });
  for (const e of r.restored) {
    const src = inById.get(e.id);
    if (!src) {
      diffs.push({ id: e.id, added: true });
      continue;
    }
    for (const k of new Set([...Object.keys(src), ...Object.keys(e)])) {
      if (!same(src[k], e[k])) diffs.push({ id: e.id, field: k, from: src[k], to: e[k] });
    }
  }
  // text measurement check: would refreshDimensions change anything?
  const textDrift = [];
  const refById = new Map(r.refreshed.map((e) => [e.id, e]));
  for (const e of scene.elements.filter((e) => e.type === "text")) {
    const x = refById.get(e.id);
    const d = {};
    for (const k of ["width", "height", "text"]) if (!same(e[k], x[k])) d[k] = [e[k], x[k]];
    if (Object.keys(d).length) textDrift.push({ id: e.id, ...d });
  }
  report[name] = { restoreDiffs: diffs, textDrift, pngSize: r.pngSize };
  const nonIndex = diffs.filter((d) => d.field !== "index");
  console.log(
    `${name}: svg ${(r.svg.length / 1024).toFixed(0)} KB, png ${r.pngSize.join("x")}, ` +
      `restore changed ${nonIndex.length} field(s) (+${diffs.length - nonIndex.length} index), text drift ${textDrift.length}`,
  );
}

fs.writeFileSync(path.join(OUT, "restore-report.json"), JSON.stringify(report, null, 2) + "\n");

if (!only.length) {
  const sheet = await page.evaluate((items) => window.excalRef.contactSheet(items), sheetItems);
  fs.writeFileSync(path.join(OUT, "contact-sheet.png"), Buffer.from(sheet.split(",")[1], "base64"));
}

const fontFiles = [...new Set(fontRequests)].sort();
fs.writeFileSync(
  path.join(OUT, "fonts.json"),
  JSON.stringify({ loadedFontFaces: [...allFonts].sort(), servedFontFiles: fontFiles, blockedExternalRequests: blocked }, null, 2) + "\n",
);
const families = [...new Set([...allFonts].map((x) => x.split(" [")[0]))].sort();
console.log(`fonts loaded: ${families.join(", ") || "NONE"} (${fontFiles.length} font files served)`);
if (blocked.length) console.log(`WARNING: ${blocked.length} external request(s) blocked, e.g. ${blocked[0]}`);
if (!families.some((f) => /Excalifont/.test(f))) console.log("WARNING: Excalifont did not load!");

await browser.close();
server.close();
console.log(`output -> ${OUT}`);
