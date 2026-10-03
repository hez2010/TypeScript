import assert from "node:assert/strict";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { sha256, referenceRevision } from "./common.mjs";
import { comparableServiceResult, unorderedCodeLensResults } from "./service-response-comparison.mjs";

const target = new URL("../compatibility/evidence/phase7-code-lens-ordering.json", import.meta.url);
if (process.argv.includes("--recording")) {
    const directory = process.argv[process.argv.indexOf("--recording") + 1];
    const text = await readFile(path.join(directory, "original-inputs.jsonl"), "utf8");
    const inputs = text.trim().split(/\r?\n/).map(JSON.parse), groups = new Map();
    for (const input of inputs) {
        const key = JSON.stringify([input.name, input.encoding, input.method, input.params]);
        if (!groups.has(key)) groups.set(key, []);
        groups.get(key).push(input);
    }
    const requests = [];
    for (const group of groups.values()) {
        assert.equal(group.length, 40);
        const first = group[0], variants = new Map();
        for (const input of group) {
            assert.deepEqual(comparableServiceResult(input, input.result), comparableServiceResult(first, first.result));
            const key = JSON.stringify(input.result);
            if (!variants.has(key)) variants.set(key, { count: 0, result: input.result });
            variants.get(key).count++;
        }
        requests.push({ name: first.name, method: first.method, encoding: first.encoding, params: first.params, variants: [...variants.values()] });
    }
    assert.equal(requests.filter(request => request.variants.length > 1).length, 2);
    await writeFile(target, JSON.stringify({ referenceRevision, recording: directory, inputsSha256: sha256(text), captures: inputs.length,
        repetitionsPerRequest: 40, policy: unorderedCodeLensResults, requests }, null, 4) + "\n");
}
const evidence = JSON.parse(await readFile(target, "utf8"));
assert.equal(evidence.referenceRevision, referenceRevision);
assert.deepEqual(evidence.policy, unorderedCodeLensResults);
let controls = 0;
for (const input of evidence.requests.filter(request => request.variants.length > 1)) {
    const result = input.variants[0].result, expected = comparableServiceResult(input, result);
    const reversed = structuredClone(result); reversed.command.arguments[2].reverse();
    assert.deepEqual(comparableServiceResult(input, reversed), expected);
    for (const mutate of [
        changed => changed.command.arguments[2].pop(),
        changed => changed.command.arguments[2].push(changed.command.arguments[2][0]),
        changed => changed.command.arguments[2][0].uri += "-wrong",
        changed => changed.command.arguments[2][0].range.end.character++,
        changed => changed.command.title = "wrong count",
        changed => changed.command.command = "wrong command",
        changed => changed.command.arguments[0] += "-wrong",
        changed => changed.command.arguments[1].character++,
        changed => changed.data.position++,
        changed => changed.range.end.character++,
    ]) {
        const changed = structuredClone(result); mutate(changed);
        assert.notDeepEqual(comparableServiceResult(input, changed), expected); controls++;
    }
    for (const changed of [{ ...input, name: "UnlistedCase" }, { ...input, method: "textDocument/codeLens" },
        { ...input, params: { ...input.params, data: { ...input.params.data, position: 99 } } }]) {
        assert.equal(comparableServiceResult(changed, reversed), reversed); controls++;
    }
}
console.log(`${evidence.captures} original captures; ${controls} negative controls passed`);
