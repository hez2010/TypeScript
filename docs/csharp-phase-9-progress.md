# Phase 9: C# product cutover

Phase 9 makes C# the default on every retained platform. Windows x64 has execution evidence; other platforms are accepted by the user after the static review below. The repository's default build now produces the C# compiler, CLI/watch/build host, language server and API. Validation executes Release CoreCLR and publishes and inspects NativeAOT without executing it. Native execution, performance and production signing remain separate release qualifications. The later instruction accepts non-Windows compatibility by static review and removes targets without a matching NativeAOT toolchain.

## Product and build changes

- `Herebyfile.mjs` builds C# with .NET 11. `built/local/tsc.exe` is the development executable. `csharp/version.txt` supplies the compiler and package version.
- Distribution packaging produces `typescript`, `@typescript/typescript-win32-x64`, the `tsc` launcher, a standalone ZIP and a Windows VSIX. Package resolution rejects mixed backend/runtime/version payloads. The old preview name is available only through the explicit `--preview` package option.
- The primary JavaScript client now implements the previously validated UTF-8/WTF-8 position contract and AST protocol 9. AST and encoder templates preserve those changes during regeneration. Wire declarations are maintained in `protocol.types.ts`; no Go generator is needed.
- Unicode casing, normalization, regexp properties, locale registries and enum wire values are frozen in `csharp/data`, with a manifest and explicit oracle refresh commands. Normal generation and checks never execute `go` or `gofmt`; Go output requires an explicit oracle option.
- The pinned Go revision, refresh inputs, former build/workflow definitions and editor setup are preserved under `csharp/oracle`. Git extraction isolates optional oracle runs from the product tree. Shared schemas, libraries and fixtures remain in place.
- GitHub CI and local validation use C# and run the product pipeline with Go tools absent from `PATH`. CodeQL selects C# and JavaScript. Azure validation produces C# artifacts; former production publishers are archived and disabled until release qualification and signing are configured.

Full client testing exposed a project-directory bug: C# API file requests normalized relative names against the session directory, while the reference resolves them against the selected program's directory. Program-scoped requests now use the configured project directory. Client caches use the same directory, so relative and absolute requests retain the same source object. Async and sync regression coverage exercises source/config files, metadata, symbol queries, diagnostics and selected-file emit.

## Validation

The final clean run passed on October 4, 2026, using SDK `11.0.100-rtm.26473.115` and Node `24.21.0`. It started with a fresh checkout plus the current patch, confirmed that no build outputs or dependency directories were copied, installed dependencies, and executed the default generator/build/test/package/VS Code pipeline with `go` and `gofmt` unavailable. NativeAOT packages and VSIX contents were then inspected without executing the compiler.

| Check | Result |
| --- | --- |
| Frozen compatibility data | All five snapshots reproduced from the pinned Go 1.27.1 oracle in the separate, explicit refresh check |
| Product generation without Go | 20 generators/checks passed; 34 generated compiler files unchanged; two independent 617-file client builds identical |
| Release compiler and solution builds | Passed with zero warnings and errors |
| C# safety coverage | 93 groups plus content-mapper lifecycle/graph checks passed, including parser depth 21,000 and 1,421,851 syntax nodes |
| Full JavaScript client suite | 815 tests passed, zero failures/cancellations/skips, including all benchmark smoke cases |
| Existing extension suite | 12 tests passed |
| Installed managed distribution | CLI/version, external libraries, localized diagnostics, incremental state, emitted JavaScript, async/sync APIs, aliases and invalid-package rejection passed |
| VS Code integration | Five SDK resolutions; actual `vscode-languageclient` run/debug transports passed diagnostics, hover, edit and shutdown checks |
| NativeAOT distribution | Published without warnings; AMD64 executable has no CLR descriptor or managed fallback; 108 external libraries, npm tarballs, ZIP, notices and VSIX payload hashes verified |

VS Code validation uses the real resolver and process transport with a filesystem/URI editor shim. It does not claim interactive VS Code activation. Benchmark smoke cases run once and are not performance measurements.

The accepted checkout is `built/csharp/clean-validation/run-pE4z1U`. Its combined log is `built/csharp/phase9-clean-product-final.log`; its original result is `built/csharp/clean-validation/summary.json`. `built/csharp/phase9-validation/summary.json` collects the generator, compiler, installed-package, VS Code and native artifact evidence. The SDK version above records the run; it does not pin future builds. The `tsc/` reference tree has no changes from the pinned revision.

Validated artifacts were copied into `built/csharp/distribution/coreclr-validation` and `built/csharp/distribution/nativeaot`, after checking every recorded product input against the working tree. Native artifact verification passed again at that destination. Version `7.1.0-dev` uses source digest `d5f1f9fd75c517fdce0d3d1f76907bf9002ca10753112dc61183a957eb9adbf9`; the 28,928,512-byte executable has SHA-256 `5da525105d84f59403630a2e35999a9a536c849075462b90efc72bad0f66b008`. The compiler and VSIX are unsigned validation artifacts.

An initial clean run passed its managed tests and packages but still used an absolute `gofmt` path to check an oracle-only generated file. That result is preserved as `built/csharp/clean-validation/initial-summary.json` and is excluded from the final no-Go claim. The generator was corrected before the final run.

Run the complete local validation with:

```powershell
npm ci
node csharp/tools/clean-product-validation.mjs --native
```

These commands create local artifacts; they do not publish to npm or the VS Code Marketplace. Production signing and native execution/performance remain release gates. Other retained platforms are enabled by the accepted static review, and packaging now uses the SDK target CPU baseline by default. See the [phase-8 report](csharp-phase-8-progress.md) for the retained benchmark evidence; one-iteration client benchmark smoke cases are correctness checks, not new performance measurements.

## Platform expansion and static review

The follow-up removes Windows-only product restrictions and selects C# for 17 RID/libc combinations. The active matrix is [platforms.mjs](../csharp/distribution/platforms.mjs); the original Go matrix remains only in historical oracle contracts.

| Node target | .NET RID | VSIX |
| --- | --- | --- |
| Windows x64 / arm64 | win-x64 / win-arm64 | win32-x64 / win32-arm64 |
| Linux x64 / arm / arm64, glibc | linux-x64 / linux-arm / linux-arm64 | linux-x64 / linux-armhf / linux-arm64 |
| Linux x64 / arm64, musl | linux-musl-x64 / linux-musl-arm64 | alpine-x64 / alpine-arm64 |
| macOS x64 / arm64 | osx-x64 / osx-arm64 | darwin-x64 / darwin-arm64 |
| Android arm64 | linux-bionic-arm64 | None |
| FreeBSD x64 / arm64 | freebsd-x64 / freebsd-arm64 | None |
| Linux LoongArch64 / RISC-V64 | linux-loongarch64 / linux-riscv64 | None |
| OpenBSD x64 / arm64 | openbsd-x64 / openbsd-arm64 | None |
| Solaris x64 | solaris-x64 | None |

AIX/ppc64, Linux/mips64el, Linux/ppc64, Linux/s390x and both NetBSD architectures were removed from the release matrix at the user's request. A toolchain advertised by the installed SDK is required for the chosen RID; this does not imply that Windows can cross-compile Unix binaries. Community targets need the corresponding SDK/native toolchain and system libraries.

The audit covered every production C# source file and the active build, generator, package, resolver and verification paths. It found and corrected:

- Windows-only CPU profiling: Unix now reads the current thread's CPU clock through `clock_gettime`; the Windows `GetThreadTimes` call remains guarded. Clock IDs and native-long `timespec` layouts were checked against the host ABI definitions, including 32-bit glibc ARM. The [Apple header](https://raw.githubusercontent.com/apple-oss-distributions/Libc/main/include/_time.h), [OpenBSD clock contract](https://man.openbsd.org/clock_gettime.2) and [FreeBSD thread clock contract](https://man.freebsd.org/cgi/man.cgi?query=pthread_getcpuclockid&sektion=3) document these interfaces.
- Missing Unix LSP watcher fallback: a client without dynamic watch registration can now use the native watcher on every retained host.
- Packaging assumptions: target-specific executable names, RID restore graphs, PE/ELF/Mach-O inspection, Unix executable permissions, npm libc filters, native-library notices, signing selection and VSIX target names are now driven by the matrix.
- Unix npm lookup: product tools use the host's npm command instead of assuming the Windows Node installation layout. The no-Go wrapper preserves unrelated Unix tools when Go shares a PATH directory.
- SDK/package version coupling: the follow-up removes all ten NuGet lockfiles, lock-generation settings and locked-restore flags. Package references retain ordinary version attributes, without floating ranges or exact-match brackets; no SDK version is pinned. Publishing restores the selected SDK/host/RID graph once and publishes without another restore. Existing Satori build targets are preserved with portable path separators.

The main npm package lists every retained platform dependency and has no target RID of its own. The CLI resolver and VS Code SDK resolver both distinguish glibc from musl. Client compilation uses a fixed LF newline convention across build hosts. NativeAOT uses the SDK's target instruction set unless `--instruction-set` is supplied. Artifacts now live under `built/csharp/distribution/<runtime>/<rid>/`.

The remaining OS branches are intentional: executable suffixes/signing, Windows named pipes versus Unix sockets, host filesystem case behavior, cache locations, npm launchers and Windows process-ID handling. No unguarded Windows API call or hardcoded Windows filesystem path remains in production C# code. No x86-only intrinsic dependency was found.

`npx hereby typescript:check-platforms` checks all 17 package plans against the installed SDK's NativeAOT runtime-pack list and exercises each retained executable-format/architecture header, including corrupt-header rejection. These are static checks, not executions of other-platform binaries. GitHub CI now selects Windows, Linux and macOS hosts through the same C# workflow; it has not been run remotely in this task.

After the expansion, the Windows Release solution build has zero warnings/errors; managed package installation and both API modes pass. Existing LSP, LSP watch, native watcher and profiling checks pass (283, 71, 10 and 51 assertions). The final NativeAOT package/VSIX verification is recorded in `built/csharp/phase9-platform-native-verify.log` and `built/csharp/phase9-platform-native-vsix.log`. No NativeAOT binary or non-Windows binary was executed.

The later instruction removes NuGet locks and exact-match pins while retaining ordinary package version attributes for manual version management. The earlier lockfile-based results above remain historical evidence; current builds do not require those versions.
