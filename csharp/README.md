# C# backend experiments

This directory executes the early work in [the rewrite plan](../docs/csharp-rewrite-plan.md). It does not contain a usable replacement compiler. The published program is a compatibility/measurement harness and never forwards candidate work to Go. Go is compiled separately as a pinned development oracle.

Phases 2 and 3 are implemented and validated on Windows x64. The [phase-2 report](../docs/csharp-phase-2-results.md) covers syntax and foundation hosts; the [phase-3 report](../docs/csharp-phase-3-results.md) covers resolution, program graphs and reuse, project references, content mappers, binding, NativeAOT gates, and the strict-difference audit.

Phase 4 is complete. The [checker completion report](../docs/csharp-phase-4-progress.md) records full semantic corpus parity, checker/query/emit-resolver coverage, provisional workload measurements, and warning-free NativeAOT publishing. Runtime validation uses Release CoreCLR; the NativeAOT artifact is build-verified only. Transformations/emission and full product integration remain later phases.

Use an installed .NET 11 SDK; the SDK version is not pinned. The current validation uses `11.0.100-rtm.26473.115`, with C# 15, `OptimizationPreference=Speed`, NativeAOT/trimming analysis, warning errors, and NuGet lockfiles. `NuGet.Config` adds the public `dotnet11` feed for matching nightly packs. The final target is .NET 11 GA; upgrades require refreshing and revalidating the evidence. Node 24 and Go 1.27.1 are required for the reference tooling. The existing Go backend and JS clients remain untouched.

Tools accept `--dotnet` where supported and otherwise use `DOTNET_ROOT` or `dotnet` from `PATH`. `InvariantGlobalization` removes the native globalization dependency. `--locale` validation uses registered language subtags generated from the pinned Go dependency by `generate-locales.mjs`; localized diagnostic resources remain available. JavaScript lone surrogates and malformed source bytes require the small WTF-8 compatibility boundary; ordinary text stays in .NET strings and BCL UTF-8 APIs handle valid encodings.

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

`pipeline` builds the probe inside the archived module, publishes the host-native candidate with locked dependencies, and checks the independently implemented C# pipeline against Go. The unchanged JavaScript API decoder verifies the generated source-file packets. It covers all four class/arena and UTF-8/UTF-16 variants. It does not take performance measurements unless explicitly passed `--benchmark`. `measure-pipeline` verifies artifact/input hashes and measures five sequential native processes without rebuilding. No existing compiler baselines are overwritten. First publishing for a new RID needs a deliberate lockfile refresh; publishing success without execution does not validate that target.

`verify-profiles` exercises the native producer and independently reads six profiles with `go tool pprof`. The producer uses real CPU/allocation/heap counters, bounded phase aggregation, cooperative checkpoints with NativeAOT stack names, and weak source-owner tracking. Exact phase/process totals are separate from approximate stack attribution; heap source counters are separate from whole-process heap bytes. Go is a development consumer, not a candidate runtime dependency. The earlier `profile.mjs` EventPipe experiment remains optional historical tooling.

NativeAOT's default x64 baseline does not enable AVX. The runner passes `/p:IlcInstructionSet=native`, publishes into `built/csharp/native-host`, and records `OptimizationPreference=Speed`. The final pipeline results are in `pipeline-summary.json`, `pipeline-measurements.json`, and `native-profile-summary.json`. Only the host-native configuration is measured, as requested. Retained CPU compatibility remains a release-packaging gate. Shared source uses portable SIMD-backed BCL count/search/UTF-8/hash operations; it has no x86-specific intrinsic dependency.

The original `run-experiments.mjs` and `remeasure.mjs` retain the foundation/primitive and uniform-record studies. The phase-1 storage decision comes from the complete typed pipeline, not those earlier hydration timings. Do not run builds, repository suites or profiling alongside measurement. Refresh checked-in evidence and the phase report after reviewing new results.

To check C# formatting:

```powershell
Push-Location csharp
& $dotnet format TypeScript.slnx --verify-no-changes --no-restore
Pop-Location
```

See [contracts](compatibility/contracts.md), the [completed phase-1 report](../docs/csharp-phase-0-1-results.md), and `phase1-scope.generated.json` for exact scope and later work. The chosen next-phase representation is typed classes, original UTF-8/WTF-8 ownership and temporary UTF-16 scanning. These prototype measurements must not be presented as complete C# compiler speedups over Go.
