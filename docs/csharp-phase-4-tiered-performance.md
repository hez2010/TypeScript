# Phase 4 performance with tiered compilation

The benchmark now explicitly enables tiered compilation and leaves dynamic PGO at the runtime default. Twenty warmups precede fifteen measured requests per workload/backend. Source changes reduce measured median elapsed time by **2.2–17.3%** and allocated bytes by **25.7–27.8%** compared with the saved unchanged build, with tiering enabled on both sides. The smaller timing differences should not be read as precise universal speedups.

Go remains faster. The optimized stock runtime takes **2.51–4.04×** Go’s time on these workloads; the optimized Satori package takes **2.29–3.20×**. This pass removes specific redundant work but does not close the overall performance gap.

## Findings and retained fixes

The original tiered measurements spend about 63–71 ms in program construction on most cases. Checking is only a few milliseconds except for the large conditional-type workload. The profile points to parsing, binding, repeated allocation, and type-instantiation bookkeeping. Changing collectors alone cannot remove that work.

- The binder allocated a LINQ iterator, predicate delegate and closure while propagating error flags for every node. A direct loop keeps the same child order and short-circuit behavior. Binding-state insertion now uses the BCL single-lookup API.
- Position conversion created a delegate for each syntax node. One delegate is now shared by the conversion pass. ASCII sources skip the identity conversion entirely, including lazy documentation trees.
- Type comparison repeatedly allocated traversal stacks. Scratch storage is reused and cleared in a `finally` block; concurrent or reentrant callers acquire separate stacks.
- Name lookup rebuilt a resolver and its delegates for every call. The checker reuses the configured resolver for the same cancellation token; custom lookup callbacks and different tokens get their own resolver.
- Type instantiation allocated predicate closures while finding active mappers and built keys for newly opened, empty caches whose own result is never cached. These operations are avoided. Alias keys retain lazy symbol-identity assignment order.
- The scanner classifies identifiers from spans, reuses existing keyword strings and slices an unescaped private identifier once. It retains the existing decoded path for escapes.

Go’s scanner can retain slices of the source string, and its parser uses arenas for node lists. The C# implementation still creates UTF-16 strings and many individual syntax/binding objects and collections. Even after these fixes, the measured C# allocated bytes remain **3.07–4.13×** Go’s. The remaining object and text representation costs are a supported explanation for part of the remaining gap; this work does not quantify every remaining cause.

Two experiments were rejected. Struct enumeration of node lists had no consistent timing benefit in an isolated comparison, so the existing enumerator remains. Per-scanner identifier interning reduced strings but increased lookup cost and slowed the construction-heavy case; the dictionary was removed. Intermediate measurements remain in the evidence.

## Paired stock-runtime results

Times are medians in milliseconds. Go, the saved baseline and the candidate alternate in the same batch. The baseline and candidate both use Server GC and tiered compilation.

| Mode | Workload | Go | Before | After | Time reduction | Allocation reduction |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| single | JSX signatures | 20.85 | 74.30 | 61.47 | 17.3% | 27.1% |
| single | Large conditional type | 64.67 | 171.64 | 162.51 | 5.3% | 27.8% |
| single | Static members | 19.71 | 74.02 | 66.24 | 10.5% | 26.4% |
| single | Node modules with JS | 18.82 | 68.77 | 65.37 | 5.0% | 26.8% |
| single | Large diagnostic program | 17.64 | 65.70 | 54.42 | 17.2% | 26.6% |
| default | JSX signatures | 18.23 | 74.10 | 68.45 | 7.6% | 26.7% |
| default | Large conditional type | 61.23 | 166.68 | 162.99 | 2.2% | 27.6% |
| default | Static members | 16.62 | 69.07 | 63.26 | 8.4% | 25.7% |
| default | Node modules with JS | 16.51 | 70.91 | 66.69 | 6.0% | 26.1% |
| default | Large diagnostic program | 16.05 | 65.04 | 60.85 | 6.5% | 26.1% |

## Satori

The application and runtime configuration are identical to the candidate stock publish. Only `coreclr.dll`, `clrjit.dll` and `System.Private.CoreLib.dll` were replaced from the supplied ZIP. No GCName knob is used. Tiered compilation stays enabled. Because the JIT and CoreLib also differ, these measurements compare runtime packages.

| Mode | Workload | Paired Go | Satori median, ms | Satori / Go | Stock peak MiB | Satori peak MiB |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| single | JSX signatures | 20.73 | 55.05 | 2.66× | 145.9 | 293.7 |
| single | Large conditional type | 63.56 | 145.73 | 2.29× | 201.9 | 307.9 |
| single | Static members | 19.79 | 51.38 | 2.60× | 152.6 | 280.1 |
| single | Node modules with JS | 20.03 | 48.66 | 2.43× | 147.7 | 275.8 |
| single | Large diagnostic program | 18.12 | 48.16 | 2.66× | 143.2 | 270.4 |
| default | JSX signatures | 18.42 | 58.95 | 3.20× | 150.6 | 301.0 |
| default | Large conditional type | 61.46 | 142.24 | 2.31× | 202.8 | 329.8 |
| default | Static members | 16.73 | 49.67 | 2.97× | 158.3 | 289.2 |
| default | Node modules with JS | 16.77 | 48.30 | 2.88× | 149.7 | 296.2 |
| default | Large diagnostic program | 16.04 | 49.08 | 3.06× | 154.2 | 286.9 |

## Measurement boundaries

- SDK `11.0.100-rtm.26473.115`, Go 1.27.1, four visible processors, Release self-contained Windows x64 builds, invariant globalization, Server GC, tiered compilation enabled, runtime-default PGO.
- Each request constructs a fresh program, binds and checks it. Input decoding, startup, graph hashing and forced cleanup are outside the timer. Profiles include that untimed harness work, so their GC frames are not estimates of GC cost inside compilation.
- All 1,750 timing/warmup samples match the expected source hashes and diagnostic counts. Builds, profiling and validation ran outside timing batches. Ordinary machine use continued as requested; samples were not discarded because of keyboard or mouse activity.
- The saved baseline is the stock publish from source commit `35ca38f85a`, unchanged by documentation commit `7db2124e38`. Runtime, application and raw-sample hashes are recorded. Profiling uses the unchanged baseline and is separate from reported timings.

## Validation

Normal Release validation passed **13,446 semantic configurations per mode**, **1,649,908 API comparisons** across 31 families, **74 safety suites**, **1,593,725 type/algebra operations**, and **12,895 scanner cases with 1,828,290 records**. Both semantic modes produced identical output hashes. Four previously known Go API query crashes remain separately recorded.

One emit-services subprocess terminated with a native access violation. The exact input passed on replay and matched Go; every other result was reused. The initial failure is preserved, and its cause is unestablished. The user identified a possible CPU defect and explicitly requested ignoring the event if it did not reproduce. The crash preceded the NativeAOT publish, and the managed compiler binary had not been rewritten.

The final NativeAOT publish completed once with no warnings or errors and was not executed. Foundation assertions also passed under the Satori runtime.

## Reproduction

With the normal Release publish available and `DOTNET_ROOT` set to the selected SDK:

```powershell
node csharp/tools/checker-workloads.mjs --manifest csharp/compatibility/phase4-tiered-workloads.json `
    --baseline-directory built/sdk-gc/server --candidate-directory built/tiered-performance/candidate `
    --include-go --server-gc --output-directory built/tiered-performance/reproduction `
    --record built/tiered-performance/reproduction.json
```

`--include-go` adds Go to the before/after batch. Without a baseline directory, the tool compares Go with the candidate as before. All new runs enable tiered compilation; the earlier report keeps its original configuration and historical results.

Evidence: [measurements, profiles and intermediate controls](../csharp/compatibility/evidence/phase4-tiered-performance.json), [raw samples](../csharp/compatibility/evidence/phase4-tiered-samples.jsonl), and [correctness validation](../csharp/compatibility/evidence/phase4-tiered-validation.json).
