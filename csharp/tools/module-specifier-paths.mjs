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
const generation = process.argv.includes("--generation");
const programHost = process.argv.includes("--program");
const printNodes = process.argv.includes("--print");
const output = path.join(root, `built/csharp/module-specifier-${printNodes ? "node-printer" : programHost ? "program" : generation ? "generation" : nodeModules ? "node-modules" : packageMaps ? "packages" : "paths"}`);
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
if (generation) {
    cases.length = 0;
    const pathScenarios = [
        { target: "/project/lib/item.ts" },
        { target: "/project/lib/item.ts", referenceOutput: "/project/dist/item.d.ts", redirects: ["/project/lib/item.ts", "/other/item.ts", "/project/near/item.ts"] },
        { target: "/store/pkg/lib/item.d.ts", symlinks: { "/store/pkg": ["/project/node_modules/pkg", "/project/sub/node_modules/pkg", "/other/node_modules/pkg"] } },
        { target: "/project/node_modules/.pnpm/pkg/index.d.ts", symlinks: { "/project/node_modules/.pnpm/pkg": ["/project/node_modules/pkg"] } },
        { target: "/project/node_modules/.pnpm/pkg/index.d.ts", redirects: ["/project/.git/pkg/index.d.ts", "/project/.#pkg/index.d.ts"] },
        { target: "/project/node_modules/.pnpm/pkg/index.d.ts", redirects: ["/project/pkg/index.d.ts"] },
        { target: "/store/pkg/src/item.ts", referenceOutput: "/store/pkg/dist/item.d.ts", symlinks: { "/store/pkg": ["/project/node_modules/pkg"], "/store": ["/project/node_modules/store"] } },
        { target: "/project/lib/item.ts", redirects: ["/project/lib/😀.ts", "/project/lib/\ue000.ts", "/project/lib/A.ts", "/project/lib/z.ts"] },
        { target: "/PROJECT/lib/item.ts", symlinks: { "/project/lib": ["/project/sub/node_modules/pkg"] } },
    ];
    for (const scenario of pathScenarios) for (const fileName of ["/project/main.ts", "/project/sub/main.ts", "/store/pkg/own.ts"]) for (const sensitive of [true, false]) for (const globalTypingsCache of ["", "/store/pkg"]) add("all-paths", { ...scenario, fileName, sensitive, globalTypingsCache });
    const optionsSets = [{}, { resolvePackageJsonImports: false }, { rootDirs: ["src", "generated"] }, { paths: { "@lib/*": ["./lib/*"], "@src/*": ["./src/*"], "@outside/*": ["../outside/*"] } }, { paths: { "long/path/*": ["./src/*"] }, moduleResolution: "nodenext" }, { paths: { "../alias/*": ["./src/*"] }, rewriteRelativeImportExtensions: true }, { paths: {}, rootDirs: ["src", "generated"], allowImportingTsExtensions: true }];
    for (const options of optionsSets) for (const target of ["/project/src/item.ts", "/project/generated/item.ts", "/project/lib/item.d.ts", "/outside/item.ts", "/project/lib/index.d.ts"]) for (const relative of ["shortest", "relative", "non-relative", "project-relative"]) for (const mode of [1, 99]) for (const pathsOnly of [true, false]) add("local", { target, options, relative, mode, defaultMode: mode, pathsOnly, fileName: "/project/src/main.ts", files: { "/project/package.json": JSON.stringify({ imports: { "#lib/*": "./lib/*.js" } }), "/outside/package.json": "{}" } });
    for (const excludedPrefix of ["./", "@", "#", "unmatched"]) add("local", { target: "/project/src/item.ts", fileName: "/project/src/main.ts", options: { paths: { "@deep/path/*": ["src/*"] } }, excludedPrefix });
    const modulePaths = [
        [{ FileName: "/project/src/item.ts", IsInNodeModules: false, IsRedirect: false }],
        [{ FileName: "/project/node_modules/pkg/index.d.ts", IsInNodeModules: true, IsRedirect: true }, { FileName: "/project/src/item.ts", IsInNodeModules: false, IsRedirect: false }],
        [{ FileName: "/project/node_modules/pkg/lib/item.d.ts", IsInNodeModules: true, IsRedirect: false }, { FileName: "/project/src/item.ts", IsInNodeModules: false, IsRedirect: false }],
        [{ FileName: "/other/node_modules/pkg/lib/item.d.ts", IsInNodeModules: true, IsRedirect: false }, { FileName: "/project/src/item.ts", IsInNodeModules: false, IsRedirect: false }],
        [{ FileName: "/project/dist/item.d.ts", IsInNodeModules: false, IsRedirect: true }, { FileName: "/project/src/item.ts", IsInNodeModules: false, IsRedirect: false }],
    ];
    for (const paths of modulePaths) for (const relative of ["shortest", "relative", "non-relative", "project-relative"]) for (const forAutoImport of [false, true]) for (const excludedPrefix of ["", "pkg", "@", "./"]) add("select", { modulePaths: paths, relative, forAutoImport, excludedPrefix, options: { paths: { "@src/*": ["src/*"], "@out/*": ["dist/*"] } } });
    for (const mode of [0, 1, 99]) for (const defaultMode of [0, 1, 99]) for (const existingMode of [0, 1, 99]) for (const relative of ["shortest", "non-relative"]) add("select", { modulePaths: modulePaths[0], mode, defaultMode, relative, source: "import './existing'; import 'alternative';", importTargets: { "./existing": "/project/src/item.ts", "alternative": "/project/src/item.ts" }, importModes: { "./existing": existingMode, "alternative": mode } });
    for (const scenario of pathScenarios) for (const relative of ["shortest", "project-relative"]) for (const mode of [1, 99]) add("generate", { ...scenario, relative, mode, defaultMode: mode, originalSource: "/project/src/main.ts", options: { moduleResolution: "nodenext", paths: { "@lib/*": ["lib/*"] } } });
    add("select", {
        source: "import ''; import 'other';",
        importTargets: { "": "/project/first.ts", "other": "/project/second.ts" },
        modulePaths: [
            { FileName: "/project/first.ts", IsInNodeModules: false, IsRedirect: false },
            { FileName: "/project/second.ts", IsInNodeModules: false, IsRedirect: false },
        ],
    });
    for (const sensitive of [true, false]) {
        add("all-paths", { target: "/project/İ.ts", redirects: ["/project/😀.ts", "/project/𐐀.ts", "/project/\ue000.ts", "/project/K.ts", "/project/Z.ts"], sensitive });
        add("select", { source: "import 'existing';", importTargets: { existing: "/project/İ.ts" }, sensitive, modulePaths: [{ FileName: "/project/i.ts", IsInNodeModules: false, IsRedirect: false }] });
    }
}
if (programHost) {
    cases.length = 0;
    const programCases = [
        { name: "local", roots: ["/project/src/main.ts"], files: { "/project/src/main.ts": "import {x} from './item'; export {x};", "/project/src/item.ts": "export const x=1;" } },
        { name: "paths", roots: ["/project/src/main.ts"], options: { paths: { "@lib/*": ["lib/*"] }, rootDirs: ["src", "generated"] }, files: { "/project/src/main.ts": "import {x} from '@lib/item'; export {x};", "/project/lib/item.ts": "export const x=1;", "/project/package.json": '{"imports":{"#item":"./lib/item.js"}}' } },
        { name: "output-imports", roots: ["/project/src/main.ts", "/project/src/item.ts"], options: { outDir: "lib", declarationDir: "types", rootDir: "src" }, files: { "/project/src/main.ts": "export const main=1;", "/project/src/item.ts": "export const x=1;", "/project/package.json": '{"imports":{"#item":{"types":"./types/item.d.ts","default":"./lib/item.js"}}}' } },
        { name: "dual-mode", roots: ["/project/main.mts", "/project/main.cts"], options: { module: "nodenext" }, files: { "/project/main.mts": "import {x} from 'pkg'; export {x};", "/project/main.cts": "import p=require('pkg'); export {p};", "/project/node_modules/pkg/package.json": '{"name":"pkg","version":"1","exports":{".":{"import":"./index.d.mts","require":"./index.d.cts"}}}', "/project/node_modules/pkg/index.d.mts": "export const x:1;", "/project/node_modules/pkg/index.d.cts": "export const x:2;" } },
        { name: "resolved-symlink", roots: ["/project/main.ts"], fileLinks: { "/project/node_modules/pkg": "/store/pkg" }, files: { "/project/main.ts": "import {x} from 'pkg'; export {x};", "/project/package.json": '{"dependencies":{"pkg":"*"}}', "/store/pkg/package.json": '{"name":"pkg","version":"1","types":"index.d.ts"}', "/store/pkg/index.d.ts": "export {x} from './hidden';", "/store/pkg/hidden.d.ts": "export const x:1;" } },
        { name: "dependency-symlink", roots: ["/project/main.ts", "/store/pkg/hidden.ts"], fileLinks: { "/project/node_modules/pkg": "/store/pkg" }, files: { "/project/main.ts": "export const main=1;", "/project/package.json": '{"dependencies":{"pkg":"*"},"optionalDependencies":{"absent":"*"}}', "/store/pkg/package.json": '{"name":"pkg","version":"1","types":"hidden.ts"}', "/store/pkg/hidden.ts": "export const x=1;" } },
        { name: "dev-symlink", roots: ["/project/main.ts", "/store/pkg/hidden.ts"], fileLinks: { "/project/node_modules/pkg": "/store/pkg" }, files: { "/project/main.ts": "export const main=1;", "/project/package.json": '{"devDependencies":{"pkg":"*"}}', "/store/pkg/package.json": '{"name":"pkg","version":"1","types":"hidden.ts"}', "/store/pkg/hidden.ts": "export const x=1;" } },
        { name: "own-package", roots: ["/store/pkg/main.ts", "/store/pkg/hidden.ts"], fileLinks: { "/store/pkg/node_modules/pkg": "/store/pkg" }, files: { "/store/pkg/main.ts": "export const main=1;", "/store/pkg/package.json": '{"name":"pkg","version":"1","types":"hidden.ts","dependencies":{"pkg":"*"}}', "/store/pkg/hidden.ts": "export const x=1;" } },
        { name: "duplicate-package", roots: ["/project/main.ts", "/other/main.ts"], files: { "/project/main.ts": "import {x} from 'pkg'; export {x};", "/other/main.ts": "import {x} from 'pkg'; export {x};", "/project/node_modules/pkg/package.json": '{"name":"pkg","version":"1","types":"index.d.ts"}', "/other/node_modules/pkg/package.json": '{"name":"pkg","version":"1","types":"index.d.ts"}', "/project/node_modules/pkg/index.d.ts": "export const x:1;", "/other/node_modules/pkg/index.d.ts": "export const x:1;" } },
        { name: "json-js", roots: ["/project/src/main.js"], options: { allowJs: true, resolveJsonModule: true, outDir: "out" }, files: { "/project/src/main.js": "import data from './data.json'; export {data};", "/project/src/data.json": '{"value":1}' } },
        { name: "global-types", roots: ["/project/main.ts"], globalTypingsCache: "/cache", files: { "/project/main.ts": "import {x} from 'pkg'; export {x};", "/cache/node_modules/@types/pkg/index.d.ts": "export const x:1;" } },
    ];
    for (const useSources of [false, true]) {
        programCases.push({
            name: "project-reference-" + useSources,
            useSources,
            config: "/project/tsconfig.json",
            files: {
                "/project/tsconfig.json": JSON.stringify({ compilerOptions: { noLib: true, module: "nodenext" }, files: ["main.mts"], references: [{ path: "../child" }] }),
                "/project/main.mts": "import {x} from '../child/src/item'; export {x};",
                "/child/tsconfig.json": JSON.stringify({ compilerOptions: { noLib: true, composite: true, module: "preserve", rootDir: "src", outDir: "dist" }, files: ["src/item.ts", "src/helper.ts"] }),
                "/child/src/item.ts": "import {y} from './helper'; export const x=y;",
                "/child/src/helper.ts": "export const y=1;",
                "/child/dist/item.d.ts": "import {y} from './helper'; export declare const x:typeof y;",
                "/child/dist/helper.d.ts": "export declare const y:1;",
            },
        });
    }
    for (const fixture of programCases) for (const sensitive of [true, false]) for (const concurrency of [1, 4]) add("program", { ...fixture, sensitive, concurrency, options: { noLib: true, ...fixture.options } });
}
if (printNodes) {
    cases.length = 0;
    const expressions = [
        "'text'",
        '"é中😀"',
        '"\\0"',
        '"\\0' + '1"',
        '"\\uD800"',
        '"quote\\"slash\\\\"',
        "0x10",
        "1_000",
        "0b101",
        "12n",
        "/a+/gi",
        "true",
        "null",
        "this",
        "new.target",
        "import.meta.url",
        "a.b.c",
        "a?.b?.[x]",
        "a[1]",
        "1..toString()",
        "f(a,...rest)",
        "f?.<T>(x)",
        "new C",
        "new C<T>()",
        "new C(x)",
        "tag`raw\\n${a+1}tail`",
        "`plain\\n`",
        "`not \\${a}`",
        "(a + b) * c",
        "a ** b ** c",
        "a ? b : c",
        "(a,b)",
        "x = y + 1",
        "+ +x",
        "- -x",
        "++x",
        "x--",
        "delete a.x",
        "typeof a",
        "void 0",
        "await f()",
        "a!",
        "a as T",
        "a satisfies T",
        "<T>a",
        "[1,,3,...rest]",
        "[]",
        "[1,]",
        "({a:1,b,...rest})",
        "({})",
        "({a:1,})",
        "({get x(){return 1;},set x(v){this.y=v;},async method<T>(x:T){return x;}})",
        "(x=>x)",
        "((x)=>x)",
        "(async x=>x)",
        "(<T>(x:T):T=>x)",
        "(()=>({x:1}))",
        "(function named<T>(x:T):T{return x;})",
        "(function*(){yield 1;yield* rest;})",
        "(class C extends Base implements I {static x=1; #private=2; constructor(public value:number){} method(){return this.value;} static { let x=1; }})",
        "(()=>{let a=1; const {x:y=2,...rest}=obj; const [first,,last]=arr; if(a){return y;}else return last;})()",
        "(()=>{for(let i=0;i<2;i++){continue;}for(const k in obj){}for(const v of arr){}while(x)break;do{x--;}while(x);return 1;})()",
        "(()=>{switch(x){case 1:case 2:return 2;default:return 0;}})()",
        "(()=>{try{throw 1;}catch(e){return e;}finally{debugger;}})()",
        "(()=>{outer:for(;;){break outer;};;return 1;})()",
        "(()=>{namespace A.B {export const x=1;} type T=number; interface I{x:T;} enum E{A=1,B} return A.B.x;})()",
        "(()=>{using a=f();await using b=f();return a;})()",
    ];
    const types = [
        "any",
        "unknown",
        "never",
        "this",
        "typeof x",
        "typeof f<T>",
        '"é" | 1 | null',
        "string[]",
        "(A|B)[]",
        "A & B",
        "[first:number,second?:string,...rest:boolean[]]",
        "readonly [1,2]",
        "{readonly x?:number; method<T>(x:T):T; (x:number):string; new(x:number):C; [key:string]:unknown;}",
        "<T extends object=Record<string,unknown>>(x:T)=>keyof T",
        "abstract new<T>(x:T)=>C<T>",
        "T extends infer U extends string ? U : never",
        "keyof T[K]",
        "unique symbol",
        "{+readonly [K in keyof T as `get${K & string}`]-?:T[K]}",
        "import('pkg').T<A>",
        "typeof import('pkg', {with:{type:'json'}}).value",
        "`${T}-${K}`",
        "(x:unknown)=>asserts x is string",
    ];
    for (const source of expressions) for (const neverAsciiEscape of [false, true]) add("print", { source: `class C { [${source}]: unknown; }`, printMode: "computed", neverAsciiEscape });
    for (const type of types) for (const neverAsciiEscape of [false, true]) add("print", { source: `type T = ${type};`, printMode: "types", neverAsciiEscape });
    for (const expression of ['<div title="é &amp; text" value={x} {...props}/>', "<><span>hello</span>{x}</>", '<ns:tag ns:value="x"/>']) for (const neverAsciiEscape of [false, true]) add("print", { source: `class C {[${expression}]:unknown;}`, fileName: "/project/main.tsx", printMode: "computed", neverAsciiEscape });
    for (const source of ["import type A, {type B as C,D} from 'pkg' with {type:'json'}; export type {C};", "import X=require('pkg'); export=X;", "export * as ns from 'pkg'; export {X as Y} from 'other';", "declare global {interface I{}} namespace A.B {export const x=1;}"]) add("print", { source, printMode: "source", neverAsciiEscape: true });
    cases.push(...cases.map(input => ({ ...input, withoutSource: true })));
    for (const neverAsciiEscape of [false, true]) {
        for (const withoutSource of [false, true]) {
            add("print", { source: "class C {[\\u0061]:unknown;[(x => x)]:unknown;}", printMode: "computed", neverAsciiEscape, withoutSource });
            add("print", { source: "type T=[];", printMode: "types", neverAsciiEscape, withoutSource });
        }
    }
    for (const source of ["", "'use strict'; let x=1;", "#!/usr/bin/env node\nlet x=1;"]) add("print", { source, printMode: "source", neverAsciiEscape: true });
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
