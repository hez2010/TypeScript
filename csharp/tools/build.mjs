import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, sha256, referenceRevision } from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const dotnet = option("--dotnet", process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, "dotnet.exe") : "dotnet");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const directory = path.join(output, "build", option("--tag", "current"));
await mkdir(directory, { recursive: true });
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc");
const bridgeDirectory = path.join(source, "cmd/csharp-build");
await mkdir(bridgeDirectory, { recursive: true });
await copyFile(path.join(root, "csharp/oracle/build/main.go"), path.join(bridgeDirectory, "main.go"));
const oracle = path.join(output, "build/oracle.exe");
const managedDirectory = option("--managed-directory", path.join(output, "phase6-build/bin/TypeScript.Compatibility/release"));
const dll = path.join(managedDirectory, "TypeScript.Compatibility.dll");
if (!process.argv.includes("--no-build")) {
    await run(go, ["-C", source, "build", "-o", oracle, "./cmd/csharp-build"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
    await run(dotnet, ["build", "csharp/tests/TypeScript.Compatibility/TypeScript.Compatibility.csproj", "-c", "Release", "--no-restore", "--artifacts-path", "built/csharp/phase6-build"]);
}
let cases = [];
const library = "/// <reference no-default-lib=\"true\"/>\ninterface Boolean {}\ninterface Function {}\ninterface CallableFunction {}\ninterface NewableFunction {}\ninterface IArguments {}\ninterface Number {}\ninterface Object {}\ninterface RegExp {}\ninterface String {}\ninterface Array<T> { length: number; [n: number]: T; }\ninterface ReadonlyArray<T> {}\n";
const args = (...options) => ["--build", "--verbose", "--pretty", "false", "--singleThreaded", ...options];
const config = (options = {}, references = []) => JSON.stringify({ compilerOptions: { lib: ["es5"], outDir: "dist", ...options }, ...(references.length ? { references: references.map(path => ({ path })) } : {}) });
const files = options => ({ "/source/tsconfig.json": config(options), "/source/a.ts": "export function value(): number { return 1; }",
    "/source/b.ts": "import { value } from './a'; export const result = value();" });
function add(name, input, steps, command = args()) {
    cases.push({ name, files: { "/lib/lib.es5.d.ts": library, ...input }, libraryDirectory: "/lib", steps: [{ args: command }, { args: command }, ...steps.map(step => ({ args: command, ...step })), { args: command }] });
}
for (const incremental of [false, true]) for (const declaration of [false, true]) for (const noEmit of [false, true]) {
    const options = { incremental, declaration, noEmit };
    const suffix = `${incremental}-${declaration}-${noEmit}`;
    add(`edits-${suffix}`, files(options), [
        { edits: { "/source/a.ts": "export function value(): number { return 2; }" } },
        { edits: { "/source/a.ts": "export function value(): string { return 'two'; }" } },
        { touch: ["/source/a.ts"] }, { edits: { "/source/a.ts": files(options)["/source/a.ts"] } }
    ]);
    add(`commands-${suffix}`, files(options), [
        { args: args("--dry") }, { args: args("--force") }, { args: args("--clean", "--dry") },
        { args: args("--clean") }, { args: args("--dry") }, {}, { args: args("--clean") }, {}
    ]);
    add(`missing-${suffix}`, files(options), [
        { edits: { "/source/dist/a.js": null } }, {}, { edits: { "/source/a.ts": null } },
        { edits: { "/source/a.ts": files(options)["/source/a.ts"] } },
        { edits: { "/source/tsconfig.json": config({ ...options, strict: false }) } }
    ]);
}
const graph = () => ({
    "/source/tsconfig.json": JSON.stringify({ files: [], references: [{ path: "./a" }, { path: "./b" }, { path: "./c" }] }),
    "/source/a/tsconfig.json": config({ composite: true }),
    "/source/a/a.ts": "export function value(): number { return 1; }",
    "/source/b/tsconfig.json": config({ composite: true }, ["../a"]),
    "/source/b/b.ts": "import { value } from '../a/a'; export const result = value();",
    "/source/c/tsconfig.json": config({ composite: true }, ["../b"]),
    "/source/c/c.ts": "import { result } from '../b/b'; const check: number = result;"
});
for (const stop of [false, true]) for (const builders of [1, 4]) {
    const command = ["--build", "--verbose", "--pretty", "false", "--builders", String(builders), "--stopBuildOnErrors", String(stop)];
    add(`graph-edits-${stop}-${builders}`, graph(), [
        { edits: { "/source/a/a.ts": "export function value(): number { return 2; }" } },
        { touch: ["/source/a/a.ts"] },
        { edits: { "/source/a/a.ts": "export function value(): string { return 'text'; }" } },
        { edits: { "/source/a/a.ts": "export const value: number = 'error';" } },
        { edits: { "/source/a/a.ts": graph()["/source/a/a.ts"] } }
    ], command);
}
add("graph-clean-force-dry", graph(), [{ args: args("--force") }, { args: args("--clean", "--dry") }, { args: args("--clean") }, { args: args("--dry") }, {}]);
const graphChanges = [
    { edits: { "/source/tsconfig.json": JSON.stringify({ files: [], references: [{ path: "./c" }] }) } },
    { edits: { "/source/c/tsconfig.json": config({ composite: true }, ["../missing"]) } },
    { edits: { "/source/c/tsconfig.json": graph()["/source/c/tsconfig.json"] } }
];
add("graph-added-removed-project", graph(), graphChanges);
add("graph-added-removed-project-stop", graph(), graphChanges, args("--stopBuildOnErrors"));
add("graph-added-removed-project-force", graph(), graphChanges, args("--force"));
add("missing-root-project", {}, [], args("./missing"));
add("missing-reference-cold", { ...graph(), "/source/c/tsconfig.json": config({ composite: true }, ["../missing"]) }, []);
for (const circular of [false, true]) add(`cycle-${circular}`, {
    "/source/tsconfig.json": JSON.stringify({ files: [], references: [{ path: "./a", circular }] }),
    "/source/a/tsconfig.json": JSON.stringify({ files: [], references: [{ path: ".." }] })
}, []);
for (const name of ["noEmit", "noCheck", "declaration", "sourceMap", "declarationMap", "emitDeclarationOnly", "skipLibCheck"]) {
    const original = { incremental: true, declaration: true, [name]: false };
    add(`options-${name}`, files(original), [
        { edits: { "/source/tsconfig.json": config({ ...original, [name]: true }) } }, {},
        { edits: { "/source/tsconfig.json": config(original) } }
    ]);
}
add("config-extends", { ...files({ incremental: true }), "/source/base.json": "{\"compilerOptions\":{\"declaration\":true}}",
    "/source/tsconfig.json": "{\"extends\":\"./base.json\",\"compilerOptions\":{\"incremental\":true,\"outDir\":\"dist\",\"lib\":[\"es5\"]}}" }, [
    { edits: { "/source/base.json": "{\"compilerOptions\":{\"declaration\":true,\"sourceMap\":true}}" } }, { edits: { "/source/base.json": null } }
]);
add("package-json", { ...files({ incremental: true, module: "nodenext" }), "/source/package.json": "{\"type\":\"commonjs\"}" }, [
    { edits: { "/source/package.json": "{\"type\":\"module\"}" } }, { edits: { "/source/package.json": null } }
]);
add("quiet", graph(), [{ args: args("--quiet") }]);
for (const force of [false, true]) add(`new-root-preserved-timestamp${force ? "-force" : ""}`, files({ incremental: true, declaration: true }), [
    { edits: { "/source/added.ts": "export const added = true;" }, times: { "/source/added.ts": 0 } }
], args(...(force ? ["--force"] : [])));
const filter = option("--filter", ""); if (filter) cases = cases.filter(test => new RegExp(filter).test(test.name));
const limit = option("--limit", ""); if (limit) cases = cases.slice(0, Number(limit));
await json(path.join(directory, "inputs.json"), cases);
async function execute(command, args) {
    const child = spawn(command, args, { cwd: root, windowsHide: true });
    let stdout = "", stderr = "";
    child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data);
    child.stdin.on("error", () => {}); child.stdin.end(cases.map(test => JSON.stringify(test)).join("\n") + "\n");
    const code = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
    if (code) throw Error(`${command} exited ${code}: ${stderr}`);
    const rows = stdout.trim().split(/\r?\n/).map(JSON.parse); assert.equal(rows.length, cases.length);
    return rows;
}
const hashes = { reference: sha256(await readFile(oracle)), candidate: sha256(await readFile(dll)), compiler: sha256(await readFile(path.join(managedDirectory, "TypeScript.Compiler.dll"))) };
const before = await execute(oracle, []), after = await execute(dotnet, [dll, "--build-lines"]);
await json(path.join(directory, "reference.json"), before); await json(path.join(directory, "candidate.json"), after);
// Status-report clock text is variable. Diagnostic text, paths, output order, writes and timestamp operations remain exact.
const normalize = rows => Array.isArray(rows) ? rows.map(row => ({ ...row, stdout: row.stdout.replace(/^\d{2}:\d{2}:\d{2} [AP]M - /gm, "<time> - ") })) : rows;
const failures = [];
for (let index = 0; index < cases.length; index++) {
    try { assert.deepEqual(normalize(after[index]), normalize(before[index])); }
    catch { failures.push({ index, name: cases[index].name, inputHash: sha256(JSON.stringify(cases[index])), reference: normalize(before[index]), candidate: normalize(after[index]) }); }
}
// Keep the original differences, bind complete outputs to reviewed bytes, and
// require independent successful controls in the same run. Panic stacks remain
// in the raw record; their process addresses are excluded from the stable hash.
const corrections = JSON.parse(await readFile(path.join(root, "csharp/compatibility/build-corrections.json"), "utf8"));
const permittedDifferences = [];
for (const failure of failures) {
    const correction = corrections.cases.find(item => item.name === failure.name && item.inputHash === failure.inputHash);
    if (!correction || corrections.referenceRevision !== referenceRevision || !Array.isArray(failure.reference)) continue;
    if (correction.error) {
        const crashed = failure.reference[correction.cycle];
        if (crashed?.error !== correction.error || !crashed.stack?.includes(correction.stackFrame)) continue;
    }
    const stableReference = failure.reference.map(({ stack, ...row }) => row);
    if (sha256(JSON.stringify(stableReference)) !== correction.referenceHash
        || sha256(JSON.stringify(failure.candidate)) !== correction.candidateHash) continue;
    const controls = correction.controls.map(control => {
        const index = cases.findIndex(test => test.name === control.name);
        return index >= 0 && sha256(JSON.stringify(cases[index])) === control.inputHash
            && !failures.some(other => other.index === index);
    });
    if (!controls.every(Boolean)) continue;
    const forcedRows = before[cases.findIndex(test => test.name === correction.forcedControl)];
    const forced = forcedRows[correction.cycle];
    assert.equal(failure.candidate[correction.cycle].status, forced.status);
    for (const write of failure.candidate[correction.cycle].writes.filter(write => !write.buildInfo))
        assert.deepEqual(write, forced.writes.find(other => other.path === write.path), "Recovered compiler output differs from forced Go compilation");
    if (correction.compareFinalFiles) {
        const finalFiles = rows => {
            const files = new Map();
            for (const row of rows) {
                for (const write of row.writes) files.set(write.path, write);
                for (const path of row.removed) files.delete(path);
            }
            return [...files].sort(([a], [b]) => a.localeCompare(b));
        };
        assert.deepEqual(finalFiles(failure.candidate), finalFiles(forcedRows), "Final outputs differ from forced Go compilation");
    }
    permittedDifferences.push({ name: failure.name, inputHash: failure.inputHash, reason: correction.reason,
        controls: correction.controls.map(control => control.name), rawReferenceHash: sha256(JSON.stringify(before[failure.index])),
        candidateHash: correction.candidateHash });
}
for (const [name, file] of Object.entries({ reference: oracle, candidate: dll, compiler: path.join(managedDirectory, "TypeScript.Compiler.dll") }))
    assert.equal(sha256(await readFile(file)), hashes[name], `${name} changed during validation`);
const summary = { referenceRevision, cases: cases.length, cycles: cases.reduce((count, test) => count + test.steps.length, 0), strictMatches: cases.length - failures.length,
    failures: failures.length - permittedDifferences.length, strictDifferences: failures.length, permittedDifferences,
    normalizedFields: ["stdout: status-report clock prefix"], hashes, inputHash: sha256(JSON.stringify(cases)),
    command: "node csharp/tools/build.mjs --dotnet <dotnet.exe> --record" };
await json(path.join(directory, "failures.json"), failures); await json(path.join(directory, "summary.json"), summary);
if (process.argv.includes("--record")) await json(path.join(root, "csharp/compatibility/evidence/phase6-build.json"), summary);
console.log(JSON.stringify(summary, null, 2));
if (failures.length) console.log(failures.slice(0, 12).map(failure => failure.name).join("\n"));
assert.equal(summary.failures, 0);
