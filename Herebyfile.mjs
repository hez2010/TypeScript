import { task } from "hereby";
import {
    copyFile,
    cp,
    mkdir,
    readFile,
} from "node:fs/promises";
import path from "node:path";
import { parseArgs } from "node:util";
import {
    buildClient,
    buildCompiler,
    buildTests,
    checkApiProtocol,
    dotnet,
    execute,
    generate,
    node,
    npm,
    testClient,
    testCompiler,
    watchCompiler,
} from "./csharp/tools/product.mjs";

const { values: options } = parseArgs({
    options: {
        "tests": { type: "string", short: "t", default: ".*" },
        "debug": { type: "boolean" },
        "managed": { type: "boolean" },
        "rid": { type: "string" },
        "codesign-identity": { type: "string" },
        "version": { type: "string" },
        "directory": { type: "string" },
        "forRelease": { type: "boolean" },
        "setPrerelease": { type: "string" },
        "vscodeTypescriptRelease": { type: "boolean" },
        "require-signed": { type: "boolean" },
        "instruction-set": { type: "string" },
        "certificate": { type: "string" },
        "timestamp-url": { type: "string" },
        "all": { type: "boolean" },
    },
    strict: false,
    allowPositionals: true,
});
for (const flag of ["race", "noembed", "respectGoEnv", "coverage", "concurrentTestPrograms"]) if (flag in options) throw new Error(`--${flag} belongs to the archived Go build; see csharp/oracle/README.md.`);

export const build = task({ name: "build", description: "Builds the C# compiler in built/local.", run: () => buildCompiler({ debug: !!options.debug }) });
export const local = task({ name: "local", dependencies: [build] });
export const tsgo = task({ name: "tsgo", description: "Compatibility alias for the C# compiler build.", dependencies: [build] });
export const tscBuild = task({ name: "tsc:build", dependencies: [build] });
export const buildWatch = task({ name: "build:watch", run: () => watchCompiler(!!options.debug) });
export const buildAPI = task({ name: "build:api", run: buildClient });
export const buildCSharp = task({ name: "csharp:build", run: buildTests });
export const buildAPITests = task({
    name: "build:api:test",
    run: async () => {
        await node("packages/typescript/scripts/generateSync.ts");
        await node("node_modules/typescript/lib/tsc.js", ["-b", "packages/typescript/test"]);
    },
});
export const testTsc = task({ name: "test:tsc", dependencies: [buildCSharp], run: () => testCompiler(String(options.tests)) });
export const testAPI = task({ name: "test:api", dependencies: [build], run: testClient });
export const testExtension = task({
    name: "test:extension",
    run: async () => {
        await npm(["run", "-w", "native-preview", "build"]);
        await npm(["run", "-w", "native-preview", "test"]);
    },
});
export const test = task({ name: "test", dependencies: [testTsc, testAPI] });
export const testAll = task({ name: "test:all", dependencies: [test, testExtension] });
export const generateAll = task({ name: "generate", run: generate });
export const generateEnums = task({ name: "generate:enums", run: () => node("csharp/tools/generate-client-enums.mjs") });
export const generateAST = task({ name: "generate:ast", run: () => node("tools/scripts/tsc/generate.ts") });
export const generateAPI = task({ name: "generate:api", description: "Checks the maintained C# API wire declarations.", run: checkApiProtocol });
export const generateExtension = task({ name: "generate:extension", run: () => npm(["run", "-w", "native-preview", "generateLocBundle"]) });
export const checkGenerators = task({ name: "generate:check", run: () => node("csharp/tools/verify-generators.mjs") });
export const checkPlatforms = task({ name: "typescript:check-platforms", run: () => node("csharp/tools/check-platforms.mjs") });

const packageArguments = async () => [
    ...(options.rid ? ["--rid", String(options.rid)] : []),
    ...(options["codesign-identity"] ? ["--codesign-identity", String(options["codesign-identity"])] : []),
    ...(options.managed ? ["--managed"] : []),
    ...(options.version || options.setPrerelease ? ["--version", String(options.version ?? (await readFile("csharp/version.txt", "utf8")).trim().split("-", 1)[0] + "-" + options.setPrerelease)] : []),
    ...(options.directory ? ["--directory", String(options.directory)] : []),
    ...(options.forRelease || options["require-signed"] ? ["--require-signed"] : []),
    ...(options["instruction-set"] ? ["--instruction-set", String(options["instruction-set"])] : []),
    ...(options.certificate ? ["--certificate", String(options.certificate)] : []),
    ...(options["timestamp-url"] ? ["--timestamp-url", String(options["timestamp-url"])] : []),
];
export const packageCompiler = task({ name: "package", description: "Publishes and packs C# distribution artifacts; native binaries are never executed.", run: async () => node("csharp/tools/package.mjs", await packageArguments()) });
export const packageCSharp = task({ name: "csharp:package", dependencies: [packageCompiler] });
export const nativePreview = task({ name: "native-preview", dependencies: [packageCompiler] });
export const nativePreviewRelease = task({
    name: "typescript:release",
    run: async () => {
        if (!options.forRelease) throw new Error("typescript:release requires --forRelease and a trusted signing certificate.");
        await node("csharp/tools/package.mjs", await packageArguments());
    },
});
export const testPackage = task({
    name: "test:package",
    run: async () => {
        await node("csharp/tools/package.mjs", ["--managed"]);
        await node("csharp/tools/package-tests.mjs");
    },
});
export const testCSharpPackage = task({ name: "csharp:test-package", dependencies: [testPackage] });
export const verifyPackage = task({ name: "package:verify", run: () => node("csharp/tools/verify-package.mjs") });
export const validate = task({
    name: "validate",
    run: async () => {
        await node("csharp/tools/verify-generators.mjs");
        await buildCompiler();
        await buildTests();
        await testCompiler();
        await testClient();
        await npm(["run", "-w", "native-preview", "build"]);
        await npm(["run", "-w", "native-preview", "test"]);
        await node("csharp/tools/package.mjs", ["--managed"]);
        await node("csharp/tools/package-tests.mjs");
        await node("csharp/tools/vscode-integration.mjs");
    },
});
export const allChecks = task({ name: "all-checks", dependencies: [validate] });

export const format = task({ name: "format", run: () => node("node_modules/dprint/bin.cjs", ["fmt"]) });
export const checkFormat = task({ name: "check:format", run: () => node("node_modules/dprint/bin.cjs", ["check"]) });
export const lint = task({ name: "lint", dependencies: [buildCSharp, buildAPI] });
export const tidy = task({ name: "tidy", run: () => execute(dotnet, ["restore", "csharp/TypeScript.slnx"]) });
export const generateVendor = task({
    name: "generate:vendor",
    run: async () => {
        for (const file of ["package.json", "README.md", "License.txt", "lib", "typings"]) await cp(path.join("node_modules/vscode-jsonrpc", file), path.join("packages/typescript/vendor/vscode-jsonrpc", file), { recursive: true });
    },
});
export const packageVsix = task({
    name: "vscode-typescript:pack",
    run: async () => {
        await node("csharp/tools/package.mjs", await packageArguments());
        await node("csharp/tools/package-vsix.mjs", [
            ...(options.rid ? ["--rid", String(options.rid)] : []),
            ...(options.managed ? ["--managed"] : []),
            ...(options.forRelease ? ["--for-release"] : []),
            ...(options.directory ? ["--directory", String(options.directory)] : []),
        ]);
    },
});
export const vscodeRelease = task({
    name: "vscode-typescript:release",
    run: () => {
        throw new Error("Production VSIX signing must be configured for the C# distribution. Use vscode-typescript:pack for local validation; see docs/csharp-phase-9-progress.md.");
    },
});
export const lib = task({
    name: "lib",
    run: async () => {
        const { readdir } = await import("node:fs/promises");
        await mkdir("built/local", { recursive: true });
        for (const name of await readdir("tsc/internal/bundled/libs")) await copyFile(path.join("tsc/internal/bundled/libs", name), path.join("built/local", name));
    },
});
export const oracle = task({ name: "oracle:validate", description: "Explicitly validates the archived Go oracle; requires its pinned Go toolchain.", run: () => node("csharp/oracle/validate.mjs") });
