import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import {
    mkdir,
    readFile,
    writeFile,
} from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { createInterface } from "node:readline";

const root = process.cwd();
const option = (name, fallback) => process.argv.includes(name) ? process.argv[process.argv.indexOf(name) + 1] : fallback;
const baselineDirectory = option("--baseline-directory");
const baselineExecutable = option("--baseline-executable");
const hasBaseline = Boolean(baselineDirectory || baselineExecutable);
const includeGo = process.argv.includes("--include-go");
const backends = hasBaseline ? ["before", "after", ...includeGo ? ["go"] : []] : ["go", "release"];
const output = path.resolve(option("--output-directory", hasBaseline ? "built/csharp/checker-workloads-comparison" : "built/csharp/checker-workloads"));
await mkdir(output, { recursive: true });
const manifestText = await readFile(option("--manifest", "csharp/compatibility/phase4-workloads.json"), "utf8");
const manifest = JSON.parse(manifestText);
const dotnet = option("--dotnet", process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, "dotnet.exe") : "dotnet");
const executable = option("--candidate-executable");
const nativeAot = process.argv.includes("--native-aot");
const tieredCompilation = !process.argv.includes("--tiering-off");
if (nativeAot && (!executable || hasBaseline && !baselineExecutable)) {
    throw Error("NativeAOT measurements require executables for each C# backend");
}
const serverGC = process.argv.includes("--server-gc");
const customInputs = option("--inputs") ? JSON.parse(await readFile(option("--inputs"), "utf8")) : null;
const dll = path.resolve(option("--candidate-directory", executable ? path.dirname(path.resolve(executable)) : "csharp/tests/TypeScript.Compatibility/bin/Release/net11.0"), "TypeScript.Compatibility.dll");
const compilerDll = path.join(path.dirname(dll), "TypeScript.Compiler.dll");
const oracle = path.resolve(option("--go-executable", path.join(root, "built/csharp/checker-workload-oracle.exe")));
const sha256 = value => createHash("sha256").update(value).digest("hex");
const env = { ...process.env,
    DOTNET_TieredCompilation: tieredCompilation ? "1" : "0", COMPlus_TieredCompilation: tieredCompilation ? "1" : "0",
    ...(serverGC ? { DOTNET_gcServer: "1" } : {}) };
delete env.DOTNET_PROCESSOR_COUNT;
delete env.COMPlus_ProcessorCount;
delete env.GOMAXPROCS;

function server(command, args) {
    const child = spawn(command, args, { cwd: root, env, windowsHide: true, stdio: ["pipe", "pipe", "pipe"] });
    let pending, error = "", closed = false;
    child.stderr.on("data", chunk => error += chunk.toString());
    const lines = createInterface({ input: child.stdout });
    lines.on("line", line => {
        const next = pending;
        pending = null;
        next?.resolve(JSON.parse(line));
    });
    child.on("error", failure => pending?.reject(failure));
    const exit = new Promise((resolve, reject) =>
        child.on("close", code => {
            closed = true;
            if (code) {
                const failure = Error(`${command} exited ${code}: ${error}`);
                pending?.reject(failure);
                reject(failure);
            }
            else resolve();
        })
    );
    return {
        request(input) {
            if (closed || pending) throw Error("Workload server is not ready");
            return new Promise((resolve, reject) => {
                pending = { resolve, reject };
                child.stdin.write(JSON.stringify(input) + "\n");
            });
        },
        async close() {
            child.stdin.end();
            await exit;
        },
    };
}

const samples = [];
for (const mode of manifest.modes) {
    const directory = path.join(root, "built/csharp/semantic-corpus-preemit-baseline", mode);
    const references = new Map(customInputs ? customInputs.map(input => [input.name, input]) :
        (await readFile(path.join(directory, "cases.jsonl"), "utf8")).trim().split(/\r?\n/).map(line => {
            const row = JSON.parse(line);
            return [row.name, row];
        }),
    );
    for (const name of manifest.cases) {
        const reference = references.get(name);
        if (!reference || reference.status !== "ready") {
            throw Error(`Workload has no active reference: ${name}`);
        }
        const diagnosticCount = reference.expected?.diagnosticCount ?? reference.semanticDiagnostics.length + reference.globalDiagnostics.length;
        const graphSha256 = reference.expected?.graphSha256 ?? sha256(reference.sources.map(s => `${s.file}\0${s.sha256}\n`).join(""));
        const input = { ...reference, blobDirectory: reference.blobDirectory ?? path.join(directory, "blobs"), singleThreaded: mode === "single" };
        const candidate = () => executable ? server(path.resolve(executable), ["--checker-workload-lines"]) : server(dotnet, [dll, "--checker-workload-lines"]);
        const processes = hasBaseline ? {
            before: baselineExecutable ? server(path.resolve(baselineExecutable), ["--checker-workload-lines"]) : server(dotnet, [path.resolve(baselineDirectory, "TypeScript.Compatibility.dll"), "--checker-workload-lines"]),
            after: candidate(),
            ...includeGo ? { go: server(oracle, []) } : {},
        } : { go: server(oracle, []), release: candidate() };
        try {
            for (let iteration = -manifest.warmups; iteration < manifest.samples; iteration++) {
                for (const backend of iteration % 2 ? backends.toReversed() : backends) {
                    const measurement = await processes[backend].request(input);
                    const sample = { name, mode, backend, iteration, inputSha256: sha256(JSON.stringify(input)), ...measurement };
                    samples.push(sample);
                    await writeFile(path.join(output, "samples.jsonl"), samples.map(s => JSON.stringify(s)).join("\n") + "\n");
                    if (measurement.graphSha256 !== graphSha256 || measurement.diagnosticCount !== diagnosticCount) {
                        throw Error(`Workload correctness differs: ${name} ${mode} ${backend}`);
                    }
                    if (backend !== "go" && serverGC && !measurement.serverGC) {
                        throw Error(`Server GC is not enabled: ${backend}`);
                    }
                }
            }
        }
        finally {
            for (const backend of backends) await processes[backend].close();
        }
        console.log(`${mode}: ${name}`);
    }
}

const percentile = (values, p) => values.toSorted((a, b) => a - b)[Math.ceil(values.length * p) - 1];
const groups = [];
for (const mode of manifest.modes) {
    for (const name of manifest.cases) {
        const group = { name, mode };
        for (const backend of backends) {
            const rows = samples.filter(s => s.name === name && s.mode === mode && s.backend === backend && s.iteration >= 0);
            group[backend] = {
                runtime: rows[0].runtime ?? "Go",
                serverGC: rows[0].serverGC ?? null,
                samples: rows.length,
                medianMs: percentile(rows.map(s => s.elapsedMs), 0.5),
                medianProgramMs: percentile(rows.map(s => s.programMs), 0.5),
                medianCheckerMs: percentile(rows.map(s => s.elapsedMs - s.programMs), 0.5),
                p95Ms: percentile(rows.map(s => s.elapsedMs), 0.95),
                medianCpuMs: percentile(rows.map(s => s.cpuMs), 0.5),
                medianAllocatedBytes: percentile(rows.map(s => s.allocatedBytes), 0.5),
                peakRssBytes: Math.max(...rows.map(s => s.peakRssBytes)),
                releasedHeapGrowthBytes: Math.max(0, ...rows.slice(1).map(s => s.releasedBytes - rows[0].releasedBytes)),
            };
        }
        const budgets = manifest.provisionalBudgets;
        const before = group[backends[0]], after = group[backends[1]];
        group.passed = after.p95Ms <= budgets.p95ElapsedMs && after.peakRssBytes <= budgets.peakRssBytes
            && after.releasedHeapGrowthBytes <= budgets.releasedHeapGrowthBytes;
        group.elapsedRatio = after.medianMs / before.medianMs;
        group.allocationRatio = after.medianAllocatedBytes / before.medianAllocatedBytes;
        groups.push(group);
    }
}
const summary = {
    timestamp: new Date().toISOString(),
    referenceRevision: manifest.referenceRevision,
    manifest,
    manifestSha256: sha256(manifestText),
    ...(customInputs ? { inputsSha256: sha256(JSON.stringify(customInputs)) } : {}),
    candidateSha256: sha256(await readFile(nativeAot ? executable : dll)),
    ...executable ? { executableSha256: sha256(await readFile(executable)) } : {},
    ...!nativeAot ? { compilerSha256: sha256(await readFile(compilerDll)) } : {},
    ...(hasBaseline ? {
        baseline: {
            ...baselineExecutable ? {
                executable: path.resolve(baselineExecutable),
                executableSha256: sha256(await readFile(baselineExecutable)),
                candidateSha256: sha256(await readFile(nativeAot ? baselineExecutable : path.join(path.dirname(baselineExecutable), "TypeScript.Compatibility.dll"))),
                ...!nativeAot ? { compilerSha256: sha256(await readFile(path.join(path.dirname(baselineExecutable), "TypeScript.Compiler.dll"))) } : {},
            } : {
                directory: path.resolve(baselineDirectory),
                candidateSha256: sha256(await readFile(path.join(baselineDirectory, "TypeScript.Compatibility.dll"))),
                compilerSha256: sha256(await readFile(path.join(baselineDirectory, "TypeScript.Compiler.dll"))),
            },
        },
    } : {}),
    ...!hasBaseline || includeGo ? { oracleSha256: sha256(await readFile(oracle)) } : {},
    samplesSha256: sha256(await readFile(path.join(output, "samples.jsonl"))),
    machine: { platform: process.platform, architecture: process.arch, os: os.version(), cpu: os.cpus()[0].model,
        logicalProcessors: os.cpus().length, availableParallelism: os.availableParallelism(), availableMemoryBytes: os.totalmem() },
    runtime: { ...!executable ? { dotnet } : {}, tieredCompilation: nativeAot ? null : tieredCompilation,
        processorCountOverride: null, nativeAotExecuted: nativeAot, serverGC, executable,
        label: option("--runtime-label", nativeAot ? "NativeAOT" : "CoreCLR") },
    groups,
    passed: groups.every(g => g.passed),
};
await writeFile(
    option(
        "--record",
        hasBaseline ? "csharp/compatibility/evidence/phase4-performance-workloads.json"
            : "csharp/compatibility/evidence/phase4-workloads.json",
    ),
    JSON.stringify(summary, null, 4) + "\n",
);
console.log(JSON.stringify(groups, null, 2));
if (!summary.passed) process.exitCode = 1;
