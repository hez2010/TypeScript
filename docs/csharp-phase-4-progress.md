# Phase 4: checker port in progress

**Phase 4 is incomplete.** The implementation now covers checker type/state foundations, lexical name and reference resolution, symbol-merge primitives, type normalization, constraint/default resolution, generic/object/mapped instantiation, tuple normalization and type-node substitutions. It does not satisfy the complete semantic-checker gate in the [rewrite plan](csharp-rewrite-plan.md). Semantic diagnostics, full type/symbol queries and the complete emit resolver remain unavailable in the C# backend.

## Implemented checkpoint

`csharp/src/TypeScript.Compiler/Checking/` contains manually ported algorithms and generated flag declarations from the pinned Go checker:

| Area | Implemented responsibility |
| --- | --- |
| `Types.cs` | Typed representations for intrinsic/literal, object/reference/interface/tuple, mapped/reverse-mapped/evolving-array, union/intersection, parameter, index/indexed-access, template/string-mapping, substitution and conditional types; signatures, predicates and index information. Representation alone does not implement evaluation of these types. |
| `TypeContext.cs` | Exclusive type ownership; intrinsic distinctions; string/number/bigint/enum literal interning; fresh/regular literal identity; reference, union, generic-index and substitution cache primitives; signature creation/cloning. |
| `TypeOrder.cs` | Deterministic type order, including declaration/file order, named aliases, type arguments, mapper composition, literal values, tuples and composite types. UTF-8/WTF-8 ordering preserves supplementary characters and lone surrogates. Input-shaped comparisons use an explicit stack. |
| `TypeMapper.cs` | Direct, array-to-single, deferred, function, merged and composite mappings; prepend/append; distributed parameter normalization. Composite instantiation is supplied by the caller. BCL `ValueTask` continuations preserve stack space during re-entry. Inference mappers require the later inference port. |
| `Links.cs` | Sparse per-checker link tables and circular-resolution detection. Resolved properties terminate cycle searches; failed pushes mark participating frames without adding another frame; resolution boundaries isolate nested computations. |
| `Flags.generated.cs` | Source-hashed type/object/signature/element/index/access/variance/check flags and resolution/predicate enums. `generate-checker.mjs --check` verifies freshness. |

Type and signature IDs are local to their owning context. Factories reject combinations from different contexts. Cache keys compare complete identity sequences, including alias arguments and union origins; a hash collision cannot merge unrelated types. Caller-owned arrays are copied before they enter retained type state. Links remain separate from the AST and binder symbols shared by programs.

`TypeAlgebra.cs`, `TypeAlgebra.Intersections.cs` and `TypeAlgebra.Templates.cs` now implement union normalization, literal/subtype reduction, constrained-variable reduction, intersection distribution and disjointness, nullable factoring, aliases/origins, filtering, template construction and intrinsic string mappings. Their required `ITypeAlgebraHost` supplies the checker's structural relations, member/constraint resolution and diagnostics. There is no production fallback for these dependencies. Full checker integration and the implementations of those services remain open.

Intersection keys preserve the reference's distinction between constraint reduction modes, including suppression of alias keys when constraint reduction is disabled. Completed results are cached after normalization. Cross-product and subtype complexity limits are retained; overflow calculations use 64-bit saturation. Input-shaped traversals use explicit stacks or BCL `ValueTask` continuations. The casing implementation uses generated Unicode 15.1.0 data, including full case expansions, Final_Sigma context and lone-surrogate preservation.

`Binding/NameResolver.cs` ports lexical scope traversal, function parameter restrictions, deferred contexts, conditional `infer` scopes, class type-parameter restrictions, decorators, default exports, enum members, `arguments`, CommonJS lookup and reference callbacks. `Binding/ReferenceResolver.cs` ports the shared import/export/value declaration queries. Both retain the reference's checker hooks; alias target resolution and semantic diagnostic callbacks still require checker integration. The corpus probe exercises these components with the reference's default lexical lookup behavior, not complete checker queries.

`Checking/SymbolMerger.cs` implements exclusion masks, transient cloning, declaration precedence, recursive member/export merging, merged-symbol identities, parent repair and directional merges. Alias resolution and diagnostic reporting are required callbacks. Cancellation and callback failures roll back the merger's own tables, symbol changes and identity mappings. Caller-owned diagnostics and alias caches retain their separate request lifetime. Binding symbols remain unchanged when checker-owned clones are extended.

## Executed validation

The published Windows x64 NativeAOT harness passes **2,904 exact differential cases**, comprising **1,333,980 constructor, mapper, resolution and ordering operations**. No comparison policy permits differences in this suite. Probe serialization represents process-local type identities as graph references so sharing, freshness, targets and origins remain observable.

Fixtures include NaN payloads and signed zero, large bigint literals, Unicode/lone-surrogate strings, duplicate enum values, repeated cache requests, strict-null and exact-optional configurations, mapper precedence and repeated deferred evaluations, all ten circular-resolution properties, and deterministic comparison across composite types.

The mapper probe isolates composition using atomic replacements. Its candidate-side callback implements the corresponding atomic instantiation behavior, including the reference's lazy flag updates, and rejects structural inputs. Passing these cases does not establish structural generic instantiation or inference correctness.

An additional **41 native ownership/state assertions** cover immutable inputs, cross-context rejection, signature cloning, cancellation recovery and concurrent independent contexts. Mapper traversal, re-entrant composite instantiation and type ordering run at **50,000 levels**. Sixteen independent contexts exercise cache isolation. These checks do not establish full-checker recursion safety or compiler scheduling parity.

Evidence: [differential summary](../csharp/compatibility/evidence/phase4-type-state.json) and [native state/build/repository validation](../csharp/compatibility/evidence/phase4-type-state-validation.json). Records retain the exact toolchain and candidate/oracle hashes. Validation uses SDK `11.0.100-rc.2.26470.103`, C# 15, `runtime-async=on`, `OptimizationPreference=Speed` and `IlcInstructionSet=native`, with compiler/trimming/AOT warnings treated as errors. No dependencies were added. Go is used exclusively by the development oracle. These correctness tests are not compiler performance measurements.

Reproduce from the repository root after preparing the frozen reference checkout:

```powershell
node csharp/tools/generate-checker.mjs --check
node csharp/tools/checker-types.mjs --record phase4-type-state
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-state
```

The runner accepts `--managed`, `--no-build`, `--go` and `--dotnet`. `--reference-only` runs just the Go fixtures. Failures retain complete inputs and reference/candidate outputs in `built/csharp/checker-types-failures.json`. The test-only bridge is copied into the ignored pinned checkout; it does not modify the product Go checker.

## Name resolution and symbol merging validation

The current NativeAOT artifact also passes:

| Check | Executed coverage |
| --- | --- |
| Lexical name/reference corpus | 17,989 units: 2,983,848 name lookups and 497,308 reference-query groups. 17,609 units match exactly; 380 parser-derived differences are individually audited, with zero unexplained differences. |
| Symbol merging | 4,083 exact cases covering cloning, declaration precedence, exclusion masks, seeded alias callbacks, directional merges and member/export identity. Formatting and attribution of checker merge diagnostics remain open. |
| Name/merge ownership and safety | 21 assertions; 20,000-level parent scopes, parameter initializers and nested namespace merges; cancellation recovery, rollback after callback failure, and immutable original bindings. |
| Earlier type/state regression | All 2,904 cases and 41 state assertions pass on the same native artifact. |
| Difference-policy guards | Fifteen negative controls reject changed source, symbols, diagnostics, reference results, transfer fingerprints or rejection evidence. |

The [name-resolution ledger](../csharp/compatibility/evidence/phase4-name-resolution-differences.json) retains every strict difference and an archive of complete inputs and outputs. Acceptance requires exact Go binding/resolution of the transferred C# tree, including diagnostics, callbacks and reference queries, plus a matching cross-language syntax fingerprint. Most cases must reproduce the existing phase-2 parser hashes. Sixty cases cover two additional reviewed malformed snippets, restricted by source hash and language flavor: both native parsers and the independent TypeScript parser must reject the input. Their full parser evidence is archived. Arbitrary malformed inputs do not receive this exception. `--strict` still fails on the original Go differences.

Evidence: [names](../csharp/compatibility/evidence/phase4-name-resolution.json), [symbol merging](../csharp/compatibility/evidence/phase4-symbol-merging.json), [type regression](../csharp/compatibility/evidence/phase4-types-regression.json), and [current native/repository validation](../csharp/compatibility/evidence/phase4-symbols-validation.json).

```powershell
node csharp/tools/checker-names.mjs --corpus --record phase4-name-resolution
node csharp/tools/checker-symbols.mjs --no-build --record phase4-symbol-merging
node csharp/tools/compare-checker-names.test.mjs
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-names-safety
```

## Type normalization validation

The normalization checkpoint passes **3,885 exact NativeAOT comparisons** containing **259,745 operations**. Coverage includes strict/loose null handling, missing/undefined identity, literal freshness, constrained intersections, union origins and aliases, intersection cache modes, discriminant-based subtype reduction, template distribution, intrinsic string mappings and complexity diagnostics. Casing fixtures cover all **1,112,064 Unicode scalar values**, plus sigma contexts and lone surrogates, against the pinned Go implementation.

The comparison host supplies explicitly constructed primitive constraints and plain-property object relations. Unsupported dependency queries throw. This validates the algebra algorithms and their calls into those services; it does not implement or establish the full structural relater, general constraint resolution, mapped-type analysis or template inference. The production `ITypeAlgebraHost` still requires those checker services. Alias argument lists are encoded as empty sequences when arity is zero; unresolved type-reference argument lists remain distinct from resolved empty lists.

Fifteen native assertions exercise 20,000-level template, intersection and union-origin chains, cross-context rejection, cancellation/retry, cache identity, duplicate compaction and 64-bit cross-product overflow behavior. The 100,000 cross-product threshold and the reference's subtype-work estimate produce diagnostic 2590. The same native artifact also passes the earlier 2,904 type/state cases and 41 ownership/state assertions.

Evidence: [algebra comparisons](../csharp/compatibility/evidence/phase4-type-algebra.json), [type/state regression](../csharp/compatibility/evidence/phase4-algebra-type-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-algebra-validation.json). These are correctness and component safety checks; complete semantic workload performance remains unmeasured.

```powershell
node csharp/tools/generate-casing.mjs --check
node csharp/tools/generate-checker.mjs --check
node csharp/tools/checker-algebra.mjs --record phase4-type-algebra
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-algebra-safety
```

## Constraint and recursion validation

`TypeConstraints.cs` implements direct/base constraint traversal and caching, type-parameter constraints and defaults, indexed and conditional constraint selection, missing type arguments, circularity markers, and the reference's constraint depth budgets. `TypeRecursion.cs` implements origin identities and deep-instantiation detection, including indexed accesses, mapped origins and shared symbols. `TypeVariables.cs` implements the cached type-variable-presence analysis used before instantiation.

The required `ITypeConstraintHost` still supplies AST type evaluation, inferred constraints, general instantiation, mapped/indexed type operations and tuple construction. Tests seed those dependencies explicitly and reject unsupported queries. The implementation therefore advances checker construction without providing a complete semantic checker or a fallback implementation of those services.

NativeAOT passes **613 exact comparisons** containing **20,154 operations**. The probes compare resolved values and cache state: absent/circular/resolving markers, direct versus base constraints, mapped-key `any`, conditional branch constraints, inherited defaults, JavaScript defaults, missing arguments, recursion identities and depth limits. Twenty-one native assertions cover 20,000-level default/identity chains, cancellation cleanup, retry, circular defaults, and ownership. The same native artifact passes all 3,885 algebra comparisons and 15 algebra safety assertions.

Evidence: [constraints](../csharp/compatibility/evidence/phase4-type-constraints.json), [algebra regression](../csharp/compatibility/evidence/phase4-constraints-algebra-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-constraints-validation.json).

```powershell
node csharp/tools/checker-constraints.mjs --record phase4-type-constraints
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-constraints-safety
```

## Instantiation and tuple validation

`TypeInstantiation.cs` implements generic parameter/reference instantiation, composite mapping, aliases, unions/intersections, template/string mappings, substitutions, restrictive/permissive instances and active-mapper caches. `TypeInstantiation.Signatures.cs` clones generic signature parameters and instantiates symbols/index information while preserving lazy return-type and predicate resolution. `TupleTypes.cs` constructs tuple targets and normalizes optional, rest and variadic elements, including spread flattening, union distribution, readonly state and labels.

The required `ITypeInstantiationHost` and `ITupleTypeHost` retain object/mapped/conditional resolution, structural assignability, reverse-mapped inference, general indexed access and array-like analysis as checker dependencies. The comparison host supplies a bounded domain of resolved arrays and primitive/property relations; unsupported services throw. These components do not provide a complete expression or declaration checker.

NativeAOT passes **2,176 exact comparisons** containing **43,584 operations**. Twenty-six native assertions cover fresh signature parameters, lazy resolution, symbol mapper composition, cache reuse, cancellation/retry, ownership, and the reference's exact limits of **100** instantiation levels, **5,000,000** instantiations per statement and **10,000** tuple elements. The same artifact passes all 613 constraint comparisons and 21 constraint safety assertions. Full repository validation passes, with no product Go/JavaScript source changes or added dependencies.

Evidence: [instantiation comparisons](../csharp/compatibility/evidence/phase4-type-instantiation.json), [constraint regression](../csharp/compatibility/evidence/phase4-instantiation-constraints-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-instantiation-validation.json). These are correctness checks, not complete semantic workload performance measurements.

```powershell
node csharp/tools/checker-instantiation.mjs --record phase4-type-instantiation
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-instantiation-safety
```

## Object instantiation validation

`ObjectInstantiation.cs` implements captured-parameter cache keys, composed mappings, anonymous object instantiation, deferred references, instantiation-expression preservation and fresh mapped-type parameters. It filters unused outer parameters through the reference's syntax rules, including conditional `infer` scopes, method signatures and `typeof` scope relationships. Syntax traversal uses explicit stacks/loops; failed cache population removes only the result owned by that operation.

NativeAOT passes **579 exact comparisons**, containing **10,923 operations**, and **40 state/safety assertions**. The comparisons cover alias/cache identity, single-signature keys, repeated/composed mappings, deferred references and 51 parsed reference-analysis snippets. State checks cover lazy members, fresh mapped parameters, caller-owned array snapshots, cancellation/retry and **20,000-level** syntax, parent and qualified-name chains. The same native artifact passes all 2,176 instantiation/tuple comparisons and 26 instantiation safety assertions.

The required `IObjectInstantiationHost` still supplies outer-parameter discovery, semantic name/alias resolution and homomorphic mapped-type evaluation. Comparisons seed outer parameters and reference symbols explicitly; they verify object instantiation and syntax filtering, not the unresolved services or complete checker behavior.

Evidence: [object comparisons](../csharp/compatibility/evidence/phase4-object-instantiation.json), [instantiation regression](../csharp/compatibility/evidence/phase4-objects-instantiation-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-objects-validation.json).

```powershell
node csharp/tools/checker-objects.mjs --record phase4-object-instantiation
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-objects-safety
```

## Mapped types and type-node substitutions

`MappedTypes.cs` implements lazy mapped parameter, constraint, name and template resolution; generic-type classification and its caches; homomorphic distribution; array/tuple mappings; readonly/optional modifiers; wildcard/error handling; and actual-variable normalization. `TypeNodeFlow.cs` applies substitutions in conditional true branches, including parameter variance and unary tuple constraints, and narrows homomorphic iteration keys under array/tuple constraints. This is type-node substitution, not expression control-flow analysis.

The required `IMappedTypeHost` retains general AST type evaluation, structural reduction, array recognition, indexed access and type-fact filtering as checker services. The comparison host connects the implemented constraint, instantiation, object and tuple components, supplies explicit AST values and resolved array/index dependencies, and rejects unsupported structural queries. Mapped member creation, property-name remapping, apparent/modifier type resolution and full checker integration remain open.

Anonymous instantiation also clears the source's member-resolution flag: the new instance owns lazy member state. Differential fixtures exercise instantiation of a resolved source, and state assertions verify that the resulting member data remains unresolved.

NativeAOT passes **784 exact comparisons**, containing **34,692 operations**. They cover strict/loose null checks, exact optional-property handling, readonly/optional modifiers, name-clause resolution and generic classification, indexed templates, array/tuple/rest/variadic/intersection mappings, error aliases, constrained `any`, and 120 parsed conditional type-node substitution cases. Thirty-seven safety assertions cover lazy caches, cancellation/retry, tuple labels, ownership and **20,000-level** actual-variable, generic-flag, kind and unary-tuple analysis. The same artifact passes the earlier 2,176 instantiation cases, 579 object cases and 613 constraint cases, together with their 87 safety assertions.

Evidence: [mapped types](../csharp/compatibility/evidence/phase4-mapped-types.json), [instantiation regression](../csharp/compatibility/evidence/phase4-mapped-instantiation-regression.json), [object regression](../csharp/compatibility/evidence/phase4-mapped-object-regression.json), [constraint regression](../csharp/compatibility/evidence/phase4-mapped-constraint-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-mapped-validation.json). These component checks do not establish complete compiler semantics or performance.

```powershell
node csharp/tools/checker-mapped.mjs --record phase4-mapped-types
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-mapped-safety
```

## Remaining completion work

The following phase-4 requirements remain open:

1. Program/checker integration, global symbol initialization, alias/module export/augmentation resolution and type/value symbol resolution. The lexical resolver and merge primitives are now implemented; their checker-specific callbacks remain to be connected.
2. Structural relations and their caches, general AST constraint evaluation/inference, outer type-parameter discovery and conditional instantiation; connect the implemented algebra, constraints, instantiation workers and tuple algorithms to these complete checker services.
3. Inference, contextual typing, signatures and overload selection, expression/declaration checking, JavaScript and JSDoc semantics.
4. Flow analysis and narrowing, evolving arrays, definite assignment, exhaustiveness and semantic diagnostics.
5. Mapped members, property remapping and modifier/apparent types; conditional, indexed-access and template type evaluation; JSX, decorators and grammar checks.
6. Type display, node builders, symbol accessibility and emit-resolver APIs.
7. All active checker/compiler type/symbol/diagnostic comparisons at single and reference-default concurrency; audits of intentional differences; complete semantic workload memory/performance measurements.

The next integration step is checker-owned global/alias resolution and declared-type construction over the phase-3 program and binding model. The original Go backend remains the product backend. The full checker completion gate and retained-platform release gates are unchanged.
