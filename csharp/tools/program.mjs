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
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const dotnet = option("--dotnet", process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, "dotnet.exe") : "dotnet");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc");
const oracle = path.join(output, "program-oracle.exe");
const managed = process.argv.includes("--managed"), native = path.join(output, "phase3-native");
const candidate = managed ? dotnet : path.join(native, "TypeScript.Compatibility.exe");
const dll = path.join(root, "csharp/tests/TypeScript.Compatibility/bin/Release/net11.0/TypeScript.Compatibility.dll");
const args = [...managed ? [dll] : [], "--program-lines"];
await mkdir(path.join(source, "cmd/program-probe"), { recursive: true });
await copyFile(path.join(root, "csharp/oracle/program/main.go"), path.join(source, "cmd/program-probe/main.go"));
await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", oracle, "./cmd/program-probe"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
if (!process.argv.includes("--no-build")) {
    await run(
        dotnet,
        managed ? ["build", "tests/TypeScript.Compatibility", "-c", "Release", "--no-restore"]
            : ["publish", "tests/TypeScript.Compatibility", "-p:PublishAot=true", "-c", "Release", "-r", "win-x64", "-p:IlcInstructionSet=native", "-p:RestoreLockedMode=true", "-o", native],
        { cwd: path.join(root, "csharp"), env: { ...process.env, DOTNET_ROOT: path.dirname(dotnet), PATH: path.dirname(dotnet) + path.delimiter + process.env.PATH } },
    );
}
const templates = [
    { files: { "a.ts": "let a=1" } },
    { files: { "a.ts": "import './b'; import './c';", "b.ts": "import './d';", "c.ts": "import './d';", "d.ts": "export const x=1" } },
    { files: { "a.ts": "import './b';", "b.ts": "import './a';" } },
    { files: { "a.ts": "/// <reference path='./b.ts' />", "b.ts": "/// <reference path='./c.ts' />", "c.ts": "let x=1" } },
    { files: { "a.ts": "/// <reference path='./missing.ts' />" } },
    { files: { "a.ts": "/// <reference types='foo' />", "node_modules/@types/foo/index.d.ts": "declare const foo:1" } },
    { files: { "a.ts": "import {x} from 'pkg';", "node_modules/pkg/package.json": '{"name":"pkg","version":"1","types":"index.d.ts"}', "node_modules/pkg/index.d.ts": "export const x:1" } },
    { files: { "a.ts": "import './a.json';", "a.json": '{"x":1}' } },
    { files: { "a.ts": "import './b.js';", "b.js": "exports.x=1" }, options: { allowJs: true } },
    { files: { "a.ts": "import './b.js';", "b.js": "exports.x=1" } },
    { files: { "a.ts": "import 'pkg';", "node_modules/pkg/index.js": "require('deep');", "node_modules/deep/index.js": "exports.x=1" }, options: { allowJs: true, maxNodeModuleJsDepth: 1 } },
    { files: { "a.ts": "import 'pkg';", "node_modules/pkg/index.js": "require('deep');", "node_modules/deep/index.js": "exports.x=1" }, options: { allowJs: true, maxNodeModuleJsDepth: 2 } },
    { files: { "a.ts": "import './b';", "b.ts": "let b=1" }, options: { noResolve: true } },
    { files: { "a.ts": "/// <reference no-default-lib='true' />" }, options: { noLib: false } },
    { files: { "a.ts": "" }, options: { noLib: false, target: "es2020" } },
    { files: { "a.ts": "" }, options: { noLib: false, lib: ["es2015", "dom"] } },
    { files: { "a.ts": "/// <reference lib='es2015.promise' />" }, options: { noLib: false, lib: [] } },
    { files: { "a.ts": "", "node_modules/@types/foo/index.d.ts": "declare const foo:1" }, options: { types: ["*"] } },
    { files: { "a.ts": "import './b';", "b.ts": "" }, options: { moduleDetection: "force" } },
    { files: { "a.ts": "let a=1", "package.json": '{"type":"module"}' }, options: { module: "nodenext" } },
    { files: { "a.ts": "import './b.js';", "b.ts": "", "package.json": '{"type":"module"}' }, options: { module: "nodenext" } },
    { files: { "a.ts": "import 'alias';", "lib/b.ts": "" }, options: { paths: { alias: ["./lib/b.ts"] } } },
    { files: { "a.ts": "import './b.tsx';", "b.tsx": "const b=<div/>" }, options: { jsx: "react-jsx" } },
    { files: { "a.ts": "import './b.tsx';", "b.tsx": "const b=<div/>" } },
    { files: { "a.ts": "import './b';", "b.mts": "export const x=1" } },
    { files: { "a.ts": "import 'linked';", "../package/index.d.ts": "export const x:1;", "../package/package.json": '{"name":"linked","version":"1"}' }, symlinks: { "node_modules/linked": "/package" } },
    { files: { "a.ts": "import 'linked';", "../package/index.d.ts": "export const x:1;", "../package/package.json": '{"name":"linked","version":"1"}' }, symlinks: { "node_modules/linked": "/package" }, options: { preserveSymlinks: true } },
];
let cases = [];
for (let i = 0; i < templates.length; i++) {
    for (const concurrency of [1, 4]) {
        const t = templates[i], absolute = key => path.posix.resolve("/project", key);
        cases.push({ name: `graph:${i}:${concurrency}`, directory: "/project", roots: ["a.ts"], sensitive: true, concurrency, ...t, options: { noLib: true, ...t.options }, files: Object.fromEntries(Object.entries(t.files).map(([k, v]) => [absolute(k), v])), symlinks: Object.fromEntries(Object.entries(t.symlinks ?? {}).map(([k, v]) => [absolute(k), v])) });
    }
}
for (const useSources of [false, true]) {
    for (const concurrency of [1, 4]) {
        for (const composite of [false, true]) {
            const files = {
                "/project/tsconfig.json": JSON.stringify({ compilerOptions: { noLib: true }, files: ["a.ts"], references: [{ path: "../lib" }] }),
                "/project/a.ts": "import '../lib/src/b';",
                "/lib/tsconfig.json": JSON.stringify({ compilerOptions: { composite, noLib: true, rootDir: "src", outDir: "dist" }, files: ["src/b.ts"] }),
                "/lib/src/b.ts": "export const x=1;",
                "/lib/dist/b.d.ts": "export declare const x=1;",
            };
            cases.push({ name: `reference:${useSources}:${concurrency}:${composite}`, directory: "/project", config: "/project/tsconfig.json", files, sensitive: true, useSources, concurrency });
        }
    }
}
for (const module of ["commonjs", "preserve", "nodenext", "node16"]) {
    for (const extension of [".ts", ".mts", ".cts", ".js", ".d.ts"]) {
        for (const type of ["module", "commonjs", undefined]) {
            for (const concurrency of [1, 4]) {
                cases.push({
                    name: `modes:${module}:${extension}:${type}:${concurrency}`,
                    directory: "/project",
                    roots: ["a" + extension],
                    sensitive: true,
                    concurrency,
                    options: { module, noLib: true, noEmit: true, allowJs: true },
                    files: {
                        ["/project/a" + extension]: "import { value } from 'pkg'; const p = import('pkg');",
                        "/project/package.json": JSON.stringify({ type }),
                        "/project/node_modules/pkg/package.json": '{"name":"pkg","version":"1","exports":{"import":"./import.d.mts","require":"./require.d.cts"}}',
                        "/project/node_modules/pkg/import.d.mts": "export const value:1;",
                        "/project/node_modules/pkg/require.d.cts": "export const value:2;",
                    },
                });
            }
        }
    }
}
for (const sensitive of [false, true]) for (const roots of [["a.ts"], ["a.ts", "B.ts"], ["B.ts", "a.ts"]]) for (const concurrency of [1, 4]) cases.push({ name: `casing:${sensitive}:${roots}:${concurrency}`, directory: "/project", roots, sensitive, concurrency, options: { noLib: true, noEmit: true }, files: { "/project/a.ts": "import './b'; import './B';", "/project/b.ts": "export {};", ...sensitive ? { "/project/B.ts": "export {};" } : {} } });
for (const concurrency of [1, 4]) {
    cases.push({
        name: `depth-revisit:${concurrency}`,
        directory: "/project",
        roots: ["a.ts"],
        sensitive: true,
        concurrency,
        options: { noLib: true, noEmit: true, allowJs: true, maxNodeModuleJsDepth: 1 },
        files: {
            "/project/a.ts": "import 'p'; import './bridge1';",
            "/project/bridge1.ts": "import './bridge2';",
            "/project/bridge2.ts": "import './local.js';",
            "/project/node_modules/p/index.js": "require('../../local.js');",
            "/project/local.js": "require('dep');",
            "/project/node_modules/dep/index.js": "exports.value=1;",
        },
    });
    for (const useSources of [false, true]) {
        for (const preserveSymlinks of [false, true]) {
            cases.push({
                name: `reference-symlink:${useSources}:${preserveSymlinks}:${concurrency}`,
                directory: "/project",
                config: "/project/tsconfig.json",
                sensitive: true,
                concurrency,
                useSources,
                symlinks: { "/project/node_modules/lib": "/lib" },
                files: {
                    "/project/tsconfig.json": JSON.stringify({ compilerOptions: { noLib: true, noEmit: true, preserveSymlinks }, files: ["a.ts"], references: [{ path: "../lib" }] }),
                    "/project/a.ts": "import 'lib';",
                    "/lib/package.json": '{"name":"lib","version":"1","types":"dist/index.d.ts"}',
                    "/lib/tsconfig.json": JSON.stringify({ compilerOptions: { noLib: true, composite: true, rootDir: "src", outDir: "dist" }, files: ["src/index.ts", "src/inner.ts"] }),
                    "/lib/src/index.ts": "import './inner.js'; export {};",
                    "/lib/src/inner.ts": "export {};",
                    ...useSources ? {} : { "/lib/dist/index.d.ts": "import './inner.js'; export {};", "/lib/dist/inner.d.ts": "export {};" },
                },
            });
        }
    }
    cases.push({
        name: `dedup:${concurrency}`,
        directory: "/project",
        roots: ["a.ts"],
        sensitive: true,
        concurrency,
        options: { noLib: true, noEmit: true },
        files: {
            "/project/a.ts": "import './nested/b'; import 'p';",
            "/project/nested/b.ts": "import 'p';",
            "/project/node_modules/p/package.json": '{"name":"p","version":"1","types":"index.d.ts"}',
            "/project/node_modules/p/index.d.ts": "export const root:1;",
            "/project/nested/node_modules/p/package.json": '{"name":"p","version":"1","types":"index.d.ts"}',
            "/project/nested/node_modules/p/index.d.ts": "export const nested:1;",
        },
    });
    cases.push({ name: `self-reference:${concurrency}`, directory: "/project", roots: ["a.ts"], sensitive: true, concurrency, options: { noLib: true, noEmit: true }, files: { "/project/a.ts": "/// <reference path='./a.ts' />" } });
    for (const jsx of ["react-jsx", "react-jsxdev", "preserve"]) {
        for (const pragma of ["", "/** @jsxRuntime classic */", "/** @jsxImportSource other */", "/** @jsxRuntime automatic */"]) {
            cases.push({
                name: `jsx:${jsx}:${pragma}:${concurrency}`,
                directory: "/project",
                roots: ["a.tsx"],
                sensitive: true,
                concurrency,
                options: { noLib: true, noEmit: true, jsx, importHelpers: true, isolatedModules: true },
                files: {
                    "/project/a.tsx": pragma + "\nconst view=<div/>;",
                    "/project/node_modules/tslib/index.d.ts": "export {};",
                    "/project/node_modules/react/jsx-runtime.d.ts": "export {};",
                    "/project/node_modules/react/jsx-dev-runtime.d.ts": "export {};",
                    "/project/node_modules/other/jsx-runtime.d.ts": "export {};",
                    "/project/node_modules/other/jsx-dev-runtime.d.ts": "export {};",
                },
            });
        }
    }
}
if (option("--filter")) cases = cases.filter(c => c.name.includes(option("--filter")));
await json(path.join(output, "program-inputs.json"), cases);
async function probe(command, args) {
    return new Promise((resolve, reject) => {
        const child = spawn(command, args, { windowsHide: true });
        const out = [], errors = [];
        child.stdout.on("data", b => out.push(b));
        child.stderr.on("data", b => errors.push(b));
        child.on("error", reject);
        child.stdin.on("error", () => {});
        child.on("close", code => {
            const lines = Buffer.concat(out).toString().trim().split(/\r?\n/).filter(Boolean);
            if (code) reject(new Error(`${command} ${code}; next ${cases[lines.length]?.name}: ${Buffer.concat(errors)}`));
            else resolve(lines.map(x => JSON.parse(x)));
        });
        child.stdin.end(cases.map(c => JSON.stringify(c)).join("\n") + "\n");
    });
}
const expected = await probe(oracle, []), actual = await probe(candidate, args);
assert.equal(expected.length, cases.length);
assert.equal(actual.length, cases.length);
const failures = [];
for (let i = 0; i < cases.length; i++) {
    try {
        assert.deepEqual(actual[i], expected[i]);
    }
    catch {
        failures.push({ input: cases[i], expected: expected[i], actual: actual[i] });
    }
}
await json(path.join(output, "program-failures.json"), failures);
const summary = { timestamp: new Date().toISOString(), referenceRevision, managed, cases: cases.length, passed: cases.length - failures.length, failed: failures.length, inputSha256: sha256(JSON.stringify(cases)), oracleSha256: sha256(await readFile(oracle)), candidateSha256: sha256(await readFile(managed ? dll : candidate)) };
await json(path.join(output, "program-summary.json"), summary);
if (option("--record")) await json(path.join(root, `csharp/compatibility/evidence/${option("--record")}.json`), summary);
console.log(summary);
if (failures.length) {
    for (const f of failures.slice(0, 10)) console.log(f.input.name, f.expected[0].map(f => f[0]), f.actual[0].map(f => f[0]), f.expected[1], f.actual[1]);
    process.exitCode = 1;
}
