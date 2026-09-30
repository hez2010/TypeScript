import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import {
    copyFile,
    mkdir,
    readFile,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import ts from "typescript";
import {
    json,
    output,
    root,
    run,
    sha256,
} from "./common.mjs";
const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const dotnet = option("--dotnet", process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, "dotnet.exe") : "dotnet");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc");
await mkdir(path.join(source, "cmd/hosts-probe"), { recursive: true });
await copyFile(path.join(root, "csharp/oracle/hosts/main.go"), path.join(source, "cmd/hosts-probe/main.go"));
const oracle = path.join(output, "hosts-oracle.exe");
await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", oracle, "./cmd/hosts-probe"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
const fixtures = JSON.parse(await run(go, ["run", path.join(root, "csharp/oracle/host-fixtures/main.go"), source], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } }));
let cases = fixtures.cases;
const rawOutputs = new Map();
const [schema] = await probe(oracle, [], [{ mode: "schema" }]);
for (const definition of schema) {
    const valid = definition.kind === "boolean" ? true : definition.kind === "number" ? 2 : definition.kind === "enum" ? definition.values[0] : definition.kind === "list" ? [definition.values[0] ?? "entry"] : definition.kind === "object" ? {} : "entry";
    const values = [undefined, "null", definition.kind === "boolean" ? "false" : definition.kind === "number" ? "0" : definition.kind === "list" ? "" : definition.kind === "object" ? "false" : "invalid-value", ...(definition.values.length ? definition.values : [String(valid)]), "--strict"];
    for (const value of values) cases.push({ name: `schema-cli:${definition.group}:${definition.name}:${value}`, mode: "cli", build: definition.group === "build", directory: "/project", sensitive: true, arguments: ["--" + definition.name, ...(value === undefined ? [] : [value]), "input.ts"] });
    if (definition.group === "compiler") { for (const value of [valid, null, definition.kind === "boolean" ? "wrong" : definition.kind === "number" ? "wrong" : false]) cases.push({ name: `schema-config:${definition.name}:${JSON.stringify(value)}`, mode: "config", directory: "/project", path: "/project/tsconfig.json", sensitive: true, files: { "/project/input.ts": "", "/project/tsconfig.json": JSON.stringify({ compilerOptions: { [definition.name]: value } }) } }); }
}
for (const pattern of ["**/*", "**/*.ts", "src", "src/", "src/*", "src/**/*.json", "src/.hidden/*", "src/node_modules/*/*.ts", "**/*.min.js", "**", "**/../*", "src/?.ts", "src/??.ts", "src/a**b.ts"]) for (const file of ["/project/src/a.ts", "/project/src/a.d.ts", "/project/src/a.min.js", "/project/src/.hidden.ts", "/project/src/.hidden/a.ts", "/project/src/node_modules/pkg/a.ts", "/project/src/deep/a.ts", "/project/src/😀.ts", "/project/src/a😀b.ts", "/project/src/data.json"]) for (const exclude of [false, true]) cases.push({ name: `glob:${pattern}:${file}:${exclude}`, mode: "glob", path: pattern, other: file, directory: "/project", sensitive: true, exclude });
for (const args of [["--checkers", "-1"], ["--checkers", "0"], ["--typeRoots", "a,b"], ["--composite", "true", "file.ts"], ["--composite", "false"], ["--plugins", "false", "x.ts"], ["--target", "--noEmit"], ["--locale", "fr_fr"], ["--locale", "a$"], ["@args.rsp"], ["@missing.rsp"]]) cases.push({ name: `cli:${args}`, mode: "cli", arguments: args, files: { "/project/args.rsp": '--strict @b.rsp "space file.ts"', "/project/b.rsp": "@args.rsp --typeRoots types" }, directory: "/project", sensitive: true });
for (const args of [["--clean", "--force"], ["--clean", "--verbose", "--watch", "--dry"], ["--target", "esnext"], ["--builders", "0"], []]) cases.push({ name: `build:${args}`, mode: "cli", build: true, arguments: args, directory: "/project", sensitive: true });
const baseFiles = { "/project/src/a.ts": "", "/project/src/a.js": "", "/project/src/a.d.ts": "", "/project/src/b.js": "", "/project/src/b.min.js": "", "/project/src/data.json": "", "/project/src/.hidden.ts": "", "/project/src/.hidden/a.ts": "", "/project/src/node_modules/pkg/a.ts": "", "/project/out/x.ts": "", "/base/base.json": JSON.stringify({ compilerOptions: { strict: true, outDir: "${configDir}/out", typeRoots: ["types"] }, include: ["${configDir}/src"] }) };
for (const config of [{}, { compilerOptions: { allowJs: true } }, { compilerOptions: { checkJs: true } }, { compilerOptions: { resolveJsonModule: true }, include: ["src/**/*"] }, { compilerOptions: { resolveJsonModule: true }, include: ["src/**/*.json"] }, { extends: "../base/base.json" }, { extends: ["../base/base.json"], compilerOptions: { strict: null } }, { files: ["src/b.js", "src/a.ts"], include: ["**/*"], exclude: ["src"] }, { files: [] }, { include: ["**"] }, { include: ["src/**/../*.ts"] }, { references: [{}, { path: "" }, { path: "../base", circular: "yes" }] }, { compilerOptions: { lib: ["es6", "bad", 1], rootDirs: ["src", null, 5], strict: "true" } }]) cases.push({ name: `config:${JSON.stringify(config)}`, mode: "config", path: "/project/tsconfig.json", directory: "/project", sensitive: true, files: { ...baseFiles, "/project/tsconfig.json": JSON.stringify(config) } });
const utf16Config = { files: ["high-\ud800.ts", "low-\udfff.ts", "pair-😀.ts", "mix-\ud800😀\udfff.ts"], compilerOptions: { outDir: "dir-\ud800", typeRoots: ["types-\udfff", "pair-😀"], paths: { "plain": ["x"], "alias-\ud800/*": ["lib-\udfff/*"], "alias-\udfff/*": ["lib-😀/*"] } } };
for (const [spelling, text] of [["raw-pairs", JSON.stringify(utf16Config)], ["escaped-pairs", JSON.stringify(utf16Config).replaceAll("😀", "\\uD83D\\uDE00")]]) cases.push({ name: `config-utf16:${spelling}`, mode: "configUnits", path: "/project/tsconfig.json", directory: "/project", sensitive: true, files: { "/project/tsconfig.json": text } });
cases.push({ name: "config-utf16:unknown-key", mode: "config", path: "/project/tsconfig.json", directory: "/project", sensitive: true, files: { "/project/tsconfig.json": '{"files":["input.ts"],"compilerOptions":{"bad\\uD800":true}}' } });
const numericCases = [
    ["unknown-positive-exponent", "1e309", false],
    ["unknown-negative-exponent", "-1e309", false],
    ["unknown-huge-integer", "9".repeat(400), false],
    ["unknown-negative-hex", "-0x" + "f".repeat(300), false],
    ["option-positive-overflow", "1e309", true],
    ["option-negative-overflow", "-1e309", true],
    ["option-int64-overflow", "9223372036854775808", true],
    ["option-fraction", "1.5", true],
    ["option-negative", "-1", true],
    ["option-exponent", "2e0", true],
    ["option-hex", "0x10", true],
    ["option-octal", "0o20", true],
    ["option-binary", "0b10000", true],
    ["option-int64-maximum", "9223372036854775807", true],
];
for (const [name, value, option] of numericCases) cases.push({ name: `config-number:${name}`, mode: "config", path: "/project/tsconfig.json", directory: "/project", sensitive: true, files: { "/project/main.ts": "", "/project/tsconfig.json": `{${option ? '"compilerOptions":{"maxNodeModuleJsDepth":' + value + "}" : '"custom":' + value},"files":["main.ts"]}` } });
for (const value of ["1e309", "-1e309", "9223372036854775808"]) cases.push({ name: `config-native-number:checkers:${value}`, mode: "config", path: "/project/tsconfig.json", directory: "/project", sensitive: true, files: { "/project/main.ts": "", "/project/tsconfig.json": `{"compilerOptions":{"checkers":${value}},"files":["main.ts"]}` } });
const managed = process.argv.includes("--managed");
if (!process.argv.includes("--no-build")) await run(dotnet, managed ? ["build", "tests/TypeScript.Compatibility", "-c", "Release", "--no-restore"] : ["publish", "tests/TypeScript.Compatibility", "-p:PublishAot=true", "-c", "Release", "-r", "win-x64", "-p:IlcInstructionSet=native", "-p:RestoreLockedMode=true", "-o", path.join(output, "hosts-native")], { cwd: path.join(root, "csharp") });
const candidate = managed ? dotnet : option("--candidate", path.join(output, "hosts-native/TypeScript.Compatibility.exe"));
const args = managed ? [path.join(root, "csharp/tests/TypeScript.Compatibility/bin/Release/net11.0/TypeScript.Compatibility.dll"), "--host-lines"] : ["--host-lines"];
async function probe(command, args, cases) {
    return new Promise((resolve, reject) => {
        const child = spawn(command, args, { windowsHide: true });
        const stdout = [], stderr = [];
        child.stdout.on("data", b => stdout.push(b));
        child.stderr.on("data", b => stderr.push(b));
        child.on("error", reject);
        child.stdin.on("error", () => {});
        child.on("close", code => {
            const lines = Buffer.concat(stdout).toString().trim().split(/\r?\n/).filter(Boolean);
            if (code) reject(new Error(`${command} case ${cases[lines.length]?.name}\n${Buffer.concat(stderr)}`));
            else {
                rawOutputs.set(command, new Map(cases.map((c, i) => [c.name, lines[i]])));
                resolve(lines.map(l => JSON.parse(l)));
            }
        });
        child.stdin.end(cases.map(c => JSON.stringify(c)).join("\n") + "\n");
    });
}
if (process.argv.includes("--filter")) cases = cases.filter(c => c.mode === option("--filter") || c.name.includes(option("--filter")));
if (process.argv.includes("--case")) cases = cases.filter(c => c.name === option("--case"));
await json(path.join(output, "hosts-inputs.json"), cases);
const expected = await probe(oracle, [], cases), actual = await probe(candidate, args, cases);
assert.equal(expected.length, cases.length);
assert.equal(actual.length, cases.length);
const numericReferences = new Map();
for (let i = 0; i < cases.length; i++) {
    if (cases[i].mode === "configUnits") {
        const c = cases[i], parsed = JSON.parse(c.files[c.path]), units = text => text.split("").map(unit => unit.charCodeAt(0));
        const independent = { files: parsed.files.map(file => units(path.posix.resolve(c.directory, file))), outDir: units(path.posix.resolve(c.directory, parsed.compilerOptions.outDir)), typeRoots: parsed.compilerOptions.typeRoots.map(root => units(path.posix.resolve(c.directory, root))), paths: Object.entries(parsed.compilerOptions.paths).map(([key, values]) => [units(key), values.map(units)]), diagnostics: [] };
        assert.deepEqual(actual[i], independent, `Independent Node UTF-16 config semantics: ${c.name}`);
        assert.deepEqual(expected[i], independent, `Reference Go UTF-16 config semantics: ${c.name}`);
    }
    if (cases[i].name.startsWith("config-number:")) {
        const c = cases[i], source = ts.parseConfigFileTextToJson(c.path, c.files[c.path]);
        const host = { useCaseSensitiveFileNames: true, readDirectory: () => ["/project/main.ts"], fileExists: () => true, readFile: () => "" };
        const independent = ts.parseJsonConfigFileContent(source.config, host, c.directory);
        assert.equal(source.error, undefined, `TypeScript numeric syntax: ${c.name}`);
        assert.equal(independent.errors.length, 0, `TypeScript numeric config validation: ${c.name}`);
        assert.equal(actual[i].diagnostics.length, 0, `Candidate numeric config validation: ${c.name}`);
        if (Object.hasOwn(independent.options, "maxNodeModuleJsDepth")) assert.equal(actual[i].options.maxNodeModuleJsDepth, independent.options.maxNodeModuleJsDepth, `TypeScript numeric option value: ${c.name}`);
        numericReferences.set(c.name, { version: ts.version, diagnostics: [], maxNodeModuleJsDepth: String(independent.options.maxNodeModuleJsDepth) });
    }
}
const differences = [], permittedDifferences = [];
for (let i = 0; i < cases.length; i++) {
    try {
        assert.deepEqual(actual[i], expected[i]);
    }
    catch {
        const difference = { name: cases[i].name, input: cases[i], expected: expected[i], actual: actual[i] };
        const c = cases[i];
        if (c.name === 'schema-config:plugins:["entry"]' && actual[i].options.plugins?.length === 0) {
            // An invalid plugin entry is diagnosed, and the retained empty list has no
            // plugin to execute. Go currently has no Plugins field on CompilerOptions.
            assert.deepEqual({ ...actual[i], options: Object.fromEntries(Object.entries(actual[i].options).filter(([key]) => key !== "plugins")) }, expected[i]);
            permittedDifferences.push({ ...difference, policy: "Retain an empty validated plugin list" });
        }
        else if (/^internal\/tsoptions\/tsconfigparsing_test.go:976:\/project\/(?:bad|tsconfig)\.json(?:true|false)$/.test(c.name) && c.files?.["/project/bad.json"] === "{ this is not json") {
            assert.ok(actual[i].diagnostics.length > 0);
            assert.deepEqual({ ...actual[i], diagnostics: [] }, { ...expected[i], diagnostics: [] });
            permittedDifferences.push({ ...difference, policy: "Malformed JSON recovery diagnostic choice" });
        }
        else if (numericReferences.has(c.name) && numericCases.some(([name, , option]) => option && c.name === `config-number:${name}`)) {
            assert.deepEqual({ ...actual[i], options: {} }, { ...expected[i], options: {} });
            assert.deepEqual(Object.keys(actual[i].options), ["maxNodeModuleJsDepth"]);
            assert.deepEqual(Object.keys(expected[i].options), ["maxNodeModuleJsDepth"]);
            permittedDifferences.push({ ...difference, policy: "Preserve TypeScript numeric option semantics", independentTypeScript: numericReferences.get(c.name) });
        }
        else if (["1e309", "-1e309", "9223372036854775808"].some(value => c.name === `config-native-number:checkers:${value}`)) {
            assert.deepEqual(actual[i].diagnostics, [5024]);
            assert.deepEqual(actual[i].options, {});
            assert.deepEqual({ ...actual[i], diagnostics: [], options: {} }, { ...expected[i], diagnostics: [], options: {} });
            assert.match(rawOutputs.get(oracle).get(c.name), /"checkers":-9223372036854775808/);
            permittedDifferences.push({ ...difference, policy: "Reject native worker-count integer overflow" });
        }
        else differences.push(difference);
    }
}
const rationales = {
    "Retain an empty validated plugin list": "The invalid plugin string is rejected with diagnostic 5024 by both implementations. C# retains the resulting empty list; Go core.CompilerOptions has no Plugins field. An empty plugin list and an absent plugin list request no plugin execution. Only that field differs; every remaining result field is asserted equal.",
    "Malformed JSON recovery diagnostic choice": "The exact malformed input '{ this is not json' is rejected by both implementations; options, discovered files, references and compileOnSave agree. C# retains located parser recovery diagnostics while Go's entry points collect different parser/conversion diagnostics. HostTests separately asserts the candidate diagnostics refer to the malformed file and bounded source positions. This policy covers only the four enumerated direct/inherited case-sensitive/case-insensitive instances of this input.",
    "Preserve TypeScript numeric option semantics": "TypeScript's maxNodeModuleJsDepth is a Number-valued option. Independent TypeScript 6.0.3 config parsing accepts the fixture and produces the same fractional/large/infinite value as C#. C# preserves valid JSON numeric spelling and exposes Number(), avoiding the Go backend's truncation or unchecked float-to-int overflow. Other parsed configuration fields are asserted equal. Raw JSON outputs below are authoritative for nonfinite and beyond-safe-integer values.",
    "Reject native worker-count integer overflow": "The Go-only checkers option is an integer worker count. For the three explicitly listed nonfinite/out-of-Int64-range inputs, Go's unchecked conversion silently stores Int64.MinValue. C# reports a located range error instead; ordinary finite config values retain the existing integer conversion. This policy does not restrict the TypeScript-native maxNodeModuleJsDepth Number option.",
};
for (const difference of permittedDifferences) {
    difference.goRawJson = rawOutputs.get(oracle).get(difference.name);
    difference.csharpRawJson = rawOutputs.get(candidate).get(difference.name);
    difference.goRawSha256 = sha256(difference.goRawJson);
    difference.csharpRawSha256 = sha256(difference.csharpRawJson);
    const numericEncoding = (_, value) => typeof value === "number" && !Number.isFinite(value) ? { numberValue: String(value) } : value;
    difference.expected = JSON.parse(JSON.stringify(difference.expected, numericEncoding));
    difference.actual = JSON.parse(JSON.stringify(difference.actual, numericEncoding));
    difference.inputSha256 = sha256(JSON.stringify(difference.input));
    difference.rationale = rationales[difference.policy];
    const quote = value => "'" + value.replaceAll("'", "''") + "'";
    difference.reproducer = `node csharp/tools/hosts.mjs --no-build --candidate ${quote(candidate)} --case ${quote(difference.name)}`;
}
await json(path.join(output, "hosts-differences.json"), differences);
await json(path.join(output, "hosts-permitted-differences.json"), permittedDifferences);
const evidence = { referenceRevision: reference.referenceRevision, managed, cases: cases.length, matched: cases.length - differences.length - permittedDifferences.length, permittedDifferences, failed: differences.length, unextractedFixtures: fixtures.unhandled, expandedFixtureLoops: fixtures.expandedLoops, oracleSha256: sha256(await readFile(oracle)), candidateSha256: sha256(await readFile(managed ? args[0] : candidate)), inputSha256: sha256(JSON.stringify(cases)), failures: differences.map(d => d.name) };
evidence.numericRegressions = cases.filter(c => numericReferences.has(c.name)).map(c => ({ name: c.name, inputJson: c.files[c.path], independentTypeScript: numericReferences.get(c.name), goRawJson: rawOutputs.get(oracle).get(c.name), csharpRawJson: rawOutputs.get(candidate).get(c.name) }));
await json(path.join(output, "hosts-results.json"), evidence);
if (process.argv.includes("--write-evidence")) {
    assert.equal(managed, false, "Release evidence requires the NativeAOT artifact");
    assert.equal(differences.length, 0, "Unclassified host differences remain");
    evidence.runtime = await run(candidate, ["--native-check"]);
    evidence.foundationOutput = await run(candidate, ["--hosts", root]);
    evidence.sdk = "11.0.100-rc.2.26470.103";
    evidence.targetFramework = "net11.0";
    evidence.languageVersion = "15.0";
    evidence.optimizationPreference = "Speed";
    evidence.instructionSet = "native";
    evidence.groups = Object.fromEntries([...new Set(cases.map(c => c.mode))].map(mode => [mode, cases.filter(c => c.mode === mode).length]));
    evidence.scope = "Host/configuration foundation comparison and focused contracts; not a compiler, watch-service, content-mapper execution or language-server gate. Input extraction uses literal/table inputs from the named existing Go suites, plus generated option schema and focused cases. It does not execute their Go test assertions against C#.";
    const embedded = evidence.foundationOutput.includes("embedded=True");
    await json(path.join(root, `csharp/compatibility/evidence/phase2-hosts-${embedded ? "embedded" : "side-by-side"}.json`), evidence);
}
console.log(JSON.stringify({ cases: evidence.cases, matched: evidence.matched, permittedDifferences: permittedDifferences.length, failed: evidence.failed, unextractedFixtures: evidence.unextractedFixtures }, null, 2));
if (differences.length) process.exitCode = 1;
