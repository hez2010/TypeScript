import assert from "node:assert/strict";
import {spawn} from "node:child_process";
import {copyFile, mkdir, readFile, writeFile} from "node:fs/promises";
import path from "node:path";
import {pathToFileURL} from "node:url";
import {Script} from "node:vm";
import {root, output, run, json, sha256, referenceRevision} from "./common.mjs";
import {jsxCases} from "./jsx-cases.mjs";
import {moduleCases} from "./module-cases.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const dotnet = option("--dotnet", "dotnet");
const go = option("--go", "D:/go1.27.1-20260904.9.windows-amd64/go/bin/go.exe");
const transforms = process.argv.includes("--transforms");
const directory = path.join(output, transforms ? "transforms" : "printer");
const moduleCorrectionsPath = path.join(root, 'csharp/compatibility/module-corrections.json');
const moduleCorrections = JSON.parse(await readFile(moduleCorrectionsPath, 'utf8'));
const reviewedModules = new Map(moduleCorrections.entries.map(entry => [entry.name, entry]));
assert.equal(moduleCorrections.referenceRevision, referenceRevision);
function canonical(value) {
    return Array.isArray(value) ? value.map(canonical) : value !== null && typeof value === 'object'
        ? Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])])) : value;
}
await mkdir(directory, {recursive: true});
const reference = JSON.parse(await readFile(path.join(output, "reference.json"), "utf8"));
const source = path.join(output, reference.sourceRelativePath, "tsc");
const oracle = path.join(directory, "oracle.exe");
const bridge = path.join(root, "csharp/oracle/printer/main.go");
const bridgeDirectory = path.join(source, "cmd/csharp-printer");
if (!process.argv.includes("--no-build")) {
    await mkdir(bridgeDirectory, {recursive: true});
    await copyFile(bridge, path.join(bridgeDirectory, "main.go"));
    await run(go, ["-C", source, "build", "-o", oracle, "./cmd/csharp-printer"], {env: {...process.env, GOWORK: "off", GOTOOLCHAIN: "local"}});
    await run(dotnet, ["build", "csharp/tests/TypeScript.Compatibility/TypeScript.Compatibility.csproj", "-c", "Release", "--no-restore"]);
}
async function execute(command, args, input = "") {
    const child = spawn(command, args, {cwd: root, windowsHide: true});
    let text = "", error = "";
    child.stdout.on("data", bytes => text += bytes);
    child.stderr.on("data", bytes => error += bytes);
    child.stdin.on("error", () => {});
    child.stdin.end(input);
    const code = await new Promise((resolve, reject) => { child.on("error", reject); child.on("close", resolve); });
    if (code) throw Error(`${command}: ${code}\n${error}`);
    return text;
}
const syntax = JSON.parse(await execute(oracle, ["--fixtures", path.join(source, "internal/printer/printer_test.go")]));
assert(syntax.length > 450);
const commentSources = [
    "// header\nconst x = 1; // tail\n// end",
    "/*! license */\n\n/** Doc. */\nexport function f(/* before */ x: number /* type */) { return /* value */ x; }",
    "const /* a */ x /* b */ = /* c */ 1 /* d */; /* e */ x /* f */ + /* g */ 2;",
    "function f() {\n  // first\n  let x = 1;\n  // last\n}\n",
    "// leading\nclass C {\n/** field */\nx = 1; // next\n// method\nm() {\nreturn this.x;\n}\n// end\n}",
    "const x = [ /* first */ 1, /* next */ 2, /* last */ ]; const o = { /* a */ x: 1 /* b */ };",
    "const x = `/*${'//'}*/`; const r = /[/*]/; const y = <div> // text {/* expr */ x}</div>;",
    "/// <reference path=\"./a.d.ts\" />\n/** type */\ninterface A { x: number }",
    "/* license\n * line 2\n */\n\nfunction f() {\n  /* body\n   * line 2\n   */\n  return 1;\n}",
    "#! /usr/bin/env node\n// comment\nexport const x = 1;",
    "\ufeff// 😀\nconst café = '😀';\r\n// tail\r\n",
    "function f() { return (\n// preserved\nx\n); }",
];
const cases = [...syntax];
for (const text of ["a()", "return x", "throw x", "break name", "continue name", "debugger", ";", "const x = 1", "do {} while (true)",
    "for (;;);", "while (true);", "if (x); else;", "class C { x = 1; ; m(): void; }", "function f(): void;", "export default x;", "import 'x';",
    "const x = 1; // comment", "function f() { 'use strict'; return 1; }"])
    for (const firstStatement of [false, true]) for (const omitSemicolon of [false, true]) for (const map of [false, true])
        cases.push({name: `semicolon-${cases.length}`, text, firstStatement, omitSemicolon, map});
for (const text of ["const x = 1;", "'use strict'; const x = 1;", "// heading\n'use strict';\n// regular\nconst x = 1;",
    "/*! license */\n\n'use strict';\nconst x = 1;", "#!/usr/bin/env node\n'use strict';\n'use next';\nconst x = 1;",
    "function f() { return 1; }", "function f() { 'use strict'; return 1; }", "function f() {\n/* body */\n\nreturn 1;\n}"])
    for (const noEmitHelpers of [false, true]) for (const externalHelpers of [false, true]) {
        const owner = text.startsWith("function") ? "body" : "file";
        cases.push({name: `helpers-${cases.length}`, text, noEmitHelpers, externalHelpers, map: true, helpers: [
            {name: "last", text: "var last = true;", owner},
            {name: "two", text: "  var two = {\n    value: 2\n  };", priority: 2, owner},
            {name: "one", text: "var one = 1;", priority: 1, scoped: true, owner},
            {name: "same", text: "var same = 2;", priority: 2, owner},
            {name: "factory", factory: "x", scoped: true, owner},
        ]});
    }
for (const text of ['{"x": 1, "a": [true, null]}', '[1, 2, "x"]', 'true'])
    cases.push({name: `json-${cases.length}`, text, file: "/source/input.json", json: true, map: true});
for (const removeComments of [false, true])
    cases.push({name: `references-${cases.length}`, file: "/source/input.d.ts", removeComments, map: true,
        text: '/// <reference path="./a.d.ts" />\n/// <reference types="abc" resolution-mode="import" preserve="true" />\n/// <reference lib="es2020" />\ndeclare const x: number;'});
for (const text of [
    "var $auto1, $auto2; $auto2 = $auto1;",
    "let _a, _i, value_1; var $auto1, $loop1, $unique1; $unique1 = $auto1 + $loop1;",
    "var $auto1; function f($auto2) { var $auto3; return $auto2 + $auto3; } var $auto4;",
    "var $auto1; const obj = { $unique1() { var $auto2; return $auto2; }, $unique2: $auto1 }; var $auto3;",
    "class C { #$private1 = 1; m() { var $auto1; return this.#$private1; } } class D { #$private2 = 2; }",
    "function $unique1($auto1) { var $auto2; return $unique1($auto1); } function $unique2($auto3) { return $auto3; }",
    "var [$auto1, {$unique1: $auto2}] = data; var $auto3;",
    "class C { static { var $auto1; } static { var $auto2; } } var $auto3;",
]) cases.push({name: `generated-${cases.length}`, text, generated: true});
const expressions = ["a + b", "a - b", "a * b", "a / b", "a ** b", "a = b", "a, b", "a ? b : c", "a => b", "function () {}",
    "class {}", "{ x: 1 }", "new A", "new A()", "a()", "a?.b", "a?.(b)", "a && b", "a || b", "a ?? b", "a as T", "a satisfies T",
    "-a", "+a", "typeof a", "void a", "await a", "a!", "a++", "a | b", "a & b", "a ^ b", "1", "'x'", "a < b"];
const contexts = ["const z = ($).x", "const z = ($)[x]", "const z = ($)(x)", "const z = new ($)(x)", "const z = ($)`x`",
    "const z = -($)", "const z = ($)++", "const z = a + ($)", "const z = ($) + a", "const z = a ** ($)", "const z = ($) ** a",
    "const z = ($) ? x : y", "const z = c ? ($) : x", "const z = () => ($)", "($);", "const z = [($)]", "f(($))", "const z = {x: ($)}",
    "const z = ($) as T", "const z = ($)!", "const z = a ?? ($)", "const z = a && ($)"];
for (let i = 0; i < expressions.length; i++) for (let j = 0; j < contexts.length; j++)
    cases.push({name: `synthetic-expression-${i}-${j}`, text: contexts[j].replace("$", expressions[i]), synthetic: true, removeComments: true});
const types = ["A | B", "A & B", "A extends B ? C : D", "() => A", "new () => A", "keyof A", "readonly A[]", "infer A", "infer A extends B", "typeof A", "A[B]", "A[]", "A"];
const typeContexts = ["type X = ($)[]", "type X = [($)?]", "type X = ($) | Z", "type X = ($) & Z", "type X = keyof ($)", "type X = readonly ($)",
    "type X = ($)[Z]", "type X = ($) extends Z ? A : B", "type X = A extends ($) ? B : C", "type X = A extends () => ($) ? B : C"];
for (let i = 0; i < types.length; i++) for (let j = 0; j < typeContexts.length; j++)
    cases.push({name: `synthetic-type-${i}-${j}`, text: typeContexts[j].replace("$", types[i]), synthetic: true, removeComments: true});
for (const [name, text] of [
    ["multiply-rounding", "const a = 1e308, b = 1e-308, c = 1e-308; globalThis.result = a * (b * c);"],
    ["addition-rounding", "globalThis.result = 1e16 + (1 + 1);"],
    ...["|", "^", "&"].map(op => [`conversion-order-${op}`, `const trace = []; const a = { valueOf() { trace.push('a'); return 1; } }, b = { valueOf() { trace.push('b'); return 2; } }; function c() { trace.push('c'); return 3; } const value = a ${op} (b ${op} c()); globalThis.result = [value, trace];`]),
]) cases.push({name: `runtime-${name}`, text, synthetic: true, removeComments: true, runtime: true});
const commentStart = cases.length;
for (let i = 0; i < commentSources.length; i++) for (const removeComments of [false, true]) for (const onlyJSDoc of [false, true]) {
    cases.push({name: `comments-${i}-${removeComments}-${onlyJSDoc}`, text: commentSources[i], jsx: true, removeComments, onlyJSDoc});
}
for (const item of [...syntax, ...cases.slice(commentStart)])
    cases.push({...item, name: `map-${item.name}`, map: true, inlineSources: true});
const filter = option("--filter", "");
const transformCases = jsxCases(await readFile(path.join(source, 'internal/transformers/jsxtransforms/jsx.go'), 'utf8'));
transformCases.push(...moduleCases());
const staticAccessorPanic = 'Debug failure. False expression: Undeclared private name for property declaration.';
const staticAccessorProbe = 'function dec(v) { return class extends v {}; } function other(v,c) { return { init(x) { return x+1; } }; } @dec class C { @other static accessor y = 41; } const before = C.y; C.y = 7; globalThis.result = [before,C.y];';
if (transforms) {
    for (const [name, text, referenceOutcome] of [
        ['logical', 'const trace = []; class B { static get x() { trace.push("get"); return 42; } static set x(v) { trace.push(v); } } function dec(v) {} @dec class C extends B { static value = super.x ||= 1; } globalThis.result = [C.value, trace];', {value: [42, ['get', 42]]}],
        ['logical-and', 'const trace = []; class B { static get x() { trace.push("get"); return 0; } static set x(v) { trace.push(v); } } function dec(v) {} @dec class C extends B { static value = super.x &&= 1; } globalThis.result = [C.value, trace];', {value: [0, ['get', 0]]}],
        ['logical-nullish', 'const trace = []; class B { static get x() { trace.push("get"); return 42; } static set x(v) { trace.push(v); } } function dec(v) {} @dec class C extends B { static value = super.x ??= 1; } globalThis.result = [C.value, trace];', {value: [42, ['get', 42]]}],
        ['parenthesized-call', 'class B { static f() { return this.name; } } function dec(v) {} @dec class C extends B { static value = (super.f)(); } globalThis.result = C.value;', {error: 'TypeError'}],
        ['parenthesized-tag', 'class B { static f() { return this.name; } } function dec(v) {} @dec class C extends B { static value = (super.f)`x`; } globalThis.result = C.value;', {error: 'TypeError'}],
    ]) for (const target of ['es2015', 'es2022']) {
        const item = {name: `esdecorator-correction-${name}-${target}`, text, runtimeControl: text.replace('@dec ', ''), nativeControl: text.replace('@dec ', ''),
            referenceBug: `decorator-super-${name}`, referenceOutcome, eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map: true, options: {target}};
        transformCases.push(item);
        if (name.startsWith('logical')) {
            const text = name.endsWith('-and') ? item.text.replace('return 0;', 'return 42;') : item.text.replace('return 42;', name.endsWith('-nullish') ? 'return null;' : 'return 0;');
            transformCases.push({...item, name: item.name.replace('-es', '-write-es'), text, runtimeControl: text.replace('@dec ', ''), nativeControl: text.replace('@dec ', ''), referenceOutcome: {value: [1, ['get', 1]]}});
        }
    }
    for (const target of ['es2015', 'es2022', 'esnext'])
        transformCases.push({name: `esdecorator-execute-static-accessor-${target}`, text: staticAccessorProbe, runtimeControl: 'globalThis.result = [42,7];',
            referencePanic: target === 'esnext' ? staticAccessorPanic : undefined, eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map: true,
            options: {target, useDefineForClassFields: false}});
    for (const text of [
        '@dec class C {}',
        '@dec class C extends B { x = 1; static y = 2; }',
        'class C { @dec method(x) { return x; } }',
        'class C { @dec static method(x) { return x; } }',
        'class C { @dec x = 1; @dec static y = 2; z = 3; static w = 4; }',
        'class C { @a @b get x() { return 1; } @c set x(v) {} }',
        'class C { @dec accessor x = 1; @dec static accessor y = 2; }',
        'class C { @dec #x = 1; @dec #m() { return this.#x; } f() { return this.#m(); } }',
        'class C { @dec get #x() { return 1; } @dec set #x(v) {} @dec accessor #y = 2; }',
        '@dec class C { static #x = 1; static #m() { return this.#x; } static f() { return this.#m(); } }',
        '@dec class C { @dec static accessor #x = 1; static f() { return this.#x; } }',
        'class C { @dec [key()] = 1; @next [other()]() {} @last static [third()] = 2; }',
        'class C { @dec ["x"]() {} @dec 1 = 2; @dec "not-id"() {} }',
        'class C { @obj.dec x = 1; @obj[key()] m() {} @factory().dec static y; }',
        '@dec export class C { @other x = 1; }',
        '@dec export default class C { @other x = 1; }',
        '@dec export default class { @other x = 1; }',
        'const C = @dec class { @other x = 1; };',
        'const C = class { @dec x = 1; }; const D = (@dec class {});',
        'const o = { field: @dec class {}, [key()]: @dec class {} };',
        'function f(x = @dec class {}) { return x; }',
        '@dec class C { static self = this; static { use(this); } }',
        '@dec class C extends B { static x = super.x; static y = super.f(); static { super.x = 2; super.x += 1; ++super.x; } }',
        '@dec class C { static x = function() { return this; }; static y = () => this; }',
        'class C { @dec m() {} constructor() { "use strict"; run(); } }',
        'class C extends B { @dec m() {} constructor() { before(); try { super(); after(); } catch(e) { fail(e); } } }',
        'class C { @dec x = 1; constructor() { run(); } }',
        'class C { @dec static x = 1; static { run(); } }',
        'function f() { return class { @this.dec x = 1; }; }',
        'class C { @dec m() { return @other class D { @last x = 1; }; } }',
        '/* head */ @dec /* between */ class C { /** field */ @other x = 1; // tail\n /** method */ @last m() { return this.x; } }',
        '@dec class C { accessor x; @other static accessor y; }',
        'class Outer { static #d; m() { return class { @Outer.#d x = 1; }; } }',
        '@dec class C { static [key()] = this; [next()] = @other class {}; }',
        'const C = class Named { @dec x = Named; @dec #f() { return Named; } m() { return this.#f(); } };',
        'function f() { const C = class { @this.dec [key()] = 1; @this.other x = 2; }; return C; }',
        '@dec class Outer { static Inner = class { @this.dec x = 1; @this.other [this.key()]() {} }; }',
        '@dec class C extends (class { static x = 1; }) { static x = super.x; }',
        '@dec class C extends B { static x = super[key()] += rhs(); static y = super.x++; static z = ++super[key()]; }',
        '@dec class C extends B { static { [super.x, { a: super[key()], ...super.y }] = values; } }',
        'let C; C = @dec class {}; C ||= @other class {}; const { x = @last class {} } = obj;',
        'const { [key()]: C = @dec class {} } = obj; const [D = class { @dec x = 1; }] = array;',
        'namespace N { @dec export class C { @other x = 1; } }',
        '@dec class C { @other *#m(x) { yield x; } @last async #f() { await this.#m(); } }',
        'class C { @dec "😀" = 1; @dec static [Symbol.iterator]() {} @dec get [key()]() { return 1; } }',
        'function f() { return class { @dec m(x = @other class {}) { return x; } }; }',
        'class C { @dec m() {} constructor() { try { try { super(); } finally { inner(); } } finally { outer(); } } }',
    ]) for (const target of ['es2015', 'es2022', 'esnext']) for (const useDefineForClassFields of [false, true]) for (const map of [false, true])
        transformCases.push({name: `esdecorator-${transformCases.length}`, text, eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map, options: {target, useDefineForClassFields}});
    for (const [name, text, result] of [
        ['class-order', 'const trace = []; function d(n) { trace.push("eval" + n); return (v,c) => { trace.push("apply" + n); c.addInitializer(function() { trace.push("init" + n + this.name); }); }; } @d("a") @d("b") class C {} globalThis.result = trace;', ['evala', 'evalb', 'applyb', 'applya', 'initbC', 'initaC']],
        ['class-replacement', 'function d(v,c) { c.addInitializer(function() { this.ready = true; }); return class Replacement extends v { m() { return 42; } }; } @d class C {} globalThis.result = [C.name, C.ready, new C().m()];', ['Replacement', true, 42]],
        ['field-order', 'const trace = []; function a(v,c) { c.addInitializer(function() { trace.push("a:" + this.x); }); return x => (trace.push("init-a"), x+1); } function b(v,c) { c.addInitializer(function() { trace.push("b:" + this.x); }); return x => (trace.push("init-b"), x*2); } class C { @a @b x = 1; y = (trace.push("y:" + this.x), 0); } const c = new C(); globalThis.result = [c.x, trace];', [4, ['init-a', 'init-b', 'b:4', 'a:4', 'y:4']]],
        ['method-receiver', 'function d(v,c) { return function(...args) { return v.apply(this,args)+1; }; } class C { x = 40; @d m(v) { return this.x+v; } } globalThis.result = new C().m(1);', 42],
        ['private-method', 'let access; function d(v,c) { access = c.access; return function() { return v.call(this)+1; }; } class C { x = 41; @d #m() { return this.x; } m() { return this.#m(); } } const c = new C(); globalThis.result = [c.m(), access.has(c), access.has({}), access.get(c).call(c)];', [42, true, false, 42]],
        ['private-field', 'let access; function d(v,c) { access = c.access; return x => x+1; } class C { @d #x = 40; value() { return this.#x; } } const c = new C(); access.set(c,42); globalThis.result = [c.value(),access.get(c),access.has(c),access.has({})];', [42, 42, true, false]],
        ['accessor', 'function d(v,c) { return { get() { return v.get.call(this)+1; }, set(x) { v.set.call(this,x*2); }, init(x) { return x+2; } }; } class C { @d accessor x = 1; } const c = new C(); const before = c.x; c.x = 3; globalThis.result = [before,c.x];', [4, 7]],
        ['private-accessor', 'function d(v,c) { return { get() { return v.get.call(this)+1; }, set(x) { v.set.call(this,x*2); }, init(x) { return x+2; } }; } class C { @d accessor #x = 1; value(v) { if (v !== undefined) this.#x = v; return this.#x; } } const c = new C(); globalThis.result = [c.value(),c.value(3)];', [4, 7]],
        ['static-private', 'function d(v) { return class extends v {}; } @d class C { static #x = 42; static #m() { return this.#x; } static value() { return this.#m(); } } globalThis.result = C.value();', 42],
        ['initializers', 'const trace = []; function d(v,c) { c.addInitializer(function() { trace.push(c.name); }); } class C { @d static m() {} @d n() {} @d static x = (trace.push("x-value"),1); static { trace.push("block"); } @d y = (trace.push("y-value"),2); constructor() { trace.push("body"); } } new C(); globalThis.result = trace;', ['m', 'x-value', 'x', 'block', 'n', 'y-value', 'y', 'body']],
        ['computed-order', 'const trace = []; function d(n) { trace.push("dec"+n); return () => {}; } function key(n) { trace.push("key"+n); return n; } class C { @d("a") [key("a")] = 1; @d("b") [key("b")]() {} @d("c") static c = 2; } globalThis.result = trace;', ['deca', 'keya', 'decb', 'keyb', 'decc']],
        ['decorator-receiver', 'const trace = []; const o = { d(v,c) { trace.push(this === o); } }; class C { @o.d x; @(o.d) y; @(o["d"]) m() {} } new C(); globalThis.result = trace;', [true, true, true]],
        ['outer-this', 'const o = { d() {}, build() { return class { @this.d x = 42; }; } }; const C = o.build(); globalThis.result = new C().x;', 42],
        ['derived-constructor', 'const trace = []; function d(v,c) { c.addInitializer(function() { trace.push(this.x); }); } class B { constructor() { this.x = 42; trace.push("base"); } } class C extends B { @d m() {} constructor() { trace.push("before"); try { super(); trace.push("after"); } catch(e) { throw e; } } new C(); globalThis.result = trace;', ['before', 'base', 42, 'after']],
        ['names', 'const names = []; function d(v,c) { names.push(c.name); } const C = @d class {}; const obj = { field: @d class {}, ["computed"]: @d class {} }; globalThis.result = [names,C.name,obj.field.name,obj.computed.name];', [['C', 'field', 'computed'], 'C', 'field', 'computed']],
        ['metadata', 'Symbol.metadata ??= Symbol("metadata"); function d(v,c) { c.metadata[c.name] = c.kind; } @d class B { @d b; } @d class C extends B { @d c; } globalThis.result = [C[Symbol.metadata].b,C[Symbol.metadata].c,C[Symbol.metadata].C,Object.getPrototypeOf(C[Symbol.metadata]) === B[Symbol.metadata]];', ['field', 'field', 'class', true]],
        ['getter-setter', 'function d(v,c) { return c.kind === "getter" ? function() { return v.call(this)+1; } : function(x) { v.call(this,x*2); }; } class C { value = 1; @d get x() { return this.value; } @d set x(v) { this.value = v; } } const c = new C(); const a = c.x; c.x = 20; globalThis.result = [a,c.x];', [2, 41]],
        ['context-symbol', 'let context; const key = Symbol("x"); function d(v,c) { context = c; } class C { @d [key] = 42; } const c = new C(); globalThis.result = [context.name === key,context.kind,context.static,context.private,context.access.get(c),context.access.has(c)];', [true, 'field', false, false, 42, true]],
        ['late-initializer', 'let add; function d(v,c) { add = c.addInitializer; } class C { @d m() {} } try { add(() => {}); } catch(e) { globalThis.result = e instanceof TypeError; }', true],
        ['nested', 'function d(v,c) {} class Outer { @d m() { return @d class Inner { @d x = 42; }; } } const C = new Outer().m(); globalThis.result = [C.name,new C().x];', ['Inner', 42]],
    ]) for (const target of ['es2015', 'es2022']) for (const useDefineForClassFields of [false, true])
        transformCases.push({name: `esdecorator-execute-${name}-${target}-${useDefineForClassFields}`, text, runtimeControl: `globalThis.result = ${JSON.stringify(result)};`,
            eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map: true, options: {target, useDefineForClassFields}});
    for (const [name, text, referenceOutcome] of [
        ['private-logical', 'const trace = []; class C { #value = 42; get #x() { trace.push("get"); return this.#value; } set #x(v) { trace.push(v); this.#value = v; } f() { return this.#x ||= 1; } } globalThis.result = [new C().f(), trace];', {value: [42, ['get', 42]]}],
        ['super-logical', 'const trace = []; class B { static get x() { trace.push("get"); return 42; } static set x(v) { trace.push(v); } } class C extends B { static value = super.x ||= 1; } globalThis.result = [C.value, trace];', {value: [42, ['get', 42]]}],
        ['private-logical-and', 'const trace = []; class C { get #x() { trace.push("get"); return 0; } set #x(v) { trace.push(v); } f() { return this.#x &&= 1; } } globalThis.result = [new C().f(), trace];', {value: [0, ['get', 0]]}],
        ['super-logical-and', 'const trace = []; class B { static get x() { trace.push("get"); return 0; } static set x(v) { trace.push(v); } } class C extends B { static value = super.x &&= 1; } globalThis.result = [C.value, trace];', {value: [0, ['get', 0]]}],
        ['private-logical-nullish', 'const trace = []; class C { get #x() { trace.push("get"); return 42; } set #x(v) { trace.push(v); } f() { return this.#x ??= 1; } } globalThis.result = [new C().f(), trace];', {value: [42, ['get', 42]]}],
        ['super-logical-nullish', 'const trace = []; class B { static get x() { trace.push("get"); return 42; } static set x(v) { trace.push(v); } } class C extends B { static value = super.x ??= 1; } globalThis.result = [C.value, trace];', {value: [42, ['get', 42]]}],
        ['private-parenthesized-call', 'class C { #x = 42; #f() { return this.#x; } f() { return (this.#f)(); } } globalThis.result = new C().f();', {error: 'TypeError'}],
        ['private-parenthesized-tag', 'class C { #x = 42; #f() { return this.#x; } f() { return (this.#f)`x`; } } globalThis.result = new C().f();', {error: 'TypeError'}],
        ['super-parenthesized-call', 'class B { static f() { return this.name; } } class C extends B { static value = (super.f)(); } globalThis.result = C.value;', {error: 'TypeError'}],
        ['super-parenthesized-tag', 'class B { static f() { return this.name; } } class C extends B { static value = (super.f)`x`; } globalThis.result = C.value;', {error: 'TypeError'}],
    ]) for (const target of ['es2015', 'es2021', 'es2022'])
        transformCases.push({name: `class-correction-${name}-${target}`, text, runtimeControl: text, referenceOutcome: target === 'es2022' ? undefined : referenceOutcome,
            referenceBug: name, eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map: true, options: {target}});
    for (const item of [...transformCases].filter(c => c.name.startsWith('class-correction-') && c.referenceBug?.includes('logical'))) {
        const text = item.referenceBug.endsWith('-and') ? item.text.replace('return 0;', 'return 42;')
            : item.referenceBug.endsWith('-nullish') ? item.text.replace('return 42;', 'return null;')
            : item.referenceBug === 'private-logical' ? item.text.replace('#value = 42;', '#value = 0;') : item.text.replace('return 42;', 'return 0;');
        transformCases.push({...item, name: item.name.replace('-es', '-write-es'), text, runtimeControl: text,
            referenceOutcome: item.options.target === 'es2022' ? undefined : {value: [1, ['get', 1]]}});
    }
    for (const text of [
        '@dec class C { x = 1; static y = 2; }',
        '@dec class C { #x = 1; static #y = 2; m() { return this.#x; } static m() { return C.#y; } }',
        '@dec class C { static self = this; static { use(this); } }',
        '@dec class C extends B { static x = super.x; }',
        '@dec export default class { static x = 1; }',
        'class C { @dec [key()] = 1; @dec static [next()] = 2; }',
        '@dec class C { @dec [key()] = 1; @dec static [next()] = 2; }',
        'class C { @dec accessor x = 1; }',
        '@dec class C { accessor x = 1; static accessor y = 2; }',
        '@dec class C { #m() { return C; } f() { return this.#m(); } }',
        'function f() { @dec class C { #x = 1; [key()] = 2; } return C; }',
    ]) for (const target of ['es2015', 'es2022']) for (const useDefineForClassFields of [false, true]) for (const map of [false, true])
        transformCases.push({name: `class-legacy-${transformCases.length}`, text, eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, legacyDecorators: true, map,
            options: {target, useDefineForClassFields, experimentalDecorators: true}});
    for (const [name, text, result] of [
        ['instance', 'function dec(C) { return class extends C {}; } @dec class C { x = 42; } globalThis.result = new C().x;', 42],
        ['private', 'function dec(C) { return class extends C {}; } @dec class C { #x = 42; f() { return this.#x; } } globalThis.result = new C().f();', 42],
        ['static', 'function dec(C) { return class extends C {}; } @dec class C { static x = 42; } globalThis.result = C.x;', 42],
        ['computed-once', 'const trace = []; function key() { trace.push("key"); return "x"; } function dec(C) { trace.push("decorate"); } @dec class C { [key()] = trace.push("init"); } new C(); globalThis.result = trace;', ['key', 'decorate', 'init']],
        ['private-method', 'function dec(C) { return class extends C {}; } @dec class C { #m() { return 42; } f() { return this.#m(); } } globalThis.result = new C().f();', 42],
        ['accessor', 'function dec(C) { return class extends C {}; } @dec class C { accessor x = 42; } globalThis.result = new C().x;', 42],
    ]) for (const target of ['es2015', 'es2022'])
        transformCases.push({name: `class-legacy-execute-${name}-${target}`, text, runtimeControl: `globalThis.result = ${JSON.stringify(result)};`,
            eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, legacyDecorators: true, map: true, options: {target, experimentalDecorators: true}});
    for (const text of [
        'class C { accessor x = 1; accessor y; }',
        'class C extends B { accessor x = 1; }',
        'class C { accessor #x = 1; f() { return this.#x++; } }',
        'class C { static accessor x = 1; static accessor y; }',
        'class C { static accessor #x = 1; static f() { return this.#x++; } }',
        'class C { accessor [key()] = 1; accessor [next()] = 2; }',
        'class C { static accessor [key()] = value(); }',
        'class C { accessor ["x"] = 1; accessor [1] = 2; }',
        'const C = class { accessor x = 1; };',
        'const C = class { static accessor x = 1; };',
        'class C { first = 1; accessor x = this.first; last = this.x; }',
        'class C { #first = 1; accessor x = this.#first; #last = this.x; }',
        'class C { accessor #x = 1; accessor #y = 2; get total() { return this.#x + this.#y; } }',
        'class C { #x_accessor_storage = 1; accessor x = 2; }',
        'class C { accessor x = class { static y = 1; }; }',
        'for (const key of xs) { classes.push(class { accessor [key] = value(); }); }',
        '/* before */ class C { /* field */ accessor x = 1; /* static */ static accessor y = 2; }',
    ]) for (const target of ['es2015', 'es2022', 'esnext']) for (const useDefineForClassFields of [false, true]) for (const map of [false, true])
        transformCases.push({name: `class-accessor-${transformCases.length}`, text, eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map, options: {target, useDefineForClassFields}});
    for (const [name, text, result] of [
        ['public', 'class C { accessor x = 41; } const c = new C(); const before = c.x++; globalThis.result = [before, c.x, Object.keys(c)];', [41, 42, []]],
        ['unset', 'class C { accessor x; } const c = new C(); globalThis.result = [c.x === undefined, Object.hasOwn(c, "x")];', [true, false]],
        ['descriptor', 'class C { accessor x = 42; } const d = Object.getOwnPropertyDescriptor(C.prototype, "x"); globalThis.result = [typeof d.get, typeof d.set, d.enumerable, d.configurable];', ['function', 'function', false, true]],
        ['private', 'class C { accessor #x = 41; f() { return [this.#x++, this.#x]; } } globalThis.result = new C().f();', [41, 42]],
        ['private-brand', 'class C { accessor #x = 42; f(o) { try { return o.#x; } catch (e) { return e.name; } } } globalThis.result = new C().f({});', 'TypeError'],
        ['static', 'class C { static accessor x = 41; } const old = C.x++; globalThis.result = [old, C.x, Object.hasOwn(C, "x")];', [41, 42, true]],
        ['static-private', 'class C { static accessor #x = 41; static f() { return [this.#x++, this.#x]; } } globalThis.result = C.f();', [41, 42]],
        ['computed-once', 'const trace = []; function key() { trace.push("key"); return "x"; } function value() { trace.push("value"); return 42; } class C { accessor [key()] = value(); } const c = new C(); c.x++; globalThis.result = [c.x, trace];', [43, ['key', 'value']]],
        ['backing-collision', 'class C { #x_accessor_storage = 40; accessor x = 2; f() { return this.#x_accessor_storage + this.x; } } globalThis.result = new C().f();', 42],
        ['two-private', 'class C { accessor #x = 40; accessor #y = 2; get total() { return this.#x + this.#y; } } globalThis.result = new C().total;', 42],
        ['mixed-order', 'const trace = []; class C { first = trace.push("first"); accessor x = trace.push("x"); last = trace.push("last"); } new C(); globalThis.result = trace;', ['first', 'x', 'last']],
        ['class-expression', 'const C = class { accessor x = 42; }; globalThis.result = new C().x;', 42],
    ]) for (const target of ['es2015', 'es2022']) for (const useDefineForClassFields of [false, true])
        transformCases.push({name: `class-accessor-execute-${name}-${target}-${useDefineForClassFields}`, text, runtimeControl: `globalThis.result = ${JSON.stringify(result)};`,
            eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map: true, options: {target, useDefineForClassFields}});
    for (const text of [
        'class C { #x = 1; #y; read() { return this.#x; } write(v) { this.#x = v; } }',
        'class C extends B { #x = 1; }',
        'class C { #x = 1; constructor(public y = 2) {} }',
        'class C { static #x = 1; static #y; static read() { return this.#x; } }',
        'class C { #m(x) { return this; } call() { return this.#m(1); } }',
        'class C { static #m(x) { return this; } static call() { return this.#m(1); } }',
        'class C { #x = 1; get #p() { return this.#x; } set #p(v) { this.#x = v; } f() { return this.#p++; } }',
        'class C { static get #x() { return this; } static set #x(v) { use(v); } static f() { this.#x = this.#x; } }',
        'class C { #x = 1; f(o) { return [#x in o, this.#x += 1, o.#x *= 2, ++o.#x, o.#x--]; } }',
        'class C { #x = 1; f(o) { o.#x++; ++this.#x; for (o.#x = 0; o.#x < 10; o.#x++) use(o); } }',
        'class C { #f = fn; call(o) { return [o.#f(1), get().#f(2), this.#f?.(3), o.#f`a${1}`]; } }',
        'class C { #x = 1; f(o) { ({x: this.#x = 2, y: [o.#x], ...get().#x} = o); } }',
        'class C { #x = 1; static make(o) { return class { #x = 2; m() { return this.#x; } }; } }',
        'class C { #x = 1; make(o) { return class { m() { return o.#x; } }; } }',
        'const C = class { #x = 1; get() { return this.#x; } };',
        'const C = class Named { #x = Named; m() { return this.#x; } };',
        'class C { #m() { return C; } f() { return this.#m(); } }',
        'for (const value of xs) { classes.push(class { #x = value; m() { return this.#x; } }); }',
        'class C { #x = 1; [key()] = 2; [last()]() {} }',
        'class C { #x = 1; static x = new C(); }',
        'class C { #x = 1; async #m(v) { return await v; } async f() { return this.#m(1); } }',
        'class C { *#m() { yield this; } f() { return this.#m(); } }',
        '/* class */ class C { /* field */ #x = 1; /* read */ m() { return /* value */ this.#x; } }',
        'class C { #constructor = 1; #x; #x; }',
        'class C { get #x() { return 1; } get #x() { return 2; } }',
    ]) for (const target of ['es2015', 'es2022']) for (const useDefineForClassFields of [false, true]) for (const map of [false, true])
        transformCases.push({name: `class-private-${transformCases.length}`, text, eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map, options: {target, useDefineForClassFields}});
    for (const [name, text] of [
        ['field', 'class C { #x = 41; #y; f() { this.#x += 1; return [this.#x, this.#y === undefined, Object.keys(this)]; } } globalThis.result = new C().f();'],
        ['static-field', 'class C { static #x = 42; static #y; static f(o) { return [C.#x, #x in o, C.#y === undefined]; } } class D extends C {} globalThis.result = [C.f(C), C.f(D), C.f({})];'],
        ['field-brand', 'class C { #x = 42; f(o) { try { return o.#x; } catch (e) { return e.name; } } has(o) { try { return #x in o; } catch (e) { return e.name; } } } const c = new C(); globalThis.result = [c.f({}), c.has(c), c.has({}), c.has(1)];'],
        ['method-brand', 'class C { #m() { return 42; } f(o) { try { return o.#m(); } catch (e) { return e.name; } } } const c = new C(); globalThis.result = [c.f(c), c.f({})];'],
        ['method-before-field', 'class C { x = this.#m(); #m() { return 42; } } globalThis.result = new C().x;'],
        ['accessors', 'const trace = []; class C { #v = 40; get #x() { trace.push("get"); return this.#v; } set #x(v) { trace.push(v); this.#v = v; } f() { return [this.#x++, ++this.#x, this.#v]; } } globalThis.result = [new C().f(), trace];'],
        ['getter-only', 'class C { get #x() { return 42; } f() { try { this.#x = 1; } catch (e) { return e.name; } } } globalThis.result = new C().f();'],
        ['setter-only', 'class C { set #x(v) {} f() { try { return this.#x; } catch (e) { return e.name; } } } globalThis.result = new C().f();'],
        ['call-receiver', 'class C { #f = function (x) { return [this instanceof C, x]; }; f(o) { return o().#f(42); } } const c = new C(); let calls = 0; globalThis.result = [c.f(() => (calls++, c)), calls];'],
        ['optional-call', 'class C { #f; f() { return this.#f?.(42) ?? 43; } } globalThis.result = new C().f();'],
        ['tag-receiver', 'class C { #f(strings, v) { return [this instanceof C, strings[0], v]; } f() { return this.#f`a${42}`; } } globalThis.result = new C().f();'],
        ['compound-receiver', 'let calls = 0; class C { #x = 40; f(o) { return [o().#x += 2, this.#x]; } } const c = new C(); globalThis.result = [c.f(() => (calls++, c)), calls];'],
        ['bigint-update', 'class C { #x = 41n; f() { const old = this.#x++; return [String(old), String(this.#x)]; } } globalThis.result = new C().f();'],
        ['destructure', 'class C { #x; #y; f() { ({x: this.#x = 42, y: [this.#y]} = {y: [43]}); return [this.#x, this.#y]; } } globalThis.result = new C().f();'],
        ['nested-shadow', 'class C { #x = 1; f(o) { return new class { #x = 2; f() { return this.#x; } }().f(); } } globalThis.result = new C().f();'],
        ['nested-capture', 'class C { #x = 42; f() { const o = this; return new class { f() { return o.#x; } }().f(); } } globalThis.result = new C().f();'],
        ['class-alias', 'class C { #m() { return C; } f() { return this.#m(); } } const Original = C; C = class {}; globalThis.result = new Original().f() === Original;'],
        ['loop-brands', 'const classes = []; for (const n of [1, 2]) classes.push(class { #x = n; f(o) { return [this.#x, #x in o]; } }); const [A, B] = classes; const a = new A(), b = new B(); globalThis.result = [a.f(b), b.f(a)];'],
        ['initializer-order', 'const trace = []; class B {} class C extends B { #x = trace.push("x"); y = trace.push("y"); constructor() { trace.push("before"); super(); trace.push("after"); } } new C(); globalThis.result = trace;'],
        ['static-method', 'class C { static #m(v) { return [this === C, v]; } static f() { return this.#m(42); } } globalThis.result = C.f();'],
    ]) for (const target of ['es2015', 'es2022'])
        transformCases.push({name: `class-private-execute-${name}-${target}`, text, runtimeControl: text, eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map: true, options: {target}});
    for (const text of [
        'class C extends B { static x = super.x; static y = super[key()]; }',
        'class C extends B { static x = super.m(1); static y = super[key()](2); static z = super.tag`x${1}`; }',
        'class C extends B { static x = super.x = 1; static y = super[key()] += value(); }',
        'class C extends B { static x = ++super.x; static y = super[key()]--; }',
        'class C extends B { static { super.x++; ++super[key()]; super.x += value(); } }',
        'class C extends B { static { for (super.x = 0; super.x < 3; super.x++) use(super.x); } }',
        'class C extends B { static { ({x: super.x = 1, y: [super[key()]], ...super.rest} = source); [super.x, ...super.rest] = source; } }',
        'class C extends B { static { this.f = () => super.m(); } m() { return super.m(); } static m() { return super.m(); } }',
        'const C = class Named extends B { static x = super.m(); };',
        'class C extends B { static { class D extends B { static x = super.x; } use(D); } }',
        'class C extends B { static { super.x &&= 1; super[key()] ||= 2; super.x ??= 3; } }',
        '/* class */ class C extends /* base */ B { /* field */ static x = /* read */ super.x; }',
    ]) for (const target of ['es2015', 'es2022']) for (const map of [false, true])
        transformCases.push({name: `class-super-${transformCases.length}`, text, eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map, options: {target}});
    for (const [name, text] of [
        ['read-receiver', 'class B { static get value() { return this.name; } } class C extends B { static value = super.value; } globalThis.result = C.value;'],
        ['computed-call', 'const trace = []; function key() { trace.push("key"); return "f"; } class B { static f(v) { trace.push(this.name); return v + 1; } } class C extends B { static value = super[key()](41); } globalThis.result = [C.value, trace];'],
        ['tag-receiver', 'class B { static tag(strings, v) { return [this.name, strings[0], v]; } } class C extends B { static value = super.tag`a${42}`; } globalThis.result = C.value;'],
        ['assignment-result', 'const trace = []; class B { static set x(v) { trace.push([this.name, v]); } } class C extends B { static value = super.x = 42; } globalThis.result = [C.value, trace];'],
        ['compound-order', 'const trace = []; function key() { trace.push("key"); return "x"; } function rhs() { trace.push("rhs"); return 2; } class B { static get x() { trace.push("get:" + this.name); return 40; } static set x(v) { trace.push([this.name, v]); } } class C extends B { static value = super[key()] += rhs(); } globalThis.result = [C.value, trace];'],
        ['prefix-result', 'const trace = []; class B { static get x() { return 41; } static set x(v) { trace.push(v); } } class C extends B { static value = ++super.x; } globalThis.result = [C.value, trace];'],
        ['postfix-result', 'const trace = []; class B { static get x() { return "41"; } static set x(v) { trace.push(v); } } class C extends B { static value = super.x++; } globalThis.result = [C.value, trace];'],
        ['bigint-update', 'const trace = []; class B { static get x() { return 41n; } static set x(v) { trace.push(String(v)); } } class C extends B { static value = super.x++; } globalThis.result = [String(C.value), trace];'],
        ['discarded-update', 'const trace = []; class B { static get x() { trace.push("get"); return 41; } static set x(v) { trace.push(v); } } class C extends B { static { super.x++; ++super.x; } } globalThis.result = trace;'],
        ['for-update', 'const trace = []; let value = 0; class B { static get x() { return value; } static set x(v) { value = v; } } class C extends B { static { for (super.x = 0; super.x < 3; ++super.x) trace.push(super.x); } } globalThis.result = trace;'],
        ['destructure-target', 'const trace = []; class B { static set x(v) { trace.push([this.name, v]); } } class C extends B { static { ({x: super.x = 42} = {}); [super.x] = [43]; } } globalThis.result = trace;'],
        ['nested-arrow', 'class B { static f() { return this.name; } } class C extends B { static { this.f = () => super.f(); } } globalThis.result = C.f();'],
    ]) for (const target of ['es2015', 'es2022'])
        transformCases.push({name: `class-super-execute-${name}-${target}`, text, runtimeControl: text, eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map: true, options: {target}});
    for (const text of [
        'class C { x = 1; y; z = this.x + 1; }',
        'class C { x = 1; constructor() { body(); } }',
        'class C { x = 1; constructor() { "use strict"; body(); } }',
        'class C extends B { x = 1; }',
        'class C extends null { x = 1; }',
        'class C extends B { x = 1; constructor() { before(); super(); after(); } }',
        'class C extends B { x = 1; constructor() { try { before(); super(); after(); } catch (e) { error(e); } finally { finish(); } } }',
        'class C { x = 1; constructor(public p: number) { body(); } }',
        'class C extends B { x = this.p; constructor(public p: number) { super(); body(); } }',
        'class C { x = foo?.bar(); constructor() { const y = baz?.(); } }',
        'class C { static x = 1; static y; static z = C.x + 1; }',
        'class C { static x = this; static { use(this); } }',
        'class C { static { let x = 1; use(x); } static { use(2); } }',
        'class C { static { // inside\n } }',
        'const C = class { static { use(this); } };',
        'const C = class { static x = 1; };',
        'const C = class Named { static x = Named; m() { return Named; } };',
        'class C { [key()] = value(); [next()]() {} [last()] = value(); }',
        'class C { [key()] = value(); static [next()] = value(); }',
        'class C { ["x"] = 1; [1] = 2; [true] = 3; }',
        'class C { [key()]; }',
        'const C = class { [key()] = value(); };',
        'for (const x of xs) { classes.push(class { [x] = x; }); }',
        'function f() { return class { [this.key] = 1; }; }',
        'export default class { static x = 1; }',
        'export default class C { static x = 1; }',
        'class C { x = class { static y = 1; }; }',
        'const obj = { foo: class { static x = 1; }, [key()]: class { static x = 2; } };',
        'function f(C = class { static x = 1; }) { return C; }',
        'class C { static { this.f = () => this; this.g = function () { return this; }; } }',
        '/* before */ class C { /* x */ x = 1; /* y */ static y = 2; // end\n }',
    ]) for (const target of ['es2015', 'es2022', 'esnext']) for (const useDefineForClassFields of [undefined, false, true]) for (const map of [false, true])
        transformCases.push({name: `classfields-${transformCases.length}`, text, eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map,
            options: {target, ...(useDefineForClassFields === undefined ? {} : {useDefineForClassFields})}});
    for (const useDefineForClassFields of [false, true]) for (const [name, text, result] of [
        ['instance-order', 'const trace = []; class C { x = trace.push("x"); y = trace.push("y"); constructor() { trace.push("body"); } } new C(); globalThis.result = trace;', ['x', 'y', 'body']],
        ['derived-order', 'const trace = []; class B { constructor() { trace.push("base"); } } class C extends B { x = trace.push("x"); constructor() { trace.push("before"); super(); trace.push("after"); } } new C(); globalThis.result = trace;', ['before', 'base', 'x', 'after']],
        ['nested-super', 'const trace = []; class B { constructor() { trace.push("base"); } } class C extends B { x = trace.push("x"); constructor() { try { super(); trace.push("after"); } finally { trace.push("finally"); } } } new C(); globalThis.result = trace;', ['base', 'x', 'after', 'finally']],
        ['synthetic-derived', 'class B { constructor(x) { this.b = x; } } class C extends B { x = this.b + 1; } globalThis.result = new C(41).x;', 42],
        ['parameter-property', 'class B {} class C extends B { x = this.p + 1; constructor(public p: number) { super(); } } globalThis.result = [new C(41).p, new C(41).x];', useDefineForClassFields ? [41, null] : [41, 42]],
        ['descriptor', 'class C { x = 42; } globalThis.result = Object.getOwnPropertyDescriptor(new C(), "x");', {value: 42, writable: true, enumerable: true, configurable: true}],
        ['unset-field', 'class C { x; } globalThis.result = Object.hasOwn(new C(), "x");', useDefineForClassFields],
        ['setter-semantics', 'const trace = []; class B { set x(value) { trace.push(value); } } class C extends B { x = 42; } const c = new C(); globalThis.result = [trace, Object.hasOwn(c, "x")];', useDefineForClassFields ? [[], true] : [[42], false]],
        ['static-order', 'const trace = []; class C { static x = trace.push("x"); static { trace.push("block"); } static y = trace.push("y"); } globalThis.result = trace;', ['x', 'block', 'y']],
        ['static-this', 'class C { static self = this; static { this.value = 42; } } globalThis.result = [C.self === C, C.value];', [true, 42]],
        ['static-class-alias', 'class C { static self = this; static f = () => C; } const original = C; C = class {}; globalThis.result = [original.self === original, original.f() === original];', [true, true]],
        ['expression-class-alias', 'const C = class Named { static x = Named; m() { return Named; } }; globalThis.result = [C.x === C, new C().m() === C];', [true, true]],
        ['static-dynamic-this', 'class C { static { this.f = function () { return this.value; }; } } globalThis.result = C.f.call({value: 42});', 42],
        ['static-var-scope', 'const trace = []; class C { static { var x = 1; trace.push(x); } static { var x = 2; trace.push(x); } } globalThis.result = trace;', [1, 2]],
        ['computed-order', 'const trace = []; function key(n) { trace.push("key:" + n); return n; } function value(n) { trace.push("value:" + n); return n; } class C { [key("x")] = value("x"); static [key("y")] = value("y"); [key("m")]() {} } new C(); globalThis.result = trace;', ['key:x', 'key:y', 'key:m', 'value:y', 'value:x']],
        ['computed-loop', 'const classes = []; for (const x of ["a", "b"]) { classes.push(class { [x] = x; }); } globalThis.result = classes.map(C => Object.keys(new C()));', [['a'], ['b']]],
        ['computed-this', 'function f() { return class { [this.key] = 42; }; } const C = f.call({key: "x"}); globalThis.result = new C().x;', 42],
        ['inferred-class-name', 'const C = class { static x = 1; }; const object = {named: class { static x = 2; }}; globalThis.result = [C.name, object.named.name];', ['C', 'named']],
        ['field-class-name', 'class C { nested = class { static x = 42; }; } globalThis.result = new C().nested.name;', 'nested'],
    ]) for (const target of ['es2015', 'es2022', 'esnext']) {
        // Native fields run before the constructor body, including parameter-property assignments.
        const expected = name === 'parameter-property' && target === 'es2015' ? [41, 42]
            : name === 'field-class-name' && target !== 'es2015' && !useDefineForClassFields ? '' : result;
        transformCases.push({name: `classfields-execute-${name}-${target}-${useDefineForClassFields}`, text, runtimeControl: `globalThis.result = ${JSON.stringify(expected)};`,
            eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map: true, options: {target, useDefineForClassFields}});
    }
    for (const text of [
        'using x = resource(); use(x);',
        'await using x = resource(); use(x);',
        '"use strict"; const before = 1; using x = resource(); let after = before + 1;',
        'function f() { "use strict"; using x = resource(); return x; }',
        'async function f() { await using x = resource(), y = resource(); return x; }',
        'async function* f() { await using x = resource(); yield x; }',
        '{ using x = a(), y = b(); { using z = c(); use(z); } use(x, y); }',
        'if (a) { using x = resource(); use(x); } else { using y = resource(); use(y); }',
        'for (using x = resource(); x.ok; x.next()) use(x);',
        'for (using x of resources) use(x);',
        'for (using x of resources) { /* body */ use(x); }',
        'async function f() { for (await using x of resources) use(x); }',
        'async function f() { for await (using x of resources) { use(x); } }',
        'async function f() { for await (await using x of resources) { use(x); } }',
        'outer: for (using x of resources) { if (x) continue outer; break outer; }',
        'using x = resource(); export const y = 1, z = 2; export { x };',
        'using x = resource(); const {a, b: c = 3, ...rest} = value; export const [d, ...e] = items;',
        'using x = resource(); import { y } from "dep"; export { y }; function f() { return x; }',
        'using x = resource(); class C { m() { return x; } } export class D extends C {}',
        'using x = resource(); export default class {}',
        'using x = resource(); export default class C { static m() { return C; } }',
        'using x = resource(); export default 42;',
        'using x = resource(); export default (() => 42);',
        'using x = resource(); export = x;',
        'using x = resource(); export default function f() { return x; }',
        'using x = resource(); var y; let z; const f = function () {};',
        'function f() { using x = function () {}, y = () => 1, z = class {}; }',
        'function f() { using x = (function () {}), y = (class {}); }',
        'function f() { using x = class C {}; }',
        '/* before */ using /* name */ x /* init */ = resource(); // after\nuse(x);',
        'function f() { /* leading */ using x = resource(); /* body */ use(x); /* end */ }',
        'using x = a?.resource() ?? fallback();',
        'const env_1 = 1, e_1 = 2, result_1 = 3; await using x = resource();',
        'using {x} = invalid;',
        'class C { m() { using x = resource(); return x; } }',
    ]) for (const target of ['es2015', 'es2018', 'es2022', 'esnext']) for (const map of [false, true])
        transformCases.push({name: `using-${transformCases.length}`, text, eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map, options: {target}});
    const resourcePrelude = 'Symbol.dispose ??= Symbol("dispose"); Symbol.asyncDispose ??= Symbol("asyncDispose"); const trace = []; function resource(n) { trace.push("open:" + n); return {[Symbol.dispose]() { trace.push("close:" + n); }}; } ';
    for (const [name, text, result] of [
        ['reverse-order', '{ using a = resource("a"), b = resource("b"); trace.push("body"); } globalThis.result = trace;', ['open:a', 'open:b', 'body', 'close:b', 'close:a']],
        ['return', 'function f() { using a = resource("a"); return 42; } globalThis.result = [f(), trace];', [42, ['open:a', 'close:a']]],
        ['throw', 'try { using a = resource("a"); throw "body"; } catch (e) { trace.push(e); } globalThis.result = trace;', ['open:a', 'close:a', 'body']],
        ['throw-undefined', 'let caught = false; try { using a = resource("a"); throw undefined; } catch (e) { caught = e === undefined; } globalThis.result = [caught, trace];', [true, ['open:a', 'close:a']]],
        ['suppressed', 'let caught; try { using a = {[Symbol.dispose]() { throw "a"; }}, b = {[Symbol.dispose]() { throw "b"; }}; throw "body"; } catch (e) { caught = [e.name, e.error, e.suppressed.name, e.suppressed.error, e.suppressed.suppressed]; } globalThis.result = caught;', ['SuppressedError', 'a', 'SuppressedError', 'b', 'body']],
        ['initializer-failure', 'function fail() { throw "init"; } try { using a = resource("a"), b = fail(); } catch (e) { trace.push(e); } globalThis.result = trace;', ['open:a', 'close:a', 'init']],
        ['nullish', '{ using a = null, b = undefined; trace.push("body"); } globalThis.result = trace;', ['body']],
        ['invalid-value', 'try { using a = 1; } catch (e) { globalThis.result = [e.name, e.message]; }', ['TypeError', 'Object expected.']],
        ['invalid-disposer', 'try { using a = {}; } catch (e) { globalThis.result = [e.name, e.message]; }', ['TypeError', 'Object not disposable.']],
        ['receiver-and-getter', 'let reads = 0; const object = {value: 42, get [Symbol.dispose]() { reads++; return function () { trace.push(this.value); }; }}; { using x = object; } globalThis.result = [reads, trace];', [1, [42]]],
        ['nested', '{ using a = resource("a"); { using b = resource("b"); } trace.push("middle"); } globalThis.result = trace;', ['open:a', 'open:b', 'close:b', 'middle', 'close:a']],
        ['for-of', 'for (using x of [resource("a"), resource("b")]) { trace.push("body"); } globalThis.result = trace;', ['open:a', 'open:b', 'body', 'close:a', 'body', 'close:b']],
        ['loop-jumps', 'outer: for (using x of [resource("a"), resource("b")]) { if (trace.length === 2) continue outer; break outer; } globalThis.result = trace;', ['open:a', 'open:b', 'close:a', 'close:b']],
        ['for-initializer', 'for (using x = resource("a"); trace.length < 3; trace.push("next")) { trace.push("body"); } globalThis.result = trace;', ['open:a', 'body', 'next', 'close:a']],
        ['async-dispose', 'async function f() { await using a = {[Symbol.asyncDispose]: async function () { trace.push("async"); await 0; trace.push("done"); }}; trace.push("body"); } globalThis.result = f().then(() => trace);', ['body', 'async', 'done']],
        ['async-fallback', 'async function f() { await using a = resource("a"); trace.push("body"); } globalThis.result = f().then(() => trace);', ['open:a', 'body', 'close:a']],
        ['async-suppressed', 'async function f() { try { await using a = {[Symbol.asyncDispose]: async function () { throw "dispose"; }}; throw "body"; } catch (e) { return [e.name, e.error, e.suppressed]; } } globalThis.result = f();', ['SuppressedError', 'dispose', 'body']],
        ['async-for-of', 'async function f() { for await (await using x of [resource("a"), resource("b")]) { trace.push("body"); } return trace; } globalThis.result = f();', ['open:a', 'open:b', 'body', 'close:a', 'body', 'close:b']],
        ['generator-close', 'async function* f() { await using a = resource("a"); yield 42; } globalThis.result = (async () => { const g = f(); const first = await g.next(); await g.return(); return [first, trace]; })();', [{value: 42, done: false}, ['open:a', 'close:a']]],
    ]) for (const target of ['es2015', 'es2018', 'es2022'])
        transformCases.push({name: `using-execute-${name}-${target}`, text: resourcePrelude + text, runtimeControl: `globalThis.result = ${JSON.stringify(result)};`,
            eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map: true, options: {target}});
    for (const text of [
        'async function f() { for await (const x of xs) use(x); }',
        'async function f() { for await (let x of values()) { await use(x); } }',
        'async function f() { for await (x of xs) { if (x) break; } }',
        'async function f() { for await (var [x, y = 2] of xs) use(x, y); }',
        'async function f() { for await (const {x, ...rest} of xs) use(x, rest); }',
        'async function f() { for await ({x, ...rest} of xs) use(x, rest); }',
        'async function f() { outer: inner: for await (const x of xs) { if (x) continue outer; break inner; } }',
        'async function f() { outer: while (true) { for await (const x of xs) break outer; } }',
        'async function f() { for (let i = 0; i < 2; i++) for await (const x of xs) use(x); }',
        'async function f() { for await (const x of xs) for await (const y of ys) use(x, y); }',
        'async function f() { do { for await (const x of xs) use(x); } while (true); }',
        'async function f() { for (const key in object) { for await (const x of xs) use(key, x); } }',
        'async function f() { for (const key of keys) { async function g() { for await (const x of xs) use(x); } } }',
        'for await (const x of xs) use(x); export {};',
        'async function* f() {}',
        'async function* f() { yield 1; yield; return 2; }',
        'async function* f(x) { yield await x; return await x; }',
        'async function* f() { yield* xs; yield* getValues(); }',
        'async function* f() { for await (const x of xs) { yield x; } }',
        'async function* f() { outer: inner: for await (const x of xs) { yield x; continue outer; } }',
        'const f = async function* named(x = 1) { yield x; return; };',
        'async function* f({x}, [y], z = 3, ...rest) { yield [x, y, z, rest]; }',
        'async function* f(...rest) { yield rest; }',
        'async function* f(x = obj.m?.()) { yield x; }',
        'async function* f({x, ...rest}) { yield [x, rest]; }',
        'async function* f() { function* g() { yield 1; } const h = async () => await 2; yield* g(); yield await h(); }',
        'async function f() { async function* g() { yield await 1; } return g(); }',
        'class C extends B { async *m(x) { yield super.m(x); yield await super[x](x); } }',
        'class C extends B { async *m(k) { super.x = await 1; super[k]++; yield super.x; } }',
        'class C extends B { async *m(k) { const f = () => super[k](); yield await f(); } }',
        'class C extends B { async *m() { yield class D extends B { async *m() { yield super.m(); } }; } }',
        'const o = { async *m() { yield super.m(); }, async *[key]() { yield 2; } };',
        '/*! head */\nasync function f() {\n// loop\nfor await (/* binding */ const x of /* source */ xs) {\n// body\nawait use(x);\n}\n}',
        '/** gen */\nasync function* f(/* x */ x = 1) {\n// body\nyield /* value */ await /* await */ x;\nreturn /* done */;\n}',
        'async function* f() { "use strict"; "custom"; yield 1; }',
    ]) for (const target of ['es2015', 'es2017', 'es2018']) for (const map of [false, true])
        transformCases.push({name: `forawait-${transformCases.length}`, text, eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map, options: {target}});
    for (const [name, text, runtimeControl] of [
        ['array', 'async function f() { const a = []; for await (const x of [Promise.resolve(1), 2]) a.push(x); return a; } globalThis.result = f();', 'globalThis.result = [1,2];'],
        ['break', 'const log = []; const xs = { [Symbol.asyncIterator]() { let i = 0; return {async next() { log.push("next"); return {value: ++i, done: false}; }, async return() { log.push("return"); return {done: true}; }}; }}; async function f() { for await (const x of xs) { log.push(x); break; } return log; } globalThis.result = f();', 'globalThis.result = ["next",1,"return"];'],
        ['exhaust', 'const log = []; const xs = { [Symbol.asyncIterator]() { let i = 0; return {async next() { return {value: ++i, done: i > 2}; }, async return() { log.push("return"); return {done: true}; }}; }}; async function f() { for await (const x of xs) log.push(x); return log; } globalThis.result = f();', 'globalThis.result = [1,2];'],
        ['throw-precedence', 'const log = []; const xs = { [Symbol.asyncIterator]() { return {async next() { return {value: 1, done: false}; }, async return() { log.push("return"); throw "close"; }}; }}; async function f() { try { for await (const x of xs) throw "body"; } catch(e) { log.push(e); } return log; } globalThis.result = f();', 'globalThis.result = ["return","body"];'],
        ['next-rejection', 'const log = []; const xs = { [Symbol.asyncIterator]() { return {async next() { throw "next"; }, async return() { log.push("return"); return {done:true}; }}; }}; async function f() { try { for await (const x of xs) log.push(x); } catch(e) { log.push(e); } return log; } globalThis.result = f();', 'globalThis.result = ["next"];'],
        ['return', 'const log = []; async function* g() { try { yield 1; yield 2; } finally { log.push("closed"); } } async function f() { for await (const x of g()) return x; } globalThis.result = f().then(x => [x, log]);', 'globalThis.result = [1,["closed"]];'],
        ['labels', 'async function f() { const log = []; outer: inner: for await (const x of [1,2,3]) { if (x === 2) continue outer; log.push(x); if (x === 3) break inner; } return log; } globalThis.result = f();', 'globalThis.result = [1,3];'],
        ['outer-reset', 'async function f() { const log = []; for (let i=0; i<2; i++) { try { for await (const x of [1]) { if (!i) throw "first"; log.push(x); } } catch(e) { log.push(e); } } return log; } globalThis.result = f();', 'globalThis.result = ["first",1];'],
        ['binding', 'async function f() { const log = []; for await (const {x, ...rest} of [{x:1,y:2}]) log.push([x,rest]); return log; } globalThis.result = f();', 'globalThis.result = [[1,{y:2}]];'],
        ['yield-return', 'async function* f(x) { yield await x; return Promise.resolve(2); } const g = f(Promise.resolve(1)); globalThis.result = Promise.all([g.next(),g.next(),g.next()]);', 'globalThis.result = [{value:1,done:false},{value:2,done:true},{done:true}];'],
        ['delegate', 'async function* f() { yield* [Promise.resolve(1),2]; yield* (async function*() { yield 3; return 4; })(); } globalThis.result = (async () => { const a=[]; for await (const x of f()) a.push(x); return a; })();', 'globalThis.result = [1,2,3];'],
        ['throw-yield', 'async function* f() { try { yield 1; } catch(e) { yield e; } finally { yield 3; } } const g=f(); globalThis.result = (async()=>[await g.next(),await g.throw(2),await g.next(),await g.next()])();', 'globalThis.result = [{value:1,done:false},{value:2,done:false},{value:3,done:false},{done:true}];'],
        ['parameters', 'let log=[]; async function* f(x, y=(log.push("default"),2), ...rest) { yield [x,y,rest]; } const g=f(1,undefined,3); globalThis.result=g.next().then(x=>[f.length,log,x]);', 'globalThis.result = [1,["default"],{value:[1,2,[3]],done:false}];'],
        ['this-arguments', 'const o={x:40, async *m(y) { yield this.x+arguments[0]; }}; globalThis.result=o.m(2).next();', 'globalThis.result={value:42,done:false};'],
        ['super', 'class B { m(x) { return this.x+x; } } class C extends B { async *m(x) { yield super.m(x); } } const c=new C(); c.x=40; globalThis.result=c.m(2).next();', 'globalThis.result={value:42,done:false};'],
        ['super-index-write', 'class B { get x() { return this.value; } set x(v) { this.value=v; } } class C extends B { async *m(k) { super[k]=await 40; yield ++super[k]+1; } } globalThis.result=new C().m("x").next();', 'globalThis.result={value:42,done:false};'],
    ]) for (const target of ['es2015', 'es2017', 'es2018'])
        transformCases.push({name: `forawait-execute-${name}-${target}`, text, runtimeControl, eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map: true, options: {target}});
    for (const [name, text, runtimeControl, referenceOutcome] of [
        ['parameter-var', 'async function* f(x) { var x; yield x; } globalThis.result=f(42).next();', 'globalThis.result={value:42,done:false};', {value:{done:false}}],
        ['parameter-initializer', 'async function* f(x) { var x=x+1; yield x; } globalThis.result=f(42).next();', 'globalThis.result={value:43,done:false};', {value:{value:null,done:false}}],
        ['parameter-arguments', 'async function* f(x) { x++; yield arguments[0]; } globalThis.result=f(42).next();', 'globalThis.result={value:43,done:false};', {value:{value:42,done:false}}],
        ['arguments-parameter', 'async function* f(arguments) { yield arguments; } globalThis.result=f(42).next();', 'globalThis.result={value:42,done:false};', {error:'TypeError'}],
        ['super-parameter', 'class B { m() { return 42; } } class C extends B { async *f(x=super.m()) { yield x; } } globalThis.result=new C().f().next();', 'globalThis.result={value:42,done:false};', {error:'SyntaxError'}],
        ['super-index-parameter', 'class B { m() { return 42; } } class C extends B { async *f(x=super["m"]()) { yield x; } } globalThis.result=new C().f().next();', 'globalThis.result={value:42,done:false};', {error:'SyntaxError'}],
    ]) for (const target of ['es2015', 'es2017', 'es2018'])
        transformCases.push({name: `forawait-correction-${name}-${target}`, text, runtimeControl, referenceOutcome: target === 'es2018' ? undefined : referenceOutcome,
            referenceBug: target === 'es2018' ? undefined : `generator-${name}`, eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map: true, options: {target}});
    for (const [name, text, runtimeControl] of [
        ['parameter-timing', 'let log=[]; async function* f(x=(log.push("parameter"),42)) { log.push("body"); yield x; } const g=f(); log.push("created"); globalThis.result=g.next().then(x=>[log,x]);', 'globalThis.result=[["parameter","created","body"],{value:42,done:false}];'],
        ['default-var', 'async function* f(x=42) { var x; yield x; } globalThis.result=f().next();', 'globalThis.result={value:42,done:false};'],
        ['parameter-catch', 'async function* f(x) { try { throw 1; } catch(x) { var x=2; } yield x; } globalThis.result=f(42).next();', 'globalThis.result={value:42,done:false};'],
    ]) for (const target of ['es2015', 'es2017', 'es2018'])
        transformCases.push({name: `forawait-execute-${name}-${target}`, text, runtimeControl,
            referenceOutcome: name === 'parameter-catch' && target !== 'es2018' ? {value:{done:false}} : undefined,
            referenceBug: name === 'parameter-catch' && target !== 'es2018' ? 'generator-parameter-catch' : undefined,
            eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map: true, options: {target}});
    for (const text of [
        'async function f() { return 42; } async function g() { return await f(); }',
        'const f = async function named(x: number) { const y = await x; return y; };',
        'const f = async x => await x; const g = async () => ({x: await f(1)});',
        'async function f(a, b = 2, ...rest) { return [a, b, rest]; }',
        'async function f({x}, [y], z = 3) { return await [x, y, z]; }',
        'const f = async ({x}, y = 2, ...rest) => await [x, y, rest];',
        'const f = async (x, ...rest) => [x, rest];',
        'async function f(...rest) { return rest; }',
        'async function f(x) { var x = await g(), y = 2; return x + y; }',
        'async function f({x}) { var {x, y = 2} = await g(); return x + y; }',
        'async function f(x) { var [x, y = 2, ...rest] = await g(); return [x, y, rest]; }',
        'async function f(x) { for (var x = 0, y = 1; x < 2; x++) await g(); }',
        'async function f(x) { for (var x in await g()) { await h(x); } for (var x of await g()) await h(x); }',
        'async function f(x) { for (var {x} of await g()) await h(x); }',
        'async function f(x) { var x; if (x) var x = 3; else var y = 4; while (x) { var x = await g(); } }',
        'async function f(x) { try { var x = await g(); } catch (x) { var x = 2; } finally { var x = 3; } }',
        'async function f(x) { try {} catch ({x}) { var x = await g(); } }',
        'async function f(x) { { let x = 2; await x; } { const x = 3; } return x; }',
        'async function f(x) { switch (await x) { case 1: var x = 2; break; default: var x; } label: do { var x = await g(); } while (x); }',
        'async function f() { return [arguments[0], await g(arguments), {arguments}, {arguments: 1}, obj.arguments]; }',
        'async function f(arguments) { var arguments = await g(); return arguments; }',
        'function f() { return async () => await arguments[0]; }',
        'function f() { const a = async () => arguments[0], b = async () => arguments[1]; return [a, b]; }',
        'async function f() { const a = async () => arguments[0], b = () => arguments[1]; return await a() + b(); }',
        'async function f() { return function() { return arguments[0]; }; }',
        'async function f() { arguments: while (true) { break arguments; } return arguments.length; }',
        'await f(); export {}; const g = async () => await f();',
        'class C extends B { async m(x) { return await super.m(x); } }',
        'class C extends B { async m(x) { return await super[x](x); } }',
        'class C extends B { async m(x) { super.x = await x; super.y++; ++super[z]; return super.x + super[z]; } }',
        'class C extends B { async m(x) { [super.x, super[y]] = await x; ({x: super.z, ...super.rest} = await x); } }',
        'class C extends B { m(x) { const f = async () => await super.m(x); return f(); } }',
        'class C extends B { m(x) { const f = async () => await super[x](x); return f(); } }',
        'class C extends B { get x() { return async () => super.x; } set x(v) { (async () => super.x = v)(); } constructor() { super(); (async () => super.x)(); } }',
        'class C extends B { async m() { return class D extends B { async m() { return super.m(); } }; } }',
        'const o = { async m(x = 1) { return await super.m(x); }, m2() { return async () => super.m(); } };',
        'async function f(x = obj.m?.()) { return await x; }',
        'const f = async (x = obj.m?.()) => await x;',
        'async function f({x, ...rest}, y = 2) { return await [x, rest, y]; }',
        '/*! head */\n/** fn */\nasync function f(/* x */ x = 1) {\n// body\nreturn /* before */ await /* value */ x;\n}',
        'const f = /* fn */ async (/* x */ x) => /* ret */ await /* value */ x;',
        'async function f() { "use strict"; "custom"; return await g(); }',
    ]) for (const target of ['es2015', 'es2016', 'es2017']) for (const map of [false, true])
        transformCases.push({name: `async-${transformCases.length}`, text, eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map, options: {target}});
    for (const [name, text, runtimeControl] of [
        ['resolve', 'async function f(x) { return await x + 2; } globalThis.result = f(Promise.resolve(40));', 'globalThis.result = 42;'],
        ['reject', 'async function f() { try { await Promise.reject("bad"); } catch (e) { return e; } } globalThis.result = f();', 'globalThis.result = "bad";'],
        ['parameters', 'let log = []; async function f(x, y = (log.push("default"), 3), ...rest) { await 0; return [x, y, rest]; } globalThis.result = f(1, undefined, 4, 5).then(x => [f.length, log, x]);', 'globalThis.result = [1,["default"],[1,3,[4,5]]];'],
        ['arrow-parameters', 'const f = async ({x}, y = 2, ...rest) => await [x, y, rest]; globalThis.result = f({x: 1}, undefined, 3).then(x => [f.length, x]);', 'globalThis.result = [1,[1,2,[3]]];'],
        ['parameter-rejection', 'async function f(x = (() => { throw "bad"; })()) { return x; } globalThis.result = f().catch(x => x);', 'globalThis.result = "bad";'],
        ['arguments', 'async function f(x) { await 0; return [arguments[0], {arguments: arguments}.arguments.length]; } globalThis.result = f(42);', 'globalThis.result = [42,1];'],
        ['arrow-arguments', 'function f(x) { return (async () => { await 0; return arguments[0]; })(); } globalThis.result = f(42);', 'globalThis.result = 42;'],
        ['this', 'const o = {x: 40, async m() { const f = async () => this.x + 2; return await f(); }}; globalThis.result = o.m();', 'globalThis.result = 42;'],
        ['colliding', 'async function f(x) { var x = await 4; for (var x of [5, 6]) await x; return x; } globalThis.result = f(2);', 'globalThis.result = 6;'],
        ['catch-shadow', 'async function f(x) { try { throw 5; } catch (x) { var x = await 6; } return x; } globalThis.result = f(42);', 'globalThis.result = 42;'],
        ['incoming-parameter', 'async function f(x) { var x; return await x; } globalThis.result = f(42);', 'globalThis.result = 42;'],
        ['parameter-initializer', 'async function f(x) { const read = () => x; var x = await (x + 1); return read(); } globalThis.result = f(42);', 'globalThis.result = 43;'],
        ['for-parameter', 'async function f(x) { for (var x;;) return await x; } globalThis.result = f(42);', 'globalThis.result = 42;'],
        ['super', 'class B { m(x) { return this.x + x; } } class C extends B { async m(x) { return await super.m(x); } } const c = new C(); c.x = 40; globalThis.result = c.m(2);', 'globalThis.result = 42;'],
        ['super-index-write', 'class B { get x() { return this.value; } set x(v) { this.value = v; } } class C extends B { async m(k) { super[k] = await 40; return ++super[k] + 1; } } globalThis.result = new C().m("x");', 'globalThis.result = 42;'],
        ['super-arrow', 'class B { m() { return this.x; } } class C extends B { m() { return (async () => await super["m"]())(); } } const c = new C(); c.x = 42; globalThis.result = c.m();', 'globalThis.result = 42;'],
    ]) for (const target of ['es2015', 'es2017'])
        transformCases.push({name: `async-execute-${name}-${target}`, text, runtimeControl, eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map: true, options: {target}});
    transformCases.push({name: 'async-arguments-shorthand-key', text: 'async function f() { await 0; return Object.hasOwn({arguments}, "arguments"); } globalThis.result = f(42);',
        runtimeControl: 'globalThis.result = true;', referenceBug: 'async-arguments-shorthand', eraseTypes: true, runtimeSyntax: true, lowerExpressions: true, map: true, options: {target: 'es2015'}});
    for (const text of [
        'const x = {...a}; const y = {a: 1, ...b, c: f(), ...d};',
        'const x = {...{a: 1}, ...b, ...c}; const y = {a, ...b, m() { return this.a; }};',
        'const x = {get a() { return 1; }, ...b, set a(v) { use(v); }};',
        'const x = {nested: {...a, ...b}};',
        'let {a, ...rest} = obj;',
        'const {a: b = f(), ...rest} = obj;',
        'let {a: {b, ...inner}, ...outer} = obj;',
        'let {[key()]: x, a, ...rest} = source();',
        'let {[key]: x = fallback(), ...rest} = source();',
        'let {["x"]: x, [1]: one, [`a`]: a, ...rest} = obj;',
        'let [a, {b, ...rest}, c = fallback(), ...tail] = source;',
        'let [[{a, ...rest}], {b, ...other}] = source;',
        '({a, ...rest} = obj);',
        'const result = ({a, ...rest} = obj);',
        '({a, ...rest} = first, {b, ...other} = second);',
        'return ({a, ...rest} = first, {b, ...other} = second);',
        '({a: {b, ...inner} = fallback(), ...rest} = obj);',
        '([{a, ...rest}, c = f()] = values);',
        '({a, ...rest} = obj = {a: 1});',
        'export const {a, ...rest} = obj;',
        'export let {[key()]: a = f(), ...rest} = obj;',
        'function f({a, ...rest}) { return rest; }',
        'function f({a, ...rest} = source(), x = fallback()) { "use strict"; return [a, rest, x]; }',
        'function f({a, ...rest}, {b} = source(), [c = fallback()] = array()) { return [rest, b, c]; }',
        'function f({a, ...rest}, {} = source(), [] = source()) {}',
        'const f = ({a, ...rest}) => rest;',
        'const f = ({a, ...rest}, x = f()) => ({rest, x});',
        'const f = function({a, ...rest}, x = f()) { return [rest, x]; };',
        'const obj = {m({a, ...rest}, x = f()) { return x; }, set x({a, ...rest}) { use(rest); }};',
        'class C { constructor({a, ...rest}, x = f()) { this.x = rest; } m({a, ...rest}) { return rest; } }',
        'try { throw obj; } catch ({a, ...rest}) { use(a, rest); }',
        'try {} catch ([{a, ...rest}]) { const x = {...rest}; }',
        'for (const {a, ...rest} of values) { use(a, rest); }',
        'for ({a, ...rest} of values) use(a, rest);',
        'for (let [{a, ...rest}] of values) { ({b, ...other} = rest); }',
        'const f = (x = {...source}) => x;',
        'function f({a, ...rest}, x = ({b, ...other} = source)) { return x; }',
        '/** binding */ const { /* a */ a, /* rest */ ...rest } = obj; // tail',
    ]) for (const target of ['es2015', 'es2017', 'es2018']) for (const map of [false, true])
        transformCases.push({name: `object-rest-${transformCases.length}`, text, eraseTypes: true, lowerExpressions: true, map, options: {target}});
    for (const [name, text, runtimeControl] of [
        ['spread-order', 'const log = []; let second = {c: 3}; const first = {get a() { log.push("get"); second = {b: 2}; return 1; }}; const value = {...first, ...second, c: (log.push("last"), 3)}; globalThis.result = [value, log];', 'globalThis.result = [{a: 1, b: 2, c: 3}, ["get", "last"]];'],
        ['spread-mutation', 'const k = {a: 1, b: 2}; const value = {a: 3, ...k, b: k.a++}; globalThis.result = [value, k];', 'globalThis.result = [{a: 1, b: 1}, {a: 2, b: 2}];'],
        ['binding', 'const log = []; const key = () => (log.push("key"), "a"); const source = () => (log.push("source"), {a: undefined, b: 2, c: 3}); const {[key()]: a = (log.push("default"), 42), b, ...rest} = source(); globalThis.result = [a, b, rest, log];', 'globalThis.result = [42, 2, {c: 3}, ["source", "key", "default"]];'],
        ['assignment-result', 'let a, rest; const obj = {a: 1, b: 2}; const result = ({a, ...rest} = obj); globalThis.result = [result === obj, a, rest];', 'globalThis.result = [true, 1, {b: 2}];'],
        ['symbols', 'const a = Symbol("a"), b = Symbol("b"); const {[a]: value, ...rest} = {[a]: 1, [b]: 2, x: 3}; globalThis.result = [value, rest[b], rest.x, Object.getOwnPropertySymbols(rest).length];', 'globalThis.result = [1,2,3,1];'],
        ['parameters', 'const log = []; const source = () => ({get a() { log.push("get"); return 1; }, b: 2}); function f({a, ...rest} = source(), x = (log.push("x"), a + 1), {c} = (log.push("c"), {c: x + 1})) { return [a, rest, x, c]; } globalThis.result = [f(), log];', 'globalThis.result = [[1,{b:2},2,3],["get","x","c"]];'],
        ['loop', 'const results = []; for (const {a, ...rest} of [{a: 1, b: 2}, {a: 3, c: 4}]) results.push([a, rest]); globalThis.result = results;', 'globalThis.result = [[1,{b:2}],[3,{c:4}]];'],
        ['catch', 'try { throw {a: 1, b: 2}; } catch ({a, ...rest}) { globalThis.result = [a, rest]; }', 'globalThis.result = [1,{b:2}];'],
        ['array-pattern', 'const [{a, ...rest}, value = rest.b] = [{a: 1, b: 2}, undefined]; globalThis.result = [a, rest, value];', 'globalThis.result = [1,{b:2},2];'],
    ]) for (const target of ['es2015', 'es2017', 'es2018'])
        transformCases.push({name: `object-rest-execute-${name}-${target}`, text, runtimeControl, eraseTypes: true, lowerExpressions: true, map: true, options: {target}});
    for (const text of [
        'const enum E { A, B = 4, C }; const values = [E.A, E.B, E.C, E["B"]];',
        'const enum E { A = "hello", B = -4, C = 1 / 0, D = -1 / 0, N = 0 / 0, Z = -0 }; use(E.A, E.B, E.C, E.D, E.N, E.Z);',
        'const enum E { A = 1e-7, B = 1e21 }; const x = E.A.toString(), y = E.B ** 2;',
        'const enum E { A = 1, B = A + 2, C = B << 1 }; const x = E.C;',
        'namespace N { export const enum E { A = 42 } } const x = N.E.A;',
        'const enum E { A = "*/" }; const x = E[/* embedded */ "A"];',
        'const enum E { A = "😀" }; /** leading */ const x = /* before */ E.A /* after */;',
        'enum E { A = 42 }; const x = E.A;',
        'import {E} from "./other"; const x = E.A;',
    ]) for (const removeComments of [false, true]) for (const map of [false, true])
        for (const options of [{}, {isolatedModules: true}, {verbatimModuleSyntax: true}])
            transformCases.push({name: `constants-${transformCases.length}`, text, eraseTypes: true, runtimeSyntax: true, inlineConstants: true, map, removeComments,
                options, files: {'/source/other.ts': 'export const enum E { A = 42 }'}});
    for (const text of ['const x = 1;', '"use strict"; const x = 1;', '"use other"; "use strict"; const x = 1;',
        '// header\nconst x = 1;', '/*! license */\n\nconst x = 1;', 'export const x = 1;', 'import "./other"; const x = 1;',
        'interface I {} "use strict"; const x = 1;', '#!/usr/bin/env node\nconst x = 1;'])
        for (const module of ['commonjs', 'esnext', 'nodenext', 'preserve']) for (const moduleFormat of [1, 99]) for (const map of [false, true])
            transformCases.push({name: `strict-${transformCases.length}`, text, eraseTypes: true, useStrict: true, map, moduleFormat, options: {module}});
    for (const text of [
        'tag`\\x`;',
        'tag`\\unicode`;',
        'tag`valid\\n`;',
        'tag`\\x${a}middle${b}tail`;',
        'tag`head${a}\\u{notHex}${b}\\x`;',
        'tag`head${a}middle${b}\\x`;',
        'export const x = tag`\\x${tag`\\unicode`}`;',
        'export function f() { return obj.tag`\\x`; }',
        'tag`\r\nraw\r\\x`;',
    ])
        for (const target of ['es2015', 'es2017', 'es2018', 'esnext']) for (const map of [false, true])
            transformCases.push({name: `tagged-${transformCases.length}`, text, eraseTypes: true, lowerExpressions: true, map, options: {target}});
    for (const [name, text, runtimeControl, flags] of [
        ['constant', 'const enum E { A = 42, S = "text", N = -4 }; globalThis.result = [E.A, E.S, E.N];', 'globalThis.result = [42,"text",-4];', {runtimeSyntax: true, inlineConstants: true}],
        ['strict', 'globalThis.result = (function() { return this === undefined; })();', 'globalThis.result = true;', {useStrict: true}],
        ['tagged', 'function tag(strings, value) { globalThis.result = [strings, strings.raw, value]; } tag`\\x${42}end`;', 'globalThis.result = [[null,"end"],["\\\\x","end"],42];', {lowerExpressions: true}],
    ]) for (const removeComments of [false, true])
        transformCases.push({name: `additional-execute-${name}-${removeComments}`, text, runtimeControl, eraseTypes: true, ...flags, map: true, removeComments, options: {target: 'es2015'}});
    for (const text of [
        'const a = x ** y, b = a ** b ** c; x **= y;',
        'obj.x **= rhs(); obj[key()] **= rhs(); factory()[key()] **= rhs();',
        'function f(x = factory()[key()] **= rhs()) { return x; }',
        'const f = (x = factory().x **= rhs()) => x;',
        'a ||= b; a &&= b; a ??= b;',
        'obj.x ||= rhs(); obj[key()] &&= rhs(); factory()[key()] ??= rhs();',
        'factory().x ||= a ?? b; factory()[key()] &&= a ??= b;',
        'const a = x ?? y, b = f() ?? g(), c = obj.x ?? obj[y];',
        'const f = (x = obj.x ?? 1) => x ?? fallback();',
        'try { f(); } catch { try { g(); } catch { h(); } }',
        'try {} catch (e) { try {} catch { use(e); } }',
        'const a = x?.y, b = x?.[y], c = x?.();',
        'const a = f()?.x.y[z](), b = f().x?.(arg());',
        'const a = f()?.x?.[key()]?.(arg())?.y;',
        'const a = (f()?.x)(), b = ((f()?.[key()]))(arg());',
        'const a = (f().x)?.(), b = ((f()[key()]))?.(arg());',
        'const a = (f()?.x)?.(), b = ((f()?.[key()]))?.(arg());',
        'const a = delete x?.y, b = delete (f()?.[key()]), c = delete ((f()?.x?.y));',
        'function f(x = obj?.m(), y = obj.m?.()) { return [x, y]; }',
        'const f = (x = obj?.x.y) => obj?.[x];',
        'class C extends B { m() { return super.m?.(1); } n() { return super[key()]?.(); } }',
        'const a = (x as T)?.y, b = ((x as T)?.y as U)();',
        'const a = x?.y!.z, b = x?.y!?.z!();',
        'const a = /* before */ f() /* call */ ?? /* after */ g();',
        'function f() { return (\n// retained\nx as T\n)?.y; }',
        'const a = (a ?? b) || c, b = a && (b ?? c);',
        'if (a) obj[key()] **= rhs(); while (x) { factory()[key()] ??= rhs(); }',
        'const a = obj?.m(...args), b = obj.m?.(...args);',
    ]) for (const target of ['es2015', 'es2016', 'es2018', 'es2019', 'es2020', 'es2021', 'esnext']) for (const map of [false, true])
        transformCases.push({name: `expressions-${transformCases.length}`, text, eraseTypes: true, lowerExpressions: true, map, options: {target}});
    for (const [name, text, runtimeControl] of [
        ['power', 'const log = []; const obj = { get x() { log.push("get"); return 3; }, set x(v) { log.push(["set", v]); } }; const f = () => (log.push("object"), obj), k = () => (log.push("key"), "x"), r = () => (log.push("right"), 2); const value = f()[k()] **= r(); globalThis.result = [value, log];', 'globalThis.result = [9, ["object", "key", "get", "right", ["set", 9]]];'],
        ['logical', 'const log = []; const obj = { x: 0, y: 1, z: null }; const f = () => (log.push("object"), obj), k = x => (log.push(x), x), r = () => (log.push("right"), 2); const values = [f()[k("x")] &&= r(), f()[k("y")] ||= r(), f()[k("z")] ??= r()]; globalThis.result = [values, obj, log];', 'globalThis.result = [[0, 1, 2], {x: 0, y: 1, z: 2}, ["object", "x", "object", "y", "object", "z", "right"]];'],
        ['nullish', 'const log = []; const f = x => (log.push(x), x), r = () => (log.push("right"), 42); globalThis.result = [[f(0) ?? r(), f(false) ?? r(), f(null) ?? r(), f(undefined) ?? r()], log];', 'globalThis.result = [[0, false, 42, 42], [0, false, null, "right", null, "right"]];'],
        ['receiver', 'const obj = { x: 42, m() { return this.x; } }; const f = () => obj; globalThis.result = [obj.m?.(), f()?.m(), (f()?.m)(), ((f().m))?.(), (f()?.m)?.()];', 'globalThis.result = [42,42,42,42,42];'],
        ['short-circuit', 'const log = []; const nil = null, obj = { m: null }; const side = () => (log.push("unexpected"), 1); globalThis.result = [nil?.[side()], nil?.(side()), obj.m?.(side()), nil?.x.y.z, log];', 'globalThis.result = [null,null,null,null,[]];'],
        ['delete', 'const log = []; const obj = {x: 1}; const f = () => (log.push("object"), obj), key = () => (log.push("key"), "x"); globalThis.result = [delete f()?.[key()], delete ((null)?.[key()]), obj, log];', 'globalThis.result = [true,true,{},["object","key"]];'],
        ['super', 'class B { m() { return this.x; } } class C extends B { constructor() { super(); this.x = 42; } m() { return super.m?.(); } } globalThis.result = new C().m();', 'globalThis.result = 42;'],
        ['parameters', 'const obj = {x: 3}; const f = (x = obj.x ?? 1) => (obj.x ??= 2) + x; function g(x = obj.x **= 2) { return x; } globalThis.result = [f(), g(), obj.x];', 'globalThis.result = [6,9,9];'],
        ['catch', 'let value; try { throw 1; } catch { try { throw 2; } catch { value = 42; } } globalThis.result = value;', 'globalThis.result = 42;'],
    ]) for (const target of ['es2015', 'es2019', 'es2020', 'es2021'])
        transformCases.push({name: `expressions-execute-${name}-${target}`, text, runtimeControl, eraseTypes: true, lowerExpressions: true, map: true, options: {target}});
    for (const text of [
        '@dec class C {}',
        '@first @second class C { constructor(@param x: number) {} }',
        'class C { @dec x: number; @dec static y = 1; @dec m(@param a: number, b: string, @param c: boolean) {} }',
        'class C { @dec get x(): number { return 1; } set x(@param value: number) {} }',
        'class C { get x(): number { return 1; } @dec set x(@param value: number) {} }',
        'class C { @dec get x() { return 1; } @second set x(value) {} @dec static get x() { return 2; } }',
        'class C { @dec [key()]() {} @dec [key] = 1; @dec ["literal"]() {} @dec [1]() {} }',
        'class C { @dec declare x: number; @dec abstract y: number; @dec abstract m(): void; }',
        '@dec export class C { f() { return C; } }',
        '@dec export default class C { f() { return C; } }',
        '@dec export default class { @member m() {} }',
        'export default class { @member m() {} }',
        '@dec class C { static x = C; static { use(C); } m() { return C; } }',
        '@dec(C) class C { static m() { return C; } }',
        '@dec class C { static C = 1; m() { const x = { C: 2 }; return x.C; } }',
        '@dec class C { m(C) { return C; } }',
        '@dec class C { m() { return class D { m() { return C; } }; } }',
        '@dec class C { m() { @dec class D { m() { return [C, D]; } } return D; } }',
        'function f() { @dec class C { @member [key()]() { return C; } } return C; }',
        'class C { static #dec() {} @C.#dec m() {} }',
        '@dec class C { static #dec() {} @C.#dec m(@C.#dec x) {} }',
        'namespace N { @dec export class C { constructor(@param public x: number) {} m() { return C; } } }',
        '/** Class */\n@dec\nclass C { /** Method */\n@member\nm(/* before */ @param x: number) {} } // tail',
        'class C { @dec m(this: C, @param x: number, ...rest: string[]) {} @dec #x; }',
        'const C = @dec class { @member m(@param x) {} };',
        'class C { @dec accessor x = 1; @dec static accessor y = 2; }',
    ]) for (const target of ['es2015', 'es2022']) for (const metadata of [false, true]) for (const map of [false, true])
        transformCases.push({name: `legacy-decorators-${transformCases.length}`, text, eraseTypes: true, runtimeSyntax: true,
            legacyDecorators: true, metadata, map, options: {target, experimentalDecorators: true, emitDecoratorMetadata: metadata}});
    for (const [name, text, runtimeControl] of [
        ['replacement', 'function dec(C) { return class D extends C {}; } @dec class C { self() { return C; } } globalThis.result = new C().self() === C;', 'globalThis.result = true;'],
        ['computed', 'const log = []; const key = () => (log.push("key"), "m"); function dec(target, name) { log.push(name); } class C { @dec [key()]() { return 42; } } globalThis.result = [log, new C().m()];', 'globalThis.result = [["key", "m"], 42];'],
        ['order', 'const log = []; const dec = n => (log.push("eval:" + n), (...args) => { log.push("apply:" + n); }); @dec("class") class C { constructor(@dec("ctor") x) {} @dec("method") m(@dec("param") x) {} @dec("static") static n() {} } globalThis.result = log;', 'globalThis.result = ["eval:method", "eval:param", "apply:param", "apply:method", "eval:static", "apply:static", "eval:class", "eval:ctor", "apply:ctor", "apply:class"];'],
        ['descriptor', 'function dec(target, key, desc) { const old = desc.value; desc.value = function() { return old.call(this) + 1; }; } class C { @dec m() { return 41; } } globalThis.result = new C().m();', 'globalThis.result = 42;'],
        ['static-alias', 'function dec(C) { return class extends C {}; } @dec class C { static before = C; static { this.equal = C === this; } static self() { return C; } } globalThis.result = [C.before !== C, C.equal, C.self() === C];', 'globalThis.result = [true, true, true];'],
        ['accessors', 'const log = []; function dec(t, k, d) { log.push([k, !!d.get, !!d.set]); } class C { @dec get x() { return 1; } set x(v) {} } globalThis.result = log;', 'globalThis.result = [["x", true, true]];'],
        ['private-expression', 'const log = []; class C { static #dec(t, k) { log.push(k); } @C.#dec m() {} } globalThis.result = log;', 'globalThis.result = ["m"];'],
        ['nested', 'function dec(C) { return class extends C {}; } @dec class C { nested() { @dec class D { self() { return [C, D]; } } return D; } } const D = new C().nested(); const pair = new D().self(); globalThis.result = [pair[0] === C, pair[1] === D];', 'globalThis.result = [true, true];'],
    ]) for (const removeComments of [false, true])
        transformCases.push({name: `legacy-execute-${name}-${removeComments}`, text, runtimeControl, eraseTypes: true, runtimeSyntax: true,
            legacyDecorators: true, map: true, removeComments, options: {target: 'es2022', experimentalDecorators: true}});
    for (const text of [
        '@dec class C { constructor(x: number, y: string, z: boolean) {} }',
        '@dec class C { constructor(x: void, y: undefined, z: never, q: null, a: any, u: unknown) {} }',
        '@dec class C { constructor(x: bigint, y: 1n, z: symbol, r: readonly number[], t: [number, string]) {} }',
        'class C { @dec x: number; @dec y: string; @dec f(x: boolean): Date { return new Date(); } }',
        'class C { @dec f(x: () => number): asserts x {} @dec async g() {} @dec *h() {} }',
        'class C { @dec get x(): string { return ""; } set x(v: number) {} }',
        'class C { get x(): number { return 1; } @dec set x(v: string) {} }',
        'class C { @dec get [key()](): string { return ""; } @dec set [key()](v: number) {} }',
        'class C { constructor(@dec x: number, @dec public y: string) {} m(@dec x: number) {} }',
        'class C { @dec m(this: C, ...x: number[]): void {} @dec n(...x: Array<string>) {} }',
        'class A {} interface I {} type Alias = A; @dec class C { constructor(a: A, b: Alias, c: I) {} }',
        '@dec class C { constructor(a: Unknown, b: Unknown.X, c: Unknown.X.Y.Z) {} }',
        'namespace N { export class A {} } class C { @dec m(x: N.A): N.A { return x; } }',
        'class C { @dec a: string | "x"; @dec b: number | string; @dec c: number | null | undefined; @dec d: never & string; @dec e: unknown & number; }',
        'class C<T> { @dec a: T extends string ? number : number; @dec b: T extends string ? Unknown : void; @dec c: unique symbol; }',
        'export default @dec class { constructor(x: number) {} }',
        'class C { @dec #x: number; @dec static get x(): number { return 1; } get x(): string { return ""; } }',
        'const C = @dec class { @dec x: number; };',
        'class C { @dec a: A | A; @dec b: Unknown.X | Unknown.X; @dec c: Unknown.X | Unknown.Y; @dec d: bigint | 1n; }',
    ]) for (const strictNullChecks of [false, true]) for (const map of [false, true])
        transformCases.push({name: `metadata-${transformCases.length}`, text, eraseTypes: true, metadata: true, map,
            options: {experimentalDecorators: true, emitDecoratorMetadata: true, strictNullChecks}});
    for (const text of [
        'import {A, B} from "./other"; @dec class C { constructor(x: A) {} }',
        'import type {A} from "./other"; import {B} from "./other"; @dec class C { constructor(x: A, y: B) {} }',
        'namespace N { export class A {} @dec export class B { constructor(public a: A) {} } }',
        'class C { @dec x: bigint; @dec y: 1n | bigint; }',
    ]) for (const target of ['es2015', 'es2020']) for (const experimentalDecorators of [false, true]) for (const map of [false, true])
        transformCases.push({name: `metadata-combined-${transformCases.length}`, text, eraseTypes: true, metadata: true, runtimeSyntax: true,
            elideImports: true, map, options: {target, experimentalDecorators, emitDecoratorMetadata: true},
            files: {'/source/other.ts': 'export class A {} export class B {}'}});
    for (const text of [
        'enum E { A, B = 4, C }',
        'enum E { A = "a", B = "b" }; enum F { A = E.A, B = 2, C = B + 1 }',
        'enum E { A = f(), B = A, C = "x" + g() }',
        'const enum E { A, B }; const v = E.B;',
        '/** enum */ enum E { /** first */ A, B = 5 // tail\n} // end\nenum E { C = 6 }',
        'export enum E { A }; export namespace E { export const x = A; }',
        'namespace N { export const x = 1, y = x + 1; export function f() { return x; } }',
        'namespace N { export let x: number; let y = 1; export class C { f() { return x + y; } } }',
        'namespace N { export let a = 1; export const f = () => ({ a }); { let a = 2; use(a); } }',
        'namespace N.A.B { export const x = 1; }',
        'namespace N { export enum E { A }; export namespace M { export const x = E.A; } }',
        'namespace N { export let {x, y: z = 3, ...rest} = source; }',
        'namespace N { export let [x,, y = f(), ...rest] = source; }',
        'namespace N { export let { [key()]: x, nested: {y = f()}, ...rest } = source(); }',
        'namespace N { export let {} = source(); export let [] = source(); export let [,,] = source(); }',
        'namespace N { export import A = B.C; import D = A.E; export const x = D; }',
        'namespace N { import x from "x"; export { x }; { import A = B.C; } }',
        'class C { constructor(public x: number, private y = 2, readonly z = 3) { use(x, y); } }',
        'class C extends B { constructor(public x: number) { "use strict"; before(); super(x); after(); } }',
        'class C extends B { constructor(public x: number) { try { before(); try { super(x); after(); } finally { done(); } } catch (e) { use(e); } } }',
        'const C = class { constructor(public x = 1) {} };',
        'function N() {} namespace N { export const x = 1; } class C {} namespace C { export const x = 2; }',
        'namespace N { export const x = 1; export function f(x: number) { return x; } export const g = (y = x) => y; }',
    ]) for (const map of [false, true]) for (const options of [{}, {preserveConstEnums: true}, {isolatedModules: true}])
        transformCases.push({name: `runtime-syntax-${transformCases.length}`, text, eraseTypes: true, runtimeSyntax: true, map, options});
    for (const [name, text, runtimeControl] of [
        ['enum', 'enum E { A, B = 4, C }; enum S { X = "x" }; globalThis.result = [E.A, E.B, E.C, E[5], S.X, S["x"]];', 'globalThis.result = [0, 4, 5, "C", "x", null];'],
        ['namespace', 'namespace N { export let x = 1; export function f() { return ++x; } export const g = () => ({x}); } globalThis.result = [N.f(), N.g(), N.x];', 'globalThis.result = [2, {x: 2}, 2];'],
        ['nested-namespace', 'namespace N.A.B { export const x = 42; } globalThis.result = N.A.B.x;', 'globalThis.result = 42;'],
        ['parameter-properties', 'class B { constructor(x) { this.log = [x]; } } class C extends B { constructor(public x = 3, public y = 4) { super(x); this.log.push(this.x, this.y); } } const c = new C(); globalThis.result = [c.x, c.y, c.log];', 'globalThis.result = [3, 4, [3, 3, 4]];'],
        ['destructuring', 'let log = []; const key = () => (log.push("key"), "a"); const source = () => (log.push("source"), {a: undefined, b: 2, c: 3}); namespace N { export let {[key()]: x = (log.push("default"), 4), b, ...rest} = source(); } globalThis.result = [log, N.x, N.b, N.rest];', 'globalThis.result = [["source", "key", "default"], 4, 2, {c: 3}];'],
        ['array-destructuring', 'namespace N { export let [x,,y = 4,...rest] = [1,2,undefined,5,6]; } globalThis.result = [N.x, N.y, N.rest];', 'globalThis.result = [1,4,[5,6]];'],
    ]) for (const removeComments of [false, true])
        transformCases.push({name: `runtime-syntax-execute-${name}-${removeComments}`, text, runtimeControl, eraseTypes: true, runtimeSyntax: true, map: true, removeComments});
    for (const text of [
        '"use strict"; const x = $var;',
        'const x = $let;',
        'function f() { "use strict"; return $var; } const y = $var;',
        'function f(x = $var) { return x; }',
        'function f({x} = $var, y = $var) { return x + y; }',
        'const f = (x = $var) => x;',
        'const f = (x = $var) => ({x});',
        'while (x) use($let, $var);',
        'for (let x of xs) { use($let, $var); split(); }',
        'if (x) split(); else remove();',
        'while (x) remove(); label: split();',
        'split(); function f() { split(); }',
        'try { split(); } catch { split(); } finally { split(); }',
    ]) for (const map of [false, true])
        transformCases.push({name: `environments-${transformCases.length}`, text, environments: true, map});
    const erase = JSON.parse(await execute(oracle, ["--fixtures", path.join(source, "internal/transformers/tstransforms/typeeraser_test.go")]));
    assert(erase.length > 60);
    for (const item of erase) for (const map of [false, true])
        transformCases.push({...item, name: `erase-${transformCases.length}-${item.name}`, eraseTypes: true, map});
    const elision = JSON.parse(await execute(oracle, ["--fixtures", path.join(source, "internal/transformers/tstransforms/importelision_test.go")]));
    assert(elision.length >= 18);
    for (const item of elision) for (const map of [false, true])
        transformCases.push({...item, text: item.text.replaceAll('"other"', '"./other"'), name: `elision-${transformCases.length}-${item.name}`,
            eraseTypes: true, elideImports: true, map, files: item.other ? {"/source/other.ts": item.other} : {}});
    for (const text of [
        '/*! header */\n\ninterface I {}\n// runtime\nconst x: number /* type */ = 1;',
        'namespace N { export const enum E { A } } namespace X { export const x: number = 1; }',
        'class C { constructor(public x: number, readonly y: string) {} abstract m(): void; declare z: number; }',
        '@dec class C { @dec declare x: number; @dec abstract y: string; m(@dec x: number) {} }',
        'function f() { return (\n// retained\nx as T\n); }',
        'function f() { throw (\n// retained\nx satisfies T\n); }',
        'function* f() { yield (\n// retained\nx!\n); }',
        'const a = ((x as T) as U).name, b = (a! as T)!; const c = (a + b as T) * c;',
        'import type X from "x"; import {type Y, Z} from "x"; export {type Y, Z}; export type {X};',
        'const f = <T>(x?: T): T => x!; class C<T> extends B<T> implements I<T> { m<U>(x: U): T {} }',
    ]) for (const map of [false, true]) for (const options of [{}, {verbatimModuleSyntax: true}, {experimentalDecorators: true}, {preserveConstEnums: true}])
        transformCases.push({name: `erase-extra-${transformCases.length}`, text, eraseTypes: true, map, options});
    for (const [name, text, runtimeControl] of [
        ["return-comment", 'function f(): number { return (\n// retained\n42 as number\n); } globalThis.result = f();', 'globalThis.result = 42;'],
        ["throw-comment", 'function f() { throw (\n// retained\n42 satisfies number\n); } try { f(); } catch (e) { globalThis.result = e; }', 'globalThis.result = 42;'],
        ["yield-comment", 'function* f() { yield (\n// retained\n42 as number\n); } globalThis.result = [...f()];', 'globalThis.result = [42];'],
        ["call-comment", 'function f() { return (\n// retained\n(() => 42) as Function\n)(); } globalThis.result = f();', 'globalThis.result = 42;'],
        ["binary-comment", 'function f() { return (\n// retained\n40 as number\n) + 2; } globalThis.result = f();', 'globalThis.result = 42;'],
    ]) for (const removeComments of [false, true])
        transformCases.push({name: `erase-runtime-${name}-${removeComments}`, text, eraseTypes: true, map: true, removeComments, runtimeControl});
}
const allCases = transforms ? transformCases : cases;
const selected = filter ? allCases.filter(c => c.name.includes(filter)) : allCases;
await json(path.join(directory, "inputs.json"), selected);
const input = selected.map(c => JSON.stringify(c)).join("\n") + "\n";
const dll = path.join(option('--managed-directory', path.join(root, 'csharp/tests/TypeScript.Compatibility/bin/Release/net11.0')), 'TypeScript.Compatibility.dll');
const before = (await execute(oracle, [], input)).trim().split(/\r?\n/).map(JSON.parse);
const after = (await execute(dotnet, [dll, "--printer-lines"], input)).trim().split(/\r?\n/).map(JSON.parse);
assert.equal(before.length, selected.length);
assert.equal(after.length, selected.length);
await json(path.join(directory, "results.json"), selected.map((input, i) => ({input, reference: before[i], candidate: after[i]})));
const failures = [], differences = [], runtime = [];
const associationCases = new Map([
    ["runtime-multiply-rounding", ["a * (b * c)", "a * b * c"]],
    ["runtime-addition-rounding", ["10000000000000000 + (1 + 1)", "10000000000000000 + 1 + 1"]],
    ...["|", "^", "&"].map(op => [`runtime-conversion-order-${op}`, [`a ${op} (b ${op} c())`, `a ${op} b ${op} c()`]]),
]);
async function evaluate(text, setup = '') {
    const sandbox = {};
    new Script(setup + '\n' + text).runInNewContext(sandbox, {timeout: 1000});
    let timer;
    try {
        const result = await Promise.race([sandbox.result, new Promise((_, reject) => { timer = setTimeout(() => reject(Error('Runtime probe did not settle')), 2000); })]);
        return result === undefined ? undefined : JSON.parse(JSON.stringify(result));
    } finally { clearTimeout(timer); }
}
async function evaluateModule(text) {
    const directoryPath = path.join(directory, 'native-modules');
    await mkdir(directoryPath, {recursive:true});
    const name = sha256(text) + '.mjs';
    const file = path.join(directoryPath, name);
    await writeFile(file, `import * as exports from './${name}';\n${text}`);
    return JSON.parse(await execute(process.execPath, ['--input-type=module', '-e',
        `await import(${JSON.stringify(pathToFileURL(file).href)}); console.log(JSON.stringify(await globalThis.result));`]));
}
function reviewedModuleCorrection(item) {
    const entry = reviewedModules.get(item.name);
    if (!entry || ['input','reference','candidate'].some(key => sha256(JSON.stringify(canonical(item[key]))) !== entry[key + 'Hash'])) return undefined;
    return {policy: entry.policy, rationale: moduleCorrections.policies[entry.policy], probes: entry.probes,
        review: {inputHash: entry.inputHash, referenceHash: entry.referenceHash, candidateHash: entry.candidateHash}};
}
function generatorParameterCorrection(item) {
    if (!/async\s+(?:function\s*\*|\*)/.test(item.input.text)) return false;
    const simpleParameters = new Set([...item.input.text.matchAll(/async\s+(?:function\s*\*\s*[\w$]*|\*\s*[\w$]+)\s*\(([^()]*)\)/g)]
        .map(m => m[1].trim().replaceAll(/\s*,\s*/g, ', '))
        .filter(p => /^(?:\.\.\.)?[\w$]+(?:, (?:\.\.\.)?[\w$]+)*$/.test(p)));
    // Earlier transforms can replace patterns or defaults with a simple outer parameter.
    for (const match of item.referenceText.matchAll(/function [\w$]+\(([^()]*)\) \{ return __asyncGenerator\(this, arguments, function\* [\w$]+\(\)/g))
        if (/^(?:\.\.\.)?[\w$]+(?:, (?:\.\.\.)?[\w$]+)*$/.test(match[1])) simpleParameters.add(match[1]);
    if (simpleParameters.size === 0) return false;
    const corrected = item.candidateText.replaceAll(/(function\* [\w$]*\()([^()]*)\)/g,
        (match, prefix, parameters) => simpleParameters.has(parameters) && item.referenceText.includes(prefix + ')') ? prefix + ')' : match);
    return corrected !== item.candidateText && corrected === item.referenceText;
}
function classMemberCorrection(item) {
    const bug = item.input.referenceBug;
    let pairs = [];
    const get = '__classPrivateFieldGet(this, _C_instances, "a", _C_x_get)';
    const set = value => `__classPrivateFieldSet(this, _C_instances, ${value}, "a", _C_x_set)`;
    const privateFunction = '__classPrivateFieldGet(this, _C_instances, "m", _C_f)';
    const op = bug?.endsWith('-and') ? '&&' : bug?.endsWith('-nullish') ? '??' : '||';
    if (bug?.startsWith('decorator-super-')) {
        if (bug === 'decorator-super-parenthesized-call') pairs = [['(Reflect.get(_classSuper, "f", _classThis)).call(_classThis)', '(Reflect.get(_classSuper, "f", _classThis))()']];
        else if (bug === 'decorator-super-parenthesized-tag') pairs = [['(Reflect.get(_classSuper, "f", _classThis)).bind(_classThis)', '(Reflect.get(_classSuper, "f", _classThis))']];
        else if (['decorator-super-logical', 'decorator-super-logical-and', 'decorator-super-logical-nullish'].includes(bug)) {
            const oldNullish = op === '??' && item.input.options.target === 'es2015';
            const value = oldNullish ? '(_a = Reflect.get(_classSuper, "x", _classThis)) !== null && _a !== void 0 ? _a : '
                : `Reflect.get(_classSuper, "x", _classThis) ${op} `;
            const result = oldNullish ? '_b' : '_a';
            pairs = [[`${value}(Reflect.set(_classSuper, "x", ${result} = 1, _classThis), ${result})`,
                `Reflect.set(_classSuper, "x", ${result} = ${value}1, _classThis), ${result}`]];
        }
    } else if (['private-logical', 'private-logical-and', 'private-logical-nullish'].includes(bug)) {
        const value = op === '??' && item.input.options.target === 'es2015'
            ? `(_a = ${get}) !== null && _a !== void 0 ? _a : ` : `${get} ${op} `;
        pairs = [[`${value}(${set('1')})`, set(value + '1')]];
    } else if (['super-logical', 'super-logical-and', 'super-logical-nullish'].includes(bug)) {
        const oldNullish = op === '??' && item.input.options.target === 'es2015';
        const [base, receiver, result] = oldNullish ? ['_c', '_b', '_d'] : ['_b', '_a', '_c'];
        const value = oldNullish ? `(_a = Reflect.get(${base}, "x", ${receiver})) !== null && _a !== void 0 ? _a : `
            : `Reflect.get(${base}, "x", ${receiver}) ${op} `;
        pairs = [[`${value}(Reflect.set(${base}, "x", ${result} = 1, ${receiver}), ${result})`,
            `(Reflect.set(${base}, "x", ${result} = ${value}1, ${receiver}), ${result})`]];
    } else if (bug === 'private-parenthesized-call') pairs = [[`${privateFunction}.call(this)`, `(${privateFunction})()`]];
    else if (bug === 'private-parenthesized-tag') pairs = [[`${privateFunction}.bind(this)`, `(${privateFunction})`]];
    else if (bug === 'super-parenthesized-call') pairs = [['(Reflect.get(_b, "f", _a)).call(_a)', '(Reflect.get(_b, "f", _a))()']];
    else if (bug === 'super-parenthesized-tag') pairs = [['(Reflect.get(_b, "f", _a)).bind(_a)', '(Reflect.get(_b, "f", _a))']];
    else if (item.input.text === 'class C extends B { static { super.x &&= 1; super[key()] ||= 2; super.x ??= 3; } }') pairs = [
        ['Reflect.get(_b, "x", _a) && (Reflect.set(_b, "x", 1, _a))', 'Reflect.set(_b, "x", Reflect.get(_b, "x", _a) && 1, _a)'],
        ['Reflect.get(_b, _d = key(), _a) || (Reflect.set(_b, _d, 2, _a))', 'Reflect.set(_b, _d = key(), Reflect.get(_b, _d, _a) || 2, _a)'],
        ['(_c = Reflect.get(_b, "x", _a)) !== null && _c !== void 0 ? _c : (Reflect.set(_b, "x", 3, _a))',
            'Reflect.set(_b, "x", (_c = Reflect.get(_b, "x", _a)) !== null && _c !== void 0 ? _c : 3, _a)'],
    ];
    let corrected = item.candidateText;
    for (const [candidate, reference] of pairs) {
        if (!corrected.includes(candidate)) return undefined;
        corrected = corrected.replace(candidate, reference);
    }
    if (corrected === item.candidateText || corrected !== item.referenceText) return undefined;
    const receiver = bug?.includes('parenthesized');
    return {policy: receiver ? 'preserve-parenthesized-class-call-receiver' : 'preserve-class-logical-assignment',
        rationale: receiver ? 'Parentheses around a member preserve its reference receiver for calls and template tags.'
            : 'Logical assignment must skip the setter when the existing value decides the result.',
        probe: bug ? item.name : 'class-correction-super-logical-es2015'};
}
async function staticAccessorCorrection(item) {
    if (item.input.options?.target !== 'esnext' || item.input.options.useDefineForClassFields !== false
        || item.reference.error !== staticAccessorPanic || item.candidate.error) return undefined;
    const retainedInstance = item.input.text === '@dec class C { accessor x; @other static accessor y; }';
    if (!retainedInstance && item.input.text !== staticAccessorProbe) return undefined;
    const controlInput = {...item.input, options: {...item.input.options, target: 'es2022'}};
    const control = JSON.parse(await execute(oracle, [], JSON.stringify(controlInput) + '\n'));
    const controlText = Buffer.from(control.textBase64, 'base64').toString();
    const candidateText = retainedInstance ? item.candidateText.replace('        accessor x;\n',
        '        #x_accessor_storage;\n        get x() { return this.#x_accessor_storage; }\n        set x(value) { this.#x_accessor_storage = value; }\n') : item.candidateText;
    if (candidateText !== controlText) return undefined;
    return {policy: 'register-forced-static-accessor-storage',
        rationale: 'A class decorator forces static private storage lowering at ESNext; register the backing storage before lowering that accessor.',
        controlInput, control, probe: 'esdecorator-execute-static-accessor-esnext'};
}
for (let i = 0; i < selected.length; i++) {
    if (selected[i].runtimeControl) {
        const expectedResult = await evaluate(selected[i].runtimeControl);
        let referenceResult;
        if (selected[i].referencePanic) {
            assert.equal(before[i].error, selected[i].referencePanic);
            referenceResult = {error: before[i].error};
        } else if (selected[i].referenceOutcome?.error) {
            try { await evaluate(Buffer.from(before[i].textBase64, "base64").toString(), selected[i].runtimeSetup); assert.fail('Expected the recorded reference error'); }
            catch (error) { assert.equal(error.name, selected[i].referenceOutcome.error); referenceResult = {error: error.name, message: error.message}; }
        } else referenceResult = await evaluate(Buffer.from(before[i].textBase64, "base64").toString(), selected[i].runtimeSetup);
        const candidateResult = await evaluate(Buffer.from(after[i].textBase64, "base64").toString(), selected[i].runtimeSetup);
        assert.deepEqual(candidateResult, expectedResult, selected[i].name);
        if (selected[i].referencePanic || selected[i].referenceOutcome?.error) { /* The exact error was checked above. */ }
        else if (selected[i].referenceOutcome) assert.deepEqual(referenceResult, selected[i].referenceOutcome.value);
        else if (selected[i].referenceBug === 'async-arguments-shorthand') assert.equal(referenceResult, false);
        else if (/^async-execute-(catch-shadow|incoming-parameter|for-parameter)-es2015$/.test(selected[i].name)) assert.equal(referenceResult, undefined);
        else if (selected[i].name === 'async-execute-parameter-initializer-es2015') assert.equal(referenceResult, null);
        else assert.deepEqual(referenceResult, expectedResult, selected[i].name);
        const nativeResult = selected[i].nativeModuleControl ? await evaluateModule(selected[i].nativeModuleControl)
            : selected[i].referenceBug || /^async-execute-(catch-shadow|incoming-parameter|parameter-initializer|for-parameter)-/.test(selected[i].name)
            ? await evaluate(selected[i].nativeControl ?? selected[i].text) : undefined;
        if (nativeResult !== undefined) assert.deepEqual(nativeResult, expectedResult);
        runtime.push({name: selected[i].name, engine: process.version, expectedResult,
            referenceResult: referenceResult === undefined ? {kind: 'undefined'} : referenceResult, candidateResult, nativeResult});
    }
    try { assert.deepEqual(after[i], before[i]); }
    catch {
        const item = {name: selected[i].name, input: selected[i], reference: before[i], candidate: after[i],
            referenceText: Buffer.from(before[i].textBase64, "base64").toString(), candidateText: Buffer.from(after[i].textBase64, "base64").toString()};
        const association = associationCases.get(item.name);
        const classCorrection = classMemberCorrection(item);
        const accessorCorrection = await staticAccessorCorrection(item);
        const moduleCorrection = reviewedModuleCorrection(item);
        if (moduleCorrection) differences.push({...item, ...moduleCorrection, caseHash: sha256(JSON.stringify(item.input))});
        else if (classCorrection) differences.push({...item, ...classCorrection, caseHash: sha256(JSON.stringify(item.input))});
        else if (accessorCorrection) differences.push({...item, ...accessorCorrection, caseHash: sha256(JSON.stringify(item.input))});
        else if (association) {
            const expectedResult = await evaluate(item.input.text), referenceResult = await evaluate(item.referenceText), candidateResult = await evaluate(item.candidateText);
            assert.deepEqual(candidateResult, expectedResult);
            assert.notDeepEqual(referenceResult, expectedResult);
            assert.equal(item.referenceText, item.candidateText.replace(association[0], association[1]));
            differences.push({...item, policy: "preserve-binary-association", caseHash: sha256(JSON.stringify(item.input)),
                rationale: "Preserve the AST's right operand grouping: floating-point arithmetic and observable operand conversion are not associative.",
                independentEvidence: {engine: process.version, expectedResult, referenceResult, candidateResult}});
            runtime.push({name: item.name, expectedResult, referenceResult, candidateResult});
        } else if (item.candidateText !== item.referenceText && item.candidateText === item.referenceText.replaceAll(/\{ arguments_(\d+) \}/g, '{ arguments: arguments_$1 }')) {
            differences.push({...item, policy: 'preserve-async-arguments-property-name', caseHash: sha256(JSON.stringify(item.input)),
                rationale: 'Capturing lexical arguments changes the value expression but must retain the shorthand property key.',
                probe: 'async-arguments-shorthand-key'});
        } else if (item.input.text.startsWith('async function f(x)') && item.candidateText !== item.referenceText
            && item.candidateText === item.referenceText.replace(/function\* \(\) \{ var ([a-z, ]+); /g, (_, names) => {
                const retained = names.split(', ').filter(name => name !== 'x');
                return 'function* () { ' + (retained.length ? `var ${retained.join(', ')}; ` : '');
            }).replace(/(catch \(x\) \{\s*)var x =/g, '$1x =')) {
            differences.push({...item, policy: 'preserve-async-parameter-scope', caseHash: sha256(JSON.stringify(item.input)),
                rationale: 'A duplicate var must retain the parameter binding in the outer function; a generated local hides its incoming value, including when declared inside a catch clause.',
                probe: 'async-execute-incoming-parameter-es2015'});
        } else if (generatorParameterCorrection(item)) {
            differences.push({...item, policy: 'preserve-async-generator-parameter-scope', caseHash: sha256(JSON.stringify(item.input)),
                rationale: 'Forward simple parameters into the generator to retain var redeclarations and the alias between parameters and mapped arguments.',
                probe: 'forawait-correction-parameter-arguments-es2015'});
        } else if (item.input.referenceBug === 'generator-arguments-parameter' && item.candidateText === item.referenceText
            .replace('function f(arguments)', 'function f(arguments_1)').replace('function* f_1()', 'function* f_1(arguments)')) {
            differences.push({...item, policy: 'preserve-async-generator-arguments-parameter', caseHash: sha256(JSON.stringify(item.input)),
                rationale: 'Keep the actual arguments object available for forwarding when a parameter itself is named arguments.', probe: item.name});
        } else if (item.input.referenceBug === 'generator-super-parameter' && item.candidateText === item.referenceText
            .replace('f() { return __asyncGenerator', 'f() { const _super = Object.create(null, {\n        m: { get: () => super.m }\n    }); return __asyncGenerator')
            .replace('x = super.m()', 'x = _super.m.call(this)')) {
            differences.push({...item, policy: 'preserve-async-generator-super-parameter', caseHash: sha256(JSON.stringify(item.input)),
                rationale: 'Capture super access in default parameters before moving them into a generator function, where direct super access is invalid.', probe: item.name});
        } else if (item.input.referenceBug === 'generator-super-index-parameter' && item.candidateText === item.referenceText
            .replace('f() { return __asyncGenerator', 'f() {\n        const _superIndex = name => super[name];\n        return __asyncGenerator')
            .replace('x = super["m"]()', 'x = _superIndex("m").call(this)').replace('}); }\n}', '});\n    }\n}')) {
            differences.push({...item, policy: 'preserve-async-generator-super-parameter', caseHash: sha256(JSON.stringify(item.input)),
                rationale: 'Capture indexed super access in default parameters before moving them into a generator function.', probe: item.name});
        } else failures.push(item);
    }
}
for (const difference of differences) if (difference.probe) {
    const probe = runtime.find(item => item.name === difference.probe);
    if (probe) difference.independentEvidence = {...probe, input: selected.find(item => item.name === difference.probe).text};
    else assert(!process.argv.includes('--record'), `Missing independent probe ${difference.probe}`);
}
for (const difference of differences) if (difference.probes) {
    difference.independentEvidence = difference.probes.map(name => {
        const probe = runtime.find(item => item.name === name);
        assert(probe || !process.argv.includes('--record'), `Missing independent probe ${name}`);
        if (probe) { assert.notEqual(probe.nativeResult, undefined); return {...probe, input: selected.find(item => item.name === name).nativeModuleControl}; }
    }).filter(Boolean);
}
await json(path.join(directory, "failures.json"), failures);
await json(path.join(directory, "intentional-differences.json"), differences);
await writeFile(path.join(directory, "differences.txt"), failures.map(f => `${f.name}\nINPUT ${JSON.stringify(f.input.text)}\nGO ${JSON.stringify(f.referenceText)}\nCS ${JSON.stringify(f.candidateText)}\n${f.input.map ? `MAP GO ${JSON.stringify(f.reference.map)}\nMAP CS ${JSON.stringify(f.candidate.map)}\n` : ""}`).join("\n"));
const summary = {referenceRevision, cases: selected.length, strictMatches: selected.length - failures.length - differences.length,
    permittedDifferences: differences.length, failed: failures.length, runtime,
    sourceHash: sha256(await readFile(path.join(source, "internal/printer/printer.go"))), bridgeHash: sha256(await readFile(bridge)), oracleHash: sha256(await readFile(oracle)),
    candidateHash: sha256(await readFile(dll)), compilerHash: sha256(await readFile(path.join(path.dirname(dll), "TypeScript.Compiler.dll"))), casesHash: sha256(JSON.stringify(canonical(selected))),
    referenceHash: sha256(JSON.stringify(canonical(before))), resultHash: sha256(JSON.stringify(canonical(after)))};
if (transforms) summary.transformSources = Object.fromEntries(await Promise.all(["typeeraser", "importelision", "runtimesyntax", "metadata", "typeserializer", "legacydecorators"].map(async name =>
    [name, sha256(await readFile(path.join(source, `internal/transformers/tstransforms/${name}.go`)))])));
if (transforms) summary.expressionSources = Object.fromEntries(await Promise.all(["logicalassignment", "nullishcoalescing", "optionalchain", "optionalcatch", "exponentiation", "taggedtemplate", "usestrict", "objectrestspread", "async", "forawait", "using", "esdecorator", "classfields", "namedevaluation", "utilities"].map(async name =>
    [name, sha256(await readFile(path.join(source, `internal/transformers/estransforms/${name}.go`)))])));
if (transforms) summary.constantInlinerHash = sha256(await readFile(path.join(source, 'internal/transformers/inliners/constenum.go')));
if (transforms) summary.jsxTransformerHash = sha256(await readFile(path.join(source, 'internal/transformers/jsxtransforms/jsx.go')));
if (transforms) summary.esModuleTransformerHash = sha256(await readFile(path.join(source, 'internal/transformers/moduletransforms/esmodule.go')));
if (transforms) summary.commonJsModuleTransformerHash = sha256(await readFile(path.join(source, 'internal/transformers/moduletransforms/commonjsmodule.go')));
if (transforms) summary.moduleCorrectionsHash = sha256(await readFile(moduleCorrectionsPath));
await json(path.join(directory, "summary.json"), summary);
console.log(JSON.stringify(summary, null, 2));
console.log(failures.slice(0, 25).map(f => f.name).join("\n"));
if (process.argv.includes("--record")) {
    assert.equal(failures.length, 0, "Cannot record an incomplete printer gate");
    await json(path.join(root, `csharp/compatibility/evidence/phase5-${transforms ? "transforms" : "printer"}.json`), {...summary, intentionalDifferences: differences});
}
process.exitCode = failures.length ? 1 : 0;
