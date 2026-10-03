import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { root, json, sha256, referenceRevision } from "./common.mjs";
import { comparableServiceResult, orderedFileRenameEdits, unorderedFileRenameResults } from "./service-response-comparison.mjs";

const evidencePath = path.join(root, "csharp/compatibility/evidence/phase7-file-rename-ordering.json");
const canonical = value => JSON.stringify(value, function (_, item) {
    return item && typeof item === "object" && !Array.isArray(item) ? Object.fromEntries(Object.entries(item).sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)) : item;
});
if (process.argv.includes("--recording")) {
    const directory = path.resolve(process.argv[process.argv.indexOf("--recording") + 1]);
    const source = await readFile(path.join(directory, "original-inputs.jsonl"), "utf8");
    const inputs = source.trim().split(/\r?\n/).map(JSON.parse), groups = new Map();
    for (const input of inputs) {
        const key = canonical([input.name, input.encoding, input.params, input.capabilities, input.preferences, input.files]);
        if (!groups.has(key)) groups.set(key, []);
        groups.get(key).push(input);
    }
    const cases = new Map(), requests = [];
    for (const runs of groups.values()) {
        assert.equal(runs.length, 40);
        assert.equal(new Set(runs.map(input => canonical(orderedFileRenameEdits(input.result)))).size, 1, "Only commuting document order may differ");
        const variants = [...new Map(runs.map(input => [canonical(input.result), input.result])).values()];
        const first = runs[0];
        requests.push({ name: first.name, encoding: first.encoding, params: first.params, runs: runs.length, variants: variants.length });
        if (variants.length > 1 && !cases.has(first.name)) cases.set(first.name, { name: first.name, examples: variants.slice(0, 2) });
    }
    assert.deepEqual([...cases.keys()].sort(), unorderedFileRenameResults.toSorted());
    await json(evidencePath, { referenceRevision, method: "workspace/willRenameFiles", requests: inputs.length, queries: requests,
        cases: [...cases.values()], inputsSha256: sha256(source), policy: "Sort contiguous text-document edits only when each edits a distinct URI; preserve resource-operation positions, text-edit ordering, every field, and duplicates.",
        reproduction: "node csharp/tools/syntax-services.mjs --file-rename --record-only --reference-count 40 --directory built/csharp/phase7-validation/file-rename-ordering" });
}
const evidence = JSON.parse(await readFile(evidencePath, "utf8"));
assert.deepEqual(evidence.cases.map(input => input.name).sort(), unorderedFileRenameResults.toSorted());
let controls = 0;
for (const example of evidence.cases) {
    const input = { name: example.name, method: evidence.method }, value = example.examples[0];
    const expected = comparableServiceResult(input, value);
    assert.deepEqual(comparableServiceResult(input, example.examples[1]), expected);
    const reject = changed => { assert.notDeepEqual(comparableServiceResult(input, changed), expected); controls++; };
    for (const change of [
        item => item.documentChanges.shift(), item => item.documentChanges.push(structuredClone(item.documentChanges[0])),
        item => item.documentChanges[0].edits.shift(), item => item.documentChanges[0].edits.push(structuredClone(item.documentChanges[0].edits[0])),
        item => item.documentChanges[0].edits[0].newText += "wrong", item => item.documentChanges[0].edits[0].range.end.character++,
        item => item.documentChanges[0].textDocument.uri += "wrong", item => item.documentChanges[0].textDocument.version = 1,
    ]) { const changed = structuredClone(value); change(changed); reject(changed); }
    const operation = { kind: "rename", oldUri: "file:///old.ts", newUri: "file:///new.ts" };
    const between = { documentChanges: [value.documentChanges[0], operation, value.documentChanges[1]] };
    assert.notDeepEqual(comparableServiceResult(input, between), comparableServiceResult(input, { documentChanges: [value.documentChanges[1], operation, value.documentChanges[0]] })); controls++;
    const withinDocument = { documentChanges: [{ textDocument: value.documentChanges[0].textDocument, edits: [value.documentChanges[0].edits[0], value.documentChanges[1].edits[0]] }] };
    const reversed = structuredClone(withinDocument); reversed.documentChanges[0].edits.reverse();
    assert.notDeepEqual(comparableServiceResult(input, withinDocument), comparableServiceResult(input, reversed)); controls++;
    assert.equal(comparableServiceResult({ ...input, name: "UnlistedCase" }, value), value); controls++;
    assert.equal(comparableServiceResult({ ...input, method: "textDocument/rename" }, value), value); controls++;
}
console.log(`${evidence.cases.length} scoped file-rename ordering policies, ${controls} negative controls passed`);
