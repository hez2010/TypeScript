export function transpileCases() {
    const cases = [];
    const add = (name, text, options = {}, extra = {}) => {
        for (const declaration of [false, true]) for (const report of [false, true])
            cases.push({name: `authored/${name}/${declaration ? 'dts' : 'js'}/${report}`, text, options, declaration, report, ...extra});
    };
    add('defaults', 'export const value: number = 1;');
    add('no-options', 'const value = 1;');
    add('errors', 'export const a: =; function f( {');
    add('isolated-error', 'export const value = run(); export function f() { return value; }');
    add('ignored-options', 'export const value: number = 1;', {incremental: true, declaration: false, emitDeclarationOnly: true, noEmit: true,
        lib: ['dom'], outFile: '/all.js', composite: true, tsBuildInfoFile: '/build.info', paths: {'*': ['./missing/*']}, rootDirs: ['/missing'],
        types: ['missing'], allowImportingTsExtensions: true, noEmitOnError: true, declarationDir: '/types'});
    add('unresolved', '/// <reference path="./none.ts" />\n/// <reference types="missing" />\n/// <reference lib="dom" />\nimport {value} from "missing"; export const x: typeof value = value;', {libReplacement: true});
    add('suppress-collisions', '/** @type {number} */ export const value = 1;', {allowJs: true}, {file: 'module.js'});
    for (const target of ['es2015', 'es2017', 'es2020', 'es2022', 'esnext'])
        for (const module of ['commonjs', 'esnext', 'nodenext', 'preserve'])
            add(`pipeline/${target}/${module}`, 'export class C { #value = 1; accessor x = 2; async f(x?: number): Promise<number> { return x ?? this.#value; } }', {target, module});
    for (const file of ['module.mts', 'module.cts', 'folder/file.ts', '/abs/name.ts', 'module.vue', 'extensionless', 'module.jsx'])
        add(`filename/${file}`, 'export const value: number = 1;', {jsx: 'preserve', module: 'nodenext', allowJs: true, sourceMap: true, declarationMap: true}, {file});
    for (const jsx of ['preserve', 'react', 'react-jsx', 'react-jsxdev'])
        add(`jsx/${jsx}`, 'export const value: JSX.Element = <><View x="a" /></>;', {jsx, sourceMap: true, declarationMap: true});
    for (const option of [{sourceMap: true}, {inlineSourceMap: true}, {declarationMap: true}, {emitBOM: true, newLine: 'crlf'},
        {sourceMap: true, declarationMap: true, inlineSources: true, sourceRoot: 'https://test/源', mapRoot: '/maps'}])
        add(`map/${JSON.stringify(option)}`, 'export const value = "<>&\u2028\u2029😀";\n// trailing\n', option, {file: '源/😀 +#.ts'});
    add('verbatim', 'import {T} from "x"; export const value: T = 1;', {verbatimModuleSyntax: true, isolatedModules: false});
    add('const-enum', 'const enum E { A = 1 } export const x: number = E.A;');
    add('strip-internal', '/** @internal */ export const hidden = 1; export const visible = 2;', {stripInternal: true});
    for (const options of [
        {target: 'es5'}, {module: 'amd'}, {module: 'system'}, {module: 'umd'}, {moduleResolution: 'classic'},
        {moduleResolution: 'node10'}, {alwaysStrict: false}, {esModuleInterop: false}, {allowSyntheticDefaultImports: false},
        {downlevelIteration: false}, {baseUrl: '/base'}, {strictNullChecks: false, strictPropertyInitialization: true},
        {strictNullChecks: false, exactOptionalPropertyTypes: true}, {sourceMap: true, inlineSourceMap: true},
        {inlineSourceMap: true, mapRoot: '/maps'}, {inlineSources: true}, {sourceRoot: '/sources'}, {mapRoot: '/maps'},
        {checkJs: true, allowJs: false}, {emitDecoratorMetadata: true}, {preserveConstEnums: false},
        {jsxFactory: 'Factory.create', reactNamespace: 'Old'}, {jsx: 'react-jsx', jsxFactory: 'Factory.create'},
        {jsxFactory: 'bad factory'}, {reactNamespace: 'not.valid'}, {jsxFragmentFactory: 'Fragment'},
        {jsx: 'react-jsxdev', jsxFragmentFactory: 'not valid'}, {jsx: 'react-jsx', reactNamespace: 'Old'},
        {jsx: 'react', jsxImportSource: 'custom'}, {moduleResolution: 'classic', resolvePackageJsonExports: true},
        {moduleResolution: 'classic', resolvePackageJsonImports: true}, {moduleResolution: 'classic', customConditions: []},
        {module: 'node16', moduleResolution: 'bundler'}, {module: 'esnext', moduleResolution: 'nodenext'},
        {module: 'node20', moduleResolution: 'classic'}, {module: 'none', moduleResolution: 'bundler'},
        {moduleResolution: 'classic', strictNullChecks: false, strictPropertyInitialization: true, exactOptionalPropertyTypes: true,
            sourceMap: true, inlineSourceMap: true, mapRoot: '/maps', preserveConstEnums: false}
    ]) add(`options/${JSON.stringify(options)}`, 'export const value: number = 1;', options);
    return cases;
}
