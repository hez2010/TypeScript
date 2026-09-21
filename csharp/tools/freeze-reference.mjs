// Run from any directory: node csharp/tools/freeze-reference.mjs --go <go.exe>
// --events accepts gotestsum JSONL; otherwise the complete Go suite is executed.
import { spawn } from "node:child_process";
import {
    createReadStream,
    createWriteStream,
} from "node:fs";
import {
    copyFile,
    mkdir,
    mkdtemp,
    readFile,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import { createInterface } from "node:readline";
import {
    hashFiles,
    json,
    output,
    referenceRevision,
    root,
    run,
    sha256,
    trackedFiles,
} from "./common.mjs";

const option = name => {
    const index = process.argv.indexOf(name);
    return index < 0 ? undefined : process.argv[index + 1];
};
const go = option("--go") ?? "go";
const revision = await run("git", ["rev-parse", referenceRevision]);
if (await run("git", ["diff", referenceRevision, "--", "tsc", "tools", "Herebyfile.mjs", "package.json", "package-lock.json", "go.work"])) {
    throw new Error("Reference sources have changed. Reconcile the pin explicitly before recording evidence.");
}
const goVersion = await run(go, ["version"]);
if (!goVersion.includes("go1.27.1 ")) throw new Error(`Expected Go 1.27.1: ${goVersion}`);
const archive = path.join(output, "reference.tar");
await mkdir(output, { recursive: true });
// A new extraction prevents stale or untracked files entering the oracle.
const source = await mkdtemp(path.join(output, "oracle-source-"));
await run("git", ["archive", "--format=tar", `--output=${archive}`, referenceRevision, "tsc"]);
await run("tar", ["-xf", archive, "-C", source]);
const probeDir = path.join(source, "tsc/cmd/rewrite-probe");
await mkdir(probeDir, { recursive: true });
await copyFile(path.join(root, "csharp/oracle/main.go"), path.join(probeDir, "main.go"));
const env = { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" };
const suffix = process.platform === "win32" ? ".exe" : "";
const oracle = path.join(output, "oracle" + suffix);
const probe = path.join(output, "oracle-probe" + suffix);
await run(go, ["-C", path.join(source, "tsc"), "build", "-mod=readonly", "-buildvcs=false", "-trimpath", "-o", oracle, "./cmd/tsc"], { env });
await run(go, ["-C", path.join(source, "tsc"), "build", "-mod=readonly", "-buildvcs=false", "-trimpath", "-o", probe, "./cmd/rewrite-probe"], { env });

const files = await trackedFiles();
const contracts = [];
const packages = new Map();
const matchers = {
    environment: /(?:os\.(?:Getenv|LookupEnv)|GetEnvironmentVariable|getEnv)\("([^"]+)"/g,
    method: /Method\w*\s+Method\s*=\s*"([^"]+)"/g,
    option: /Name:\s*"([^"]+)"/g,
    native: /(?:#cgo[^\n]*|\/\/go:(?:build|embed|generate|linkname)[^\n]*|\/\/sys[^\n]*)/g,
};
for (const file of files.filter(file => /^(tsc\/(internal|cmd)\/.*\.go|tools\/scripts\/tsc\/.*|packages\/typescript\/src\/.*)$/.test(file))) {
    const content = await readFile(path.join(root, file), "utf8");
    if (file.endsWith(".go")) {
        const name = path.posix.dirname(file);
        if (!packages.has(name)) packages.set(name, { name, files: [], status: "unported" });
        packages.get(name).files.push({ path: file, sha256: sha256(content), test: file.endsWith("_test.go") });
    }
    if (file.endsWith("_test.go")) continue;
    for (const [kind, regex] of Object.entries(matchers)) {
        if (kind === "option" && !file.includes("tsoptions")) continue;
        for (const match of content.matchAll(regex)) {
            contracts.push({ kind, value: match[1] ?? match[0], file, line: content.slice(0, match.index).split("\n").length });
        }
    }
}
const hereby = await readFile(path.join(root, "Herebyfile.mjs"), "utf8");
const platforms = [...hereby.slice(hereby.indexOf("const platforms = [")).split("];", 1)[0].matchAll(/\{ os: "([^"]+)", arch: "([^"]+)"([^}]+)?\}/g)].map(([, os, arch, flags]) => ({
    os,
    arch,
    vsix: flags?.includes("vsix: true") ?? false,
    alpine: flags?.includes("alpine: true") ?? false,
    status: "blocking-until-native-publish-and-execution",
    minimumOS: "unverified",
    minimumCPU: "unverified",
}));
await json(path.join(root, "csharp/compatibility/contracts.generated.json"), {
    referenceRevision,
    extraction: "Source inventory, not a claim that every matched method is externally reachable. See contracts.md for dispatch boundaries.",
    platforms,
    contracts,
    packages: [...packages.values()],
    assetSets: await Promise.all([
        ["test-inputs", /^tsc\/testdata\/tests\/cases\//],
        ["baselines", /^tsc\/testdata\/baselines\/reference\//],
        ["bundled", /^tsc\/internal\/(bundled|locale)\//],
        ["generators", /^tools\//],
        ["clients", /^packages\//],
    ].map(async ([name, pattern]) => {
        const selected = files.filter(file => pattern.test(file));
        return { name, count: selected.length, sha256: await hashFiles(selected) };
    })),
});

let events = option("--events");
if (!events) {
    events = path.join(output, "go-tests.jsonl");
    const sink = createWriteStream(events);
    const child = spawn(go, ["-C", path.join(source, "tsc"), "test", "-json", "./..."], { env, windowsHide: true });
    child.stdout.pipe(sink);
    child.stderr.pipe(process.stderr);
    const completed = new Promise((resolve, reject) => child.on("error", reject).on("close", code => code === 0 ? resolve() : reject(new Error(`Go tests: ${code}`))));
    const written = new Promise((resolve, reject) => sink.on("finish", resolve).on("error", reject));
    await Promise.all([completed, written]);
}
const totals = { pass: 0, skip: 0, fail: 0 };
const skipped = [];
const identities = [];
const lastOutput = new Map();
for await (const line of createInterface({ input: createReadStream(events), crlfDelay: Infinity })) {
    const event = JSON.parse(line);
    if (!event.Test) continue;
    const key = `${event.Package}/${event.Test}`;
    if (event.Action === "output" && event.Output?.trim() && !/^\s*(---|===)/.test(event.Output)) lastOutput.set(key, event.Output.trim());
    if (event.Action in totals) {
        totals[event.Action]++;
        identities.push(`${event.Action}\0${key}`);
        if (event.Action === "skip") {
            const reason = lastOutput.get(key) ?? "No reason captured";
            const category = /Known failing|not implemented|not supported|unsupported|Unimplemented|TODO|Skipping test|Skipped submodule|nondeterministic|No files are emitted|missing statements/i.test(reason) ? "reference-exclusion"
                : /replay|REPLAY/i.test(reason) ? "missing-replay"
                : /platform|Windows|linux|darwin|symlink|permission|not running|CI|CI_ONLY|TYPESCRIPT_SKIP|short mode|fast recursive backends/i.test(reason) ? "host-or-environment"
                : /_test\.go:\d+:$/.test(reason) ? "reference-skip-without-reason"
                : "requires-review";
            skipped.push({ package: event.Package, test: event.Test, category, reason });
        }
        lastOutput.delete(key);
    }
}
if (totals.fail || !totals.pass) throw new Error(`Invalid reference results: ${JSON.stringify(totals)}`);
await json(path.join(root, "csharp/compatibility/skips.generated.json"), { referenceRevision, totals, identitiesSha256: sha256(identities.sort().join("\n")), skipped: skipped.sort((a, b) => `${a.package}/${a.test}`.localeCompare(`${b.package}/${b.test}`, "en")) });
await writeFile(path.join(output, "test-identities.txt"), identities.join("\n"));
await json(path.join(output, "reference.json"), {
    referenceRevision,
    sourceRelativePath: path.relative(output, source),
    goVersion,
    nodeVersion: process.version,
    host: `${process.platform}-${process.arch}`,
    oracleSha256: sha256(await readFile(oracle)),
    probeSha256: sha256(await readFile(probe)),
    probeSourceSha256: sha256(await readFile(path.join(probeDir, "main.go"))),
    archiveSha256: sha256(await readFile(archive)),
    eventsSha256: sha256(await readFile(events)),
    totals,
});
console.log(JSON.stringify({ oracle, probe, totals, platforms: platforms.length }));
