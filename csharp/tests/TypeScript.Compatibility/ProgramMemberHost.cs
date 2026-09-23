using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

// Source-backed member algorithms with explicitly annotated value dependencies.
// Body inference, computed names, class bases and composite members are not fixtures.
internal sealed partial class ProgramTypeHost : ISignatureHost, IStructuredMemberHost, IBaseTypeHost, IIndexSignatureHost, ISymbolTypeHost
{
    internal Signatures Signatures { get; }
    internal BaseTypes Bases { get; }
    internal StructuredMembers Members { get; }
    internal IndexSignatures IndexSignatures { get; }
    internal SymbolTypes Values { get; }
    internal Func<SyntaxNode, CancellationToken, ValueTask<Type>>? ReturnBody { get; set; }
    internal Func<SyntaxNode, CancellationToken, ValueTask<TypePredicate?>>? PredicateBody { get; set; }
    internal Func<Symbol, bool, CancellationToken, ValueTask<Type>>? VariableBody { get; set; }
    internal Func<Symbol, bool>? SensitiveParameter { get; set; }

    public ValueTask<Signature?> FullSignatureAsync(SyntaxNode declaration, CancellationToken cancellation) =>
        (declaration.Flags & NodeFlags.JavaScriptFile) != 0
            ? throw new InvalidOperationException("Probe requires JSDoc signatures") : ValueTask.FromResult<Signature?>(null);

    public ValueTask<Type?> ContextualTypeAsync(SyntaxNode declaration, CancellationToken cancellation) =>
            throw new InvalidOperationException("Probe requires contextual signatures");

    public ValueTask<bool> BindableNameAsync(SyntaxNode node, CancellationToken cancellation) =>
            (node as INamedNode)?.Name is ComputedPropertyNameNode
                ? throw new InvalidOperationException("Probe requires computed names") : ValueTask.FromResult(true);

    public ValueTask<Type> ReturnFromBodyAsync(SyntaxNode declaration, CancellationToken cancellation) =>
            ReturnBody?.Invoke(declaration, cancellation) ?? throw new InvalidOperationException("Probe requires return inference");

    public ValueTask<TypePredicate?> PredicateFromBodyAsync(SyntaxNode declaration, CancellationToken cancellation) =>
            PredicateBody?.Invoke(
                declaration,
                cancellation) ?? (SemanticSyntax.Body(declaration) is null ? ValueTask.FromResult<TypePredicate?>(null)
                : throw new InvalidOperationException("Probe requires predicate inference"));

    public ValueTask<int> ParameterCountAsync(Signature signature, CancellationToken cancellation) =>
        Parameters.CountAsync(signature, cancellation);

    public void CircularReturn(Signature signature) => Error(signature.Declaration!, 2577);

    public ValueTask<IReadOnlyDictionary<string, Symbol>> ExportsAsync(Symbol symbol, CancellationToken cancellation) =>
        (symbol.Flags & SymbolFlags.Module) != 0
            ? program.ModuleExports.ResolveAsync(symbol, cancellation)
            : ValueTask.FromResult(symbol.Exports);

    public ValueTask<IReadOnlyList<IndexInfo>> IndexInfosAsync(
        Symbol symbol,
        IReadOnlyList<Symbol> siblings,
        CancellationToken cancellation) =>
            IndexSignatures.ResolveAsync(symbol, siblings, cancellation);

    public ValueTask<IReadOnlyList<Type>> BaseTypesAsync(InterfaceType type, CancellationToken cancellation) =>
        Bases.GetAsync(type, cancellation);

    public ValueTask<Type> WithThisAsync(Type type, Type argument, CancellationToken cancellation) =>
        Bases.WithThisAsync(type, argument, cancellation: cancellation);

    public ValueTask<IReadOnlyList<Symbol>> PropertiesAsync(Type type, CancellationToken cancellation) =>
        Properties.GetAsync(type, cancellation);

    public async ValueTask<IReadOnlyList<Signature>> SignaturesAsync(Type type, bool construct, CancellationToken cancellation)
    {
        type = await Views.ReducedApparentAsync(type, cancellation);
        if (type is not StructuredType structured)
            return [];
        await Members.ResolveAsync(structured, cancellation);
        return (construct ? structured.ConstructSignatures : structured.CallSignatures) ?? [];
    }

    public async ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation)
    {
        type = await Views.ReducedApparentAsync(type, cancellation);
        return type is StructuredType structured ? (await Members.ResolveAsync(structured, cancellation)).IndexInfos : [];
    }

    public ValueTask<Type> BaseConstructorAsync(InterfaceType type, CancellationToken cancellation)
    {
        if (type.Symbol!.Declarations.OfType<ClassDeclarationNode>().Any(c => c.HeritageClauses is { Count: > 0 }))
            throw new InvalidOperationException("Probe requires class base constructor checking");
        return ValueTask.FromResult(type.ResolvedBaseConstructorType ??= context.UndefinedType);
    }

    public async ValueTask<IReadOnlyList<Signature>> DefaultConstructorsAsync(InterfaceType type, CancellationToken cancellation)
    {
        if (await BaseConstructorAsync(type, cancellation) != context.UndefinedType)
            throw new InvalidOperationException("Probe requires inherited constructors");
        var declaration = type.Symbol!.Declarations.OfType<ClassDeclarationNode>().FirstOrDefault();
        var flags = SignatureFlags.Construct | (declaration is not null
            && SemanticSyntax.HasModifier(declaration, SyntaxKind.AbstractKeyword)
            ? SignatureFlags.Abstract
            : 0);
        return [context.NewSignature(flags, null, type.AllTypeParameters.Skip(type.OuterTypeParameterCount)
            .Take(type.AllTypeParameters.Count - type.OuterTypeParameterCount - 1).Cast<TypeParameter>().ToArray(),
            null,
            [],
            type,
            null,
            0)];
    }

    public ValueTask<Type> DeclaredTypeAsync(Symbol symbol, CancellationToken cancellation) => Declared.GetAsync(symbol, cancellation);

    public ValueTask ResolveUnionAsync(UnionType type, CancellationToken cancellation) => Composites.UnionAsync(type, cancellation);

    public ValueTask ResolveIntersectionAsync(IntersectionType type, CancellationToken cancellation) =>
        Composites.IntersectionAsync(type, cancellation);

    public ValueTask ResolveReverseMappedAsync(ReverseMappedType type, CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires reverse mapped members");

    public ValueTask<Type> ReducedAsync(Type type, CancellationToken cancellation) => Views.ReducedAsync(type, cancellation);

    public ValueTask<Type> ApparentAsync(Type type, CancellationToken cancellation) => Views.ApparentAsync(type, cancellation);

    public ValueTask<IReadOnlyList<Type>> ClassBasesAsync(InterfaceType type, CancellationToken cancellation) =>
            type.Symbol!.Declarations.OfType<ClassDeclarationNode>().Any(c => c.HeritageClauses is { Count: > 0 })
                ? throw new InvalidOperationException("Probe requires class bases") : ValueTask.FromResult<IReadOnlyList<Type>>([]);

    public ValueTask<Type> IndexedAccessAsync(Type objectType, Type indexType, CancellationToken cancellation) =>
            Instantiation.IndexedAccessAsync(objectType, indexType, 0, null, cancellation);

    public void CircularBase(SyntaxNode declaration, Type type) => Error(declaration, 2310);

    public void InvalidInterfaceBase(SyntaxNode declaration) => Error(declaration, 2312);

    public ValueTask<bool> LateIndexAsync(SyntaxNode declaration, CancellationToken cancellation) =>
            throw new InvalidOperationException("Probe requires late index binding");

    public ValueTask<Type> ComputedKeyAsync(SyntaxNode declaration, CancellationToken cancellation) =>
            throw new InvalidOperationException("Probe requires computed keys");

    public ValueTask<bool> AssignableAsync(Type source, Type target, CancellationToken cancellation) =>
        Instantiation.IsAssignableAsync(source, target, cancellation);

    public ValueTask<bool> SymbolNameAsync(Symbol symbol, CancellationToken cancellation) =>
            throw new InvalidOperationException("Probe requires symbol name evaluation");

    public ValueTask<bool> NumericNameAsync(Symbol symbol, CancellationToken cancellation) =>
            throw new InvalidOperationException("Probe requires numeric name evaluation");

    public ValueTask<Type> SymbolTypeAsync(Symbol symbol, CancellationToken cancellation) => Values.GetAsync(symbol, cancellation);

    public async ValueTask<Type> VariableAsync(Symbol symbol, bool reportErrors, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (VariableBody is not null)
            return await VariableBody(symbol, reportErrors, cancellation);
        Type result;
        if (symbol.ValueDeclaration is ITypedNode { Type: { } annotation } declaration)
        {
            result = await TypeFromNodeAsync(annotation, cancellation);
            if (context.StrictNullChecks && ((symbol.Flags & SymbolFlags.Optional) != 0
                || declaration is ParameterDeclarationNode { QuestionToken: not null }))
                result = await Algebra.UnionAsync(
                    [result, declaration is ParameterDeclarationNode ? context.UndefinedType : context.UndefinedOrMissingType],
                    cancellation: cancellation);
        }
        else
            throw new InvalidOperationException($"Probe requires value inference for {symbol.Name}");
        cancellation.ThrowIfCancellationRequested();
        return result;
    }

    public bool ContextSensitiveParameter(Symbol symbol)
    {
        if (SensitiveParameter is not null)
            return SensitiveParameter(symbol);
        if (symbol.ValueDeclaration is ParameterDeclarationNode parameter && parameter.Type is null
            && parameter.Parent is ArrowFunctionNode or FunctionExpressionNode)
            throw new InvalidOperationException("Probe requires contextual parameter analysis");
        return false;
    }

    public ValueTask<Type> ReverseMappedAsync(Symbol symbol, CancellationToken cancellation) =>
            throw new InvalidOperationException("Probe requires reverse mapped symbol types");

    public Type CircularSymbol(Symbol symbol)
    {
        Error(symbol.ValueDeclaration!, symbol.ValueDeclaration is ITypedNode { Type: not null } ? 2502 : 7022);
        return context.AnyType;
    }

    public void ImplicitAccessor(Symbol symbol, SyntaxNode declaration)
    {
        if ((declaration.Flags & NodeFlags.Ambient) != 0 && (SemanticSyntax.HasModifier(declaration, SyntaxKind.PrivateKeyword)
            || (declaration as INamedNode)?.Name is PrivateIdentifierNode))
            return;
        if (program.Symbols.Program.Configuration.Options.StrictOption("noImplicitAny"))
            Error(declaration, declaration is SetAccessorDeclarationNode ? 7032 : declaration is GetAccessorDeclarationNode ? 7033 : 7008);
    }

    public void CircularAccessor(Symbol symbol, SyntaxNode? annotation, SyntaxNode? getter)
    {
        if (annotation is not null)
            Error(annotation, 2502);
        else if (getter is not null && program.Symbols.Program.Configuration.Options.StrictOption("noImplicitAny"))
            Error(getter, 7023);
    }
}
