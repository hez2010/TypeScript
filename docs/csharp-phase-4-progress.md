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

The required `IMappedTypeHost` retains general AST type evaluation, structural reduction, array recognition, indexed access and type-fact filtering as checker services. The comparison host connects the implemented constraint, instantiation, object and tuple components, supplies explicit AST values and resolved array/index dependencies, and rejects unsupported structural queries. Full structural relations, apparent-type resolution and checker integration remain open.

Anonymous instantiation also clears the source's member-resolution flag: the new instance owns lazy member state. Differential fixtures exercise instantiation of a resolved source, and state assertions verify that the resulting member data remains unresolved.

NativeAOT passes **784 exact comparisons**, containing **34,692 operations**. They cover strict/loose null checks, exact optional-property handling, readonly/optional modifiers, name-clause resolution and generic classification, indexed templates, array/tuple/rest/variadic/intersection mappings, error aliases, constrained `any`, and 120 parsed conditional type-node substitution cases. Thirty-seven safety assertions cover lazy caches, cancellation/retry, tuple labels, ownership and **20,000-level** actual-variable, generic-flag, kind and unary-tuple analysis. The same artifact passes the earlier 2,176 instantiation cases, 579 object cases and 613 constraint cases, together with their 87 safety assertions.

Evidence: [mapped types](../csharp/compatibility/evidence/phase4-mapped-types.json), [instantiation regression](../csharp/compatibility/evidence/phase4-mapped-instantiation-regression.json), [object regression](../csharp/compatibility/evidence/phase4-mapped-object-regression.json), [constraint regression](../csharp/compatibility/evidence/phase4-mapped-constraint-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-mapped-validation.json). These component checks do not establish complete compiler semantics or performance.

```powershell
node csharp/tools/checker-mapped.mjs --record phase4-mapped-types
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-mapped-safety
```

## Mapped member construction

`MappedMembers.cs` implements mapped property/index construction, name filtering and remapping, merged keys for colliding names, source declarations and modifier inheritance, lazy property types, circular-property detection, index signature combination, modifier-type resolution, combined optionality and key lower bounds. Lower bounds retain the special primitive/empty-type-literal intersections and use the distinct `EmptyTypeLiteralType` sentinel.

Member resolution exposes an initially empty resolved type to recursive queries, then installs the completed members. Cancellation restores the prior member state and removes newly allocated property links. Lazy property resolution unwinds its resolution stack on failure, caches completed types and reports diagnostic 2615 for a circular property through the required reporting callback.

The required `IMappedMemberHost` supplies general apparent types, properties, semantic readonly/name queries, applicable index signatures, assignability and conditional instantiation. The test host provides explicit resolved property/index fixtures, preserving metadata and rejecting unsupported structural operations. Complete semantic name/alias/type construction and the structural relater remain required for checker integration.

NativeAOT passes **1,592 exact comparisons**, containing **28,962 operations**. They cover finite keys, optional/readonly inheritance and overrides, filtering and constant remapping, declaration order, lazy property caches, colliding enum names, string/number/symbol/pattern index signatures, and primitive/empty-literal lower bounds. Member names are compared as WTF-8 bytes, retaining NUL, supplementary characters and lone surrogates without JSON replacement loss. Thirty safety assertions verify re-entry into member construction, cancellation cleanup and retry, circular property resolution, ownership and **20,000-level** key/modifier traversals.

The same native artifact passes 784 mapped-type cases, 2,176 instantiation/tuple cases and 2,904 type/state cases, plus their 104 safety assertions. Evidence: [members](../csharp/compatibility/evidence/phase4-mapped-members.json), [mapped regression](../csharp/compatibility/evidence/phase4-members-mapped-regression.json), [instantiation regression](../csharp/compatibility/evidence/phase4-members-instantiation-regression.json), [type/state regression](../csharp/compatibility/evidence/phase4-members-type-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-mapped-members-validation.json). Full semantic workload performance remains unmeasured.

```powershell
node csharp/tools/checker-mapped-members.mjs --record phase4-mapped-members
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-mapped-members-safety
```

## Program symbols and declaration headers

`CheckerSymbols.cs` now owns global symbols over a `CompilerProgram`, connects the lexical resolver to merged declarations, tracks reference kinds and scope caches, and preserves the ordering of global, ambient and module augmentations. It maintains UMD first-in-wins behavior and separate directional pattern-augmentation tables without modifying shared bindings.

`GlobalTypes.cs` resolves standard global class/interface types with the reference's arity checks and missing-type fallbacks. `TypeParameterScopes.cs` constructs class/interface headers, captured and local generic parameters, mapped/infer scopes and polymorphic `this` types. Interface analysis reads `ContainsThis` from binding state rather than modifying syntax. Failed recursive class construction removes the provisional headers owned by that operation.

The required symbol host still supplies alias and computed-name resolution, import-attribute identity, export-star resolution and checker diagnostic hooks. Contextual signatures and entity-name resolution remain required scope dependencies. The program probe uses actual parsed/bound file graphs and these production initialization/header algorithms, while rejecting semantic queries outside its explicit host domain. This does not yet expose a complete checker through `CompilerProgram`.

NativeAOT passes **180 exact program comparisons**: 90 source configurations under single-worker and parallel program construction. They cover merged interfaces/namespaces, nested generic scopes, polymorphic `this`, cyclic interface headers, global and module augmentations, directional pattern augmentations, UMD exports, Unicode names and global-type arity errors. The probe compares global/member/export symbol graphs, declaration identities, class/interface header graphs, captured parameters and initialization diagnostic codes. It does not validate general diagnostic formatting or expression/declaration checking.

Twenty-five native assertions verify shared program/binding ownership, isolated checker caches, initialization cancellation, rollback of recursive interface construction, contextual-signature injection and **20,000-level** interface and scope chains. The same artifact passes the 2,904 type/state comparisons, 41 type/state assertions and 21 name/merge assertions. Evidence: [program symbols and headers](../csharp/compatibility/evidence/phase4-program-symbols.json), [type/state regression](../csharp/compatibility/evidence/phase4-program-type-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-program-symbols-validation.json).

```powershell
node csharp/tools/checker-program.mjs --record phase4-program-symbols
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-program-safety
```

## Alias and module export resolution

`AliasResolver.cs` resolves pure alias chains while retaining the local meanings of merged aliases. It implements immediate/final target caches, type-only propagation, meaning-specific lookup, aggregate flags, circularity and deprecation traversal. `AliasTargets.cs` dispatches import/export/CommonJS alias declarations; `EntityNames.cs` connects qualified names to the program-backed lexical resolver. `ModuleExports.cs` traverses export-star graphs, handles cycles and collisions, and records type-only exports with ordinary-export overrides. `ModuleTypes.cs` creates namespace wrapper symbols with independent tables and drops call/construct signatures from the wrapper type.

The remaining target hosts provide expression checking, synthetic-default and CommonJS/ES module type adaptation, computed exports, import-attribute evaluation and complete diagnostics. The source probe supplies declared ES module dependencies and rejects unsupported services. Cancellation removes unfinished alias targets and type-only markers, unwinds resolution stacks, and leaves unfinished export tables unpublished.

NativeAOT passes **124 exact alias/export program comparisons**, covering 62 source configurations under single-worker and parallel program construction. The probes retain immediate/final alias identities, merged value/type meanings, type-only declaration origins, namespace wrapper identity, export-star cycles/overrides/collisions, internal import aliases, direct `require` aliases and initialization/resolution diagnostic codes. Full diagnostic formatting and module interop behavior remain outside this host domain.

Thirty-one native safety assertions cover **20,000-alias chains**, cycles, cancellation/retry, deprecation callbacks, wrapper ownership/signature removal and failed export-table construction. The same artifact passes the earlier 180 program comparisons and 2,904 type/state comparisons, with 66 program/type safety assertions. Evidence: [aliases](../csharp/compatibility/evidence/phase4-aliases.json), [program regression](../csharp/compatibility/evidence/phase4-alias-program-regression.json), [type/state regression](../csharp/compatibility/evidence/phase4-alias-type-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-alias-validation.json).

```powershell
node csharp/tools/checker-program.mjs --aliases --record phase4-aliases
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-alias-safety
```

## Source type-node evaluation

`TypeNodes.cs` evaluates primitive, literal, union/intersection, array/tuple, function/object, mapped, template, operator and inferred type syntax, and dispatches type queries, conditional types and import types to their required semantic services. It attaches aliases, preserves completion-oriented primitive/empty-literal intersections and eagerly resolves mapped constraints.

`DeclaredTypes.cs` constructs declared alias and enum types and connects class/interface, parameter and import-alias declarations to the existing symbol/type components. `TypeReferences.cs` resolves named types, generic arity/defaults, unresolved-name error aliases, intrinsic aliases and deferred references, including recursive alias caching and distributed parameter identity. Generic indexed-access interning now retains alias identity and persistent access flags in `TypeContext`.

The program probe evaluates real source annotations and alias bodies, then materializes deferred arguments and compares type graphs and cache state. It supplies literal/enum-value and bounded relation/index dependencies; full value checking, conditional/import-type evaluation, JSDoc interpretation and general structural relations remain required services. Dispatch coverage is not proof that those services are implemented.

NativeAOT passes **192 exact source-type comparisons**, covering 96 configurations under single-worker and parallel program construction. Coverage includes primitive/literal types, generic defaults and nested captures, aliased objects/functions, arrays/tuples, recursive aliases, template/intrinsic aliases, mapped/indexed syntax, unique symbols, enum type construction and arity/circularity errors. Twenty-five safety assertions verify deferred argument/alias cancellation and retry, cache identity, ownership and **20,000-level** array/alias ancestry traversal.

The same artifact passes 124 alias/export programs, 1,592 mapped-member cases and 2,904 type/state cases, plus their 102 safety assertions. Evidence: [source types](../csharp/compatibility/evidence/phase4-type-nodes.json), [alias regression](../csharp/compatibility/evidence/phase4-type-nodes-alias-regression.json), [member regression](../csharp/compatibility/evidence/phase4-type-nodes-members-regression.json), [type/state regression](../csharp/compatibility/evidence/phase4-type-nodes-state-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-type-nodes-validation.json).

```powershell
node csharp/tools/checker-program.mjs --type-nodes --record phase4-type-nodes
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-type-nodes-safety
```

## Structured members and signatures

`StructuredMembers.cs` resolves declared and instantiated object members, generic inheritance, call/construct signatures and index information. It preserves the reference's member ordering and exposes partial members during recursive resolution. `BaseTypes.cs` implements interface and tuple bases, base-cycle checks and polymorphic `this` substitution. `IndexSignatures.cs` constructs explicit indexes and aggregates computed index declarations through required semantic callbacks.

`Signatures.cs` constructs declaration signatures, skips overload implementations, tracks explicit `this`, minimum argument counts, rest/literal/constructor flags, and lazily resolves return types and predicates. Instantiated and composite signatures preserve mapper and predicate behavior. Cancellation restores unfinished member state, clears provisional predicate results and unwinds return/base resolution stacks.

The source probe connects these components to actual bound declarations. Its value dependency accepts explicit annotations and instantiated/mapped symbols; body inference, contextual typing, computed names, class base constructors and union/intersection/reverse-mapped member composition remain required services. Those boundaries are explicit failures, not success fallbacks. The probe compares reachable property, signature, predicate, index, type and symbol graphs; diagnostics are compared by code only.

NativeAOT passes **176 exact source-member comparisons**, covering 88 configurations under single-worker and reference-default concurrency. Cases include generic inheritance and defaults, overrides, recursive objects, overloads, call/construct signatures, typed rest/optional/`this` parameters, assertions, polymorphic `this`, arrays/tuples, readonly/pattern/union indexes, interface cycles, merged declarations and Unicode names. Thirty-one safety assertions cover cancellation/retry, cache identity, cross-checker ownership, composite predicates, optional-chain return markers and **20,000-level** base/signature chains.

The same artifact passes 192 source-type, 124 alias/export and 2,904 type/state comparisons, plus 122 earlier safety assertions. Evidence: [source members](../csharp/compatibility/evidence/phase4-structured-members.json), [source-type regression](../csharp/compatibility/evidence/phase4-structured-types-regression.json), [alias regression](../csharp/compatibility/evidence/phase4-structured-alias-regression.json), [type/state regression](../csharp/compatibility/evidence/phase4-structured-state-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-structured-validation.json). Full semantic workload memory and performance remain unmeasured.

```powershell
node csharp/tools/checker-program.mjs --members --record phase4-structured-members
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-members-safety
```

## Symbol read and write types

`SymbolTypes.cs` adds the production symbol-type dispatcher and read/write caches. It resolves instantiated and deferred types, accessor annotations and their distinct setter types, value aliases, class prototypes, and function/class/enum/module value objects. Optional-property writes remove the missing-type sentinel. Variable resolution preserves types assigned by contextual typing and defers caching while a parameter remains context-sensitive. Return annotations and accessor reads now share the setter-parameter selection rule.

Variable inference, contextual-sensitivity analysis, reverse-mapped values, class base constructors and diagnostic policy remain required host services. The source probe supplies explicit variable annotations and classes without inheritance; unsupported inference paths fail. This checkpoint does not implement expression checking or control-flow-sensitive symbol queries.

NativeAOT passes **136 exact source-value comparisons**, covering 68 configurations under single-worker and reference-default concurrency. They compare read/write identities, generic and automatic accessors, optional methods/properties, class prototypes/constructors, enum values, namespaces, imported/internal aliases, alias cycles, ambient modules and private ambient accessors. Thirty-two safety assertions verify accessor/alias cancellation and retry, separate read/write caches, deferred composite types, circular variables, contextual-parameter cache ownership and **20,000-level** instantiated-symbol chains.

The same artifact passes 176 structured-member and 192 source-type comparisons and 128 earlier safety assertions. Evidence: [symbol types](../csharp/compatibility/evidence/phase4-symbol-types.json), [member regression](../csharp/compatibility/evidence/phase4-values-members-regression.json), [source-type regression](../csharp/compatibility/evidence/phase4-values-types-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-values-validation.json).

```powershell
node csharp/tools/checker-program.mjs --values --record phase4-symbol-types
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-values-safety
```

## Composite properties and type views

`TypeProperties.cs` resolves ordinary and composite properties, Object/Function augmentation, type-only export filtering, partial union properties, accessibility/readonly propagation, shared declarations, instantiated-member clones and deferred read/write types. Separate caches retain augmented and unaugmented lookups. `TypeViews.cs` implements primitive/constraint views, apparent mapped-array types, polymorphic `this` views, empty-type classification and reduction of conflicting discriminant/private-member intersections to `never`. Provisional reduction/classification flags are rolled back on cancellation.

`CompositeMembers.cs` assembles union/intersection members, index information and mixin constructor results. Full signature matching and array-member signature adaptation remain required callbacks. Applicable index lookup now combines overlapping indexes and retains the reference's string-index fallback and readonly rules. The source probe connects mapped-member queries to the production property/view services; general relations, computed-name evaluation, inference and callable-composite matching remain open.

These queries also exposed two integration dependencies: numeric template-key checks need ECMAScript string-to-number conversion, and omitted strict options default to enabled in the pinned reference. `JsNumber.FromString` now handles ECMAScript whitespace, decimal and prefixed integers, signed zero and binary64 rounding/overflow. `CompilerOptions.StrictOption` applies the reference default and explicit per-option overrides to the checker consumers.

NativeAOT passes **192 exact program comparisons**, covering 96 configurations at single and reference-default concurrency. They exercise partial/read-write properties, optional/readonly combinations, private/protected declarations, discriminant reduction, generic instances, index combinations, tuple rests, primitive augmentation, mapped array constraints and strict defaults/overrides. The same run includes **3,818 exact numeric-string conversions** with binary64 bit comparisons, including finite/overflow boundaries, long inputs and invalid UTF-16. NaN results use the existing numeric probe's canonical `nan` representation.

Forty-nine native safety assertions cover cache sharing/separation, cancellation and retry, provisional reduction/classification flags, ownership, index applicability and a **20,000-level** reduction chain. The same artifact passes 136 symbol-type, 176 structured-member, 192 source-type, 180 program/header and 784 mapped-type comparisons, plus 150 earlier safety assertions. Evidence: [properties/views](../csharp/compatibility/evidence/phase4-properties.json), [values](../csharp/compatibility/evidence/phase4-properties-values-regression.json), [members](../csharp/compatibility/evidence/phase4-properties-members-regression.json), [source types](../csharp/compatibility/evidence/phase4-properties-types-regression.json), [programs](../csharp/compatibility/evidence/phase4-properties-program-regression.json), [mapped types](../csharp/compatibility/evidence/phase4-properties-mapped-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-properties-validation.json).

```powershell
node csharp/tools/checker-program.mjs --properties --record phase4-properties
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-properties-safety
```

## Signature matching and composition

`SignatureParameters.cs` implements expanded tuple-rest counts, minimum arity (including trailing `void` and untyped JavaScript modes), positional parameter types, effective rest types, tuple slices and labels derived from binding patterns. `SignatureComparison.cs` matches arity, maps generic constraints/defaults, compares predicates and preserves the relater's ternary results through a required type-comparison service.

`SignatureComposition.cs` selects common overloads, constructs compatible union/intersection signatures, combines `this` and parameter types, and adapts unions of array methods through a common element type. Failed composition removes its unfinished synthetic symbol links. Inherited signature lists retain their identity when concatenating an empty list, which matters when selecting a union's primary overload list.

The source probe supplies bounded primitive identity/subtype comparisons and indexed access to declared arrays/tuples. General structural relations, contextual typing, inference and JavaScript/JSDoc value checking remain required services. It queries signatures of declared and reachable member types while recording parameter/return/rest type graphs without recursively enumerating generated generic members. This keeps queries such as array methods returning `Array<U>` finite without imposing a depth cutoff; both runtimes execute the same queries, and the generated type graphs remain in the output.

NativeAOT passes **216 exact signature program comparisons**, covering 108 configurations at single and reference-default concurrency. They compare expanded positions and labels, cached arity, signature identity/targets/composites, overload inheritance, generics and defaults, predicates/assertions, constructors/mixins, `Function` unions, and mutable/readonly array-method fallback. Thirty-nine native safety assertions cover cancellation/retry, synthetic-symbol cleanup, ternary propagation, ownership and **20,000-level** binding labels.

The same artifact passes 192 property/view cases (including 3,818 numeric-string conversions), 176 member cases, 192 source-type cases, 2,176 instantiation/tuple cases and 613 constraint cases, plus 152 earlier safety assertions. Evidence: [signatures](../csharp/compatibility/evidence/phase4-signatures.json), [properties](../csharp/compatibility/evidence/phase4-signatures-properties-regression.json), [members](../csharp/compatibility/evidence/phase4-signatures-members-regression.json), [source types](../csharp/compatibility/evidence/phase4-signatures-types-regression.json), [instantiation](../csharp/compatibility/evidence/phase4-signatures-instantiation-regression.json), [constraints](../csharp/compatibility/evidence/phase4-signatures-constraints-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-signatures-validation.json).

```powershell
node csharp/tools/checker-program.mjs --signatures --record phase4-signatures
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-signatures-safety
```

## Relation keys, normalization and structural identity

`RelationKeys.cs` preserves symmetric identity keys, generic parameter renaming, constrained/broad key distinctions and the reference's four-level generic-key expansion. Keys retain complete typed contents as well as a hash. `Relations.cs` owns recursive assumptions, reliability flags and completed comparison results. It preserves the 100-level `Maybe` cutoff, expanding-generic detection and the work-budget formula. Cancellation rolls back the session's cache writes only while it still owns those entries.

`TypeNormalization.cs` normalizes literals, deferred references, substitution types and eligible empty derived interfaces, and supplies the tuple/intersection normalization paths. Indexed/conditional simplification remains a required service. `TypeRelations.cs` supplies primitive relation predicates and the comparison entry point; `TypeIdentity.cs` handles recursive structural identity. Signature identity now shares the active relation operation, so recursive function types use its assumptions and cache. General structural assignability/subtyping, variance measurement, enum relations and complete diagnostics remain required services; these are not fallback successes.

NativeAOT passes **160 exact program comparisons**, containing **3,000 identity/cache queries**, primitive predicates for all five relation kinds, and **6,240 relation-key queries**. Coverage includes recursive objects/functions, nested method types, accessors, optional/readonly members, tuples, unions/intersections, template literals, empty derived interfaces, generic key equivalence and the reference's 99/100/101-level behavior.

Thirty-six native safety assertions verify cancellation/retry, rollback ownership, cache reliability, provisional results, the expanding-generic heuristic and the exact 100-level cutoff. Key and normalization chains reach **20,000 levels**. The work-budget test forces the remaining counter to zero to verify overflow caching/reporting; it does not claim to execute millions of comparisons.

The same artifact passes 216 signature, 192 property/view, 192 source-type and 613 constraint cases, plus 175 earlier safety assertions. Evidence: [identity and keys](../csharp/compatibility/evidence/phase4-relations.json), [signatures](../csharp/compatibility/evidence/phase4-relations-signatures-regression.json), [properties](../csharp/compatibility/evidence/phase4-relations-properties-regression.json), [source types](../csharp/compatibility/evidence/phase4-relations-types-regression.json), [constraints](../csharp/compatibility/evidence/phase4-relations-constraints-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-relations-validation.json).

```powershell
node csharp/tools/checker-program.mjs --identity --record phase4-relations
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-relations-safety
```

## Structural relations, variance and type facts

`StructuralRelations.cs` implements ordered union/intersection comparisons, nullable-target normalization, weak-type checks and the structural comparison path. `ObjectRelations.cs` compares properties, optionality, accessibility, tuples and index signatures. `SignatureAssignability.cs` handles parameter/return variance, callbacks, rest parameters, predicates, constructor visibility and generic erasure. Contextual generic inference remains a required service.

`TypeVariance.cs` measures generic variance with checker-owned marker types, honors explicit `in`/`out` annotations, propagates reliability flags and restarts circular measurements in declaration order. Failed measurements restore cache and resolution state. `DiscriminantRelations.cs` implements discriminant indexing and combination checks, including the reference's 25-combination limit.

`TypeFactQueries.cs` supplies type facts and nullable filtering/adjustment. `TemplateMatching.cs` infers template segments and checks number/bigint/string-mapping placeholders. It uses WTF-8 byte matching and consumes complete code points for adjacent placeholders, preserving the pinned reference's behavior for supplementary characters and lone surrogates.

The source probe now uses production structural relation and variance algorithms. Fresh-object checking, expression contextual typing, enum comparison and full diagnostic elaboration remain required services. These gaps still prevent complete checker integration.

NativeAOT passes **332 exact program comparisons**, covering 166 configurations at single and reference-default concurrency. The programs contain **6,696 type pairs**, each checked under identity, subtype, strict subtype, assignability and comparability (**33,480 relation decisions**), with cache counts, inferred variances, type facts and resulting type graphs retained. Cases include recursive variance, aliases, callbacks, readonly/optional members, protected/private properties, tuples, discriminants and template matching.

Thirty-three native safety assertions verify variance cancellation/retry, relation-cache rollback, reliability markers, discriminant caching, signature erasure and nullable facts. The same artifact passes 160 identity/key, 216 signature, 192 property/view, 192 source-type and 2,904 type/state cases, plus 190 earlier safety assertions. Evidence: [structural relations](../csharp/compatibility/evidence/phase4-assignability.json), [identity](../csharp/compatibility/evidence/phase4-assignability-identity-regression.json), [signatures](../csharp/compatibility/evidence/phase4-assignability-signatures-regression.json), [properties](../csharp/compatibility/evidence/phase4-assignability-properties-regression.json), [source types](../csharp/compatibility/evidence/phase4-assignability-types-regression.json), [type/state](../csharp/compatibility/evidence/phase4-assignability-state-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-assignability-validation.json).

```powershell
node csharp/tools/checker-program.mjs --assignability --record phase4-assignability
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-assignability-safety
```

## Key enumeration and indexed-access validation

`TypeKeys.cs` implements `keyof` over ordinary, union/intersection, mapped and generic types. It preserves key origins, accessibility, index-signature filtering and the unique-literal substitution used to detect generic intersections that can reduce to `never`. Property-key caches retain the reference's distinction between unresolved and resolved members.

`IndexedTypes.cs` implements type-level indexed access, tuple bounds, index-signature selection, missing/undefined handling, generic deferral, read/write distribution and mapped-template substitution. Simplification caches recover after cancellation. Generic indexed-access cache keys retain all access flags while the resulting type stores only persistent flags, matching the reference. Expression access, contextual properties and deprecation reporting remain required host services.

The source probe uses these production components for key and indexed-access evaluation, including instantiation and constraint queries. NativeAOT passes **320 exact program comparisons** at single and reference-default concurrency: **4,736 key queries** across all key flags and **8,000 indexed-access queries**, each also simplified for reading and writing. Cases cover strict nulls, exact optional properties, unchecked indexing, tuples, generic intersections, remapping/filtering, nested accesses, accessor write types and multiple missing-key diagnostics. Comparisons retain diagnostics codes, complete type graphs and cache identities.

Twenty-four native safety assertions cover cancellation/retry, context ownership, flag-sensitive interning and **20,000-level** indexed simplification/key traversal. The same artifact passes 1,404 earlier source-program comparisons, 2,904 type/state cases (1,333,980 operations), and 511 earlier safety assertions. Evidence: [indexing](../csharp/compatibility/evidence/phase4-indexing.json), [structural relations](../csharp/compatibility/evidence/phase4-indexing-assignability-regression.json), [type/state](../csharp/compatibility/evidence/phase4-indexing-state-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-indexing-validation.json).

```powershell
node csharp/tools/checker-program.mjs --indexing --record phase4-indexing
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-indexing-safety
```

## Generic key, indexed and mapped relations

`GenericRelations.cs` adds contravariant key comparisons, indexed-access component comparisons and writable constraints, known tuple keys, mapped-template comparisons, key remapping/filtering and optionality rules. Generic targets are examined before source constraints. Mapped types can continue to ordinary structural comparison when appropriate; generic mapped identity compares renamed parameters, modifiers and templates. Variance reporting retains the instantiated constraint used by the reference.

The expanded relation probe also checks remapped-key constraints, including template literals whose prefix must survive constraint resolution. NativeAOT passes **128 exact program comparisons**, covering **3,048 type pairs** across five relation kinds (**15,240 decisions**) with cache counts, variance arrays, facts and type graphs. Fixtures include recursive mapped types, partial/required copies, nested alias variance, constrained index parameters and homomorphic key remapping.

Twenty-two native safety assertions verify cancellation rollback/retry, mapped optionality and identity, key contravariance, writable constraints and cached-request cancellation. The same artifact passes all 1,724 earlier source-program comparisons, 2,904 type/state cases and 535 earlier safety assertions. Evidence: [generic relations](../csharp/compatibility/evidence/phase4-generic-relations.json), [indexing](../csharp/compatibility/evidence/phase4-generic-relations-indexing-regression.json), [structural relations](../csharp/compatibility/evidence/phase4-generic-relations-assignability-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-generic-relations-validation.json).

```powershell
node csharp/tools/checker-program.mjs --generic-relations --record phase4-generic-relations
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-generic-relations-safety
```

## Conditional evaluation and relations

`ConditionalTypes.cs` implements deferred branch selection, permissive/restrictive checks, distributive instantiation, alias-sensitive caches, tail recursion and conditional simplification. `ConditionalRelations.cs` implements branch and constraint comparisons, distribution-dependency checks and the reference's conditional nesting cutoff. The subsequent inference checkpoint below supplies conditional `infer` binding and inference between conditional types.

The source probe now serializes conditional roots, captured/inferred parameters, branch caches and default/distributive constraints. Differential comparisons exposed and fixed the order of substitution intersections: the reference inserts the constraint before the base type. That order affects relation traversal and cache entries even when the final Boolean result agrees.

NativeAOT passes **152 exact program comparisons**, including **1,200 type pairs** under five relation kinds (**6,000 decisions**). Cases cover primitive/object/function checks, tuples, unions, nullable modes, alias/mapper propagation, recursive evaluation and both dependent and independent conditional branches. Twenty-four native safety assertions verify cancellation/retry, cache reuse, restrictive instantiation and the exact **1,000-step tail-recursion** and **10-level conditional-relation** limits.

The same artifact passes 1,852 earlier source-program comparisons, 613 constraint cases, 2,904 type/state cases and 557 earlier safety assertions. Evidence: [conditionals](../csharp/compatibility/evidence/phase4-conditional.json), [constraints](../csharp/compatibility/evidence/phase4-conditional-constraints-regression.json), [generic relations](../csharp/compatibility/evidence/phase4-conditional-generic-relations-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-conditional-validation.json).

```powershell
node csharp/tools/checker-program.mjs --conditional --record phase4-conditional
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-conditional-safety
```

## Type inference, reverse mapping and widening

`TypeInference.*.cs` implements candidate collection/resolution, inference priorities, variance, union/intersection matching, tuple slices/rests, signatures, template placeholders, conditional inference and generic mapped inference. `InferenceContext.cs` supplies fixing/non-fixing mappers, cloning and cancellation recovery. `InferredConstraints.cs` resolves constraints from `infer` declarations and surrounding references. `TypeMapper.cs` now supports async inference callbacks through BCL `ValueTask`.

`ReverseMappedInference.cs` implements homomorphic reversal, arrays/tuples, lazy reversed properties, index signatures, filtering constraints and recursive inference limits. `TypeWidening.cs` supplies literal and contextual object widening. `SignatureInstantiation.cs` and the inference engine instantiate generic signatures in a contextual signature. Context-sensitive expression sites and call-site overload/type-argument selection still require expression-checker integration.

NativeAOT passes **176 exact program comparisons** with **856 type pairs** under five relation kinds (**4,280 decisions**). Cases cover repeated `infer` variables, covariance/contravariance, tuple rests, template scalar constraints, dependent inferred constraints, reverse mapped objects/arrays/tuples, and contextual generic callbacks/defaults. Thirty-five native safety assertions cover priorities, fixed inference state, cloning, cancellation/retry, widening, reverse mapping and **20,000-level** traversal.

The reference's `getInferTypeParameters` enumerates a Go map. Two unchanged reference runs differed on 15 of 96 inputs solely through inferred-parameter ordering and consequent graph IDs. The development probes now order inferred parameter groups by declaration **only when serializing outer scopes and conditional-root metadata**. Ordinary type-parameter ordering and checker-internal lists/mappers are unchanged. Three repeated reference runs then produced identical output. The [ordering audit](../csharp/compatibility/evidence/phase4-inference-reference-order-audit.json) records this normalization; inferred types, constraints and cache comparisons remain required to match.

The same artifact passes 2,004 earlier source-program comparisons, 613 constraint cases, 2,904 type/state cases and 581 earlier safety assertions. Evidence: [inference](../csharp/compatibility/evidence/phase4-inference.json), [conditionals](../csharp/compatibility/evidence/phase4-inference-conditional-regression.json), [constraints](../csharp/compatibility/evidence/phase4-inference-constraints-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-inference-validation.json).

```powershell
node csharp/tools/checker-program.mjs --inference --record phase4-inference
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-inference-safety
```

## Constants, primitive expressions and declaration initializers

`Semantics/ConstantEvaluator.cs` evaluates numeric/string operations and templates with entity callbacks, outer-expression skip modes, and cross-file/external-reference provenance. `EnumValues.cs` adds constant/computed enum values, automatic numbering, forward-reference checks, ambient/const enum rules and cancellation recovery. Declaration ordering and computed initializer checking retain required host services.

`ExpressionTypes.cs`, `ExpressionChecks.cs` and `TypePredicates.cs` implement primitive literals, unary results, `typeof`/`void`, non-null assertions, conditional/template result types, null/truthiness checks and expression-state restoration. `VariableTypes.cs` implements annotation/initializer selection, optionality, automatic variable types, setter parameters, catch variables and widening. Binding patterns, property-initialization flow and context-sensitive parameters remain required services. General access/call/object/array/function expression checking is still incomplete.

The numeric helpers now retain the reference's explicit NaN payload and remainder special cases. Integer exponentiation follows its exact-integer path and 256-bit intermediate rounding, including the pinned implementation's int64 conversion boundary. Existing implementation-approximated floating-power policy is unchanged.

NativeAOT passes **40 exact primitive-expression programs** (256 expression queries), **48 exact constant/enum programs** (292 value/provenance queries), and **48 exact initializer programs**, at single and reference-default concurrency. Twenty-five native safety assertions cover cache/cancellation recovery, source-state restoration, literal widening, skip modes, signed zero and **20,000-level** expression/constant traversal.

The same artifact passes 2,180 earlier source-program comparisons, 2,904 type/state cases and 616 earlier safety assertions. The existing foundation verifier passes 458,098 assertions, including 676 numeric pairs; its 18 permitted floating-power differences remain within the previously recorded policy. Evidence: [expressions](../csharp/compatibility/evidence/phase4-expressions.json), [constants](../csharp/compatibility/evidence/phase4-expressions-constants-regression.json), [initializers](../csharp/compatibility/evidence/phase4-expressions-initializers-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-expressions-validation.json).

```powershell
node csharp/tools/checker-program.mjs --expressions --record phase4-expressions
node csharp/tools/checker-program.mjs --constants --record phase4-expressions-constants-regression
node csharp/tools/checker-program.mjs --initializers --record phase4-expressions-initializers-regression
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-expressions-safety
```

## Binary operators and awaited types

`BinaryExpressions.cs` implements arithmetic, comparison, logical/coalescing and comma result types, preserving operand order and the reference's nullable/BigInt rules. `ExpressionChecks.cs` now checks syntactic nullishness and mixed coalescing/logical grammar. Assignment references, destructuring, `in`/`instanceof`, and complete diagnostic elaboration remain required host services.

`AwaitedTypes.cs` resolves promises and thenables, validates fulfillment callbacks and `this` receivers, detects recursive fulfillment, distributes unions, and preserves generic `Awaited<T>` wrappers. Failed or canceled recursive evaluation unwinds the awaited stack.

NativeAOT passes **56 exact binary-expression programs** (352 expression queries) and **24 exact awaited-type programs** (116 query groups) at single and reference-default concurrency. Twenty-eight native safety assertions cover diagnostics, recursive promises, cancellation/retry, and **20,000-level** binary traversal. The same artifact passes 2,316 earlier source-program comparisons, 2,904 type/state cases and 641 earlier safety assertions.

Repeated reference runs revealed that unordered inferred-parameter traversal can populate different lazy generic-variable flags. The awaited probe now explicitly queries `couldContainTypeVariables` for every serialized type and compares its Boolean result and resulting flags. This does not reorder inference or remove semantic/cache comparisons. Three repeated reference runs then produced identical output; the [classification audit](../csharp/compatibility/evidence/phase4-binary-awaited-classification-audit.json) records the query change.

Evidence: [binary expressions](../csharp/compatibility/evidence/phase4-binary.json), [awaited types](../csharp/compatibility/evidence/phase4-binary-awaited-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-binary-validation.json).

```powershell
node csharp/tools/checker-program.mjs --binary --record phase4-binary
node csharp/tools/checker-program.mjs --awaited --record phase4-binary-awaited-regression
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-binary-safety
```

## Value references and declaration ordering

`ReferenceSymbols.cs` resolves and caches value identifiers, preserves read/write reference marking, and selects missing-name diagnostics. `ValueUseChecks.cs` adds block-scoped use-before-declaration checks, parameter initializer restrictions, UMD access, type-only alias use and isolated-module import conflicts. `DeclarationOrder.cs` handles binding patterns, immediate/deferred execution, class fields, parameter properties and decorators. Constant evaluation now uses this shared ordering implementation. Static-block initialization flow, missing-prefix suggestions and full diagnostic related-information formatting remain required services.

`ReferenceSyntax.cs` classifies expression contexts, valid type-only alias uses, read/write accesses and assignment targets. Parent traversal is iterative, including the distinct treatment of non-null assertions by assignment and read/write classification.

NativeAOT passes **140 exact source-program comparisons**, with **280 value resolutions**, **48 class-member ordering queries** and **12,092 syntax-classification queries**, at single and reference-default concurrency. The reference mode serializes merged binding declarations without triggering unrelated computed-member evaluation; resolution results, syntax classifications, ordering results, symbol graphs and diagnostic/suggestion codes are compared. Twenty-three native safety assertions cover cancellation, cached failures, checker isolation, read/write marking and **20,000-level** traversal.

The same artifact passes all **2,700** earlier source-program comparisons, **2,904** type/state cases and **669** earlier safety assertions. These tests do not establish complete identifier expression types or control-flow narrowing. Evidence: [references](../csharp/compatibility/evidence/phase4-references.json), [constants](../csharp/compatibility/evidence/phase4-references-constants-regression.json), [aliases](../csharp/compatibility/evidence/phase4-references-aliases-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-references-validation.json).

```powershell
node csharp/tools/checker-program.mjs --references --record phase4-references
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-references-safety
```

## Control-flow evaluation and narrowing

`FlowTypes.*.cs` evaluates binder flow graphs, including assignment reduction, branch and loop joins, shared-node caches, reduced labels and evolving arrays. Stable expression checks isolate temporary loop/cache state. `FlowReachability.cs` supplies reachability, assertion/never-call effects and post-super analysis. `FlowReferences.cs` matches references and constructs loop keys, including qualified `this` references.

`FlowNarrowing.*.cs` implements truthiness, optionality, equality, `typeof`, aliased conditions, type predicates/assertions, discriminant properties, switch clauses and exhaustiveness. `AssignmentMarks.cs` tracks definite/last assignments and exported or nested-function writes. `ExplicitValueTypes.cs` and `FlowEffects.cs` resolve explicit callee types and annotated predicate/never effects. General identifier/access expression checking, constructor/`in`/`instanceof` narrowing, some reference/initializer services and full call resolution remain required dependencies.

NativeAOT passes **212 exact source-program comparisons**, covering **496 flow-type/reachability queries**, **944 assignment-state queries** and loop/shared/reachability cache state at single and reference-default concurrency. Cases include nested loops, `try`/`finally`, aliased guards, nullable/generic constraints, evolving arrays, assertions, predicates, switches and large discriminated unions. Forty-seven native safety assertions cover cache isolation, cancellation/retry, reentrant assignment scans, reduced labels, post-super state, **20,000-node** traversal and the exact **2,000-level** recursive-flow limit.

A generic nullable guard exposed an old primitive-only subtype shortcut in the program test host: its result agreed with the reference, but it skipped resolution of the global `String` members. Program algebra now calls the production relation engine. A separate cancellation test exposed nested assignment-scan markers surviving an interrupted ancestor scan; their ownership and rollback now follow the ancestor scan.

The same artifact passes all **2,840** earlier source-program comparisons, **2,904** type/state cases, **613** constraint cases and **692** earlier safety assertions. These probes exercise the flow components directly; they do not establish complete semantic checking of arbitrary programs. Evidence: [flow](../csharp/compatibility/evidence/phase4-flow.json), [constraints](../csharp/compatibility/evidence/phase4-flow-constraints-regression.json), [structural relations](../csharp/compatibility/evidence/phase4-flow-assignability-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-flow-validation.json).

```powershell
node csharp/tools/checker-program.mjs --flow --record phase4-flow
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-flow-safety
```

## Identifier expressions and assignment checks

`IdentifierTypes.cs` connects value references to flow typing, definite-assignment diagnostics, captured-variable analysis, parameter defaults, automatic types and readonly assignment checks. `ReferenceTypeNarrowing.cs` substitutes generic constraints in constraint/contextual positions and unwraps `NoInfer`. `SymbolNarrowing.cs` supplies the binding/contextual-parameter narrowing algorithms; their binding extraction and contextual-expression services remain required dependencies.

`AliasReferences.cs` tracks retained aliases, including internal import chains and cancellation rollback. `Deprecations.cs` handles declaration/symbol deprecation and uncalled-function references. `MissingNamePrefixes.cs` checks static and instance member suggestions. `AssignmentChecks.cs` validates assignment references and dispatches assignability checks; `RelationDiagnostics.cs` adds call/constructor hint selection and error-elaboration dispatch. Complete structural diagnostic elaboration, property access and CommonJS assignment services remain open.

NativeAOT passes **164 exact source-program comparisons**, including **340 expression queries**, alias-retention state, deprecation suggestions and call/constructor hint codes, at single and reference-default concurrency. Twenty-seven new safety assertions cover parameter-default cancellation, alias-chain rollback, context ownership, captured flow, readonly targets and **20,000-level** reference traversal. Existing expression-state cleanup coverage now injects a finish-check failure directly instead of depending on identifiers being unsupported.

Type queries without instantiation arguments now use the expression checker and regular/widened types. Integration also corrected typed circularity to return the error type and circular-initializer reporting under `noImplicitAny: false` and connected missing-name class-member lookup and assignment error elaboration so their lazy member caches match the reference. The same artifact passes **3,052** earlier source-program comparisons, **2,904** type/state cases, **613** constraint cases and **739** earlier safety assertions. These probes still do not establish complete semantic checking of arbitrary programs.

Evidence: [identifier expressions](../csharp/compatibility/evidence/phase4-identifiers.json), [flow regressions](../csharp/compatibility/evidence/phase4-identifiers-flow-regression.json), [alias regressions](../csharp/compatibility/evidence/phase4-identifiers-aliases-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-identifiers-validation.json).

```powershell
node csharp/tools/checker-program.mjs --identifiers --record phase4-identifiers
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-identifiers-safety
```

## Remaining completion work

The following phase-4 requirements remain open:

1. Complete the program/checker entry point, module interop/type adaptation, computed exports and type/value symbol resolution. Program-backed globals, augmentation merging, declaration headers and alias/export algorithms now exist; their remaining semantic callbacks must be connected.
2. Complete relation diagnostics and remaining type-node dependencies; connect the implemented declaration/type-node, algebra, scope, inference, instantiation and tuple algorithms to complete checker services.
3. Remaining expression forms, call-site inference, context-sensitive expression typing, overload selection, full declaration checking, JavaScript and JSDoc semantics.
4. Complete binding/contextual dependencies of identifier checking and property/declaration flow integration; complete constructor/`in`/`instanceof` narrowing, initialization/reference services and semantic diagnostics.
5. Indexed expression checking, JSX, decorators and grammar checks.
6. Type display, node builders, symbol accessibility and emit-resolver APIs.
7. All active checker/compiler type/symbol/diagnostic comparisons at single and reference-default concurrency; audits of intentional differences; complete semantic workload memory/performance measurements.

The next integration step is property/element access and binding type inference, followed by contextual checking, call-site inference/overload selection, diagnostics and module type adaptation over the program-backed symbol environment. The original Go backend remains the product backend. The full checker completion gate and retained-platform release gates are unchanged.
