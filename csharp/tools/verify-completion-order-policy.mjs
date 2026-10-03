import assert from "node:assert/strict";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { sha256, referenceRevision } from "./common.mjs";
import { comparableServiceResult } from "./service-response-comparison.mjs";

const target = new URL("../compatibility/evidence/phase7-completion-ordering.json", import.meta.url);
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
        assert.equal(group.length, 8);
        const first = group[0], variants = new Map();
        for (const input of group) {
            assert.deepEqual(comparableServiceResult(input, input.result), comparableServiceResult(first, first.result));
            const key = JSON.stringify(input.result);
            if (!variants.has(key)) variants.set(key, { count: 0, result: input.result });
            variants.get(key).count++;
        }
        requests.push({ name: first.name, method: first.method, encoding: first.encoding, params: first.params, variants: [...variants.values()] });
    }
    assert.ok(requests.some(request => request.variants.length > 1));
    await writeFile(target, JSON.stringify({ referenceRevision, recording: directory, inputsSha256: sha256(text), captures: inputs.length,
        repetitionsPerRequest: 8, policy: "Completion items are canonicalized by exact sortText and label; ties remain in response order. No fields or duplicates are removed.",
        sources: ["tsc/internal/fourslash/fourslash.go:1339", "tsc/internal/ls/completions.go:3828"], requests }, null, 2) + "\n");
}
const evidence = JSON.parse(await readFile(target, "utf8"));
assert.equal(evidence.referenceRevision, referenceRevision);
let controls = 0, ties = 0;
for (const input of evidence.requests.filter(request => request.method === "textDocument/completion")) {
    const result = input.variants[0].result;
    if (!result?.items?.length) continue;
    const expected = comparableServiceResult(input, result);
    // Moving an entire group of equal keys keeps each group's tie order intact.
    const groups = new Map();
    for (const item of result.items) {
        const key = JSON.stringify([item.sortText, item.label]);
        if (!groups.has(key)) groups.set(key, []);
        groups.get(key).push(item);
    }
    const reordered = { ...result, items: [...groups.values()].reverse().flat() };
    assert.deepEqual(comparableServiceResult(input, reordered), expected);
    for (const mutate of [
        changed => changed.items.pop(), changed => changed.items.push(structuredClone(changed.items[0])),
        changed => changed.items[0].label += "-wrong", changed => changed.items[0].sortText += "-wrong",
        changed => changed.items[0].kind = 99, changed => changed.items[0].data.position++,
        changed => changed.items[0].detail = "wrong", changed => changed.items[0].commitCharacters = ["!"],
        changed => changed.isIncomplete = !changed.isIncomplete,
    ]) {
        const changed = structuredClone(result); mutate(changed);
        assert.notDeepEqual(comparableServiceResult(input, changed), expected); controls++;
    }
    for (const group of groups.values()) if (group.length > 1 && JSON.stringify(group[0]) !== JSON.stringify(group[1])) {
        const changed = structuredClone(result), a = result.items.indexOf(group[0]), b = result.items.indexOf(group[1]);
        [changed.items[a], changed.items[b]] = [changed.items[b], changed.items[a]];
        assert.notDeepEqual(comparableServiceResult(input, changed), expected); controls++; ties++;
    }
    assert.equal(comparableServiceResult({ ...input, method: "completionItem/resolve" }, reordered), reordered);
}
assert.ok(ties > 0);
console.log(`${evidence.captures} original captures; ${controls} negative controls, including ${ties} tie-order controls passed`);
