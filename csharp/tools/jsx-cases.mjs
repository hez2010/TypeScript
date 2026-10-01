export function jsxCases(referenceSource) {
    const cases = [];
    const sources = [
        'const el = <div/>;',
        'export const el = <View<T> x={value} />;',
        'export const el = <UI.View checked hidden={false} data-id="x" xml:lang="en" />;',
        'export const el = <x:tag />;',
        'export const el = <div>a {x} b <span/> c</div>;',
        'export const el = <><A/><B/></>;',
        'export const el = <><A/></>;',
        'export const el = <></>;',
        'export const el = <div>{/* comment */}</div>;',
        'export const el = <div>{...values}</div>;',
        'export const el = <div>\n    hello\n    world\n  </div>;',
        'export const el = <div>  hello\r\n world  </div>;',
        'export const el = <div> \t\n\t  </div>;',
        'export const el = <div>\u00a0\u200b A\u2028 \u1680B\u2029 C\u00a0</div>;',
        'export const el = <div title="&amp;&lt;&quot;&#65;&#x1F600;">&nbsp;&copy;&unknown;& &amp; &#xD800;</div>;',
        'export const el = <div title=\'a\\b\n&amp;\'>A &oops &amp; B &&gt; C &unterminated</div>;',
        'export const el = <div>&#0; &#X41; &#-1; &#x110000; &#2147483647; &#2147483648; &#; &#x; &;</div>;',
        'export const el = <View {...props}/>;',
        'export const el = <View a={1} {...props} b={2} {...other} c={3}/>;',
        'export const el = <View {...{a: 1, ...props, b: 2}} c={3}/>;',
        'export const el = <View {...{__proto__: parent, x: 1}}/>;',
        'export const el = <View {...{["__proto__"]: parent, x: 1}}/>;',
        'export const el = <View key={first} {...props}/>;',
        'export const el = <View {...props} key={last}/>;',
        'export const el = <View {...{x: 1}} key={last}/>;',
        'export const el = <View {...{...props}} key={last}/>;',
        'export const el = <View key={first} key={last}/>;',
        'export const el = <View key title={<span/>} />;',
        'export const el = <View title=<span/> />;',
        'export const el = <View children="before">after</View>;',
        'export const f = (x = <View/>) => <>{x}<Child /></>;',
        '"use client"; /* file */ export const el = <div><A/> text <B/></div>; // tail',
        'export const _jsx = 1, _jsxs = 2, _Fragment = 3, _jsxDEV = 4, _jsxFileName = 5, _createElement = 6; const el = <><A/><B {...p} key="x"/></>;',
        '/* 😀 */ export const el = <div>é😀<X/></div>;',
        '/** @jsx h */\nexport const el = <View/>;',
        '/** @jsx UI.h */\n/** @jsxFrag UI.F */\nexport const el = <><View/></>;',
        '/** @jsxRuntime classic */\nexport const el = <><View/></>;',
        '/** @jsxRuntime automatic */\nexport const el = <><View/></>;',
        '/** @jsxImportSource custom */\nexport const el = <><View/><X {...p} key="x"/></>;',
        'namespace M { export const React = factory; export const el = <View/>; }',
        'namespace M { export const UI = factory; /** @jsx UI.h */ export const el = <View/>; }',
        'export class C { field = <View/>; static value = <div/>; async m() { return <div>{await value}</div>; }',
        'export const el = <div>' + [...referenceSource.matchAll(/^\s*"(\w+)":\s*0x[0-9A-Fa-f]+,/gm)].map(match => `&${match[1]};`).join(' ') + '</div>;',
    ];
    const add = (name, text, options, extra = {}) => cases.push({name: `jsx-${name}`, text, file: '/source/input.tsx', jsx: true,
        eraseTypes: true, transformJsx: true, runtimeSyntax: true, lowerExpressions: true, options, ...extra});
    for (let i = 0; i < sources.length; i++) for (const jsx of ['react', 'react-jsx', 'react-jsxdev'])
        for (const target of ['es2015', 'es2018']) for (const map of [false, true])
            add(`syntax-${i}-${jsx}-${target}-${map}`, sources[i], {jsx, target}, {map});
    for (const jsx of ['preserve', 'react-native']) for (const map of [false, true])
        add(`preserve-${jsx}-${map}`, 'export const el = <View<number> x={value as number}><Child /></View>;', {jsx}, {map});
    for (const options of [{jsxFactory: 'UI.h', jsxFragmentFactory: 'UI.F'}, {reactNamespace: 'UI'}, {jsxImportSource: 'custom'},
        {jsxFactory: 'broken.()', jsxFragmentFactory: '!'}, {moduleDetection: 'legacy'}])
        for (const jsx of ['react', 'react-jsx', 'react-jsxdev'])
            add(`options-${cases.length}`, '"use client"; const value = <><View/><X {...props} key="x"/></>;', {jsx, ...options}, {map: true});
    for (const jsx of ['react', 'react-jsx', 'react-jsxdev'])
        add(`commonjs-${jsx}`, 'exports.value = <><View/><X {...props} key="x"/></>;', {jsx, allowJs: true}, {file: '/source/input.jsx', map: true});
    for (const text of [
        'import React from "./react"; import {View, unused} from "./react"; export const value = <><View/></>;',
        '/** @jsx React.h */\n/** @jsxFrag React.F */\nimport React from "./react"; import {View, unused} from "./react"; export const value = <><View/></>;',
        'import * as React from "./react"; export const value = <React.View/>;',
        'import type {View} from "./react"; export const value = <View/>;',
    ]) for (const jsx of ['react', 'react-jsx', 'react-jsxdev']) for (const verbatimModuleSyntax of [false,true])
        add(`imports-${cases.length}`, text, {jsx, verbatimModuleSyntax}, {elideImports:!verbatimModuleSyntax, map:true,
            files:{'/source/react.ts':'export const View=()=>null, unused=1; export function createElement() {} export const Fragment=1; export default {createElement, Fragment, h:createElement, F:Fragment};'}});
    const prelude = `const exports = {}; exports.loaded = true;
const View = "View";
function build(type, props, key) { props = {...props}; if (key === undefined) key = props.key; delete props.key; return {type, props, key}; }
function createElement(type, props, ...children) { props = {...props}; if (children.length) props.children = children.length === 1 ? children[0] : children; return build(type, props); }
const React = {Fragment: "Fragment", createElement};
function require(name) { return name.endsWith("runtime") ? {Fragment: "Fragment", jsx: build, jsxs: build, jsxDEV: build} : {createElement}; }
`;
    const probes = [
        ['basic', 'globalThis.result = <div title="&amp;" hidden data-id="x" xml:lang="en"/>;', {type:'div', props:{title:'&', hidden:true, 'data-id':'x', 'xml:lang':'en'}}],
        ['whitespace', 'globalThis.result = <div>  A\n B\n C  </div>;', {type:'div', props:{children:'  A B C  '}}],
        ['entities', 'const el = <div>&#xD800;&#x1F600; &amp; &unknown; &#0; &#x110000;</div>; globalThis.result = Array.from(el.props.children, c => c.codePointAt(0));', [0xD800,0x1F600,32,38,32,38,117,110,107,110,111,119,110,59,32,0,32,0xFFFD]],
        ['children', 'globalThis.result = <div>before {42}<span/> after</div>;', {type:'div', props:{children:['before ',42,{type:'span',props:{}},' after']}}],
        ['fragment', 'globalThis.result = <><View/><div>text</div></>;', {type:'Fragment',props:{children:[{type:'View',props:{}},{type:'div',props:{children:'text'}}]}}],
        ['spread-children', 'const children=[1,2,3]; globalThis.result = <View>{...children}</View>;', {type:'View',props:{children:[1,2,3]}}],
        ['empty-children', 'globalThis.result = <View>\n {/* nothing */}\n</View>;', {type:'View',props:{}}],
        ['children-override', 'globalThis.result = <div children="old">new</div>;', {type:'div',props:{children:'new'}}],
        ['spread-copy', 'const props={a:1}; const el=<View {...props}/>; el.props.a=2; globalThis.result=[props.a,el.props.a];', [1,2]],
        ['spread-order', 'const log=[]; const a={get x(){log.push("get");return 1;}}; const b=()=> (log.push("spread"),{y:2}); const el=<View {...a} {...b()} z={(log.push("value"),3)}/>; globalThis.result=[el.props,log];', jsx => [{x:1,y:2,z:3}, ['spread','value','get']]],
        ['key-before', 'const log=[]; const el=<View key={(log.push("key"),42)} x={(log.push("prop"),1)}/>; globalThis.result=[el,log];', jsx => [{type:'View',props:{x:1},key:42},jsx==='react'?['key','prop']:['prop','key']]],
        ['key-after', 'const log=[]; const props={get key(){log.push("get");return 1;}}; const el=<View {...props} key={(log.push("key"),42)}/>; globalThis.result=[el,log];', (jsx,target) => [{type:'View',props:{},key:42},target==='es2015'?['key','get']:['get','key']]],
        ['proto', 'const el=<View {...{__proto__:{inherited:1}, own:2}}/>; globalThis.result=[el.props.own,"inherited" in el.props];', [2,false]],
        ['namespace-factory', 'namespace M { export const React = {createElement(...args) { return args; }}; export const value=<div x={42}/>; } globalThis.result=M.value;', jsx => jsx==='react'?['div',{x:42}]:{type:'div',props:{x:42}}],
        ['async', 'async function f() { return <View>{await Promise.resolve(42)}</View>; } globalThis.result=f();', {type:'View',props:{children:42}}],
    ];
    for (const [name, text, expected] of probes) for (const jsx of ['react', 'react-jsx', 'react-jsxdev'])
        for (const target of ['es2015', 'es2018']) {
            let value = typeof expected === 'function' ? expected(jsx, target) : expected;
            if (name === 'spread-order' && target === 'es2018') value = [{x:1,y:2,z:3},['get','spread','value']];
            add(`execute-${name}-${jsx}-${target}`, prelude + text, {jsx, target, allowJs:true, moduleDetection:'legacy'},
                {file:'/source/input.jsx', map:true, runtimeControl:`globalThis.result=${JSON.stringify(value)};`});
        }
    return cases;
}
