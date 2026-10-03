export function referenceCases() {
    const ref = name => ({ $ref: name });
    const request = (method, params = {}, save) => ({ method, params, ...(save ? { save } : {}) });
    const context = { snapshot: ref("snapshot.snapshot"), project: ref("snapshot.operation.createdPrograms.0") };
    const samples = [
        ["shadowing", "value", "const value = 1; value; function f(value: number) { return value; } const o = { value }; export { value }; export { value as renamed };"],
        ["unicode", "日本語", "const 日本語 = 1; 日本語; const o = { 日本語 }; // 日本語\nconst text = '日本語'; export { 日本語 };"],
        ["escapes", "name", "const name = 1; name; const o = { name }; n\\u0061me; const nameLong = 2; // name\nexport { name };"],
        ["aliases", "local", 'import { source as local } from "./dependency"; local; export { local as renamed }; const o = { local };'],
        ["docs", "T", "/** @template U */ function f() {}\ninterface T {} /** @type {T} */ const value = {}; let x: T;"],
        ["property", "method", "class C { method() {} } new C().method(); const object = { method() {} }; object.method();"],
    ];
    return samples.map(([name, symbol, text]) => ({ name: `references-${name}`, files: {
        "/p/main.ts": text, "/p/dependency.ts": "export const source = 1;",
    }, requests: [
        request("createSnapshot", { createPrograms: [{ rootFiles: ["/p/main.ts"], compilerOptions: { noLib: true, strict: true } }] }, "snapshot"),
        request("getSymbolAtPosition", { ...context, file: "/p/main.ts", position: Buffer.byteLength(text.slice(0, text.indexOf(symbol))) }, "symbol"),
        request("getReferencesToSymbolInFile", { ...context, file: "/p/main.ts", symbol: ref("symbol.id") }, "nodes"),
        ...["/p/dependency.ts", "/missing.ts"].map(file => request("getReferencesToSymbolInFile", { ...context, file, symbol: ref("symbol.id") })),
        request("getReferencedSymbolsForNode", { ...context, node: ref("nodes.0"), position: Buffer.byteLength(text.slice(0, text.indexOf(symbol))) }),
        request("getSignatureUsages", { ...context, signatureDecl: ref("symbol.declarations.0") }),
        request("getReferencesToSymbolInFile", { ...context, file: "/missing.ts", symbol: 0 }),
        request("getReferencesToSymbolInFile", { ...context, file: "/missing.ts", symbol: 999999 }),
        ...["", "invalid", "99999.0./p/main.ts", "1.0./missing.ts"].flatMap(node => [
            request("getReferencedSymbolsForNode", { ...context, node, position: 0 }),
            request("getSignatureUsages", { ...context, signatureDecl: node }),
        ]),
    ] }));
}
