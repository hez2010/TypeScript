# Compact symbol declarations

Go stores a symbol's first declaration in a binder arena. The C# port used `ImmutableArray<SyntaxNode>.Add`, which allocated a separate one-element array for each symbol. A managed Satori CPU profile with tiered compilation disabled showed declaration binding among the distributed program-construction costs. An isolated operation count found 32,152 declaration appends on `giant.ts` and 37,253 on the conditional-type workload.

`SymbolDeclarations` now stores an empty sequence as `null`, a singleton as the node itself, and merged declarations as an immutable array. Its value wrapper keeps snapshot and rollback behavior when checker merges extend transient symbols. Direct lookup and predicate methods avoid boxing the wrapper through LINQ; `AddRange` copies a known-size collection once. The first prototype saved allocation but made the conditional-type checker slower: it enumerated through `IEnumerable` 34,271 times and passed a list to `AddRange` 2,002 times. Those two costs were removed before the retained measurement.

The workload driver now accepts `--tiering-off` and records that setting. The matched run used self-contained Satori CoreCLR, Server GC, four processors, 100 warmups and 15 samples for each of 10 inputs in both checker modes. The before and after harnesses used the same apphost and runtime binaries; each request matched the expected file graph and diagnostic count. The [full summary](../csharp/compatibility/evidence/phase4-symbol-declarations-performance.json) and [raw samples](../csharp/compatibility/evidence/phase4-symbol-declarations-samples.jsonl) include hashes and all cases.

| Default-mode case | Before | After | Go | Allocation change |
| --- | ---: | ---: | ---: | ---: |
| JSX applicability | 33.526 ms | 32.981 ms | 17.948 ms | −3.1% |
| Conditional type | 82.115 ms | 79.696 ms | 61.543 ms | −3.5% |
| Giant source | 27.934 ms | 27.755 ms | 16.095 ms | −3.1% |
| 5,000 ASCII declarations | 10.371 ms | 10.123 ms | 6.971 ms | −5.7% |

Allocation fell 3.0–5.7% in all 20 comparisons. Seventeen elapsed medians improved; the three increases were 0.2%, 0.5%, and 1.4%. The smallest elapsed differences are close to run variation. The C# port remains slower than Go on these cases. NativeAOT validation is deferred until the remaining code work is complete.

The normal Release solution built with zero warnings or errors. Both semantic corpus modes matched all 13,446 configurations, including complete diagnostics and the unchanged output hash. All 74 safety suites passed. The API and validation totals are recorded in the [validation summary](../csharp/compatibility/evidence/phase4-symbol-declarations-validation.json).
