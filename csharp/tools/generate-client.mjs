import {
    copyFile,
    cp,
    mkdir,
} from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
    json,
    output,
    root,
    run,
} from "./common.mjs";

export async function generateClient(directory = path.join(output, "client")) {
    directory = path.resolve(directory);
    const client = path.join(directory, "packages/typescript"), generators = path.join(directory, "tools/scripts/tsc");
    await mkdir(client, { recursive: true });
    await mkdir(generators, { recursive: true });
    for (const name of ["src", "lib", "bin", "vendor", "scripts", "package.json", "tsconfig.json", "tsconfig.base.json"]) await cp(path.join(root, "packages/typescript", name), path.join(client, name), { recursive: true });
    for (const name of ["generate-encoder.ts", "generate-ts-ast.ts", "schema.ts", "ast.json", "package.json"]) await copyFile(path.join(root, "tools/scripts/tsc", name), path.join(generators, name));
    await copyFile(path.join(root, ".dprint.jsonc"), path.join(directory, ".dprint.jsonc"));
    const environment = { ...process.env, PATH: path.join(root, "node_modules/.bin") + path.delimiter + process.env.PATH };
    for (const generator of ["generate-ts-ast.ts", "generate-encoder.ts"]) await run(process.execPath, ["--conditions", "@typescript/source", path.join(generators, generator)], { cwd: directory, env: environment });
    await run(process.execPath, [path.join(root, "node_modules/typescript/lib/tsc.js"), "-b", path.join(client, "tsconfig.json"), "--force"]);
    await json(path.join(directory, "client-generation.json"), { protocolVersion: 9, coordinateContract: "UTF-8/WTF-8 byte offsets", source: "packages/typescript", generators: ["generate-ts-ast.ts", "generate-encoder.ts"] });
    return client;
}

if (path.resolve(process.argv[1] ?? "") === fileURLToPath(import.meta.url)) {
    const index = process.argv.indexOf("--directory");
    console.log(await generateClient(index < 0 ? undefined : process.argv[index + 1]));
}
