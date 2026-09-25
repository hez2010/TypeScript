# Phase 4: checker port in progress

**Phase 4 is incomplete.** Production checker creation, internal queries and an initial source-file semantic traversal now use the implemented type system, scope, instantiation, inference, flow and expression services. This does not satisfy the complete semantic-checker gate in the [rewrite plan](csharp-rewrite-plan.md). Complete semantic coverage and diagnostic formatting, full type/symbol queries and the emit resolver remain unavailable in the C# backend.

## Current overall status

The complete Release import baseline plus subsequent affected-case results record **12,757 matches out of 13,446 active compiler configurations**, up from 12,755 before overload/literal diagnostic fixes and 9,365 at the original Release baseline. **All 13,446 configurations now finish execution and match source graphs.** **689 configurations still have diagnostic-code differences.** One exported input was corrected to reproduce the reference harness's duplicate-filename overwrite order; the reference's expected graph and diagnostics were unchanged.

These comparisons cover source graphs and diagnostic codes. **94.9% matching on this measure is not a Phase-4 completion percentage.** The remaining gates are:

| Completion requirement | Current status |
| --- | --- |
| Execute active corpus and match source graphs | Complete: 13,446 configurations |
| Match semantic diagnostic codes | 12,757 match; 689 differ |
| Match diagnostic text, locations and related information | Incomplete: detailed records match in 2,054 of 3,430 selected configurations, including 1,383 with nonempty semantic diagnostics; the remaining corpus has not been compared at this level |
| Complete type/symbol comparisons, type display, node builders, accessibility and emit-resolver APIs | Incomplete; query families, visibility and complete formatted accessibility results have fixture comparisons; general symbol-format modes, type display/node builders and emit-resolver coverage remain open |
| Validate actual parallel checker scheduling | Incomplete; reference-mode corpus agreement is a narrower check |
| Meet complete semantic workload memory/performance budgets | Incomplete |
| Verify warning-free NativeAOT publishing | Deferred until final completion; no native execution |

Phase 4 is not nearly complete, and the evidence does not yet support a reliable completion estimate. Completed results are retained: each implementation checkpoint replays affected inputs against cached oracle results. A failed check is repeated after a relevant fix; unchanged checks are reused.

**Current implementation focus:** finish general symbol-format modes, type display/node building and the remaining emit-resolver APIs. Diagnostic symbol names now use the program-backed module-specifier generator, and formatted accessibility results have complete fixture comparisons. This closes the previously missing accessibility error-name path, but does not close the entire formatting/API gate or reduce the 689 semantic corpus differences. Remaining semantic and detailed diagnostic differences still need work; parallel scheduling and performance/memory gates follow. Updates should identify which requirement changed, rather than treating component test counts as a completion estimate.

The next implementation priorities are missing diagnostic arguments and relation chains, remaining declaration/expression checks, and the unfinished checker APIs. The sections below are historical implementation checkpoints; their individual passing counts do not represent whole-phase completion.

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

## Standard and legacy decorators

Decorator calls now use synthetic signatures for classes, methods, accessors, fields and legacy parameters. Standard decorator contexts retain member names and private/static flags; legacy calls use property keys and typed descriptors. These signatures participate in contextual typing, generic inference, overload resolution and return-type validation. Standard decorators preserve property-call receivers. Legacy metadata retains value imports and checks isolated-module restrictions. Grammar, helper requests, invalid targets and deferred calls are also connected.

Targeted Release corpus validation covers **566 affected configurations per reference mode**. Graph/code matches increased from **48 to 522**, recovering **474 configurations**, with **no previously matching case regressing**. Only 13 cases affected by the final recovery-node and illegal-target corrections were rerun. The existing call suite passes **180 query configurations**. Signature safety passes **112 assertions**, including seven new checks for standard/legacy calls, metadata imports and source-parent preservation; the three new diagnostic fixtures agree with the Go oracle. Unchanged validation results are reused, and NativeAOT verification remains deferred until final completion.

Evidence: [single-threaded affected corpus](../csharp/compatibility/evidence/phase4-decorator-corpus-single.json), [reference-default affected corpus](../csharp/compatibility/evidence/phase4-decorator-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-decorator-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-decorator-validation.json). Remaining diagnostic/grammar differences, JSX, import services, full semantic fidelity and the final workload gates remain open.

## Import expressions, types and attributes

Dynamic imports now check specifiers/options and produce promise types, including contextual argument types and missing-constructor diagnostics. Import types resolve namespace paths and generic type/value instantiations. Attribute-aware ambient-module selection uses assignability, specificity and pattern precedence. Import/export attributes, resolution-mode overrides, TypeScript extensions, JSON imports and CommonJS/ESM restrictions have corresponding checks. Production checker services are available during symbol initialization so module merging can resolve semantic attribute and wrapper types.

Because initialization is shared by every program, this batch replayed **all 13,446 active compiler configurations in both reference modes**, reusing the saved reference. The final correction reran only **253 import configurations**. The combined result is **11,464 graph/code matches**, **339 newly matching configurations**, and **no regressions**. The alias and type-node suites pass **316 query configurations**. Program safety passes **74 assertions**, including four new import checks; its diagnostic fixture agrees with the Go oracle. Unchanged validations were retained, and NativeAOT verification remains deferred until final completion.

Evidence: [full single-threaded corpus and final correction](../csharp/compatibility/evidence/phase4-import-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-import-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-import-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-import-validation.json). The overall status above includes all retained failures and unfinished phase-4 gates.

## JSX integration

JSX now resolves global, factory-local and automatic-runtime namespaces; intrinsic elements; function/class component signatures; managed props; contextual attributes and children; spreads; and generic inference. Checking includes fragment factories, element bounds, grammar, child arity/type errors and excess attributes. The shared export lookup now uses the existing late-member resolver. Isolated factory-name parsing reuses the parser's entity-name grammar.

Targeted Release validation covers **504 JSX/export configurations per reference mode**. Graph/code matches increased from **16 to 447**, recovering **431 configurations**, with **no regressions**. Alias/call queries pass **304 configurations**, and expression safety passes **73 assertions**, including nine new JSX checks. Two focused diagnostic fixtures agree with a pinned Go probe adapted only to select the `.tsx` input file.

The regression run exposed nontermination in `discriminatedUnionJsxElement.tsx`. A captured stack showed generic discriminant checking re-entering contextual discrimination because JSX inner-expression lookup bypassed an active context override. Routing that lookup through the shared context service fixes the cycle. The original case and an added generic safety fixture pass. The interrupted run's 15 completed non-JSX results and isolated-case result were retained; the remaining JSX cases were rechecked after the fix. No arbitrary recursion cutoff was added.

Evidence: [single-mode affected corpus](../csharp/compatibility/evidence/phase4-jsx-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-jsx-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-jsx-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-jsx-validation.json). JSX diagnostic edge cases remain in the retained failures. Full semantic fidelity and the final workload gates remain open; NativeAOT verification stays deferred until final completion.

## Late computed indexes and property names

Computed class members and assignments now produce string, number and symbol index signatures through the existing late-binding and index-building services. Index values include the appropriate sibling property types and retain readonly flags. Property-name queries resolve computed and late-bound symbols before extracting literal keys. Class grammar accepts entity-name expressions that can be bound later, dynamic class members are checked against explicit indexes, and type-literal index signatures receive their grammar checks.

Targeted Release validation covers **4,117 affected and regression configurations per reference mode**. Graph/code matches increased from **3,547 to 3,658**, recovering **111 configurations**, with **no regressions**. Index/member queries pass **496 configurations**, and index safety passes **31 assertions**, including seven new checks covering key domains, sibling aggregation, readonly indexes, cancellation rollback, grammar acceptance and source-parent preservation. Unchanged results are retained; NativeAOT verification remains deferred until final completion.

Evidence: [single-mode affected corpus](../csharp/compatibility/evidence/phase4-late-index-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-late-index-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-late-index-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-late-index-validation.json). Remaining differences stay in the overall counts above.

## Merge diagnostics and declaration names

Conflicting declarations now report duplicate, block-scoped, enum and namespace-augmentation diagnostics on the appropriate declarations. Related declaration locations are retained with deduplication and the reference's five-location limit. Plain JavaScript suppression applies separately to each side of a conflict. Full diagnostic formatting remains open.

Declaration checks now distinguish module-level emitted names from valid parameters, members, ambient declarations and type-only imports. Function declarations and destructured bindings receive the same checks. Private-field and static-super helper collisions are finalized after source analysis; code-generation-only errors respect `noEmit`.

Release validation covers **1,835 configurations in each reference mode**, increasing graph/code matches from **1,548 to 1,638**, with **90 recovered configurations and no regressions**. The initial single-mode run's 74 unaffected results were retained; 1,761 name-sensitive configurations were replayed after the follow-up fixes. Program/checker safety passes **80 assertions**, including six new checks for file attribution, related declarations, JavaScript suppression, runtime-name exclusions, deferred helper collisions and `noEmit`. The build has zero warnings and errors. Unchanged reference, query and repository validation results were reused; NativeAOT remains deferred until phase 4 is complete.

Evidence: [single-mode corpus](../csharp/compatibility/evidence/phase4-declaration-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-declaration-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-declaration-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-declaration-validation.json).

## Statements, template contexts and variance annotations

`with` statements now check their expression and report the reference's ambient, async and unsupported-statement diagnostics. Their bodies retain the reference's unchecked semantics. Ordinary template substitutions have no contextual type; tagged substitutions continue to use the call's parameter type. Computed enum initializers use expression checking, including property accesses whose receiver is not a namespace name.

Type parameters now validate `const`, `in` and `out` placement, duplicates and ordering. Deferred variance checks merge declaration annotations, reject unsupported alias forms, and compare separately instantiated sub/super marker types through the existing structural relation machinery. The active annotation parameter is restored after checking. Misplaced class-member variance modifiers receive their specific grammar diagnostic.

Release validation covers **1,501 configurations in each reference mode**, increasing graph/code matches from **1,259 to 1,328**, with **69 recovered configurations and no regressions**. Forty-seven unaffected single-mode results were retained from the initial run. Both modes agree on all classifications, source graphs and diagnostic codes. Expression/signature queries pass **256 configurations**; the corresponding safety suites pass **190 assertions**, including five new assertions backed by two pinned-Go fixtures. The Release build has zero warnings and errors. Unchanged reference and repository checks were reused; NativeAOT remains deferred until final phase-4 completion.

Evidence: [single-mode corpus](../csharp/compatibility/evidence/phase4-grammar-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-grammar-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-grammar-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-grammar-validation.json).

## Enum relations, heritage errors and shared modifiers

Enum relations now compare enum identity, names, regular/const kind, member presence and values. Opaque numeric values remain compatible with numeric values, while differing known values and string/unknown-numeric combinations are rejected. Comparisons are directional, cached by symbol pair and cancellation-aware. Enum-member symbols resolve through their parent enums.

Heritage diagnostics recognize attempts to extend interfaces through both missing-property and failed-name resolution. Declaration, class-member, function-signature, import and export grammar share modifier validation for placement, ordering, duplicates and invalid combinations. Function and property grammar stop after a modifier error instead of producing cascading errors.

Release validation covers **8,617 configurations in each reference mode**, increasing graph/code matches from **7,701 to 7,830**, with **129 recovered configurations and no regressions**. Two unaffected enum-only single-mode results were retained from the initial run. Access/assignability queries pass **744 configurations**; relation/access safety passes **102 assertions**, including 13 new checks. The declaration/heritage fixture matches the pinned Go checker. The Release build has zero warnings and errors; formatting preserved tokens, comments and syntax in all 14 changed C# files. Reference and unchanged repository results were reused. NativeAOT remains deferred until final phase-4 completion.

Evidence: [single-mode corpus](../csharp/compatibility/evidence/phase4-relations-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-relations-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-relations-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-enum-modifiers-validation.json).

## Unresolved names and spelling suggestions

Failed name resolution now distinguishes type, value and namespace misuse, recognizes primitive exports and heritage names, and reports library hints before considering spelling suggestions. Suggestions use the ordinary lexical resolver with a custom lookup, preserving scope restrictions and alias handling. Global primitive type suggestions are included where their corresponding built-in types exist. Unchecked JavaScript suggestions retain their separate diagnostic category, and suggested declarations are retained for related information. Library-name hints are generated from the pinned feature map alongside the existing property-library hints.

Release validation covers **1,000 affected configurations per reference mode**, increasing graph/code matches from **607 to 770**, with **163 recovered configurations and no regressions**. After two corrections to generic `const` references and mixed type/value lookups, only 48 single-mode cases were replayed; 952 unaffected results were retained. Both reference modes agree. Identifier queries pass **830 configurations**, and identifier safety passes **39 assertions**, including two new checks against a pinned-Go diagnostic fixture. Query and safety records identify the build they exercised and the targeted follow-up validation. The final Release build has zero warnings and errors. Generator regeneration, freshness and formatting pass; unchanged Go/repository validation is reused. NativeAOT remains deferred until final phase-4 completion.

Evidence: [single-mode corpus](../csharp/compatibility/evidence/phase4-name-errors-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-name-errors-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-name-errors-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-name-errors-validation.json).

## Remaining checker stops and predicate diagnostics

Constructor-identity comparisons now narrow by class identity or subtype compatibility, while inequality retains the reference's behavior. Optional-call flow receives the expression it analyzes. Inference handles non-parameter entries without invalid casts, and protected-constructor traversal stops at types without class/interface bases. Numeric-literal and comma diagnostics convert between byte offsets and UTF-16 scanner positions. Malformed computed/import expressions, `new super`, and return statements outside functions follow the reference's recovery paths. Assertion diagnostics retain the declarations that need explicit annotations. Type predicates now validate return-type placement, parameter/rest/binding references and assignability.

All 25 previously recorded checker crashes or unsupported paths now finish checking. Release validation covers **1,564 configurations per reference mode**, increasing graph/code matches from **1,378 to 1,410**, with **32 recovered configurations and no regressions**. Nineteen unaffected initial results were retained. A selection review added 32 previously unrun object/literal-predicate cases without replaying completed cases. Both modes agree. Flow/inference/call queries pass **568 configurations**; four safety suites pass **282 assertions**, including 10 new checks backed by four pinned-Go fixtures. The final Release build has zero warnings and errors. Formatting preserved tokens, comments and syntax in 14 C# files. Unchanged repository and generator validation was reused; NativeAOT remains deferred until final phase-4 completion.

Evidence: [single-mode corpus](../csharp/compatibility/evidence/phase4-checker-stops-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-checker-stops-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-checker-stops-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-checker-stops-validation.json).

## Mapped programs and diagnostic integration

The corpus adapter now reconstructs mapper configuration from the test filesystem and drives the production C# mapper host. A small development-only bridge exposes the pinned reference suite's external mapper fixtures through their normal protocol. Those fixtures supply transformation output; the C# host, parser, mapping decoder, binder and checker process it. Fixture source, revision, executable and toolchain identity are recorded, and unchanged fixture binaries are reused.

Mapper project opening now receives the full project options, while transform identity still depends only on options declared by the mapper. Semantic diagnostics apply mapped directives after ordinary comment directives and suppress unnecessary diagnostics whose spans are entirely synthesized. Mapper-authored diagnostics retain their distinct source and coordinates.

All **15 mapped configurations match source graphs and diagnostic codes in both reference modes**, closing the final corpus execution stops. The Release build has zero warnings and errors. Mapper-host safety passes **44 assertions**, including the added full-options/declared-identity check; program/checker safety passes **80 assertions**. Formatting preserved tokens, comments and syntax in four C# files. The original reference corpus and unchanged Go/main repository validation were reused. These checks cover semantic processing, not phase-5 declaration emission. NativeAOT remains deferred until final phase-4 completion.

Evidence: [single-mode corpus](../csharp/compatibility/evidence/phase4-mapped-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-mapped-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-mapped-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-mapped-programs-validation.json).

## Import paths and unresolved modules

Missing relative ESM imports under Node16/NodeNext resolution now receive extension diagnostics, using the resolved usage mode and the reference's ordered file probes. Suggestions preserve `.mjs`, `.cjs` and JSX output extensions. JSON imports report the missing option when appropriate. Side-effect imports use the same resolution path, recognize ambient wildcard modules and permit resolved script targets. Untyped JavaScript modules produce implicit-any diagnostics or suggestions instead of a false "not a module" error. Missing Node built-ins use the Node type-definition message; the built-in name set is generated from the pinned source.

Release validation covers **562 configurations per reference mode**, increasing graph/code matches from **475 to 523**, with **48 recovered configurations and no regressions**. After the Node/untyped-module follow-up, 518 unaffected single-mode results were retained and 44 were replayed. Both modes agree. Alias/type-node queries pass **316 configurations**, and program/checker safety passes **83 assertions**, including three new checks backed by a pinned-Go `.mts` fixture. The final Release build has zero warnings and errors. Generated data freshness and formatting pass. Unchanged reference and repository validation was reused; NativeAOT remains deferred until final phase-4 completion.

Evidence: [single-mode corpus](../csharp/compatibility/evidence/phase4-import-paths-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-import-paths-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-import-paths-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-import-paths-validation.json).

## Source-graph closure and required properties

Graph construction retains ordering edges for JavaScript dependencies that exceed the search depth but are loaded as explicit roots or through shallower imports. Arbitrary-extension declarations are excluded when the importing source requires an option that is not enabled, and the checker reports the corresponding option diagnostic. The reference exporter now captures entry and auxiliary files in the harness's overwrite order. Only `augmentExportEquals2.ts` required corrected input; the other duplicate-filename sources retain their existing order. The cached correction cannot change expected outputs, and the driver rejects unknown correction names and missing oracle provenance.

Graph validation covers **74 configurations per reference mode**, with **six recovered configurations and no regressions**. All source graphs now match. The corrected reference case was regenerated once per mode; all other reference results were reused. Three negative controls confirm that invalid correction/provenance data is rejected before candidate execution.

Required-property diagnostics now distinguish one missing property, several properties and longer lists, retaining the required declarations for related information. Assignments, arguments and generic constraints share this reporting path. Function signatures, private members, normalized base types, generic mapped types and array-like types retain the appropriate broader diagnostic when a missing-property heading would misidentify the relation failure. Class-implementation and conversion headings remain intact.

The required-property comparison covers **1,439 configurations per reference mode**, increasing matches from **1,171 to 1,312**, with **141 recovered configurations and no regressions**. The final diagnostic-context correction replayed 158 single-mode cases and retained 1,281 unchanged results. Both modes agree. Call/type-node/assignability queries pass **704 configurations**; graph and structural-relation safety pass **55 assertions**, including seven new checks. The final Release build has zero warnings and errors. Formatting preserved tokens, comments and syntax in 12 C# files. Unchanged repository checks and completed validation results were reused; NativeAOT remains deferred until final phase-4 completion.

Evidence: [graph corpus](../csharp/compatibility/evidence/phase4-source-graphs-corpus-single.json), [input correction](../csharp/compatibility/evidence/phase4-source-graph-input-correction.json), [required-property corpus](../csharp/compatibility/evidence/phase4-required-properties-corpus-single.json), [required-property mode comparison](../csharp/compatibility/evidence/phase4-required-properties-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-graphs-properties-validation.json).

## Regular-expression diagnostics

The checker now invokes the existing regular-expression validator once per literal, using the configured target and skipping grammar checks when parse diagnostics are already present. Scanner coordinates are converted to byte-based source ranges. Distinct errors with the same code remain separate, same-position primary errors follow the reference's suppression rule, and spelling suggestions are attached as related diagnostics. These diagnostics participate in ordinary and mapped directive processing.

All **67 focused configurations match**. The affected comparison covers **1,316 configurations per reference mode**, increasing matches from **1,199 to 1,225**, with **26 recovered configurations and no regressions**. The initial 67 single-mode results were retained when checking the remaining 1,249 cases. Both modes agree. Expression queries pass **40 configurations**, and expression safety passes **81 assertions**, including three new range, related-information and repeated-query checks. The final Release build has zero warnings and errors. Formatting preserved tokens, comments and syntax in four C# files; unchanged reference and repository checks were reused. NativeAOT remains deferred until final phase-4 completion.

Evidence: [single-mode corpus](../csharp/compatibility/evidence/phase4-regexp-checker-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-regexp-checker-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-regexp-checker-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-regexp-checker-validation.json).

## Generic tuple relations and recursive constraints

Single-element generic tuples now use the reference's early relation rules: a mutable `[...U]` can relate through `U`, and a source can relate through the element of a readonly variadic target or a mutable variadic target when its constraint permits mutation. These checks run before expanding generic constraints. The missing relation had caused `infiniteConstraints2.ts` to recursively expand `Conv` while comparing signatures and emit **13,481 spurious instantiation-limit errors**. It now reports none, matching the reference. Instantiation limits and caches are unchanged. Temporary tracing and unrelated trial changes were removed, and the pinned Go source was restored and hash-checked.

Release validation covers **1,244 configurations per reference mode**, increasing matches from **1,152 to 1,156**, with **four recovered configurations and no regressions**. Both modes agree. Generic-relation/conditional/signature queries pass **496 configurations**. Safety passes **27 assertions**, including five new checks for recursion termination and mutable/readonly relation boundaries. The Release build has zero warnings and errors. Formatting preserved tokens, comments and syntax in three C# files; unchanged reference and repository results were reused. NativeAOT remains deferred until final phase-4 completion.

Evidence: [single-mode corpus](../csharp/compatibility/evidence/phase4-variadic-relations-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-variadic-relations-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-variadic-relations-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-variadic-relations-validation.json).

## Catch declarations, ambient initializers and import attributes

Catch bindings now follow catch-specific checks instead of ordinary variable-declaration initializer grammar. This removes false destructuring-initializer errors and reports invalid catch initializers and block-scoped redeclarations. Ambient variables and class properties share the reference's initializer rules, including enum property/element access and rejection of bare enum-valued aliases. Import/export attribute string checks now run at external-module syntax validation and prevent subsequent binding checks when invalid; import types retain their own attribute validation.

Release validation covers **2,320 configurations per reference mode**, increasing matches from **2,169 to 2,190**, with **21 recovered configurations and no regressions**. Both modes agree. The initial 66 single-mode results were retained; the second run checked only the other 2,254 cases. All oracle results were reused. Program safety passes **84 assertions**, including a new fixture checked against the pinned reference. The Release build has zero warnings and errors. Formatting preserved tokens, comments and syntax in seven C# files. Unchanged repository validation was reused. Malformed import-attribute parser recovery remains unresolved. NativeAOT remains deferred until final phase-4 completion.

Evidence: [single-mode corpus](../csharp/compatibility/evidence/phase4-context-grammar-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-context-grammar-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-context-grammar-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-context-grammar-validation.json).

## Diagnostic records and error ranges

Checker diagnostics now retain supplied message arguments and source ranges instead of discarding them into code-only lists. Error ranges follow the reference's declaration-name, first-token, multiline-arrow, constructor, switch-clause and `satisfies` rules, including byte positions for Unicode source. Ambient statement/body errors use their first token. Catch redeclarations, uninitialized const/using declarations, circular aliases and unresolved augmentations retain their available message arguments. Spelling suggestions and merged declarations now expose their retained related declarations. Diagnostics preserve message chains, and formatting traverses those chains without recursive calls. Program-level filtering retains the records through JavaScript checks, comment directives and content-mapper directives.

The corpus runner's `--diagnostics` option compares every exported diagnostic field: file, byte range, code, category, message key, arguments, message chains and related information. Top-level collection order is normalized; nested ordering and duplicate counts remain significant. Missing details fail the comparison. Existing callers that omit arguments and relation chains remain visible as mismatches; this does not complete diagnostic fidelity.

Release validation covers **1,331 configurations per reference mode**. Graph/code matches remain **1,233**, with **no regressions**; the cumulative code result remains **12,750/13,446**. Detailed records match in **787 configurations**, comprising **171 with semantic diagnostics and 616 with empty semantic diagnostics**. **544 selected configurations still differ in detailed records.** Both reference modes produce identical candidate records. The initial run's **57 unaffected results were retained**; only nine affected initial cases were replayed alongside 1,265 additional configurations. Cached reference outputs were reused throughout.

Program safety passes **90 assertions**, including six new diagnostic checks; expression safety passes **81 assertions**, retaining the existing regex range/related-information checks. Seventeen comparison assertions reject changes to fields, nested ordering and multiplicity. The Release build has zero warnings and errors. Formatting preserves tokens, comments and syntax in ten C# files. Unchanged repository validation was reused. NativeAOT remains deferred until final phase-4 completion.

Evidence: [single-mode corpus](../csharp/compatibility/evidence/phase4-diagnostic-details-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-diagnostic-details-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-diagnostic-details-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-diagnostic-details-validation.json).

## Assignment type text and reverse-mapped displays

Assignment, constraint, conversion and missing-property diagnostics now retain their source/target type text and property arguments. Literal generalization follows the target's singleton constraints, while `never` targets preserve the original source literal. Missing-property errors retain the related declaration. Computed property names use their symbolic spelling, such as `[Symbol.dispose]`, instead of exposing process-local IDs; quoted names preserve their diagnostic spelling.

Type display now covers templates, string mappings, substitution/`NoInfer` types, conditionals with inferred parameters, generic mapped types, named interfaces/classes, and additional precedence cases. Tuple and generic-reference displays omit the internal `this` argument. Reverse-mapped properties and indexes use the reference's elision rules, including `noErrorTruncation`'s `any` representation. This prevents unbounded expansion of a reverse-mapped `XMLHttpRequest` in `mappedTypeRecursiveInference.ts`. Computed enum types also render without throwing. Complete display flags, truncation, qualification, node building and nested relation-message chains remain incomplete.

Release validation covers **1,541 configurations per reference mode**. Graph/code matches remain **1,411**, with **no regressions and no checker failures**. Detailed diagnostic records match in **369 configurations**, all with semantic diagnostics; **1,172 still differ**. Within the 220 affected configurations that had already been checked for detailed fidelity, matches increased from **zero to 32**. Both modes agree on every final record. The cumulative detailed comparison now covers **2,652 configurations**, with **1,156 matches**, including **540 with semantic diagnostics** and 616 with empty semantic diagnostics. The cumulative graph/code result remains **12,750/13,446**.

The display fixture contains **32 exact queries against the pinned reference**, retained as regression expectations, plus two cancellation/retry checks. Program safety passes **124 assertions**, and expression safety passes **81**. The Release build has zero warnings and errors. Follow-up runs retain unaffected results: the last property-name fix replayed 522 configurations per mode and retained 1,019. Earlier enum, reverse-mapping and tuple fixes similarly replayed their affected cases; interrupted runs retain only completed results. Cached reference outputs and unchanged repository validation were reused. Formatting preserves tokens, comments and syntax in seven C# files. NativeAOT remains deferred until final phase-4 completion.

Evidence: [single-mode corpus](../csharp/compatibility/evidence/phase4-relation-details-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-relation-details-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-relation-details-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-relation-details-validation.json).

## Property, index and constraint explanation chains

Failed relations now support an explanation pass through the existing relation cache. Property and index incompatibilities retain their nested type mismatch; nested property paths collapse using the reference's dotted-name rule. Generic-target errors include the arbitrary-instantiation, constrained-subtype or distributed-parameter explanation. Trial union, template-placeholder and key comparisons suppress explanations, and variance fallback restores the previous explanation state. The negative-relation cache and cancellation behavior are preserved.

Type display also preserves index-signature parameter names and enforces the reference printer's absolute byte limits before invalid-byte replacement: **320 bytes normally and 2,000,000 with `noErrorTruncation`**. Rendering observes these limits before constructing unbounded intermediate strings. UTF-8 truncation uses the reference's per-byte replacement behavior. This resolves the excessive string expansion exposed by `recursiveIndexedAccessSimplification.ts`; that case's pre-existing semantic differences remain unresolved. The soft node-builder truncation rules and complete type display remain unfinished.

Release validation covers the same **1,541 configurations per reference mode** as the preceding checkpoint. Detailed matches increase from **369 to 527**, recovering **158 configurations with no regressions**. Every recovered case contains semantic diagnostics. Graph/code matches remain **1,411**, with no checker failures; both reference modes agree on every final record. Cumulative detailed matches increase to **1,314/2,652**, including **698 with semantic diagnostics** and 616 with empty semantic diagnostics. The cumulative graph/code result remains **12,750/13,446**.

Six focused diagnostics match the complete pinned-reference records, including property paths, index signatures and generic constraints. Safety passes **39 relation assertions**, **127 program assertions** and **81 expression assertions**. Three display-limit probes match the reference, including a truncated multibyte character and the expanded limit. New checks cover diagnostic replay from a cached failure and cancellation/retry without cache changes. The Release build has zero warnings and errors. The final single-mode replay covers 861 generic/template cases and retains 680 unaffected results; all oracle outputs are reused. Formatting checks preserve code/comments and raw-string values. Unchanged repository validation was reused. NativeAOT remains deferred until final phase-4 completion.

Remaining relation work includes signature/overload explanations, missing and inaccessible nested properties, readonly/tuple-specific errors, related declarations and complete error-selection behavior. This checkpoint does not complete diagnostic parity.

Evidence: [single-mode corpus](../csharp/compatibility/evidence/phase4-relation-chains-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-relation-chains-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-relation-chains-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-relation-chains-validation.json).

## Signature explanations and display

Signature comparisons now retain parameter-count, parameter-type, `this`-type, return-type and type-predicate explanations. Constructor comparisons report abstractness and visibility mismatches, and missing call/construct signatures include the target signature. Multiple-signature comparisons retain the first failed explanation and discard it if a later signature matches. Bivariant trial comparisons suppress explanations. Return-type markers produce method-return paths and are removed from the final diagnostic chain. Primitive wrapper comparisons follow the reference's structural-error suppression rule.

Diagnostic display now preserves predicate returns, method syntax and abstract constructor types. These additions do not complete overload resolution, signature display or diagnostic selection.

Release validation covers **1,541 configurations per reference mode**. Detailed matches increase from **527 to 574**, recovering **47 configurations with no regressions or checker failures**. Graph/code matches remain **1,411**, and both modes agree on every final record. Cumulative detailed matches reach **1,361/2,652**, including **745 with semantic diagnostics** and 616 with empty semantic diagnostics. The cumulative graph/code result remains **12,750/13,446**.

A focused fixture matches **13 complete pinned-reference diagnostic records** and verifies that a successful later overload discards earlier failures. Safety passes **129 signature/function/call assertions**, **127 program assertions** and **39 relation assertions**. The Release build has zero warnings and errors. The follow-up single-mode run replays 1,135 affected configurations and retains 406 completed results; reference outputs are reused. Formatting preserves tokens, comments and syntax in seven C# files. Unchanged repository validation was reused. NativeAOT remains deferred until final phase-4 completion.

Evidence: [single-mode corpus](../csharp/compatibility/evidence/phase4-signature-diagnostics-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-signature-diagnostics-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-signature-diagnostics-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-signature-diagnostics-validation.json).

## Property access, missing properties and tuple explanations

Property comparisons now report private/protected declaration conflicts and optional-versus-required mismatches. Nested missing-property errors retain their required declarations throughout the diagnostic chain; distinct private identifiers keep their specific explanation. Readonly array/tuple assignments select the reference's readonly diagnostic while preserving any preceding property explanation. The global `Object` case retains its general assignment error and explanatory note.

Tuple diagnostics now explain minimum/maximum lengths, required and variadic positions, and incompatible element positions or ranges. Tuple display parenthesizes optional/rest unions and other compound element types where required, while preserving boolean and alias spelling. Assignability decisions are unchanged by these reporting additions.

Release validation covers **1,541 configurations per reference mode**. Detailed matches increase from **574 to 649**, recovering **75 configurations**. Graph/code matches increase from **1,411 to 1,413**, recovering **two configurations**. There are no regressions or checker failures, and both modes agree on every final record. Cumulative detailed matches reach **1,436/2,652**, including **820 with semantic diagnostics** and 616 with empty semantic diagnostics. The cumulative graph/code result is **12,752/13,446**, leaving **694 code differences**.

A focused fixture matches **18 complete pinned-reference diagnostic records**. Safety passes **57 relation assertions**, **127 program assertions** and **129 signature assertions**. The Release build has zero warnings and errors. The final single-mode replay covers 139 cases potentially affected by the global `Object` explanation and retains 1,402 completed results. Cached oracle outputs and unchanged repository validation were reused. Formatting preserves tokens, comments and syntax in seven C# files. NativeAOT remains deferred until final phase-4 completion.

Evidence: [single-mode corpus](../csharp/compatibility/evidence/phase4-object-diagnostics-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-object-diagnostics-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-object-diagnostics-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-object-diagnostics-validation.json).

## Call-arity diagnostics

Call diagnostics now retain the expected and supplied value/type argument counts, including ranges and gaps between overload arities. Too-few-argument errors point to the callee and retain the missing parameter, binding pattern or rest-parameter declaration. Too-many-argument errors cover the excess arguments. Type-argument arity errors cover the type-argument list and distinguish overload gaps. Promise resolve callbacks receive the reference's `void` or JSDoc hint where applicable. This does not complete overload explanation chains or all call diagnostics.

Release validation covers **137 configurations per reference mode**, increasing graph/code matches from **120 to 123** with **three recovered configurations and no regressions or checker failures**. Detailed diagnostic records match in **74 configurations**, all with semantic diagnostics. Within the 66 configurations previously checked at this level, detailed matches increase from **one to 26**. Both modes agree on every final record. Cumulative detailed matches reach **1,509/2,723**, including **893 with semantic diagnostics** and 616 with empty semantic diagnostics. The cumulative graph/code result is **12,755/13,446**, leaving **691 code differences**.

A focused fixture matches **12 complete pinned-reference records**. Safety passes **141 signature/function/call assertions** and **127 program assertions**. The Release build has zero warnings and errors. Cached reference results and unchanged repository validation were reused; the candidate cases were run once per reference mode. Formatting preserves tokens, comments and syntax in three C# files. NativeAOT remains deferred until final phase-4 completion.

Evidence: [single-mode corpus](../csharp/compatibility/evidence/phase4-call-arity-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-call-arity-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-call-arity-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-call-arity-validation.json).

## Overload argument errors and expected-property origins

Failed overload calls now collect the selected candidate's argument diagnostics before adding the last-overload and no-matching-overload messages. The original error range, arguments and nested explanation remain intact. Reports include the last overload's declaration and, when applicable, the hidden implementation signature that would have accepted the call. The implementation check uses a separate call-resolution state. Diagnostic collection is scoped and restored through `finally`, including JSX relation elaboration.

Literal elaboration now retains expected-property or index-signature origins while excluding default-library declarations. Existing missing-property related information is preserved. Exact-optional literal mismatches use their specific diagnostic, and `this`-context failures retain both type arguments. Other call and decorator diagnostic work remains unfinished.

Release validation covers **1,576 configurations per reference mode**, increasing graph/code matches from **1,446 to 1,448**, with **two recovered configurations and no regressions or checker failures**. Detailed matches reach **733**, all with semantic diagnostics. Within the 1,554 configurations previously checked at this level, matches increase from **671 to 718**; 15 further matches are newly measured. Both modes agree on every final record. Cumulative detailed matches reach **1,571/2,745**, including **955 with semantic diagnostics** and 616 with empty semantic diagnostics. The cumulative graph/code result is **12,757/13,446**, leaving **689 code differences**.

A focused fixture matches **seven complete pinned-reference records**, including ordinary and generic implementation notes, property origins and `this`-context failures. Safety passes **148 signature/function/call assertions**, **127 program assertions** and **81 expression assertions**. The Release build has zero warnings and errors. Cached reference results and unchanged repository validation were reused; affected candidate configurations were run once per reference mode. Formatting preserves tokens, comments and syntax in ten C# files. NativeAOT remains deferred until final phase-4 completion.

Evidence: [single-mode corpus](../csharp/compatibility/evidence/phase4-overload-diagnostics-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-overload-diagnostics-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-overload-diagnostics-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-overload-diagnostics-validation.json).

## Missing-property diagnostic details

Missing-property reports now retain property spelling and receiver types, identify the first incompatible union constituent, and include static-member, library and spelling suggestions. Promise-backed properties retain the `await` hint, spelling suggestions point to their declaration, and impossible intersections include the conflicting-property explanation. Diagnostic names preserve escaped source spelling. This does not complete all property/element access diagnostics or suggestion APIs.

Release validation covers **523 configurations per reference mode**. Graph/code matches remain **459**, with no regressions or checker failures. Detailed records match in **196 configurations**, all with semantic diagnostics. Within the 155 configurations previously checked at this level, matches increase from **zero to 55**; 141 additional matches are newly measured. Both modes agree on every final record. Cumulative detailed matches reach **1,767/3,113**, including **1,151 with semantic diagnostics** and 616 with empty semantic diagnostics. The cumulative graph/code result remains **12,757/13,446**.

A focused fixture matches **10 complete pinned-reference records**. Safety passes **64 access/member/spelling assertions**, **127 program assertions** and **81 expression assertions**. The Release build has zero warnings and errors. Cached reference results and unchanged repository validation were reused; affected candidate configurations were run once per reference mode. Formatting preserves tokens, comments and syntax in three C# files. NativeAOT remains deferred until final phase-4 completion.

Evidence: [single-mode corpus](../csharp/compatibility/evidence/phase4-property-diagnostics-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-property-diagnostics-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-property-diagnostics-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-property-diagnostics-validation.json).

## Element and index diagnostic details

Element/index errors now retain receiver and index types, tuple lengths and positions, spelling/static suggestions, and receiver-qualified `get`/`set` hints. Implicit-`any` index errors retain the specific missing-property or missing-index-signature explanation, including enum and unique-symbol keys. Reporting is asynchronous so type display can use the existing checker services; cancellation removes the in-progress deduplication key and permits retry without publishing a partial diagnostic. This does not complete all indexed-access validation or symbol qualification.

Release validation covers **594 configurations per reference mode**. Graph/code matches remain **517**, with no regressions or checker failures. Detailed records match in **249 configurations**, all with semantic diagnostics. Within the 538 configurations previously checked at this level, matches increase from **189 to 217**; 32 additional matches are newly measured. Both modes agree on every final record. Cumulative detailed matches reach **1,827/3,169**, including **1,211 with semantic diagnostics** and 616 with empty semantic diagnostics. The cumulative graph/code result remains **12,757/13,446**.

A focused fixture matches **18 complete pinned-reference records**. Safety passes **51 indexed-type assertions**, including cancellation/retry checks, **127 program assertions** and **64 access assertions**. The Release build has zero warnings and errors. Cached reference results and unchanged repository validation were reused; affected candidate configurations were run once per reference mode. Formatting preserves tokens, comments and syntax in five C# files. NativeAOT remains deferred until final phase-4 completion.

Evidence: [single-mode corpus](../csharp/compatibility/evidence/phase4-index-diagnostics-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-index-diagnostics-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-index-diagnostics-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-index-diagnostics-validation.json).

## Member access and declaration references

Access diagnostics now retain property and receiver names for readonly writes, private/protected/abstract members, generic indexing and initialization errors. Protected-instance errors distinguish the enclosing class from the resolved receiver constraint; synthetic properties without a declaring class use the containing type. Private-identifier shadowing retains both relevant declarations. Access/member/index-write callbacks are awaitable so reporting can use the existing asynchronous type services.

The declaration-use host now retains the related information already requested by the checker, including declaration-order and type-only import/export references. These callbacks previously discarded it. This does not complete all access, declaration or suggestion APIs.

Release validation covers **755 configurations per reference mode**. Graph/code matches remain **673**, with no regressions or checker failures. Detailed records match in **372 configurations**, all with semantic diagnostics. Within the 556 configurations previously checked at this level, matches increase from **202 to 238**; 134 additional matches are newly measured. Both modes agree on every final record. Cumulative detailed matches reach **1,997/3,368**, including **1,381 with semantic diagnostics** and 616 with empty semantic diagnostics. The cumulative graph/code result remains **12,757/13,446**.

A focused fixture matches **18 complete pinned-reference records**. Safety passes **82 access/member assertions**, **127 program assertions** and **51 indexed-type assertions**. The Release build has zero warnings and errors. The follow-up single-mode run replays 50 affected cases and retains 705 completed results. Cached oracle outputs and unchanged repository validation were reused. Formatting preserves tokens, comments and syntax in twelve C# files. NativeAOT remains deferred until final phase-4 completion.

Evidence: [single-mode corpus](../csharp/compatibility/evidence/phase4-access-diagnostics-corpus-single.json), [reference-default corpus](../csharp/compatibility/evidence/phase4-access-diagnostics-corpus-default.json), [mode comparison](../csharp/compatibility/evidence/phase4-access-diagnostics-mode-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-access-diagnostics-validation.json).

## Type queries at syntax locations

`GetTypeAtLocationAsync` now dispatches by syntax context to the existing expression, type-node, declaration, binding, heritage and module services. It preserves regular literal types for expressions, declaration types for names, inherited `this` arguments, and the reference's behavior inside `with` statements. Original JSDoc nodes resolve to their reparsed semantic counterparts. The query boundary retains program ownership checks, exclusive checker access and cancellation. The expression query path uses the reference's quick call/constructor/assertion rules and type-only checking. JSX attribute symbols now use the same initializer typing as JSX attribute objects.

The new Release comparison visits every main-file syntax node in twenty fixtures under strict/non-strict options and single/reference-default program modes. **All 80 candidate configurations execute**, comprising **3,956 queries**. **76 configurations match the entire exported type/symbol graph and diagnostic-code output**, including **3,812 type-at-location queries**. Four configurations encounter a null-symbol panic in the pinned Go reference for a type-only named-import clause; those are retained as reference failures, not passing comparisons. The candidate handles that missing declaration symbol with its error type. Default type-only imports and individual type-only specifiers have separate passing coverage. These fixtures do not establish full-corpus query parity or actual parallel checker scheduling.

The final Release build has zero warnings and errors. **Twenty query safety assertions** pass, covering regular versus declaration literal identity, flow narrowing, checker ownership, serialized queries, queued cancellation/retry, a 20,000-level qualified name and original/reparsed JSDoc lookup. The preceding program safety checks reached the new query assertions without failure; their 127 existing results were retained. After the JSX fix, 72 unaffected candidate results were reused and only the four affected JSX configurations plus four previously unexecuted candidate configurations were run. Cached oracle results and the unchanged repository suite were reused. A later change affected only safety tests and their command entry point; corpus results were retained. Formatting preserves tokens, comments and syntax in eight C# files.

The cumulative semantic-diagnostic counts above are unchanged. This checkpoint covers general type lookup; subsequent work covers symbol lookup. Remaining query APIs, complete type display/node builders, emit resolver and phase-wide completion gates remain open. NativeAOT publishing remains deferred until final completion.

Evidence: [query comparisons](../csharp/compatibility/evidence/phase4-type-queries.json), [reference failures](../csharp/compatibility/evidence/phase4-type-queries-reference-failures.json), [retained results](../csharp/compatibility/evidence/phase4-type-queries-reuse.json), and [Release validation](../csharp/compatibility/evidence/phase4-type-queries-validation.json). The managed-only runner is `node csharp/tools/checker-locations.mjs`; it uses the existing Release harness and prepared pinned program oracle, caching results by input and executable hashes. Its exit code remains nonzero while the four reference failures are unresolved.

## Symbol queries at syntax locations

`GetSymbolAtLocationAsync` now implements context-sensitive lookup for declaration and import/export names, aliases, qualified names, properties, literal element keys, index signatures, private identifiers, `this`/`super`, heritage clauses, JSX tags, module specifiers and JSDoc references. It shares unresolved-symbol identities with type resolution and retains synthetic index and `import.meta` symbols per checker. Original documentation nodes map to reparsed declarations. Module lookup can omit lookup diagnostics without marking them as reported, so a later semantic check still reports missing imports. The new query uses the existing ownership and serialization boundary.

Release comparisons execute **104 configurations and 5,312 symbol queries**, including original JSDoc comments. **96 complete records match exactly.** The other eight differ only in the input comment nodes' recorded start/end positions; their symbol answers, symbol/type graphs and diagnostic codes match. All eight remain strict failures in the evidence and runner exit status. This does not extend the parser compatibility ledger or establish a complete source-corpus query gate. Neither reference nor candidate has an execution failure in this suite.

**Twenty-five symbol-query safety assertions pass**, covering alias targets, property declarations, synthetic index identity, unresolved-name identity, independent checker state, foreign-node rejection, serialized queries and queued cancellation/retry. Lookup of missing names/modules leaves the diagnostic collection unchanged; a later semantic pass still emits the errors. The Release build has zero warnings and errors. Follow-up comparisons reuse **80 unaffected candidate configurations and 96 reference configurations**; the existing type-query, program-safety and repository results are also retained. Formatting preserves tokens, comments and syntax in eight C# files. The pinned checker source matches Git blob `8aec6df21d7af00dae77b0e831dcb4d34149893e`.

Evidence: [symbol-query comparisons](../csharp/compatibility/evidence/phase4-symbol-queries.json), [strict documentation-span differences](../csharp/compatibility/evidence/phase4-symbol-queries-documentation-spans.json), [retained results](../csharp/compatibility/evidence/phase4-symbol-queries-reuse.json), and [Release validation](../csharp/compatibility/evidence/phase4-symbol-queries-validation.json). The managed runner is `node csharp/tools/checker-locations.mjs --symbols`. Complete query coverage, type display/node builders, accessibility, emit resolver, actual parallel checker scheduling and semantic workload budgets remain open. NativeAOT verification remains deferred until phase-4 completion.

## Scope, exports and symbol types

Seven query APIs now expose visible symbols, module exports, alias targets, local export targets, shorthand values, parameter-property symbol pairs and a symbol's type at a use location. Scope traversal preserves lexical shadowing, locally visible exports, class type-parameter visibility, named expressions, `arguments`, module-attribute boundaries and reserved-name filtering. Symbol-type queries distinguish narrowed reads, setter/write types and queries without a use location. The methods use a shared query lease that releases the checker gate after success, cancellation or exceptions; node-based queries validate syntax ownership.

The Release comparison passes **all 104 configurations and 29,500 records** against the pinned reference, including the exported type/symbol graphs and diagnostic codes. The records cover **27,440 scope results**, **1,904 symbol types at a location and 1,904 without a location**, **52 module-export results**, **60 alias targets**, **16 local-export targets**, **16 shorthand values**, and **12 parameter-property pairs**. Each symbol-type record contains both type answers. Scope and export collections are sorted by symbol name in the probe because the Go API enumerates maps; the returned symbols, multiplicity and graph identities remain compared. Fixtures include strict/non-strict options, nested shadowing, static and instance generics, inferred/mapped parameters, re-exports, read/write accessors and constructor parameter properties. This is fixture coverage, not a completed source-corpus or parallel-checker gate.

**Twenty-three scope/symbol-service safety assertions pass**, including shadowing, distinct parameter/property identities, export-versus-alias identity, foreign-node rejection, cancellation, queued requests and recovery after a callback throws. The Release build has zero warnings and errors. The 104 comparison results were retained after adding the safety tests; the product compiler and comparison path were unchanged. Earlier type/symbol query and semantic-corpus results were reused, as was the unchanged repository suite. Formatting preserves tokens, comments and syntax in seven C# files. The frozen `services.go` matches pinned Git blob `c7d9995de5747cef4d82925d85c9def54f456c8f`.

Evidence: [scope/service comparisons](../csharp/compatibility/evidence/phase4-scope-queries.json) and [Release validation](../csharp/compatibility/evidence/phase4-scope-queries-validation.json). Run the prepared managed harness and pinned oracle with `node csharp/tools/checker-locations.mjs --scopes`. Remaining contextual/signature/accessibility APIs, complete display/node builders, emit resolver and whole-phase validation gates remain open. NativeAOT verification remains deferred until phase-4 completion.

## Contextual types and call-signature queries

Nine query APIs now expose contextual expression, argument, object-element, JSX-attribute and array-position types; resolved signatures and return types; signature-help candidates; and string-literal completion candidates. Queries that ignore an expression's inference temporarily clear enclosing call/function caches, mark only the relevant source nodes and suppress overload-error reporting. `finally` restores the saved signature/value caches, inference markers and apparent argument count on success, cancellation or exceptions. Normal source checking keeps its existing default state.

The comparisons exposed three shared gaps: JSX tag-name context lookup was not connected, generic overload failures reused instances that the reference creates afresh, and an omitted binding element received widening `any` instead of ordinary `any` in non-strict mode. Those paths now follow the pinned implementation. Fresh overload-failure instances preserve candidate identity in repeated signature-help and completion queries.

Release comparison passes **all 112 configurations and 7,644 records**, including complete exported signature, symbol and type graphs plus diagnostic codes. Coverage includes all contextual flags, nested generic callbacks, constrained string completions, failed overloads, apparent argument counts, tuples/spreads, JSX and dynamic imports. The follow-up replays the fourteen affected configurations and retains 98 completed results. **Twenty-four context-query cache/cancellation assertions**, **148 signature/function assertions** and **81 expression/context assertions** pass. The final Release build has zero warnings and errors.

Shared fixes also receive a targeted corpus replay of **571 configurations per reference mode**, selected for call failures, array elisions or suppressed diagnostics. Both modes retain **519 graph/code matches**, with no regressions or execution failures, and all final records agree between modes. One spurious 7031 diagnostic is removed from each of three destructuring configurations; their 7031 counts now match the reference, though other differences remain. Detailed records match in **335 configurations**; the 278 previously matching detailed configurations remain matched. The 57 additional matches are newly measured, not recovered failures. Cumulative detailed coverage is now **2,054/3,430**, including **1,383 with semantic diagnostics** and 671 with empty semantic diagnostics. The overall graph/code count remains **12,757/13,446**, leaving **689 differences**.

Evidence: [context/signature comparisons](../csharp/compatibility/evidence/phase4-context-queries.json), [retained query results](../csharp/compatibility/evidence/phase4-context-queries-reuse.json), [single-mode semantic replay](../csharp/compatibility/evidence/phase4-context-query-semantics-single.json), [reference-default replay](../csharp/compatibility/evidence/phase4-context-query-semantics-default.json), [mode agreement](../csharp/compatibility/evidence/phase4-context-query-semantics-parity.json), and [Release validation](../csharp/compatibility/evidence/phase4-context-queries-validation.json). The managed API runner is `node csharp/tools/checker-locations.mjs --contexts`. Cached oracle outputs and unaffected repository checks are reused; formatting preserves tokens, comments and syntax in eleven C# files. Remaining checker APIs, full display/node builders, accessibility, emit resolver and whole-phase gates remain open. NativeAOT verification remains deferred until final completion.

## Declaration visibility and alias retention

The checker now implements the emit resolver's declaration-visibility state: visibility checks, upfront export/import-alias marking and retention of otherwise hidden declarations needed to name exported types. It handles declaration containers, ambient modules and augmentations, member modifiers, destructuring, CommonJS exports and reparsed/original JSDoc declarations. Internal import-equals chains are followed with cycle detection. Visibility decisions are cached as in the reference, including their behavior when queried before alias marking. A mutation journal restores both declaration entries and file markers if an operation is canceled or throws; normal results retain the reference's state changes.

Release comparison passes **all 112 configurations and 24,832 records**, covering visibility before and after precalculation, on-demand retention, and repeated precalculation. Each visibility phase compares **5,628 AST/documentation nodes**; another **2,320 records** compare visible-declaration results and retained alias statements. Alias-statement collections are sorted by stable node identity in the probe, preserving duplicates. Original documentation inputs are identified by owner, traversal index and kind; their source ranges remain part of the separate parser contract. The complete exported type/symbol graphs and diagnostic codes also match.

**Thirty-three safety assertions pass**, covering alias-chain retention, queries without alias marking, cancellation after marking multiple declarations, rollback after a callback throws, retry, idempotent precalculation, synthetic/foreign nodes and a **20,000-level** parent chain. The final Release build has zero warnings and errors. The comparison passed on its first run and was retained after adding safety tests. Earlier query, source-corpus and repository results were reused because existing checker paths are unchanged. Formatting preserves tokens, comments and syntax in five C# files. The frozen emit-resolver source matches Git blob `9bc590fd179bfb8f44b75350ac296adf55709945`.

Evidence: [visibility comparisons](../csharp/compatibility/evidence/phase4-declaration-visibility.json) and [Release validation](../csharp/compatibility/evidence/phase4-declaration-visibility-validation.json). The managed runner is `node csharp/tools/checker-locations.mjs --visibility`. This is the visibility foundation for accessibility and emission; accessible symbol chains, complete symbol-accessibility results and the rest of the emit resolver remain open. NativeAOT verification remains deferred until phase-4 completion.

## Accessible symbol chains

`GetAccessibleSymbolChainAsync` now searches the reference's scope tables for direct names and usable aliases. The lookup honors shadowing, qualification, external-alias restrictions, namespace re-exports, UMD restrictions, named class expressions and the `globalThis` fallback. Competing chains prefer shorter paths and then the reference's declaration order. Member lookup uses raw type-parameter tables without forcing late-bound names.

Local, member, raw-export and resolved-export tables have distinct identities for cycle detection and alias caching. Positive and negative chain results are cached by the first relevant scope, meaning and alias restriction. Returned chains are immutable. Exceptions and cancellation restore cache entries created or replaced by the request, and active traversal markers are removed in `finally`. Recursive alias traversal uses the existing runtime-async stack boundary.

Release comparison passes **all 112 configurations and 60,216 records**, including the complete exported symbol/type graphs and diagnostic codes. It compares **17,340 found chains** and **42,876 absent chains**, in value/type/namespace meanings with and without external-alias restrictions. Coverage includes four-part qualification through cyclic namespace re-exports, shorter aliases, shadowed imports, merged symbols, type-only imports and class self references. The original 108 results were reused when four multi-hop configurations were added.

**Eighteen safety assertions pass**, covering chain choice and identity, immutable results, qualification, `globalThis`, class-expression names, callback/cancellation rollback and retry, foreign syntax and a **20,000-level** scope chain. The Release build has zero warnings and errors. Existing semantic/query results and the unchanged repository suite were retained. Formatting preserves tokens, comments and syntax in five C# files. The frozen accessibility source matches Git blob `d19806d50599d98d89e0908aa8ac60db15dbc369`.

Evidence: [chain comparisons](../csharp/compatibility/evidence/phase4-symbol-chains.json), [retained results](../csharp/compatibility/evidence/phase4-symbol-chains-reuse.json), and [Release validation](../csharp/compatibility/evidence/phase4-symbol-chains-validation.json). The managed runner is `node csharp/tools/checker-locations.mjs --chains`. Full symbol-accessibility results still require alternative-container discovery and diagnostic naming; those, complete display/node builders and the remaining emit resolver stay open. NativeAOT verification remains deferred until final phase-4 completion.

## Accessibility decisions and symbol containers

Container discovery now follows parent symbols, export-equals targets, re-exporting modules, object/type-literal variables and instance variables that can act as namespaces. It preserves the reference's ordering and duplicates, prefers imports from the enclosing file when they resolve in that location's mode, and otherwise searches the program's external modules. File-specific and program-wide results are cached separately. The new location-mode query reuses the program's existing resolution-mode rules; existing import-checking calls keep their previous resolution path.

The accessibility decision service now combines container discovery, accessible chains and visible declarations. It distinguishes accessible, inaccessible and unnameable symbols, retains required aliases, and supplies the reference's type/value/flag boolean APIs. Entity-name visibility is implemented with its complete result, including unresolved names and error nodes. **General symbol-accessibility diagnostic names are not implemented or compared here**: the decision keeps the symbols requiring names for the future node-builder formatter. This does not establish full formatted `IsSymbolAccessible` parity.

Release comparison matches **all 156 configurations and 125,544 records**: **19,452 ordered container lists**, **77,808 accessibility decisions**, **19,452 flag queries**, **6,484 paired type/value queries**, and **2,348 complete entity-name visibility results**. Decision records compare status, alias statements and error locations. Exported symbol/type graphs and diagnostic codes also match. Coverage includes direct and alternative containers, re-exports, export-equals modules, instance/object containers, hidden declarations, alias marking, type/value/namespace meanings and module-policy variants. Correcting enclosing-location resolution replayed 44 configurations and retained 112 completed results.

**Twenty-four new accessibility/container/rollback assertions** and **18 existing chain safety assertions** pass. The checks cover imported aliases, inaccessible local and foreign-module declarations, resolution-mode selection, exact entity-name errors, combined cache rollback after cancellation or exceptions, and retry. The Release build has zero warnings and errors. Existing source-corpus results are retained; formatting preserves tokens, comments and syntax in eight C# files. The legacy chain transaction was extracted for reuse and its cancellation checks were rerun.

Evidence: [accessibility comparisons](../csharp/compatibility/evidence/phase4-accessibility.json), [retained results](../csharp/compatibility/evidence/phase4-accessibility-reuse.json), and [Release validation](../csharp/compatibility/evidence/phase4-accessibility-validation.json). The managed runner is `node csharp/tools/checker-locations.mjs --accessibility`. Diagnostic-name formatting, the remaining node-builder and emit-resolver services, and whole-phase gates remain open. NativeAOT verification remains deferred until final phase-4 completion.

## Module-specifier ending and path rules

Symbol-name formatting exposed a missing dependency: module-specifier generation. Its extension and path layer now implements ending preferences, Node ESM/CJS rules, existing-import style, TypeScript-extension permissions, declaration/non-JavaScript extension remapping, `index` filename collisions, `rootDirs` projection and reverse `paths` mappings. Mapping order, wildcard matching, case sensitivity and extension priority follow the pinned implementation. The helpers accept cancellation during input-dependent searches. They are building blocks for the generator; package discovery, package exports/imports, redirects and final candidate selection are not yet implemented by this layer.

Release comparison passes **all 16,240 cases**: **12,960 ending-preference combinations**, **920 filename-ending cases**, **eight non-JavaScript declaration mappings**, **1,008 root-directory cases**, and **1,344 path-mapping cases**. Coverage includes strict extension ordering, `.d.mts`/`.d.cts` preservation, JSX output, ambiguous `index` paths, case-sensitive/insensitive hosts, multiple roots and drive/share paths. Twenty focused path/extension/cancellation assertions pass. The Release build has zero warnings and errors.

The first run found sixteen declaration-extension precedence differences. After the correction, only those sixteen candidate cases were replayed; **16,224 candidate results and all 16,240 reference results were reused**. A reference-wrapper correction changed byte-enum output from base64 to integer arrays; the saved bytes were decoded losslessly rather than rerunning the unchanged Go algorithms. Formatting preserves tokens, comments and syntax in three C# files. Existing checker and repository validations remain applicable because no existing production path calls the new naming helpers yet.

Evidence: [path/ending comparisons](../csharp/compatibility/evidence/phase4-module-specifier-paths.json), [reuse provenance](../csharp/compatibility/evidence/phase4-module-specifier-paths-reuse.json), and [Release validation](../csharp/compatibility/evidence/phase4-module-specifier-paths-validation.json). The managed runner is `node csharp/tools/module-specifier-paths.mjs`, using the prepared module-specifier oracle and Release harness. This does not complete module-specifier generation or formatted symbol-accessibility results. NativeAOT verification remains deferred until phase-4 completion.

## Reverse package maps and output paths

Module-specifier naming now has the reverse package-map layer. It follows ordered conditional branches, custom and versioned `types` conditions, arrays, exact targets, legacy directory mappings and wildcard mappings. Package-import lookup observes the nearest package boundary and the reference's `#/` restrictions. JavaScript/declaration output projections account for output directories, JSX, Node extensions and content-mapper extensions. The implementation uses an explicit stack for nested map values and checks cancellation during traversal.

Release comparison passes **all 24,455 cases**: **21,504 raw map combinations**, **784 package-export maps**, **2,016 package-import cases**, **136 output-path cases**, and **15 condition lists**. It covers ordering and fallback, null/invalid entries, compiler-version conditions, case-sensitive/insensitive hosts, TypeScript-extension preferences and declaration output. The matrix passed on its first run. **Nineteen focused assertions** also pass, including **20,000-level** nesting, cancellation and retry. The Release build has zero warnings and errors.

The completed matrix was retained after adding safety tests. Earlier ending/path, checker and repository evidence remains applicable; no existing production caller invokes the new package-naming layer. Formatting preserves tokens, comments and syntax in four C# files. The pinned map and output-path sources match blobs `5418ca371aa3a9e64e0631aa4b37a6e7201b9d89` and `83a098654063fa6a9ba27e523cb9e48d9a1be65f`.

Evidence: [package-map comparisons](../csharp/compatibility/evidence/phase4-module-package-maps.json) and [Release validation](../csharp/compatibility/evidence/phase4-module-package-maps-validation.json). Run the prepared managed harness and reference oracle with `node csharp/tools/module-specifier-paths.mjs --packages`. Candidate module-path discovery, final specifier selection and integration with symbol formatting remain open. NativeAOT verification remains deferred until phase-4 completion.

## Node-module package naming

The package-naming layer now combines ending preferences and reverse export/path maps to name files under `node_modules`. It handles package entry points, scoped and nested packages, `@types` names, version-specific paths, target-extension import modes, redirects and global-typings-cache restrictions. The pinned implementation retries the same package root for each directory component; the C# implementation performs that stable cached lookup once.

Release comparison passes **1,674 new cases**, and **10 safety assertions** cover public/private exports, redirects, cache exclusions, cancellation/retry and a **20,000-directory** path. The Release build has zero warnings and errors. An attempted UNC importing-root fixture failed in the reference virtual filesystem before comparison; UNC-root naming is not claimed as validated by this checkpoint. Completed reference rows were retained while correcting fixture setup, and every candidate comparison ran once. Earlier path/package-map/checker results remain retained because their algorithms and callers did not change.

Evidence: [node-module comparisons](../csharp/compatibility/evidence/phase4-node-module-specifiers.json) and [Release validation](../csharp/compatibility/evidence/phase4-node-module-specifiers-validation.json). The managed runner is `node csharp/tools/module-specifier-paths.mjs --node-modules`. Candidate-path discovery, final selection and symbol-formatter integration remain incomplete; the overall corpus counts above are unchanged. NativeAOT remains deferred until final phase completion.

## Candidate paths and module-specifier selection

`ModuleSpecifierGenerator` now combines the existing ending, path-map and package-name algorithms. Candidate discovery includes project outputs, duplicate-package redirects and symlink alternatives; it filters ignored real paths and orders candidates by proximity, redirect status and path length. Selection first reuses an existing import when its resolution mode permits, then follows the reference's priority among mapped, redirected, package and relative names. Local naming handles root directories, package imports, project/package boundaries, extension preferences and exclusions supplied by the caller.

Release comparisons match **927 cases**: **110 candidate-path cases**, **564 local-name cases**, **217 selection cases**, and **36 combined discovery/selection cases**. They exercise existing-import mode conflicts, empty imports, Unicode ordering, dotted-I path identity, symlinks, ignored paths, redirects, custom path maps and preference/exclusion interactions. Twelve safety assertions cover immutable results, self-package symlink avoidance, cancellation, callback failure and retry. The Release build has zero warnings and errors. After the Unicode identity correction, only one affected candidate case was replayed; 926 candidate results and all 927 reference results were retained.

The required `IModuleSpecifierHost` explicitly supplies program resolutions, project redirects and known symlink directories. These comparisons use a fixture implementation of that contract. **The production compiler-program adapter and symbol-formatter integration remain incomplete.** Exclusion tests supply equivalent prefix predicates and reference regexes; they validate selection behavior, not a general regex engine. Existing semantic corpus and earlier naming-helper results remain applicable because their callers and algorithms are unchanged.

Evidence: [generation comparisons](../csharp/compatibility/evidence/phase4-module-generation.json), [retained results](../csharp/compatibility/evidence/phase4-module-generation-reuse.json), and [Release validation](../csharp/compatibility/evidence/phase4-module-generation-validation.json). Run the prepared reference oracle and managed harness with `node csharp/tools/module-specifier-paths.mjs --generation`. The overall corpus counts remain unchanged, and NativeAOT verification remains deferred until final phase completion.

## Production program host for module naming

The module-specifier generator now has a production host on `CompilerProgram`. It supplies resolved imports, per-project resolution modes, project source/output mappings, duplicate-package redirects, global typings and known directory symlinks. Symlinks are discovered from resolved modules/type references and runtime dependencies of files eligible for emit; development-only dependencies are excluded. Host initialization publishes its caches only after successful completion, so exceptions or cancellation can be retried. Returned names and candidate lists remain immutable, and concurrent calls serialize access to the naming caches.

The program retains external-library reachability metadata and uses the reference's common-source-directory rule: an explicit `rootDir`, otherwise the config directory when a config exists, otherwise the common directory of eligible source files. Resolution-mode queries now use the referenced project's options for its source/output files. Three missing program diagnostics exposed by the new integration fixtures were implemented: **5090** for non-relative path substitutions, **5069** for `declarationDir` without declaration emit, and **5011** for changed inferred output layout, including its migration message chain. These program-option diagnostics are separate from the semantic-corpus counts at the top of this report.

Release comparison matches **52 complete program configurations**, **112 source/resolution metadata records**, and **2,448 module-naming queries**, including candidate paths and ordered names. Coverage includes both case-sensitivity settings and graph-construction concurrency modes, project-reference sources/outputs, dual import modes, duplicate packages, symlinks discovered without imports, self-package avoidance, JSON/JavaScript sources and global typings. Complete exported diagnostic records match, including locations, arguments and chains. Twelve affected configurations were replayed after adding the missing diagnostics; forty matching configurations were retained. Exports of complete diagnostics extended the forty known-empty reference results without rerunning them.

**Twelve host safety assertions pass**, covering cold and in-progress cancellation, failure/retry, cache reuse without repeated reads, foreign-source rejection, immutable results and sixteen concurrent callers. This validates naming-cache ownership, not parallel semantic checker scheduling. The Release build has zero warnings and errors. Formatting preserves tokens, comments and syntax in nine C# files. Existing semantic corpus results remain applicable: those probes do not include program-option diagnostics, and existing programs in the query fixtures have no project references. The earlier path/package/generation algorithms are unchanged, and earlier program graph inputs do not meet the new option-diagnostic conditions.

Evidence: [program-host comparisons](../csharp/compatibility/evidence/phase4-module-program-host.json), [retained results](../csharp/compatibility/evidence/phase4-module-program-host-reuse.json), and [Release validation](../csharp/compatibility/evidence/phase4-module-program-host-validation.json). The managed runner is `node csharp/tools/module-specifier-paths.mjs --program`. Symbol-name construction and formatted accessibility results still require integration with this generator. NativeAOT remains deferred until final phase completion.

## Diagnostic symbol names and formatted accessibility

`GetSymbolDisplayNameAsync` implements the diagnostic `AllowAnyNodeKind` symbol-name path: accessible aliases, parent qualification, alternative-container ordering, default/export-equals declarations, assigned anonymous declarations, JavaScript property declarations, computed/literal names and external-module specifiers. It preserves declaration spelling and the reference's quoting and Unicode-escaping rules. Qualification uses the existing chain/container services and the production module-specifier generator.

`GetSymbolAccessibilityAsync` now returns the complete result, including error symbol/module names. Decisions retain the meaning at the point where an inaccessible parent was found, so formatting uses that recursive context. Inaccessible names from a foreign module format the module without the enclosing declaration; inaccessible containers use its namespace context. Visibility, chain and container request caches retain their existing cancellation/failure rollback.

Release comparison matches **172 configurations and 122,012 records**: **27,116 symbol displays** and **94,896 complete accessibility results**. Exported symbol/type graphs and diagnostic codes also match. Coverage includes exports, export-equals wrappers, aliases and shadowing, private/unnameable symbols, computed and numeric names, quoted/Unicode names, assigned function/class names and JavaScript declarations. After correcting declaration-name lookup, 140 unaffected configurations were retained; 24 affected existing configurations and 8 new ones were executed. All 164 existing reference results were retained, with only the 8 new reference configurations executed.

**Seventeen safety assertions pass**, covering qualification and module names, complete inaccessible results, source ownership, cancellation, callback failure, rollback of chain/container caches, retry and independent checker state. The Release build has zero warnings and errors. Formatting preserves tokens, comments and syntax in six C# files. The reference probe function was subsequently moved unchanged into the existing bridge so existing oracle setup scripts continue to include it; this required no repeated comparison.

Evidence: [display and accessibility comparisons](../csharp/compatibility/evidence/phase4-symbol-display.json), [retained results](../csharp/compatibility/evidence/phase4-symbol-display-reuse.json), and [Release validation](../csharp/compatibility/evidence/phase4-symbol-display-validation.json). The managed runner is `node csharp/tools/checker-locations.mjs --symbol-display`. This checkpoint implements diagnostic symbol names, **not every `SymbolToStringEx` flag or general node-builder operation**. Entity-name-only formatting, type-argument/parameter formatting, computed-property write mode and full type/node serialization remain part of the unfinished phase. Existing semantic diagnostic callers retain their previous display service, so the corpus totals above are unchanged. NativeAOT remains deferred until final phase completion.

## Remaining completion work

The following phase-4 requirements remain open:

1. Complete semantic-pass coverage and finalization, remaining module adaptation and import-attribute checks, computed exports and remaining type/value symbol resolution. Checker creation, source traversal, program-backed queries, globals, augmentation merging, declaration headers and alias/export algorithms now exist; their remaining semantic callbacks must be connected.
2. Complete relation diagnostics and remaining type-node dependencies; connect the implemented declaration/type-node, algebra, scope, inference, instantiation and tuple algorithms to complete checker services.
3. Remaining expression forms and special call forms, full declaration checking, JavaScript and JSDoc semantics, and completion of contextual/inference integration across those services.
4. Complete property/declaration flow integration, constructor-identity narrowing, initialization/reference services, and iterator/generator diagnostic and emit integration.
5. Remaining indexed/member diagnostic and declaration services, JSX/decorator edge cases and grammar checks.
6. Type display, node builders, symbol accessibility and emit-resolver APIs.
7. All active checker/compiler type/symbol/diagnostic comparisons at single and reference-default concurrency; audits of intentional differences; complete semantic workload memory/performance measurements.

The next integration work is semantic finalization, remaining expression/declaration services, complete diagnostics and the remaining module dependencies. Production source traversal does not yet supply complete program checking. Component comparison counts and validation of the existing Go backend do not measure full C# checker completion. The original Go backend remains the product backend. The full checker completion gate and retained-platform release gates are unchanged.
