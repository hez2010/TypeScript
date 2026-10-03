import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { gunzipSync } from "node:zlib";
import { documentationBytePositions } from "./documentation-byte-positions.mjs";

export const digest = value => createHash("sha256").update(typeof value === "string" || Buffer.isBuffer(value) ? value : JSON.stringify(value)).digest("hex");
export const caseName = value => value.replaceAll("\\", "/");

// Text[] stores parser fragments. Joining those fragments preserves every
// character; positions, flags and list ranges remain in the strict output/hash.
export function documentationMeaning(output) {
    return output[0].map(host => [
        host[0],
        host[1],
        host[2],
        host[3].map(rows => {
            const stack = [];
            let root;
            for (const [kind, , , , count, properties] of rows) {
                const scalar = { ...properties };
                if (Array.isArray(scalar.Text)) scalar.Text = scalar.Text.map(value => Buffer.from(value, "base64").toString("utf8")).join("");
                const node = { kind, scalar, children: [] };
                while (stack.length && stack.at(-1).remaining === 0) stack.pop();
                if (stack.length) {
                    stack.at(-1).node.children.push(node);
                    stack.at(-1).remaining--;
                }
                else if (root) throw new Error("Multiple roots in documentation tree");
                else root = node;
                if (count) stack.push({ node, remaining: count });
            }
            if (stack.some(frame => frame.remaining !== 0)) throw new Error("Incomplete documentation tree");
            return root;
        }),
    ]);
}

const document = JSON.parse(readFileSync(new URL("../tests/fixtures/jsdoc/documentation-differences.json", import.meta.url), "utf8"));
const approved = new Map(document.cases.map(entry => [entry.name, entry]));
const previousOutputs = new Map();
const evidence = JSON.parse(gunzipSync(readFileSync(new URL("../tests/fixtures/jsdoc/documentation-evidence.json.gz", import.meta.url))));
for (const record of evidence) {
    const entry = approved.get(caseName(record.input.name));
    if (!entry || entry.sourceSha256 !== digest(Buffer.from(record.input.text, "base64")) || entry.expectedSha256 !== digest(record.expected) || entry.actualSha256 !== digest(record.actual)) {
        throw new Error(`Documentation evidence does not match its approved hashes: ${record.input.name}`);
    }
    entry.expectedSha256 = digest(documentationBytePositions(record.input, record.expected));
    const previous = documentationBytePositions(record.input, record.actual);
    entry.actualSha256 = digest(previous);
    previousOutputs.set(entry.name, previous);
}

// A fix may replace an approved differing field with its exact reference value.
// Every remaining field must still be either the pinned old value or the reference
// value. In particular this does not discard ranges, flags, fragments or ordering.
export function convergesToReference(previous, expected, actual) {
    if (equal(actual, expected) || equal(actual, previous)) return true;
    if (!previous || !expected || !actual || typeof previous !== "object" || typeof expected !== "object" || typeof actual !== "object"
        || Array.isArray(previous) !== Array.isArray(actual) || Array.isArray(expected) !== Array.isArray(actual)) return false;
    return [...new Set([...Object.keys(previous), ...Object.keys(expected), ...Object.keys(actual)])]
        .every(key => convergesToReference(previous[key], expected[key], actual[key]));
}

function equal(left, right) {
    if (left === right) return true;
    if (!left || !right || typeof left !== "object" || typeof right !== "object" || Array.isArray(left) !== Array.isArray(right)) return false;
    const keys = Object.keys(left);
    return keys.length === Object.keys(right).length && keys.every(key => Object.hasOwn(right, key) && equal(left[key], right[key]));
}

function referenceForRetainedAssociations(previous, expected) {
    const hostKey = host => JSON.stringify(host.slice(0, 3));
    const hosts = new Map(expected[0].map(host => [hostKey(host), host]));
    if (expected[0].some(host => !previous[0].some(old => hostKey(old) === hostKey(host)))) return expected;
    const comments = expected[0].flatMap(host => host[3]);
    return [previous[0].map(host => {
        if (hosts.has(hostKey(host))) return hosts.get(hostKey(host));
        // The approved extra owner is fixed. Its comment can acquire a corrected
        // field only from that same comment already attached to a reference owner.
        const trees = host[3].map(tree => {
            const matches = comments.filter(candidate => candidate[0][0] === tree[0][0] && candidate[0][2] === tree[0][2]);
            return matches.length && matches.every(candidate => equal(candidate, matches[0])) ? matches[0] : tree;
        });
        return [...host.slice(0, 3), trees];
    }), expected[1]];
}
export function classifyDocumentationDifference(input, expected, actual) {
    const expectedSha256 = digest(expected), actualSha256 = digest(actual);
    if (expectedSha256 === actualSha256) return "exact";
    const entry = approved.get(caseName(input.name));
    if (!entry || entry.sourceSha256 !== digest(Buffer.from(input.text, "base64")) || entry.expectedSha256 !== expectedSha256) return null;
    const previous = previousOutputs.get(entry.name);
    const comparison = entry.policy === "retained-documentation-association" ? referenceForRetainedAssociations(previous, expected) : expected;
    if (entry.actualSha256 !== actualSha256 && !convergesToReference(previous, comparison, actual)) return null;
    if (entry.identicalTypesTagsAndText && digest(documentationMeaning(expected)) !== digest(documentationMeaning(actual))) return null;
    return entry.policy;
}
