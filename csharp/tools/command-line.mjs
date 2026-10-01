import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, readFile, stat, writeFile } from "node:fs/promises";
import path from "node:path";
import { output, root, json, sha256, referenceRevision } from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const dotnet = option("--dotnet", "dotnet");
const managedDirectory = path.resolve(option("--managed-directory", path.join(output, "phase6-build/bin/TypeScript.CommandLine/release")));
const dll = path.join(managedDirectory, "tsgo-cs.dll");
const binaries = { command: dll, compiler: path.join(managedDirectory, "TypeScript.Compiler.dll") };
const hashes = Object.fromEntries(await Promise.all(Object.entries(binaries).map(async ([name, file]) => [name, sha256(await readFile(file))])));
const directory = path.join(output, "command-line");
await mkdir(directory, { recursive: true });
const project = await mkdtemp(path.join(directory, "project-"));
const records = [];
async function execute(args, expectedStatus) {
    const child = spawn(dotnet, [dll, ...args], { cwd: project, windowsHide: true });
    let stdout = "", stderr = "";
    child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data);
    child.stdin.end();
    const status = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
    records.push({ args, status, stdout, stderr });
    assert.equal(status, expectedStatus, stdout + stderr); assert.equal(stderr, "");
    return stdout;
}
await execute(["--version"], 0);
assert.match(await execute(["--help"], 0), /TypeScript/);
await writeFile(path.join(project, "tsconfig.json"), '{"compilerOptions":{"lib":["es5"],"incremental":true,"declaration":true,"outDir":"dist","strict":true},"files":["index.ts"]}');
await writeFile(path.join(project, "index.ts"), "export const value: number = 42;");
await execute(["-p", ".", "--pretty", "false"], 0);
assert.match(await readFile(path.join(project, "dist/index.js"), "utf8"), /value = 42/);
assert.match(await readFile(path.join(project, "dist/index.d.ts"), "utf8"), /value: number/);
const state = path.join(project, "dist/tsconfig.tsbuildinfo"), before = await stat(state);
await execute(["-b", "--pretty", "false"], 0);
assert.equal((await stat(state)).mtimeMs, before.mtimeMs, "No-op build must preserve build-info mtime");
await writeFile(path.join(project, "index.ts"), "export const value: number = 'wrong';");
assert.match(await execute(["-p", ".", "--pretty", "false", "--noEmitOnError"], 1), /TS2322/);
assert.match(await readFile(path.join(project, "dist/index.js"), "utf8"), /value = 42/);
await writeFile(path.join(project, "index.ts"), "export const value: number = 7;");
await execute(["-b", "--force", "--pretty", "false"], 0);
assert.match(await readFile(path.join(project, "dist/index.js"), "utf8"), /value = 7/);
await execute(["-b", "--clean", "--pretty", "false"], 0);
for (const file of ["index.js", "index.d.ts", "tsconfig.tsbuildinfo"])
    await assert.rejects(stat(path.join(project, "dist", file)), { code: "ENOENT" });
assert.equal(await readFile(path.join(project, "index.ts"), "utf8"), "export const value: number = 7;");
for (const [name, file] of Object.entries(binaries)) assert.equal(sha256(await readFile(file)), hashes[name], "Binary changed during validation");
const summary = { referenceRevision, runtime: "Release CoreCLR", checks: records.length, failures: 0, hashes,
    records, command: "node csharp/tools/command-line.mjs --dotnet <dotnet.exe> --record" };
await json(path.join(directory, "summary.json"), summary);
if (process.argv.includes("--record")) await json(path.join(root, "csharp/compatibility/evidence/phase6-command-line.json"), summary);
console.log(JSON.stringify(summary, null, 2));
