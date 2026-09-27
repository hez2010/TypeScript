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
const output = path.join(root, "built/csharp/checker-workloads");
await mkdir(output, { recursive: true });
const manifestText = await readFile("csharp/compatibility/phase4-workloads.json", "utf8");
const manifest = JSON.parse(manifestText);
const dotnet = "D:/dotnet-sdk-11.0.100-rc.2.26470.103-win-x64/dotnet.exe";
const dll = path.join(root, "csharp/tests/TypeScript.Compatibility/bin/Release/net11.0/TypeScript.Compatibility.dll");
const compilerDll = path.join(path.dirname(dll), "TypeScript.Compiler.dll");
const oracle = path.join(root, "built/csharp/checker-workload-oracle.exe");
const sha256 = value => createHash("sha256").update(value).digest("hex");
const env = { ...process.env, DOTNET_PROCESSOR_COUNT: String(manifest.processorCount), GOMAXPROCS: String(manifest.processorCount), DOTNET_TieredCompilation: "0" };

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
    const references = new Map(
        (await readFile(path.join(directory, "cases.jsonl"), "utf8")).trim().split(/\r?\n/).map(line => {
            const row = JSON.parse(line);
            return [row.name, row];
        }),
    );
    for (const name of manifest.cases) {
        const reference = references.get(name);
        if (!reference || reference.status !== "ready" || reference.semanticDiagnostics.length || reference.globalDiagnostics.length) {
            throw Error(`Workload must be diagnostic-free: ${name}`);
        }
        const graphSha256 = sha256(reference.sources.map(s => `${s.file}\0${s.sha256}\n`).join(""));
        const input = { ...reference, blobDirectory: path.join(directory, "blobs"), singleThreaded: mode === "single" };
        const processes = { go: server(oracle, []), release: server(dotnet, [dll, "--checker-workload-lines"]) };
        try {
            for (let iteration = -manifest.warmups; iteration < manifest.samples; iteration++) {
                for (const backend of iteration % 2 ? ["release", "go"] : ["go", "release"]) {
                    const measurement = await processes[backend].request(input);
                    const sample = { name, mode, backend, iteration, inputSha256: sha256(JSON.stringify(input)), ...measurement };
                    samples.push(sample);
                    await writeFile(path.join(output, "samples.jsonl"), samples.map(s => JSON.stringify(s)).join("\n") + "\n");
                    if (measurement.graphSha256 !== graphSha256 || measurement.diagnosticCount !== 0) {
                        throw Error(`Workload correctness differs: ${name} ${mode} ${backend}`);
                    }
                }
            }
        }
        finally {
            await processes.go.close();
            await processes.release.close();
        }
        console.log(`${mode}: ${name}`);
    }
}

const percentile = (values, p) => values.toSorted((a, b) => a - b)[Math.ceil(values.length * p) - 1];
const groups = [];
for (const mode of manifest.modes) {
    for (const name of manifest.cases) {
        const group = { name, mode };
        for (const backend of ["go", "release"]) {
            const rows = samples.filter(s => s.name === name && s.mode === mode && s.backend === backend && s.iteration >= 0);
            group[backend] = {
                samples: rows.length,
                medianMs: percentile(rows.map(s => s.elapsedMs), 0.5),
                p95Ms: percentile(rows.map(s => s.elapsedMs), 0.95),
                medianCpuMs: percentile(rows.map(s => s.cpuMs), 0.5),
                medianAllocatedBytes: percentile(rows.map(s => s.allocatedBytes), 0.5),
                peakRssBytes: Math.max(...rows.map(s => s.peakRssBytes)),
                releasedHeapGrowthBytes: Math.max(0, ...rows.slice(1).map(s => s.releasedBytes - rows[0].releasedBytes)),
            };
        }
        const budgets = manifest.provisionalBudgets;
        group.passed = group.release.p95Ms <= budgets.p95ElapsedMs && group.release.peakRssBytes <= budgets.peakRssBytes
            && group.release.releasedHeapGrowthBytes <= budgets.releasedHeapGrowthBytes;
        group.elapsedRatio = group.release.medianMs / group.go.medianMs;
        groups.push(group);
    }
}
const summary = {
    timestamp: new Date().toISOString(),
    referenceRevision: manifest.referenceRevision,
    manifest,
    manifestSha256: sha256(manifestText),
    candidateSha256: sha256(await readFile(dll)),
    compilerSha256: sha256(await readFile(compilerDll)),
    oracleSha256: sha256(await readFile(oracle)),
    samplesSha256: sha256(await readFile(path.join(output, "samples.jsonl"))),
    machine: { platform: process.platform, architecture: process.arch, os: os.version(), cpu: os.cpus()[0].model, availableMemoryBytes: os.totalmem() },
    runtime: { dotnet, tieredCompilation: false, processorCount: manifest.processorCount, nativeAotExecuted: false },
    groups,
    passed: groups.every(g => g.passed),
};
await writeFile("csharp/compatibility/evidence/phase4-workloads.json", JSON.stringify(summary, null, 4) + "\n");
console.log(JSON.stringify(groups, null, 2));
if (!summary.passed) process.exitCode = 1;
