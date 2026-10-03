export function emissionCases() {
    const ref = name => ({ $ref: name });
    const request = (method, params = {}, save) => ({ method, params, ...(save ? { save } : {}) });
    const context = { snapshot: ref("snapshot.snapshot"), project: ref("snapshot.operation.createdPrograms.0") };
    const query = (method, params = {}) => request(method, { ...context, ...params });
    const text = 'export const 日本語: string = "😀";\nexport class C { constructor(public x: number) {} }';
    const files = { "/p/a.ts": text, "/p/b.ts": 'import { 日本語 } from "./a"; export const b = 日本語;' };
    const results = [{ name: "emission-transpile", files, requests: [
        ...["transpileModule", "transpileDeclaration"].flatMap(method => [
            [text, {}], [text, { compilerOptions: { sourceMap: true, declarationMap: true }, fileName: "src/日本語.ts" }],
            ["export const f = (x: number) => x + 1;", { compilerOptions: { module: 1, target: 2 } }],
            ["const a = ;\nconst 日本語: number = '😀';", { reportDiagnostics: true }],
            ["export const x = (a: number) => a + 1;", { reportDiagnostics: true }],
            ["export const x = <div>😀</div>;", { compilerOptions: { jsx: 4 }, fileName: "src/a.tsx", reportDiagnostics: true }],
            [text, { compilerOptions: { target: 1, noEmit: true, sourceMap: true }, reportDiagnostics: true }]
        ].map(([input, options]) => request(method, { input, options }))),
        ...["transpileModuleFromFile", "transpileDeclarationFromFile"].flatMap(method => [
            request(method, { fileName: "/p/a.ts", options: { compilerOptions: { sourceMap: true, declarationMap: true }, reportDiagnostics: true } }),
            request(method, { fileName: "/missing.ts" })])
    ] }];
    for (const [name, compilerOptions] of [
        ["standard", { declaration: true, sourceMap: true, declarationMap: true }],
        ["no-emit", { noEmit: true }], ["declarations", { declaration: true, emitDeclarationOnly: true }],
        ["no-emit-on-error", { noEmitOnError: true }], ["bom", { emitBOM: true }]
    ]) for (const full of [false, true]) results.push({ name: `emission-${name}-${full ? "full" : "host"}`, files, requests: [
        request("createSnapshot", { ...(full ? { fileSystem: { kind: "full", files } } : {}),
            createPrograms: [{ rootFiles: ["/p/a.ts", "/p/b.ts"], compilerOptions: { noLib: true, outDir: "/out", ...compilerOptions } }] }, "snapshot"),
        query("emitToString"), ...[0, 1, 2, 3].map(emitOnly => query("emitToString", { emitOnly })),
        ...["getJavaScriptEmit", "getDeclarationEmit"].flatMap(method => [undefined, [], ["/p/a.ts"], ["/p/b.ts", "/p/a.ts"], ["/p/a.ts", "/p/a.ts"],
            [{ uri: "file:///p/a.ts" }], ["/missing.ts"]].map(files => query(method, { files }))),
        query("emit"), request("createSourceFileFromFile", { fileName: "/out/a.js" }),
        query("emit", { emitOnly: 2 }), request("createSourceFileFromFile", { fileName: "/out/a.d.ts" }),
        query("getSourceFile", { file: "/p/a.ts" }),
    ] });
    return results;
}
