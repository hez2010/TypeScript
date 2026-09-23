import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { gunzipSync } from "node:zlib";
import {
    classifyBindingDifference,
    parserBaseline,
} from "./compare-binding.mjs";

const archive = JSON.parse(gunzipSync(readFileSync(new URL("../compatibility/evidence/phase2-parser-differences.outputs.json.gz", import.meta.url))));
const recorded = archive.differences[0];
const input = recorded.case;
const baseline = parserBaseline(input);
assert(baseline);
const syntax = { expectedHash: baseline.expectedSha256, actualHash: baseline.actualSha256 };
const binding = [[[307, 0, Buffer.from(input.text, "base64").length, 0, 0, [], 0, 0, 0, 0]], [], [], [], [], 0];
const exported = { binding, syntaxFingerprint: "same-complete-tree" }, rebound = { binding, syntaxFingerprint: "same-complete-tree" };
assert(classifyBindingDifference(input, binding, exported, rebound, syntax));
let rejected = 0;
const reject = (fixture = input, actual = binding, candidate = exported, reference = rebound, parser = syntax) => {
    assert.equal(classifyBindingDifference(fixture, actual, candidate, reference, parser), null);
    rejected++;
};
reject({ ...input, text: Buffer.from("const changed=1").toString("base64") });
reject({ ...input, fileName: "different-mode.js" });
reject(input, binding, exported, rebound, { ...syntax, actualHash: "changed-parser" });
reject(input, binding, exported, rebound, { ...syntax, expectedHash: "changed-reference" });
reject(input, binding, { ...exported, syntaxFingerprint: "field-transfer-bug" });
reject(input, binding, exported, { ...rebound, syntaxFingerprint: "field-transfer-bug" });
reject(input, binding, exported, { ...rebound, binding: [] });
reject(input, binding, { ...exported, binding: [] });
for (
    const mutation of [
        b => b[0][0][3] = 1,
        b => b[0][0][5].push(["lost-local", 1]),
        b => b[1].push([4, "changed-symbol", 0, 0, 1, [1], [], []]),
        b => b[2].push([2, 0, 0, [], 0, 0, 0, []]),
        b => b[3].push([2300, 0, 1]),
        b => b[4].push(["lost-export", 1]),
        b => b[5] = 1,
    ]
) {
    const changed = structuredClone(binding);
    mutation(changed);
    reject(input, changed, { ...exported, binding: changed });
}
console.log(`Binding policy: acceptance plus ${rejected} mutation guards passed`);
