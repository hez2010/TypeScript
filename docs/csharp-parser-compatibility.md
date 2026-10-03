# Parser compatibility and the strict-difference ledger

The phase-2 gate requires correct TypeScript syntax and stable source/ownership contracts. It does not require the C# parser to reproduce every internal Go flag, trivia boundary, or recovery tree. Every intentional difference remains recorded so a future strict-Go requirement can be implemented without rediscovering the cases.

The reference is Go revision `f29aeb9f825d96feea27841f3f7342dbf0df68a8`. The independent JavaScript TypeScript parser is version `6.0.3`; it is an additional check, not a substitute for newer rules implemented in the pinned Go source. Version-specific differences need explicit evidence.

The phase-7 recovery audits pass all 17,448 expanded inputs and 1,699,364 records on Release CoreCLR. Ten previously unresolved cases now match, and strict differences decreased from 353 to 257. The changes preserve diagnostic arguments, ranges, and node error flags for malformed class members, misspelled or joined keywords, type-alias line breaks, and missing commas in variable declarations. Completion work also corrected recovery for missing conditional colons and missing interface or namespace bodies. The [current audit](../csharp/compatibility/evidence/phase7-completion-parser.json) and [complete strict-difference ledger](../csharp/compatibility/evidence/phase7-completion-parser-differences.json) retain all remaining differences, with no new strict cases; the earlier audits remain historical evidence.

## What the comparison checks

`csharp/tools/syntax.mjs --parse --corpus --expanded` expands compiler-test virtual files and compares all files using their declared names and script kinds. Each preorder record contains kind, UTF-8 byte start/end, flags, literal/name text, child count, all 50 schema scalar fields, and all 127 schema NodeList/ModifierList fields. Scalar fields include operators, type-only/phase modifiers, raw template spelling, token flags, and multiline state. List fields distinguish null from a list and record count, range, and the derived trailing-comma value. Both test executables report byte positions directly. Only the independent JavaScript parser's results and historical fixtures need coordinate translation in the comparison tools.

The C# executable also verifies byte bounds, parent identity, and absence of duplicated or cyclic children for every corpus tree. An unlocated generated node may use exactly `(-1, -1)`. Other negative or inverted ranges fail. The generated serializers use typed dispatch and support NativeAOT without reflection.

The payload has separate parse-diagnostic and JavaScript-only diagnostic buckets. The latter catches TypeScript-only annotations, modifiers and declarations in JavaScript and includes decorator-placement checks. General semantic comparison policies require equality of the JavaScript diagnostic bucket; focused tests additionally compare arguments and related information. Diagnostic sorting is confined to the comparison, while the production API retains its diagnostic sequence.

## Comparison policies

The general policies are deliberately limited:

- `equivalent-parser-context`: both parsers and the independent parser accept the file; node kinds, text, children, scalar/list properties and source spans agree. Only the five parser-context bits differ.
- `equivalent-trivia-boundary`: the same language tree and properties are preserved. A different start must resolve to the same token after TypeScript trivia scanning; a different end must cover only whitespace. Missing-node ranges cannot be normalized this way.
- `equivalent-context-and-trivia`: the preceding two conditions both hold.
- `invalid-source-recovery`: Go, C#, and the independent parser all reject the input. The candidate tree is structurally valid, every diagnostic is bounded, and at least one candidate diagnostic identifies the same offending span as either reference. Recovery may change subsequent tree structure, wording, or diagnostic order.

These policies cannot hide a rejection of valid input, a lost identifier or child, a changed operator/type-only flag, or a changed list contract. Other differences require an individually reviewed fixture that matches the case name, input SHA-256, complete Go result hash, and complete C# result hash. A source or output change invalidates that approval.

The exact fixtures are in [source differences](../csharp/tests/fixtures/parser/source-differences.json), [recovery differences](../csharp/tests/fixtures/parser/recovery-differences.json), and [JSDoc differences](../csharp/tests/fixtures/parser/jsdoc-differences.json). Each explains the rationale, independent evidence, and reproduction. The recovery ledger links a compressed archive containing the original inputs and complete output records. The invalid-UTF-8 literal fixture also identifies the implementation work needed to reproduce Go's raw-byte string payload instead of the C# decoded text view.

## Reproducing strict comparisons

```powershell
# NativeAOT semantic gate, with the complete strict audit retained:
node csharp/tools/syntax.mjs --parse --corpus --expanded --record phase2-parser

# Fail on every difference, including the documented semantic equivalents:
node csharp/tools/syntax.mjs --no-build --parse --corpus --expanded --strict

# Restrict a reproduction to the original corpus path/name fragment:
node csharp/tools/syntax.mjs --no-build --parse --corpus --expanded --strict --filter caseName
```

The runner writes `built/csharp/syntax-differences.json` with full records and `built/csharp/syntax-difference-ledger.json` with every strict difference's case, source/result hashes, comparison policy, first differing record, record counts, and both diagnostic sequences. `--record` also retains the summary, per-case ledger, and a hash-verified compressed archive of the complete source inputs and Go/C# outputs under `csharp/compatibility/evidence/`. Strict differences are counted separately from unexplained failures; they never disappear from the evidence when a semantic policy accepts them.

`--managed` is a development check. Final phase evidence uses the pinned .NET 11 SDK, C# 15 runtime async, `OptimizationPreference=Speed`, and NativeAOT with `IlcInstructionSet=native`.
