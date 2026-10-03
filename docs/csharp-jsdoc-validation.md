# JSDoc parsing and validation

The C# parser keeps documentation trees for JavaScript and exposes lazy documentation for TypeScript through `SourceFileNode.GetDocumentation` and `GetDocumentationAsync`. TypeScript comments containing `@see` or `@link` are parsed eagerly, preserving declaration context and references needed by the checker. Tags retain their type expressions, names, optional/rest information, descriptions, links, namespace structure, source ranges, and scalar fields. JavaScript annotations are also projected into the executable/type AST where the language permits them.

The implementation handles positional destructuring and unnamed parameter tags, Unicode-escaped names, array-qualified property paths, nested typedef properties, callback/overload grouping, `this` parameters, full-signature precedence, template constraints/defaults/modifiers, return type predicates, and import tags. Type-first and name-first parameter/property tags have the corresponding visitor order. Trailing comments are considered only for supported host kinds; an incomplete host cannot acquire a truncated comment.

The async source parser awaits the complete documentation chain. `LeadingAsync`, tag parsing, and the underlying type/import parsers never call a synchronous parser facade. This matters when a deeply nested documentation type needs a fresh stack while only one ThreadPool worker is available. Synchronous API facades remain separate entry points.

## Reproduce the complete documentation gate

```powershell
node csharp/tools/jsdoc.mjs

# Replay an existing Release build in a separate artifacts directory.
node csharp/tools/jsdoc.mjs --no-build --dotnet <path-to-dotnet> `
    --managed-directory built/csharp/phase7-build/bin/TypeScript.SourceMetadata/release
```

The default scope is every `.ts`, `.tsx`, `.js`, and `.jsx` compiler-test source containing `/**`, plus all bundled declaration libraries, including libraries without documentation. Test directives are expanded into their virtual source files. The phase 7 Release CoreCLR run covers 839 physical files, including all 108 libraries, and 1,228 expanded files. It visits 12,366 documentation hosts, 12,600 comment trees, and 50,955 documentation nodes: 901 files match exactly and 327 retain previously documented differences. NativeAOT execution and performance measurement are outside this run.

Each host is identified by kind and source range. Every documentation node records kind, range, flags, child count, generated scalar properties, and generated NodeList metadata. Diagnostics are compared separately. The candidate also checks source-bounded byte ranges, unique node identity, acyclic traversal, and child-parent ownership within each comment tree. The same comment may legitimately be associated with multiple hosts.

The test builds a separate development oracle from the archived revision in `built/csharp/reference.json`. It does not depend on ignored parser-difference snapshots. `--filter <filename>` runs a targeted subset, and `--input <requests.json>` replays a saved input set. Unknown or changed differences fail with a nonzero exit code.

## Intentional differences

`csharp/tests/fixtures/jsdoc/documentation-differences.json` contains one stable ID and exact input/Go/C# SHA-256 triple for each raw difference, together with its policy, rationale, and reproduction command. Its companion `documentation-evidence.json.gz` stores the full input and both complete output arrays. Joining parser text fragments for a semantic assertion never removes characters; the original fragment arrays, ranges, flags, and list metadata remain in the strict evidence.

The historical evidence pins 896 differing files: 868 with identical host identity, type/tag topology, scalar values, and concatenated description text; four synthetic-typedef host ranges; and 24 individually reviewed differences. The current 327 differences comprise 308 lossless representation differences, the four synthetic host ranges, and 15 of the reviewed cases. Parser fixes account for the reduction; the historical evidence has not been replaced.

The input and reference output hashes must still match the saved record. A changed candidate output is accepted only when every field remains its previously approved value or becomes its exact reference value. This comparison preserves ranges, flags, text fragments, list metadata, diagnostics, and ordering. For the already approved extra owner after `await` reparsing, corrected comment fields may come from that same comment on its reference owner; the added owner's identity remains pinned. The helper also rechecks the identical-content assertion for records that make it. Every run executes 36 negative controls, including changed fields, unapproved source files, and moving documentation between distinct declarations of the same kind.

Malformed annotations retain error nodes or diagnostics and their original source. URL links retain the scheme as a name and the remaining URL and label as text fragments. Remaining reviewed differences include malformed recovery, whitespace, and comment decoration; the complete evidence identifies each occurrence, diagnostics, and context.

`csharp/tests/fixtures/parser/jsdoc-differences.json` separately records main-AST differences with generated scalar and list fields. Equal-shape entries preserve node kinds, text, scalar properties, child topology, list counts, and trailing-comma results. The fallback-assignment cases retain `number` parameter annotations through `||`/`??`; TypeScript 6.0.3's checker independently confirms those parameter types. Malformed trailing-dot typedef recovery retains a namespace rather than introducing a file-level type alias.

For future strict Go compatibility, use the recorded input, output arrays, changed fields, and per-case reproduction command to identify the exact behavior to change. The test never regenerates its approvals automatically. Run summaries, binary hashes, full current differences, and unexplained failures are written under `built/csharp/documentation-*.json`.
