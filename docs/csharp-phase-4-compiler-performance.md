# Compiler storage and execution performance

This pass reduces elapsed time by **18-38%** and measured allocation by **36-47%** against the validated struct checkpoint `811abc5f44`. It **does not meet the requested faster-than-Go target**: the retained compiler takes approximately **1.9-2.5 times Go's elapsed time** on these fresh-program workloads.

## Measured causes and retained changes

The gap has several causes rather than one switch that removes it. Direct GC counters showed roughly 18-21 ms of pauses in the construction-heavy probe and about 36 ms in the conditional-type probe before the source-buffer changes. These collections explain some earlier approximately 15 ms jumps between the construction and checker phases; they should not all be attributed to scheduling noise.

The file-loading path copied the filesystem buffer during encoding normalization and copied it again into `SourceText`. Bundled libraries were also decoded repeatedly. The host now transfers owned buffers directly when no encoding transformation is necessary. Immutable bundled `SourceText` instances are shared, while every fresh program still parses, binds and checks its own trees. Public source constructors retain copy semantics for caller-owned spans, and public filesystem reads still return owned buffers. In the isolated `giant` probe, the full collection disappeared and elapsed time fell from about 57 ms to 43 ms. The conditional workload still had substantial GC cost.

Binding state previously required a separate node-index dictionary and storage records. It now lives in generated slots on the syntax nodes that need each semantic role. `NodeBinding` is a small readonly view; clones clear their semantic slots, and a published binding identifies which source tree owns them. Symbol declarations use BCL immutable arrays instead of a mutable list, an oversized first backing array and a read-only wrapper for every symbol. Flow antecedent collections are lazy, and active labels reuse one list with scope boundaries.

The binder now accumulates parse-error flags during traversal and bypasses container setup for ordinary nodes, following the pinned Go binder's control flow. It dispatches hot cases using syntax kinds, inserts new symbols with the BCL single-lookup dictionary API, and traverses node lists without allocating enumerators. Fixed internal names are compile-time literals. The parser reuses static lookahead callbacks and avoids a full-tree import-meta search when its existing source flag proves no import-meta was parsed.

Conditional checking was allocating generic array-backed keys even for a single alias-free type ID, plus arrays and wrappers for single-parameter mappers. Those cases now use the ID directly and a compact mapper. Mapper continuation stacks and relation-session scratch collections are reused with exclusive ownership and cleanup. Completed-result caching remains context-owned.

A large additional cost came from logging every relation-cache write for exact rollback. Failed comparisons now invalidate that cache and let later requests recompute. Successful comparisons no longer allocate and maintain a second dictionary of writes. Cancellation no longer promises to retain completed cache entries from before an interrupted operation; semantic results and recovery still have to be correct. This is an intentional compatibility change, with cancellation tests updated to check invalidation and successful recomputation.

The identifier-dictionary microbenchmark used the actual 36,271 identifiers from `lib.dom.d.ts`. The existing BCL XxHash3 implementation was about 13% faster than the prior hash in that isolated workload. Text-slice hashing now uses it with a per-process seed; case-insensitive hashing retains the BCL ordinal-ignore-case behavior. Canonical types use their already assigned IDs for identity hashes. These microbenchmark results are leads, not standalone claims about total compiler speed.

Normal builds no longer inherit the runtime feature switches associated with `PublishAot=true`, such as size-oriented LINQ behavior and disabled dynamic-code support. Native publishing is explicit in the scripts. Both sides of the retained source comparison use matching normal Release runtime configuration; the old binaries themselves are unchanged.

## Retained workload comparison

| Mode | Workload | Before | After | Go | Time change | Allocation change |
|---|---|---:|---:|---:|---:|---:|
| single | JSX signatures | 62.79 | 38.78 | 20.61 | -38.2% | -44.3% |
| single | Large conditional type | 150.02 | 122.61 | 64.36 | -18.3% | -36.8% |
| single | Static members | 62.33 | 39.75 | 18.92 | -36.2% | -46.4% |
| single | Node modules with JS | 51.73 | 42.17 | 18.95 | -18.5% | -46.9% |
| single | Large diagnostic program | 58.78 | 38.21 | 17.92 | -35.0% | -46.1% |
| default | JSX signatures | 61.20 | 38.60 | 17.79 | -36.9% | -43.0% |
| default | Large conditional type | 148.83 | 117.33 | 62.63 | -21.2% | -36.3% |
| default | Static members | 60.21 | 40.63 | 16.44 | -32.5% | -44.4% |
| default | Node modules with JS | 60.73 | 40.76 | 16.41 | -32.9% | -44.9% |
| default | Large diagnostic program | 59.81 | 38.29 | 16.00 | -36.0% | -44.8% |

Times are medians in milliseconds. All runs use the selected .NET 11 SDK, normal Release/CoreCLR, Server GC, four visible processors, enabled tiered compilation and runtime-default PGO. Twenty warmups precede fifteen measured requests per backend/workload/mode. The baseline, candidate and pinned Go oracle alternate, and every request checks the source-graph hash and diagnostic count. Builds, profiling and validation do not overlap these measurements. Ordinary machine use remains allowed as requested.

The original baseline runtime configuration enabled AOT feature switches even under CoreCLR. For this comparison, `built/compiler-model/baseline-normal` contains the unchanged `811abc5f44` binaries with the same normal runtime configuration as the candidate. This avoids crediting a configuration-only difference to source changes.

## Controls and remaining costs

Replacing native async with conventional C# async did not yield a useful improvement. Both variants compiled from equivalent source with tiering enabled; conventional async changed timings by about -1% to +4%. A synchronous binder with a safe fresh-stack fallback also showed no meaningful gain and was reverted. Those controls are retained rather than silently discarded.

Disabling GC adaptation reduced collection frequency but did not close the CPU gap, so that setting was not retained. The initial full-inline binding prototype reduced allocation but inflated every syntax node; the retained generator stores semantic fields according to node roles instead.

The remaining gap is substantial. The fresh-program suite still spends about 34-41 ms constructing the C# program on most cases, versus roughly 15-18 ms for Go. The conditional workload adds about 84-87 ms of checking, versus about 45-46 ms for Go. Individual phase medians are calculated separately and need not sum to the median total.

Profiles of the evolving implementation continue to show symbol-table allocation/growth, parser object creation and type-relation/property construction. Go's parser uses arena storage and its semantic representation has fewer separate managed objects and collection layers. The new changes reduce that difference but do not establish that the remaining gap is attributable entirely to allocation, GC, or the runtime. Further architectural work needs another controlled profile rather than assuming that a larger rewrite will be faster.

No whole-AST, program or checker-result cache was added to bypass the fresh-compilation workload. Sharing immutable library text avoids redundant source decoding; it does not skip parsing, binding or checking. Cold-start behavior was not benchmarked here.

## Correctness and build verification

Normal Release validation passed **13,446 semantic configurations in each mode**, **1,649,908 query comparisons across 31 API families**, **74 safety suites**, **6,789 type/algebra cases**, **12,882 parser cases / 1,684,933 records**, **23,152 host comparisons**, and **4,777 foundation assertions**. Both semantic modes produced the unchanged full-output hash `8390e6e546cf407bd5030dfabecd2983bd7aee283ae8d0a0678963646c6b31a4`. Four existing Go query failures remain separately recorded.

The cancellation tests exposed one outdated exact-cache-retention assertion and a pre-cancelled elaboration path that rented a session before checking cancellation. The assertion now tests invalidation/recomputation; pre-cancelled elaboration rejects immediately. Only the affected assignability and relation-context checks were repeated.

NativeAOT code generation completed once with zero warnings/errors and the executable was not run. An earlier restore-only failure exposed the difference between normal and AOT dependency sets. Separate lock files now support both configurations, and both locked restores pass. The compiler library keeps its managed-library build settings when the executable is published as AOT.

Evidence: [measurements and controls](../csharp/compatibility/evidence/phase4-compiler-model-performance.json), [raw retained samples](../csharp/compatibility/evidence/phase4-compiler-model-samples.jsonl), and [validation](../csharp/compatibility/evidence/phase4-compiler-model-validation.json).

The retained measurements, intermediate controls, profiles, per-file probes and build logs are saved under `built/compiler-model` and `logs/compiler-*`. The checked-in evidence links below contain result summaries and the raw retained samples.
