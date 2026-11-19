// rough.js 4.6.4로 여러 도형·옵션의 결과(ops)를 JSON으로 남긴다. C# 포팅(RoughGenerator)이 같은 값을 내는지 테스트에 쓴다.
// 실행: NODE_PATH=<roughjs가 있는 node_modules> node rough-golden.mjs > ../../tests/DayDeck.Core.Tests/Golden/rough.json
import { createRequire } from 'module';
const require = createRequire(import.meta.url);
const rough = require('roughjs/bundled/rough.cjs.js');
const gen = rough.generator();
const cases = [];
const base = (extra) => ({ seed: 1234567, ...extra });
const variants = [
  base({}), base({ roughness: 0 }), base({ roughness: 2 }), base({ roughness: 1, preserveVertices: true }),
  base({ strokeWidth: 4, hachureGap: 16, fillWeight: 2 }), base({ disableMultiStroke: true, strokeWidth: 2.5 }),
  { seed: 987654321, roughness: 1, strokeWidth: 2, hachureGap: 8, fillWeight: 1 },
];
const fills = [undefined, 'hachure', 'cross-hatch', 'solid', 'zigzag', 'dashed', 'zigzag-line'];
const rareFills = ['dashed', 'zigzag-line'];
let n = 0;
for (const v of variants) {
  for (const fs of fills) {
    if (rareFills.includes(fs) && v !== variants[0]) continue;
    const o = { ...v, ...(fs ? { fill: '#a5d8ff', fillStyle: fs } : {}) };
    const tag = JSON.stringify(o);
    cases.push({ kind: 'rectangle', args: [10, 20, 180, 90], o, d: gen.rectangle(10, 20, 180, 90, o) });
    cases.push({ kind: 'ellipse', args: [100, 60, 160, 90], o, d: gen.ellipse(100, 60, 160, 90, o) });
    cases.push({ kind: 'polygon', args: [[[90, 0], [180, 50], [90, 100], [0, 50]]], o, d: gen.polygon([[90, 0], [180, 50], [90, 100], [0, 50]], o) });
    cases.push({ kind: 'curve', args: [[[0, 0], [60, 40], [120, -10], [200, 30]]], o, d: gen.curve([[0, 0], [60, 40], [120, -10], [200, 30]], o) });
    cases.push({ kind: 'curve', args: [[[0, 0], [100, 80], [200, 0]]], o, d: gen.curve([[0, 0], [100, 80], [200, 0]], o) });
    cases.push({ kind: 'path', args: ['M 32 0 L 168 0 Q 200 0, 200 32 L 200 88 Q 200 120, 168 120 L 32 120 Q 0 120, 0 88 L 0 32 Q 0 0, 32 0'], o,
      d: gen.path('M 32 0 L 168 0 Q 200 0, 200 32 L 200 88 Q 200 120, 168 120 L 32 120 Q 0 120, 0 88 L 0 32 Q 0 0, 32 0', o) });
    if (!fs) {
      cases.push({ kind: 'line', args: [0, 0, 300, 120], o, d: gen.line(0, 0, 300, 120, o) });
      cases.push({ kind: 'linearPath', args: [[[0, 0], [80, 60], [160, 10], [260, 700]]], o, d: gen.linearPath([[0, 0], [80, 60], [160, 10], [260, 700]], o) });
      cases.push({ kind: 'circle', args: [20, 20, 14], o, d: gen.circle(20, 20, 14, o) });
    }
    n++;
  }
}
const out = cases.map(c => ({ kind: c.kind, args: c.args, options: c.o, sets: c.d.sets.map(s => ({ type: s.type, ops: s.ops.map(op => ({ op: op.op, data: op.data.map(v => +v.toFixed(5)) })) })) }));
process.stdout.write(JSON.stringify(out));
