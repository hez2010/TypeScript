import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile, writeFile, rm } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, sha256, referenceRevision } from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const directory = path.resolve(option("--directory", path.join(output, "phase7-validation/overlays")));
await mkdir(directory, { recursive: true });
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
assert.equal(reference.referenceRevision, referenceRevision);
const source = path.join(output, reference.sourceRelativePath, "tsc");
const original = await run("git", ["show", `${referenceRevision}:tsc/internal/project/overlayfs_test.go`]);
let instrumented = original.replace("createOverlayFS := func()", "createOverlayFS := func(t *testing.T)")
    .replaceAll("createOverlayFS()", "createOverlayFS(t)")
    .replace("return newOverlayFS(", "fs := newOverlayFS(")
    .replace("\n\t\t)\n\t}", "\n\t\t)\n\t\trecordProcessChanges(t, fs, nil)\n\t\treturn fs\n\t}")
    .replaceAll("fs.processChanges(", "recordProcessChanges(t, fs, ");
assert.notEqual(instrumented, original);
const testFile = path.join(source, "internal/project/overlayfs_test.go");
const recorderFile = path.join(source, "internal/project/csharp_overlay_test.go");
const recording = path.join(directory, "original-operations.jsonl");
await writeFile(recording, "");
try {
await writeFile(testFile, instrumented);
await copyFile(path.join(root, "csharp/oracle/project/overlay_test.go"), recorderFile);
const tests = await run(go, ["-C", source, "test", "-json", "./internal/project", "-run", "^TestProcessChanges$", "-count=1"],
    { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local", CSHARP_OVERLAY_RECORD: recording } });
await writeFile(path.join(directory, "reference-tests.jsonl"), tests + "\n");
} finally {
    await writeFile(testFile, original + "\n");
    await rm(recorderFile, { force: true });
}
const inputs = (await readFile(recording, "utf8")).trim().split(/\r?\n/).map(JSON.parse);
const managed = path.resolve(option("--managed-directory", path.join(output, "phase7-build/bin/TypeScript.Compatibility/release")));
const dll = path.join(managed, "TypeScript.Compatibility.dll");
const child = spawn(dotnet, [dll, "--overlay-lines"], { cwd: root, windowsHide: true });
let stdout = "", stderr = "";
child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data);
child.stdin.on("error", () => {}); child.stdin.end(inputs.map(input => JSON.stringify(input)).join("\n") + "\n");
const code = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
assert.equal(code, 0, stderr);
const results = stdout.trim().split(/\r?\n/).map(JSON.parse); assert.equal(results.length, inputs.length);
await json(path.join(directory, "candidate.json"), results);
const mismatches = [];
for (let index = 0; index < inputs.length; index++) {
    try { assert.deepEqual(results[index], inputs[index].expected); }
    catch (error) { mismatches.push({ index, name: inputs[index].name, difference: error.message }); }
}
const summary = { referenceRevision, sourceSha256: sha256(original), instrumentationSha256: sha256(instrumented),
    recorderSha256: sha256(await readFile(path.join(root, "csharp/oracle/project/overlay_test.go"))), compilerSha256: sha256(await readFile(path.join(managed, "TypeScript.Compiler.dll"))),
    harnessSha256: sha256(await readFile(dll)), inputsSha256: sha256(await readFile(recording)),
    cases: [...new Set(inputs.map(input => input.name))].sort(), operations: inputs.length, mismatches };
await json(path.join(directory, "summary.json"), summary);
console.log(`${summary.cases.length} original overlay cases, ${inputs.length} operations, ${mismatches.length} mismatches`);
if (mismatches.length) { console.error(mismatches.slice(0, 3)); process.exitCode = 1; }
