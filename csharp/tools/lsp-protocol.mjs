import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { readFile, writeFile, mkdir, rm } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, referenceRevision, sha256 } from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const directory = path.resolve(option("--directory", path.join(output, "phase7-validation/lsp-protocol")));
await mkdir(directory, { recursive: true });
const model = JSON.parse(await readFile(path.join(output, "phase7-validation/lsp-protocol-schema.json"), "utf8"));
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
assert.equal(reference.referenceRevision, referenceRevision);
const source = path.join(output, reference.sourceRelativePath, "tsc");
assert.equal(sha256((await readFile(path.join(source, "internal/lsp/lsproto/lsp_generated.go"), "utf8")).replaceAll("\r", "")), model.sourceSha256);
const go = "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe";
const dotnet = "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe";
const dll = path.resolve(option("--managed-directory", path.join(output, "phase7-tags-build/bin/TypeScript.Compatibility/release")), "TypeScript.Compatibility.dll");
const env = { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" };
const originalFile = path.join(source, "internal/lsp/lsproto/lsp_json_test.go");
const helperFile = path.join(source, "internal/lsp/lsproto/csharp_record_test.go");
const original = await readFile(originalFile, "utf8");
const recordFile = path.join(directory, "original-records.jsonl");
if (!process.argv.includes("--no-prepare")) {
    await writeFile(recordFile, "");
    const recorder = `package lsproto
import ("os"; "sync"; "reflect"; "strings"; stdjson "encoding/json"; "github.com/microsoft/TypeScript/tsc/internal/json")
var csharpRecordMutex sync.Mutex
func recordUnmarshal(data []byte, target any) error {
    err := json.Unmarshal(data, target)
    record := struct { Type string \`json:"type"\`; Raw string \`json:"raw"\`; Accepted bool \`json:"accepted"\` }{
        strings.TrimPrefix(reflect.TypeOf(target).Elem().String(), "lsproto."), string(data), err == nil }
    payload, failure := stdjson.Marshal(record); if failure != nil { panic(failure) }
    csharpRecordMutex.Lock(); defer csharpRecordMutex.Unlock()
    file, failure := os.OpenFile(os.Getenv("CSHARP_LSP_RECORD"), os.O_APPEND|os.O_WRONLY, 0600); if failure != nil { panic(failure) }
    defer file.Close(); if _, failure := file.Write(append(payload, '\\n')); failure != nil { panic(failure) }
    return err
}
`;
    await writeFile(helperFile, recorder);
    try {
        await writeFile(originalFile, original.replaceAll("json.Unmarshal(", "recordUnmarshal("));
        const events = await run(go, ["-C", source, "test", "-json", "./internal/lsp/lsproto", "-count=1"], { env: { ...env, CSHARP_LSP_RECORD: recordFile } });
        await writeFile(path.join(directory, "original-tests.jsonl"), events + "\n");
    } finally { await writeFile(originalFile, original); await rm(helperFile); }
}
const recordings = (await readFile(recordFile, "utf8")).trim().split("\n").map(JSON.parse);
for (const input of recordings) assert.ok(model.schemas[input.type], `Missing recorded type ${input.type}; add it to the generator roots`);
const goType = type => type.replace(/\b[A-Z]\w*/g, name => "lsproto." + name);
const oracleSource = `package main
import ("bufio"; "os"; "fmt"; stdjson "encoding/json"; "github.com/microsoft/TypeScript/tsc/internal/json"; "github.com/microsoft/TypeScript/tsc/internal/lsp/lsproto")
func main() {
    scanner := bufio.NewScanner(os.Stdin); scanner.Buffer(make([]byte, 4096), 64*1024*1024)
    encoder := stdjson.NewEncoder(os.Stdout)
    for scanner.Scan() {
        var input struct { Type string; Method string; Raw string }; if err := stdjson.Unmarshal(scanner.Bytes(), &input); err != nil { panic(err) }
        var err error
        if input.Method != "" {
            message := &lsproto.RequestMessage{Params: json.Value(input.Raw)}
            switch input.Method {
${Object.entries(model.requests).map(([method, type]) => `            case ${JSON.stringify(method)}: _, err = message.UnmarshalParams[${goType(type)}]()` ).join("\n")}
            default: panic(input.Method)
            }
        } else {
            var target any
            switch input.Type {
${Object.keys(model.schemas).filter(type => type !== "NoParams").map(type => `            case ${JSON.stringify(type)}: target = new(${goType(type)})`).join("\n")}
            default: panic(input.Type)
            }
            err = json.Unmarshal([]byte(input.Raw), target)
        }
        result := map[string]any{"accepted": err == nil}; if err != nil { result["error"] = fmt.Sprint(err) }
        if err := encoder.Encode(result); err != nil { panic(err) }
    }
    if err := scanner.Err(); err != nil { panic(err) }
}
`;
const oracle = path.join(directory, "oracle.exe");
if (!process.argv.includes("--no-prepare")) {
    const target = path.join(source, "cmd/csharp-lsp-protocol"); await mkdir(target, { recursive: true });
    await writeFile(path.join(target, "main.go"), oracleSource);
    await writeFile(path.join(directory, "oracle.go"), oracleSource);
    await run(go, ["-C", source, "build", "-o", oracle, "./cmd/csharp-lsp-protocol"], { env });
}
function minimal(type, seen = new Set()) {
    if (seen.has(type)) return null;
    const next = new Set(seen); next.add(type); const s = model.schemas[type];
    if (["pointer", "ref"].includes(s.kind)) return minimal(s.type, next);
    if (s.kind === "object") return Object.fromEntries(s.fields.filter(f => f.required).map(f => [f.name, minimal(f.type, next)]));
    if (s.kind === "string") return "";
    if (["uint32", "int32", "float64"].includes(s.kind)) return 0;
    if (s.kind === "bool") return false;
    if (["array"].includes(s.kind)) return [];
    if (s.kind === "tuple") return [0, 0];
    if (["emptyObject", "map"].includes(s.kind)) return {};
    if (s.kind === "literal") return JSON.parse(s.value);
    if (s.kind === "anyOf") return minimal(s.types[0], next);
    if (s.cases) {
        const branch = Object.values(s.cases).find(v => v.kind === "ref" && v.type !== "Null");
        return branch ? minimal(branch.type, next) : null;
    }
    return null;
}
const cases = recordings.map((input, i) => ({ name: `original-${i}`, type: input.type, raw: input.raw }));
function add(name, type, value) { cases.push({ name, type, raw: JSON.stringify(value) }); }
for (const [type, schema] of Object.entries(model.schemas)) {
    if (type === "NoParams") continue;
    add(`${type}/minimal`, type, minimal(type));
    for (const value of [null, false, true, 0, -1, 1.5, 2147483648, 4294967296, "", "x", [], [null], {}, { unknown: null }]) add(`${type}/kind-${JSON.stringify(value)}`, type, value);
    if (schema.kind === "object") {
        const base = minimal(type);
        for (const field of schema.fields) {
            const omitted = { ...base }; delete omitted[field.name]; add(`${type}/${field.name}/omitted`, type, omitted);
            for (const value of [minimal(field.type), null, false, 0, "x", [], [null], {}]) add(`${type}/${field.name}/${JSON.stringify(value)}`, type, { ...base, [field.name]: value });
        }
    }
}
for (const [method, type] of Object.entries(model.requests)) {
    for (const raw of ["", "null", "false", "0", '"x"', "[]", "{}", JSON.stringify(minimal(type))]) cases.push({ name: `${method}/params/${raw}`, method, raw });
}
for (const [type, raw] of [
    ["Position", '{"line":0,"line":1,"character":0}'], ["Position", '{"line":0,"l\\u0069ne":1,"character":0}'],
    ["Position", '{"l\\u0069ne":0,"character":0}'], ["Position", '{"line":0,"character":0,"unknown":{"x":1,"x":2}}'],
    ["TextEditOrInsertReplaceEdit", '{"insert":null,"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":0}},"newText":"x"}'],
    ["TextEditOrInsertReplaceEdit", '{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":0}},"insert":null,"newText":"x"}'],
    ["TextDocumentContentChangePartialOrWholeDocument", '{"range":null,"text":"x"}'],
    ["StringLiteralCreate", '"c\\u0072eate"'], ["ParameterInformation", '{"label":[0,1,2]}'],
]) cases.push({ name: `raw-${cases.length}`, type, raw });
cases.push({ name: "deep-initialization-user-preferences", type: "InitializeParams", raw:
    '{"processId":null,"rootUri":null,"capabilities":{},"initializationOptions":{"userPreferences":' + '{"child":'.repeat(2000) + 'null' + '}'.repeat(2000) + '}}' });
await json(path.join(directory, "inputs.json"), cases);
async function execute(command, args, label) {
    const child = spawn(command, args, { windowsHide: true, cwd: root });
    const outputs = [], errors = [];
    child.stdin.on("error", error => errors.push(Buffer.from(error.message)));
    child.stdout.on("data", c => outputs.push(c)); child.stderr.on("data", c => errors.push(c));
    const done = new Promise((resolve, reject) => { child.on("error", reject); child.on("close", code => code === 0 ? resolve() : reject(new Error(`${label} ${code}: ${Buffer.concat(errors)}`))); });
    child.stdin.end(cases.map(input => JSON.stringify(input)).join("\n") + "\n");
    await done;
    const text = Buffer.concat(outputs).toString(); await writeFile(path.join(directory, `${label}.jsonl`), text);
    const results = text.trim().split("\n").map(JSON.parse); assert.equal(results.length, cases.length); return results;
}
const [expected, actual] = await Promise.all([execute(oracle, [], "reference"), execute(dotnet, [dll, "--lsp-protocol-lines"], "candidate")]);
const differences = cases.flatMap((input, i) => expected[i].accepted === actual[i].accepted ? [] : [{ input, reference: expected[i], candidate: actual[i] }]);
await json(path.join(directory, "differences.json"), differences);
const originalEvents = (await readFile(path.join(directory, "original-tests.jsonl"), "utf8")).trim().split("\n").map(JSON.parse);
const result = { referenceRevision, originalTestsPassed: originalEvents.filter(e => e.Action === "pass" && e.Test).length,
    recordedInputs: recordings.length, generatedTypes: Object.keys(model.schemas).length, methods: Object.keys(model.requests).length,
    cases: cases.length, differences: differences.length, compilerSha256: sha256(await readFile(path.join(path.dirname(dll), "TypeScript.Compiler.dll"))),
    oracleSha256: sha256(await readFile(oracle)), generatorSourceSha256: model.sourceSha256,
    comparison: "Strict acceptance and rejection; runtime-specific JSON decoder error wrappers are retained but not compared.", phase7Complete: false };
await json(path.join(directory, "result.json"), result); console.log(JSON.stringify(result));
process.exitCode = differences.length ? 1 : 0;
