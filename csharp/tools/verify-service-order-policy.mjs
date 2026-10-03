import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { comparableServiceResult } from "./service-response-comparison.mjs";

const evidence = JSON.parse(await readFile(new URL("../compatibility/evidence/phase7-reference-ordering.json", import.meta.url), "utf8"));
const cases = [{ name: evidence.test, method: evidence.method, result: evidence.runs[0].result },
    ...evidence.runtimeImports.cases.map(test => ({ name: test.name, method: "textDocument/references", result: test.examples[0] }))];
let controls = 0;
for (const input of cases) {
    const expected = comparableServiceResult(input, input.result);
    assert.deepEqual(comparableServiceResult(input, input.result.toReversed()), expected);
    const changedRange = structuredClone(input.result), changedUri = structuredClone(input.result);
    const location = changedRange[0];
    (location.targetSelectionRange ?? location.range).end.character++;
    const uri = "targetUri" in changedUri[0] ? "targetUri" : "uri";
    changedUri[0][uri] += "-wrong";
    for (const changed of [input.result.slice(1), [...input.result, input.result[0]], changedRange, changedUri]) {
        assert.notDeepEqual(comparableServiceResult(input, changed), expected); controls++;
    }
    assert.equal(comparableServiceResult({ ...input, name: "UnlistedCase" }, input.result), input.result); controls++;
    assert.equal(comparableServiceResult({ ...input, method: "textDocument/definition" }, input.result), input.result); controls++;
}
console.log(`${cases.length} scoped ordering policies, ${controls} negative controls passed`);
