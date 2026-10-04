# Phase 8: distribution, integration and performance

The Windows x64 C# preview has an npm distribution, a standalone archive, a byte-coordinate JavaScript client, VS Code SDK integration, compiler statistics and trace/profile consumers. Go remains the default product backend. The C# package contains no Go execution dependency.

The user's phase-8 validation scope resumes Release CoreCLR benchmarks, accepts Windows x64 validation with the other retained platforms recorded as release blockers, and keeps NativeAOT **publish-only**. This report distinguishes that work from the original replacement gate, which still requires execution and performance validation of the published native artifacts.

Implementation and validation within that scope are complete. The original release gate remains open: measured CoreCLR performance does not match Go, and native execution, signing, CPU-baseline policy and the remaining platforms are unresolved. The [completion evidence](../csharp/compatibility/evidence/phase8-completion.json) records 23 validation entries, artifact/source hashes, accepted performance distributions and the boundaries for reused results.

## Distribution and adoption

`csharp/tools/package.mjs` creates `@typescript/csharp-preview` and its matching `@typescript/csharp-preview-win32-x64` optional dependency. The platform package includes `tsgo.exe`, 108 external standard-library files, and the project, Go-port, .NET runtime and dependency notices. The main package includes the compiler launcher and the asynchronous and synchronous API clients. A standalone ZIP contains the platform payload. Package manifests record source hashes, the dirty-worktree state, toolchain version, payload hashes, archive hashes, publishing arguments and signature status.

The default packaging command publishes NativeAOT with `IlcInstructionSet=native`. It never executes that artifact. `--managed` creates a framework-dependent CoreCLR validation package, explicitly marked as such. The executable resolver rejects missing executables, mismatched package versions and mixed native/managed packages. Compiler version, JavaScript package version and persisted build-info version agree.

```powershell
$env:DOTNET_ROOT = 'D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64'
node csharp/tools/package.mjs
node csharp/tools/verify-package.mjs

# Install and exercise an isolated CoreCLR validation package.
npx hereby csharp:test-package
node csharp/tools/vscode-integration.mjs
```

These commands write local artifacts; they do not publish to npm or install a VS Code extension. To opt an existing project into the preview, install both matching tarballs from the generated `npm` directory. Its `tsgo` launcher selects the C# executable. The existing TypeScript extension can select that installation through `js/ts.tsdk.path`, pointing at `node_modules/@typescript/csharp-preview`.

The VS Code integration test uses the actual extension SDK resolver and installed `vscode-languageclient` executable transport. It resolves the main package, its `lib` directory and three alias layouts, rejects a missing executable, and exercises Unicode diagnostics, hover and edits with both run and debug launch settings. Editor filesystem and URI services are supplied by a small test shim. This is not a claim of an interactive VS Code extension-host run.

`--certificate <thumbprint>` selects a certificate from `CurrentUser/My`; `--timestamp-url` supplies the timestamp service. `--require-signed` refuses to pack unless Authenticode reports `Valid`. The negative control published a real unsigned executable and verified rejection before any tarball or distribution manifest was created. Current artifacts are unsigned: no trusted production signing certificate was supplied.

The [Windows workflow](../.github/workflows/csharp-windows.yml) builds with an installed .NET 11 SDK, checks generation, exercises the managed packages and extension integration, and publishes the native CLI and compatibility harness without running them. It is a local source change; no GitHub Actions execution is claimed.

## Client coordinates and host contracts

The C# client uses UTF-8/WTF-8 byte offsets and AST protocol version 9. Source slices, factory and remote nodes, token navigation, scanners, line starts, diagnostic display and source-file re-encoding use that contract. `@typescript/csharp-preview/unstable/text` exports `utf8Offset`, `utf16Offset` and `byteLength` for JavaScript string-index boundaries. Source-file position maps use weak ownership. The raw source bytes remain available internally so malformed UTF-8 and separate WTF-8 surrogate encodings do not lose their byte identity when decoded for JavaScript.

The build copies and adapts the original generator templates before generating the C# client. It does not edit generated AST classes after generation. Original Go client behavior stays unchanged; the shared API sources only gain three explicit snapshot type annotations and their generated synchronous counterparts to satisfy the current TypeScript compiler.

The CLI supports the original LSP transport flags, profiling directory, parent-process override and initialization parent ID. Only stdio is supported, matching Go. Integer flags follow Go's base-zero syntax and width; malformed flags, a terminating single dash and redirected help output now follow the original host behavior. Expected shutdown and parent cancellation return success in C#, while the pinned Go executable reports `context canceled` and exits 1. This exit-status distinction is recorded separately from protocol parity.

The production LSP host now supplies the OS/version-specific typings cache, npm executor and progress delay. A real LSP session with a controlled npm executor confirms that automatic type acquisition reaches that executor, installs into the selected cache and leaves editor requests responsive while installation is pending.

The physical content-mapper benchmark exposed a missing API presentation step: a diagnostic named the original component file but used the projected TypeScript offset and line number. API diagnostic and emit responses now apply the mapper's source spans, text, aliases and canonical filenames, including related diagnostics and supplemental outputs. Synthesized locations retain virtual text with the reference's explanatory note. Mapper failure notes preserve their no-location sentinel. The original package fails 80 of 384 focused comparisons; the corrected implementation passes all of them across both transports, physical files and changed filesystem layers. The Windows workflow also exercises a focused mapper/API check with the repository's Node fixture.

## Statistics and profiling

`--diagnostics` and `--extendedDiagnostics` report compiler counts, phase durations and explicitly named CLR memory counters. Parallel parser, binder, checker and emitter durations are labeled as summed worker time. CLR managed and allocated bytes are real process counters. Identifier, symbol, type and instantiation totals describe the C# implementation; they are not presented as Go-identical allocations or private cache counts.

`--generateTrace` writes Chrome trace events, a legend and per-checker type tables. Events retain balanced logical lanes across asynchronous work, and type tables close their referenced IDs. The writer uses a bounded buffer, reports I/O failures without changing compilation results, and releases captured compiler objects after completion. The generated traces were consumed by the pinned `@typescript/analyze-trace` 0.11.1 tools, including type-table simplification.

CLI and LSP `--pprofDir` write CPU and allocation profiles readable by `go tool pprof`. The existing editor/API heap and CPU commands remain available. C# attribution uses cooperative compiler scopes and checkpoints; it is not Go's statistical profiler. Heap captures report the CLR's whole-process managed total, while Go's heap profile is sampled.

The five formerly deferred profiling scenarios now pass the phase-8 contract checks. Each is also replayed with profiling disabled on both backends. Ordinary diagnostics, emitted bytes, removals, timestamps and watch sets remain exact; only validated statistics tables and valid trace artifacts receive the documented runtime-specific treatment. Both scripted filesystem recorders now include appended trace bytes. The earlier incomplete recordings and raw profile differences remain in the evidence.

## Validation

| Gate | Result |
| --- | --- |
| Full semantic regression | 13,446 configurations in each concurrency mode; complete graphs and diagnostic records match, with identical final output hashes |
| Original build/watch scenarios | 516 scenarios and 1,771 cycles pass the phase-8 contract; all five former profiling deferrals have independent controls |
| API comparison | 193 scenarios and 6,358 requests; zero differences under the recorded byte-coordinate contract |
| Mapped API diagnostics | 12 original mapper fixtures, 48 snapshots per backend and 384 comparisons across both transports; zero differences after the presentation fix |
| Original JavaScript client tests | 303 asynchronous and 296 synchronous tests pass per backend, repeated against the packaged compiler with external libraries |
| Packaged byte-coordinate client | Six Unicode files in each API mode, including AST/token/navigation comparisons; lone-surrogate and malformed-byte cases pass, and the original version-8 server is rejected |
| CLI host | Five redirected help/version comparisons, 38 LSP flag cases, two live sessions, four parent-watchdog cases and four pprof reads pass |
| Trace implementation | 9,729 assertions covering event structure, type references, failure handling, concurrency and release of compiler objects |
| Profiling comparison policy | 29 deliberate corruptions are rejected |
| CLI profile/trace consumers | 16 CLI cases with one/four checkers; ordinary output equality, pprof reads and trace-analysis consumers pass |
| Integration safety | 26 typings, 283 LSP, 181 RPC, 177 project and 48 mapper assertions pass |
| Generation | 18 compiler/table generators match 34 generated files; the client generator produces 617 identical files in two independent builds |
| Package installation | Offline tarball install, launcher, external libraries, localization, build-info version, emitted JavaScript execution, both API clients and lookup failure controls pass |
| Original VS Code extension | Build/typecheck and all 12 tests pass |
| NativeAOT publishing | CLI package and compatibility harness publish with warning errors enabled; native executables are not run |
| Retention | Two hours, six sessions, 6,990 cycles and 78 readable heap captures; checked requests and snapshot release pass, with no sustained heap growth observed |

The regression policies and previously classified parser, JSDoc, declaration, ordering and reference defects remain those recorded by earlier phases. No new broad normalization or passing-reference exclusion is introduced.

## Performance and remaining release gates

The phase-8 benchmark tools compare the optimized pinned Go executable with Release CoreCLR on the same Windows x64 host. The 55 workload/concurrency groups cover fresh CLI processes, checking and declaration emission, project-reference builds, no-op and edited builds, native watch cycles, LSP operations, both API transports, callbacks, AST materialization, external content mappers and Windows junctions. Cancellation and retained memory have separate controls. Raw samples include executable/input hashes and retain rejected or failed runs. NativeAOT performance is not inferred from these results.

The recorded CLI batch shows a substantial CoreCLR gap: help/version take about 189–194 ms versus 35–37 ms for Go; the medium compilation takes about 951 ms versus 70 ms. Across the 20 fresh-process scenarios, the measured median ratios range from 4.63 to 15.23. These are recorded Go/CoreCLR comparisons, not before/after optimization speedups. The batch uses 21 accepted paired samples per scenario, three warmups and an initial identical-Go noise control. One measured pair affected by input activity is retained and excluded. The raw evidence retains OS process snapshots alongside input-activity checks.

Subsequent validation found that the CLI compilation fixtures omitted an explicit `rootDir` and reported TS5011 on both backends. Those timings include emission after a configuration diagnostic and do not establish successful clean-compilation performance. The [incremental optimization study](csharp-incremental-performance.md) corrects the fixtures, requires successful compilation and records a separate C# before/after comparison. The original raw measurements remain available.

The accepted service batch has 101 paired samples for warm requests, 21 for startup and 31 for snapshot creation/check/release, after 50 warmups. The five watch workloads each have 21 paired samples after three warmups. The original checker workloads have 31 samples after 50 warmups in each concurrency mode. All compare the intended language effects as well as timings. Service and watch pairs with input activity are rejected; the checker gate requires an entire batch without input activity. These accepted batches had no such contamination.

| Workload | Go median | C# CoreCLR median | C# / Go |
| --- | ---: | ---: | ---: |
| Fresh process, medium compilation with TS5011 | 69.64 ms | 950.65 ms | 13.65 |
| Fresh process, project-reference no-op build | 37.47 ms | 270.15 ms | 7.21 |
| LSP launch through first diagnostics | 43.33 ms | 616.13 ms | 14.22 |
| Warm LSP hover | 0.181 ms | 0.288 ms | 1.59 |
| Warm LSP edit through diagnostics | 0.373 ms | 1.474 ms | 3.95 |
| Sixteen concurrent LSP hover requests | 0.437 ms | 2.849 ms | 6.51 |
| Async API, materialize a complete AST | 0.362 ms | 0.993 ms | 2.74 |
| Sync API, materialize a complete AST | 0.234 ms | 0.898 ms | 3.83 |
| Async API, snapshot/check/release | 3.228 ms | 4.746 ms | 1.47 |
| Sync API, snapshot/check/release | 2.917 ms | 3.998 ms | 1.37 |
| Native watcher, implementation edit | 53.26 ms | 69.01 ms | 1.30 |
| Junction resolution, `preserveSymlinks=false` | 43.64 ms | 584.32 ms | 13.39 |
| Junction resolution, `preserveSymlinks=true` | 41.66 ms | 588.79 ms | 14.13 |
| Mapped project, async snapshot/check/release | 26.02 ms | 61.66 ms | 2.37 |
| Mapped project, sync snapshot/check/release | 26.37 ms | 65.52 ms | 2.48 |

The final four host groups use the corrected package and 21 accepted pairs. Junction checks include ordered source graphs and emitted bytes; they assert the expected real or preserved path. Mapper checks launch the actual external fixture, require a diagnostic at the original source span, compare every diagnostic field and release each snapshot. Mapper requests use 50 warmups and separate identical-Go controls. All four groups completed without input-contaminated pairs; their reference controls had p95 relative pair differences of about 14–16%.

The five watch medians range from 53–56 ms for Go and 66–69 ms for C#. The five original checker workloads, in both concurrency modes, meet the inherited 5-second p95, 512 MiB peak-RSS and 16 MiB released-heap-growth budgets. Current C#/Go checker median ratios are approximately 1.42–1.65. The phase-7 comparison also shows a possible 6–13% cost in several construction-heavy cases with tracing disabled, while allocation totals are essentially unchanged. An isolated class-based trace-scope prototype did not produce an accepted improvement: input activity invalidated its comparison, and it was not adopted. This does not establish the cause of the phase-7 timing difference.

The checker samples also retain program construction, pool construction, GC pauses, collection counts and allocation totals. The large conditional-type workload allocates about 64–66 MB and spends a median 41–42 ms in GC pauses during its roughly 100–104 ms C# run. The other four workloads have median GC pauses of about 5.3–5.5 ms. These are measured costs in this harness, which forces collection before each request and excludes that preparatory collection from its timer. They are diagnostic leads, not evidence for a particular GC or allocation optimization. The reference-default checker pool contains four checkers in these fixtures.

The identical-Go control has a 21.2% p95 relative pair difference for fresh CLI processes and 64.5% for sub-millisecond hover requests. The recorded margin classifications use those controls plus paired bootstrap intervals; a warm result labeled `within margin` is not proof of equal performance. The large CLI/startup gaps and several service gaps clearly exceed their controls. Peak RSS comes from Windows process counters, not a managed-heap estimate. Small C# CLI processes use about 95 MiB versus 12–55 MiB for Go; the huge-file workload uses about 229 MiB versus 138 MiB. Both the raw distributions and the operational budgets remain available for review.

Active-request cancellation was observed in both backends for all 21 paired probes, followed by successful diagnostic requests. The median response after cancellation was 8.35 ms for Go and 2.41 ms for C#, with broad tails. A separate queued-request race cancelled zero of 64 Go requests and all 64 C# requests; that difference is recorded as a dispatch/cancellation race and is not treated as a comparative speed result.

Separate instrumented API sessions record transport counts without using those instrumented timings as benchmark results. Both backends send 2,374 bytes for the measured initialization, option and source-file requests; they receive 27,065 bytes through the asynchronous transport and 20,330 through the synchronous transport. The complete AST traversal benchmark separately forces all nodes to materialize.

The earlier service batch stalled during cleanup after its cancellation probes; it remains excluded. A cleanup control and a fresh complete batch passed with bounded request timeouts. No compiler defect or fix is inferred from the stalled batch. The preliminary retention run was stopped for the isolated scope investigation and replaced by the completed two-hour run.

The retention run completed 6,990 cycles across six Go/C# sessions: LSP, asynchronous API and synchronous API. Every iteration checked edited documents or independently changed filesystem layers and released every API snapshot. All sessions shut down successfully, all artifact hashes remained unchanged, and `go tool pprof` read 78 heap captures. After excluding the initial two captures, final C# managed-heap growth was 21,584 bytes for LSP, 5,608 bytes for asynchronous API and 4,776 bytes for synchronous API. The series fluctuate within small bounds and show no sustained upward trend. Go's corresponding sampled-heap changes were −1,212,708, 969 and 0 bytes. These are different heap measurement definitions, not a claim of an equivalent absolute footprint. A passive observer also recorded the shared Node client process; its handle count stayed at 261.

The general compiler, service and retention batches precede the final mapped-diagnostic presentation fix. Their exact binaries and input hashes remain recorded. The later source changes affect mapped API diagnostic rendering and a mapper-failure note; the earlier ordinary compiler/watch/LSP paths and the retention run's empty API diagnostic responses are unchanged. The API regression, mapped diagnostics, packaged clients and host benchmarks are revalidated on the corrected artifacts.

Measurements use the i7-13700K host with 24 logical processors, about 64 GiB RAM, workstation GC, tiered compilation and runtime-default dynamic PGO. Fresh CLI processes use a warm filesystem cache. The 256 MiB experiment preserves each runtime's setting, including the distinction between Go's soft memory limit and CoreCLR's hard heap limit. Before/after process CPU snapshots are retained locally; this is not a claim of a completely quiescent machine.

The original replacement gate remains open for published NativeAOT execution and performance, trusted signing, CPU-baseline distribution policy and validation/maintenance of the other 20 retained platform entries and Linux Alpine variants. The current `native` instruction-set artifacts target the validation host, not every supported x64 CPU. The Go backend remains the product backend while these gates are open.

Final local artifacts are in `built/csharp/phase8-final-managed3` and `built/csharp/phase8-final-native3`. Both package manifests identify source hash `9c44238d46552c782d000fc7f8a8fe5c646431264cdbfa11571c62b505ed298a`. The native compiler is 28,922,880 bytes and its standalone ZIP is 14,130,193 bytes. The compatibility harness is published separately under `built/csharp/phase8-native-harness3`. These files are local validation outputs; no npm publication, GitHub Actions run, commit or push is part of this phase.
