import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, cp, mkdir, mkdtemp, readFile, readdir, stat, writeFile } from "node:fs/promises";
import path from "node:path";
import { parseArgs } from "node:util";
import AdmZip from "adm-zip";
import { json, output, root, run, sha256 } from "./common.mjs";
import { generateClient } from "./generate-client.mjs";

const { values } = parseArgs({ options: {
    directory: { type: "string" }, dotnet: { type: "string" }, version: { type: "string" },
    managed: { type: "boolean", default: false }, "require-signed": { type: "boolean", default: false },
    "client-directory": { type: "string" }, "instruction-set": { type: "string", default: "native" },
    certificate: { type: "string" }, "timestamp-url": { type: "string" }, powershell: { type: "string", default: "pwsh" },
} });
assert.equal(process.platform + "-" + process.arch, "win32-x64", "The phase-8 distribution validation currently covers Windows x64; other targets remain recorded release blockers.");
const runtime = values.managed ? "coreclr-validation" : "nativeaot";
const directory = path.resolve(values.directory ?? path.join(output, "distribution", runtime));
await mkdir(directory, { recursive: true });
const stage = await mkdtemp(path.join(directory, "stage-"));
const version = values.version ?? /^var version = "([^"]+)"/m.exec(await readFile(path.join(root, "tsc/internal/core/version.go"), "utf8"))[1] + ".csharp";
assert.match(version, /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/, "Package/compiler version must be a SemVer value");
const dotnet = values.dotnet ?? (process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, "dotnet.exe") : "dotnet");
async function sourceProvenance() {
    const files = (await run("git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z", "--",
        "csharp/src", "csharp/client", "csharp/distribution", "csharp/tools/package.mjs", "csharp/tools/generate-client.mjs",
        "csharp/Directory.Build.props", "csharp/NuGet.Config", "packages/typescript", "tools/scripts/tsc", "tsc/internal/bundled",
        "package.json", "package-lock.json", "LICENSE.txt", "NOTICE.txt", "csharp/NOTICE.txt"])).split("\0").filter(Boolean).sort();
    const hashes = Object.fromEntries(await Promise.all(files.map(async file => [file, sha256(await readFile(path.join(root, file)))])));
    return { commit: (await run("git", ["rev-parse", "HEAD"])).trim(), dirty: !!(await run("git", ["status", "--porcelain"])),
        sha256: sha256(JSON.stringify(hashes)), files: hashes };
}
const provenance = await sourceProvenance();
const sdk = await run(dotnet, ["--version"]);
assert.match(sdk, /^11\./, "A .NET 11 SDK with C# 15 and runtime-async support is required");
const compilerDirectory = path.join(stage, "publish");
const publishArgs = ["publish", "csharp/src/TypeScript.CommandLine/TypeScript.CommandLine.csproj", "-c", "Release",
    "--artifacts-path", path.join(stage, "artifacts"), "-o", compilerDirectory, "-p:UseSharedCompilation=false", "-p:RestoreLockedMode=true",
    "-p:EmbedTypeScriptLibraries=false", "-p:TypeScriptExecutableName=tsgo", `-p:TypeScriptVersion=${version}`, `-p:Version=${version}`,
    ...(values.managed ? ["--self-contained", "false", "-p:PublishAot=false"] : ["-r", "win-x64", "-p:PublishAot=true", `-p:IlcInstructionSet=${values["instruction-set"]}`])];
const publishLog = await new Promise((resolve, reject) => {
    const child = spawn(dotnet, publishArgs, { cwd: root, windowsHide: true });
    const output = []; child.stdin.end();
    child.stdout.on("data", data => output.push(data)); child.stderr.on("data", data => output.push(data));
    child.on("error", reject);
    child.on("close", async code => {
        const log = Buffer.concat(output).toString("utf8");
        try { await writeFile(path.join(stage, "publish.log"), log); }
        catch (error) { reject(error); return; }
        if (code !== 0) reject(new Error(`Publish failed (${code}): ${log}`)); else resolve(log);
    });
});
const executable = path.join(compilerDirectory, "tsgo.exe");
await stat(executable);

// A release must opt into signature verification. Local preview packages retain an explicit unsigned status.
if (values.certificate) {
    assert.match(values.certificate, /^[0-9a-f]{40}$/i, "Signing certificate must be a CurrentUser/My certificate thumbprint");
    if (values["timestamp-url"]) assert.ok(["http:", "https:"].includes(new URL(values["timestamp-url"]).protocol));
    await run(values.powershell, ["-NoProfile", "-NonInteractive", "-Command",
        "$ErrorActionPreference = 'Stop'; $certificate = Get-Item -LiteralPath ('Cert:\\CurrentUser\\My\\' + $env:CSHARP_SIGNING_THUMBPRINT); $options = @{ LiteralPath = $env:CSHARP_SIGNED_EXECUTABLE; Certificate = $certificate; HashAlgorithm = 'SHA256' }; if ($env:CSHARP_SIGNING_TIMESTAMP) { $options.TimestampServer = $env:CSHARP_SIGNING_TIMESTAMP }; $result = Set-AuthenticodeSignature @options; if ($result.Status -ne 'Valid') { throw ('Authenticode signing failed: ' + $result.Status) }"],
        { env: { ...process.env, CSHARP_SIGNED_EXECUTABLE: executable, CSHARP_SIGNING_THUMBPRINT: values.certificate, CSHARP_SIGNING_TIMESTAMP: values["timestamp-url"] ?? "" } });
}
const signature = JSON.parse(await run(values.powershell, ["-NoProfile", "-NonInteractive", "-Command",
    "$signature = Get-AuthenticodeSignature -LiteralPath $env:CSHARP_SIGNED_EXECUTABLE; [pscustomobject]@{ status = $signature.Status.ToString(); thumbprint = $signature.SignerCertificate.Thumbprint } | ConvertTo-Json -Compress"],
    { env: { ...process.env, CSHARP_SIGNED_EXECUTABLE: executable } }));
if (values["require-signed"]) assert.equal(signature.status, "Valid", "A trusted Authenticode signature is required before packing this distribution");

const sourceClient = values["client-directory"] ? path.resolve(values["client-directory"]) : await generateClient(path.join(stage, "client-source"));
const clientProtocol = await readFile(path.join(sourceClient, "dist/api/node/protocol.js"), "utf8");
assert.match(clientProtocol, /PROTOCOL_VERSION = 9;/, "The distribution requires the generated byte-coordinate client");
const npmDirectory = path.join(stage, "npm"), packageBase = "csharp-preview", packageName = `@typescript/${packageBase}`;
const platformBase = `${packageBase}-win32-x64`, platformName = `@typescript/${platformBase}`;
const mainDirectory = path.join(npmDirectory, packageBase), platformDirectory = path.join(npmDirectory, platformBase);
await mkdir(path.join(mainDirectory, "bin"), { recursive: true });
await mkdir(path.join(platformDirectory, "lib"), { recursive: true });
const sourcePackage = JSON.parse(await readFile(path.join(sourceClient, "package.json"), "utf8"));
function stripConditions(value) {
    if (!value || typeof value !== "object") return value;
    const result = Object.fromEntries(Object.entries(value).filter(([key]) => key !== "@typescript/source").map(([key, item]) => [key, stripConditions(item)]));
    return Object.keys(result).length === 1 && "default" in result ? result.default : result;
}
const backend = { kind: "csharp", runtime, rid: "win-x64", positionEncoding: "utf-8", astProtocolVersion: 9 };
const mainPackage = { ...sourcePackage, name: packageName, version, private: undefined, scripts: undefined, devDependencies: undefined,
    engines: { node: ">=22.18" },
    description: "Opt-in C# TypeScript compiler, language server and UTF-8 API preview", bin: { tsgo: "./bin/tsgo" },
    files: ["bin", "lib", "dist", "vendor", "licenses", "NOTICE.txt", "NOTICE-Go.txt"],
    exports: { ...stripConditions(sourcePackage.exports), "./unstable/text": "./dist/ast/positions.js" },
    imports: stripConditions(sourcePackage.imports), optionalDependencies: { [platformName]: version },
    publishConfig: { access: "public", tag: "csharp" }, typescriptBackend: backend,
    gitHead: provenance.commit,
};
await json(path.join(mainDirectory, "package.json"), mainPackage);
for (const name of ["lib", "dist", "vendor"]) await cp(path.join(sourceClient, name), path.join(mainDirectory, name), { recursive: true });
const resolverPath = path.join(mainDirectory, "lib/getExePath.js");
const resolver = await readFile(resolverPath, "utf8");
const resolverMarker = "    let exe = path.join(exeDir, binName);";
assert.equal(resolver.split(resolverMarker).length - 1, 1);
await writeFile(resolverPath, resolver.replace(resolverMarker,
    '    const platform = JSON.parse(fs.readFileSync(path.join(exeDir, "..", "package.json"), "utf8"));\n' +
    '    if (platform.version !== pkg.version || platform.typescriptBackend?.kind !== "csharp"\n' +
    '        || platform.typescriptBackend.astProtocolVersion !== 9 || platform.typescriptBackend.runtime !== pkg.typescriptBackend.runtime) {\n' +
    '        throw new Error("Incompatible C# platform package: " + platform.name + " " + platform.version);\n    }\n\n' + resolverMarker));
await writeFile(path.join(mainDirectory, "lib/version.cjs"), `const { version } = require("../package.json");\nexports.version = version;\nexports.versionMajorMinor = ${JSON.stringify(version.split(".").slice(0, 2).join("."))};\n`);
await writeFile(path.join(mainDirectory, "bin/tsgo"), '#!/usr/bin/env node\nimport "../lib/tsc.js";\n');
const platformPackage = { name: platformName, version, description: "Windows x64 executable for the C# TypeScript preview", license: sourcePackage.license,
    os: ["win32"], cpu: ["x64"], files: ["lib", "licenses", "NOTICE.txt", "NOTICE-Go.txt"],
    exports: { "./package.json": "./package.json" }, publishConfig: mainPackage.publishConfig, typescriptBackend: backend };
await json(path.join(platformDirectory, "package.json"), platformPackage);
for (const name of await readdir(compilerDirectory)) {
    if (name.endsWith(".pdb") || name === "NOTICE-Go.txt") continue;
    const file = path.join(compilerDirectory, name); if (!(await stat(file)).isFile()) continue;
    await copyFile(file, path.join(platformDirectory, "lib", name));
}
for (const destination of [mainDirectory, platformDirectory]) {
    await copyFile(path.join(root, "LICENSE.txt"), path.join(destination, "LICENSE"));
    await copyFile(path.join(root, "NOTICE.txt"), path.join(destination, "NOTICE.txt"));
    await copyFile(path.join(root, "csharp/NOTICE.txt"), path.join(destination, "NOTICE-Go.txt"));
}
const assets = JSON.parse(await readFile(path.join(stage, "artifacts/obj/TypeScript.CommandLine/project.assets.json"), "utf8"));
const packageFolders = Object.keys(assets.packageFolders);
async function packageFile(relative) {
    for (const folder of packageFolders) {
        const file = path.join(folder, relative);
        try { await stat(file); return file; } catch (error) { if (error.code !== "ENOENT") throw error; }
    }
    throw new Error(`Restored package notice is missing: ${relative}`);
}
const hashing = Object.entries(assets.libraries).find(([name]) => name.startsWith("System.IO.Hashing/")); assert.ok(hashing);
const licenses = [{ name: "LICENSE-DotNet.txt", path: path.join(root, "csharp/distribution/LICENSE-DotNet.txt") },
    { name: "NOTICE-System.IO.Hashing.txt", path: await packageFile(path.join(hashing[1].path, "THIRD-PARTY-NOTICES.TXT")) }];
if (!values.managed) {
    const runtimePack = assets.project.frameworks.net11_0 ?? assets.project.frameworks["net11.0"];
    const pack = runtimePack.downloadDependencies.find(item => item.name === "Microsoft.NETCore.App.Runtime.NativeAOT.win-x64"); assert.ok(pack);
    const packVersion = /^\[([^,]+)/.exec(pack.version)[1];
    licenses.push({ name: "NOTICE-NativeAOT.txt", path: await packageFile(path.join(pack.name.toLowerCase(), packVersion, "THIRD-PARTY-NOTICES.TXT")) });
}
for (const destination of [mainDirectory, platformDirectory]) {
    await mkdir(path.join(destination, "licenses"), { recursive: true });
    for (const notice of licenses) await copyFile(notice.path, path.join(destination, "licenses", notice.name));
}
await writeFile(path.join(mainDirectory, "README.md"), `# C# TypeScript preview\n\nThis package opts into the C# backend; it does not replace the default Go release.\n\nRun \`tsgo\` for compilation or \`tsgo --lsp --stdio\` for LSP. Point the VS Code TypeScript extension's SDK setting at this package directory to select it.\n\nThe unstable API and AST/scanner positions are UTF-8/WTF-8 byte offsets (protocol 9). Import \`utf8Offset\`, \`utf16Offset\` and \`byteLength\` from \`${packageName}/unstable/text\` when crossing JavaScript string indices. Lone surrogates are preserved.\n\nRuntime: ${runtime}. ${values.managed ? "This validation package requires .NET 11 and is not a NativeAOT release artifact." : "The compiler is a NativeAOT executable and requires no installed .NET runtime."}\n\nValidated target: Windows x64. Other original targets remain release blockers. Native instruction set: ${values.managed ? "CoreCLR runtime dispatch" : values["instruction-set"]}. Authenticode status: ${signature.status}.\n`);
await writeFile(path.join(platformDirectory, "README.md"), `# ${platformName}\n\nExecutable and external standard libraries for ${packageName} ${version}. Runtime: ${runtime}. Authenticode status: ${signature.status}.\n`);
const payload = {};
for (const name of await readdir(path.join(platformDirectory, "lib"))) payload[name] = sha256(await readFile(path.join(platformDirectory, "lib", name)));
assert.ok(payload["lib.d.ts"] && payload["lib.es5.d.ts"], "External libraries must be included");
assert.equal("tsgo.dll" in payload, values.managed, "NativeAOT payload must not contain a managed compiler fallback");
assert.ok(values.managed || !Object.keys(payload).some(name => /\.(?:dll|deps\.json|runtimeconfig\.json)$/.test(name)), "NativeAOT payload contains runtime-dependent files");

const tarballs = path.join(directory, "npm"); await mkdir(tarballs, { recursive: true });
const npm = process.env.npm_execpath ?? path.join(path.dirname(process.execPath), "node_modules/npm/bin/npm-cli.js");
async function npmPack(packageDirectory) {
    const result = await run(process.execPath, [npm, "pack", packageDirectory, "--json", "--ignore-scripts", "--pack-destination", tarballs],
        { env: { ...process.env, COREPACK_ENABLE_STRICT: "0" } });
    return JSON.parse(result)[0];
}
const packages = [await npmPack(platformDirectory), await npmPack(mainDirectory)];
const archive = new AdmZip(); archive.addLocalFolder(platformDirectory);
const zipPath = path.join(directory, `typescript-csharp-${version}-win-x64.zip`); archive.writeZip(zipPath);
assert.deepEqual(await sourceProvenance(), provenance, "Distribution inputs changed while publishing");
await json(path.join(stage, "source-provenance.json"), provenance);
const manifest = {
    version, packageName, runtime, rid: "win-x64", sdk: sdk.trim(), nativeExecuted: false, signature,
    instructionSet: values.managed ? "runtime default" : values["instruction-set"],
    sourceCommit: mainPackage.gitHead, sourceDirty: provenance.dirty, sourceSha256: provenance.sha256,
    sourceProvenance: path.join(stage, "source-provenance.json"), sourceClient, mainDirectory, platformDirectory, payload,
    licenses: await Promise.all(licenses.map(async notice => ({ ...notice, sha256: sha256(await readFile(notice.path)) }))),
    publish: { command: [dotnet, ...publishArgs], log: path.join(stage, "publish.log"), sha256: sha256(publishLog) },
    packages: await Promise.all(packages.map(async info => ({ ...info, path: path.join(tarballs, info.filename), sha256: sha256(await readFile(path.join(tarballs, info.filename))) }))),
    zip: { path: zipPath, sha256: sha256(await readFile(zipPath)) },
    originalTargets: JSON.parse(await readFile(path.join(root, "csharp/compatibility/contracts.generated.json"), "utf8")).platforms,
    releaseBlockers: ["Other original targets have not been validated", ...signature.status !== "Valid" ? ["No trusted production signing certificate was supplied"] : [],
        "NativeAOT execution is intentionally outside the user-directed validation scope", ...values.managed ? ["CoreCLR validation packages are not release artifacts"] : []],
};
await json(path.join(directory, "manifest.json"), manifest);
console.log(JSON.stringify({ manifest: path.join(directory, "manifest.json"), runtime, version, packages: packages.map(info => info.filename), executableBytes: (await stat(executable)).size }));
