import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import {
    copyFile,
    mkdir,
    readFile,
} from "node:fs/promises";
import path from "node:path";
import {
    json,
    output,
    referenceRevision,
    root,
    run,
    sha256,
} from "./common.mjs";
const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe"), dotnet = option("--dotnet", process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, "dotnet.exe") : "dotnet");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8")), source = path.join(output, reference.sourceRelativePath, "tsc");
const oracle = path.join(output, "mapping-oracle.exe"), managed = process.argv.includes("--managed"), native = path.join(output, "phase3-native");
const candidate = managed ? dotnet : path.join(native, "TypeScript.Compatibility.exe"), dll = path.join(root, "csharp/tests/TypeScript.Compatibility/bin/Release/net11.0/TypeScript.Compatibility.dll");
const args = [...managed ? [dll] : [], "--mapping-lines"];
await mkdir(path.join(source, "cmd/mapping-probe"), { recursive: true });
await copyFile(path.join(root, "csharp/oracle/mapping/main.go"), path.join(source, "cmd/mapping-probe/main.go"));
await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", oracle, "./cmd/mapping-probe"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
if (!process.argv.includes("--no-build")) {
    await run(
        dotnet,
        managed ? ["build", "tests/TypeScript.Compatibility", "-c", "Release", "--no-restore"]
            : ["publish", "tests/TypeScript.Compatibility", "-c", "Release", "-r", "win-x64", "-p:IlcInstructionSet=native", "-p:RestoreLockedMode=true", "-o", native],
        { cwd: path.join(root, "csharp"), env: { ...process.env, DOTNET_ROOT: path.dirname(dotnet), PATH: path.dirname(dotnet) + path.delimiter + process.env.PATH } },
    );
}
const maps = [[], [[0, 10, 0, 10, 0]], [[0, 5, 2, 3, 1]], [[0, 5, 2, 3, 2]], [[2, 2, 0, 2, 0], [4, 2, 2, 2, 0]], [[0, 3, 0, 3, 0], [7, 3, 0, 3, 0]], [[0, 2, 0, 2, 0], [3, 2, 2, 2, 0], [6, 2, 0, 2, 0], [9, 2, 2, 2, 0]], [[1, 0, 1, 0, 0], [1, 2, 5, 2, 0]], [[0, 4, 0, 2, 1], [4, 4, 2, 2, 1]], [[0, 4, 0, 4, 0, 1], [4, 4, 4, 4, 0, 256]], [[0, 2, 2, 2, 0, 0], [3, 4, 0, 4, 0]], [[1, 2, 0, 5, 2, 1], [4, 2, 0, 5, 2, 256]], [[0, 4, 0, 4, 0], [4, 4, 8, 4, 0]], [[0, 4, 0, 4, 0], [4, 2, 4, 3, 1]], [[0, 4, 2, 4, 0], [4, 2, 0, 2, 0]], [[0, 0, 0, 0, 0]], [[0, 0, 0, 0, 1]], [[0, 0, 0, 0, 2]], [[0, 0, 0, 0, 0], [0, 0, 1, 0, 1]]];
const cases = [];
for (let i = 0; i < maps.length; i++) for (let start = -1; start <= 14; start++) for (let end = -1; end <= 14; end++) for (const feature of [0, 1, 256, 1048575]) cases.push({ name: `map:${i}:${start}:${end}:${feature}`, virtual: "x".repeat(20), original: "x".repeat(20), mappings: maps[i], start, end, feature });
let seed = 193751;
const random = n => (seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0) % n;
for (let i = 0; i < 3000; i++) {
    const segments = [];
    let pos = 0;
    for (let j = 0; j < 10; j++) {
        pos += random(3);
        const length = random(6), kind = random(3), original = random(20);
        segments.push([pos, length, original, kind === 0 ? length : random(10), kind, [0, 1, 256, 1048575][random(4)]]);
        pos += length;
    }
    cases.push({ name: `random:${i}`, virtual: "x".repeat(60), original: "x".repeat(40), mappings: segments, start: random(60), end: random(60), feature: [0, 1, 256, 1048575][random(4)] });
}
for (const mappings of [[[0, -1, 0, 1, 0]], [[-1, 1, 0, 1, 0]], [[0, 9, 0, 1, 0]], [[0, 1, -1, 1, 0]], [[0, 1, 0, 1, 9]], [[0, 1, 0, 1, 0, -1]], [[0, 1, 0, 1, 0, 1048576]], [[0, 1, 0, 2, 0]], [[0, 1, 0, 1]], [[0, 1, 0, 1, 0, 0, 0]], [[0, 2147483647, 0, 1, 0]], [[1, 2147483647, 0, 1, 0]], null]) cases.push({ name: `invalid:${cases.length}`, mappings, virtual: "x", original: "x", start: 0, end: 1, feature: 1 });
async function probe(command, args) {
    return new Promise((resolve, reject) => {
        const child = spawn(command, args, { windowsHide: true }), out = [], errors = [];
        child.stdout.on("data", b => out.push(b));
        child.stderr.on("data", b => errors.push(b));
        child.on("error", reject);
        child.stdin.on("error", () => {});
        child.on("close", code => {
            if (code) reject(new Error(`${command}: ${Buffer.concat(errors)}`));
            else resolve(Buffer.concat(out).toString().trim().split(/\r?\n/).map(x => JSON.parse(x)));
        });
        child.stdin.end(cases.map(c => JSON.stringify(c)).join("\n") + "\n");
    });
}
const expected = await probe(oracle, []), actual = await probe(candidate, args);
assert.equal(expected.length, cases.length);
assert.equal(actual.length, cases.length);
const differences = [];
for (let i = 0; i < cases.length; i++) {
    try {
        assert.deepEqual(actual[i], expected[i]);
    }
    catch {
        differences.push({ input: cases[i], expected: expected[i], actual: actual[i] });
    }
}
await json(path.join(output, "mapping-failures.json"), differences);
const summary = { timestamp: new Date().toISOString(), referenceRevision, managed, cases: cases.length, passed: cases.length - differences.length, failed: differences.length, inputSha256: sha256(JSON.stringify(cases)), oracleSha256: sha256(await readFile(oracle)), candidateSha256: sha256(await readFile(managed ? dll : candidate)) };
await json(path.join(output, "mapping-summary.json"), summary);
if (option("--record")) await json(path.join(root, `csharp/compatibility/evidence/${option("--record")}.json`), summary);
console.log(summary);
if (differences.length) {
    console.log(differences.slice(0, 5));
    process.exitCode = 1;
}
