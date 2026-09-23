using System.Globalization;
using System.Numerics;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

// AST evaluation is production code. Relations, value checking and conditional
// evaluation remain explicit fixture dependencies; unsupported queries fail.
internal sealed partial class ProgramTypeHost : ITypeNodeHost, IDeclaredTypeHost, ITypeReferenceHost, IInstantiationFixtureSource
{
    private readonly TypeContext context;
    private readonly CheckerLinks links;
    private readonly ProgramScopeHost program;
    private readonly AlgebraFixtureHost relations;
    private readonly Dictionary<EnumMemberNode, object?> enumValues = [];
    internal TypeNodes Nodes { get; }
    internal DeclaredTypes Declared { get; }
    internal TypeReferences References { get; }
    internal InstantiationFixtureHost Instantiation { get; }
    internal TypeAlgebra Algebra { get; }
    internal List<int> Diagnostics { get; } = [];
    internal IReadOnlyList<int> AlgebraDiagnostics => relations.Diagnostics;
    private readonly HashSet<(SyntaxNode, int)> reported = [];
    internal Action<SyntaxNode>? BeforeNode { get; set; }
    internal Action<Symbol>? BeforeMemberTable { get; set; }

    internal ProgramTypeHost(TypeContext context, CheckerLinks links, ProgramScopeHost program)
    {
        this.context = context;
        this.links = links;
        this.program = program;
        relations = new(context);
        Algebra = new(context, new(program.Symbols.Program.SourceFiles.Select(f => f.Syntax).ToArray()), relations);
        Instantiation = new(context, Algebra, links, relations, this);
        relations.ResolveBaseConstraint = Instantiation.Constraints.BaseConstraintAsync;
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
        relations.EmptyAnonymousSource = Views.EmptyAnonymousAsync;
        relations.EmptyObjectSource = Views.EmptyObjectAsync;
        relations.PropertiesSource = Properties.GetAsync;
        relations.SymbolTypeSource = Values.GetAsync;
        relations.PropertyTypeSource = async (type, name, cancellation) =>
            await Properties.PropertyAsync(type, name, cancellation: cancellation) is { } property
                ? await Values.GetAsync(property, cancellation)
                : null;
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
        throw new InvalidOperationException("Probe requires value symbol resolution");

    public ValueTask<TypeParameter> ParameterAsync(TypeParameterDeclarationNode node, CancellationToken cancellation) =>
        ValueTask.FromResult(program.Scopes.Parameter(program.Symbols.Declaration(node)!));

    public async ValueTask<MappedType> MappedNodeAsync(MappedTypeNode node, CancellationToken cancellation) =>
        (MappedType)await Nodes.WorkerAsync(node, cancellation);

    public Type ArrayTarget(bool isReadonly) => program.Globals.Types[isReadonly ? "ReadonlyArray" : "Array"];

    public ValueTask<bool> IdenticalAsync(Type first, Type second, CancellationToken cancellation)
            => Relations.RelatedAsync(first, second, RelationKind.Identity, cancellation);

    public ValueTask<Type> IndexAsync(Type type, CancellationToken cancellation) => Instantiation.IndexTypeAsync(type, cancellation);

    public ValueTask<Type> IndexedAccessAsync(
        Type objectType,
        Type indexType,
        SyntaxNode node,
        TypeAlias? alias,
        CancellationToken cancellation)
            => Instantiation.IndexedAccessAsync(objectType, indexType, 0, alias, cancellation);

    public ValueTask<IReadOnlyDictionary<string, Symbol>> MembersAsync(Symbol symbol, CancellationToken cancellation)
    {
        BeforeMemberTable?.Invoke(symbol);
        if (symbol.Members.ContainsKey(Symbol.InternalPrefix + "computed"))
            throw new InvalidOperationException("Probe requires computed member evaluation");
        return ValueTask.FromResult(symbol.Members);
    }

    public ValueTask<Type> TypeQueryAsync(TypeQueryNode node, CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires value/type-query checking");

    public ValueTask<Type> ConditionalAsync(ConditionalRoot root, CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires conditional type evaluation");

    public ValueTask<Type> ImportTypeAsync(ImportTypeNode node, CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires import-type evaluation");

    public ValueTask<Type> ConstAssertionAsync(SyntaxNode node, CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires const assertion checking");

    public ValueTask<Type?> IntendedJsDocTypeAsync(SyntaxNode node, CancellationToken cancellation)
            =>
                (node.Flags & NodeFlags.JSDoc) != 0
                    ? throw new InvalidOperationException("Probe requires JSDoc type interpretation")
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

    public async ValueTask<Type> LiteralExpressionAsync(SyntaxNode expression, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (expression is PrefixUnaryExpressionNode { Operand: NumericLiteralNode or BigIntLiteralNode } unary)
            await LiteralExpressionAsync(unary.Operand!, cancellation).ConfigureAwait(false);
        Type result = expression switch
        {
            StringLiteralNode literal => context.GetStringLiteralType(literal.Text),
            NoSubstitutionTemplateLiteralNode literal => context.GetStringLiteralType(literal.Text),
            NumericLiteralNode literal => context.GetNumberLiteralType(double.Parse(literal.Text, CultureInfo.InvariantCulture)),
            BigIntLiteralNode literal => context.GetBigIntLiteralType(BigInt(literal.Text)),
            { Kind: SyntaxKind.TrueKeyword } => context.RegularTrueType,
            { Kind: SyntaxKind.FalseKeyword } => context.RegularFalseType,
            PrefixUnaryExpressionNode { Operator: SyntaxKind.MinusToken, Operand: NumericLiteralNode literal }
                => context.GetNumberLiteralType(-double.Parse(literal.Text, CultureInfo.InvariantCulture)),
            PrefixUnaryExpressionNode { Operator: SyntaxKind.MinusToken, Operand: BigIntLiteralNode literal }
                => context.GetBigIntLiteralType(-BigInt(literal.Text)),
            _ => throw new InvalidOperationException("Probe requires non-literal expression checking")
        };
        return context.GetFreshLiteralType((LiteralType)result);
    }

    private static BigInteger BigInt(string text)
    {
        text = text.TrimEnd('n');
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? BigInteger.Parse("0" + text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : BigInteger.Parse(text, CultureInfo.InvariantCulture);
    }

    public bool IsDynamicEnumName(EnumMemberNode member) => member.Name is ComputedPropertyNameNode;

    public ValueTask<object?> EnumValueAsync(EnumMemberNode member, CancellationToken cancellation)
    {
        if (!enumValues.ContainsKey(member))
        {
            double next = 0;
            foreach (var item in ((EnumDeclarationNode)member.Parent!).Members!.Cast<EnumMemberNode>())
            {
                cancellation.ThrowIfCancellationRequested();
                object? value = item.Initializer switch
                {
                    null => next,
                    StringLiteralNode literal => literal.Text,
                    NumericLiteralNode literal => double.Parse(literal.Text, CultureInfo.InvariantCulture),
                    PrefixUnaryExpressionNode { Operator: SyntaxKind.MinusToken, Operand: NumericLiteralNode literal }
                        => -double.Parse(literal.Text, CultureInfo.InvariantCulture),
                    _ => throw new InvalidOperationException("Probe requires computed enum values")
                };
                if (value is not double and not string)
                    throw new InvalidOperationException("Probe requires computed enum values");
                enumValues[item] = value;
                next = value is double number ? number + 1 : double.NaN;
                if (item.Initializer is null && double.IsNaN(next))
                    throw new InvalidOperationException("Probe requires invalid enum diagnostics");
            }
        }
        return ValueTask.FromResult(enumValues[member]);
    }
}
