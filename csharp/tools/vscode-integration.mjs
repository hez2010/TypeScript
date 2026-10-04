import { build } from "esbuild";
import assert from "node:assert/strict";
import {
    mkdir,
    readFile,
    rename,
    stat,
} from "node:fs/promises";
import Module, { createRequire } from "node:module";
import path from "node:path";
import { pathToFileURL } from "node:url";
import {
    json,
    output,
    root,
    sha256,
} from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const directory = path.resolve(option("--directory", path.join(output, "phase8-validation/vscode")));
const packageResults = JSON.parse(await readFile(option("--package-summary", path.join(output, "phase8-validation/package/summary.json"))));
assert.equal(packageResults.runtime, "coreclr-validation", "The current NativeAOT policy allows publishing only");
await mkdir(directory, { recursive: true });
const require = createRequire(import.meta.url);
// Supply only editor filesystem/URI services. Resolver and process/JSON-RPC transport code are the actual installed implementations.
const uri = file => ({
    fsPath: path.resolve(file),
    scheme: "file",
    toString() {
        return pathToFileURL(this.fsPath).href;
    },
});
const shim = new Proxy({ version: "1.125.0", Uri: { file: uri, joinPath: (base, ...parts) => uri(path.join(base.fsPath, ...parts)) }, workspace: { fs: { readFile: file => readFile(file.fsPath), stat: file => stat(file.fsPath) }, workspaceFolders: [] } }, { get: (target, key) => key in target ? target[key] : class {} });
const load = Module._load;
Module._load = function (id, ...args) {
    return id === "vscode" ? shim : load.call(this, id, ...args);
};
let resolver, LanguageClient, TransportKind;
try {
    const file = path.join(directory, "resolver.cjs");
    await build({ entryPoints: [path.join(root, "packages/vscode-typescript/src/util.ts")], bundle: true, platform: "node", format: "cjs", external: ["vscode"], outfile: file });
    resolver = require(file);
    ({ LanguageClient, TransportKind } = require("vscode-languageclient/node"));
}
finally {
    Module._load = load;
}
const installed = packageResults.installedDirectory, main = path.join(installed, "node_modules", packageResults.packageName ?? "@typescript/csharp-preview");
const exe = path.join(installed, "node_modules", packageResults.platformName ?? "@typescript/csharp-preview-win32-x64", "lib", (packageResults.executableName ?? "tsgo") + (process.platform === "win32" ? ".exe" : ""));
const resolutions = [];
for (const packagePath of [main, path.join(main, "lib"), ...packageResults.aliases.map(alias => path.join(installed, alias))]) {
    const resolved = await resolver.resolveTsdkPathToExe(packagePath);
    assert.equal(path.normalize(resolved?.path), path.normalize(exe));
    assert.equal(resolved.version, packageResults.version);
    resolutions.push(packagePath);
}
await rename(exe, exe + ".missing");
try {
    assert.equal(await resolver.resolveTsdkPathToExe(main), undefined);
}
finally {
    await rename(exe + ".missing", exe);
}
const clientSource = await readFile(path.join(root, "packages/vscode-typescript/src/client.ts"), "utf8");
assert.equal(clientSource.split('args: ["--lsp", ...pprofArgs]').length - 1, 2, "Extension run/debug argument contract changed");
assert.equal(clientSource.split("transport: TransportKind.stdio").length - 1, 2);
const protocol = require("vscode-languageserver-protocol/node"), sessions = [];
for (const mode of ["run", "debug"]) {
    const diagnostics = [], profile = path.join(directory, `profile-${mode}`);
    const launch = { command: exe, args: ["--lsp", "--pprofDir", profile], transport: TransportKind.stdio, options: { cwd: installed, windowsHide: true, env: { ...process.env, DOTNET_ROOT: option("--dotnet-root", process.env.DOTNET_ROOT) } } };
    const host = { _serverOptions: { run: launch, debug: launch }, _forceDebug: mode === "debug", _stdioOptions: { stdout: () => {}, stderr: stream => stream.on("data", value => diagnostics.push(value.toString())) }, _getServerWorkingDir: async () => installed, info: () => {}, error: () => {} };
    const transports = await LanguageClient.prototype.createMessageTransports.call(host, "utf8");
    const child = host._serverProcess;
    const finished = new Promise((resolve, reject) => {
        child.once("error", reject);
        child.once("close", (code, signal) => resolve({ code, signal }));
    });
    const timeout = setTimeout(() => child.kill(), 30000);
    const connection = protocol.createMessageConnection(transports.reader, transports.writer);
    connection.onRequest("workspace/configuration", () => [null, null, null, null]);
    connection.onRequest("client/registerCapability", () => null);
    connection.onRequest("window/workDoneProgress/create", () => null);
    connection.onRequest("workspace/diagnostic/refresh", () => null);
    connection.listen();
    try {
        assert.deepEqual(child.spawnargs.slice(1), ["--lsp", "--pprofDir", profile, "--stdio"]);
        const initialized = await connection.sendRequest("initialize", { processId: process.pid, rootUri: pathToFileURL(installed).href, capabilities: { general: { positionEncodings: ["utf-16"] }, textDocument: { diagnostic: {} } } });
        assert.equal(initialized.capabilities.positionEncoding, "utf-16");
        await connection.sendNotification("initialized", {});
        const file = pathToFileURL(path.join(installed, "vscode.ts")).href;
        await connection.sendNotification("textDocument/didOpen", { textDocument: { uri: file, languageId: "typescript", version: 1, text: 'const café: number = "世界 😀";\n' } });
        const first = await connection.sendRequest("textDocument/diagnostic", { textDocument: { uri: file } });
        assert.ok(first.items.some(item => item.code === 2322));
        const hover = await connection.sendRequest("textDocument/hover", { textDocument: { uri: file }, position: { line: 0, character: 8 } });
        assert.match(JSON.stringify(hover), /café/);
        await connection.sendNotification("textDocument/didChange", { textDocument: { uri: file, version: 2 }, contentChanges: [{ text: "const café: number = 42;\n" }] });
        const second = await connection.sendRequest("textDocument/diagnostic", { textDocument: { uri: file } });
        assert.ok(!second.items.some(item => item.code === 2322));
        await connection.sendRequest("shutdown");
        await connection.sendNotification("exit");
        const result = await finished;
        assert.equal(result.code, 0);
        sessions.push({ mode, args: child.spawnargs.slice(1), initialTypeError: true, hover: true, editClearsError: true, ...result });
    }
    finally {
        clearTimeout(timeout);
        connection.dispose();
        if (child.exitCode === null) child.kill();
        await finished;
    }
}
const summary = { packageManifest: packageResults.packageManifest, resolutions: resolutions.length, missingExecutableRejected: true, sessions, compilerSha256: sha256(await readFile(path.join(path.dirname(exe), "TypeScript.Compiler.dll"))), languageClientVersion: JSON.parse(await readFile(path.resolve(path.dirname(require.resolve("vscode-languageclient/node")), "../../package.json"))).version, nativeExecuted: false, boundary: "Actual extension SDK resolver and vscode-languageclient executable transport with filesystem/URI editor shim; no VS Code UI or extension-host activation claim" };
await json(path.join(directory, "summary.json"), summary);
console.log(JSON.stringify(summary));
