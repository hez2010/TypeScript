export function documentationCases() {
    const ref = name => ({ $ref: name });
    const request = (method, params = {}, save) => ({ method, params, ...(save ? { save } : {}) });
    const context = { snapshot: ref("snapshot.snapshot"), project: ref("snapshot.operation.createdPrograms.0") };
    const cases = [
        ["plain", "/** Adds numbers.\n * @param a first\n * @returns the sum\n * @deprecated old\n */\nexport function add(a: number, b: number): number { return a+b; }", ["add(", "a: number", "b: number"]],
        ["variables", "/** variable */ export const a=1, b=2;\n/** lambda */ export const f = (x: number) => x;\n/** object */ export const obj = { /** property */ x: 1 }; const {x} = obj;", ["a=", "b=", "f =", "obj =", "x: 1", "x}"]],
        ["links", "/** See {@link Target name}, {@link Target() call}, {@link Missing label}, {@linkcode https://example.org|site}, {@linkplain https://example.org}. */ export const x=1;\nexport interface Target {}", ["x="]],
        ["templates", "/** Generic.\n * @template T first type\n * @param x value\n */ export function f<T>(x: T): T { return x; }", ["f<", "T>", "x: T"]],
        ["binding-parameters", "/** Binding.\n * @param options first\n * @param values second\n */ export function f({x}: {x:number}, [y]: number[]) {}", ["f(", "x}:", "y]"]],
        ["overloads", "/** first */ export function f(x: number): number;\nexport function f(x: string): string;\nexport function f(x: any) { return x; }", ["f(x:", "x: any"]],
        ["inheritance", "export class Base { /** base method */ m(): number { return 1; } /** base static */ static s(): number { return 2; } }\nexport class Derived extends Base { m(): number { return 3; } static s(): number { return 4; } }", ["Derived extends", "m(): number", "s(): number"]],
        ["interfaces", "export interface A { /** A property */ x: number; } export interface B extends A { x: number; }\n/** one */ export interface M {a:number;}\n/** two */ export interface M {b:number;}\n/** one */ export interface M {c:number;}", ["x: number", "M {c:"]],
        ["js-tags", "/** Description.\n * @template {string} T,U - types\n * @param {T} [x='a'] input\n * @returns {U} output\n * @throws {Error} failure\n * @see Target description\n * @example sample\n */ export function f(x) { return x; }", ["f(", "x)"], 1],
        ["js-type-tags", "/** Value.\n * @type {number}\n * @satisfies {number}\n * @custom value {@link Foo desc}\n */ export const value = 1;", ["value ="], 1],
        ["js-class-tags", "/** Class.\n * @extends {Base}\n * @implements {I}\n */ export class C {}", ["C {}"], 1],
        ["typedef", "/** Type docs.\n * @typedef {Object} Thing\n * @property {number} x field\n */\n/** @type {Thing} */ export const value = {x:1};", ["value ="], 1],
        ["typedef-local", "/** Function docs.\n * @typedef {number} N\n * @param {N} x input\n * @returns {N} result\n */ export function f(x) { return x; }", ["f("], 1],
        ["unicode", "/** 説明 😀\n * @param 名前 - 値\n * @returns 結果\n */ export function 関数(名前: string) { return 名前; }", ["関数(", "名前:"]],
    ];
    return cases.map(([name, sourceText, needles, scriptKind = 3]) => {
        const file = scriptKind === 1 ? "/p/main.js" : "/p/main.ts";
        return { name: `documentation-${name}`, files: { [file]: sourceText }, requests: [
            request("createSnapshot", { createPrograms: [{ rootFiles: [file], compilerOptions: { noLib: true, allowJs: scriptKind === 1 } }] }, "snapshot"),
            ...needles.flatMap((needle, index) => [
                request("getSymbolAtPosition", { ...context, file, position: Buffer.byteLength(sourceText.slice(0, sourceText.lastIndexOf(needle))) }, `s${index}`),
                request("getDocumentationComment", { ...context, symbol: ref(`s${index}.id`) }),
                request("getJsDocTags", { ...context, symbol: ref(`s${index}.id`) }),
            ]),
        ] };
    });
}
