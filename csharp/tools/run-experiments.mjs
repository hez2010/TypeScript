import { spawn } from "node:child_process";
import {
    copyFile,
    mkdir,
    readFile,
    writeFile,
} from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { performance } from "node:perf_hooks";
import {
    json,
    output,
    referenceRevision,
    root,
    run,
    sha256,
} from "./common.mjs";

const option = name => {
    const i = process.argv.indexOf(name);
    return i < 0 ? undefined : process.argv[i + 1];
};
const go = option("--go") ?? "go";
const dotnet = option("--dotnet") ?? "dotnet";
const instructionSet = "native";
const variant = "-host";
const nativeOutput = path.join(output, "native" + variant);
const resultsPath = path.join(output, `experiments${variant}.json`);
const rid = `${process.platform === "win32" ? "win" : process.platform === "darwin" ? "osx" : process.platform}-${process.arch}`;
if (await run("git", ["rev-parse", "HEAD"]) !== referenceRevision) throw new Error("Reference revision changed");
await run(process.execPath, [path.join(root, "csharp/tools/generate-schema.mjs"), "--check"]);
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const referenceSource = path.join(output, reference.sourceRelativePath ?? "oracle-source");
const probeDirectory = path.join(referenceSource, "tsc/cmd/rewrite-probe");
await copyFile(path.join(root, "csharp/oracle/main.go"), path.join(probeDirectory, "main.go"));
const suffix = process.platform === "win32" ? ".exe" : "";
const probe = path.join(output, "oracle-probe" + suffix);
await run(go, ["-C", path.join(referenceSource, "tsc"), "build", "-mod=readonly", "-buildvcs=false", "-trimpath", "-o", probe, "./cmd/rewrite-probe"], { env: { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" } });
const publish = await run(dotnet, ["publish", "tests/TypeScript.Compatibility", "-c", "Release", "-r", rid, "-p:RestoreLockedMode=true", "-p:IlcInstructionSet=native", "-o", nativeOutput], { cwd: path.join(root, "csharp"), env: { ...process.env, DOTNET_CLI_UI_LANGUAGE: "en-US" } });
await writeFile(path.join(output, `publish${variant}.log`), publish);
console.log(publish);
const candidate = path.join(nativeOutput, "TypeScript.Compatibility" + suffix);
const map = await readFile(path.join(root, `csharp/tests/TypeScript.Compatibility/obj/Release/net11.0/${rid}/native/TypeScript.Compatibility.map.xml`), "utf8");
const methods = [...map.matchAll(/<MethodCode Name="([^"]+)" Length="(\d+)" Hash="([^"]+)"/g)].map(([, name, length, hash]) => ({ name: name.replaceAll("&lt;", "<").replaceAll("&gt;", ">"), bytes: Number(length), hash }));
const nativeCode = {
    totalMethodBytes: methods.reduce((sum, method) => sum + method.bytes, 0),
    measuredHelpers: methods.filter(method => /TypeRelations__Assignable|StrictAssignment__Relates|Storage_Arena/.test(method.name)),
};

const runtime = await run(candidate, ["--native-check"]);
// Check the image as well as the executing runtime; an apphost alone also has
// no CLR header and therefore would not establish NativeAOT execution.
if (process.platform === "win32") {
    const image = await readFile(candidate);
    const pe = image.readUInt32LE(0x3C);
    if (image.readUInt32LE(pe) !== 0x4550 || image.readUInt16LE(pe + 24) !== 0x20B || image.readUInt32LE(pe + 24 + 112 + 14 * 8) !== 0) throw new Error("Candidate is not a native PE32+ image");
}
const texts = [Buffer.alloc(0), Buffer.from("ASCII\r\n\t\0"), Buffer.from("日本語 e\u0301 😀\u2028\u2029"), Buffer.from([0xED, 0xA0, 0x80, 0xED, 0xB0, 0x80]), Buffer.from([0xF0, 0x9F, 0xFF, 0x80])];
for (let value = 0; value < 256; value++) texts.push(Buffer.from([value]));
for (let value = 0xD800; value <= 0xDFFF; value++) texts.push(Buffer.from([0xED, 0x80 | (value >> 6 & 63), 0x80 | (value & 63)]));
let seed = 0x545343;
const random = () => {
    seed ^= seed << 13;
    seed ^= seed >>> 17;
    seed ^= seed << 5;
    return seed >>> 0;
};
for (let i = 0; i < 1024; i++) texts.push(Buffer.from(Array.from({ length: random() % 64 + 1 }, () => random() & 255)));
const numberBits = value => {
    const bytes = Buffer.alloc(8);
    bytes.writeDoubleBE(value);
    return bytes.toString("hex");
};
const values = [0, -0, 1, -1, 2, -2, 3, 0.5, -0.5, 1.5, -1.5, 31, 32, 33, 53, 63, 2147483647, 2147483648, 4294967295, 4294967296, 9007199254740991, Number.MIN_VALUE, Number.MAX_VALUE, Infinity, -Infinity, NaN];
const numbers = values.flatMap(x => values.map(y => [numberBits(x), numberBits(y)]));
const powers = values.flatMap(x => values.map(y => Number.isNaN(x ** y) ? "nan" : numberBits(x ** y)));
const types = ["any", "unknown", "never", "string", "number", "bigint", "boolean", "undefined", "null", "void", "symbol", "object", "'hello'", "'other'", "42", "43", "true", "false", "string | number", "'hello' | 'other'", "number | undefined", "null | undefined", "T12 | T13", "T18 | never", "unknown | string", "any | number", "boolean | null", "(T22)"];
const files = [];
const paths = ["tsc/testdata/fixtures/compiler/checker.ts", "tsc/testdata/tests/cases/compiler/assignmentCompat1.ts"];
for (const file of paths) {
    // Missing fixtures must not silently reduce coverage.
    files.push({ name: file, text: (await readFile(path.join(root, file))).toString("base64") });
}
for (
    const [name, text] of [
        ["unicode.ts", "const 日本語 = '😀'; const lone = '\\ud800';\n"],
        ["malformed.ts", "const x = ; function f( {\n"],
        ["deep.ts", `type Deep = ${"(".repeat(1000)}string${")".repeat(1000)};\n`],
        ["graph/a.ts", "export type Value = string | number; export const value: Value = 42;\n"],
        ["graph/b.ts", "import { value, type Value } from './a'; export const copy: Value = value;\n"],
    ]
) files.push({ name, text: Buffer.from(text).toString("base64") });
const input = { texts: texts.map(text => text.toString("base64")), numbers, powers, types, files };
const inputPath = path.join(output, "input.json");
const expectedPath = path.join(output, "expected.json");
await json(inputPath, input);
const expectedText = await new Promise((resolve, reject) => {
    const child = spawn(probe, [], { windowsHide: true, cwd: root });
    const stdout = [], stderr = [];
    child.stdout.on("data", chunk => stdout.push(chunk));
    child.stderr.on("data", chunk => stderr.push(chunk));
    child.on("error", reject);
    child.on("close", code => code === 0 ? resolve(Buffer.concat(stdout)) : reject(new Error(`Oracle probe ${code}: ${Buffer.concat(stderr)}`)));
    child.stdin.end(JSON.stringify(input));
});
await writeFile(expectedPath, expectedText);
const verification = await run(candidate, ["--verify", inputPath, expectedPath]);
console.log(verification);
const benchmarkText = await run(candidate, ["--benchmark", inputPath, expectedPath]);
const benchmarks = JSON.parse(benchmarkText.slice(benchmarkText.indexOf("{")));
const startup = [];
for (let i = 0; i < 20; i++) {
    const start = performance.now();
    await run(candidate, ["--startup"]);
    startup.push(performance.now() - start);
}
await json(resultsPath, {
    referenceRevision,
    timestamp: new Date().toISOString(),
    os: os.version(),
    cpu: os.cpus()[0]?.model,
    architecture: process.arch,
    instructionSet,
    optimizationPreference: "Speed",
    nodeVersion: process.version,
    sdk: await run(dotnet, ["--version"], { cwd: path.join(root, "csharp") }),
    runtime,
    verification,
    oracle: reference,
    probeSha256: sha256(await readFile(probe)),
    probeSourceSha256: sha256(await readFile(path.join(root, "csharp/oracle/main.go"))),
    candidateSha256: sha256(await readFile(candidate)),
    candidateBytes: (await readFile(candidate)).length,
    nativeCode,
    inputSha256: sha256(await readFile(inputPath)),
    expectedSha256: sha256(expectedText),
    semanticDifferencesSha256: sha256(await readFile(path.join(output, "semantic-differences.json"))),
    fixtures: files.map(file => ({ name: file.name, sha256: sha256(Buffer.from(file.text, "base64")) })),
    startupWallMilliseconds: startup,
    benchmarks,
});
console.log(`Saved native execution and measurements to ${resultsPath}`);
