import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, sha256, referenceRevision } from "./common.mjs";

const option = (name, fallback) => process.argv.includes(name) ? process.argv[process.argv.indexOf(name) + 1] : fallback;
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe");
const directory = path.join(output, "phase7-validation/formatting");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
assert.equal(reference.referenceRevision, referenceRevision);
const source = path.join(output, reference.sourceRelativePath, "tsc");
const inputs = path.join(directory, "original-inputs.jsonl");
let cases = (await readFile(inputs, "utf8")).trim().split(/\r?\n/).filter(Boolean).map(JSON.parse);
const filter = option("--filter", ""); if (filter) cases = cases.filter(input => new RegExp(filter).test(input.name));
if (!process.argv.includes("--no-build")) await run(dotnet, ["build", "csharp/tests/TypeScript.Compatibility", "-c", "Release", "--no-restore", "--artifacts-path", "built/csharp/phase7-build"]);
const assembly = path.join(output, "phase7-build/bin/TypeScript.Compatibility/release/TypeScript.Compatibility.dll");
const child = spawn(dotnet, [assembly, "--formatting-lines"], { windowsHide: true }); let stdout = "", stderr = "";
child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data);
child.stdin.on("error", () => {}); child.stdin.end(cases.map(input => JSON.stringify(input)).join("\n") + "\n");
const exit = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
await writeFile(path.join(directory, "candidate.stdout"), stdout); await writeFile(path.join(directory, "candidate.stderr"), stderr);
const actual = stdout.trim().split(/\r?\n/).filter(Boolean).map(JSON.parse);
assert.equal(exit, 0, `${cases[actual.length]?.name}: ${stderr}`); assert.equal(actual.length, cases.length);
const differences = [];
for (let i = 0; i < cases.length; i++) {
    try { assert.deepEqual(actual[i], cases[i].result); }
    catch { differences.push({ input: cases[i], actual: actual[i] }); }
}
await json(path.join(directory, "differences.json"), differences);
const names = [...new Set(cases.map(c => c.name))];
const events = (await readFile(path.join(directory, "original-tests.jsonl"), "utf8")).trim().split(/\r?\n/).filter(Boolean).map(JSON.parse);
assert.equal(events.filter(event => event.Action === "fail").length, 0, "Original formatting tests must pass");
assert.ok(names.every(name => events.some(event => event.Action === "pass" && event.Test === name)));
const sourceFiles = new Map();
for (const file of (await run("rg", ["--files", path.join(source, "internal/fourslash/tests")])).split(/\r?\n/).filter(file => file.endsWith("_test.go"))) {
    const text = await readFile(file, "utf8");
    for (const match of text.matchAll(/^func (Test\w+)\(/gm)) sourceFiles.set(match[1], { source: "tsc/" + path.relative(source, file).replaceAll("\\", "/"), sourceSha256: sha256(text) });
}
const manifest = names.map(name => ({ name, ...sourceFiles.get(name), requests: cases.filter(input => input.name === name).length,
    mappedRequests: cases.filter(input => input.name === name && input.projections).length, methods: [...new Set(cases.filter(input => input.name === name).map(input => input.method))] }));
assert.ok(manifest.every(test => test.name.startsWith("TestCSharp") || test.source));
await json(path.join(directory, "manifest.json"), manifest);
const summary = { referenceRevision, originalTests: names.filter(name => !name.startsWith("TestCSharp")).length,
    authoredTests: names.filter(name => name.startsWith("TestCSharp")).length, requests: cases.length, differences: differences.length,
    inputSha256: sha256(await readFile(inputs)), compilerSha256: sha256(await readFile(path.join(path.dirname(assembly), "TypeScript.Compiler.dll"))) };
await json(path.join(directory, "summary.json"), summary); console.log(JSON.stringify(summary));
if (differences.length) process.exitCode = 1;
