# Compatibility contracts

The governing requirement is **semantic correctness and preservation of supported functionality**, as clarified in [the rewrite plan](../../docs/csharp-rewrite-plan.md). Go is a reference implementation. Differences must be classified, rather than automatically duplicated or ignored.

`contracts.generated.json` records the pinned source revision, package/file ledger (including nested packages and tests), release targets, source-declared methods/options/environment access, native/build directives, and hashes of shared assets. `skips.generated.json` records every observed skipped test/subtest, its reason/category, and the hash of the complete sorted result identities. Full identities, executables, input/output fixtures, and machine-specific results live under ignored `built/csharp/`.

## Boundaries

| Surface | Reference implementation | Required meaning |
| --- | --- | --- |
| CLI and process | `tsc/cmd/tsc`; `internal/execute/tsc`; `internal/tsoptions` | Arguments, option defaults, exit status, cancellation, file discovery and writes, build/watch, console capabilities, parent monitoring. Help text is inventoried with source declarations; cosmetic wording may differ. |
| TypeScript language | `parser`, `binder`, `checker`, `transformers`, `printer` | Correct grammar/recovery, binding, type relationships, diagnostics and spans, emitted-program behavior, declarations and maps. Reference failures are not automatically language rules. |
| API | `internal/api/proto.go`, dispatch in `session.go`, `protocol_msgpack.go` | Sync MessagePack and async JSON-RPC, callbacks, batch ordering, snapshot lifetime, stable node/type/symbol identity, errors and disposal. Inventory distinguishes declared methods from dispatch implementation. |
| Binary AST | `internal/api/encoder/encoder.go` and decoder/client | The C# experiment uses version 9 with UTF-8 byte positions, a 44-byte header, 28-byte nodes, LE offsets/hash, WTF-8 and structured MessagePack. It rejects version 8, whose node coordinates are UTF-16. The version occupies byte 3, the high byte of the first LE uint32. The development oracle translates reference node coordinates; the Go backend and JavaScript client are unchanged. |
| LSP | `internal/lsp/server.go` handler registration; `lsproto/lsp_generated.go` protocol | Initialize/capabilities, UTF-16 default/UTF-8 preference, request cancellation, shutdown, editor requests, notifications/progress, and custom methods. Generated protocol declarations also include client methods; they are not all server handlers. |
| Incremental state | `execute/incremental`, `execute/build`, `project` | Build-info interoperability, invalidation and signatures, no-op/edit transitions, shared snapshots, query/API checker ownership. |
| Host and content mapping | `vfs`, `nativepath`, `fswatch`, `contentmapper`, `spanmap`, `osutil` | Platform path/case/Unicode semantics, filesystem errors, symlinks, watcher overflow/cancellation, mapped positions and permissions, external mapper lifecycle. |
| Packaging and resources | `Herebyfile.mjs`, `packages/typescript`, VS Code clients, `bundled`, `locale` | Existing executable discovery and npm target names, runtime-free deployment, logical libraries/locales, signing and notices. Generated target inventory is configuration evidence, not proof of a published artifact or minimum OS support. |
| Diagnostics/profiling | `pprof`, `tracing` and API/LSP profiling methods | Useful CPU/allocation/heap data and working consumers. Trace creation without usable stacks or the expected sample meaning is insufficient. |

The declaration extraction is deliberately conservative: manually consult dispatch and generators when implementing each surface. The package ledger is an inventory, not a claim that the listed code is ported. Add implementation and test coverage to its disposition as slices land.

## Environment and native dependencies

In addition to literal environment lookups in the generated ledger, preserve Android's `TERMUX_EXEC__PROC_SELF_EXE` indirection from `osutil/os_android.go`. `GOMAXPROCS`, `GODEBUG`, Go's GC/stack settings and compiler tuning variables need separate .NET equivalents where they have a supported observable purpose. Do not introduce Go runtime configuration as a new C# dependency. Test harness variables such as `TS_TEST_PROGRAM_SINGLE_THREADED`, `TSGO_BASELINE_TRACKING_DIR`, and `TS_TEST_TERMINAL_WIDTH` are development contracts.

The release build sets `CGO_ENABLED=0` and `GOARM=6` unless asked to respect the Go environment. NativeAOT requires native platform linkers and runtime libraries instead; its OS/CPU/libc baseline is a compatibility gate. The current Windows experiment uses the installed Visual Studio 18 C++ toolchain and the user-supplied .NET 11 nightly SDK `11.0.100-rc.2.26470.103` with matching packs from the `dotnet11` feed. .NET 11 GA is the final target. It is not an audit of the oldest supported Windows machine. Other retained OS/architecture targets remain blocked until their native artifacts execute on suitable hosts; stock NativeAOT does not cover the entire 21-target matrix.

## Comparison policies

| Policy | Scope | Test treatment |
| --- | --- | --- |
| Exact contract | Wire structure, source/code-unit coordinates, identity, specified number operations, semantic relation results | Equality or a contract-aware structural comparison. Reject malformed packet envelopes and invalid topology. |
| `ecmascript-power-approximation` | Finite nonzero exponentiation, for which ECMAScript allows implementation approximation | Use BCL `Math.Pow` with required JavaScript special cases. Record differences from Go. An independent Node/V8 result supplies a second reference; the experiment accepts exact agreement or at most four ULP with equal sign. This conservative regression guard is not a bound specified by ECMAScript. Zeros, infinities and NaN handling remain strict. |
| Semantic output | JS/declaration output, diagnostics and formatting | A textual difference needs evidence that program/declaration meaning, spans, maps and tooling contracts remain valid. No blanket normalizer or added test skip. These downstream comparison policies are not implemented by the initial foundation runner. |

Reference skips are classified as reference exclusions, missing external replay, host/environment conditions, or an explicit skip without a reason. Their continued presence does not excuse a C# failure on an active reference test. Test totals count subtests, not independent source files.

## BCL decisions

Use `Rune`, `Utf8`, `Encoding.UTF8`, `ArrayBufferWriter<T>`, `MemoryExtensions`, `Array.BinarySearch`, `BinaryPrimitives`, `System.IO.Hashing.XxHash128`, `List<T>`, `Dictionary<TKey,TValue>`, `Math`, `JsonDocument` and `Utf8JsonWriter` directly. WTF-8 is a semantic adapter because normal UTF-8 fallback does not preserve lone surrogates or Go's single-byte invalid-input consumption. Valid UTF-8 conversion and ASCII-run scanning use the BCL's portable SIMD paths. The arena is an ownership/layout experiment over ordinary BCL arrays; it is not a new general-purpose allocator. Constrained generic policies share relation loops without interface boxing. Performance runs use only `/p:IlcInstructionSet=native`, as requested.

The measured node storage fits ordinary arrays. Hezium.Memory is therefore conditional and has not been added: no owner in this experiment approaches the BCL length limit. Use it, rather than custom oversized-array infrastructure, if measured compiler owners later require that capacity. Its package/version/API combinations would then need their own GC, boundary and NativeAOT tests.
