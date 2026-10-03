import assert from "node:assert/strict";
import { readFile, writeFile } from "node:fs/promises";
import { comparableTelemetry } from "./service-response-comparison.mjs";
import { referenceRevision, sha256 } from "./common.mjs";

const target = "csharp/compatibility/evidence/phase7-telemetry-ordering.json";
if (process.argv.includes("--record")) {
    const captures = [];
    for (const directory of ["lsp-telemetry", "lsp-telemetry-repeat"]) {
        const file = `built/csharp/phase7-validation/${directory}/results.json`, bytes = await readFile(file);
        captures.push({ file, sha256: sha256(bytes), cases: JSON.parse(bytes).filter(value => /^telemetry-project-(tsconfig|jsconfig|inferred)$/.test(value.name))
            .map(value => ({ name: value.name, telemetry: value.reference.telemetry })) });
    }
    await writeFile(target, JSON.stringify({ referenceRevision, policy: "Parse only projectInfo.properties.compilerOptions as JSON. Keep all fields, values, arrays, and measurement counts exact.", captures }, null, 2) + "\n");
}
const evidence = JSON.parse(await readFile(target));
assert.equal(evidence.referenceRevision, referenceRevision);
let variedCases = 0, controls = 0;
for (const first of evidence.captures[0].cases) {
    const second = evidence.captures[1].cases.find(value => value.name === first.name);
    assert.deepEqual(first.telemetry.map(comparableTelemetry), second.telemetry.map(comparableTelemetry));
    if (first.telemetry[0].properties.compilerOptions !== second.telemetry[0].properties.compilerOptions) variedCases++;
    const original = first.telemetry[0], changedOption = structuredClone(original), options = JSON.parse(changedOption.properties.compilerOptions);
    options.allowJs = !options.allowJs; changedOption.properties.compilerOptions = JSON.stringify(options);
    const changedCount = structuredClone(original); changedCount.measurements.tsFileCount++;
    const missingProperty = structuredClone(original); delete missingProperty.properties.projectType;
    const extraProperty = structuredClone(original); extraProperty.properties.extra = "unexpected";
    for (const changed of [changedOption, changedCount, missingProperty, extraProperty]) {
        assert.notDeepEqual(comparableTelemetry(changed), comparableTelemetry(original)); controls++;
    }
    const invalid = structuredClone(original); invalid.properties.compilerOptions = "malformed";
    assert.throws(() => comparableTelemetry(invalid)); controls++;
    invalid.properties.compilerOptions = {};
    assert.throws(() => comparableTelemetry(invalid)); controls++;
}
assert.equal(variedCases, 3);
console.log(JSON.stringify({ cases: evidence.captures[0].cases.length, variedCases, corruptionControls: controls }));
