import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { json, output, root, run, sha256 } from "./common.mjs";
import { Metrics, activityReport, fileHashes } from "./performance-common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const directory = path.resolve(option("--directory", path.join(output, "phase8-performance/checker")));
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe");
const candidate = path.resolve(option("--candidate", path.join(output, "phase8-build/bin/TypeScript.Compatibility/release")));
const baseline = path.resolve(option("--baseline", path.join(output, "phase7-tags-build/bin/TypeScript.Compatibility/release")));
await mkdir(directory, { recursive: true });
const reference = JSON.parse(await readFile(path.join(output, "reference.json")));
const source = path.join(output, reference.sourceRelativePath, "tsc"), bridge = path.join(source, "cmd/csharp-checker-workload");
await mkdir(bridge, { recursive: true }); await copyFile(path.join(root, "csharp/oracle/checker-workload/main.go"), path.join(bridge, "main.go"));
await run(go, ["-C", source, "build", "-o", path.join(output, "checker-workload-oracle.exe"), "./cmd/csharp-checker-workload"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
const manifest = { ...JSON.parse(await readFile(path.join(root, "csharp/compatibility/phase4-tiered-workloads.json"))),
    execution: "Paired Go, phase-7 and phase-8 Release CoreCLR; workstation GC, tiering enabled, default dynamic PGO; no NativeAOT execution",
    processorCount: os.availableParallelism(), warmups: 50, samples: 31,
    budgetScope: "Inherited operability budgets; independent of parity with Go or a performance improvement over phase 7" };
await json(path.join(directory, "manifest.json"), manifest);
if (process.argv.includes("--prepare-only")) { console.log(directory); process.exit(0); }
const binaries = await fileHashes({ reference: path.join(output, "checker-workload-oracle.exe"),
    before: path.join(baseline, "TypeScript.Compatibility.dll"), beforeCompiler: path.join(baseline, "TypeScript.Compiler.dll"),
    after: path.join(candidate, "TypeScript.Compatibility.dll"), afterCompiler: path.join(candidate, "TypeScript.Compiler.dll"),
    runner: path.join(root, "csharp/tools/checker-workloads.mjs"), driver: path.join(root, "csharp/tools/performance-checker.mjs") });
const metrics = await Metrics.start();
const attempts = [];
try {
    for (let attempt = 1; attempt <= 3; attempt++) {
        const current = path.join(directory, `attempt-${attempt}`); await mkdir(current, { recursive: true });
        const before = await metrics.call({ kind: "activity" });
        const args = [path.join(root, "csharp/tools/checker-workloads.mjs"), "--manifest", path.join(directory, "manifest.json"), "--dotnet", dotnet,
            "--baseline-directory", baseline, "--candidate-directory", candidate, "--include-go", "--output-directory", current, "--record", path.join(current, "summary.json")];
        const child = spawn(process.execPath, args, { cwd: root, windowsHide: true, env: { ...process.env,
            DOTNET_ROOT: path.dirname(dotnet), DOTNET_gcServer: "0", COMPlus_gcServer: "0" } });
        let log = ""; child.stdout.on("data", data => { log += data; process.stdout.write(data); }); child.stderr.on("data", data => log += data);
        const code = await new Promise((resolve, reject) => { child.once("error", reject); child.once("close", resolve); });
        await writeFile(path.join(current, "run.log"), log);
        const machineActivity = await activityReport(metrics, before, [process.pid, metrics.child.pid, child.pid]);
        const contaminated = machineActivity.before.lastInputTick !== machineActivity.after.lastInputTick;
        const summary = { attempt, code, contaminated, machineActivity, record: path.join(current, "summary.json") };
        attempts.push(summary); await json(path.join(directory, "attempts.json"), attempts);
        if (contaminated) { console.log(`Checker batch ${attempt} discarded because input activity changed`); continue; }
        assert.equal(code, 0, "Checker workload correctness and inherited operability budgets");
        const measurements = JSON.parse(await readFile(summary.record));
        assert.equal(measurements.runtime.nativeAotExecuted, false);
        assert.deepEqual(await fileHashes(Object.fromEntries(Object.entries(binaries).map(([name, item]) => [name, item.file]))), binaries);
        await json(path.join(directory, "summary.json"), { referenceRevision: reference.referenceRevision, binaries, manifest, acceptedAttempt: attempt, attempts,
            measurements, nativeExecuted: false });
        console.log(JSON.stringify({ acceptedAttempt: attempt, groups: measurements.groups.length, nativeExecuted: false }));
        break;
    }
    assert.ok(attempts.some(attempt => !attempt.contaminated && attempt.code === 0), "No uncontaminated checker batch completed; earlier attempts are retained");
} finally { await metrics.close(); }
