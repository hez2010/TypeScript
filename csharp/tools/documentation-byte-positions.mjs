// Historical documentation evidence records UTF-16 offsets. Translate only its
// numeric source coordinates; current compiler and Go results use byte offsets.
export function documentationBytePositions(input, output) {
    const text = Buffer.from(input.text, "base64").toString("utf8");
    const offsets = new Uint32Array(text.length + 1);
    for (let i = 0, offset = 0; i < text.length;) {
        const unit = text.charCodeAt(i++);
        offsets[i] = offset + (unit < 0x80 ? 1 : unit < 0x800 ? 2 : 3);
        if (unit >= 0xd800 && unit <= 0xdbff && text.charCodeAt(i) >= 0xdc00 && text.charCodeAt(i) <= 0xdfff)
            offsets[++i] = offset + 4;
        offset = offsets[i];
    }
    const byte = position => position < 0 ? position : offsets[Math.min(position, text.length)];
    const result = structuredClone(output);
    for (const host of result[0]) {
        host[1] = byte(host[1]);
        host[2] = byte(host[2]);
        for (const rows of host[3]) {
            for (const row of rows) {
                row[1] = byte(row[1]);
                row[2] = byte(row[2]);
                for (const list of Object.values(row[6])) {
                    if (!list) continue;
                    list[1] = byte(list[1]);
                    list[2] = byte(list[2]);
                }
            }
        }
    }
    for (const diagnostic of result[1]) {
        const start = byte(diagnostic[1]);
        diagnostic[2] = byte(diagnostic[1] + diagnostic[2]) - start;
        diagnostic[1] = start;
    }
    return result;
}
