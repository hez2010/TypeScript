# C# rewrite: phase 0 and 1 execution

Recorded 2026-09-22 on Windows x64. The governing requirements are in [the plan](csharp-rewrite-plan.md): semantic correctness, C# 15, NativeAOT, BCL-first implementation, portable SIMD, and host-native performance measurements only.

**Status: phase 1 is complete for the Windows x64 feasibility milestone.** The independent C# parse/bind/check/print slice, generated typed payloads and clone/child operations, client-consumable source-file encoding, native profiling, snapshot ownership, deep traversal and representation measurements now execute in the published NativeAOT artifact. The full compiler is still the work of phases 2–9; no Go product code or JS client has been replaced. Phase-0 platform and minimum-hardware blockers remain release blockers, rather than being silently dropped.

## Completed phase-1 gates

The final configuration is .NET SDK `11.0.100-rc.2.26470.103`, C# 15, **`OptimizationPreference=Speed`**, and **`IlcInstructionSet=native`**. The compiler response file contains `--Ot` and `--instruction-set:native`. Native publishing has zero unexplained warnings. The verified executable is 5,144,576 bytes with SHA-256 `e3e5b178c253b10e1a2d41afaff42ec4d0ca8824302be6b53bff8881a9db2130`.

| Gate | Completed evidence |
| --- | --- |
| Real C# pipeline | 15 projects exercise modules/imports/exports, primitive/literal/union/alias types, annotated and inferred variables, widening, forward references, cycles, type/value namespaces, Unicode/escaped strings, malformed input and missing modules. The input grammar is explicitly bounded; unsupported grammar is not reported as a successful compilation. |
| Semantic reference comparison | 90,212 assignability results, bound names, diagnostics including byte spans, and 3,886 decoded syntax nodes agree with the pinned Go implementation. Positive projects include an actually resolved graph with 100 import declarations. |
| Generation and storage | 19 typed payloads plus keyword text, child dispatch and cloning are generated from the existing schema selection. The same parser/checker/encoder runs with class nodes or typed arenas and UTF-8 or UTF-16 scanning. The selection and handwritten hook dispositions are recorded in `phase1-scope.generated.json`. Full syntax generation remains phase 2. |
| Independent encoding | C# generates every packet section from its own tree: string offsets/data, extended fields, MessagePack import indices, node records, topology, source hash and metadata. The unchanged JavaScript API decoder consumes the packets and agrees with Go on the selected syntax. MessagePack array16 and wider integer indices are exercised. No Go packet section is copied into these outputs. |
| Printing and cloning | Iterative printing produces source that checks again with equivalent types. Generated cloning preserves encoded bytes across storage models. |
| Stack and ownership | 20,000 nested type parentheses pass parsing, checking, cloning, printing and encoding; a 10,000-alias dependency chain checks without native recursion. Snapshot tests cover shared unchanged sources, old leases across edits/GC, stale-handle rejection, 32 concurrent readers, stable symbol identity, cancelled publication and disposed leases. |
| Native profiling | The native executable writes CPU, allocation and heap `.pb.gz` profiles directly. Go's independent `pprof -raw` and `-top` consume all six artifacts. Actual NativeAOT managed stack names are present. A known 4 MiB owner is reported while retained and absent after release/collection. |

The existing foundation harness also passes **458,098 assertions** on this artifact. The [pipeline evidence](../csharp/compatibility/evidence/phase1-pipeline.json), [profile evidence](../csharp/compatibility/evidence/phase1-profiles.json), and [raw measurement evidence](../csharp/compatibility/evidence/phase1-measurements.json) identify the executable and inputs. These are prototype completion gates, not a claim that the selected grammar implements the rest of TypeScript.

## Representation decision for phase 2

Choose **generated typed class nodes**, retain **original UTF-8/WTF-8 source bytes**, and use a **temporary UTF-16 scanning view**. Keep the alternative stores/views in the experiment harness. The snapshot test harness independently retains its UTF-8 path as coverage; it is not the final product entry point.

This decision follows the complete parse/bind/check/encode pipeline, rather than the older uniform-record hydration experiment below. Five sequential processes produced 75 samples per variant, with 20 complete pipeline operations per sample. No build, test suite or profiler ran concurrently. All variants produced the same results. The workload contains two files, 100 exported aliases, 200 variables and 100 import declarations.

| Pipeline variant | Median ms | Allocated bytes/op | Process-median spread |
| --- | ---: | ---: | ---: |
| Typed classes, UTF-8 scanning | 0.793750 | 1,704,208 | 3.13% |
| **Typed classes, UTF-16 scanning** | **0.746055** | **1,730,920** | **3.31%** |
| Typed arenas, UTF-8 scanning | 0.940745 | 1,628,320 | 4.45% |
| Typed arenas, UTF-16 scanning | 0.903435 | 1,655,032 | 2.62% |

UTF-16 scanning adds about 1.6% allocation versus class/UTF-8 in this experiment and is about 6% faster. Arenas save some allocation but take longer through the actual typed pipeline. Source-byte fidelity remains independent of the scanning view, including malformed text and protocol hashing. These results justify the next-phase implementation choice; revisit measured hot spots on the full scanner/checker workload rather than treating this slice as an end-to-end compiler speed claim. No owner requires Hezium.Memory at these sizes.

Median launch/output/exit is 40.93 ms across 100 launches, including Node/Windows orchestration. This is not isolated runtime startup.

## Profiling semantics

`NativeProfile` uses OS thread CPU counters, process CPU counters, CLR allocation counters and `GC.GetTotalMemory(true)`. BCL `ConditionalWeakTable` tracks source ownership without keeping sources alive. Samples aggregate by stack/phase instead of growing one record per operation. BCL buffers, seven-bit integer writing and gzip supply the format primitives.

- `cpu.pb.gz` and `alloc.pb.gz` provide exclusive compiler-phase accounting, with remaining process work grouped separately.
- `cpu-stacks.pb.gz` and `alloc-stacks.pb.gz` use cooperative 100 Hz checkpoints and actual `StackTrace` names from NativeAOT. Deltas stay within the same compiler phase. Polling-location bias and unsampled tails apply; these are not asynchronous instruction samples or per-object allocation stacks.
- Heap profiles separate measured whole-process `inuse_space` from attributable `source_bytes` and `syntax_nodes`. Source counters are not presented as estimates of total object sizes. The retention test drops the tracked 4,194,304 bytes and 123 test nodes to zero after collection.

These differences from Go's statistical profiler are explicit semantic policies, not fabricated stacks or values. The phase/process profiles remain the complete accounting view; cooperative stacks provide detail. The producer has no Go or externally installed .NET runtime dependency. Go is only the independent development consumer. The thread CPU adapter is currently validated on Windows; remaining target adapters belong to the existing platform workstream.

`dotnet-gcdump` also collected an actual heap graph from the nightly NativeAOT process, but its report did not provide useful type names here. The earlier EventPipe stack-symbolization limitation remains a fact about that alternative collector; phase 1 now has a directly produced, independently consumed profiling route.

## Final validation

`npx --no-install hereby validate --all` passed: 161,228 Go tests/subtests passed, 2,152 existing skips, 813 API tests, 12 extension tests and 17 tools tests. Generation, benchmark smoke tests, self-host checks, both lint modules and formatting passed. The final log is `logs/csharp-phase-1-complete-validate-all.log`. Generated C# checks, nightly NativeAOT publication/execution, client packet decoding, `pprof` consumption and C# formatting passed separately.

The remainder of this document preserves the initial phase-0/foundation evidence. Its earlier executable and timing figures are historical and are superseded by the completion evidence above.

## Toolchain and artifacts

- SDK: user-supplied `D:\dotnet-sdk-11.0.100-rc.2.26470.103-win-x64`, version `11.0.100-rc.2.26470.103`; runtime, NativeAOT compiler/linker and `System.IO.Hashing` packages use `11.0.0-rc.2.26470.103`.
- Restore: the `dotnet11` feed in `csharp/NuGet.Config`, pinned SDK with roll-forward disabled, checked-in NuGet lockfiles, and locked restore when publishing. Final target remains .NET 11 GA.
- Publish: `win-x64`, Release, `/p:IlcInstructionSet=native`; AOT/trimming/reference-compatibility analysis and warnings-as-errors enabled, no warning suppressions. The harness verified both the native image and executing runtime.
- Host: Intel Core i7-13700K, Windows 11 Pro for Workstations. Native execution reports `Vector128=True`, `Vector256=True`, `Vector512=False`.
- Native executable: 4,091,392 bytes, SHA-256 `732e7c44a17068fb31beb2a78e000de496be21179bd80f14764db2b38f3125ff`. This is the experiment harness, not a compiler-size estimate.

Reproduction commands are in [csharp/README.md](../csharp/README.md). Raw observations, binaries, fixtures, native code map and traces are under ignored `built/csharp/`; the [checked-in evidence snapshot](../csharp/compatibility/evidence/windows-x64-native.json) preserves the measured configuration and raw timing samples. The supplied SDK supersedes the initial RC1 experiment. Earlier portable-baseline measurements are historical and are not used in this report; only host-native performance is requested going forward.

## Phase 0

`freeze-reference.mjs` requires revision `f29aeb9f825d96feea27841f3f7342dbf0df68a8` and Go 1.27.1, rejects changed reference sources, extracts a fresh archive, and builds a separate embedded oracle. The candidate never calls Go. The probe is development tooling, compiled inside that archived module so it can use the reference's internal APIs. Executable, source archive, assets, fixtures and event hashes are recorded.

The generated ledger contains 104 package directories and 5,093 Go files, including 4,546 test files. It records 262 method declarations, 187 option-name declarations, 15 literal environment-access sites and 192 native/build directives. Declaration counts are not counts of implemented server operations; dispatch boundaries and indirect environment names are explained in [contracts.md](../csharp/compatibility/contracts.md). Asset-set hashes cover test inputs, reference outputs, bundled resources/locales, generators and clients.

All 2,152 observed skipped test/subtest identities are retained: 2,145 reference exclusions, five host/environment exclusions, one missing replay, and one explicit reference skip without a supplied reason. The original complete event stream is imported with its hash; the freeze command can execute a new complete Go run instead. No new exception was added to the existing Go suites.

The 21-target matrix remains intact. Windows x64 has a published, executed NativeAOT experiment on this host. All other targets explicitly block full replacement until their compiler/runtime/interop/packaging route and execution are established. Minimum OS/CPU/libc support remains a packaging gate, including older Windows hosts. A configuration entry is not proof of an available runtime port or a working published package.

## Phase 1 implementation and verification

| Implemented slice | Evidence | Boundary |
| --- | --- | --- |
| UTF-8/WTF-8 and byte/UTF-16 maps | 3,333 inputs: empty/ASCII, multilingual and supplementary characters, all 2,048 surrogate code units, all byte values and deterministic malformed-input fuzz; every interior and boundary position compared with Go | Uses BCL UTF-8 fast paths and vectorized ASCII-run search; custom handling is limited to semantic differences. Full scanner/casing/filename semantics are not claimed. |
| JavaScript numbers | 676 pairs, 11 operations each; conversions/shifts/bitwise/remainder/special values tested | 27 exponentiation rounding differences from Go are allowed under `ecmascript-power-approximation` and independently checked against Node/V8. Each difference is recorded in `semantic-differences.json`; no custom Go transcendental implementation is retained. |
| Type-relation slice | 784 assignability comparisons against the real Go parser, binder and checker; generic and concrete C# implementations agree | C# accepts a bounded primitive/literal/union/previous-alias type-expression grammar with strict null checking. Unsupported syntax fails explicitly. It is not a general TypeScript parser, resolver, binder or checker. |
| Syntax generation | All 351 syntax kinds and markers generated from `tools/scripts/tsc/ast.json`; 58 schema exceptions inventoried; stale generation check | Full generated nodes, factories, visitors, field/hook mapping and codecs remain outstanding. |
| AST packet and hashing | 343,250 node records from seven source inputs, including the repository's large `checker.ts`, a compiler case, Unicode, malformed syntax, deep syntax and two import-bearing files; lossless node-record reencoding and independent BCL xxh3-128 checks | Parsing/binding is performed by the reference probe. String/extended/structured sections are retained, not independently regenerated. The two import-bearing files are not yet a C# resolved project graph. |
| Managed ownership | Owner-bearing typed handles, foreign-owner rejection, stable refs across growth/GC, 250,000-record iterative chain and 100,000 nested type parentheses | Real project snapshots, API leases, checker recursion and production parser/printer stacks require later tests. |
| OS and deployment | Native Windows process call, file contents and unpaired-surrogate/Unicode filename roundtrip, published startup | This does not implement the native watcher or validate every target. |

The host-native executable passes **458,098 assertions**, including scalar-reference checks for BCL SIMD byte counting and repeated AST packet reloads across collections. Truncated packets, corrupt section offsets and foreign arena ownership are rejected. The packet test found a documentation discrepancy in the Go format comment: the actual version is byte 3, the high byte of a little-endian metadata word. The C# packet reader follows the writer and client contract.

An RC1 packet-reload failure occurred once during development. It has not recurred under the supplied nightly with repeated reload/GC checks. Its original cause was not established; this is not a claim that a particular SDK fix caused the change.

## Host-native measurements

These numbers replace the initial measurements taken while the user was using the computer. The rerun uses the **same verified executable and fixtures**, with the user leaving the computer idle and no concurrent agent build, repository test, or profiler. Five sequential measurement processes each recorded 15 samples of 20 operations per workload with rotating workload order: **75 samples per workload** in total. Each row reports the median over those samples. The final column is the range of the five process medians divided by their median; it makes remaining variation visible. OS background activity was not controlled.

Allocation is thread allocation per operation, not peak RSS or retained heap. BCL count/search/UTF-8/hash code provides portable SIMD source; the executable uses the host instruction set. No portable-baseline timing comparison is included.

| Workload | Median ms | Allocated bytes | Process-median spread |
| --- | ---: | ---: | ---: |
| Class-wrapped node-record hydration/traversal | 16.27420 | 19,222,186 | 4.64% |
| Chunked node-record arena hydration/traversal | 4.23986 | 9,714,192 | 4.44% |
| UTF-8 newline count | 0.061805 | 0 | 3.78% |
| UTF-16 newline count | 0.133645 | 0 | 6.61% |
| UTF-8 to UTF-16 | 0.794615 | 6,308,880 | 5.30% |
| UTF-16 to UTF-8 | 0.534925 | 3,154,568 | 4.47% |
| Generic relation matrix | 0.005155 | 0 | 2.13% |
| Concrete relation matrix | 0.005085 | 0 | 3.05% |
| UTF-8/UTF-16 position-map construction/query | 0.069475 | 1,408 | 9.27% |

The native code map gives **190 bytes and the same code hash** for the constrained generic relation kernel and concrete kernel. Both allocate zero in the measured loop. The small timing difference does not justify retaining duplicated implementation. This is evidence for this specialization, not a guarantee about all generic instantiations.

Continue with UTF-8/WTF-8 ownership and typed managed chunks as the leading candidates. The storage experiment reduces allocation and is faster for these records. It compares a uniform record wrapped in a class with a uniform arena record; it does **not** compare fully generated heterogeneous compiler ASTs or complete parse/bind/check workloads. Therefore, do not freeze the production AST layout from these numbers alone. Hezium.Memory is not needed for these owner sizes; its conditional large-storage experiment remains available if future measurements reach ordinary array limits.

Median process launch, output capture and exit is 38.88 ms across 100 host-native launches. This includes Node/Windows process orchestration and is not isolated runtime startup or whole-compiler startup. These measurements are not C# versus Go compiler speedups, editor-latency measurements, or release performance gates. The rerun log is `logs/csharp-phase-0-1-idle-rerun.log`; individual process results and all raw samples are retained in the evidence snapshot.

## Initial profiling findings and subsequently completed gates

The initial `dotnet-trace` 10.0.745401 experiment captured the published workload and converted it to Speedscope, but had unresolved native frames. That result did not establish profiling parity. The direct native profiling implementation described above subsequently supplied the phase-1 route. See `built/csharp/profile-evidence.json` for the historical collector result and the [NativeAOT diagnostic documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/diagnostics).

The original outstanding items below are now covered for the phase-1 slice by the completion matrix above. Full grammar, product endpoint integration and all-target shipping remain the later phases already defined by the plan:

1. Complete generated typed AST payloads and hooks for a real C# parse/bind/check slice; compare class and arena layouts through that pipeline, including an actually resolved import graph and malformed/deep source behavior.
2. Encode that C# syntax into client-consumable source-file packets independently, including strings, extended data and structured MessagePack. Retaining Go-produced sections does not close the encoder gate.
3. Establish a usable NativeAOT profiling route for the supported CPU/allocation/heap operations and consumers; validate required target routes on the corresponding hosts.
4. Extend stack/ownership evidence to real parser/checker/printer recursion, snapshot sharing, cancellation, and API identity. Keep semantic differences explicit rather than demanding Go-identical incidental output.

## Repository validation

`npx --no-install hereby validate --all` passed using Go 1.27.1 and the repository-pinned Node 24.20.0/npm 11.19.1: 161,228 passing Go tests/subtests, 2,152 existing skips, 813 API tests, 12 extension tests, and 17 tools tests. Benchmark smoke tests, self-host checks, generation, both lint modules and repository formatting passed. The log is `logs/csharp-phase-0-1-validate-all.log`.

The nightly C# publish, locked restore, native execution, generated-source check and `dotnet format --verify-no-changes` passed separately. No tracked Go/npm/JS source or dependency file changed. All added work is under `csharp/` and the two rewrite documents; ignored runtime artifacts remain under `built/csharp/` and `logs/`.
