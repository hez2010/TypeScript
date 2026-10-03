import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, readFile, writeFile, rm } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, sha256, referenceRevision } from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const directory = path.resolve(option("--directory", path.join(output, "phase7-validation/typings"))); await mkdir(directory, { recursive: true });
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8")); assert.equal(reference.referenceRevision, referenceRevision);
const source = path.join(output, reference.sourceRelativePath, "tsc"), sources = [];
const originals = new Map();
const discovery = path.join(directory, "discovery.jsonl"), batching = path.join(directory, "batching.jsonl");
try {
for (const [name, call, replacement] of [["discovertypings_test.go", "ata.DiscoverTypings(", "recordDiscovery(t, "],
    ["validatepackagename_test.go", "ata.ValidatePackageName(", "recordValidation(t, "],
    ["installnpmpackages_test.go", "installNpmPackages(", "recordBatches(t, "]]) {
    const file = `internal/project/ata/${name}`;
    const original = await run("git", ["show", `${referenceRevision}:tsc/${file}`]);
    const instrumented = original.replaceAll(call, replacement); assert.notEqual(original, instrumented);
    originals.set(file, original + "\n");
    await writeFile(path.join(source, file), instrumented + "\n");
    sources.push({ file, originalSha256: sha256(original), instrumentedSha256: sha256(instrumented) });
}
for (const name of ["typings_record_test.go", "typings_batches_test.go"]) {
    const bytes = await readFile(path.join(root, "csharp/oracle/project", name));
    const file = `internal/project/ata/csharp_${name}`; await writeFile(path.join(source, file), bytes);
    sources.push({ file, instrumentedSha256: sha256(bytes) });
}
await writeFile(discovery, ""); await writeFile(batching, "");
const tests = await run(go, ["-C", source, "test", "-json", "./internal/project/ata", "-run", "^(TestDiscoverTypings|TestValidatePackageName|TestInstallNpmPackages)$", "-count=1"],
    { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local", CSHARP_TYPINGS_RECORD: discovery, CSHARP_TYPINGS_BATCH_RECORD: batching } });
await writeFile(path.join(directory, "reference-tests.jsonl"), tests + "\n");
} finally {
    for (const entry of sources)
        if (originals.has(entry.file)) await writeFile(path.join(source, entry.file), originals.get(entry.file));
        else await rm(path.join(source, entry.file), { force: true });
}
const cases = (await readFile(discovery, "utf8") + await readFile(batching, "utf8")).trim().split(/\r?\n/).map(JSON.parse)
    .sort((a, b) => a.name.localeCompare(b.name) || (a.text ?? "").localeCompare(b.text ?? ""));
const managed = path.resolve(option("--managed-directory", path.join(output, "phase7-build/bin/TypeScript.Compatibility/release"))), dll = path.join(managed, "TypeScript.Compatibility.dll");
const child = spawn(dotnet, [dll, "--typings-lines"], { cwd: root, windowsHide: true });
let stdout = "", stderr = "";
child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data);
child.stdin.on("error", () => {}); child.stdin.end(cases.map(({ expected, ...input }) => JSON.stringify(input)).join("\n") + "\n");
const code = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); }); assert.equal(code, 0, stderr);
const results = stdout.trim().split(/\r?\n/).map(JSON.parse); assert.equal(results.length, cases.length);
await json(path.join(directory, "candidate.json"), results); await json(path.join(directory, "cases.json"), cases);
const mismatches = [];
for (let index = 0; index < cases.length; index++) {
    try { assert.deepEqual(results[index], cases[index].expected); }
    catch (error) { mismatches.push({ index, name: cases[index].name, difference: error.message }); }
}
await json(path.join(directory, "summary.json"), { referenceRevision, sources,
    compilerSha256: sha256(await readFile(path.join(managed, "TypeScript.Compiler.dll"))), harnessSha256: sha256(await readFile(dll)),
    cases: [...new Set(cases.map(input => input.name))], operations: cases.length, mismatches });
console.log(`${cases.length} original typing operations, ${mismatches.length} differences`);
if (mismatches.length) { console.error(mismatches.slice(0, 3)); process.exitCode = 1; }
