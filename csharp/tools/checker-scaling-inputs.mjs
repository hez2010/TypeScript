// Synthetic file-level scaling controls; these are not representative application benchmarks.
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";

const directory = path.resolve(process.argv[2] ?? "built/csharp/checker-scaling");
const blobDirectory = path.join(directory, "blobs");
await mkdir(blobDirectory, { recursive: true });
const manifest = JSON.parse(await readFile("csharp/compatibility/phase4-workloads.json", "utf8"));
const inputs = [];
for (const shape of ["balanced", "uneven"]) {
    const files = {};
    for (let file = 0; file < 32; file++) {
        const declarations = shape === "balanced" ? 64 : file % 8 === 1 ? 288 : 32;
        let source = "";
        for (let i = 0; i < declarations; i++) {
            source += `export interface Item${i} { left: number; right: string; values: number[]; }\n`
                + `export const value${i}: Item${i} = { left: ${i}, right: 'text', values: [1, 2, 3] };\n`;
        }
        const blob = createHash("sha256").update(source).digest("hex");
        await writeFile(path.join(blobDirectory, blob), source);
        files[`/scaling/file${file}.ts`] = blob;
    }
    const input = {
        name: `scaling/${shape}-32-files`, status: "ready", files, blobDirectory, symlinks: {},
        roots: Object.keys(files), currentDirectory: "/scaling", configFileName: "",
        caseSensitive: true, libraryDirectory: "bundled:///libs", singleThreaded: true,
        options: { target: "es2022", lib: ["es5"], strict: true, skipLibCheck: true, noEmit: true },
    };
    const oracle = spawnSync(path.resolve("built/csharp/checker-workload-oracle.exe"), [], {
        input: JSON.stringify(input) + "\n", encoding: "utf8", windowsHide: true,
        env: { ...process.env, GOMAXPROCS: String(manifest.processorCount) },
    });
    if (oracle.error || oracle.status !== 0) throw oracle.error ?? Error(oracle.stderr);
    const { diagnosticCount, graphSha256 } = JSON.parse(oracle.stdout);
    if (diagnosticCount !== 0) throw Error(`Unexpected scaling diagnostics: ${diagnosticCount}`);
    inputs.push({ ...input, expected: { diagnosticCount, graphSha256 } });
}
await writeFile(path.join(directory, "inputs.json"), JSON.stringify(inputs, null, 4) + "\n");
await writeFile(path.join(directory, "manifest.json"), JSON.stringify({
    ...manifest,
    scope: "Synthetic balanced and uneven independent source files; fresh parsing, binding and checking, with es5 libraries",
    warmups: 20, samples: 15, cases: inputs.map(input => input.name),
}, null, 4) + "\n");
console.log(directory);
