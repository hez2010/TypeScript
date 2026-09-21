# Phase 2 implementation checkpoint

Phase 2 is **in progress**, not complete. The new implementation is independent C# code, but it is not a replacement compiler. The original Go backend remains the product backend. The compatibility policy in the [rewrite plan](csharp-rewrite-plan.md) still governs this work: semantic correctness is required; unexplained differences are not accepted just to make a comparison pass.

The current implementation adds:

- A UTF-16 scanner over owned UTF-8/WTF-8 source, including trivia, identifiers, literals, JSX/JSDoc lexical modes, and rescanning. It uses pinned Unicode tables and BCL span/search operations.
- 192 generated AST classes and typed factories, child traversal, iterative cloning, parent ownership, and byte-position conversion. Generated interfaces share operations on typed nodes, signatures, modifiers, and initializers without reflection. Data comes from the shared AST schema.
- An initial parser for declarations, statements, expressions, types, JSX, JSON, and JSDoc. Documentation has source-local query identity and participates in JavaScript annotation, typedef, callback, overload, and import rewriting. JSDoc edge cases, recovery, and some grammar and context rules remain unfinished.
- 2,213 diagnostic definitions, 13 embedded locales, and embedded or side-by-side access to all 108 library files.
- Compiler paths, physical and memory filesystems, source encoding, configuration/response-file parsing, generated option declarations, and compiler test directive expansion. Broader option/host differential coverage remains necessary.

The SDK remains `11.0.100-rc.2.26470.103`, with C# 15, `OptimizationPreference=Speed`, NativeAOT/trimming warnings as errors, and `IlcInstructionSet=native`. There are no new NuGet dependencies. These results are Windows x64 execution evidence; the platform release blockers in the plan remain open.

| Check on the published NativeAOT executable | Checkpoint result |
| --- | --- |
| Scanner corpus, including explicit rescan/malformed/Unicode cases | 12,895 cases and 1,828,290 token records matched Go. |
| Test directives, virtual files, options, and symlinks | 12,866 cases and 17,433 units matched Go, including BOM and invalid UTF-8 handling. |
| All bundled declaration libraries | 108 files and 183,538 AST nodes matched Go, including flags, positions, literal text, child structure, and parse diagnostics. |
| Expanded parser corpus | 15,896 of 17,448 files matched; **1,552 comparisons failed**. This gate remains open. |
| Focused foundation tests | 4,704 assertions passed with embedded libraries; 4,703 with side-by-side libraries. Covers locales/resources, ownership, encodings, configuration inheritance/response cycles, cancellation, a 20,000-node AST clone, and concurrent documentation queries and cloning. |
| Previous phase-1 primitives | 458,098 assertions passed on the new native executable. |
| Previous phase-1 pipeline and unchanged JS decoder | 15 projects, 3,886 decoded nodes and 90,212 type relations passed, including deep input, snapshot and cancellation checks. |
| Repository validation | `npx --no-install hereby validate --all` passed after the documentation changes; local log: `logs/csharp-phase2-validation.log`. This validates the original Go/JS backend and repository tooling, not the unfinished C# parser. |

The checked-in [scanner evidence](../csharp/compatibility/evidence/phase2-scanner.json), [directive evidence](../csharp/compatibility/evidence/phase2-directives.json), [library parser evidence](../csharp/compatibility/evidence/phase2-library-parser.json), and [open parser gate](../csharp/compatibility/evidence/phase2-parser-open-gate.json) record the executable/input/oracle hashes. The open gate includes every failing case name. Detailed differences are retained locally in `built/csharp/syntax-differences.json`.

[Foundation evidence](../csharp/compatibility/evidence/phase2-foundations.json) records both resource-layout artifacts and the previous phase's regression checks. C# formatting, all schema-generator freshness checks, and CRLF-aware Git whitespace checks passed.

The parser comparison serializes independently produced trees; it does not reuse Go ASTs in the candidate. Corpus files are split into their declared virtual files, decoded according to source BOMs, and parsed using their actual extensions. The directive extractor preserves invalid source bytes. The Go directive package needs `runtime.Caller` source paths, so this development oracle is built without `-trimpath`; that is not a shipped dependency.

Scanner type ranges borrow the original source rather than copying comment prefixes. Hexadecimal and binary integers use BCL `BigInteger.Parse`. Octal input uses linear bit packing into the BCL integer constructor because the BCL has no octal parse mode; it does not repeatedly multiply growing big integers. This is a semantic API gap, not a replacement for an available BCL helper.

Remaining exit work includes JavaScript/JSDoc parsing and rewriting, full regex grammar validation, malformed-input recovery, remaining syntax/context rules and source-file metadata, deep-input safety across production parser paths, and the complete configuration/path/VFS option suites. No failing cases have been removed or normalized away. The phase-1 deep-parser experiments are not proof of deep-input safety for the new production parser.

Reproduce from the repository root with Node 24:

```powershell
node csharp/tools/generate-foundations.mjs --check
node csharp/tools/generate-ast.mjs --check
node csharp/tools/generate-options.mjs --check
node csharp/tools/syntax.mjs --corpus
node csharp/tools/syntax.mjs --no-build --units --corpus
node csharp/tools/syntax.mjs --no-build --parse --corpus --filter tsc/internal/bundled
# This command currently fails and reports the remaining parser differences:
node csharp/tools/syntax.mjs --no-build --parse --corpus --expanded
& ./built/csharp/phase2-native/TypeScript.Compatibility.exe --foundations $PWD
```

The runner accepts explicit `--go` and `--dotnet` paths and defaults to the user-supplied installations. `--managed` is for fast development checks; passing it does not establish the NativeAOT gate. The runner sorts corpus paths for deterministic selection. No phase-2 performance claim is made from these correctness runs.
