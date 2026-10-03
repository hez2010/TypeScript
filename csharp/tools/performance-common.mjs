import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { appendFile, readFile } from "node:fs/promises";
import { createInterface } from "node:readline";
import path from "node:path";
import { json, root, sha256 } from "./common.mjs";

export class Metrics {
    static async start() {
        const metrics = new Metrics();
        await metrics.ready;
        for (let index = 0; index < 3; index++) await metrics.call({ kind: "idle" });
        return metrics;
    }
    constructor() {
        this.child = spawn("pwsh", ["-NoProfile", "-NonInteractive", "-File", path.join(root, "csharp/tools/performance-metrics.ps1")], { windowsHide: true });
        this.stderr = ""; this.child.stderr.on("data", data => this.stderr += data);
        this.ready = new Promise((resolve, reject) => { this.next = { resolve, reject }; });
        createInterface({ input: this.child.stdout }).on("line", line => {
            const next = this.next; this.next = null;
            try { const result = JSON.parse(line); if (result.error) next.reject(new Error(result.error)); else next.resolve(result); }
            catch (error) { next?.reject(error); }
        });
        this.exit = new Promise((resolve, reject) => {
            this.child.once("error", error => { this.next?.reject(error); reject(error); });
            this.child.once("close", code => { if (this.next) this.next.reject(new Error(`Metrics process closed: ${code} ${this.stderr}`)); code === 0 ? resolve() : reject(new Error(this.stderr)); });
        });
    }
    call(request) {
        assert.equal(this.next, null, "Metrics requests are sequential");
        return new Promise((resolve, reject) => { this.next = { resolve, reject }; this.child.stdin.write(JSON.stringify(request) + "\n"); });
    }
    async close() { this.child.stdin.end(); await this.exit; }
}

export const percentile = (values, fraction) => values.toSorted((a, b) => a - b)[Math.max(0, Math.ceil(values.length * fraction) - 1)];
export const median = values => percentile(values, 0.5);
export function statistics(values) { return { samples: values.length, median: median(values), p95: percentile(values, 0.95), p99: percentile(values, 0.99), min: Math.min(...values), max: Math.max(...values) }; }
export function summarizePairs(pairs, metric = value => value.elapsedMs) {
    const accepted = pairs.filter(pair => !pair.contaminated);
    assert.ok(accepted.length > 0, "There are no uncontaminated samples");
    const go = accepted.map(pair => metric(pair.go)), csharp = accepted.map(pair => metric(pair.csharp));
    let random = 817263;
    const next = () => { random ^= random << 13; random ^= random >>> 17; random ^= random << 5; return random >>> 0; };
    const ratios = [];
    for (let draw = 0; draw < 4000; draw++) {
        const indices = Array.from({ length: accepted.length }, () => next() % accepted.length);
        ratios.push(median(indices.map(index => csharp[index])) / median(indices.map(index => go[index])));
    }
    return { go: statistics(go), csharp: statistics(csharp), medianRatio: median(go) > 0 ? median(csharp) / median(go) : null,
        pairedBootstrap95: ratios.every(Number.isFinite) ? [percentile(ratios, 0.025), percentile(ratios, 0.975)] : null,
        zeroReferenceSamples: go.filter(value => value === 0).length, discardedPairs: pairs.length - accepted.length };
}
export async function paired(metrics, samples, action, file, warmups = 3) {
    const pairs = [];
    for (let iteration = -warmups; pairs.filter(pair => !pair.contaminated).length < samples; iteration++) {
        if (iteration > samples * 4 + 50) throw new Error("Input activity prevented a clean benchmark batch; rejected samples are preserved");
        const before = await metrics.call({ kind: "idle" });
        const pair = { iteration };
        for (const backend of iteration % 2 === 0 ? ["go", "csharp"] : ["csharp", "go"]) pair[backend] = await action(backend, iteration);
        const after = await metrics.call({ kind: "idle" });
        pair.contaminated = before.lastInputTick !== after.lastInputTick || [pair.go, pair.csharp].some(value => value.inputBefore !== value.inputAfter);
        await appendFile(file, JSON.stringify(pair) + "\n");
        if (iteration >= 0) pairs.push(pair);
        if (pair.contaminated) console.log(`Discarded input-contaminated pair ${iteration}`);
    }
    return pairs;
}
export async function activityReport(metrics, before, ownProcessIds) {
    const after = await metrics.call({ kind: "activity" }), previous = new Map(before.processes.map(item => [item.Id, item]));
    const external = after.processes.filter(item => !ownProcessIds.includes(item.Id)).map(item => ({ name: item.ProcessName, pid: item.Id,
        cpuSeconds: item.CPU - (previous.get(item.Id)?.CPU ?? 0) })).filter(item => item.cpuSeconds > 0.5).sort((a, b) => b.cpuSeconds - a.cpuSeconds);
    return { before, after, externalCpu: external };
}
export async function fileHashes(files) { return Object.fromEntries(await Promise.all(Object.entries(files).map(async ([name, file]) => [name, { file, sha256: sha256(await readFile(file)) }]))); }
export async function saveSummary(file, value) { await json(file, value); console.log(JSON.stringify({ summary: file, groups: value.groups?.length, nativeExecuted: value.nativeExecuted })); }
