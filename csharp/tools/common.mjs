import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import {
    mkdir,
    readFile,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
export const output = path.join(root, "built/csharp");
export const referenceRevision = "f29aeb9f825d96feea27841f3f7342dbf0df68a8";
export const sha256 = data => createHash("sha256").update(data).digest("hex");
export async function json(file, value) {
    await mkdir(path.dirname(file), { recursive: true });
    await writeFile(file, JSON.stringify(value, null, 4) + "\n");
}
export async function run(command, args, options = {}) {
    const child = spawn(command, args, { cwd: root, windowsHide: true, ...options });
    child.stdin?.end();
    const stdout = [];
    const stderr = [];
    child.stdout?.on("data", chunk => stdout.push(chunk));
    child.stderr?.on("data", chunk => stderr.push(chunk));
    await new Promise((resolve, reject) => {
        child.on("error", reject);
        child.on("close", code => code === 0 ? resolve() : reject(new Error(`${command} ${args.join(" ")} exited ${code}\n${Buffer.concat(stderr)}\n${Buffer.concat(stdout)}`)));
    });
    return Buffer.concat(stdout).toString("utf8").trimEnd();
}
export async function trackedFiles() {
    return (await run("git", ["ls-files", "-z"])).split("\0").filter(Boolean).sort();
}
export async function hashFiles(files) {
    const hash = createHash("sha256");
    for (const file of files) {
        hash.update(file + "\0");
        hash.update(sha256(await readFile(path.join(root, file))) + "\n");
    }
    return hash.digest("hex");
}
