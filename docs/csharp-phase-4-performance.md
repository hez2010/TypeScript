# Phase 4 performance pass

The performance pass removes repeated syntax traversal, allocation and diagnostic collection work. Correctness validation covers the complete active Phase 4 corpus and the expanded API suite. Measurements compare the saved Release build from commit `a8ef31d228` with the final Release build; neither NativeAOT binary was executed.

## Changes

- Ambient module discovery uses the binder's existing container list. Binding lookup fills its ancestry cache without a temporary list. Read-only views are retained instead of recreated on each access.
- Type-cache keys use one exactly sized array. Instantiation keys no longer construct and hash a temporary union key.
- Bulk diagnostic requests group records by file once. Snapshots are rebuilt for subsequent requests so later diagnostics and related notes remain visible.
- Diagnostic sorting compacts its existing array. Ordinary comparisons avoid stacks used for nested diagnostic trees.

## Measurements

Five workloads run in both checker modes, with four processors, tiered compilation disabled, three warmups and nine measured pairs. Each process constructs a fresh program. Requests alternate between the saved baseline and candidate; input decoding is outside the timer. Graph hashes and diagnostic counts are checked on every sample.

| Mode | Workload | Whole program median, ms | Checker initialization/checking median, ms | Allocation reduction |
| --- | --- | ---: | ---: | ---: |
| single | JSX signatures | 131.46 → 134.58 | 11.97 → 8.77 | 0.53% |
| single | Large conditional type | 328.70 → 320.43 | 189.53 → 187.35 | 5.91% |
| single | Static members | 131.39 → 126.16 | 7.03 → 4.45 | 0.20% |
| single | Node modules with JS | 134.60 → 138.43 | 5.38 → 2.76 | 0.22% |
| single | Large diagnostic program | 117.99 → 115.31 | 6.52 → 3.57 | 0.48% |
| default | JSX signatures | 150.76 → 143.64 | 31.13 → 26.24 | 0.81% |
| default | Large conditional type | 292.45 → 304.20 | 189.28 → 183.65 | 6.02% |
| default | Static members | 129.57 → 126.40 | 6.28 → 3.47 | 0.55% |
| default | Node modules with JS | 126.49 → 125.01 | 4.81 → 2.30 | 0.57% |
| default | Large diagnostic program | 114.67 → 114.12 | 8.83 → 3.80 | 0.76% |

Allocations fall by **0.20–6.02%**, including about **12.1 MiB per request** for the large conditional-type workload in default mode. Whole-program medians range from **4.72% faster to 4.02% slower**. Program construction dominates several workloads; these measurements do **not** establish an overall wall-clock speedup. The provisional operability budgets pass. Initial measurements and an identical-binary timing control are retained alongside the final samples.

## Correctness

- **13,446 active configurations per mode** match source graphs, semantic/global codes and complete diagnostic records. Single/default outputs agree. The 1,762 existing reference skips remain, with no new exclusions.
- **1,945 active API configurations and 1,649,908 comparisons** pass across 31 families. Four known Go query crashes are recorded separately and are not passing cases.
- **74 safety suites** pass. Type-state/algebra differential validation passes **6,789 cases and 1,593,725 operations**. Node-builder tracking and flag validation adds **84 configurations and 8,136 comparisons** across both modes.
- Release builds and the final Windows x64 NativeAOT publish have no warnings or errors. NativeAOT was published once and was not run.

The full run exposed two pre-existing issues, both reproduced on the saved baseline: recursive type formatting retained an extra relation diagnostic, and optionality introduced by a mapped type incorrectly removed `undefined` from property display. Both now match the reference. A stale include-diagnostic assertion was also corrected to expect the original relative reference spelling.

One initial single-checker run reported a different `lib.dom.d.ts` hash for `scannerES3NumericLiteral7.ts`, while all detailed diagnostics still matched. An 11-case neighboring-input replay and a complete same-binary replay passed. Its cause remains undetermined; the failed output and both replays are retained, rather than treating the failed run as clean.

Evidence: [complete validation](../csharp/compatibility/evidence/phase4-performance-validation.json), [measurements](../csharp/compatibility/evidence/phase4-performance-workloads.json), [raw samples](../csharp/compatibility/evidence/phase4-performance-samples.jsonl), and [workload manifest](../csharp/compatibility/phase4-performance-workloads.json).
