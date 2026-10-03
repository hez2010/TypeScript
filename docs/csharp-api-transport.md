# C# API transport validation

The C# API frontend runs over stdio and Windows named pipes using synchronous MessagePack or asynchronous JSON-RPC. It also supports API connections to the language server's project session. The [phase-7 report](csharp-phase-7-progress.md) records the overall validation status.

## Implemented behavior

`--api` selects the frontend. Its flags are `--cwd`, `--pipe`, `--callbacks`, `--async`, `--timing`, and `--runExternalCode`. MessagePack is the default. Ordinary compiler arguments continue to select the existing compiler command.

The binary protocol retains the reference three-element tuple, message-type numbers, binary method and payload fields, and field-length encodings. Binary echo preserves arbitrary bytes, including empty payloads. AST responses use the [C# UTF-8 version-9 format](csharp-ast-codec.md). JSON-RPC uses byte-counted `Content-Length` frames and string or signed 32-bit request identities. Partial reads are supported; writers serialize complete frames, and a request cancellation cannot interrupt a frame that has begun.

Synchronous requests and outgoing callbacks are serialized. As in the reference, a different incoming request while a synchronous callback is pending is a protocol error. JSON-RPC runs an independent reader and concurrent request handlers, allowing a client request to finish while a filesystem callback is outstanding. `$/cancelRequest` cancels the identified active request. The connection joins handlers on EOF, wakes callback waiters, preserves terminal write failures, and drains outgoing calls before disposing its synchronization and cancellation resources. Callback cancellation failures are observed without skipping handler cleanup.

All six filesystem callbacks are supported: `readFile`, `fileExists`, `directoryExists`, `getAccessibleEntries`, `realpath`, and `writeFile`. A null or empty callback reply falls back to the host; `readFile` uses `{ "content": null }` for an explicitly missing file. Append, remove, timestamps, and stat use the host. Callback source text is already decoded: a leading BOM remains part of that text, its hash, and its source positions. Disk bytes still undergo the normal encoding conversion. Configuration parsing and project overlays use the same distinction.

The connection-level timing requests retain the reference totals, five-entry chronological history, reset behavior, and exclusion of timing requests and unhandled handler faults. Duration and wall-clock values are checked for valid values before comparison.

## Executed checks

All checks below use Release CoreCLR with the default GC on Windows x64. No NativeAOT executable or performance benchmark was run.

| Check | Result |
| --- | --- |
| Wire replay against the adapted pinned Go API | 94 response groups across MessagePack/JSON-RPC and stdio/named pipes; no unexplained differences |
| Concurrent JSON-RPC echo | 40 simultaneous requests in each JSON-RPC transport pair, with identity and result assertions |
| RPC lifecycle and callback harness | 181 assertions |
| API snapshots, checker queries, configuration, resolution, diagnostics, printing, emission, and language services | 193 scenarios, 6,358 requests matching Go |
| Original JavaScript API clients | All 303 asynchronous and 296 synchronous tests pass |
| Shared language-server snapshots | Original shared-session scenarios plus synthetic-ID alias and ownership controls; real API-pipe lifecycle coverage also passes |
| Original project replay | 200 original and 2 authored sessions, 444 transitions; zero unclassified differences and two verified ordering differences |
| Project and request-filesystem safety checks | 177 and 19 assertions |
| Compiler, compatibility harness, and CLI builds | Zero warnings and errors |

The lifecycle harness carries over the assertions from the 13 original `ipc` test functions: seven asynchronous-connection tests, two synchronous write-failure tests, and four timing tests. It also covers all four `TestServerRunError` scenarios. Additional cases exercise one-byte reads at the MessagePack field-width boundaries, every truncated prefix of a representative tuple, malformed headers, callback fallback and explicit absence, concurrent synchronous callbacks, reentrant asynchronous requests, request cancellation, cancellation during a frame write, late callback replies, repeated disposal, and throwing cancellation callbacks.

The final replay saves complete frames, callback arguments, responses, stderr, and source-adapter hashes under `built/csharp/phase7-validation/api-transport-final-verified`. Its comparison substitutes only snapshot identities at snapshot-response fields, checked timing measurements, and the Go stack attached to the exact deliberate callback failure. AST and arbitrary binary echo payloads compare byte for byte. The original Go transport and callback implementations remain unchanged; the separate API oracle adapts source positions and the AST version to the C# UTF-8 contract.

Reproduce with the pinned SDK and Go executable configured in the scripts:

```text
dotnet build csharp/tests/TypeScript.Compatibility -c Release --no-restore --artifacts-path built/csharp/phase7-build
dotnet build csharp/src/TypeScript.CommandLine -c Release --no-restore --artifacts-path built/csharp/phase7-build
dotnet built/csharp/phase7-build/bin/TypeScript.Compatibility/release/TypeScript.Compatibility.dll --rpc-safety
node csharp/tools/api-transport.mjs
```

## Explicit differences and runtime contracts

Two exact controls are pinned in [the RPC difference ledger](../csharp/tests/fixtures/protocol/rpc-differences.json). C# preserves a lone-surrogate JSON escape under the chosen WTF-8 contract, while Go's JSON-RPC reader rejects it. C# rejects EOF inside an unfinished header, while Go treats that case as clean EOF. The ledger stores the input bytes, input hash, both complete outcomes, rationale, and independent checks. Changing an outcome fails the replay.

Unix-domain socket support is implemented but has not been executed on this Windows host. It intentionally refuses to replace an existing socket path; the Go listener removes the existing path before binding. This is a static inspection finding and remains a platform validation item.

The connected methods include checker queries and their type/symbol/signature identities, documentation, diagnostics, configuration and resolution, printing and emission, insertion formatting, import edits, completions with persistent symbol handles, references, signature usages, shared language-server snapshots, and profiling. Both backends pass all 303 asynchronous and 296 synchronous original-client tests. Executable selection, AST v9, and UTF-8 insertion-position adapters are recorded; original assertions remain unchanged. The direct API replay matches 6,335 requests over 190 scenarios. Completion entries use the reference sort-text/name comparison, preserving exact ties and every field; eight original runs and identity-corruption controls establish the policy in [the service API evidence](../csharp/compatibility/evidence/phase7-api-services.json). Synthetic programs retain the reference's unavailable auto-import behavior.

CPU and heap profiling return pprof files in the requested directory, and LSP also provides allocation snapshots and explicit garbage collection. Independent `go tool pprof` reads validate the generated files. These endpoints retain the existing profiler's measured counters and compiler-phase attribution; statistical stack attribution remains a phase-8 difference. C# session disposal completes an active profile and releases ownership, while the reference leaves its profiler active. The [developer-service evidence](../csharp/compatibility/evidence/phase7-developer-services.json) records the endpoint comparisons, lifecycle checks and limits.

LSP validates 55 incoming method contracts and seven client-response contracts using generated metadata without reflection. The original codec package passes 145 test cases; 17,870 decoded-input comparisons agree on acceptance and rejection. Required fields, explicit null, numeric widths, union discriminators, duplicate properties and methods without parameters are covered. Runtime-specific decoder error wording is retained in the evidence but is not compared. Live sessions pass 52 scenarios and 159 requests, with two explicitly classified Go line-index overflows: Go returns an internal panic error for the specified large unsigned line numbers, while C# clamps them to the document end. The [protocol evidence](../csharp/compatibility/evidence/phase7-lsp-protocol.json) records those exact requests and responses, 283 LSP safety assertions and 181 transport assertions.

File watching registers configuration roots, extended configs, program and resolution probes, mapper configuration files, typings files, and auto-import package directories. Shared globs retain one registration. Each client call has a separate one-second timeout; failed groups retry on a later snapshot. Windows and macOS use the native backend when the client does not support dynamic registration. Missing directories are watched through their nearest existing ancestor, then promoted when they appear. Recovery emits synthetic creates without replacing pending real events or following symbolic links.

The [watching evidence](../csharp/compatibility/evidence/phase7-lsp-watching.json) records 3,338 path and registration comparisons, 71 lifecycle assertions, four real Windows filesystem checks, and 18 passing original Go test functions. The full live replay now passes 58 scenarios and 167 requests. C# also avoids three confirmed reference defects: common-parent path corruption caused by shared slice storage, leaked shared references after failed registration, and successful partial registrations left alive across a native retry. The path control changes only the Go slice append to copy its prefix; both rollback probes call the original session update method. Complete classified outcomes are retained, and none are counted as exact matches to unmodified Go.

Telemetry is opt-in through `enableTelemetry`. A newly loaded project reports its config category, a fixed allowlist of compiler options, and file counts and UTF-8 sizes. It sends no paths, source contents, or symbol names. Each project ID is reported once after a successful send. Every five minutes, the server samples current project and cache counts plus explicitly named CLR allocation, heap, collection, and thread-pool counters. Go-specific runtime, system-memory, and auto-import bucket measurements are absent: the C# implementation does not invent equivalents for those definitions. Shutdown cancels and joins the timer and publisher, whose queue contains serialized summaries only.

`trackFlakyDiagnostics` retains one checker while collecting diagnostics, emitting with output writes suppressed, and collecting diagnostics again. Differences are logged; optional error telemetry contains diagnostic codes only. Panic mode also fails the request. Recovered request failures report only exception type and compiler method frames, excluding exception messages and source paths. The [telemetry evidence](../csharp/compatibility/evidence/phase7-telemetry.json) records live-session and lifecycle checks. Its comparison parses the JSON-valued compiler-options string after unchanged Go controls proved that its map-key order varies; all values and other fields remain exact.

The [project report](csharp-project-services.md) records the original-test inventory, retained-snapshot and cache-ownership checks, mapper refresh fixes and synthetic-ID aliases. The original Go backend remains the product backend.
