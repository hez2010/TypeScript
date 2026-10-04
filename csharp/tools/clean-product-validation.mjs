import assert from "node:assert/strict";
import {
    mkdir,
    mkdtemp,
    readFile,
    rm,
    stat,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import { hostPlatform } from "../distribution/platforms.mjs";
import {
    json,
    output,
    root,
    run,
    sha256,
} from "./common.mjs";
import { execute } from "./product.mjs";

// A fresh checkout plus the current patch: no built files, node_modules or reference caches.
await mkdir(path.join(output, "clean-validation"), { recursive: true });
const directory = await mkdtemp(path.join(output, "clean-validation/run-"));
await run("git", ["clone", "--shared", "--no-checkout", root, directory]);
await run("git", ["-c", "core.longpaths=true", "checkout", "--detach", "HEAD"], { cwd: directory });
const files = [
    ...new Set(
        (await run("git", ["diff", "--name-only", "-z", "HEAD"])).split("\0")
            .concat((await run("git", ["ls-files", "--others", "--exclude-standard", "-z"])).split("\0")).filter(Boolean),
    ),
];
const patch = {};
for (const file of files) {
    const destination = path.resolve(directory, file);
    assert.ok(destination.startsWith(directory + path.sep));
    try {
        const bytes = await readFile(path.join(root, file));
        await mkdir(path.dirname(destination), { recursive: true });
        await writeFile(destination, bytes);
        patch[file] = sha256(bytes);
    }
    catch (error) {
        if (error.code !== "ENOENT") throw error;
        await rm(destination, { force: true });
        patch[file] = null;
    }
}
await json(path.join(output, "clean-validation/current.json"), { directory, patch });
for (const name of ["node_modules", "built", "csharp/src/TypeScript.Compiler/bin", "csharp/src/TypeScript.Compiler/obj"]) await assert.rejects(stat(path.join(directory, name)), { code: "ENOENT" });
await execute(process.execPath, ["csharp/tools/without-go.mjs", "--npm", "ci", "--no-audit", "--no-fund"], { cwd: directory });
await execute(process.execPath, ["csharp/tools/without-go.mjs", "node_modules/hereby/bin/hereby.js", "validate"], { cwd: directory });
if (hostPlatform().vsix) await execute(process.execPath, ["csharp/tools/without-go.mjs", "csharp/tools/package-vsix.mjs", "--managed"], { cwd: directory });
if (process.argv.includes("--native")) {
    for (const script of ["package.mjs", "verify-package.mjs", ...(hostPlatform().vsix ? ["package-vsix.mjs"] : [])]) await execute(process.execPath, ["csharp/tools/without-go.mjs", "csharp/tools/" + script], { cwd: directory });
}
const result = { directory, sourceCommit: await run("git", ["rev-parse", "HEAD"]), patchSha256: sha256(JSON.stringify(patch)), goAvailable: false, freshDependencies: true, managedValidation: true, nativePublished: process.argv.includes("--native"), nativeExecuted: false };
await json(path.join(output, "clean-validation/summary.json"), result);
console.log(JSON.stringify(result));
