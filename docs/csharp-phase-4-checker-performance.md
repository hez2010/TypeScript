# Checker allocation and option lookup performance

The compiler still does not beat the pinned Go implementation. This pass removes repeated option decoding and short-lived checker, binder and parser allocations found after the Unicode work in `2dae88fe3f`. It preserves the active semantic corpus and checker API results.

## Investigation and changes

The 5,000-declaration ASCII control spent 13.2 ms in C# semantic checking versus 2.6 ms in Go before this pass. Differential counters showed similar high-level traversal: 15,016 versus 15,030 source-element visits and 5,004 variable checks in each compiler. C# decoded the unchanged `target` option 45,001 times per request. A Visual Studio CPU capture and a separate allocation trace identified option lookups, GC work, modifier sets, identifier-name scanning and per-node binding-map growth as leads. Sample counts guided the edits; paired elapsed measurements below establish their effect.

- `CompilerOptions` now decodes string options when they are set or merged, caches the target year, and reads Boolean options with one dictionary lookup. `Set` and `Merge` invalidate the derived target value. Host checks cover replacement, merge, null and concurrent reads.
- Merged-export checking skips pure singleton declarations, computes declaration spaces directly for simple kinds, and revisits declarations only when it finds a conflict. Alias resolution keeps its iterative cycle handling.
- Binder diagnostics scan an identifier's source text only when a reserved-name error needs it. Simple initialized names avoid a traversal stack. Checker binding lookup uses the binding ID already stored on syntax nodes instead of retaining a growing node-to-file table.
- Modifier checking uses a bit set instead of a set allocation. The parser builds a modifier list only when it sees a second modifier, skips a LINQ iterator for ambient modifiers, and enters arrow lookahead through a separate slow path. Simple variable names avoid a grammar traversal stack.
- Ordinary expression checks pass state to the stable-flow helper without capturing a callback. Inferential generic finishing enters its closure-bearing path only when the mode requires it.

## Matched NativeAOT results

The same ten inputs ran in single-checker and default-checker modes. Each backend used four processors, 20 warmups and 15 measured fresh-program requests. The C# binaries used Server GC; the native candidate was republished with that property explicitly set after an initial Workstation GC publish was rejected by the harness. Tiered compilation remained enabled in the separate CoreCLR controls. Every native request matched its expected source graph and diagnostic count.

Default-checker medians, in milliseconds:

| Workload | Committed C# | Current C# | Go | C# change |
| --- | ---: | ---: | ---: | ---: |
| JSX signatures | 38.46 | 37.61 | 17.87 | −2.2% |
| Large conditional type | 91.24 | 88.12 | 61.23 | −3.4% |
| Static members | 38.20 | 36.17 | 16.53 | −5.3% |
| Node modules with JS | 39.24 | 35.60 | 16.62 | −9.3% |
| Large diagnostic program | 34.39 | 34.30 | 15.89 | −0.3% |
| 5,000 ASCII exports | 19.21 | 10.34 | 6.87 | −46.2% |
| Unicode strings | 20.84 | 12.00 | 6.77 | −42.4% |

The five isolated ASCII/Unicode/malformed controls improved by 42–46% in default mode and 42–45% in single mode. The small changes in the original workloads are close enough to normal timing variation that they should not be treated as individually established wins. ASCII allocation fell from 21.38 MB to 10.57 MB per default-mode request; Go allocated 6.53 MB. The controls show a consistent reduction of about 10.8 MB per request. All 20 workload/mode groups and 2,100 raw requests are retained in the evidence.

## Validation and remaining gap

The normal Release build and Server GC NativeAOT publish completed with zero warnings and errors. The saved Go semantic reference matched 13,446 of 13,446 configurations in each checker mode, including complete diagnostic records; both candidate output hashes remain `8390e6e546cf407bd5030dfabecd2983bd7aee283ae8d0a0678963646c6b31a4`. All 31 checker API families passed 1,649,908 comparisons with zero candidate failures; four existing Go query failures remain identified separately. Seventy-four safety suites, 73 host assertions, and 6,778 metadata cases passed. Unchanged scanner and foundation results were reused.

The current parser's result hashes match the committed compiler on all 12,865 corpus files, and the 17 focused parser cases pass strictly. A fresh strict Go syntax audit now reports 422 differences, 37 unresolved. The committed compiler produces the same corpus output as the current compiler under that audit, and the rebuilt syntax oracle's hash differs from the earlier passing run. This pass therefore establishes parser behavior preservation, while the new Go syntax audit needs a separate reference investigation before it can be claimed as passing.

Go remains 1.44–2.19× faster on the five original default-mode workloads and 1.51–1.77× faster on the isolated controls. Program construction is now the larger gap on the original workloads: for JSX, C# takes 32.12 ms before checking versus Go's 15.30 ms; for the conditional workload, 31.29 versus 15.55 ms. The ASCII control still has a checker gap of 4.84 versus 2.60 ms. These phase timings identify where to profile next, not the cause of the remaining difference.

Evidence: [matched performance](../csharp/compatibility/evidence/phase4-checker-performance.json), [raw native samples](../csharp/compatibility/evidence/phase4-checker-native-samples.jsonl), and [validation](../csharp/compatibility/evidence/phase4-checker-validation.json).
