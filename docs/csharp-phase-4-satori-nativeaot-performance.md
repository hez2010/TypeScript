# Satori GC with NativeAOT

The compiler at `eaf5fefb13` was published with the local Satori AOT SDK through `csharp/Directory.Build.targets`. A later build audit found that the first comparison omitted the repository's requested `IlcInstructionSet=native` setting. The corrected paired run set it for both .NET binaries. On the five original workloads, Satori reduced default-mode median elapsed time by **15.3–25.7%** against Server GC NativeAOT. The five smaller controls were approximately flat. Go remains faster on every workload.

## Matched workload results

The comparison used four visible processors, 20 warmups and 15 measured requests per backend, case and checker mode. Each request constructed a fresh program and completed semantic checking in a warm process. The driver alternated the stock NativeAOT, Satori NativeAOT and pinned Go backends. Input decoding, startup and forced cleanup were outside the timed interval. All 2,100 requests matched the expected source graph and diagnostic count, and both .NET binaries reported Server GC enabled.

Default-checker medians, in milliseconds:

| Workload | Server AOT | Satori AOT | Change | Go |
| --- | ---: | ---: | ---: | ---: |
| JSX signatures | 36.70 | 31.07 | −15.3% | 17.90 |
| Large conditional type | 87.98 | 73.41 | −16.6% | 59.97 |
| Static members | 35.78 | 27.73 | −22.5% | 16.56 |
| Node modules with JS | 36.29 | 26.95 | −25.7% | 16.64 |
| Large diagnostic program | 34.03 | 26.61 | −21.8% | 16.09 |
| 5,000 ASCII exports | 10.61 | 10.54 | −0.7% | 6.92 |
| Unicode strings | 12.09 | 12.09 | 0.0% | 7.18 |
| Unicode identifiers | 11.97 | 11.96 | −0.1% | 7.03 |
| Malformed byte run | 10.89 | 10.99 | +0.9% | 7.02 |
| Sparse malformed byte | 10.90 | 11.06 | +1.4% | 6.88 |

In single-checker mode, Satori's five original-workload medians were 12.5–22.4% lower; the five controls changed by −2.0% to +0.6%. The Satori default-mode times are still 1.22–1.74 times Go's on the original workloads. The [initial run](../csharp/compatibility/evidence/phase4-satori-nativeaot-performance.json) and its [samples](../csharp/compatibility/evidence/phase4-satori-nativeaot-samples.jsonl) remain available as excluded host-native evidence.

## GC and memory observations

For the five original default-mode workloads in table order, the runtime's median reported GC pause changed from **7.41, 20.16, 9.74, 9.79 and 8.48 ms** with Server GC to **1.99, 2.04, 1.90, 1.81 and 2.00 ms** with Satori. These observations align with the large-workload speedup, though they do not isolate every source of elapsed-time change.

Managed allocated bytes were effectively unchanged. Peak working set includes runtime memory and retained GC pages, so it is not a live-heap measurement. The complete per-case memory figures are retained in the corrected benchmark summary.

## Build and evidence

The self-contained Windows x64 publish used the .NET SDK at `D:\dotnet-sdk-11.0.100-rtm.26473.115-win-x64`, `PublishAot=true`, `ServerGarbageCollection=true`, and `IlcInstructionSet=native` for both binaries; the Satori publish also used `UseSatoriGC=true` and `SatoriBuildRoot=D:\Satori\artifacts\bin\coreclr\windows.x64.Release`. Both completed without warnings or errors. The Satori process reported `.NET 11.0.0-dev`; the Server GC binary reported `.NET 11.0.0-rtm.26473.115`. The comparison includes any differences between those runtime builds, as well as the GC difference.

Satori executable SHA-256: `7cda08d128a512575ec958b2bc03ed8c8f1842fe3a08bceb6904c9688b4dffd6`. Server GC executable SHA-256: `81e5a76c9a39dc4602b71a6fbc2d88c41fa6bacb7663c07ddebf2e7a3df60bc3`. The input and Go executable hashes are recorded in the [corrected benchmark summary](../csharp/compatibility/evidence/phase4-satori-native-isa-performance.json), which includes all 20 workload/mode groups. The [raw samples](../csharp/compatibility/evidence/phase4-satori-native-isa-samples.jsonl) retain every checked request; their SHA-256 is `46c1e8c49e1e122547636ea0a9e0be36097810d5d5cdcd3458c72d6f672661ab`.

The workload driver checks graph hashes and diagnostic counts. This run did not repeat the full semantic corpus under the Satori runtime. The compiler source is the same as the committed Server GC benchmark; only the build target was added for this experiment.
