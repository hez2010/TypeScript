import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import {
    executableFile,
    platformForRid,
    platforms,
    platformSuffix,
} from "../distribution/platforms.mjs";
import {
    json,
    output,
    root,
    run,
} from "./common.mjs";
import { inspectNativeBinary } from "./native-binary.mjs";
import { dotnet } from "./product.mjs";

const sdk = JSON.parse(await run(dotnet, ["msbuild", "csharp/src/TypeScript.CommandLine/TypeScript.CommandLine.csproj", "-p:PublishAot=true", "-getProperty:NETCoreSdkVersion", "-getItem:KnownRuntimePack"]));
const nativeRids = sdk.Items.KnownRuntimePack.find(pack => pack.TargetFramework === "net11.0" && pack.RuntimePackLabels === "NativeAOT").RuntimePackRuntimeIdentifiers.split(";");
const plans = [];
for (const target of platforms) {
    assert.ok(nativeRids.includes(target.rid), `The installed SDK has no NativeAOT runtime pack for ${target.rid}`);
    const plan = JSON.parse(await run(process.execPath, ["csharp/tools/package.mjs", "--rid", target.rid, "--plan"]));
    assert.deepEqual(plan.target, target);
    assert.equal(plan.executable, executableFile(target));
    assert.ok(plan.optionalDependencies.includes("@typescript/typescript-" + platformSuffix(target)));
    plans.push(plan);
    // Header fixtures cover every retained object format and architecture without executing a binary.
    const bytes = Buffer.alloc(512);
    if (target.os === "win32") {
        bytes.writeUInt16LE(0x5a4d);
        bytes.writeUInt32LE(64, 0x3c);
        bytes.writeUInt32LE(0x4550, 64);
        bytes.writeUInt16LE(target.arch === "x64" ? 0x8664 : 0xaa64, 68);
        bytes.writeUInt16LE(0x20b, 88);
    }
    else if (target.os === "darwin") {
        bytes.writeUInt32LE(0xfeedfacf);
        bytes.writeUInt32LE(target.arch === "x64" ? 0x1000007 : 0x100000c, 4);
        bytes.writeUInt32LE(2, 12);
    }
    else {
        bytes.set([0x7f, 69, 76, 70, target.arch === "arm" ? 1 : 2, 1]);
        bytes.writeUInt16LE(3, 16);
        bytes.writeUInt16LE({ x64: 62, arm: 40, arm64: 183, loong64: 258, riscv64: 243 }[target.arch], 18);
    }
    inspectNativeBinary(bytes, target);
    bytes[0] = 0;
    assert.throws(() => inspectNativeBinary(bytes, target));
}
assert.equal(new Set(platforms.map(platformSuffix)).size, platforms.length);
assert.equal(new Set(platforms.map(p => p.rid)).size, platforms.length);
for (const rid of ["aix-ppc64", "linux-mips64el", "linux-ppc64", "linux-s390x", "netbsd-x64", "netbsd-arm64"]) assert.throws(() => platformForRid(rid));
const profile = await readFile(path.join(root, "csharp/src/TypeScript.Compiler/Diagnostics/NativeProfile.cs"), "utf8");
assert.ok(!profile.includes("throw new PlatformNotSupportedException"));
const summary = { sdk: sdk.Properties.NETCoreSdkVersion, targets: plans.map(p => ({ ...p.target, backend: p.backend, executable: p.executable })), objectFormats: ["PE", "ELF", "Mach-O"], nativeExecuted: false, scope: "Static SDK/RID, package-plan and object-header checks; no non-Windows execution" };
await json(path.join(output, "platform-validation.json"), summary);
console.log(JSON.stringify(summary));
