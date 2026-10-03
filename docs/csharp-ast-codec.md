# C# AST binary format

`AstEncoder` writes the complete generated syntax schema, child lists, documentation trees, source references, module metadata, and content mappings. `AstDecoder` reconstructs typed nodes, parent links, documentation caches, and the metadata represented by the wire format. `DecodedAst.Options` carries the source mapping and parse settings needed when encoding the result again.

The C# format is version **9**, with UTF-8 byte positions throughout. It retains the pinned Go version-8 section layout: a 44-byte header, string offset pairs and bytes, extended node data, structured MessagePack metadata, and 28-byte node records. The source hash is XXH3-128. A subtree packet has no source hash or parse-options header. The Go version-8 encoder and its UTF-16 positions remain unchanged in the reference backend.

`GetNodeIndexTableAsync` provides handles before encoding. The same source-file instance keeps the same table; list entries occupy wire indices but return no syntax node. The cache uses weak source-file keys. Holding a table retains its nodes, while releasing both the source and its table allows collection.

Decoding validates section boundaries, string ranges, preorder topology, sibling ownership, typed child slots, list lengths, and structured references. It rejects unsupported versions and source hashes that do not match the embedded text. Traversal and reconstruction use explicit stacks and observe cancellation. Source strings are slices of the decoder's owned packet buffer.

## Validation

Build the compatibility harness in normal Release mode, then run:

```powershell
node csharp/tools/generate-ast-codec.mjs --check
node csharp/tools/ast-codec.mjs
node csharp/tools/ast-codec-corpus.mjs
& $dotnet built/csharp/phase7-build/bin/TypeScript.Compatibility/release/TypeScript.Compatibility.dll --ast-codec-safety
```

The development oracle copies the pinned Go encoder into a separate package and changes only its version and position conversion. The original version-8 encoder/decoder tests execute with their existing assertions and baselines; wrappers record their inputs for the C# comparison. The product has no Go dependency.

Every candidate fixture checks a byte-exact encode/decode/re-encode round trip. Strict comparisons preserve parser differences. A second control feeds the complete C# syntax and source metadata to the independent Go encoder, separating encoder defects from existing syntax differences. Corpus summaries report strict matches, parser differences, and these controls separately; passing a control does not make the two parsers' output identical. Full differing packets and input trees remain in the compressed evidence.

The initial complete corpus run covers 12,865 physical fixtures, 17,431 expanded inputs, and 2,122,684 wire records. All round trips pass; 16,552 packets match strictly, and the 879 differing inputs match the independent encoder when it receives the C# syntax. The focused suite includes 30 recorded original test invocations. Four authored JSDoc cases retain the parser's established range, empty-list, and synthesized-context choices; exact input and output hashes, field explanations, and complete evidence are pinned under `csharp/tests/fixtures/protocol`. Twelve negative controls reject changed inputs or outputs. This ledger does not authorize further differences.

The wire format does not carry checker state, diagnostics, original JSDoc text-fragment boundaries, or a mapped diagnostic's display message and source label. Decoding does not invent those values. Concatenated documentation text, tree structure, ranges, flags, mapping features, and directive ranges/policies/codes are retained.
