import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, sha256, referenceRevision } from "./common.mjs";
import { printingCases } from "./api-printing-cases.mjs";

const option = (name, fallback) => process.argv.includes(name) ? process.argv[process.argv.indexOf(name) + 1] : fallback;
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
assert.equal(reference.referenceRevision, referenceRevision);
const source = path.join(output, reference.sourceRelativePath, "tsc");
const directory = path.join(output, "phase7-validation/insertion"); await mkdir(directory, { recursive: true });
const target = "// 日本語😀\nfunction target() {\n    if (true) {\n        target();\n    }\n}\n";
const positions = [0, target.indexOf("function"), target.indexOf("    if"), target.indexOf("        target"), target.indexOf("    }")]
    .map(pos => Buffer.byteLength(target.slice(0, pos)));
const variants = [{}, { IndentSize: 2, TabSize: 2, BaseIndentSize: 1, NewLineCharacter: "\r\n" },
    { IndentSize: 3, TabSize: 3, ConvertTabsToSpaces: false }, { IndentSize: 0, TabSize: 0 },
    { Semicolons: "insert", InsertSpaceBeforeTypeAnnotation: true, InsertSpaceAfterOpeningAndBeforeClosingNonemptyBraces: false },
    { Semicolons: "remove", InsertSpaceBeforeAndAfterBinaryOperators: false, PlaceOpenBraceOnNewLineForFunctions: true, PlaceOpenBraceOnNewLineForControlBlocks: true }];
let cases = printingCases().filter(c => c.requests[0].method === "createSourceFile").flatMap(c => variants.map((options, index) => ({
    name: `${c.name}-${index}`, insertion: true, file: c.requests[0].params.options.scriptKind === 4 ? "/test.tsx" : "/test.ts",
    text: c.requests[0].params.sourceText, kind: c.requests[0].params.options.scriptKind, target, positions, options,
})));
const filter = option("--filter", ""); if (filter) cases = cases.filter(input => new RegExp(filter).test(input.name));
await json(path.join(directory, "inputs.json"), cases);
const command = path.join(source, "cmd/csharp-formatting"); await mkdir(command, { recursive: true });
for (const name of ["main.go", "insertion.go"])
    await copyFile(path.join(root, "csharp/oracle/formatting", name), path.join(command, name));
const executable = path.join(directory, "oracle.exe");
await run(go, ["-C", source, "build", "-o", executable, "./cmd/csharp-formatting"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
if (!process.argv.includes("--no-build")) await run(dotnet, ["build", "csharp/tests/TypeScript.Compatibility", "-c", "Release", "--no-restore", "--artifacts-path", "built/csharp/phase7-build"]);
const assembly = path.join(output, "phase7-build/bin/TypeScript.Compatibility/release/TypeScript.Compatibility.dll");
async function lines(command, args, label) {
    const child = spawn(command, args, { windowsHide: true, signal: AbortSignal.timeout(180_000) }); let stdout = "", stderr = "";
    child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data);
    child.stdin.on("error", () => {}); child.stdin.end(cases.map(input => JSON.stringify(input)).join("\n") + "\n");
    const exit = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
    await writeFile(path.join(directory, `${label}.stdout`), stdout); await writeFile(path.join(directory, `${label}.stderr`), stderr);
    assert.equal(exit, 0, stderr); return stdout.trim().split(/\r?\n/).filter(Boolean).map(JSON.parse);
}
const runs = await Promise.allSettled([lines(executable, [], "reference"), lines(dotnet, [assembly, "--insertion-lines"], "candidate")]);
for (const result of runs) if (result.status === "rejected") console.error(result.reason);
assert.ok(runs.every(result => result.status === "fulfilled"));
const [original, actual] = runs.map(result => result.value);
// The reference's generic embedded-statement visitor wraps a null else branch in
// an unpositioned block. A position-only copy must preserve absent children.
const trackerPath = path.join(source, "internal/printer/changetrackerwriter.go");
const tracker = await readFile(trackerPath, "utf8");
const corrected = tracker.replace(/VisitToken:\s+ct\.assignPositionsToNodeWorker,/,
    "$&\n            VisitEmbeddedStatement: ct.assignPositionsToNodeWorker,");
assert.notEqual(tracker, corrected);
const correctedExecutable = path.join(directory, "oracle-corrected.exe");
try {
    await writeFile(trackerPath, corrected);
    await run(go, ["-C", source, "build", "-o", correctedExecutable, "./cmd/csharp-formatting"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
} finally { await writeFile(trackerPath, tracker); }
const expected = await lines(correctedExecutable, [], "reference-corrected");
const corrections = [];
for (let i = 0; i < cases.length; i++) for (let j = 0; j < original[i].length; j++) {
    for (const field of ["text", "ranges", "formatted"]) {
        try { assert.deepEqual(expected[i][j][field], original[i][j][field]); }
        catch {
            assert.match(cases[i].name, /^printing-empty-blocks-\d$/);
            assert.notEqual(field, "text", "Assigning positions must not change printed text");
            corrections.push({ name: cases[i].name, node: j, field, original: original[i][j][field], corrected: expected[i][j][field] });
        }
    }
}
await json(path.join(directory, "reference-correction.json"), {
    referenceRevision, policy: "preserve-absent-embedded-statements", originalSha256: sha256(tracker), correctedSha256: sha256(corrected),
    explanation: "AssignPositionsToNode installs VisitNode, which visitEmbeddedStatement passes through liftToBlock even for a nil ElseStatement. This creates a spurious empty block at (-1,-1), and FormatNodeGivenIndentation then fails its non-synthesized list-position assertion. An explicit VisitEmbeddedStatement hook copies the child as-is and preserves nil. The source checkout is restored after building the control.",
    corrections,
});
assert.equal(expected.length, cases.length); assert.equal(actual.length, cases.length);
const differences = []; let queries = 0;
for (let i = 0; i < cases.length; i++) {
    assert.equal(actual[i].length, expected[i].length, cases[i].name);
    for (let j = 0; j < expected[i].length; j++) for (const field of ["text", "ranges", "formatted"]) {
        queries++;
        try { assert.deepEqual(actual[i][j][field], expected[i][j][field]); }
        catch { differences.push({ name: cases[i].name, node: j, field, expected: expected[i][j][field], actual: actual[i][j][field] }); }
    }
}
await json(path.join(directory, "differences.json"), differences);
const summary = { referenceRevision, cases: cases.length, queries, differences: differences.length, referenceCorrections: corrections.length,
    compilerSha256: sha256(await readFile(path.join(path.dirname(assembly), "TypeScript.Compiler.dll"))) };
await json(path.join(directory, "summary.json"), summary); console.log(JSON.stringify(summary));
if (differences.length) process.exitCode = 1;
