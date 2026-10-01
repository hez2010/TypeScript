# C# backend experiments

This directory implements the C# backend in [the rewrite plan](../docs/csharp-rewrite-plan.md). It contains the compiler library, a development command-line executable, and compatibility harnesses. Candidate work never forwards to Go. Go is compiled separately as a pinned development oracle and remains the product backend until the replacement gates are complete.

Phases 2 and 3 are implemented and validated on Windows x64. The [phase-2 report](../docs/csharp-phase-2-results.md) covers syntax and foundation hosts; the [phase-3 report](../docs/csharp-phase-3-results.md) covers resolution, program graphs and reuse, project references, content mappers, binding, NativeAOT gates, and the strict-difference audit.

Phase 4 is complete. The [checker completion report](../docs/csharp-phase-4-progress.md) records full semantic corpus parity, checker/query/emit-resolver coverage, provisional workload measurements, and warning-free NativeAOT publishing. The full semantic and API matrices use Release CoreCLR; historical NativeAOT artifacts also execute safety, host, resolution, mapper, and workload checks.

Phase 5 is complete on Windows x64. It implements JavaScript and declaration emission, source maps, transforms and helpers, declaration signatures, output callbacks and single-file transpilation. The [emit and transpilation report](../docs/csharp-phase-5-progress.md) records differential comparisons, independent JavaScript execution probes, reviewed reference corrections and regression controls. Use `CompilerProgram.EmitAsync` for a program, or `Transpiler.TranspileModuleAsync` and `Transpiler.TranspileDeclarationAsync` for one source file. Performance tuning is on hold.

Phase 6 is implemented and validated on Windows x64. It adds persistent build-info interoperability, incremental diagnostics and emit, project builds and cleanup, watch sessions, and native filesystem watches. The development executable is `tsgo-cs`, from `src/TypeScript.CommandLine`. The [build and watch report](../docs/csharp-phase-6-progress.md) records the transition comparisons, Windows watch tests, reference corrections, and remaining platform and profiling gates. Services and full distribution integration remain later phases.

The [Server GC and Satori comparison](../docs/csharp-phase-4-gc-performance.md) measures self-contained Release builds against Go using the current SDK and invariant globalization.

The latest [performance pass with tiered compilation](../docs/csharp-phase-4-tiered-performance.md) profiles construction and checking, removes repeated allocations, and compares the optimized builds with Go and the saved baseline. Benchmark runs now enable tiered compilation and retain runtime-default dynamic PGO.

Compiler text uses `Utf8String`, backed by `ReadOnlyMemory<byte>`, throughout scanning, the AST, binding, checking, hosts, and diagnostics. Slices and source ranges count bytes. Unescaped tokens borrow source memory; decoded escapes and generated text own UTF-8/WTF-8 buffers. The [UTF-8 migration report](../docs/csharp-utf8-migration.md) records its validation and performance, superseding the UTF-16 representation measured in the earlier [source-backed text report](../docs/csharp-phase-4-span-text.md).

Use an installed .NET 11 SDK; the SDK version is not pinned. The current validation uses `11.0.100-rtm.26473.115`, with C# 15, `OptimizationPreference=Speed`, NativeAOT/trimming analysis, warning errors, and NuGet lockfiles. `NuGet.Config` adds the public `dotnet11` feed for matching nightly packs. The final target is .NET 11 GA; upgrades require refreshing and revalidating the evidence. Node 24 and Go 1.27.1 are required for the reference tooling. The existing Go backend and JS clients remain untouched.

Normal Release builds use CoreCLR settings. NativeAOT publishing is explicit: add `-p:PublishAot=true` to `dotnet publish`. Normal restores use `packages.lock.json`; AOT restores use `packages.aot.lock.json`. The compiler library remains managed IL for the publishing executable to compile. See [the current performance results](../docs/csharp-phase-4-compiler-performance.md).

Tools accept `--dotnet` where supported and otherwise use `DOTNET_ROOT` or `dotnet` from `PATH`. `InvariantGlobalization` removes the native globalization dependency. `--locale` validation uses registered language subtags generated from the pinned Go dependency by `generate-locales.mjs`; localized diagnostic resources remain available. Source text owns its original bytes, including malformed sequences. WTF-8 preserves JavaScript lone surrogates, and BCL span, SIMD, numeric-formatting, and rune APIs handle byte operations. Conversion to `System.String` is explicit at .NET boundaries such as Windows paths and process arguments; there is no UTF-16 compiler facade or position map.

Content mappers must negotiate `utf-8` positions. JSON strings and property names preserve lone-surrogate escapes. `JsonStrings` uses three .NET 11 `UnsafeAccessor` bindings for raw `JsonElement` bytes and writing escaped property names; SDK upgrades must revalidate these bindings in both CoreCLR and NativeAOT.

Diagnostic identifiers use the generated `DiagnosticCode` enum throughout the C# compiler, including `Diagnostic.Code`, message lookup, checker callbacks and diagnostic collections. For example, compare against `DiagnosticCode.CannotFindName0` instead of `2304`. Enum members and messages are generated together from the pinned diagnostic catalog by `generate-foundations.mjs`; `generate-checker.mjs` generates the JavaScript diagnostic policy from those names. Both generators support `--check`.

JSON and other external representations retain the original integer codes. Content-mapper identifiers are converted explicitly at the boundary and may contain values outside the TypeScript catalog. `DiagnosticCode.None` and `DiagnosticCode.Custom` preserve the existing zero and negative-one sentinels.

From the repository root (PowerShell example):

```powershell
$go = 'D:\go1.27.1-20260904.9.windows-amd64\go\bin\go.exe'
$env:DOTNET_ROOT = 'D:\dotnet-sdk-11.0.100-rtm.26473.115-win-x64'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
node csharp/tools/generate-schema.mjs
node csharp/tools/generate-slice.mjs
node csharp/tools/freeze-reference.mjs --go $go
node --conditions @typescript/source csharp/tools/pipeline.mjs --go $go --dotnet $dotnet
node csharp/tools/verify-profiles.mjs $go
# Run alone, when the computer is available for measurement:
node csharp/tools/measure-pipeline.mjs
```

`freeze-reference` extracts the pinned sources into a new ignored directory, builds an embedded oracle, runs the Go suite, and records tests/assets/contracts. To import an already verified run from this exact revision, pass `--events <gotestsum.jsonl>`; the imported file hash is recorded, but the script cannot prove the provenance of an arbitrary external event file. The initial evidence imports the full reference run recorded in the plan. Refreshing the reference revision is an explicit operation, not an automatic update to current HEAD.

`pipeline` builds the probe inside the archived module, publishes the host-native candidate with locked dependencies, and checks the independently implemented C# pipeline against Go. The C# experiment uses packet version 9 with byte positions; the oracle translates Go's record coordinates for comparison through the unchanged JavaScript decoder. The class and arena variants both scan UTF-8. The runner takes performance measurements only with `--benchmark`. `measure-pipeline` verifies artifact/input hashes and measures five sequential native processes without rebuilding. No existing compiler baselines are overwritten. First publishing for a new RID needs a deliberate lockfile refresh; publishing success without execution does not validate that target.

`verify-profiles` exercises the native producer and independently reads six profiles with `go tool pprof`. The producer uses real CPU/allocation/heap counters, bounded phase aggregation, cooperative checkpoints with NativeAOT stack names, and weak source-owner tracking. Exact phase/process totals are separate from approximate stack attribution; heap source counters are separate from whole-process heap bytes. Go is a development consumer, not a candidate runtime dependency. The earlier `profile.mjs` EventPipe experiment remains optional historical tooling.

NativeAOT's default x64 baseline does not enable AVX. The runner passes `/p:IlcInstructionSet=native`, publishes into `built/csharp/native-host`, and records `OptimizationPreference=Speed`. The final pipeline results are in `pipeline-summary.json`, `pipeline-measurements.json`, and `native-profile-summary.json`. Only the host-native configuration is measured, as requested. Retained CPU compatibility remains a release-packaging gate. Shared source uses portable SIMD-backed BCL count/search/UTF-8/hash operations; it has no x86-specific intrinsic dependency.

The original `run-experiments.mjs` and `remeasure.mjs` retain the foundation/primitive and uniform-record studies. The phase-1 storage decision comes from the complete typed pipeline, not those earlier hydration timings. Do not run builds, repository suites or profiling alongside measurement. Refresh checked-in evidence and the phase report after reviewing new results.

To check C# formatting:

```powershell
Push-Location csharp
& $dotnet format TypeScript.slnx --verify-no-changes --no-restore
Pop-Location
```

See [contracts](compatibility/contracts.md), the historical [phase-1 report](../docs/csharp-phase-0-1-results.md), and `phase1-scope.generated.json` for scope. The full compiler now uses typed classes and UTF-8/WTF-8 text throughout. The older representation experiments are not measurements of the current compiler.
