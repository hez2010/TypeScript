# Source-file metadata in the C# parser

`Parser.SourceMetadata.cs` extracts leading-comment pragmas and records path, types, and library references with exact value ranges, preservation flags, and resolution modes. It retains the final `ts-check`/`ts-nocheck` directive, JSX factory/runtime/import-source pragmas, AMD dependencies and module names, and the no-default-lib declaration. Extraction and public metadata ranges use the AST's UTF-8 byte coordinates.

The external-module indicator follows the AST: top-level imports/exports first, then `import.meta`, followed by optional JSX or forced-module detection for non-declaration files. JSON files never acquire a module indicator. Compiler-option policy selects the JSX/force parse options; the parser does not infer package resolution policy itself.

Imports, module augmentations, and ambient module names are parser-owned in the reference's `parser/references.go`. C# collects them before resolution or binding. Static imports/re-exports retain declaration order; dynamic imports, import types, and JavaScript `require` calls follow in source order. Relative imports inside ambient modules are excluded as in the reference. JavaScript documentation type imports are included, and cloning remaps metadata references to the cloned AST or its reparsed documentation.

## Validation and reproduction

```powershell
node csharp/tools/metadata.mjs
$dotnet = 'D:/dotnet-sdk-11.0.100-rc.2.26470.103-win-x64/dotnet.exe'
& $dotnet publish csharp/tests/TypeScript.SourceMetadata -p:PublishAot=true -r win-x64 -c Release `
    -p:IlcInstructionSet=native -p:RestoreLockedMode=true -o built/csharp/metadata-native
node csharp/tools/metadata.mjs --no-build `
    --native built/csharp/metadata-native/TypeScript.SourceMetadata.exe
```

The isolated test project compiles production source into its own output directory. It inherits C# 15, the pinned .NET 11 SDK, `OptimizationPreference=Speed`, and `runtime-async=on`. Native publication uses only `IlcInstructionSet=native`; its runtime check requires dynamic code support to be disabled. The Go oracle is built separately from the archived revision recorded in `built/csharp/reference.json` and is never invoked by the production compiler.

The focused suite compares 6,778 source files, including 5,000 seeded malformed attribute combinations and matrices for comments, import/export forms, script kinds, declaration files, JSX, and forced modules. Independent checks cover six AMD/no-default-lib scenarios, 19 clone-identity scenarios, and 42 semantic cases. Diagnostic positions, reference ranges, pragma arguments, and module-node identities are observable test outputs.

## Intentional differences and traceability

Every intentional metadata difference is recorded in `csharp/tests/fixtures/metadata/semantic-differences.json`. Its 48 records contain a stable case ID, source SHA-256, complete source text, both Go and C# output arrays, reason, and reproduction command. The suite asserts both full outputs against the saved records. It does not bless new differences automatically. The output schema is:

`[pragmas, pathReferences, typeReferences, libReferences, checkJs, externalModuleIndicator, diagnostics, imports, moduleAugmentations, ambientModuleNames, amdDependencies, moduleName, hasNoDefaultLib]`.

The Go adapter emits the first ten fields because its source-file metadata has no corresponding AMD/no-default-lib fields. The extension checks assert the three additional C# fields directly, including duplicate AMD-name diagnostics and ignored directives after source code.

| Case IDs | Reason |
| --- | --- |
| `metadata-001`–`metadata-038` | Recognize ECMAScript whitespace in pragmas and require full directive-name boundaries. The Go extractor accepts malformed lookalikes such as `@ts-check1` and misses valid non-ASCII whitespace. The expected pragma names are independently checked against TypeScript 6.0.3's `createSourceFile` parser. |
| `metadata-039`–`metadata-042` | Collect each module literal once from syntax and recognize escaped `require` identifiers. Searching source text for `import`/`require` duplicates a call whose string argument contains those words and misses escaped identifier spellings. |
| `metadata-043`–`metadata-048` | Preserve the explicitly requested AMD and no-default-lib information, which the Go source-file contract omits. These include negative-control cases where no extension metadata should be produced. |

`built/csharp/metadata-results.json` records the SDK, runtime, feature settings, input/binary hashes, and totals. `metadata-failures.json` contains unexpected differential failures; `metadata-semantic-differences.json` reproduces the approved evidence. For a future strict-Go mode, the fixtures identify the exact lexical and reference-collection rules to change without losing the current semantic baseline.
