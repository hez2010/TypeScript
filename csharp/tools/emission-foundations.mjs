import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile } from "node:fs/promises";
import path from "node:path";
import { SourceMap } from "node:module";
import { Script } from "node:vm";
import { root, output, run, json, sha256, referenceRevision } from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const canonical = value => Array.isArray(value) ? value.map(canonical) : value !== null && typeof value === "object"
    ? Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])])) : value;
const dotnet = option("--dotnet", process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, "dotnet.exe") : "dotnet");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const directory = path.join(output, "emission-foundations");
await mkdir(directory, {recursive: true});
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc");
const oracle = path.join(directory, "oracle.exe");
const bridge = path.join(root, "csharp/oracle/emission-foundations/main.go");
const bridgeDirectory = path.join(source, "cmd/csharp-emission-foundations");
await mkdir(bridgeDirectory, {recursive: true});
await copyFile(bridge, path.join(bridgeDirectory, "main.go"));
if (!process.argv.includes("--no-build")) {
    await run(go, ["-C", source, "build", "-o", oracle, "./cmd/csharp-emission-foundations"], {env: {...process.env, GOWORK: "off", GOTOOLCHAIN: "local"}});
    await run(dotnet, ["build", "csharp/TypeScript.slnx", "-c", "Release", "--no-restore"]);
}
const cases = [];
for (const newLine of ["\n", "\r\n"]) for (const indentSize of [2, 4]) {
    for (const value of ["plain", "é", "😀𝄞", "a\r\nb\nc\u2028d\u2029e", "a\t ", "/* comment */"]) {
        cases.push({name: `writer-${cases.length}`, operation: "writer", newLine, indentSize, steps: [
            {kind: "write", text: ""}, {kind: "indent"}, {kind: "write", text: value},
            {kind: "comment", text: "/* end */"}, {kind: "line"}, {kind: "line"}, {kind: "line", force: true},
            {kind: "write", text: "last"}, {kind: "dedent"}, {kind: "clear"}, {kind: "raw", text: value},
        ]});
    }
}
cases.push({name: "writer-split-crlf", operation: "writer", steps: [{kind: "write", text: "a\r"}, {kind: "raw", text: "\n"}, {kind: "write", text: "b"}]});
cases.push({name: "writer-raw-wtf8", operation: "writer", steps: [{kind: "write", textBase64: Buffer.from([0xED, 0xA0, 0x80]).toString("base64")}]});
for (const directory of ["/out", "C:/out", "https://example.org/out", "out"]) {
    cases.push({name: `paths-${directory}`, operation: "map", file: "main.js", directory, sourceRoot: "../source", steps: [
        ...["/src/a.ts", "/src/a.ts", "src/a.ts", "C:/src/a.ts", "D:/src/b.ts", "https://example.org/src/a.ts", "/src/😀.ts"].map(text => ({kind: "source", text})),
        {kind: "name", text: "😀"}, {kind: "name", text: "😀"}, {kind: "name", text: "name"},
        {kind: "content", source: 0, text: "const value = '😀';"},
        {kind: "named", line: 0, column: 0, source: 0, sourceLine: 0, sourceColumn: 0, name: 0},
        {kind: "mapping", line: 0, column: 5, source: 1, sourceLine: 12, sourceColumn: 8},
        {kind: "generated", line: 1, column: 0}, {kind: "mapping", line: 1, column: 0, source: 0, sourceLine: 0, sourceColumn: 0},
        {kind: "mapping", line: 2, column: 1024, source: 0, sourceLine: 1, sourceColumn: 0},
    ]});
}
let seed = 0xC0FFEE;
const next = n => ((seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0) % n);
for (let test = 0; test < 256; test++) {
    const steps = Array.from({length: 4}, (_, i) => ({kind: "source", text: `/source/${i}.ts`}));
    steps.push(...Array.from({length: 3}, (_, i) => ({kind: "name", text: `value${i}`})));
    let line = 0, column = 0;
    for (let i = 0; i < 80; i++) {
        if (!next(9)) { line += next(4) + 1; column = 0; }
        else column += next(7);
        const kind = ["mapping", "named", "generated"][next(3)];
        steps.push({kind, line, column, source: next(4), sourceLine: next(2000), sourceColumn: next(10000), name: next(3)});
    }
    cases.push({name: `map-${test}`, operation: "map", file: "output.js", directory: "/out", steps});
}
for (const kind of ["auto", "loop", "unique", "private", "identifierNode"]) for (const flags of [0, 8, 16, 32, 48]) {
    for (const [prefix, suffix] of [["", ""], ["P", "S"], ["#P", "#S"]]) {
        const steps = [];
        for (let index = 0; index < 40; index++) {
            if (index === 5 || index === 10 || index === 20) steps.push({kind: "push", reuse: index === 20});
            if (index === 15 || index === 25 || index === 30) steps.push({kind: "pop", reuse: index === 25});
            steps.push({kind, text: kind === "private" ? "#foo" : "foo", flags, prefix, suffix}, {kind: "generate", index});
        }
        steps.push({kind: "clone", index: 0}, {kind: "generate", index: 40}, {kind: "generate", index: 0});
        steps.push({kind: "helper", text: "foo"}, {kind: "helper", text: "foo"});
        cases.push({name: `names-${kind}-${flags}-${prefix}`, operation: "names", blocked: ["_a", "_i", "foo_1", "#foo_1", "P_aS", "P_iS"], steps});
    }
}
for (const [source, target] of [
    ["namespace foo { }", "ModuleDeclaration"], ["namespace foo { var foo; }", "ModuleDeclaration"],
    ["namespace foo { function f(foo: number) {} }", "ModuleDeclaration"], ["enum foo { a }", "EnumDeclaration"],
    ["import * as foo from 'lib/foo-bar.js'", "ImportDeclaration"], ["export * from 'lib/01-café'", "ExportDeclaration"],
    ["export default function () {}", "FunctionDeclaration"], ["function foo() {}", "FunctionDeclaration"],
    ["export default class {}", "ClassDeclaration"], ["class foo {}", "ClassDeclaration"],
    ["export default 1", "ExportAssignment"], ["(class {})", "ClassExpression"],
    ["class C { foo() {} }", "MethodDeclaration"], ["class C { ['x']() {} }", "MethodDeclaration"],
    ["class C { ['x']() {} }", "ComputedPropertyName"], ["const foo = 1", "VariableStatement"],
]) {
    cases.push({name: `names-source-${cases.length}`, operation: "names", source, steps: [
        {kind: "node", target}, {kind: "generate", index: 0}, {kind: "node", target}, {kind: "generate", index: 1},
        {kind: "clone", index: 1}, {kind: "generate", index: 2},
    ]});
}
cases.push({name: "names-private-method", operation: "names", source: "class C { foo() {} }", steps: [
    {kind: "privateNode", target: "MethodDeclaration"}, {kind: "generate", index: 0}, {kind: "push"},
    {kind: "privateNode", target: "MethodDeclaration"}, {kind: "generate", index: 1}, {kind: "pop"},
]});
await json(path.join(directory, "inputs.json"), cases);
async function execute(command, args) {
    const child = spawn(command, args, {cwd: root, windowsHide: true});
    let text = "", error = "";
    child.stdout.on("data", bytes => text += bytes); child.stderr.on("data", bytes => error += bytes);
    child.stdin.on("error", () => {});
    child.stdin.end(cases.map(c => JSON.stringify(c)).join("\n") + "\n");
    const code = await new Promise((resolve, reject) => {child.on("error", reject); child.on("close", resolve);});
    if (code) throw Error(`${command}: ${code}\n${error}`);
    const rows = text.trim().split(/\r?\n/).map(JSON.parse);
    assert.equal(rows.length, cases.length);
    return rows;
}
const dll = path.join(option('--managed-directory', path.join(root, 'csharp/tests/TypeScript.Compatibility/bin/Release/net11.0')), 'TypeScript.Compatibility.dll');
const before = await execute(oracle, []), after = await execute(dotnet, [dll, "--emit-foundations-lines"]);
const failures = [], differences = [];
for (let i = 0; i < cases.length; i++) {
    try { assert.deepEqual(after[i], before[i]); }
    catch {
        const difference = {input: cases[i], inputHash: sha256(Buffer.from(JSON.stringify(cases[i]))), reference: before[i], candidate: after[i]};
        if (cases[i].name === "writer-split-crlf") {
            const expected = structuredClone(before[i]);
            expected[1].line = 1;
            expected[2].line = 1;
            assert.deepEqual(after[i], expected);
            const text = Buffer.from(after[i][2].textBase64, "base64").toString();
            assert.equal(text, "a\r\nb");
            const globals = {a: 0};
            Object.defineProperty(globals, "b", {get() { throw Error("position probe"); }});
            let stack;
            try { new Script(text, {filename: "chunked-crlf.js"}).runInNewContext(globals); }
            catch (error) { stack = error.stack; }
            assert.match(stack, /chunked-crlf\.js:2:1/);
            difference.policy = "writer-chunked-crlf";
            difference.sourceHash = sha256(Buffer.from(text));
            difference.evidence = {nodeVersion: process.version, location: "chunked-crlf.js:2:1", expectedZeroBasedLine: 1};
        } else failures.push(difference);
        differences.push(difference);
    }
}
// An independent consumer must decode source/name indices and the UTF-16 coordinates.
const probe = new SourceMap({version: 3, file: "out.js", sourceRoot: "", sources: ["input.ts"], names: ["x"], mappings: "AAAAA,KAAI;AACJ,E"});
assert.equal(probe.findEntry(0, 5).originalColumn, 4);
assert.equal(probe.findEntry(1, 0).originalLine, 1);
await json(path.join(directory, "failures.json"), failures);
await json(path.join(directory, "differences.json"), differences);
const summary = {cases: cases.length, exact: cases.length - differences.length, permitted: differences.filter(d => d.policy).length, failed: failures.length, referenceRevision,
    oracleSourceSha256: sha256(await readFile(bridge)), oracleSha256: sha256(await readFile(oracle)),
    candidateHash: sha256(Buffer.concat([await readFile(dll), await readFile(path.join(path.dirname(dll), "TypeScript.Compiler.dll"))])),
    inputHash: sha256(Buffer.from(JSON.stringify(cases))), referenceHash: sha256(Buffer.from(JSON.stringify(canonical(before)))), candidateOutputHash: sha256(Buffer.from(JSON.stringify(canonical(after))))};
await json(path.join(directory, "summary.json"), summary);
if (process.argv.includes("--record")) {
    assert.equal(failures.length, 0, "Cannot record an incomplete foundation gate");
    await json(path.join(root, "csharp/compatibility/evidence/phase5-emission-foundations.json"), {...summary, differences});
}
console.log(summary);
console.log(failures.map(f => f.input.name));
if (failures.length) process.exitCode = 1;
