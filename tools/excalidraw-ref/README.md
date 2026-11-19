# excalidraw-ref — reference renders from real Excalidraw

Ground truth for the C# port of rough.js 4.6.4 + Excalidraw 0.18.1 shape generation.
Each `samples/*.excalidraw` file is rendered by the real `@excalidraw/excalidraw@0.18.1`
(bundled locally with esbuild, run in headless Chromium through Playwright).

## Run

```bash
cd tools/excalidraw-ref
node render.mjs                # render every sample -> out/
node render.mjs 06-arrows      # just one/some samples (no contact sheet)
```

Takes about 15 s. No network needed: the page and Excalidraw's own font files
(`dist/prod/fonts/**.woff2`) are served from a local HTTP server, and `window.EXCALIDRAW_ASSET_PATH`
points at it. Every request outside that server is blocked and listed in `out/fonts.json`.

### Setup (node_modules is not in git)

`node_modules` here is a gitignored symlink. On a new machine, either run `npm install`
in this folder (versions are pinned in `package.json`) or symlink a `node_modules`
that already has them. Playwright is **not** in `package.json`: `render.mjs` loads it with
`require("playwright")`, so a global install on `NODE_PATH` is enough. Chromium must already be installed.

### Adding or changing samples

`make-samples.mjs` is the source of the sample files. Ids, seeds and versionNonces are fixed,
and `updated` is a constant. Text sizes in it are only rough estimates, so after editing it run:

```bash
node make-samples.mjs && node render.mjs --sync-text
```

`--sync-text` measures text with Excalidraw itself, after the fonts are loaded, and writes the results back into
`samples/*.excalidraw`: `restoreElements(..., {refreshDimensions:true})` supplies width, height and the wrapped `text`
(re-wrapped from `originalText`). Bound-text x/y is recomputed with the same formulas as
`computeBoundTextPosition`. For arrow labels, only arrows with 2 points or an odd number of points are supported. It then renders as usual.
You can also hand-edit a sample or drop in any `.excalidraw` file and run `node render.mjs`.

## Output (`out/`)

| file | content |
|---|---|
| `<name>.svg` | `exportToSvg({elements, appState:{exportBackground:true, viewBackgroundColor:"#ffffff"}, files, exportPadding:10})` (fonts inlined, as Excalidraw's own "Export SVG") |
| `<name>.png` | `exportToCanvas(...)` with the same options, scale 2 (`getDimensions: (w,h)=>({width:2w, height:2h, scale:2})`) |
| `<name>.restored.json` | `restoreElements(elements, null, {repairBindings:true, refreshDimensions:false})`: what the editor does when it opens a file |
| `restore-report.json` | per sample: every field restore changed (`restoreDiffs`), and `textDrift` (fields that `refreshDimensions:true` would change in text elements; should be empty) |
| `fonts.json` | FontFaces that reached `status === "loaded"`, the font files served, and blocked external requests |
| `contact-sheet.png` | all PNGs tiled with their names |

Restore currently changes **nothing** in any sample (`restoreDiffs` are all empty). The samples
carry `index` ("a0", "a1", …) and `boundElements: []`. Without them, restore adds `index`, bumps `version`
1→2, sets new `versionNonce`/`updated` values, and turns `boundElements: null` into `[]`. None of that affects rendering.

## Samples

| file | covers |
|---|---|
| 01-shapes | rectangle sharp / roundness {type:3}, diamond sharp / {type:2}, ellipse, circle, small and large radius |
| 02-fills | hachure / cross-hatch / solid / zigzag on rectangle, ellipse and diamond; rounded rect fills; thick hachure |
| 03-stroke-styles | solid/dashed/dotted × strokeWidth 1/2/4 (rects), plus dashed/dotted ellipse and diamond |
| 04-roughness | roughness 0/1/2 × rect (hachure), rounded rect, ellipse (cross-hatch), diamond, curved arrow |
| 05-opacity-angle | opacity 50 over an opaque shape; rotated rect, ellipse, diamond, rounded rect and text; translucent arrow |
| 06-arrows | straight 2-point, diagonal sharp, multi-point sharp (roundness null) vs curved ({type:2}), 3-point curve, dashed/dotted, roughness 0/2 |
| 07-arrowheads | end / both / start for arrow, triangle(_outline), bar, dot, circle(_outline), diamond(_outline) at strokeWidth 2/1/4; curved arrow with heads |
| 08-bindings | arrows with startBinding/endBinding (focus 0 and ±0.6, gap 6/8) and `boundElements` on rect, rounded rect, ellipse and diamond; half-bound arrow |
| 09-lines | open polyline sharp/curved, closed loops with hachure, solid (curved), cross-hatch (dashed) and zigzag (roughness 0) fills |
| 10-freedraw | pressures with simulatePressure false, simulatePressure true, strokeWidth 1/2/4, opacity, closed freedraw with fill |
| 11-text | fontFamily 1 Virgil, 2 Helvetica, 3 Cascadia, 5 Excalifont, 6 Nunito, 7 Lilita One, 8 Comic Shanns; left/center/right multi-line; 16/36 px; Korean; autoResize:false wrapping |
| 12-bound-text | text in rect, diamond and ellipse (center/middle), top-left and bottom-right alignment, wrapped label, straight and curved arrow labels (Korean) |
| 13-workflow | 시작 → 요청 접수 → 검토/승인? diamond → 예/아니오 curved, labelled branches → 완료, with bindings, mixed fills and a dashed arrow |
| 14-groups-colors | groupIds, colours, mixed widths, roughness and fills |

## SVG structure (exportToSvg, Excalidraw 0.18.1)

```
<svg viewBox="0 0 W H" width="W" height="H">           W,H = common bounds + 2*padding (scale 1)
  <!-- svg-source:excalidraw --><metadata/>
  <defs><style class="style-fonts">@font-face{font-family:Excalifont; src:url(data:font/woff2;base64,…)} …</style></defs>
  <rect x="0" y="0" width="W" height="H" fill="#ffffff"/>   background
  … one entry per element, in scene (z) order …
</svg>
```

* **No ids or classes** on element nodes. Map nodes to elements by **order**: elements are drawn in
  array order (deleted elements skipped). A **bound text element is not drawn at its own position in the array**. It is emitted
  immediately after its container (`renderSceneToSvg`: container, then `getBoundTextElement(container)`).
  Free text is drawn in array order. The only names that appear are `mask-<arrowId>` on arrows that have labels.
* **Global offset**: `offsetX = -minX + padding` and `offsetY = -minY + padding`, where `[minX,minY]` comes from
  `getCommonBounds(elements)`. Those bounds use the *rough-generated* shape extents for linear and freedraw elements,
  and the rotated bounding box for rotated elements. That is why `translate()` values like `10.93` are fractional
  even when every element sits on an integer x. The export size depends on the same bounds.
* **Shapes** (rectangle/diamond/ellipse):
  `<g stroke-linecap="round" transform="translate(x+offX y+offY) rotate(angleDeg w/2 h/2)">`
  then one `<path>` per rough drawable set, in rough.js order: **fill first, then stroke**.
  * hachure / cross-hatch / zigzag fill: `<path d="M… C…" stroke="<backgroundColor>" stroke-width="<fillWeight>" fill="none">`.
    fillWeight = strokeWidth/2, so 2 → `1`.
  * solid fill: `<path d="M… L…/C…" stroke="none" stroke-width="0" fill="<backgroundColor>">`,
    with `fill-rule="evenodd"` on some shapes (diamond, line, freedraw).
  * outline: `<path d="…" stroke="<strokeColor>" stroke-width="<strokeWidth>" fill="none">`, plus
    `stroke-dasharray` for dashed/dotted.
  * Path coordinates are **local** (relative to element x/y) and **rounded to 2 decimals**: Excalidraw passes
    `fixedDecimalPlaceDigits: 2` (`MAX_DECIMALS_FOR_SVG_EXPORT`) to rough's svg renderer, so trailing zeros are dropped
    (`+n.toFixed(2)`). The PNG/canvas path is **not** rounded.
  * `rotate()` is in **degrees** around the element's own centre (w/2, h/2) in local coordinates.
* **Opacity**: `stroke-opacity="0.5" fill-opacity="0.5"` on the element's `<g>` (opacity/100). The attribute is omitted at 100.
* **Arrows and lines**: `<g stroke-linecap="round">` wraps one `<g transform="translate(..) rotate(deg w/2 h/2)">`
  per rough shape: the shaft first (closed lines put their fill path before the stroke, inside the same group, which carries `fill-rule="evenodd"`),
  then each arrowhead piece. An `arrow` head is two separate `<g>`s, one per side. triangle, dot and diamond heads are filled paths.
  Every arrow **and line** is followed by a `<mask>` sibling, which is **empty** (`<mask></mask>`) when the arrow has no label.
  With a label the outer group becomes `<g mask="url(#mask-<arrowId>)">`, and the mask is a white full-size rect
  plus a black rect covering the label box (that is how the line is cut under the label).
* **Freedraw**: `<g transform="translate(..) rotate(deg cx cy)" stroke="none"><path fill="<strokeColor>" d="M x,y Q …">`
  (perfect-freehand outline, numbers formatted with `toFixed(2)`). A closed freedraw with a background gets a rough fill path
  before the outline.
* **Text**: `<g transform="translate(x+offX y+offY) rotate(deg w/2 h/2)">` with **one `<text>` per line**:
  `x` = `0` / `width/2` / `width` with `text-anchor` = `start` / `middle` / `end` (left/center/right),
  `y` = `lineIndex * fontSize * lineHeight + verticalOffset`,
  `dominant-baseline="alphabetic"`, `direction="ltr"`, `style="white-space: pre;"`, `font-size="20px"`, `fill=strokeColor`.
  `verticalOffset = fontSizeEm*ascender + (lineHeightPx - fontSizeEm*ascender + fontSizeEm*descender)/2`, where
  `fontSizeEm = fontSize/unitsPerEm` (`getVerticalOffset`, font metrics from Excalidraw's FONT_METADATA).
  For Excalifont at 20px with lineHeight 1.25 the result is y=17.62 for line 1 and 42.62 for line 2.
  `font-family` is the family plus fallbacks, e.g. `"Excalifont, Xiaolai, Segoe UI Emoji"`, `"Virgil, Segoe UI Emoji"`,
  `"Helvetica, Segoe UI Emoji"`, `"Cascadia, Segoe UI Emoji"`, `"Nunito, Segoe UI Emoji"`, `"Lilita One, …"`, `"Comic Shanns, …"`.
  Bound text uses its own stored x/y/width/height. The renderer does not lay it out again.

## Notes / caveats

* Fonts load from the local copies: Excalifont, Virgil, Xiaolai (CJK, including Hangul), Cascadia, Nunito, Lilita One
  and Comic Shanns (see `out/fonts.json`). **Helvetica (2)** has no webfont, so Chromium uses whatever the system maps
  "Helvetica" to (Liberation Sans here; on Windows it is Arial).
* Korean text in **Excalifont** falls back to **Xiaolai**, which is real Excalidraw behaviour. Korean in **Virgil/Nunito/…**
  has no CJK fallback in Excalidraw's font stack, so it comes from a system font (WenQuanYi Zen Hei on this Linux box). Both
  widths and glyphs depend on the machine.
* Text widths come from Chromium canvas `measureText`. They can differ slightly from DirectWrite/WPF measurements.
* The SVG (fonts inlined) and the PNG (canvas) are drawn from the same seeds, so the rough geometry is identical.
  Only the precision differs: 2 decimals in the SVG, full precision on the canvas.
