import assert from "node:assert/strict";
import { copyFile, mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, sha256, referenceRevision } from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc");
const directory = path.join(output, "scripted-builds", option("--tag", "current"));
const casesDirectory = path.join(directory, "cases");
await mkdir(casesDirectory, { recursive: true });
const testDirectory = path.join(source, "internal/execute/tsctests");
const runnerFile = path.join(testDirectory, "runner.go");
const originalFile = path.join(output, "scripted-builds/runner.original.go");
let runner = await readFile(runnerFile, "utf8");
if (runner.includes("newCSharpExport")) runner = await readFile(originalFile, "utf8");
else await writeFile(originalFile, runner);
const originalHash = sha256(runner);
function insert(before, after) {
    assert.equal(runner.split(before).length, 2, `unique instrumentation anchor: ${before}`);
    runner = runner.replace(before, after);
}
insert("sys := newTestSys(test, false)", "sys := newTestSys(test, false)\n\t\texport := newCSharpExport(t, test, scenario, sys)\n\t\tdefer export.finish()");
insert("if do.edit != nil {\n\t\t\t\t\tdo.edit(sys)\n\t\t\t\t}", "export.beforeEdit()\n\t\t\t\tif do.edit != nil {\n\t\t\t\t\tdo.edit(sys)\n\t\t\t\t}\n\t\t\t\texport.afterEdit(do, commandLineArgs)");
await writeFile(runnerFile, runner);
const exporterSource = path.join(root, "csharp/oracle/build/scripted_export.go");
await copyFile(exporterSource, path.join(testDirectory, "csharp_scripted_export.go"));
let failure;
const filter = option("--filter", "Test");
try {
    const log = await run(go, ["-C", source, "test", "./internal/execute/tsctests", "-run", filter, "-count=1", "-parallel=4", "-timeout=15m", "-json"],
        { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local", CSHARP_SCRIPTED_EXPORT: casesDirectory } });
    await writeFile(path.join(directory, "go-tests.jsonl"), log);
} catch (error) {
    failure = String(error);
    await writeFile(path.join(directory, "go-test-error.txt"), failure);
} finally {
    // Leave only the test exporter source in the archive. Product and ordinary oracle builds
    // use the original runner, and exported cases remain available after a failed Go assertion.
    await writeFile(runnerFile, await readFile(originalFile));
}
const manifest = [];
for (const name of (await readdir(casesDirectory)).filter(name => name.endsWith(".json")).sort()) {
    const bytes = await readFile(path.join(casesDirectory, name));
    const entry = JSON.parse(bytes);
    manifest.push({ file: name, name: entry.name, suite: entry.suite, cycles: entry.steps.length, sha256: sha256(bytes) });
}
const summary = { referenceRevision, filter, originalRunnerSha256: originalHash, instrumentedRunnerSha256: sha256(runner),
    exporterSha256: sha256(await readFile(exporterSource)), cases: manifest.length, cycles: manifest.reduce((total, item) => total + item.cycles, 0),
    suites: Object.fromEntries([...new Set(manifest.map(item => item.suite))].sort().map(suite => [suite, manifest.filter(item => item.suite === suite).length])),
    goTestsPassed: !failure, casesManifestSha256: sha256(JSON.stringify(manifest)), command: "node csharp/tools/export-scripted-builds.mjs" };
await json(path.join(directory, "manifest.json"), manifest); await json(path.join(directory, "summary.json"), summary);
console.log(JSON.stringify(summary, null, 2));
if (failure) throw Error(`The pinned Go tests failed. See ${path.join(directory, "go-test-error.txt")}`);
