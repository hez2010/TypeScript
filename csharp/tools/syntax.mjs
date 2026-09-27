import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import {
    copyFile,
    mkdir,
    readFile,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import { gzipSync } from "node:zlib";
import {
    json,
    output,
    root,
    run,
    sha256,
} from "./common.mjs";
import {
    classifySyntaxDifference,
    independentParserVersion,
} from "./compare-syntax.mjs";
const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const dotnet = option("--dotnet", process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, "dotnet.exe") : "dotnet");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc");
await mkdir(path.join(source, "cmd/syntax-probe"), { recursive: true });
await copyFile(path.join(root, "csharp/oracle/syntax/main.go"), path.join(source, "cmd/syntax-probe/main.go"));
await copyFile(path.join(root, "csharp/oracle/syntax/scalars.generated.go"), path.join(source, "internal/ast/csharp_scalars.generated.go"));
const oracle = path.join(output, "syntax-oracle.exe");
// The existing test-directive package locates fixtures using runtime.Caller.
// Its development oracle must retain source paths; shipped C# binaries do not depend on it.
await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", oracle, "./cmd/syntax-probe"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
const managed = process.argv.includes("--managed");
if (!process.argv.includes("--no-build")) {
    const args = managed ? ["build", "TypeScript.slnx", "-c", "Release", "--no-restore"] : ["publish", "tests/TypeScript.Compatibility", "-r", "win-x64", "-c", "Release", "-p:IlcInstructionSet=native", "-p:RestoreLockedMode=true", "-o", path.join(output, "phase2-native")];
    await writeFile(path.join(output, "phase2-build.log"), await run(dotnet, args, { cwd: path.join(root, "csharp") }));
}
const candidate = managed ? dotnet : option("--candidate", path.join(output, "phase2-native/TypeScript.Compatibility.exe"));
const args = managed ? [path.join(root, "csharp/tests/TypeScript.Compatibility/bin/Release/net11.0/TypeScript.Compatibility.dll"), "--scan-lines"] : ["--scan-lines"];
async function probe(command, args, cases) {
    return await new Promise((resolve, reject) => {
        const child = spawn(command, args, { windowsHide: true });
        const stdout = [], stderr = [];
        let traced = "";
        child.stdout.on("data", bytes => {
            stdout.push(bytes);
            if (process.argv.includes("--trace")) {
                traced += bytes.toString();
                const lines = traced.split(/\r?\n/);
                traced = lines.pop();
                for (const line of lines) if (line) console.error(command, JSON.parse(line).name);
            }
        });
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
const parserSuite = process.argv.includes("--parse") && !process.argv.includes("--units");
const details = await Promise.all(failures.slice(0, parserSuite ? failures.length : Number(option("--details-limit", 30))).map(async c => ({ ...c, text: c.text ?? (await readFile(c.path)).toString("base64"), details: true })));
const wanted = details.length ? await probe(oracle, [], details) : [];
const got = details.length ? await probe(candidate, args, details) : [];
const differences = details.map((c, i) => ({ case: c, expected: wanted[i], actual: got[i], policy: parserSuite ? classifySyntaxDifference(c, wanted[i], got[i]) : null }));
await json(path.join(output, "syntax-differences.json"), differences);
const unresolved = parserSuite ? differences.filter(d => !d.policy) : failures;
const ledger = await Promise.all(differences.map(async ({ case: c, expected: a, actual: b, policy }) => {
    const expectedRecords = a.details[0], actualRecords = b.details[0];
    const first = expectedRecords.findIndex((record, i) => JSON.stringify(record) !== JSON.stringify(actualRecords[i]));
    return {
        name: c.name,
        fileName: c.fileName,
        sourceSha256: sha256(c.path ? await readFile(c.path) : Buffer.from(c.text, "base64")),
        policy,
        expectedSha256: a.hash,
        actualSha256: b.hash,
        expectedRecords: expectedRecords.length,
        actualRecords: actualRecords.length,
        firstDifferentRecord: first,
        expectedRecord: expectedRecords[first],
        actualRecord: actualRecords[first],
        expectedDiagnostics: a.details[1],
        actualDiagnostics: b.details[1],
        expectedJavaScriptDiagnostics: a.details[2],
        actualJavaScriptDiagnostics: b.details[2],
    };
}));
const inputHashes = [];
for (const c of cases) inputHashes.push({ ...c, path: c.path && path.relative(root, c.path).replaceAll("\\", "/"), sourceSha256: sha256(c.path ? await readFile(c.path) : Buffer.from(c.text, "base64")) });
const summary = {
    timestamp: new Date().toISOString(),
    recordFormat: process.argv.includes("--units") ? "virtual files, options, symlinks and directory" : parserSuite ? "kind-range-flags-text-children-scalars-lists; parse and JavaScript diagnostic buckets" : "kind-fullstart-start-end-tokenflags-value; scanner diagnostics and empty JavaScript diagnostic bucket",
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
    runtimeAsync: true,
    comparison: parserSuite && !process.argv.includes("--strict") ? "semantic policies with strict audit" : "strict",
    independentParserVersion: parserSuite ? independentParserVersion : null,
    passed: cases.length - unresolved.length,
    failed: unresolved.length,
    strictPassed: cases.length - failures.length,
    strictFailed: failures.length,
    permittedDifferences: differences.filter(d => d.policy).length,
    failures: unresolved.map(d => d.case?.name ?? d.name),
    oracleSha256: sha256(await readFile(oracle)),
};
await json(path.join(output, "syntax-summary.json"), summary);
await json(path.join(output, "syntax-difference-ledger.json"), { reference: reference.referenceRevision, independentParserVersion, cases: ledger });
if (process.argv.includes("--record")) {
    const name = option("--record");
    const reusedLedger = option("--reuse-parser-ledger");
    if (reusedLedger) {
        assert(parserSuite, "Only parser audits have a reusable syntax ledger");
        const bytes = await readFile(path.join(root, "csharp/compatibility/evidence", reusedLedger));
        const baseline = JSON.parse(bytes);
        assert.deepEqual(JSON.parse(JSON.stringify(ledger)), baseline.cases, "Parser differences changed; a new audit is required");
        summary.inheritedParserLedger = reusedLedger;
        summary.inheritedParserLedgerSha256 = sha256(bytes);
    }
    await json(path.join(root, "csharp/compatibility/evidence", name + ".json"), summary);
    if (parserSuite && !reusedLedger) {
        const outputsFile = name + "-differences.outputs.json.gz";
        const archive = gzipSync(JSON.stringify({ reference: reference.referenceRevision, differences }));
        await writeFile(path.join(root, "csharp/compatibility/evidence", outputsFile), archive);
        await json(path.join(root, "csharp/compatibility/evidence", name + "-differences.json"), { reference: reference.referenceRevision, independentParserVersion, outputsFile, outputsSha256: sha256(archive), cases: ledger });
    }
}
if (unresolved.length || process.argv.includes("--strict") && failures.length) {
    const report = differences.filter(d => !d.policy || process.argv.includes("--strict")).slice(0, 10);
    for (const d of report) {
        const a = d.expected.details[0], b = d.actual.details[0];
        const mismatch = a.findIndex((t, j) => JSON.stringify(t) !== JSON.stringify(b[j]));
        console.log(JSON.stringify({ name: d.case.name, token: mismatch, expected: a[mismatch], actual: b[mismatch], expectedDiagnostics: d.expected.details[1], actualDiagnostics: d.actual.details[1] }).slice(0, 1500));
    }
    throw new Error(`${unresolved.length}/${cases.length} unresolved syntax comparisons; ${failures.length} strict differences (built/csharp/syntax-differences.json)`);
}
console.log(`Passed ${cases.length} ${process.argv.includes("--units") ? "directive" : process.argv.includes("--parse") ? "parser" : "scanner"} cases and ${expected.reduce((sum, c) => sum + c.tokens, 0)} records (${managed ? "CoreCLR" : "NativeAOT"}); ${failures.length} documented strict differences`);
