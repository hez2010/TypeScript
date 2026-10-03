import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { gunzipSync } from "node:zlib";
import { documentationBytePositions } from "./documentation-byte-positions.mjs";
import {
    classifyDocumentationDifference,
    convergesToReference,
    digest,
    documentationMeaning,
} from "./compare-documentation.mjs";

const evidence = JSON.parse(gunzipSync(readFileSync(new URL("../tests/fixtures/jsdoc/documentation-evidence.json.gz", import.meta.url))));
const sample = evidence.find(record => record.actual[0].length >= 2 && record.actual[0][0][0] === record.actual[0][1][0] && record.actual[0][0][1] !== record.actual[0][1][1]);
assert.ok(sample, "Need distinct same-kind documentation hosts");
sample.expected = documentationBytePositions(sample.input, sample.expected);
sample.actual = documentationBytePositions(sample.input, sample.actual);
assert.ok(classifyDocumentationDifference(sample.input, sample.expected, sample.actual));
const mutations = [
    out => {
        out[0][0][1] = out[0][1][1];
        out[0][0][2] = out[0][1][2];
    },
    out => {
        [out[0][0][3], out[0][1][3]] = [out[0][1][3], out[0][0][3]];
    },
    out => {
        out[0][0][1]++;
    },
    out => {
        out[0][0][2]++;
    },
    out => {
        out[0][0][0]++;
    },
    out => {
        out[0].reverse();
    },
    out => {
        out[0].pop();
    },
    out => {
        out[0].push(structuredClone(out[0][0]));
    },
    out => {
        out[0][0][3][0][0][0]++;
    },
    out => {
        out[0][0][3][0][0][1]++;
    },
    out => {
        out[0][0][3][0][0][2]++;
    },
    out => {
        out[0][0][3][0][0][3] ^= 8;
    },
    out => {
        out[0][0][3][0][0][4]++;
    },
    out => {
        out[0][0][3][0][0][5].UnexpectedScalar = true;
    },
    out => {
        out[0][0][3][0][0][6].UnexpectedList = [0, 0, 0, false];
    },
    out => {
        out[1].push([1003, 0, 0]);
    },
    out => {
        out[0][0][3][0].pop();
    },
    out => {
        out[0][0][3].push([]);
    },
];
for (const mutate of mutations) {
    const changed = structuredClone(sample.actual);
    mutate(changed);
    assert.equal(classifyDocumentationDifference(sample.input, sample.expected, changed), null);
}
assert.equal(classifyDocumentationDifference({ ...sample.input, text: Buffer.from("different source").toString("base64") }, sample.expected, sample.actual), null);
assert.equal(classifyDocumentationDifference({ ...sample.input, name: "different-case.ts" }, sample.expected, sample.actual), null);
const moved = structuredClone(sample.actual);
moved[0][0][1] = moved[0][1][1];
moved[0][0][2] = moved[0][1][2];
assert.notEqual(digest(documentationMeaning(sample.actual)), digest(documentationMeaning(moved)), "Semantic proof must retain host identity independently of exact-hash pinning");
const previous = [[315, 12, 40, { Text: ["a", "bc"], Comment: [1, 12, 40, false] }]];
const expected = [[315, 10, 42, { Text: ["abc"], Comment: [1, 10, 42, false] }]];
const improved = [[315, 10, 40, { Text: ["a", "bc"], Comment: [1, 10, 40, false] }]];
assert.ok(convergesToReference(previous, expected, improved));
assert.ok(convergesToReference(previous, expected, previous));
assert.ok(convergesToReference(previous, expected, expected));
for (const mutate of [
    out => { out[0][0]++; },
    out => { out[0][1]++; },
    out => { out[0][2]--; },
    out => { out[0][3].Text = ["changed"]; },
    out => { out[0][3].Comment[3] = true; },
    out => { out[0][3].NewScalar = 1; },
    out => { out.pop(); },
    out => { out.push(structuredClone(out[0])); },
]) {
    const changed = structuredClone(improved);
    mutate(changed);
    assert.equal(convergesToReference(previous, expected, changed), false);
}
const retained = evidence.find(record => record.input.name.endsWith("jsDocAwaitMemberAccessTopLevel.ts::0"));
const retainedExpected = documentationBytePositions(retained.input, retained.expected);
const retainedActual = documentationBytePositions(retained.input, retained.actual);
const corrected = structuredClone(retainedActual);
const retainedOwner = corrected[0].at(-1);
const shared = retainedExpected[0].flatMap(host => host[3]).find(tree => tree[0][2] === retainedOwner[3][0][0][2]);
retainedOwner[3][0][0][1] = shared[0][1];
retainedOwner[3][0][0][6].Comment = structuredClone(shared[0][6].Comment);
assert.ok(classifyDocumentationDifference(retained.input, retainedExpected, corrected));
for (const mutate of [
    out => { out[0].at(-1)[0]++; },
    out => { out[0].at(-1)[1]++; },
    out => { out[0].at(-1)[2]++; },
    out => { out[0].at(-1)[3][0][0][1]--; },
    out => { out[0].at(-1)[3][0][0][3] ^= 8; },
    out => { out[0].at(-1)[3].push(structuredClone(out[0].at(-1)[3][0])); },
    out => { out[0].reverse(); },
    out => { out[1].push([1003, 0, 0]); },
]) {
    const changed = structuredClone(corrected);
    mutate(changed);
    assert.equal(classifyDocumentationDifference(retained.input, retainedExpected, changed), null);
}
console.log("Documentation policy: 36 negative controls passed, including same-kind host reassociation and field convergence.");
