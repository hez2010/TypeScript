export function diagnosticCases() {
    const ref = name => ({ $ref: name });
    const request = (method, params = {}, save) => ({ method, params, ...(save ? { save } : {}) });
    const context = { snapshot: ref("snapshot.snapshot"), project: ref("snapshot.operation.createdPrograms.0") };
    const methods = ["getSyntacticDiagnostics", "getBindDiagnostics", "getSemanticDiagnostics", "getSuggestionDiagnostics", "getDeclarationDiagnostics"];
    function scenario(name, files, options = {}, configured = false) {
        const roots = Object.keys(files).filter(file => /\.[cm]?[jt]sx?$/.test(file));
        const project = configured ? { snapshot: context.snapshot, project: "/p/tsconfig.json" } : context;
        return { name: `diagnostics-${name}`, files, requests: [
            request("createSnapshot", configured ? { openProjects: ["/p/tsconfig.json"] }
                : { createPrograms: [{ rootFiles: roots, compilerOptions: { noLib: true, target: 99, ...options } }] }, "snapshot"),
            ...methods.flatMap(method => [undefined, [], [roots[0]], [roots.at(-1), roots[0]], [roots[0], roots[0]],
                [{ uri: `file://${roots[0]}` }], ["/p/missing.ts"]].map(files => request(method, { ...project, ...(files ? { files } : {}) }))),
            ...["getConfigFileParsingDiagnostics", "getProgramDiagnostics", "getGlobalDiagnostics"].map(method => request(method, project)),
            ...methods.map(method => request(method, project)),
            request("release", { snapshot: context.snapshot }), request("getSemanticDiagnostics", project)
        ] };
    }
    return [
        scenario("syntax-bind", { "/p/a.ts": "let a = ;\nconst 日本語: number = '😀';\n", "/p/b.ts": "let duplicate = 1; let duplicate = 2;" }),
        scenario("semantic", { "/p/a.ts": "export interface A { 日本語: string; }\nexport const a: A = { 日本語: 42 };",
            "/p/b.ts": "import { a } from './a';\na.missing;\n// @ts-expect-error\na.日本語;\n// @ts-ignore\nmissing;" }, { strict: true }),
        scenario("suggestions", { "/p/a.ts": "export {};\nfunction unused<T>(a, b: string) { let x; return 9007199254740993; }\nasync function f() { await 1; }\n" }),
        scenario("implicit-suggestions", { "/p/a.ts": "export {};\nfunction unused<T>(a, b: string) { let x; return 9007199254740993; }\nasync function f() { await 1; }\n" }, { strict: false }),
        scenario("unused-errors", { "/p/a.ts": "export {};\nfunction unused<T, U>(a: number, b: string) { let x = 1; return a; }" }, { noUnusedLocals: true, noUnusedParameters: true }),
        scenario("deprecation", { "/p/a.ts": "/** @deprecated use replacement */\nexport const old = 1;\n/** @deprecated old signature */\nexport function oldFn(a: number): void {}\n",
            "/p/b.ts": "import { old, oldFn } from './a';\nold; oldFn(1);\nconst object = { /** @deprecated use fresh */ old: 1 }; object.old;" }),
        scenario("jsdoc", { "/p/a.ts": "/** @param {number} missing hi */\nexport function f(actual: number) { return actual; }" }),
        scenario("unreachable-labels", { "/p/a.ts": "export function f() { unused: { } return 1; let after = 2; after++; }" }),
        scenario("unused-forms", { "/p/a.ts": "import { a, b } from './b'; export {};\nconst unused = 1, second = 2;\nfunction f<T, U>(a: string, b: number) { return true; }\nclass C<T> { private field = 1; constructor(private p: number) {} private m() {} }",
            "/p/b.ts": "export const a = 1, b = 2;" }),
        scenario("deprecated-signatures", { "/p/a.ts": "declare let other: any;\n/** @deprecated old class */\ndeclare class C { /** @deprecated old constructor */ constructor(a: number); /** @deprecated old method */ m(a: string): void; }\nconst c = new C(1); c.m('x'); c['m']('x');\n/** @deprecated anonymous call */\ndeclare function f(): void; (f)();\n" }),
        scenario("js-suggestions", { "/p/a.js": "/** @type {{property:number}} */\nconst x = { property: 1 }; x.proprty;\nfunction unused(a) { return 1; }\nmodule.exports = unused;" }, { allowJs: true, strict: false, noEmit: true }),
        scenario("implicit-import", { "/p/a.ts": "import value from 'pkg'; export const result = value;",
            "/p/node_modules/pkg/package.json": '{"main":"index.js"}', "/p/node_modules/pkg/index.js": "module.exports = 1;" }, { strict: false, noEmit: true }),
        scenario("chained-errors", { "/p/a.ts": "interface A { 日本語: { first: { value: string } }; }\nconst value: A = { 日本語: { first: { value: 42 } } };\nfunction f(a: A): void; function f(a: string): void; function f(a: any) {}\nf(42);" }),
        ...[undefined, false, true].map(checkJs => scenario(`javascript-${checkJs}`, {
            "/p/a.js": "const x = { property: 1 }; x.proprty;\nfunction f(a) { return a; }\nconst n = 9007199254740993;\nclass C { m(@d a) {} }",
        }, { allowJs: true, checkJs })),
        scenario("no-check", { "/p/a.ts": "let a = 1; let a = 2;\nexport const bad: string = 1;" }, { noCheck: true }),
        scenario("declaration", { "/p/a.ts": "export const anonymous = class { private value = 1; };" }, { declaration: true }),
        scenario("configured", { "/p/tsconfig.json": '{"compilerOptions":{"noLib":true,"unknownOption":true,"sourceMap":false,"inlineSources":true},"files":["a.ts","missing.ts"]}',
            "/p/a.ts": "export const 日本語: number = '😀';" }, {}, true),
        scenario("configured-globals", { "/p/tsconfig.json": '{"extends":"./missing.json","compilerOptions":{"noLib":true,"target":"unknown"},"files":["a.ts"]}',
            "/p/a.ts": "export {};" }, {}, true),
    ];
}
