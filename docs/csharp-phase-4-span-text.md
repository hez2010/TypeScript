# Source-backed compiler text

Compiler text now flows through scanning, parsing, binding and checking as `TextSlice` values backed by `ReadOnlyMemory<char>`. Text operations use `ReadOnlySpan<char>`. Ordinary identifiers, string literals, template segments and regular-expression group names refer to the original source instead of allocating a string for each token.

`TextSlice` adds content equality and hashing to memory slices. Two names from different files compare equally when their characters match. Slicing preserves the backing owner; decoded escapes, normalized numbers, generated names and rendered compiler text use owned character arrays. Temporary pooled buffers are copied before they are returned to the pool. There is no implicit conversion back to `string`.

This changes the public AST, factory, symbol, literal-type and diagnostic-argument APIs. Callers can inspect `.Span`, retain `.Memory`, and explicitly call `.ToString()` at a boundary that requires a string. Original source strings, static metadata, filesystem paths and external JSON input/output remain valid owners or interoperability boundaries. A borrowed backing buffer must remain unchanged while its slices are in use.

## Changes

- Scanner state and generated AST text fields retain slices. Unescaped text uses the source owner; escaped text allocates its final character buffer. The AST generator emits the new field and factory types.
- Symbol tables, type caches, template text, name-resolution APIs and diagnostic arguments carry slices through the checker. Comparisons and lookups use content equality; generated keyword and library-feature checks use span patterns.
- Template matching searches source slices directly. It no longer encodes every segment into a separate UTF-8 array. Match boundaries respect surrogate pairs, preserving the previous WTF-8 behavior for lone surrogates.
- Numeric parsing, formatting, JSDoc processing, regular-expression name handling, cache-key framing and diagnostic rendering operate on spans or character buffers. Character and rune rendering avoids temporary strings.
- The binary encoder writes WTF-8 directly into its destination buffer and patches extended fields in the final packet. Path and version operations avoid temporary component strings where ownership is unnecessary.

Ownership tests verify source sharing, decoded-buffer ownership, equality across distinct backing owners, empty versus missing text, and unambiguous cache keys. Template tests cover lone surrogates that must not match half of a paired scalar.

## Validation and measurements

Allocated bytes fell **0.9–3.4%** across the ten workload/mode groups. Timing is mixed: the final paired medians range from **21.5% faster to 4.4% slower**. This does **not** establish an overall compiler speedup. The larger stored slice values and remaining AST/binding objects limit the net allocation reduction. Go remains faster, with the candidate taking **2.51–4.09×** its time in this batch.

| Mode | Workload | Before, ms | After, ms | Time change | Allocation reduction | After / Go |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| single | JSX signatures | 62.18 | 63.18 | 1.6% | 2.5% | 3.10× |
| single | Large conditional type | 160.21 | 159.96 | -0.2% | 1.1% | 2.51× |
| single | Static members | 65.53 | 68.40 | 4.4% | 2.9% | 3.56× |
| single | Node modules with JS | 66.89 | 68.25 | 2.0% | 3.4% | 3.72× |
| single | Large diagnostic program | 60.03 | 62.67 | 4.4% | 2.6% | 3.49× |
| default | JSX signatures | 68.27 | 64.01 | -6.2% | 2.0% | 3.59× |
| default | Large conditional type | 159.37 | 162.58 | 2.0% | 0.9% | 2.70× |
| default | Static members | 67.55 | 67.90 | 0.5% | 2.3% | 4.09× |
| default | Node modules with JS | 68.22 | 53.55 | -21.5% | 2.8% | 3.28× |
| default | Large diagnostic program | 60.73 | 62.65 | 3.2% | 2.1% | 3.92× |

Negative time changes mean faster. Source commit `468747f03b` supplies the saved baseline. Both sides use the selected .NET 11 RTM SDK, Server GC, four processors, enabled tiering and runtime-default PGO. Each backend receives twenty warmups and fifteen measured samples per workload. Builds and validation did not overlap timing; normal computer use continued as requested.

The first slice-model batch exposed a slowdown in the large diagnostic program. Accessing the scanner's original source owner directly through spans recovered part of that cost; the isolated control and initial measurements are retained. The earlier small-refactor results in `built/span-performance/measurements.json` are preliminary and are not used as evidence for this representation change.

Correctness passed **13,446 semantic configurations in each mode**, **1,649,908 query comparisons across 31 API families**, **74 safety suites**, **6,789 type/algebra cases**, **12,895 scanner cases / 1,828,290 records**, **12,882 parser cases / 1,684,933 records**, **23,152 host/path comparisons**, and **15 wire-pipeline fixtures**. Source ownership, scalar-boundary matching, empty-string inference and nullable text received focused checks. Four known Go query crashes remain separately recorded.

The initial full run caught an old boxed-string check in optional-chain narrowing and a serializer that still expected string values; both were corrected. Both final semantic modes produce output hash `8390e6e546cf407bd5030dfabecd2983bd7aee283ae8d0a0678963646c6b31a4`. Initial failures are preserved. Formatting was checked to preserve token contents.

All runtime checks used normal Release builds. The final NativeAOT publish completed once with zero warnings/errors and was not executed.

Evidence: [performance and controls](../csharp/compatibility/evidence/phase4-span-text-performance.json), [raw samples](../csharp/compatibility/evidence/phase4-span-text-samples.jsonl), and [validation](../csharp/compatibility/evidence/phase4-span-text-validation.json).
