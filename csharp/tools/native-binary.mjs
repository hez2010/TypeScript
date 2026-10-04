import assert from "node:assert/strict";

export function inspectNativeBinary(bytes, target) {
    if (target.os === "win32") {
        assert.equal(bytes.readUInt16LE(0), 0x5a4d, "PE DOS header");
        const pe = bytes.readUInt32LE(0x3c), optional = pe + 24;
        assert.equal(bytes.readUInt32LE(pe), 0x4550);
        assert.equal(bytes.readUInt16LE(pe + 4), target.arch === "arm64" ? 0xaa64 : 0x8664);
        assert.equal(bytes.readUInt16LE(optional), 0x20b);
        assert.equal(bytes.readUInt32LE(optional + 112 + 14 * 8), 0, "Native executable has no CLR descriptor");
        return { format: "PE", architecture: target.arch, clrDescriptor: false };
    }
    if (target.os === "darwin") {
        assert.equal(bytes.readUInt32LE(0), 0xfeedfacf, "64-bit Mach-O header");
        assert.equal(bytes.readUInt32LE(4), target.arch === "arm64" ? 0x100000c : 0x1000007);
        assert.equal(bytes.readUInt32LE(12), 2, "Mach-O executable");
        return { format: "Mach-O", architecture: target.arch };
    }
    assert.equal(bytes.subarray(0, 4).toString("hex"), "7f454c46", "ELF header");
    assert.equal(bytes[4], target.arch === "arm" ? 1 : 2, "ELF class");
    assert.equal(bytes[5], 1, "Retained ELF targets are little-endian");
    assert.ok([2, 3].includes(bytes.readUInt16LE(16)), "ELF executable or position-independent executable");
    assert.equal(bytes.readUInt16LE(18), { x64: 62, arm: 40, arm64: 183, loong64: 258, riscv64: 243 }[target.arch]);
    return { format: "ELF", architecture: target.arch };
}
