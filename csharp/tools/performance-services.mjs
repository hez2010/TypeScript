import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { appendFile, mkdir, readFile, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { performance } from "node:perf_hooks";
import { json, output, referenceRevision, root, run, sha256 } from "./common.mjs";
import { Metrics, activityReport, fileHashes, paired, percentile, saveSummary, summarizePairs } from "./performance-common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const directory = path.resolve(option("--directory", path.join(output, "phase8-performance/services")));
const manifest = JSON.parse(await readFile(option("--manifest", path.join(output, "phase8-final-managed2/manifest.json"))));
assert.equal(manifest.runtime, "coreclr-validation");
const executables = { go: path.resolve(option("--go-executable", path.join(output, "phase8-validation/cli-host-final/oracle.exe"))), csharp: path.join(manifest.platformDirectory, "lib/tsgo.exe") };
const clients = { go: path.join(root, "packages/typescript"), csharp: manifest.mainDirectory };
const samples = Number(option("--samples", "101")), warmups = Number(option("--warmups", "20"));
const soakMinutes = Number(option("--soak-minutes", "0"));
const env = { ...process.env, DOTNET_ROOT: option("--dotnet-root", process.env.DOTNET_ROOT ?? "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64"),
    DOTNET_TieredCompilation: "1", COMPlus_TieredCompilation: "1", DOTNET_gcServer: "0", COMPlus_gcServer: "0" };
// The API client owns child creation, so set its inherited environment before constructing it.
Object.assign(process.env, { DOTNET_ROOT: env.DOTNET_ROOT, DOTNET_TieredCompilation: "1", COMPlus_TieredCompilation: "1", DOTNET_gcServer: "0", COMPlus_gcServer: "0" });
await mkdir(directory, { recursive: true });
const protocol = createRequire(import.meta.url)("vscode-languageserver-protocol/node");
const metrics = await Metrics.start(), own = [process.pid, metrics.child.pid], activity = await metrics.call({ kind: "activity" });
const binaries = await fileHashes({ ...executables, compiler: path.join(path.dirname(executables.csharp), "TypeScript.Compiler.dll"),
    driver: path.join(root, "csharp/tools/performance-services.mjs"), metrics: path.join(root, "csharp/tools/performance-metrics.ps1"), common: path.join(root, "csharp/tools/performance-common.mjs") });
const groups = [], controls = [], transfers = [], retention = [];
const document = value => `export const café = { value: ${value}, label: "世界 😀" };\nexport const used = café.value;\ncafé.value;\n`;
const apiText = Array.from({ length: 64 }, (_, index) => `export const café${index}: number = ${index};\n`).join("");
const virtualFiles = { "/p/tsconfig.json": '{"compilerOptions":{"lib":["es5"],"strict":true,"skipLibCheck":true},"files":["index.ts"]}',
    "/p/index.ts": apiText, "/p/callback.ts": apiText };
await json(path.join(directory, "inputs.json"), { document: document(1), virtualFiles });
async function measure(action) { const start = performance.now(); let elapsedMs; const value = await action(() => elapsedMs = performance.now() - start); return { elapsedMs: elapsedMs ?? performance.now() - start, value }; }
async function compare(name, action, count = samples) {
    const raw = path.join(directory, name + ".jsonl"); await writeFile(raw, "");
    const values = new Map();
    const pairs = await paired(metrics, count, async (backend, iteration) => {
        const result = await measure(stop => action(backend, iteration, stop));
        if (values.has(iteration)) { assert.deepEqual(result.value, values.get(iteration), `${name}: response contract`); values.delete(iteration); }
        else values.set(iteration, result.value);
        result.responseSha256 = sha256(JSON.stringify(result.value)); delete result.value; return result;
    }, raw, warmups);
    const summary = { name, elapsedMs: summarizePairs(pairs), rawSha256: sha256(await readFile(raw)), rawFile: raw };
    if (controls.length) {
        const margin = Math.max(0.05, controls[0].p95RelativePairDifference), confidence = summary.elapsedMs.pairedBootstrap95;
        summary.referenceNoiseMargin = margin;
        summary.coreclrMedianGate = confidence[1] <= 1 + margin ? "within margin" : confidence[0] > 1 + margin ? "regression" : "inconclusive";
    }
    groups.push(summary); console.log(`${name}: Go ${summary.elapsedMs.go.median.toFixed(3)} ms, C# ${summary.elapsedMs.csharp.median.toFixed(3)} ms`);
}
async function lsp(backend) {
    const folder = path.join(directory, "lsp", backend); await mkdir(folder, { recursive: true });
    await writeFile(path.join(folder, "tsconfig.json"), '{"compilerOptions":{"target":"es2022","lib":["es5"],"strict":true,"skipLibCheck":true}}');
    const child = spawn(executables[backend], ["--lsp", "--stdio", `--clientProcessId=${process.pid}`], { cwd: folder, windowsHide: true, env }); own.push(child.pid);
    let stderr = ""; child.stderr.on("data", data => stderr += data);
    const exit = new Promise((resolve, reject) => { child.once("error", reject); child.once("close", (code, signal) => resolve({ code, signal, stderr })); });
    const connection = protocol.createMessageConnection(new protocol.StreamMessageReader(child.stdout), new protocol.StreamMessageWriter(child.stdin));
    if (process.argv.includes("--trace")) connection.trace(protocol.Trace.Verbose, { log: (message, data) => { void appendFile(path.join(directory, backend + ".wire.log"), message + "\n" + (data ?? "") + "\n"); } });
    connection.onRequest("client/registerCapability", () => null);
    connection.onRequest("workspace/diagnostic/refresh", () => null);
    connection.onRequest("window/workDoneProgress/create", () => null);
    connection.listen();
    const timeout = setTimeout(() => child.kill(), Math.max(30, soakMinutes + 5) * 60 * 1000);
    const uri = pathToFileURL(path.join(folder, "index.ts")).href;
    let version = 1;
    const request = async (method, params, token) => {
        let timeout;
        const pending = token ? connection.sendRequest(method, params, token)
            : params === undefined ? connection.sendRequest(method) : connection.sendRequest(method, params);
        try { return await Promise.race([pending, new Promise((_, reject) => timeout = setTimeout(() => { child.kill(); reject(new Error(`${backend}: ${method} exceeded 30 seconds; stderr: ${stderr}`)); }, 30000))]); }
        finally { clearTimeout(timeout); }
    };
    const notify = (method, params) => params === undefined ? connection.sendNotification(method) : connection.sendNotification(method, params);
    await request("initialize", { processId: process.pid, rootUri: pathToFileURL(folder).href,
        capabilities: { general: { positionEncodings: ["utf-16"] }, textDocument: { diagnostic: {} } } });
    await notify("initialized", {});
    await notify("textDocument/didOpen", { textDocument: { uri, languageId: "typescript", version, text: document(1) } });
    return { child, request, notify, uri, folder,
        position: character => ({ textDocument: { uri }, position: { line: 2, character } }),
        trace() { return connection.trace(protocol.Trace.Verbose, { log: (message, data) => { void appendFile(path.join(directory, backend + ".cleanup.log"), message + "\n" + (data ?? "") + "\n"); } }); },
        async change(value) { await notify("textDocument/didChange", { textDocument: { uri, version: ++version }, contentChanges: [{ text: document(value) }] }); },
        async diagnostic() { const value = await request("textDocument/diagnostic", { textDocument: { uri } }); assert.equal(value.kind, "full"); return value.items; },
        async close() { try { if (child.exitCode === null) { await request("shutdown"); await notify("exit"); } const result = await exit; assert.ok(result.code === 0 || backend === "go" && result.code === 1 && /context canceled/.test(result.stderr)); }
            finally { clearTimeout(timeout); connection.dispose(); if (child.exitCode === null) child.kill(); await exit; } },
    };
}
async function api(backend, mode, collectTiming = false) {
    const base = clients[backend], load = relative => import(pathToFileURL(path.join(base, "dist", relative)).href);
    const { API } = await load(`api/${mode}/api.js`), { createVirtualFileSystem } = await load("api/fs.js");
    const instance = new API({ cwd: root, tsserverPath: executables[backend], fs: createVirtualFileSystem(virtualFiles), collectTiming });
    await instance.parseCommandLine(["--strict"]);
    const child = mode === "async" ? instance.client.process : instance.client.channel.child;
    assert.ok(child?.pid); own.push(child.pid);
    return { instance, child };
}
async function soak() {
    const sessions = [], captures = [], started = performance.now();
    const intervalMs = Number(option("--soak-interval-ms", "1000"));
    const captureIntervalMs = Number(option("--soak-capture-ms", "600000"));
    const raw = path.join(directory, "soak.jsonl"); await writeFile(raw, "");
    const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
    async function capture(iteration) {
        global.gc?.();
        for (const session of sessions) {
            const profileDirectory = path.join(directory, "heap", `${session.backend}-${session.mode}`, String(iteration));
            const file = session.mode === "lsp" ? (await session.host.request("custom/saveHeapProfile", { dir: profileDirectory })).file
                : await session.host.instance.internal.saveHeapProfile(profileDirectory);
            const profile = await run(go, ["tool", "pprof", "-raw", file]);
            await writeFile(file + ".txt", profile);
            const table = profile.split(/Samples:\r?\n/)[1].split(/\r?\nLocations/)[0].trim().split(/\r?\n/);
            const columns = table.shift().trim().split(/\s+/), index = columns.findIndex(value => value.startsWith("inuse_space/bytes"));
            assert.ok(index >= 0, "Heap profile exposes in-use bytes");
            let heapBytes = 0, records = 0;
            for (const line of table) {
                const match = /^\s*(-?\d+(?:\s+-?\d+)*)\s*:/.exec(line); if (!match) continue;
                heapBytes += Number(match[1].trim().split(/\s+/)[index]); records++;
            }
            assert.ok(records > 0 && heapBytes > 0);
            const record = { iteration, elapsedMs: performance.now() - started, backend: session.backend, mode: session.mode, heapBytes,
                metrics: await metrics.call({ kind: "observe", processId: session.host.child.pid }), profile: file, profileSha256: sha256(await readFile(file)) };
            captures.push(record); await appendFile(raw, JSON.stringify(record) + "\n");
        }
        console.log(`Retention: ${iteration} cycles, ${((performance.now() - started) / 60000).toFixed(1)} minutes, ${captures.length} heap captures`);
    }
    let iteration = 0;
    try {
        for (const backend of ["go", "csharp"]) {
            sessions.push({ backend, mode: "lsp", host: await lsp(backend) });
            for (const mode of ["async", "sync"]) sessions.push({ backend, mode, host: await api(backend, mode) });
        }
        await capture(0);
        let nextCapture = performance.now() + captureIntervalMs;
        while (performance.now() - started < soakMinutes * 60000) {
            iteration++;
            for (const session of sessions) {
                if (session.mode === "lsp") {
                    await session.host.change(iteration); assert.deepEqual(await session.host.diagnostic(), []);
                    assert.ok(await session.host.request("textDocument/hover", session.host.position(2)));
                }
                else {
                    const instance = session.host.instance, text = apiText + `export const generation = ${iteration};\n`;
                    const snapshot = await instance.createSnapshot({ openProject: "/p/tsconfig.json", fileSystem: { kind: "layer", files: { "/p/index.ts": text } } });
                    try {
                        const program = snapshot.getConfiguredProject("/p/tsconfig.json").program;
                        const file = await program.getSourceFile("/p/index.ts"); assert.equal(file.text, text);
                        assert.equal(await program.getSourceFile("/p/index.ts"), file);
                        assert.deepEqual(await program.getSemanticDiagnostics(), []);
                    } finally { await snapshot.dispose(); }
                    assert.equal(instance.activeSnapshots.size, 0);
                }
            }
            if (performance.now() >= nextCapture) { await capture(iteration); nextCapture = performance.now() + captureIntervalMs; }
            await new Promise(resolve => setTimeout(resolve, intervalMs));
        }
        await capture(iteration);
        const results = sessions.map(session => {
            const series = captures.filter(value => value.backend === session.backend && value.mode === session.mode);
            const settled = series.slice(Math.min(2, Math.max(0, series.length - 2)));
            return { backend: session.backend, mode: session.mode, measurements: series.length, heapGrowthBytes: settled.at(-1).heapBytes - settled[0].heapBytes,
                minimumHeapBytes: Math.min(...settled.map(value => value.heapBytes)), maximumHeapBytes: Math.max(...settled.map(value => value.heapBytes)),
                finalPrivateBytes: settled.at(-1).metrics.privateBytes };
        });
        return { requestedMinutes: soakMinutes, elapsedMs: performance.now() - started, iterations: iteration, sessions: sessions.length,
            results, rawFile: raw, rawSha256: sha256(await readFile(raw)), captures,
            contract: "Changing documents and independent API filesystem layers; every request checked and every snapshot released. Both heap profiles force collection. Go in-use heap is sampled; CLR in-use heap is the actual whole-process managed total. Initial two captures are excluded from settled growth." };
    } finally { for (const session of sessions) { if (session.mode === "lsp") await session.host.close(); else await session.host.instance.close(); } }
}
try {
    if (!process.argv.includes("--soak-only")) {
    await compare("lsp-open-to-diagnostics", async (backend, iteration, stop) => {
        const server = await lsp(backend);
        try { const result = await server.diagnostic(); stop(); return result; } finally { await server.close(); }
    }, Math.min(samples, 21));
    const servers = { go: await lsp("go"), csharp: await lsp("csharp") };
    try {
        for (const server of Object.values(servers)) assert.deepEqual(await server.diagnostic(), []);
        const noiseFile = path.join(directory, "reference-noise.jsonl"); await writeFile(noiseFile, "");
        const noise = await paired(metrics, samples, async () => {
            const start = performance.now(); await servers.go.request("textDocument/hover", servers.go.position(2));
            return { elapsedMs: performance.now() - start };
        }, noiseFile, warmups);
        controls.push({ name: "identical warm Go hover requests", elapsedMs: summarizePairs(noise),
            p95RelativePairDifference: percentile(noise.filter(pair => !pair.contaminated).map(pair => Math.abs(pair.go.elapsedMs - pair.csharp.elapsedMs) / pair.go.elapsedMs), 0.95) });
        await compare("lsp-hover", async backend => { const server = servers[backend]; return await server.request("textDocument/hover", server.position(2)); });
        await compare("lsp-completion", async backend => {
            const server = servers[backend], response = await server.request("textDocument/completion", server.position(5));
            const items = response.items ?? response;
            return items.map(item => ({ label: item.label, kind: item.kind, sortText: item.sortText, insertText: item.insertText, textEdit: item.textEdit }));
        });
        await compare("lsp-rename", async backend => {
            const server = servers[backend], response = await server.request("textDocument/rename", { ...server.position(2), newName: "renamed" });
            assert.deepEqual(Object.keys(response), ["changes"]);
            const changes = Object.entries(response.changes); assert.equal(changes.length, 1);
            const uri = new URL(changes[0][0]); assert.equal(uri.protocol, "file:");
            const file = decodeURIComponent(uri.pathname).replace(/^\/(?=[a-z]:\/)/i, "");
            assert.equal(path.normalize(file).toLowerCase(), path.join(server.folder, "index.ts").toLowerCase());
            return { changes: { "<document>": changes[0][1] } };
        });
        await compare("lsp-edit-diagnostics", async (backend, iteration) => { const server = servers[backend]; await server.change(iteration % 2 ? 1 : 2); return await server.diagnostic(); });
        await compare("lsp-concurrent-16-hovers", async backend => {
            const server = servers[backend]; return await Promise.all(Array.from({ length: 16 }, () => server.request("textDocument/hover", server.position(2))));
        });
        // Cancellation races may finish before the cancellation is delivered. Record both outcomes; any other failure is an error.
        const cancellation = {};
        for (const backend of ["go", "csharp"]) {
            const server = servers[backend], source = new protocol.CancellationTokenSource();
            const large = Array.from({ length: 8192 }, (_, index) => `export const item${index}: number = ${index};\n`).join("");
            await server.notify("textDocument/didChange", { textDocument: { uri: server.uri, version: 100000 }, contentChanges: [{ text: large }] });
            const preceding = server.diagnostic();
            const start = performance.now();
            const requests = Array.from({ length: 64 }, () => server.request("textDocument/hover", { textDocument: { uri: server.uri }, position: { line: 0, character: 15 } }, source.token)
                .then(() => "completed", error => { assert.equal(error.code, -32800); return "cancelled"; }));
            source.cancel(); const results = await Promise.all(requests); source.dispose();
            assert.deepEqual(await preceding, []);
            cancellation[backend] = { elapsedMs: performance.now() - start, cancelled: results.filter(value => value === "cancelled").length, completed: results.filter(value => value === "completed").length };
        }
        const cancelFile = path.join(directory, "active-cancellation.jsonl"); await writeFile(cancelFile, "");
        const cancelVersions = { go: 100001, csharp: 100001 };
        const large = Array.from({ length: 16384 }, (_, index) => `export const item${index}: number = ${index};\n`).join("");
        const cancelled = await paired(metrics, Math.min(samples, 21), async (backend, iteration) => {
            const server = servers[backend], text = large + `// edit ${iteration}\n`;
            await server.notify("textDocument/didChange", { textDocument: { uri: server.uri, version: cancelVersions[backend]++ }, contentChanges: [{ text }] });
            const token = new protocol.CancellationTokenSource();
            const pending = server.request("textDocument/diagnostic", { textDocument: { uri: server.uri } }, token.token)
                .then(() => "completed", error => { assert.equal(error.code, -32800); return "cancelled"; });
            await new Promise(resolve => setTimeout(resolve, 5));
            const cancelledAt = performance.now(); token.cancel();
            const outcome = await pending, elapsedMs = performance.now() - cancelledAt; token.dispose();
            assert.deepEqual(await server.diagnostic(), [], "The session recovers after cancellation");
            return { outcome, elapsedMs };
        }, cancelFile);
        const comparable = cancelled.filter(pair => pair.go.outcome === "cancelled" && pair.csharp.outcome === "cancelled");
        assert.ok(comparable.some(pair => !pair.contaminated), "The active-request probe must observe real cancellation in both backends");
        cancellation.active = { afterCancelMs: summarizePairs(comparable), totalPairs: cancelled.length, bothCancelled: comparable.length,
            rawFile: cancelFile, rawSha256: sha256(await readFile(cancelFile)), cancellationDelayMs: 5 };
        await json(path.join(directory, "cancellation.json"), cancellation);
        for (const backend of ["go", "csharp"]) {
            await servers[backend].trace();
            console.log(`${backend}: collect post-cancellation memory`);
            await servers[backend].request("custom/runGC");
            retention.push({ backend, kind: "lsp-after-requests", metrics: await metrics.call({ kind: "observe", processId: servers[backend].child.pid }) });
            console.log(`${backend}: post-cancellation memory collected`);
        }
    } finally { await servers.go.close(); await servers.csharp.close(); }
    for (const mode of ["async", "sync"]) {
        const sessions = { go: await api("go", mode), csharp: await api("csharp", mode) };
        try {
            await compare(`api-${mode}-command-request`, async backend => (await sessions[backend].instance.parseCommandLine(["--strict", "--noEmit"])).options);
            await compare(`api-${mode}-batch-20`, async backend => {
                const instance = sessions[backend].instance;
                if (mode === "sync") return instance.batch(...Array.from({ length: 20 }, () => instance.parseCommandLine.gen(["--strict"]))).map(value => value.options);
                const batch = instance.batchContext(), requests = Array.from({ length: 20 }, () => instance.parseCommandLine(["--strict"]));
                batch[Symbol.dispose](); return (await Promise.all(requests)).map(value => value.options);
            });
            function astSummary(file) {
                let nodes = 0; const visit = node => { nodes++; node.forEachChild(child => { visit(child); }); }; visit(file);
                return { nodes, statements: file.statements.length, text: file.text, last: file.statements.at(-1).getText() };
            }
            await compare(`api-${mode}-ast-encode-decode`, async (backend, iteration) => astSummary(await sessions[backend].instance.createSourceFile(`/p/generated${iteration}.ts`, apiText)));
            await compare(`api-${mode}-filesystem-callback`, async backend => astSummary(await sessions[backend].instance.createSourceFileFromFile("/p/callback.ts")));
            await compare(`api-${mode}-snapshot-check-release`, async backend => {
                const snapshot = await sessions[backend].instance.createSnapshot({ openProject: "/p/tsconfig.json" });
                try {
                    const program = snapshot.getConfiguredProject("/p/tsconfig.json").program;
                    const first = await program.getSourceFile("/p/index.ts"), second = await program.getSourceFile("/p/index.ts");
                    assert.equal(first, second, "The public client preserves source-file identity within a snapshot");
                    return await program.getSemanticDiagnostics();
                } finally { await snapshot.dispose(); }
            }, Math.min(samples, 31));
            for (const backend of ["go", "csharp"]) retention.push({ backend, kind: `api-${mode}-after-release`, metrics: await metrics.call({ kind: "observe", processId: sessions[backend].child.pid }) });
        } finally { await sessions.go.instance.close(); await sessions.csharp.instance.close(); }
        for (const backend of ["go", "csharp"]) {
            const { instance } = await api(backend, mode, true);
            try { const file = await instance.createSourceFile("/p/measured.ts", apiText); for (const statement of file.statements) statement.getText();
                transfers.push({ backend, mode, timing: await instance.getTimingInfo() }); }
            finally { await instance.close(); }
        }
    }
    }
    const soakResult = soakMinutes > 0 ? await soak() : undefined;
    assert.deepEqual(await fileHashes(Object.fromEntries(Object.entries(binaries).map(([name, item]) => [name, item.file]))), binaries);
    await saveSummary(path.join(directory, "summary.json"), { referenceRevision, nativeExecuted: false, runtime: "Release CoreCLR", binaries, samples, warmups, controls, groups, transfers, retention, soak: soakResult,
        machineActivity: await activityReport(metrics, activity, own),
        boundaries: ["Original Go version-8 client and generated C# version-9 client", "Warm RPC latencies include client, transport and server work; AST traversal materializes every node",
            "API transfer counters are collected in separate instrumented sessions", "Cancellation is checked independently from timing pairs",
            "Completion checks labels, kinds, insertion edits and sort keys; the full client/protocol regression gates cover remaining fields",
            "Short-run process memory snapshots do not establish hours-long retention"] });
} finally { await metrics.close(); }
