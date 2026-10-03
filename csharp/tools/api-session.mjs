import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, referenceRevision, sha256 } from "./common.mjs";
import { checkerCases, normalizeCheckerResponses } from "./api-checker-cases.mjs";
import { diagnosticCases } from "./api-diagnostic-cases.mjs";
import { emissionCases } from "./api-emission-cases.mjs";
import { configurationCases } from "./api-configuration-cases.mjs";
import { resolutionCases } from "./api-resolution-cases.mjs";
import { printingCases } from "./api-printing-cases.mjs";
import { documentationCases } from "./api-documentation-cases.mjs";
import { lspCases } from "./api-lsp-cases.mjs";
import { referenceCases } from "./api-reference-cases.mjs";
import { serviceCases } from "./api-service-cases.mjs";

const option = (name, fallback) => process.argv.includes(name) ? process.argv[process.argv.indexOf(name) + 1] : fallback;
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8")); assert.equal(reference.referenceRevision, referenceRevision);
const source = path.join(output, reference.sourceRelativePath, "tsc");
const directory = path.resolve(option("--directory", path.join(output, "phase7-validation/api-session"))); await mkdir(directory, { recursive: true });
const adapted = path.join(source, "internal/csharpapi"); await mkdir(adapted, { recursive: true });
const codec = path.join(source, "internal/csharpastencoder"); await mkdir(codec, { recursive: true });
const adaptations = [];
for (const name of ["encoder.go", "encoder_generated.go", "stringtable.go", "decoder.go", "decoder_generated.go"]) {
    const original = await run("git", ["show", `${referenceRevision}:tsc/internal/api/encoder/${name}`]);
    const changed = name === "encoder.go" ? original.replace("ProtocolVersion uint8 = 8", "ProtocolVersion uint8 = 9")
        .replaceAll(/\b(?:positionMap|virtualPositions|originalPositions)\.UTF8ToUTF16\(/g, "csharpBytePosition(")
        + "\nfunc csharpBytePosition(position int) int { return position }\n" : original;
    await writeFile(path.join(codec, name), changed);
    adaptations.push({ file: `encoder/${name}`, originalSha256: sha256(original), adaptedSha256: sha256(changed) });
}
for (const name of ["session.go", "proto.go", "enum_values_generated.go", "stringer_generated.go", "protocol_msgpack.go", "callbackfs.go", "server.go"]) {
    const original = await run("git", ["show", `${referenceRevision}:tsc/internal/api/${name}`]);
    let changed = original.replaceAll('"github.com/microsoft/TypeScript/tsc/internal/api/encoder"', '"github.com/microsoft/TypeScript/tsc/internal/csharpastencoder"')
        .replaceAll(/\b\w+\.GetPositionMap\(\)/g, "csharpPositionMap()")
        .replaceAll("ast.ComputePositionMap(originalText)", "csharpPositionMap()");
    if (name === "session.go") changed = changed.replace('"fmt"', '"fmt"\n "github.com/zeebo/xxh3"')
        .replace("data, _, err := encoder.EncodeSourceFile(sourceFile)", "sourceFile.Hash = xxh3.HashString128(sourceFile.Text())\n data, _, err := encoder.EncodeSourceFile(sourceFile)")
        + "\ntype csharpPositionIdentity struct{}\nfunc csharpPositionMap() csharpPositionIdentity { return csharpPositionIdentity{} }\nfunc (csharpPositionIdentity) UTF8ToUTF16(value int) int { return value }\nfunc (csharpPositionIdentity) UTF16ToUTF8(value int) int { return value }\n";
    if (name === "proto.go") changed = changed.replaceAll("scanner.GetECMALineAndUTF16CharacterOfPosition(", "csharpDiagnosticPosition(")
        .replace("if sourceFile, ok := file.(*ast.SourceFile); ok {", "if _, ok := file.(*ast.SourceFile); ok {")
        .replaceAll("core.UTF16Len(file.Text()[:pos])", "len(file.Text()[:pos])").replaceAll("core.UTF16Len(file.Text()[:end])", "len(file.Text()[:end])")
        + "\nfunc csharpDiagnosticPosition(file ast.SourceFileLike, pos int) (int, core.UTF16Offset) { line, offset := scanner.GetECMALineAndByteOffsetOfPosition(file, pos); return line, core.UTF16Offset(offset) }\n";
    await writeFile(path.join(adapted, name), changed);
    adaptations.push({ file: name, originalSha256: sha256(original), adaptedSha256: sha256(changed) });
}
await json(path.join(directory, "oracle-adaptations.json"), adaptations);
const cmd = path.join(source, "cmd/csharp-api"); await mkdir(cmd, { recursive: true });
await copyFile(path.join(root, "csharp/oracle/api/main.go"), path.join(cmd, "main.go"));
const executable = path.join(directory, "oracle.exe");
await run(go, ["-C", source, "build", "-o", executable, "./cmd/csharp-api"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
const ref = name => ({ $ref: name });
const request = (method, params = {}, save) => ({ method, params, ...(save ? { save } : {}) });
const snap = name => ({ snapshot: ref(`${name}.snapshot`) });
const project = (name, index = 0) => ({ ...snap(name), project: ref(`${name}.operation.createdPrograms.${index}`) });
const files = { "/p/a.ts": "export const a = 1;", "/p/b.ts": 'import { a } from "./a"; export const b = a;',
    "/p/tsconfig.json": '{"compilerOptions":{"noLib":true},"files":["a.ts","b.ts"]}' };
let cases = [
    { name: "simple-and-batch", files: {}, requests: [request("initialize"), request("ping"), request("echo", { text: "😀" }),
        request("batchRequests", { requests: [request("ping"), request("unknown"), request("batchRequests"), request("echo", [1, true, null])] }),
        request("batchRequests", { requests: Array.from({ length: 5 }, () => request("ping")), maxResponseBytesPerPage: 100 }, "page"),
        request("batchRequests", { continuationToken: ref("page.continuationToken") }),
        request("batchRequests", { continuationToken: ref("page.continuationToken") }), request("release", {}), request("getCurrentLanguageServerSnapshot") ] },
    { name: "independent-synthetic-snapshots", files, requests: [
        request("createSnapshot", { createPrograms: [{ rootFiles: ["/p/a.ts"], compilerOptions: { noLib: true } }] }, "a"),
        request("getSourceFileNames", project("a")), request("getSourceFile", { ...project("a"), file: "/p/a.ts" }),
        request("updateSnapshot", { ...snap("a"), changes: { fileSystem: { kind: "layer", files: { "/p/a.ts": "export const a = 2;" } } } }, "dirty"),
        request("getSourceFile", { ...snap("dirty"), project: ref("a.operation.createdPrograms.0"), file: "/p/a.ts" }),
        request("updateSnapshot", { ...snap("dirty"), changes: { ensurePrograms: true } }, "fresh"),
        request("getSourceFile", { ...snap("fresh"), project: ref("a.operation.createdPrograms.0"), file: "/p/a.ts" }),
        request("getSourceFile", { ...project("a"), file: "/p/a.ts" }),
        request("release", snap("a")), request("getSourceFileNames", project("a")),
        request("updateSnapshot", { ...snap("fresh"), changes: { removePrograms: [ref("a.operation.createdPrograms.0")] } }),
        request("release", snap("dirty")), request("release", snap("fresh")) ] },
    { name: "configured-opens-and-references", files, requests: [
        request("createSnapshot", { openProjects: ["/p/tsconfig.json"], openFiles: [{ uri: "file:///p/a.ts" }, "/p/b.ts"] }, "a"),
        request("getDefaultProjectForFile", { ...snap("a"), file: "/p/a.ts" }),
        request("updateSnapshot", { ...snap("a"), changes: { openProjects: ["/p/tsconfig.json"], openFiles: ["/p/a.ts"] } }, "b"),
        request("updateSnapshot", { ...snap("b"), changes: { closeProjects: ["/p/tsconfig.json"], closeFiles: ["/p/a.ts", "/p/b.ts"] } }),
        request("getSourceFileNames", { ...snap("a"), project: "/p/tsconfig.json" }),
        request("getSourceFile", { ...snap("a"), project: "/p/tsconfig.json", file: { uri: "file:///p/b.ts" } }) ] },
    { name: "reconfigure-and-branch", files, requests: [
        request("createSnapshot", { createPrograms: [{ rootFiles: ["/p/a.ts"], compilerOptions: { noLib: true } },
            { rootFiles: ["/p/b.ts"], compilerOptions: { noLib: true, strict: false } }] }, "a"),
        request("updateSnapshot", { ...snap("a"), changes: { reconfigurePrograms: [{ id: ref("a.operation.createdPrograms.0"), rootFiles: ["/p/b.ts"], compilerOptions: { noLib: true } }] } }, "b"),
        request("getSourceFileNames", { ...snap("b"), project: ref("a.operation.createdPrograms.0") }), request("getSourceFileNames", project("a")),
        request("updateSnapshot", { ...snap("a"), changes: { createPrograms: [], openFiles: [] } }),
        request("getSourceFile", { ...project("a"), file: "/missing.ts" }) ] },
    { name: "full-filesystem", files, requests: [
        request("createSnapshot", { fileSystem: { kind: "full", files: { "/p/tsconfig.json": '{"compilerOptions":{"noLib":true},"files":["a.ts"]}', "/p/a.ts": "export const a = 3;" } }, openProjects: ["/p/tsconfig.json"] }, "a"),
        request("getSourceFileNames", { ...snap("a"), project: "/p/tsconfig.json" }),
        request("updateSnapshot", { ...snap("a"), changes: { fileSystem: { kind: "full", files: { "/p/tsconfig.json": '{"compilerOptions":{"noLib":true},"files":["b.ts"]}', "/p/b.ts": "export const b = 4;" } }, ensurePrograms: true } }, "b"),
        request("getSourceFileNames", { ...snap("b"), project: "/p/tsconfig.json" }),
        request("getSourceFileNames", { ...snap("a"), project: "/p/tsconfig.json" }) ] },
    { name: "create-source-and-invalid-requests", files, requests: [
        request("createSourceFile", { fileName: "/unicode.ts", sourceText: 'const 日本語 = "😀";', options: { scriptKind: 3 } }),
        request("createSourceFileFromFile", { fileName: "/p/a.ts" }),
        request("createSourceFile", { fileName: "/a.ts", sourceText: "", options: { scriptKind: 42 } }),
        request("createSnapshot", { ensurePrograms: false }), request("createSnapshot", { fileSystem: { kind: "unknown", files: {} } }),
        request("createSnapshot", { createPrograms: [null] }), request("getSourceFileNames", { snapshot: 999, project: "/x" }) ] }
].concat(checkerCases(), diagnosticCases(), emissionCases(), configurationCases(), resolutionCases(), printingCases(), documentationCases(), lspCases(), referenceCases(), serviceCases()).map(input => ({ cwd: "/", caseSensitive: true, ...input }));
cases.push({ name: "profiling-errors-and-files", cwd: "/", caseSensitive: true, files, requests: [
    request("startCPUProfile", null), request("startCPUProfile"), request("startCPUProfile", { dir: "" }),
    request("saveHeapProfile", null), request("saveHeapProfile"), request("saveHeapProfile", { dir: "" }), request("stopCPUProfile"),
    request("startCPUProfile", { dir: "$profileDirectory" }), request("startCPUProfile", { dir: "$profileDirectory" }),
    request("createSnapshot", { createPrograms: [{ rootFiles: ["/p/a.ts"], compilerOptions: { noLib: true } }] }, "profileSnapshot"),
    request("saveHeapProfile", { dir: "$profileDirectory" }), request("stopCPUProfile"), request("stopCPUProfile"), request("release", snap("profileSnapshot")),
] });
cases.push({ name: "project-id-aliases", cwd: "/", caseSensitive: true, files, requests: [
    request("createSnapshot", { createPrograms: [{ rootFiles: ["/p/a.ts"], compilerOptions: { noLib: true } }] }, "ids"),
    ...["1", "01", "+1", "0001"].map(suffix => request("getSourceFileNames", { ...snap("ids"), project: `/dev/null/synthetic/${suffix}` })),
    request("updateSnapshot", { ...snap("ids"), changes: { reconfigurePrograms: [{ id: "/dev/null/synthetic/01", rootFiles: ["/p/b.ts"], compilerOptions: { noLib: true } }] } }),
    request("updateSnapshot", { ...snap("ids"), changes: { removePrograms: ["/dev/null/synthetic/0001"] } }),
] });
const filter = option("--filter", ""); if (filter) cases = cases.filter(input => new RegExp(filter).test(input.name));
await json(path.join(directory, "inputs.json"), cases);
async function lines(command, args, label) {
    const child = spawn(command, args, { cwd: root, windowsHide: true, signal: AbortSignal.timeout(180_000) }); let stdout = "", stderr = "";
    child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data);
    child.stdin.on("error", () => {});
    const inputs = cases.map(input => ({ ...input, requests: input.requests.map(request => request.params?.dir === "$profileDirectory"
        ? { ...request, params: { ...request.params, dir: path.join(directory, `${label}-profiles`) } } : request) }));
    await json(path.join(directory, `${label}-inputs.json`), inputs);
    child.stdin.end(inputs.map(input => JSON.stringify(input)).join("\n") + "\n");
    const exit = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
    await writeFile(path.join(directory, `${label}.stdout`), stdout); await writeFile(path.join(directory, `${label}.stderr`), stderr);
    assert.equal(exit, 0, stderr); return stdout.trim().split(/\r?\n/).map(JSON.parse);
}
const dll = path.join(option("--managed-directory", path.join(output, "phase7-build/bin/TypeScript.Compatibility/release")), "TypeScript.Compatibility.dll");
const runs = await Promise.allSettled([lines(executable, [], "reference"), lines(dotnet, [dll, "--api-session-lines"], "candidate")]);
for (const result of runs) if (result.status === "rejected") console.error(result.reason);
assert.ok(runs.every(result => result.status === "fulfilled"));
const [expected, actual] = runs.map(result => result.value);
assert.equal(expected.length, cases.length); assert.equal(actual.length, cases.length);
await json(path.join(directory, "reference.json"), expected); await json(path.join(directory, "candidate.json"), actual);
const profileFiles = [];
for (let index = 0; index < cases.length; index++) if (cases[index].name === "profiling-errors-and-files") {
    for (const [label, responses] of [["reference", expected[index]], ["candidate", actual[index]]]) {
        for (let position = 0; position < responses.length; position++) if (responses[position].result?.file) {
            const result = responses[position].result, kind = cases[index].requests[position].method === "saveHeapProfile" ? "heap" : "cpu";
            assert.deepEqual(Object.keys(result), ["file"]); assert.equal(path.dirname(result.file), path.join(directory, `${label}-profiles`));
            assert.match(path.basename(result.file), new RegExp(`^\\d+-\\d+-${kind}profile\\.pb\\.gz$`));
            const raw = await run(go, ["tool", "pprof", "-raw", result.file]); await writeFile(`${result.file}.raw.txt`, raw);
            assert.ok(raw.includes(kind === "heap" ? "inuse_space/bytes" : "cpu/nanoseconds"));
            profileFiles.push({ label, method: cases[index].requests[position].method, file: result.file, sha256: sha256(await readFile(result.file)) });
            result.file = `<${kind}-profile>`;
        }
    }
}
if (profileFiles.length) await json(path.join(directory, "profiles.json"), { files: profileFiles,
    policy: "Each result has exactly one file property, an existing file in the requested directory, a PID/timestamp/kind basename, and the expected pprof sample type. Only the dynamic file path is normalized; profile attribution is a documented runtime difference." });
function normalize(responses) {
    const snapshots = new Map(); let next = 0;
    function visit(value) {
        if (!value || typeof value !== "object") return value;
        if (Array.isArray(value)) return value.map(visit);
        return Object.fromEntries(Object.entries(value).map(([key, child]) => {
            // Runtime-specific JSON decoder context precedes the same contract error.
            if (key === "error" && typeof child === "string" && child.startsWith("api: invalid request: failed to unmarshal *api.CreateSnapshotParams:")
                && child.endsWith("ensurePrograms must be true or an array of project IDs")) child = "api: invalid request: ensurePrograms must be true or an array of project IDs";
            if (key === "snapshot") { if (!snapshots.has(child)) snapshots.set(child, ++next); child = snapshots.get(child); }
            if (key === "continuationToken") child = child.replace(/api-session-\d+/, "api-session-X");
            return [key, visit(child)];
        }));
    }
    return visit(responses);
}
const differences = [];
for (let i = 0; i < cases.length; i++) {
    const checkerHandles = cases[i].requests.some(request => request.method === "getSymbolAtPosition" || request.method === "getSymbolOfSourceFile" || request.method === "getCompletionsAtPosition");
    const left = checkerHandles ? normalizeCheckerResponses(expected[i], cases[i].requests) : normalize(expected[i]);
    const right = checkerHandles ? normalizeCheckerResponses(actual[i], cases[i].requests) : normalize(actual[i]);
    for (let j = 0; j < cases[i].requests.length; j++)
        try { assert.deepEqual(right[j], left[j]); } catch { differences.push({ case: i, name: cases[i].name, request: j, method: cases[i].requests[j].method, expected: left[j], actual: right[j] }); }
}
await json(path.join(directory, "differences.json"), differences);
const summary = { referenceRevision, cases: cases.length, requests: cases.reduce((n, input) => n + input.requests.length, 0), differences: differences.length,
    compilerSha256: sha256(await readFile(path.join(path.dirname(dll), "TypeScript.Compiler.dll"))), coordinateContract: "UTF-8 / AST v9; isolated oracle adapters listed separately" };
await json(path.join(directory, "summary.json"), summary); console.log(JSON.stringify(summary));
assert.equal(differences.length, 0, `${differences.length} API requests differ`);
