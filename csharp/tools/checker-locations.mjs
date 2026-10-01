import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import {
    mkdir,
    readFile,
    writeFile,
} from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { isDeepStrictEqual } from "node:util";
import { referenceRevision } from "./common.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const symbolLocations = process.argv.includes("--symbols");
const scopeServices = process.argv.includes("--scopes");
const contextQueries = process.argv.includes("--contexts");
const declarationVisibility = process.argv.includes("--visibility");
const symbolChains = process.argv.includes("--chains");
const conditionalSyntax = process.argv.includes("--conditional-syntax");
const mappedSyntax = process.argv.includes("--mapped-syntax");
const signatureSyntax = process.argv.includes("--signature-syntax");
const anonymousSyntax = process.argv.includes("--anonymous-syntax");
const syntaxOptions = process.argv.includes("--syntax-options");
const syntaxNames = process.argv.includes("--syntax-names");
const generatedNames = process.argv.includes("--generated-names");
const functionOptions = process.argv.includes("--function-options");
const classOptions = process.argv.includes("--class-options");
const signatureDeclarations = process.argv.includes("--signature-declarations");
const emitQueries = process.argv.includes("--emit-queries");
const emitReferences = process.argv.includes("--emit-references");
const emitSerialization = process.argv.includes("--emit-serialization");
const emitLinks = process.argv.includes("--emit-links");
const emitJsx = process.argv.includes("--emit-jsx");
const emitSyntax = process.argv.includes("--emit-syntax");
const emitRecovery = process.argv.includes("--emit-recovery");
const emitServices = process.argv.includes("--emit-services");
const typeSyntax = process.argv.includes("--type-syntax") || conditionalSyntax || mappedSyntax || signatureSyntax || anonymousSyntax || syntaxOptions || syntaxNames || generatedNames || functionOptions || classOptions;
const symbolTypeNodes = process.argv.includes("--symbol-type-nodes");
const computedSymbols = process.argv.includes("--computed-symbols");
const symbolTypeArguments = process.argv.includes("--symbol-type-arguments");
const symbolFormats = process.argv.includes("--symbol-formats") || computedSymbols || symbolTypeArguments;
const symbolDisplay = process.argv.includes("--symbol-display") || symbolFormats || symbolTypeNodes;
const accessibility = process.argv.includes("--accessibility") || symbolDisplay;
const output = path.join(
    root,
    `built/csharp/checker-${
        emitRecovery ? "emit-recovery" : emitSyntax ? "emit-syntax" : emitServices ? "emit-services"
            : emitJsx ? "emit-jsx"
            : emitLinks ? "emit-links" : emitSerialization ? "emit-serialization" : emitReferences ? "emit-references" : emitQueries ? "emit-queries" : signatureDeclarations ? "signature-declarations" : classOptions ? "class-options" : functionOptions ? "function-options" : generatedNames ? "generated-names" : syntaxNames ? "syntax-names" : syntaxOptions ? "syntax-options" : symbolTypeArguments ? "symbol-type-arguments" : anonymousSyntax ? "anonymous-syntax" : signatureSyntax ? "signature-syntax" : mappedSyntax ? "mapped-syntax" : conditionalSyntax ? "conditional-syntax" : typeSyntax ? "type-syntax" : symbolTypeNodes ? "symbol-type-nodes" : computedSymbols ? "computed-symbols" : symbolFormats ? "symbol-formats" : symbolDisplay ? "symbol-display" : accessibility ? "accessibility" : symbolChains ? "symbol-chains" : declarationVisibility ? "declaration-visibility" : contextQueries ? "context-queries" : scopeServices ? "scope-services" : symbolLocations ? "symbol-locations" : "locations"
    }`,
);
const option = name => process.argv[process.argv.indexOf(name) + 1];
const dotnet = process.env.DOTNET_ROOT ? path.join(process.env.DOTNET_ROOT, "dotnet.exe") : "dotnet";
const dll = path.join(process.argv.includes("--managed-directory") ? option("--managed-directory")
    : path.join(root, "csharp/tests/TypeScript.Compatibility/bin/Release/net11.0"), "TypeScript.Compatibility.dll");
const oracle = path.join(root, "built/csharp/checker-program-oracle.exe");
const hash = value => createHash("sha256").update(value).digest("hex");
await mkdir(output, { recursive: true });
const oracleHash = hash(await readFile(oracle));
const candidateHash = hash(Buffer.concat([await readFile(dll), await readFile(path.join(path.dirname(dll), "TypeScript.Compiler.dll"))]));
const library = `interface IArguments {} interface Object {} interface Function {} interface CallableFunction extends Function {}
interface NewableFunction extends Function {} interface String {} interface Number {} interface Boolean {} interface RegExp {}
interface Array<T> { length: number; [n: number]: T; } interface ReadonlyArray<T> { readonly length: number; readonly [n: number]: T; }
interface ThisType<T> {}`;
const fixtures = {
    literals: `let a=1; const b="text"; const c=true; const d=12n; let n=null; let u=undefined; const r=/a/; const t=\`a\${a}\`;`,
    types: `type U = string | number; type I = {x:number}&{y:string}; interface Box<T> { value:T; method(x:T):T; }
declare const x: Box<U>; type Item=typeof x; type Key=keyof Item; type Value=Item["value"];`,
    expressions: `let x: string|number=1; if(typeof x === "number") { x+2; } const o={x, f(a:number){return a+1;}};
o.x; o["x"]; o.f(1); (o.x as number); o satisfies {x:number}; x!; void x; typeof o; delete o.x;`,
    namespaces: `namespace A { export namespace B { export interface C { value:number } export const value=1; } }
import Alias=A.B; let x: A.B.C; Alias.value; type T=typeof A.B.value;`,
    imports: `import value, { named as local, type Shape } from "./dep"; import * as ns from "./dep";
export {local as renamed}; export type {Shape as Exported}; let x:Shape; ns.named; value; local;`,
    typeImports: `import type {Shape as S} from "./dep"; import type Default from "./dep"; export type {Shape as T} from "./dep";
type Imported=import("./dep").Shape; type Values=typeof import("./dep"); let x:S;`,
    typeImportDefault: `import type Default from "./dep"; let x:Default; type Imported=import("./dep").Shape; type Values=typeof import("./dep");`,
    bindings: `declare const source:{a:number,b?:string,nested:{c:boolean}}; const {a: renamed,b="fallback",nested:{c},...rest}=source;
const [first,,third=3,...tail]=[1,2,3]; function f({a}:{a:number},[b]:[string]) { return [a,b]; }`,
    classes: `interface I { id:number; } class Base<T> { value!:T; self():this{return this;} }
class Derived extends Base<string> implements I { id=1; #secret=1; constructor(){super();this.value="";}
method(this:Derived,arg:Base<string>){return arg.value;} get item(){return this.id;} set item(value:number){this.id=value;} }
new Derived().method(new Base<string>());`,
    functions: `function f<T>(x:T):T { return x; } const g=<T>(x:T)=>x; const h=function named(x:number){return x;};
function predicate(x:unknown):x is number{return typeof x === "number";} f(1); g("x"); h(2);`,
    tuples: `type Tuple=[first:number,second?:string,...rest:boolean[]]; type T=readonly [1,"x"]; type F=(x:number)=>string;
type C=new(x:number)=>{x:number}; declare const tuple:Tuple; tuple[0];`,
    mapped: `type Map<T>={readonly [K in keyof T]?:T[K]}; type Pick<T>=T extends {value:infer V}?V:never;
type Names<T extends string>=\`get\${T}\`; let x:Map<{a:number}>;`,
    loops: `let x=0; for(x=1;x<3;x++){ x; } for(const k in {a:1}){k;} for(const v of [1,2]){v;}
while(x){x--;} do{x++;}while(x<2); switch(x){case 1:x;break;default:x;} try{throw x;}catch(e){e;}`,
    withStatement: `declare const obj:any; with(obj){ missing; let x=missing; x(); }`,
    meta: `interface ImportMeta { url:string; } function f(){new.target;} import.meta.url;`,
    enum: `enum E { A=1,B=A+1 } const e=E.A; type EnumType=E; namespace E { export const value=3; } E.value;`,
    importsCommonJs: `import dep=require("./dep"); export=dep;`,
    contextual: `const f:(x:{value:number})=>number=x=>x.value; const a:{method(x:number):number}={method(x){return x;}};
const b=[1,"x"] as const; const c=(1 as const); const named:{x?:number}={x:1};`,
    jsx: `declare namespace JSX { interface Element {} interface IntrinsicElements { div:{title?:string}; } }
function Component(props:{value:number}) {return <div title="x"/>;} const node=<Component value={1}/>;`,
    jsdoc: `/** @template T @param {T} value @returns {T} */ function identity(value){ return value; }
/** @type {number} */ let n=1; const s=identity("text");`,
};
if (symbolLocations) {
    Object.assign(fixtures, {
        jsdocReferences: `class C { field=1; static value=2; }
/** Links {@link value}, {@link C.field} and {@link C.value}.
 * @param {number} value A value.
 * @returns {number} The result.
 */
function documented(value){return value;}
/** @template T
 * @param {T} item
 * @returns {T}
 */
function generic(item){return item;}`,
        indexSymbols: `interface Dictionary { [key:string]:number; } declare const dictionary:Dictionary;
dictionary.value; dictionary.value; dictionary["key"]; type Value=Dictionary["key"];
declare const both: { [key:\`a\${string}\`]:number } & { [key:\`\${string}z\`]:1 }; both.az; both.az;`,
        missingSymbols: `let a:Missing; let b:Missing.Member; let c:Missing.Member; missingValue; unknownObject.property;
import {absent} from "./missing"; type Imported=import("./missing").Type;`,
        privateSymbols: `class C { #value=1; #method(){return this.#value;} has(other:object){return #value in other;} }
declare const c:C; c.#value; class D { #value=2; read(){return this.#value;} }`,
        declarationNames: `class C { ["named"](){return 1;} [42]=true; constructor(){} } const o={ ["text"]:1, [42]:true };
namespace N { export class Item {} } import Alias=N.Item; export=Alias;`,
        moduleQueries: `import {named} from "./dep" with {type:"json"}; export {named} from "./dep";
const module=import("./dep", {with:{type:"json"}}); type T=import("./dep", {with:{type:"json"}}).Shape;`,
    });
}
if (scopeServices) {
    Object.assign(fixtures, {
        scopeShadow: `const outer=1; function f<T>(argument:T){let outer="inner"; {const block=true; (()=>argument)();} return argument;}
namespace N { export const outer=true; export interface Item { value:number; } function inner(){let local=outer;return local;} }`,
        scopeStatic: `class C<T> { field!:T; method<U>(this:C<T>,value:U){const local=value;return ()=>arguments;}
static method<U>(value:U){return value;} } const cls=class Named<T> {method(value:T){return Named;}};
const fn=function self(value:number){return ()=>self(value);};`,
        scopeConditional: `type Choose<T>=T extends infer U?U:T; type M<T>={[K in keyof T]:T[K]};
function nested<T>(value:T){function inner<U>(other:U){return {value,other};} return inner;}`,
        scopeExports: `import {named as local} from "./dep"; export {local as forwarded}; export * from "./dep";
export * as everything from "./dep"; export default class Default { method(){return local;} }
namespace N {export const x=1; export interface I {value:number;} }`,
        symbolTypes: `declare let value:string|number; if(typeof value==="string"){value;} value=1; value;
class C { get property():number{return 1;} set property(value:string|number){} }
declare const c:C; c.property; c.property="text"; c["property"]=1;
declare const optional:{value?:number}; optional.value; optional.value=1; optional?.value;`,
        parameterProperties: `export class C { constructor(public value:string,readonly count=1,protected item?:number){const copy={value};this.value=value;} }
declare const c:C; c.value; c.count;`,
    });
}
if (contextQueries) {
    Object.assign(fixtures, {
        contextGeneric: `declare function choose<T>(value:T, callback:(x:T)=>T):T; choose("a",x=>x);
declare function select<T,K extends keyof T>(object:T,key:K):T[K]; select({left:1,right:"x"},"left");`,
        contextBlocked: `declare function color<T extends "red"|"blue">(value:T):T; color("red"); color("missing");
declare function pair<T>(first:T,second:T):T[]; pair("red","blue");`,
        contextOverloads: `declare function f(value:string):string; declare function f(value:number,extra?:number):number;
f("text"); f(true); f(1,2); f(1,2,3);`,
        contextCallbacks: `declare function map<T,U>(items:T[],callback:(value:T)=>U):U[];
const values:number[]=map([1,2],value=>value+1); const functions:Array<(value:number)=>number>=[value=>value+1];`,
        contextTuples: `const tuple:[number,...string[],boolean]=[1,...["x"],true]; const array:number[]=[1,2];
declare function tupleCall(...values:[number,string?,...boolean[]]):void; tupleCall(1,"x",true);`,
        contextTemplates: `interface TemplateStringsArray extends ReadonlyArray<string> { readonly raw:readonly string[]; }
declare function tag<T>(strings:TemplateStringsArray,value:T):T; tag\`text \${1}\`;`,
        jsxContexts: `declare namespace JSX { interface Element {} interface IntrinsicElements {div:{title?:string};} }
function Component(props:{value:"yes"|"no",onChange:(value:number)=>void}){return <div/>;}
const element=<Component value="yes" onChange={value=>{value;}}/>;`,
        contextImports: `interface ImportCallOptions {with?:{type:string};} const module=import("./dep",{with:{type:"json"}});`,
    });
}
if (declarationVisibility || accessibility) {
    Object.assign(fixtures, {
        visibilityExports: `const hidden=1; export const visible=hidden; class Hidden {} export class Public { private secret=1; protected member=1;
public field=1; #private=1; constructor(value:number){} get property(){return this.field;} set property(value:number){this.field=value;} }
export {hidden}; export default Hidden;`,
        visibilityAliases: `namespace N { export class C {} } import A=N; import B=A; export {B};
import Default,{named as value,Shape} from "./dep"; import * as All from "./dep"; export {value,All};`,
        visibilityAmbient: `declare namespace N { interface I {value:number;} class C {private field:number; public method(x:number):void;} }
declare module "external" { class C {} import Alias=N; module "nested" {interface I {}} }`,
        visibilityAugmentation: `export {}; declare module "./dep" { interface Shape { extra:string; } }
declare global { interface Global {value:number;} }`,
        visibilityDts: `export {}; interface Hidden {value:number;} export interface Public {value:Hidden; method(x:number):string;}
declare const hidden:Hidden; export {hidden};`,
        visibilityBindings: `const {a,b:{c}}={a:1,b:{c:2}}; const [first,,...rest]=[1,2,3]; const {}={}; const []=[];
export {a,c,first,rest}; function f({local}:{local:number}){return local;}`,
        jsdocVisibilityCommonJs: `const local=1; function C(){} exports.value=local; module.exports.C=C;
const {named}=require("./dep"); exports.named=named;`,
        jsdocVisibilityTypes: `exports.value=1;
/** @typedef {{value:number}} Shape */
/** @callback Callback
 * @param {Shape} value
 * @returns {number}
 */
function f(value){return value;}
class C { /** @private */ secret=1; /** @protected */ field=1; public=1; }`,
    });
}
if (symbolChains || accessibility) {
    Object.assign(fixtures, {
        chainAliases: `import * as Long from "./dep"; import {Deep} from "./dep"; import Root=require("./dep");
import Local=Deep; import Short=Deep.Member; let value:Deep.Member; function f(Short:number){let value:Deep.Member;}`,
        chainShadow: `namespace N {export class Member{} export const value=1;} import A=N; import B=N;
function f(N:number,A:string){let value:B.Member;} function g(globalValue:string){globalThis.globalValue;}`,
        chainClassNames: `const outer=class Self<T>{method(value:Self<T>){const inner=class Inner<U>{value!:Self<T>;};return inner;}};`,
        chainReexports: `import * as All from "./dep"; export * as Forward from "./dep"; export {named as Alias} from "./dep";
namespace N {export import Internal=All.Deep;} import Internal=N.Internal; let member:Internal.Member;`,
        chainCycles: `import A=B; import B=A; namespace N {export import C=D; export import D=C;} export {A,B};`,
        chainMerged: `interface Shared {value:number;} namespace Shared {export const value=1;} const c:Shared={value:1};
namespace N {export interface T{a:number;}} namespace N {export interface T{b:string;}} import Alias=N; let merged:Alias.T;`,
        chainTypeOnly: `import type {Shape} from "./dep"; import type * as Types from "./dep"; import type Alias=Types.Deep;
function f<Shape>(value:Shape){let other:Types.Shape;} let member:Alias.Member;`,
        chainDeepReexports: `import * as Surface from "./barrel2"; import Root=require("./barrel2");
let value=Surface.next.nested.named; function shadow(next:number,nested:number){return Surface.next.nested.named;}`,
    });
}
if (accessibility) {
    Object.assign(fixtures, {
        accessibilityObjects: `const value={nested:{field:1}}; declare const typed:{property:string}; export type Result=typeof value.nested;
function local(){const hidden={field:1};return hidden;}`,
        accessibilityInstances: `interface Constants {readonly key:unique symbol; method():number;} declare const SymbolLike:Constants;
export type Key=typeof SymbolLike.key; namespace N {export interface I {field:number;} } declare const instance:N.I;`,
        accessibilityExportEquals: `import Root=require("./exported"); export const value:Root.Item=new Root.Item();`,
    });
}
if (symbolDisplay) {
    Object.assign(fixtures, {
        displayNames: `namespace Names { export class C { "with space"=1; 'é'=2; ["computed"]=3; [-1]=4; 0x10=5; #private=6; }
export enum E { "some-key", "中", Numeric=0x10 } } const anonymous=class { "line\\n"=1; }; const fn=()=>1;
export default function Named() { return Names.C; }`,
        displayComputed: `declare const key:unique symbol; export class Outer { [key]=1; ["a-b"]=2; [1e3]=3; }
export namespace Nested { export const literal={ "nul\\0":1, "slash\\\\name":2 }; }
const other=function(){}; const missing=class {}; export {other,missing};`,
        displayAssigned: `const values={"with space":function(){}, method:class {}, ["computed"]:()=>1};
let assigned:any; assigned=function(){}; assigned.member=class {}; assigned["element"]=()=>1; export {values,assigned};`,
        jsdocDisplayNames: `function C(){this["field-name"]=1;this.named=2;} C.prototype["method-name"]=function(){};
exports["exported-name"]=C; Object.defineProperty(exports,"defined-name",{value:C}); const values={"a-b":function(){}};`,
    });
}
const inputs = [];
if (emitRecovery) {
    Object.assign(fixtures, {
        emitRecoveryIndexes: `declare const key:string;declare const num:number;declare const sym:symbol;class C{[key]=1;readonly [sym]='x';static [num]=true}class Scope{}type Serialize=InstanceType<typeof C>;`,
        emitRecoveryIndexLengths: `declare const key:string;declare const num:number;declare const sym:symbol;class C{[key]={firstLongMember:1,secondLongMember:2,thirdLongMember:3,fourthLongMember:4};[sym]={firstLongMember:1,secondLongMember:2,thirdLongMember:3,fourthLongMember:4};static [num]={firstLongMember:1,secondLongMember:2,thirdLongMember:3,fourthLongMember:4}}class Scope{}`,
        emitRecoveryIndexScopes: `function create(key:string){class C{[key]=1;static [key]='x'}return C}const C=create('a');type Serialize=InstanceType<typeof C>;class Scope{}`,
        emitRecoveryIndexMix: `declare const key:string;interface I{[key]:number;[s:string]:number;named:number}class Base{[s:string]:any}class C extends Base{[key]=true}class Scope{}`,
        emitRecoveryReverse: `declare function unwrap<T>(value:{[K in keyof T]:{value:T[K]}}):T;const result=unwrap({one:{value:1},two:{value:'x'}});type Serialize=typeof result;class Scope{}`,
        emitRecoveryReverseNested: `type Box<T>={value:T};type Deep<T>={[K in keyof T]:Deep<T[K]>|Box<T[K]>};declare function unwrap<T>(value:Deep<T>):T;const result=unwrap({a:{b:{value:1}},c:{value:'x'}});type Serialize=typeof result;class Scope{}`,
        emitRecoveryReverseDepth: `type Box<T>={value:T};type Deep<T>={[K in keyof T]:Deep<T[K]>|Box<T[K]>};declare function unwrap<T>(value:Deep<T>):T;const result=unwrap({a:{b:{c:{d:{e:{value:1}}}}}});type Serialize=typeof result;class Scope{}`,
        emitRecoveryReverseArrays: `declare function unwrap<T>(value:{[K in keyof T]:{value:T[K]}}):T;declare const source:{[x:string]:{value:{next:{value:number}}}};const result=unwrap(source);type Serialize=typeof result;class Scope{}`,
        emitRecoveryAnnotations: `import {Shape} from './dep';type Serialize={value:Shape|string;callback:<T>(value:T)=>T;tuple:readonly ['é',number]};type MissingUse=Missing;class Scope<T>{field!:this;}`,
        emitRecoveryImports: `type Serialize=import('./dep').Shape;type Module=typeof import('./dep');class Scope{}`,
        jsdocEmitRecoveryPrimitives: `/** @type {?String} */let text;/** @type {!Number} */let number;/** @type {Boolean=} */let optional;/** @type {*} */let any;/** @type {Array} */let array;class Scope{}`,
        jsdocEmitRecoveryFunctions: `/** @param {string} x @param {...number} values @returns {?number} */function f(x,...values){return null}/** @type {function(string):boolean} */let callback;class Scope{}`,
        jsdocEmitRecoveryAliases: `/** @template T @typedef {{value:T}} Box *//** @type {Box} */let box;/** @type {Box<string>} */let typed;/** @type {import('./dep').Shape} */let imported;class Scope{}`,
        jsdocEmitRecoveryObject: `/** @typedef {object} Data @property {string} name @property {number} [count] *//** @type {Data} */let data;/** @type {{nested:?string, fn:function(number):string}} */let object;class Scope{}`,
    });
}
if (emitSyntax) {
    Object.assign(fixtures, {
        emitSyntaxReturnLiterals: "function number(){return 0x10 as const}function text(){return 'é' as const}function negative(){return -0xF as const}function big(){return 12n as const}function ordinary(){return 0x10}class Scope{}",
        emitSyntaxReturnTuples: "function tuple(){return [0x10,'é',{nested:'x'}] as const}const arrow=()=>[0x20,'y'] as const;function spread(){return [...arrow()] as const}class Scope{}",
        emitSyntaxReturnObjects: "function object(){return {value:0x10 as const,text:'é' as const,nested:{item:true}}}function constant(){return {value:0x10,text:'é',nested:{item:true}} as const}class Scope{}",
        emitSyntaxReturnAssertions: "type Name='é'|'x';function asserted(){return 'é' as Name}function angled(){return <Name>'x'}function parentheses(){return ('é' as Name)}function checked(){return ({value:'x'} satisfies {value:string})}class Scope{}",
        emitSyntaxReturnBranches: "function nested(x:boolean){if(x)return 0x10 as const;return 0x20 as const}function many(x:boolean){return x?0x10 as const:0x20 as const}function empty(){return;}function bare(){return;return 0x10 as const}class Scope{}",
        emitSyntaxReturnFunctions: "function outer(){return function<T>(value:T){return [value,0x10] as const}}function arrow(){return <T>(value:T)=>({value})}function overloaded(value:string):string;function overloaded(value:number):number;function overloaded(value:any){return value}class Scope{}",
        emitSyntaxReturnAccessors: "class Values{get first(){return 0x10 as const}get second(){return 'é' as const}set second(value:'é'){}get object(){return {value:0x10} as const}}class Scope{}",
        emitSyntaxReturnContext: "const precise:()=>number=()=>0x10 as const;const wide:()=>readonly [number,string]=()=>[0x20,'é'] as const;function identity<T>(value:T){return value}const generic=identity<string>;class Scope{}",
        emitSyntaxReturnPredicates: "function guard(value:unknown){return typeof value==='string'}function annotated(value:unknown):value is 'é'{return value==='é'}function assertion(value:unknown):asserts value is string{if(typeof value!=='string')throw 0}class Scope{}",
        emitSyntaxReturnMoreCallable: "function factory(){return (...args:[first:number,second:string])=>[args[0],0x10] as const}function defaults(){return (first=0x10,second:string)=>({first,second})}function constrained(){return <T extends 'é'>(value:T)=>value}class Scope{}",
        emitSyntaxReturnMoreMembers: "function create(){return {method(){return 0x10 as const},get value(){return 'é' as const},set value(value:'é'){}}}function constant(){return {method(){return [0x10,'é'] as const},value:0x10} as const}class Scope{}",
        emitSyntaxReturnMoreAccessors: "class Values{get value(){return 0x10 as const}set value(v:16){}set text(v:'é'){}get text(){return 'é' as const}}class Scope{}",
        emitSyntaxReturnMoreFallback: "declare const value:unique symbol;const x=0x10 as const;function computed(){return {[value]:x}}function shorthand(){return {x}}function spread(){return {...{x}}}function contextual(){const f:()=>number=()=>0x10 as const;return f}class Scope{}",
        emitSyntaxReturnMoreTuples: "function holes(){return [0x10,,'é'] as const}function nested(){return [[0x10],{value:'é'}] as const}function numeric(){return [+0x10,-0xF,0b11,1e3] as const}class Scope{}",
        emitSyntaxReturnMoreAnnotations: "type A='é'|'x';function typed():A{return 'é'}function tuple():readonly [0x10,'é']{return [16,'é']}function arrow(){return (()=>'é' as const)}class Scope{}",
        emitSyntaxReturnDeepMethods: "function methods(){return {'quoted method'(...args:[first:number,second:string]){return 'é' as const},ordinary(x:number){return x}}}function constant(){return {method(...args:[value:number]){return 0x10 as const}} as const}class Scope{}",
        emitSyntaxReturnDeepAccessors: "function accessors(){return {get value():'é'{return 'é'},set value(value:'é'){},get number(){return 0x10 as const}}}function readonly(){return {get 'quoted'(){return 'é' as const}} as const}class Scope{}",
        emitSyntaxReturnDeepNames: "function names(){return {['quoted']:0x10 as const,['é']:'é' as const,[0x10]:true}}const x='é' as const;function reuse(){return {'quoted value':x,nested:{value:x}}}class Scope{}",
        emitSyntaxReturnDeepAssertions: "type A='é'|'x';function members(){return {literal:'é' as 'é',alias:'é' as A,tuple:[16,'é'] as readonly [0x10,'é']}}const object={literal:'é' as 'é'};class Scope{}",
        emitSyntaxReturnUnary: "function logical(){return !false as const}function bitwise(){return ~0 as const}function literals(){return [+0x10,-0x10,-1n,true,false] as const}class Scope{}",
        jsdocEmitSyntaxReturnDeep: "/** @returns {function(string):'é'} */function factory(){return /** @type {function(string):'é'} */(function(value){return 'é'})}function nested(){return {value:/** @type {'é'} */('é')}}class Scope{}",
        jsdocEmitSyntaxReturnMore: "/** @returns {?string} */function nullable(){return null}/** @returns {function(string):'é'} */function factory(){return x=>'é'}class Values{/** @returns {'é'} */get value(){return 'é'}}class Scope{}",
        jsdocEmitSyntaxReturn: "/** @returns {'é'|'x'} */function annotated(){return 'é'}function direct(){return /** @type {const} */(0x10)}function object(){return {value:0x10,text:'é'}}class Scope{}",
        emitSyntaxLiterals: `const text='é';const number=-2;const big=-12n;const yes=true;const no=false;const zero=-0;let ordinary=1;const infinity=1e999;const annotated:'x'='x';class Scope{readonly value='é';mutable='x'}`,
        emitSyntaxParameters: `function f(x:number=1,y:string,z?:boolean,...rest:number[]):[number,string]{return [x,y]}class Scope{constructor(public optional?:string,private value:number=1,readonly required:string){}}`,
        emitSyntaxFunctions: `function identity<T extends string,U=T>(value:T,other:U):T{return value}function guard(value:unknown):value is string{return typeof value==='string'}function inferred(value:unknown){return typeof value==='number'}function anyReturn():any{}function Scope<T>(){}const arrow=<T>(value:T)=>value;`,
        emitSyntaxObjects: `const object={value:1,text:'x',method(x:number){return x}};const tuple=[1,'x'] as const;const array=[1,2];const fn=(value:string)=>value;class Scope{get value():number{return 1}set value(v:string|number){}}`,
        emitSyntaxSymbols: `declare const unique:unique symbol;const same=unique;namespace N{export enum E{A=1,B='é'}}const numeric=N.E.A;const text=N.E.B;class Scope{readonly unique:unique symbol;optional?:typeof unique}`,
        emitSyntaxAnnotations: `import type {Shape} from './dep';declare const shape:Shape;declare const union:Shape|string;declare const missing:Missing;function f<T>(x:T):{value:T}{return {value:x}}class Scope<T>{}`,
        emitSyntaxScope: `declare var value:unique symbol;function f(value:number):typeof globalThis.value{throw 1}function g<T>(value:T):<U>(other:U)=>[T,U]{throw 1}function Scope<T>(){}`,
        emitSyntaxTupleForms: "const tuple=[0x10,-0xF,true,12n,`é`,['nested']] as const;const scalar='é' as const;const spread=[...tuple] as const;class Scope{}",
        emitSyntaxRestScope: `declare var value:unique symbol;function f(...args:[value:number]):typeof globalThis.value{throw 1}function g<T>({value}:{value:T}):typeof value{return value}function Scope<T>(){}`,
        emitSyntaxContextual: "const fn:(value:readonly ['x'])=>void=(value=['x'])=>{};const checked=([`é`] as const) satisfies readonly string[];class Scope{}",
        jsdocEmitSyntax: `/** @type {string} */ let text='x';/** @param {number} x @returns {number} */ function f(x){return x}class Scope{/** @type {number} */ value=1;get text(){return 'x'}}`,
    });
}
if (emitServices) {
    Object.assign(fixtures, {
        emitServicesEnums: `enum E{A=1,B,C='é',D='x'+'y'}const enum CE{Zero=-0,Value=2,Text='s'}E.A;E['C'];CE.Value;CE['Text'];`,
        emitServicesModifiers: `export const {value}= {value:1};export default abstract class C{private field=1;protected readonly other='';static value=1;abstract method():void;constructor(public readonly parameter:number){}}export async function f(){}`,
        emitServicesAmbient: `export {};declare namespace N{const value:number;interface I{readonly field:string}class C{private x:number;readonly y:string}}declare global{interface Global{value:number}}`,
        emitServicesInheritance: `class Base{value=1;readonly read=2;optional?:string;method(){}get accessor(){return 1}}class Derived extends Base{value=1;read=2;optional?:string;method=()=>{};accessor=1}interface I{only:number}class Implements implements I{only=1}`,
        jsdocEmitServices: `class Base{value=1;method(){}}class Derived extends Base{constructor(){super();this.value=1;this.method=()=>{};this.extra='x'}}function F(){this.field=1}F.prototype.method=function(){};`,
    });
}
if (emitJsx) {
    Object.assign(fixtures, {
        jsxFactoryDefault: `const value=<><div/><Custom/></>;`,
        jsxFactoryPragma: `/** @jsx Local.h */\n/** @jsxFrag Local.Fragment */\nconst value=<><div/></>;`,
        jsxFactoryConfigured: `const value=<><div/></>;`,
        jsxFactoryNullFragment: `/** @jsxFrag null */\nconst value=<><div/></>;`,
        jsxFactoryInvalid: `/** @jsx not.a[0] */\n/** @jsxFrag invalid[0] */\nconst value=<><div/></>;`,
        jsxFactoryNamespace: `const value=<div/>;`,
        jsxFactoryEmptyOptions: `const value=<><div/></>;`,
    });
}
if (emitLinks) {
    Object.assign(fixtures, {
        emitLinksValues: `import Default,{named as value,type Shape} from './dep';import * as ns from './dep';const object={value};const C=Default;ns.named;type T=Shape;`,
        emitLinksTypes: `import Default,{named,type Shape} from './dep';import * as ns from './dep';let x:Default;type T=Shape;type U=typeof named;type V=ns.Shape;`,
        emitLinksExports: `import {named as value} from './dep';import * as ns from './dep';import A=ns;export import B=A;export {value};export type {Shape} from './dep';`,
        emitLinksConstEnums: `import * as ns from './dep';import {CE,Only} from './dep';const a=ns.CE.A;const b=CE.A;type T=typeof Only;enum E{A=ns.Regular.A}export=ns.Only;`,
        emitLinksVerbatim: `import {named as value} from './dep';import * as ns from './dep';value;ns.named;export {value};`,
        emitLinksInvalid: `import {named as value} from './dep';const a={missing=1};enum E{[missing]=1}interface I extends missing(){}class C extends Object, missing{}for(var of missing){}value;`,
        emitLinksDecorators: `import Default,{named as dec} from './dep';@dec class C{@dec field!:Default;@dec method(value:Default):Default{return value}}`,
        emitLinksMissing: `import {missing} from './absent';missing();export {missing};`,
        jsxEmitLinks: `import * as React from './dep';import Default from './dep';const value=<><Default/><div/></>;`,
        jsxEmitLinksConstFactory: `import {CE as React} from './dep';const value=<div/>;`,
        emitLinksIllegalExports: `namespace N{export import External=require('./dep')}export import Bad=require(missing);`,
        jsdocEmitLinks: `const dep=require('./dep');const value=dep.named;exports.value=value;const {named}=require('./dep');named;`,
    });
}
if (emitSerialization) {
    Object.assign(fixtures, {
        emitSerializationPrimitives: `type Num=number;type Str=string;type Bool=boolean;type Big=bigint;type Sym=symbol;type Void=void;type Null=null;type Never=never;type Any=any;type Unknown=unknown;let a:Num,b:Str,c:Bool,d:Big,e:Sym,f:Void,g:Null,h:Never,i:Any,j:Unknown;class Scope{}`,
        emitSerializationObjects: `interface I{x:number}class C{x=1}type Arr=number[];type Tuple=[number,string];type Fn=(x:number)=>string;type Union=number|string;type Both={x:number}&{y:string};let a:I,b:C,c:Arr,d:Tuple,e:Fn,f:Union,g:Both;class Scope<C>{}function location<C>(){}`,
        emitSerializationImports: `import Default from './dep';import type {Shape} from './dep';import type TypeDefault from './dep';import * as ns from './dep';let a:Default,b:TypeDefault,c:Shape,d:ns.Shape;class Scope<Default>{}function location(Default:number){}`,
        emitSerializationMissing: `import type {Missing} from './missing';let a:Missing,b:Unresolved;namespace N{export interface Shape{x:number}}let c:N.Shape;class Scope{}`,
        emitSerializationPromise: `interface Promise<T>{then():T}declare var Promise:{new<T>():Promise<T>};class C{}type P=Promise<number>;let a:Promise<number>,b:P,c:C;class Scope{}`,
        emitSerializationGenerics: `interface Fn{(x:number):string}type Num<T extends number>=T;type Box<T>={value:T};function f<T extends new(...args:any[])=>any,U extends Fn>(a:T,b:U,c:Num<1>,d:Box<T>){return a}class Scope{}`,
    });
}
if (emitReferences) {
    Object.assign(fixtures, {
        emitReferencesImports: `import Default,{named as value,type Shape} from './dep';import * as ns from './dep';const instance=new Default();value;ns.named;instance.value;type T=Shape;export {value};`,
        emitReferencesExports: `export const value=1;export function f(){return value}export class C{field=value;method(){return value}}namespace N{export let value=1;export function f(){return value}}enum E{A=1,B=A+1}N.f();E.A;`,
        emitReferencesElements: `const key='field';const numeric=1;declare const object:{field:number;1:string;other:boolean};object[key];object[numeric];object['other'];object[0x1];let dynamic='field';object[dynamic];`,
        emitReferencesBindings: `const source={value:1,other:'x'};const {value:renamed,other}=source;renamed;other;function f({x}:{x:number},...rest:[number,string]){x;rest;return x}const arrow=(x:number)=>x;f({x:1},1,'x');`,
        emitReferencesTypeOnly: `import type Default from './dep';import type * as ns from './dep';import type {Shape} from './dep';type T=Default;type U=ns.Shape;type V=Shape;export type {Shape};`,
        jsdocEmitReferences: `function f(x){return x}f.value=1;f.other='x';const arrow=x=>x;arrow.member=true;exports.fn=f;module.exports=f;f.value;arrow.member;`,
    });
}
if (emitQueries) {
    Object.assign(fixtures, {
        emitQueriesOverloads: `function f(x:string):string;function f(x:number):number;function f(x:any){return x}function single(x:string):void;function single(x:any){}function ordinary(x:number){return x}class C{method(x:string):string;method(x:any){return x}get value(){return 1}set value(v:number){}constructor(x:string);constructor(x:any){}}`,
        emitQueriesParameters: `function f(x:number=1,y:string,z?:boolean,...rest:number[]){}class C{constructor(public value:number=1,y:string,readonly optional?:string){}readonly field=1;optional?:number}interface I{readonly field:'x';optional?:string}function location<T>(){}`,
        emitQueriesLiterals: `const a=1,b='é',c=true,d=12n,e=-1;let mutable=1;const widened:number=1;const literal:1=1;const object={x:1};class C{readonly value=1;readonly named:number=2;static readonly text='x';field=1}enum E{A=1,B}const enum CE{A=1}`,
        emitQueriesAliases: `import Default,{named,type Shape} from './dep';import * as ns from './dep';import Alias=ns;export {named,Shape};export {named as renamed} from './dep';export * as values from './dep';export default named;named;ns.named;type T=Shape;`,
        emitQueriesConstAliases: `namespace N{export const enum E{A=1}export namespace Only{export const enum E{A=1}}export const value=1}import E=N.E;import Only=N.Only;export import Value=N.value;E.A;type T=typeof Only;`,
        emitQueriesVerbatim: `import {named,type Shape} from './dep';import * as ns from './dep';export {named,Shape};export * as values from './dep';type T=Shape;`,
        emitQueriesSymbols: `declare const Symbol:{iterator:unique symbol};Symbol.iterator;globalThis.Symbol.iterator;function local(Symbol:{iterator:number}){Symbol.iterator;globalThis.Symbol.iterator}function shadow(globalThis:any){globalThis.Symbol.iterator}const object={Symbol};object.Symbol.iterator;`,
        emitQueriesComputed: `declare const key:unique symbol;class C{[key]=1;['plain']=2;[1]=3}interface I{[key]:string}type Map<T>={[K in keyof T]:T[K]};`,
        emitQueriesAugmentation: `import './augment';export interface Value{x:number}import {named} from './dep';export {named};`,
        emitQueriesMissing: `import {Missing} from './missing';import MissingEq=require('./missing');export {Missing};type T=import('./missing').Value;declare module './missing'{interface Value{x:number}}`,
        jsdocEmitQueries: `const value=1;function f(x){return x}exports.value=value;exports.fn=f;module.exports=f;function nested(){exports.inner=value}const local={exports:{}};local.exports=value;`,
    });
}
if (signatureDeclarations) {
    Object.assign(fixtures, {
        signatureDeclarationsFunctions: `function serializePlain(x:number,y?:string):any{return x}function serializeGeneric<T extends string>(x:T):T{return x}function location<T>(){}`,
        signatureDeclarationsConstructors: `class SerializeClass<T>{constructor(public value:T,protected optional?:string,private readonly count=1){}get item():T{return this.value}set item(value:T){this.value=value}}class Scope<T>{}`,
        signatureDeclarationsDefaults: `function serializeDefaults(x:number=1,y:string,z?:boolean):void{}function serializeBindings({x=1}:{x?:number}={},...rest:[string,boolean?]):void{}class Scope{}`,
        signatureDeclarationsPredicates: `function serializeGuard(x:unknown):x is string{return typeof x==='string'}function serializeAssert(this:{x:unknown},x:unknown):asserts x{}class Scope{}`,
        signatureDeclarationsInitializedProperties: `class SerializeRequired{constructor(public x:number=1,y:string,protected optional?:number|undefined){}}class SerializeError{constructor(public x?:Unresolved|undefined){}}function location(){}`,
    });
}
if (classOptions) {
    Object.assign(fixtures, {
        classOptionsExpressions: `const C=class<T>{value!:T;static member=1;constructor(value:T){this.value=value}};const Named=class Inner{field=1};type SerializeClass=typeof C;type SerializeNamed=typeof Named;class Scope{}function location(){}`,
        classOptionsPrivate: `function make(){class Hidden{private secret=1;protected field='';public value=1;get item(){return this.value}set item(value:string|number){}method(x:number){return x}}return Hidden}const C=make();type SerializeClass=typeof C;type SerializeInstance=InstanceType<typeof C>;type InstanceType<T extends new(...args:any[])=>any>=T extends new(...args:any[])=>infer U?U:never;class Scope{}function location(){}`,
        classOptionsAbstract: `function make(){abstract class Hidden{abstract method():void;static member=1}return Hidden}const C=make();const Empty=class{};type SerializeClass=typeof C;type SerializeEmpty=typeof Empty;class Scope{}function location(){}`,
        classOptionsInstantiations: `namespace N{export class Box<T>{value!:T}export function f<T>(x:T){return x}}type SerializeQualified=N.Box<number>;type SerializeFunction=typeof N.f<string>;function outer<T>(){class Inner<U>{value!:T;other!:U}return Inner}const C=outer<string>();type SerializeOuter=typeof C;class Scope{}function location(){}`,
        classOptionsMixins: `function mix<T extends abstract new(...args:any[])=>object>(Base:T){abstract class Derived extends Base{value=1;static member=1}type SerializeMixin=typeof Derived;return Derived}class Scope{}function location(){}`,
        classOptionsEmpty: `type SerializeEmpty={};type SerializeNested={field:{};optional?:{};readonly value:[]};type SerializeMembers={method():{};new():{}};class Scope{}function location(){}`,
        classOptionsQualifiedParameters: `function outer<T>(){class Inner<U>{value!:T;other!:U}return Inner}const C=outer<string>();type SerializeOuter=typeof C;namespace N{export class Box<T>{value!:T}}type SerializeReference=N.Box<number>;class Scope{}function location(){}`,
    });
}
if (functionOptions) {
    Object.assign(fixtures, {
        functionOptionsNames: `function f<T>(x:T):T{return x}const arrow=(x:number)=>x;const expr=function named(x:string){return x};class C{static f(x:number){return x}f(x:string){return x}}type SerializeFunction=typeof f;type SerializeArrow=typeof arrow;type SerializeExpression=typeof expr;type SerializeStatic=typeof C.f;class Scope{}function location(){}`,
        functionOptionsLocal: `function outer(){function local(x:number){return x}const arrow=(x:string)=>x;type SerializeLocal=typeof local;type SerializeArrow=typeof arrow;return local}type SerializeOuter=typeof outer;class Scope{}function location(){}`,
        functionOptionsSuppress: `type SerializeAny=(x:number)=>any;type SerializeNested=(callback:()=>any)=>()=>any;type SerializeObject={call(x:string):any;new():any;(x:number):any};type SerializeUnknown=()=>unknown;class Scope{}function location(){}`,
        functionOptionsGeneric: `interface Box<T>{call:<U extends T>(x:U)=>U;method<U extends T>(x:U):U}declare const box:Box<string>;type SerializeCall=typeof box.call;type SerializeMethod=typeof box.method;function id<T>(x:T):T{return x}const stringId=id<string>;type SerializeInstantiated=typeof stringId;class Scope{}function location(){}`,
        functionOptionsConstruct: `type SerializeNew=new(x:number)=>any;type SerializeAbstract=abstract new()=>unknown;class C<T>{constructor(public value:T){}}type SerializeClass=typeof C;class Scope{}function location(){}`,
        functionOptionsAliases: `namespace N{export function f(x:number){return x}}import Alias=N.f;type SerializeAlias=typeof Alias;type SerializeFunction=typeof N.f;class Scope{}function location(N:number){}`,
    });
}
if (generatedNames) {
    Object.assign(fixtures, {
        generatedNamesParameters: `type SerializeNested=<T>(a:T)=><T>(b:T)=>[T,typeof a];type SerializeSimple<T>=[T,T];class Scope<T,T_1>{}function location<T>(){}`,
        generatedNamesSiblings: `type SerializeSiblings={first:<T>()=>T;second:<T>()=>T};type SerializeOverloads={<T>(x:T):T;<T>(x:T,y:T):T};class Scope<T>{}function location<T>(){}`,
        generatedNamesCapture: `type SerializeCapture=<T>(x:T)=><T_1>(y:T_1)=><T>(z:T)=>[typeof x,typeof y,T];type SerializeConstraint=<T extends 'é',U extends T=T>(x:U)=>T;class Scope<T,T_1>{}function location<T>(){}`,
        generatedNamesInfer: `type SerializeInfer<T>=T extends [infer T,infer U]?[T,U]:never;type SerializeNested<T>=T extends infer U?U extends infer U?U:never:never;class Scope<T,U>{}function location<T,U>(){}`,
        generatedNamesMapped: `type SerializeMap<T>={readonly [K in keyof T]?:T[K]};type SerializeNested<T>={[K in keyof T]:{[K in keyof T[K]]:T[K][K]}};type SerializeRemap<T>={[K in keyof T as \`x\${K & string}\`]:T[K]};class Scope<T,K>{}function location<T,K>(){}`,
        generatedNamesModifiers: `type Pick<T,K extends keyof T>={[P in K]:T[P]};type SerializePick<T>=Pick<T,keyof T>;type SerializeSubset<T,K extends keyof T>=Pick<T,K>;class Scope<T,P>{}function location<T>(){}`,
        generatedNamesHomomorphic: `type Map<T>={[K in keyof T]?:T[K]};type SerializeIntersection<T>=Map<T&{extra:number}>;type SerializeUnion<T>=Map<T|{extra:number}>;class Scope<T>{}function location<T>(){}`,
        generatedNamesDistribution: `type C<T>=T extends string?T[]:number;type SerializeDistribution<T>=C<T&string>;type D<T>=T extends {value:infer U}?U:never;type SerializeInfer<T>=D<T&{value:string}>;class Scope<T,U>{}function location<T>(){}`,
        generatedNamesAnnotations: `type SerializeAnnotations=<T>(value:T)=>{fn:<T>(x:T)=>T;mapped:{[K in keyof T]:T[K]};conditional:T extends infer U?U:never};class Scope<T,K,U>{}function location<T>(){}`,
        generatedNamesDefaultScopes: `type SerializeCapture=<T>(x:T)=><U>(y:U)=>[T,U,typeof x,typeof y];type SerializeObject=<T>(x:T)=>{value:typeof x};class Scope<T>{}function location<T>(){}`,
    });
}
if (syntaxNames) {
    Object.assign(fixtures, {
        syntaxNamesNamespaces: `namespace A{export interface Foo{x:number}}namespace B{export interface Foo{y:string}}type SerializeTuple=[A.Foo,B.Foo];type SerializeUnion=A.Foo|B.Foo;type SerializeIntersection=A.Foo&B.Foo;class Scope{}function location(){}`,
        syntaxNamesSameReference: `namespace N{export interface Box<T>{value:T}}type SerializeSame=[N.Box<number>,N.Box<string>,N.Box<number>];type SerializeSingle=N.Box<boolean>;class Scope{}function location(){}`,
        syntaxNamesAliases: `namespace A{export type Item={x:number}}namespace B{export type Item={y:string}}type SerializeAlias=[A.Item,B.Item];type SerializeUnion=A.Item|B.Item;class Scope{}function location(){}`,
        syntaxNamesImports: `namespace N{export interface Shape{local:true}}type SerializeImports=[N.Shape,import('./dep').Shape];type SerializeSingle=import('./dep').Shape;class Scope{}function location(){}`,
        syntaxNamesNested: `namespace A{export namespace Inner{export class Value{a=1}}}namespace B{export namespace Inner{export class Value{b=''}}}type SerializeNested=[A.Inner.Value,B.Inner.Value];type SerializeParameter<T>=[T,A.Inner.Value];class Scope<T>{}function location<T>(){}`,
        syntaxNamesEnums: `namespace A{export enum E{X,Y}}namespace B{export enum E{X,Y}}type SerializeEnum=[A.E,B.E];type SerializeMembers=[A.E.X,B.E.X];type SerializeValues=[typeof A.E,typeof B.E];class Scope{}function location(){}`,
    });
}
if (syntaxOptions) {
    const union = Array.from({ length: 32 }, (_, i) => JSON.stringify(`literal${i}`)).join("|");
    const tuple = Array.from({ length: 32 }, (_, i) => JSON.stringify(`item${i}`)).join(",");
    const members = Array.from({ length: 24 }, (_, i) => `property${i}:number`).join(";");
    Object.assign(fixtures, {
        syntaxOptionsLists: `type SerializeUnion=${union};type SerializeTuple=[${tuple}];type SerializeNested=[${tuple},(${union})];class Scope{} function location(){}`,
        syntaxOptionsObjects: `type SerializeObject={${members}};type SerializeNested={head:{${members}};tail:{x:number}};class Scope{} function location(){}`,
        syntaxOptionsReferences: `interface VeryLongNameForReference<T,U,V>{a:T;b:U;c:V}type SerializeReferences=[VeryLongNameForReference<number,string,boolean>,VeryLongNameForReference<string,string,string>,VeryLongNameForReference<boolean,boolean,boolean>,VeryLongNameForReference<number[],number[],number[]>];class Scope{} function location(){}`,
        syntaxOptionsConditional: `type SerializeConditional<T>=[${tuple},T extends string ? (${union}) : {${members}}];type SerializeMapped<T>=[${tuple},{readonly [K in keyof T]?:T[K]}];class Scope{} function location(){}`,
        syntaxOptionsArrays: `type SerializeArrays=[number[],readonly string[],(number|string)[]];declare const key:unique symbol;type SerializeUnique=typeof key;type SerializeQuotes=['é','a\"b',"c'd"];type SerializeThis=(this:{value:number},arg:string)=>number;class Scope{} function location(){}`,
        syntaxOptionsBytes: `type SerializeBytes=${Array.from({ length: 16 }, (_, i) => JSON.stringify("é中😀" + i)).join("|")};type SerializeAnnotation={${Array.from({ length: 20 }, (_, i) => `field${i}:'é中😀'`).join(";")}};class Scope{} function location(){}`,
        syntaxOptionsSignatures: `type SerializeCall=(a:{${members}},b:[${tuple}],c:${union})=>{${members}};type SerializeOverloads={(x:${union}):number;(y:[${tuple}]):string;${members}};class Scope{} function location(){}`,
        syntaxOptionsReduction: `type SerializeReduction={tag:'a'}&{tag:'b'};type SerializePreserved=({x:number}|{y:string})&{z:boolean};class Scope{} function location(){}`,
        syntaxOptionsQueries: `function f<T>(x:T):T{return x}type SerializeQuery=[typeof f<'${"x".repeat(80)}'>,${tuple}];type SerializeQueryObject=[typeof f<'${"é".repeat(45)}'>,{${members}}];class Scope{} function location(){}`,
        syntaxOptionsQuotes: `enum E{'a-b'='é','c d'='x'}type SerializeEnum=E['a-b'];type SerializeImported=import('./dep').Shape;type SerializeUnion='é'|'中'|'😀';class Scope{} function location(){}`,
        syntaxOptionsQuoteAnnotations: `type SerializeQuotes=(arg:{"a-b":"x"})=>{"c-d":"y"};type SerializeNested=(arg:[{p:"é"}])=>[{q:"中"}];class Scope{} function location(){}`,
        syntaxOptionsUntruncatedLimit: `type SerializeLimit=["${"a".repeat(1_000_010)}",{value:number},'alpha'|'beta'|'gamma'|'delta'|'epsilon',[number,string]];`,
    });
}
if (symbolTypeArguments) {
    Object.assign(fixtures, {
        symbolArgumentsParameters: `class C<T extends string='é',U={readonly value?:T}>{field!:U;method(value:T):U{return this.field}static member=1}interface I<in T,out U>{method(value:T):U}class Unicode<É extends 'é'='é'>{value!:É}namespace N{export class Inner<T extends number=1>{value!:T}}`,
        symbolArgumentsInstances: `class C<T>{value!:T;method(value:T):T{return value};'a-b'!:T}declare const number:C<number>, text:C<string>;number.value;text.value;number.method;text['a-b'];namespace N{export class D<T>{field!:T}}import Alias=N.D;declare const aliased:Alias<boolean>;aliased.field;`,
        symbolArgumentsConstraints: `type Map<T>={[K in keyof T]:T[K]};class C<T extends Map<{value:number}>,U extends (x:T)=>T,V=T extends {value:infer R}?R:never>{field!:V}function outer<T extends string>(){class Inner<U extends T>{value!:U}return Inner}const Local=outer<'é'>();new Local().value;`,
        symbolArgumentsTruncation: `class C<T=readonly [${Array.from({ length: 40 }, (_, i) => JSON.stringify(`value${i}`)).join(",")}],U={${Array.from({ length: 24 }, (_, i) => `property${i}:number`).join(";")}}>{field!:U}class Scope{}function location(){}`,
    });
}
if (anonymousSyntax) {
    Object.assign(fixtures, {
        anonymousSyntaxValues: `function f(x:number):string{return ''}const arrow=(x:string)=>x;const expression=function named(x:boolean){return x};type SerializeFunction=typeof f;type SerializeArrow=typeof arrow;type SerializeExpression=typeof expression;class Scope{} function location(){}`,
        anonymousSyntaxNamed: `class C<T>{static value=1;field!:T;}abstract class A{}enum E {A,B}namespace N {export const value=1;}type SerializeClass=typeof C;type SerializeAbstract=typeof A;type SerializeEnum=typeof E;type SerializeNamespace=typeof N;class Scope{} function location(){}`,
        anonymousSyntaxMembers: `class C {static method(x:number):string{return ''} method(x:string):number{return 0}}declare const instance:C;const obj={method(x:string){return x},get value(){return 1},set value(x:string|number){}};type SerializeStatic=typeof C.method;type SerializeInstance=typeof instance.method;type SerializeObject=typeof obj;class Scope{} function location(){}`,
        anonymousSyntaxDefaults: `function f(x:number=1,y:string,z?:boolean){return y}function g(x='é'){return x}function h({x=1,y:renamed='a'}:{x?:number,y?:string}={}){return x}type SerializeRequired=typeof f;type SerializeOptional=typeof g;type SerializeBindings=typeof h;class Scope{} function location(){}`,
        anonymousSyntaxRecursive: `function f():typeof f{return f}const arrow=():typeof arrow=>arrow;class C {static f():typeof C.f{return C.f}}type SerializeFunction=typeof f;type SerializeArrow=typeof arrow;type SerializeStatic=typeof C.f;class Scope{} function location(){}`,
        anonymousSyntaxClassExpressions: `const C=class Inner<T>{field!:T;static x=1};const D=class {value=1};type SerializeNamed=typeof C;type SerializeUnnamed=typeof D;class Scope{} function location(){}`,
        anonymousSyntaxMixins: `function mix<T extends abstract new (...args:any[])=>object>(Base:T){abstract class Derived extends Base{value=1;static field='x'}type SerializeMixin=typeof Derived;return Derived}class Scope{} function location(){}`,
        anonymousSyntaxInstantiations: `function f<T>(x:T):T{return x}class C<T>{value!:T;constructor(value:T){this.value=value}}type SerializeFunction=typeof f<string>;type SerializeClass=typeof C<number>;class Scope{} function location(){}`,
        anonymousSyntaxComputed: `declare const key:unique symbol;enum E {A='a',B=2}type SerializeSymbols={[key]:string};type SerializeEnums={[E.A]:number;[E.B]:string};type SerializeMethods={[key](x:number):string};class Scope{} function location(){}`,
        anonymousSyntaxInstantiationScopes: `namespace N{export function f<T>(x:T):T{return x}}type SerializeGlobal=typeof N.f<'é'>;type SerializeArg<T>=typeof N.f<T>;class Scope<N>{}function location(N:number){}`,
        anonymousSyntaxComposite: `type SerializeUnion=({x:number}&{y:string})|boolean;type SerializeFunction=((x:number)=>string)|boolean;type SerializeIntersection=({x:number}|{y:string})&{z:boolean};class Scope{} function location(){}`,
    });
}
if (signatureSyntax) {
    Object.assign(fixtures, {
        signatureSyntaxBasic: `type SerializeFn=(x:number,y?:string)=>boolean;type SerializeVoid=()=>void;type SerializeRest=(...args:string[])=>number;type SerializeLiteral=(x:'é'|'b')=>'é';class Scope{} function location(){}`,
        signatureSyntaxGeneric: `type SerializeGeneric=<T extends string,U=T>(value:T,other?:U)=>[T,U];type SerializeConst=<const T extends readonly unknown[]>(value:T)=>T;type SerializeNested=<T>(value:T)=><U>(other:U)=>[T,U];class Scope<T>{} function location<U>(){}`,
        signatureSyntaxConstruct: `type SerializeNew=new (x:number)=>{value:number};type SerializeAbstract=abstract new <T>(x:T)=>{value:T};type SerializeOverloads={new(x:number):{n:number};new(x:string):{s:string}};class Scope{} function location(){}`,
        signatureSyntaxTuples: `type SerializeRest=(...args:[x:number,y?:string,...z:boolean[]])=>void;type SerializeUnnamed=(...args:[number,string])=>void;type SerializeRepeated=(...args:[x:number,x:string,x_1:boolean])=>void;type SerializeVariadic<T extends unknown[]>=(...args:[...T,number])=>T;class Scope{} function location(){}`,
        signatureSyntaxPredicates: `type SerializeGuard=(x:unknown)=>x is string;type SerializeAssert=(x:unknown)=>asserts x is string;type SerializeTruthy=(x:unknown)=>asserts x;type SerializeThis=(this:{value:unknown})=>this is {value:string};class Scope{} function location(){}`,
        signatureSyntaxOverloads: `type SerializeCalls={(x:number):number;(x:string):string;field:number};type SerializeBoth={(x:string):number;new(x:number):{value:number};[key:string]:unknown};type SerializeCallback={callback:(value:number)=>string};class Scope{} function location(){}`,
        signatureSyntaxBindings: `type SerializeObject=({x,y:renamed}:{x:number,y:string})=>void;type SerializeArray=([a,,...rest]:[number,string,...boolean[]])=>void;type SerializeThis=(this:{x:number},x:string)=>void;class Scope{} function location(){}`,
        signatureSyntaxShadows: `declare const x:unique symbol;type SerializeShadow=(x:number)=>typeof globalThis.x;type SerializeNested=(x:number)=>(y:string)=>typeof globalThis.x;type SerializeBindings=({x}:{x:number})=>typeof globalThis.x;class Scope{} function location(){}`,
        signatureSyntaxMethods: `type SerializeMethods={m<T>(value:T):T;m(value:string):number;optional?(x:number):string;'a-b'(x:string):void};type SerializeProperty={readonly callback:(x:number)=>string;};class Scope{} function location(){}`,
        signatureSyntaxAccessors: `type SerializeSame={get value():string;set value(v:string);};type SerializeDifferent={get value():number;set value(v:string|number);};type SerializeRead={get value():'é';};type SerializeWrite={set value(v:number);};class Scope{} function location(){}`,
        signatureSyntaxInstantiation: `type Box<T>={method(value:T):T; get field():T;set field(v:T|undefined)};type SerializeConcrete=Box<string>;type SerializeGeneric<U>=Box<U>;type SerializeCall=({value:number}&((x:number)=>string));class Scope{} function location(){}`,
        signatureSyntaxScopeValues: `declare var x:unique symbol;type SerializeShadow=(x:number)=>typeof globalThis.x;type SerializeNested=(x:number)=>(y:string)=>typeof globalThis.x;type SerializeReuse=(x:number)=>typeof x;class Scope{} function location(){}`,
    });
}
if (mappedSyntax) {
    Object.assign(fixtures, {
        mappedSyntaxModifiers: `type SerializePlain<T>={[K in keyof T]:T[K]};type SerializePlus<T>={+readonly [K in keyof T]+?:T[K]};type SerializeMinus<T>={-readonly [K in keyof T]-?:T[K]};type SerializeOptional<T>={[K in keyof T]?:T[K]};class Scope{} function location(){}`,
        mappedSyntaxConstraints: `type SerializeKeys<K extends string>={[P in K]:P};type SerializeUnion<T,U>={[K in keyof (T|U)]:K};type SerializeIntersection<T,U>={[K in keyof (T&U)]:K};type SerializeIndexed<T,K extends keyof T>={[P in K]:T[P]};class Scope{} function location(){}`,
        mappedSyntaxRemap: `type SerializeRename<T>={[K in keyof T as \`prefix_\${K & string}\`]:T[K]};type SerializeFilter<T>={[K in keyof T as T[K] extends string ? K : never]:T[K]};type SerializeConditional<T>={[K in keyof T]:T[K] extends infer U ? U : never};class Scope{} function location(){}`,
        mappedSyntaxConcrete: `type Map<T>={readonly [K in keyof T]?:T[K]};type SerializeObject=Map<{x:number;'a-b':string}>;type SerializeTuple=Map<[number,string]>;type SerializeArray=Map<number[]>;type SerializeFinite={[K in 'a'|'b']:number};class Scope{} function location(){}`,
        mappedSyntaxNested: `type SerializeNested<T>={[K in keyof T]:{[P in keyof T[K]]:T[K][P]}};type SerializeRecursive<T>={[K in keyof T]:SerializeRecursive<T[K]>};type SerializeOuter<T>={[K in keyof T as K]:{key:K,value:T[K]}};class Scope<K>{} function location<K>(){}`,
        mappedSyntaxAliases: `import type {Shape as S} from './dep';type M<T>={[K in keyof T]:S};type SerializeAlias<T>=M<T>;type SerializeNested<T>={[K in keyof T]:S|T[K]};class Scope<S>{} function location<S>(){}`,
    });
}
if (conditionalSyntax) {
    Object.assign(fixtures, {
        conditionalSyntaxBasic: `type SerializeBasic<T>=T extends string ? number : boolean; type SerializeNested<T>=T extends string ? T extends "x" ? 1 : 2 : never; type SerializeCheck<T>=(T extends string ? number : boolean) extends number ? 1 : 2; type SerializeExtends<T,U>=T extends (U extends string ? number : boolean) ? 1 : 2; class Scope{} function location(){}`,
        conditionalSyntaxInfer: `type SerializeInfer<T>=T extends infer U ? U : never; type SerializeConstrained<T>=T extends infer U extends string ? U : never; type SerializeArray<T>=T extends (infer U)[] ? U : never; type SerializeTuple<T>=T extends [infer H,...infer R] ? [H,R] : never; class Scope{} function location(){}`,
        conditionalSyntaxObject: `type SerializeObject<T>=T extends {value:infer U} ? U : never; type SerializeFields<T>=T extends {readonly value?:infer U, 'a-b':infer V} ? [U,V] : never; type SerializeIndex<T>=T extends {[name:string]:infer U} ? U : never; class Scope{} function location(){}`,
        conditionalSyntaxScopes: `type SerializeNested<T>=T extends infer U ? U extends infer U ? U : never : never; type SerializeConstrained<T>=T extends [infer U extends string,infer V extends number] ? U|V : never; type SerializeTemplate<T>=T extends \`a\${infer U}b\` ? U : never; class Scope<U>{} function location<V>(){}`,
        conditionalSyntaxDistribution: `type C<T>=T extends string ? T[] : T; type SerializeDistribute<T>=C<T|number>; type SerializeUnion<T>=C<T>|string; type SerializeRecursive<T>=T extends readonly [infer H,...infer R] ? [H,SerializeRecursive<R>] : []; class Scope{} function location(){}`,
        conditionalSyntaxProperties: `type SerializeFields={readonly x?:number; "é":string; 0x10:boolean;}; type SerializeIndex={[key:string]:string|number;[key:number]:number;}; type SerializeNested={a:{b:[number,string]};c?:number[];}; class Scope{} function location(){}`,
        conditionalSyntaxLiteralAnnotations: `type SerializeLiterals={single?:'é'; choice?:'a'|'b'; numeric?:0x10; nested:('x'|'y'); explicit?:number|undefined;}; class Scope{} function location(){}`,
    });
}
if (typeSyntax) {
    Object.assign(fixtures, {
        typeSyntaxPrimitives: `type SerializeAny=any; type SerializeUnknown=unknown; type SerializeString=string; type SerializeNumber=number; type SerializeBool=boolean; type SerializeBigInt=bigint; type SerializeSymbol=symbol; type SerializeObject=object; type SerializeVoid=void; type SerializeUndefined=undefined; type SerializeNull=null; type SerializeNever=never; class Scope{} function location(){}`,
        typeSyntaxLiterals: `type SerializeString="é中😀";type SerializeNumber=-12;type SerializeBigInt=-12n;type SerializeTrue=true;type SerializeFalse=false;type SerializeZero=-0;type SerializeEscaped="\\uD800";class Scope{} function location(){}`,
        typeSyntaxComposite: `type SerializeUnion=number|string|null|undefined;type SerializeBooleans=true|false|string;type SerializeIntersection=string&{};type SerializeLiteralUnion=1|2|3;type SerializeArray=(1|2)[];type SerializeReadonly=readonly string[];class Scope{} function location(){}`,
        typeSyntaxTuples: `type SerializeTuple=[first:number,second?:string,...rest:boolean[]];type SerializeReadonly=readonly [1,"é"];type SerializeOptional=[(string|number)?];type SerializeEmpty=[];type SerializeVariadic<T extends unknown[]>=[number,...T];class Scope{} function location(){}`,
        typeSyntaxNamed: `import type {Shape as Imported} from "./dep";interface Box<T>{value:T;}namespace N {export class C<T>{value!:T;}}type SerializeBox=Box<string>;type SerializeNested=N.C<number[]>;type SerializeImported=Imported;type SerializeParameter<T>=T;class Scope{} function location(){}`,
        typeSyntaxOperators: `type SerializeKey<T>=keyof T;type SerializeIndexed<T,K extends keyof T>=T[K];type SerializeTemplate<T extends string>=\`é-\${T}-😀\`;type Uppercase<S extends string>=intrinsic;type SerializeMapping<T extends string>=Uppercase<T>;class Scope{} function location(){}`,
        typeSyntaxEnums: `enum E {A=1,B=2}enum Named {"a-b"="text","c d"="other"}type SerializeEnum=E;type SerializeMember=E.A;type SerializeStringEnum=Named;type SerializeStringMember=Named["a-b"];type SerializeMixed=E.A|E.B|string;class Scope{} function location(){}`,
        typeSyntaxUnique: `declare const key:unique symbol;type SerializeKey=typeof key;class Scope {method():this{return this;}}function location(){}`,
    });
}
if (symbolTypeNodes) {
    Object.assign(fixtures, {
        symbolTypeModes: `import type {Shape} from "./esm.mjs"; export type Result=Shape; export class Local {field!:Shape;}`,
        symbolTypeAttributes: `declare module "data" with {type:"json"} {export interface Shape {field:number;}}
import {Shape} from "data" with {type:"json",extra:"yes"}; export type Result=Shape;`,
    });
}
for (const [name, source] of Object.entries(fixtures)) {
    for (const strict of [false, true]) {
        for (const concurrency of [1, 4]) {
            if (name === "syntaxOptionsUntruncatedLimit" && (strict || concurrency !== 1)) continue;
            const files = { "/project/globals.d.ts": library + (symbolChains || accessibility ? "declare const globalValue:number;" : ""), [`/project/main.${name === "visibilityDts" ? "d.ts" : name.startsWith("jsx") ? "tsx" : name.startsWith("jsdoc") ? "js" : "ts"}`]: source, "/project/dep.ts": "export interface Shape {value:number;} export const named=1; export default class Default { value=1; }" + (symbolChains || accessibility ? " export namespace Deep {export class Member {value=1;}}" : "") };
            if (name === "chainDeepReexports") {
                files["/project/barrel.ts"] = "export * as nested from './dep'; export * as loop from './barrel2';";
                files["/project/barrel2.ts"] = "export * as next from './barrel';";
            }
            if (name === "accessibilityExportEquals") files["/project/exported.ts"] = "class Root {} namespace Root {export class Item {value=1;}} export=Root;";
            if (name === "symbolTypeModes") {
                delete files["/project/main.ts"];
                files["/project/main.cts"] = source;
                files["/project/esm.mts"] = "export interface Shape {value:number;} export const value=1;";
            }
            inputs.push({
                name: `${name}:${strict}:${concurrency}`,
                files: Object.fromEntries(Object.entries(files).map(([name, text]) => [name, Buffer.from(text).toString("base64")])),
                roots: Object.keys(files),
                options: { strict, target: "esnext", module: "esnext", moduleResolution: "bundler", jsx: "preserve", ...name.startsWith("jsdoc") ? { allowJs: true, checkJs: true } : {} },
                typeNodes: true,
                ...typeSyntax ? { typeSyntax: true, ...functionOptions ? { typeSyntaxFlags: [1, 4097, 4105, 257, 33, 289, 8193, 134217729] } : generatedNames ? { typeSyntaxFlags: [4, 5, 69] } : syntaxNames ? { typeSyntaxFlags: [0, 1, 65, 17, 81] } : syntaxOptions ? { typeSyntaxFlags: [0, 1, 2, 3, 1 | (1 << 28), 1 | (1 << 20), 1 | (1 << 25), 1 | (1 << 29)] } : {} }
                    : symbolTypeNodes ? { symbolTypeNodes: true } : symbolFormats ? { symbolFormats: true, ...symbolTypeArguments ? { symbolFormatFlags: [5, 7, 13, 15, 21, 23, 29, 31, 37, 39, 45, 47, 53, 55, 61, 63] } : computedSymbols ? { symbolFormatFlags: [20, 22, 28, 30, 52, 54, 60, 62] } : {} } : symbolDisplay ? { symbolDisplay: true } : accessibility ? { accessibility: true } : symbolChains ? { symbolChains: true } : declarationVisibility ? { declarationVisibility: true } : contextQueries ? { contextQueries: true } : scopeServices ? { scopeServices: true } : symbolLocations ? { symbolLocations: true, ...name.startsWith("jsdoc") ? { documentationSymbols: true } : {} } : { locations: true },
                concurrency,
            });
            if (name === "syntaxOptionsUntruncatedLimit") inputs.at(-1).typeSyntaxFlags = [1];
            if (name === "generatedNamesDefaultScopes") inputs.at(-1).typeSyntaxFlags = [1];
            if (classOptions) inputs.at(-1).typeSyntaxFlags = [1, 2049, 1025, 1073741825, 1073743873, 2053];
            if (name === "classOptionsQualifiedParameters") inputs.at(-1).typeSyntaxFlags = [513, 1073742337, 1073744385];
            if (signatureDeclarations) {
                delete inputs.at(-1).locations;
                inputs.at(-1).signatureSyntax = true;
            }
            if (emitQueries) {
                const input = inputs.at(-1);
                delete input.locations;
                input.emitQueries = true;
                if (name === "emitQueriesVerbatim") input.options.verbatimModuleSyntax = true;
                if (name === "emitQueriesConstAliases" && strict) input.options.preserveConstEnums = true;
                if (name === "emitQueriesAugmentation") input.files["/project/augment.ts"] = Buffer.from(`import './main';declare module './main'{interface Value{extra:string}}`).toString("base64");
            }
            if (emitReferences) {
                const input = inputs.at(-1);
                delete input.locations;
                input.emitReferences = true;
            }
            if (emitSerialization) {
                const input = inputs.at(-1);
                delete input.locations;
                input.emitSerialization = true;
            }
            if (emitLinks) {
                const input = inputs.at(-1);
                delete input.locations;
                input.emitLinks = true;
                if (name === "emitLinksVerbatim") input.options.verbatimModuleSyntax = true;
                if (name === "emitLinksConstEnums") {
                    input.options.preserveConstEnums = strict;
                    input.files["/project/dep.ts"] = Buffer.from(`export const enum CE{A=1}export namespace Only{export const enum E{A=1}}export enum Regular{A=1}`).toString("base64");
                }
                if (name === "emitLinksDecorators") Object.assign(input.options, { experimentalDecorators: true, emitDecoratorMetadata: true });
                if (name === "jsxEmitLinks") {
                    input.options.jsx = "react";
                    input.files["/project/dep.ts"] = Buffer.from(`export function createElement(...args:any[]):any{}export const Fragment={};export default function Component():any{}export namespace JSX{export interface Element{}export interface IntrinsicElements{div:{}}}`).toString("base64");
                }
                if (name === "jsxEmitLinksConstFactory") {
                    input.options.jsx = "react";
                    input.files["/project/dep.ts"] = Buffer.from(`export const enum CE{createElement=1}`).toString("base64");
                }
            }
            if (emitJsx) {
                const input = inputs.at(-1);
                delete input.locations;
                input.emitJsx = true;
                input.options.jsx = "react";
                if (name === "jsxFactoryConfigured") Object.assign(input.options, { jsxFactory: "Factory.make", jsxFragmentFactory: "Factory.Fragment" });
                if (name === "jsxFactoryInvalid") Object.assign(input.options, { jsxFactory: "bad[0]", jsxFragmentFactory: "bad[0]" });
                if (name === "jsxFactoryNamespace") input.options.reactNamespace = strict ? "Custom.Nested" : "Custom";
                if (name === "jsxFactoryEmptyOptions") Object.assign(input.options, { jsxFactory: "", jsxFragmentFactory: "", reactNamespace: "Custom" });
            }
            if (emitRecovery) {
                delete inputs.at(-1).locations;
                inputs.at(-1).emitRecovery = true;
            }
            if (emitSyntax) {
                delete inputs.at(-1).locations;
                inputs.at(-1).emitSyntax = true;
            }
            if (emitServices) {
                const input = inputs.at(-1);
                delete input.locations;
                input.emitServices = true;
            }
        }
    }
}
const formatFixtures = new Set(["namespaces", "imports", "typeImports", "classes", "functions", "importsCommonJs", "visibilityAliases", "visibilityAmbient", "visibilityExports", "chainShadow", "chainReexports", "chainTypeOnly", "chainClassNames", "displayNames", "displayComputed", "displayAssigned", "accessibilityExportEquals", "jsdocDisplayNames"]);
const computedFixtures = new Set(["displayNames", "displayComputed", "displayAssigned", "jsdocDisplayNames", "classes", "visibilityExports", "accessibilityInstances", "chainClassNames"]);
const typeNodeFixtures = new Set(["namespaces", "imports", "typeImports", "classes", "importsCommonJs", "visibilityAliases", "visibilityAmbient", "visibilityExports", "chainShadow", "chainReexports", "chainTypeOnly", "displayNames", "displayComputed", "accessibilityExportEquals", "accessibilityInstances", "symbolTypeModes", "symbolTypeAttributes"]);
if (symbolTypeNodes) { for (const input of inputs) if (input.name.startsWith("symbolTypeModes:")) Object.assign(input.options, { module: "nodenext", moduleResolution: "nodenext" }); }
const eligible = emitRecovery ? inputs.filter(input => input.name.startsWith("emitRecovery") || input.name.startsWith("jsdocEmitRecovery")) : emitSyntax ? inputs.filter(input => input.name.startsWith("emitSyntax") || input.name.startsWith("jsdocEmitSyntax")) : emitServices ? inputs.filter(input => input.name.startsWith("emitServices") || input.name.startsWith("jsdocEmitServices")) :
    emitJsx ? inputs.filter(input => input.name.startsWith("jsxFactory")) : emitLinks ? inputs.filter(input => input.name.startsWith("emitLinks") || input.name.startsWith("jsxEmitLinks") || input.name.startsWith("jsdocEmitLinks")) : emitSerialization ? inputs.filter(input => input.name.startsWith("emitSerialization")) : emitReferences ? inputs.filter(input => input.name.startsWith("emitReferences") || input.name.startsWith("jsdocEmitReferences")) : emitQueries ? inputs.filter(input => input.name.startsWith("emitQueries") || input.name.startsWith("jsdocEmitQueries")) : signatureDeclarations ? inputs.filter(input => input.name.startsWith("signatureDeclarations")) : classOptions ? inputs.filter(input => input.name.startsWith("classOptions")) : functionOptions ? inputs.filter(input => input.name.startsWith("functionOptions")) : generatedNames ? inputs.filter(input => input.name.startsWith("generatedNames"))
    : syntaxNames ? inputs.filter(input => input.name.startsWith("syntaxNames")) : syntaxOptions ? inputs.filter(input => input.name.startsWith("syntaxOptions")) : symbolTypeArguments ? inputs.filter(input => input.name.startsWith("symbolArguments") || formatFixtures.has(input.name.split(":")[0])) : anonymousSyntax ? inputs.filter(input => input.name.startsWith("anonymousSyntax")) : signatureSyntax ? inputs.filter(input => input.name.startsWith("signatureSyntax")) : mappedSyntax ? inputs.filter(input => input.name.startsWith("mappedSyntax")) : conditionalSyntax ? inputs.filter(input => input.name.startsWith("conditionalSyntax")) : typeSyntax ? inputs.filter(input => input.name.startsWith("typeSyntax")) : symbolTypeNodes ? inputs.filter(input => typeNodeFixtures.has(input.name.split(":")[0])) : computedSymbols ? inputs.filter(input => computedFixtures.has(input.name.split(":")[0])) : symbolFormats ? inputs.filter(input => formatFixtures.has(input.name.split(":")[0])) : inputs;
const selected = process.argv.includes("--filter") ? eligible.filter(input => input.name.includes(option("--filter"))) : eligible;
await writeFile(path.join(output, "inputs.json"), JSON.stringify(selected, null, 2));
if (process.argv.includes("--list")) process.exit(0);
async function probe(command, args, input, executableHash, role) {
    const cache = path.join(output, `${role}-${hash(JSON.stringify(input) + executableHash)}.json`);
    try {
        return JSON.parse(await readFile(cache, "utf8"));
    }
    catch (error) {
        if (error.code !== "ENOENT") throw error;
    }
    let previousError;
    try {
        previousError = await readFile(cache + ".error.txt", "utf8");
    }
    catch (error) {
        if (error.code !== "ENOENT") throw error;
    }
    if (previousError) throw Error(previousError);
    const result = await new Promise((resolve, reject) => {
        const child = spawn(command, args, { windowsHide: true });
        const stdout = [], stderr = [];
        child.stdout.on("data", data => stdout.push(data));
        child.stderr.on("data", data => stderr.push(data));
        child.on("error", reject);
        child.stdin.on("error", () => {});
        child.on("close", code =>
            code ? reject(Error(`${role} ${input.name}: ${Buffer.concat(stderr)}`))
                : resolve(JSON.parse(Buffer.concat(stdout).toString())));
        child.stdin.end(JSON.stringify(input) + "\n");
    }).catch(async error => {
        await writeFile(cache + ".error.txt", error.message);
        throw error;
    });
    await writeFile(cache, JSON.stringify(result));
    return result;
}
const failures = [];
const locationMetadataDifferences = [];
let queries = 0, comparedQueries = 0, referenceFailures = 0, candidateFailures = 0;
const results = [];
for (const input of selected) {
    let reference, candidate, referenceError, candidateError;
    try {
        reference = await probe(oracle, [], input, oracleHash, "reference");
    }
    catch (error) {
        referenceError = String(error);
        referenceFailures++;
    }
    try {
        candidate = await probe(dotnet, [dll, "--checker-program-lines"], input, candidateHash, "candidate");
    }
    catch (error) {
        candidateError = String(error);
        candidateFailures++;
    }
    if (candidate) queries += (candidate.emitQueries ?? candidate.typeSyntaxQueries ?? candidate.symbolTypeNodeQueries ?? candidate.symbolFormatQueries ?? candidate.symbolDisplayQueries ?? candidate.accessibilityQueries ?? candidate.symbolChainQueries ?? candidate.visibilityQueries ?? candidate.contextQueries ?? candidate.serviceQueries ?? candidate.symbolLocationQueries ?? candidate.locationQueries).length + (candidate.documentationSymbolQueries?.length ?? 0);
    results.push({ input, reference, candidate, referenceError, candidateError });
    if (referenceError || candidateError) {
        failures.push({ name: input.name, referenceError, candidateError });
        continue;
    }
    comparedQueries += (candidate.emitQueries ?? candidate.typeSyntaxQueries ?? candidate.symbolTypeNodeQueries ?? candidate.symbolFormatQueries ?? candidate.symbolDisplayQueries ?? candidate.accessibilityQueries ?? candidate.symbolChainQueries ?? candidate.visibilityQueries ?? candidate.contextQueries ?? candidate.serviceQueries ?? candidate.symbolLocationQueries ?? candidate.locationQueries).length + (candidate.documentationSymbolQueries?.length ?? 0);
    try {
        assert.deepStrictEqual(candidate, reference);
    }
    catch {
        failures.push({ name: input.name, input, reference, candidate });
        if (symbolLocations) {
            // These columns describe the input comment node, not a returned
            // symbol. Retain every strict failure while reporting answer parity.
            const answers = result =>
                result.documentationSymbolQueries ? {
                    ...result,
                    documentationSymbolQueries: result.documentationSymbolQueries.map(([owner, kind, , , symbol]) => [owner, kind, symbol]),
                } : result;
            if (isDeepStrictEqual(answers(candidate), answers(reference))) locationMetadataDifferences.push(input.name);
        }
    }
}
await writeFile(path.join(output, "failures.json"), JSON.stringify(failures, null, 2));
await writeFile(path.join(output, "results.json"), JSON.stringify(results));
const summary = { configurations: selected.length, queries, comparedQueries, exact: selected.length - failures.length, failed: failures.length, referenceRevision, referenceFailures, candidateFailures, oracleHash, candidateHash, inputHash: hash(JSON.stringify(selected)), resultHash: hash(JSON.stringify(results)), managed: true };
if (symbolLocations) Object.assign(summary, { symbolAnswerMatches: summary.exact + locationMetadataDifferences.length, locationMetadataDifferences });
if (scopeServices) {
    const counts = Array(7).fill(0);
    for (const result of results) for (const record of result.candidate?.serviceQueries ?? []) counts[record[0]]++;
    summary.services = {
        scopes: counts[0],
        symbolTypesAtLocation: counts[1],
        symbolTypesWithoutLocation: counts[1],
        moduleExports: counts[2],
        aliasTargets: counts[3],
        localExportTargets: counts[4],
        shorthandValues: counts[5],
        parameterPropertyPairs: counts[6],
    };
}
if (declarationVisibility) {
    const phases = Array(5).fill(0);
    for (const result of results) for (const record of result.candidate?.visibilityQueries ?? []) phases[record[0]]++;
    summary.visibilityRecords = { initial: phases[0], afterPrecalculation: phases[1], aliasRetention: phases[2], afterRetention: phases[3], afterRepeatedPrecalculation: phases[4] };
}
if (symbolChains) {
    let found = 0, absent = 0, longest = 0;
    for (const result of results) {
        for (const record of result.candidate?.symbolChainQueries ?? []) {
            if (record[4] === null) absent++;
            else {
                found++;
                longest = Math.max(longest, record[4].length);
            }
        }
    }
    summary.chains = { found, absent, longest };
}
if (accessibility && !symbolDisplay) {
    const counts = Array(5).fill(0), states = Array(4).fill(0);
    for (const result of results) {
        for (const record of result.candidate?.accessibilityQueries ?? []) {
            counts[record[0]]++;
            if (record[0] === 1) states[record[6]]++;
        }
    }
    summary.accessibilityRecords = { containers: counts[0], decisions: counts[1], flagQueries: counts[2], typeAndValueQueries: counts[3], entityNames: counts[4], decisionStates: states };
    summary.symbolDiagnosticNamesCompared = false;
    summary.entityNameVisibilityComparedCompletely = true;
}
if (symbolDisplay && !symbolFormats && !symbolTypeNodes) {
    let names = 0, accessibilityResults = 0;
    for (const entry of results) for (const row of entry.candidate?.symbolDisplayQueries ?? []) row[0] === 0 ? names++ : accessibilityResults++;
    summary.symbolDisplayRecords = { names, accessibilityResults };
}
if (symbolFormats) summary.symbolFormatFlags = symbolTypeArguments ? [5, 7, 13, 15, 21, 23, 29, 31, 37, 39, 45, 47, 53, 55, 61, 63] : computedSymbols ? [20, 22, 28, 30, 52, 54, 60, 62] : [0, 1, 2, 6, 8, 10, 12, 14, 16, 17, 32, 34, 36, 38, 40, 42, 44, 46, 64];
if (contextQueries) {
    const counts = Array(8).fill(0);
    for (const result of results) for (const record of result.candidate?.contextQueries ?? []) counts[record[0]]++;
    summary.contextRecords = {
        expressionTypes: counts[0],
        objectElements: counts[1],
        arrayPositions: counts[2],
        jsxAttributes: counts[3],
        resolvedSignaturesAndReturns: counts[4],
        argumentTypes: counts[5],
        signatureHelp: counts[6],
        stringCompletions: counts[7],
    };
}
await writeFile(path.join(output, "summary.json"), JSON.stringify(summary, null, 2));
if (process.argv.includes("--record")) await writeFile(path.join(root, "csharp/compatibility/evidence", option("--record") + ".json"), JSON.stringify(summary, null, 4) + "\n");
console.log(summary);
console.log(failures.map(f => ({ name: f.name, ...f.referenceError ? { referenceError: f.referenceError.slice(0, 180) } : {}, ...f.candidateError ? { candidateError: f.candidateError.slice(0, 180) } : {} })));
if (failures.length) process.exitCode = 1;
