import assert from "node:assert/strict";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { normalizeCheckerResponses } from "./api-checker-cases.mjs";
import { referenceRevision, sha256 } from "./common.mjs";

const target = new URL("../compatibility/evidence/phase7-api-service-ordering.json", import.meta.url);
if (process.argv.includes("--recording")) {
    const directory = process.argv[process.argv.indexOf("--recording") + 1];
    const inputs = JSON.parse(await readFile(path.join(directory, "inputs.json"), "utf8"))
        .filter(input => /completion-(globals|module-path|type)-configured/.test(input.name));
    const captures = [];
    for (let i = 0; i < 8; i++) {
        const text = await readFile(path.join(directory, `ordering-${i}.jsonl`), "utf8");
        captures.push({ sha256: sha256(text), responses: text.trim().split(/\r?\n/).map(JSON.parse) });
    }
    await writeFile(target, JSON.stringify({ referenceRevision, directory,
        policy: "Completion entries use the reference fourslash sortText/name ordering; exact ties, fields and duplicates are retained. Private symbol names normalize only the embedded class symbol handle using the same identity map as parent handles.",
        sources: ["tsc/internal/binder/binder.go:GetSymbolNameForPrivateIdentifier", "tsc/internal/fourslash/fourslash.go", "tsc/internal/ls/completions.go"],
        inputs, captures }, null, 2) + "\n");
}
const evidence = JSON.parse(await readFile(target, "utf8"));
assert.equal(evidence.referenceRevision, referenceRevision);
let variations = 0;
for (let i = 0; i < evidence.inputs.length; i++) {
    const requests = evidence.inputs[i].requests;
    const baseline = normalizeCheckerResponses(evidence.captures[0].responses[i], requests);
    for (const capture of evidence.captures.slice(1)) {
        assert.deepEqual(normalizeCheckerResponses(capture.responses[i], requests), baseline);
        if (JSON.stringify(capture.responses[i].map(response => response.result?.entries?.map(entry => entry.name)))
            !== JSON.stringify(evidence.captures[0].responses[i].map(response => response.result?.entries?.map(entry => entry.name)))) variations++;
    }
}
assert.ok(variations > 0);
const request = [{ method: "getCompletionsAtPosition", params: { snapshot: 1, project: "/p" } }];
const normalize = entries => normalizeCheckerResponses([{ result: { entries, isIncomplete: false } }], request);
const symbol = (id, owner, parent = owner) => ({ id, project: "/p", flags: 4, name: `__#${owner}@#field`, parent });
assert.deepEqual(normalize([{ name: "#field", sortText: "11", symbol: symbol(1, 2) }]),
    normalize([{ name: "#field", sortText: "11", symbol: symbol(51, 72) }]));
assert.notDeepEqual(normalize([{ name: "#field", sortText: "11", symbol: symbol(1, 2) }]),
    normalize([{ name: "#field", sortText: "11", symbol: symbol(51, 72, 73) }]));
const tied = [{ name: "x", sortText: "11", detail: "first" }, { name: "x", sortText: "11", detail: "second" }];
assert.notDeepEqual(normalize(tied), normalize(tied.toReversed()));
assert.notDeepEqual(normalize(tied), normalize(tied.slice(0, 1)));
console.log(JSON.stringify({ captures: evidence.captures.length, cases: evidence.inputs.length, varyingOrderComparisons: variations, controls: 4 }));
