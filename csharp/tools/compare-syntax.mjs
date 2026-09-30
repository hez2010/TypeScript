import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import ts from "typescript";

const fixturePolicies = new Map();
for (const file of ["source", "recovery", "jsdoc"]) {
    let document;
    try {
        document = JSON.parse(readFileSync(new URL(`../tests/fixtures/parser/${file}-differences.json`, import.meta.url), "utf8"));
    }
    catch (error) {
        if (error.code === "ENOENT") continue;
        throw error;
    }
    for (const entry of document.cases) {
        if (fixturePolicies.has(entry.name)) throw new Error(`Duplicate parser difference: ${entry.name}`);
        fixturePolicies.set(entry.name, entry);
    }
}

// These policies compare language structure, source coverage and independent
// rejection of malformed input. They never remove diagnostics from a valid file.
const contextFlags = 1024 | 2048 | 4096 | 8192 | 16384;
const whitespace = /^[\s\u0085\u200B]*$/u;
const equals = (a, b) => JSON.stringify(a) === JSON.stringify(b);

function sourceText(fixture) {
    return Buffer.from(fixture.text, "base64").toString("utf8");
}
function validTree(records, length) {
    let remaining = 1;
    for (const [kind, start, end, flags, value, children] of records) {
        if (!Number.isInteger(kind) || !Number.isInteger(children) || children < 0 || --remaining < 0) return false;
        if (start < 0 || end < 0) { if (start !== -1 || end !== -1) return false; }
        else if (end < start || end > length) return false;
        remaining += children;
    }
    return remaining === 0;
}
function spanEquivalent(a, b, text, bytes) {
    const character = offset => bytes.subarray(0, offset).toString("utf8").length;
    if (a[1] !== b[1]) {
        if (a[1] < 0 || b[1] < 0 || a[1] === a[2] || b[1] === b[2]) return false;
        if (ts.skipTrivia(text, character(a[1])) !== ts.skipTrivia(text, character(b[1]))) return false;
    }
    if (a[2] !== b[2]) {
        if (a[2] < 0 || b[2] < 0 || !whitespace.test(bytes.subarray(Math.min(a[2], b[2]), Math.max(a[2], b[2])).toString("utf8"))) return false;
    }
    return true;
}
export function classifySyntaxDifference(fixture, expected, actual) {
    const text = sourceText(fixture);
    const bytes = Buffer.from(fixture.text, "base64");
    const [wanted, wantedErrors, wantedJsErrors = []] = expected.details, [observed, observedErrors, observedJsErrors = []] = actual.details;
    if (!validTree(observed, bytes.length)) return null;
    const sameJsErrors = equals(wantedJsErrors, observedJsErrors);
    if (equals(wanted, observed) && equals(wantedErrors, observedErrors) && sameJsErrors) return "exact";
    const documented = fixturePolicies.get(fixture.name);
    if (
        documented && documented.expectedSha256 === expected.hash && documented.actualSha256 === actual.hash &&
        documented.sourceSha256 === createHash("sha256").update(Buffer.from(fixture.text, "base64")).digest("hex")
    ) return documented.policy;
    if (!sameJsErrors) return null;
    const name = fixture.fileName ?? fixture.name;
    const kind = fixture.jsx ? ts.ScriptKind.TSX : /\.jsx$/i.test(name) ? ts.ScriptKind.JSX : /\.(?:[cm]?js)$/i.test(name) ? ts.ScriptKind.JS : /\.json$/i.test(name) ? ts.ScriptKind.JSON : ts.ScriptKind.TS;
    const independent = ts.createSourceFile(name, text, ts.ScriptTarget.Latest, true, kind);
    if (!wantedErrors.length && !observedErrors.length && !independent.parseDiagnostics.length && wanted.length === observed.length) {
        let flags = false, spans = false;
        for (let i = 0; i < wanted.length; i++) {
            const a = wanted[i], b = observed[i];
            if (a[0] !== b[0] || a[4] !== b[4] || a[5] !== b[5] || !equals(a[6], b[6]) || !equals(a[7], b[7]) || ((a[3] ^ b[3]) & ~contextFlags) !== 0 || !spanEquivalent(a, b, text, bytes)) return null;
            flags ||= a[3] !== b[3];
            spans ||= a[1] !== b[1] || a[2] !== b[2];
        }
        return flags && spans ? "equivalent-context-and-trivia" : flags ? "equivalent-parser-context" : "equivalent-trivia-boundary";
    }
    if (wantedErrors.length && observedErrors.length && independent.parseDiagnostics.length) {
        if (observedErrors.some(d => !Number.isInteger(d[1]) || !Number.isInteger(d[2]) || d[1] < 0 || d[2] < 0 || d[1] + d[2] > bytes.length)) return null;
        // Recovery structure is unspecified for an invalid program. Require both
        // independent references to reject it and at least one independently
        // identified offending token. Recovery can change diagnostic ordering.
        const byte = position => Buffer.byteLength(text.slice(0, position));
        const reference = [...wantedErrors, ...independent.parseDiagnostics.map(d => [d.code, byte(d.start), byte(d.start + d.length) - byte(d.start)])];
        if (observedErrors.some(actual => reference.some(d => d[1] === actual[1] && d[2] === actual[2]))) return "invalid-source-recovery";
    }
    return null;
}
export const independentParserVersion = ts.version;
