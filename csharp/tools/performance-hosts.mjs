import assert from "node:assert/strict";
import {
    mkdir,
    readdir,
    readFile,
    realpath,
    symlink,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import { pathToFileURL } from "node:url";
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
const directory = path.resolve(option("--directory", path.join(output, "phase8-performance/hosts")));
const manifest = JSON.parse(await readFile(option("--manifest", path.join(output, "distribution/coreclr-validation/manifest.json"))));
assert.equal(manifest.runtime, "coreclr-validation", "NativeAOT remains publish-only");
const executables = { go: path.resolve(option("--go-executable", path.join(output, "phase8-validation/cli-host-final/oracle.exe"))), csharp: path.join(manifest.platformDirectory, "lib", (manifest.executableName ?? "tsgo") + ".exe") };
const fixture = path.join(output, "content-mapper-fixture.exe");
const fixtureInfo = JSON.parse(await readFile(path.join(output, "content-mapper-fixture.json")));
assert.equal(sha256(await readFile(fixture)), fixtureInfo.executableSha256);
const env = { DOTNET_ROOT: option("--dotnet-root", process.env.DOTNET_ROOT ?? "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64"), DOTNET_TieredCompilation: "1", COMPlus_TieredCompilation: "1", DOTNET_gcServer: "0", COMPlus_gcServer: "0" };
Object.assign(process.env, env);
await mkdir(directory, { recursive: true });
const samples = Number(option("--samples", "21")), warmups = Number(option("--warmups", "20"));
const component = generation => `<component name="ProfileCard">\n<template><h1>{{ title }}</h1></template>\n<script lang="ts">\nexport const title: number = "Profile";\nexport const generation = ${generation};\n</script>`;
const mapperDirectory = path.join(directory, "mapper"), mapperFile = path.join(mapperDirectory, "ProfileCard.vue").replaceAll("\\", "/");
const mapperConfig = path.join(mapperDirectory, "tsconfig.json").replaceAll("\\", "/");
const mapperFiles = {
    "tsconfig.json": JSON.stringify({ compilerOptions: { target: "es2022", lib: ["es5"], strict: true, skipLibCheck: true }, contentMappers: [{ package: "mapper", extensions: [".vue"] }], files: ["ProfileCard.vue"] }),
    "node_modules/mapper/package.json": JSON.stringify({ name: "mapper", version: "1.0.0", typescript: { contentMapper: { exec: [fixture, "component-mapper"] } } }),
    "ProfileCard.vue": component(0),
};
for (const [file, text] of Object.entries(mapperFiles)) {
    const full = path.join(mapperDirectory, file);
    await mkdir(path.dirname(full), { recursive: true });
    await writeFile(full, text);
}
const junctionFiles = { "package.json": '{"type":"module"}', "tsconfig.json": JSON.stringify({ compilerOptions: { target: "es2022", module: "nodenext", lib: ["es5"], strict: true, skipLibCheck: true, declaration: true, rootDir: ".", outDir: "out" }, files: ["src/index.ts"] }), "src/index.ts": "import { value } from 'linked';\nexport const answer: number = value;\n", "real-package/package.json": '{"name":"linked","type":"module","types":"index.ts"}', "real-package/index.ts": "export const value: number = 42;\n" };
const folders = {};
for (const backend of ["go", "csharp"]) {
    const folder = path.join(directory, "junction", backend);
    folders[backend] = folder;
    for (const [file, text] of Object.entries(junctionFiles)) {
        const full = path.join(folder, file);
        await mkdir(path.dirname(full), { recursive: true });
        await writeFile(full, text);
    }
    const link = path.join(folder, "node_modules/linked");
    await mkdir(path.dirname(link), { recursive: true });
    try {
        await symlink(path.join(folder, "real-package"), link, "junction");
    }
    catch (error) {
        if (error.code !== "EEXIST") throw error;
    }
    assert.equal(await realpath(link), await realpath(path.join(folder, "real-package")));
}
await json(path.join(directory, "inputs.json"), { mapperFiles, junctionFiles });
if (process.argv.includes("--prepare-only")) {
    console.log(directory);
    process.exit(0);
}
const binaries = await fileHashes({ ...executables, compiler: path.join(path.dirname(executables.csharp), "TypeScript.Compiler.dll"), mapper: fixture, driver: path.join(root, "csharp/tools/performance-hosts.mjs"), metrics: path.join(root, "csharp/tools/performance-metrics.ps1"), common: path.join(root, "csharp/tools/performance-common.mjs") });
const metrics = await Metrics.start(), activity = await metrics.call({ kind: "activity" }), own = [process.pid, metrics.child.pid];
const groups = [], controls = [];
async function compare(name, action, count, warmupCount) {
    const raw = path.join(directory, name + ".jsonl");
    await writeFile(raw, "");
    const expected = new Map();
    const pairs = await paired(
        metrics,
        count,
        async (backend, iteration) => {
            const result = await action(backend, iteration);
            if (expected.has(iteration)) {
                assert.deepEqual(result.value, expected.get(iteration), name);
                expected.delete(iteration);
            }
            else expected.set(iteration, result.value);
            result.responseSha256 = sha256(JSON.stringify(result.value));
            delete result.value;
            return result;
        },
        raw,
        warmupCount,
    );
    groups.push({ name, elapsedMs: summarizePairs(pairs), rawFile: raw, rawSha256: sha256(await readFile(raw)) });
    console.log(`${name}: Go ${groups.at(-1).elapsedMs.go.median.toFixed(3)} ms, C# ${groups.at(-1).elapsedMs.csharp.median.toFixed(3)} ms`);
}
async function emitted(folder) {
    const result = {};
    async function visit(relative = "out") {
        for (const entry of await readdir(path.join(folder, relative), { withFileTypes: true })) {
            const file = path.posix.join(relative, entry.name);
            if (entry.isDirectory()) await visit(file);
            else result[file] = sha256(await readFile(path.join(folder, file)));
        }
    }
    await visit();
    assert.ok(Object.keys(result).some(file => file.endsWith(".js")));
    return Object.fromEntries(Object.entries(result).sort(([a], [b]) => a.localeCompare(b)));
}
async function api(backend, mode) {
    const base = backend === "go" ? path.join(root, "packages/typescript") : manifest.mainDirectory;
    const load = file => import(pathToFileURL(path.join(base, "dist", file)).href);
    const { API } = await load(`api/${mode}/api.js`);
    const instance = new API({ cwd: mapperDirectory, tsserverPath: executables[backend], runExternalCode: true });
    await instance.parseCommandLine([]);
    own.push((mode === "async" ? instance.client.process : instance.client.channel.child).pid);
    return instance;
}
async function mappedCheck(instance, generation) {
    const snapshot = await instance.createSnapshot({ openProject: mapperConfig, fileSystem: { kind: "layer", files: { [mapperFile]: component(generation) } } });
    try {
        const project = snapshot.getConfiguredProject(mapperConfig);
        assert.ok(project);
        const diagnostics = await project.program.getSemanticDiagnostics();
        assert.equal(diagnostics.length, 1);
        assert.equal(diagnostics[0].code, 2322);
        assert.equal(path.resolve(diagnostics[0].fileName).toLowerCase(), path.resolve(mapperFile).toLowerCase());
        assert.equal(component(generation).slice(diagnostics[0].pos, diagnostics[0].end), "title");
        return diagnostics.map(diagnostic => ({ ...diagnostic, fileName: "<component>" }));
    }
    finally {
        await snapshot.dispose();
        assert.equal(instance.activeSnapshots.size, 0);
    }
}
try {
    const noiseFile = path.join(directory, "noise.jsonl");
    await writeFile(noiseFile, "");
    const noise = await paired(metrics, samples, () => metrics.call({ kind: "run", executable: executables.go, arguments: ["--help"], directory, environment: env }), noiseFile, 3);
    controls.push({ name: "identical Go fresh processes", elapsedMs: summarizePairs(noise), p95RelativePairDifference: percentile(noise.filter(pair => !pair.contaminated).map(pair => Math.abs(pair.go.elapsedMs - pair.csharp.elapsedMs) / pair.go.elapsedMs), 0.95), rawFile: noiseFile, rawSha256: sha256(await readFile(noiseFile)) });
    for (const preserve of [false, true]) {
        await compare(
            `junction-preserve-${preserve}`,
            async backend => {
                const result = await metrics.call({ kind: "run", executable: executables[backend], arguments: ["--project", folders[backend], "--pretty", "false", "--preserveSymlinks", String(preserve), "--listFiles"], directory: folders[backend], environment: env });
                assert.equal(result.exitCode, 0, `${backend}: ${result.stdout}${result.stderr}`);
                assert.equal(result.stderr, "");
                const workspace = folders[backend].replaceAll("\\", "/").toLowerCase() + "/";
                const library = path.join(manifest.platformDirectory, "lib").replaceAll("\\", "/").toLowerCase() + "/";
                const files = result.stdout.trim().split(/\r?\n/).map(file => {
                    file = file.replaceAll("\\", "/");
                    const root = backend === "go" ? "bundled:///libs/" : library;
                    if (file.toLowerCase().startsWith(root)) {
                        const name = file.slice(root.length);
                        assert.match(name, /^lib[.a-z0-9-]*\.d\.ts$/);
                        return "<library>/" + name;
                    }
                    assert.ok(file.toLowerCase().startsWith(workspace), `Unexpected source path: ${file}`);
                    return file.slice(workspace.length);
                });
                const expected = preserve ? "node_modules/linked/index.ts" : "real-package/index.ts";
                assert.ok(files.includes(expected), `Junction resolution must follow preserveSymlinks=${preserve}`);
                assert.ok(!files.includes(preserve ? "real-package/index.ts" : "node_modules/linked/index.ts"));
                return { ...result, value: { files, emitted: await emitted(folders[backend]) } };
            },
            samples,
            3,
        );
    }
    for (const mode of ["async", "sync"]) {
        const sessions = { go: await api("go", mode), csharp: await api("csharp", mode) };
        try {
            let generation = 1_000_000;
            const raw = path.join(directory, `noise-mapper-${mode}.jsonl`);
            await writeFile(raw, "");
            const noise = await paired(
                metrics,
                samples,
                async () => {
                    const start = performance.now();
                    await mappedCheck(sessions.go, generation++);
                    return { elapsedMs: performance.now() - start };
                },
                raw,
                warmups,
            );
            controls.push({ name: `identical Go mapper ${mode} requests`, elapsedMs: summarizePairs(noise), p95RelativePairDifference: percentile(noise.filter(pair => !pair.contaminated).map(pair => Math.abs(pair.go.elapsedMs - pair.csharp.elapsedMs) / pair.go.elapsedMs), 0.95), rawFile: raw, rawSha256: sha256(await readFile(raw)) });
            await compare(
                `mapper-${mode}-snapshot-check-release`,
                async (backend, iteration) => {
                    const started = performance.now(), value = await mappedCheck(sessions[backend], iteration);
                    return { elapsedMs: performance.now() - started, value };
                },
                samples,
                warmups,
            );
        }
        finally {
            await sessions.go.close();
            await sessions.csharp.close();
        }
    }
    assert.deepEqual(await fileHashes(Object.fromEntries(Object.entries(binaries).map(([name, item]) => [name, item.file]))), binaries);
    await saveSummary(path.join(directory, "summary.json"), { referenceRevision, runtime: "Release CoreCLR", nativeExecuted: false, binaries, samples, warmups, controls, groups, inputsSha256: sha256(JSON.stringify({ mapperFiles, junctionFiles })), machineActivity: await activityReport(metrics, activity, own), boundaries: ["Real Windows directory junctions with preserveSymlinks disabled and enabled; ordered file graphs and emitted JavaScript/declarations match; graph paths label only verified library and fixture roots", "Trusted external mapper uses the pinned reference test fixture, not a Go compiler fallback", "Mapped diagnostics compare completely after verifying and labeling the component path; an expected error and original-source span prove the mapper executed", "Changing independent filesystem layers exercise both API transports; every snapshot is released", "Fresh CLI processes use a warm filesystem cache; mapper measurements use warm API servers"] });
}
finally {
    await metrics.close();
}
