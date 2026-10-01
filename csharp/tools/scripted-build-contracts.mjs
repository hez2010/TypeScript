import assert from "node:assert/strict";
import path from "node:path";

// Allocation identities are not serialized symbol identities. Restrict this
// normalization to the internal iterator name in the duplicate-spread diagnostic.
const iteratorIdentity = /^\ufffd@iterator@\d+$/;
function normalizeDiagnostic(diagnostic) {
    if (diagnostic.code === 2783 && iteratorIdentity.test(diagnostic.messageArgs?.[0]))
        diagnostic.messageArgs[0] = "\ufffd@iterator@<id>";
    for (const child of [...diagnostic.messageChain ?? [], ...diagnostic.relatedInformation ?? []]) normalizeDiagnostic(child);
}

export function normalize(value, input) {
    const copy = structuredClone(value);
    // A source-written string with this spelling must remain observable.
    const internalNames = !Object.values(input?.files ?? {}).some(file => file.dataBase64
        && Buffer.from(file.dataBase64, "base64").includes(Buffer.from("\ufffd@iterator@")));
    if (Array.isArray(copy)) for (const cycle of copy) {
        if (typeof cycle.stdout === "string") {
            cycle.stdout = cycle.stdout.replace(/\d{2}:\d{2}:\d{2} [AP]M(?= - |\x1b\[0m\] )/g, "<time>");
            if (internalNames) cycle.stdout = cycle.stdout.replace(/(TS2783: (?:\x1b\[[0-9;]*m)*'\ufffd@iterator@)\d+(?=')/g, "$1<id>");
        }
        if (internalNames) for (const write of cycle.writes ?? [])
            for (const field of ["semanticDiagnosticsPerFile", "emitDiagnosticsPerFile"])
                for (const entry of write.buildInfo?.[field] ?? [])
                    if (Array.isArray(entry)) for (const diagnostic of entry[1]) normalizeDiagnostic(diagnostic);
    }
    return copy;
}

// These original cases still execute and remain failures in the full replay.
// Phase 6 additionally checks every non-profiling effect against a fresh control;
// implementing these profile/trace consumers is explicitly Phase 8 work.
const deferred = new Map([
    ["TestTscModuleResolution/tsc/package_json_scope", "--extendedDiagnostics"],
    ["TestTscExtends/tsbuild/resolves_the_symlink_path", "--extendedDiagnostics"],
    ["TestTscExtends/tsc/resolves_the_symlink_path", "--extendedDiagnostics"],
    ["TestGenerateTrace/tsc/generateTrace_with_multiple_files_and_complex_types", "--generateTrace"],
    ["TestGenerateTrace/tsc/generateTrace_generates_types_file", "--generateTrace"],
]);

export function profilingControl(input) {
    const flag = deferred.get(input.name);
    if (!flag) return null;
    const control = structuredClone(input);
    control.name += " [profiling-disabled control]";
    for (const step of control.steps) {
        const index = step.args?.indexOf(flag) ?? -1;
        assert.ok(index >= 0, `deferred flag is explicit: ${input.name}`);
        step.args.splice(index, flag === "--generateTrace" ? 2 : 1);
    }
    return { original: input.name, flag, input: control };
}

const statistics = ["Files", "Lines", "Identifiers", "Symbols", "Types", "Instantiations", "Memory used", "Memory allocs",
    "Config time", "BuildInfo read time", "Parse time", "Bind time", "Check time", "Emit time", "Changes compute time", "Total time"];

export function withoutProfiling(records, input, flag) {
    const copy = normalize(records, input);
    for (let index = 0; index < copy.length; index++) {
        const cycle = copy[index];
        if (flag === "--generateTrace") {
            const args = input.steps[index].args;
            const directory = args[args.indexOf(flag) + 1];
            const files = ["legend.json", "trace.json", "types_0.json"].map(file => path.posix.join(directory, file));
            const removed = cycle.writes.filter(write => files.includes(write.path));
            // The reference produces all three consumers; the candidate has none yet.
            assert.ok(removed.length === 0 || removed.length === files.length, "complete trace artifact group");
            cycle.writes = cycle.writes.filter(write => !files.includes(write.path));
        } else {
            const start = cycle.stdout.startsWith("Files:") ? 0 : cycle.stdout.lastIndexOf("\nFiles:") + 1;
            if (start === 0 && !cycle.stdout.startsWith("Files:")) continue;
            const rows = cycle.stdout.slice(start).trimEnd().split("\n");
            const labels = input.suite === "tsbuild" ? [...statistics, "Projects in scope", "Projects built", "Timestamps only updates",
                ...statistics.map(label => `Aggregate ${label}`)] : statistics;
            assert.equal(rows.length, labels.length, "statistics section contains only the declared fields");
            for (let row = 0; row < rows.length; row++) {
                const match = /^(.+?):\s+(\d+(?:\.\d+)?[Ks]?)$/.exec(rows[row]);
                assert.ok(match, `statistics row: ${rows[row]}`);
                assert.equal(match[1], labels[row]);
            }
            cycle.stdout = cycle.stdout.slice(0, start);
        }
    }
    return copy;
}

// Guard the boundary: source excerpts, emitted bytes and unrelated arguments
// cannot be changed merely because they contain a compiler-looking string.
const sample = [{ stdout: "a.ts(1,1): error TS2783: '\ufffd@iterator@123' is specified more than once.\nsource '\ufffd@iterator@123'\n",
    writes: [{ textBase64: Buffer.from("\ufffd@iterator@123").toString("base64"), buildInfo: { fileInfos: [{ version: "\ufffd@iterator@123" }],
        semanticDiagnosticsPerFile: [[1, [{ code: 2783, messageArgs: ["\ufffd@iterator@123"] }, { code: 2322, messageArgs: ["\ufffd@iterator@123"] }]]] } }] }];
const normalized = normalize(sample);
assert.ok(normalized[0].stdout.includes("TS2783: '\ufffd@iterator@<id>'"));
assert.ok(normalized[0].stdout.includes("source '\ufffd@iterator@123'"));
assert.equal(normalize([{ stdout: "TS2783: \x1b[0m'\ufffd@iterator@123'" }])[0].stdout, "TS2783: \x1b[0m'\ufffd@iterator@<id>'");
assert.equal(normalized[0].writes[0].textBase64, sample[0].writes[0].textBase64);
assert.deepEqual(normalized[0].writes[0].buildInfo.fileInfos, sample[0].writes[0].buildInfo.fileInfos);
assert.equal(normalized[0].writes[0].buildInfo.semanticDiagnosticsPerFile[0][1][1].messageArgs[0], "\ufffd@iterator@123");
assert.deepEqual(normalize(sample, { files: { "a.ts": { dataBase64: Buffer.from("'\ufffd@iterator@123'").toString("base64") } } }), sample);
