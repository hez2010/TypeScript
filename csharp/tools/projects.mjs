import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, sha256, referenceRevision } from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const directory = path.join(output, "phase7-validation/projects");
await mkdir(directory, { recursive: true });
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
assert.equal(reference.referenceRevision, referenceRevision);
const source = path.join(output, reference.sourceRelativePath, "tsc");
const bridge = path.join(source, "cmd/csharp-project");
await mkdir(bridge, { recursive: true });
await copyFile(path.join(root, "csharp/oracle/project/main.go"), path.join(bridge, "main.go"));
const oracle = path.join(directory, "oracle.exe");
await run(go, ["-C", source, "build", "-o", oracle, "./cmd/csharp-project"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
const managed = path.join(output, "phase7-build/bin/TypeScript.Compatibility/release");
const dll = path.join(managed, "TypeScript.Compatibility.dll");
const config = (files, extra = {}) => JSON.stringify({ compilerOptions: { noLib: true }, files, ...extra });
const baseFiles = { "/p/tsconfig.json": config(["a.ts"]), "/p/a.ts": "export const a = 1;", "/q/b.ts": "export const b = 1;" };
const program = (rootFiles = ["/q/b.ts"], compilerOptions = { noLib: true }) => ({ rootFiles, compilerOptions });
let cases = [];
function add(name, files, steps, extra = {}) {
    for (const caseSensitive of [false, true]) cases.push({ name: `${name}-${caseSensitive}`, cwd: "/", caseSensitive, files, steps,
        queries: Object.keys(files).filter(name => name.endsWith(".ts")), ...extra });
}
add("synthetic-independent-branches", baseFiles, [
    { createPrograms: [program(), program(["/p/a.ts"])] },
    { base: 1, reconfigurePrograms: [{ id: "/dev/null/synthetic/1", ...program(["/p/a.ts"]) }] },
    { base: 1, removePrograms: ["/dev/null/synthetic/1"] },
    { createPrograms: [program()] },
    { base: 1, openFiles: ["/q/b.ts"] },
    { closeFiles: ["/q/b.ts"] },
]);
add("dirty-ensure-selection", baseFiles, [
    { openProjects: ["/p/tsconfig.json"], createPrograms: [program()] },
    { edits: { "/p/a.ts": "export const a = 2;", "/q/b.ts": "export const b = 2;" }, changed: ["/p/a.ts", "/q/b.ts"] },
    { ensurePrograms: ["/p/tsconfig.json"] }, { ensureAllPrograms: true },
    { changed: ["/p/a.ts", "/q/b.ts"], ensureAllPrograms: true },
    { base: 1, ensureAllPrograms: true },
]);
add("counted-project-opens", baseFiles, [
    { openProjects: ["/p/tsconfig.json", "/p/tsconfig.json"] }, { openProjects: ["/p/tsconfig.json"] },
    { closeProjects: ["/p/tsconfig.json"] }, { closeProjects: ["/p/tsconfig.json"] }, { closeProjects: ["/p/tsconfig.json"] },
]);
add("counted-file-opens", baseFiles, [
    { openFiles: ["/p/a.ts", "/q/b.ts"] }, { openFiles: ["/p/a.ts", "/q/b.ts"] },
    { closeFiles: ["/p/a.ts", "/q/b.ts"] }, { closeFiles: ["/p/a.ts", "/q/b.ts"] },
]);
add("file-dirty-deferred", baseFiles, [
    { openFiles: ["/p/a.ts", "/q/b.ts"] },
    { edits: { "/p/a.ts": "export const a = 3;", "/q/b.ts": "export const b = 3;" }, changed: ["/p/a.ts", "/q/b.ts"] },
    {}, { ensureFiles: ["/p/a.ts"] }, { ensureFiles: ["/q/b.ts"] },
    { closeFiles: ["/p/a.ts"] }, { closeFiles: ["/q/b.ts"] },
]);
add("open-and-close-same-request", baseFiles, [
    { openProjects: ["/p/tsconfig.json"], openFiles: ["/q/b.ts"] },
    { closeProjects: ["/p/tsconfig.json"], openProjects: ["/p/tsconfig.json"], closeFiles: ["/q/b.ts"], openFiles: ["/q/b.ts"] },
    { closeProjects: ["/p/tsconfig.json"], closeFiles: ["/q/b.ts"] },
]);
add("config-change-and-discovery", baseFiles, [
    { openFiles: ["/p/a.ts", "/q/b.ts"] },
    { edits: { "/q/tsconfig.json": config(["b.ts"]) }, created: ["/q/tsconfig.json"], ensureFiles: ["/q/b.ts"] },
    { edits: { "/p/tsconfig.json": null }, deleted: ["/p/tsconfig.json"], ensureFiles: ["/p/a.ts"] },
    { edits: { "/p/tsconfig.json": config(["a.ts"]) }, created: ["/p/tsconfig.json"], ensureFiles: ["/p/a.ts"] },
]);
add("files-and-import-discovery", { ...baseFiles, "/p/a.ts": "import {x} from './new'; export {x};" }, [
    { openFiles: ["/p/a.ts"] }, { edits: { "/p/new.ts": "export const x = 1;" }, created: ["/p/new.ts"], ensureFiles: ["/p/a.ts"] },
    { edits: { "/p/new.ts": null }, deleted: ["/p/new.ts"], ensureFiles: ["/p/a.ts"] },
]);
add("default-project-solutions", {
    "/s/tsconfig.json": config([], { references: [{ path: "./a" }, { path: "./b" }] }),
    "/s/a/tsconfig.json": config(["a.ts"], { compilerOptions: { noLib: true, composite: true } }),
    "/s/b/tsconfig.json": config(["b.ts"], { compilerOptions: { noLib: true, composite: true }, references: [{ path: "../a" }] }),
    "/s/a/a.ts": "export const a = 1;", "/s/b/b.ts": "import {a} from '../a/a'; export {a};",
}, [{ openFiles: ["/s/b/b.ts"] }, { openFiles: ["/s/a/a.ts"] }, { ensureAllPrograms: true }, { closeFiles: ["/s/a/a.ts"] }]);
add("filesystem-replacement", baseFiles, [
    { openProjects: ["/p/tsconfig.json"], createPrograms: [program()] },
    { fileSystem: { ...baseFiles, "/p/a.ts": "export const a = 9;", "/q/b.ts": "export const b = 9;" }, replaceFileSystem: true },
    { ensureAllPrograms: true }, { base: 1, ensureAllPrograms: true },
]);
add("invalid-atomic-requests", baseFiles, [
    { createPrograms: [program()] }, { removePrograms: ["/dev/null/synthetic/2"] },
    { base: 1, reconfigurePrograms: [{ id: "/dev/null/synthetic/1", ...program() }], removePrograms: ["/dev/null/synthetic/1"] },
    { base: 1, reconfigurePrograms: [{ id: "/dev/null/synthetic/1", ...program() }, { id: "/dev/null/synthetic/1", ...program() }] },
    { base: 1, ensureAllPrograms: true },
]);
const filter = option("--filter", ""); if (filter) cases = cases.filter(input => new RegExp(filter).test(input.name));
await json(path.join(directory, "inputs.json"), cases);
async function execute(command, args) {
    const child = spawn(command, args, { cwd: root, windowsHide: true });
    let stdout = "", stderr = "";
    child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data);
    child.stdin.on("error", () => {}); child.stdin.end(cases.map(input => JSON.stringify(input)).join("\n") + "\n");
    const code = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
    assert.equal(code, 0, stderr);
    const results = stdout.trim().split(/\r?\n/).map(JSON.parse); assert.equal(results.length, cases.length); return results;
}
const before = await execute(oracle, []), after = await execute(dotnet, [dll, "--project-lines"]);
await json(path.join(directory, "reference.json"), before); await json(path.join(directory, "candidate.json"), after);
const mismatches = [];
for (let index = 0; index < cases.length; index++) for (let step = 0; step < cases[index].steps.length; step++) {
    try { assert.deepEqual(after[index][step], before[index][step]); }
    catch (error) { mismatches.push({ index, step, name: cases[index].name, difference: error.message }); }
}
await json(path.join(directory, "summary.json"), { referenceRevision,
    compilerSha256: sha256(await readFile(path.join(managed, "TypeScript.Compiler.dll"))), harnessSha256: sha256(await readFile(dll)),
    oracleSha256: sha256(await readFile(oracle)), oracleSourceSha256: sha256(await readFile(path.join(bridge, "main.go"))),
    cases: cases.length, steps: cases.reduce((sum, test) => sum + test.steps.length, 0), mismatches });
console.log(`${cases.length} project cases, ${mismatches.length} mismatching transitions`);
if (mismatches.length) { console.error(mismatches.slice(0, 4)); process.exitCode = 1; }
