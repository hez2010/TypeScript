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
