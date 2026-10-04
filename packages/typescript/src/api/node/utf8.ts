import { isUtf8 } from "node:buffer";

const decoder = new TextDecoder("utf-8", { ignoreBOM: true });
const replacement = 0xfffd;

/** Decodes one WTF-8 scalar; malformed input consumes one byte, as in the compiler. */
export function decodeUtf8(bytes: Uint8Array, position: number): readonly [codePoint: number, width: number] {
    const first = bytes[position];
    if (first < 0x80) return [first, 1];
    const width = first >= 0xc2 && first <= 0xdf ? 2 : first >= 0xe0 && first <= 0xef ? 3 : first >= 0xf0 && first <= 0xf4 ? 4 : 0;
    if (!width || position + width > bytes.length) return [replacement, 1];
    const second = bytes[position + 1];
    if (second < 0x80 || second > 0xbf || first === 0xe0 && second < 0xa0 || first === 0xf0 && second < 0x90 || first === 0xf4 && second > 0x8f) return [replacement, 1];
    let point = first & (0x7f >> width);
    for (let index = 1; index < width; index++) {
        const next = bytes[position + index];
        if (next < 0x80 || next > 0xbf) return [replacement, 1];
        point = point << 6 | next & 0x3f;
    }
    return [point, width];
}

/** Preserves BOMs and lone surrogates, and does not coalesce malformed bytes. */
export function decodeWtf8(bytes: Uint8Array): string {
    if (isUtf8(bytes)) return decoder.decode(bytes);
    const parts: string[] = [];
    let start = 0;
    for (let index = 0; index < bytes.length;) {
        if (bytes[index] < 0x80) {
            index++;
            continue;
        }
        const [point, width] = decodeUtf8(bytes, index);
        if (width === 1 || point >= 0xd800 && point <= 0xdfff) {
            if (start < index) parts.push(decoder.decode(bytes.subarray(start, index)));
            parts.push(String.fromCodePoint(point));
            start = index + width;
        }
        index += width;
    }
    if (start < bytes.length) parts.push(decoder.decode(bytes.subarray(start)));
    return parts.join("");
}
