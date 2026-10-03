import assert from "node:assert/strict";
import { readFile, mkdir, writeFile } from "node:fs/promises";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { json, output, root, sha256 } from "./common.mjs";

const option = (key, fallback) => process.argv.includes(key) ? process.argv[process.argv.indexOf(key) + 1] : fallback;
const directory = path.resolve(option("--directory", path.join(output, "phase8-validation/client-positions")));
const client = path.resolve(option("--client", path.join(output, "phase8-client/packages/typescript")));
const compiler = path.resolve(option("--managed-directory", path.join(output, "phase8-build/bin/TypeScript.CommandLine/release")));
const candidate = path.resolve(option("--executable", path.join(compiler, "tsgo-cs.exe")));
const oracle = path.resolve(option("--oracle", path.join(output, "phase8-validation/cli-host-verified/oracle.exe")));
await mkdir(directory, { recursive: true });
process.env.DOTNET_ROOT ??= "D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64";
const files = [
    "// 😀\nexport const café = '世界';\nexport const 名 = café;\n",
    "/* 😀 中文 */\r\nexport function 名前(値: number) { return 値 + 1; }\r\n名前(2);",
    "// é 世\nexport const astral = '😀';\nexport const accents = 'é世界';\n",
    "export type 形<T> = { [鍵 in keyof T]: T[鍵] };\nexport const 値 = { 名: '😀' };\n",
    "export const a = `😀 ${1}`;\u2028export const b = '世';\u2029export const c = a;",
    "/** 名前 😀\n * @param 値 中文\n */\nexport function 関数(値: string) { return 値; }\n",
];
const load = (base, file) => import(pathToFileURL(path.join(base, "dist", file)).href);
const results = [], failures = [];
for (const mode of ["async", "sync"]) {
    const captures = [];
    for (const [label, base, executable] of [["reference", path.join(root, "packages/typescript"), oracle], ["candidate", client, candidate]]) {
        const { API } = await load(base, `api/${mode}/api.js`), ast = await load(base, "ast/index.js"), { createVirtualFileSystem } = await load(base, "api/fs.js");
        const api = new API({ cwd: root, tsserverPath: executable, fs: createVirtualFileSystem({
            "/p/tsconfig.json": JSON.stringify({ compilerOptions: { noLib: true, target: "esnext" }, files: files.map((_, index) => `f${index}.ts`) }),
            ...Object.fromEntries(files.map((text, index) => [`/p/f${index}.ts`, text])),
        }) });
        try {
            const snapshot = await api.createSnapshot({ openProject: "/p/tsconfig.json" });
            const program = snapshot.getConfiguredProject("/p/tsconfig.json").program;
            const records = [];
            for (let index = 0; index < files.length; index++) {
                const text = files[index], source = await program.getSourceFile(`/p/f${index}.ts`);
                assert.equal(source.text, text);
                const toByte = position => label === "candidate" ? position : new TextEncoder().encode(text.slice(0, position)).length;
                const nodes = [];
                const visit = node => {
                    nodes.push({ kind: node.kind, pos: toByte(node.pos), end: toByte(node.end), start: toByte(node.getStart(source)), text: node.getText(source), fullText: node.getFullText(source) });
                    node.forEachChild(child => { visit(child); });
                };
                visit(source);
                const tokens = [], scanner = ast.createScanner(false, undefined, text);
                for (;;) {
                    const kind = scanner.scan();
                    tokens.push({ kind, pos: toByte(scanner.getTokenFullStart()), start: toByte(scanner.getTokenStart()), end: toByte(scanner.getTokenEnd()), text: scanner.getTokenText() });
                    if (kind === ast.SyntaxKind.EndOfFile) break;
                }
                const navigation = [];
                for (let position = 0; position < text.length; position++) {
                    const code = text.charCodeAt(position);
                    if (code >= 0xdc00 && code <= 0xdfff && position > 0 && text.charCodeAt(position - 1) >= 0xd800 && text.charCodeAt(position - 1) <= 0xdbff) continue;
                    const byte = new TextEncoder().encode(text.slice(0, position)).length;
                    const token = ast.getTokenAtPosition(source, label === "candidate" ? byte : position);
                    navigation.push({ position: byte, kind: token.kind, start: toByte(token.getStart(source)), end: toByte(token.end), text: token.getText(source) });
                }
                const lineStarts = source.getLineStarts().map(toByte);
                for (const position of source.getLineStarts()) {
                    const line = source.getLineAndCharacterOfPosition(position);
                    assert.equal(line.character, 0);
                }
                const printed = await api.printer.printFile(source, { neverAsciiEscape: true });
                records.push({ nodes, tokens, navigation, lineStarts, printed });
            }
            if (label === "candidate") {
                const text = "// \ud800 \udfff\nexport const lone = '\ud800';\nexport const astral = '😀';\n";
                const source = await api.createSourceFile("/lone.ts", text);
                assert.equal(source.text, text); assert.equal(source.getFullText(), text);
                assert.equal(source.end, ast.byteLength(text));
                const first = source.statements[0];
                assert.equal(first.getText(), "export const lone = '\ud800';");
                assert.equal(first.getStart(), ast.byteLength("// \ud800 \udfff\n"));
                const rawApi = new API({ cwd: root, tsserverPath: executable });
                try {
                    for (const [index, bytes] of [[0xff], [0xe2, 0x82], [0xed, 0xa0, 0x80, 0xed, 0xb0, 0x80]].entries()) {
                        const file = path.join(directory, `${mode}-raw-${index}.ts`);
                        const declaration = "export const café = '世界';";
                        const prefix = Buffer.from([47, 47, 32, ...bytes, 10]);
                        const input = Buffer.concat([prefix, Buffer.from(declaration + "\n")]);
                        await writeFile(file, input);
                        const raw = await rawApi.createSourceFileFromFile(file);
                        assert.equal(raw.end, input.length); assert.equal(raw.getLineStarts()[1], prefix.length);
                        assert.equal(raw.statements[0].getStart(), prefix.length);
                        assert.equal(raw.statements[0].getText(), declaration);
                    }
                } finally { await rawApi.close(); }
            }
            captures.push({ label, records });
            await json(path.join(directory, `${label}-${mode}.json`), records);
        }
        finally { await api.close(); }
    }
    for (let index = 0; index < files.length; index++) {
        try { assert.deepEqual(captures[1].records[index], captures[0].records[index]); }
        catch { failures.push({ mode, index, reference: captures[0].records[index], candidate: captures[1].records[index] }); }
    }
    const { API } = await load(client, `api/${mode}/api.js`);
    const incompatible = new API({ cwd: root, tsserverPath: oracle });
    try {
        await assert.rejects(async () => await incompatible.createSourceFile("/wrong-protocol.ts", "export const café = '😀';"),
            /requires AST protocol version 9/, "The byte-coordinate client must reject the original version-8 server");
    } finally { await incompatible.close(); }
    results.push({ mode, files: files.length, loneSurrogateCases: 1, malformedByteCases: 3, incompatibleProtocolRejected: true,
        nodes: captures[1].records.reduce((sum, item) => sum + item.nodes.length, 0),
        tokens: captures[1].records.reduce((sum, item) => sum + item.tokens.length, 0), navigationQueries: captures[1].records.reduce((sum, item) => sum + item.navigation.length, 0) });
}
await json(path.join(directory, "differences.json"), failures);
const summary = { results, differences: failures.length, inputsSha256: sha256(JSON.stringify(files)),
    compilerSha256: sha256(await readFile(path.join(path.dirname(candidate), "TypeScript.Compiler.dll"))),
    contract: "Original version-8 Go client compared to the generated version-9 C# client; reference positions converted to bytes, text and navigation results otherwise exact" };
await json(path.join(directory, "summary.json"), summary); console.log(JSON.stringify(summary));
assert.equal(failures.length, 0);
