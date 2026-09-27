# C# regular-expression validation

The independent scanner validates the supported ECMAScript regular-expression grammar, flags and target gates, named-group scopes and references, Unicode property aliases, and Unicode-set operations and string alternatives. Unicode property tables are generated from the repository's pinned Unicode 15.1 data; parsing, recovery, and diagnostics are handwritten C#. The runtime uses no Go process, dynamic code, reflection-based dispatch, or .NET regex parser.

Group and Unicode-set traversal use explicit stacks. Named-group scopes merge smaller sets into larger sets when closing a group, avoiding repeated whole-scope copies through nested parentheses. Decimal quantifier bounds are compared as digit spans, and backreference values saturate at the maximum group count representation; neither operation parses unbounded integers.

## Focused validation

`csharp/tools/regex.mjs` builds its own `regex-oracle.exe` from the archived reference revision recorded in `built/csharp/reference.json`. It compiles the production scanner files in a separate test project, without rebuilding parser outputs. It compares token kind, end, flags, value, and every diagnostic's code, UTF-16 span, and arguments. Optional corpus extraction uses the Go parser only as a development fixture collector.

```powershell
node csharp/tools/generate-regex.mjs --check
node csharp/tools/regex.mjs --fuzz 100000 --corpus

$dotnet = 'D:/dotnet-sdk-11.0.100-rc.2.26470.103-win-x64/dotnet.exe'
& $dotnet publish csharp/tests/TypeScript.RegularExpression -p:PublishAot=true -r win-x64 -c Release `
    -p:IlcInstructionSet=native -p:RestoreLockedMode=true -o built/csharp/regex-native
node csharp/tools/regex.mjs --fuzz 100000 --corpus --no-build `
    --native built/csharp/regex-native/TypeScript.RegularExpression.exe
```

The suite includes grammar/recovery cases, flag availability, all pinned property names and values plus misspellings, 864 combinations of Unicode-set operands/operators/complements, seeded malformed patterns, and literals extracted from regex-specific corpus files. Independent assertions cover 100,000 nested groups, named groups, sets, missing closing delimiters, and 100,000-digit bounds/references. All reported diagnostic spans must remain within the source. Validated patterns are also compiled by Node as a secondary grammar check. The isolated test project inherits `runtime-async=on`; refreshed native evidence includes that compiler feature.

## Reviewed semantic differences

The compatibility rule is correct TypeScript behavior, not preservation of reference bugs. `csharp/tests/fixtures/regex/semantic-differences.json` records stable case IDs (`regex-001` through `regex-037`), input SHA-256, exact input/target, Go diagnostics, required C# diagnostics, reason, and reproduction command. The test accepts an exception only if all token fields and both full diagnostic arrays match that record. New or changed differences fail; the runner does not automatically update expected results. A future strict-Go mode can use each record to trace the precise behavior that must change.

| Difference | Required C# behavior |
| --- | --- |
| Raw supplementary characters in non-Unicode ranges | Character atoms are UTF-16 code units. Range diagnostics can begin on a low surrogate and end between a pair; Go's UTF-8 position conversion instead points at the scalar boundary. |
| Escaped single/double quotes in `u`/`v` | Report an invalid identity escape. Quotes are absent from the allowed Unicode-mode identity escapes. |
| A trailing unescaped `-` in a `v` class | Report the unexpected hyphen, including during recovery from an unclosed nested class. It cannot form a range without a second character. |
| Annex B class control escapes | `\c0` through `\c9` and `\c_` have control-character values; an otherwise invalid `\c` inside a class first contributes the literal backslash. Range comparisons use those values and consumed spans. |
| Literal U+FFFD | Treat it as a source character and compare its value normally. It is not an invalid UTF-8 decoding sentinel or another character class. |

These follow the [ECMAScript pattern grammar and character values](https://tc39.es/ecma262/multipage/text-processing.html#sec-patterns) and [Annex B class escapes](https://tc39.es/ecma262/multipage/additional-ecmascript-features-for-web-browsers.html#sec-regular-expressions-patterns). Node confirms acceptance/rejection for the isolated grammar regressions. Malformed compound cases retain the reference's other diagnostics while changing only the reviewed condition.

Node 24 rejects the two Script aliases `Hrkt` and `Katakana_Or_Hiragana`, which the reference and C# accept. They occur in [Unicode 15.1 PropertyValueAliases](https://raw.githubusercontent.com/unicode-org/unicodetools/main/unicodetools/data/ucd/15.1.0/PropertyValueAliases.txt), and [UnicodeMatchPropertyValue](https://tc39.es/ecma262/multipage/text-processing.html#sec-runtime-semantics-unicodematchpropertyvalue-p-v) requires those aliases. The secondary Node check records exactly those two inputs rather than weakening property validation.

Results, binary/input hashes, and full permitted/unexpected differences are written to `built/csharp/regex-results.json`, `regex-permitted-differences.json`, `regex-failures.json`, and `regex-node-differences.json`.
