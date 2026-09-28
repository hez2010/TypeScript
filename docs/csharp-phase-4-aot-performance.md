# NativeAOT compiler performance investigation

This pass compares fresh program construction, binding and semantic checking against the pinned Go compiler. NativeAOT removes JIT compilation from the comparison. The baseline is `7debf3322c`; both native builds use the selected .NET 11 SDK, native instruction selection, Server GC and four visible processors.

## Repeated work and dispatch

The large conditional-type workload exposed a costly intersection reduction. For each candidate type, the compiler searched a large union by asking whether each earlier member was a subtype. When the union already contains the exact candidate, identity answers that question immediately. The compiler now checks membership first, using BCL span binary search over canonical order. Scalar literal comparisons use their value and existing identity tiebreak directly, without the general comparison stack. The compiler also uses the existing `PrimitiveUnion` classification instead of repeatedly inspecting every constituent to rediscover that all of them are primitive.

An isolated instrumented run establishes the work removed:

| Operation | C# before | C# after | Go |
|---|---:|---:|---:|
| Relation entry calls | 2,013,009 | 12,009 | 2,013,004 |
| Simple relation calls | 2,031,036 | 32,036 | 2,031,031 |
| Deep relation comparisons | 22,033 | 22,033 | 22,033 |
| Structural comparisons | 18,013 | 18,013 | 18,013 |
| Composite-property constructions | 4,002 | 4,002 | 4,002 |

In the corresponding uninstrumented NativeAOT control, default-mode elapsed time fell from 152.83 to 104.40 ms. Allocation remained approximately 72.7 MB and GC pauses remained approximately 22 ms. This isolates a substantial improvement in computation and dispatch, rather than an allocation-driven improvement. That intermediate candidate still trailed Go's 60.79 ms.

Type-order comparison was another source of repeated work: C# performed 75,522 comparisons where Go performed 20,400. Checking whether input is already ordered lowered the C# count to 37,894. Combining an existing union with another type or union now merges ordered constituents directly. The existing comparer still controls canonical order.

Relation kinds use indexed storage instead of an enum-keyed dictionary. Primitive rejections precede cache access, and ordinary targets no longer enter the unknown-like-union resolver. Sparse-link probes avoid creating empty state records, and new link insertion uses the BCL single-lookup dictionary API. Symbol rollback snapshots retain immutable declaration arrays directly.

## Parsing, binding and scheduling

The scanner formerly tried up to three full token-text lookups for punctuation. Generated longest-prefix punctuation dispatch now selects the operator directly. Keyword lookup first selects the text length and initial character, avoiding full identifier hashing. The generator continues to derive spellings from the pinned token data. Greater-than rescanning, JSX closing tags and malformed-escape recovery retain their existing behavior.

Generated AST accessors replace repeated interface checks for names, modifiers and function signatures. Parent attachment visits the actual child fields directly. Tokens take a synchronous binding path; recursive syntax retains the execution-stack check and fresh-stack continuation. Binding ownership uses an identity stamp, avoiding a reference back to the entire binding from every node.

Position conversion now reuses the last Unicode interval when both bounds still match. The interval is a hint, so nonmonotonic and concurrent accesses to shared source text remain valid. Parser list buffers and intersection traversal storage are cleared and reused with exclusive ownership. Symbol tables reserve capacity using existing syntax lists, and algebra operations use spans instead of temporary array copies.

Program loading previously bounded outstanding results by the worker count. A slow first result could therefore block publication while completed workers had no further work admitted. All known files can now queue independently; a semaphore bounds active parsing and binding, and results still publish in discovery order. The regression test blocks the first file until the third file starts with a concurrency limit of two. It also verifies graph order and complete binding.

## Measurement controls

Twenty warmups precede fifteen measured requests for each native backend, workload and mode. Backends alternate. Every request checks the source-graph hash and diagnostic count. Builds, profiling and correctness validation run outside timing batches. Ordinary mouse and keyboard activity remains allowed.

The benchmark runner now fingerprints the executable that actually ran, distinguishes NativeAOT from CoreCLR, records the reported GC mode and rejects a requested Server GC run if the process is using Workstation GC. The previous native binary ignored the runtime Server GC environment setting; native publishing must select Server GC explicitly. CoreCLR comparisons fingerprint the managed entry assembly and compiler as well as the app host.

Diagnostic controls were kept separate from retained results:

- Conventional async was 30–53% slower under NativeAOT; native async remains enabled.
- Recursion-stack pooling reduced allocation but regressed elapsed time and was reverted.
- Disabling size-oriented LINQ had no useful conditional-workload improvement and was not retained as a project setting.
- A workload-only executable using the identical compiler and harness did not materially outperform the full compatibility executable. Test-harness reachability was not established as the cause of the gap.

Native EventPipe samples required matching PDBs for symbol resolution. They also showed severe suspension-point bias: the apparent `Array.Copy` hotspot was consistently the return address immediately after `RhpGcPoll`. Those percentages are not measurements of copying time. A separate process-local sampler captured actual instruction pointers from running threads, filtered to the harness's compilation timestamps. Its samples identified relation dispatch, scanning and GC work as investigation leads; uninstrumented paired measurements establish the reported gains.

The per-file diagnostic probe still showed a construction gap before the last scanner and binding refinements: `lib.dom.d.ts` took roughly 17.93 ms to parse and 6.34 ms to bind in C#, versus 9.39 and 3.44 ms in Go. These probes are diagnostic, not the retained end-to-end comparison.

## Results and validation

The retained compiler still does not meet the faster-than-Go target. The following are median elapsed milliseconds from the final matched NativeAOT comparison.

| Mode | Workload | Before | After | Go | Improvement |
|---|---|---:|---:|---:|---:|
| single | JSX signatures | 49.11 | 41.34 | 20.76 | 15.8% |
| single | Large conditional type | 167.24 | 95.33 | 62.45 | 43.0% |
| single | Static members | 51.77 | 40.81 | 18.96 | 21.2% |
| single | Node modules with JS | 50.03 | 40.26 | 19.64 | 19.5% |
| single | Large diagnostic program | 45.39 | 38.51 | 17.69 | 15.2% |
| default | JSX signatures | 47.54 | 39.19 | 18.12 | 17.6% |
| default | Large conditional type | 165.97 | 92.75 | 62.26 | 44.1% |
| default | Static members | 49.86 | 38.16 | 16.54 | 23.5% |
| default | Node modules with JS | 47.88 | 38.12 | 16.48 | 20.4% |
| default | Large diagnostic program | 44.68 | 36.65 | 16.12 | 18.0% |

The separate self-contained CoreCLR comparison uses 100 warmups and fifteen samples, with tiered compilation enabled. Satori was installed by replacing the supplied `coreclr.dll`, `clrjit.dll` and `System.Private.CoreLib.dll`; no GCName setting was used. Its runtime, JIT and CoreLib differ from the SDK distribution, so this is a runtime-distribution comparison, not an isolated GC substitution.

| Mode | Workload | Server GC | Satori | Go |
|---|---|---:|---:|---:|
| single | JSX signatures | 33.51 | 30.08 | 20.91 |
| single | Large conditional type | 76.30 | 66.86 | 63.84 |
| single | Static members | 34.99 | 27.39 | 19.42 |
| single | Node modules with JS | 34.78 | 26.57 | 18.80 |
| single | Large diagnostic program | 34.00 | 25.99 | 17.39 |
| default | JSX signatures | 33.15 | 28.78 | 18.06 |
| default | Large conditional type | 77.45 | 65.42 | 60.37 |
| default | Static members | 32.79 | 25.14 | 16.90 |
| default | Node modules with JS | 32.41 | 24.73 | 16.53 |
| default | Large diagnostic program | 30.52 | 24.28 | 16.28 |

Correctness passed 13,446 semantic configurations in each mode, 1,649,908 query comparisons across 31 API families, 74 safety suites, 6,789 type/algebra cases, 12,882 parser cases / 1,684,933 records, 12,895 scanner cases / 1,828,290 records, 23,152 host comparisons and 4,778 foundation assertions. The full semantic output hash remains `8390e6e546cf407bd5030dfabecd2983bd7aee283ae8d0a0678963646c6b31a4`. Four preexisting Go query failures remain separately identified; there were no candidate failures. Source-specific validation reuse and affected reruns are recorded in the evidence. NativeAOT builds completed without warnings or errors.

Evidence: [performance and controls](../csharp/compatibility/evidence/phase4-aot-structural-performance.json), [validation](../csharp/compatibility/evidence/phase4-aot-structural-validation.json), [native samples](../csharp/compatibility/evidence/phase4-aot-structural-samples.jsonl), [runtime samples](../csharp/compatibility/evidence/phase4-aot-structural-runtime-samples.jsonl).

The focused scanner and source-metadata test projects now reference the production compiler instead of maintaining stale linked-source dependency lists. Normal and AOT lock files cover the resulting project dependencies.

No AST, program or checker-result cache was added to bypass fresh compilation. Raw measurements, controls, operation-count instrumentation and profiles are retained under `built/aot-performance`; build and validation logs are under `logs/aot-*`.
