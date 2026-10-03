import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { performance } from "node:perf_hooks";
import { json, output, referenceRevision, sha256 } from "./common.mjs";
import { Metrics, activityReport, fileHashes, paired, saveSummary, summarizePairs } from "./performance-common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const directory = path.resolve(option("--directory", path.join(output, "phase8-performance/watch")));
const manifest = JSON.parse(await readFile(option("--manifest", path.join(output, "phase8-final-managed2/manifest.json"))));
assert.equal(manifest.runtime, "coreclr-validation");
const executables = { go: path.resolve(option("--go-executable", path.join(output, "phase8-validation/cli-host-final/oracle.exe"))), csharp: path.join(manifest.platformDirectory, "lib/tsgo.exe") };
const samples = Number(option("--samples", "21"));
await mkdir(directory, { recursive: true });
const env = { ...process.env, DOTNET_ROOT: process.env.DOTNET_ROOT ?? "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64",
    DOTNET_TieredCompilation: "1", COMPlus_TieredCompilation: "1", DOTNET_gcServer: "0", COMPlus_gcServer: "0" };
const config = { compilerOptions: { target: "es2022", module: "nodenext", lib: ["es5"], strict: true, skipLibCheck: true, declaration: true, outDir: "out" }, files: ["index.ts", "model.ts"] };
const files = { "tsconfig.json": JSON.stringify(config), "package.json": '{"private":true,"type":"module"}',
    "index.ts": "import { answer } from './model.js'; export const result = answer();\n",
    "model.ts": "export function answer(): number { return 1; }\n" };
await json(path.join(directory, "inputs.json"), files);
const metrics = await Metrics.start(), own = [process.pid, metrics.child.pid], activity = await metrics.call({ kind: "activity" });
const binaries = await fileHashes({ ...executables, compiler: path.join(path.dirname(executables.csharp), "TypeScript.Compiler.dll") });
const servers = {}, groups = [];
async function start(backend) {
    const folder = path.join(directory, backend); await mkdir(folder, { recursive: true });
    for (const [name, text] of Object.entries(files)) await writeFile(path.join(folder, name), text);
    const child = spawn(executables[backend], ["--watch", "--project", ".", "--pretty", "false", "--preserveWatchOutput"], { cwd: folder, env, windowsHide: true });
    own.push(child.pid);
    const cycles = [], waiting = []; let stdout = "", stderr = "", previousEnd = 0;
    child.stderr.on("data", data => stderr += data);
    child.stdout.on("data", data => {
        stdout += data;
        const expression = /Found (\d+) errors?\. Watching for file changes\./g; expression.lastIndex = previousEnd;
        for (let match; (match = expression.exec(stdout));) {
            const cycle = { errors: +match[1], completed: performance.now(), stdout: stdout.slice(previousEnd, expression.lastIndex) };
            previousEnd = expression.lastIndex; cycles.push(cycle);
        }
        for (let index = waiting.length - 1; index >= 0; index--) if (cycles.length > waiting[index].after) {
            const next = waiting.splice(index, 1)[0]; clearTimeout(next.timer); next.resolve(cycles[next.after]);
        }
    });
    const exit = new Promise((resolve, reject) => { child.once("error", reject); child.once("close", resolve); });
    function wait(after) {
        if (cycles.length > after) return Promise.resolve(cycles[after]);
        return new Promise((resolve, reject) => { const item = { after, resolve, timer: setTimeout(() => reject(new Error(`${backend}: watch cycle timed out\n${stdout.slice(-3000)}\n${stderr}`)), 30000) }; waiting.push(item); });
    }
    const initial = await wait(0); assert.equal(initial.errors, 0);
    return { folder, child, cycles, wait, async close() { child.kill(); await exit; await writeFile(path.join(directory, backend + ".stdout.log"), stdout); await writeFile(path.join(directory, backend + ".stderr.log"), stderr); } };
}
try {
    for (const backend of ["go", "csharp"]) servers[backend] = await start(backend);
    for (const kind of ["implementation", "declaration", "config", "package", "churn"]) {
        const raw = path.join(directory, kind + ".jsonl"); await writeFile(raw, "");
        const effects = new Map();
        const pairs = await paired(metrics, samples, async (backend, iteration) => {
            const server = servers[backend], after = server.cycles.length, value = iteration % 2 ? 2 : 1;
            const start = performance.now();
            if (kind === "implementation") await writeFile(path.join(server.folder, "model.ts"), `export function answer(): number { return ${value}; }\n`);
            else if (kind === "declaration") await writeFile(path.join(server.folder, "model.ts"), `export function answer(): ${value === 1 ? "number" : "string"} { return ${value === 1 ? "1" : "'one'"}; }\n`);
            else if (kind === "config") await writeFile(path.join(server.folder, "tsconfig.json"), JSON.stringify({ ...config, compilerOptions: { ...config.compilerOptions, removeComments: value === 1 } }));
            else if (kind === "package") await writeFile(path.join(server.folder, "package.json"), JSON.stringify({ private: true, type: value === 1 ? "module" : "commonjs" }));
            else {
                for (let edit = 0; edit < 10; edit++) await writeFile(path.join(server.folder, "model.ts"), `export function answer(): number { return ${iteration * 10 + edit}; }\n`);
            }
            const completed = await server.wait(after), elapsedMs = completed.completed - start;
            assert.equal(completed.errors, 0);
            const files = {};
            for (const name of ["index.js", "index.d.ts", "model.js", "model.d.ts"]) files[name] = sha256(await readFile(path.join(server.folder, "out", name)));
            if (effects.has(iteration)) { assert.deepEqual(files, effects.get(iteration), `${kind}: emitted files`); effects.delete(iteration); }
            else effects.set(iteration, files);
            return { elapsedMs, errors: completed.errors, emittedSha256: sha256(JSON.stringify(files)), cycle: after };
        }, raw);
        const summary = { name: kind, elapsedMs: summarizePairs(pairs), rawFile: raw, rawSha256: sha256(await readFile(raw)) };
        groups.push(summary); console.log(`${kind}: Go ${summary.elapsedMs.go.median.toFixed(2)} ms, C# ${summary.elapsedMs.csharp.median.toFixed(2)} ms`);
    }
    const memory = {};
    for (const [backend, server] of Object.entries(servers)) memory[backend] = await metrics.call({ kind: "observe", processId: server.child.pid });
    assert.deepEqual(await fileHashes(Object.fromEntries(Object.entries(binaries).map(([name, item]) => [name, item.file]))), binaries);
    await saveSummary(path.join(directory, "summary.json"), { referenceRevision, runtime: "Release CoreCLR", nativeExecuted: false, binaries, samples, groups, memory,
        machineActivity: await activityReport(metrics, activity, own), inputsSha256: sha256(JSON.stringify(files)),
        boundaries: ["Real Windows filesystem watchers, elapsed time from write to completed compiler status", "Identical emitted JavaScript and declarations after every cycle",
            "Churn writes ten source versions per cycle; batching/debounce latency is included", "Only owned watcher processes are terminated during cleanup"] });
} finally { for (const server of Object.values(servers)) await server.close(); await metrics.close(); }
