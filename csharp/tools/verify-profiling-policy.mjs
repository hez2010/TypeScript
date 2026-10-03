import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { output, json, sha256 } from "./common.mjs";
import { profilingControl } from "./scripted-build-contracts.mjs";
import { compareProfiling } from "./scripted-profiling-contracts.mjs";

const directory = process.argv[2] ?? path.join(output, "scripted-builds/phase8-profile-contracts");
const files = await Promise.all(["inputs", "reference", "candidate"].map(name => readFile(path.join(directory, `${name}.json`))));
const [inputs, reference, candidate] = files.map(bytes => JSON.parse(bytes));
const controls = [];
for (let index = 0; index < inputs.length; index++) {
    const input = inputs[index], policy = profilingControl(input); if (!policy) continue;
    compareProfiling(reference[index], candidate[index], input);
    function reject(name, change) {
        const changed = structuredClone(candidate[index]); change(changed);
        assert.throws(() => compareProfiling(reference[index], changed, input), name);
        controls.push({ case: input.name, mutation: name });
    }
    reject("ordinary diagnostic text", records => records[0].stdout = "unexpected\n" + records[0].stdout);
    reject("status", records => records[0].status++);
    if (policy.flag === "--extendedDiagnostics") {
        reject("shared file count", records => records[0].stdout = records[0].stdout.replace(/(Files:\s+)(\d+)/, (_, label, count) => label + (+count + 1)));
        reject("missing counter", records => records[0].stdout = records[0].stdout.replace(/^CLR managed bytes:.*\r?\n/m, ""));
        reject("invalid timing", records => records[0].stdout = records[0].stdout.replace(/(Total time:\s+)\S+/, "$1NaNs"));
    }
    else {
        function edit(records, suffix, change) {
            const file = records[0].writes.find(file => file.path.endsWith(suffix)); assert.ok(file);
            const data = JSON.parse(Buffer.from(file.textBase64, "base64")); change(data);
            file.textBase64 = Buffer.from(JSON.stringify(data)).toString("base64");
        }
        reject("missing trace", records => records[0].writes = records[0].writes.filter(file => !file.path.endsWith("/trace.json")));
        reject("missing type reference", records => edit(records, "types_0.json", types => types[0].unionTypes = [99999999]));
        reject("unclosed event", records => edit(records, "/trace.json", events => events.splice(events.findIndex(event => event.ph === "E"), 1)));
        reject("invalid phase", records => edit(records, "/trace.json", events => events[0].ph = "invalid"));
        reject("undeclared checker", records => edit(records, "/trace.json", events => events.find(event => event.ph === "B").args = { checkerId: 99999999 }));
    }
}
assert.ok(controls.length >= 25);
const summary = { source: directory, inputs: files.map(bytes => sha256(bytes)), controls: controls.length, mutations: controls };
await json(path.join(directory, "profiling-policy-controls.json"), summary);
console.log(JSON.stringify({ corruptionControls: controls.length }));
