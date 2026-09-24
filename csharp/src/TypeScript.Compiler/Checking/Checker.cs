using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

// An exclusive checker owns semantic state for one immutable program. Services
// share that state; unsupported semantic forms fail explicitly during the port.
internal sealed partial class Checker : ITypeNodeHost, IDeclaredTypeHost, ITypeReferenceHost
{
    private readonly TypeContext context;
    private readonly CheckerLinks links;
    private readonly CheckerEnvironment program;
    internal TypeNodes Nodes { get; }
    internal DeclaredTypes Declared { get; }
    internal TypeReferences References { get; }
    internal InstantiationServices Instantiation { get; }
    internal TypeAlgebra Algebra { get; }
    internal List<int> Diagnostics { get; } = [];
    private readonly HashSet<(SyntaxNode, int)> reported = [];
    internal Action<SyntaxNode>? BeforeNode { get; set; }
    internal Action<Symbol>? BeforeMemberTable { get; set; }

    internal Checker(TypeContext context, CheckerLinks links, CheckerEnvironment program)
    {
        this.context = context;
        this.links = links;
        this.program = program;
        Algebra = new(context, new(program.Symbols.Program.SourceFiles.Select(f => f.Syntax).ToArray()), this);
        Instantiation = new(context, Algebra, links, this);
        Declared = new(context, links, program.Symbols, program.Scopes, program.Aliases, Algebra, Instantiation.Resolutions, this);
        References = new(context, links, program.Symbols, program.Scopes, Declared, program.EntityNames, program.Aliases,
            Algebra, Instantiation.Constraints, Instantiation.Engine, Instantiation.Objects, Instantiation.Resolutions, this);
        Nodes = new(context, links, program.Symbols, program.Scopes, Algebra, Instantiation.Tuples, Instantiation.Objects,
            Instantiation.Mapped, Instantiation.TypeNodeFlow, this);
        Signatures = new(context, links, program.Symbols, program.Scopes, Instantiation.Engine, Algebra, Instantiation.Resolutions, this);
        Bases = new(
            context,
            Algebra,
            Instantiation.Constraints,
            References,
            Instantiation.Tuples,
            Instantiation.Mapped,
            Instantiation.Resolutions,
            this);
        IndexSignatures = new(context, program.Symbols, Algebra, Instantiation.Members, this);
        Members = new(context, program.Symbols, program.Scopes, References, Instantiation.Engine, Signatures, program.Aliases,
            Instantiation.Members, new(program.Symbols.Program.SourceFiles.Select(f => f.Syntax).ToArray()), this);
        Values = new(context, links, program.Symbols, Declared, program.Aliases, program.AliasTargets, Algebra,
            Instantiation.Engine, Instantiation.Members, Instantiation.Resolutions, this);
        Properties = new(context, links, program.Symbols, program.Scopes, program.Aliases, Values, Algebra, this);
        Views = new(context, Algebra, Instantiation.Constraints, Instantiation.Engine, Instantiation.Mapped, Instantiation.Members, this);
        Composites = new(context, Algebra, Instantiation.Members, Signatures, this);
        Parameters = new(context, Algebra, Values, Instantiation.Tuples, this);
        SignatureComparison = new(context, Parameters, Signatures, Instantiation.Constraints, Instantiation.Engine, this);
        SignatureComposition = new(
            context,
            links,
            Algebra,
            Values,
            Properties,
            Instantiation.Tuples,
            Instantiation.Engine,
            Parameters,
            SignatureComparison,
            this);
        Normalization = new(context, Algebra, References, Instantiation.Tuples, Instantiation.Engine, Bases, Views, this);
        RelationKeys = new(context, References, Instantiation.Constraints);
        Relations = new(
            context,
            Normalization,
            Views,
            RelationKeys,
            new TypeRecursion(async (type, token) => await Instantiation.Members.ModifiersTypeAsync(type, token)),
            this);
        Identity = new(context, links, Members, Properties, Values, SignatureComparison, Instantiation.Mapped, this);
        ObjectRelations = new(
            context,
            Algebra,
            Members,
            Properties,
            Values,
            IndexSignatures,
            References,
            Instantiation.Tuples,
            Instantiation.Mapped,
            this);
        Structural = new(
            context,
            Algebra,
            Normalization,
            Views,
            Instantiation.Constraints,
            Properties,
            ObjectRelations,
            Instantiation.Mapped,
            this);
        RelationSupport = new(context, links, Algebra, Instantiation.Constraints, Normalization, Views, Properties, Declared, Bases, this);
        Variances = new(context, links, Declared, References, Instantiation.Engine, Instantiation.Constraints, Instantiation.Resolutions,
            new(program.Symbols.Program.SourceFiles.Select(f => f.Syntax).ToArray()), Relations.State, this);
        SignatureAssignability = new(context, Parameters, Signatures, Instantiation.Engine, this);
        Facts = new(context, Algebra, Instantiation.Constraints, Views, Members, this);
        Discriminants = new(context, Algebra, Properties, Values, Instantiation.Mapped, ObjectRelations, this);
        Templates = new(context, Algebra, Instantiation.Constraints, Relations);
        Keys = new(context, Algebra, Views, Instantiation.Engine, Instantiation.Mapped, Instantiation.Members, this);
        Indexed = new(context, Algebra, Views, Keys, Properties, Values, IndexSignatures, Relations,
            Instantiation.Engine, Instantiation.Constraints, Instantiation.Tuples, Instantiation.Mapped, Instantiation.Members, this);
        Generics = new(context, Algebra, Instantiation.Constraints, Keys, Indexed, Instantiation.Mapped,
            Instantiation.Members, Instantiation.Engine, Variances, Views, Bases, ArrayTarget);
        Conditionals = new(context, Algebra, Instantiation.Engine, Instantiation.Constraints,
            Instantiation.Mapped, Views, Relations, References.TypeArgumentsAsync, this);
        ConditionalRelations = new(context, Instantiation.Engine, Instantiation.Constraints, Instantiation.Objects, Relations,
            new TypeRecursion(async (type, token) => await Instantiation.Members.ModifiersTypeAsync(type, token)), this);
        InferredConstraints = new(context, Algebra, Instantiation.Constraints, Instantiation.Engine, Instantiation.Tuples, this);
        Widening = new(context, Algebra, Views, links, this);
        SignatureInstantiation = new(
            context,
            Instantiation.Engine,
            Instantiation.Constraints,
            Signatures,
            Members,
            program.Symbols,
            Relations);
        Inference = new(context, Algebra, Instantiation.Constraints, Instantiation.Engine, Relations,
            new TypeVariables(References.TypeArgumentsAsync),
            new TypeRecursion(async (type, token) => await Instantiation.Members.ModifiersTypeAsync(type, token)),
            Views, Instantiation.Mapped, Indexed, Keys, Instantiation.Tuples, IndexSignatures, Values, Parameters,
            Signatures,
            SignatureAssignability,
            SignatureInstantiation,
            Templates,
            Widening,
            new(program.Symbols.Program.SourceFiles.Select(f => f.Syntax).ToArray()),
            this);
        ReverseInference = new(context, Algebra, Instantiation.Engine, Instantiation.Mapped, Keys, Indexed, Instantiation.Tuples,
            new TypeRecursion(async (type, token) => await Instantiation.Members.ModifiersTypeAsync(type, token)),
            Inference,
            Widening,
            links,
            this);
        EnumValues = new(program.Symbols, program.EntityNames, links, this);
        Predicates = new(context, Instantiation.Constraints, Relations);
        ExpressionChecks = new(context, Facts, this);
        Expressions = new(context, Algebra, Facts, Relations, Instantiation.Engine, EnumValues.Evaluator, this);
        Variables = new(context, Algebra, Widening, program.Symbols, Signatures, this);
        WideningDiagnostics = new(program.Symbols, Widening, Views, Properties, Values, this, (node, code) => Error(node, code));
        Awaited = new(context, Algebra, Instantiation.Constraints, Properties, Values, Parameters, Relations,
            Instantiation.Mapped, Predicates, Views, Facts, this);
        Iterators = new(context, Algebra, Views, Facts, Properties, Values, Parameters, Signatures, Relations, Awaited, this);
        Iteration = new(context, Algebra, Iterators, Relations, Awaited, this);
        Binary = new(context, Algebra, Predicates, Facts, Widening, Relations, ExpressionChecks, EnumValues.Evaluator, this);
        Assignments = new(links, program.Symbols, program.ReferenceSymbols, program.EntityNames);
        FlowReferences = new(this);
        FlowTypes = new(context, Algebra, Widening, Facts, Relations, Predicates, this);
        FlowNarrowing = new(
            context,
            Algebra,
            Facts,
            Relations,
            Instantiation.Constraints,
            Views,
            Predicates,
            program.Symbols,
            FlowReferences,
            Discriminants,
            FlowTypes,
            this);
        FlowEffects = new(context, Views, Signatures, this);
        ExplicitValues = new(links, program.Symbols, program.ReferenceSymbols, program.Aliases, Values, Properties, this);
        MissingNames = new(program.Symbols, Values, Declared, Properties, (node, code, symbol) => Error(node, code));
        program.MissingPrefixCheck = (node, name) => MissingNames.CheckAsync(node, name);
        AliasReferences = new(program.Symbols, links, program.ReferenceSymbols, program.Aliases);
        ReferenceNarrowing = new(context, Algebra, Instantiation.Constraints, Predicates, Instantiation.Mapped, this);
        SymbolNarrowing = new(context, links, Values, Algebra, Instantiation.Constraints, Instantiation.Engine, Views, FlowTypes, this);
        AssignmentChecks = new(context, links, Predicates, this);
        RelationDiagnostics = new(context, Relations, Signatures, Properties, Values, Predicates, this);
        Optional = new(context, Algebra, Facts);
        AccessNames = new(program.EntityNames, program.DeclarationOrder, this);
        Bindings = new(context, links, program.Symbols, Algebra, Facts, Views, Instantiation.Constraints,
            Instantiation.Mapped, Properties, Values, Keys, Relations, Indexed, Instantiation.Tuples, Variables,
            AccessNames, FlowTypes, this);
        BindingPatterns = new(context, links, Algebra, Widening, Properties, Instantiation.Tuples, InferencePatterns, this);
        Contexts = new(context, Algebra, Views, Widening, Instantiation.Constraints, Instantiation.Engine, Predicates,
            Inference, Properties, Values, Instantiation.Tuples, BindingPatterns, InferencePatterns, this);
        ContextualProperties = new(context, links, Algebra, Properties, Values, Members, Instantiation.Mapped,
            Instantiation.Members, Indexed, IndexSignatures, Instantiation.Tuples, Instantiation.Constraints,
            Views, Relations, Instantiation.Resolutions);
        ArrayLiterals = new(context, Algebra, Contexts, Properties, Values, Instantiation.Mapped, Instantiation.Tuples, Indexed, this);
        ObjectSpreads = new(context, links, Algebra, Facts, Views, Properties, Values, Instantiation.Mapped, Bindings, this);
        ObjectLiterals = new(context, links, program.Symbols, Algebra, Views, Properties, Values, Relations, Predicates,
            Widening, Members, Contexts, Bindings, ObjectSpreads, InferencePatterns, this);
        TypeDiscrimination = new(context, Algebra, Views, Properties, Values, Relations, Discriminants, program.Symbols, Contexts, this);
        ExcessProperties = new(context, Algebra, Views, Properties, Values, Predicates, TypeDiscrimination, this);
        LateMembers = new(program.Symbols, links, this);
        program.LateMemberSymbol = symbol => LateMembers.SymbolAsync(symbol).GetAwaiter().GetResult();
        FunctionContexts = new(context, links, program.Symbols, Contexts, Algebra, Instantiation.Constraints, Instantiation.Engine,
            Relations, Inference, Signatures, Parameters, SignatureComparison, SignatureComposition, Values, Widening,
            Variables, Bindings, BindingPatterns, Awaited, Instantiation.Resolutions, this);
        Generators = new(context, Algebra, Iterators, Iteration, Awaited, Contexts, FunctionContexts, Relations, this);
        Yields = new(context, Algebra, Generators, Iterators, Contexts, Signatures, this);
        FunctionBodies = new(context, program.Symbols, Algebra, Views, Widening, Contexts, FunctionContexts, Signatures,
            Values, Awaited, FlowTypes, Assignments, this);
        FunctionWidening = new(FunctionContexts, Signatures, Awaited, Instantiation.Mapped, WideningDiagnostics, this);
        FunctionThis = new(program.Symbols, FunctionContexts, Contexts, Algebra, Values, Instantiation.Engine, Facts, Widening, this);
        AwaitExpressions = new(context, Awaited, this);
        TypeReferenceChecks = new(context, links, References, Declared, program.Scopes, Instantiation.Constraints,
            Instantiation.Engine, Relations, (node, code) => Error(node, code));
        IndexDeclarationChecks = new(program.Symbols, Nodes, Members, Properties, Values, IndexSignatures, Bases, Relations,
            PropertyNameTypeAsync, (node, code) => Error(node, code));
        program.CallSignature = async (symbol, token) => (await SignaturesAsync(
            await Values.GetAsync(symbol, token),
            false,
            token)).FirstOrDefault();
        Functions = new(context, links, program.Symbols, Values, Signatures, FunctionContexts, FunctionBodies, Contexts, Parameters,
            Instantiation.Engine, new TypeVariables(References.TypeArgumentsAsync), this);
        AccessFlow = new(context, Algebra, Values, Widening, ReferenceNarrowing, FlowTypes, this);
        MemberAccess = new(program.Symbols, links, program.ReferenceSymbols, Declared, Properties, Bases, program.DeclarationOrder, this);
        PropertyInitializers = new(context, this);
        program.StaticInitialization = PropertyInitializers.BeforeStaticUseAsync;
        IndexValidation = new(
            context,
            Keys,
            Instantiation.Mapped,
            Instantiation.Members,
            IndexSignatures,
            Relations,
            Views,
            Properties,
            this);
        ElementErrors = new(context, Algebra, program.Symbols, Properties, Values, this);
        MemberAccessibility = new(program.Symbols, links, Declared, Bases, Instantiation.Constraints, Properties, this);
        FunctionDeclarations = new(context, program.Symbols, program.Scopes, Instantiation.Constraints, Instantiation.Engine, Bases,
            Relations, Values, Variables, Bindings, Properties, MemberAccess, MemberAccessibility, this);
        PrivateAccess = new(context, program.Symbols, Properties, this);
        SymbolSuggestions = new(program.Aliases, new(program.Symbols.Program.SourceFiles.Select(f => f.Syntax)));
        ClassBases = new(
            context,
            Declared,
            References,
            Bases,
            Views,
            Instantiation.Constraints,
            Instantiation.Engine,
            SignatureInstantiation,
            Signatures,
            Parameters,
            Members,
            Instantiation.Resolutions,
            Relations,
            this);
        ThisExpressions = new(
            context,
            links,
            program.Symbols,
            Values,
            Declared,
            Signatures,
            Parameters,
            ClassBases,
            Bases,
            FlowTypes,
            this);
        Access = new(
            context,
            links,
            program.Symbols,
            program.Aliases,
            Algebra,
            Widening,
            Views,
            Properties,
            Values,
            IndexSignatures,
            Indexed,
            Instantiation.Mapped,
            Optional,
            AccessFlow,
            FlowTypes,
            this);
        Identifiers = new(context, links, program.Symbols, program.ReferenceSymbols, program.Aliases, Values, Algebra, Widening, Facts,
            Instantiation.Resolutions, Assignments, FlowTypes, this);
        GenericExpressions = new(context, Members, Signatures, SignatureInstantiation, Contexts, Facts, Inference);
        CallArguments = new(context, Algebra, Contexts, Inference, Predicates, Instantiation.Constraints, Widening,
            Instantiation.Tuples, Indexed, Optional, FlowTypes, this);
        CallSignatures = new(context, program.Symbols, Parameters, Instantiation.Constraints, Instantiation.Engine, Bases, Relations, this);
        CallInference = new(Inference, Instantiation.Constraints, Instantiation.Engine, Signatures, SignatureInstantiation,
            Parameters, new TypeVariables(References.TypeArgumentsAsync), Contexts, GenericExpressions, CallSignatures, CallArguments);
        CallResolution = new(context, links, Instantiation.Resolutions, Algebra, Views, ExpressionChecks, Optional,
            Contexts, ObjectLiterals, Relations, Signatures, Parameters, SignatureInstantiation, Inference,
            Instantiation.Constraints,
            Values,
            Widening,
            Instantiation.Tuples,
            CallSignatures,
            CallArguments,
            CallInference,
            FlowTypes,
            this);
        Calls = new(context, CallResolution, CallSignatures, Signatures, this);
        Assertions = new(context, Algebra, Widening, ObjectLiterals, Relations, RelationDiagnostics, this);
        TypeDisplay = new(context, Members, Instantiation.Constraints, References, Values, Signatures, Parameters, Nodes);
        ValueExpressions = new(context, links, program.Symbols, Values, Facts, this);
        InstantiationExpressions = new(context, Algebra, Instantiation.Constraints, Members, CallSignatures, SignatureInstantiation, this);
        ConstructorAccess = new(program.Symbols, Declared, Bases, Composites, this);
        BestMatchingTypes = new(TypeDiscrimination, Relations, Algebra, Keys, this, this);
        LiteralElaboration = new(context, program.Symbols, Algebra, Relations, RelationDiagnostics, Indexed, Keys,
            Properties, Values, Predicates, BestMatchingTypes, Contexts, ArrayLiterals, GenericExpressions, Signatures, this);
    }

    public ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation)
    {
        BeforeNode?.Invoke(node);
        return Nodes.FromNodeAsync(node, cancellation);
    }

    public ValueTask<Type> ReferenceAsync(SyntaxNode node, CancellationToken cancellation) => References.FromNodeAsync(node, cancellation);

    public ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference type, CancellationToken cancellation) =>
        References.TypeArgumentsAsync(type, cancellation);

    public ValueTask<bool> DeferredReferenceAsync(SyntaxNode node, bool defaults, CancellationToken cancellation) =>
        References.DeferredAsync(node, defaults, cancellation);

    public Symbol? AliasSymbol(SyntaxNode node) => Nodes.AliasSymbol(node);

    public ValueTask<TypeAlias?> AliasAsync(SyntaxNode node, CancellationToken cancellation) => ValueTask.FromResult(Nodes.Alias(node));

    public ValueTask<IReadOnlyList<Type>> OuterParametersAsync(SyntaxNode node, CancellationToken cancellation) =>
        program.Scopes.OuterAsync(node, cancellation: cancellation);

    public async ValueTask<Symbol?> ReferenceSymbolAsync(TypeReferenceNode node, CancellationToken cancellation) =>
        await References.SymbolAsync(node, cancellation);

    public ValueTask<Symbol> ValueSymbolAsync(IdentifierNode node, CancellationToken cancellation) =>
        ValueTask.FromResult(program.ReferenceSymbols.Resolve(node, cancellation));

    public ValueTask<TypeParameter> ParameterAsync(TypeParameterDeclarationNode node, CancellationToken cancellation) =>
        ValueTask.FromResult(program.Scopes.Parameter(program.Symbols.Declaration(node)!));

    public async ValueTask<MappedType> MappedNodeAsync(MappedTypeNode node, CancellationToken cancellation) =>
        (MappedType)await Nodes.WorkerAsync(node, cancellation);

    public Type ArrayTarget(bool isReadonly) => program.Globals.Types[isReadonly ? "ReadonlyArray" : "Array"];

    public ValueTask<bool> IdenticalAsync(Type first, Type second, CancellationToken cancellation)
            => Relations.RelatedAsync(first, second, RelationKind.Identity, cancellation);

    public ValueTask<Type> IndexAsync(Type type, CancellationToken cancellation) => Keys.GetAsync(type, cancellation: cancellation);

    public ValueTask<Type> IndexedAccessAsync(
        Type objectType,
        Type indexType,
        SyntaxNode node,
        TypeAlias? alias,
        CancellationToken cancellation)
            => Indexed.GetAsync(objectType, indexType, node: node, alias: alias, cancellation: cancellation);

    public ValueTask<IReadOnlyDictionary<string, Symbol>> MembersAsync(Symbol symbol, CancellationToken cancellation)
    {
        BeforeMemberTable?.Invoke(symbol);
        return LateMembers.TableAsync(symbol, cancellation: cancellation);
    }

    public async ValueTask<Type> TypeQueryAsync(TypeQueryNode node, CancellationToken cancellation)
    {
        if (node.TypeArguments is not null)
            return await Algebra.RegularTypeAsync(
                await Widening.GetAsync(await InstantiationExpressions.CheckAsync(node, cancellation), cancellation),
                cancellation);
        return await Algebra.RegularTypeAsync(
            await Widening.GetAsync(await Expressions.CheckAsync(node.ExprName!, cancellation: cancellation), cancellation),
            cancellation);
    }

    public ValueTask<Type> ConditionalAsync(ConditionalRoot root, CancellationToken cancellation) =>
        Conditionals.EvaluateAsync(root, cancellation: cancellation);

    public ValueTask<Type> ImportTypeAsync(ImportTypeNode node, CancellationToken cancellation) =>
        throw new InvalidOperationException("Checker requires import-type evaluation");

    public async ValueTask<Type> ConstAssertionAsync(SyntaxNode node, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var data = links.TypeNodes.Get(node);
        if (data.ResolvedType is { } cached)
            return cached;
        var type = await FlowTypes.StableAsync(() => Expressions.CheckAsync(node, cancellation: cancellation), cancellation);
        cancellation.ThrowIfCancellationRequested();
        Functions.RecordExpressionCache(node, type);
        return data.ResolvedType = type;
    }

    public ValueTask<Type?> IntendedJsDocTypeAsync(SyntaxNode node, CancellationToken cancellation)
            =>
                (node.Flags & NodeFlags.JSDoc) != 0
                    ? throw new InvalidOperationException("Checker requires JSDoc type interpretation")
                    : ValueTask.FromResult<Type?>(null);

    public void InvalidThisType(SyntaxNode node) => Error(node, 2526);

    public void CircularTypeAlias(Symbol symbol, TypeAliasDeclarationNode declaration) => Error(declaration, 2456);

    public void TypeArgumentCount(SyntaxNode node, Symbol symbol, Type type, int minimum, int maximum, bool missingAugments)
            => Error(node, missingAugments ? minimum == maximum ? 8026 : 8027 : minimum == maximum ? 2314 : 2707);

    public void NotGeneric(SyntaxNode node, Symbol symbol) => Error(node, 2315);

    public void CircularArguments(SyntaxNode? node, InterfaceType target) => Error(node!, target.Symbol is null ? 4110 : 4109);

    private void Error(SyntaxNode node, int code)
    {
        if (reported.Add((node, code)))
            Diagnostics.Add(code);
    }

    public ValueTask<Type> LiteralExpressionAsync(SyntaxNode expression, CancellationToken cancellation)
        => Expressions.CheckAsync(expression, cancellation: cancellation);

    public bool IsDynamicEnumName(EnumMemberNode member) => TypeScript.Compiler.Checking.EnumValues.DynamicName(member.Name!);

    public async ValueTask<object?> EnumValueAsync(EnumMemberNode member, CancellationToken cancellation)
        => (await EnumValues.GetAsync(member, cancellation)).Value;
}
