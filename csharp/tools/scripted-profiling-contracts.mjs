import assert from "node:assert/strict";
import path from "node:path";
import { normalize, profilingControl } from "./scripted-build-contracts.mjs";

const goRows = ["Files", "Lines", "Identifiers", "Symbols", "Types", "Instantiations", "Memory used", "Memory allocs",
    "Config time", "BuildInfo read time", "Parse time", "Bind time", "Check time", "Emit time", "Changes compute time", "Total time"];
const csRows = ["Files", "Lines", "Identifiers", "Symbols", "Types", "Instantiations", "CLR managed bytes", "CLR allocated bytes",
    "Config time", "Program time", "Parse time (summed)", "Bind time (summed)", "Check time (summed)", "Emit time (summed)",
    "BuildInfo read time", "Changes compute time", "Total time"];
const buildRows = ["Projects in scope", "Projects built", "Timestamps only updates"];
const referenceFields = new Set(["unionTypes", "intersectionTypes", "aliasTypeArguments", "typeArguments", "keyofType", "indexedAccessObjectType",
    "indexedAccessIndexType", "conditionalCheckType", "conditionalExtendsType", "conditionalTrueType", "conditionalFalseType", "substitutionBaseType",
    "constraintType", "instantiatedType", "reverseMappedSourceType", "reverseMappedMappedType", "reverseMappedConstraintType", "evolvingArrayElementType", "evolvingArrayFinalType"]);

export function inspectProfiling(records, input, runtime) {
    const control = profilingControl(input); assert.ok(control, "A profile policy applies only to the five named original cases");
    const ordinary = normalize(records, input), evidence = [];
    assert.ok(Array.isArray(ordinary));
    for (let index = 0; index < ordinary.length; index++) {
        const cycle = ordinary[index]; assert.ok(!cycle.error, cycle.error);
        if (control.flag === "--extendedDiagnostics") {
            const start = cycle.stdout.startsWith("Files:") ? 0 : cycle.stdout.lastIndexOf("\nFiles:") + 1;
            assert.ok(start > 0 || cycle.stdout.startsWith("Files:"), "The requested statistics table exists");
            const rows = cycle.stdout.slice(start).trimEnd().split(/\r?\n/).map(row => {
                const match = /^([^:]+):\s+(\d+(?:\.\d+)?[Ks]?)$/.exec(row); assert.ok(match, `Statistics row: ${row}`);
                return [match[1], match[2]];
            });
            const names = runtime === "candidate" ? csRows : goRows;
            const expected = input.suite === "tsbuild" ? [...names, ...buildRows, ...names.map(name => `Aggregate ${name}`)] : names;
            assert.deepEqual(rows.map(([name]) => name), expected);
            for (const [name, value] of rows) {
                const timing = /time(?: \(summed\))?$/.test(name);
                assert.match(value, timing ? /^\d+\.\d{3}s$/ : name.endsWith("Memory used") ? /^\d+K$/ : /^\d+$/);
            }
            const values = Object.fromEntries(rows); assert.ok(Number(values.Files) > 0 && Number(values.Lines) > 0);
            if (runtime === "candidate") assert.ok(Number(values["CLR managed bytes"]) > 0 && Number(values["CLR allocated bytes"]) > 0);
            evidence.push({ kind: "statistics", values });
            cycle.stdout = cycle.stdout.slice(0, start);
        }
        else {
            const args = input.steps[index].args, traceDirectory = path.posix.resolve(input.cwd, args[args.indexOf("--generateTrace") + 1]);
            const tracePath = path.posix.join(traceDirectory, "trace.json"), legendPath = path.posix.join(traceDirectory, "legend.json");
            const files = new Map(cycle.writes.map(write => [write.path, write]));
            const read = file => { const entry = files.get(file); assert.ok(entry, `Missing trace artifact: ${file}`); return JSON.parse(Buffer.from(entry.textBase64, "base64")); };
            const legend = read(legendPath), events = read(tracePath), types = new Map();
            assert.ok(Array.isArray(legend) && legend.length > 0 && Array.isArray(events));
            const removed = new Set([tracePath, legendPath]);
            for (const entry of legend) {
                assert.equal(entry.tracePath, tracePath); assert.ok(Number.isInteger(entry.checkerId) && entry.checkerId >= 0);
                assert.equal(entry.typesPath, path.posix.join(traceDirectory, `types_${entry.checkerId}.json`));
                assert.ok(!types.has(entry.checkerId));
                const table = read(entry.typesPath); assert.ok(Array.isArray(table) && table.length > 0);
                const ids = new Set(table.map(type => type.id)); assert.equal(ids.size, table.length);
                for (const type of table) {
                    assert.ok(Number.isInteger(type.id) && type.id > 0 && Array.isArray(type.flags) && type.flags.length > 0);
                    for (const [key, value] of Object.entries(type)) if (referenceFields.has(key))
                        for (const id of Array.isArray(value) ? value : [value]) if (id > 0) assert.ok(ids.has(id), `${entry.typesPath}: missing ${key} ${id}`);
                }
                types.set(entry.checkerId, ids); removed.add(entry.typesPath);
            }
            const stacks = new Map(), lanes = new Set();
            for (const event of events) {
                assert.ok(Number.isInteger(event.tid) && Number.isFinite(event.ts) && event.ts >= 0);
                if (event.name === "thread_name") lanes.add(event.tid);
                if (!stacks.has(event.tid)) stacks.set(event.tid, []);
                if (event.ph === "B") stacks.get(event.tid).push(event.name);
                else if (event.ph === "E") assert.equal(stacks.get(event.tid).pop(), event.name);
                else if (event.ph === "X") assert.ok(Number.isFinite(event.dur) && event.dur >= 0);
                else assert.equal(event.ph, "M");
                if (event.args?.checkerId !== undefined) {
                    const ids = types.get(event.args.checkerId); assert.ok(ids);
                    for (const key of ["sourceId", "targetId", "id"]) if (event.args[key] !== undefined) assert.ok(ids.has(event.args[key]));
                }
            }
            for (const [lane, stack] of stacks) { assert.ok(lanes.has(lane)); assert.equal(stack.length, 0); }
            assert.ok(events.some(event => event.name === "createProgram"));
            assert.ok(events.some(event => event.name === "checkSourceFile"));
            evidence.push({ kind: "trace", events: events.length, files: [...removed], typeRecords: [...types].map(([checker, ids]) => ({ checker, types: ids.size })) });
            cycle.writes = cycle.writes.filter(write => !removed.has(write.path));
        }
    }
    return { ordinary, evidence };
}

export function compareProfiling(reference, candidate, input) {
    const expected = inspectProfiling(reference, input, "reference"), actual = inspectProfiling(candidate, input, "candidate");
    assert.deepEqual(actual.ordinary, expected.ordinary, "Ordinary output and side effects remain exact");
    for (let index = 0; index < expected.evidence.length; index++) {
        const a = expected.evidence[index], b = actual.evidence[index]; assert.equal(a.kind, b.kind);
        if (a.kind === "statistics") for (const name of ["Files", "Lines", "Projects in scope", "Projects built", "Timestamps only updates", "Aggregate Files", "Aggregate Lines"])
            assert.equal(b.values[name], a.values[name], name);
    }
    return { reference: expected.evidence, candidate: actual.evidence };
}
