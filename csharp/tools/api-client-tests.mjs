import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createWriteStream } from "node:fs";
import {
    cp,
    mkdir,
    readFile,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import {
    json,
    output,
    referenceRevision,
    root,
    run,
    sha256,
} from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const directory = path.resolve(option("--directory", path.join(output, "phase7-validation/api-client")));
const client = path.join(directory, "packages/typescript");
const clientSource = path.resolve(option("--client-directory", path.join(root, "packages/typescript")));
await mkdir(client, { recursive: true });
for (const name of ["src", "lib", "vendor", "package.json"]) await cp(path.join(clientSource, name), path.join(client, name), { recursive: true });
await cp(path.join(root, "packages/typescript/test"), path.join(client, "test"), { recursive: true });
const adaptations = [];
async function adapt(relative, transform) {
    const file = path.join(client, relative), original = await readFile(file, "utf8"), modified = transform(original);
    if (original === modified) return;
    await writeFile(file, modified);
    adaptations.push({ file: relative, originalSha256: sha256(original), adaptedSha256: sha256(modified) });
}
await adapt("lib/getExePath.js", text => text.replace("export default function getExePath() {", "export default function getExePath() {\n    if (process.env.CSHARP_API_EXE) return process.env.CSHARP_API_EXE;"));
if ((await readFile(path.join(client, "src/api/node/protocol.ts"), "utf8")).includes("PROTOCOL_VERSION = 8")) await adapt("src/api/node/protocol.ts", text => text.replace("PROTOCOL_VERSION = 8", "PROTOCOL_VERSION = 9"));
else assert.match(await readFile(path.join(client, "src/api/node/protocol.ts"), "utf8"), /PROTOCOL_VERSION = 9/);
for (const mode of ["async", "sync"]) await adapt(`test/${mode}/api.test.ts`, text => text.replaceAll('const insertionPos = sourceText.indexOf("\\n    console.log") + 1;', 'const insertionPos = Buffer.byteLength(sourceText.slice(0, sourceText.indexOf("\\n    console.log") + 1));'));
await json(path.join(directory, "client-adaptations.json"), adaptations);
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
assert.equal(reference.referenceRevision, referenceRevision);
const source = path.join(output, reference.sourceRelativePath, "tsc");
const bridge = path.join(source, "cmd/csharp-api-server");
await mkdir(bridge, { recursive: true });
await cp(path.join(root, "csharp/oracle/api-server/main.go"), path.join(bridge, "main.go"));
const oracle = path.join(directory, "oracle.exe");
await run("D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe", ["-C", source, "build", "-o", oracle, "./cmd/csharp-api-server"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
const candidate = path.resolve(option("--executable", path.join(option("--managed-directory", path.join(output, "phase7-build/bin/TypeScript.CommandLine/release")), "tsgo-cs.exe")));
const filter = option("--filter", "printNode|printFile");
async function execute(executable, label, mode) {
    const child = spawn(process.execPath, ["--conditions", "@typescript/source", "--test", "--test-reporter", "tap", "--test-timeout=180000", "--test-skip-pattern", "Benchmarks", "--test-name-pattern", filter, `test/${mode}/api.test.ts`], { cwd: client, windowsHide: true, env: { ...process.env, CSHARP_API_EXE: executable, DOTNET_ROOT: process.env.DOTNET_ROOT ?? "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64" } });
    let stdout = "", stderr = "";
    const out = createWriteStream(path.join(directory, `${label}-${mode}.tap`)), err = createWriteStream(path.join(directory, `${label}-${mode}.stderr`));
    child.stdout.on("data", data => {
        stdout += data;
        out.write(data);
    });
    child.stderr.on("data", data => {
        stderr += data;
        err.write(data);
    });
    const exitCode = await new Promise((resolve, reject) => {
        child.on("error", reject);
        child.on("close", resolve);
    });
    await Promise.all([new Promise(resolve => out.end(resolve)), new Promise(resolve => err.end(resolve))]);
    const counts = Object.fromEntries([...stdout.matchAll(/^# (tests|pass|fail|cancelled|skipped) (\d+)$/gm)].map(match => [match[1], +match[2]]));
    return { label, mode, exitCode, ...counts };
}
const results = [];
for (const mode of ["async", "sync"]) results.push(...await Promise.all([execute(oracle, "reference", mode), execute(candidate, "candidate", mode)]));
const summary = { referenceRevision, filter, results, compilerSha256: sha256(await readFile(path.join(path.dirname(candidate), "TypeScript.Compiler.dll"))), ...(process.argv.includes("--client-directory") ? { generatedClient: clientSource } : {}), clientAdaptations: "Executable selection, AST v9, and UTF-8 insertion positions for both backends; original test assertions retained" };
await json(path.join(directory, "summary.json"), summary);
console.log(JSON.stringify(summary));
assert(results.every(result => result.exitCode === 0));
