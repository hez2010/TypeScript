**Host and configuration foundation validation**

The C# host implementation now parses command lines and response files, validates generated option declarations and list elements, resolves configuration inheritance, retains JSON source trees and diagnostic byte positions, discovers files, and supports physical and memory filesystems. Configuration includes project references, watch settings, type acquisition, `compileOnSave`, `${configDir}` substitution at the consuming config, and content-mapper definitions and package manifests. Reading a mapper manifest does not execute it.

File discovery uses `System.IO.Enumeration.FileSystemName.MatchesSimpleExpression` for component wildcards, with compiler rules for recursive components, package folders, hidden files, minified JavaScript, include order, and extension precedence. Directory traversal and config inheritance use explicit work stacks. Memory filesystems retain empty directories, own file buffers, resolve intermediate symbolic links, and terminate on link cycles. Bundled libraries remain read-only; side-by-side libraries resolve beside the native executable.

Configuration, command-line and response-file path strings retain paired and unpaired UTF-16 code units. `JsonStrings` uses the BCL JSON encoder for valid spans and escapes isolated surrogate units at the JSON boundary; property keys receive the same treatment. `CompilerOptions.String`, `Strings`, and the public `JsonStrings` readers provide lossless access to those values. Independent Go and Node probes compare numeric UTF-16 units for scalar options, list options, filenames and path-mapping keys, with both raw and escaped spelling of surrogate pairs. Focused tests also round-trip an unpaired-surrogate filename on the physical Windows filesystem and ensure NUL paths produce read diagnostics instead of unhandled exceptions.

JSON numeric literals retain valid numeric spelling even beyond finite `double` range, including negative values. JSONC nondecimal literals use the scanner's canonical value. Unknown extension metadata such as `"custom": 1e309` therefore produces no spurious conversion diagnostic. TypeScript's `maxNodeModuleJsDepth` retains Number semantics, including fractions and infinities; consumers should use `CompilerOptions.Number`. Native worker counts and intervals keep their integer storage conversion with explicit overflow checks. Fourteen numeric fixtures are also checked through the TypeScript 6.0.3 configuration parser.

The host validation harness extracts literal and table inputs from the pinned Go path, command-line, configuration, and filesystem-matching test sources. It also generates valid, null, missing, and invalid option cases from the Go option declarations. Dynamic content-mapper and type-acquisition tables are expanded explicitly; the evidence names each expansion. These are independent input/output comparisons, not a claim that every assertion in those Go test functions executes against C#.

| NativeAOT validation on Windows x64 | Result per resource layout |
| --- | --- |
| Path cases | 1,148 |
| Command-line and build-option cases | 1,073 |
| Configuration cases | 520 |
| Exact UTF-16 string-unit cases, also checked with Node | 2 |
| Existing directory-enumeration table cases | 74 |
| Glob cases | 335 |
| Total | 3,152; 3,135 exact matches, 17 documented differences, zero unexplained failures |
| Focused host contracts | 67 assertions with embedded libraries; 62 with side-by-side libraries |
| Existing foundation suite | 4,704 / 4,703 assertions, including all 108 library files and 13 locales |

Both executables report NativeAOT execution on .NET `11.0.0-rc.2.26470.103`; builds use the matching pinned SDK, C# 15, `OptimizationPreference=Speed` and `IlcInstructionSet=native`. NativeAOT and trimming warnings remain errors. This is host foundation evidence, not a gate for the full compiler, watch service, external mapper execution, language server, or other operating systems.

The [embedded ledger](../csharp/compatibility/evidence/phase2-hosts-embedded.json) and [side-by-side ledger](../csharp/compatibility/evidence/phase2-hosts-side-by-side.json) contain executable/oracle/input hashes and every intentional difference's exact case ID, input, per-case input hash, Go result, C# result, rationale, and reproducer. The policies are:

- Four supplementary-plane filename cases use UTF-16 `?` units, as the original TypeScript regular expressions do. The Go matching specification allows its scalar-based implementation to differ. Each accepted C# result is checked independently with a JavaScript regular expression without `/u`.
- One invalid plugin-list case retains an empty validated list after reporting the same invalid-element diagnostic. Go has no corresponding `Plugins` field on `CompilerOptions`; neither result requests plugin execution.
- Four direct/inherited, case-sensitive/case-insensitive instances of the exact malformed JSON text `{ this is not json` recover with different diagnostics. Both reject the source and return the same options, files and references. Focused tests also verify candidate diagnostics retain the malformed source filename and bounded byte positions.
- Five `maxNodeModuleJsDepth` cases retain fractional or large/infinite Number values, agreeing with the independent TypeScript configuration parser. The Go backend truncates or overflows these into its integer storage.
- Three Go-only `checkers` cases reject nonfinite/out-of-range worker counts with a range diagnostic. The reference silently stores `Int64.MinValue` after unchecked conversion.

These policies do not remove failures from the harness. Each permitted record remains separate from exact matches; every other difference fails the run. CLI paths are compared in absolute form relative to the specified current directory, and enum aliases are compared through their declared value identity. Those comparison representations do not alter production option storage.

The ledgers retain exact raw JSON output lines and their hashes. Those lines are authoritative for integers beyond JavaScript's safe-integer range and for huge exponents; the readable decoded records mark nonfinite values explicitly instead of turning them into `null`.

Reproduce after publishing either native resource layout:

```powershell
node csharp/tools/generate-options.mjs --check
node csharp/tools/hosts.mjs --no-build --candidate built/csharp/hosts-native/TypeScript.Compatibility.exe --write-evidence
node csharp/tools/hosts.mjs --no-build --candidate built/csharp/hosts-side-by-side/TypeScript.Compatibility.exe --write-evidence
& ./built/csharp/hosts-native/TypeScript.Compatibility.exe --foundations $PWD
& ./built/csharp/hosts-side-by-side/TypeScript.Compatibility.exe --foundations $PWD
```

The harness accepts `--case` for an exact ledger case ID, `--filter` for a subsystem or name fragment, and `--managed` for development runs. Managed runs cannot write release evidence. Native publishing uses `-r win-x64 -p:IlcInstructionSet=native`; the side-by-side build additionally uses `-p:EmbedTypeScriptLibraries=false`.

An initial native publish failed NuGet's `NU1004` check because a preceding generic restore had removed the library's RID entry from working restore state. A fresh `win-x64` restore with the unchanged pinned dependency graph corrected it; both locked native publishes then passed. This was a restore-state mismatch, not a NativeAOT runtime failure.
