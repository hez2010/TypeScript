export function moduleCases() {
    const cases = [];
    const add = (name, text, options = {}, extra = {}) => cases.push({name: `module-${name}`, text, eraseTypes: true,
        elideImports: true, runtimeSyntax: true, lowerExpressions: true, useStrict: true, transformModules: true, map: true,
        options: {target: 'es2015', module: 'esnext', ...options}, file: extra.jsx ? '/source/input.tsx' : undefined, ...extra});
    const sources = [
        'export const x: number = 1;', 'export type X = number;', 'import type {X} from "./mod.ts"; const x = 1;',
        'import "./mod.ts"; export {x as y} from "./mod.mts";', 'export * from "./mod.ts";',
        'export * as space from "./mod.ts";', 'export * as default from "./mod.ts";',
        'import x = require("./mod.cts"); x();', 'export import x = require("./mod.ts");',
        'export = value;', 'export default value;',
        '"use custom"; /* first */ import x = require("./mod.ts"); // one\nimport y = require("./other.ts"); x(y);',
        'const _createRequire = 1, __require = 2; import x = require("./mod.ts"); x();',
        'import {x} from "./mod.ts" with {type: "json"}; export {x} from "./other.ts" with {type: "json"}; x();',
        'import("./mod.ts"); import(path, {with: {type: "json"}}); import(`./mod.ts`);',
        'export async function f() { return await value; }',
        'const __awaiter = 1; export async function f() { return await __awaiter; }',
        'export const {a, ...rest} = data; export async function f() { const {...other} = await rest; return other; }',
        'export class C { #x = 1; get x() { return this.#x; } }',
        'export const x = <div />;',
        '/* first */ export * as ns from "./mod.ts"; // last\n',
    ];
    for (const module of ['es2015', 'es2020', 'es2022', 'esnext', 'node16', 'nodenext', 'preserve'])
        for (const [index, text] of sources.entries()) for (const importHelpers of [false, true])
            add(`es-${module}-${index}-${importHelpers}`, text, {module, importHelpers, rewriteRelativeImportExtensions: true, jsx: 'react-jsx'},
                {jsx: true, transformJsx: true, moduleFormat: 5});
    for (const extension of ['ts', 'tsx', 'mts', 'cts', 'd.ts', 'd.mts', 'd.cts', 'js', 'jsx', 'mjs', 'cjs', 'json', 'TS', 'Tsx', 'ts?query', 'ts#hash'])
        for (const prefix of ['./', '../', '/', '', 'pkg/', '../a/../'])
            for (const jsx of ['preserve', 'react'])
                add(`rewrite-${extension}-${prefix}-${jsx}`, `import '${prefix}mod.${extension}'; export * from '${prefix}other.${extension}'; import('${prefix}last.${extension}');`,
                    {rewriteRelativeImportExtensions: true, jsx});
    for (const text of [
        'import "./mod.ts"; require("./other.ts"); require(path);',
        'require("./mod.ts"); require(path); require("./mod.ts", extra);',
        'export {}; import(path); import(123); import(`./mod.ts`);',
        'import(path);',
        'import "./mod.ts"; export = value;',
        'export type X = number;'
    ]) for (const isolatedModules of [false, true]) for (const rewriteRelativeImportExtensions of [false, true])
        add(`script-${cases.length}`, text, {allowJs: true, isolatedModules, rewriteRelativeImportExtensions, module: 'preserve'}, {file: '/source/input.js'});
    for (const [name, text, expected] of [
        ['require', 'function require(name) { return name; } const module = {}; import x = require("./mod.ts"); export = x; globalThis.result = module.exports;', '"./mod.js"'],
        ['require-twice', 'let calls = []; function require(name) { calls.push(name); return {name}; } const module = {}; import x = require("./mod.cts"); import y = require("./other.mts"); export = [x.name, y.name, calls]; globalThis.result = module.exports;', '["./mod.cjs","./other.mjs",["./mod.cjs","./other.mjs"]]']
    ]) add(`execute-${name}`, text, {module: 'preserve', rewriteRelativeImportExtensions: true}, {runtimeControl: `globalThis.result = ${expected};`});
    const commonJs = [...sources,
        'export let a = 1, b; a += 2; b = a; a++; --b;',
        'let a = 1; export {a, a as b}; a = 2; ++a; let x = a++; void a++; for (a = 1; a < 2; a++) {}',
        'import x, {a, b as c} from "./mod.ts"; x(a, c); export {x, a, c as d};',
        'import x, * as ns from "./mod.ts"; x(ns); export {x, ns};',
        'import {"a-b" as x, default as y} from "./mod.ts"; x(y); export {x as "c-d"};',
        'import * as ns from "./mod.ts"; ns.m(); const x = {ns}; export {ns};',
        'export {a, b as c, default, "a-b" as "c-d"} from "./mod.ts";',
        'import {f} from "./mod.ts"; f(); f`text`; f?.(); new f(); (f)();',
        'export function f() {} export class C {} export {f as alias, C as Other};',
        'function f() {} export {f, f as alias, f as default};',
        'export default function () {}', 'export default class {}',
        'export const f = () => 1, g = function () {}, C = class {}, n = 3;',
        'export const {a, b: c = 1, ...rest} = data; export const [x,,y = 2, ...tail] = items;',
        'let a, b; export {a as x, b}; ({a, b} = data); [a,b] = items;',
        'export let a, b; ({a, b} = data); [a,b] = items;',
        'export {a, b, c}; if (x) { var a = 1; } for (var b = 0; b < 2; b++) {} for (var c of values) c++;',
        'export {a}; try { var a = 1; } catch (e) { a = 2; } finally { a++; }',
        'export {a}; while (x) { var a = 1; break; } do { a++; } while (x); switch (x) { case 0: a = 2; break; default: a++; }',
        'export namespace N { export let x = 1; } export enum E { A } const n = N.x + E.A;',
        'export let a = 1; function f(a) { a++; return {a}; } const g = () => ++a;',
        'import(path); import("./mod.ts"); import(`./mod.ts`); import(123); import();',
        '"custom"; export async function f() { return import("./mod.ts"); }',
        ...[49, 50, 51, 101].map(count => `export const ${Array.from({length: count}, (_, i) => `x${i} = ${i}`).join(', ')};`),
    ];
    for (const module of ['commonjs', 'node16', 'nodenext', 'none']) for (const target of ['es2015', 'es2022'])
        for (const [index, text] of commonJs.entries()) for (const importHelpers of [false, true])
            add(`cjs-${module}-${target}-${index}-${importHelpers}`, text, {module, target, importHelpers, rewriteRelativeImportExtensions: true, jsx: 'react-jsx'},
                {jsx: true, transformJsx: true, moduleFormat: 1});
    for (const module of ['commonjs','esnext']) for (const target of ['es2015','es2022','esnext']) for (const importHelpers of [false,true])
        for (const [index,text] of [
            'function dec(value, context) { return value; } @dec export class C { @dec accessor x = 1; @dec static m() {} } export {C as D};',
            'function dec(value, context) { return value; } export default @dec class { static #x = 1; static get x() { return this.#x; } }',
            'export async function f(items) { for await (const item of items) { using value = item; yieldValue(value); } }',
            'export let value = class { #x = 1; m() { return this.#x; } }; export {value as alias}; value = class {};',
            'export namespace N { export class C { x = 1; } } export namespace N { export const x = 2; } export {N as Alias};',
            'export enum E { A, B=3 } export namespace E { export const x = 4; } export {E as Alias};',
            'import {Component} from "./ui.tsx"; export const View = () => <Component {...data} key={key}/>;',
        ].entries()) add(`integration-${module}-${target}-${index}-${importHelpers}`,text,{module,target,importHelpers,jsx:'react-jsx'},
            {jsx:true,transformJsx:true,useStrict:true});
    for (const module of ['esnext','commonjs','node16','node18','node20','nodenext','preserve'])
        for (const extension of ['ts','mts','cts','js','mjs','cjs']) for (const type of [undefined,'module','commonjs'])
            for (const text of ['export const x = 1;', 'import x = require("mod"); x();', 'import(path);'])
                add(`format-${module}-${extension}-${type}-${cases.length}`, text, {module,allowJs:true},
                    {file:`/source/input.${extension}`,files:type ? {'/source/package.json':JSON.stringify({type})} : {}});
    const runtimeSetup = 'var exports = {}; var module = {exports};';
    const probes = [
        ['direct', 'export let x = 1; x += 2; const previous = x++; globalThis.result = [x, previous, exports.x];', '[4, 3, 4]'],
        ['aliases', 'let x = 1; export {x, x as other}; const first = x++; ++x; x += 3; globalThis.result = [first, x, exports.x, exports.other];', '[1, 6, 6, 6]'],
        ['direct-alias-assignment', 'export let x = 1; export {x as other}; x = 4; globalThis.result = [x, exports.x, exports.other];', '[4, 4, 4]'],
        ['shadow', 'export let x = 1; function f(x) { x++; return x; } const a = f(3); globalThis.result = [a, x, exports.x];', '[4, 1, 1]'],
        ['function-hoisting', 'export function f() { return 42; } globalThis.result = [exports.f === f, exports.f()];', '[true, 42]'],
        ['function-alias', 'function f() { return 42; } export {f as alias, f as default}; globalThis.result = [exports.alias === f, exports.default()];', '[true, 42]'],
        ['classes', 'export class C { #x = 42; get x() { return this.#x; } } globalThis.result = [new exports.C().x, C === exports.C];', '[42, true]'],
        ['anonymous', 'export default class { x = 42; } globalThis.result = new exports.default().x;', '42'],
        ['named-initializers', 'export const f = () => 1, g = function () {}, C = class {}; globalThis.result = [f.name, g.name, C.name, exports.f()];', '["f", "g", "C", 1]'],
        ['destructure', 'export const {x, y: z = 2, ...rest} = {x:1, extra:3}; globalThis.result = [exports.x, exports.z, rest];', '[1,2,{extra:3}]'],
        ['destructure-iterator', 'let log=[]; function* values() { try { log.push(1); yield 1; log.push(2); yield 2; } finally {log.push("close");} } export const [x] = values(); globalThis.result = [x, log];', '[1,[1,"close"]]'],
        ['destructure-alias', 'let x, y; export {x as a, y as b}; [x,y] = [1,2]; globalThis.result = [x,y,exports.a,exports.b];', '[1,2,1,2]'],
        ['object-alias', 'let x, y; export {x as a, y as b}; ({x,y} = {x:1,y:2}); globalThis.result = [x,y,exports.a,exports.b];', '[1,2,1,2]'],
        ['nested-vars', 'export {x,y}; if (true) {var x=1;} for (var y of [2,3]) {} globalThis.result=[exports.x,exports.y];', '[1,3]'],
        ['for-init', 'export {x}; for (var x=0;x<2;x++) {} globalThis.result=[x,exports.x];', '[2,2]'],
        ['enum-space', 'export enum E {A,B=3} export namespace N {export let x=E.B;} globalThis.result=[exports.E.A,exports.E.B,N.x,exports.N.x];', '[0,3,3,3]'],
        ['import-live', 'function require() {return globalThis.shared;} globalThis.shared={x:1}; import {x} from "mod"; const a=x; shared.x=3; globalThis.result=[a,x];', '[1,3]'],
        ['import-default', 'function require() {return 42;} import value from "mod"; globalThis.result=value;', '42'],
        ['import-namespace', 'function require() {return {x:42};} import * as ns from "mod"; globalThis.result=[ns.x, ns.default.x];', '[42,42]'],
        ['import-call', 'function require() {return {f:function(){"use strict";return this===undefined;}};} import {f} from "mod"; globalThis.result=[f(),f`text`,f?.()];', '[true,true,true]'],
        ['reexport-live', 'function require() {return globalThis.shared;} globalThis.shared={x:1}; export {x as y} from "mod"; const first=exports.y; shared.x=3; globalThis.result=[first,exports.y,Object.getOwnPropertyDescriptor(exports,"y").enumerable];', '[1,3,true]'],
        ['reexport-star', 'function require() {return globalThis.shared;} globalThis.shared={x:1,default:4}; export * from "mod"; shared.x=2; globalThis.result=[exports.x,Object.keys(exports)];', '[2,["x"]]'],
        ['reexport-default', 'function require() {return 42;} export {default as x} from "mod"; globalThis.result=exports.x;', '42'],
        ['export-equals', 'const value={x:42}; export = value; globalThis.result=Promise.resolve().then(()=>module.exports.x);', '42'],
        ['async-exports', 'export async function f() { return 42; } globalThis.result=exports.f();', '42'],
        ['async-loop-parameter', 'export async function f(items) { const log=[]; for await (const item of items) log.push(item); return log; } globalThis.result=f([1,2]);', '[1,2]'],
        ['dynamic', 'const log=[]; function require(name) {log.push(name);return {x:42};} const promise=import("./mod.ts"); log.push("sync"); globalThis.result=promise.then(m=>[m.x,log]);', '[42,["sync","./mod.js"]]'],
        ['dynamic-coercion', 'const log=[]; function require(name) {log.push(name);return {x:42};} const path={toString(){log.push("string");return "./mod.ts";}}; const promise=import(path); log.push("sync"); globalThis.result=promise.then(m=>[m.x,log]);', '[42,["string","sync","./mod.ts"]]'],
        ['dynamic-expression', 'const log=[]; function require(name) {log.push(name);return {x:42};} function path(){log.push("path");return "./mod.ts";} const promise=import(path()); log.push("sync"); globalThis.result=promise.then(m=>[m.x,log]);', '[42,["path","sync","./mod.js"]]'],
    ];
    for (const target of ['es2015', 'es2022']) for (const [name, text, expected] of probes)
        add(`execute-cjs-${name}-${target}`, text, {module:'commonjs',target,rewriteRelativeImportExtensions:true},
            {runtimeSetup,runtimeControl:`globalThis.result=${expected};`,
                nativeModuleControl: ['destructure-alias','object-alias'].includes(name) ? text : undefined});
    for (const [name,text,expected,referenceOutcome] of [
        ['direct-postfix', 'export let x=1; export {x as y}; const old=x++; globalThis.result=[old,x,exports.y];', [1,2,2], {error:'ReferenceError'}],
        ['direct-postfix-discard', 'export let x=1; export {x as y}; x++; globalThis.result=[x,exports.y];', [2,2], {error:'ReferenceError'}],
        ['aliased-iterator', 'let x; export {x as y}; let log=[]; function* values(){try {log.push(1);yield 42;log.push(2);yield 99;} finally {log.push("close");}} [x]=values(); globalThis.result=[x,exports.y,log];', [42,42,[1,'close']], {value:[null,null,[]]}],
        ['aliased-default', 'let x; export {x as y}; [x=42]=[]; globalThis.result=[x,exports.y];', [42,42]],
        ['aliased-default-present', 'let x; export {x as y}; [x=42]=[7]; globalThis.result=[x,exports.y];', [7,7], {value:[null,7]}],
        ['aliased-object-default', 'let x; export {x as y}; ({x=42}={}); globalThis.result=[x,exports.y];', [42,42]],
        ['aliased-renamed-default', 'let x; export {x as y}; ({p:x=42}={}); globalThis.result=[x,exports.y];', [42,42]],
        ['aliased-renamed-present', 'let x; export {x as y}; ({p:x=42}={p:7}); globalThis.result=[x,exports.y];', [7,7], {value:[null,7]}],
        ['parenthesized-import-call', 'function require(){return {f:function(){"use strict";return this===undefined;}};} import {f} from "mod"; globalThis.result=[(f)(),((f))(),(f)`text`];', [true,true,true], {value:[false,false,false]}],
        ['nested-function-loop', 'let x=1; export {x}; function f(){for(var x=0;x<2;x++){} return x;} globalThis.result=[f(),x,exports.x];', [2,1,1]],
        ['nested-block-shadow', 'let x=1; export {x}; {let x=2;} globalThis.result=[x,exports.x];', [1,1], {value:[1,2]}],
        ['default-name', 'let x; export {x}; [x=()=>{}]=[]; globalThis.result=[x.name,exports.x===x];', ['x',true]],
        ['object-default-name', 'let x; export {x}; ({x=function(){}}={}); globalThis.result=[x.name,exports.x===x];', ['x',true], {value:['',true]}],
        ['class-default-name', 'let x; export {x}; [x=class {static seen=this.name}]=[]; globalThis.result=[x.name,x.seen,exports.x===x];', ['x','x',true]],
        ['array-value', 'let x; export {x as y}; const rhs=[7]; const result=([x]=rhs); globalThis.result=[x,exports.y,result===rhs];', [7,7,true]],
        ['nested-iterator', 'let x; export {x as y}; const log=[]; function* values(){try{yield 42;}finally{log.push("close");}} ({p:[x]}={p:values()}); globalThis.result=[x,exports.y,log];', [42,42,['close']], {value:[null,null,[]]}],
        ['iterator-throw', 'let x; const fixed=0; export {x as y}; const log=[]; function* values(){try{yield 1;yield 2;}finally{log.push("close");}} try{[x,fixed]=values();}catch(e){log.push(e.name);} globalThis.result=[x,exports.y,log];', [1,1,['close','TypeError']], {value:[null,null,['TypeError']]}],
        ['array-rest', 'let x; export {x as y}; const log=[]; function* values(){try{yield 1;yield 2;yield 3;}finally{log.push("close");}} [...x]=values(); globalThis.result=[x,exports.y,log];', [[1,2,3],[1,2,3],['close']], {error:'TypeError'}],
        ['object-order', 'let x,y; export {x as a,y as b}; const log=[]; const rhs={get first(){log.push(["first",exports.a]);return 1;},get second(){log.push(["second",exports.a]);return 2;}}; const result=({["first"]:x,["second"]:y}=rhs); globalThis.result=[x,y,exports.a,exports.b,result===rhs,log];', [1,2,1,2,true,[['first',null],['second',1]]]],
        ['assign-name', 'export let x; x=function(){}; globalThis.result=[x.name,exports.x===x];', ['x',true], {value:['',true]}],
        ['private-class-name', 'export let x=class { #value=42; get value(){return this.#value;} }; globalThis.result=[x.name,new x().value,exports.x===x];', ['x',42,true], {value:['_a',42,true]}],
        ['direct-default-name', 'export let x; [x=()=>{}]=[]; globalThis.result=[x.name,exports.x===x];', ['x',true], {value:['',true]}],
        ['for-assignment', 'let x; export {x as y}; const log=[]; for(x of [1,2]) log.push(exports.y); globalThis.result=[x,exports.y,log];', [2,2,[1,2]], {value:[2,null,[null,null]]}],
        ['for-pattern', 'let x; export {x as y}; const log=[]; for([x] of [[1],[2]]) log.push(exports.y); globalThis.result=[x,exports.y,log];', [2,2,[1,2]], {value:[2,null,[null,null]]}],
        ['for-in-assignment', 'let x; export {x as y}; const log=[]; for(x in {a:1,b:2}) log.push(exports.y); globalThis.result=[x,exports.y,log];', ['b','b',['a','b']], {value:['b',null,[null,null]]}],
    ]) for (const target of ['es2015','es2022']) {
        const nativeModuleControl = text.replace('from "mod"', 'from ' + JSON.stringify('data:text/javascript,' + encodeURIComponent('export function f(){return this===undefined;}')));
        add(`edge-${name}-${target}`,text,{module:'commonjs',target},
                {runtimeSetup,runtimeControl:`globalThis.result=${JSON.stringify(expected)};`,nativeModuleControl,
                    referenceOutcome: name === 'private-class-name' && target === 'es2022' ? undefined : referenceOutcome});
    }
    return cases;
}
