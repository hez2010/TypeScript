# Satori GC with NativeAOT

The compiler at `eaf5fefb13` was published with the local Satori AOT SDK through `csharp/Directory.Build.targets`. On the five original compiler workloads, Satori reduced default-mode median elapsed time by **15.3–21.6%** against the existing Server GC NativeAOT binary. The five smaller controls changed by **+1.6–5.7%** in default mode. Go remains faster on every workload.

## Matched workload results

The comparison used four visible processors, 20 warmups and 15 measured requests per backend, case and checker mode. Each request constructed a fresh program and completed semantic checking in a warm process. The driver alternated the stock NativeAOT, Satori NativeAOT and pinned Go backends. Input decoding, startup and forced cleanup were outside the timed interval. All 2,100 requests matched the expected source graph and diagnostic count, and both .NET binaries reported Server GC enabled.

Default-checker medians, in milliseconds:

| Workload | Stock AOT | Satori AOT | Change | Go |
| --- | ---: | ---: | ---: | ---: |
| JSX signatures | 37.46 | 31.74 | −15.3% | 17.95 |
| Large conditional type | 88.92 | 74.50 | −16.2% | 61.42 |
| Static members | 35.03 | 28.48 | −18.7% | 16.37 |
| Node modules with JS | 35.15 | 27.57 | −21.6% | 16.31 |
| Large diagnostic program | 34.61 | 27.79 | −19.7% | 16.21 |
| 5,000 ASCII exports | 10.40 | 10.63 | +2.2% | 6.89 |
| Unicode strings | 11.91 | 12.44 | +4.4% | 6.99 |
| Unicode identifiers | 11.86 | 12.05 | +1.6% | 6.98 |
| Malformed byte run | 10.87 | 11.27 | +3.7% | 6.86 |
| Sparse malformed byte | 10.88 | 11.50 | +5.7% | 6.94 |

In single-checker mode, Satori's five original-workload medians were 11.5–19.4% lower; the five controls were 0.1–1.3% higher. The Satori default-mode times are still 1.21–1.77 times Go's on the original workloads.

## GC and memory observations

For the five original default-mode workloads in table order, the runtime's median reported GC pause changed from **7.56, 20.44, 8.81, 9.34 and 8.22 ms** with stock Server GC to **1.95, 2.00, 1.98, 1.83 and 1.85 ms** with Satori. The small controls had no measured stock collection; Satori reported one Gen0 collection and roughly 0.42–0.52 ms of pause per request. These observations align with the large-workload speedup and small-control slowdown, though they do not isolate every source of elapsed-time change.

Managed allocated bytes were effectively unchanged. Peak working set increased for four of the five original default-mode workloads: the conditional case rose from **105.8 to 123.7 MiB**, while the large diagnostic case remained about **89 MiB**. Peak working set includes runtime memory and retained GC pages, so it is not a live-heap measurement.

## Build and evidence

The self-contained Windows x64 publish used the .NET SDK at `D:\dotnet-sdk-11.0.100-rtm.26473.115-win-x64`, `PublishAot=true`, `ServerGarbageCollection=true`, `UseSatoriGC=true`, and `SatoriBuildRoot=D:\Satori\artifacts\bin\coreclr\windows.x64.Release`. It completed without warnings or errors. The Satori process reported `.NET 11.0.0-dev`; the stock binary reported `.NET 11.0.0-rtm.26473.115`. The comparison includes any differences between those runtime builds, as well as the GC difference.

Satori executable SHA-256: `0081829806b5f0d7f2e7ab934a3ebb94379ee7c19372c7f358086cdd3c35b8eb`. Stock executable SHA-256: `01af7cdd6000f40a6493cdbec1b66b1bd6c5fb3b54a67fd83c88c9f4ce48c4ba`. The input and Go executable hashes are recorded in the [benchmark summary](../csharp/compatibility/evidence/phase4-satori-nativeaot-performance.json), which also includes all 20 workload/mode groups. The [raw samples](../csharp/compatibility/evidence/phase4-satori-nativeaot-samples.jsonl) retain every checked request; their SHA-256 is `cccda6e10f410c47dff58b2f9fbdfd4e44bccdb978c24b3b284e864df4bfaa94`.

The workload driver checks graph hashes and diagnostic counts. This run did not repeat the full semantic corpus under the Satori runtime. The compiler source is the same as the committed Server GC benchmark; only the build target was added for this experiment.
