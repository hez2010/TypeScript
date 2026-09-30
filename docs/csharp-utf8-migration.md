# UTF-8 compiler text

The C# compiler now uses `Utf8String`, a `ReadOnlyMemory<byte>` value, throughout its public and internal text APIs. Scanning, AST names and values, binding, checking, paths, configuration, hosts, diagnostics, and generated tables operate on UTF-8/WTF-8. Source ranges and slice lengths count bytes. Unescaped tokens borrow source memory; decoded escapes and generated text own byte buffers. The previous UTF-16 scanning view, `TextSlice`, and `PositionMap` are removed.

Byte operations use BCL span searches, `SearchValues<byte>`, ASCII SIMD operations, `XxHash3`, rune decoding, and UTF-8 numeric parsing and formatting. JavaScript lone surrogates remain WTF-8; malformed source bytes retain their original spelling. Surrogate-pair normalization follows the language operation being performed. Conversions to `System.String` are explicit at .NET boundaries such as Windows paths, process arguments, exceptions, and CLR metadata.

Content mappers now require `utf-8` positions. The experimental binary AST format is version 9 and rejects version 8, whose coordinates were UTF-16. The development oracle converts reference coordinates for comparison; the Go backend and JavaScript client are unchanged. Generator updates preserve the new types and literal representations when generated sources are refreshed.

JSON values and property names preserve lone-surrogate escapes without routing compiler text through UTF-16. Three `UnsafeAccessor` bindings access raw `JsonElement` bytes and the JSON writer's escaped-property-name method. These bindings passed CoreCLR and both NativeAOT builds with SDK `11.0.100-rtm.26473.115`; SDK upgrades must revalidate them.

## Correctness

The [migration validation record](../csharp/compatibility/evidence/utf8-migration-validation.json) and [final validation record](../csharp/compatibility/evidence/utf8-final-validation.json) include source and artifact hashes, suite results, and the limitations of each comparison. Normal Release and fresh stock/Satori NativeAOT builds completed with zero warnings and errors. All 11 generator checks passed.

| Check | Result |
| --- | --- |
| Semantic corpus, Release CoreCLR | 13,446 cases in each checker mode; complete diagnostics and graph hashes match Go |
| Checker API, Release CoreCLR | 31 families, 1,649,908 comparisons; zero candidate failures; four existing Go failures excluded |
| Scanner | 12,895 cases, 1,828,290 records; strict Go match |
| Regex | 23,077 cases; 30 existing permitted differences, zero failures; 11 deep-input checks |
| Source metadata | 6,778 cases plus clone, extension, and semantic assertions; zero failures |
| Parser versus saved C# baseline | 12,864 of 12,865 identical after coordinate conversion; one corrected malformed-byte spelling |
| JSDoc versus saved C# baseline | 1,228 identical cases; 80 additional Unicode line-separator cases also match |
| Satori NativeAOT safety | All 74 suites pass, including 41 parser cases with depth 21,000 |
| Satori NativeAOT hosts and resolution | 5,019 foundation assertions, 86 host assertions, 3,152 host corpus cases, 16,524 resolution cases |
| Satori NativeAOT mappers and binary AST | 44 lifecycle assertions, 774 mapper cases, 15 binary pipeline cases |

The unchanged full semantic output hash is `8390e6e546cf407bd5030dfabecd2983bd7aee283ae8d0a0678963646c6b31a4`. The parser's one changed literal now preserves raw `0x80`, matching Go, instead of storing replacement-character bytes. The parser comparison is a migration regression check against C#; it does not establish full strict Go parser parity. Existing parser and JSDoc differences remain recorded separately. The full semantic corpus and API matrix were not repeated under NativeAOT.

The migration passed the full corpus with fresh programs in both modes. After the final ASCII prefix/suffix optimization, the full corpus was repeated in both modes with syntax reuse, together with the complete API matrix and the listed native checks. The two validation records identify those source versions separately.

## Performance

The comparison uses the saved pre-migration compiler, which already includes the compact symbol table change, and the pinned Go reference. Fresh Satori NativeAOT builds use the same Satori SDK and runtime libraries, Server GC, --Ot, and native instruction selection. The managed pair uses identical Satori CoreCLR runtime files with tiering enabled. Neither .NET nor Go has a processor-count override; the machine exposes all 24 logical processors of its i7-13700K.

Each full runtime comparison uses 20 warmups and 15 measured fresh-program requests for each of 10 workloads in both checker modes. Backends alternate order. All 2,100 requests per comparison check their source graph and diagnostic count. The final batches started after 30 seconds without desktop input and recorded no input during measurement. All final workload time, memory, and correctness budgets passed. These results describe one quiet batch per runtime, not confidence intervals.

Default-mode NativeAOT medians, in milliseconds:

| Workload | UTF-16 baseline | UTF-8 | Go | UTF-8 change |
| --- | ---: | ---: | ---: | ---: |
| JSX signatures | 30.41 | 28.26 | 17.92 | -7.1% |
| Large conditional type | 70.93 | 70.01 | 62.00 | -1.3% |
| Static members | 26.43 | 24.70 | 16.65 | -6.5% |
| Node modules with JS | 25.97 | 24.46 | 17.09 | -5.8% |
| Large diagnostic program | 25.52 | 24.07 | 17.12 | -5.7% |
| 5,000 ASCII exports | 8.87 | 9.01 | 7.01 | +1.5% |
| Unicode strings | 10.35 | 9.16 | 6.82 | -11.5% |
| Unicode identifiers | 10.18 | 9.55 | 7.23 | -6.2% |
| Malformed-byte run | 8.96 | 9.46 | 6.80 | +5.6% |
| Sparse malformed bytes | 9.18 | 9.19 | 7.16 | +0.1% |

The original five workloads have 1.3–7.1% lower medians in this batch; the smallest difference is modest. Their allocation reductions are only 0.3–1.2%, while the smaller controls save 3.3–7.8%. Text migration does not remove AST, symbol, type, and checker allocations. The native ASCII control remains 1.5% slower and the malformed-byte-run control 5.6% slower, despite allocating less. NativeAOT remains 1.13–1.58× slower than Go on the original workloads.

Managed default-mode medians change from 27.28 to 26.50 ms for JSX, 57.40 to 56.22 ms for the conditional case, 23.10 to 21.87 ms for static members, 22.13 to 20.25 ms for modules, and 22.12 to 20.35 ms for the large diagnostic program. These medians are 2.1–8.5% lower than the saved baseline. Managed UTF-8 beats Go on the conditional case and trails it on the other four. Its ASCII control is 5.3% slower than the saved C# baseline, while the Unicode-string control improves from 8.91 to 6.91 ms.

The [final combined record](../csharp/compatibility/evidence/utf8-final-performance.json) retains phase data, runtime provenance, and activity observations. Separate [native](../csharp/compatibility/evidence/utf8-final-native-performance.json) and [managed](../csharp/compatibility/evidence/utf8-final-managed-performance.json) summaries include both modes, with [native samples](../csharp/compatibility/evidence/utf8-final-native-samples.jsonl) and [managed samples](../csharp/compatibility/evidence/utf8-final-managed-samples.jsonl).

## Profile-guided follow-up

The first UTF-8 build showed an ASCII regression. A fresh tiering-off CoreCLR CPU profile exposed repeated Wtf8.DecodeLast calls from case-insensitive suffix comparisons. File-extension checks during declaration checking were decoding ASCII one rune at a time. The final implementation uses BCL ASCII comparison when both compared byte windows are ASCII, retaining the existing rune path for Unicode. Added checks cover vector boundaries, Unicode outside the comparison window, and case pairs with different UTF-8 byte lengths.

A separate 50-sample NativeAOT comparison measured the ASCII fast path against the first UTF-8 build: total median time fell from 9.75 to 9.22 ms, a 5.5% reduction, and checking fell from 5.29 to 4.73 ms. See the [focused comparison](../csharp/compatibility/evidence/utf8-final-focus-performance.json) and [samples](../csharp/compatibility/evidence/utf8-final-focus-samples.jsonl). These are separate batches from the full comparison above.

The [CPU profile record](../csharp/compatibility/evidence/utf8-cpu-profiles.json) contains actual ETW CPU samples restricted to compilation intervals. In the large diagnostic program, 8,522 of 13,923 samples with compiler frames contain parser frames, 3,737 contain binder frames, and 1,529 contain checker frames. Scanner operations, keyword classification, and binding remain distributed costs. These profiles use CoreCLR with tiering off, before the ASCII follow-up; sample shares are not elapsed-time shares or NativeAOT measurements.

## Remaining cost and retained evidence

The final native phase measurements still put most of the original-workload gap in program construction. For the large diagnostic program, UTF-8 construction takes 20.78 ms versus Go's 15.50 ms; checking takes 3.22 ms versus 1.05 ms. The conditional workload has nearly matching checking time, 46.35 ms versus 46.12 ms, but construction takes 23.42 ms versus 15.56 ms. Separate phase medians need not sum exactly to the total median. The C# factory still allocates individual class nodes; Go uses typed arenas. That remains a structural optimization lead, not a measured replacement in this change.

The [earlier measurements](../csharp/compatibility/evidence/utf8-migration-performance.json) preserve excluded desktop-activity batches and the first UTF-8 build's quiet results. Both early quiet runs failed one provisional 16 MiB post-collection heap-growth budget: the native conditional case and managed JSX case. A low first measured sample preceded a stable higher plateau. Those failures remain in their original summaries. Independent 200-request follow-ups passed the budget: growth was 12.6 KiB for native UTF-8 versus 21.2 KiB for its baseline, and 61.5 KiB for managed UTF-8 versus 65.1 KiB for its baseline. Their [native](../csharp/compatibility/evidence/utf8-native-retention-samples.jsonl) and [managed](../csharp/compatibility/evidence/utf8-managed-retention-samples.jsonl) samples show no accumulating retention in those bounded runs. The final builds passed all 20 workload groups in each runtime comparison without changing the budgets.

## Constant-folding follow-up

The next pass starts from the committed UTF-8 implementation, `245c22d700843f12175f4af764034f98a01d8d82`. Fresh CPU profiles identified keyword classification and declaration modifier checks as candidates. Disassembly then exposed two concrete code-generation problems:

- The single generated `TokenFacts.FromText` method retained calls to span constructors and `SequenceEqual` for constant UTF-8 literals. Partitioning it by token length lets the JIT fold those comparisons into constant comparisons in the inspected helpers. The complete NativeAOT classifier, including its dispatcher and all helpers, shrinks from 9,258 to 6,783 bytes.
- Declaration modifier checks represented token kinds with `UInt128` shifts. Reusing the existing `ModifierFlags` mapping removes those operations and lets combined masks fold. NativeAOT code for `DeclarationModifiers` shrinks from 9,895 to 7,703 bytes. Diagnostic ordering is preserved. The isolated measurements do not establish a separate whole-program speedup for this change; it is retained for the smaller representation and generated code.

The [profile and native code record](../csharp/compatibility/evidence/utf8-optimization-profiles.json) includes before/after CPU profiles and complete NativeAOT method sizes. Profiles use the same Satori CoreCLR with tiering disabled and count samples only inside compilation intervals. They identify costs; they are not NativeAOT elapsed-time measurements.

Additional scanner experiments were rejected. Splitting, inlining, and outlining UTF-8 code-point decoding reduced some code sizes but gave mixed whole-program results. A BCL SIMD search for runs of ASCII whitespace regressed most workloads. The [experiment record](../csharp/compatibility/evidence/utf8-optimization-experiments.json) preserves those results, the identical-binary control, and the original budget outcomes. No scanner change from those experiments is retained.

### Repeated NativeAOT measurements

The [repeated comparison](../csharp/compatibility/evidence/utf8-optimization-replications.json) uses six fresh process pairs per workload, with the original and optimized binaries exchanged between the two process positions in three pairs. Each process receives 30 warmups and 20 measured fresh-program requests in default checker mode. The pinned SDK, Satori runtime libraries, Server GC, native instruction selection, and Go reference are unchanged. All 24 logical processors remain available. All 9,000 requests passed correctness and memory budgets, with no desktop input recorded during measurement.

Times below are medians of the six process medians, in milliseconds. The reduction column is the median of the six paired percentage reductions, so it need not equal the percentage calculated from the aggregate time columns.

| Workload | Committed UTF-8 | Follow-up | Go | Paired reduction |
| --- | ---: | ---: | ---: | ---: |
| JSX signatures | 28.13 | 27.65 | 17.73 | 1.8% |
| Large conditional type | 68.98 | 68.46 | 60.16 | 0.6% |
| Static members | 24.72 | 24.29 | 17.10 | 2.1% |
| Node modules with JS | 24.49 | 24.06 | 17.19 | 1.6% |
| Large diagnostic program | 23.78 | 23.30 | 16.27 | 2.5% |
| 5,000 ASCII exports | 9.39 | 8.83 | 6.81 | 6.6% |
| Unicode strings | 9.30 | 8.75 | 6.83 | 5.7% |
| Unicode identifiers | 9.64 | 9.27 | 7.06 | 4.4% |
| Malformed-byte run | 9.37 | 8.93 | 6.81 | 4.3% |
| Sparse malformed bytes | 9.52 | 8.94 | 6.80 | 5.0% |

Program construction improves in 59 of 60 process pairs, with median paired reductions of 1.9–2.5% on the original workloads and 4.5–5.1% on the controls. Total times vary more: JSX and static members improve in all six pairs, while each other original workload improves in four. Identical NativeAOT binaries differed by up to 4.6% in the earlier control, and the tiered CoreCLR control differed by up to 9.8%. These six-pair results provide repeated evidence, not confidence intervals. Allocations are essentially unchanged.

The [full runtime comparisons](../csharp/compatibility/evidence/utf8-optimization-performance.json) also include both checker modes under NativeAOT and CoreCLR with tiering enabled and disabled, using 60 warmups and 30 measured requests. All final workload groups passed their budgets. Managed comparisons have one process pair each and should be read with the observed control variation. The same record contains the isolated modifier comparison and NativeAOT identical-binary control. Compressed request samples accompany the records.

The gap to Go is smaller but remains substantial: NativeAOT takes 1.14–1.56 times Go's elapsed time on the original workloads. For the large diagnostic program, construction takes 20.23 ms versus Go's 15.17 ms and checking takes 3.23 ms versus 1.04 ms. The conditional workload takes 22.84 versus 15.11 ms for construction, while checking is close at 45.63 versus 45.37 ms. Separate phase medians do not necessarily sum to the total median. Per-node allocation and the distributed parser/binder costs remain leads for further work; this pass does not establish an arena-allocation speedup.

### Follow-up validation

The [validation record](../csharp/compatibility/evidence/utf8-optimization-validation.json) ties results to the final source and binaries. Release CoreCLR passed all 13,446 semantic cases with fresh programs in each mode, with the unchanged full output hash, and all 1,649,908 comparisons across 31 API families. The four pre-existing Go API failures remain excluded. Fresh stock and Satori NativeAOT builds completed with zero warnings and errors and passed their runtime checks and 5,019 foundation assertions. Satori passed all 74 safety suites and all 12,895 strict scanner cases; stock also passed the 41-case parser safety suite. Full semantic and API matrices were not repeated under NativeAOT.

One exploratory tiered modifier batch failed the retained-heap growth budget; its failure remains in the experiment record. A separate 200-request follow-up showed bounded alternating heap levels for both versions and passed the budget. That follow-up ran alongside validation, so its elapsed times are excluded from performance conclusions.
