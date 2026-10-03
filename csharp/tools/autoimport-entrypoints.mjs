import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { output, root, run, sha256, referenceRevision } from "./common.mjs";

const option = (name, fallback) => process.argv.includes(name) ? process.argv[process.argv.indexOf(name) + 1] : fallback;
const directory = path.resolve(option("--directory", path.join(output, "phase7-validation/autoimport-entrypoints")));
const managed = path.resolve(option("--managed-directory", path.join(output, "phase7-tags-build/bin/TypeScript.Compatibility/release")));
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
assert.equal(reference.referenceRevision, referenceRevision);
const source = path.join(output, reference.sourceRelativePath, "tsc");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe");
await mkdir(directory, { recursive: true });
await mkdir(path.join(source, "cmd/resolution-probe"), { recursive: true });
await copyFile(path.join(root, "csharp/oracle/resolution/main.go"), path.join(source, "cmd/resolution-probe/main.go"));
const oracle = path.join(directory, "oracle.exe");
await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", oracle, "./cmd/resolution-probe"],
    { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
const manifests = [undefined, "{", {}, { main: "./a.js" }, { types: "./a.d.ts" }, { main: "missing.js", types: "b.d.ts" },
    { types: "sub" }, { main: "a.js", typesVersions: { ">=7": { "*": ["sub/*"] } } },
    ...[null, 1, false, [], {}, "./a.js", "./a.d.ts", "./a", "./sub", "../a.js", "./sub/../a.js", "././a.js", "./node_modules/nested/a.js",
        ["./missing.js", "./a.js", "./b.js"], { import: "./a.mjs", require: "./a.cjs" },
        { "types@>=7": "./b.d.ts", default: "./a.js" }, { "types@>=8": "./b.d.ts", types: "./a.d.ts" },
        { custom: "./b.js", types: "./a.d.ts", default: "./a.js" },
        { import: { custom: "./a.mjs", default: "./b.js" }, require: "./a.cjs", default: "./a.js" },
        { import: "./a.js", require: { import: "./b.js", custom: "./a.cjs", default: "./a.mjs" } },
        { ".": "./a.js", "./*": "./sub/*.js" }, { "./*": "./sub/*" }, { "./*.js": "./sub/*" },
        { "./*": "./sub/*.d.ts" }, { "./*": "./sub/**/*.js" }, { "./*": "./sub/prefix*Suffix.js" },
        { "./fixed": "./sub/*" }, { "./*.js": "./sub/*.js", "./private/*": null },
        { "./*": ["./sub/*.mjs", "./sub/*.cjs"] }, { ".": "./a.js", custom: "./b.js" },
        { "./*": "./SUB/*.JS" }, { ".": { types: null, default: "./a.js" } },
        { "./*.js": { import: "./sub/*.js", require: "./sub/*.cjs" } }].map(exports => ({ exports }))];
const filenames = ["a.ts", "a.tsx", "a.d.ts", "a.mts", "a.d.mts", "a.cts", "a.d.cts", "a.js", "b.d.ts", "b.js", "index.ts",
    "sub/a.ts", "sub/a.tsx", "sub/a.d.ts", "sub/a.mts", "sub/a.cts", "sub/a.js", "sub/a.native.ts", "sub/index.ts", "sub/prefixXSuffix.d.ts",
    "sub/more/a.d.ts", "sub/a.d.css.ts", "sub/a.d.one.d.two.ts", "sub/.hidden.ts", "sub/node_modules/nested/a.ts", "node_modules/nested/a.ts"];
const cases = [];
for (const [manifestIndex, manifest] of manifests.entries()) for (const recursive of [false, true]) for (const sensitive of [false, true])
    for (const linked of [false, true]) for (const options of [{}, { moduleResolution: "nodenext", moduleSuffixes: [".native", ""] }, { noDtsResolution: true, jsx: "preserve" }]) {
        const packageDirectory = "/project/node_modules/pkg", realDirectory = linked ? "/workspace/pkg" : packageDirectory;
        const files = Object.fromEntries(filenames.map(name => [`${realDirectory}/${name}`, "export const value = 0;"]));
        if (manifest !== undefined) files[`${realDirectory}/package.json`] = typeof manifest === "string" ? manifest : JSON.stringify(manifest);
        cases.push({ name: `${manifestIndex}:${recursive}:${sensitive}:${linked}:${cases.length}`, operation: "entrypoints", directory: "/project",
            containingFile: "/project/index.ts", other: packageDirectory, path: "pkg", recursive, sensitive, options, files,
            symlinks: linked ? { [packageDirectory]: realDirectory } : {} });
    }
const inputs = cases.map(input => JSON.stringify(input)).join("\n") + "\n";
await writeFile(path.join(directory, "inputs.jsonl"), inputs);
async function probe(command, args, filename) {
    const child = spawn(command, args, { windowsHide: true }), out = [], errors = [];
    child.stdout.on("data", data => out.push(data)); child.stderr.on("data", data => errors.push(data));
    child.stdin.on("error", () => {}); child.stdin.end(inputs);
    const code = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
    await writeFile(path.join(directory, filename + ".stderr"), Buffer.concat(errors));
    await writeFile(path.join(directory, filename + ".jsonl"), Buffer.concat(out));
    assert.equal(code, 0, Buffer.concat(errors).toString());
    const results = Buffer.concat(out).toString().trim().split(/\r?\n/).map(JSON.parse);
    assert.equal(results.length, cases.length); return results;
}
const [expected, actual] = await Promise.all([probe(oracle, [], "reference"), probe(dotnet, [path.join(managed, "TypeScript.Compatibility.dll"), "--resolution-lines"], "candidate")]);
const differences = cases.flatMap((input, i) => { try { assert.deepEqual(actual[i], expected[i]); return []; } catch { return [{ input, expected: expected[i], actual: actual[i] }]; } });
await writeFile(path.join(directory, "differences.json"), JSON.stringify(differences, null, 2) + "\n");
const summary = { referenceRevision, cases: cases.length, passed: cases.length - differences.length, differences: differences.length,
    inputsSha256: sha256(inputs), oracleSha256: sha256(await readFile(oracle)), compilerSha256: sha256(await readFile(path.join(managed, "TypeScript.Compiler.dll"))),
    harnessSha256: sha256(await readFile(path.join(managed, "TypeScript.Compatibility.dll"))) };
await writeFile(path.join(directory, "summary.json"), JSON.stringify(summary, null, 2) + "\n");
console.log(JSON.stringify(summary));
if (differences.length) process.exitCode = 1;
