# Phase 4 Server GC and Satori comparison

With the source changes committed as `35ca38f85a`, Satori reduced measured whole-program median time by **5.5–20.7%** across all ten workload/mode combinations. Its peak working set was **17.3–64.3% higher** than stock Server GC. Allocations were essentially unchanged. Both runtimes remained slower than Go: stock Server GC took **3.74–5.13×** Go’s time; Satori took **3.51–4.24×**.

These runs used the computer during normal activity, as explicitly requested after activity was detected. The Go control medians changed by −2.44% to +2.68% between batches. These are observations from two runtime packages: replacing Satori’s JIT and CoreLib along with CoreCLR prevents attributing the difference solely to GC.

## Whole-program medians

Times are milliseconds. Go was paired independently with each C# runtime; both Go medians are shown. Each mode/workload has three warmups and nine measured pairs.

| Mode | Workload | Go, Server run | Server GC | Go, Satori run | Satori | Satori time reduction |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| single | JSX signatures | 20.87 | 90.45 | 20.93 | 78.49 | 13.22% |
| single | Large conditional type | 63.59 | 237.63 | 63.85 | 224.08 | 5.70% |
| single | Static members | 19.78 | 85.70 | 19.58 | 68.99 | 19.50% |
| single | Node modules with JS | 19.33 | 84.86 | 18.86 | 68.78 | 18.95% |
| single | Large diagnostic program | 17.24 | 78.59 | 17.68 | 66.12 | 15.87% |
| default | JSX signatures | 17.74 | 89.68 | 18.11 | 76.71 | 14.45% |
| default | Large conditional type | 60.48 | 239.83 | 62.10 | 226.62 | 5.51% |
| default | Static members | 16.48 | 84.59 | 16.64 | 67.11 | 20.67% |
| default | Node modules with JS | 16.57 | 81.07 | 16.72 | 66.98 | 17.38% |
| default | Large diagnostic program | 16.02 | 78.82 | 15.96 | 65.06 | 17.46% |

## Peak working set

Process peak working sets include runtime/JIT overhead and GC retention; they are not managed live-heap sizes. Values are MiB.

| Mode | Workload | Go, Server run | Server GC | Go, Satori run | Satori |
| --- | --- | ---: | ---: | ---: | ---: |
| single | JSX signatures | 44.5 | 145.7 | 44.1 | 206.1 |
| single | Large conditional type | 55.3 | 212.6 | 56.0 | 249.2 |
| single | Static members | 43.7 | 139.2 | 43.1 | 219.5 |
| single | Node modules with JS | 42.1 | 135.3 | 42.3 | 214.5 |
| single | Large diagnostic program | 40.9 | 132.9 | 41.1 | 216.0 |
| default | JSX signatures | 46.6 | 143.9 | 46.5 | 219.6 |
| default | Large conditional type | 56.1 | 200.1 | 56.6 | 260.0 |
| default | Static members | 45.9 | 142.8 | 45.2 | 231.8 |
| default | Node modules with JS | 44.4 | 137.1 | 44.5 | 225.2 |
| default | Large diagnostic program | 42.9 | 147.2 | 43.0 | 225.5 |

## Setup and correctness

- SDK: `D:\dotnet-sdk-11.0.100-rtm.26473.115-win-x64`; Go 1.27.1 with reference revision `f29aeb9f825d96feea27841f3f7342dbf0df68a8`.
- Release, self-contained Windows x64 publish, `PublishAot=false`, `PublishTrimmed=false`, `ServerGarbageCollection=true`, and `InvariantGlobalization=true`. Both processes report Server GC active. NativeAOT was not executed.
- Four visible processors (`DOTNET_PROCESSOR_COUNT=4`, `GOMAXPROCS=4`), .NET tiered compilation disabled, default collector policies otherwise. No GCName knob is used.
- The Satori directory is a copy of the stock publish. Only `coreclr.dll`, `clrjit.dll`, and `System.Private.CoreLib.dll` were replaced from the supplied ZIP. Application binaries and runtime configuration are byte-identical. The replacement runtime identifies commit `07127280d42354eaaa358d8ea0c9f939d49ac6e0`.
- Each request creates a fresh program, binds and checks it in a warm process. Input decoding, startup, graph hashing and forced cleanup are outside the timer. The harness forces collection before and after requests; this is not a sustained-allocation or pause-latency study.
- Server GC ran first, then Satori. Within each batch, Go/C# request order alternated. No builds or validation suites overlapped timing. Each runtime has 240 samples including warmups; all 480 samples matched source hashes and diagnostic counts. Satori also passed 4,751 foundation assertions. Both runtimes passed the existing workload operability budgets.

## SDK and text changes

The SDK version lock was removed. Tools now use `DOTNET_ROOT`, `PATH`, or an explicit `--dotnet` option. The effective .NET property is `InvariantGlobalization`, rather than `InvariantCulture`. Locale validation uses registry data generated from the pinned Go dependency, so OS globalization data is unnecessary while diagnostic translations remain available.

`SourceText(string)` retains the original .NET string instead of encoding, copying and decoding it again. Spelling rune enumeration reads UTF-16 directly through `Rune.DecodeFromUtf16`; byte-length checks use `Encoding.UTF8.GetByteCount`. BOM checks and JSON surrogate escapes use `u8` literals and UTF-8 numeric formatting. The small WTF-8 boundary remains necessary for lone JavaScript surrogates, malformed source bytes and byte-oriented compatibility contracts.

Normal Release validation passed 13,446 semantic configurations per mode, 1,649,908 API comparisons across 31 families, 74 safety suites, and the host/mapper checks. The first default-concurrency corpus run reported one differing `lib.dom.d.ts` hash, while all detailed diagnostics matched. The affected case and ten neighbors passed on replay; a full same-binary default-concurrency replay matched the single-checker output exactly. The initial failure is retained and its cause remains undetermined, as with the earlier recorded hash discrepancy.

Locale validation compared 26,508 inputs: 26,505 match Go exactly. Three malformed empty Unicode extensions remain rejected, matching the previous C# build; Go accepts them after variant reordering. The final NativeAOT publish completed once with no warnings or errors and was not run.

## Reproduction

Publish and copy the runtime before running the workload tool:

```powershell
$env:DOTNET_ROOT = 'D:\dotnet-sdk-11.0.100-rtm.26473.115-win-x64'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
& $dotnet publish csharp/tests/TypeScript.Compatibility -c Release -r win-x64 --self-contained true `
    -p:PublishAot=false -p:PublishTrimmed=false -p:ServerGarbageCollection=true -o built/sdk-gc/server
Copy-Item -LiteralPath built/sdk-gc/server -Destination built/sdk-gc/satori -Recurse
Expand-Archive -LiteralPath 'C:\Users\i\Downloads\Compressed\net11.0\windows_x64.zip' -DestinationPath built/sdk-gc/satori-runtime
foreach ($name in @('coreclr.dll', 'clrjit.dll', 'System.Private.CoreLib.dll')) {
    Copy-Item -LiteralPath (Join-Path built/sdk-gc/satori-runtime $name) -Destination (Join-Path built/sdk-gc/satori $name) -Force
}
foreach ($flavor in @('server', 'satori')) {
    node csharp/tools/checker-workloads.mjs --manifest csharp/compatibility/phase4-gc-workloads.json `
        --candidate-directory "built/sdk-gc/$flavor" --candidate-executable "built/sdk-gc/$flavor/TypeScript.Compatibility.exe" `
        --server-gc --runtime-label $flavor --output-directory "built/sdk-gc/$flavor-measurements" `
        --record "built/sdk-gc/$flavor-summary.json"
}
```

Evidence: [comparison and runtime hashes](../csharp/compatibility/evidence/phase4-gc-comparison.json), [raw samples](../csharp/compatibility/evidence/phase4-gc-samples.jsonl), [Server GC](../csharp/compatibility/evidence/phase4-server-gc-workloads.json), [Satori](../csharp/compatibility/evidence/phase4-satori-gc-workloads.json), and [validation including the initial failure](../csharp/compatibility/evidence/sdk-globalization-validation.json).
