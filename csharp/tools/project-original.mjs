import assert from "node:assert/strict";
import { isDeepStrictEqual } from "node:util";
import { spawn } from "node:child_process";
import { mkdir, readFile, writeFile, rm } from "node:fs/promises";
import path from "node:path";
import { root, output, run, json, sha256, referenceRevision } from "./common.mjs";
import { comparableProjectRoots, unorderedProjectRoots } from "./project-response-comparison.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const directory = path.resolve(option("--directory", path.join(output, "phase7-validation/original-projects")));
await mkdir(directory, { recursive: true });
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
assert.equal(reference.referenceRevision, referenceRevision);
const source = path.join(output, reference.sourceRelativePath, "tsc");
const instrumented = [];
const autoImports = process.argv.includes("--autoimports");
const fixture = path.join(output, "content-mapper-fixture.exe");
const fixtureInfo = JSON.parse(await readFile(path.join(output, "content-mapper-fixture.json"), "utf8"));
assert.equal(fixtureInfo.referenceRevision, referenceRevision);
assert.equal(fixtureInfo.executableSha256, sha256(await readFile(fixture)));
const originals = new Map();
const reuseRecording = process.argv.includes("--reuse-recording");
async function edit(relative, transform) {
    relative = path.posix.normalize(relative);
    const original = await run("git", ["show", `${referenceRevision}:tsc/${relative}`]);
    const changed = transform(original);
    assert.notEqual(changed, original, `No instrumentation applied to ${relative}`);
    originals.set(relative, original + "\n");
    await writeFile(path.join(source, relative), changed + "\n");
    instrumented.push({ file: relative, originalSha256: sha256(original), instrumentedSha256: sha256(changed) });
}
if (!reuseRecording) {
try {
await edit("internal/project/snapshot.go", original => original
    .replace("\tstore := s.host\n", "\tcsharpAfterClone := csharpRecordBeforeClone(s, change, overlays)\n\tstore := s.host\n")
    .replace("if store.options.LoggingEnabled && sessionLogger != nil {\n\t\tlogger =", "if store.options.LoggingEnabled && sessionLogger != nil || csharpRecordLogs() {\n\t\tlogger =")
    .replace("\treturn newSnapshot\n", "\tcsharpAfterClone(newSnapshot)\n\treturn newSnapshot\n"));
for (const name of autoImports ? ["../ls/autoimport/registry_test.go", "../testutil/autoimporttestutil/fixtures.go"] : ["snapshot_test.go", "session_test.go", "projectcollectionbuilder_test.go", "configfilechanges_test.go",
    "projectlifetime_test.go", "projectcollectiondefaultproject_test.go", "projectreferencesprogram_test.go", "untitled_test.go",
    "bulkcache_test.go", "customconfigfilename_test.go", "project_test.go", "contentmapper_test.go", "refcountcache_test.go", "extendedconfigcache_test.go", "ata/ata_test.go"]) {
    await edit(`internal/project/${name}`, original => {
        // Only instrument test bodies, keeping benchmarks and production behavior untouched.
        return original.replace(/(^func (?:Test\w+|Setup\w+)\(t \*testing\.T[^\n]*[\s\S]*?)(?=^func |$(?![\s\S]))/gm, body => {
            if (["snapshot_test.go", "refcountcache_test.go", "extendedconfigcache_test.go"].includes(name))
                body = body.replace("setup := func(files", "setup := func(t *testing.T, files").replaceAll("setup(", "setup(t, ");
            body = body.replace(/(?<!\.)NewSession\(/g, "CSharpNewSession(t.Name(), ").replaceAll("project.NewSession(", "project.CSharpNewSession(t.Name(), ");
            for (const setup of ["Setup", "SetupWithOptions", "SetupWithTypingsInstaller", "SetupWithOptionsAndTypingsInstaller"])
                body = body.replaceAll(`projecttestutil.${setup}(`, `projecttestutil.CSharp${setup}(t, `);
            if (body.startsWith("func TestContentMapperInferredProjectSurvivesTypingsInstall(")) {
                const packages = body.match(/PackageToFile: (map\[string\]string\{[\s\S]*?\n\t\t\})/)[1];
                body = body.replace(/(session := project.CSharpNewSession\(t.Name\(\), init\))/, `$1\n\tprojecttestutil.CSharpRecordNpm(t, session, utils, &projecttestutil.TypingsInstallerOptions{PackageToFile: ${packages}})`);
            }
            return body;
        });
    });
}
for (const [input, target] of [["snapshot_record.go", "internal/project/csharp_snapshot_record.go"],
    ["snapshot_setup.go", "internal/testutil/projecttestutil/csharp_snapshot_setup.go"],
    ["project_diagnostics_test.go", "internal/project/csharp_project_diagnostics_test.go"],
    ...(autoImports ? [["autoimport_test.go", "internal/ls/autoimport/csharp_autoimport_test.go"]] : [])]) {
    const bytes = await readFile(path.join(root, "csharp/oracle/project", input));
    await writeFile(path.join(source, target), bytes); instrumented.push({ file: target, instrumentedSha256: sha256(bytes) });
}
// Exercise the reference publication algorithm against each captured snapshot pair. The
// helper is copied verbatim except for routing its publication callback to the recorder.
const sessionSource = await run("git", ["show", `${referenceRevision}:tsc/internal/project/session.go`]);
const publication = sessionSource.slice(sessionSource.indexOf("func (s *Session) publishProgramDiagnostics("), sessionSource.indexOf("func shouldPublishProgramDiagnostics("))
    .replace("publishProgramDiagnostics(oldSnapshot *Snapshot, newSnapshot *Snapshot)", "csharpProjectDiagnosticChanges(oldSnapshot *Snapshot, newSnapshot *Snapshot, publish func(context.Context, string, []*ast.Diagnostic, *lsconv.Converters))")
    .replaceAll("s.publishProjectDiagnostics(", "publish(");
const diagnosticHelper = `package project
import (
 "context"
 "github.com/microsoft/TypeScript/tsc/internal/ast"
 "github.com/microsoft/TypeScript/tsc/internal/collections"
 "github.com/microsoft/TypeScript/tsc/internal/ls/lsconv"
)
${publication}`;
await writeFile(path.join(source, "internal/project/csharp_diagnostic_changes.go"), diagnosticHelper);
instrumented.push({ file: "internal/project/csharp_diagnostic_changes.go", instrumentedSha256: sha256(diagnosticHelper) });
const recording = path.join(directory, "original-operations.jsonl");
{
    await writeFile(recording, "");
    const tests = await run(go, ["-C", source, "test", "-json", "-tags=bundled", ...(autoImports ? ["./internal/ls/autoimport"] : ["./internal/project/..."]), "-run",
        option("--record-match", autoImports ? "^(TestRegistryLifecycle|TestContentMappedNodeModulesFileUsesProjectBucket|TestHiddenDirectoriesInNodeModules|TestAutoImportEntrypointDirectorySearch|TestUpdateIndexesConcurrentMapSafety|TestCSharpAutoImportRealpaths|TestGetPackageRealpathFuncs_.*|TestAliasResolverGetDiagnosticsDoesNotPanic)$" : "^Test"), `-count=${option("--record-count", "1")}`],
        { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local", CSHARP_PROJECT_RECORD: recording,
            CSHARP_PROJECT_LOGS: process.argv.includes("--capture-logs") ? "1" : "" } });
    await writeFile(path.join(directory, "reference-tests.jsonl"), tests + "\n");
}
} finally {
    for (const entry of instrumented) {
        if (originals.has(entry.file)) await writeFile(path.join(source, entry.file), originals.get(entry.file));
        else await rm(path.join(source, entry.file), { force: true });
    }
}
} else {
    const previous = JSON.parse(await readFile(path.join(directory, "summary.json")));
    assert.equal(previous.referenceRevision, referenceRevision);
    instrumented.push(...previous.instrumented);
}
const recording = path.join(directory, "original-operations.jsonl");
const recorded = (await readFile(recording, "utf8")).trim().split(/\r?\n/).map(JSON.parse);
const rows = recorded.filter(row => !row.observation);
const key = row => `${row.name}:${row.session}`;
const npmCalls = new Map(recorded.filter(row => row.observation === "npm").map(row => [key(row), row.calls]));
const grouped = Map.groupBy(rows, key);
let cases = [];
for (const rows of grouped.values()) {
    const steps = [], expected = [];
    let files = rows[0].files, links = rows[0].symlinks ?? {}, directories = rows[0].directories ?? [];
    for (const row of rows) {
        const edits = {};
        for (const name of new Set([...Object.keys(files), ...Object.keys(row.files)]))
            if (files[name] !== row.files[name]) edits[name] = row.files[name] ?? null;
        const symlinks = {}, nextLinks = row.symlinks ?? {};
        for (const name of new Set([...Object.keys(links), ...Object.keys(nextLinks)]))
            if (links[name] !== nextLinks[name]) symlinks[name] = nextLinks[name] ?? null;
        assert.equal(row.snapshot, steps.length + 1);
        const nextDirectories = row.directories ?? [];
        steps.push({ ...row.params, base: row.base, edits, symlinks, queries: row.queries ?? [],
            removeDirectories: directories.filter(name => !nextDirectories.includes(name)), createDirectories: nextDirectories.filter(name => !directories.includes(name)) });
        expected.push(row.expected); files = row.files; links = nextLinks; directories = nextDirectories;
    }
    if (rows[0].npm) expected.push({ npmCalls: npmCalls.get(key(rows[0])) ?? [] });
    cases.push({ name: rows[0].name, session: rows[0].session, cwd: rows[0].cwd, caseSensitive: rows[0].caseSensitive,
        files: rows[0].files, symlinks: rows[0].symlinks ?? {}, directories: rows[0].directories ?? [], typingsLocation: rows[0].typingsLocation, npm: rows[0].npm,
        observeProgramUpdates: true, observePushDiagnostics: true, pushDiagnostics: rows[0].pushDiagnostics, encoding: rows[0].encoding,
        runExternalCode: rows[0].runExternalCode, mapperFixture: fixture, steps, expected });
}
cases.sort((a, b) => a.name.localeCompare(b.name) || a.session - b.session);
const filter = option("--filter", ""); if (filter) cases = cases.filter(input => new RegExp(filter).test(input.name));
await json(path.join(directory, "inputs.json"), cases.map(({ expected, ...input }) => input));
const managed = option("--managed-directory", path.join(output, "phase7-build/bin/TypeScript.Compatibility/release"));
const dll = path.join(managed, "TypeScript.Compatibility.dll");
const child = spawn(dotnet, [dll, "--project-lines"], { cwd: root, windowsHide: true });
let stdout = "", stderr = "";
child.stdout.on("data", data => stdout += data); child.stderr.on("data", data => stderr += data);
child.stdin.on("error", () => {}); child.stdin.end(cases.map(({ expected, ...input }) => JSON.stringify(input)).join("\n") + "\n");
const code = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
await writeFile(path.join(directory, "candidate.stdout"), stdout); await writeFile(path.join(directory, "candidate.stderr"), stderr);
assert.equal(code, 0, stderr);
const results = stdout.trim().split(/\r?\n/).map(JSON.parse); assert.equal(results.length, cases.length);
await json(path.join(directory, "candidate.json"), results);
await json(path.join(directory, "reference.json"), cases.map(input => input.expected));
const mismatches = [];
let classifiedOrderDifferences = 0;
function normalize(value, name, reference) {
    if (value?.autoImports) value = { ...value, autoImports: value.autoImports.map(entry => Object.fromEntries(Object.entries(entry).sort(([a], [b]) => a.localeCompare(b))))
        .sort((a, b) => JSON.stringify(a).localeCompare(JSON.stringify(b))) };
    if (value?.npmCalls) return { npmCalls: value.npmCalls.map(call => ({ ...call, args: [...call.args.filter(arg => !arg.startsWith("@types/")),
        ...call.args.filter(arg => arg.startsWith("@types/")).sort()] })) };
    if (!value?.projects) return value;
    // Go's single-file clone is an implementation distinction: the C# builder shares bound
    // syntax and rebuilds resolution. Both paths preserve the same file-name classification.
    value = comparableProjectRoots(value, name);
    return { ...value, projects: value.projects.map(({ updateKind, ...project }) => ({ ...project,
        // Other tests can build a project twice within one snapshot. This internal counter
        // then describes only Go's last build, rather than the transition being replayed.
        ...(name.startsWith("TestProjectProgramUpdateKind/") ? {
            updateKind: reference ? updateKind === 3 ? 2 : updateKind === 0 ? 0 : 1 : updateKind
        } : {}),
    })) };
}
for (let index = 0; index < cases.length; index++) for (let step = 0; step < cases[index].expected.length; step++) {
    try {
        assert.deepEqual(normalize(results[index][step], cases[index].name, false), normalize(cases[index].expected[step], cases[index].name, true));
        if (unorderedProjectRoots.has(cases[index].name)) {
            const order = value => value.projects?.map(project => ({ roots: project.roots, sources: project.sources }));
            if (!isDeepStrictEqual(order(results[index][step]), order(cases[index].expected[step]))) classifiedOrderDifferences++;
        }
    }
    catch (error) { mismatches.push({ index, step, name: cases[index].name, difference: error.message }); }
}
const summary = { referenceRevision, instrumented, mapperFixture: fixtureInfo, compilerSha256: sha256(await readFile(path.join(managed, "TypeScript.Compiler.dll"))),
    harnessSha256: sha256(await readFile(dll)), inputsSha256: sha256(await readFile(recording)),
    cases: cases.map(test => ({ name: test.name, transitions: test.steps.length })), transitions: rows.length, classifiedOrderDifferences, mismatches };
await json(path.join(directory, "summary.json"), summary);
console.log(`${cases.filter(test => !test.name.startsWith("TestCSharp")).length} original and ${cases.filter(test => test.name.startsWith("TestCSharp")).length} authored project sessions, ${cases.reduce((sum, test) => sum + test.steps.length, 0)} transitions, ${mismatches.length} mismatches, ${classifiedOrderDifferences} classified ordering differences`);
if (mismatches.length) { console.error(mismatches.slice(0, 3)); process.exitCode = 1; }
