import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import {
    readdir,
    readFile,
} from "node:fs/promises";
import path from "node:path";
import {
    json,
    npmCommand,
    output,
    root,
    sha256,
} from "./common.mjs";

export const dotnet = process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, process.platform === "win32" ? "dotnet.exe" : "dotnet") : "dotnet";
export function execute(command, args, options = {}) {
    return new Promise((resolve, reject) => {
        const child = spawn(command, args, { cwd: root, windowsHide: true, stdio: "inherit", ...options });
        child.once("error", reject);
        child.once("close", code => code === 0 ? resolve() : reject(new Error(`${command} exited ${code}`)));
    });
}
export const node = (script, args = [], options) => execute(process.execPath, [script, ...args], options);
export const npm = (args, options) => execute(...npmCommand(args), options);

export async function buildCompiler({ debug = false, signal } = {}) {
    await execute(dotnet, ["publish", "csharp/src/TypeScript.CommandLine/TypeScript.CommandLine.csproj", "-c", debug ? "Debug" : "Release", "--self-contained", "false", "-p:PublishAot=false", "-p:TypeScriptExecutableName=tsc", "-p:EmbedTypeScriptLibraries=false", "-p:UseSharedCompilation=false", "--artifacts-path", "built/csharp/product", "-o", "built/local"], { signal });
    await json(path.join(root, "built/local/backend.json"), { backend: "csharp", runtime: "coreclr", astProtocolVersion: 9, positionEncoding: "utf-8", configuration: debug ? "Debug" : "Release" });
}

export const buildClient = () => node("node_modules/typescript/lib/tsc.js", ["-b", "packages/typescript/tsconfig.json"]);

export async function buildTests() {
    await execute(dotnet, ["build", "csharp/TypeScript.slnx", "-c", "Release", "--artifacts-path", "built/csharp/build", "-p:UseSharedCompilation=false"]);
}

export async function testCompiler(filter = ".*") {
    const source = await readFile(path.join(root, "csharp/tests/TypeScript.Compatibility/Program.cs"), "utf8");
    const tests = [...new Set([...source.matchAll(/args\.Length == 1 && args\[0\] == "(--[^"]+-safety)"u8/g)].map(match => match[1]))];
    tests.push(...[...source.matchAll(/args is \[var \w+\] && \w+ == "(--[^"]+-safety)"u8/g)].map(match => match[1]));
    const selected = [...new Set(tests)].filter(test => new RegExp(filter).test(test));
    assert.ok(selected.length, "No C# tests matched the filter");
    const harness = path.join(output, "build/bin/TypeScript.Compatibility/release/TypeScript.Compatibility.dll");
    for (const test of selected) await execute(dotnet, [harness, test]);
    if (filter === ".*") await execute(dotnet, [harness, "--content-mappers", root]);
    await json(path.join(output, "product-tests.json"), { runtime: "Release CoreCLR", nativeExecuted: false, tests: selected, compilerSha256: sha256(await readFile(path.join(path.dirname(harness), "TypeScript.Compiler.dll"))) });
}

export async function testClient() {
    await node("packages/typescript/scripts/generateSync.ts");
    await node("node_modules/typescript/lib/tsc.js", ["-b", "packages/typescript/test"]);
    await execute(process.execPath, ["--conditions", "@typescript/source", "--test", "./test/**/*.test.ts"], { cwd: path.join(root, "packages/typescript") });
}

export async function generate() {
    for (
        const name of (await readdir(path.join(root, "csharp/tools"))).filter(name =>
            /^generate-.*\.mjs$/.test(name)
            && !["generate-client.mjs", "generate-client-enums.mjs"].includes(name)
        ).sort()
    ) await node("csharp/tools/" + name);
    await node("csharp/tools/generate-client-enums.mjs");
    await node("tools/scripts/tsc/generate.ts");
    await node("packages/typescript/scripts/generateSync.ts");
}

export async function watchCompiler(debug = false) {
    const { watch } = await import("chokidar");
    let pending = true, building = false;
    async function build() {
        if (building) return;
        building = true;
        while (pending) {
            pending = false;
            console.log("[build:watch] changed due to source update");
            try {
                await buildCompiler({ debug });
            }
            catch (error) {
                console.error(error);
            }
            console.log("[build:watch] run complete");
        }
        building = false;
    }
    const watcher = watch(["csharp/src", "csharp/Directory.Build.props", "tsc/internal/bundled/libs", "tsc/internal/diagnostics/loc"], { ignoreInitial: true, ignored: /[\\/](?:bin|obj)[\\/]/, awaitWriteFinish: { stabilityThreshold: 200 } });
    watcher.on("all", () => {
        pending = true;
        void build();
    });
    await build();
    await new Promise(resolve => {
        process.once("SIGINT", resolve);
        process.once("SIGTERM", resolve);
    });
    await watcher.close();
}

export async function checkApiProtocol() {
    const declaration = await readFile(path.join(root, "packages/typescript/src/api/protocol.types.ts"), "utf8");
    assert.match(declaration, /export interface APIMethodInfo/);
    assert.ok(!declaration.includes("UTF-16 code-unit offset"));
    await buildClient();
}
