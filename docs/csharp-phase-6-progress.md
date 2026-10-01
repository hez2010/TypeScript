# C# build, incremental compilation, and watch

Phase 6 is implemented and validated on Windows x64. It provides persistent incremental state, project builds, cleanup, watch sessions, and a command-line executable. Native watcher validation on other retained targets remains open. Performance work remains on hold.

The reference is Go revision `f29aeb9f825d96feea27841f3f7342dbf0df68a8`. Correctness runs use Release CoreCLR with .NET SDK `11.0.100-rtm.26473.115`, C# 15, invariant globalization, and the default GC. NativeAOT publishing is validated separately; neither native executable was executed or benchmarked. The existing Go product entry points and JavaScript clients are unchanged.

## Implementation

`IncrementalProgram` retains file versions, dependency edges, declaration signatures, pending emits, and diagnostic caches across program snapshots. It propagates affected files when exported types change, reuses diagnostics for unchanged dependencies, handles option and package changes, and preserves pending work through cancellation or failed writes. Previous snapshots remain independent. Cached diagnostics whose explanations depend on current module resolution are repopulated when materialized, including after a restart from build-info.

`BuildInfo` reads and writes the pinned `.tsbuildinfo` format without reflection-based serialization. It preserves compact file information, root ranges, dependency lists, pending emit kinds, diagnostic chains and repopulation metadata, package lookups, content-mapper identities, and option paths relative to the state file. Malformed, incompatible, or unusable cache data causes a fresh compilation.

`ProjectBuilder` traverses project references in dependency order, bounds parallel work, and reports results in project order. It implements no-op and timestamp-only builds, declaration-change propagation, force/dry/clean modes, `stopBuildOnErrors`, missing and circular references, and output-write failures. Cleanup removes computed outputs while preserving inputs.

`ProjectWatchSession` and `BuildWatchSession` own serialized compiler cycles, watch reconciliation, configuration reloads, mapper refreshes, and cancellation. `NativeWatchBackend` uses `FileSystemWatcher`, batches native events, preserves rename/delete transitions, handles overflow and root termination, and serializes callbacks. Watch sessions compare event paths using the filesystem's case rules. A configuration event remains actionable when the file changed during emission and its new timestamp has already been observed. Builder timestamps are normalized to UTC.

The `TypeScript.CommandLine` project produces `tsgo-cs`. It supplies embedded libraries and locales, terminal-sensitive diagnostic reporting, ordinary compilation, project/build/watch modes, help, initialization, effective configuration output, file explanations, and resolution traces. Ctrl+C cancels the command and disposes watch and mapper resources. This is a development entry point; service frontends and distribution integration remain later phases.

## Build and watch evidence

| Gate | Coverage | Result |
| --- | --- | --- |
| [Build-info codec](../csharp/compatibility/evidence/phase6-build-info.json) | 567 cases, including 545 distinct states extracted from the original command baselines; 28 malformed/cache safety assertions | Exact agreement |
| [Incremental state](../csharp/compatibility/evidence/phase6-incremental.json) | 135 scenarios and 942 cycles; 762 additional state handoffs in both Go-to-C# and C#-to-Go directions | Exact agreement |
| [Project builds](../csharp/compatibility/evidence/phase6-build.json) | 48 scenarios and 342 cycles | 46 strict matches; two independently checked reference corrections |
| [Authored watch transitions](../csharp/compatibility/evidence/phase6-watch.json) | 33 scenarios and 242 cycles | Exact agreement |
| [Original command replays](../csharp/compatibility/evidence/phase6-scripted-builds.json) | All 516 exported scenarios and 1,771 cycles from `tsc`, `tsbuild`, `tscWatch`, and `tsbuildWatch` | Compilation/build/watch scope verified; five profiling contracts deferred as described below |
| [Command-line executable](../csharp/compatibility/evidence/phase6-command-line.json) | Seven separate managed process invocations | Version/help, compile, no-op state preservation, error exit/noEmitOnError, forced rebuild, and clean |

The state-handoff tests feed outputs produced by one backend into the other's initial filesystem and continue compilation. They compare diagnostics, source graphs, emitted bytes, state contents, and subsequent write decisions. Comparing two independent cold compilations alone would not establish this interoperability.

The original command exporter executes the reference test closures and records initial filesystem contents, symbolic links, case rules, edits, command arguments, and watch cycles. Replay compares status, diagnostics, output files and bytes, state files, timestamp operations, and watch subscriptions. Raw input and output records and binary hashes remain under `built/csharp`; checked-in evidence records their identities and counts. The tests retain the reference's emitted-file timestamp ordering and replace only wall-clock prefixes in status messages.

Native Windows tests exercise creation, modification, rename, deletion, new descendants, ignored paths, watched-root replacement, registration rollback, and callback shutdown. Complete project/build watch tests then edit source signatures, recover from diagnostics, change output directories, add roots, cancel or dispose the running session, and verify that subscriptions and callbacks stop. A deterministic test edits a differently cased config path during emission. The newly discovered-root race is also reproduced independently of native event timing.

### Recorded differences

The [build correction ledger](../csharp/compatibility/build-corrections.json) binds each input and both complete output sequences to hashes. Controls must pass in the same run.

* **Missing project reference after an earlier successful build.** The pinned Go builder dereferences a missing `buildInfoEntry` in `getLatestChangedDtsMTime`. C# reports the missing configuration, continues according to the build options, and recovers when the reference is restored. Separate forced-build, stop-on-error, missing-root, and cold-missing-reference controls establish the expected compilation behavior. Raw panic stacks are preserved; process addresses are excluded from the stable reference hash.
* **A newly discovered root with an older timestamp.** Go's timestamp shortcut can omit a new source file whose mtime predates the previous state file. This also occurs when a file is created while another watch build is finishing. C# checks root membership as well as timestamps. Its accumulated JavaScript, declarations, and complete build-info must exactly match an independent forced Go build. Existing removed-root diagnostics keep their priority.

One original incremental test prints a compiler-internal iterator symbol allocation ID in TS2783. That ID varies even between Go runs. The replay comparator recognizes only that diagnostic's internal iterator name and its stored first message argument. It preserves source text, emitted bytes, other diagnostics, and all other arguments; a source containing the reserved internal prefix disables this normalization for the case.

### Profiling boundary

Five of the original 516 scenarios request `--extendedDiagnostics` or `--generateTrace`. Profile/trace consumers belong to phase 8, and performance work is on hold. The complete original records still run and remain five full-contract differences. The phase-6 runner additionally executes separate copies with just the profiling arguments removed, compares both backends, and checks each original backend's non-profiling effects against its control. It permits only the explicitly listed statistics suffixes or three trace output files for those exact cases. It does not remove cases, source/configuration content, ordinary resolution traces, diagnostics, or compilation outputs.

Accordingly, the phase-6 count is 516 matching compilation transitions, not 516 complete CLI contracts. The default replay command without `--phase6` continues to fail on the five deferred profiling contracts.

## Regression and platform boundaries

All final gates use the same frozen compiler and harness artifacts. The [validation rollup](../csharp/compatibility/evidence/phase6-validation.json) records their hashes, the 502-file compiler source fingerprint, publish logs, and preserved lockfile hashes.

| Regression gate | Coverage | Result |
| --- | --- | --- |
| Complete semantic corpus: [single](../csharp/compatibility/evidence/phase6-checker-regression-single.json) and [reference-default](../csharp/compatibility/evidence/phase6-checker-regression-default.json) | 13,446 active configurations per concurrency mode | Exact source graphs and complete diagnostic records; zero failures |
| [Checker APIs](../csharp/compatibility/evidence/phase6-checker-apis.json) | 31 suites, 1,949 configurations, 1,649,908 queries | Zero candidate failures; four existing reference failures remain recorded |
| [Emission](../csharp/compatibility/evidence/phase6-emission-regression.json) | 1,760 declaration/composite configurations, 240 program-emission cases, 415 transpile comparisons | No failures; the same two phase-5 fixture corrections remain explicit |
| [Host and configuration](../csharp/compatibility/evidence/phase6-host-regression.json) | 3,152 cases | 3,139 strict matches and 13 existing phase-2 policy differences; no failures |
| [Safety and lifecycle](../csharp/compatibility/evidence/phase6-safety.json) | 83 commands, including native watch and complete command lifecycles | All pass |
| [Parser control](../csharp/compatibility/evidence/phase6-parser-regression.json) | 12,882 original files and 17,448 expanded units | No new strict or unresolved differences against phase 5 |
| Managed build and NativeAOT publish | Complete solution; separate CLI and compatibility-harness native executables | Zero warnings and errors; native executables were not run |

The parser control retains 33 unresolved original-file cases and ten unresolved expanded-unit cases from phase 5. It also retains 253 and 353 previously permitted differences, respectively. Those unresolved cases are not claimed as passing. Input hashes and complete candidate syntax hashes verify that the retained strict differences did not change.

Native watch execution in this phase is Windows x64 only. Linux, macOS, and the other retained target combinations still require real-host execution. Publishing alone does not satisfy that gate. These platform gaps, service frontends, profile consumers, packaging, and the held performance work continue to block full replacement of the Go backend.

## Reproduction

From the repository root, using an installed matching SDK:

```powershell
$dotnet = 'D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe'
& $dotnet restore csharp/TypeScript.slnx --artifacts-path built/csharp/phase6-build `
    -p:NuGetLockFilePath=obj/phase6-sdk-restore.lock.json -p:RestoreLockedMode=false
& $dotnet build csharp/TypeScript.slnx -c Release --artifacts-path built/csharp/phase6-build --no-restore
node csharp/tools/build-info.mjs --dotnet $dotnet
node csharp/tools/incremental.mjs --dotnet $dotnet --interop
node csharp/tools/build.mjs --dotnet $dotnet
node csharp/tools/watch.mjs --dotnet $dotnet
node csharp/tools/export-scripted-builds.mjs --tag phase6
node csharp/tools/scripted-builds.mjs --dotnet $dotnet --exports phase6 --phase6
node csharp/tools/command-line.mjs --dotnet $dotnet
& $dotnet built/csharp/phase6-build/bin/TypeScript.Compatibility/release/TypeScript.Compatibility.dll --watch-safety
& $dotnet built/csharp/phase6-build/bin/TypeScript.Compatibility/release/TypeScript.Compatibility.dll --native-watch-safety
& $dotnet built/csharp/phase6-build/bin/TypeScript.Compatibility/release/TypeScript.Compatibility.dll --native-command-safety
& $dotnet built/csharp/phase6-build/bin/TypeScript.CommandLine/release/tsgo-cs.dll -p path/to/project
```

The isolated restore lock path preserves existing normal lockfiles. The new executable has its own normal and NativeAOT lockfiles. Use `--no-build` with an explicit `--managed-directory` to validate a frozen artifact once its Go oracle is built. The full checker regression reuses the verified pre-emit reference export, including its recorded original-content corrections for mapper inputs; the exporter binary and input hashes must match the recorded reference provenance.
