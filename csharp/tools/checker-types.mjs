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
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rc.2.26470.103-win-x64/dotnet.exe");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc"), oracle = path.join(output, "checker-types-oracle.exe");
const managed = process.argv.includes("--managed"), native = path.join(output, "phase4-native");
const dll = path.join(root, "csharp/tests/TypeScript.Compatibility/bin/Release/net11.0/TypeScript.Compatibility.dll");
const candidate = managed ? dotnet : path.join(native, "TypeScript.Compatibility.exe");
await mkdir(path.join(source, "cmd/checker-types-probe"), { recursive: true });
await copyFile(path.join(root, "csharp/oracle/checker-types/main.go"), path.join(source, "cmd/checker-types-probe/main.go"));
await copyFile(path.join(root, "csharp/oracle/checker-types/bridge.go"), path.join(source, "internal/checker/csharp_types_probe.go"));
await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", oracle, "./cmd/checker-types-probe"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });

const names = ["any", "auto", "wildcard", "blockedString", "error", "unresolved", "nonInferrableAny", "intrinsic", "unknown", "undefined", "undefinedWidening", "missing", "undefinedOrMissing", "optional", "null", "nullWidening", "string", "number", "bigint", "false", "true", "regularFalse", "regularTrue", "boolean", "symbol", "void", "never", "silentNever", "implicitNever", "unreachableNever", "object", "uniqueLiteral"];
const bits = value => {
    const b = Buffer.alloc(8);
    b.writeDoubleBE(value);
    return b.toString("hex");
};
const atomSteps = names.map(text => ({ op: "builtin", text }));
for (const text of ["", "a", "あ", "😀", "__x", "\ufdd0", "\u0000", "x".repeat(1024)]) atomSteps.push({ op: "string", text: Buffer.from(text).toString("base64") });
for (const text of ["eda080", "edb080", "eda08061edb080"]) atomSteps.push({ op: "string", text: Buffer.from(text, "hex").toString("base64") });
for (const value of [0, -0, 1, -1, Number.MIN_VALUE, Number.MAX_VALUE, Infinity, -Infinity, NaN, 0.1, 9007199254740991]) atomSteps.push({ op: "number", text: bits(value) });
for (const text of ["7ff0000000000001", "fff8000000000001"]) atomSteps.push({ op: "number", text });
for (const text of ["0", "-0", "000", "1", "-1", "99999999999999999999999999999999", "-99999999999999999999999999999999", "9".repeat(4096)]) atomSteps.push({ op: "bigint", text });
let cases = [];
for (const strict of [false, true]) {
    for (const exact of [false, true]) {
        const steps = [...atomSteps, ...atomSteps];
        for (let i = names.length + 1; i <= atomSteps.length; i++) {
            steps.push({ op: "fresh", args: [i] }, { op: "fresh", args: [i] });
            steps.push({ op: "regular", args: [steps.length] }, { op: "fresh", args: [steps.length] });
        }
        for (const op of ["enumNumber", "enumString"]) {
            for (const symbol of ["E", "F"]) {
                for (const member of ["x", "y"]) {
                    for (const text of op === "enumNumber" ? [bits(0), bits(-0), bits(1), bits(NaN), "7ff0000000000001"] : ["a", "b"]) steps.push({ op, symbol, member, text });
                }
            }
        }
        steps.push({ op: "computedEnum", symbol: "E" }, { op: "fresh", args: [steps.length + 1] });
        cases.push({ name: `atoms:${strict}:${exact}`, strict, exact, steps });
    }
}

// Constructor graph checks retain process-local identities as graph edges rather
// than compare absolute IDs, which include unrelated checker initialization work.
for (const strict of [false, true]) {
    for (const exact of [false, true]) {
        let state = 0x56328;
        const random = n => (state = (Math.imul(state, 1664525) + 1013904223) >>> 0) % n;
        for (let iteration = 0; iteration < 500; iteration++) {
            const steps = names.map(text => ({ op: "builtin", text }));
            const add = step => {
                steps.push(step);
                return steps.length;
            };
            const parameter = add({ op: "parameter", symbol: "T", this: iteration % 3 === 0 });
            const intf = add({ op: "object", flags: 2 | 4, symbol: "I" });
            const literal = add({ op: "string", text: Buffer.from(String(iteration)).toString("base64") });
            const fresh = add({ op: "fresh", args: [literal] });
            const selected = [10, 11, 12, 15, 16, 17, 18, 19, 26, 28, parameter, literal, fresh];
            const raw = add({ op: iteration % 2 ? "rawUnion" : "rawIntersection", args: [parameter, literal] });
            const index = add({ op: "index", args: [parameter], flags: iteration % 8 });
            const aliasArgs = iteration % 2 ? [parameter] : [];
            for (let j = 0; j < 20; j++) {
                const args = Array.from({ length: random(5) }, () => selected[random(selected.length)]);
                // Inputs to this internal primitive are pre-normalized, and their
                // given order participates in identity. Full reduction has its own milestone.
                const unionArgs = [...new Set(args)].sort((a, b) => selected.indexOf(a) - selected.indexOf(b));
                for (const flags of [0, 1 << 17]) add({ op: "reference", args: [intf, ...args], flags });
                const origin = iteration % 3 === 0 ? raw : iteration % 3 === 1 ? index : 0;
                for (let repeat = 0; repeat < 2; repeat++) add({ op: "union", args: unionArgs, symbol: "U", aliasArgs, origin });
                if (args.length >= 2) {
                    add({ op: "substitution", args: args.slice(0, 2) });
                    add({ op: "substitution", args: args.slice(0, 2) });
                    add({ op: "indexed", args: args.slice(0, 2), flags: iteration % 2 });
                }
                add({ op: "index", args: [parameter], flags: j % 8 });
            }
            add({ op: "template", text: "a", args: [parameter, literal] });
            add({ op: "stringMapping", symbol: "Uppercase", args: [parameter] });
            for (const flags of [1, 2, 4, 8 | 4, 16, 32, 1024 | 16, 256, (1 << 24) | 16]) add({ op: "object", flags, symbol: iteration % 2 ? "S" : "" });
            const ref = add({ op: "reference", args: [intf, fresh] });
            add({ op: "clone", args: [ref] });
            cases.push({ name: `constructors:${strict}:${exact}:${iteration}`, strict, exact, steps });
        }
    }
}

for (let iteration = 0; iteration < 500; iteration++) {
    let state = iteration + 17;
    const random = n => (state = (Math.imul(state, 1664525) + 1013904223) >>> 0) % n;
    const steps = [
        { op: "parameter", symbol: "T" },
        { op: "parameter", symbol: "U" },
        { op: "parameter", symbol: "this", this: true },
        { op: "builtin", text: "string" },
        { op: "builtin", text: "number" },
        { op: "string", text: "YQ==" },
        { op: "distributed", args: [1] },
    ];
    const mappers = [];
    for (const op of ["direct", "single", "deferred", "function"]) {
        mappers.push({ op, sources: [3], targets: [4] });
        const sources = Array.from({ length: random(5) }, () => 1 + random(3));
        const targets = op === "single" ? [4] : sources.map(() => 1 + random(6));
        mappers.push({ op, sources, targets });
    }
    for (let j = 0; j < 12; j++) {
        const op = ["merged", "composite", "prepend", "append"][random(4)];
        mappers.push({ op, sources: [1 + random(7)], targets: [1 + random(6)], parts: [random(mappers.length + 1), 1 + random(mappers.length)] });
    }
    const queries = [];
    for (let mapper = 1; mapper <= mappers.length; mapper++) for (let type = 1; type <= steps.length; type++) for (const normalize of [false, true]) queries.push({ mapper, type, normalize }, { mapper, type, normalize });
    cases.push({ name: `mappers:${iteration}`, strict: true, steps, mappers, queries });
}
for (let first = 0; first < 10; first++) {
    for (let second = 0; second < 10; second++) {
        for (let cycle = 0; cycle < 3; cycle++) {
            const a = { target: 1, property: first }, b = { target: 2, property: second };
            const resolutions = [{ op: "push", ...a }, { op: "push", ...b }];
            if (cycle === 1) resolutions.push({ op: "set", ...b, value: 1 });
            if (cycle === 2) resolutions.push({ op: "start", value: 1 });
            resolutions.push({ op: "find", ...a }, { op: "push", ...a });
            if (cycle !== 0) resolutions.push({ op: "pop" });
            if (cycle === 2) resolutions.push({ op: "start", value: 0 });
            resolutions.push({ op: "pop" }, { op: "pop" }, { op: "find", ...a }, { op: "push", ...a }, { op: "pop" });
            cases.push({ name: `resolution:${first}:${second}:${cycle}`, resolutions, steps: [] });
        }
    }
}
for (let iteration = 0; iteration < 100; iteration++) {
    let state = iteration + 74839;
    const random = n => (state = (Math.imul(state, 1664525) + 1013904223) >>> 0) % n;
    const steps = [...atomSteps];
    const add = step => {
        steps.push(step);
        return steps.length;
    };
    const parameter = add({ op: "parameter", symbol: "T" });
    const intf = add({ op: "object", flags: 2, symbol: "I" });
    const origins = [add({ op: "rawUnion", args: [17, 18] }), add({ op: "rawIntersection", args: [parameter, 17] }), add({ op: "index", args: [parameter] })];
    for (let j = 0; j < 25; j++) {
        const args = Array.from({ length: random(4) + 1 }, () => 1 + random(atomSteps.length));
        const target = 1 + random(atomSteps.length);
        add({ op: "reference", args: [intf, ...args] });
        add({ op: "union", args, origin: origins[random(origins.length)], symbol: "U", aliasArgs: [target] });
        add({ op: "rawUnion", args });
        add({ op: "rawIntersection", args });
        add({ op: "index", args: [target], flags: random(2) });
        add({ op: "indexed", args: [parameter, target], flags: random(2) });
        add({ op: "template", text: ["a", "z", "\ue000", "😀"][random(4)], args });
        add({ op: "substitution", args: [parameter, target] });
        add({ op: "stringMapping", symbol: ["Uppercase", "Lowercase"][random(2)], args: [target] });
        add({ op: "enumString", symbol: "E", member: String(random(3)), text: String(j) });
    }
    for (const text of ["\ue000", "😀", "𐀀", "z", "aa", "a", "é", "\u0000"]) add({ op: "string", text: Buffer.from(text).toString("base64") });
    const comparisons = [];
    const nameStart = steps.length + 1;
    for (const symbol of ["a", "z", "\ufdd0x", "\ufdd0\ufdd0x", "😀", "\ue000", "@internal:type", "@internal:call"]) add({ op: "parameter", symbol });
    for (let i = nameStart; i <= steps.length; i++) for (let j = nameStart; j <= steps.length; j++) comparisons.push([i, j]);
    for (let i = 0; i <= atomSteps.length; i++) for (let j = 0; j <= atomSteps.length; j++) comparisons.push([i, j]);
    for (let j = 0; j < 2000; j++) comparisons.push([random(steps.length + 1), random(steps.length + 1)]);
    cases.push({ name: `order:${iteration}`, strict: true, steps, comparisons });
}

if (option("--inputs")) cases = JSON.parse(await readFile(option("--inputs"), "utf8"));
if (option("--filter")) cases = cases.filter(c => c.name.includes(option("--filter")));

async function probe(command, args) {
    return new Promise((resolve, reject) => {
        const child = spawn(command, args, { windowsHide: true }), stdout = [], stderr = [];
        child.stdout.on("data", b => stdout.push(b));
        child.stderr.on("data", b => stderr.push(b));
        child.on("error", reject);
        child.stdin.on("error", () => {});
        child.on("close", code => {
            const lines = Buffer.concat(stdout).toString().trim().split(/\r?\n/).filter(Boolean);
            if (code) reject(Error(`${command}: ${cases[lines.length]?.name}: ${Buffer.concat(stderr)}`));
            else resolve(lines.map(JSON.parse));
        });
        child.stdin.end(cases.map(c => JSON.stringify(c)).join("\n") + "\n");
    });
}
const expected = await probe(oracle, []);
assert.equal(expected.length, cases.length);
if (process.argv.includes("--reference-only")) {
    await json(path.join(output, "checker-types-reference.json"), { cases: cases.length, inputSha256: sha256(JSON.stringify(cases)), outputSha256: sha256(JSON.stringify(expected)) });
    console.log(`Reference: ${cases.length} cases`);
    process.exit(0);
}
if (!process.argv.includes("--no-build")) {
    await run(
        dotnet,
        managed ? ["build", "tests/TypeScript.Compatibility", "-c", "Release", "--no-restore"]
            : ["publish", "tests/TypeScript.Compatibility", "-c", "Release", "-r", "win-x64", "-p:IlcInstructionSet=native", "-p:RestoreLockedMode=true", "-o", native],
        { cwd: path.join(root, "csharp"), env: { ...process.env, DOTNET_ROOT: path.dirname(dotnet), PATH: path.dirname(dotnet) + path.delimiter + process.env.PATH } },
    );
}
const runtime = managed ? "managed development run" : await run(candidate, ["--native-check"]);
const actual = await probe(candidate, [...managed ? [dll] : [], "--checker-types-lines"]);
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
await json(path.join(output, "checker-types-failures.json"), differences);
const summary = { timestamp: new Date().toISOString(), scope: option("--scope", "checker type constructors, identity, order, mapper composition and resolution state; not complete semantic checking"), referenceRevision, managed, runtime, cases: cases.length, operations: cases.reduce((n, c) => n + c.steps.length + (c.mappers?.length ?? 0) + (c.queries?.length ?? 0) + (c.resolutions?.length ?? 0) + (c.comparisons?.length ?? 0), 0), passed: cases.length - differences.length, failed: differences.length, inputSha256: sha256(JSON.stringify(cases)), referenceOutputSha256: sha256(JSON.stringify(expected)), outputSha256: sha256(JSON.stringify(actual)), oracleSha256: sha256(await readFile(oracle)), candidateSha256: sha256(await readFile(managed ? dll : candidate)), sdk: await run(dotnet, ["--version"], { cwd: path.join(root, "csharp") }), go: await run(go, ["version"]) };
await json(path.join(output, "checker-types-summary.json"), summary);
if (option("--record")) await json(path.join(root, `csharp/compatibility/evidence/${option("--record")}.json`), summary);
console.log(summary);
if (differences.length) {
    console.log(differences.slice(0, 5).map(d => d.input.name));
    process.exitCode = 1;
}
