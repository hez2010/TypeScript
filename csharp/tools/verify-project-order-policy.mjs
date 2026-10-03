import assert from "node:assert/strict";
import { readFile, writeFile } from "node:fs/promises";
import { comparableProjectRoots, unorderedProjectRoots } from "./project-response-comparison.mjs";
import { referenceRevision, sha256 } from "./common.mjs";

const target = "csharp/compatibility/evidence/phase7-project-ordering.json";
if (process.argv.includes("--record")) {
    const captures = [];
    for (const [name, file] of [
        ["TestProjectProgramUpdateKind/NewFiles_on_root_addition", "built/csharp/phase7-validation/root-addition-order-control.jsonl"],
        ["TestContentMapperCreatedFileAdoptedByConfiguredProject", "built/csharp/phase7-validation/project-created-mapper-ordering/original-operations.jsonl"],
    ]) {
        const bytes = await readFile(file);
        const rows = bytes.toString().trim().split(/\r?\n/).map(JSON.parse).filter(row => row.name === name && row.snapshot === 2);
        assert.equal(rows.length, 20);
        const inputs = rows.map(({ expected, session, mapperResults, ...input }) => input);
        for (const input of inputs) assert.deepEqual(input, inputs[0]);
        captures.push({ name, file, sha256: sha256(bytes), input: inputs[0], outputs: rows.map(row => row.expected) });
    }
    await writeFile(target, JSON.stringify({ referenceRevision,
        policy: "Sort only roots and source-file arrays for these two original tests. Preserve every element, field, diagnostic, project identity and other array order.", captures }, null, 2) + "\n");
}
const evidence = JSON.parse(await readFile(target));
assert.equal(evidence.referenceRevision, referenceRevision);
assert.deepEqual(new Set(evidence.captures.map(capture => capture.name)), unorderedProjectRoots);
let controls = 0;
const cases = [];
for (const capture of evidence.captures) {
    const original = capture.outputs[0];
    const orders = new Map();
    for (const output of capture.outputs) {
        assert.deepEqual(comparableProjectRoots(output, capture.name), comparableProjectRoots(original, capture.name));
        const order = JSON.stringify(output.projects[0].roots); orders.set(order, (orders.get(order) ?? 0) + 1);
    }
    assert.equal(orders.size, 2);
    for (const mutate of [
        value => { value.projects[0].sources[0].text += "changed"; },
        value => { value.projects[0].sources[0].kind++; },
        value => { value.projects[0].sources[0].fileName += "changed"; },
        value => { value.projects[0].sources.pop(); },
        value => { value.projects[0].roots.pop(); },
        value => { value.projects[0].program++; },
        value => { value.projects[0].id += "changed"; },
        value => { value.projects[0].dirty = !value.projects[0].dirty; },
        value => { (value.pushes ??= []).push({ uri: "file:///changed", diagnostics: [] }); },
    ]) {
        const changed = structuredClone(original); mutate(changed);
        assert.notDeepEqual(comparableProjectRoots(changed, capture.name), comparableProjectRoots(original, capture.name)); controls++;
    }
    const unlisted = structuredClone(original); unlisted.projects[0].sources.reverse();
    assert.notDeepEqual(comparableProjectRoots(unlisted, "UnlistedTest"), comparableProjectRoots(original, "UnlistedTest")); controls++;
    cases.push({ name: capture.name, orders: [...orders.values()] });
}
console.log(JSON.stringify({ cases, corruptionControls: controls }));
