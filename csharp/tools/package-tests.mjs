import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { copyFile, cp, mkdir, mkdtemp, readFile, rename, writeFile } from "node:fs/promises";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { json, output, root, run, sha256 } from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const manifestPath = path.resolve(option("--manifest", path.join(output, "distribution/coreclr-validation/manifest.json")));
const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
assert.equal(manifest.runtime, "coreclr-validation", "NativeAOT artifacts are publish-only under the current validation policy");
const directory = path.resolve(option("--directory", path.join(output, "phase8-validation/package")));
await mkdir(directory, { recursive: true });
const installed = await mkdtemp(path.join(directory, "installed-"));
await json(path.join(installed, "package.json"), { private: true, type: "module" });
const npm = process.env.npm_execpath ?? path.join(path.dirname(process.execPath), "node_modules/npm/bin/npm-cli.js");
const install = await run(process.execPath, [npm, "install", ...manifest.packages.map(item => item.path), "--offline", "--ignore-scripts", "--no-audit", "--no-fund", "--package-lock=false"], { cwd: installed });
await writeFile(path.join(directory, "npm-install.log"), install + "\n");
const main = path.join(installed, "node_modules/@typescript/csharp-preview"), platform = path.join(installed, "node_modules/@typescript/csharp-preview-win32-x64");
const bin = path.join(main, "bin/tsgo"), executable = path.join(platform, "lib/tsgo.exe");
const env = { ...process.env, DOTNET_ROOT: option("--dotnet-root", process.env.DOTNET_ROOT ?? "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64") };
const version = await run(process.execPath, [bin, "--version"], { cwd: installed, env });
assert.equal(version, `Version ${manifest.version}`);
for (const name of ["LICENSE", "NOTICE.txt", "NOTICE-Go.txt", "licenses/LICENSE-DotNet.txt", "licenses/NOTICE-System.IO.Hashing.txt"])
    assert.ok((await readFile(path.join(platform, name))).length > 0, name);
for (const [name, expected] of Object.entries(manifest.payload)) assert.equal(sha256(await readFile(path.join(platform, "lib", name))), expected, name);
const source = path.join(installed, "index.ts"), text = "export const café: string = '世界 😀';\n";
await writeFile(source, text);
const listed = await run(process.execPath, [bin, "--noEmit", "--listFiles", source], { cwd: installed, env });
assert.ok(!listed.includes("bundled:///"));
assert.ok(listed.replaceAll("\\", "/").toLowerCase().includes(path.join(platform, "lib/lib.es5.d.ts").replaceAll("\\", "/").toLowerCase()));
await json(path.join(installed, "tsconfig.json"), { compilerOptions: { composite: true, declaration: true, target: "esnext", module: "nodenext", outDir: "out" }, files: ["index.ts"] });
await run(process.execPath, [bin, "--build", "tsconfig.json"], { cwd: installed, env });
const buildInfo = JSON.parse(await readFile(path.join(installed, "out/tsconfig.tsbuildinfo"), "utf8"));
assert.equal(buildInfo.version, manifest.version);
const emitted = await import(pathToFileURL(path.join(installed, "out/index.js")).href); assert.equal(emitted.café, "世界 😀");
const outputBefore = sha256(await readFile(path.join(installed, "out/index.js")));
await run(process.execPath, [bin, "--build", "tsconfig.json"], { cwd: installed, env });
assert.equal(sha256(await readFile(path.join(installed, "out/index.js"))), outputBefore);
await writeFile(path.join(installed, "error.ts"), "const 値: number = '世界';\n");
const localized = spawnSync(process.execPath, [bin, "--ignoreConfig", "--noEmit", "--locale", "ja", path.join(installed, "error.ts")], { cwd: installed, windowsHide: true, env, encoding: "utf8" });
assert.equal(localized.status, 2, localized.stderr); assert.match(localized.stdout, /TS2322/); assert.match(localized.stdout, /型/);
await writeFile(path.join(directory, "localized-diagnostic.txt"), localized.stdout);

const probe = path.join(installed, "api-probe.mjs");
await writeFile(probe, `import assert from "node:assert/strict";
import { API as AsyncAPI } from "@typescript/csharp-preview/unstable/async";
import { API as SyncAPI } from "@typescript/csharp-preview/unstable/sync";
import { utf8Offset } from "@typescript/csharp-preview/unstable/text";
import { getTokenAtPosition } from "@typescript/csharp-preview/unstable/ast";
for (const API of [AsyncAPI, SyncAPI]) {
    const api = new API({ cwd: process.cwd() });
    try {
        const snapshot = await api.createSnapshot({ openProject: "tsconfig.json" });
        const project = snapshot.getProjects().find(project => project.program);
        const file = await project.program.getSourceFile("index.ts");
        assert.ok(file); assert.equal(file.statements[0].getText(), "export const café: string = '世界 😀';");
        const position = utf8Offset(file.text, file.text.indexOf("café"));
        assert.equal(getTokenAtPosition(file, position).getText(), "café");
        const diagnostics = await project.program.getSemanticDiagnostics(); assert.equal(diagnostics.length, 0);
        assert.ok((await api.printer.printFile(file, { neverAsciiEscape: true })).includes("世界"));
    } finally { await api.close(); }
}
console.log("Installed async and sync API clients passed");
`);
const api = await run(process.execPath, [probe], { cwd: installed, env });
await writeFile(path.join(directory, "api.log"), api + "\n");

const resolver = path.join(main, "lib/getExePath.js");
function resolve() {
    return spawnSync(process.execPath, ["--input-type=module", "-e", `import get from ${JSON.stringify(pathToFileURL(resolver).href)}; console.log(get());`],
        { cwd: installed, windowsHide: true, env, encoding: "utf8" });
}
assert.equal(resolve().status, 0);
const metadataPath = path.join(platform, "package.json"), originalMetadata = await readFile(metadataPath, "utf8"), metadata = JSON.parse(originalMetadata);
try {
    await json(metadataPath, { ...metadata, version: "0.0.0" });
    const mismatch = resolve(); assert.notEqual(mismatch.status, 0); assert.match(mismatch.stderr, /Incompatible C# platform package/);
    await json(metadataPath, { ...metadata, typescriptBackend: { ...metadata.typescriptBackend, runtime: "nativeaot" } });
    const wrongRuntime = resolve(); assert.notEqual(wrongRuntime.status, 0); assert.match(wrongRuntime.stderr, /Incompatible C# platform package/);
} finally { await writeFile(metadataPath, originalMetadata); }
await rename(executable, executable + ".missing");
try { const missing = resolve(); assert.notEqual(missing.status, 0); assert.match(missing.stderr, /Executable not found/); }
finally { await rename(executable + ".missing", executable); }

const aliases = [];
for (const relative of ["node_modules/typescript-csharp", "node_modules/@typescript/native-csharp", "node_modules/.pnpm/csharp-preview/node_modules/@typescript/csharp-preview"]) {
    const destination = path.join(installed, relative); await mkdir(path.dirname(destination), { recursive: true });
    await cp(main, destination, { recursive: true });
    const output = await run(process.execPath, [path.join(destination, "bin/tsgo"), "--version"], { cwd: installed, env });
    assert.equal(output, version); aliases.push(relative);
}
const summary = { runtime: manifest.runtime, version: manifest.version, packageManifest: manifestPath,
    packageHashes: manifest.packages.map(item => ({ filename: item.filename, sha256: item.sha256 })),
    installedDirectory: installed, aliases, apiModes: ["async", "sync"], versionMatches: true, externalLibraries: true,
    emittedProgramExecuted: true, incrementalVersionMatches: true, localizedDiagnostics: true,
    missingExecutableRejected: true, mixedVersionsRejected: true, mixedRuntimesRejected: true, nativeExecuted: false };
await json(path.join(directory, "summary.json"), summary); console.log(JSON.stringify(summary));
