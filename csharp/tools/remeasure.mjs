// Rerun only the existing host-native artifact. Do not build, restore, execute
// repository suites, or collect profiles concurrently with these measurements.
import {
    copyFile,
    readFile,
} from "node:fs/promises";
import path from "node:path";
import { performance } from "node:perf_hooks";
import {
    json,
    output,
    run,
    sha256,
} from "./common.mjs";

const resultsPath = path.join(output, "experiments-host.json");
const previous = JSON.parse(await readFile(resultsPath, "utf8"));
const candidate = path.join(output, "native-host/TypeScript.Compatibility" + (process.platform === "win32" ? ".exe" : ""));
const input = path.join(output, "input.json");
const expected = path.join(output, "expected.json");
if (previous.instructionSet !== "native") throw new Error("Expected host-native evidence");
for (const [file, hash] of [[candidate, previous.candidateSha256], [input, previous.inputSha256], [expected, previous.expectedSha256]]) {
    if (sha256(await readFile(file)) !== hash) throw new Error(`Artifact changed since verification: ${file}`);
}
await copyFile(resultsPath, path.join(output, "experiments-host.before-rerun.json"));
const runs = [];
for (let index = 0; index < 5; index++) {
    console.log(`Measurement process ${index + 1}/5`);
    const started = new Date().toISOString();
    const text = await run(candidate, ["--benchmark", input, expected]);
    const benchmarks = JSON.parse(text.slice(text.indexOf("{")));
    const verification = text.slice(0, text.indexOf("{")).trim();
    const startupWallMilliseconds = [];
    for (let sample = 0; sample < 20; sample++) {
        const start = performance.now();
        await run(candidate, ["--startup"]);
        startupWallMilliseconds.push(performance.now() - start);
    }
    runs.push({ started, verification, benchmarks, startupWallMilliseconds });
}
const workloads = runs[0].benchmarks.workloads.map((workload, index) => {
    for (const run of runs) {
        if (run.benchmarks.workloads[index].name !== workload.name || run.benchmarks.workloads[index].checksum !== workload.checksum) throw new Error("Measurement workload/checksum changed between processes");
    }
    return {
        name: workload.name,
        checksum: workload.checksum,
        milliseconds: runs.flatMap(run => run.benchmarks.workloads[index].milliseconds),
        allocatedBytes: runs.flatMap(run => run.benchmarks.workloads[index].allocatedBytes),
    };
});
const result = {
    ...previous,
    timestamp: new Date().toISOString(),
    measurement: {
        reason: "User requested a rerun while not using the computer",
        method: "Five sequential processes; 15 samples of 20 operations each per workload per process. No concurrent agent builds, repository tests, or profiling. OS background activity was not controlled.",
        priorTimestamp: previous.timestamp,
        runs,
    },
    benchmarks: { ...runs[0].benchmarks, workloads },
    startupWallMilliseconds: runs.flatMap(run => run.startupWallMilliseconds),
};
await json(resultsPath, result);
const median = values => values.toSorted((a, b) => a - b)[Math.floor(values.length / 2)];
console.log(JSON.stringify(
    {
        timestamp: result.timestamp,
        startupMedianMilliseconds: median(result.startupWallMilliseconds),
        workloads: workloads.map((workload, index) => {
            const processMedians = runs.map(run => median(run.benchmarks.workloads[index].milliseconds));
            return {
                name: workload.name,
                medianMilliseconds: median(workload.milliseconds),
                allocatedBytes: median(workload.allocatedBytes),
                processMedians,
                processMedianSpreadPercent: 100 * (Math.max(...processMedians) - Math.min(...processMedians)) / median(processMedians),
            };
        }),
    },
    null,
    2,
));
