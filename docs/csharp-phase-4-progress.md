# Phase 4: checker port in progress

**Phase 4 is incomplete.** Production checker creation, internal queries and an initial source-file semantic traversal now use the implemented type system, scope, instantiation, inference, flow and expression services. This does not satisfy the complete semantic-checker gate in the [rewrite plan](csharp-rewrite-plan.md). Complete semantic coverage and diagnostic formatting, full type/symbol queries and the emit resolver remain unavailable in the C# backend.

**Validation policy, updated 2026-09-24.** At the user's request, all further execution and validation use the normal Release build. NativeAOT publishing is deferred until the final completion check, when it must finish without warnings or errors. NativeAOT binaries are no longer executed for validation. Earlier NativeAOT results below remain historical evidence.

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

`TypeAlgebra.cs`, `TypeAlgebra.Intersections.cs` and `TypeAlgebra.Templates.cs` implement union normalization, literal/subtype reduction, constrained-variable reduction, intersection distribution and disjointness, nullable factoring, aliases/origins, filtering, template construction and intrinsic string mappings. Their required `ITypeAlgebraHost` is now supplied by `Checker`, using its structural relations, member/constraint resolution and diagnostics. Whole-program checking and complete diagnostic attribution remain open.

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

## Property and element access

`AccessExpressions.cs`, `AccessFlow.cs`, `OptionalExpressions.cs` and `IndexedAccessValidation.cs` connect member/index reads and writes to widening, index signatures, optional-chain markers and flow typing. `IndexedTypes.cs` now uses the same property/index algorithms for expression access, including readonly indexes, tuple bounds, generic writes and unchecked-access optionality. `AccessNames.cs` resolves constant keys and destructuring names.

`MemberAccessRules.cs`, `MemberAccessibility.cs` and `PrivateAccess.cs` implement readonly-constructor exceptions, declaration ordering, private/protected access, private-name scope/shadowing and reference tracking. `ClassBases.cs` resolves class bases and inherited/default constructors. `ThisExpressions.cs` handles `this`/`super` types and their initialization restrictions. Contextual object-literal/JavaScript `this`, auto-property initialization, imported emit helpers and some declaration services remain required dependencies.

`ElementAccessErrors.cs` and `SymbolSuggestions.cs` add indexed-access diagnostics and spelling suggestions. `Semantics/SpellingSuggestions.cs` uses generated **Go Unicode 17.0.0** simple lowercase/fold tables, preserving the reference's UTF-8/rune length rules and invalid-byte handling. The program probe also compares deprecation suggestions and queued property-diagnostic counts; full deferred diagnostic reporting and diagnostic text/related information remain open. Exact-optional assignment diagnostics now enumerate target properties as the reference does.

NativeAOT passes **412 exact source-program comparisons**, covering **1,100 access queries**, resolved member symbols, private-member usage and deferred/suggestion state at single and reference-default concurrency. Cases include nullable chains, tuples, generic indexes, readonly writes, accessors, private shadowing, protected inheritance, constructors, `this`/`super`, constant keys, numeric `for-in` indexing and spelling suggestions. Thirty-four native safety assertions cover cancellation/retry, optional markers, base-constructor caches, readonly rules, Unicode spelling and **20,000-level** traversal.

The first private-name comparisons differed only in 32 fields containing allocator IDs across 16 cases. Both probes now replace those IDs with the declaring class's AST node ID **only when serializing names and sorting serialized symbol tables**. Internal names and lookup/type algorithms are unchanged. The [private-name audit](../csharp/compatibility/evidence/phase4-access-private-name-audit.json) records the boundary; shadowing, declarations, cached symbols, references and diagnostics remain compared.

The same artifact passes **3,216** earlier source-program comparisons, **2,904** type/state cases, **613** constraint cases and **766** earlier safety assertions. Evidence: [access](../csharp/compatibility/evidence/phase4-access.json), [identifier regressions](../csharp/compatibility/evidence/phase4-access-identifiers-regression.json), [indexed-type regressions](../csharp/compatibility/evidence/phase4-access-indexing-regression.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-access-validation.json).

```powershell
node csharp/tools/checker-program.mjs --access --record phase4-access
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-access-safety
node csharp/tools/generate-go-casing.mjs --check
```

## Destructuring bindings

`BindingTypes.cs` connects object/array binding extraction to variable and identifier checking, indexed access, initializer flow and correlated narrowing. It handles nested bindings, defaults, object rest, generic `Omit` instantiation, tuple rest slices, array element types and synthetic flow references. Synthetic references preserve the original AST parents and use weak flow associations. `GlobalTypes.cs` resolves and caches required global aliases, including missing/incorrect-arity errors.

Out-of-range tuple defaults exposed an existing widening bug: `VariableTypes.Constant` inspected the binding element instead of its root declaration. Following the root now preserves a default's literal type for `const` bindings. Annotated defaults retain the reference's strict-null-dependent evaluation order.

The Windows x64 NativeAOT artifact passes **88 new binding configurations**, within **252 exact identifier comparisons**. Cases cover nested/defaulted bindings, discriminant correlation, generic/union rest, readonly and accessor properties, class member exclusions, nullable/invalid rest, array/tuple unions, tuple bounds, exact optional properties and unchecked indexed access. The same artifact passes **1,128 affected regression comparisons** across flow, access, initializers, symbol values and indexed types, for **1,380 comparisons total**, plus **199 safety assertions**. Ten additional identifier safety assertions cover binding cancellation/retry, resolution cleanup, readonly spread origins, cached-parent cancellation and foreign-context rejection.

This completes the binding extraction slice, not general destructuring support or expression inference. Implied binding-pattern contextual types, object/array literal checking, computed-name checking, `Symbol.iterator` protocol validation, iteration diagnostic hints and `for-of` initializers still require services. The tests use the explicit minimal array library without `Iterable`; full-library iteration and full-checker performance gates remain open.

Evidence: [identifier and binding comparisons](../csharp/compatibility/evidence/phase4-binding-identifiers.json), [flow](../csharp/compatibility/evidence/phase4-binding-flow.json), [access](../csharp/compatibility/evidence/phase4-binding-access.json), [initializers](../csharp/compatibility/evidence/phase4-binding-initializers.json), [values](../csharp/compatibility/evidence/phase4-binding-values.json), [indexing](../csharp/compatibility/evidence/phase4-binding-indexing.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-binding-validation.json).

```powershell
node csharp/tools/checker-program.mjs --identifiers --filter binding
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-identifiers-safety
```

## Literal inference and contextual types

`BindingPatterns.cs` implements implied object/tuple binding types and parameter-initializer padding. `ExpressionContexts.cs` supplies contextual lookup, cached and explicit context stacks, context-free checks, contextual literal preservation and inference mapper selection. `ContextualProperties.cs` resolves concrete, indexed, intersected and mapped contextual properties; `TypeDiscrimination.cs` selects union constituents from discriminant properties.

`ArrayLiterals.cs`, `ObjectLiterals.cs` and `ObjectSpreads.cs` connect these services to initializer and identifier checking. They cover nested literals, tuple contexts, holes, rest/spread, `const` contexts, computed names, object grammar, fresh-object regularization and optional/generic spread merging. `ExcessProperties.cs` supplies relation decisions for excess properties; full diagnostic elaboration remains separate. `WideningDiagnostics.cs` reports widening errors inside variable initializer types. `LateMembers.cs` resolves computed members/exports, retains original binding symbols and rolls back its tables and declaration links on cancellation.

The computed-name comparisons first exposed matching result types with different resolved symbols. Correct late binding required creating checker services before the probe's declaration queries. This changes composition order, not comparison policy; all earlier source suites were rerun against the resulting artifact.

Windows x64 NativeAOT passes **3,816 exact source-program configurations**, including **100 new literal/context configurations** and **352 identifier configurations overall**. New cases cover nested object/array inference, pattern defaults and padding, annotated tuples, union/intersection contexts, optional discriminants, computed object/interface members, template contexts, `as const`, tuple spreads, invalid spreads and object grammar. Exact-optional and unchecked-index options are compared. The same artifact passes **572 safety assertions**, including **23 new assertions** for context/inference rollback, cancelled late binding, pattern cleanup, cached-name cancellation, type ownership, regular-object caching and **20,000-level** contextual lookup.

These remain component tests. Function/method body inference, contextual function parameters, call/overload integration, `Symbol.iterator` protocols, general assertion/declaration checking, deferred diagnostics and JavaScript/JSX services remain incomplete. Literal tests use the explicit minimal array library; complete semantic corpus and memory/performance gates remain open.

Evidence: [identifier and literal comparisons](../csharp/compatibility/evidence/phase4-literals-identifiers.json), [relation regressions](../csharp/compatibility/evidence/phase4-literals-assignability.json), [member regressions](../csharp/compatibility/evidence/phase4-literals-members.json), [flow regressions](../csharp/compatibility/evidence/phase4-literals-flow.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-literals-validation.json).

```powershell
node csharp/tools/checker-program.mjs --identifiers --filter literal
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-expressions-safety
```

## Function bodies and contextual parameters

`FunctionSyntax.cs`, `FunctionContexts.cs`, `FunctionExpressions.cs` and `FunctionBodies.cs` connect function/arrow/object-method expressions to contextual signature selection, parameter/default/rest assignment, return-expression collection, contextual return widening and inferred type predicates. Contextual checks roll back parameter types, signature metadata, flags and owned expression caches on failure. Return traversal excludes nested functions and uses an explicit stack.

`FunctionThis.cs` connects contextual receivers and `ThisType<T>` to the existing `this` checker. `AwaitExpressions.cs` connects `await` types and diagnostics; async return inference uses the existing awaited-type engine. `FunctionDeclarations.cs`, `FunctionWidening.cs` and `TypeReferenceChecks.cs` add the parameter/type-parameter and annotation checks needed by these expression paths. Broader declaration, modifier, JavaScript, generator and emit-helper services remain required dependencies.

The new tests explicitly request member, value and signature queries in addition to identifier queries, so return types, parameter types and predicates are forced. Function value lookups alone were insufficient to test body inference. The candidate now collects identifier suggestions and assignment hints after the remaining queries, matching the reference's snapshot point. No comparison exceptions were added.

The stronger queries exposed two earlier integration bugs: synthesized binding-pattern members were not sorted using the reference's symbol order, and context-sensitive `this` detection read only syntax flags instead of including binder-owned flags. Anonymous binding/rest/spread/widened properties now use the shared symbol ordering, and function sensitivity reads the binder's immutable side data. Function annotation checks also resolve actual type-argument constraints instead of forcing cache flags in the serializer.

Windows x64 NativeAOT passes **3,912 exact source-program configurations**, including **96 new function configurations**, and **589 safety assertions**. The new function cases cover arrows, methods/accessors, default/rest/destructured parameters, generics and constraints, union/intersection contexts, recursive returns, inferred predicates, implicit returns, nested functions, const returns, contextual `this`, async promises and `await`. Seventeen new safety assertions cover cancellation followed by a different contextual type, generic rollback, predicates, cached checks and **20,000-level** return traversal.

Evidence: [function and identifier comparisons](../csharp/compatibility/evidence/phase4-functions-identifiers.json), [signature regressions](../csharp/compatibility/evidence/phase4-functions-signatures.json), [literal/property regressions](../csharp/compatibility/evidence/phase4-functions-properties.json), [flow regressions](../csharp/compatibility/evidence/phase4-functions-flow.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-functions-validation.json).

```powershell
node csharp/tools/checker-program.mjs --identifiers --filter function
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-signatures-safety
```

## Calls, overloads and generic expression finishing

`GenericExpressions.cs` connects generic function arguments to contextual signatures, including inferred return type parameters and unique parameter names. `CallArguments.cs`, `CallSignatures.cs`, `CallInference.cs`, `CallResolution.*.cs` and `CallExpressions.cs` implement effective tuple-spread arguments, argument/type-argument arity, contextual inference, candidate ordering, the subtype/assignability passes, error-candidate selection and resolved-signature caching. Immediately-invoked parameter contexts and optional-call flow use the same services.

Constructor calls now include private/protected access through `ConstructorAccess.cs`, abstract checks, inherited constructors and `super` instantiation. Tagged templates and global `Symbol` call result types are connected. `BestMatchingTypes.cs` and `LiteralElaboration.cs` add object/array/arrow diagnostic elaboration and union-target selection. Diagnostic comparisons still cover codes and selected state, not complete text, spans or related information.

The new oracle inputs force member/value/signature queries and additionally serialize each resolved call signature and its graph. Candidate-output lists retain the actual instantiated candidates. Cancellation restores resolution markers, published error candidates, inference metadata and contextual callback state. Inference rollback also restores replaced inference records by identity.

A strict object-argument error initially differed only in the Boolean union's lazy classification flag. A temporary trace in the ignored reference checkout located the missing `maybeAddMissingAwaitInfo` call. The implementation now runs the real promised-type checks and records the resulting hint; the reference source was restored byte-for-byte, and serialization flags were not overridden.

Windows x64 NativeAOT passes **4,048 exact source-program configurations**, including **136 new call configurations**, and **611 safety assertions**. Cases cover generic and higher-order calls, contextual returns, immediate invocation, overloads and failures, tuple/readonly spreads, defaults/constraints, optional and nullable calls, recursive calls, constructors/accessibility/`super`, tagged templates, symbols, inferred binding/mapped types, deprecations and property/element-level argument errors. Twenty-two new safety assertions cover argument/callback cancellation, failure-candidate rollback, cached calls, candidate output and inference-record restoration.

Dynamic imports and JavaScript call adaptation, decorators, JSX and `instanceof` dispatch, complete invocation/argument diagnostic details, and some emit-related checks remain required dependencies. The complete semantic pass and corpus/memory/performance gates remain open.

Evidence: [call and identifier comparisons](../csharp/compatibility/evidence/phase4-calls-identifiers.json), [signature regressions](../csharp/compatibility/evidence/phase4-calls-signatures.json), [relation regressions](../csharp/compatibility/evidence/phase4-calls-assignability.json), [inference regressions](../csharp/compatibility/evidence/phase4-calls-inference.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-calls-validation.json).

```powershell
node csharp/tools/checker-program.mjs --identifiers --filter call
& ./built/csharp/phase4-native/TypeScript.Compatibility.exe --checker-signatures-safety
```

## Iterator and generator protocols

`IteratorProtocols`, `IterationElements`, `GeneratorTypes` and `YieldExpressions` implement synchronous/asynchronous iterator lookup, standard-library fast paths, structural `next`/`return`/`throw` analysis, async-from-sync behavior, iteration element checks, generator yield/return/next inference, contextual generator types and yield-expression checks. Spread, destructuring, contextual array elements and `for…of` now share these services. The superseded array-only binding helper was removed. Iteration-variable inference also handles generic `for…in` keys.

Windows x64 NativeAOT passes **4,116 exact source-program configurations**, including **68 new iteration configurations**, and **629 safety assertions**. The new cases cover structural and builtin iterators, optional/invalid `next`, return/throw unions, synchronous/asynchronous iteration, promise elements, destructured loop variables, generator contexts, `yield` and `yield*`. Eighteen new assertions cover iteration results, negative-cache diagnostic retries, cancellation/recovery, ownership and a 20,000-level yield traversal. These are component checks, not the complete semantic corpus gate.

The comparison serializer replaces the allocator suffix in unique-symbol property names with the defining declaration's AST identity. It preserves names and distinct declarations, reads existing symbol/type state, and does not change checker keys or force type resolution. This makes `Symbol.iterator` and `Symbol.asyncIterator` graphs comparable across processes.

Evidence: [identifier and iteration comparisons](../csharp/compatibility/evidence/phase4-iteration-identifiers.json), [signature regressions](../csharp/compatibility/evidence/phase4-iteration-signatures.json), [flow regressions](../csharp/compatibility/evidence/phase4-iteration-flow.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-iteration-validation.json). Deferred iterator diagnostic formatting and related information, generator emit helpers, remaining declaration/body checks and complete checker integration remain open.

## Assertions, explicit instantiation and value expressions

`TypeAssertions` implements ordinary and const assertions, deferred overlap diagnostics and `satisfies`. `InstantiationExpressions` specializes generic call/construct signatures, filters overloads by type-argument arity, checks constraints, preserves object members and handles unions/intersections and constrained type variables. Results are cached by expression and source type; a cancelled diagnostic rolls back the result published by that request. Type queries with explicit arguments share this implementation.

`ValueExpressionChecks` implements deletion requirements, `new.target`, `import.meta` module restrictions and const-enum access checks. Regular-expression values use the global `RegExp` type. The initial `TypeDisplay` service renders structural instantiation diagnostics, including generic parameter constraints/defaults, signatures, tuples and literal escaping. Complete accessibility-aware type display, all type forms and declaration node building remain open.

Windows x64 NativeAOT passes **4,218 exact source-program configurations**, including **102 new configurations**, and **643 safety assertions**. New cases include overload/union/intersection instantiation, constructors, constraints/defaults, optional/rest parameters, assertion grammar and deferred errors, const assertions, `satisfies`, delete/readonly/optional checks, metadata module modes, const enums and Unicode diagnostic text. Fourteen new safety assertions exercise cache identity, diagnostic cancellation/recovery, cross-context rejection and deferred assertion publication.

The probe now compares instantiation-expression source nodes and the rendered type argument of diagnostic 2635, and explicitly runs deferred assertion checks for the new expression cases. The renderer performs the real constraint/default queries required by that diagnostic; no lazy state is forced by the serializer. Full diagnostic text, range and related-information comparison across the checker remains a separate unfinished gate.

Evidence: [expression and identifier comparisons](../csharp/compatibility/evidence/phase4-ordinary-identifiers.json), [signature regressions](../csharp/compatibility/evidence/phase4-ordinary-signatures.json), [property regressions](../csharp/compatibility/evidence/phase4-ordinary-properties.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-ordinary-validation.json).

## Production checker composition and queries

`CompilerProgram.CreateCheckerAsync` now creates an exclusive production `Checker`, with its own context, links, symbols and semantic caches. Internal expression and type-node queries validate syntax ownership and serialize access to each checker; independent checkers share immutable program syntax and binding. Cancellation during creation does not publish a checker, and a cancelled queued query does not disturb the active request.

Twenty-four composition files moved from the compatibility project into the compiler assembly as `Checker` and `CheckerEnvironment` partials. Production composition no longer references `AlgebraFixtureHost`, `InstantiationFixtureHost` or `ConstraintFixtureHost`. `InstantiationServices` connects the actual mapped, tuple, constraint, inference, index and relation implementations. Algebra now uses full generic classification, template matching and class derivation. Primitive fixture hosts remain confined to isolated component tests.

The source-program differential harness now creates the production checker and uses its query methods. Windows x64 NativeAOT passes **4,254 exact source-program configurations**, including **36 new integration configurations**, and **656 safety assertions**. New cases cover optional mapped properties, template reduction, generic/array-like tuples, derived unions, captured type queries, and type-literal index/duplicate checks. Thirteen additional safety assertions cover independent ownership, rejected syntax, queued cancellation/recovery and an `Array.map` query using the actual bundled ES5 declarations; that query returns `number[]` without diagnostics.

`IndexDeclarationChecks` adds index compatibility and duplicate checks while checking type-literal annotations. Complete declaration/body checking and the whole-program semantic pass remain unfinished; the new factory is not a complete compiler-check operation, and the product backend remains Go.

Evidence: [production query comparisons](../csharp/compatibility/evidence/phase4-composition-identifiers.json), [constraint/relation regressions](../csharp/compatibility/evidence/phase4-composition-generic-relations.json), [property regressions](../csharp/compatibility/evidence/phase4-composition-properties.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-composition-validation.json).

## Source traversal and deferred checking

`Checker.CheckSourceFileAsync` and `CheckProgramAsync` now drive source-element checking for variables, function declarations and deferred function bodies, returns, type aliases, interfaces, branches, loops, switches, labels, jumps and exception statements. Return-path analysis handles annotated, inferred and `never` returns; source traversal performs unreachable-code checks and preserves the function flow-analysis boundary. Interface checks include inherited-property identity, base compatibility, merged type-parameter lists and generic heritage constraints. Variable validation now distinguishes parameters from ordinary declarations and checks secondary declaration types.

Deferred nodes retain insertion order, including nodes discovered while checking deferred bodies. Completion is recorded only after deferred checks finish. Completed source files are not checked twice. Cancellation during source checking invalidates that checker, matching the reference's cancellation lifetime; a fresh checker can retry the immutable program. Unsupported source forms and incomplete finalization services still fail explicitly.

The new `--semantic` probe invokes the reference's `GetDiagnostics` for the complete main source file. Windows x64 NativeAOT passes **108 exact semantic diagnostic-code configurations**, alongside **4,254 exact query configurations** and **664 safety assertions**. Semantic cases cover initializer/assignment failures, deferred errors, return paths, narrowing through statements, destructuring, `for…in`/`for…of`/`for await`, switch and jump errors, unreachable code, interfaces and declaration merging. Eight new safety assertions cover completion caching, deferred-body checking, cancellation invalidation/recovery and a 20,000-level statement traversal.

This new probe compares diagnostic codes, not complete diagnostic text, ranges or related information. Module source dispatch, export/unused checks, some deferred diagnostics and other semantic forms remain unfinished. Passing these cases does not establish the complete corpus or memory/performance gate.

Evidence: [source-file semantic comparisons](../csharp/compatibility/evidence/phase4-source-semantic.json), [query regressions](../csharp/compatibility/evidence/phase4-source-identifiers.json), [signature regressions](../csharp/compatibility/evidence/phase4-source-signatures.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-source-validation.json).

## Class checking and property initialization

Class declarations and expressions now participate in source checking. The class path checks instance/static base compatibility, implemented types, generic base arguments, abstract-member fulfillment, member-kind overrides, override annotations, duplicate members, constructor `super` requirements, accessors and property grammar. Function/method/constructor overload checks now report duplicate or missing implementations, inconsistent declaration flags and incompatible implementation signatures. Index signatures have a dedicated declaration path rather than being cast to a function-only AST interface.

`PropertyInitialization` uses the existing bound constructor/static-block return flow for property inference and definite-assignment checks. Synthetic references preserve the original syntax parents. Private-name flow resolution, reads of inferred properties and static-block initialization-before-use are connected to the same services.

Windows x64 NativeAOT passes **238 semantic diagnostic-code configurations**, **4,274 query configurations**, and **674 safety assertions**. This adds 130 semantic and 20 query configurations for classes, constructors, accessors, private/readonly fields, abstract members, overloads, override/class-field options and inferred instance/static properties. Ten additional safety assertions cover constructor inference cancellation/recovery, private/static inferred types, definite assignment and immutable syntax parents.

The pinned Go reference asserts in `getOptionalType` for a non-strict static-block initialization-before-use case. Its source and assertion are unchanged. The strict variant is compared; the failing non-strict reproduction is retained in [reference crash evidence](../csharp/compatibility/evidence/phase4-class-reference-static-block-crash.json) and is not counted as a passing comparison. The candidate retains the strict-null precondition on definite property assignment.

Evidence: [class semantic comparisons](../csharp/compatibility/evidence/phase4-class-semantic.json), [property inference and query comparisons](../csharp/compatibility/evidence/phase4-class-identifiers.json), [signature regressions](../csharp/compatibility/evidence/phase4-class-signatures.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-class-validation.json). Decorators, JavaScript-specific class behavior, remaining grammar/emit hooks and complete diagnostic attribution remain open.

## Module checking and diagnostic file attribution

Source traversal now checks imports, import-equals declarations, exports, export assignments and namespace bodies. Module checks cover declaration context, merged export spaces, duplicate exports, type-only aliases, isolated/verbatim module restrictions and export-assignment value/type rules. Module type adaptation handles synthetic defaults, callable namespace imports, export-assignment members and combined value/type symbols. Emit-format decisions distinguish package metadata and file extensions from module-resolution modes.

Diagnostic codes now retain their defining source file. An error discovered while resolving an imported initializer remains attached to the imported file; checking that file later does not duplicate the error. This is file attribution for the existing code-only diagnostics, not complete diagnostic text, ranges or related information.

Windows x64 NativeAOT passes **422 semantic diagnostic-code configurations**, **4,294 query configurations**, and **680 safety assertions**. The added coverage comprises 184 semantic configurations, 20 imported-type graph configurations and six cross-file diagnostic/completion assertions. Cases include merged exports, missing imports, export assignments, synthetic defaults, callable namespace imports, type-only exports and package-format combinations under CommonJS, ESNext, Node16, NodeNext and preserve modes.

Evidence: [module semantic comparisons](../csharp/compatibility/evidence/phase4-module-semantic.json), [imported type and query comparisons](../csharp/compatibility/evidence/phase4-module-identifiers.json), [alias regressions](../csharp/compatibility/evidence/phase4-module-aliases.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-module-validation.json). Import attributes, remaining augmentation/CommonJS/JSON edge cases and complete diagnostics remain open.

## Unused declarations, enums and deferred property diagnostics

Source-file finalization now checks unused locals, parameters, imports, private members and type parameters, including grouped imports/variables/destructuring, underscore exemptions, object-rest exclusions, merged type parameters and syntax/ambient suppression. Reference tracking distinguishes a read from a write. Unused items become errors under the corresponding compiler options and suggestions otherwise. Renamed bindings in signatures receive their dedicated diagnostic independently of those options.

Enum declarations now check merged export spaces, const consistency, initial numbering across merged declarations, private names, erasable syntax and verbatim exports. Their members use the existing constant evaluator and computed-initializer checks. Function-type signatures now run declaration checks, and bodyless functions/methods report missing return annotations.

Missing-property diagnostics are finalized once per source node, with spelling/accessibility, static-member, promise, library-version and empty-DOM-type decisions. Library feature names are generated from the reference's constant table with a source hash and freshness check. Deferred property errors discovered in imported initializers remain queued for their defining file. Complete diagnostic messages, spans and related information remain unfinished.

Windows x64 NativeAOT passes **678 semantic diagnostic-code configurations**, **4,294 query configurations**, and **686 safety assertions**. This adds 256 semantic configurations and six cross-file finalization assertions. The latter check deferred-file ownership, completion caching, independent checkers and duplicate prevention. Existing deep-input and cancellation assertions remain enabled.

Evidence: [semantic finalization comparisons](../csharp/compatibility/evidence/phase4-finalization-semantic.json), [query regressions](../csharp/compatibility/evidence/phase4-finalization-identifiers.json), [signature regressions](../csharp/compatibility/evidence/phase4-finalization-signatures.json), and [native/repository validation](../csharp/compatibility/evidence/phase4-finalization-validation.json).

## Original compiler corpus baseline

`checker-corpus.mjs` now exports programs through the pinned reference's compiler-test runner, retaining its option variations, roots, symlinks, library defaults and skip rules. Input content is stored in hashed blobs. An inventory and configuration plan detect missing exports. The Release candidate replays complete programs with embedded libraries and records exceptions or process failures without counting them as matches. Reference diagnostics retain positions, arguments, chains and related information; candidate comparison currently covers ordered source graphs/content hashes and diagnostic codes.

Both reference modes cover **12,730 test files**, expanded into **15,208 configurations**. The original runner skips **1,762**, leaving **13,446 active configurations** per mode. No reference files or configurations are missing, and the reference reports no failures. Each Release run records **9,365 graph/code matches**, **3,015 candidate failures**, and **1,066 completed diagnostic mismatches**. There are **20 graph mismatches**, overlapping those failure categories; 15 are explicit unsupported content-mapper adapter cases. Candidate results are identical across modes apart from exception stacks. Fresh and reused syntax also produce identical results on the 151-configuration unused-declaration sample; every replay creates a fresh checker.

These counts are a baseline, not a completed gate. Full diagnostic, type and symbol comparison, the content-mapper replay adapter, parallel candidate checker scheduling and the memory/performance gates remain open. The largest stop groups are JSDoc signatures, JSX expressions, decorators, JavaScript classes, disposable declarations and callable/awaitable condition analysis. The reports retain all failure groups and strict mismatches.

The corpus exposed nontermination in `constAssertionInLoop.ts`: the assertion checker checked the `const` marker as an ordinary annotation, re-entered its operand and reset active flow-loop state. The port now follows the reference's early return for const assertions. The original case passes in Release under both reference modes. A bounded regression guard uses an independent thread because timer cancellation did not reliably interrupt the pre-fix runaway. The same work connects explicit `this` types for flow queries, handles binder-owned UMD namespace declarations, includes bind diagnostics in program-level code reporting, and applies diagnostic directives and `noCheck` policy.

The normal Release component suite passes **4,294 query configurations**, **678 semantic diagnostic-code configurations**, and **694 safety assertions**. NativeAOT execution was stopped when the user changed validation policy; its partial corpus run is not a completed result. NativeAOT publishing is deferred until final completion.

Evidence: [single-threaded corpus](../csharp/compatibility/evidence/phase4-corpus-release-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-corpus-release-default.json), [loop reproduction](../csharp/compatibility/evidence/phase4-corpus-const-loop-single.json), and [Release/repository validation](../csharp/compatibility/evidence/phase4-corpus-validation.json).

## JSDoc signatures and JavaScript diagnostic policy

Function `@type` signatures now supply parameter, return and generic types through the existing signature services. Source and expression checks validate signature arity. Unmatched `@param` checks follow documentation ownership, binding-pattern positions and `arguments` references, including inherited documentation behind a tagless comment. JSDoc primitive names and `Object<K,V>` use the reference's type interpretation, and constraint checking uses already resolved symbols rather than resolving JSDoc primitive names again.

The JavaScript paths now distinguish ordinary calls from CommonJS `require`, classify exported-property assignments and constructor `this` properties, and apply the reference's unchecked-JavaScript suggestion rules. Checked JavaScript includes JSDoc parse diagnostics; plain JavaScript uses a generated, source-hashed diagnostic allowlist. Mixed JavaScript/TypeScript overload checking preserves the reference's local-symbol check policy.

Targeted Release comparisons cover **1,453 affected and regression configurations per mode**. Graph/code matches increased from **221 to 561**, recovering **340 configurations**, with **no previously matching case regressing**. Earlier valid case results were retained; only the subset affected by the final documentation-parent correction was rechecked. Source graphs, diagnostic codes and failure classifications agree between modes. One remaining exception differs only in a generated symbol number and remains a failure in both records.

The affected type-node and signature suites pass **408 query configurations**. Signature safety passes **105 assertions**, including nine new documentation checks; the existing **66 program assertions** also pass. Unchanged validation results are reused. NativeAOT verification remains deferred until final completion.

Evidence: [single-threaded affected corpus](../csharp/compatibility/evidence/phase4-jsdoc-corpus-single.json), [reference-default affected corpus](../csharp/compatibility/evidence/phase4-jsdoc-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-jsdoc-mode-parity.json), and [Release/repository validation](../csharp/compatibility/evidence/phase4-jsdoc-validation.json). JavaScript classes and expandos, import types, JSX and other remaining callbacks still block cases; the full phase-4 gate remains incomplete.

## JavaScript classes and assignment-declared properties

JavaScript classes now use the class declaration checks, including JSDoc `@extends` validation. Assignment-declared properties infer types from annotations, constructor flow, method assignments, inherited properties and `Object.defineProperty` descriptors. CommonJS export assignments retain literal types and ignore an initial `undefined` export when later assignments provide its type. JSON module values use the parsed JSON expression; destructured `require` bindings resolve module members.

Assignment declarations now receive contextual types only where the reference permits them, avoiding circular reads of the property being inferred. Class-instance expando classification, property-descriptor readonly checks and duplicate CommonJS export exceptions use the corresponding reference rules. Constructor inference continues to use synthetic references without changing original syntax parents.

Release comparisons cover **6,210 affected and regression configurations per mode**. Graph/code matches increased from **3,919 to 4,362**, recovering **443 configurations**, with no regressions. Failure classifications, graphs and diagnostic codes agree across both modes. The focused value/access query suites pass **548 configurations**, and access/class safety passes **53 assertions**, including nine new JavaScript property checks.

Evidence: [single-threaded affected corpus](../csharp/compatibility/evidence/phase4-expando-corpus-single.json), [reference-default affected corpus](../csharp/compatibility/evidence/phase4-expando-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-expando-mode-parity.json), and [validation and reused results](../csharp/compatibility/evidence/phase4-expando-validation.json). Unchanged Go/tooling validation and unaffected component results are reused. NativeAOT verification remains deferred until final completion; import types, condition analysis, remaining diagnostics and other phase-4 services are still open.

## Conditions and relational operators

Known-truthy callable, promise and enum conditions now use the reference's branch-usage rules, including receiver identity, logical chains and parenthesized assertion exemptions. The `in` and `instanceof` operators check operand types and support private names. Custom `Symbol.hasInstance` methods use overload resolution, argument and return-type diagnostics, and predicate narrowing. Flow analysis now handles property presence, private-name guards, constructor instances and constant references through readonly properties or binding patterns.

Targeted Release corpus validation covers **3,446 affected configurations per reference mode**. Graph/code matches increased from **2,349 to 2,613**, recovering **264 configurations**, with **no previously matching case regressing**. The focused condition/operator test also agrees with the saved Go oracle. Unchanged corpus and repository validation results are reused, and NativeAOT verification remains deferred until final completion.

Evidence: [single-threaded affected corpus](../csharp/compatibility/evidence/phase4-condition-corpus-single.json), [reference-default affected corpus](../csharp/compatibility/evidence/phase4-condition-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-condition-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-condition-validation.json). Complete diagnostic fidelity, remaining semantic services and the final workload gates remain open.

## Destructuring assignments

Object and array assignment patterns now check nested targets, defaults, tuple bounds, object/array rest elements and `for...of` targets. Assigned flow types reuse the binding projection and synthetic-access services. Binding declarations also check rest placement, trailing commas, rest initializers and private property names. Tuple-index diagnostics report on the original target so rechecking a synthetic access does not duplicate an error.

Targeted Release corpus validation covers **4,408 affected configurations per reference mode**. Graph/code matches increased from **3,185 to 3,311**, recovering **126 configurations**, with **no previously matching case regressing**. The affected suites pass **588 query configurations**, **678 semantic diagnostic-code configurations**, and **102 safety assertions**. The new destructuring fixture agrees with the Go oracle and checks source-parent preservation and cancellation. Unchanged validation results are reused; NativeAOT verification remains deferred until final completion.

Evidence: [single-threaded affected corpus](../csharp/compatibility/evidence/phase4-destructuring-corpus-single.json), [reference-default affected corpus](../csharp/compatibility/evidence/phase4-destructuring-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-destructuring-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-destructuring-validation.json). External emit-helper checks and full diagnostic fidelity remain open alongside the other phase-4 requirements.

## Disposable declarations and imported helpers

`using` and `await using` initializers now use the global disposal interfaces, including null/undefined acceptance and async-or-sync disposal. Declaration checks cover binding patterns, required initializers, modifiers, ambient contexts, switch clauses, invalid block placement and await context. Imported-helper lookup resolves `tslib`, checks each requested helper once per source file, and validates private-field helper arity. Disposable declarations, private-field access, object rest, async functions, async generators and async iteration now use that service.

Targeted Release corpus validation covers **1,801 affected configurations per reference mode**. Graph/code matches increased from **1,069 to 1,182**, recovering **113 configurations**, with **no previously matching case regressing**. The affected suites pass **628 query configurations**, **678 semantic diagnostic-code configurations**, and **70 program safety assertions**, including four new disposal/helper checks. The disposal fixture agrees with the Go oracle. Formatting-equivalent results and unchanged repository validations are reused. NativeAOT verification remains deferred until final completion.

Evidence: [single-threaded affected corpus](../csharp/compatibility/evidence/phase4-disposable-corpus-single.json), [reference-default affected corpus](../csharp/compatibility/evidence/phase4-disposable-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-disposable-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-disposable-validation.json). Decorators, complete diagnostic fidelity, remaining semantic/emit integration and the final workload gates remain open.

## Remaining completion work

The following phase-4 requirements remain open:

1. Complete semantic-pass coverage and finalization, remaining module adaptation and import-attribute checks, computed exports and remaining type/value symbol resolution. Checker creation, source traversal, program-backed queries, globals, augmentation merging, declaration headers and alias/export algorithms now exist; their remaining semantic callbacks must be connected.
2. Complete relation diagnostics and remaining type-node dependencies; connect the implemented declaration/type-node, algebra, scope, inference, instantiation and tuple algorithms to complete checker services.
3. Remaining expression forms and special call forms, full declaration checking, JavaScript and JSDoc semantics, and completion of contextual/inference integration across those services.
4. Complete property/declaration flow integration, constructor-identity narrowing, initialization/reference services, and iterator/generator diagnostic and emit integration.
5. Remaining indexed/member diagnostic and declaration services, JSX, decorators and grammar checks.
6. Type display, node builders, symbol accessibility and emit-resolver APIs.
7. All active checker/compiler type/symbol/diagnostic comparisons at single and reference-default concurrency; audits of intentional differences; complete semantic workload memory/performance measurements.

The next integration work is semantic finalization, remaining expression/declaration services, complete diagnostics and the remaining module dependencies. Production source traversal does not yet supply complete program checking. Component comparison counts and validation of the existing Go backend do not measure full C# checker completion. The original Go backend remains the product backend. The full checker completion gate and retained-platform release gates are unchanged.
