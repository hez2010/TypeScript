import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import {
    copyFile,
    mkdir,
    readFile,
    writeFile,
} from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";
import {
    output,
    root,
    run,
    sha256,
} from "./common.mjs";

const option = (name, fallback) => process.argv.includes(name) ? process.argv[process.argv.indexOf(name) + 1] : fallback;
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rc.2.26470.103-win-x64/dotnet.exe");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const ts = createRequire(import.meta.url)("typescript");
const source = path.join(output, reference.sourceRelativePath, "tsc");
await mkdir(path.join(source, "cmd/metadata-probe"), { recursive: true });
await copyFile(path.join(root, "csharp/oracle/metadata/main.go"), path.join(source, "cmd/metadata-probe/main.go"));
const oracle = path.join(output, "metadata-oracle.exe");
if (!process.argv.includes("--no-build")) {
    await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", oracle, "./cmd/metadata-probe"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
    await run(dotnet, ["build", "csharp/tests/TypeScript.SourceMetadata", "-c", "Release", "--no-restore"]);
}
const native = option("--native");
const candidate = native ?? dotnet;
const args = native ? [] : [path.join(root, "csharp/tests/TypeScript.SourceMetadata/bin/Release/net11.0/TypeScript.SourceMetadata.dll")];
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
            if (code || inputError) reject(new Error(`${command}: ${code}; case ${lines.length} ${JSON.stringify(cases[lines.length])}\n${Buffer.concat(stderr)}\n${inputError ?? ""}`));
            else resolve(lines.map(line => JSON.parse(line)));
        });
        child.stdin.end(cases.map(c => JSON.stringify(c)).join("\n") + "\n");
    });
}
const cases = [];
const add = (text, options = {}) => cases.push({ text: Buffer.from(text).toString("base64"), ...options });
const comments = [
    "",
    "// plain\n",
    "/* comment */\n",
    "#!node\n// @ts-check\n",
    "\uFEFF// @ts-check\n",
    '/// <reference path="a.ts" />\n',
    "/// <reference types='node' resolution-mode='require' preserve='true' />\n",
    '/// <reference types="名😀" resolution-mode="import" />\n',
    '/// <reference lib="esnext" preserve="false" />\n',
    '/// <reference no-default-lib="true" />\n',
    '/// <reference path="ignored" types="node" lib="esnext" />\n',
    "// @ts-check\n// @ts-nocheck\n// @ts-check\n",
    "/// @TS-NOCHECK because reason\n",
    "/** @jsx h */\n/** @jsxFrag Fragment */\n/** @jsxImportSource preact */\n/** @jsxRuntime automatic */\n",
    "/** email@domain @jsx wrong\n * @jsx h\n * @jsxfrag Fragment\n */\n",
    "/** @jsx \n * @jsxfrag F */\n",
    "/* @jsx h @jsxfrag Wrong */\n",
    '/// <REFERENCE PATH="名😀.ts" preserve="true" />\n',
    '/// <reference types="a" resolution-mode="bad" />\n',
    "/// <reference />\n",
    '/// <reference path="" />\n',
    '/// <reference path="a" path="b" />\n',
    '/// <reference path="unterminated />\n',
    '/// <reference foo="bar" />\n',
    "// @ts-check\r\n/* @jsx h */\r\n",
    "/* @jsx h */ /* @jsxfrag F */\n",
    '/// <reference path="a" />\u2028// @ts-nocheck\u2029',
    '/// <reference no-default-lib="false" />\n',
    '//// <reference path="a" />\n',
    '// <reference path="a" />\n',
];
const bodies = [
    "",
    "const x = 1;",
    "export {};",
    "import 'a';",
    "import x from 'a';",
    "import type { A } from 'a';",
    "export const x = 1;",
    "export default 1;",
    "export = A;",
    "export * from 'a';",
    "import A = require('a');",
    "import A = N.A;",
    "const x = import('a');",
    "const x = import.meta;",
    "function f() { return import.meta; }",
    "type T = import('a').A;",
    "namespace N { export const x = 1; }",
    "// @ts-nocheck\nconst x = 1;\n// @ts-check",
    "const view = <><A /></>;",
];
for (const comment of comments) for (const body of bodies) for (const flags of [{}, { force: true }, { jsx: true }]) add(comment + body, { ...flags, fileName: body.includes("<A") ? "/test.tsx" : "/test.ts" });
for (const fileName of ["/test.d.ts", "/test.d.mts", "/test.js", "/test.jsx", "/test.json"]) for (const force of [false, true]) for (const jsx of [false, true]) add(fileName.endsWith("json") ? '{"x": 1}' : "// @ts-check\n/** @jsx h */\nconst x = 1;", { fileName, force, jsx });
const moduleSources = [
    'import ""; import "a"; export * from "b"; import C = require("c");',
    'const x = import("dynamic"); import "static"; type T = import("type").T;',
    'const x = import(`template`); function f() { return import("nested", { with: { type: "json" } }); }',
    'const x = require("a"); const y = require(`b`); const z = require("c", "extra");',
    'const x = obj.require("ignored"); const y = require(value); const z = import(value);',
    'declare module "a" { import A = require("b"); import C = require("./c"); export * from "d"; }',
    'declare module "a" { module "b" {} module "./c" {} }',
    'declare module "a" {} declare module "b" {}',
    'export {}; declare module "./a" {} declare module "b" {}',
    "declare global { interface X {} }",
    "export {}; declare global { interface X {} }",
    'module "a" { import "b"; }',
    'namespace N { import A = require("hidden"); }',
    'declare module "a" { import "C:/b"; import "../c"; import "https://example.com/d"; }',
    '/** @type {import("a").A} */ let x; const y = import("b");',
    '/** @import { A } from "a" */ let x;',
];
for (const text of moduleSources) for (const fileName of ["/test.ts", "/test.js", "/test.d.ts"]) add(text, { fileName });
let seed = 0x497a31f2;
const random = max => {
    seed ^= seed << 13;
    seed ^= seed >>> 17;
    seed ^= seed << 5;
    return (seed >>> 0) % max;
};
const attributes = ["path", "types", "lib", "resolution-mode", "preserve", "no-default-lib", "PATH", "unknown"];
const values = ["a.ts", "node", "日本語😀", "import", "require", "true", "false", "", "\u00a0", "esnext"];
for (let i = 0; i < Number(option("--fuzz", 5000)); i++) {
    let text = random(2) ? "/// <reference" : " ///\t<REFERENCE";
    for (let j = 0, count = random(6); j < count; j++) text += ` ${attributes[random(attributes.length)]}=${random(2) ? '"' : "'"}${values[random(values.length)]}${random(2) ? '"' : "'"}`;
    text += [" />\n", ">\n", "\n", " / >\n"][random(4)] + "const x = 1;";
    add(text);
}
if (process.argv.includes("--input")) cases.splice(0, cases.length, ...JSON.parse(await readFile(option("--input"), "utf8")));
const expected = await probe(oracle, [], cases), actual = await probe(candidate, args, cases);
assert.equal(expected.length, cases.length);
assert.equal(actual.length, cases.length);
const failures = cases.flatMap((c, i) => JSON.stringify(expected[i]) === JSON.stringify(actual[i].slice(0, 10)) ? [] : [{ ...c, source: Buffer.from(c.text, "base64").toString(), expected: expected[i], actual: actual[i] }]);
await writeFile(path.join(output, "metadata-failures.json"), JSON.stringify(failures, null, 2));

// Requested metadata absent from the Go implementation is checked independently.
const extensions = [
    ['/// <amd-dependency path="a" />\n/// <amd-dependency name="b" path="c" />\n/// <amd-module name="module" />\n', [["a", null], ["c", "b"]], "module", false, []],
    ['/// <amd-module name="first" />\n/// <amd-module name="second" />\n', [], "second", false, [2458]],
    ['/// <reference no-default-lib="true" path="ignored.ts" />\n', [], null, true, []],
    ['/// <reference no-default-lib="false" path="included.ts" />\n', [], null, false, []],
    ['/// <amd-dependency name="missing-path" />\n/// <amd-module path="missing-name" />\n', [], null, false, []],
    ['const x = 1;\n/// <amd-module name="ignored" />\n', [], null, false, []],
];
const extensionOutput = await probe(candidate, args, extensions.map(([text]) => ({ text: Buffer.from(text).toString("base64") })));
const extensionReference = await probe(oracle, [], extensions.map(([text]) => ({ text: Buffer.from(text).toString("base64") })));
for (let i = 0; i < extensions.length; i++) {
    assert.deepEqual(extensionOutput[i].slice(10), extensions[i].slice(1, 4));
    assert.deepEqual(extensionOutput[i][6].map(d => d[0]), extensions[i][4]);
}
const semanticCases = [];
for (const blank of ["\v", "\f", "\u00a0", "\u1680", "\u2003", "\u202f", "\u205f", "\u3000", "\ufeff"]) {
    semanticCases.push([`///${blank}<reference${blank}path${blank}=${blank}"a.ts" />\n`, "reference"]);
    semanticCases.push([`//${blank}@ts-check\n`, "ts-check"]);
    semanticCases.push([`/** @jsx${blank}h */`, "jsx"]);
}
for (const suffix of ["1", ".", "!", "@other", "_suffix"]) semanticCases.push([`// @ts-check${suffix}\n`, null], [`/// <reference${suffix} path="a.ts" />\n`, null]);
semanticCases.push(["/** @jsx h\u2028 * @jsxfrag F\u2029 */", "jsx", "jsxfrag"]);
const semanticRequests = semanticCases.map(([text]) => ({ text: Buffer.from(text).toString("base64") }));
const semanticOutput = await probe(candidate, args, semanticRequests);
const semanticReference = await probe(oracle, [], semanticRequests);
const semanticDifferences = [];
for (let i = 0; i < semanticCases.length; i++) {
    assert.deepEqual(semanticOutput[i][0].map(p => p[0]), semanticCases[i].slice(1).filter(Boolean));
    assert.deepEqual([...ts.createSourceFile("test.ts", semanticCases[i][0], ts.ScriptTarget.Latest).pragmas.keys()], semanticCases[i].slice(1).filter(Boolean));
    assert.deepEqual(semanticOutput[i][6], []);
    if (JSON.stringify(semanticOutput[i].slice(0, 10)) !== JSON.stringify(semanticReference[i])) semanticDifferences.push({ source: semanticCases[i][0], expectedPragmas: semanticCases[i].slice(1).filter(Boolean), go: semanticReference[i], candidate: semanticOutput[i], reason: "ECMAScript whitespace and full pragma-name boundaries" });
}
const moduleSemanticCases = [
    ['const a = import("import");', ["import"]],
    ['const a = require("require");', ["require"]],
    ['const a = r\\u0065quire("x");', ["x"]],
    ['const a = import("require");', ["require"]],
];
const moduleRequests = moduleSemanticCases.map(([text]) => ({ text: Buffer.from(text).toString("base64"), fileName: "/test.js" }));
const moduleOutput = await probe(candidate, args, moduleRequests), moduleReference = await probe(oracle, [], moduleRequests);
for (let i = 0; i < moduleRequests.length; i++) {
    assert.deepEqual(moduleOutput[i][7].map(n => n[1]), moduleSemanticCases[i][1]);
    assert.deepEqual(moduleOutput[i][6], []);
    semanticDifferences.push({ source: moduleSemanticCases[i][0], fileName: "/test.js", go: moduleReference[i], candidate: moduleOutput[i], reason: "Collect module references once per actual AST literal; recognize escaped require identifiers" });
}
for (let i = 0; i < extensions.length; i++) semanticDifferences.push({ source: extensions[i][0], go: extensionReference[i], candidate: extensionOutput[i], reason: "Requested AMD and no-default-lib metadata absent from the Go source-file contract" });
for (const [index, difference] of semanticDifferences.entries()) {
    difference.id = `metadata-${String(index + 1).padStart(3, "0")}`;
    difference.inputSha256 = sha256(Buffer.from(difference.source));
    difference.reproduction = "node csharp/tools/metadata.mjs --fuzz 0";
}
const approvedDifferences = JSON.parse(await readFile(path.join(root, "csharp/tests/fixtures/metadata/semantic-differences.json"), "utf8"));
assert.deepEqual(semanticDifferences, approvedDifferences, "A documented metadata difference changed; inspect both outputs before updating its exact fixture");
await writeFile(path.join(output, "metadata-semantic-differences.json"), JSON.stringify(semanticDifferences, null, 2));
// Clones must retain references to their own AST, including forced source files.
const clones = ["export {};", "const x = import.meta;", "const x = 1;", ...moduleSources].map(text => ({ text: Buffer.from(text).toString("base64"), force: true, fileName: "/test.js" }));
assert.deepEqual(await probe(candidate, args, clones), await probe(candidate, args, clones.map(c => ({ ...c, clone: true }))));
const result = {
    timestamp: new Date().toISOString(),
    referenceRevision: reference.referenceRevision,
    runtimeKind: native ? "NativeAOT" : "CoreCLR",
    runtime: native ? await run(native, ["--native-check"]) : "CoreCLR",
    sdk: await run(dotnet, ["--version"], { cwd: path.join(root, "csharp") }),
    typescriptPragmaReference: ts.version,
    configuration: { optimizationPreference: "Speed", runtimeAsync: true, ...(native ? { ilcInstructionSet: "native" } : {}) },
    seed: "0x497a31f2",
    cases: cases.length,
    failures: failures.length,
    extensionAssertions: extensions.length,
    cloneAssertions: clones.length,
    semanticAssertions: semanticCases.length + moduleRequests.length,
    semanticDifferences: semanticDifferences.length,
    inputSha256: sha256(JSON.stringify(cases)),
    oracleSha256: sha256(await readFile(oracle)),
    candidateSha256: sha256(await readFile(native ?? args[0])),
};
await writeFile(path.join(output, "metadata-results.json"), JSON.stringify(result, null, 2));
console.log(JSON.stringify(result, null, 2));
for (const failure of failures.slice(0, 5)) console.log(JSON.stringify(failure));
if (failures.length) process.exitCode = 1;
