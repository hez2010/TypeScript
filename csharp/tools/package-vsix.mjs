import AdmZip from "adm-zip";
import assert from "node:assert/strict";
import {
    cp,
    mkdir,
    mkdtemp,
    readFile,
} from "node:fs/promises";
import path from "node:path";
import { parseArgs } from "node:util";
import {
    json,
    output,
    root,
    sha256,
} from "./common.mjs";
import {
    node,
    npm,
} from "./product.mjs";

import {
    hostPlatform,
    platformForRid,
} from "../distribution/platforms.mjs";
const { values } = parseArgs({ options: { "managed": { type: "boolean" }, "rid": { type: "string" }, "for-release": { type: "boolean" }, "directory": { type: "string" } } });
const directory = path.resolve(values.directory ?? path.join(output, "distribution", values.managed ? "coreclr-validation" : "nativeaot", values.rid ?? hostPlatform().rid));
const manifest = JSON.parse(await readFile(path.join(directory, "manifest.json"), "utf8"));
const target = platformForRid(manifest.rid);
assert.ok(target.vsix, `VS Code has no VSIX target for ${target.rid}`);
assert.equal(manifest.packageName, "typescript");
assert.equal(manifest.executableName, "tsc");
assert.equal(manifest.runtime, values.managed ? "coreclr-validation" : "nativeaot");
const platform = JSON.parse(await readFile(path.join(manifest.platformDirectory, "package.json"), "utf8"));
assert.equal(platform.typescriptBackend.kind, "csharp");
if (values["for-release"]) assert.equal(manifest.signature.status, "Valid", "Release VSIX packaging requires a signed compiler");
await npm(["run", "-w", "native-preview", "bundle:release"]);
await mkdir(path.join(directory, "vsix"), { recursive: true });
const stage = await mkdtemp(path.join(directory, "vsix/stage-"));
const extension = path.join(root, "packages/vscode-typescript");
await cp(extension, stage, { recursive: true, filter: source => !source.split(path.sep).some(part => ["node_modules", "src", "test", "lib"].includes(part)) });
await cp(path.join(manifest.platformDirectory, "lib"), path.join(stage, "lib"), { recursive: true });
for (const name of ["LICENSE", "NOTICE.txt", "NOTICE-Go.txt", "licenses"]) await cp(path.join(manifest.platformDirectory, name), path.join(stage, name), { recursive: true });
const pkg = JSON.parse(await readFile(path.join(stage, "package.json"), "utf8"));
pkg.bundledTypeScriptVersion = manifest.version;
pkg.typescriptBackend = platform.typescriptBackend;
pkg.files = [...new Set([...pkg.files, "NOTICE-Go.txt", "licenses"])];
await json(path.join(stage, "package.json"), pkg);
const vsix = path.join(directory, "vsix", `${pkg.name}-${pkg.version}-${target.vsix}.vsix`);
await node(path.join(root, "node_modules/@vscode/vsce/vsce"), ["package", "--no-dependencies", "--out", vsix, "--target", target.vsix], { cwd: stage });
const archive = new AdmZip(vsix);
for (const [name, hash] of Object.entries(manifest.payload)) assert.equal(sha256(archive.readFile("extension/lib/" + name)), hash, name);
await json(path.join(directory, "vsix/manifest.json"), { path: vsix, sha256: sha256(await readFile(vsix)), compilerSourceSha256: manifest.sourceSha256, target: target.vsix, backend: "csharp", runtime: manifest.runtime, signature: "unsigned VSIX", nativeExecuted: false });
console.log(vsix);
