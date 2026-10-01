import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, sha256, referenceRevision } from "./common.mjs";
import { normalize, profilingControl, withoutProfiling } from "./scripted-build-contracts.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const dotnet = option("--dotnet", "dotnet");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const exportDirectory = path.join(output, "scripted-builds", option("--exports", "first"));
const directory = path.join(output, "scripted-builds", option("--tag", "current"));
await mkdir(directory, { recursive: true });
const manifest = JSON.parse(await readFile(path.join(exportDirectory, "manifest.json"), "utf8"));
const suite = option("--suite", ".*"), filter = option("--filter", ".*");
const selected = manifest.filter(item => new RegExp(suite).test(item.suite) && new RegExp(filter).test(item.name)).slice(0, Number(option("--limit", "Infinity")));
const cases = [];
for (const item of selected) {
    const bytes = await readFile(path.join(exportDirectory, "cases", item.file));
    assert.equal(sha256(bytes), item.sha256, `export hash: ${item.name}`);
    cases.push(JSON.parse(bytes));
}
assert.ok(cases.length > 0, "at least one scripted case selected");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc");
const bridge = path.join(source, "cmd/csharp-scripted");
await mkdir(bridge, { recursive: true });
await copyFile(path.join(root, "csharp/oracle/scripted/main.go"), path.join(bridge, "main.go"));
const oracle = path.join(output, "scripted-builds/oracle.exe");
const managedDirectory = option("--managed-directory", path.join(output, "phase6-build/bin/TypeScript.Compatibility/release"));
const dll = path.join(managedDirectory, "TypeScript.Compatibility.dll");
const fixture = path.join(output, "content-mapper-fixture.exe");
if (!process.argv.includes("--no-build")) {
    await run(go, ["-C", source, "build", "-o", oracle, "./cmd/csharp-scripted"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
    await run(dotnet, ["build", "csharp/tests/TypeScript.Compatibility/TypeScript.Compatibility.csproj", "-c", "Release", "--no-restore", "--artifacts-path", "built/csharp/phase6-build"]);
}
const binaries = { reference: oracle, candidate: dll, compiler: path.join(managedDirectory, "TypeScript.Compiler.dll"), mapperFixture: fixture };
const hashes = Object.fromEntries(await Promise.all(Object.entries(binaries).map(async ([name, file]) => [name, sha256(await readFile(file))])));
await json(path.join(directory, "inputs.json"), cases);
async function execute(command, args, requests = cases) {
    const child = spawn(command, args, { cwd: root, windowsHide: true, env: { ...process.env, CSHARP_CONTENT_MAPPER_FIXTURE: fixture } });
    let stdout = "", stderr = "";
    child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data);
    const finished = new Promise((resolve, reject) => { child.once("error", reject); child.once("close", code => code === 0 ? resolve() : reject(Error(`${command}: ${code}\n${stderr}\n${stdout.slice(-3000)}`))); });
    child.stdin.on("error", () => {});
    for (const input of requests) if (!child.stdin.write(JSON.stringify(input) + "\n")) await new Promise(resolve => child.stdin.once("drain", resolve));
    child.stdin.end(); await finished;
    const records = stdout.trimEnd().split(/\r?\n/).map(line => JSON.parse(line));
    assert.equal(records.length, requests.length, `record count: ${command}`);
    return records;
}
const [goRecords, csRecords] = await Promise.all([execute(oracle, []), execute(dotnet, [dll, "--scripted-build-lines"])]);
await json(path.join(directory, "reference.json"), goRecords); await json(path.join(directory, "candidate.json"), csRecords);
const failures = [];
for (let index = 0; index < cases.length; index++) {
    const expected = normalize(goRecords[index], cases[index]), actual = normalize(csRecords[index], cases[index]);
    try { assert.deepEqual(actual, expected); }
    catch { failures.push({ name: cases[index].name, suite: cases[index].suite, expected, actual }); }
}
const phase6 = process.argv.includes("--phase6");
const deferredProfiles = [];
if (phase6) {
    const controls = cases.map(profilingControl).filter(Boolean);
    if (controls.length) {
        const inputs = controls.map(control => control.input);
        const [expected, actual] = await Promise.all([execute(oracle, [], inputs), execute(dotnet, [dll, "--scripted-build-lines"], inputs)]);
        await json(path.join(directory, "profiling-controls.json"), { controls, reference: expected, candidate: actual });
        for (let index = 0; index < controls.length; index++) {
            const control = controls[index], original = cases.findIndex(input => input.name === control.original);
            assert.deepEqual(normalize(actual[index], inputs[index]), normalize(expected[index], inputs[index]), `profiling-disabled control: ${control.original}`);
            assert.deepEqual(withoutProfiling(goRecords[original], cases[original], control.flag), normalize(expected[index], inputs[index]), `reference non-profiling effects: ${control.original}`);
            assert.deepEqual(withoutProfiling(csRecords[original], cases[original], control.flag), normalize(actual[index], inputs[index]), `candidate non-profiling effects: ${control.original}`);
            deferredProfiles.push({ name: control.original, flag: control.flag, phase: 8, inputHash: sha256(JSON.stringify(cases[original])),
                controlHash: sha256(JSON.stringify(inputs[index])), controlMatches: true, originalNonProfilingEffectsMatch: true });
        }
    }
}
const phase6Failures = failures.filter(failure => !deferredProfiles.some(control => control.name === failure.name));
for (const [name, file] of Object.entries(binaries)) assert.equal(sha256(await readFile(file)), hashes[name], `${name} changed during replay`);
const summary = { referenceRevision, cases: cases.length, cycles: cases.reduce((sum, item) => sum + item.steps.length, 0),
    strictMatches: cases.length - failures.length, failures: failures.length, hashes, inputHash: sha256(JSON.stringify(cases)),
    suites: Object.fromEntries([...new Set(cases.map(item => item.suite))].map(suite => [suite, cases.filter(item => item.suite === suite).length])),
    normalizedFields: ["stdout: status-report clock", "TS2783 internal iterator allocation id in diagnostic headers and stored message argument 0; source text and emitted bytes preserved",
        "input mtimes: original relative order in a deterministic clock", "input buildInfo.version: FakeTSVersion to pinned compiler version"],
    ...(phase6 ? { phase6TransitionMatches: cases.length - phase6Failures.length, phase6Failures: phase6Failures.length, deferredProfiles } : {}),
    command: `node csharp/tools/scripted-builds.mjs --dotnet <dotnet.exe>${phase6 ? " --phase6" : ""} --record` };
await json(path.join(directory, "failures.json"), failures); await json(path.join(directory, "summary.json"), summary);
console.log(JSON.stringify(summary, null, 2));
for (const failure of failures.slice(0, 40)) console.log(failure.name);
if (process.argv.includes("--record")) await json(path.join(root, "csharp/compatibility/evidence/phase6-scripted-builds.json"), summary);
assert.equal(phase6 ? phase6Failures.length : failures.length, 0);
