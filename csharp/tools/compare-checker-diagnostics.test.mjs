import assert from "node:assert/strict";
import { sameDiagnostics } from "./compare-checker-diagnostics.mjs";

const leaf = { file: "/main.ts", start: 4, length: 2, code: 2304, category: 1, key: "Cannot_find_name_0_2304", arguments: ["name"], chain: [], related: [] };
const diagnostic = { ...leaf, chain: [leaf, { ...leaf, start: 5 }], related: [{ ...leaf, file: "/other.ts" }, { ...leaf, file: "/third.ts" }] };
assert(sameDiagnostics([leaf, diagnostic], [diagnostic, leaf]));
assert(sameDiagnostics([Object.fromEntries(Object.entries(leaf).reverse())], [leaf]));
assert(!sameDiagnostics(undefined, []));
assert(!sameDiagnostics([leaf, leaf], [leaf]));
assert(!sameDiagnostics([leaf], [diagnostic]));
for (const [field, changed] of Object.entries({ file: "/wrong.ts", start: 5, length: 3, code: 2305, category: 0, key: "wrong", arguments: ["other"], chain: [], related: [] })) assert(!sameDiagnostics([{ ...diagnostic, [field]: changed }], [diagnostic]), field);
assert(!sameDiagnostics([{ ...diagnostic, chain: [...diagnostic.chain].reverse() }], [diagnostic]));
assert(!sameDiagnostics([{ ...diagnostic, related: [...diagnostic.related].reverse() }], [diagnostic]));
assert(!sameDiagnostics([{ ...diagnostic, chain: [{ ...leaf, arguments: ["nested change"] }, diagnostic.chain[1]] }], [diagnostic]));
console.log("17 complete-diagnostic comparison assertions passed");
