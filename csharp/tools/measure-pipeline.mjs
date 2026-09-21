import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { performance } from "node:perf_hooks";
import {
    json,
    output,
    run,
    sha256,
} from "./common.mjs";

const verification = JSON.parse(await readFile(path.join(output, "pipeline-summary.json"), "utf8"));
const executable = path.join(output, "native-host/TypeScript.Compatibility" + (process.platform === "win32" ? ".exe" : ""));
const input = path.join(output, "pipeline-input.json");
assert.equal(sha256(await readFile(executable)), verification.candidateSha256);
assert.equal(sha256(await readFile(input)), verification.inputSha256);
assert.equal(verification.optimizationPreference, "Speed");
const runs = [];
for (let index = 0; index < 5; index++) {
    console.log(`Pipeline measurement ${index + 1}/5`);
    const resultPath = path.join(output, `pipeline-measurement-${index + 1}.json`);
    await run(executable, ["--pipeline-benchmark", input, resultPath]);
    const result = JSON.parse(await readFile(resultPath, "utf8"));
    assert.equal(result.deepAndCancellation, true);
    runs.push(result.cases.find(fixture => fixture.name === "large-graph").measurements);
}
const median = values => values.toSorted((a, b) => a - b)[Math.floor(values.length / 2)];
const names = [...new Set(runs[0].map(row => row.name))];
const workloads = names.map(name => {
    const samples = runs.flatMap(run => run.filter(row => row.name === name));
    const medians = runs.map(run => median(run.filter(row => row.name === name).map(row => row.milliseconds)));
    return { name, samples, medianMilliseconds: median(samples.map(row => row.milliseconds)), allocatedBytes: median(samples.map(row => row.allocatedBytes)), processMedians: medians, spreadPercent: (Math.max(...medians) - Math.min(...medians)) / median(medians) * 100 };
});
assert.equal(new Set(workloads.flatMap(workload => workload.samples.map(sample => sample.checksum))).size, 1);
const startup = [];
for (let sample = 0; sample < 100; sample++) {
    const start = performance.now();
    await run(executable, ["--startup"]);
    startup.push(performance.now() - start);
}
const evidence = { ...verification, timestamp: new Date().toISOString(), experiment: "Independent C# parse, bind, check and encode; 2 files with 100 exported type aliases, 200 variables and 100 import declarations", processes: 5, samplesPerProcess: 15, iterationsPerSample: 20, workloads, startupWallMilliseconds: startup, startupMedianMilliseconds: median(startup), environment: "Sequential existing-artifact execution; no concurrent agent builds/tests/profiling. OS background activity is not controlled." };
await json(path.join(output, "pipeline-measurements.json"), evidence);
console.log(JSON.stringify({ workloads: workloads.map(({ samples, ...summary }) => summary), startupMedianMilliseconds: evidence.startupMedianMilliseconds }, null, 2));
