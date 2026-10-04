import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import {
    copyFile,
    readFile,
    writeFile,
} from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";
import {
    json,
    output,
    root,
    run,
    sha256,
} from "./common.mjs";

const option = name => {
    const i = process.argv.indexOf(name);
    return i < 0 ? undefined : process.argv[i + 1];
};
const go = option("--go") ?? "go";
const dotnet = option("--dotnet") ?? "dotnet";
const fixture = (name, contents) => ({ name, text: Buffer.from(contents).toString("base64") });
const sourceFiles = await Promise.all(["values", "imports", "unicode"].map(async name => fixture(`/phase1/${name}.ts`, await readFile(path.join(root, `csharp/tests/fixtures/phase1/${name}.ts`)))));
const cases = [
    { name: "modules", files: sourceFiles },
    { name: "invalid-assignment", files: [fixture("/bad.ts", "const result: string = 42;\n")] },
    { name: "unknown-type", files: [fixture("/unknown.ts", "type Result = Missing;\n")] },
    { name: "malformed", files: [fixture("/malformed.ts", "type Broken = ;\n")] },
    { name: "inference", files: [fixture("/inference.ts", "let n = 42; let s = 'text'; let b = true; const fixed = 42; const absent = undefined; type Later = Forward; type Forward = | string | number; type Signed = -42 | 42; const positive = +42;\n")] },
    { name: "escaped-literals", files: [fixture("/escapes.ts", "export type Text = '\\n\\t\\x41\\u0042\\u{1F600}' | '\\ud800'; const value: Text = '\\n\\t\\x41\\u0042\\u{1F600}'; const decimal: number = 1.25e3;\n")] },
    { name: "type-only-value", files: [fixture("/a.ts", "export const value = 42;"), fixture("/b.ts", "import type { value } from './a'; const wrong = value;")] },
    { name: "circular-aliases", files: [fixture("/cycles.ts", "type Outer = A; type A = B; type B = A;")] },
    { name: "circular-variable", files: [fixture("/cycles.ts", "const value = value;")] },
    { name: "value-as-type", files: [fixture("/namespace.ts", "const Value = 42; type Wrong = Value;")] },
    { name: "type-as-value", files: [fixture("/namespace.ts", "type Value = string; const wrong = Value;")] },
    { name: "unterminated", files: [fixture("/unterminated.ts", "type Unterminated = 'missing\n")] },
    { name: "missing-import", files: [fixture("/missing.ts", "import { Missing } from './absent';")] },
    { name: "deep", files: [fixture("/deep.ts", `type Deep = ${"(".repeat(500)}string${")".repeat(500)};`)] },
    {
        name: "large-graph",
        files: [
            fixture("/graph/base.ts", Array.from({ length: 100 }, (_, i) => `export type T${i} = string | ${i}; export const v${i}: T${i} = ${i};`).join("\n")),
            fixture("/graph/use.ts", Array.from({ length: 100 }, (_, i) => `import { type T${i}, v${i} } from "./base"; export const result${i}: T${i} = v${i};`).join("\n")),
        ],
    },
];
const inputs = path.join(output, "pipeline-input.json");
const expectedPath = path.join(output, "pipeline-go.json");
const actualPath = path.join(output, "pipeline-native.json");
await json(inputs, { cases });
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const referenceSource = path.join(output, reference.sourceRelativePath);
await copyFile(path.join(root, "csharp/oracle/main.go"), path.join(referenceSource, "tsc/cmd/rewrite-probe/main.go"));
const suffix = process.platform === "win32" ? ".exe" : "";
const probe = path.join(output, "oracle-probe" + suffix);
await run(go, ["-C", path.join(referenceSource, "tsc"), "build", "-mod=readonly", "-buildvcs=false", "-trimpath", "-o", probe, "./cmd/rewrite-probe"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
await new Promise((resolve, reject) => {
    const child = spawn(probe, [], { windowsHide: true });
    const stdout = [], stderr = [];
    child.stdout.on("data", data => stdout.push(data));
    child.stderr.on("data", data => stderr.push(data));
    child.on("error", reject);
    child.stdin.on("error", reject);
    child.on("close", async code => {
        if (code) reject(new Error(Buffer.concat(stderr).toString()));
        else {
            await writeFile(expectedPath, Buffer.concat(stdout));
            resolve();
        }
    });
    child.stdin.end(JSON.stringify({ cases }));
});
const publish = await run(dotnet, ["publish", "tests/TypeScript.Compatibility", "-p:PublishAot=true", "-r", "win-x64", "-c", "Release", "-p:IlcInstructionSet=native", "-o", path.join(output, "native-host")], { cwd: path.join(root, "csharp") });
await writeFile(path.join(output, "pipeline-publish.log"), publish);
const executable = path.join(output, "native-host/TypeScript.Compatibility" + suffix);
const runtime = await run(executable, ["--native-check"]);
await run(executable, [process.argv.includes("--benchmark") ? "--pipeline-benchmark" : "--pipeline", inputs, actualPath]);
const expected = JSON.parse(await readFile(expectedPath, "utf8")).cases;
const actual = JSON.parse(await readFile(actualPath, "utf8"));
// The package's own imports map chooses source enum modules under this condition.
const { decodeNode } = await import(pathToFileURL(path.join(root, "packages/typescript/src/api/node/node.ts")));
function tree(packet) {
    const bytes = Buffer.from(packet, "base64");
    assert.equal(bytes.readUInt32LE(0) >>> 24, 9, "C# experiment packets use UTF-8 byte positions");
    const rootNode = decodeNode(bytes);
    const stack = [[rootNode, -1]];
    const records = [];
    while (stack.length) {
        const [node, parent] = stack.pop();
        const index = records.length;
        records.push({ kind: node.kind, pos: node.pos, end: node.end, parent, ...(typeof node.text === "string" ? { text: node.text } : {}) });
        const children = [];
        node.forEachChild(child => {
            children.push(child);
        });
        for (let i = children.length - 1; i >= 0; i--) stack.push([children[i], index]);
    }
    return { records, name: rootNode.fileName, text: rootNode.text, imports: rootNode.imports.map(node => node.text), externalModuleKind: rootNode.externalModuleIndicator?.kind };
}
let nodes = 0, relations = 0;
for (let i = 0; i < cases.length; i++) {
    const wanted = expected[i], observed = actual.cases[i];
    assert.equal(observed.name, wanted.name);
    const diagnostics = list => list.map(({ file, code, pos, length }) => ({ file, code, pos, length })).sort((a, b) => a.file.localeCompare(b.file) || a.pos - b.pos || a.code - b.code);
    assert.deepEqual(diagnostics(observed.diagnostics), diagnostics(wanted.diagnostics), `Diagnostics: ${wanted.name}`);
    if (wanted.diagnostics.length === 0) {
        assert.deepEqual(observed.symbolNames, wanted.symbolNames, `Bound symbols: ${wanted.name}`);
        assert.deepEqual(observed.relations, wanted.relations, `Type relations: ${wanted.name}`);
        relations += observed.relations.length;
    }
    for (let j = 0; j < wanted.files.length; j++) {
        assert.deepEqual(observed.files[j].locals, wanted.files[j].locals, `Locals: ${wanted.files[j].name}`);
        const decoded = tree(observed.files[j].wire);
        if (!wanted.diagnostics.length) assert.deepEqual(decoded, tree(wanted.files[j].wire), `Decoded AST: ${wanted.files[j].name}`);
        nodes += decoded.records.length;
    }
}
assert.equal(actual.deepAndCancellation, true);
const summary = { timestamp: new Date().toISOString(), sdk: await run(dotnet, ["--version"], { cwd: path.join(root, "csharp") }), runtime, cpu: os.cpus()[0].model, instructionSet: "native", optimizationPreference: "Speed", candidateSha256: sha256(await readFile(executable)), candidateBytes: (await readFile(executable)).length, inputSha256: sha256(await readFile(inputs)), oracleSha256: sha256(await readFile(expectedPath)), outputSha256: sha256(await readFile(actualPath)), cases: cases.length, comparedNodes: nodes, relations, decoder: "unchanged packages/typescript/src/api/node/node.ts", deepAndCancellation: true };
await json(path.join(output, "pipeline-summary.json"), summary);
console.log(JSON.stringify(summary));
