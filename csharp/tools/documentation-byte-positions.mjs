// Historical documentation evidence records UTF-16 offsets. Translate only its
// numeric source coordinates; current compiler and Go results use byte offsets.
export function documentationBytePositions(input, output) {
    const text = Buffer.from(input.text, "base64").toString("utf8");
    const byte = position => position < 0 ? position : Buffer.byteLength(text.slice(0, position));
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
