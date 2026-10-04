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
const oracle = path.join(output, "mappers-oracle.exe"), managed = process.argv.includes("--managed"), native = path.join(output, "phase3-native");
const candidate = managed ? dotnet : option("--candidate", path.join(native, "TypeScript.Compatibility.exe")), dll = path.join(root, "csharp/tests/TypeScript.Compatibility/bin/Release/net11.0/TypeScript.Compatibility.dll");
const args = [...managed ? [dll] : [], "--mapper-codec-lines"];
await mkdir(path.join(source, "cmd/mappers-probe"), { recursive: true });
await copyFile(path.join(root, "csharp/oracle/mappers/main.go"), path.join(source, "cmd/mappers-probe/main.go"));
await copyFile(path.join(root, "csharp/oracle/mappers/bridge.go"), path.join(source, "internal/contentmapper/csharp_bridge.go"));
await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", oracle, "./cmd/mappers-probe"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
if (!process.argv.includes("--no-build")) {
    await run(
        dotnet,
        managed ? ["build", "tests/TypeScript.Compatibility", "-c", "Release", "--no-restore"]
            : ["publish", "tests/TypeScript.Compatibility", "-p:PublishAot=true", "-c", "Release", "-r", "win-x64", "-p:IlcInstructionSet=native", "-o", native],
        { cwd: path.join(root, "csharp"), env: { ...process.env, DOTNET_ROOT: path.dirname(dotnet), PATH: path.dirname(dotnet) + path.delimiter + process.env.PATH } },
    );
}
let cases = [];
for (const options of [{}, { target: "esnext", module: "nodenext", strict: true, jsx: "react-jsx", moduleResolution: "nodenext" }, { target: "es2015", module: "preserve", noEmit: false, checkJs: false, maxNodeModuleJsDepth: 0, checkers: 0 }, { outDir: "日本語/😀", rootDirs: ["src", "other"], paths: { "alias/*": ["src/*"] }, types: [], lib: ["es2015"] }, { paths: { "😀/*": ["日本語/😀/*"], 'quoted"key': ["a\nb"] }, types: ["😀"], outDir: 'dir"😀' }]) for (const declared of [[], Object.keys(options), ["strict", "target", "jsx", "module", "moduleResolution"], ["target", "target", "missing"]]) for (const mapperOptions of [undefined, null, {}, { language: "日本語😀", ampersand: "<&>" }]) for (const dynamic of [false, true]) cases.push({ operation: "identity", name: "mapper", version: "1.2", configIdentity: "config-日本語", options, declared, mapperOptions, dynamic });
for (const original of ["", "x", "日本語😀", "a\n😀b"]) {
    for (const encoding of ["utf-8"]) {
        const length = Buffer.byteLength(original);
        const base = { operation: "decode", original, encoding, source: "fixture" };
        for (let start = -1; start <= length + 1; start++) {
            for (let end = start; end <= length + 1; end++) {
                cases.push({ ...base, result: { text: original, extension: ".ts", mappings: [[start, end - start, start, end - start, 0]] } });
                cases.push({ ...base, result: { text: "", extension: ".ts", diagnostics: [{ start, length: end - start, code: 42, messageText: "problem" }] } });
            }
        }
        for (const extension of [".ts", ".tsx", ".js", ".jsx", ".mts", ".cts", ".mjs", ".cjs", ".json", "", ".d.ts", ".vue"]) cases.push({ ...base, result: { text: original, extension } });
        for (const policy of [-1, 0, 1, 2]) for (const index of [undefined, -1, 0, 1]) for (const unused of [[], [{ code: 7, messageText: "unused" }]]) cases.push({ ...base, result: { text: original, extension: ".ts", diagnosticDirectives: { unusedExpectDirectiveDiagnostics: unused, directives: [[0, length, 0, length, policy, ...index === undefined ? [] : [index]]] } } });
        cases.push({ ...base, result: { text: original, extension: ".ts", supplemental: [{ text: original, extension: ".js", mappings: [[0, length, 0, length, 0]] }] } });
        for (const diagnostic of [{}, { start: 0 }, { start: 0, length: 0 }, { code: 1 }, { messageText: "message" }]) cases.push({ ...base, result: { text: "", extension: ".ts", diagnostics: [diagnostic] } });
    }
}
cases = cases.map((c, i) => ({ caseName: `${c.operation}:${i}`, ...c }));
async function probe(command, args) {
    return new Promise((resolve, reject) => {
        const child = spawn(command, args, { windowsHide: true }), out = [], errors = [];
        child.stdout.on("data", b => out.push(b));
        child.stderr.on("data", b => errors.push(b));
        child.on("error", reject);
        child.stdin.on("error", () => {});
        child.on("close", code => {
            if (code) reject(new Error(`${command}: ${Buffer.concat(errors)}`));
            else resolve(Buffer.concat(out).toString().trim().split(/\r?\n/).map(x => JSON.parse(x)));
        });
        child.stdin.end(cases.map(c => JSON.stringify(c)).join("\n") + "\n");
    });
}
const expected = await probe(oracle, []), actual = await probe(candidate, args);
assert.equal(expected.length, cases.length);
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
await json(path.join(output, "mappers-failures.json"), differences);
const summary = { timestamp: new Date().toISOString(), referenceRevision, managed, positionEncoding: "utf-8", cases: cases.length, passed: cases.length - differences.length, failed: differences.length, inputSha256: sha256(JSON.stringify(cases)), oracleSha256: sha256(await readFile(oracle)), candidateSha256: sha256(await readFile(managed ? dll : candidate)) };
await json(path.join(output, "mappers-summary.json"), summary);
if (option("--record")) await json(path.join(root, `csharp/compatibility/evidence/${option("--record")}.json`), summary);
console.log(summary);
if (differences.length) {
    console.log(differences.slice(0, 6));
    process.exitCode = 1;
}
