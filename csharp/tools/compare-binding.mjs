import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
const ledger = JSON.parse(readFileSync(new URL("../compatibility/evidence/phase2-parser-differences.json", import.meta.url)));
export const bindingHash = value => createHash("sha256").update(JSON.stringify(value)).digest("hex");
const flavor = file =>
    /\.d\.[cm]?ts$/i.test(file) ? "declaration" : /\.tsx$/i.test(file) ? "tsx"
        : /\.jsx$/i.test(file) ? "jsx" : /\.[cm]?js$/i.test(file) ? "js" : /\.json$/i.test(file) ? "json" : "ts";
export function parserBaseline(input) {
    const hash = createHash("sha256").update(Buffer.from(input.text, "base64")).digest("hex");
    return ledger.cases.find(entry => entry.sourceSha256 === hash && flavor(entry.fileName) === flavor(input.fileName));
}
export function classifyBindingDifference(input, actual, exported, rebound, syntax) {
    const baseline = parserBaseline(input);
    if (!baseline || !syntax || syntax.expectedHash !== baseline.expectedSha256 || syntax.actualHash !== baseline.actualSha256) return null;
    if (!Array.isArray(actual) || actual.length !== 6 || !Array.isArray(exported?.binding) || !Array.isArray(rebound?.binding)) return null;
    if (!exported.syntaxFingerprint || exported.syntaxFingerprint !== rebound.syntaxFingerprint) return null;
    if (bindingHash(actual) !== bindingHash(exported.binding) || bindingHash(actual) !== bindingHash(rebound.binding)) return null;
    return "phase2-parser-tree:" + baseline.policy;
}
