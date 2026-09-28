# Reuse parser lookahead scans

A tiering-off managed CPU profile showed scanner work distributed through parsing. Differential instrumentation found 204,571 C# scanner calls versus 183,582 in Go for `giant.ts`; `lib.dom.d.ts` accounted for 16,339 of the excess. The 5,000-export ASCII case made 20,018 extra C# calls. Isolated declarations traced the difference to repeated lookahead: `export const` made four extra calls per declaration, and ordinary interfaces and type aliases made two.

The parser now keeps a successful contextual-modifier lookahead, as Go does. Export disambiguation uses that same lookahead, and declaration parsing receives the already consumed `export` token instead of resetting the scanner and parsing it again. The statement parser reuses its declaration-start result, and primitive keyword types consume once before checking for a following dot. Failed lookaheads still restore scanner, diagnostic, and parser state.

In isolated instrumented copies, `giant.ts` fell to 186,697 C# scans against Go's 183,582, and the ASCII case fell to 40,111 against Go's 45,111. Those are operation counts, not timing claims; the [scan-count record](../csharp/compatibility/evidence/phase4-parser-scan-counts.json) includes per-file and declaration-pattern controls.

The matched elapsed run used the final compiler DLL under self-contained Satori CoreCLR with Server GC, tiered compilation disabled, four processors, 100 warmups, and 15 samples. It covered 10 inputs in each checker mode; each request matched the expected file graph and diagnostic count. Default-mode medians are below, in milliseconds:

| Case | Before | After | Go |
| --- | ---: | ---: | ---: |
| JSX applicability | 33.334 | 33.033 | 18.230 |
| Conditional type | 80.108 | 79.647 | 61.211 |
| Giant source | 27.369 | 27.080 | 16.092 |
| 5,000 ASCII exports | 9.858 | 9.146 | 6.968 |

All 20 elapsed medians improved. The smallest changes are within run variation; the five small ASCII and Unicode controls improved about 6–14%. On the default ASCII case, program construction fell from 5.440 to 4.447 ms. The C# port remains slower than Go on the measured workloads. [Full measurements](../csharp/compatibility/evidence/phase4-parser-scan-performance.json) and [raw samples](../csharp/compatibility/evidence/phase4-parser-scan-samples.jsonl) retain the hashes and complete matrix. NativeAOT validation is deferred until the remaining code work is complete.

The final Release solution built with zero warnings and errors. The parser output matched the committed build on all 12,865 syntax files. Both semantic corpus modes matched all 13,446 configurations with complete diagnostics and the unchanged output hash. The final API rollup passed 31 families and 1,649,908 comparisons with no C# failures; 74 safety suites, 6,778 metadata cases, and 86 host assertions also passed. One initial API process exited with `0xC0000005`. The exact input then succeeded in 12 isolated committed/candidate and stock/Satori controls with identical output, and the pinned-SDK accessibility suite passed 156/156 on rerun. The cause of that single process exit remains unestablished. The [validation record](../csharp/compatibility/evidence/phase4-parser-scan-validation.json) states this boundary.
