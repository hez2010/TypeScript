export function programEmitCases() {
    const cases = [];
    const add = (name, input) => cases.push({name, text: 'export const answer: number = 42;', ...input});
    const modes = [
        ['default', {}], ['declaration', {declaration: true}], ['declaration-only', {declaration: true, emitDeclarationOnly: true}],
        ['composite', {composite: true}], ['map', {sourceMap: true}], ['map-inline', {inlineSourceMap: true}],
        ['map-sources', {sourceMap: true, inlineSources: true}], ['maps-both', {declaration: true, declarationMap: true, sourceMap: true}],
        ['declaration-map-inline', {declaration: true, declarationMap: true, inlineSourceMap: true, inlineSources: true}],
        ['no-emit', {noEmit: true}], ['no-emit-error', {noEmitOnError: true}], ['map-without-dts', {declarationMap: true}],
    ];
    for (const [name, options] of modes) for (const only of [0, 1, 2, 3]) for (const force of [false, true])
        add(`${name}-${only}-${force}`, {options, only, force});
    for (const extension of ['ts', 'tsx', 'mts', 'cts', 'js', 'jsx', 'mjs', 'cjs', 'json'])
        for (const jsx of ['preserve', 'react-jsx']) for (const outDir of ['/out', '/source'])
            add(`extension-${extension}-${jsx}-${outDir}`, {file: `/source/input.${extension}`, text: extension === 'json' ? '{"answer":42}'
                : extension.includes('x') ? 'export const value = <div>hi</div>;' : 'export const answer = 42;',
                options: {allowJs: true, resolveJsonModule: true, declaration: true, declarationMap: true, sourceMap: true, jsx, outDir}});
    for (const sourceRoot of ['', 'sources', '/sources', 'https://example.com/source'])
        for (const mapRoot of ['', 'maps', '/maps', 'https://example.com/maps'])
            add(`map-paths-${sourceRoot}-${mapRoot}`, {file: '/source/sub/日本 x.ts', text: 'export const 日本 = "🌲";',
                options: {sourceRoot, mapRoot, sourceMap: true, declaration: true, declarationMap: true, declarationDir: '/types'}});
    for (const newLine of ['lf', 'crlf']) for (const emitBOM of [false, true]) for (const sourceMap of [false, true])
        add(`text-${newLine}-${emitBOM}-${sourceMap}`, {text: '/** hello */\nexport const π = "🍁";', options: {newLine, emitBOM, sourceMap, declaration: true, declarationMap: true}});
    for (const only of [0, 1, 2, 3]) for (const force of [false, true])
        add(`invalid-declaration-${only}-${force}`, {text: 'export const data = {value: 1};', options: {declaration: true, declarationMap: true, isolatedDeclarations: true}, only, force});
    for (const target of ['es2015', 'es2017', 'es2020', 'es2022', 'esnext']) for (const module of ['commonjs', 'esnext', 'preserve'])
        add(`pipeline-${target}-${module}`, {text: 'export enum Color {Red = 1, Blue} export namespace N {export const x = Color.Blue;} export class C {#x = 1; constructor(public y = 2) {} async value() { return this.#x + this.y; }} export async function f(o: any) { const {a, ...b} = o ?? {}; return {...b, a: a?.x ?? N.x}; }',
            options: {target, module, declaration: true, declarationMap: true, sourceMap: true}});
    const files = {'/source/sub/other.ts': 'export const x = 1;', '/source/types.d.ts': 'declare const other: number;'};
    add('multi-file', {text: 'export {x} from "./sub/other";', files, options: {declaration: true, sourceMap: true, declarationMap: true}});
    add('targeted-file', {text: 'export {x} from "./sub/other";', files, targets: ['/source/input.ts'], options: {declaration: true}});
    add('targeted-noemit', {targets: ['/source/input.ts'], options: {noEmit: true}});
    add('targeted-none', {targets: [], options: {declaration: true}});
    add('empty-source', {text: '', options: {declaration: true, sourceMap: true, declarationMap: true}});
    add('declaration-input', {file: '/source/input.d.ts', text: 'export declare const answer: number;', only: 3});
    add('out-root', {file: '/source/sub/input.ts', options: {rootDir: '/source/sub', outDir: 'dist', declarationDir: 'types', declaration: true}});
    add('root-outside', {file: '/source/input.ts', options: {rootDir: '/source/sub', outDir: '/out', declaration: true}});
    add('output-collision', {file: '/source/input.ts', files: {'/source/input.tsx': 'export const x = 1;'}, roots: ['/source/input.ts', '/source/input.tsx'], options: {declaration: true}});
    add('output-overwrite', {file: '/source/input.js', text: 'export const x = 1;', options: {outDir: '/source', allowJs: true, declaration: true}});
    add('output-case', {file: '/Source/Input.ts', cwd: '/Source', caseInsensitive: true, options: {outDir: '/out', declaration: true, sourceMap: true}});
    add('external-library', {text: 'export {value} from "pkg";', files: {'/source/node_modules/pkg/index.ts': 'export const value = 1;'}, options: {declaration: true}});
    for (const only of [0, 1, 2]) for (const force of [false, true])
        add(`noemit-js-${only}-${force}`, {file: '/source/input.js', text: 'export const x = 1;', only, force, options: {allowJs: true, noEmitForJsFiles: true, declaration: true}});
    for (const suffix of ['js', 'js.map', 'd.ts', 'd.ts.map'])
        add(`write-failure-${suffix}`, {failWrite: `/out/input.${suffix}`, options: {sourceMap: true, declaration: true, declarationMap: true}});
    add('skip-declaration-write', {skipWrite: '/out/input.d.ts', options: {declaration: true, declarationMap: true}});
    add('noemit-error-parse', {text: 'export const = ;', options: {noEmitOnError: true}});
    add('noemit-error-semantic', {text: 'export let n: number = "bad";', options: {noEmitOnError: true}});
    const invalidOptions = [
        {moduleResolution: 'node10'}, {baseUrl: '/base'}, {strictNullChecks: false, strictPropertyInitialization: true},
        {strictNullChecks: false, exactOptionalPropertyTypes: true}, {sourceMap: true, inlineSourceMap: true},
        {inlineSourceMap: true, mapRoot: '/maps'}, {inlineSources: true}, {sourceRoot: '/sources'}, {mapRoot: '/maps'},
        {declarationMap: true}, {emitDeclarationOnly: true}, {isolatedDeclarations: true},
        {isolatedDeclarations: true, declaration: true, allowJs: true}, {composite: true, declaration: false},
        {composite: true, incremental: false}, {checkJs: true, allowJs: false}, {emitDecoratorMetadata: true},
        {isolatedModules: true, preserveConstEnums: false}, {verbatimModuleSyntax: true, preserveConstEnums: false},
        {jsxFactory: 'Factory.create', reactNamespace: 'Old'}, {jsx: 'react-jsx', jsxFactory: 'Factory.create'},
        {jsxFactory: 'bad factory'}, {reactNamespace: 'not.valid'}, {jsxFragmentFactory: 'Fragment'},
        {jsx: 'react-jsxdev', jsxFragmentFactory: 'not valid'}, {jsx: 'react-jsx', reactNamespace: 'Old'},
        {jsx: 'react', jsxImportSource: 'custom'}, {moduleResolution: 'classic', resolvePackageJsonExports: true},
        {moduleResolution: 'classic', resolvePackageJsonImports: true}, {moduleResolution: 'classic', customConditions: []},
        {module: 'node16', moduleResolution: 'bundler'}, {module: 'esnext', moduleResolution: 'nodenext'},
        {module: 'node20', moduleResolution: 'classic'}, {module: 'none', moduleResolution: 'bundler'},
        {allowImportingTsExtensions: true}, {lib: ['esnext']},
    ];
    for (const [index, options] of invalidOptions.entries())
        add(`config-option-diagnostic-${index}`, {config: '/* 日本語 config */\n' + JSON.stringify({compilerOptions:
            {noLib: true, module: 'esnext', moduleResolution: 'bundler', outDir: '/out', noEmitOnError: true, ...options},
            files: ['/source/input.ts']}, null, 2)});
    return cases;
}
