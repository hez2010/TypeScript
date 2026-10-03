import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, referenceRevision, sha256 } from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const directory = path.resolve(option("--directory", path.join(output, "phase7-validation/lsp-watch")));
await mkdir(directory, { recursive: true });
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
assert.equal(reference.referenceRevision, referenceRevision);
const source = path.join(output, reference.sourceRelativePath, "tsc");
const oracle = path.join(directory, "oracle.exe");
const control = path.join(directory, "alias-control.exe");
const sources = {};
for (const file of ["internal/project/watch.go", "internal/project/session.go", "internal/lsp/lspwatcher/lspwatcher.go", "internal/tspath/path.go"]) {
    const expected = await run("git", ["show", `${referenceRevision}:tsc/${file}`]);
    const actual = (await readFile(path.join(source, file), "utf8")).replaceAll("\r", "").trimEnd();
    assert.equal(actual, expected); sources[file] = sha256(actual);
}
if (!process.argv.includes("--no-prepare")) {
    const helper = path.join(source, "internal/project/csharp_watch_probe.go");
    const nativeHelper = path.join(source, "internal/lsp/lspwatcher/csharp_watch_probe.go");
    const target = path.join(source, "cmd/csharp-lsp-watch");
    const parentSource = path.join(source, "internal/tspath/path.go"), originalParents = await readFile(parentSource);
    try {
        await copyFile(path.join(root, "csharp/oracle/lsp-watch/probe.go"), helper);
        await writeFile(nativeHelper, 'package lspwatcher\nfunc CSharpRootFromGlob(pattern string) string { return rootFromGlob(pattern) }\n');
        await mkdir(target, { recursive: true });
        await copyFile(path.join(root, "csharp/oracle/lsp-watch/main.go"), path.join(target, "main.go"));
        await run("D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe", ["-C", source, "build", "-o", oracle, "./cmd/csharp-lsp-watch"],
            { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
        const original = originalParents.toString(), revised = original.replace("append(group.head, sr...)", "append(slices.Clone(group.head), sr...)");
        assert.notEqual(revised, original); assert.equal(original.split("append(group.head, sr...)").length, 2);
        await writeFile(parentSource, revised);
        await run("D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe", ["-C", source, "build", "-o", control, "./cmd/csharp-lsp-watch"],
            { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
    } finally {
        await writeFile(parentSource, originalParents); await rm(helper, { force: true }); await rm(nativeHelper, { force: true });
        await rm(path.join(target, "main.go"), { force: true });
    }
}
const dll = path.resolve(option("--managed-directory", path.join(output, "phase7-tags-build/bin/TypeScript.Compatibility/release")), "TypeScript.Compatibility.dll");
const cases = [];
for (const pattern of ["shared", "partial"]) cases.push({ mode: "registry", pattern });
const roots = ["/", "/project", "/home", "/home/user", "/home/user/project", "/Home/user", "C:/", "C:/repo", "C:/Users", "C:/USERS/User", "C:/Users/User/project", "//server/share", "//server/share/project"];
for (const pattern of ["", "*", "/**/*", ...roots.flatMap(root => [root, `${root}/`, `${root}/**/*`, `${root}\\*.ts`, `${root}/?.ts`, `${root}/{a,b}/*.ts`, `${root}/a[0].ts`])]) cases.push({ mode: "root", pattern });
for (const pattern of roots) cases.push({ mode: "components", pattern });
const files = roots.flatMap(root => [root, `${root}/a`, `${root}/a/b`, `${root}/a/c`, `${root}/x/y`]);
for (const caseSensitive of [false, true]) {
    for (const file of files) cases.push({ mode: "parents", files: [file], caseSensitive });
    for (let index = 0; index < files.length; index++)
        for (let next = index + 1; next < files.length; next += 3) cases.push({ mode: "parents", files: [files[index], files[next]], caseSensitive });
}
cases.push({ mode: "parents", files: ["/a/b/c", "/x/y", "C:/repo/src"], caseSensitive: true });
const probeFiles = ["/workspace/a.ts", "/workspace/src/a.ts", "/Workspace/other/b.ts", "/workspace2/a.ts", "/project/a.ts", "/lib/lib.d.ts",
    "/node_modules/pkg/a.d.ts", "/external/node_modules/other/a.d.ts", "/external/a/src/a.ts", "/external/a/test/b.ts", "/external/b/main.ts", "/typings/node_modules/@types/a/index.d.ts",
    "/home/user/config/a.json", "/home/other/src/b.ts", "C:/Users/me/src/a.ts", "C:/Users/other/src/b.ts", "//server/share/config/file.json", "^/untitled/1"];
let state = 123456;
const random = n => { state = (Math.imul(state, 1664525) + 1013904223) >>> 0; return state % n; };
for (const mode of ["resolution", "typings", "exact"]) for (const caseSensitive of [false, true]) for (const relative of [false, true]) {
    const defaults = { mode, caseSensitive, relative, workspace: "/workspace", currentDirectory: "/project", library: "/lib", typings: "/typings" };
    cases.push({ ...defaults, files: [] });
    for (const file of probeFiles) cases.push({ ...defaults, files: [file] });
    for (let i = 0; i < 120; i++) cases.push({ ...defaults, files: Array.from({ length: 1 + random(probeFiles.length) }, () => probeFiles[random(probeFiles.length)]) });
}
await writeFile(path.join(directory, "inputs.jsonl"), cases.map(input => JSON.stringify(input) + "\n").join(""));
async function execute(command, args, label) {
    const child = spawn(command, args, { cwd: root, windowsHide: true }); let stdout = "", stderr = "";
    child.stdin.on("error", () => {});
    child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data);
    const done = new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
    child.stdin.end(await readFile(path.join(directory, "inputs.jsonl")));
    const code = await done; await writeFile(path.join(directory, `${label}.jsonl`), stdout); await writeFile(path.join(directory, `${label}.stderr`), stderr);
    assert.equal(code, 0, stderr); return stdout.trim().split(/\r?\n/).map(line => JSON.parse(line));
}
const runs = await Promise.allSettled([execute(oracle, [], "reference"), execute(control, [], "alias-control"),
    execute("D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe", [dll, "--lsp-watch-lines"], "candidate")]);
for (const result of runs) if (result.status === "rejected") throw result.reason;
const [expected, corrected, actual] = runs.map(result => result.value);
for (const results of [expected, corrected, actual]) assert.equal(results.length, cases.length);
const differences = [], classified = [];
for (let i = 0; i < cases.length; i++) {
    if (cases[i].mode === "registry") {
        const shared = cases[i].pattern === "shared";
        assert.deepEqual(expected[i], shared ? { attempts: 3, unregistered: 1, active: 1, retryErrors: 0 } : { attempts: 4, unregistered: 0, active: 2, retryErrors: 1 });
        assert.deepEqual(corrected[i], expected[i]);
        assert.deepEqual(actual[i], shared ? { attempts: 3, unregistered: 2, active: 0, retryErrors: 0 } : { attempts: 4, unregistered: 3, active: 0, retryErrors: 0 });
        classified.push({ index: i, input: cases[i], reference: expected[i], candidate: actual[i], reason: shared
            ? "Go updateWatch rolls back only new glob acquisitions, leaking an existing shared reference after a failed group and retry. C# releases every acquisition."
            : "Go updateWatch leaves successful partial registrations live. A native-style retry rejects the duplicate ID. C# closes successful partial registrations before retry." });
        continue;
    }
    try { assert.deepEqual(actual[i], corrected[i]); }
    catch { differences.push({ index: i, input: cases[i], reference: expected[i], control: corrected[i], candidate: actual[i] }); continue; }
    try { assert.deepEqual(actual[i], expected[i]); }
    catch { classified.push({ index: i, input: cases[i], reference: expected[i], candidate: actual[i],
        reason: "Go GetCommonParents reuses slice backing storage in append(group.head, sr...). The isolated control clones that prefix; C# matches the control exactly." }); }
}
await json(path.join(directory, "differences.json"), differences);
await json(path.join(directory, "classified.json"), classified);
const summary = { referenceRevision, cases: cases.length, differences: differences.length, classifiedDifferences: classified.length,
    rejectedInputs: expected.filter(value => value?.error).length, sources,
    classifications: { registryRollback: classified.filter(item => item.input.mode === "registry").length,
        commonParentSliceAliasing: classified.filter(item => item.input.mode !== "registry").length },
    comparison: "Exact decoded outputs; malformed dynamic path failures compare rejection, with both diagnostic streams retained.",
    controlMutation: "append(group.head, sr...) -> append(slices.Clone(group.head), sr...)",
    oracleSha256: sha256(await readFile(oracle)), controlSha256: sha256(await readFile(control)),
    compilerSha256: sha256(await readFile(path.join(path.dirname(dll), "TypeScript.Compiler.dll"))), inputsSha256: sha256(await readFile(path.join(directory, "inputs.jsonl"))) };
await json(path.join(directory, "summary.json"), summary); console.log(JSON.stringify(summary)); assert.equal(differences.length, 0);
