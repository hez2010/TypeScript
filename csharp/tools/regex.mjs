import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import {
    copyFile,
    mkdir,
    readFile,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import {
    output,
    root,
    run,
    sha256,
} from "./common.mjs";

const option = (name, fallback) => process.argv.includes(name) ? process.argv[process.argv.indexOf(name) + 1] : fallback;
const dotnet = option("--dotnet", process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, "dotnet.exe") : "dotnet");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc");
const probeSource = path.join(source, "cmd/regex-probe");
await mkdir(probeSource, { recursive: true });
await copyFile(path.join(root, "csharp/oracle/regex/main.go"), path.join(probeSource, "main.go"));
const oracle = path.join(output, "regex-oracle.exe");
if (!process.argv.includes("--no-build")) {
    await run(go, ["-C", source, "build", "-mod=readonly", "-buildvcs=false", "-o", oracle, "./cmd/regex-probe"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
    await run(dotnet, ["build", "csharp/tests/TypeScript.RegularExpression", "-c", "Release", "--no-restore"]);
}
const native = option("--native");
const candidate = native ?? dotnet;
const args = native ? [] : [path.join(root, "csharp/tests/TypeScript.RegularExpression/bin/Release/net11.0/TypeScript.RegularExpression.dll")];
async function probe(command, args, cases) {
    return await new Promise((resolve, reject) => {
        const child = spawn(command, args, { windowsHide: true });
        const stdout = [], stderr = [];
        child.stdout.on("data", bytes => stdout.push(bytes));
        child.stderr.on("data", bytes => stderr.push(bytes));
        child.on("error", reject);
        child.stdin.on("error", reject);
        child.on("close", code => {
            if (code) reject(new Error(`${command} exited ${code}: ${Buffer.concat(stderr)}`));
            else resolve(Buffer.concat(stdout).toString().trim().split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line)));
        });
        child.stdin.end(cases.map(c => JSON.stringify(c)).join("\n") + "\n");
    });
}
let cases = [];
function add(pattern, flags = "", target = 9999) {
    cases.push({ name: `/${pattern}/${flags}@${target}`, text: Buffer.from(`/${pattern}/${flags}`).toString("base64"), target });
}
const patterns = [
    "a",
    "",
    "abc",
    "a|b",
    "|",
    "^*",
    "$+",
    "\\b*",
    "\\B?",
    "(?=a)*",
    "(?!a)+",
    "(?<=a)*",
    "(?<!a)?",
    "(?:a)*",
    "(a)+",
    "(",
    "((",
    ")",
    "(a",
    "a)",
    "(?",
    "(?x)",
    "(?-:a)",
    "(?i-:a)",
    "(?ii:a)",
    "(?g:a)",
    "(?gg:a)",
    "(?i-i:a)",
    "(?-im:a)",
    "(?i!a)",
    "(?😀:a)",
    "(?<a>x)(?<a>y)",
    "(?<a>x)|(?<a>y)",
    "(?:(?<a>x))(?<a>y)",
    "(?:(?<a>x)|(?<a>y))(?<a>z)",
    "(?<a>(?<a>x)|y)",
    "(?<1>x)",
    "(?<>x)",
    "(?<\\u0061>x)\\k<a>",
    "(?<a>x)\\k<\\u0061>",
    "(?<𐐀>x)\\k<𐐀>",
    "(?<\\uD801\\uDC00>x)\\k<𐐀>",
    "(?<\\u{D801}\\u{DC00}>x)",
    "\\k",
    "\\k<x>",
    "(?<name>x)\\k<nme>",
    "\\k(?<a>x)",
    "\\1",
    "\\2(a)",
    "(a)\\1",
    "\\9999999999999999999999999999999999999",
    "{}",
    "{,}",
    "{,3}",
    "{,x}",
    "{3}",
    "a{3,2}",
    "a{3,2x}",
    "a{3",
    "a{1,}",
    "a{0003,02}",
    "a{999999999999999999999,999999999999999999998}",
    "a**",
    "a{2}??",
    "\\c",
    "\\c1",
    "\\cA",
    "\\c_",
    "\\x",
    "\\x0",
    "\\xGG",
    "\\u",
    "\\u0",
    "\\u123Z",
    "\\u{}",
    "\\u{110000}",
    "\\u{1f600}",
    "\\u{fffffff}",
    "\\u{1g}",
    "\\u{1",
    "\\0",
    "\\00",
    "\\08",
    "\\7",
    "\\8",
    "\\9",
    "\\'",
    '\\"',
    "\\-",
    "\\a",
    "\\😀",
    "\\p",
    "\\P",
    "\\p{}",
    "\\p{=}",
    "\\p{Foo=Bar}",
    "\\p{gc=}",
    "\\p{gc=lowercase_letter}",
    "\\p{ASCII}",
    "\\p{Script=Greek}",
    "\\P{RGI_Emoji}",
    "[]",
    "[^]",
    "[a-z]",
    "[z-a]",
    "[-a]",
    "[a-]",
    "[a-b-c]",
    "[\\d-z]",
    "[a-\\d]",
    "[\\d-\\s]",
    "[\\1]",
    "[\\8]",
    "[\\c1]",
    "[a-\\xG]",
    "[a-/]",
    "[😀-😁]",
    "[😁-😀]",
    "[😀-a]",
    "[\\uD83D\\uDE00-a]",
    "[\\u{D83D}\\u{DE00}-a]",
    "[[a][b]]",
    "[a&&b]",
    "[a--b]",
    "[a&&]",
    "[a--]",
    "[&&a]",
    "[--a]",
    "[a&&b--c]",
    "[ab&&c]",
    "[ab--c]",
    "[a&&bc]",
    "[a--bc]",
    "[a&&&b]",
    "[a----b]",
    "[a---b]",
    "[&&]",
    "[a--b-c]",
    "[a&&b&c]",
    "[\\c_-A]",
    "[\\c0-/]",
    "[\\c-a]",
    "[z-\\c]",
    "[^\\q{ab}&&[]]",
    "[^\\q{ab}&&[^]]",
    "[�-a]",
    "[\\q{a|b}]",
    "[\\q{ab|c}]",
    "[^\\q{a|b}]",
    "[^\\q{ab|c}]",
    "[^\\q{}]",
    "[\\q]",
    "[\\q{a]",
    "\\q{a}",
    "[^\\q{ab}&&a]",
    "[^\\q{ab}--a]",
    "[^a--\\q{ab}]",
    "[^\\q{ab}&&\\q{cd}]",
    "[^\\p{RGI_Emoji}&&[a]]",
    "[^[[\\q{ab}]]]",
    "[\\q{ab}[]]",
    "[[^]\\q{a}]",
];
for (const pattern of patterns) for (const flag of ["", "u", "v"]) add(pattern, flag);
for (const character of "!#%*+,.:;<=>?@`~&-(){}|/") {
    add(`[${character}${character}]`, "v");
    add(`[\\${character}]`, "v");
    add(`\\${character}`, "u");
}
for (const flags of ["dgimsuvy", "vv", "uu", "uv", "vu", "vuv", "uuvv", "gg", "abc", "名", "𐐀"]) add("a", flags);
for (const target of [5, 2015, 2017, 2018, 2021, 2022, 2023, 2024, 2025]) for (const [pattern, flags] of [["a", "uy"], ["a", "sdv"], ["(?:a)", ""], ["(?i:a)", ""], ["(?<a>x)|(?<a>y)", ""], ["(?<a>x)(?<a>y)", ""]]) add(pattern, flags, target);
const propertySource = await readFile(path.join(source, "internal/scanner/unicodeproperties.go"), "utf8");
for (const [, property] of propertySource.matchAll(/"([A-Za-z_][A-Za-z_0-9]*)"/g)) {
    add(`\\p{${property}}`, "u");
    add(`\\p{sc=${property}}`, "v");
    add(`\\p{${property.toLowerCase()}}`, "u");
}
const operands = ["a", "b", "\\d", "\\q{a}", "\\q{ab}", "\\q{}", "\\p{RGI_Emoji}", "[a]", "[]", "[^]", "[a-z]", "\\u{1F600}"];
for (const left of operands) for (const right of operands) for (const operation of ["", "&&", "--"]) for (const complement of ["", "^"]) add(`[${complement}${left}${operation}${right}]`, "v");
let seed = 0x63e27a19;
const random = max => {
    seed ^= seed << 13;
    seed ^= seed >>> 17;
    seed ^= seed << 5;
    return (seed >>> 0) % max;
};
const atoms = ["a", "b", "0", "-", "&", "!", "^", "$", "|", ".", "*", "+", "?", "(", ")", "[", "]", "{", "}", ":", ",", "\\d", "\\p{ASCII}", "\\P{RGI_Emoji}", "\\q{a|ab}", "\\1", "\\u0061", "\\c_", "(?<a>", "(?=", "(?:", "\\k<a>", "😀"];
for (let i = 0; i < Number(option("--fuzz", 20000)); i++) {
    let pattern = "";
    for (let j = 0, count = 1 + random(16); j < count; j++) pattern += atoms[random(atoms.length)];
    add(pattern, ["", "u", "v"][random(3)]);
}
if (process.argv.includes("--corpus")) {
    const files = (await run("rg", ["--files", "tsc/testdata/tests/cases"])).split(/\r?\n/).filter(file => /regexp|regularexpression/i.test(file)).sort();
    const extracted = await probe(oracle, [], files.map(file => ({ path: path.join(root, file).replaceAll("\\", "/") })));
    for (let i = 0; i < files.length; i++) for (const [index, pattern] of extracted[i].entries()) cases.push({ name: `${files[i]}:${index}`, text: Buffer.from(pattern).toString("base64"), target: 9999 });
}
const semanticDifferences = JSON.parse(await readFile(path.join(root, "csharp/tests/fixtures/regex/semantic-differences.json"), "utf8"));
const present = new Set(cases.map(c => `${c.text}:${c.target}`));
for (const difference of semanticDifferences) {
    const text = Buffer.from(difference.literal).toString("base64");
    if (!present.has(`${text}:${difference.target}`)) cases.push({ name: `semantic-${difference.reason}: ${difference.literal}`, text, target: difference.target });
}
if (process.argv.includes("--input")) cases = JSON.parse(await readFile(option("--input"), "utf8"));
if (process.argv.includes("--filter")) cases = cases.filter(c => c.name.includes(option("--filter")));
const expected = await probe(oracle, [], cases);
const actual = await probe(candidate, args, cases);
assert.equal(expected.length, cases.length);
assert.equal(actual.length, cases.length);
for (let i = 0; i < cases.length; i++) {
    const length = Buffer.from(cases[i].text, "base64").length;
    assert.ok(actual[i][1] >= 0 && actual[i][1] <= length, cases[i].name);
    for (const [, start, size] of actual[i][4]) assert.ok(start >= 0 && size >= 0 && start + size <= length, cases[i].name);
}
const rawDifferences = cases.flatMap((c, i) => JSON.stringify(expected[i]) === JSON.stringify(actual[i]) ? [] : [{ ...c, expected: expected[i], actual: actual[i] }]);
const permitted = [], failures = [];
for (const difference of rawDifferences) {
    const literal = Buffer.from(difference.text, "base64").toString();
    const fixture = semanticDifferences.find(f => f.literal === literal && f.target === difference.target);
    const byteDiagnostics = diagnostics => diagnostics.map(([code, start, length, ...rest]) => {
        const byteStart = Buffer.byteLength(literal.slice(0, start));
        return [code, byteStart, Buffer.byteLength(literal.slice(start, start + length)), ...rest];
    });
    if (
        fixture && JSON.stringify(difference.expected.slice(0, 4)) === JSON.stringify(difference.actual.slice(0, 4)) &&
        JSON.stringify(byteDiagnostics(fixture.goDiagnostics)) === JSON.stringify(difference.expected[4]) && JSON.stringify(byteDiagnostics(fixture.diagnostics)) === JSON.stringify(difference.actual[4])
    ) permitted.push({ ...difference, caseId: fixture.id, inputSha256: fixture.inputSha256, reason: fixture.reason, reproduction: fixture.reproduction });
    else failures.push(difference);
}
const nodeDifferences = [], nodeFailures = [];
for (let i = 0; i < cases.length; i++) {
    const literal = Buffer.from(cases[i].text, "base64").toString();
    if (actual[i][0] !== 13 || actual[i][1] !== Buffer.byteLength(literal) || actual[i][4].length) continue;
    const slash = literal.lastIndexOf("/");
    try {
        new RegExp(literal.slice(1, slash), literal.slice(slash + 1));
    }
    catch (error) {
        const entry = { ...cases[i], error: error.message };
        // Both are mandatory Unicode 15.1 Script aliases. V8/ICU currently omits
        // them, so Node is a secondary check, not the definition of the grammar.
        if (["/\\p{sc=Hrkt}/v", "/\\p{sc=Katakana_Or_Hiragana}/v"].includes(literal)) nodeDifferences.push({ ...entry, reason: "unicode-15.1-script-alias" });
        else nodeFailures.push(entry);
    }
}
await writeFile(path.join(output, "regex-node-differences.json"), JSON.stringify({ permitted: nodeDifferences, failures: nodeFailures }, null, 2));
const result = {
    timestamp: new Date().toISOString(),
    referenceRevision: reference.referenceRevision,
    runtimeKind: native ? "NativeAOT" : "CoreCLR",
    runtime: native ? await run(native, ["--native-check"]) : "CoreCLR",
    sdk: await run(dotnet, ["--version"], { cwd: path.join(root, "csharp") }),
    node: process.version,
    configuration: native ? { optimizationPreference: "Speed", ilcInstructionSet: "native", runtimeAsync: true } : { configuration: "Release", runtimeAsync: true },
    seed: "0x63e27a19",
    cases: cases.length,
    exact: cases.length - rawDifferences.length,
    permittedDifferences: permitted.length,
    failures: failures.length,
    nodePermittedDifferences: nodeDifferences.length,
    nodeFailures: nodeFailures.length,
    inputSha256: sha256(JSON.stringify(cases)),
    oracleSha256: sha256(await readFile(oracle)),
    candidateSha256: sha256(await readFile(native ?? args[0])),
};
await writeFile(path.join(output, "regex-results.json"), JSON.stringify(result, null, 2));
await writeFile(path.join(output, "regex-failures.json"), JSON.stringify(failures, null, 2));
await writeFile(path.join(output, "regex-permitted-differences.json"), JSON.stringify(permitted, null, 2));
console.log(JSON.stringify(result, null, 2));
for (const failure of failures.slice(0, 12)) console.log(JSON.stringify(failure));
if (failures.length || nodeFailures.length) process.exitCode = 1;
for (const failure of nodeFailures.slice(0, 10)) console.log(JSON.stringify(failure));
// These are semantic assertions, independent of the reference and its recursion
// implementation. Each expression has an exact known token and diagnostic count.
const deep = [];
for (const depth of [1000, 10000, 100000]) {
    for (const pattern of ["(".repeat(depth) + "a" + ")".repeat(depth), "[".repeat(depth) + "a" + "]".repeat(depth)]) deep.push({ name: `deep-${depth}`, text: Buffer.from(`/${pattern}/v`).toString("base64"), target: 9999 });
}
deep.push({ name: "deep-named-100000", text: Buffer.from("/" + Array.from({ length: 100000 }, (_, i) => `(?<a${i}>`).join("") + "a" + ")".repeat(100000) + "/v").toString("base64"), target: 9999 });
for (
    const [name, literal, diagnosticCount] of [
        ["unclosed-groups-100000", "/" + "(".repeat(100000) + "a/v", 100000],
        ["unclosed-sets-100000", "/" + "[".repeat(100000) + "]/v", 99999],
        ["long-quantifier", "/a{" + "9".repeat(100000) + "," + "8".repeat(100000) + "}/u", 1],
        ["long-backreference", "/\\" + "9".repeat(100000) + "/u", 1],
    ]
) deep.push({ name, text: Buffer.from(literal).toString("base64"), target: 9999, diagnosticCount });
for (const [i, result] of (await probe(candidate, args, deep)).entries()) {
    assert.equal(result[1], Buffer.from(deep[i].text, "base64").length);
    assert.equal(result[4].length, deep[i].diagnosticCount ?? 0);
}
console.log(`Deep-input assertions: ${deep.length} passed (up to 100,000 nested groups/sets).`);
result.deepInputAssertions = deep.length;
await writeFile(path.join(output, "regex-results.json"), JSON.stringify(result, null, 2));
