import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, readFile, writeFile, appendFile } from "node:fs/promises";
import path from "node:path";
import { gzipSync, gunzipSync } from "node:zlib";
import { root, output, run, json, sha256, referenceRevision } from "./common.mjs";

const option = (name, fallback) => process.argv.includes(name) ? process.argv[process.argv.indexOf(name) + 1] : fallback;
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe");
const directory = path.join(output, "phase7-validation/ast-codec-corpus");
await mkdir(directory, { recursive: true });
if (!process.argv.includes("--no-prepare")) await run(process.execPath, [path.join(root, "csharp/tools/ast-codec.mjs"), "--prepare-only"]);
const oracle = path.join(output, "phase7-validation/ast-codec/oracle.exe");
const dll = path.join(output, "phase7-build/bin/TypeScript.Compatibility/release/TypeScript.Compatibility.dll");
async function probe(command, args, cases, label) {
    const child = spawn(command, args, { windowsHide: true }); const stdout = [], stderr = [];
    child.stdout.on("data", bytes => stdout.push(bytes)); child.stderr.on("data", bytes => stderr.push(bytes));
    child.stdin.on("error", () => {}); child.stdin.end(cases.map(value => JSON.stringify(value)).join("\n") + "\n");
    const code = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
    const lines = Buffer.concat(stdout).toString("utf8").trim().split(/\r?\n/).filter(Boolean);
    if (code !== 0) {
        await json(path.join(directory, "failure.json"), { label, code, input: cases[lines.length], stderr: Buffer.concat(stderr).toString("utf8") });
        throw Error(`${label}: next input ${cases[lines.length]?.name}\n${Buffer.concat(stderr)}`);
    }
    assert.equal(lines.length, cases.length); return lines.map(JSON.parse);
}
if (process.argv.includes("--reference-decode-only")) {
    const summary = JSON.parse(await readFile(path.join(directory, "summary.json"), "utf8"));
    assert.equal(summary.compilerHash, sha256(await readFile(path.join(path.dirname(dll), "TypeScript.Compiler.dll"))), "Corpus replay requires the same compiler");
    const differences = JSON.parse(await readFile(path.join(directory, "differences.json"), "utf8"));
    for (const file of new Set(differences.map(record => record.evidence))) {
        const records = JSON.parse(gunzipSync(await readFile(path.join(directory, file))));
        await probe(dotnet, [dll, "--ast-decode-lines"], records.map(record => record.reference), "reference decode");
    }
    await json(path.join(directory, "summary.json"), { ...summary, referenceRoundTrips: summary.units,
        differentReferencePacketsDecoded: differences.length, identicalReferencePackets: summary.strictMatches });
    console.log(`${differences.length} differing Go packets decoded and re-encoded exactly; ${summary.strictMatches} identical packets reuse corpus round trips`);
    process.exit(0);
}
const files = (await run("rg", ["--files", "tsc/testdata/tests/cases", "tsc/internal/bundled/libs"]))
    .split(/\r?\n/).filter(file => /\.(?:[cm]?tsx?|[cm]?jsx?|json)$/.test(file) && file.includes(option("--filter", ""))).sort();
const units = await probe(oracle, [], files.map(file => ({ mode: "units", unitPath: path.join(root, file), name: file })), "fixture expansion");
const cases = units.flatMap((unitList, i) => (unitList ?? []).map((unit, j) => ({
    ...unit, name: `${files[i].replaceAll("\\", "/")}::${j}`, compact: true,
    fileName: path.posix.normalize(unit.fileName.replaceAll("\\", "/").startsWith("/") ? unit.fileName.replaceAll("\\", "/") : "/fixtures/" + unit.fileName.replaceAll("\\", "/")),
    scriptKind: /\.jsx$/i.test(unit.fileName) ? 2 : /\.[cm]?js$/i.test(unit.fileName) ? 1 : /\.tsx$/i.test(unit.fileName) ? 4 : /\.json$/i.test(unit.fileName) ? 6 : 3
})));
await writeFile(path.join(directory, "inputs.json.gz"), gzipSync(JSON.stringify(cases)));
await writeFile(path.join(directory, "results.jsonl"), "");
const differences = [], kinds = new Set(); let nodes = 0, controls = 0;
for (let start = 0; start < cases.length; start += 200) {
    const batch = cases.slice(start, start + 200);
    const [reference, candidate] = await Promise.all([
        probe(oracle, [], batch, "reference"), probe(dotnet, [dll, "--ast-codec-lines"], batch, "candidate")
    ]);
    const changed = [];
    for (let i = 0; i < batch.length; i++) {
        nodes += candidate[i].nodes; for (const kind of candidate[i].kinds) kinds.add(kind);
        if (reference[i].hash !== candidate[i].hash) changed.push({ ...batch[i], compact: false });
        await appendFile(path.join(directory, "results.jsonl"), JSON.stringify({ name: batch[i].name, reference: reference[i], candidate: candidate[i] }) + "\n");
    }
    if (changed.length) {
        const candidateTrees = await probe(dotnet, [dll, "--ast-codec-lines"], changed, "candidate syntax");
        const expected = await probe(oracle, [], changed, "strict reference");
        await probe(dotnet, [dll, "--ast-decode-lines"], expected, "reference decode");
        const actual = await probe(oracle, [], changed.map((input, i) => ({ ...input, tree: candidateTrees[i].tree, metadata: candidateTrees[i].metadata })), "candidate syntax control");
        const records = changed.map((input, i) => ({ input, reference: expected[i], candidate: candidateTrees[i], control: actual[i] }));
        await writeFile(path.join(directory, `differences-${start}.json.gz`), gzipSync(JSON.stringify(records)));
        for (let i = 0; i < records.length; i++) {
            const { input, reference, candidate, control } = records[i];
            assert.equal(candidate.bytes, control.bytes, `Encoder algorithm differs on candidate syntax: ${input.name}`);
            assert.deepEqual(candidate.kinds, control.kinds, `Index table differs on candidate syntax: ${input.name}`);
            differences.push({ name: input.name, inputHash: sha256(JSON.stringify(input)), referenceHash: sha256(Buffer.from(reference.bytes, "base64")),
                candidateHash: sha256(Buffer.from(candidate.bytes, "base64")), evidence: `differences-${start}.json.gz` });
            controls++;
        }
    }
    console.log(`AST codec corpus ${Math.min(start + batch.length, cases.length)}/${cases.length}; ${controls} syntax controls`);
}
const summary = { referenceRevision, fixtures: files.length, units: cases.length, nodes, syntaxKinds: [...kinds].sort((a, b) => a - b),
    roundTrips: cases.length, referenceRoundTrips: cases.length, strictMatches: cases.length - differences.length, parserDifferences: differences.length,
    candidateSyntaxControls: controls, codecDifferences: 0, compilerHash: sha256(await readFile(path.join(path.dirname(dll), "TypeScript.Compiler.dll"))) };
await json(path.join(directory, "differences.json"), differences); await json(path.join(directory, "summary.json"), summary);
console.log(JSON.stringify(summary));
