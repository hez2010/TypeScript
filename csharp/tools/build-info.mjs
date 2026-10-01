import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile, readdir } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, sha256, referenceRevision } from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const dotnet = option("--dotnet", process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, "dotnet.exe") : "dotnet");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const directory = path.join(output, "build-info");
await mkdir(directory, { recursive: true });
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc");
const bridgeDirectory = path.join(source, "cmd/csharp-build-info");
await mkdir(bridgeDirectory, { recursive: true });
await copyFile(path.join(root, "csharp/oracle/build-info/main.go"), path.join(bridgeDirectory, "main.go"));
const oracle = path.join(directory, "oracle.exe");
const managedDirectory = option("--managed-directory", path.join(output, "phase6-build/bin/TypeScript.Compatibility/release"));
const dll = path.join(managedDirectory, "TypeScript.Compatibility.dll");
if (!process.argv.includes("--no-build")) {
    await run(go, ["-C", source, "build", "-o", oracle, "./cmd/csharp-build-info"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
    await run(dotnet, ["build", "csharp/tests/TypeScript.Compatibility/TypeScript.Compatibility.csproj", "-c", "Release", "--no-restore", "--artifacts-path", "built/csharp/phase6-build"]);
}
const cases = [], seen = new Set();
for (const suite of ["tsc", "tscWatch", "tsbuild", "tsbuildWatch"]) {
    const baselineDirectory = path.join(source, "testdata/baselines/reference", suite);
    for (const name of (await readdir(baselineDirectory, { recursive: true })).filter(name => name.endsWith(".js")).sort()) {
        const text = await readFile(path.join(baselineDirectory, name), "utf8");
        for (const match of text.matchAll(/^\{"version":.*\}\r?$/gm)) {
            const hash = sha256(match[0]);
            if (seen.has(hash)) continue;
            const info = JSON.parse(match[0]);
            if (!info.fileInfos && !info.root) continue;
            seen.add(hash);
            cases.push({ name: `${suite}/${name}:${text.slice(0, match.index).split("\n").length}`, info });
        }
    }
}
const baselineCount = cases.length;
for (const version of ["7.1.0-dev", "7.0.0", "", "future"])
    cases.push({ name: `version-${version}`, info: { version, root: ["./a.ts"], errors: true, checkPending: true, semanticErrors: true } });
cases.push({ name: "compact-variants", info: { version: "7.1.0-dev", fileNames: ["./a.ts", "./b.ts", "./c.ts", "./d.ts"],
    fileInfos: ["v", { version: "b", noSignature: true }, { version: "c", signature: "s", affectsGlobalScope: true }, { version: "d", impliedNodeFormat: 1 }],
    root: [1, [3, 4]], fileIdsList: [[2, 3]], referencedMap: [[1, 1]], semanticDiagnosticsPerFile: [1, [2, [{ code: 2304, category: 1, pos: 4, end: 8, messageKey: "Cannot_find_name_0_2304", messageArgs: ["🦄"], relatedInformation: [{ file: 1, code: 2304, messageText: "context" }] }]]],
    emitDiagnosticsPerFile: [[3, []]], affectedFilesPendingEmit: [1, [2], [3, 3]], emitSignatures: [1, [2, "s"], [3, []], [4, ["s"]]],
    resolvedRoot: [[1, 2]], changeFileSet: [1], options: { composite: true, outDir: "./out", target: 99 }, contentMapperIdentities: ["mapper-v1"],
    latestChangedDtsFile: "./out/é.d.ts", packageJsons: ["./package.json"], missingPackageJsons: ["../package.json"] } });
for (const text of ["", "ascii", "é", "😀", "a\r\nb", "a\0b", "x".repeat(100000)])
    for (const includeText of [false, true]) cases.push({ name: `hash-${cases.length}`, text, includeText });
for (const bytes of [[0xED, 0xA0, 0x80], [0xFF, 0xC0, 0x80], Array.from({length: 256}, (_, i) => i)])
    cases.push({ name: `hash-bytes-${cases.length}`, textBase64: Buffer.from(bytes).toString("base64") });
await json(path.join(directory, "inputs.json"), cases);
async function execute(command, args) {
    const child = spawn(command, args, { cwd: root, windowsHide: true });
    let stdout = "", stderr = "";
    child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data);
    child.stdin.on("error", () => {});
    child.stdin.end(cases.map(c => JSON.stringify(c)).join("\n") + "\n");
    const code = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
    if (code) throw Error(`${command} exited ${code}: ${stderr}\n${stdout.slice(-2000)}`);
    const rows = stdout.trim().split(/\r?\n/).map(JSON.parse);
    assert.equal(rows.length, cases.length);
    return rows;
}
const hashes = { reference: sha256(await readFile(oracle)), candidate: sha256(await readFile(dll)), compiler: sha256(await readFile(path.join(managedDirectory, "TypeScript.Compiler.dll"))) };
const before = await execute(oracle, []), after = await execute(dotnet, [dll, "--build-info-lines"]);
const failures = [];
for (let index = 0; index < cases.length; index++) {
    try { assert.deepEqual(after[index], before[index]); }
    catch { failures.push({ input: cases[index], inputHash: sha256(JSON.stringify(cases[index])), reference: before[index], candidate: after[index] }); }
}
const safety = await run(dotnet, [dll, "--build-info-safety"]);
for (const [name, file] of Object.entries({ reference: oracle, candidate: dll, compiler: path.join(managedDirectory, "TypeScript.Compiler.dll") }))
    assert.equal(sha256(await readFile(file)), hashes[name], `${name} changed during validation`);
const summary = { referenceRevision, cases: cases.length, baselineStates: baselineCount, strictMatches: cases.length - failures.length,
    failures: failures.length, safety, hashes, inputHash: sha256(JSON.stringify(cases)), command: "node csharp/tools/build-info.mjs --dotnet <dotnet.exe> --record" };
await json(path.join(directory, "failures.json"), failures);
await json(path.join(directory, "summary.json"), summary);
if (process.argv.includes("--record")) await json(path.join(root, "csharp/compatibility/evidence/phase6-build-info.json"), summary);
console.log(JSON.stringify(summary, null, 2));
assert.equal(failures.length, 0);
