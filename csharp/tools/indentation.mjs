import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile, writeFile, rm } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, sha256, referenceRevision } from "./common.mjs";

const option = (name, fallback) => process.argv.includes(name) ? process.argv[process.argv.indexOf(name) + 1] : fallback;
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
assert.equal(reference.referenceRevision, referenceRevision);
const source = path.join(output, reference.sourceRelativePath, "tsc");
const directory = path.join(output, "phase7-validation/indentation");
const formatting = path.join(output, "phase7-validation/formatting");
await mkdir(directory, { recursive: true }); await mkdir(formatting, { recursive: true });
const recorded = path.join(formatting, "original-inputs.jsonl");
if (!process.argv.includes("--no-record")) {
    const file = path.join(source, "internal/fourslash/fourslash.go");
    const helper = path.join(source, "internal/fourslash/csharp_formatting_record.go");
    const authored = path.join(source, "internal/fourslash/csharp_formatting_test.go");
    const original = await readFile(file, "utf8");
    const needle = "resMsg, result, resultOk := f.client.SendRequest(t, info, params)";
    assert.equal(original.split(needle).length, 2);
    const changed = original.replace(needle, needle + "\n f.csharpRecordFormatting(t, info, params, result)")
        .replaceAll("lsproto.PositionEncodingKindUTF8", "csharpFormattingEncoding()");
    await writeFile(recorded, "");
    await copyFile(path.join(root, "csharp/oracle/formatting/record.go"), helper);
    await copyFile(path.join(root, "csharp/oracle/formatting/authored_test.go"), authored);
    await writeFile(file, changed);
    await json(path.join(formatting, "instrumentation.json"), { originalSha256: sha256(original), recordedSha256: sha256(changed) });
    try {
        let allResults = "";
        for (const encoding of ["utf-8", "utf-16"]) {
        const child = spawn(go, ["-C", source, "test", "-json", "-count=1", "-run", "^Test.*([Ff]ormat|[Ii]ndent)", "./internal/fourslash", "./internal/fourslash/tests", "./internal/format"],
            { windowsHide: true, env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local", CSHARP_FORMAT_RECORD: recorded, CSHARP_FORMAT_ENCODING: encoding } });
        let stdout = "", stderr = "";
        child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data);
        const exit = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
        allResults += stdout.trim().split(/\r?\n/).filter(Boolean).map(line => JSON.stringify({ ...JSON.parse(line), encoding })).join("\n") + "\n";
        await writeFile(path.join(formatting, "original-tests.jsonl"), allResults); await writeFile(path.join(formatting, `original-${encoding}.stderr`), stderr);
        assert.equal(exit, 0, stderr);
        }
    } finally { await writeFile(file, original); await rm(helper); await rm(authored); }
}
if (process.argv.includes("--record-only")) process.exit(0);
const records = (await readFile(recorded, "utf8")).trim().split(/\r?\n/).filter(Boolean).map(JSON.parse);
const unique = new Map();
for (const record of records) {
    const key = JSON.stringify([record.file, record.text, record.options]);
    unique.set(key, { name: record.name, file: record.file, text: record.text, options: record.options });
}
let cases = [...unique.values()].map(input => ({ ...input, kind: /\.tsx$/i.test(input.file) ? 4 : /\.jsx$/i.test(input.file) ? 2 : /\.[cm]?js$/i.test(input.file) ? 1 : 3 }));
const authored = [
    ["named-imports", 'import {\n    type SomeInterface,\n} from "./exports.js";'],
    ["tabs", ' \tfunction 名(\n \t\t参数: number,\n\t b = 2) {\n  \treturn 参数 + b;\n}\n'],
    ["unicode", '/** 日本語 😀\r\n * contents\r\n */\r\nfunction f() {\r\n\tconst 名前 = `a${42}z`;\r\n    return 名前;\r\n}\r\n'],
    ["unicode-space", '\u3000\tconst x = {\n\u00a0\t a: 1,\n b: 2\n};\u2028const y = 1;\u2029'],
    ["comments", '/* start\n * middle\n * end */\nfunction f() { // trailing\n/* unclosed\n *\n'],
    ["incomplete", 'function f(a,,b:) { let = ; return { x: }; }\ninterface I { a: ; }\nif (x)\n  if (y) foo(); else\n bar();'],
    ["jsx", 'const x = <a-b.c>\n  <div a="😀">text\n {x &&\n <span/>}\n </div>\n</a-b.c>;'],
    ["conditional", 'const x = (() => {\n return true;\n})() ? {\n a: 1\n} : (\n f(0)\n);'],
    ["switch", 'switch (x) {\n case 1:\n  if (y)\n   return;\n  f();\n default:\n  break;\n}\n'],
    ["literals", 'const x = /a[ ]+/g;\nconst y = "text😀";\nconst z = `start\n${x}\nend`;'],
];
const variants = [{}, { IndentStyle: 0, BaseIndentSize: 3 }, { IndentStyle: 1 }, { IndentSize: 2, TabSize: 2, BaseIndentSize: 1 },
    { IndentSize: 0, TabSize: 0 }, { IndentSwitchCase: false, IndentMultiLineObjectLiteralBeginningOnBlankLine: true }];
for (const [name, text] of authored) for (const [index, options] of variants.entries())
    cases.push({ name: `authored-${name}-${index}`, file: name === "jsx" ? "/test.tsx" : "/test.ts", text, options, kind: name === "jsx" ? 4 : 3 });
const filter = option("--filter", ""); if (filter) cases = cases.filter(input => new RegExp(filter).test(input.name));
await json(path.join(directory, "inputs.json"), cases);
const command = path.join(source, "cmd/csharp-formatting"); await mkdir(command, { recursive: true });
await copyFile(path.join(root, "csharp/oracle/formatting/main.go"), path.join(command, "main.go"));
await copyFile(path.join(root, "csharp/oracle/formatting/insertion.go"), path.join(command, "insertion.go"));
const executable = path.join(directory, "oracle.exe");
await run(go, ["-C", source, "build", "-o", executable, "./cmd/csharp-formatting"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
if (!process.argv.includes("--no-build")) await run(dotnet, ["build", "csharp/tests/TypeScript.Compatibility", "-c", "Release", "--no-restore", "--artifacts-path", "built/csharp/phase7-build"]);
const assembly = path.join(output, "phase7-build/bin/TypeScript.Compatibility/release/TypeScript.Compatibility.dll");
async function lines(command, args, label) {
    const child = spawn(command, args, { windowsHide: true }); let stdout = "", stderr = "";
    child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data);
    child.stdin.on("error", () => {}); child.stdin.end(cases.map(input => JSON.stringify(input)).join("\n") + "\n");
    const exit = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
    await writeFile(path.join(directory, `${label}.stdout`), stdout); await writeFile(path.join(directory, `${label}.stderr`), stderr);
    assert.equal(exit, 0, stderr); return stdout.trim().split(/\r?\n/).filter(Boolean).map(JSON.parse);
}
const runs = await Promise.allSettled([lines(executable, [], "reference"), lines(dotnet, [assembly, "--indentation-lines"], "candidate")]);
for (const result of runs) if (result.status === "rejected") console.error(result.reason);
assert.ok(runs.every(result => result.status === "fulfilled"));
const [expected, actual] = runs.map(result => result.value);
assert.equal(expected.length, cases.length); assert.equal(actual.length, cases.length);
let queries = 0; const differences = [];
for (let i = 0; i < cases.length; i++) for (const method of ["positions", "nodes"]) {
    const a = actual[i][method], e = expected[i][method];
    if (a.length !== e.length) { differences.push({ name: cases[i].name, caseIndex: i, method, expectedLength: e.length, actualLength: a.length }); continue; }
    for (let j = 0; j < e.length; j++) {
        queries += method === "positions" ? 2 : 4;
        try { assert.deepEqual(a[j], e[j]); }
        catch { differences.push({ name: cases[i].name, caseIndex: i, method, index: j, expected: e[j], actual: a[j] }); }
    }
}
await json(path.join(directory, "differences.json"), differences);
const summary = { referenceRevision, cases: cases.length, queries, differences: differences.length,
    recordedFormattingRequests: records.length, compilerSha256: sha256(await readFile(path.join(path.dirname(assembly), "TypeScript.Compiler.dll"))) };
await json(path.join(directory, "summary.json"), summary); console.log(JSON.stringify(summary));
if (differences.length) process.exitCode = 1;
