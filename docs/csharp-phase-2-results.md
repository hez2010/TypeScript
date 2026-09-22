# Phase 2: syntax and host foundations

Phase 2 is implemented and validated on Windows x64. It provides the C# scanner, typed AST, parser, JavaScript syntax diagnostics, JSDoc parsing and rewriting, source metadata, diagnostics/resources, compiler paths, filesystem abstractions, configuration, options, and test-directive expansion. The original Go backend remains the product backend; program construction/binding, the complete checker, emit, watch/build execution, LSP, and API integration follow the later phases of the [rewrite plan](csharp-rewrite-plan.md).

Compatibility follows the user's semantic policy. The complete parser gate passes with **686 documented strict Go differences**. The [parser audit](csharp-parser-compatibility.md) explains the policies, exact fixtures, retained results, and `--strict` reproductions for a future requirement to match Go completely. The older `phase2-parser-open-gate.json` is historical checkpoint evidence, superseded by the completed gate below.

All release checks use SDK `11.0.100-rc.2.26470.103`, .NET 11, C# 15, `runtime-async=on`, `OptimizationPreference=Speed`, NativeAOT/trimming warnings as errors, and `IlcInstructionSet=native`. The final product target remains .NET 11 GA. There are no additional NuGet dependencies. Other operating systems and architectures remain release gates in the plan.

| Published NativeAOT check | Result |
| --- | --- |
| Scanner, rescanning, malformed input and Unicode | 12,895 cases; 1,828,290 token records; exact Go matches. |
| Test directives, virtual files, options and symlinks | 12,866 cases; 17,433 units; exact Go matches. |
| All bundled declaration libraries | 108 files; 183,538 AST nodes; exact Go matches. |
| Complete expanded parser corpus | 17,448 files; 1,699,364 AST records; 16,762 exact matches and 686 documented differences; zero unexplained failures. |
| JavaScript-only syntax diagnostics | 28 focused cases; 58 diagnostics, including arguments and related information; 27 exact results and one documented malformed-source recovery. Also compared across the complete parser corpus. |
| Explicit documentation trees | 839 physical files, including all 108 libraries; 1,228 expanded units; 12,599 trees and 50,944 nodes. 332 exact outputs and 896 individually pinned differences; zero unexplained failures. |
| Regular-expression grammar | 103,248 cases; 103,211 exact matches and 37 documented corrections; 11 deep-input checks. |
| Source metadata | 6,778 comparisons, six extension checks, 19 clone checks and 42 semantic assertions; all passed. |
| Paths, CLI/configuration and VFS | 3,152 cases per resource layout; 3,135 exact matches and 17 documented differences; zero unexplained failures. |
| Foundation/resources and focused hosts/modules | 4,704 / 4,703 foundation assertions and 67 / 62 host assertions for embedded / side-by-side resources; 42 module assertions per layout. Includes 2,213 diagnostics, 13 locales and 108 libraries. |
| Production parser safety | 36 deep cases; 1,421,783 nodes; depth 21,000, cancellation, byte ranges, parent ownership and a hostile synchronization context passed. |
| Earlier phase-1 regressions | 458,098 primitive assertions; 15 pipeline projects, 3,886 decoded nodes and 90,212 type relations; unchanged JS decoder, deep input, snapshots and cancellation passed. |

The parser comparison checks tree structure, source ranges, flags, literal text, all 50 schema scalar properties, and all 127 NodeList/ModifierList fields. Parse diagnostics and JavaScript-only diagnostics occupy separate comparison buckets. Every candidate tree is checked for bounds, duplicate/cyclic nodes and parent identity. Documentation has its own tree and host-identity checks. Policy tests include 21 parser regression guards and 20 documentation negative controls, including moving a comment to another declaration of the same kind.

## Async execution and ownership

Production parsing uses BCL `ValueTask<T>` and direct configured awaits. When `RuntimeHelpers.TryEnsureSufficientExecutionStack` reports insufficient space, the BCL forced-yield option schedules the continuation on a fresh stack. There is no custom task type, async builder, scheduler, nesting limit or enlarged thread stack. `ParseSourceFileAsync` and `GetDocumentationAsync` await the complete documentation/type/import chain. Synchronous facades remain available for synchronous callers.

The constrained-worker regression caught and fixed a nested synchronous wait in documentation parsing. A separate NativeAOT artifact with the **test-only** setting `UseWindowsThreadPool=false` passes the strict one-worker test for eager JS documentation, lazy TS documentation, Unicode ranges, cloning and cache identity. The normal Windows NativeAOT artifact uses the SDK's default Windows pool, which does not expose worker-count controls; the same operations pass there without imposing that unsupported control.

Cloning copies cached documentation associations by reference identity, preserving synthetic typedef aliases and shared comment associations while remapping module references to the clone's own nodes. Unqueried documentation remains lazy. Cloning performs no parser calls or synchronous waits. Recursive documentation traversal/grouping and host/configuration traversal use explicit work stacks.

## Evidence and reproduction

Checked-in summaries contain executable, input and oracle hashes: [parser](../csharp/compatibility/evidence/phase2-parser.json), [parser differences](../csharp/compatibility/evidence/phase2-parser-differences.json), [scanner](../csharp/compatibility/evidence/phase2-scanner.json), [directives](../csharp/compatibility/evidence/phase2-directives.json), [libraries](../csharp/compatibility/evidence/phase2-library-parser.json), [JavaScript diagnostics](../csharp/compatibility/evidence/phase2-javascript-syntax.json), [documentation](../csharp/compatibility/evidence/phase2-documentation.json), [regex](../csharp/compatibility/evidence/phase2-regex.json), [metadata](../csharp/compatibility/evidence/phase2-metadata.json), [safety](../csharp/compatibility/evidence/phase2-parser-safety.json), and [combined regressions](../csharp/compatibility/evidence/phase2-foundations.json).

The [host](csharp-host-compatibility.md), [regex](csharp-regex-validation.md), [metadata](csharp-source-metadata.md), and [JSDoc](csharp-jsdoc-validation.md) reports describe their separate native artifacts, scope, exact difference ledgers and commands. Intentional differences retain their original inputs and complete results or reproducible strict comparisons. No source fixture is removed from a gate because it fails.

```powershell
node csharp/tools/generate-foundations.mjs --check
node csharp/tools/generate-ast.mjs --check
node csharp/tools/generate-options.mjs --check
node csharp/tools/generate-regex.mjs --check
node csharp/tools/syntax.mjs --parse --corpus --expanded --record phase2-parser
node csharp/tools/syntax.mjs --no-build --corpus
node csharp/tools/syntax.mjs --no-build --units --corpus
node csharp/tools/syntax.mjs --no-build --parse --corpus --filter tsc/internal/bundled
& ./built/csharp/phase2-native/TypeScript.Compatibility.exe --foundations $PWD
& ./built/csharp/phase2-native/TypeScript.Compatibility.exe --hosts $PWD
& ./built/csharp/phase2-native/TypeScript.Compatibility.exe --modules
& ./built/csharp/phase2-native/TypeScript.Compatibility.exe --javascript-syntax $PWD
& ./built/csharp/phase2-native/TypeScript.Compatibility.exe --parser-safety
```

The syntax runner accepts `--go` and `--dotnet` and defaults to the supplied installations. `--managed` is a development check; recorded release evidence comes from NativeAOT. BCL span/search operations provide portable SIMD opportunities; measured workloads have not required storage beyond BCL array limits. These are correctness and ownership checks, with no new compiler performance claim.

Repository validation `npx --no-install hereby validate --all` passed, including the original Go/JS suites, API/tool checks, generation, linting and formatting. Its Go run reported 163,380 tests with 2,152 existing skips. The run also executes the repository's one-iteration benchmark smoke tests; their timings are not used as performance baselines. The log is `logs/csharp-phase2-final-validation.log`, with its hash retained in the [validation record](../csharp/compatibility/evidence/phase2-validation.json).

For workspace-loading tools such as `dotnet format`, run from `csharp/` with `DOTNET_ROOT` and the start of `PATH` set to the pinned SDK directory. This also pins MSBuild SDK discovery; using the executable path alone allowed a design-time load to restore machine-wide RC1 packs. The final lockfiles and format verification use the requested RC2 SDK and package graph.
