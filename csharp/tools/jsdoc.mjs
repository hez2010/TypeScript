import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import {
    copyFile,
    mkdir,
    readFile,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import {
    output,
    root,
    run,
} from "./common.mjs";
import {
    caseName,
    classifyDocumentationDifference,
    digest,
} from "./compare-documentation.mjs";

const option = (name, fallback) => process.argv.includes(name) ? process.argv[process.argv.indexOf(name) + 1] : fallback;
const dotnet = option("--dotnet", process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, "dotnet.exe") : "dotnet");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc");
const oracle = path.join(output, "jsdoc-oracle.exe");
if (!process.argv.includes("--no-build")) {
    await mkdir(path.join(source, "cmd/jsdoc-probe"), { recursive: true });
    await copyFile(path.join(root, "csharp/oracle/jsdoc/main.go"), path.join(source, "cmd/jsdoc-probe/main.go"));
    await copyFile(path.join(root, "csharp/oracle/syntax/scalars.generated.go"), path.join(source, "internal/ast/csharp_scalars.generated.go"));
    await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", oracle, "./cmd/jsdoc-probe"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
    await run(dotnet, ["build", "csharp/tests/TypeScript.SourceMetadata", "-c", "Release", "--no-restore"]);
}
const native = option("--native");
const candidate = native ?? dotnet;
const args = native ? [] : [path.join(option("--managed-directory", path.join(root,
    "csharp/tests/TypeScript.SourceMetadata/bin/Release/net11.0")), "TypeScript.SourceMetadata.dll")];
await run(process.execPath, [path.join(root, "csharp/tools/verify-documentation-policy.mjs")]);
async function probe(command, args, requests) {
    return await new Promise((resolve, reject) => {
        const child = spawn(command, args, { windowsHide: true });
        const stdout = [], stderr = [];
        child.stdout.on("data", data => stdout.push(data));
        child.stderr.on("data", data => stderr.push(data));
        child.on("error", reject);
        let inputError;
        child.stdin.on("error", error => {
            inputError = error;
        });
        child.on("close", code => {
            const lines = Buffer.concat(stdout).toString().trim().split(/\r?\n/).filter(Boolean);
            if (code || inputError) reject(new Error(`${command}: ${code}; next case ${requests[lines.length]?.name}\n${Buffer.concat(stderr)}\n${inputError ?? ""}`));
            else resolve(lines.map(JSON.parse));
        });
        child.stdin.end(requests.map(x => JSON.stringify(x)).join("\n") + "\n");
    });
}
let requests, sourceFiles = 0, libraryFiles = 0;
if (process.argv.includes("--input")) requests = JSON.parse(await readFile(option("--input"), "utf8")).map(request => ({ ...request, mode: "documentation" }));
else {
    const contentFiles = (await run("rg", ["-l", "-F", "/**", "tsc/testdata/tests/cases", "-g", "*.ts", "-g", "*.tsx", "-g", "*.js", "-g", "*.jsx"])).split(/\r?\n/).filter(Boolean);
    const libraries = (await run("rg", ["--files", "tsc/internal/bundled/libs"])).split(/\r?\n/).filter(file => file.endsWith(".d.ts"));
    const files = [...new Set([...contentFiles, ...libraries])].sort();
    sourceFiles = files.length;
    libraryFiles = libraries.length;
    const units = await probe(oracle, [], files.map(file => ({ mode: "units", path: path.join(root, file) })));
    requests = units.flatMap((list, i) => (list ?? []).map((unit, j) => ({ ...unit, name: caseName(`${files[i]}::${j}`), mode: "documentation" })));
}
if (process.argv.includes("--filter")) requests = requests.filter(request => request.name.includes(option("--filter")));
console.log(JSON.stringify({ suite: "documentation", sourceFiles, libraryFiles, expandedUnits: requests.length }));
const [expected, actual] = await Promise.all([probe(oracle, [], requests), probe(candidate, args, requests)]);
assert.equal(expected.length, requests.length);
assert.equal(actual.length, requests.length);
const differences = [], failures = [];
let hosts = 0, trees = 0, nodes = 0;
for (let i = 0; i < requests.length; i++) {
    hosts += actual[i][0].length;
    for (const host of actual[i][0]) {
        trees += host[3].length;
        for (const tree of host[3]) nodes += tree.length;
    }
    const policy = classifyDocumentationDifference(requests[i], expected[i], actual[i]);
    if (policy === "exact") continue;
    const difference = { case: requests[i], sourceSha256: digest(Buffer.from(requests[i].text, "base64")), expectedSha256: digest(expected[i]), actualSha256: digest(actual[i]), policy, expected: expected[i], actual: actual[i] };
    differences.push(difference);
    if (!policy) failures.push(difference);
}
const summary = {
    timestamp: new Date().toISOString(),
    referenceRevision: reference.referenceRevision,
    sourceFiles,
    libraryFiles,
    expandedUnits: requests.length,
    hosts,
    documentationTrees: trees,
    documentationNodes: nodes,
    exact: requests.length - differences.length,
    permittedDifferences: differences.length - failures.length,
    failures: failures.length,
    policyNegativeControls: 36,
    runtimeKind: native ? "NativeAOT" : "CoreCLR",
    runtime: native ? await run(native, ["--native-check"]) : "CoreCLR",
    sdk: await run(dotnet, ["--version"], { cwd: path.join(root, "csharp") }),
    configuration: { optimizationPreference: "Speed", runtimeAsync: true, ...(native ? { ilcInstructionSet: "native" } : {}) },
    inputSha256: digest(requests),
    candidateSha256: digest(await readFile(native ?? args[0])),
    compilerSha256: native ? undefined : digest(await readFile(path.join(path.dirname(args[0]), "TypeScript.Compiler.dll"))),
    oracleSha256: digest(await readFile(oracle)),
    invariants: ["acyclic documentation trees", "unique node identity per comment tree", "correct child-parent identity", "source-bounded UTF-8 spans"],
};
await writeFile(path.join(output, "documentation-inputs.json"), JSON.stringify(requests));
await writeFile(path.join(output, "documentation-differences.json"), JSON.stringify(differences));
await writeFile(path.join(output, "documentation-failures.json"), JSON.stringify(failures));
await writeFile(path.join(output, "documentation-summary.json"), JSON.stringify(summary, null, 2));
console.log(JSON.stringify(summary, null, 2));
for (const failure of failures.slice(0, 5)) console.log(failure.case.name);
if (failures.length) process.exitCode = 1;
