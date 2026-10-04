// Explicit oracle-only entry point. Product builds never import this module.
import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { output, root, run, referenceRevision, json, sha256 } from "../tools/common.mjs";

export async function prepareOracle(go = "go") {
    const env = { ...process.env, GOWORK: "off", GOTOOLCHAIN: "local" };
    const version = await run(go, ["version"], { env });
    assert.match(version, /go1\.27\.1 /, "The oracle requires Go 1.27.1");
    await mkdir(path.join(output, "archived-oracle"), { recursive: true });
    const directory = await mkdtemp(path.join(output, "archived-oracle/run-"));
    const archive = path.join(directory, "source.tar");
    await run("git", ["archive", "--format=tar", `--output=${archive}`, referenceRevision]);
    await run("tar", ["-xf", archive, "-C", directory]);
    await json(path.join(directory, "oracle.json"), { revision: referenceRevision, go: version, archiveSha256: sha256(await readFile(archive)) });
    return { directory, source: path.join(directory, "tsc"), go, env };
}

if (path.resolve(process.argv[1] ?? "") === fileURLToPath(import.meta.url)) {
    const index = process.argv.indexOf("--go");
    const oracle = await prepareOracle(index < 0 ? "go" : process.argv[index + 1]);
    const result = await run(oracle.go, ["-C", oracle.source, "test", "-mod=readonly", "./..."], { env: oracle.env });
    console.log(result);
    console.log(`Pinned oracle validation: ${oracle.directory}`);
}
