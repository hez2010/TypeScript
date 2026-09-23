import { createHash } from "node:crypto";
import {
    bindingHash,
    parserBaseline,
} from "./compare-binding.mjs";

const sourceHash = text => createHash("sha256").update(text).digest("hex");
// Additional reviewed malformed fixtures. Do not expand this to arbitrary rejected input.
const malformedSources = new Map([
    [sourceHash("export default function named(){named;} named; default;"), new Set(["ts", "js", "declaration"])],
    [sourceHash("let x=1; let a=x as const; let b=<const>x; type const=number;const;"), new Set(["ts", "declaration"])],
]);

export function classifyNameDifference(input, actual, exported, rebound, syntax, independent) {
    if (!Array.isArray(actual) || actual.length !== 3 || !Array.isArray(exported?.data) || !Array.isArray(rebound?.data)) return null;
    if (!exported.syntaxFingerprint || exported.syntaxFingerprint !== rebound.syntaxFingerprint) return null;
    if (bindingHash(actual) !== bindingHash(exported.data) || bindingHash(actual) !== bindingHash(rebound.data)) return null;
    const baseline = parserBaseline(input);
    if (baseline && syntax?.expectedHash === baseline.expectedSha256 && syntax?.actualHash === baseline.actualSha256) return "phase2-parser-tree:" + baseline.policy;
    const hash = sourceHash(Buffer.from(input.text, "base64"));
    const flavor = /\.d\.ts$/i.test(input.fileName) ? "declaration" : /\.js$/i.test(input.fileName) ? "js" : /\.ts$/i.test(input.fileName) ? "ts" : "unsupported";
    if (
        malformedSources.get(hash)?.has(flavor) && exported.parseErrors > 0 && rebound.parseErrors > 0 && independent?.errors > 0 && independent?.version
        && independent.sourceSha256 === hash
    ) return "malformed-parser-recovery:same-tree-name-resolution";
    return null;
}
