import AdmZip from "adm-zip";
import assert from "node:assert/strict";
import {
    readdir,
    readFile,
    stat,
} from "node:fs/promises";
import path from "node:path";
import {
    json,
    output,
    sha256,
} from "./common.mjs";

import {
    executableFile,
    hostPlatform,
    platformForRid,
} from "../distribution/platforms.mjs";
import { inspectNativeBinary } from "./native-binary.mjs";
const manifestPath = path.resolve(process.argv[2] ?? path.join(output, "distribution/nativeaot", hostPlatform().rid, "manifest.json"));
const manifest = JSON.parse(await readFile(manifestPath));
assert.equal(manifest.nativeExecuted, false);
assert.equal(manifest.runtime, "nativeaot");
const source = JSON.parse(await readFile(manifest.sourceProvenance));
assert.equal(sha256(JSON.stringify(source.files)), manifest.sourceSha256);
assert.equal(source.commit, manifest.sourceCommit);
assert.equal(source.dirty, manifest.sourceDirty);
assert.equal(sha256(await readFile(manifest.publish.log)), manifest.publish.sha256);
for (const item of manifest.packages) assert.equal(sha256(await readFile(item.path)), item.sha256);
const zipBytes = await readFile(manifest.zip.path);
assert.equal(sha256(zipBytes), manifest.zip.sha256);
const archive = new AdmZip(zipBytes), entries = new Map(archive.getEntries().map(entry => [entry.entryName, entry]));
for (const [name, hash] of Object.entries(manifest.payload)) {
    assert.equal(sha256(await readFile(path.join(manifest.platformDirectory, "lib", name))), hash);
    assert.equal(sha256(entries.get("lib/" + name).getData()), hash);
}
for (const name of ["LICENSE", "NOTICE.txt", "NOTICE-Go.txt", "licenses/LICENSE-DotNet.txt", "licenses/NOTICE-System.IO.Hashing.txt", "licenses/NOTICE-NativeAOT.txt"]) assert.ok(entries.get(name)?.getData().length > 0, name);
const target = platformForRid(manifest.rid);
const executablePath = path.join(manifest.platformDirectory, "lib", manifest.executableFile ?? executableFile(target, manifest.executableName ?? "tsgo"));
const executable = await readFile(executablePath);
const binary = inspectNativeBinary(executable, target);
if (process.platform !== "win32") assert.ok((await stat(executablePath)).mode & 0o111, "Unix executable mode");
const main = JSON.parse(await readFile(path.join(manifest.mainDirectory, "package.json")));
const platform = JSON.parse(entries.get("package.json").getData());
assert.equal(main.version, platform.version);
assert.deepEqual({ ...main.typescriptBackend, rid: manifest.rid }, platform.typescriptBackend);
assert.equal(main.optionalDependencies[platform.name], main.version);
assert.equal(main.typescriptBackend.astProtocolVersion, 9);
assert.ok(!JSON.stringify(main).includes("@typescript/source"));
assert.ok(!Object.keys(manifest.payload).some(name => /\.(?:dll|deps\.json|runtimeconfig\.json|pdb)$/.test(name)));
const summary = { manifest: manifestPath, executableBytes: executable.length, executableSha256: sha256(executable), ...binary, externalLibraries: Object.keys(manifest.payload).filter(name => /^lib.*\.d\.ts$/.test(name)).length, zipBytes: zipBytes.length, packageBytes: await Promise.all(manifest.packages.map(async item => ({ name: item.name, size: (await stat(item.path)).size, sha256: item.sha256 }))), notices: 6, signature: manifest.signature, sourceSha256: manifest.sourceSha256, nativeExecuted: false };
await json(path.join(path.dirname(manifestPath), "verification.json"), summary);
console.log(JSON.stringify(summary));
