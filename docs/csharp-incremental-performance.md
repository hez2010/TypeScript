# Incremental compiler work reuse

Incremental emission, signature calculation and declaration diagnostics now create one checker per pass instead of one per file. Each checker stays local to its operation under the existing incremental-program gate. Cancellation discards it, and no checker is carried into the next build generation. The diagnostic worker pool is unchanged.

Ambient module selection now scans the initialized global-symbol table once per build. Per-file dependency collection still visits the same ambient symbols and retains its existing source-file exclusions.

## Removed work

A separate trace probe compiles 32 source files with four diagnostic workers. Ordinary output and emitted bytes match the baseline in every mode.

| Pass | Checker instances before | After |
| --- | ---: | ---: |
| JavaScript and declaration emit | 37 | 6 |
| Declaration diagnostics with noEmit | 37 | 6 |
| Emit with noEmitOnError | 69 | 7 |
| Incremental implementation edit | 69 | 7 |

The totals include dependency discovery and diagnostic workers. Trace timings are excluded from the performance results.

## Measured performance

The baseline is the completed Windows preview committed as `5030ee3acd`. The [evidence](../csharp/compatibility/evidence/incremental-checker-reuse.json) records source and binary hashes, raw-sample locations, confidence intervals and validation results. Both builds use Release CoreCLR on the same Windows i7-13700K host, workstation GC, tiered compilation and runtime-default dynamic PGO. These are fresh compiler processes with a warm filesystem. Each workload has three warmup pairs and 21 accepted pairs with alternating order. No input activity was detected. Builds, tests and profiling were completed before measurement. Normal desktop background activity remains in the record.

| Workload | Before, ms | After, ms | After / before | Paired bootstrap 95% interval |
| --- | ---: | ---: | ---: | ---: |
| version | 189.84 | 188.62 | 0.994 | 0.982–1.004 |
| medium | 1145.41 | 1140.87 | 0.996 | 0.977–1.012 |
| declarations | 936.73 | 932.42 | 0.995 | 0.985–1.001 |
| tiny-files | 1210.72 | 965.13 | 0.797 | 0.791–0.812 |
| huge-file | 3293.25 | 3293.48 | 1.000 | 0.980–1.018 |
| tiny-files-noemit | 810.78 | 600.87 | 0.741 | 0.735–0.746 |
| tiny-files-noemit-on-error | 1452.40 | 1025.07 | 0.706 | 0.696–0.718 |
| tiny-files-incremental-edit | 1823.68 | 1293.32 | 0.709 | 0.702–0.723 |
| project-references-clean | 653.43 | 656.36 | 1.004 | 0.995–1.007 |
| project-references-noop | 270.61 | 268.59 | 0.993 | 0.982–0.999 |

The tiny-file fixtures contain 512 modules. Incremental edits change every module's implementation while retaining its declaration shape. The medium fixture contains 64 larger modules; the huge-file fixture contains 16,384 exported declarations in one source file. The identical-baseline startup control has a 3.78% p95 relative pair difference. Small timing differences should be read against that control rather than treated as established improvements.

The combined changes reduce elapsed time by 20.3% for the 512-file build, 25.9% for its declaration-diagnostic pass, 29.4% with `noEmitOnError`, and 29.1% for an incremental implementation edit. CPU time also decreases in these four cases. Peak RSS remains essentially unchanged. The other workloads show no improvement beyond the control margin; this study does not establish a general checker or startup speedup.

## Validation and benchmark correction

The Release solution builds with zero warnings and errors. All 516 original build/watch scenarios and 1,771 cycles pass the phase-8 contract, including separate controls for the five runtime-specific profiling cases. Program emission passes 240/240 cases; declaration emission passes 428/428 under its existing six documented reference corrections. Incremental, emit, declaration and watch safety checks pass 115 assertions, including the existing depth-20,000 declaration check. All 16 CLI statistics, trace and profile consumer cases pass.

The earlier CLI fixtures omitted an explicit `rootDir`, which produced TS5011 on both compilers. Their emission timings remain recorded but do not establish successful clean-compilation performance. The driver now sets `rootDir: "src"` and requires zero exit status for every successful-compilation fixture. The final measurements above use the corrected inputs on both builds and compare ordinary output plus emitted language artifacts exactly. Incremental build-info behavior is covered separately by the original scripted cases.

The driver's default Go/C# mode also passes all three added workloads across 12 smoke-test pairs, including warmups, with successful exits and identical ordinary output. Those small-sample timings are excluded from performance conclusions. All 1,036 files in the baseline package's source provenance match the committed baseline.

The CoreCLR package and NativeAOT package publish successfully from the same compiler source hash. NativeAOT binaries were not executed. These results do not change the remaining platform, signing or native-performance release gates.

Reproduce the paired comparison with the baseline and candidate package manifests:

```powershell
node csharp/tools/performance-cli.mjs --baseline-manifest built/csharp/phase8-final-managed3/manifest.json --manifest built/csharp/optimization-final-package/manifest.json --samples 21 --filter '^(version|medium|declarations|tiny-files|tiny-files-noemit|tiny-files-noemit-on-error|tiny-files-incremental-edit|huge-file|project-references-clean|project-references-noop)$' --directory built/csharp/optimization-final-measured
```
