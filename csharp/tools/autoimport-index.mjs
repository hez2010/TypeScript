import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { output, root, run, sha256, referenceRevision } from "./common.mjs";

const option = (name, fallback) => process.argv.includes(name) ? process.argv[process.argv.indexOf(name) + 1] : fallback;
const directory = path.resolve(option("--directory", path.join(output, "phase7-validation/autoimport-index")));
const managed = path.resolve(option("--managed-directory", path.join(output, "phase7-tags-build/bin/TypeScript.Compatibility/release")));
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
assert.equal(reference.referenceRevision, referenceRevision);
const source = path.join(output, reference.sourceRelativePath, "tsc");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe");
await mkdir(directory, { recursive: true });
await mkdir(path.join(source, "cmd/autoimport-index-probe"), { recursive: true });
await copyFile(path.join(root, "csharp/oracle/autoimport-index/main.go"), path.join(source, "cmd/autoimport-index-probe/main.go"));
await copyFile(path.join(root, "csharp/oracle/autoimport-index/bridge.go"), path.join(source, "internal/ls/autoimport/csharp_index_probe.go"));
const oracle = path.join(directory, "oracle.exe");
await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", oracle, "./cmd/autoimport-index-probe"],
    { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
const base64 = text => Buffer.from(text).toString("base64");
const cases = [{ mode: "unicode" }];
const add = (names, queries, more = {}) => cases.push({ names: names.map(base64), queries: queries.map(base64), ...more });
const originals = ["camelCase", "snake_case", "ParseURL", "XMLHttpRequest", "hello", "HELLO", "parseHTML5Parser", "__proto__", "_private_member", "a", "A", "test__double__underscore"];
add(originals, ["", ...originals, "cC", "sc", "URL", "Http", "Request", "HTMLP", "proto", "private", "member", "double", "undersc", "z"]);
for (const keep of [[0, 2], [], [0, 1, 2]]) add(["fooBar", "bazQux", "fooQux"], ["", "fooBar", "bazQux", "foo", "qux"], { keep });
add([], ["", "a"]); add([], ["", "a"], { nil: true });
const samples = ["", "K", "k", "K", "S", "s", "ſ", "Σ", "σ", "ς", "İ", "ı", "I", "i", "Ω", "Ω", "Å", "Å", "ﬀ", "ẞ", "ß", "𐐀", "𐐨", "𐓐", "𐓸", "😀", "𓀀", "中", "é", "ǅ", "ǆ", "Ա", "ა", "Ა", "\0", "�", "_", "9", "$",
    Buffer.from([0xff]), Buffer.from([0xc0, 0xaf]), Buffer.from([0xed, 0xa0, 0x80]), Buffer.from([0xf0, 0x9f]), Buffer.from([0x80, 0x41])];
const concat = (...parts) => Buffer.concat(parts.map(part => Buffer.from(part)));
for (const a of samples) {
    const names = [a, concat(a, "Name"), concat("foo", a, "Bar"), concat("FOO", a, "bar"), concat("pre_", a), concat("_", a, "_Suffix"), concat(a, "_", a)];
    add(names, ["", a, ...names, "bar", "N", "fb", "sf"], { keep: [0, 2, 4, 6] });
}
// Visit every cased Unicode scalar plus selected points from each plane. The
// complete property digest above independently covers all 1,114,112 code points.
for (let point = 0; point <= 0x10ffff; point++) {
    const a = String.fromCodePoint(point);
    if (a.toLowerCase() === a.toUpperCase() && point % 257 !== 0) continue;
    const names = [a, a.toLowerCase(), a.toUpperCase(), `a${a}b`, `A${a}b`, `a_${a}B`, `${a}_${a}`];
    add(names, ["", ...names, a, a.toLowerCase(), a.toUpperCase(), "ab", "b"], { keep: [0, 1, 3, 5] });
}
const inputs = cases.map(input => JSON.stringify(input)).join("\n") + "\n";
await writeFile(path.join(directory, "inputs.jsonl"), inputs);
async function probe(command, args, filename) {
    const child = spawn(command, args, { windowsHide: true }), out = [], errors = [];
    child.stdout.on("data", data => out.push(data)); child.stderr.on("data", data => errors.push(data));
    child.stdin.on("error", () => {}); child.stdin.end(inputs);
    const code = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
    await writeFile(path.join(directory, filename + ".stderr"), Buffer.concat(errors));
    await writeFile(path.join(directory, filename + ".jsonl"), Buffer.concat(out));
    assert.equal(code, 0, Buffer.concat(errors).toString());
    const results = Buffer.concat(out).toString().trim().split(/\r?\n/).map(JSON.parse);
    assert.equal(results.length, cases.length); return results;
}
const [expected, actual] = await Promise.all([probe(oracle, [], "reference"), probe(dotnet, [path.join(managed, "TypeScript.Compatibility.dll"), "--autoimport-index-lines"], "candidate")]);
const differences = cases.flatMap((input, i) => { try { assert.deepEqual(actual[i], expected[i]); return []; } catch { return [{ input, expected: expected[i], actual: actual[i] }]; } });
await writeFile(path.join(directory, "differences.json"), JSON.stringify(differences, null, 2) + "\n");
const summary = { referenceRevision, cases: cases.length, passed: cases.length - differences.length, differences: differences.length,
    unicodePropertyDigest: { reference: expected[0], candidate: actual[0] }, inputsSha256: sha256(inputs), oracleSha256: sha256(await readFile(oracle)),
    compilerSha256: sha256(await readFile(path.join(managed, "TypeScript.Compiler.dll"))), harnessSha256: sha256(await readFile(path.join(managed, "TypeScript.Compatibility.dll"))) };
await writeFile(path.join(directory, "summary.json"), JSON.stringify(summary, null, 2) + "\n");
console.log(JSON.stringify(summary));
if (differences.length) process.exitCode = 1;
