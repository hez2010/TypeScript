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
const oracle = path.join(output, "checker-symbols-oracle.exe"), managed = process.argv.includes("--managed"), native = path.join(output, "phase4-native");
const dll = path.join(root, "csharp/tests/TypeScript.Compatibility/bin/Release/net11.0/TypeScript.Compatibility.dll"), candidate = managed ? dotnet : path.join(native, "TypeScript.Compatibility.exe");
await mkdir(path.join(source, "cmd/checker-symbols-probe"), { recursive: true });
await copyFile(path.join(root, "csharp/oracle/checker-symbols/main.go"), path.join(source, "cmd/checker-symbols-probe/main.go"));
await copyFile(path.join(root, "csharp/oracle/checker-symbols/bridge.go"), path.join(source, "internal/checker/csharp_symbols_probe.go"));
await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", oracle, "./cmd/checker-symbols-probe"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
if (!process.argv.includes("--no-build")) await run(dotnet, managed ? ["build", "tests/TypeScript.Compatibility", "-c", "Release", "--no-restore"] : ["publish", "tests/TypeScript.Compatibility", "-p:PublishAot=true", "-c", "Release", "-r", "win-x64", "-p:IlcInstructionSet=native", "-o", native], { cwd: path.join(root, "csharp"), env: { ...process.env, DOTNET_ROOT: path.dirname(dotnet), PATH: path.dirname(dotnet) + path.delimiter + process.env.PATH } });
const cases = [];
let state = 631243;
const random = n => (state = (Math.imul(state, 1664525) + 1013904223) >>> 0) % n;
const kinds = [0, 79, 212, 213, 214, 227, 261, 263, 268];
const pairs = [[64, 64], [64, 32], [32, 64], [16, 32], [32, 16], [512, 16], [512, 256], [256, 512], [1024, 64], [64, 1024], [4, 4], [32768, 65536], [65536, 32768], [8192, 8192], [1, 1], [128, 128], [512 | (1 << 28), 512], [512, 512 | (1 << 28)], [512 | (1 << 28), 512 | (1 << 28)]];
for (const [a, b] of pairs) for (const leftKind of kinds) for (const rightKind of kinds) for (const unidirectional of [false, true]) cases.push({ name: `pair:${a}:${b}:${leftKind}:${rightKind}:${unidirectional}`, symbols: [{ name: "x", flags: a, declaration: leftKind }, { name: "x", flags: b, declaration: rightKind }], operations: [{ left: 1, right: 2, unidirectional }] });
for (let i = 0; i < 1000; i++) {
    const symbols = [
        { name: "N", flags: 512, exports: [3, 4] },
        { name: "N", flags: 512, exports: [5, 6] },
        { name: "I", flags: 64, members: [7], parent: 1 },
        { name: "A", flags: 16, parent: 1 },
        { name: "I", flags: 64, members: [8], parent: 2 },
        { name: "B", flags: 16, parent: 2 },
        { name: "p", flags: 4, declaration: kinds[random(kinds.length)], parent: 3 },
        { name: i % 2 ? "p" : "q", flags: 4, declaration: kinds[random(kinds.length)], parent: 5 },
        { name: "N", flags: 512, exports: [10] },
        { name: "C", flags: 32, parent: 9 },
    ];
    cases.push({ name: `tables:${i}`, symbols, operations: [{ left: 1, right: 2, unidirectional: i % 3 === 0 }, { left: 11, right: 9 }] });
}
for (const target of [-1, 3]) for (const unidirectional of [false, true]) cases.push({ name: `alias:${target}:${unidirectional}`, symbols: [{ name: "A", flags: 1 << 21, aliasTarget: target }, { name: "I", flags: 64 }, { name: "I", flags: 64 }], operations: [{ left: 1, right: 2, unidirectional }] });
cases.push({ name: "exclusions", symbols: [], operations: [], flags: [...Array.from({ length: 31 }, (_, i) => 2 ** i), ...Array.from({ length: 10000 }, () => random(2 ** 30))] });
async function probe(command, args, items) {
    return new Promise((resolve, reject) => {
        const child = spawn(command, args, { windowsHide: true }), stdout = [], stderr = [];
        child.stdout.on("data", b => stdout.push(b));
        child.stderr.on("data", b => stderr.push(b));
        child.on("error", reject);
        child.stdin.on("error", () => {});
        child.on("close", code => {
            const lines = Buffer.concat(stdout).toString().trim().split(/\r?\n/).filter(Boolean);
            if (code) reject(Error(`${command}: ${items[lines.length]?.name}: ${Buffer.concat(stderr)}`));
            else resolve(lines.map(JSON.parse));
        });
        child.stdin.end(items.map(c => JSON.stringify(c)).join("\n") + "\n");
    });
}
const failures = [];
for (let offset = 0; offset < cases.length; offset += 100) {
    const batch = cases.slice(offset, offset + 100), expected = await probe(oracle, [], batch), actual = await probe(candidate, [...managed ? [dll] : [], "--checker-symbols-lines"], batch);
    assert.equal(expected.length, batch.length);
    assert.equal(actual.length, batch.length);
    for (let i = 0; i < batch.length; i++) {
        try {
            assert.deepEqual(actual[i], expected[i]);
        }
        catch {
            failures.push({ input: batch[i], expected: expected[i], actual: actual[i] });
        }
    }
}
await json(path.join(output, "checker-symbols-failures.json"), failures);
const summary = { timestamp: new Date().toISOString(), scope: "Symbol merge identities, declaration precedence, exclusion masks, alias callbacks and member/export ownership; diagnostic formatting is a checker responsibility", referenceRevision, managed, runtime: managed ? "managed development run" : await run(candidate, ["--native-check"]), cases: cases.length, exact: cases.length - failures.length, failed: failures.length, inputSha256: sha256(JSON.stringify(cases)), candidateSha256: sha256(await readFile(managed ? dll : candidate)), oracleSha256: sha256(await readFile(oracle)) };
await json(path.join(output, "checker-symbols-summary.json"), summary);
if (option("--record")) await json(path.join(root, `csharp/compatibility/evidence/${option("--record")}.json`), summary);
console.log(summary);
if (failures.length) {
    console.log(failures.slice(0, 5).map(f => f.input.name));
    process.exitCode = 1;
}
