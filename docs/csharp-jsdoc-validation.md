# JSDoc parsing and validation

The C# parser keeps documentation trees for JavaScript and exposes lazy documentation for TypeScript through `SourceFileNode.GetDocumentation` and `GetDocumentationAsync`. Tags retain their type expressions, names, optional/rest information, descriptions, links, namespace structure, source ranges, and scalar fields. JavaScript annotations are also projected into the executable/type AST where the language permits them.

The implementation handles positional destructuring and unnamed parameter tags, Unicode-escaped names, array-qualified property paths, nested typedef properties, callback/overload grouping, `this` parameters, full-signature precedence, template constraints/defaults/modifiers, return type predicates, and import tags. Type-first and name-first parameter/property tags have the corresponding visitor order. Trailing comments are considered only for supported host kinds; an incomplete host cannot acquire a truncated comment.

The async source parser awaits the complete documentation chain. `LeadingAsync`, tag parsing, and the underlying type/import parsers never call a synchronous parser facade. This matters when a deeply nested documentation type needs a fresh stack while only one ThreadPool worker is available. Synchronous API facades remain separate entry points.

## Reproduce the complete documentation gate

```powershell
node csharp/tools/jsdoc.mjs

$dotnet = 'D:/dotnet-sdk-11.0.100-rc.2.26470.103-win-x64/dotnet.exe'
& $dotnet publish csharp/tests/TypeScript.SourceMetadata -r win-x64 -c Release `
    -p:IlcInstructionSet=native -p:RestoreLockedMode=true -o built/csharp/metadata-native
node csharp/tools/jsdoc.mjs --no-build `
    --native built/csharp/metadata-native/TypeScript.SourceMetadata.exe
```

The default scope is every `.ts`, `.tsx`, `.js`, and `.jsx` compiler-test source containing `/**`, plus all bundled declaration libraries, including libraries without documentation. Test directives are expanded into their virtual source files. The recorded run covers 839 physical files, including all 108 libraries, and 1,228 expanded files. It visits 12,365 documentation hosts, 12,599 comment trees, and 50,944 documentation nodes.

Each host is identified by kind and source range. Every documentation node records kind, range, flags, child count, generated scalar properties, and generated NodeList metadata. Diagnostics are compared separately. The candidate also checks source-bounded byte ranges, unique node identity, acyclic traversal, and child-parent ownership within each comment tree. The same comment may legitimately be associated with multiple hosts.

The test builds a separate development oracle from the archived revision in `built/csharp/reference.json`. It does not depend on ignored parser-difference snapshots. `--filter <filename>` runs a targeted subset, and `--input <requests.json>` replays a saved input set. Unknown or changed differences fail with a nonzero exit code.

## Intentional differences

`csharp/tests/fixtures/jsdoc/documentation-differences.json` contains one stable ID and exact input/Go/C# SHA-256 triple for each raw difference, together with its policy, rationale, and reproduction command. Its companion `documentation-evidence.json.gz` stores the full input and both complete output arrays. Joining parser text fragments for a semantic assertion never removes characters; the original fragment arrays, ranges, flags, and list metadata remain in the strict evidence.

The current 896 raw differences comprise 868 cases with identical host kind/start/end, type/tag topology, scalar values, and concatenated description text; four individually pinned synthetic-typedef host ranges; and 24 cases with explicitly reviewed differences. Those cases cover malformed documentation recovery, exclusion of comment-line decoration from descriptions, complete external-URL representation, retained documentation-owner associations, and whitespace normalization. No general rule permits new differences in these categories: the input and both full output hashes must match the saved record. The helper also rechecks the identical-content assertion for records that make it, including exact host identity. Every run executes 20 negative controls, including moving documentation between distinct declarations of the same kind.

Malformed annotations retain error nodes or diagnostics and their original source. External URLs retain the complete URL and label; Go sometimes represents the scheme as a symbol name and `://...` as text. C# excludes decoration-only asterisks from descriptions and does not attach those artifacts as separate documentation to synthetic parameters. The complete evidence identifies every such occurrence, including diagnostics and context.

`csharp/tests/fixtures/parser/jsdoc-differences.json` separately records main-AST differences with generated scalar and list fields. Equal-shape entries preserve node kinds, text, scalar properties, child topology, list counts, and trailing-comma results. The fallback-assignment cases retain `number` parameter annotations through `||`/`??`; TypeScript 6.0.3's checker independently confirms those parameter types. Malformed trailing-dot typedef recovery retains a namespace rather than introducing a file-level type alias.

For future strict Go compatibility, use the recorded input, output arrays, changed fields, and per-case reproduction command to identify the exact behavior to change. The test never regenerates its approvals automatically. Run summaries, binary hashes, full current differences, and unexplained failures are written under `built/csharp/documentation-*.json`.
