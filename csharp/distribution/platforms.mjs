// Product targets with a .NET 11 NativeAOT toolchain. The archived Go matrix is historical.
export const platforms = [
    { os: "win32", arch: "x64", rid: "win-x64", vsix: "win32-x64" },
    { os: "win32", arch: "arm64", rid: "win-arm64", vsix: "win32-arm64" },
    { os: "linux", arch: "x64", rid: "linux-x64", libc: "glibc", vsix: "linux-x64" },
    { os: "linux", arch: "arm", rid: "linux-arm", libc: "glibc", vsix: "linux-armhf" },
    { os: "linux", arch: "arm64", rid: "linux-arm64", libc: "glibc", vsix: "linux-arm64" },
    { os: "linux", arch: "x64", rid: "linux-musl-x64", libc: "musl", vsix: "alpine-x64" },
    { os: "linux", arch: "arm64", rid: "linux-musl-arm64", libc: "musl", vsix: "alpine-arm64" },
    { os: "darwin", arch: "x64", rid: "osx-x64", vsix: "darwin-x64" },
    { os: "darwin", arch: "arm64", rid: "osx-arm64", vsix: "darwin-arm64" },
    { os: "android", arch: "arm64", rid: "linux-bionic-arm64" },
    { os: "freebsd", arch: "x64", rid: "freebsd-x64" },
    { os: "freebsd", arch: "arm64", rid: "freebsd-arm64" },
    { os: "linux", arch: "loong64", rid: "linux-loongarch64", libc: "glibc" },
    { os: "linux", arch: "riscv64", rid: "linux-riscv64", libc: "glibc" },
    { os: "openbsd", arch: "x64", rid: "openbsd-x64" },
    { os: "openbsd", arch: "arm64", rid: "openbsd-arm64" },
    { os: "sunos", arch: "x64", rid: "solaris-x64" },
];

export function hostPlatform() {
    const libc = process.platform === "linux" ? (process.report.getReport().header.glibcVersionRuntime ? "glibc" : "musl") : undefined;
    const result = platforms.find(p => p.os === process.platform && p.arch === process.arch && p.libc === libc);
    if (!result) throw new Error(`No C# distribution target for ${process.platform}-${process.arch}${libc ? "-" + libc : ""}`);
    return result;
}

export function platformForRid(rid) {
    const result = platforms.find(p => p.rid === rid);
    if (!result) throw new Error(`Unknown C# distribution RID: ${rid}`);
    return result;
}

export const platformSuffix = target => `${target.os}${target.libc === "musl" ? "-musl" : ""}-${target.arch}`;
export const executableFile = (target, name = "tsc") => name + (target.os === "win32" ? ".exe" : "");
