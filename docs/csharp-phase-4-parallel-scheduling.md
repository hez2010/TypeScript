# Phase 4 parallel scheduling

Program construction bound every source file on the graph-publication thread, even when parsing used several workers. It also waited for an entire parse batch before publishing any results or admitting more files. The scheduler now keeps a bounded queue of file-local parse-and-bind operations and publishes their results in discovery order. A completed leading file can admit another file while a later worker is still busy. Outstanding workers are drained before a failed build returns and before its mapper host is disposed.

Checker ownership, checker count, and the reference partition policy are unchanged. Checking is still parallel across files assigned to independent checkers; an individual source file is checked by one checker. Publication can still wait behind an earlier slow file, and a bounded queue does not eliminate every source of imbalance.

The previous five workloads are dominated by serial file work. Three have one application source file. An isolated warm-process breakdown of the `giant` graph on the saved baseline measured 17.38 ms parsing and 15.79 ms binding `lib.dom.d.ts`: 33.17 ms of the 38.74 ms sum of per-file medians, or 85.6%. These measurements reuse the existing source objects and exclude source decoding, graph discovery, and checking. They explain the limited opportunity for file-level parallelism; they are not an end-to-end speedup measurement.

The first paired scheduler trial used the five existing workloads, twenty warmups and fifteen measured requests for each backend, Server GC, four processors, and enabled tiered compilation. It showed default-mode program medians lower by 0.3-2.2 ms. Total-time changes were noisier: the module workload had a similar approximately 15 ms checker-phase step in single mode, whose checking code did not change. That step should not be attributed to parallel scheduling. The original samples are retained, and no broad speedup is claimed from that trial.

Two additional synthetic workloads contain 32 independent modules and the same 2,048 interface/value declaration pairs. The balanced workload distributes 64 pairs to each file. The uneven workload places 288 pairs in every eighth file (starting with the second file) and 32 pairs in the others. Both load only the ES5 library set. These controls test available file-level parallelism and uneven work sizes; they are not application benchmarks.

| Synthetic workload | New single | New default | Default vs single | Default vs old default | Go default |
|---|---:|---:|---:|---:|---:|
| balanced-32-files | 90.92 ms | 49.42 ms | 1.84x | -15.6% time | 11.05 ms |
| uneven-32-files | 90.02 ms | 58.22 ms | 1.55x | -7.6% time | 10.93 ms |

Times are paired-run medians with 20 warmups and 15 measured samples per backend/workload/mode. Negative changes mean less elapsed time. Tiering remains enabled; the processor count is four. These measurements compare the final scheduler against the saved span-text baseline and the pinned Go compiler.

The final candidate is 1.84x faster in default mode than single mode on balanced files and 1.55x on uneven files. Against the old default mode, construction time fell 31.4% and 15.6%, accounting for most of the 15.6% and 7.6% end-to-end reductions. Go remains faster: the new default-mode totals are 4.47x and 5.33x the Go totals on these synthetic cases. The uneven single-mode total increased 8.4% (83.07 to 90.02 ms), while its construction median remained approximately 29.8 ms. The increase is in the checker phase; this pass did not change checker code, and this measurement does not establish its cause. The balanced single-mode total was 0.7% lower. Both results are retained without a rerun to select more favorable timings.

Both complete semantic modes passed **13,446 configurations each**, including full diagnostic records and graph identity. The **232 graph comparisons** and **388 focused assertions** passed, including a blocked-reader regression that requires worker refill before the previous batch completes, failure cleanup, 12,000 syntax levels and a 3,000-file dependency chain. Existing scanner, parser, type algebra and unchanged query validation was retained. NativeAOT was published once at the end with zero warnings/errors and was not executed.

Evidence: [performance](../csharp/compatibility/evidence/phase4-parallel-performance.json), [validation](../csharp/compatibility/evidence/phase4-parallel-validation.json), [scaling samples](../csharp/compatibility/evidence/phase4-parallel-scaling-samples.jsonl), and [initial trial samples](../csharp/compatibility/evidence/phase4-parallel-trial-samples.jsonl).

To reproduce the synthetic inputs and comparison after building the normal Release compatibility executable:

```powershell
$env:DOTNET_ROOT = 'D:\dotnet-sdk-11.0.100-rtm.26473.115-win-x64'
node csharp/tools/checker-scaling-inputs.mjs built/parallel-performance/scaling
node csharp/tools/checker-workloads.mjs --inputs built/parallel-performance/scaling/inputs.json --manifest built/parallel-performance/scaling/manifest.json --baseline-directory built/parallel-performance/baseline --include-go --server-gc --output-directory built/parallel-performance/scaling-results --record built/parallel-performance/scaling-results.json
```

The saved baseline contains the Release binaries for commit `12b2f91f44`. The pinned Go workload oracle must already be available at `built/csharp/checker-workload-oracle.exe`. The input generator obtains the expected graph hash and diagnostic count from that oracle, then every timed request checks those values. Timing runs do not overlap builds, profiling, or correctness suites; normal computer use continues as authorized.
