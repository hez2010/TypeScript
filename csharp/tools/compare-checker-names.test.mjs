import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { classifyNameDifference } from "./compare-checker-names.mjs";
const source = Buffer.from("export default function named(){named;} named; default;"), input = { fileName: "/new.ts", text: source.toString("base64") };
const data = [[[1, 2, 3, [[0, 1, 2304, []]]]], [["x", 4, 5, [5]]], [[1, 2, 3, 4, 5, [5], 0]]];
const exported = { data, syntaxFingerprint: "fingerprint", parseErrors: 1 }, rebound = structuredClone(exported);
const independent = { version: "6.0.3", errors: 1, sourceSha256: createHash("sha256").update(source).digest("hex") };
const call = (i = input, a = data, e = exported, r = rebound, evidence = independent) => classifyNameDifference(i, a, e, r, null, evidence);
assert.equal(call(), "malformed-parser-recovery:same-tree-name-resolution");
let guards = 0;
function reject(mutator) {
    const args = structuredClone([input, data, exported, rebound, independent]);
    mutator(args);
    assert.equal(call(...args), null);
    guards++;
}
reject(a => {
    a[0].text = Buffer.from("different source").toString("base64");
});
reject(a => {
    a[2].syntaxFingerprint = "changed";
});
reject(a => {
    a[3].syntaxFingerprint = "";
});
reject(a => {
    a[2].parseErrors = 0;
});
reject(a => {
    a[3].parseErrors = 0;
});
reject(a => {
    a[4].errors = 0;
});
reject(a => {
    a[4].version = "";
});
reject(a => {
    a[4].sourceSha256 = "other";
});
reject(a => {
    a[3].data[0][0][2] = 99;
});
reject(a => {
    a[3].data[0][0][3][0][2] = 9999;
});
reject(a => {
    a[3].data[1][0][1] = 8;
});
reject(a => {
    a[3].data[2][0][4] = 99;
});
reject(a => {
    a[2].data = [[], [], []];
});
reject(a => {
    a[1] = [];
});
reject(a => {
    const changed = Buffer.from("function broken(");
    a[0].text = changed.toString("base64");
    a[4].sourceSha256 = createHash("sha256").update(changed).digest("hex");
});
console.log(`${guards} name-resolution difference-policy guards passed`);
