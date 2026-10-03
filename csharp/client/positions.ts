import type { SourceFile } from "./ast.ts";
import { isUtf8 } from "node:buffer";
import { decodeUtf8, decodeWtf8 } from "../api/node/utf8.ts";

/** Maps JavaScript string offsets to the C# API's UTF-8/WTF-8 byte offsets. */
export class Utf8Positions {
    readonly text: string;
    private readonly ends: number[] = [];
    private readonly deltas: number[] = [];

    constructor(text: string, bytes?: Uint8Array) {
        this.text = text;
        if (bytes && !isUtf8(bytes)) {
            let characters = 0, delta = 0;
            for (let position = 0; position < bytes.length;) {
                if (bytes[position] < 0x80) { position++; characters++; continue; }
                const [point, width] = decodeUtf8(bytes, position);
                position += width; characters += point >= 0x10000 ? 2 : 1;
                if (position - characters !== delta) {
                    delta = position - characters;
                    this.ends.push(position); this.deltas.push(delta);
                }
            }
            return;
        }
        let delta = 0;
        for (let index = 0; index < text.length; index++) {
            const code = text.charCodeAt(index);
            if (code < 0x80) continue;
            let width = code < 0x800 ? 2 : 3;
            let units = 1;
            if (code >= 0xd800 && code <= 0xdbff && index + 1 < text.length) {
                const next = text.charCodeAt(index + 1);
                if (next >= 0xdc00 && next <= 0xdfff) { width = 4; units = 2; index++; }
            }
            delta += width - units;
            this.ends.push(index + 1 + delta);
            this.deltas.push(delta);
        }
    }

    toUtf16(position: number): number {
        let low = 0, high = this.ends.length;
        while (low < high) {
            const middle = (low + high) >>> 1;
            if (this.ends[middle] <= position) low = middle + 1; else high = middle;
        }
        return position - (low ? this.deltas[low - 1] : 0);
    }

    toUtf8(position: number): number {
        let low = 0, high = this.ends.length;
        while (low < high) {
            const middle = (low + high) >>> 1;
            if (this.ends[middle] - this.deltas[middle] <= position) low = middle + 1; else high = middle;
        }
        return position + (low ? this.deltas[low - 1] : 0);
    }
}

const sourcePositions = new WeakMap<SourceFile, Utf8Positions>();

export function getSourceBytes(sourceFile: SourceFile): Uint8Array | undefined {
    return (sourceFile as SourceFile & { getSourceBytes?(): Uint8Array; }).getSourceBytes?.();
}

export function getSourcePositions(sourceFile: SourceFile): Utf8Positions {
    let positions = sourcePositions.get(sourceFile);
    if (!positions || positions.text !== sourceFile.text) {
        positions = new Utf8Positions(sourceFile.text, getSourceBytes(sourceFile));
        sourcePositions.set(sourceFile, positions);
    }
    return positions;
}

/** Converts an index returned by JavaScript string operations to an API position. */
export function utf8Offset(text: string, position: number): number {
    return new Utf8Positions(text).toUtf8(position);
}

/** Converts an API position to an index for JavaScript string operations. */
export function utf16Offset(text: string, position: number): number {
    return new Utf8Positions(text).toUtf16(position);
}

export function byteLength(text: string): number {
    return new Utf8Positions(text).toUtf8(text.length);
}

export function sliceSourceText(sourceFile: SourceFile, start: number, end: number): string {
    const bytes = getSourceBytes(sourceFile);
    if (bytes) return decodeWtf8(bytes.subarray(Math.max(0, start), Math.max(0, end)));
    const positions = getSourcePositions(sourceFile);
    return sourceFile.text.substring(positions.toUtf16(start), positions.toUtf16(end));
}
