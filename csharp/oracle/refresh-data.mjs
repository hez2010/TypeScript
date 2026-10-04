// Optional, explicit refresh of frozen compatibility data; requires the pinned Go oracle.
import assert from "node:assert/strict";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { prepareOracle } from "./validate.mjs";
import { root, run } from "../tools/common.mjs";

const index = process.argv.indexOf("--go");
const oracle = await prepareOracle(index < 0 ? "go" : process.argv[index + 1]);
for (const name of ["go-casing", "normalization", "regexp-properties", "locales"]) {
    const raw = JSON.parse(await run(oracle.go, ["-C", oracle.source, "run", path.join(root, "csharp/oracle/data", name + ".go")], { env: oracle.env }));
    const version = name === "locales" ? /golang.org\/x\/text (v\S+)/.exec(await readFile(path.join(oracle.source, "go.mod"), "utf8"))[1] : raw.version;
    if (name === "locales") {
        const module = JSON.parse(await run(oracle.go, ["-C", oracle.source, "list", "-m", "-json", "golang.org/x/text"], { env: oracle.env }));
        const tables = await readFile(path.join(module.Dir, "internal/language/tables.go"), "utf8");
        const lookup = await readFile(path.join(module.Dir, "internal/language/lookup.go"), "utf8");
        raw.Variants = [...tables.split("var variantIndex = ")[1].split("\n}")[0].matchAll(/"([a-z0-9]+)":/g)].map(m => m[1]);
        raw.Grandfathered = [...lookup.split("grandfatheredMap = ")[1].split("\n\t}")[0].matchAll(/\[maxLen\]byte\{([^}]+)\}/g)].map(m => [...m[1].matchAll(/'(.)'/g)].map(c => c[1]).join(""));
    }
    const snapshot = { version, data: raw };
    const target = path.join(root, "csharp/data", name + ".json");
    if (process.argv.includes("--check")) assert.deepEqual(snapshot, JSON.parse(await readFile(target, "utf8")), name);
    else await writeFile(target, JSON.stringify(snapshot, null, 2) + "\n");
}
const enums = JSON.parse(await run(oracle.go, ["-C", oracle.source, "run", "internal/api/enum_values_generated.go"], { env: oracle.env }));
const target = path.join(root, "csharp/data/enum-values.json");
if (process.argv.includes("--check")) assert.deepEqual(enums, JSON.parse(await readFile(target, "utf8")));
else await writeFile(target, JSON.stringify(enums, null, 2) + "\n");
console.log("Frozen data matches the pinned oracle.");
