import assert from "node:assert/strict";
import { copyFile, cp, mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { json, output, root, run, sha256 } from "./common.mjs";

export async function generateClient(directory = path.join(output, "client")) {
    directory = path.resolve(directory);
    const client = path.join(directory, "packages/typescript"), generators = path.join(directory, "tools/scripts/tsc");
    await mkdir(client, { recursive: true }); await mkdir(generators, { recursive: true });
    await mkdir(path.join(directory, "tsc/internal/api/encoder"), { recursive: true });
    for (const name of ["src", "lib", "bin", "vendor", "scripts", "package.json", "tsconfig.json", "tsconfig.base.json"])
        await cp(path.join(root, "packages/typescript", name), path.join(client, name), { recursive: true });
    for (const name of ["generate-encoder.ts", "generate-ts-ast.ts", "schema.ts", "ast.json", "package.json"])
        await copyFile(path.join(root, "tools/scripts/tsc", name), path.join(generators, name));
    await copyFile(path.join(root, ".dprint.jsonc"), path.join(directory, ".dprint.jsonc"));
    const changes = [];
    async function adapt(file, transform) {
        const original = await readFile(file, "utf8"), changed = transform(original);
        assert.notEqual(changed, original, file); await writeFile(file, changed);
        changes.push({ file: path.relative(directory, file).replaceAll("\\", "/"), originalSha256: sha256(original), generatedSha256: sha256(changed) });
    }
    function replace(text, before, after, count = 1) {
        assert.equal(text.split(before).length - 1, count, `Client source contract changed: ${before}`);
        return text.replaceAll(before, after);
    }
    // Adapt the original generator templates before generating either class.
    // Regeneration therefore preserves byte-aware text access in both remote and factory nodes.
    for (const name of ["generate-encoder.ts", "generate-ts-ast.ts"]) await adapt(path.join(generators, name), text => {
        text = replace(text, "return (sourceFile ?? this.getSourceFile()).text.substring(this.pos, this.end);",
            "return sliceSourceText(sourceFile ?? this.getSourceFile(), this.pos, this.end);");
        text = replace(text, "return sourceFile.text.substring(this.getStart(sourceFile), this.end);",
            "return sliceSourceText(sourceFile, this.getStart(sourceFile), this.end);");
        return name === "generate-encoder.ts"
            ? replace(text, "w.write(`    getTokenPosOfNode,`);", "w.write(`    getTokenPosOfNode,`);\n    w.write(`    sliceSourceText,`);")
            : replace(text, 'out.push(`import { getChildren, getFirstToken, getLastToken, getTokenPosOfNode } from "./astnav.ts";`);',
                'out.push(`import { getChildren, getFirstToken, getLastToken, getTokenPosOfNode } from "./astnav.ts";`);\n    out.push(`import { sliceSourceText } from "./positions.ts";`);');
    });
    await adapt(path.join(client, "src/api/node/protocol.ts"), text => replace(text, "PROTOCOL_VERSION = 8", "PROTOCOL_VERSION = 9"));
    await adapt(path.join(client, "src/api/node/node.ts"), text => {
        text = 'import { computeLineStarts as computeUtf16LineStarts } from "../../ast/scanner.utf16.ts";\n' + text;
        text = replace(text, "    computeLineStarts,", "    getSourcePositions,");
        text = replace(text, "    type Node,", "    type Node,\n    type SourceFile,");
        text = replace(text, "    HEADER_OFFSET_EXTENDED_DATA,", "    HEADER_SIZE,\n    PROTOCOL_VERSION,\n    HEADER_OFFSET_EXTENDED_DATA,");
        text = replace(text, "const view = new DataView(data.buffer, data.byteOffset, data.byteLength);",
            'if (data.byteLength < HEADER_SIZE || data[3] !== PROTOCOL_VERSION) throw new Error("The C# client requires AST protocol version 9 with UTF-8 byte positions.");\n        const view = new DataView(data.buffer, data.byteOffset, data.byteLength);');
        text = replace(text, 'return this._lineStarts ??= computeLineStarts(this.text ?? "");',
            'return this._lineStarts ??= computeUtf16LineStarts(this.text).map(position => getSourcePositions(this as unknown as SourceFile).toUtf8(position));');
        text = replace(text, "    get text(): string {", `    /** @internal */
    getSourceBytes(): Uint8Array {
        const index = this.view.getUint32(this.extendedDataOffset + sourceFileExtendedDataOffsets.Text, true);
        const start = this.view.getUint32(this._offsetStringTableOffsets + index * 4, true);
        const end = this.view.getUint32(this._offsetStringTableOffsets + (index + 1) * 4, true);
        return new Uint8Array(this.view.buffer, this.view.byteOffset + this._offsetStringTable + start, end - start);
    }

    get text(): string {`);
        return text;
    });
    await copyFile(path.join(root, "csharp/client/utf8.ts"), path.join(client, "src/api/node/utf8.ts"));
    await adapt(path.join(client, "src/api/node/wtf8.ts"), text => {
        text = 'import { decodeWtf8 } from "./utf8.ts";\n' + text;
        const start = text.indexOf("export class Wtf8Decoder extends TextDecoder {"); assert.ok(start > 0);
        return text.slice(0, start) + `export class Wtf8Decoder extends TextDecoder {
    override decode(input?: DecodeInput): string {
        return input == null ? "" : decodeWtf8(toUint8Array(input));
    }
}
`;
    });
    await adapt(path.join(client, "src/api/node/encoder.ts"), text => {
        text = 'import { getSourceBytes } from "../../ast/positions.ts";\n' + text;
        text = replace(text, "add(text: string): number {", "add(text: string, bytes?: Uint8Array): number {");
        text = replace(text, "const encoded = encodeWtf8(text);", "const encoded = bytes ?? encodeWtf8(text);");
        return replace(text, "const textIndex = strs.add(sf.text);", "const textIndex = strs.add(sf.text, getSourceBytes(sf));");
    });
    for (const file of ["async/api.ts", "sync/api.ts", "proto.generated.ts"]) await adapt(path.join(client, "src/api", file), text =>
        text.replaceAll("UTF-16 code-unit offset", "UTF-8/WTF-8 byte offset").replaceAll("UTF-16 character position", "UTF-8 byte position"));
    await copyFile(path.join(client, "src/ast/scanner.ts"), path.join(client, "src/ast/scanner.utf16.ts"));
    for (const name of ["positions.ts", "scanner.ts"]) await copyFile(path.join(root, "csharp/client", name), path.join(client, "src/ast", name));
    await adapt(path.join(client, "src/ast/index.ts"), text => text + '\nexport * from "./positions.ts";\n');
    await adapt(path.join(client, "src/ast/astnav.ts"), text => {
        text = 'import { getSourcePositions } from "./positions.ts";\n' + text;
        text = replace(text, "/*stopAtComments*/ true);", "/*stopAtComments*/ true, false, getSourcePositions(sourceFile));");
        text = replace(text, "/*inJSDoc*/ !!(node.flags & NodeFlags.JSDoc));", "/*inJSDoc*/ !!(node.flags & NodeFlags.JSDoc), getSourcePositions(sourceFile));");
        return replace(text, "createScanner(/*skipTrivia*/ true, sourceFile.languageVariant, sourceFile.text)",
            "createScanner(/*skipTrivia*/ true, sourceFile.languageVariant, sourceFile.text, undefined, undefined, getSourcePositions(sourceFile))", 2);
    });
    await adapt(path.join(client, "src/api/diagnosticFormatter.ts"), text => {
        text = 'import { utf16Offset } from "../ast/positions.ts";\n' + text;
        return replace(text, "const { startPosition, endPosition, sourceLines } = diagnostic;",
            "const { sourceLines } = diagnostic;\n    const displayPosition = (position: Diagnostic[\"startPosition\"]) => {\n        const line = sourceLines?.find(line => line.line === position?.line);\n        return position && line ? { ...position, character: utf16Offset(line.text, position.character) } : position;\n    };\n    const startPosition = displayPosition(diagnostic.startPosition), endPosition = displayPosition(diagnostic.endPosition);");
    });
    const environment = { ...process.env, PATH: path.join(root, "node_modules/.bin") + path.delimiter + process.env.PATH };
    for (const generator of ["generate-ts-ast.ts", "generate-encoder.ts"])
        await run(process.execPath, ["--conditions", "@typescript/source", path.join(generators, generator)], { cwd: directory, env: environment });
    await run(process.execPath, [path.join(root, "node_modules/typescript/lib/tsc.js"), "-b", path.join(client, "tsconfig.json"), "--force"]);
    await json(path.join(directory, "client-generation.json"), { protocolVersion: 9, coordinateContract: "UTF-8/WTF-8 byte offsets", changes,
        overlays: await Promise.all(["positions.ts", "scanner.ts", "utf8.ts"].map(async name => ({ file: "csharp/client/" + name, sha256: sha256(await readFile(path.join(root, "csharp/client", name))) }))) });
    return client;
}

if (path.resolve(process.argv[1] ?? "") === fileURLToPath(import.meta.url)) {
    const index = process.argv.indexOf("--directory");
    console.log(await generateClient(index < 0 ? undefined : process.argv[index + 1]));
}
