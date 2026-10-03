export function serviceCases() {
    const ref = name => ({ $ref: name });
    const request = (method, params = {}, save) => ({ method, params, ...(save ? { save } : {}) });
    const context = { snapshot: ref("snapshot.snapshot"), project: ref("snapshot.operation.createdPrograms.0") };
    const query = (method, params = {}, save) => request(method, { ...context, ...params }, save);
    const create = (roots, options = {}) => request("createSnapshot", {
        createPrograms: [{ rootFiles: roots, compilerOptions: { noLib: true, strict: true, ...options } }],
    }, "snapshot");
    const completions = [
        ["member", "const o = { alpha: 1, beta: 's' }; o.§", "."],
        ["unicode", "/* 😀 */ const 日本語 = { 名前: '値' }; 日本語.§", "."],
        ["optional", "declare const o: { prop?: number } | undefined; o?.§", "."],
        ["private", "class C { #p = 1; method() { this.§ } }", "."],
        ["string-property", "const o = { 'two words': 1 }; o['§']", "'"],
        ["literals", "declare function f(p: 'a' | 'b'): void; f('§');", "'"],
        ["object", "interface I { a: number; b?: string; } const x: I = { § };"],
        ["class-member", "class Base { method() {} } class C extends Base { § }"],
        ["globals", "const local = 1; export {}; §"],
        ["autoimport", "export {}; imported§"],
        ["import", "import §", " "],
        ["import-specifier", "import { § } from './dependency';"],
        ["module-path", "import {} from './§';", "/"],
        ["type", "interface Box<T> { value: T } let x: §"],
        ["documentation", "/** @§ */ function f() {}", "@"],
        ["documentation-template", "/**§ */ function f(value: string): void {}", "*"],
        ["invalid-trigger", "const x = 1; §", "."],
    ];
    const result = completions.map(([name, marked, triggerCharacter]) => {
        const text = marked.replace("§", ""), position = Buffer.byteLength(marked.slice(0, marked.indexOf("§")));
        return { name: `api-completion-${name}`, files: { "/p/main.ts": text,
            "/p/dependency.ts": "export const imported = 1; export interface ImportedType { value: string }" }, requests: [
            create(["/p/main.ts", "/p/dependency.ts"]),
            ...[false, true].map(includeSymbol => query("getCompletionsAtPosition", { file: "/p/main.ts", position, triggerCharacter, includeSymbol })),
            query("getCompletionsAtPosition", { file: "/missing.ts", position: 0, includeSymbol: true }),
        ] };
    });
    for (const [name, declaration] of [["property", "declare const o: { value?: string }"],
        ["mapped", "declare const o: { [P in 'value']: number }"], ["union", "declare const o: { value: number } | { value: string }"]]) {
        const text = `${declaration}; o.`;
        result.push({ name: `api-completion-symbol-lifetime-${name}`, files: { "/p/main.ts": text }, requests: [
            create(["/p/main.ts"]), query("getCompletionsAtPosition", { file: "/p/main.ts", position: text.length, includeSymbol: true }, "completion"),
            query("getTypeOfSymbol", { symbol: ref("completion.entries.0.symbol.id") }, "type"),
            query("typeToString", { type: ref("type.id") }),
            query("getCompletionsAtPosition", { file: "/p/main.ts", position: text.length, includeSymbol: true }),
            query("getTypeOfSymbol", { symbol: ref("completion.entries.0.symbol.id") }),
        ] });
    }
    for (const [name, main, options] of [["new", "const value = imported;\n", {}],
        ["existing", "import { present } from './dependency';\nconst value = imported;\n", {}],
        ["unicode", "// 😀日本語\nconst value = imported;\n", {}],
        ["verbatim", "const value = imported;\n", { verbatimModuleSyntax: true }]]) {
        const dependency = "export const imported = 1; export const present = 2; export interface ImportedType {} const hidden = 3;";
        result.push({ name: `api-import-adder-${name}`, files: { "/p/main.ts": main, "/p/dependency.ts": dependency }, requests: [
            create(["/p/main.ts", "/p/dependency.ts"], options),
            ...["imported", "present", "ImportedType", "hidden"].map(symbol => query("getSymbolAtPosition",
                { file: "/p/dependency.ts", position: dependency.indexOf(symbol) }, symbol)),
            ...[undefined, false, true].map(isValidTypeOnlyUseSite => query("getImportAdderEdits", { file: "/p/main.ts", actions: [
                { kind: "importSymbol", symbol: ref("imported.id"), isValidTypeOnlyUseSite },
                { kind: "importSymbol", symbol: ref("ImportedType.id"), isValidTypeOnlyUseSite },
                { kind: "importSymbol", symbol: ref("imported.id"), isValidTypeOnlyUseSite },
            ] })),
            query("getImportAdderEdits", { file: "/p/main.ts", actions: [{ kind: "importSymbol", symbol: ref("hidden.id") }] }),
            query("getImportAdderEdits", { file: { uri: "file:///p/main.ts" }, actions: [] }),
            ...[[{ kind: "importSymbol" }], [{ kind: "importSymbol", symbol: 999999 }], [{ kind: "unknown" }]].map(actions =>
                query("getImportAdderEdits", { file: "/p/main.ts", actions })),
            query("getImportAdderEdits", { file: "/missing.ts", actions: [] }),
        ] });
    }
    const declarations = "export function target(x: string): string; export function target(x: number): number; export function target(x: any) { return x; }";
    result.push({ name: "api-signature-cross-file-usages", files: { "/p/main.ts": declarations,
        "/p/use.ts": "import { target as renamed } from './main'; renamed(1); const f = renamed; export { renamed };",
        "/p/namespace.ts": "import * as ns from './main'; ns.target('x'); ns.target;" }, requests: [
        create(["/p/main.ts", "/p/use.ts", "/p/namespace.ts"]),
        query("getSymbolAtPosition", { file: "/p/main.ts", position: declarations.indexOf("target") }, "target"),
        query("getReferencesToSymbolInFile", { file: "/p/main.ts", symbol: ref("target.id") }, "names"),
        query("getReferencedSymbolsForNode", { node: ref("names.0"), position: declarations.indexOf("target") }),
        ...[0, 1, 2].map(index => query("getSignatureUsages", { signatureDecl: ref(`target.declarations.${index}`) })),
    ] });
    return result.flatMap(input => {
        const program = input.requests[0].params.createPrograms[0];
        return [input, { ...input, name: `${input.name}-configured`, files: { ...input.files,
            "/p/tsconfig.json": JSON.stringify({ compilerOptions: program.compilerOptions, files: program.rootFiles }) }, requests: [
            request("createSnapshot", { openProjects: ["/p/tsconfig.json"] }, "snapshot"),
            ...input.requests.slice(1).map(item => ({ ...item, params: { ...item.params, project: "/p/tsconfig.json" } })),
        ] }];
    });
}
