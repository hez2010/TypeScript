# Final Satori NativeAOT comparison

The typed-options, compact-symbol, and parser-scan changes were published from commit `62904ab25f` in separate fresh checkouts for stock and Satori NativeAOT. Both Windows x64 builds used Server GC, `IlcInstructionSet=native`, and `--Ot`. The Satori link used its local `Runtime.ServerGC.lib`; the stock link used the SDK runtime library. This avoids the stale incremental native output found in an earlier attempted publish.

The matched run used four visible processors, 20 warmups, and 15 measured fresh-program requests per backend, case, and checker mode. It checked the source graph and diagnostic count on all 2,100 requests across 10 inputs, both modes, and stock, Satori, and pinned Go. Both .NET binaries reported Server GC. Default-mode medians, in milliseconds:

| Workload | Server AOT | Satori AOT | Go | Satori / Go |
| --- | ---: | ---: | ---: | ---: |
| JSX signatures | 35.63 | 30.66 | 17.82 | 1.72× |
| Large conditional type | 86.86 | 72.17 | 59.59 | 1.21× |
| Static members | 34.81 | 26.74 | 16.44 | 1.63× |
| Node modules with JS | 33.66 | 26.52 | 16.27 | 1.63× |
| Large diagnostic program | 33.27 | 25.72 | 15.91 | 1.62× |
| 5,000 ASCII exports | 8.66 | 8.61 | 6.76 | 1.27× |

Satori's five original default-mode medians are 13.9–23.2% below stock NativeAOT. The five small ASCII, Unicode, and malformed-byte controls change by roughly ±3% between the .NET builds. Satori remains 1.21–1.72× slower than Go on the original default workloads. The complete [performance summary](../csharp/compatibility/evidence/phase4-final-satori-nativeaot-performance.json) and [raw samples](../csharp/compatibility/evidence/phase4-final-satori-nativeaot-samples.jsonl) retain all 20 groups and artifact hashes.

The two AOT executables have different runtime builds: stock reports `.NET 11.0.0-rtm.26473.115`, while Satori reports `.NET 11.0.0-dev`. The measured difference therefore includes more than the collector alone. Fresh native object trees, response-file flags, runtime library paths, executable hashes, and smoke results are recorded in the [build and validation record](../csharp/compatibility/evidence/phase4-final-satori-nativeaot-validation.json). Both native binaries passed 39 parser safety cases; Satori also passed 40 object and 28 program safety assertions. The same source passed the normal Release 13,446-case semantic corpus in each mode and 1,649,908 API comparisons. The full corpus was not repeated under NativeAOT.

The C# port does not yet meet the faster-than-Go target under NativeAOT. The remaining original-workload gap is concentrated in program construction, where managed CPU profiles show distributed parser and binder costs rather than one dominant extra operation.
