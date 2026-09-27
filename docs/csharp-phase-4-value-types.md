# Struct storage and the remaining Go performance gap

This pass converts 21 types to structs. The high-volume changes are `NodeBinding`, `TypeCacheKey`, `RelationKey`, and relation-cache entries. The other 17 are immutable records for source metadata, parse options, labels, filesystem entries, project references, command-line results, resolution traces/results, mapper timings/presentation, pattern modules and merge snapshots.

Binding values use the existing chunked `Arena<T>` storage with a node-to-slot dictionary. Internal mutation obtains a stable reference to a slot; dictionary growth cannot invalidate that reference. `BoundSourceFile.Get` now returns a nullable value snapshot. Small files cap chunk capacity at their syntax-node count. The published syntax, symbol and flow graphs retain their existing reference identity.

Ordinary relation keys now store the two type references directly. They no longer allocate a list, its backing buffer, a separate key object or the generic writer's closure. Generic keys retain their normalized part sequence. Struct relation-cache entries carry a write version so cancellation rollback still distinguishes two writes of the same result. Tests cover key equality/hashing, later-write protection, restoration of an earlier entry, missing bindings and references surviving storage growth.

The public metadata records and binding snapshots are API changes. Large, nullable AST lists and objects whose identity or mutable state is shared remain classes; making those values would require a separate representation change and could introduce boxing, copying or larger empty fields.

## Measurements

Allocated bytes fall **1.5–5.0%** across the ten workload/mode groups. The large conditional case is **5.5–7.0% faster** in the retained batch. Other timing changes range from 22.2% lower to 0.5% higher and include the unstable module/checker phase discussed below. Times are median milliseconds; negative changes mean less time or allocation.

| Mode | Workload | Before | After | Go | Time change | Allocation change |
|---|---|---:|---:|---:|---:|---:|
| single | JSX signatures | 63.04 | 62.39 | 20.66 | -1.0% | -1.9% |
| single | Large conditional type | 160.00 | 148.87 | 63.18 | -7.0% | -5.0% |
| single | Static members | 65.90 | 62.41 | 19.27 | -5.3% | -1.6% |
| single | Node modules with JS | 66.71 | 51.93 | 18.64 | -22.2% | -1.6% |
| single | Large diagnostic program | 61.56 | 59.02 | 17.98 | -4.1% | -1.8% |
| default | JSX signatures | 66.44 | 66.79 | 17.85 | 0.5% | -1.8% |
| default | Large conditional type | 162.36 | 153.44 | 61.94 | -5.5% | -4.9% |
| default | Static members | 63.10 | 61.17 | 16.96 | -3.1% | -1.5% |
| default | Node modules with JS | 67.30 | 59.41 | 16.39 | -11.7% | -1.6% |
| default | Large diagnostic program | 61.35 | 58.22 | 15.87 | -5.1% | -1.8% |

The retained comparison uses the Release binaries from `e730c3bdd5` as its baseline, the selected .NET 11 SDK, Server GC, four visible processors, enabled tiering and runtime-default PGO. There are 20 warmups and 15 measured requests for each workload/mode/backend. The baseline, candidate and pinned Go oracle alternate. Every request checks the graph hash and diagnostic count. No profiling, build or validation run overlaps the timing batches. Normal machine use continues as authorized.

The first batch found excess allocation from full-sized binding chunks on small files. The retained version bounds those chunks using the known node count. Both batches are preserved. The first batch's single-mode module workload was 22.0% slower; the retained batch was 22.2% faster. These opposing checker-phase steps were present in earlier passes too, and this work does not establish their cause. They should not be credited to the struct conversion. The conditional-type improvement is more consistent across both batches; the allocation reductions are also consistent after correcting small-file capacity.

## Where the gap remains

For the construction-heavy cases, the retained .NET program-construction medians are approximately 46-60 ms against Go's 15-18 ms. The large conditional case additionally spends about 93-94 ms checking against Go's 45-46 ms. Program/checker/total medians are calculated separately and need not sum exactly.

A separate per-file probe parses and binds identical existing source text in warm processes. It excludes source decoding, graph discovery and checking. Both implementations use Windows QueryPerformanceCounter for these small intervals. The initial Go `time.Now` probe returned zero for many short operations; that result is retained separately and its timings are not used below.

| `lib.dom.d.ts` operation | .NET before | .NET retained | Go | Retained .NET / Go |
|---|---:|---:|---:|---:|
| Parse | 16.66 ms | 16.99 ms | 9.48 ms | 1.79x |
| Bind | 14.13 ms | 14.29 ms | 3.49 ms | 4.09x |
| Parse allocation | 15.30 MB | 15.30 MB | 10.13 MB | 1.51x |
| Bind allocation | 21.16 MB | 20.16 MB | 5.29 MB | 3.81x |

The struct conversion saves binding memory, but this isolated probe does not demonstrate faster binding. Across all files in that graph, .NET binding allocation falls from 25.85 MB to 24.77 MB; Go uses about 6.57 MB. Removing object headers alone cannot account for the remaining difference.

The saved-baseline EventPipe profiles locate the remaining work. Among CPU samples with managed compiler frames, the construction-heavy case attributes about 51% to binding and 29% to parsing. `Binder.Declare`, node-state lookup, dictionary initialization/resizing and list growth are prominent. In the conditional case, checking accounts for about 66%; relation-key construction, type-key value arrays, type instantiation and associated collections remain significant. Allocation ticks are sampled estimates, include harness work and cannot be treated as exact per-type compiler byte totals. The per-file and end-to-end allocation counters above are measured separately.

The source comparison supports those observations. Go stores symbol, local-table and flow references directly in relevant AST payloads (`DeclarationBase`, `LocalsContainerBase`, `FlowNodeBase`) and allocates parser nodes/lists through arenas. The C# compiler preserves separately owned binding state, requiring a node index and per-node records; symbols also carry owned collection state and read-only views. The parser still creates individual AST/list objects and speculation delegates. Conditional checking still builds type-ID arrays and traversal/instantiation collections even after eliminating the ordinary relation-key allocations.

These measurements identify binding representation and collection growth, parser object construction, and conditional-type bookkeeping as substantial costs. They do not assign every millisecond of the remaining end-to-end gap: source conversion, graph resolution, scheduling, JIT activity and GC were not independently isolated. The profiles exclude harness-only and GC-only stacks from the displayed compiler percentages, so those percentages are not a GC budget. This pass narrows the gap; it does not close it.

## Validation and evidence

Normal Release validation passed **13,446 semantic configurations in each mode**, **1,649,908 API comparisons across 31 families**, **74 safety suites**, **6,789 type/algebra cases**, **12,882 parser cases / 1,684,933 records**, and **23,152 host comparisons**. Both semantic modes retain output hash `8390e6e546cf407bd5030dfabecd2983bd7aee283ae8d0a0678963646c6b31a4`. Four known Go API query failures remain separately recorded.

The NativeAOT check is deferred until the end of the continued performance work, as requested; no NativeAOT binary has been executed.

Evidence: [measurements and profiles](../csharp/compatibility/evidence/phase4-value-types-performance.json), [validation](../csharp/compatibility/evidence/phase4-value-types-validation.json), [retained samples](../csharp/compatibility/evidence/phase4-value-types-samples.jsonl), and [initial samples](../csharp/compatibility/evidence/phase4-value-types-initial-samples.jsonl).

The source, traces, isolated probes and intermediate outputs are retained under `built/value-performance`. The workload comparison can be repeated with `csharp/tools/checker-workloads.mjs`, `--baseline-directory built/value-performance/baseline`, `--manifest built/parallel-performance/manifest.json`, `--include-go`, and `--server-gc`. The selected SDK is resolved through `DOTNET_ROOT`; no SDK version lock was added.
