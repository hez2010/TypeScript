import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, referenceRevision, sha256 } from "./common.mjs";

const option = (name, fallback) => process.argv.includes(name) ? process.argv[process.argv.indexOf(name) + 1] : fallback;
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
assert.equal(reference.referenceRevision, referenceRevision);
const source = path.join(output, reference.sourceRelativePath, "tsc");
const directory = path.join(output, "phase7-validation/request-filesystem");
await mkdir(directory, { recursive: true });
const packagePath = path.join(source, "internal/api/requestfilesystem");
await copyFile(path.join(root, "csharp/oracle/request-filesystem/replay.go"), path.join(packagePath, "csharp_replay.go"));
const recordFile = path.join(directory, "original-inputs.jsonl");
if (!process.argv.includes("--reuse-recording")) {
    await writeFile(recordFile, "");
    await copyFile(path.join(root, "csharp/oracle/request-filesystem/record_test.go"), path.join(packagePath, "csharp_record_test.go"));
    const originals = [];
    try {
        for (const name of ["requestfilesystem_test.go", "pathtree_test.go", "filechanges_test.go"]) {
            const original = await run("git", ["show", `${referenceRevision}:tsc/internal/api/requestfilesystem/${name}`]);
            originals.push({ name, original });
            const instrumented = original.split(/(?=^func )/m).map(part => /^func \w+\([^\n]*t \*testing\.T/.test(part)
                ? part.replace(/(?<![.\w])new(?:Layered)?RequestFileSystem\(/g, 'csharpNewRequestFileSystem(t.Name(), ')
                    .replace(/(?<![.\w])NewForUpdate\(/g, 'csharpNewForUpdate(t.Name(), ') : part).join("");
            await writeFile(path.join(packagePath, name), instrumented);
        }
        const testOutput = await run(go, ["-C", source, "test", "-json", "-count=1", "./internal/api/requestfilesystem"],
            { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local", CSHARP_REQUEST_FS_RECORD: recordFile } });
        await writeFile(path.join(directory, "original-tests.jsonl"), testOutput);
    } finally { for (const { name, original } of originals) await writeFile(path.join(packagePath, name), original); }
}
let cases = (await readFile(recordFile, "utf8")).trim().split(/\r?\n/).filter(Boolean).map(JSON.parse);
const originalCount = cases.length;
// Deterministic extra compositions exercise collisions and branches beyond the original fixtures.
let seed = 0x19281;
const random = max => { seed ^= seed << 13; seed ^= seed >>> 17; seed ^= seed << 5; return (seed >>> 0) % max; };
for (let i = 0; i < 150; i++) {
    const input = { name: `generated-${i}`, cwd: "/", caseSensitive: i % 2 === 0, files: { "/host/file.ts": "host", "/dir/old.ts": "old" }, steps: [] };
    for (let j = 0; j < 5; j++) {
        const request = { kind: j === 0 && i % 3 === 0 ? "full" : "layer", files: {}, directories: {}, symlinks: {}, removedPaths: [] };
        const names = ["/dir", "/dir/pkg", "/host", "/target", "/link", "/dir/file.ts", "/target/file.ts"];
        for (let k = 0; k < 4; k++) {
            const name = names[random(names.length)];
            switch (random(4)) {
                case 0: request.files[name] = `file-${i}-${j}-${k}-😀`; break;
                case 1: request.directories[name] = { files: ["z.ts", "file.ts", "a.ts"], directories: ["nested"] }; break;
                case 2: {
                    const target = names[random(names.length)], host = random(4) === 0;
                    request.symlinks[name] = { target: host ? "/external" : target, host }; break;
                }
                case 3: request.removedPaths.push(name); break;
            }
        }
        input.steps.push({ base: j, request });
    }
    cases.push(input);
}
function absolute(name, cwd) {
    name = name.replaceAll("\\", "/");
    if (!name.startsWith("/") && !/^[\w-]+:/.test(name)) name = cwd.replace(/\/$/, "") + "/" + name;
    // Preserve URI authority and drive roots, while resolving dot segments in fixture paths.
    const match = /^(\w+:\/\/[^/]*|[A-Za-z]:)(\/.*)?$/.exec(name);
    return match ? match[1] + path.posix.normalize(match[2] || "/") : path.posix.normalize(name);
}
for (const input of cases) {
    const paths = new Set([input.cwd, ...Object.keys(input.files), ...Object.keys(input.symlinks ?? {}), ...(input.directories ?? [])].map(p => absolute(p, input.cwd)));
    const links = [];
    for (const step of input.steps) {
        const request = step.request;
        for (const p of [...Object.keys(request.files ?? {}), ...Object.keys(request.directories ?? {}), ...Object.keys(request.symlinks ?? {}), ...(request.removedPaths ?? [])]) paths.add(absolute(p, input.cwd));
        for (const [name, entries] of Object.entries(request.directories ?? {}))
            for (const entry of [...entries.files ?? [], ...entries.directories ?? []]) paths.add(absolute(name + "/" + entry, input.cwd));
        for (const [name, link] of Object.entries(request.symlinks ?? {})) links.push([absolute(name, input.cwd), absolute(link.target, path.posix.dirname(absolute(name, input.cwd)))]);
    }
    for (const p of [...paths]) {
        let ancestor = p;
        for (let depth = 0; depth < 20; depth++) {
            paths.add(ancestor); const parent = path.posix.dirname(ancestor);
            if (parent === ancestor || parent === "." || ancestor.endsWith(":") || ancestor.endsWith(":/")) break;
            ancestor = parent;
        }
        paths.add(p.replace(/\/$/, "") + "/missing.ts");
    }
    for (const [name, target] of links) {
        paths.add(name + "/file.ts"); paths.add(name + "/index.d.ts");
        for (const p of [...paths]) if (p.startsWith(target.replace(/\/$/, "") + "/")) paths.add(name.replace(/\/$/, "") + p.slice(target.replace(/\/$/, "").length));
    }
    const queries = [...paths].filter(p => !p.endsWith(":") && !p.includes(":/vscode-remote:")).sort();
    if (!input.caseSensitive) queries.push(...queries.map(p => p.toUpperCase()));
    for (const step of input.steps) step.queries = queries;
}
const filter = option("--filter", ""); if (filter) cases = cases.filter(input => new RegExp(filter).test(input.name));
await json(path.join(directory, "inputs.json"), cases);
const cmd = path.join(source, "cmd/csharp-request-filesystem"); await mkdir(cmd, { recursive: true });
await copyFile(path.join(root, "csharp/oracle/request-filesystem/main.go"), path.join(cmd, "main.go"));
const executable = path.join(directory, "oracle.exe");
await run(go, ["-C", source, "build", "-o", executable, "./cmd/csharp-request-filesystem"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
async function lines(command, args, label) {
    const child = spawn(command, args, { cwd: root, windowsHide: true });
    let stdout = "", stderr = "", timedOut = false;
    let timer;
    const reset = () => { clearTimeout(timer); timer = setTimeout(() => { timedOut = true; child.kill(); }, 30000); };
    reset();
    child.stdout.on("data", chunk => { stdout += chunk; reset(); }); child.stderr.on("data", chunk => stderr += chunk);
    child.stdin.on("error", () => {}); child.stdin.end(cases.map(input => JSON.stringify(input)).join("\n") + "\n");
    const exit = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
    clearTimeout(timer);
    await writeFile(path.join(directory, `${label}.stdout`), stdout); await writeFile(path.join(directory, `${label}.stderr`), stderr);
    if (timedOut) {
        const index = stdout.split("\n").length - 1;
        await json(path.join(directory, `${label}-timeout-input.json`), cases[index]);
        throw new Error(`${label} timed out at input ${index}: ${cases[index]?.name}`);
    }
    assert.equal(exit, 0, stderr); return stdout.trim().split(/\r?\n/).map(JSON.parse);
}
const dll = path.join(output, "phase7-build/bin/TypeScript.Compatibility/release/TypeScript.Compatibility.dll");
const runs = await Promise.allSettled([lines(executable, [], "reference"), lines(dotnet, [dll, "--request-filesystem-lines"], "candidate")]);
for (const result of runs) if (result.status === "rejected") console.error(result.reason);
assert.ok(runs.every(result => result.status === "fulfilled"), "A filesystem replay failed; raw output is preserved");
const [expected, actual] = runs.map(result => result.value);
await json(path.join(directory, "reference.json"), expected); await json(path.join(directory, "candidate.json"), actual);
assert.equal(expected.length, cases.length); assert.equal(actual.length, cases.length);
const differences = [];
for (let i = 0; i < cases.length; i++) {
    for (let j = 0; j < cases[i].steps.length; j++) {
        try { assert.deepEqual(actual[i][j], expected[i][j]); }
        catch {
            const fields = ["error", "kind", "compacted", "changes"].filter(name => JSON.stringify(actual[i][j]?.[name]) !== JSON.stringify(expected[i][j]?.[name]));
            const queries = [];
            for (let k = 0; k < cases[i].steps[j].queries.length; k++) {
                try { assert.deepEqual(actual[i][j]?.queries?.[k], expected[i][j]?.queries?.[k]); }
                catch { queries.push({ path: cases[i].steps[j].queries[k], expected: expected[i][j]?.queries?.[k], actual: actual[i][j]?.queries?.[k] }); }
            }
            differences.push({ name: cases[i].name, case: i, step: j, fields, queries });
        }
    }
}
await json(path.join(directory, "differences.json"), differences);
const summary = { referenceRevision, originalInputs: originalCount, cases: cases.length, transitions: cases.reduce((n, c) => n + c.steps.length, 0),
    queries: cases.reduce((n, c) => n + c.steps.reduce((v, s) => v + s.queries.length, 0), 0), differences: differences.length,
    compilerSha256: sha256(await readFile(path.join(path.dirname(dll), "TypeScript.Compiler.dll"))) };
await json(path.join(directory, "summary.json"), summary); console.log(JSON.stringify(summary));
assert.equal(differences.length, 0, `${differences.length} request filesystem transitions differ`);
