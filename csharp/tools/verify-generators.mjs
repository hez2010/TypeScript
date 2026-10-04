import assert from "node:assert/strict";
import {
    mkdir,
    mkdtemp,
    readdir,
    readFile,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import {
    json,
    output,
    root,
    run,
    sha256,
} from "./common.mjs";
import { generateClient } from "./generate-client.mjs";

const directory = path.join(output, "generator-validation");
await mkdir(directory, { recursive: true });
const snapshots = JSON.parse(await readFile(path.join(root, "csharp/data/manifest.json"), "utf8"));
for (const [name, expected] of Object.entries(snapshots.snapshots)) assert.equal(sha256(await readFile(path.join(root, "csharp/data", name))), expected, `Frozen input changed: ${name}`);
async function hashes(directory, filter = () => true) {
    const result = {};
    async function visit(relative = "") {
        for (const entry of await readdir(path.join(directory, relative), { withFileTypes: true })) {
            const name = path.posix.join(relative.replaceAll("\\", "/"), entry.name);
            if (entry.isDirectory()) await visit(name);
            else if (filter(name)) result[name] = sha256(await readFile(path.join(directory, name)));
        }
    }
    await visit();
    return Object.fromEntries(Object.entries(result).sort(([a], [b]) => a.localeCompare(b)));
}
const generated = () => hashes(path.join(root, "csharp/src"), name => name.endsWith(".generated.cs"));
const before = await generated(), checks = [];
for (const file of (await readdir(path.join(root, "csharp/tools"))).filter(name => /^generate-.*\.mjs$/.test(name) && name !== "generate-client.mjs").sort()) {
    const log = await run(process.execPath, [path.join(root, "csharp/tools", file), "--check"]);
    await writeFile(path.join(directory, file + ".log"), log + "\n");
    checks.push(file);
}
assert.deepEqual(await generated(), before, "Checking generators must not modify product files");
const clients = [];
for (const name of ["first", "second"]) {
    const client = await generateClient(await mkdtemp(path.join(directory, name + "-")));
    clients.push(await hashes(client, name => !name.endsWith(".tsbuildinfo")));
}
assert.deepEqual(clients[0], clients[1], "Independent byte-coordinate client generation must be reproducible");
const summary = { generators: checks, generatedFiles: Object.keys(before).length, generatedHashes: before, clientFiles: Object.keys(clients[0]).length, clientHash: sha256(JSON.stringify(clients[0])), clientHashes: clients[0], identicalIndependentClientBuilds: true, excludedClientFiles: ["TypeScript incremental build caches (.tsbuildinfo)"] };
await json(path.join(directory, "summary.json"), summary);
console.log(JSON.stringify({ generators: checks.length + 1, generatedFiles: summary.generatedFiles, clientFiles: summary.clientFiles, clientHash: summary.clientHash }));
