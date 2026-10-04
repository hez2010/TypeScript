# C#, NativeAOT and Satori versus Go

These are replacement measurements from 2026-10-04 with **runtime async verified in every C# build**. All tables use the fresh runs recorded here; earlier timing data is retained separately and is not reused in the comparison.

Runtime async was already enabled in the earlier binaries through `<Features>$(Features);runtime-async=on</Features>`. The binary audit found **2,511 methods with the runtime-async implementation flag and zero compiler-generated async state machines** in each compiler assembly. It also verified the CLI and checker entry assemblies and traced the NativeAOT compiler inputs through their ILC response files. The rerun clarifies and records the existing configuration; it is not a before/after runtime-async optimization.

## Representative CLI results

Medians in milliseconds. Every C# configuration has its own paired Go batch; the Go column is the range of those Go medians. CoreCLR and stock NativeAOT use .NET 11.0.0-rtm.26473.115. Satori uses its custom .NET 11.0.0-dev compiler/runtime build. AOT W means workstation GC; AOT S means Server GC.

| Workload | Go range, ms  | CoreCLR (WKS GC, tiering) ms  | NAOT (WKS GC), ms  |  NAOT (SVR GC), ms| NAOT (Satori GC), ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| Version startup | 28.1–29.5 | 179.8 | 30.7 | 33.2 | 31.6 |
| 64-module build | 54.9–56.6 | 1114.5 | 208.0 | 217.5 | 196.3 |
| Declaration-only build | 52.4–53.3 | 921.7 | 150.1 | 156.0 | 133.5 |
| 512-file build | 80.0–81.9 | 943.7 | 221.0 | 238.4 | 235.2 |
| Large-file build¹ | 299.8–302.1 | 3195.3 | 1100.7 | 995.9 | 900.3 |
| 512-file incremental edit | 88.5–95.0 | 1020.8 | 267.0 | 281.6 | 266.9 |
| Project-reference no-op | 30.1–30.8 | 259.9 | 35.1 | 37.4 | 35.5 |

¹ The Server GC large-file value is the requested successful rerun: **21 accepted pairs**, with a median of 995.85 ms. An earlier attempt encountered an access violation during `SyntaxPrinter.GenerateNames`; ten debugger attempts did not reproduce it. The original crash log, partial samples and debugger records remain in the evidence. The cause is unconfirmed, and the successful rerun does not erase that observation.

Go has the lowest median on every workload in this set. NativeAOT substantially reduces CLI startup and compilation time relative to CoreCLR. Satori has the lowest C# medians on the medium, declaration-only and large-file fixtures; workstation GC has the lower 512-file build median. Smaller cross-configuration differences should be treated as descriptive because these were separate batches, not one randomized trial across all runtimes.

## Build and runtime verification

The application source is commit `d820ab660f`; Go source is pinned to `f29aeb9f825d96feea27841f3f7342dbf0df68a8`. All **1,041 product input hashes** were checked against the current checkout, and every CLI uses the same **108 external standard-library files**. Compiler implementation and default GC settings were not changed for this study.

- Host: Windows 11 Pro for Workstations, Intel i7-13700K, 24 available logical processors, approximately 64 GiB RAM. No processor-count override.
- Stock C#: SDK 11.0.100-rtm.26473.115, Release. CoreCLR uses workstation GC, tiering enabled and runtime-default dynamic PGO. NativeAOT was executed with separately compiled workstation and Server GC builds.
- Go: Go 1.27.1, optimized `GOAMD64=v1` build with `CGO_ENABLED=0`, `-trimpath`, `-ldflags="-s -w"` and `-tags=noembed`.
- Satori: compiled using `D:\Satori\artifacts\bin\coreclr\windows.x64.Release\ilc-published\ilc.exe`, linked with files from `D:\Satori\artifacts\bin\coreclr\windows.x64.Release\aotsdk`. Build logs and linker response files confirm both paths. The linked `Runtime.ServerGC.lib` contains Satori implementation symbols. The recorded checkout is `7069a1cc611c1e1da98dd1a118d130782b54d63d`; 17 toolchain/link-input hashes were retained and rechecked.
- All native builds use their toolchain defaults without a host-native instruction-set override. Server GC and Satori pass `ServerGarbageCollection=true`; every checker observation confirms the requested GC mode. Satori's `.NET 11.0.0-dev` build includes ILC, CoreLib and runtime differences beyond the collector, so this is a comparison of configurations rather than an isolated GC substitution.

Runtime async is established by emitted metadata, not an assumed environment setting. The [official .NET 11 runtime documentation](https://github.com/dotnet/docs/blob/main/docs/core/whats-new/dotnet-11/runtime.md#runtime-async) describes the compiler feature and notes that the old `DOTNET_RuntimeAsync` environment switch was removed. No such switch was added to these runs.

The [evidence JSON](../csharp/compatibility/evidence/phase9-go-performance.json) contains all configurations, the runtime-async audit, binary/source/toolchain hashes, CPU and memory statistics, paired-bootstrap 95% intervals, input-activity records, the original failure and its accepted rerun. Raw artifacts are under `built/csharp/benchmark-runtime-async-20261004/`.

## Complete CLI timings

Each of the 92 configuration/workload groups has three warmup pairs and 21 accepted measured pairs, with alternating backend order, a fresh compiler process and a warm filesystem. Times include process creation. Successful compilation fixtures require exit code zero and explicit `rootDir`; expected-error fixtures compare exit status and diagnostics. Normalized ordinary output and emitted language bytes match. Build-info files are excluded from byte comparison.

| Workload | Go range, ms | CoreCLR (WKS GC, tiering), ms | NAOT (WKS GC), ms | NAOT (SVR GC), ms | NAOT (Satori GC), ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| help | 28.8–30.0 | 191.77 | 31.09 | 33.88 | 32.46 |
| version | 28.1–29.5 | 179.76 | 30.73 | 33.25 | 31.63 |
| empty-project | 28.9–30.3 | 311.58 | 33.35 | 36.09 | 34.09 |
| small-first-diagnostic | 32.8–34.1 | 524.20 | 39.88 | 43.23 | 41.08 |
| medium | 54.9–56.6 | 1114.51 | 207.99 | 217.49 | 196.34 |
| declarations | 52.4–53.3 | 921.71 | 150.13 | 156.03 | 133.48 |
| tiny-files | 80.0–81.9 | 943.72 | 220.97 | 238.42 | 235.22 |
| huge-file¹ | 299.8–302.1 | 3195.27 | 1100.74 | 995.85 | 900.32 |
| host-paths | 34.4–35.4 | 583.84 | 49.39 | 54.53 | 49.23 |
| tiny-files-noemit | 49.8–51.7 | 586.03 | 81.12 | 85.92 | 77.62 |
| tiny-files-noemit-on-error | 82.0–86.4 | 995.71 | 247.07 | 269.12 | 266.31 |
| tiny-files-incremental-edit | 88.5–95.0 | 1020.84 | 266.96 | 281.61 | 266.93 |
| medium-checkers-1 | 67.4–70.1 | 1242.23 | 236.57 | 246.98 | 229.34 |
| medium-checkers-2 | 58.7–60.5 | 1180.91 | 219.54 | 228.88 | 209.02 |
| medium-checkers-4 | 55.9–57.5 | 1110.40 | 206.26 | 218.64 | 197.18 |
| medium-checkers-8 | 54.3–56.4 | 1138.38 | 207.72 | 217.39 | 194.22 |
| medium-256m-heap-setting | 55.3–57.9 | 1121.11 | 208.78 | 190.92 | 198.84 |
| project-references-clean | 36.5–37.2 | 637.33 | 48.48 | 54.50 | 49.26 |
| project-references-noop | 30.1–30.8 | 259.85 | 35.13 | 37.36 | 35.48 |
| project-references-implementation | 35.5–35.9 | 579.02 | 42.45 | 46.96 | 42.50 |
| project-references-declaration | 36.9–38.3 | 678.93 | 49.28 | 55.62 | 49.72 |
| project-references-config | 35.2–35.7 | 548.03 | 42.13 | 45.74 | 42.84 |
| project-references-package | 35.3–36.0 | 497.34 | 47.55 | 52.78 | 47.62 |

The medium fixture has 64 modules with 24 interfaces and corresponding typed values each. Tiny-file fixtures have 512 modules, and the incremental edit changes every implementation without changing declaration shape. The large file has 16,384 exported declarations. The project-reference fixtures use two small projects. The `noEmit` case retains declaration diagnostics. The 256 MiB row applies different Go/.NET memory-limit semantics and is not an equal enforced memory budget; Satori's interpretation is not assumed.

CLI peak RSS below is the median of per-process peaks, in MiB. It includes runtime overhead and is not live-heap size.

| Workload | Go range, MB      | CoreCLR (WKS GC, tiering), MB | NAOT (WKS GC), MB | NAOT (SVR GC), MB | NAOT (Satori GC), MB  |
| --- | ---: | ---: | ---: | ---: | ---: |
| Version startup | 12.4–12.4 | 94.5 | 14.0 | 16.0 | 20.1 |
| 64-module build | 60.8–60.9 | 103.2 | 57.1 | 117.7 | 89.4 |
| 512-file build | 45.8–47.9 | 94.5 | 43.1 | 53.7 | 53.8 |
| Large-file build | 160.9–174.7 | 301.5 | 240.0 | 350.5 | 328.1 |
| 512-file incremental edit | 55.2–56.7 | 94.5 | 47.0 | 64.2 | 52.0 |

## Warm program and checker work

Each case/mode uses 50 warmups and 31 measured pairs in retained processes. Each iteration creates a new program, binds sources and performs complete semantic checking. Input decoding and explicit full collections before/after each operation are excluded. `single` uses one checker; `default` uses the compiler's default concurrency. All accepted iterations match expected source graph hashes and diagnostic counts; these checks do not compare full diagnostic text or model continuous editor sessions.

| Workload | Mode | Go range, ms | CoreCLR (WKS GC, tiering), ms | NAOT (WKS GC), ms | NAOT (SVR GC), ms | NAOT (Satori GC), ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Complex JSX signatures | single | 19.9–20.2 | 27.80 | 34.58 | 28.87 | 31.07 |
| Large conditional union | single | 63.3–64.4 | 104.60 | 122.76 | 75.89 | 72.04 |
| Static super members | single | 18.3–19.2 | 27.36 | 32.42 | 25.82 | 28.03 |
| Node modules / allowJs | single | 18.2–18.8 | 26.47 | 30.34 | 24.93 | 27.31 |
| giant.ts | single | 17.2–17.9 | 23.62 | 30.34 | 23.94 | 26.03 |
| Complex JSX signatures | default | 17.9–18.5 | 26.11 | 32.45 | 26.15 | 28.64 |
| Large conditional union | default | 60.6–61.3 | 102.08 | 118.86 | 71.28 | 68.28 |
| Static super members | default | 17.2–17.6 | 22.85 | 28.66 | 22.71 | 24.62 |
| Node modules / allowJs | default | 16.8–17.3 | 22.41 | 27.77 | 22.32 | 24.69 |
| giant.ts | default | 16.3–16.6 | 21.63 | 26.92 | 21.22 | 23.72 |

Default-concurrency elapsed-time overhead relative to each configuration's paired Go baseline:

| Configuration | Overhead versus Go |
| --- | ---: |
| CoreCLR workstation | 30–67% |
| NativeAOT workstation | 62–95% |
| NativeAOT Server GC | 17–44% |
| Satori NativeAOT | 13–57% |

Satori is fastest among the C# configurations on the large conditional-union case, while stock Server GC is faster than Satori on the other four default-mode checker cases. Satori uses less peak memory than stock Server GC in all five of those cases. This is a speed/memory tradeoff, not a consistent Satori throughput win.

Warm checker peak RSS is the maximum process peak over each entire workload run, including warmups. It differs from the CLI table's median process peak. Default-concurrency results, in MiB:

| Workload | Go range, MB  | CoreCLR (WKS GC, tiering), MB   | NAOT (WKS GC), MB  | NAOT (SVR GC), MB   | NAOT (Satori GC), MB |
| --- | ---: | ---: | ---: | ---: | ---: |
| Complex JSX signatures | 51.1–53.8 | 141.1 | 69.6 | 126.6 | 97.0 |
| Large conditional union | 61.5–62.0 | 137.2 | 77.7 | 167.5 | 116.1 |
| Static super members | 54.2–56.7 | 135.0 | 69.0 | 120.5 | 96.7 |
| Node modules / allowJs | 52.4–56.7 | 126.2 | 66.0 | 114.1 | 89.9 |
| giant.ts | 48.8–49.5 | 126.3 | 65.4 | 115.4 | 91.0 |

## Sample acceptance and limitations

The replacement tables contain **1,932 accepted CLI pairs** and **1,240 accepted checker pairs**, plus 276 and 2,000 warmup pairs respectively. No input-contaminated CLI pairs appear in the accepted set. One complete Server GC checker batch was discarded for input activity; its clean second attempt supplies the checker results. All inherited checker operability budgets passed. The initial Server GC CLI run is excluded; the complete replacement uses 22 successful cases plus the separately requested successful large-file rerun.

Identical-Go startup controls had these p95 relative pair differences: CoreCLR workstation: 8.53%; NativeAOT workstation: 7.85%; NativeAOT Server GC: 23.47%; NativeAOT Server GC large-file rerun: 16.83%; Satori NativeAOT: 22.66%. Some short startup observations are noisy, and small differences should not be called established improvements. The full intervals and raw samples are retained. CPU counters have 15.625 ms granularity, limiting interpretation of individual short checker operations. Normal desktop background activity is recorded; the machine was not isolated from ordinary services.

All builds and diagnostic debugger attempts completed outside measured batches. NativeAOT execution is validated here only for these Windows x64 workloads. These measurements do not establish other-platform performance, production editor latency, or the root cause of the earlier access violation. No further timing runs were added after the requested rerun passed.

## Reproduction

The saved `configurations.json`, `runtime-async-verification.json` and `satori-provenance.json` identify the exact inputs and binaries. Use fresh output directories to retain these observations. For example, the accepted Server GC large-file rerun was:

```powershell
node csharp/tools/performance-cli.mjs --manifest built/csharp/benchmark-csharp-go-20261004/native-server-package/manifest.json --go-executable built/csharp/benchmark-csharp-go-20261004/go/tsc.exe --server-gc --samples 21 --filter '^huge-file$' --directory built/csharp/benchmark-runtime-async-repeat/cli-native-server-huge
```

The same driver runs each manifest from `configurations.json`; omit `--filter` for the complete CLI suite, and use `--server-gc` for Server GC and Satori. The checker wrapper and its per-configuration manifests are saved alongside the raw data.
