import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, referenceRevision, sha256 } from "./common.mjs";

const option = (name, fallback) => process.argv.includes(name) ? process.argv[process.argv.indexOf(name) + 1] : fallback;
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8")); assert.equal(reference.referenceRevision, referenceRevision);
const source = path.join(output, reference.sourceRelativePath, "tsc");
const directory = path.join(output, "phase7-validation/navigation"); await mkdir(directory, { recursive: true });
const cmd = path.join(source, "cmd/csharp-navigation"); await mkdir(cmd, { recursive: true });
await copyFile(path.join(root, "csharp/oracle/navigation/main.go"), path.join(cmd, "main.go"));
const executable = path.join(directory, "oracle.exe");
await run(go, ["-C", source, "build", "-o", executable, "./cmd/csharp-navigation"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
const original = await run("git", ["show", `${referenceRevision}:tsc/internal/astnav/tokens_test.go`]);
const fixture = await run("git", ["show", `${referenceRevision}:tsc/testdata/fixtures/services/mapCode.ts`]);
let cases = [{ name: "original-mapCode", file: "/file.ts", kind: 3, text: fixture },
    ...[...original.matchAll(/fileText := `([^`]+)`/g)].map((m, i) => ({ name: `original-literal-${i}`, file: i < 2 ? "/test.js" : "/file.ts", kind: i < 2 ? 1 : 3, text: m[1] })),
    ...[...original.matchAll(/fileContent:\s*`([^`]+)`/g)].map((m, i) => ({ name: `original-unit-${i}`, file: "/file.ts", kind: 3, text: m[1] })),
    { name: "unicode", file: "/unicode.ts", kind: 3, text: '/** 日本語 😀 */ export const 名 = `a${42}z`; 名;\r\n' },
    { name: "jsx", file: "/jsx.tsx", kind: 4, text: 'const x = <div>{foo}<span a="😀"/> x << y</div>; const f = <T,>(x:T) => x;' },
    { name: "documentation", file: "/doc.js", kind: 1, text: '/** @template T\n * @param {T} x doc {@link f}\n * @returns {T}\n */ function f(x) { return /** @type {T} */ (x); }\n/** @type {{\n * a: number, b: string\n * }} */ let a;' },
    { name: "missing", file: "/missing.ts", kind: 3, text: 'function f(a,,b:) { let = ; return { x: }; }\ninterface I { a: ; }' },
    { name: "trivia", file: "/trivia.ts", kind: 3, text: '#!/usr/bin/env node\n<<<<<<< HEAD\nconst x = 1;\n=======\nlet y = 2;\n>>>>>>> main\n/** unclosed\n * @param {T} x' },
];
const filter = option("--filter", ""); if (filter) cases = cases.filter(input => new RegExp(filter).test(input.name));
await json(path.join(directory, "inputs.json"), cases);
async function lines(command, args, label, inputs = cases) {
    const child = spawn(command, args, { cwd: root, windowsHide: true }); let stdout = "", stderr = "";
    child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data);
    child.stdin.on("error", () => {}); child.stdin.end(inputs.map(input => JSON.stringify(input)).join("\n") + "\n");
    const exit = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
    await writeFile(path.join(directory, `${label}.stdout`), stdout); await writeFile(path.join(directory, `${label}.stderr`), stderr);
    assert.equal(exit, 0, stderr); return stdout.trim().split(/\r?\n/).map(JSON.parse);
}
const dll = path.join(output, "phase7-build/bin/TypeScript.Compatibility/release/TypeScript.Compatibility.dll");
const runs = await Promise.allSettled([lines(executable, [], "reference"), lines(dotnet, [dll, "--navigation-lines"], "candidate")]);
for (const result of runs) if (result.status === "rejected") console.error(result.reason);
assert.ok(runs.every(result => result.status === "fulfilled"));
const [expected, actual] = runs.map(result => result.value);
assert.equal(expected.length, cases.length); assert.equal(actual.length, cases.length);
const differences = [];
let queries = 0, panics = 0;
for (let i = 0; i < cases.length; i++) {
    assert.equal(actual[i].length, expected[i].length);
    for (let j = 0; j < expected[i].length; j++) for (let method = 0; method < 6; method++) {
        queries++; if (expected[i][j][method]?.error) panics++;
        try { assert.deepEqual(actual[i][j][method], expected[i][j][method]); }
        catch { differences.push({ name: cases[i].name, position: cases[i].positions?.[j] ?? j, method, expected: expected[i][j][method], actual: actual[i][j][method] }); }
    }
}
await json(path.join(directory, "differences.json"), differences);
const policy = JSON.parse(await readFile(path.join(root, "csharp/tests/fixtures/protocol/navigation-differences.json"), "utf8"));
assert.equal(policy.referenceRevision, referenceRevision);
const unexplained = differences.filter(difference => !policy.cases.some(entry => entry.name === difference.name
    && entry.inputHash === sha256(JSON.stringify(cases.find(input => input.name === difference.name)))
    && entry.differences.some(permitted => JSON.stringify(permitted) === JSON.stringify(difference))));
const controlCases = cases.filter(input => differences.some(difference => difference.name === input.name)
    && policy.cases.some(entry => entry.name === input.name));
let controlQueries = 0;
if (controlCases.length) {
    const controls = await lines(executable, [], "source-range-control", controlCases.map(input => ({ ...input, templateSourceRanges: true })));
    for (let i = 0; i < controlCases.length; i++) {
        const index = cases.indexOf(controlCases[i]); assert.deepEqual(controls[i], actual[index]); controlQueries += controls[i].length * 6;
        const entry = policy.cases.find(entry => entry.name === controlCases[i].name);
        assert.deepEqual(differences.filter(difference => difference.name === entry.name), entry.differences);
    }
}
const summary = { referenceRevision, cases: cases.length, queries, referencePanics: panics, differences: differences.length,
    unexplainedDifferences: unexplained.length, sourceRangeControlQueries: controlQueries,
    originalTestsSha256: sha256(original), originalFixtureSha256: sha256(fixture), compilerSha256: sha256(await readFile(path.join(path.dirname(dll), "TypeScript.Compiler.dll"))) };
await json(path.join(directory, "summary.json"), summary); console.log(JSON.stringify(summary));
assert.equal(unexplained.length, 0, `${unexplained.length} unexplained navigation differences`);
