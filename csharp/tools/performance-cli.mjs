import assert from "node:assert/strict";
import {
    mkdir,
    readdir,
    readFile,
    writeFile,
} from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import {
    json,
    output,
    referenceRevision,
    root,
    sha256,
} from "./common.mjs";
import {
    activityReport,
    fileHashes,
    Metrics,
    paired,
    percentile,
    saveSummary,
    summarizePairs,
} from "./performance-common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const directory = path.resolve(option("--directory", path.join(output, "phase8-performance/cli")));
const packageManifest = JSON.parse(await readFile(option("--manifest", path.join(output, "phase8-final-managed/manifest.json"))));
assert.ok(["coreclr-validation", "nativeaot"].includes(packageManifest.runtime));
const nativeAot = packageManifest.runtime === "nativeaot";
const serverGC = process.argv.includes("--server-gc");
const baseline = option("--baseline-manifest") ? JSON.parse(await readFile(option("--baseline-manifest"))) : null;
if (baseline) {
    assert.equal(baseline.runtime, packageManifest.runtime);
    assert.equal(baseline.version, packageManifest.version);
}
const backends = baseline ? ["before", "after"] : ["go", "csharp"];
const executables = {
    [backends[0]]: baseline ? path.join(baseline.platformDirectory, "lib", (baseline.executableName ?? "tsgo") + ".exe")
        : path.resolve(option("--go-executable", path.join(output, "phase8-validation/cli-host-verified/oracle.exe"))),
    [backends[1]]: path.join(packageManifest.platformDirectory, "lib", (packageManifest.executableName ?? "tsgo") + ".exe"),
};
const filter = new RegExp(option("--filter", ".*"));
const samples = Number(option("--samples", "21"));
const env = { ...!nativeAot ? { DOTNET_ROOT: option("--dotnet-root", process.env.DOTNET_ROOT ?? "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64"), DOTNET_TieredCompilation: "1", COMPlus_TieredCompilation: "1" } : {}, DOTNET_gcServer: serverGC ? "1" : "0", COMPlus_gcServer: serverGC ? "1" : "0" };
await mkdir(directory, { recursive: true });
const inputs = {}, fixtures = {};
async function fixture(name, files, options = {}) {
    const tree = { "package.json": '{"private":true,"type":"module"}', "tsconfig.json": JSON.stringify({ compilerOptions: { target: "es2022", module: "esnext", moduleResolution: "bundler", lib: ["es5"], strict: true, skipLibCheck: true, declaration: true, rootDir: "src", outDir: "out", ...options }, include: ["src/**/*.ts"] }), ...files };
    inputs[name] = tree;
    fixtures[name] = {};
    for (const backend of backends) {
        const folder = path.join(directory, name, backend);
        fixtures[name][backend] = folder;
        for (const [file, text] of Object.entries(tree)) {
            await mkdir(path.dirname(path.join(folder, file)), { recursive: true });
            await writeFile(path.join(folder, file), text);
        }
    }
}
await fixture("empty", {}, { noEmit: true });
await fixture("small", { "src/index.ts": 'export const café: number = "世界 😀";\n' }, { noEmit: true });
const medium = Object.fromEntries(Array.from({ length: 64 }, (_, file) => [`src/f${file}.ts`, Array.from({ length: 24 }, (_, item) => `export interface Item${item} { name: string; count: number; values: readonly number[] }\nexport const value${item}: Item${item} = { name: '日本語😀', count: ${item}, values: [1, 2, 3] };\n`).join("")]));
await fixture("medium", medium);
await fixture("declarations", { ...medium, "src/generic.ts": "export type Flatten<T> = T extends readonly (infer U)[] ? Flatten<U> : T;\nexport type Values<T> = { [K in keyof T]: { key: K; value: Flatten<T[K]> } }[keyof T];\nexport type Model = Values<{ a: number[]; b: string[][]; c: { x: boolean } }>;\n" }, { emitDeclarationOnly: true });
const tiny = Object.fromEntries(Array.from({ length: 512 }, (_, index) => [`src/f${index}.ts`, `export const v${index}: number = ${index};\n`]));
await fixture("tiny-files", tiny);
await fixture("tiny-incremental", tiny, { incremental: true });
await fixture("huge-file", { "src/index.ts": Array.from({ length: 16384 }, (_, index) => `export const value${index}: { count: number; text: string } = { count: ${index}, text: '日本語😀' };\n`).join("") });
const deep = "src/" + "deep-path/".repeat(10);
await fixture("host-paths", { [`${deep}日本語.ts`]: "export const 値 = 42;\n", "src/index.ts": `import { 値 } from './${"deep-path/".repeat(10)}日本語';\nexport const answer = 値;\nimport './missing';\n` });
const projects = {
    "tsconfig.json": '{"files":[],"references":[{"path":"lib"},{"path":"app"}]}',
    "lib/tsconfig.json": '{"compilerOptions":{"composite":true,"target":"es2022","lib":["es5"],"skipLibCheck":true,"outDir":"../out/lib"},"files":["index.ts"]}',
    "lib/index.ts": "export function answer(): number { return 1; }\n",
    "app/tsconfig.json": '{"compilerOptions":{"composite":true,"target":"es2022","lib":["es5"],"skipLibCheck":true,"outDir":"../out/app"},"references":[{"path":"../lib"}],"files":["index.ts"]}',
    "app/index.ts": "import { answer } from '../lib/index'; export const result: number = answer();\n",
};
await fixture("build", projects);
await json(path.join(directory, "inputs.json"), inputs);
const binaries = await fileHashes({ ...executables, ...!nativeAot ? { compiler: path.join(path.dirname(executables[backends[1]]), "TypeScript.Compiler.dll"), ...baseline ? { beforeCompiler: path.join(path.dirname(executables[backends[0]]), "TypeScript.Compiler.dll") } : {} } : {}, driver: path.join(root, "csharp/tools/performance-cli.mjs"), metrics: path.join(root, "csharp/tools/performance-metrics.ps1"), common: path.join(root, "csharp/tools/performance-common.mjs") });
const metrics = await Metrics.start(), activity = await metrics.call({ kind: "activity" }), own = [process.pid, metrics.child.pid];
const groups = [], controls = [];
async function artifacts(folder) {
    const result = {};
    async function visit(relative = "out") {
        let entries;
        try {
            entries = await readdir(path.join(folder, relative), { withFileTypes: true });
        }
        catch (error) {
            if (error.code === "ENOENT") return;
            throw error;
        }
        for (const entry of entries) {
            const file = path.posix.join(relative, entry.name);
            if (entry.isDirectory()) await visit(file);
            else if (!file.endsWith(".tsbuildinfo")) result[file] = sha256(await readFile(path.join(folder, file)));
        }
    }
    await visit();
    return Object.fromEntries(Object.entries(result).sort());
}
async function execute(backend, name, args, extraEnv = {}, command = executables[backend]) {
    const folder = fixtures[name][backend];
    const result = await metrics.call({ kind: "run", executable: command, arguments: args, directory: folder, environment: { ...env, ...extraEnv } });
    own.push(result.metrics.pid);
    if (["medium", "declarations", "tiny-files", "tiny-incremental", "huge-file", "build"].includes(name)) assert.equal(result.exitCode, 0, `${name}: ${result.stdout}${result.stderr}`);
    let stdout = result.stdout.replaceAll(folder.replaceAll("\\", "/"), "<workspace>").replaceAll(folder, "<workspace>").replaceAll("\r\n", "\n");
    stdout = stdout.replace(`Version ${packageManifest.version}`, "Version 7.1.0-dev");
    const ordinary = { exitCode: result.exitCode, stdout, stderr: result.stderr.replaceAll("\r\n", "\n"), files: await artifacts(folder) };
    return { ...result, ordinary, stdout: undefined, stderr: undefined };
}
async function compare(name, action, count = samples, warmups = 3) {
    if (!filter.test(name)) return;
    const raw = path.join(directory, name + ".jsonl");
    await writeFile(raw, "");
    const byIteration = new Map();
    const pairs = await paired(
        metrics,
        count,
        async (backend, iteration) => {
            const result = await action(backend, iteration);
            const other = byIteration.get(iteration);
            if (other) {
                assert.deepEqual(result.ordinary, other, `${name}: ordinary outputs`);
                byIteration.delete(iteration);
            }
            else byIteration.set(iteration, result.ordinary);
            result.outputSha256 = sha256(JSON.stringify(result.ordinary));
            delete result.ordinary;
            return result;
        },
        raw,
        warmups,
        backends,
    );
    const summarize = metric => summarizePairs(pairs, metric, backends);
    const summary = { name, elapsedMs: summarize(), cpuMs: summarize(result => result.metrics.cpuMs), peakRssBytes: summarize(result => result.metrics.peakRssBytes), rawSha256: sha256(await readFile(raw)), rawFile: raw };
    const margin = Math.max(0.05, controls[0].p95RelativePairDifference);
    summary.referenceNoiseMargin = margin;
    const confidence = summary.elapsedMs.pairedBootstrap95;
    summary[nativeAot ? "nativeAotMedianGate" : "coreclrMedianGate"] = confidence[1] <= 1 + margin ? "within margin" : confidence[0] > 1 + margin ? "regression" : "inconclusive";
    if (pairs.every(pair => backends.every(backend => pair[backend].firstOutputMs !== null))) summary.firstOutputMs = summarize(result => result.firstOutputMs);
    groups.push(summary);
    console.log(`${name}: ${backends[0]} ${summary.elapsedMs[backends[0]].median.toFixed(2)} ms, ${backends[1]} ${summary.elapsedMs[backends[1]].median.toFixed(2)} ms`);
}
try {
    // Identical reference controls establish startup/measurement noise before the candidate comparison.
    const raw = path.join(directory, "reference-noise.jsonl");
    await writeFile(raw, "");
    const noise = await paired(metrics, samples, backend => execute(backend, "empty", ["--version"], {}, executables[backends[0]]), raw, 3, backends);
    controls.push({ name: "same reference executable and arguments", elapsedMs: summarizePairs(noise, undefined, backends), p95RelativePairDifference: percentile(noise.filter(pair => !pair.contaminated).map(pair => Math.abs(pair[backends[0]].elapsedMs - pair[backends[1]].elapsedMs) / pair[backends[0]].elapsedMs), 0.95) });
    await compare("help", backend => execute(backend, "empty", ["--help"]));
    await compare("version", backend => execute(backend, "empty", ["--version"]));
    await compare("empty-project", backend => execute(backend, "empty", ["--project", ".", "--pretty", "false"]));
    await compare("small-first-diagnostic", backend => execute(backend, "small", ["--project", ".", "--pretty", "false"]));
    for (const name of ["medium", "declarations", "tiny-files", "huge-file", "host-paths"]) await compare(name, backend => execute(backend, name, ["--project", ".", "--pretty", "false"]));
    await compare("tiny-files-noemit", backend => execute(backend, "tiny-files", ["--project", ".", "--pretty", "false", "--noEmit"]));
    await compare("tiny-files-noemit-on-error", backend => execute(backend, "tiny-files", ["--project", ".", "--pretty", "false", "--noEmitOnError"]));
    await compare("tiny-files-incremental-edit", async (backend, iteration) => {
        for (let index = 0; index < 512; index++) await writeFile(path.join(fixtures["tiny-incremental"][backend], `src/f${index}.ts`), `export const v${index}: number = ${index + (iteration % 2 === 0 ? 1 : 0)};\n`);
        return execute(backend, "tiny-incremental", ["--project", ".", "--pretty", "false"]);
    });
    for (const checkers of [1, 2, 4, 8]) await compare(`medium-checkers-${checkers}`, backend => execute(backend, "medium", ["--project", ".", "--pretty", "false", "--checkers", String(checkers)]));
    await compare("medium-256m-heap-setting", backend => execute(backend, "medium", ["--project", ".", "--pretty", "false"], backend === "go" ? { GOMEMLIMIT: "256MiB" } : { DOTNET_GCHeapHardLimit: "10000000" }));
    await compare("project-references-clean", backend => execute(backend, "build", ["--build", "--force", "--pretty", "false"]));
    await compare("project-references-noop", backend => execute(backend, "build", ["--build", "--pretty", "false"]));
    for (const kind of ["implementation", "declaration", "config", "package"]) {
        await compare(`project-references-${kind}`, async (backend, iteration) => {
            const folder = fixtures.build[backend], value = iteration % 2 ? 2 : 1;
            if (kind === "implementation") await writeFile(path.join(folder, "lib/index.ts"), `export function answer(): number { return ${value}; }\n`);
            else if (kind === "declaration") await writeFile(path.join(folder, "lib/index.ts"), `export function answer(): number { return 1; }\nexport const extra${value} = ${value};\n`);
            else if (kind === "config") await writeFile(path.join(folder, "lib/tsconfig.json"), projects["lib/tsconfig.json"].replace('"composite":true', `"composite":true,"removeComments":${value === 1}`));
            else await writeFile(path.join(folder, "package.json"), JSON.stringify({ private: true, type: "module", version: `1.0.${value}` }));
            return execute(backend, "build", ["--build", "--pretty", "false"]);
        });
    }
    assert.deepEqual(await fileHashes(Object.fromEntries(Object.entries(binaries).map(([name, item]) => [name, item.file]))), binaries);
    const machineActivity = await activityReport(metrics, activity, own);
    await saveSummary(path.join(directory, "summary.json"), {
        referenceRevision,
        runtime: nativeAot ? "Release NativeAOT" : "Release CoreCLR",
        nativeExecuted: nativeAot,
        serverGC,
        binaries,
        packageManifest: packageManifest.sourceSha256,
        baselineManifest: baseline?.sourceSha256,
        comparison: backends,
        samples,
        inputsSha256: sha256(JSON.stringify(inputs)),
        controls,
        groups,
        machineActivity,
        machine: { os: os.version(), cpu: os.cpus()[0].model, logicalProcessors: os.availableParallelism(), totalMemory: os.totalmem() },
        environment: env,
        boundaries: ["Warm filesystem, fresh compiler processes; process creation included", "Win32 peak RSS and process CPU, measured using the same collector for both backends", "Files, diagnostics and emitted bytes compare exactly; package version header is mapped explicitly; runtime-specific tsbuildinfo version is excluded from emitted-language-artifact hashes", "Keyboard/mouse-contaminated pairs are preserved and replaced", baseline ? "The 256 MiB settings use the CLR GC heap hard limit for both builds" : "The 256 MiB settings have distinct runtime semantics: Go soft memory limit and CLR GC heap hard limit", "Synthetic stress fixtures complement the original checker corpus; they are not a benchmark of VS Code or another complete application"],
    });
}
finally {
    await metrics.close();
}
