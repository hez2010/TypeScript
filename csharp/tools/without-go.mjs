import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import {
    mkdir,
    mkdtemp,
    readdir,
    rm,
    symlink,
} from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { npmCommand } from "./common.mjs";
import { execute } from "./product.mjs";

const names = process.platform === "win32" ? ["go.exe", "go.cmd", "go.bat", "gofmt.exe"] : ["go", "gofmt"];
const env = { ...process.env };
const pathKey = Object.keys(env).find(key => key.toLowerCase() === "path") ?? "PATH";
const directories = [];
let aliases;
for (const directory of env[pathKey].split(path.delimiter)) {
    if (!names.some(name => existsSync(path.join(directory, name)))) {
        directories.push(directory);
        continue;
    }
    if (process.platform === "win32") continue;
    // A Unix package manager may put Go beside git, npm and the native linker.
    // Keep those tools through a temporary directory that exposes no Go executable.
    aliases ??= await mkdtemp(path.join(os.tmpdir(), "typescript-without-go-"));
    const filtered = path.join(aliases, String(directories.length));
    await mkdir(filtered);
    for (const entry of await readdir(directory)) if (!names.includes(entry)) await symlink(path.resolve(directory, entry), path.join(filtered, entry));
    directories.push(filtered);
}
env[pathKey] = directories.join(path.delimiter);
env.GOTOOLCHAIN = "local";
env.GOWORK = "off";
delete env.GOFMT;
for (const name of ["go", "gofmt"]) {
    const probe = spawnSync(name, ["version"], { env, windowsHide: true });
    assert.equal(probe.error?.code, "ENOENT", `${name} must be unavailable in the product validation environment`);
}
assert.ok(process.argv[2], "Usage: node csharp/tools/without-go.mjs <node script> [...arguments]");
console.log("Go is absent from PATH; running the product command.");
const args = process.argv.slice(2);
const [command, commandArgs] = args[0] === "--npm" ? npmCommand(args.slice(1)) : [process.execPath, args];
try {
    await execute(command, commandArgs, { env });
}
finally {
    if (aliases) await rm(aliases, { recursive: true, force: true });
}
