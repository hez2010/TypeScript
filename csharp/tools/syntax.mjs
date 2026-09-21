import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import {
    copyFile,
    mkdir,
    readFile,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import {
    json,
    output,
    root,
    run,
    sha256,
} from "./common.mjs";
const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rc.2.26470.103-win-x64/dotnet.exe");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc");
await mkdir(path.join(source, "cmd/syntax-probe"), { recursive: true });
await copyFile(path.join(root, "csharp/oracle/syntax/main.go"), path.join(source, "cmd/syntax-probe/main.go"));
const oracle = path.join(output, "syntax-oracle.exe");
// The existing test-directive package locates fixtures using runtime.Caller.
// Its development oracle must retain source paths; shipped C# binaries do not depend on it.
await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", oracle, "./cmd/syntax-probe"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
const managed = process.argv.includes("--managed");
if (!process.argv.includes("--no-build")) {
    const args = managed ? ["build", "TypeScript.slnx", "-c", "Release", "--no-restore"] : ["publish", "tests/TypeScript.Compatibility", "-r", "win-x64", "-c", "Release", "-p:IlcInstructionSet=native", "-p:RestoreLockedMode=true", "-o", path.join(output, "phase2-native")];
    await writeFile(path.join(output, "phase2-build.log"), await run(dotnet, args, { cwd: path.join(root, "csharp") }));
}
const candidate = managed ? dotnet : path.join(output, "phase2-native/TypeScript.Compatibility.exe");
const args = managed ? [path.join(root, "csharp/tests/TypeScript.Compatibility/bin/Release/net11.0/TypeScript.Compatibility.dll"), "--scan-lines"] : ["--scan-lines"];
async function probe(command, args, cases) {
    return await new Promise((resolve, reject) => {
        const child = spawn(command, args, { windowsHide: true });
        const stdout = [], stderr = [];
        child.stdout.on("data", bytes => stdout.push(bytes));
        child.stderr.on("data", bytes => stderr.push(bytes));
        let inputError;
        child.on("error", reject);
        child.stdin.on("error", error => {
            inputError = error;
        });
        child.on("close", code => {
            const lines = Buffer.concat(stdout).toString().trim().split(/\r?\n/).filter(Boolean);
            if (code || inputError) reject(new Error(`${command}; next case ${cases[lines.length]?.name}\n${Buffer.concat(stderr)}\n${inputError ?? ""}`));
            else resolve(lines.map(line => JSON.parse(line)));
        });
        child.stdin.end(cases.map(c => JSON.stringify(c)).join("\n") + "\n");
    });
}
const texts = [
    "",
    "const text = '日本語😀';\nlet $foo = 1.5e-10;",
    "a ? .2 : b?.x ??= 2;",
    "0 01 077 08 0xabc 0B111 0o13 1_000 0_1 1__0 0b_1_ 1.2n 1e3n 0xffn 0b11n 1abc 0x",
    "'\\uD800' '\\uD83D\\uDE00' '\\u{D83D}\\u{DE00}' '\\xG' '\\u{}' '\\u{110000}' '\\8' '\\077' \"line\n",
    "`hi ${name} end` `line\r\n日本語\\u{1F600}`",
    "\\u0061 \\u{1D400} #private #\\u0061 # \\u0030 \\a",
    "/** @deprecated x {@link A} */ const x=1;\n// @ts-expect-error\n/* @ts-ignore */ x; /*\n @ts-ignore */ y;",
    "#!/usr/bin/env node\nconst ok = true; #!bad",
    "<<<<<<< ours\nx;\n=======\ny;\n>>>>>>> theirs\nz;",
    "\uFEFFa\u0085b\u200Bc\u2028d\u2029e\0",
    "'text' /* unclosed",
    '/* 😀日本語 */ 名字\r\n= "𝑨";',
];
let cases = texts.flatMap((text, i) => [false, true].map(trivia => ({ name: `lexical-${i}-${trivia}`, text: Buffer.from(text).toString("base64"), trivia })));
for (const [mode, text] of [["greater", "a >> b >>> c >>= 1 >= 2;"], ["regex", "/[a-z]+/g; /a\\/b/u; /unterminated; )"], ["jsx", "<p>hello {x}</p>"], ["jsdoc", " * @param {string} foo-bar text\n @returns {X} `x`"]]) cases.push({ name: mode, mode, text: Buffer.from(text).toString("base64") });
if (process.argv.includes("--parse")) {
    const fixtures = [
        "",
        "const n: number = 42; let value = '😀日本語';",
        "export type A<T extends string = string> = T | number;",
        "interface Box<T> { readonly value?: T; get(): T; set(value: T): void; [key: string]: T; }",
        "type Tuple = [first: string, optional?: number, ...rest: boolean[]]; type M<T> = { readonly [K in keyof T as `get${K}`]?: T[K] };",
        "type F = <T>(value: T) => T; type C = new (value: string) => Object; type X<T> = T extends string ? true : false;",
        "function add(a: number, b = 1): number { if (a) return a + b; else return 0; }",
        "class A<T> extends B<T> implements C { readonly value!: T; constructor(public x: T) {} get v(): T { return this.value; } set v(x: T) { this.value = x; } f<U>(x: U): U { return x; } }",
        "import { type A, b as c } from './a'; export { c }; export * from './a'; export type { A };",
        "const x = { value: 1, foo, ...rest, method(x) { return x; }, get z() { return 1; } }; const y = [1, , ...items];",
        "const f = (x: number): number => x + 1; const g = async x => await x;",
        "for (let i = 0; i < 10; i++) { continue; } for (const x of xs) { break; } while (x) x--; do x++; while (x < 10);",
        "switch (x) { case 1: break; default: foo(); } try { f(); } catch (e) { throw e; } finally { clean(); }",
        "namespace N { export interface I { x: number } } enum E { A, B = 2 }",
        "const x = a?.b[c]?.(value) ?? fallback; const y = new A<string>(1); const z = tag`hello ${value}!`;",
        "type X = import('./a').A<string>; type Q = typeof foo; type R = readonly number[]; type P = ((string | number))[];",
        'const view = <div data-name="a">hello <span>{value}</span></div>; const frag = <><A {...props} /></>;',
    ];
    cases = fixtures.map((text, i) => ({ name: `parse-${i}.ts${i === 16 ? "x" : ""}`, text: Buffer.from(text).toString("base64"), mode: "parse", jsx: i === 16 }));
}
if (process.argv.includes("--corpus")) {
    const files = (await run("rg", ["--files", "tsc/testdata/tests/cases", "tsc/internal/bundled/libs"])).split(/\r?\n/).filter(p => /\.(?:tsx?|jsx?|json)$/.test(p)).sort();
    cases.push(...files.map(file => ({ name: file.replaceAll("\\", "/"), path: path.join(root, file), jsx: /\.(tsx|jsx)$/.test(file), mode: process.argv.includes("--parse") ? "parse" : "" })));
}
if (process.argv.includes("--units")) {
    cases = cases.filter(c => c.path).map(c => ({ ...c, mode: "units" }));
    cases.unshift({ name: "units.ts", mode: "units", text: Buffer.from("// @strict: true\n// @filename: a.ts\nconst a = 1;\n// @link: a.ts -> link.ts\n// @filename: 日本語.ts\n// @noopen: true\nexport {};\n").toString("base64") });
}
if (process.argv.includes("--filter")) cases = cases.filter(c => c.name.includes(option("--filter")));
if (process.argv.includes("--limit")) cases = cases.slice(0, Number(option("--limit")));
if (process.argv.includes("--expanded")) {
    const units = await probe(candidate, args, cases.map(c => ({ ...c, mode: "units", details: true })));
    cases = cases.flatMap((c, i) => units[i].details[0].map((unit, index) => ({ name: `${c.name}::${index}`, fileName: Buffer.from(unit[0], "base64").toString("utf8"), text: unit[1], mode: "parse" })));
}
const expected = await probe(oracle, [], cases);
const actual = await probe(candidate, args, cases);
assert.equal(expected.length, cases.length);
assert.equal(actual.length, cases.length);
const failures = cases.filter((_, i) => expected[i].hash !== actual[i].hash);
const inputHashes = [];
for (const c of cases) inputHashes.push({ ...c, path: c.path && path.relative(root, c.path).replaceAll("\\", "/"), sourceSha256: sha256(c.path ? await readFile(c.path) : Buffer.from(c.text, "base64")) });
const summary = {
    timestamp: new Date().toISOString(),
    suite: process.argv.includes("--units") ? "directives" : process.argv.includes("--parse") ? "parser" : "scanner",
    expanded: process.argv.includes("--expanded"),
    candidate: managed ? "CoreCLR development check" : "NativeAOT",
    sdk: await run(dotnet, ["--version"], { cwd: path.join(root, "csharp") }),
    runtime: managed ? null : await run(candidate, ["--native-check"]),
    instructionSet: managed ? null : "native",
    optimizationPreference: "Speed",
    candidateSha256: sha256(await readFile(managed ? args[0] : candidate)),
    inputSha256: sha256(JSON.stringify(inputHashes)),
    cases: cases.length,
    records: expected.reduce((sum, c) => sum + c.tokens, 0),
    passed: cases.length - failures.length,
    failed: failures.length,
    failures: failures.map(c => c.name),
    oracleSha256: sha256(await readFile(oracle)),
};
await json(path.join(output, "syntax-summary.json"), summary);
if (process.argv.includes("--record")) await json(path.join(root, "csharp/compatibility/evidence", option("--record") + ".json"), summary);
if (failures.length) {
    const details = failures.slice(0, 30).map(c => ({ ...c, details: true }));
    const wanted = await probe(oracle, [], details), got = await probe(candidate, args, details);
    await json(path.join(output, "syntax-differences.json"), details.map((c, i) => ({ case: c, expected: wanted[i], actual: got[i] })));
    for (let i = 0; i < Math.min(10, details.length); i++) {
        const a = wanted[i].details[0], b = got[i].details[0];
        const mismatch = a.findIndex((t, j) => JSON.stringify(t) !== JSON.stringify(b[j]));
        console.log(JSON.stringify({ name: details[i].name, token: mismatch, expected: a[mismatch], actual: b[mismatch], expectedDiagnostics: wanted[i].details[1], actualDiagnostics: got[i].details[1] }).slice(0, 1500));
    }
    throw new Error(`${failures.length}/${cases.length} syntax comparisons failed (built/csharp/syntax-differences.json)`);
}
console.log(`Passed ${cases.length} ${process.argv.includes("--units") ? "directive" : process.argv.includes("--parse") ? "parser" : "scanner"} cases and ${expected.reduce((sum, c) => sum + c.tokens, 0)} records (${managed ? "CoreCLR" : "NativeAOT"})`);
