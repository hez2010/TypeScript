import assert from "node:assert/strict";
import {
    readFile,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import {
    json,
    output,
    run,
    sha256,
} from "./common.mjs";

const go = process.argv[2] ?? "go";
const executable = path.join(output, "native-host/TypeScript.Compatibility" + (process.platform === "win32" ? ".exe" : ""));
const directory = path.join(output, "profiles");
await run(executable, ["--profile-pipeline", path.join(output, "pipeline-input.json"), directory]);
const files = ["cpu", "alloc", "cpu-stacks", "alloc-stacks", "heap-retained", "heap-released"];
const evidence = {};
for (const name of files) {
    const file = path.join(directory, `${name}.pb.gz`);
    const raw = await run(go, ["tool", "pprof", "-raw", file]);
    const top = await run(go, ["tool", "pprof", "-top", file]);
    await writeFile(path.join(directory, `${name}.raw.txt`), raw);
    await writeFile(path.join(directory, `${name}.top.txt`), top);
    const sampleText = raw.split("Samples:\n")[1]?.split("Locations")[0] ?? raw.replaceAll("\r\n", "\n").split("Samples:\n")[1]?.split("Locations")[0];
    assert.ok(sampleText, "pprof sample section missing");
    const samples = [...sampleText.matchAll(/^\s*((?:\d+\s+)*\d+):/gm)].map(match => match[1].trim().split(/\s+/).map(Number));
    assert.ok(samples.length > 0, "Empty profile");
    const totals = samples[0].map((_, index) => samples.reduce((total, sample) => total + sample[index], 0));
    assert.ok(totals[0] > 0, "No measured CPU/allocation/heap bytes");
    if (name.endsWith("-stacks")) assert.ok(["SliceLexer", "SliceParser", "SliceProject", "SliceEncoder", "SlicePrinter"].some(type => raw.includes(type)), "NativeAOT managed stack names missing");
    if (name === "cpu" || name === "alloc") { for (const phase of ["Parse", "Bind", "Check", "Encode", "Print"]) assert.ok(raw.includes(`TypeScript.${phase}`), `Missing phase ${phase}`); }
    if (name === "heap-retained") {
        assert.ok(raw.includes("known-retained-source"));
        assert.ok(samples.some(sample => sample[1] === 4 * 1024 * 1024 && sample[2] === 123));
    }
    if (name === "heap-released") assert.ok(!raw.includes("known-retained-source"), "Released owner still present");
    evidence[name] = { sha256: sha256(await readFile(file)), bytes: (await readFile(file)).length, totals, sampleCount: samples.length };
}
const runEvidence = JSON.parse(await readFile(path.join(directory, "run.json"), "utf8"));
assert.equal(runEvidence.releasedOwnerCollected, true);
const summary = {
    timestamp: new Date().toISOString(),
    runtime: await run(executable, ["--native-check"]),
    instructionSet: "native",
    optimizationPreference: "Speed",
    candidateSha256: sha256(await readFile(executable)),
    consumer: await run(go, ["version"]),
    semantics: runEvidence,
    profiles: evidence,
    interpretation: "NativeAOT emits these profiles directly, without an external runtime or Go dependency. CPU/allocation attribution is instrumented by compiler phase, not statistical stack sampling. Heap exposes measured whole-process managed bytes and separate live-source counters.",
};
await json(path.join(output, "native-profile-summary.json"), summary);
console.log(JSON.stringify(summary));
