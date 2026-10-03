import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { output, root, run, sha256, referenceRevision } from "./common.mjs";

const option = (name, fallback) => process.argv.includes(name) ? process.argv[process.argv.indexOf(name) + 1] : fallback;
const directory = path.resolve(option("--directory", path.join(output, "phase7-validation/specifier-regex")));
const managed = path.resolve(option("--managed-directory", path.join(output, "phase7-tags-build/bin/TypeScript.Compatibility/release")));
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
assert.equal(reference.referenceRevision, referenceRevision);
const source = path.join(output, reference.sourceRelativePath, "tsc");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const dotnet = option("--dotnet", "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe");
await mkdir(directory, { recursive: true });
await mkdir(path.join(source, "cmd/specifier-regex-probe"), { recursive: true });
await copyFile(path.join(root, "csharp/oracle/specifier-regex/main.go"), path.join(source, "cmd/specifier-regex-probe/main.go"));
const oracle = path.join(directory, "oracle.exe");
await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", oracle, "./cmd/specifier-regex-probe"],
    { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
const base64 = text => Buffer.from(text).toString("base64");
const cases = (await run(oracle, ["--fixtures", ...process.argv.includes("--exhaustive") ? ["--exhaustive"] : []])).trim().split(/\r?\n/).map(JSON.parse);
const originalCases = cases.length;
const texts = ["", "a", "b", "aa", "ab", "abc", "abab", "abbbc", "a\nb", "a\n", "\n", "\r\n", " ", "\t", "\v", "\f", "0", "1", "123", "١", "５", "foo", "node:fs", "@scope/pkg", "lodash/fp", "pkg$", "ABC", "S", "s", "ſ", "K", "k", "K", "İ", "ı", "I", "i", "σ", "Σ", "ς", "Ā", "ā", "ǅ", "ǆ", "𐐀", "𐐨", "😀", "中", "\0", "�", "x\u2028y", "\u00a0", Buffer.from([255]), Buffer.from([0xed, 0xa0, 0x80])];
const add = (pattern, subjects = texts) => cases.push({ pattern: base64(pattern), texts: subjects.map(base64) });
const basic = ["", "a", "a|", "|", "a|b", "a*", "a+", "a?", ".", ".+", ".*", "^", "$", "^$", "a$", "^a$", "(?m)^a$", "(?s).", "(?U:a+)", "(?:a|ab)*b", "(a?)*", "(a*)+", "(?:)*", "(?i)a", "(?i)k", "(?i)s", "(?i)I", "(?i)σ", "(?i)𐐀", "(?i)[A-Z]", "(?i)[^A-Z]", "[a-z]", "[^a-z]", "[]a]", "[-a]", "[a-]", "[a-b-c]", "[a-\\d]", "[\\d-a]", "[^]", "[a&&b]", "[a-[b]]", "a{0}", "a{0,0}", "a{0,2}", "a{2,}", "a{3}", "a{01}", "a{00,3}", "a{1,02}", "a{1, 2}", "a{,2}", "a{}", "a{1x}", "a{9999999x}", "a{1001}", "a{1,0}", "a{999999999999}", "(a{100}){11}", "((a{100}){0}){100}", "a**", "a*+", "a*?", "a++", "a(?i)*", "a*(?i)*", "(?P<name>a)", "(?<1>a)", "(?P<a-b>x)", "(?'x'a)", "(?<x>a)(?<x>b)", "(?i-m:a)", "(?i-m)a", "(?-:a)", "(?-i:a)", "(?i--m:a)", "(?x)a", "(?=a)", "(?!a)", "(?<=a)b", "(?>a)", "(?#x)", "(a)\\1", "(?i:a)b", "a(?i)b|c", "(?i)a(?-i)b", "(?i)[[:ascii:]]", "(?:[a-z]|[0-9])*", "😀+", "^.$", "^[^a]$", "^..$", "^\\x{D800}$"];
for (const pattern of basic) { add(pattern); add(`/${pattern}/i`); }
for (const escape of ["A", "z", "Z", "b", "B", "d", "D", "w", "W", "s", "S", "h", "H", "v", "V", "a", "f", "n", "r", "t", "e", "cA", "C", "Q[a-z]\\E", "Qab", "E", "_", "/", "!", "é", "0", "00", "000", "01", "1", "12", "123", "777", "8", "x00", "xFF", "x{10ffff}", "x{}", "x{0}", "x{D800}", "u0041", "x{110000}", "x0", "xGG", "x{00000041}", "k<x>"])
    for (const pattern of [`\\${escape}`, `[\\${escape}]`, `(?i)\\${escape}`]) add(pattern);
for (const name of ["alnum", "alpha", "ascii", "blank", "cntrl", "digit", "graph", "lower", "print", "punct", "space", "upper", "word", "xdigit", "unknown", "Alpha"])
    for (const flags of ["", "(?i)"]) for (const negative of ["", "^"]) add(`${flags}[[:${negative}${name}:]]`);
for (const name of ["L", "Lu", "LC", "Letter", "Uppercase_Letter", "LOWER CASE LETTER", "l-c", "Greek", "Old_Italic", "olditalic", "Han", "Any", "Assigned", "ASCII", "Latin", "Zs", "Nd", "N", "Cn", "Cs", "Cc", "Co", "Z", "White_Space", "Emoji", "Latin-1", "", " Unknown", "^Greek"])
    for (const letter of ["p", "P"]) for (const flags of ["", "(?i)"]) add(`${flags}\\${letter}{${name}}`);
for (const raw of ["/foo/", "/foo/i", "/foo/gimsy", "/foo\\/bar/i", "/foo/bar/i", "/foo\\\\/bar/i", "//", "//i", "/a/unknown", "/a/İ", "/a/$", "///i", "/^node:/", "/^node:/i", "/^node:/m", "/^node:/s", "/\\//i", Buffer.from([47, 255, 47, 105]), Buffer.from([47, 255, 47]), Buffer.from([255])]) add(raw);
for (const start of ["", "^", "\\A", "(^", "(?:^", "(?i)^", "^("]) for (const literal of ["\\x{d800}", "[\\x{d800}]", "a\\x{dc00}", "\\x{d800}\\x{dc00}", "\\x{d800}a", "�\\x{d800}"])
    for (const end of ["", "$", "a?$", "(?:a|b)$", "(?:a|ab)$", "(?:a|a)$", "(?:a|[a-b])$", "(?:|)$", "a*a$", "[ab]*$", "(a)$", "\\b$"])
        add(start + literal + (start.includes("(") && !start.startsWith("(?i)") ? ")" : "") + end,
            ["", "�", "��", "a�", "�a", "�b", "�ab", "�aa", "�aaa", "a�a", "��a", "��aaa", Buffer.from([255]), Buffer.from([255, 97]), Buffer.from([0xed, 0xa0, 0x80])]);
let seed = 0x42dc911;
const random = n => { seed ^= seed << 13; seed ^= seed >>> 17; seed ^= seed << 5; return (seed >>> 0) % n; };
const atoms = ["a", "b", ".", "[ab]", "[^b]", "\\w", "\\W", "^", "$", "\\b", "\\B", "\\p{L}", "😀", "(?i:k)", "(?:)"];
for (let i = 0; i < 3500; i++) {
    let pattern = atoms[random(atoms.length)];
    for (let j = 0, n = random(6); j < n; j++) {
        const atom = atoms[random(atoms.length)];
        switch (random(6)) {
            case 0: pattern += atom; break;
            case 1: pattern = `(?:${pattern}|${atom})`; break;
            case 2: pattern = `(${pattern})?`; break;
            case 3: pattern = `(?:${pattern})*`; break;
            case 4: pattern = `(?:${pattern}){0,2}`; break;
            case 5: pattern = `(?i:${pattern})`; break;
        }
    }
    add(pattern);
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
const [expected, actual] = await Promise.all([probe(oracle, [], "reference"), probe(dotnet, [path.join(managed, "TypeScript.Compatibility.dll"), "--specifier-regex-lines"], "candidate")]);
const differences = cases.flatMap((input, i) => { try { assert.deepEqual(actual[i], expected[i]); return []; } catch { return [{ input, expected: expected[i], actual: actual[i] }]; } });
await writeFile(path.join(directory, "differences.json"), JSON.stringify(differences, null, 2) + "\n");
const summary = { referenceRevision, cases: cases.length, originalCases, matchQueries: cases.reduce((n, c) => n + c.texts.length * 2, 0),
    passed: cases.length - differences.length, differences: differences.length, inputsSha256: sha256(inputs), oracleSha256: sha256(await readFile(oracle)),
    compilerSha256: sha256(await readFile(path.join(managed, "TypeScript.Compiler.dll"))), harnessSha256: sha256(await readFile(path.join(managed, "TypeScript.Compatibility.dll"))) };
await writeFile(path.join(directory, "summary.json"), JSON.stringify(summary, null, 2) + "\n");
console.log(JSON.stringify(summary));
if (differences.length) process.exitCode = 1;
