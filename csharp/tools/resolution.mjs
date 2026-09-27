import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import {
    copyFile,
    mkdir,
    readFile,
} from "node:fs/promises";
import path from "node:path";
import { gzipSync } from "node:zlib";
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
const oracle = path.join(output, "resolution-oracle.exe");
const managed = process.argv.includes("--managed");
const native = path.join(output, "phase3-native");
const candidate = managed ? dotnet : path.join(native, "TypeScript.Compatibility.exe");
const dll = path.join(root, "csharp/tests/TypeScript.Compatibility/bin/Release/net11.0/TypeScript.Compatibility.dll");
const args = [...managed ? [dll] : [], "--resolution-lines"];
await mkdir(path.join(source, "cmd/resolution-probe"), { recursive: true });
await copyFile(path.join(root, "csharp/oracle/resolution/main.go"), path.join(source, "cmd/resolution-probe/main.go"));
await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", oracle, "./cmd/resolution-probe"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
if (!process.argv.includes("--no-build")) {
    await run(
        dotnet,
        managed ? ["build", "tests/TypeScript.Compatibility", "-c", "Release", "--no-restore"]
            : ["publish", "tests/TypeScript.Compatibility", "-p:PublishAot=true", "-c", "Release", "-r", "win-x64", "-p:IlcInstructionSet=native", "-p:RestoreLockedMode=true", "-o", native],
        { cwd: path.join(root, "csharp"), env: { ...process.env, DOTNET_ROOT: path.dirname(dotnet), PATH: path.dirname(dotnet) + path.delimiter + process.env.PATH } },
    );
}
let cases = [];
const literals = async file =>
    [...(await readFile(path.join(source, file), "utf8")).matchAll(/"(?:[^"\\]|\\.)*"/g)]
        .map(m => {
            try {
                return JSON.parse(m[0]);
            }
            catch {
                return null;
            }
        }).filter(x => x !== null);
const versions = [...new Set(await literals("internal/semver/version_test.go"))];
const ranges = [...new Set(await literals("internal/semver/version_range_test.go"))];
for (const value of versions) for (const other of versions) cases.push({ operation: "version", path: value, other });
for (const value of ranges) for (const other of versions) cases.push({ operation: "range", path: value, other });
const base = { directory: "/project", containingFile: "/project/src/main.ts", sensitive: true, operation: "resolve" };
const extensions = [".ts", ".tsx", ".d.ts", ".mts", ".d.mts", ".cts", ".d.cts", ".js", ".jsx", ".mjs", ".cjs", ".json", ".d.css.ts", ".vue"];
const modes = [0, 1, 99];
const settings = [{}, { moduleResolution: "bundler" }, { moduleResolution: "node16" }, { moduleResolution: "nodenext" }, { noDtsResolution: true }, { resolveJsonModule: false }, { moduleSuffixes: [".native", ""] }, { module: "commonjs" }];
for (const options of settings) {
    for (const mode of modes) {
        for (const ext of extensions) {
            for (const spec of ["./a", "./a.js", "./a.ts", "./a" + ext, "./a/", "./a/index.js"]) {
                for (const files of [{ ["/project/src/a" + ext]: "" }, { ["/project/src/a/index" + ext]: "" }, { ["/project/src/a.native" + ext]: "" }]) cases.push({ ...base, options, mode, path: spec, files, extraExtensions: [".vue"] });
            }
        }
    }
}
const manifests = [
    {},
    { main: "a.js" },
    { main: "a" },
    { main: "lib" },
    { types: "a.d.ts" },
    { typings: "a.d.ts", types: "b.d.ts" },
    { main: "a.js", types: "a.d.ts" },
    { main: "a.js", type: "module" },
    { main: "a.js", exports: "./b.js" },
    { exports: { import: "./a.mjs", require: "./b.cjs" } },
    { exports: { types: "./a.d.ts", default: "./b.js" } },
    { exports: { "types@>=7": "./a.d.ts", "types": "./b.d.ts", "default": "./a.js" } },
    { exports: { "./*": "./lib/*.js", ".": "./a.js" } },
    { exports: { "./*.js": "./lib/*.js", "./private/*": null } },
    { exports: ["./missing.js", "./a.js"] },
    { exports: [null, "./a.js"] },
    { exports: null },
    { exports: {} },
    { typesVersions: { ">=7": { "*": ["types/*"] }, "*": { "*": ["old/*"] } }, types: "a.d.ts" },
    { exports: { custom: "./a.d.ts", default: "./b.js" } },
    { exports: "../escape.js" },
];
for (const manifest of manifests) {
    for (const options of [{}, { noDtsResolution: true }, { customConditions: ["custom"] }, { resolvePackageJsonExports: false }, { moduleResolution: "node16" }]) {
        for (const mode of modes) {
            for (const spec of ["pkg", "pkg/", "pkg/a", "pkg/a.js", "pkg/private/a"]) {
                const files = { "/project/node_modules/pkg/package.json": JSON.stringify({ name: "pkg", version: "1.2.3", ...manifest }) };
                for (const name of ["a.ts", "a.d.ts", "b.d.ts", "a.js", "b.js", "a.mts", "b.cts", "index.ts", "lib/index.ts", "lib/a.ts", "types/a.d.ts", "types/index.d.ts"]) files["/project/node_modules/pkg/" + name] = "";
                cases.push({ ...base, path: spec, options, mode, files });
            }
        }
    }
}
for (const options of [{}, { typeRoots: ["./types"] }, { types: ["*"] }, { types: ["foo", "*", "bar"] }, { types: [] }]) {
    const files = { "/project/types/foo/index.d.ts": "", "/project/node_modules/@types/foo/index.d.ts": "", "/project/node_modules/@types/bar/package.json": '{"typings":null}', "/project/node_modules/@types/bar/index.d.ts": "" };
    cases.push({ ...base, operation: "automatic", options, files });
    for (const spec of ["foo", "bar", "missing", "./a"]) for (const mode of modes) cases.push({ ...base, operation: "types", path: spec, options, files, mode });
}
for (const containingFile of ["/project/main.ts", "/project/__inferred type names__.ts"]) for (const typeRoots of [[], ["./custom"]]) cases.push({ ...base, containingFile, operation: "types", path: "foo", options: { typeRoots }, files: { "/project/node_modules/@types/foo/index.d.ts": "" } });
for (const options of [{}, { moduleResolution: "node16" }, { moduleResolution: "nodenext" }, { rootDir: "src", outDir: "dist" }]) {
    for (const spec of ["#local", "#external", "#x/a", "#/root", "self", "self/a", "#", "#missing"]) {
        for (const mode of modes) {
            cases.push({
                ...base,
                path: spec,
                mode,
                options,
                files: {
                    "/project/package.json": JSON.stringify({ name: "self", version: "1", exports: { ".": "./dist/a.js", "./*": "./src/*.js" }, imports: { "#local": "./src/a.js", "#external": "pkg", "#x/*": "./src/*.js", "#/root": "./src/a.js" } }),
                    "/project/src/a.ts": "",
                    "/project/dist/a.d.ts": "",
                    "/project/node_modules/pkg/index.d.ts": "",
                },
            });
        }
    }
}
for (const options of [{ paths: { "alias/*": ["src/*"] } }, { rootDirs: ["src", "generated"] }, { paths: { alias: ["missing.ts", "src/a.ts"] } }]) for (const spec of ["alias/a", "alias", "./a", "./b"]) cases.push({ ...base, path: spec, options, files: { "/project/src/a.ts": "", "/project/generated/b.ts": "" } });
for (const manifest of [{}, { tsconfig: "base.json" }, { tsconfig: "configs" }, { exports: "./base.json" }, { exports: { ".": "./base.json", "./base": "./configs/base.json" } }, { exports: { require: "./base.json", default: "./missing.json" } }, { tsconfig: "base.json", typesVersions: { ">=7": { "*": ["configs/*"] } } }, { exports: null, tsconfig: "base.json" }]) {
    for (const specifier of ["pkg", "pkg/", "pkg/base", "pkg/base.json", "pkg/configs"]) {
        cases.push({
            ...base,
            operation: "config",
            path: specifier,
            files: {
                "/project/node_modules/pkg/package.json": JSON.stringify(manifest),
                "/project/node_modules/pkg/base.json": "{}",
                "/project/node_modules/pkg/tsconfig.json": "{}",
                "/project/node_modules/pkg/configs/base.json": "{}",
                "/project/node_modules/pkg/configs/tsconfig.json": "{}",
            },
        });
    }
}
for (const specifier of ["#config", "#external", "self", "self/base"]) {
    cases.push({
        ...base,
        operation: "config",
        path: specifier,
        files: {
            "/project/package.json": JSON.stringify({ name: "self", exports: { ".": "./base.json", "./base": "./base.json" }, imports: { "#config": "./base.json", "#external": "pkg" } }),
            "/project/base.json": "{}",
            "/project/node_modules/pkg/package.json": '{"tsconfig":"tsconfig.json"}',
            "/project/node_modules/pkg/tsconfig.json": "{}",
        },
    });
}
if (process.argv.includes("--filter")) cases = cases.filter(c => c.operation === option("--filter"));
if (process.argv.includes("--trace")) {
    cases = cases.filter(c => c.operation === "resolve")
        .map(c => ({ ...c, operation: "resolveTrace", options: { ...c.options, traceResolution: true } }));
}
cases = cases.map((c, i) => ({ name: `${c.operation}:${i}:${c.path ?? ""}`, ...c }));
await json(path.join(output, "resolution-inputs.json"), cases);
async function probe(command, args) {
    return new Promise((resolve, reject) => {
        const child = spawn(command, args, { windowsHide: true });
        const out = [], errors = [];
        child.stdout.on("data", b => out.push(b));
        child.stderr.on("data", b => errors.push(b));
        child.on("error", reject);
        child.stdin.on("error", () => {});
        child.on("close", code => {
            if (code) reject(new Error(`${command} exited ${code}: ${Buffer.concat(errors)}`));
            else resolve(Buffer.concat(out).toString().trim().split(/\r?\n/).map(x => JSON.parse(x)));
        });
        child.stdin.end(cases.map(c => JSON.stringify(c)).join("\n") + "\n");
    });
}
const expected = await probe(oracle, []), actual = await probe(candidate, args);
assert.equal(expected.length, cases.length);
assert.equal(actual.length, cases.length);
const failures = [];
const traceRecords = [];
for (let i = 0; i < cases.length; i++) {
    try {
        if (cases[i].operation === "resolveTrace") {
            assert.deepEqual(actual[i][0], expected[i][0]);
            const [result, trace] = actual[i];
            assert.equal(trace.at(-1)?.[0], "result");
            assert.equal(trace.at(-1)?.[1], result[0]);
            if (result[0]) assert(trace.some(t => t[0] === "file" && t[2] === "exists" && (t[1] === result[0] || t[1] === result[2])));
            assert(expected[i][1].length > 0);
            traceRecords.push({ input: cases[i], expected: expected[i], actual: actual[i] });
        }
        else assert.deepEqual(actual[i], expected[i]);
    }
    catch {
        failures.push({ input: cases[i], expected: expected[i], actual: actual[i] });
    }
}
await json(path.join(output, "resolution-failures.json"), failures);
const summary = { timestamp: new Date().toISOString(), referenceRevision, managed, cases: cases.length, passed: cases.length - failures.length, failed: failures.length, inputSha256: sha256(JSON.stringify(cases)), oracleSha256: sha256(await readFile(oracle)), candidateSha256: sha256(await readFile(managed ? dll : candidate)) };
await json(path.join(output, "resolution-summary.json"), summary);
if (option("--record")) {
    const record = option("--record");
    await json(path.join(root, `csharp/compatibility/evidence/${record}.json`), summary);
    if (traceRecords.length) {
        const compressed = gzipSync(JSON.stringify({ referenceRevision, cases: traceRecords }));
        await (await import("node:fs/promises")).writeFile(path.join(root, `csharp/compatibility/evidence/${record}-differences.outputs.json.gz`), compressed);
        await json(path.join(root, `csharp/compatibility/evidence/${record}-differences.json`), {
            referenceRevision,
            policy: "structured-resolution-events",
            rationale: "The C# resolver retains structured lookup, mapping, condition and result events. Each resolution result exactly matches Go, and every reported successful result is backed by a recorded successful file lookup. Internal trace representation and cache scheduling are implementation details; CLI rendering follows in its host phase.",
            outputsFile: `${record}-differences.outputs.json.gz`,
            outputsSha256: sha256(compressed),
            reproduction: "node csharp/tools/resolution.mjs --trace",
            cases: traceRecords.map(r => ({ name: r.input.name, inputSha256: sha256(JSON.stringify(r.input)), expectedSha256: sha256(JSON.stringify(r.expected)), actualSha256: sha256(JSON.stringify(r.actual)) })),
        });
    }
}
console.log(summary);
if (failures.length) {
    console.log(failures.slice(0, 6));
    process.exitCode = 1;
}
