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
const evidence = JSON.parse(gunzipSync(readFileSync(new URL("../tests/fixtures/jsdoc/documentation-evidence.json.gz", import.meta.url))));
for (const record of evidence) {
    const entry = approved.get(caseName(record.input.name));
    if (!entry || entry.sourceSha256 !== digest(Buffer.from(record.input.text, "base64")) || entry.expectedSha256 !== digest(record.expected) || entry.actualSha256 !== digest(record.actual)) {
        throw new Error(`Documentation evidence does not match its approved hashes: ${record.input.name}`);
    }
    entry.expectedSha256 = digest(documentationBytePositions(record.input, record.expected));
    entry.actualSha256 = digest(documentationBytePositions(record.input, record.actual));
}
export function classifyDocumentationDifference(input, expected, actual) {
    const expectedSha256 = digest(expected), actualSha256 = digest(actual);
    if (expectedSha256 === actualSha256) return "exact";
    const entry = approved.get(caseName(input.name));
    if (!entry || entry.sourceSha256 !== digest(Buffer.from(input.text, "base64")) || entry.expectedSha256 !== expectedSha256 || entry.actualSha256 !== actualSha256) return null;
    if (entry.identicalTypesTagsAndText && digest(documentationMeaning(expected)) !== digest(documentationMeaning(actual))) return null;
    return entry.policy;
}
