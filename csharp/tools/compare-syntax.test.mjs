import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import ts from "typescript";
import { classifySyntaxDifference } from "./compare-syntax.mjs";

const [valid, invalid] = JSON.parse(readFileSync(new URL("../tests/fixtures/parser/policy-guards.json", import.meta.url), "utf8"));
let checks = 0;
function check(sample, mutate, policy) {
    const candidate = structuredClone(sample.expected);
    mutate(candidate.details);
    // A changed output must never reuse an exact fixture approval.
    candidate.hash = "changed-for-policy-negative-control";
    assert.equal(classifySyntaxDifference(sample.fixture, sample.expected, candidate), policy);
    checks++;
}
check(valid, () => {}, "exact");
check(valid, ([nodes]) => {
    nodes[0][3] ^= 8192;
}, "equivalent-parser-context");
check(valid, ([nodes]) => {
    const text = Buffer.from(valid.fixture.text, "base64").toString();
    const name = nodes.find(node => node[4] === Buffer.from("value").toString("base64"));
    name[1] = ts.skipTrivia(text, name[1]);
}, "equivalent-trivia-boundary");
check(valid, ([nodes]) => {
    nodes[0][3] ^= 32;
}, null);
check(valid, ([nodes]) => {
    nodes[0][0]++;
}, null);
check(valid, ([nodes]) => {
    nodes[0][5]++;
}, null);
check(valid, ([nodes]) => {
    nodes[0][2]++;
}, null);
check(valid, ([nodes]) => {
    nodes[0][1] = -1;
}, null);
check(valid, ([nodes]) => {
    nodes.find(node => node[4])[4] = Buffer.from("changed").toString("base64");
}, null);
check(valid, ([nodes]) => {
    nodes.find(node => node[6].Text)[6].Text = Buffer.from("changed").toString("base64");
}, null);
check(valid, ([nodes]) => {
    nodes[0][7].Statements[0]++;
}, null);
check(valid, ([nodes]) => {
    nodes[0][7].Statements[3] = !nodes[0][7].Statements[3];
}, null);
check(valid, ([, errors]) => {
    errors.push([1003, 0, 1]);
}, null);
check(invalid, ([, errors]) => {
    errors[0][0] = 1005;
}, "invalid-source-recovery");
check(invalid, ([, errors]) => {
    errors.length = 0;
}, null);
check(invalid, ([, errors]) => {
    errors[0][1] = -1;
}, null);
check(invalid, ([, errors]) => {
    errors[0][1] = 0;
    errors[0][2] = 1;
}, null);
const documented = JSON.parse(readFileSync(new URL("../tests/fixtures/parser/source-differences.json", import.meta.url), "utf8")).cases[0];
assert.equal(classifySyntaxDifference(documented.input, documented.expected, documented.actual), documented.policy);
assert.equal(classifySyntaxDifference(documented.input, documented.expected, { ...documented.actual, hash: "changed" }), null);
assert.equal(classifySyntaxDifference({ ...documented.input, text: Buffer.from("/changed/u").toString("base64") }, documented.expected, documented.actual), null);
checks += 3;
check(valid, details => {
    details[2] = [[8010, 0, 1]];
}, null);
console.log(`Parser comparison policies: ${checks} acceptance and regression guards passed`);
