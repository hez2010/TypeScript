import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import {
    mkdir,
    readFile,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import {
    referenceRevision,
    root,
} from "./common.mjs";

const packageMaps = process.argv.includes("--packages");
const nodeModules = process.argv.includes("--node-modules");
const output = path.join(root, `built/csharp/module-specifier-${nodeModules ? "node-modules" : packageMaps ? "packages" : "paths"}`);
const hash = value => createHash("sha256").update(value).digest("hex");
const dotnet = process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, "dotnet.exe") : "dotnet";
const oracle = path.join(root, "built/csharp/module-specifier-oracle.exe");
const dll = path.join(root, "csharp/tests/TypeScript.Compatibility/bin/Release/net11.0/TypeScript.Compatibility.dll");
const option = key => process.argv[process.argv.indexOf(key) + 1];
await mkdir(output, { recursive: true });
const oracleHash = hash(await readFile(oracle));
const candidateHash = hash(Buffer.concat([await readFile(dll), await readFile(path.join(root, "csharp/src/TypeScript.Compiler/bin/Release/net11.0/TypeScript.Compiler.dll"))]));
const cases = [];
const base = { source: "", fileName: "/project/main.ts", directory: "/project", sensitive: true, files: {}, options: {} };
function add(operation, fields) {
    cases.push({ ...base, operation, ...fields });
}
for (const options of [{}, { moduleResolution: "node16" }, { module: "nodenext" }, { moduleResolution: "bundler", module: "node20" }]) for (const allow of [{}, { allowImportingTsExtensions: true }, { rewriteRelativeImportExtensions: true }]) for (const source of ["", "import './x';import './y.js';", "import './x.js';import './y.ts';", "import './x.ts';import './y.js';", "import './x.mts';import './y.cjs';"]) for (const fileName of ["/project/main.ts", "/project/main.d.ts"]) for (const preference of ["", "minimal", "index", "js"]) for (const oldSpecifier of ["", "./old.js", "./old/index"]) for (const defaultMode of [0, 1, 99]) for (const mode of [0, 1, 99]) add("endings", { options: { ...options, ...allow }, source, fileName, preference, oldSpecifier, defaultMode, mode });
const endings = [[0, 1, 2], [1, 0, 2], [2, 0, 1], [3, 0, 2, 1], [2, 3, 0, 1], [3, 2], [0], [1], [2], [3]];
for (const target of ["./file.ts", "./file.tsx", "./file.d.ts", "./file.mts", "./file.cts", "./file.d.mts", "./file.d.cts", "./file.js", "./file.jsx", "./file.mjs", "./file.cjs", "./file.json", "./file", "./file.css", "./file.d.css.ts", "./file.module.d.css.ts", "./file.d.json.ts", "./file.d.one.two.ts", "./folder/index.ts", "./folder/index.d.ts", "./folder/index.tsx", "./index.d.ts", "./nested/folder/index.d.ts"]) for (const ending of endings) for (const jsx of ["preserve", "react-jsx"]) for (const files of [{}, { "/project/folder.ts": "", "/project/nested/folder.json": "" }]) add("process", { target, endings: ending, options: { jsx }, files });
for (const target of ["foo.d.json.ts", "foo.module.d.css.ts", "foo.d.ts", "foo.ts", "foo.d.one.two.ts", "./dir.d.x/foo.d.css.ts", "foo.css", "foo.d.css.mts"]) add("non-js", { target });
for (const sensitive of [true, false]) for (const roots of [["/project/src", "/project/generated"], ["/project", "/project/src", "/project/generated"], ["C:/src", "D:/generated"], ["//server/share/src", "//server/share/generated"]]) for (const sourceDirectory of roots.flatMap(root => [root, root + "/sub"])) for (const target of roots.flatMap(root => [root + "/folder/index.ts", root + "/sub/file.ts", root.toUpperCase() + "/sub/file.d.ts"])) for (const ending of endings.slice(0, 4)) add("roots", { sensitive, roots, sourceDirectory, target, endings: ending });
for (const sensitive of [true, false]) for (const target of ["src/item.ts", "src/item.d.ts", "src/folder/index.d.ts", "src/file.js.js", "SRC/ITEM.ts", "../item.ts", "src/foo.d.css.ts"]) for (const paths of [[["@app/*", ["./src/*"]]], [["@app/*", ["./src/*.ts"]]], [["@app/*", ["./src/*.d.ts"]]], [["exact", ["./src/item.ts"]], ["@app/*", ["./src/*"]]], [["first/*", ["./src/*"]], ["second/*", ["./src/*"]]], [["@app/*", ["C:/other/*", "/project/src/*"]]], [["@item", ["./src/item"]]], [["@app/*", ["./src/*.js"]]]]) for (const ending of endings.slice(0, 6)) for (const files of [{}, { "/project/src/folder.ts": "", "/project/src/file.js": "" }]) add("paths", { sensitive, target, paths, endings: ending, baseDirectory: "/project", files });
if (packageMaps) {
    cases.length = 0;
    const optionSets = [{}, { outDir: "lib", declarationDir: "types" }, { outDir: "lib", jsx: "preserve" }, { declarationDir: "types", moduleResolution: "nodenext" }];
    const conditionSets = [["import", "types", "node"], ["require", "types", "node"], ["import", "custom"], ["types"]];
    const targets = ["/project/src/item.ts", "/project/lib/item.ts", "/project/lib/item.d.ts", "/project/lib/item.tsx", "/project/lib/item.jsx", "/project/lib/item.mts", "/project/lib/item.d.mts", "/project/lib/item.cts", "/project/lib/item.cjs", "/project/lib/item.json", "/project/types/item.d.ts", "/project/types/item.d.mts", "/project/lib/item.vue", "/PROJECT/LIB/ITEM.ts"];
    const mapCases = [
        ["./lib/item.js", "pkg", 0],
        ["./lib/item.d.ts", "pkg", 0],
        ["./lib/*.js", "pkg/*", 2],
        ["./lib/*", "pkg/*", 2],
        ["./lib/", "pkg/", 1],
        ["./types/*.d.ts", "pkg/*", 2],
        ["./types/", "pkg/", 1],
        [{ types: "./types/item.d.ts", import: "./lib/item.js", require: "./lib/item.cjs", default: "./src/item.js" }, "pkg", 0],
        [{ "types@>=7.1.0-dev": "./types/item.d.ts", "default": "./lib/item.js" }, "pkg", 0],
        [{ "types@>99": "./types/item.d.ts", "types@invalid": "./lib/item.js", "custom": "./lib/item.js" }, "pkg", 0],
        [[null, { unknown: "./none.js" }, "./lib/item.js"], "pkg", 0],
        [false, "pkg", 0],
    ];
    for (const [packageMap, packageName, matchMode] of mapCases) for (const target of targets) for (const options of optionSets) for (const conditions of conditionSets) for (const imports of [false, true]) for (const preferTypeScript of [false, true]) for (const sensitive of [false, true]) add("package-map", { packageMap, packageName, matchMode, target, options, conditions, imports, preferTypeScript, sensitive, packageDirectory: "/project", commonDirectory: "/project/src", mapperExtensions: [".vue"] });
    for (const packageMap of ["./lib/item.js", { ".": "./lib/item.js", "./*": "./lib/*.js" }, { "./sub/": "./lib/" }, { default: "./lib/item.js" }, { "./*": [null, "./lib/*.js"] }, { ".": { types: "./types/item.d.ts", default: "./lib/item.js" } }, { ".": "./lib/item.js", "default": "./types/item.d.ts" }]) for (const target of targets) for (const conditions of conditionSets) for (const sensitive of [true, false]) add("package-exports", { packageMap, target, conditions, sensitive, packageName: "@scope/pkg", packageDirectory: "/project" });
    for (const imports of [{ "#item": "./lib/item.js" }, { "#*": "./lib/*.js" }, { "#/items/*": "./lib/*.js" }, { "#": "./lib/item.js", "#/": "./lib/item.js", "plain": "./lib/item.js", "#valid": "./lib/item.js" }, { "#item": { types: "./types/item.d.ts", import: "./lib/item.js", require: "./lib/item.cjs" } }, { "#item": [null, "./types/item.d.ts", "./lib/item.js"] }, "./lib/item.js", null]) for (const target of targets) for (const resolution of ["bundler", "node16", "nodenext"]) for (const mode of [0, 1, 99]) for (const preferTypeScript of [false, true]) add("package-imports", { target, mode, preferTypeScript, sourceDirectory: "/project/src/nested", commonDirectory: "/project/src", options: { outDir: "lib", declarationDir: "types", moduleResolution: resolution }, files: { "/project/package.json": JSON.stringify({ imports }) } });
    for (const options of optionSets) for (const target of [...targets, "/project/src/style.module.css", "/project/src/noextension", "/project/src/item.d.css.ts"]) for (const sensitive of [true, false]) add("output-paths", { options, target, sensitive, commonDirectory: "/project/src", mapperExtensions: [".module.css", ".css", ".vue"] });
    for (const options of [{}, { moduleResolution: "node16" }, { moduleResolution: "nodenext" }, { module: "node20" }, { moduleResolution: "bundler", customConditions: ["custom", "custom"] }]) for (const mode of [0, 1, 99]) add("package-conditions", { options, mode });
}
if (nodeModules) {
    cases.length = 0;
    const manifests = [undefined, "{", {}, { name: "wrong-name", types: "lib/main.d.ts" }, { typings: "lib/main.d.ts", types: "other.d.ts", main: "index.js" }, { typings: "", types: "lib/main.d.ts" }, { main: "lib" }, { type: "module", main: "lib" }, { main: "./LIB/main.js" }, { exports: null }, { name: "wrong-name", exports: { ".": "./lib/main.js", "./features/*": "./lib/*.js" } }, { exports: { ".": { import: "./lib/main.mjs", require: "./lib/main.cjs", types: "./lib/main.d.ts" } } }, { exports: { "./first": "./lib/main.js", "./second": "./lib/main.js" } }, { main: "index.js", typesVersions: { "*": { "*": ["lib/*"] } } }, { main: "index.js", typesVersions: { "*": { "index.js": ["lib/main.d.ts"] } } }, { types: "lib/main.d.ts", typesVersions: { "*": { "lib/*": ["hidden/*"] } } }, { typesVersions: { "bad-version": {}, "<7": { "*": ["old/*"] }, "*": { "feature": ["lib/main"], "*": ["lib/*"] } } }, { typesVersions: { "*": { bad: false, valid: [null, false, "lib/main.d.ts"] } } }, {
        typesVersions: { "*": false, ">=7": { "*": ["lib/*"] } },
    }];
    const targets = ["index.d.ts", "index.js", "index.tsx", "index.mts", "lib/main.d.ts", "lib/main.ts", "lib/main.d.mts", "lib/main.cts", "lib/index.d.ts", "lib/index.mts", "lib/feature.d.css.ts", "lib/data.json"];
    for (const manifest of manifests) {
        for (const suffix of targets) {
            for (const sensitive of [true, false]) {
                for (const mode of [1, 99]) {
                    const packageRoot = "/project/node_modules/pkg";
                    add("node-modules", { target: packageRoot + "/" + suffix, sensitive, mode, defaultMode: mode, options: { moduleResolution: "nodenext" }, files: manifest === undefined ? {} : { [packageRoot + "/package.json"]: typeof manifest === "string" ? manifest : JSON.stringify(manifest) } });
                }
            }
        }
    }
    for (const packagePath of ["pkg", "@scope/pkg", "@types/pkg", "@types/scope__pkg", "outer/node_modules/inner", "@scope/outer/node_modules/@types/a__b"]) for (const suffix of ["index.d.ts", "lib/main.d.ts"]) for (const fileName of ["/project/main.ts", "/project/sub/main.ts", "/other/main.ts", "/PROJECT/main.ts"]) for (const sensitive of [true, false]) for (const packageNameOnly of [true, false]) for (const redirect of [true, false]) add("node-modules", { target: "/project/node_modules/" + packagePath + "/" + suffix, fileName, sensitive, packageNameOnly, redirect });
    for (const options of [{}, { allowImportingTsExtensions: true }, { resolvePackageJsonExports: false }, { moduleResolution: "node16" }, { moduleResolution: "bundler" }]) for (const preference of ["", "minimal", "index", "js"]) for (const defaultMode of [0, 1, 99]) for (const mode of [0, 1, 99]) for (const target of ["/project/node_modules/pkg/lib/main.d.ts", "/project/node_modules/pkg/lib/main.d.mts"]) add("node-modules", { target, options, preference, defaultMode, mode, files: { "/project/node_modules/pkg/package.json": JSON.stringify({ exports: { "./import": { import: "./lib/main.js" }, "./require": { require: "./lib/main.js" }, "./esm": "./lib/main.mjs" } }) } });
    for (const globalTypingsCache of ["", "/project/cache", "/elsewhere/cache"]) for (const redirect of [true, false]) for (const files of [{}, { "/project/node_modules/pkg/package.json": JSON.stringify({ exports: { ".": "./index.js" } }) }]) add("node-modules", { target: "/project/node_modules/pkg/index.d.ts", globalTypingsCache, redirect, files });
    // The reference VFS cannot represent UNC roots; these need a different host fixture.
    for (const target of ["/project/src/file.ts", "/project/node_modules", "/project/node_modules/pkg", "/project/node_modules/@scope/pkg", "/project/node_modules/pkg/lib/item.ts", "C:/project/node_modules/pkg/index.d.ts"]) add("node-modules", { target, directory: target.startsWith("C:") ? "c:/project" : "/project", fileName: target.startsWith("C:") ? "c:/project/main.ts" : "/project/main.ts" });
}
const selected = process.argv.includes("--filter") ? cases.filter(c => c.operation === option("--filter")) : cases;
await writeFile(path.join(output, "inputs.json"), JSON.stringify(selected));
if (process.argv.includes("--list")) process.exit(0);
async function run(role, command, args, executableHash) {
    const cacheFile = path.join(output, role + "-cache.json");
    let cache = {};
    try {
        cache = JSON.parse(await readFile(cacheFile, "utf8"));
    }
    catch (error) {
        if (error.code !== "ENOENT") throw error;
    }
    const key = input => hash(JSON.stringify(input) + executableHash);
    const pending = selected.filter(input => !(key(input) in cache));
    if (pending.length) {
        const result = await new Promise((resolve, reject) => {
            const child = spawn(command, args, { windowsHide: true });
            const stdout = [], stderr = [];
            child.stdout.on("data", data => stdout.push(data));
            child.stderr.on("data", data => stderr.push(data));
            child.on("error", reject);
            child.stdin.on("error", () => {});
            child.on("close", code => resolve({ code, stdout: Buffer.concat(stdout).toString(), stderr: Buffer.concat(stderr).toString() }));
            child.stdin.end(pending.map(JSON.stringify).join("\n") + "\n");
        });
        const lines = result.stdout.trim().split(/\r?\n/).filter(Boolean);
        for (let i = 0; i < lines.length; i++) cache[key(pending[i])] = JSON.parse(lines[i]);
        await writeFile(cacheFile, JSON.stringify(cache));
        if (result.code || lines.length !== pending.length) throw Error(`${role} failed at ${JSON.stringify(pending[lines.length])}: ${result.stderr}`);
    }
    return { rows: selected.map(input => cache[key(input)]), executed: pending.length, reused: selected.length - pending.length };
}
const reference = await run("reference", oracle, [], oracleHash);
const candidate = await run("candidate", dotnet, [dll, "--module-specifiers-lines"], candidateHash);
const failures = [];
for (let i = 0; i < selected.length; i++) {
    try {
        assert.deepStrictEqual(candidate.rows[i], reference.rows[i]);
    }
    catch {
        failures.push({ input: selected[i], reference: reference.rows[i], candidate: candidate.rows[i] });
    }
}
await writeFile(path.join(output, "failures.json"), JSON.stringify(failures, null, 2));
const summary = { cases: selected.length, exact: selected.length - failures.length, failed: failures.length, byOperation: Object.fromEntries([...new Set(selected.map(c => c.operation))].map(operation => [operation, selected.filter(c => c.operation === operation).length])), candidateExecuted: candidate.executed, candidateReused: candidate.reused, referenceExecuted: reference.executed, referenceReused: reference.reused, referenceRevision, oracleHash, candidateHash, inputHash: hash(JSON.stringify(selected)), referenceHash: hash(JSON.stringify(reference.rows)), outputHash: hash(JSON.stringify(candidate.rows)), managed: true };
await writeFile(path.join(output, "summary.json"), JSON.stringify(summary, null, 2));
if (process.argv.includes("--record")) await writeFile(path.join(root, "csharp/compatibility/evidence", option("--record") + ".json"), JSON.stringify(summary, null, 4) + "\n");
console.log(summary);
console.log(failures.slice(0, 4));
if (failures.length) process.exitCode = 1;
