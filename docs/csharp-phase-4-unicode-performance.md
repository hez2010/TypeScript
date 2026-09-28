# Unicode compiler performance pass

This pass investigates UTF-8 decoding and the byte-position contract against the compiler committed in `84fe388abb`. C# still trails Go overall. The retained changes improve the original NativeAOT workloads by about 1–4%, and the Unicode/malformed controls by about 2–8%. The ASCII control is essentially unchanged; its small elapsed increase is shown below.

## Costs found and changes retained

One non-ASCII character formerly triggered a complete AST walk that called a delegate for each position and repeatedly dispatched through child counts and indexed child access. An isolated normal Release probe measured this remapping at 1.40 ms in an 11.00 ms `lib.dom.d.ts` parse. Generated conversion now enqueues the actual child fields directly, remains iterative for deep syntax, and uses separate start/end interval hints so one endpoint does not evict the other's nearby mapping.

The position map formerly decoded every non-ASCII sequence again and grew three temporary integer lists. SourceText now shares the decoder's validation result. Known-valid UTF-8 headers are counted with portable `Vector<byte>` operations, then stored in one exactly sized array of byte/character boundaries. Adjacent Unicode characters avoid repeated ASCII searches. Malformed bytes that do not change the offset delta do not create useless boundaries. ASCII source views share an identity map.

The malformed decoder formerly used scalar Rune decoding for the entire file after finding any error. It now resumes BCL bulk transcoding over valid runs, uses SIMD SearchValues and Span.Fill for runs of invalid leading bytes, and rents temporary character storage. It preserves WTF-8 surrogate values and one replacement character per malformed byte. The first resume-only candidate doubled the cost of an all-invalid run; that candidate was rejected and run recovery was added.

These choices draw on [SimdUnicode's vector classification/counting](https://github.com/simdutf/SimdUnicode/blob/main/src/UTF8.cs) and [bulk repair kernels](https://github.com/simdutf/SimdUnicode/blob/main/src/UTF16.cs). The implementation uses .NET's portable APIs, including [Utf8.ToUtf16](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Text/Unicode/Utf8.cs). Its lossless WTF-8 contract requires retaining lone surrogates, so SimdUnicode's UTF-16 replacement behavior cannot be substituted directly.

## Primitive probes

Normal Release, Server GC, tiering enabled, four processors, 30 warmups and 15 samples. The decoder/map probes batch 20 operations per sample. Processes ran sequentially; these figures identify large primitive costs, while the paired complete-workload comparison below establishes end-to-end results.

| Operation | Before ms | After ms | Allocated bytes before → after |
|---|---:|---:|---:|
| Dense Unicode position map | 3.580 | 0.417 | 3,987,938 → 560,105 |
| Dense Unicode parse | 7.540 | 6.174 | 7,612,824 → 7,612,568 |
| Decode 10,000 malformed bytes | 0.0529 | 0.00173 | 80,137 → 20,057 |
| Decode one malformed byte amid 200 KB ASCII | 1.536 | 0.0466 | 1,600,624 → 400,057 |

Standalone public PositionMap construction has a tradeoff: validation and exact sizing add passes for sparse Unicode. The `lib.dom.d.ts` map probe rose from 0.040 to 0.122 ms while allocated bytes fell from 4,689 to 761. SourceText's byte constructor shares validation already performed by its decoder, and bundled source views are reused between warm benchmark requests. Unchanged valid decoding showed allocation/timing variation across probe batches and is not claimed as a retained speedup.

## Complete NativeAOT comparison

The same .NET 11 SDK, native instruction selection, Server GC and four visible processors are used before and after. Twenty warmups precede fifteen measured requests per backend/case/mode, and backends alternate. Every request matches the source graph and diagnostic count. Correctness runs, profiling and builds finish before timing. Both modes are retained in the evidence; these are default-mode medians in milliseconds.

| Workload | Before | After | Go | Improvement |
|---|---:|---:|---:|---:|
| JSX signatures | 39.19 | 38.32 | 17.95 | 2.2% |
| Conditional types | 93.70 | 91.28 | 60.69 | 2.6% |
| Static members | 38.23 | 36.97 | 16.48 | 3.3% |
| Node modules with JS | 37.28 | 37.01 | 16.66 | 0.7% |
| Large diagnostic program | 35.52 | 34.45 | 16.06 | 3.0% |
| ASCII control | 19.40 | 19.63 | 6.97 | -1.2% |
| Unicode strings | 21.61 | 20.76 | 6.92 | 3.9% |
| Unicode identifiers | 21.83 | 21.25 | 6.98 | 2.7% |
| Malformed byte run | 20.70 | 19.49 | 6.84 | 5.8% |
| Sparse malformed byte | 21.47 | 19.81 | 6.86 | 7.7% |

The five controls contain 5,000 exported constants and a small supplied library. They isolate ASCII, Unicode string/identifier and malformed-comment costs; they do not replace the original library-heavy workloads.

The remaining gap is larger in semantic checking. On the ASCII control, final NativeAOT spends about 6.31 ms constructing the program and 13.18 ms checking it, versus Go's 4.36 and 2.62 ms. C# allocates about 21.4 MB versus Go's 6.5 MB. The checker pool itself takes only 0.09 ms. UTF-8 handling therefore does not explain most of the remaining gap; checker work and its allocation are the next investigation lead.

## Correctness and evidence

Normal Release validation passed 13,446 semantic configurations in each mode, 1,649,908 API query comparisons across 31 families, 74 safety suites, 12,882 parser cases / 1,684,933 records, 12,895 scanner cases / 1,828,290 records, 6,778 metadata cases and 4,929 foundation assertions. Semantic output remains `8390e6e546cf407bd5030dfabecd2983bd7aee283ae8d0a0678963646c6b31a4`. Four existing Go API failures remain separately identified. The normal solution and NativeAOT app built without warnings or errors.

The additional standalone documentation suite reports 798 strict Go differences. A differential control using the same driver with the committed compiler established identical before/after output for all 1,228 documentation cases. These are existing differences, not regressions introduced by this pass; documentation Go parity is not claimed.

Final compiler DLL SHA-256: `ad9194cd29979da552549efedc348a4742db2a55927c07d05732a8817a3bf7bc`. [Performance and controls](../csharp/compatibility/evidence/phase4-unicode-performance.json), [validation](../csharp/compatibility/evidence/phase4-unicode-validation.json), and [paired native samples](../csharp/compatibility/evidence/phase4-unicode-native-samples.jsonl) contain the results and provenance. Diagnostic helpers and intermediate controls remain under `built/unicode-performance`; logs are under `logs/unicode-*`.
