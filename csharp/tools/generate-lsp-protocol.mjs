import { readFile, writeFile, readdir, mkdir } from "node:fs/promises";
import assert from "node:assert/strict";
import path from "node:path";
import { root, sha256 } from "./common.mjs";

const sourceFile = "tsc/internal/lsp/lsproto/lsp_generated.go";
const source = (await readFile(path.join(root, sourceFile), "utf8")).replaceAll("\r", "");
const types = new Map([...source.matchAll(/^type (\w+) (?:= )?([^\n]+)/gm)]
    .map(m => [m[1], { type: m[2] }]));
for (const m of source.matchAll(/^type (\w+) struct \{\n([\s\S]*?)^\}/gm)) types.get(m[1]).body = m[2];
const decoders = new Map([...source.matchAll(/^func \(\w+ \*(\w+)\) UnmarshalJSONFrom\(dec \*json.Decoder\) error \{([\s\S]*?)^\}/gm)]
    .map(m => [m[1], m[2]]));
const methods = new Map([...source.matchAll(/(Method\w+)\s+Method = "([^"]+)"/g)].map(m => [m[1], m[2]]));
const directory = path.join(root, "csharp/src/TypeScript.Compiler/LanguageServer");
const handlers = (await Promise.all((await readdir(directory)).filter(n => n.startsWith("LanguageServerSession") && n.endsWith(".cs"))
    .map(n => readFile(path.join(directory, n), "utf8")))).join("\n");
const infos = [...source.matchAll(/var \w+Info = (?:Request|Notification)Info\[([^\]]+)\]\{Method: (\w+)\}/g)]
    .map(m => ({ method: methods.get(m[2]), params: m[1].split(", ")[0], result: m[1].split(", ")[1] }));
const requests = Object.fromEntries(infos.filter(x => handlers.includes(`"${x.method}"u8`)).map(x => [x.method, x.params]));
const responses = Object.fromEntries(infos.filter(x => ["workspace/configuration", "client/registerCapability", "client/unregisterCapability",
    "workspace/diagnostic/refresh", "workspace/codeLens/refresh", "workspace/inlayHint/refresh", "window/workDoneProgress/create"].includes(x.method)).map(x => [x.method, x.result]));
for (const method of Object.keys(responses)) delete requests[method];
for (const method of ["window/logMessage", "textDocument/publishDiagnostics", "telemetry/event", "$/progress"]) delete requests[method];
for (const method of ["$/setTrace", "$/cancelRequest"]) requests[method] = infos.find(x => x.method === method).params;
const schemas = {};
function reference(type) { build(type); return type; }
function branch(body) {
    const sw = body.match(/^(\t*)switch (.+) \{$/m);
    if (sw) {
        const start = sw.index + sw[0].length, indent = sw[1];
        const end = body.indexOf(`\n${indent}}`, start);
        if (end < 0) throw new Error("Unclosed switch");
        const arms = [...body.slice(start, end).matchAll(new RegExp(`^${indent}(case (.+)|default):[^\\n]*\\n([\\s\\S]*?)(?=^${indent}(?:case |default:)|$)`, "gm"))];
        const cases = {};
        for (let i = 0; i < arms.length; i++) {
            // Find the next case at this indentation, not the end of a line.
            const armStart = start + arms[i].index + arms[i][0].indexOf("\n") + 1;
            const armEnd = i + 1 < arms.length ? start + arms[i + 1].index : end;
            const value = branch(body.slice(armStart, armEnd));
            const labels = arms[i][2] ? arms[i][2].split(", ") : ["default"];
            for (const label of labels) cases[label.startsWith("'") ? label.slice(1, -1) : label.startsWith("`") ? label.slice(1, -1) : label] = value;
        }
        if (sw[2].includes("PeekKind()")) return { kind: "kind", cases };
        const keys = sw[2].match(/^jsonObjectHasKey\(data, (.+)\)$/);
        if (keys) return { kind: "keys", keys: JSON.parse(`[${keys[1]}]`), cases };
        const field = sw[2].match(/^string\(jsonObjectRawField\(data, "([^"]+)"\)\)$/);
        if (field) return { kind: "tag", field: field[1], cases };
        throw new Error(`Unknown union switch: ${sw[2]}`);
    }
    const alloc = body.match(/o\.\w+ = new\((.+)\)/);
    if (alloc) return { kind: "ref", type: reference(alloc[1] === "kind == 't'" ? "bool" : alloc[1]) };
    const candidates = [...body.matchAll(/var v\w+ (.+)/g)].map(m => reference(m[1]));
    if (candidates.length) return { kind: "anyOf", types: candidates };
    if (body.includes("dec.ReadToken()")) return { kind: "ref", type: reference("Null") };
    return { kind: "invalid" };
}
function nilable(type) {
    if (type.startsWith("*") || type.startsWith("[]") || type.startsWith("map[")) return true;
    const def = types.get(type)?.type;
    return def && !def.startsWith("struct") && def !== type ? nilable(def) : false;
}
function build(type) {
    if (Object.hasOwn(schemas, type)) return;
    schemas[type] = null;
    let schema;
    if (["any", "json.Value"].includes(type)) schema = { kind: "any" };
    else if (["string", "bool", "uint32", "int32", "float64", "Null", "NoParams"].includes(type)) schema = { kind: type };
    else if (type === "DocumentUri" || type === "URI") schema = { kind: "ref", type: reference("string") };
    else if (type === "struct{}") schema = { kind: "emptyObject" };
    else if (type.startsWith("*")) schema = { kind: "pointer", type: reference(type.slice(1)) };
    else if (type.startsWith("[]")) schema = { kind: "array", type: reference(type.slice(2)) };
    else if (type.startsWith("[2]")) schema = { kind: "tuple", length: 2, type: reference(type.slice(3)) };
    else if (type.startsWith("map[")) schema = { kind: "map", type: reference(type.slice(type.indexOf("]") + 1)) };
    else {
        const def = types.get(type), decoder = decoders.get(type);
        if (!def) throw new Error(`Unknown type: ${type}`);
        const literal = decoder?.match(/errLiteralMismatch\("\w+", `(.+)`, v\)/);
        if (literal) schema = { kind: "literal", value: literal[1] };
        else if (def.type === "struct{}") schema = { kind: "emptyObject" };
        else if (!def.type.startsWith("struct")) schema = { kind: "ref", type: reference(def.type) };
        else if (decoder?.includes("unmarshalStruct(s, dec)")) {
            const fields = [...def.body.matchAll(/^\t\w+\s+(.+?)\s+`json:"([^",]+)[^"]*"([^`]*)`/gm)]
                .map(m => ({ name: m[2], type: reference(m[1]), required: m[3].includes("required"), rejectNull: !!nilable(m[1]) && !m[3].includes("nullable") }));
            schema = { kind: "object", fields };
        } else if (decoder) schema = branch(decoder);
        else throw new Error(`Missing decoder: ${type}`);
    }
    schemas[type] = schema;
}
for (const type of [...Object.values(requests), ...Object.values(responses)]) build(type);
// Include the original codec test targets, including types not used by the server.
const originalTests = await readFile(path.join(root, "tsc/internal/lsp/lsproto/lsp_json_test.go"), "utf8");
for (const m of originalTests.matchAll(/(?:new\(|&)(\w+)(?:\)|\{)/g)) if (types.has(m[1])) build(m[1]);
for (const m of originalTests.matchAll(/\bvar \w+ (\w+)/g)) if (types.has(m[1])) build(m[1]);
const data = { sourceFile, sourceSha256: sha256(source), requests, responses, schemas };
const strings = JSON.stringify;
function emit(s) {
    const args = [`Kind.${s.kind[0].toUpperCase() + s.kind.slice(1)}`];
    if (s.type) args.push(`Type: ${strings(s.type)}`);
    if (s.fields) args.push(`Fields: [${s.fields.map(f => `new(${strings(f.name)}, ${strings(f.type)}, ${f.required}, ${f.rejectNull})`).join(", ")}]`);
    if (s.cases) args.push(`Cases: new() { ${Object.entries(s.cases).map(([k, v]) => `[${strings(k)}] = ${emit(v)}`).join(", ")} }`);
    if (s.keys) args.push(`Keys: [${s.keys.map(strings).join(", ")}]`);
    if (s.field) args.push(`Field: ${strings(s.field)}`);
    if (s.value) args.push(`Value: ${strings(s.value)}`);
    if (s.types) args.push(`Types: [${s.types.map(strings).join(", ")}]`);
    return `new(${args.join(", ")})`;
}
const chunks = Object.entries(schemas).reduce((a, item, index) => { (a[Math.floor(index / 50)] ??= []).push(item); return a; }, []);
const generated = ["// <auto-generated />", "#nullable enable", `// ${sourceFile} SHA256 ${data.sourceSha256}`, "namespace TypeScript.Compiler.LanguageServer;", "", "internal static partial class LspProtocol", "{",
    ...[ ["Requests", requests], ["Responses", responses] ].flatMap(([name, values]) => [
        `    private static readonly Dictionary<string, string> ${name} = new(StringComparer.Ordinal)`, "    {", ...Object.entries(values).map(([k, v]) => `        [${strings(k)}] = ${strings(v)},`), "    };", ""]),
    "    private static Dictionary<string, Schema> CreateSchemas()", "    {", "        Dictionary<string, Schema> result = new(StringComparer.Ordinal);",
    ...chunks.map((_, i) => `        Add${i}(result);`), "        return result;", "    }", "",
    ...chunks.flatMap((chunk, i) => [`    private static void Add${i}(Dictionary<string, Schema> result)`, "    {", ...chunk.map(([k, v]) => `        result[${strings(k)}] = ${emit(v)};`), "    }", ""]), "}", ""].join("\n");
const target = path.join(directory, "LspProtocol.generated.cs");
if (process.argv.includes("--check")) assert.equal((await readFile(target, "utf8")).replaceAll("\r\n", "\n"), generated);
else {
    await writeFile(target, generated);
    await mkdir(path.join(root, "built/csharp/phase7-validation"), { recursive: true });
    await writeFile(path.join(root, "built/csharp/phase7-validation/lsp-protocol-schema.json"), JSON.stringify(data, null, 2) + "\n");
}
console.log(JSON.stringify({ requests: Object.keys(requests).length, responses: Object.keys(responses).length, schemas: Object.keys(schemas).length }));
