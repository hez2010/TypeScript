import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { gunzipSync } from "node:zlib";
import { documentationBytePositions } from "./documentation-byte-positions.mjs";
import {
    classifyDocumentationDifference,
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
console.log("Documentation policy: 20 negative controls passed, including same-kind host reassociation.");
