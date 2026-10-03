import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import path from "node:path";
import { json, output, root, sha256 } from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const directory = path.resolve(option("--directory", path.join(output, "phase8-validation/cli-profiling")));
const managed = path.resolve(option("--managed-directory", path.join(output, "phase8-build/bin/TypeScript.CommandLine/release")));
const oracle = path.resolve(option("--oracle", path.join(output, "phase8-validation/cli-host-verified/oracle.exe")));
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const analyzer = path.resolve(option("--analyzer-directory", "csharp/tools/consumers/node_modules/@typescript/analyze-trace"));
const node = process.execPath;
const env = { ...process.env, DOTNET_ROOT: process.env.DOTNET_ROOT ?? "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64" };
await mkdir(directory, { recursive: true });
const inputs = {
    "tsconfig.json": JSON.stringify({ compilerOptions: { strict: true, target: "esnext", module: "esnext", skipLibCheck: true, outDir: "out", declaration: true }, files: ["model.ts", "index.ts"] }),
    "model.ts": "export interface Box<T> { value: T; }\nexport type Picked<T> = T extends Box<infer U> ? U : never;\nexport type Repeated<T, N extends number, A extends T[] = []> = A['length'] extends N ? A : Repeated<T, N, [...A, T]>;\nexport function wrap<T>(value: T): Box<T> { return { value }; }\n",
    "index.ts": "import { Box, Picked, Repeated, wrap } from './model.js';\nexport const café: Box<{ 名: string }> = wrap({ 名: '世界 😀' });\nexport type Answer = Picked<typeof café>;\nexport type Copies = Repeated<Answer, 24>;\n",
};
await json(path.join(directory, "inputs.json"), inputs);
async function execute(executable, args, cwd) {
    const child = spawn(executable, args, { cwd, windowsHide: true, env }); child.stdin.end();
    let stdout = "", stderr = "";
    child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data);
    const timer = setTimeout(() => child.kill(), 180000);
    const code = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); }); clearTimeout(timer);
    return { code, stdout, stderr };
}
async function emitted(work) {
    const result = {};
    for (const name of (await readdir(path.join(work, "out"))).sort()) result[name] = (await readFile(path.join(work, "out", name))).toString("base64");
    return result;
}
function validateTrace(events, legend, types) {
    const stack = new Map(), metadata = new Set();
    for (const event of events) {
        assert.ok(Number.isFinite(event.ts) && event.ts >= 0);
        if (event.name === "thread_name") metadata.add(event.tid);
        if (!stack.has(event.tid)) stack.set(event.tid, []);
        if (event.ph === "B") stack.get(event.tid).push(event.name);
        if (event.ph === "E") assert.equal(stack.get(event.tid).pop(), event.name);
        if (event.ph === "X") assert.ok(Number.isFinite(event.dur) && event.dur >= 0);
        if (event.args?.checkerId !== undefined) {
            const table = types.get(event.args.checkerId); assert.ok(table);
            for (const key of ["sourceId", "targetId", "id"]) if (event.args[key] !== undefined) assert.ok(table.some(type => type.id === event.args[key]), key);
        }
    }
    for (const [thread, values] of stack) { assert.equal(values.length, 0); assert.ok(metadata.has(thread)); }
    assert.ok(events.some(event => event.name === "createProgram"));
    assert.ok(events.some(event => event.name === "checkSourceFile"));
    assert.ok(legend.length > 0 && types.size === legend.length);
    for (const table of types.values()) {
        const ids = new Set(table.map(type => type.id)); assert.equal(ids.size, table.length);
        for (const type of table) {
            assert.ok(type.flags.length > 0);
            for (const [key, value] of Object.entries(type)) {
                if (["unionTypes", "intersectionTypes", "aliasTypeArguments", "typeArguments"].includes(key)) for (const id of value) assert.ok(ids.has(id), key);
                if (/^(keyofType|indexedAccessObjectType|indexedAccessIndexType|conditionalCheckType|conditionalExtendsType|conditionalTrueType|conditionalFalseType|substitutionBaseType|constraintType|instantiatedType|reverseMappedSourceType|reverseMappedMappedType|reverseMappedConstraintType|evolvingArrayElementType|evolvingArrayFinalType)$/.test(key) && value > 0) assert.ok(ids.has(value), key);
            }
        }
    }
}
const results = [];
for (const checkers of [1, 4]) {
    let expected;
    for (const [label, executable] of [["reference", oracle], ["candidate", path.join(managed, "tsgo-cs.exe")]]) {
        for (const mode of ["control", "statistics", "trace", "profile"]) {
            const work = path.join(directory, `${label}-${checkers}-${mode}`); await mkdir(work, { recursive: true });
            for (const [name, text] of Object.entries(inputs)) await writeFile(path.join(work, name), text);
            const flags = mode === "statistics" ? ["--extendedDiagnostics"] : mode === "trace" ? ["--generateTrace", "trace"] : mode === "profile" ? ["--pprofDir", "profiles"] : [];
            const process = await execute(executable, ["--project", "tsconfig.json", "--checkers", String(checkers), ...flags], work);
            await json(path.join(work, "process.json"), process); assert.equal(process.code, 0, `${label}/${mode}: ${process.stderr}\n${process.stdout}`);
            const files = await emitted(work);
            if (expected === undefined) expected = files;
            else assert.deepEqual(files, expected, `${label}/${checkers}/${mode} ordinary emit`);
            const result = { label, checkers, mode, exitCode: process.code, files: Object.keys(files), outputSha256: sha256(JSON.stringify(files)) };
            if (mode === "control") assert.equal(process.stdout, "");
            if (mode === "statistics") {
                const rows = Object.fromEntries(process.stdout.trim().split(/\r?\n/).map(row => {
                    const match = /^([^:]+):\s+(\d+(?:\.\d+)?[Ks]?)$/.exec(row); assert.ok(match, row);
                    return [match[1], match[2]];
                }));
                assert.ok(Number(rows.Files) >= 2 && Number(rows.Lines) > 0 && Number(rows.Identifiers) > 0);
                if (label === "candidate") {
                    assert.ok(Number(rows["CLR allocated bytes"]) > 0 && Number(rows["CLR managed bytes"]) > 0);
                    assert.ok(!("Memory allocs" in rows)); assert.ok("Parse time (summed)" in rows);
                }
                result.rows = rows;
            }
            if (mode === "trace") {
                assert.equal(process.stdout, "");
                const traceDir = path.join(work, "trace"), legend = JSON.parse(await readFile(path.join(traceDir, "legend.json")));
                const events = JSON.parse(await readFile(path.join(traceDir, "trace.json"))), types = new Map();
                for (const entry of legend) types.set(entry.checkerId, JSON.parse(await readFile(path.join(traceDir, path.basename(entry.typesPath)))));
                validateTrace(events, legend, types);
                const analysis = await execute(node, [path.join(analyzer, "bin/analyze-trace"), traceDir, "--json", "--forceMillis", "0", "--skipMillis", "0"], root);
                await json(path.join(work, "analyze-trace.json"), analysis); assert.ok(analysis.code === 0 || analysis.code === 1, analysis.stderr);
                const analyzed = JSON.parse(analysis.stdout); assert.ok(analyzed);
                for (const entry of legend) {
                    const simplified = path.join(work, `simplified-${entry.checkerId}.txt`);
                    const simplifiedResult = await execute(node, [path.join(analyzer, "bin/simplify-trace-types"), path.join(traceDir, path.basename(entry.typesPath)), simplified], root);
                    assert.equal(simplifiedResult.code, 0, simplifiedResult.stderr); assert.ok((await readFile(simplified)).length > 0);
                }
                result.events = events.length; result.typeFiles = legend.length; result.types = [...types.values()].reduce((sum, values) => sum + values.length, 0);
            }
            if (mode === "profile") {
                const profiles = (await readdir(path.join(work, "profiles"))).filter(name => name.endsWith(".pb.gz")); assert.equal(profiles.length, 2);
                for (const name of profiles) {
                    const profile = await execute(go, ["tool", "pprof", "-raw", path.join(work, "profiles", name)], root);
                    assert.equal(profile.code, 0, profile.stderr); assert.match(profile.stdout, name.includes("-cpu") ? /cpu\/nanoseconds/ : /alloc_space\/bytes/);
                    await writeFile(path.join(work, name + ".txt"), profile.stdout);
                }
                result.profiles = profiles;
            }
            results.push(result);
        }
    }
}
const summary = { results, cases: results.length, differences: 0, nativeExecuted: false,
    compilerSha256: sha256(await readFile(path.join(managed, "TypeScript.Compiler.dll"))),
    consumerVersion: JSON.parse(await readFile(path.join(analyzer, "package.json"))).version,
    policy: "Ordinary diagnostics/emit remain exact. Trace events reflect actual C# operations and checker identities; CLR allocation bytes, type allocations and summed parallel phase durations retain their explicit units." };
await json(path.join(directory, "summary.json"), summary); console.log(JSON.stringify({ cases: results.length, differences: 0, consumerVersion: summary.consumerVersion }));
