import { spawn } from "node:child_process";
import { createWriteStream } from "node:fs";
import {
    copyFile,
    mkdir,
    readFile,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import { createInterface } from "node:readline";
import {
    json,
    output,
    referenceRevision,
    root,
    run,
    sha256,
} from "./common.mjs";
import { sameDiagnostics } from "./compare-checker-diagnostics.mjs";
import { prepareCheckerCorpusOracle } from "./prepare-checker-corpus-oracle.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const tag = option("--tag", "current");
if (!/^[a-z0-9-]+$/.test(tag)) throw Error("Invalid corpus tag");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const dotnet = option("--dotnet", process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, "dotnet.exe") : "dotnet");
const managed = !process.argv.includes("--native");
const includeDiagnosticDetails = process.argv.includes("--diagnostics");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc");
const oracle = path.join(output, "semantic-corpus-oracle.exe");
const mapperFixture = path.join(output, "content-mapper-fixture.exe");
let mapperFixtureInfo;
async function prepareMapperFixture() {
    if (mapperFixtureInfo) return;
    const fixtureSource = path.join(root, "csharp/oracle/content-mapper-fixture/main.go");
    const sourceSha256 = sha256(await readFile(fixtureSource));
    const manifest = path.join(output, "content-mapper-fixture.json");
    try {
        const cached = JSON.parse(await readFile(manifest, "utf8"));
        if (
            cached.referenceRevision === referenceRevision && cached.sourceSha256 === sourceSha256 && cached.go === go
            && cached.executableSha256 === sha256(await readFile(mapperFixture))
        ) {
            mapperFixtureInfo = cached;
            return;
        }
    }
    catch (error) {
        if (error.code !== "ENOENT") throw error;
    }
    const fixtureDirectory = path.join(source, "cmd/csharp-content-mapper-fixture");
    await mkdir(fixtureDirectory, { recursive: true });
    await copyFile(fixtureSource, path.join(fixtureDirectory, "main.go"));
    await run(go, ["-C", source, "build", "-o", mapperFixture, "./cmd/csharp-content-mapper-fixture"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
    mapperFixtureInfo = { referenceRevision, sourceSha256, go, executableSha256: sha256(await readFile(mapperFixture)) };
    await json(manifest, mapperFixtureInfo);
}
const native = option("--candidate", path.join(output, "phase4-native/TypeScript.Compatibility.exe"));
const managedDirectory = option("--managed-directory", path.join(root, "csharp/tests/TypeScript.Compatibility/bin/Release/net11.0"));
const dll = path.join(managedDirectory, "TypeScript.Compatibility.dll");
const compilerDll = path.join(managedDirectory, "TypeScript.Compiler.dll");
const candidate = managed ? dotnet : native;
const candidateArgs = managed ? [dll] : [];
const reuseSyntax = !process.argv.includes("--fresh");
const directory = path.join(output, `semantic-corpus-${tag}`);
const referenceDirectory = option("--reference-directory", directory);
await mkdir(directory, { recursive: true });
const filter = option("--filter", "");
const mode = option("--mode", "both");
if (!["single", "default", "both"].includes(mode)) throw Error("Invalid corpus mode");
const modes = mode === "both" ? ["single", "default"] : [mode];
if (!process.argv.includes("--no-build")) {
    await prepareCheckerCorpusOracle(source);
    await copyFile(path.join(root, "csharp/oracle/semantic-corpus/bridge_test.go"), path.join(source, "internal/testrunner/csharp_semantic_corpus_test.go"));
    await run(go, ["-C", source, "test", "-c", "-o", oracle, "./internal/testrunner"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
    await run(
        dotnet,
        managed ? ["build", "tests/TypeScript.Compatibility", "-c", "Release", "--no-restore"]
            : ["publish", "tests/TypeScript.Compatibility", "-c", "Release", "-r", "win-x64", "-p:IlcInstructionSet=native", "-p:RestoreLockedMode=true", "-o", path.dirname(native)],
        { cwd: path.join(root, "csharp"), env: { ...process.env, DOTNET_ROOT: path.dirname(dotnet), PATH: path.dirname(dotnet) + path.delimiter + process.env.PATH } },
    );
}
const canonical = value =>
    Array.isArray(value) ? value.map(canonical) : value !== null && typeof value === "object"
        ? Object.fromEntries(Object.keys(value).sort().map(k => [k, canonical(value[k])])) : value;
const same = (a, b) => JSON.stringify(canonical(a)) === JSON.stringify(canonical(b));
const compareCodes = (a, b) => a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : a[1] - b[1];
let failed = false;
for (const mode of modes) {
    const modeDirectory = path.join(directory, mode);
    const inputDirectory = path.join(referenceDirectory, mode);
    await mkdir(modeDirectory, { recursive: true });
    await mkdir(inputDirectory, { recursive: true });
    if (!process.argv.includes("--reuse-reference")) {
        const code = await new Promise((resolve, reject) => {
            const child = spawn(oracle, ["-test.run=^TestCSharpSemanticCorpus$", `-test.parallel=${option("--reference-jobs", "1")}`], {
                cwd: source,
                windowsHide: true,
                env: { ...process.env, CSHARP_CORPUS_OUTPUT: inputDirectory, CSHARP_CORPUS_FILTER: filter, TS_TEST_PROGRAM_SINGLE_THREADED: mode === "single" ? "true" : "false" },
            });
            const log = createWriteStream(path.join(inputDirectory, "reference.log"));
            child.stdout.pipe(log, { end: false });
            child.stderr.pipe(log, { end: false });
            child.on("error", reject);
            child.on("close", code => {
                log.end();
                resolve(code);
            });
        });
        await json(path.join(inputDirectory, "reference-run.json"), { filter, mode, jobs: Number(option("--reference-jobs", "1")), exitCode: code, oracleSha256: sha256(await readFile(oracle)) });
    }
    const referenceRun = JSON.parse(await readFile(path.join(inputDirectory, "reference-run.json"), "utf8"));
    if (referenceRun.filter !== filter || referenceRun.mode !== mode) throw Error("Reference reuse does not match requested scope");
    if (typeof referenceRun.oracleSha256 !== "string" || !/^[a-f0-9]{64}$/.test(referenceRun.oracleSha256)) {
        throw Error("Cached reference is missing its oracle hash; restore it from recorded evidence or regenerate that reference scope");
    }
    const inputText = await readFile(path.join(inputDirectory, "cases.jsonl"), "utf8");
    const cases = inputText.trim().split(/\r?\n/).filter(Boolean).map(JSON.parse).sort((a, b) => a.name.localeCompare(b.name, "en"));
    let referenceInputCorrectionsSha256;
    try {
        const corrections = await readFile(path.join(inputDirectory, "input-corrections.jsonl"), "utf8");
        referenceInputCorrectionsSha256 = sha256(Buffer.from(corrections));
        for (const line of corrections.trim().split(/\r?\n/).filter(Boolean)) {
            const replacement = JSON.parse(line);
            const index = cases.findIndex(c => c.name === replacement.name);
            if (index < 0) throw Error(`Unknown corrected reference case: ${replacement.name}`);
            const { files: originalFiles, ...original } = cases[index];
            const { files: replacementFiles, ...corrected } = replacement;
            if (!same(original, corrected)) throw Error(`Reference input correction changes expectations: ${replacement.name}`);
            cases[index] = replacement;
        }
    }
    catch (error) {
        if (error.code !== "ENOENT") throw error;
    }
    if (!cases.length) throw Error("The reference exporter produced no cases");
    const inventory = JSON.parse(await readFile(path.join(inputDirectory, "inventory.json"), "utf8"));
    const planned = JSON.parse(await readFile(path.join(inputDirectory, "plan.json"), "utf8"));
    const completedNames = new Set(cases.map(c => c.name)), completedSources = new Set(cases.map(c => c.source));
    if (completedNames.size !== cases.length) throw Error("Duplicate reference configuration names");
    const missingFiles = inventory.filter(f => !completedSources.has(f));
    const missingConfigurations = planned.filter(c => !completedNames.has(c.name));
    let ready = cases.filter(c => c.status === "ready");
    const availableConfigurations = ready.length;
    let selectionSha256;
    if (option("--cases")) {
        const selectionText = await readFile(option("--cases"), "utf8");
        const names = new Set(JSON.parse(selectionText));
        const available = new Set(ready.map(c => c.name));
        for (const name of names) if (!available.has(name)) throw Error(`Unknown active corpus configuration: ${name}`);
        ready = ready.filter(c => names.has(c.name));
        selectionSha256 = sha256(Buffer.from(selectionText));
    }
    if (ready.some(c => c.contentMappers)) await prepareMapperFixture();
    let candidateFailures = 0, graphMismatches = 0, diagnosticMismatches = 0, codeMatches = 0, processed = 0;
    let detailedDiagnosticMatches = 0;
    const errors = new Map();
    const results = createWriteStream(path.join(modeDirectory, "candidate.jsonl"));
    const differences = createWriteStream(path.join(modeDirectory, "differences.jsonl"));
    function accept(actual) {
        const expected = ready[processed++];
        if (actual.name !== expected.name) throw Error("Corpus response identity mismatch");
        results.write(JSON.stringify(actual) + "\n");
        const graph = same(actual.sources, expected.sources ?? []);
        const expectedCodes = expected.semanticDiagnostics.map(d => [d.file, d.code]).sort(compareCodes);
        const codes = same(actual.semanticCodes, expectedCodes) && same(actual.globalCodes, expected.globalDiagnostics.map(d => d.code).sort((a, b) => a - b));
        const details = includeDiagnosticDetails && actual.status === "checked"
            && sameDiagnostics(actual.semanticDiagnostics, expected.semanticDiagnostics)
            && sameDiagnostics(actual.globalDiagnostics, expected.globalDiagnostics);
        if (details) detailedDiagnosticMatches++;
        if (!graph) graphMismatches++;
        if (actual.status !== "checked") {
            candidateFailures++;
            const key = actual.stage + ": " + actual.error;
            errors.set(key, (errors.get(key) ?? 0) + 1);
        }
        else if (!codes) diagnosticMismatches++;
        if (graph && codes && actual.status === "checked") codeMatches++;
        if (!graph || !codes || actual.status !== "checked" || includeDiagnosticDetails && !details) differences.write(JSON.stringify({ name: expected.name, graph, codes, actual, expectedSources: expected.sources, expectedCodes, expectedGlobalCodes: expected.globalDiagnostics.map(d => d.code), ...includeDiagnosticDetails ? { details, expectedDiagnostics: expected.semanticDiagnostics, expectedGlobalDiagnostics: expected.globalDiagnostics } : {} }) + "\n");
        if (processed % 100 === 0 || processed === ready.length) console.log(`${mode}: ${processed}/${ready.length}; ${codeMatches} graph/code matches; ${candidateFailures} candidate failures`);
    }
    while (processed < ready.length) {
        await new Promise((resolve, reject) => {
            let stderr = "", closing = false;
            const child = spawn(candidate, [...candidateArgs, "--checker-corpus-lines", path.join(inputDirectory, "blobs"), ...reuseSyntax ? ["--reuse-syntax"] : []], {
                windowsHide: true,
                env: { ...process.env, CSHARP_CONTENT_MAPPER_FIXTURE: mapperFixtureInfo ? mapperFixture : "" },
            });
            child.stderr.on("data", bytes => {
                stderr = (stderr + bytes.toString()).slice(-65536);
            });
            child.stdin.on("error", () => {});
            child.on("error", reject);
            const lines = createInterface({ input: child.stdout });
            lines.on("line", line => {
                try {
                    accept(JSON.parse(line));
                    if (processed < ready.length) child.stdin.write(JSON.stringify({ ...ready[processed], includeDiagnosticDetails }) + "\n");
                    else {
                        closing = true;
                        child.stdin.end();
                    }
                }
                catch (error) {
                    child.stdin.end();
                    child.kill();
                    reject(error);
                }
            });
            child.on("close", code => {
                if (!closing && processed < ready.length) accept({ name: ready[processed].name, status: "failed", stage: "process", error: `Exit ${code}: ${stderr}`, sources: [], semanticCodes: [], globalCodes: [] });
                else if (code) {
                    reject(Error(`Candidate failed after its last response: ${code}: ${stderr}`));
                    return;
                }
                resolve();
            });
            child.stdin.write(JSON.stringify({ ...ready[processed], includeDiagnosticDetails }) + "\n");
        });
    }
    await Promise.all([new Promise(resolve => results.end(resolve)), new Promise(resolve => differences.end(resolve))]);
    const passed = referenceRun.exitCode === 0 && !cases.some(c => c.status === "reference-failed")
        && missingFiles.length === 0 && missingConfigurations.length === 0 && codeMatches === ready.length
        && (!includeDiagnosticDetails || detailedDiagnosticMatches === ready.length);
    const summary = {
        timestamp: new Date().toISOString(),
        referenceRevision,
        mode,
        filter,
        managed,
        reuseSyntax,
        referenceJobs: referenceRun.jobs,
        scope: "Original compiler test program graphs and semantic/global diagnostics in the selected checker concurrency mode; checker query APIs are validated separately",
        referenceExitCode: referenceRun.exitCode,
        exportedConfigurations: cases.length,
        readyConfigurations: ready.length,
        availableConfigurations,
        selectionSha256,
        referenceInventoryFiles: inventory.length,
        plannedConfigurations: planned.length,
        referenceMissingFiles: missingFiles,
        referenceMissingConfigurations: missingConfigurations,
        referenceSkipped: cases.filter(c => c.status === "reference-skipped").length,
        referenceFailed: cases.filter(c => c.status === "reference-failed").length,
        candidateFailures,
        graphMismatches,
        diagnosticMismatches,
        graphAndCodeMatches: codeMatches,
        ...includeDiagnosticDetails ? { detailedDiagnosticMatches, detailedDiagnosticMismatches: ready.length - detailedDiagnosticMatches, detailedComparison: "Complete diagnostic records, including arguments, ranges, category, message key, chains and related information; top-level collection order normalized" } : {},
        errorGroups: [...errors].sort((a, b) => b[1] - a[1]).map(([error, count]) => ({ error, count })),
        inputSha256: sha256(Buffer.from(inputText)),
        ...referenceInputCorrectionsSha256 ? { referenceInputCorrectionsSha256 } : {},
        oracleSha256: referenceRun.oracleSha256,
        candidateSha256: sha256(await readFile(managed ? dll : native)),
        ...managed ? { compilerSha256: sha256(await readFile(compilerDll)) } : {},
        ...mapperFixtureInfo ? { mapperFixture: mapperFixtureInfo } : {},
        candidateOutputSha256: sha256(await readFile(path.join(modeDirectory, "candidate.jsonl"))),
        fullSemanticCorpusGateComplete: passed && includeDiagnosticDetails && filter === "" && ready.length === availableConfigurations,
    };
    await json(path.join(modeDirectory, "summary.json"), summary);
    if (option("--record")) await json(path.join(root, `csharp/compatibility/evidence/${option("--record")}-${mode}.json`), summary);
    console.log(JSON.stringify(summary, null, 2));
    failed ||= !passed;
}
if (failed) process.exitCode = 1;
