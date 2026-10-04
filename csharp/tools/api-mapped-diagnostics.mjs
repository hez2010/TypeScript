import assert from "node:assert/strict";
import {
    mkdir,
    readFile,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import { pathToFileURL } from "node:url";
import {
    json,
    output,
    referenceRevision,
    root,
    sha256,
} from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const directory = path.resolve(option("--directory", path.join(output, "phase8-validation/api-mapped-diagnostics")));
const manifest = JSON.parse(await readFile(option("--manifest", path.join(output, "distribution/coreclr-validation/manifest.json"))));
assert.equal(manifest.runtime, "coreclr-validation");
const candidate = path.resolve(option("--executable", path.join(manifest.platformDirectory, "lib", (manifest.executableName ?? "tsgo") + ".exe")));
const oracle = path.resolve(option("--oracle", path.join(output, "phase8-validation/cli-host-final/oracle.exe")));
const fixture = path.join(output, "content-mapper-fixture.exe"), fixtureInfo = JSON.parse(await readFile(path.join(output, "content-mapper-fixture.json")));
assert.equal(sha256(await readFile(fixture)), fixtureInfo.executableSha256);
process.env.DOTNET_ROOT ??= "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64";
const component = script => `<component name="ProfileCard">\n<template><h1>{{ title }}</h1></template>\n<script lang="ts">\n${script}\n</script>`;
const cases = [
    { name: "component", mapper: "component-mapper", text: component('export const title: number = "Profile";'), code: 2322 },
    { name: "component-unicode", mapper: "component-mapper", text: "// 世界 😀\n" + component('export const café: number = "世界"; export const title = café;'), code: 2322 },
    { name: "component-related", mapper: "component-mapper", text: component('export const title: { nested: { value: number } } = { nested: { value: "bad" } };'), code: 2322 },
    { name: "component-syntax", mapper: "component-mapper", text: component("export const title = ;"), code: 1109 },
    { name: "synthesized", mapper: "synthesizing-mapper", text: "<p>世界 😀</p>", code: 2304 },
    { name: "supplemental", mapper: "supplemental-diagnostics-mapper", text: '// 世界 😀\nconst item: number = "bad";\n', code: 2322 },
    { name: "custom-source", mapper: "diagnostic-code-collision-mapper", text: "// 世界 😀\nexport function foo() { return 1; }\n", code: 9007 },
    { name: "transformed", mapper: "compiler-test-mapper", text: '// 世界 😀\nexport const item: number = "bad";\n', code: 2322 },
    { name: "custom-unclosed", mapper: "compiler-test-mapper", text: "// 世界 😀\nexport const item = #{target;\n", code: 1000 },
    { name: "unused-directive", mapper: "compiler-test-mapper", text: "// @box-expect-error\nexport const item = 1;\n" },
    { name: "alias", mapper: "lisp-mapper", text: '(+ 1 2 "oops")', code: 2304 },
    { name: "failure", mapper: "failing-mapper", text: "export const item = 1;\n" },
];
const methods = ["getSyntacticDiagnostics", "getBindDiagnostics", "getSemanticDiagnostics", "getSuggestionDiagnostics", "getDeclarationDiagnostics", "getProgramDiagnostics", "getGlobalDiagnostics"];
const rows = [], differences = [];
for (const test of cases) {
    const folder = path.join(directory, test.name);
    await mkdir(path.join(folder, "node_modules/mapper"), { recursive: true });
    const config = path.join(folder, "tsconfig.json").replaceAll("\\", "/"), file = path.join(folder, "input.box").replaceAll("\\", "/");
    await json(config, { compilerOptions: { target: "es2022", module: "esnext", lib: ["es5"], strict: true, skipLibCheck: true, declaration: true, noEmitOnError: true, rootDir: ".", outDir: "out" }, contentMappers: [{ package: "mapper", extensions: [".box"] }], files: ["input.box"] });
    await json(path.join(folder, "node_modules/mapper/package.json"), { name: "mapper", version: "1.0.0", typescript: { contentMapper: { exec: [fixture, test.mapper] } } });
    await writeFile(file, test.text);
    for (const mode of ["async", "sync"]) {
        const results = [];
        for (const backend of ["go", "csharp"]) {
            const base = backend === "go" ? path.join(root, "packages/typescript") : manifest.mainDirectory;
            const { API } = await import(pathToFileURL(path.join(base, `dist/api/${mode}/api.js`)).href);
            const instance = new API({ cwd: folder, tsserverPath: backend === "go" ? oracle : candidate, runExternalCode: true });
            try {
                const observations = [];
                for (const layer of [false, true]) {
                    const text = test.text + (layer && test.mapper !== "lisp-mapper" ? "\n// new generation\n" : "");
                    const snapshot = await instance.createSnapshot({ openProject: config, ...layer ? { fileSystem: { kind: "layer", files: { [file]: text } } } : {} });
                    try {
                        const program = snapshot.getConfiguredProject(config).program;
                        const virtual = [(await program.getSourceFile(file)).text];
                        if (test.mapper === "supplemental-diagnostics-mapper") virtual.push((await program.getSourceFile(file + ".0.ts")).text);
                        const diagnostics = {};
                        for (const method of methods) diagnostics[method] = await program[method]();
                        diagnostics.emitToString = (await program.emitToString()).diagnostics;
                        observations.push({ layer, original: text, virtual, diagnostics });
                    }
                    finally {
                        await snapshot.dispose();
                        assert.equal(instance.activeSnapshots.size, 0);
                    }
                }
                await json(path.join(folder, `${backend}-${mode}.json`), observations);
                const normalized = observations.map(row => Object.fromEntries(Object.entries(row.diagnostics).map(([method, diagnostics]) => [method, diagnostics.map(diagnostic => normalize(diagnostic, backend, row))])));
                if (test.code) assert.ok(normalized.some(row => Object.values(row).some(diagnostics => diagnostics.some(d => d.code === test.code))), `${backend}: expected ${test.code} in ${test.name}`);
                results.push(normalized);
            }
            finally {
                await instance.close();
            }
        }
        for (let layer = 0; layer < 2; layer++) {
            for (const method of [...methods, "emitToString"]) {
                try {
                    assert.deepEqual(results[1][layer][method], results[0][layer][method]);
                }
                catch {
                    differences.push({ case: test.name, mode, layer: !!layer, method, reference: results[0][layer][method], candidate: results[1][layer][method] });
                }
            }
        }
        rows.push({ case: test.name, mode, layers: 2, methods: methods.length + 1 });
    }
    console.log(`${test.name}: ${differences.filter(row => row.case === test.name).length} differences`);

    function normalize(diagnostic, backend, row) {
        const result = structuredClone(diagnostic);
        if (result.fileName) {
            const absolute = path.resolve(result.fileName), relative = path.relative(folder, absolute).replaceAll("\\", "/");
            assert.ok(!relative.startsWith("..") && !path.isAbsolute(relative), `Unexpected diagnostic file: ${absolute}`);
            result.fileName = "<project>/" + relative;
            if (backend === "go") {
                const candidates = [row.original, ...row.virtual];
                const matching = candidates.filter(text => {
                    const lines = text.match(/[^\n]*\n|[^\n]+$/g) ?? [""];
                    return (result.sourceLines ?? []).every(line => (lines[line.line] ?? "") === line.text);
                });
                assert.ok(matching.length, "Reference formatting context must match known original or virtual source text");
                const byte = position => Buffer.byteLength(matching[0].slice(0, position));
                result.pos = byte(result.pos);
                result.end = byte(result.end);
                for (const key of ["startPosition", "endPosition"]) {
                    if (result[key]) {
                        const line = result.sourceLines.find(line => line.line === result[key].line);
                        assert.ok(line);
                        result[key].character = Buffer.byteLength(line.text.slice(0, result[key].character));
                    }
                }
            }
        }
        for (const key of ["messageChain", "relatedInformation"]) if (result[key]) result[key] = diagnostic[key].map(child => normalize(child, backend, row));
        return result;
    }
}
await json(path.join(directory, "differences.json"), differences);
const summary = { referenceRevision, cases: cases.length, modes: ["async", "sync"], snapshotsPerBackend: cases.length * 4, diagnosticComparisons: rows.reduce((sum, row) => sum + row.layers * row.methods, 0), differences: differences.length, compilerSha256: sha256(await readFile(path.join(path.dirname(candidate), "TypeScript.Compiler.dll"))), oracleSha256: sha256(await readFile(oracle)), mapperSha256: sha256(await readFile(fixture)), inputsSha256: sha256(JSON.stringify(cases)), contract: "Actual packaged clients and physical external mapper processes; diagnostics compare completely, with verified fixture-root labels and explicit Go UTF-16 to C# byte-coordinate conversion using the returned source context", nativeExecuted: false };
await json(path.join(directory, "summary.json"), summary);
console.log(JSON.stringify(summary));
assert.equal(differences.length, 0);
