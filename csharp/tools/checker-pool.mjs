import assert from "node:assert/strict";
import {
    copyFile,
    mkdir,
    readFile,
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

const directory = path.join(output, "checker-pool");
const sourceFirst = process.argv.includes("--source-first");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc");
const go = "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe";
const dotnet = process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, "dotnet.exe") : "dotnet";
const dll = path.join(root, "csharp/tests/TypeScript.Compatibility/bin/Release/net11.0/TypeScript.Compatibility.dll");
await mkdir(directory, { recursive: true });
const cases = [];
let seed = 0x42bc69;
const random = maximum => {
    seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
    return seed % maximum;
};
for (let scenario = 0; scenario < 512; scenario++) {
    const length = scenario < 16 ? scenario : 1 + random(128);
    const weights = Array.from({ length }, () => 1 + random(scenario % 5 === 0 ? 100000000 : 1000));
    const imports = Array.from({ length }, () => random(20));
    const declarations = Array.from({ length }, () => random(4) !== 0);
    if (sourceFirst) declarations.fill(false);
    const adjacency = Array.from({ length }, () => []);
    for (let i = 0; i < length && scenario % 3 !== 0; i++) {
        for (let j = 0; j < imports[i]; j++) {
            const target = scenario % 3 === 1 ? random(length) : 0;
            if (target !== i) {
                adjacency[i].push(target);
                adjacency[target].push(i);
            }
        }
    }
    if (!sourceFirst || length !== 0) cases.push({ weights, imports, declarations, adjacency, count: 1 + scenario % 8 });
}
const input = path.join(directory, sourceFirst ? "source-inputs.json" : "inputs.json");
await json(input, cases);
const oracle = path.join(directory, "oracle.exe");
const bridge = path.join(root, "csharp/oracle/checker-pool/bridge_test.go");
const oracleMetadata = path.join(directory, "oracle-build.json");
const bridgeHash = sha256(await readFile(bridge));
let cached;
try {
    cached = JSON.parse(await readFile(oracleMetadata, "utf8"));
    if (cached.executableSha256 !== sha256(await readFile(oracle))) cached = null;
}
catch (error) {
    if (error.code !== "ENOENT") throw error;
    cached = null;
}
if (cached?.sourceSha256 !== bridgeHash || cached?.referenceRevision !== referenceRevision) {
    await copyFile(bridge, path.join(source, "internal/compiler/csharp_checker_pool_test.go"));
    await run(go, ["-C", source, "test", "-c", "-o", oracle, "./internal/compiler"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
    await json(oracleMetadata, { referenceRevision, sourceSha256: bridgeHash, executableSha256: sha256(await readFile(oracle)) });
}
const oracleHash = sha256(await readFile(oracle));
const inputHash = sha256(await readFile(input));
const referenceOutput = path.join(directory, `reference-${inputHash}-${oracleHash}.json`);
try {
    await readFile(referenceOutput);
}
catch (error) {
    if (error.code !== "ENOENT") throw error;
    await run(oracle, ["-test.run=^TestCSharpCheckerPartitions$"], {
        cwd: source,
        env: { ...process.env, CSHARP_PARTITION_INPUT: input, CSHARP_PARTITION_OUTPUT: referenceOutput },
    });
}
const candidateOutput = path.join(directory, sourceFirst ? "source-candidate.json" : "candidate.json");
await run(dotnet, [dll, "--checker-pool-partitions", input, candidateOutput]);
assert.deepEqual(JSON.parse(await readFile(candidateOutput, "utf8")), JSON.parse(await readFile(referenceOutput, "utf8")));
const result = {
    timestamp: new Date().toISOString(),
    sourceFirst,
    referenceRevision,
    managed: true,
    cases: cases.length,
    assignments: cases.reduce((n, row) => n + row.weights.length, 0),
    inputSha256: inputHash,
    oracleSha256: oracleHash,
    candidateSha256: sha256(await readFile(dll)),
    compilerSha256: sha256(await readFile(path.join(path.dirname(dll), "TypeScript.Compiler.dll"))),
    resultSha256: sha256(await readFile(candidateOutput)),
};
await json(
    path.join(
        root,
        sourceFirst ? "csharp/compatibility/evidence/phase4-checker-partitions-source.json"
            : "csharp/compatibility/evidence/phase4-checker-partitions.json",
    ),
    result,
);
console.log(result);
