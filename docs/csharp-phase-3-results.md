# Phase 3: program graph and binding

Phase 3 is implemented and validated on Windows x64. The C# backend now constructs and reuses program graphs, resolves modules and project references, runs content mappers, and binds symbols and control flow. This completes the milestone in the [rewrite plan](csharp-rewrite-plan.md). Validation uses SDK `11.0.100-rc.2.26470.103`, C# 15, `runtime-async=on`, `OptimizationPreference=Speed`, and published NativeAOT binaries using `IlcInstructionSet=native`. The final product target remains .NET 11 GA.

The original Go backend remains the product backend. Semantic checking, transforms and emit, build/watch execution, language services, and the public LSP/API hosts follow the later phases. There are no new NuGet dependencies and no Go invocation or fallback in the C# implementation. Go tools are development oracles only. These correctness checks do not establish a compiler performance advantage.

## Implementation

`Resolution/` implements the compiler's version/range grammar, ordered package JSON, package identities and peer dependencies, Node/Bundler resolution, imports/exports and their conditions, `typesVersions`, path mappings, root directories, extension substitution, suffixes, type roots, inferred type directives, symlinks, configuration package lookup, and generation-scoped caches. Configuration inheritance and mapper manifest discovery now use these shared resolution rules.

`Programs/` assembles the graph with bounded parallel parsing and deterministic traversal. It handles library and triple-slash references, synthetic helper/JSX imports, augmentation resolutions, package deduplication, casing conflicts, changing JavaScript search depth, and project source/declaration redirects, including unbuilt outputs and symlinked packages. Resolution uses the referenced project's source location and options when required. Program construction validates inclusion and output-path collisions. Complete checker/emit option validation remains with those phases.

Each program owns its options, graph, inclusion reasons, and resolution results. Unchanged contents and parse settings retain source and binding identity; rebuilding re-evaluates resolutions, including previously missing files. Old programs remain usable after the filesystem changes. Mapper output can retain syntax identity when its transform identity, source, virtual extension, and output bytes remain equal. This currently reconciles results after transformation; it is not a claim that every repeated transform is avoided.

`Binding/` implements declaration names, local/export/member tables, declaration merging and exclusions, JavaScript assignment declarations, ambient modules, documentation-derived declarations, strict-mode diagnostics, and flow graphs for assignments, conditions, calls, optional chains, loops, switches, returns, and exception/finally paths. Binding state is separate from parsed syntax and is published only after a successful bind. Concurrent callers share the completed result. Input-shaped recursive binding uses BCL `ValueTask` continuations when native stack space becomes insufficient; graph traversal and mapping indexes use explicit work stacks.

`Mapping/` owns lazy mapper processes, retained project handles, initialization, dynamic configuration identities, watch dependencies, refresh, locale changes, cancellation, and shutdown. It uses byte-counted JSON-RPC framing and validates UTF-8/UTF-16 positions, mappings, diagnostic directives, and supplemental outputs. Mapper fingerprints preserve the reference's XXH3-128 inputs, enum wire values, ordered declared options, and compact UTF-8 JSON. A canceled caller cannot abandon shared project initialization or corrupt a following frame. Execution retains the existing `runExternalCode` requirement and five-failure disabling behavior.

Span maps support sparse and duplicate projections, exact edit boundaries, aliases, and feature filters. Mapper-authored diagnostics retain original coordinates. Compiler diagnostics can be presented against the original text; diagnostics wholly in generated text retain that virtual location. Alias presentation does not modify stored diagnostic arguments.

## Native validation

| Check | Recorded coverage |
| --- | --- |
| Semver, module/type/configuration resolution | 16,524 exact reference comparisons. |
| Resolution traces | 7,731 cases with exact results, successful-file lookup checks, and archived reference/candidate trace representations. |
| Binder corpus | 16,633 units; 16,236 exact original-Go results and 397 audited parser-derived differences. Includes scopes, symbol identity, declarations, flow topology, and diagnostics with arguments and related locations. |
| Program graphs | 232 comparisons, including single/four-way scheduling, module modes, JSX, package deduplication, depth revisits, casing, and symlinked source/declaration project references. |
| Span maps | 22,469 exact comparisons of validation, forward/reverse ranges and positions, aliases, and feature participation. |
| Mapper codec and persisted identities | 1,150 exact comparisons, including Unicode, enum/list options, repeated declared names, dynamic identities, and invalid responses. |
| Live mapper processes | 43 assertions covering shared/independent project handles, cancellation during initialization and transformation, refresh, locale restart, malformed data, process failure, failure budgets, discovery, and graph integration. |
| Program ownership and safety | 15 assertions, 12,000 syntax nesting levels and a 3,000-file import chain; reuse, concurrent resolution, cancellation, and a hostile synchronization context. |
| Phase-2 parser regression | All 17,448 units and 1,699,364 records pass; the same 686 differences reproduce the existing parser ledger. |
| Foundation/host regression | 3,152 comparisons with the existing 17 reviewed differences; 4,704 foundation assertions and 67 focused host assertions. |
| Parser safety regression | 36 cases, 1,421,783 nodes and depth 21,000; ownership, ranges, and cancellation pass. |

Evidence: [resolution](../csharp/compatibility/evidence/phase3-resolution.json), [traces](../csharp/compatibility/evidence/phase3-resolution-traces.json), [binding](../csharp/compatibility/evidence/phase3-binding.json), [programs](../csharp/compatibility/evidence/phase3-program.json), [span maps](../csharp/compatibility/evidence/phase3-mapping.json), [mapper codecs](../csharp/compatibility/evidence/phase3-mappers.json), [lifecycle/safety](../csharp/compatibility/evidence/phase3-lifecycle.json), [hosts](../csharp/compatibility/evidence/phase3-hosts-regression.json), and [parser regression](../csharp/compatibility/evidence/phase3-parser-regression.json). Summaries retain input, oracle, and candidate hashes.

## Strict differences and evidence

The [binder ledger](../csharp/compatibility/evidence/phase3-binding-differences.json) retains every strict difference and a compressed archive of complete inputs and outputs. Each permitted case must satisfy all of these checks:

1. Its source hash and language/declaration mode identify an existing reviewed [phase-2 parser difference](csharp-parser-compatibility.md).
2. Fresh reference and candidate parser hashes exactly reproduce that review. Changing either parser result requires another audit.
3. A generated, typed development bridge transfers the complete C# syntax tree to the pinned Go binder. A cross-language fingerprint verifies node kinds, flags, ranges, child counts, scalar values and list fields before binding.
4. Go binding of that tree exactly matches C# binding, including symbol relationships, flow edges, diagnostic arguments and related information. The exported candidate result must also match the original candidate run.

The bridge is used only by the reference executable. The candidate remains a standalone C# implementation. Fifteen negative controls reject changed sources, modes, parser hashes, transfer fingerprints, symbols, locals, exports, flow data and diagnostics. No binder failure is accepted merely because its file has a parser difference.

The [trace ledger](../csharp/compatibility/evidence/phase3-resolution-traces-differences.json) records the separate `structured-resolution-events` policy. C# retains structured lookup, field, mapping, condition and result events rather than the reference's diagnostic-message objects. Resolution results match exactly, and each successful trace ends at a file supported by a successful lookup. Both full representations remain archived for future strict compatibility work. CLI trace text rendering belongs to the executable-host phase.

Internal symbol names use a collision-safe C# representation instead of Go's invalid UTF-8 sentinel. User names beginning with the chosen prefix are escaped distinctly. Client-facing escaped names retain their established spelling; private and attributed-module identities remain unique and stable for their owning syntax. Differential tooling normalizes only those process-local identity numbers to their owning AST nodes.

## Reproduction

From the repository root:

```powershell
node csharp/tools/generate-binding.mjs --check
node csharp/tools/generate-options.mjs --check
node csharp/tools/compare-binding.test.mjs
node csharp/tools/binding.mjs --corpus --record phase3-binding
node csharp/tools/resolution.mjs --no-build --record phase3-resolution
node csharp/tools/resolution.mjs --no-build --trace --record phase3-resolution-traces
node csharp/tools/program.mjs --no-build --record phase3-program
node csharp/tools/mapping.mjs --no-build --record phase3-mapping
node csharp/tools/mappers.mjs --no-build --record phase3-mappers
& ./built/csharp/phase3-native/TypeScript.Compatibility.exe --program-safety
& ./built/csharp/phase3-native/TypeScript.Compatibility.exe --content-mappers $PWD
node csharp/tools/syntax.mjs --no-build --candidate built/csharp/phase3-native/TypeScript.Compatibility.exe --parse --corpus --expanded --record phase3-parser-regression --reuse-parser-ledger phase2-parser-differences.json
```

The runners accept `--go` and `--dotnet`; `--managed` is a development check. `binding.mjs --strict` reports the original Go differences and exits unsuccessfully. It does not rewrite or normalize away those results. The initial reference checkout is prepared using the existing `freeze-reference.mjs` workflow.

Formatting preserves tokens, comments and syntax in both library configurations and keeps auto-properties on one line. Native publishing passed with compiler, trimming and AOT warnings treated as errors. Repository validation `npx --no-install hereby validate --all` passed: 163,380 Go test/subtest results with 2,152 existing skips, 813 API tests, 12 extension tests, and the required generators, tool tests, lint and formatting. The [validation record](../csharp/compatibility/evidence/phase3-validation.json) retains the log and artifact hashes. Repository-required benchmark smoke runs are not used as performance measurements. Other operating systems and architectures remain the release gates recorded in the plan.
