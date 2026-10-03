import assert from "node:assert/strict";
import { readFile, writeFile } from "node:fs/promises";
import { run, referenceRevision, sha256 } from "./common.mjs";

const directory = "built/csharp/phase7-validation";
const replay = JSON.parse(await readFile(`${directory}/original-projects-final/summary.json`));
const events = (await readFile(`${directory}/original-projects-final/reference-tests.jsonl`, "utf8")).trim().split(/\r?\n/).map(JSON.parse);
const files = (await run("git", ["ls-tree", "-r", "--name-only", referenceRevision, "tsc/internal/project"]))
    .split("\n").filter(file => file.endsWith("_test.go"));
const inventory = [], sourceHashes = {};
const tests = "csharp/tests/TypeScript.Compatibility/";
const snapshotCases = new Set(replay.cases.map(row => row.name.split("/")[0]));
const disposition = name => {
    if (snapshotCases.has(name)) return { mode: "original snapshot replay", candidate: [tests + "ProjectReplay.cs", tests + "ProjectTests.cs"],
        contract: "Replay every captured filesystem/configuration/editor transition and compare projects, program identity, source graphs, default ownership and diagnostic publications. Direct Go refcount/cache-layout assertions are replaced by the managed ownership checks." };
    if (name.startsWith("TestCheckerPool")) return { mode: "managed lease contracts", candidate: [tests + "ProjectTests.cs", tests + "ProjectTests.CheckerPool.cs"],
        contract: "Dedicated query/diagnostic/API ownership, request and file affinity, contention, cancellation, discard, idle cleanup and global diagnostics. C# uses explicit disposable request scopes; timer scheduling and runtime-private slot/map representation are not wire contracts." };
    if (["TestDiscoverTypings", "TestValidatePackageName", "TestInstallNpmPackages"].includes(name))
        return { mode: "original operation replay", candidate: [tests + "TypingsTests.cs"], contract: "Captured original discovery, package-name validation and npm batching inputs/results." };
    if (name === "TestProcessChanges") return { mode: "original operation replay", candidate: [tests + "ProjectReplay.cs"], contract: "Original overlay notification batches, documents and change summaries." };
    if (["TestOverlayFSFileSystem", "TestSnapshotFSBuilderCachesReturnedSourceHandle", "TestSnapshotFSBuilder", "TestSnapshotFS", "TestSourceFS",
        "TestAutoImportBuilderFS", "TestRealpathAliasLifecycle", "TestExpandAndFilterWatchEvents"].includes(name))
        return { mode: "managed filesystem contracts", candidate: [tests + "ProjectTests.cs", tests + "ProjectTests.FileSystem.cs", tests + "LspWatchTests.cs"],
            contract: "Snapshot-stable handles, overlays, directory merges, concurrent reads, read observation, deletion and alias invalidation, unretained auto-import reads and live watcher effects. Go builder maps and Finalize flags have no direct managed equivalent." };
    if (["TestContentMappedParseCacheBundleLifetime", "TestContentMappedParseCacheKeyReconstruction", "TestParseCacheBindsBeforePublishing"].includes(name))
        return { mode: "managed cache ownership", candidate: [tests + "ContentMapperTests.cs", tests + "ProjectTests.cs", tests + "ProjectTests.FileSystem.cs"],
            contract: "Bound syntax publication, shared mapped canonical/supplemental graphs, unchanged mapper identity and collection of retired graphs. C# retains mapped bundles through programs and ordinary syntax through weak cache entries, without Go cache-key reconstruction/refcounts." };
    if (["TestGetPathComponentsForWatching", "TestNilWatchedFilesClone", "TestUpdateWatchTimeoutAndRollback"].includes(name))
        return { mode: "watch contract comparisons", candidate: [tests + "LspWatchTests.cs", "csharp/compatibility/evidence/phase7-lsp-watching.json"],
            contract: "Watcher path/pattern generation, immutable registrations, cancellation and rollback. Reference slice-aliasing and rollback defects have independent recorded controls." };
    if (name === "TestProjectIDNarrowing") return { mode: "project identity contracts", candidate: [tests + "ProjectTests.cs", "csharp/tools/api-session.mjs", "csharp/tools/api-lsp-cases.mjs"],
        contract: "C# uses ProjectKind rather than Go ID wrapper types. Positive signed/zero-padded synthetic aliases preserve lookup, mutation and API ownership; invalid numeric suffixes remain ordinary IDs." };
    if (name === "TestSyncMapProxyFor") return { mode: "runtime-specific structure", candidate: [tests + "ProjectTests.cs"],
        contract: "Go dirty.SyncMap proxy mutation is replaced by owned snapshot dictionaries and serialized publication. Managed checks cover simultaneous readers, independent branches, canceled updates and retained old graphs." };
    if (name === "TestQueue") return { mode: "runtime-specific structure", candidate: [tests + "LspApiTests.cs", tests + "TypingsTests.cs", tests + "TelemetryTests.cs"],
        contract: "Go's background queue is replaced by Task/Channel workers with linked cancellation and joined disposal. Session checks cover pending work, nested callbacks and shutdown; there is no standalone translated Go queue." };
    if (["TestLogTree", "TestLogTreeImplementsLogger"].includes(name)) return { mode: "runtime-specific structure", candidate: ["csharp/tools/lsp-project-services.mjs"],
        contract: "One original test is empty and one is a Go interface conformance check. Managed logging is checked through every LSP logVerbosity value and the development-service contracts." };
    throw Error(`Unclassified project test: ${name}`);
};
for (const file of files) {
    const source = await run("git", ["show", `${referenceRevision}:${file}`]); sourceHashes[file] = sha256(source);
    for (const match of source.matchAll(/^func (Test\w+)\(t \*testing\.T\)/gm)) {
        const name = match[1], children = events.filter(event => event.Test === name || event.Test?.startsWith(name + "/"));
        assert(children.some(event => event.Test === name && event.Action === "pass"), `${name} did not pass in the reference`);
        assert(!children.some(event => event.Action === "fail"), `${name} has a failed subtest`);
        const coverage = disposition(name);
        for (const path of coverage.candidate) await readFile(path);
        inventory.push({ name, source: file, line: source.slice(0, match.index).split("\n").length,
            originalPassed: children.filter(event => event.Action === "pass").length,
            originalSkipped: children.filter(event => event.Action === "skip").map(event => event.Test), ...coverage });
    }
}
assert.equal(inventory.length, 97);
assert.equal(replay.mismatches.length, 0);
const evidence = { referenceRevision, inventoryCount: inventory.length, sourceHashes,
    boundaries: [
        "This is an exhaustive disposition inventory, not a claim that Go's private cache/refcount/queue representations were translated assertion for assertion.",
        "Every original test function ran with bundled libraries enabled; the existing TestATA/deduplicate_from_local_@types_packages subtest remains skipped.",
        "Snapshot replay retains the Go single-file-clone/update-counter architecture distinction and two independently verified root-order cases. It preserves program identities, complete source text/kinds, project ownership and diagnostic publications.",
        "Validation uses Release CoreCLR and the default GC. No performance measurement or NativeAOT execution is part of this audit.",
    ],
    original: { passed: inventory.reduce((sum, row) => sum + row.originalPassed, 0), skipped: inventory.flatMap(row => row.originalSkipped) },
    replay, inventory: inventory.sort((a, b) => a.source.localeCompare(b.source) || a.line - b.line),
};
const target = "csharp/compatibility/evidence/phase7-project-test-inventory.json";
if (process.argv.includes("--record")) await writeFile(target, JSON.stringify(evidence, null, 2) + "\n");
else assert.deepEqual(JSON.parse(await readFile(target)), evidence);
console.log(JSON.stringify({ tests: inventory.length, modes: Object.fromEntries(Object.entries(Object.groupBy(inventory, row => row.mode)).map(([mode, rows]) => [mode, rows.length])),
    original: evidence.original, snapshotSessions: replay.cases.length, transitions: replay.transitions, classifiedOrderDifferences: replay.classifiedOrderDifferences }));
