import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, sha256, referenceRevision } from "./common.mjs";

const option = (name, fallback) => process.argv.includes(name) ? process.argv[process.argv.indexOf(name) + 1] : fallback;
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
assert.equal(reference.referenceRevision, referenceRevision);
const source = path.join(output, reference.sourceRelativePath, "tsc");
const directory = path.join(output, "phase7-validation/ast-codec");
await mkdir(directory, { recursive: true });
await mkdir(path.join(source, "internal/csharpastencoder"), { recursive: true });
await mkdir(path.join(source, "cmd/csharp-ast-codec"), { recursive: true });
const originalDirectory = path.join(source, "internal/api/encoder");
const originalRecord = path.join(directory, "original-inputs.jsonl");
await writeFile(originalRecord, "");
const originalTests = [];
await copyFile(path.join(root, "csharp/oracle/ast-codec/record_test.go"), path.join(originalDirectory, "csharp_record_test.go"));
try {
    for (const name of ["encoder_test.go", "decoder_test.go"]) {
        const original = await run("git", ["show", `${referenceRevision}:tsc/internal/api/encoder/${name}`]);
        originalTests.push({ name, original });
        const recorded = original.split(/(?=^func )/m).map(part => part.startsWith("func Test")
            ? part.replaceAll("encoder.EncodeSourceFile(", "csharpRecordSource(t, ").replaceAll("encoder.EncodeNode(", "csharpRecordNode(t, ") : part).join("");
        await writeFile(path.join(originalDirectory, name), recorded);
    }
    const result = await run(go, ["-C", source, "test", "-json", "-count=1", "./internal/api/encoder"],
        { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local", CSHARP_AST_RECORD: originalRecord } });
    await writeFile(path.join(directory, "original-tests.jsonl"), result);
} finally {
    for (const { name, original } of originalTests) await writeFile(path.join(originalDirectory, name), original);
}
const instrumented = [];
for (const name of ["encoder.go", "encoder_generated.go", "stringtable.go"]) {
    const original = await run("git", ["show", `${referenceRevision}:tsc/internal/api/encoder/${name}`]);
    let changed = original;
    if (name === "encoder.go") {
        changed = changed.replace("ProtocolVersion uint8 = 8", "ProtocolVersion uint8 = 9")
            .replaceAll(/\b(?:positionMap|virtualPositions|originalPositions)\.UTF8ToUTF16\(/g, "csharpBytePosition(")
            + "\nfunc csharpBytePosition(position int) int { return position }\n";
        assert.notEqual(changed, original);
    }
    await writeFile(path.join(source, "internal/csharpastencoder", name), changed);
    instrumented.push({ name, originalSha256: sha256(original), adaptedSha256: sha256(changed) });
}
await copyFile(path.join(root, "csharp/oracle/ast-codec/main.go"), path.join(source, "cmd/csharp-ast-codec/main.go"));
await copyFile(path.join(root, "csharp/oracle/ast-codec/metadata.go"), path.join(source, "internal/ast/csharp_encoding_metadata.go"));
await copyFile(path.join(root, "csharp/oracle/binding/syntax.generated.go"), path.join(source, "internal/ast/csharp_binding_syntax.generated.go"));
const oracle = path.join(directory, "oracle.exe");
await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", oracle, "./cmd/csharp-ast-codec"],
    { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
if (process.argv.includes("--prepare-only")) process.exit(0);
const fixtures = [
    ["empty", ""],
    ["original-encoder-baseline", 'import { bar } from "bar";\nexport function foo<T, U>(a: string, b: string): any {}\nfoo();'],
    ["unicode", 'const 日本語 = "😀\\u{1f600}\\uD800"; const s = `始${日本語}終`;'],
    ["literals", 'const a = 0xAb_Cd; const b = 12_345n; const c = /a[\\d]/giu; let d = `hi\\n`;'],
    ["modifiers", 'export default abstract class C<T> extends B<T> implements I { public static readonly x = 0; abstract f<U>(x: U): T; private get p() { return 0; } }'],
    ["operators", 'const x = [++i, --i, +i, -i, ~i, !i, i++, i--, new.target, import.meta]; type T = readonly (keyof X)[]; declare const k: unique symbol;'],
    ["imports", 'import type X from "x"; import defer * as p from "p"; import q = require("q"); export type { X }; export { Y } from "y" with {type:"json"};'],
    ["references", '/// <reference path="./日本語.ts" preserve="true" />\n/// <reference types="node" resolution-mode="require" />\n/// <reference lib="es2025" />\nexport {};'],
    ["augmentation", 'import "side"; declare module "pkg" { export interface X {} } declare global { interface Window {} }'],
    ["statements", 'for (let i=0;i<3;i++) { if(i) continue; else break; } try { switch(x) { case 0: throw x; default: x++; } } catch(e) {} finally {}'],
    ["types", 'type X<T> = T extends infer U ? { readonly [K in keyof U as K]?: U[K] } : [a:string,...b:number[]]; function f<T>(this:X<T>, x:T): asserts x is NonNullable<T> {}'],
    ["jsdoc", '/** Description {@link Foo text}.\n * @template {string} T - type\n * @param {T} [x="a"] value\n * @returns {number} result\n */\nexport function f(x) { return 0; }', 1],
    ["jsdoc-typedef", '/** @typedef {Object} Thing\n * @property {string} name text\n * @property {number} [age] \n */\n/** @type {Thing} */ let x;\nmodule.exports = x;', 1],
    ["jsx", 'export const c = <div id="😀">hi <span>{1 + 2}</span>{...xs}</div>;', 4],
    ["recovery", 'function f<T(x: { [a: string]: number, }) { return [1,,2,]; }'],
    ["large-strings", `const x = "${"a".repeat(70000)}";`],
];
let cases = fixtures.flatMap(([name, text, scriptKind = 3]) => [false, true].map(bind => ({ name: `${name}/${bind ? "bound" : "parsed"}`,
    fileName: `/project/${name}.${scriptKind === 1 ? "js" : scriptKind === 4 ? "tsx" : "ts"}`, text: Buffer.from(text).toString("base64"), scriptKind, bind })));
for (const force of [false, true]) for (const jsx of [false, true]) cases.push({ name: `options/${force}/${jsx}`, fileName: "/test.tsx",
    path: "/TEST.TSX", text: Buffer.from("const x = <div/>;").toString("base64"), scriptKind: 4, force, jsx });
cases.push(...(await readFile(originalRecord, "utf8")).trim().split(/\r?\n/).map(JSON.parse).sort((a, b) => a.name.localeCompare(b.name)));
cases.push({ name: "mapped-segments-and-supplemental", fileName: "/component.vue", scriptKind: 3,
    text: Buffer.from("const x = 1;").toString("base64"), supplemental: ["/component.vue.__style.ts", "/日本語.ts"], canonical: "/root.vue",
    mapping: { original: Buffer.from("const y = 1;").toString("base64"), mapper: "mapper@1.0", virtualFileName: "/component.vue.ts", hasSpanMap: true,
        segments: [[0, 6, 0, 6, 0, 1048575], [6, 7, 6, 7, 2, 1], [7, 12, 7, 12, 0, 1048575]], directives: [[0, 5, 0, 5, 1, 2578]] } });
cases.push({ name: "jsdoc-subtree", fileName: "/subtree.js", scriptKind: 1, nodeKind: 263,
    text: Buffer.from("/** A description with {@link Foo}. @param {string} x */ function f(x) { return x; }").toString("base64") });
if (process.argv.includes("--inputs")) cases = JSON.parse(await readFile(option("--inputs"), "utf8"));
await json(path.join(directory, "inputs.json"), cases);
async function probe(command, args, label, inputs = cases) {
    const child = spawn(command, args, { windowsHide: true }); let stdout = "", stderr = "";
    child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data); child.stdin.on("error", () => {});
    child.stdin.end(inputs.map(test => JSON.stringify(test)).join("\n") + "\n");
    const code = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
    await writeFile(path.join(directory, `${label}.jsonl`), stdout); await writeFile(path.join(directory, `${label}.stderr`), stderr);
    assert.equal(code, 0, `${label} after ${stdout.split("\n").length - 1} cases: ${stderr}`);
    const results = stdout.trim().split(/\r?\n/).map(JSON.parse); assert.equal(results.length, cases.length); return results;
}
const expected = await probe(oracle, [], "reference");
const dll = path.join(output, "phase7-build/bin/TypeScript.Compatibility/release/TypeScript.Compatibility.dll");
const actual = await probe(dotnet, [dll, "--ast-codec-lines"], "candidate");
await probe(dotnet, [dll, "--ast-decode-lines"], "reference-decode", expected);
const controls = await probe(oracle, [], "candidate-syntax-control", cases.map((test, index) => ({ ...test, tree: actual[index].tree, metadata: actual[index].metadata })));
const policy = JSON.parse(await readFile(path.join(root, "csharp/tests/fixtures/protocol/ast-codec-differences.json"), "utf8"));
assert.equal(policy.referenceRevision, referenceRevision);
const permitted = (input, expected, actual) => policy.cases.some(item => item.name === input.name && item.inputHash === sha256(JSON.stringify(input))
    && item.referenceHash === sha256(expected) && item.candidateHash === sha256(actual));
let policyNegativeControls = 0;
for (const item of policy.cases) {
    const index = cases.findIndex(input => input.name === item.name); if (index < 0) continue;
    const expectedBytes = Buffer.from(expected[index].bytes, "base64"), actualBytes = Buffer.from(actual[index].bytes, "base64");
    assert(!permitted({ ...cases[index], text: cases[index].text + "AA==" }, expectedBytes, actualBytes));
    assert(!permitted(cases[index], Buffer.concat([expectedBytes, Buffer.from([0])]), actualBytes));
    assert(!permitted(cases[index], expectedBytes, Buffer.concat([actualBytes, Buffer.from([0])])));
    policyNegativeControls += 3;
}
const mismatches = [], controlMismatches = [], unexpected = [];
for (let index = 0; index < cases.length; index++) {
    const a = Buffer.from(actual[index].bytes, "base64"), b = Buffer.from(expected[index].bytes, "base64");
    if (!a.equals(b) || JSON.stringify(actual[index].kinds) !== JSON.stringify(expected[index].kinds)) {
        let first = 0; while (first < Math.min(a.length, b.length) && a[first] === b[first]) first++;
        const difference = { index, name: cases[index].name, first, actual: a.length, expected: b.length };
        mismatches.push(difference); if (!permitted(cases[index], b, a)) unexpected.push(difference);
    }
    if (actual[index].bytes !== controls[index].bytes || JSON.stringify(actual[index].kinds) !== JSON.stringify(controls[index].kinds))
        controlMismatches.push({ index, name: cases[index].name });
}
await json(path.join(directory, "summary.json"), { referenceRevision, instrumented,
    originalTests: originalTests.map(({ name, original }) => ({ name, sha256: sha256(original) })), cases: cases.length, mismatches,
    unexpected, policyNegativeControls, controlMismatches, compilerSha256: sha256(await readFile(path.join(path.dirname(dll), "TypeScript.Compiler.dll"))) });
console.log(`${cases.length} AST encodings, ${mismatches.length} strict differences, ${controlMismatches.length} differences on candidate syntax`);
if (unexpected.length || controlMismatches.length) { console.error({ unexpected: unexpected.slice(0, 10), controlMismatches }); process.exitCode = 1; }
