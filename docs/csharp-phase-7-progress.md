# Phase 7: project services, language server and API

Phase 7 is implemented and validated on Windows x64. The C# backend provides the project system, language services, LSP server and both API transports supported by the pinned reference under the contracts below. The [completion evidence](../csharp/compatibility/evidence/phase7-completion.json) records the accepted gates and artifact hashes. Validation uses Release CoreCLR and the default GC. Both NativeAOT publishes completed without warnings or errors and have not been executed. Performance work remains on hold, and Go remains the product backend.

## Implemented scope

The development executable accepts `--lsp` and `--api`. API clients can use synchronous MessagePack or asynchronous JSON-RPC over stdio or Windows named pipes, and can connect to the language server's project session. Filesystem callbacks, request cancellation, timing, batching, paging, retained snapshots, checker identities, printing, emission and source queries are implemented. The byte-oriented API uses AST protocol version 9; the client comparisons retain the recorded executable-selection, protocol-version and UTF-8 insertion-position adaptations.

Language services include completions and resolution, auto-imports, code actions, import organization, hover, signature help, definitions and source/type definitions, references and implementations, grouped Visual Studio references, highlights, rename and file renames, call hierarchy, CodeLens, inlay hints, JSX insertion, diagnostics, selection, linked editing, folding, symbols, semantic tokens and formatting. Service results preserve their feature-specific content mappings and negotiated UTF-8/UTF-16 LSP coordinates. Inactive or unregistered handlers in the pinned Go backend are not presented as shipped providers.

The project system shares immutable source graphs while keeping request and checker ownership explicit. API clients retain independent branches and own their opens and synthetic programs. Cancellation, failed updates and disconnected clients do not invalidate another client's retained snapshot. Watch registrations, mapper configuration changes, typings installation, diagnostic publication and refreshes, progress notifications, logging, profiling and opt-in telemetry are integrated with session disposal. The [project report](csharp-project-services.md), [language-service report](csharp-language-services.md), [API report](csharp-api-transport.md) and [AST wire-format report](csharp-ast-codec.md) describe those contracts.

## Validation

| Gate | Result |
| --- | --- |
| Full semantic corpus | 13,446 active configurations in each of single and reference-default concurrency; complete graph and diagnostic parity, zero failures |
| Declaration/composite regression | 1,760 configurations; 1,758 exact matches and the two existing reviewed differences; zero unresolved failures |
| API snapshots, checker queries, services, printing and emission | 193 scenarios and 6,358 requests; zero differences under the recorded wire contract |
| Original JavaScript API clients | 303 asynchronous and 296 synchronous tests pass for both backends |
| API wire and callback replay | 94 response groups across both protocols and both transports, plus concurrent JSON-RPC and the two pinned malformed-input controls |
| Original project inventory | All 97 test functions ran; 383 original test/subtest passes and one inherited ATA subtest skip |
| Snapshot transitions | 200 original and 2 authored sessions; 444 transitions; zero unclassified differences and two verified ordering differences |
| Live LSP project/session replay | 66 scenarios and 188 requests; zero unclassified differences and two recorded Go line-overflow outcomes |
| Completion regression | 8,270 requests across 1,056 original functions and 52 authored scenarios in both encodings; zero unresolved differences |
| Code actions and import organization | 1,268 original requests and 1,152 mapped requests; zero differences |
| Diagnostic service regression | 1,122 complete responses, including mapped files and both encodings; zero differences |
| Protocol validation | 17,870 decoded-input comparisons, covering required/null fields, numeric limits, unions, duplicates and callback responses |
| Project and service lifecycle checks | 177 project, 283 LSP, 337 service, 181 RPC, 2,119 codec, 44 mapper, 51 developer-service, 28 telemetry, 21 typings, 19 request-filesystem and 6 npm-process assertions |
| Generated source checks | AST, codec, binding, formatting rules, LSP schemas, typings map, Unicode normalization and regular-expression properties match their generators |
| Windows x64 NativeAOT publishing | CLI and compatibility harness publish successfully with zero warnings/errors; neither executable was run |

The [project-test inventory](../csharp/compatibility/evidence/phase7-project-test-inventory.json) maps every original project test to snapshot/operation replay, managed contract checks, or a documented runtime-specific structure. It does not imply that Go's private refcount maps, queue or interface-conformance assertions were copied into C#. The service reports retain original test identities, reference skips, recorded inputs, artifact hashes and initial failures. Earlier evidence files retain their as-of completion flags and remaining-work lists; the completion record identifies the accepted results and current boundaries.

The final semantic, declaration, API, client, transport, snapshot and project-safety gates use the final compiler. Individual service checkpoints retain their own artifact hashes and are not presented as reruns of every provider on that artifact. The [source manifest](../csharp/compatibility/evidence/phase7-source-hashes.json) records all 841 product, test and build-configuration files frozen before the final gates; their hashes were verified again after validation.

The audit fixed mapped-file program reuse, inferred mapper contributions, dynamic mapper refresh, sibling project loading, a checker-category affinity deadlock, real-path invalidation of package aliases, and canonical synthetic-ID lookup and ownership. Each issue has an initial failing reproduction and a passing subsequent comparison or contract test in the [project-service evidence](../csharp/compatibility/evidence/phase7-project-services.json).

## Comparison and release boundaries

Permitted differences remain explicit. The UTF-8/WTF-8 contract preserves lone surrogates and uses version-9 byte coordinates. Original map-dependent ordering is normalized only for identified operations with unchanged-reference controls and corruption checks. Decoder wording, runtime counters and profiler attribution retain their documented runtime distinctions. Three watcher defects, two large-line failures and the existing parser/JSDoc/declaration differences have separate evidence; they are not reported as exact matches to unmodified Go.

The original reference skips remain visible. The expanded parser audit passes 17,448 units, with 17,191 strict matches and 257 documented differences, and has no unresolved cases. Converted active editor, project and API requests pass their stated contracts; this is not a claim that every private Go testing mechanism has a direct C# counterpart.

This phase does not promote the C# backend to the product backend or establish replacement-performance results. Other-host native watcher validation, maintained NativeAOT distribution routes, release packaging and performance gates remain later work. CPU/heap profiling endpoints work, but their cooperative compiler-phase attribution is not Go-identical statistical sampling. Telemetry exposes explicitly named CLR metrics and omits Go-specific measurements that have no equivalent definition.

## Reproduction

Use the pinned SDK and Go executable recorded in the tool scripts. From the repository root:

```powershell
$dotnet = 'D:/dotnet-sdk-11.0.100-rtm.26473.115-win-x64/dotnet.exe'
$managed = 'built/csharp/phase7-tags-build/bin/TypeScript.Compatibility/release'
$cli = 'built/csharp/phase7-tags-build/bin/TypeScript.CommandLine/release'
& $dotnet build csharp/TypeScript.slnx -c Release --artifacts-path built/csharp/phase7-tags-build -p:UseSharedCompilation=false
& $dotnet "$managed/TypeScript.Compatibility.dll" --project-safety
& $dotnet "$managed/TypeScript.Compatibility.dll" --lsp-safety
node csharp/tools/project-original.mjs --managed-directory $managed
node csharp/tools/api-session.mjs --managed-directory $managed
node csharp/tools/api-transport.mjs --no-prepare --managed-directory $cli
node csharp/tools/api-client-tests.mjs --managed-directory $cli --filter '.'
node csharp/tools/verify-project-order-policy.mjs
```

The replay tools restore temporary instrumentation in the isolated Go source tree. Saved recordings can be compared again with `project-original.mjs --reuse-recording --directory <recording-directory>`. The full semantic regression uses the corrected pre-emit reference corpus, fresh candidate syntax, complete diagnostics and both concurrency modes; it does not reuse the earlier contaminated reference capture.
