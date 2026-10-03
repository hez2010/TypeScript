export function resolutionCases() {
    const ref = name => ({ $ref: name });
    const request = (method, params = {}, save) => ({ method, params, ...(save ? { save } : {}) });
    const context = { snapshot: ref("snapshot.snapshot"), project: ref("snapshot.operation.createdPrograms.0") };
    const query = (method, params = {}) => request(method, { ...context, ...params });
    const files = {
        "/p/main.ts": '/// <reference types="foo" />\n/// <reference types="foo" resolution-mode="import" />\nimport { x } from "./dep.js"; import p = require("pkg"); export { x } from "./dep.ts"; import("pkg"); type T = import("pkg").T; declare module "pkg" { interface Extra {} } declare global { interface Global {} }',
        "/p/dep.ts": 'export const x = "日本語";',
        "/p/node_modules/pkg/package.json": '{"name":"pkg","version":"1.2.3","exports":{"import":"./esm.d.mts","require":"./cjs.d.cts"}}',
        "/p/node_modules/pkg/esm.d.mts": 'export const p: number; export type T = string;',
        "/p/node_modules/pkg/cjs.d.cts": 'export const p: number; export type T = number;',
        "/p/node_modules/@types/foo/package.json": '{"name":"@types/foo","version":"2.3.4","types":"./index.d.ts"}',
        "/p/node_modules/@types/foo/index.d.ts": 'declare const foo: number;',
    };
    const cases = ["commonjs", "module", "absent"].flatMap(packageType => ["nodenext", "bundler", "node10"].map(resolution => ({
        name: `resolution-${packageType}-${resolution}`, cwd: "/p", files: { ...files,
            ...(packageType === "absent" ? {} : { "/p/package.json": JSON.stringify({ type: packageType }) }) },
        requests: [request("createSnapshot", { createPrograms: [{ rootFiles: ["/p/main.ts"], compilerOptions: {
            noLib: true, module: resolution === "bundler" ? 200 : resolution === "node10" ? 1 : 199,
            moduleResolution: resolution === "bundler" ? 100 : resolution === "node10" ? 2 : 99,
        } }] }, "snapshot"),
        query("getConfigFileNames"), query("getConfigSourceFile", { file: "/p/tsconfig.json" }),
        ...[...Object.keys(files), "/missing.ts"].map(file => query("getSourceFileMetadata", { file })),
        ...Array.from({ length: 9 }, (_, i) => query("getModeForResolutionAtIndex", { file: "/p/main.ts", index: i - 1 })),
        ...["./dep.js", "./dep.ts", "pkg", "missing"].flatMap(moduleName => [0, 1, 99, 42].map(mode => query("getResolvedModule", { file: "/p/main.ts", moduleName, mode }))),
        ...["foo", "missing"].flatMap(typeDirectiveName => [0, 1, 99, 42].flatMap(mode => [
            query("getResolvedTypeReferenceDirective", { file: "/p/main.ts", typeDirectiveName, mode }),
            query("getResolvedTypeReferenceDirectiveFromTypeReferenceDirective", { sourceFile: "/p/main.ts", typeDirectiveName, resolutionMode: mode }),
        ])),
        ...Array.from({ length: 55 }, (_, i) => `${i}.0./p/main.ts`).flatMap(handle => [
            query("getModeForUsageLocation", { file: "/p/main.ts", usage: handle }),
            query("getResolvedModuleFromModuleSpecifier", { moduleSpecifier: handle }),
            query("getResolvedModuleFromModuleSpecifier", { moduleSpecifier: handle, sourceFile: "/p/dep.ts" }),
        ]),
        ...["getModeForResolutionAtIndex", "getResolvedModule", "getResolvedTypeReferenceDirective", "getModeForUsageLocation"].map(method => query(method, { file: { uri: "file:///missing.ts" }, usage: "bad" })),
        query("getResolvedTypeReferenceDirectiveFromTypeReferenceDirective", { sourceFile: "/missing.ts" }),
        request("release", { snapshot: ref("snapshot.snapshot") }),
        ],
    })));
    const configured = { snapshot: ref("snapshot.snapshot"), project: "/p/tsconfig.json" };
    cases.push({ name: "resolution-config-sources", cwd: "/p", files: {
        "/p/main.ts": "export {};", "/p/tsconfig.json": '{"extends":["./base.json","./extra.json"],"files":["main.ts"]}',
        "/p/base.json": '{"extends":"./shared.json","compilerOptions":{"noLib":true}}',
        "/p/extra.json": '{"extends":"./shared.json","compilerOptions":{"strict":true}}', "/p/shared.json": '{"compilerOptions":{"module":"esnext"}}',
    }, requests: [request("createSnapshot", { openProjects: ["/p/tsconfig.json"] }, "snapshot"), request("getConfigFileNames", configured),
        ...["tsconfig.json", "base.json", "extra.json", "shared.json", "missing.json", "main.ts"].map(name => request("getConfigSourceFile", { ...configured, file: { uri: `file:///p/${name}` } })),
        request("updateSnapshot", { snapshot: ref("snapshot.snapshot"), changes: { fileSystem: { kind: "layer", files: { "/p/base.json": '{"compilerOptions":{"noLib":true,"strict":true}}' } }, ensurePrograms: true } }, "next"),
        request("getConfigSourceFile", { ...configured, file: "/p/base.json" }),
        request("getConfigSourceFile", { snapshot: ref("next.snapshot"), project: "/p/tsconfig.json", file: "/p/base.json" }),
    ] });
    return cases;
}
