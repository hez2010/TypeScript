using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

// Source-backed member algorithms with explicitly annotated value dependencies.
// Body inference, computed names, class bases and composite members are not fixtures.
internal sealed partial class ProgramTypeHost : ISignatureHost, IStructuredMemberHost, IBaseTypeHost, IIndexSignatureHost
{
    internal Signatures Signatures { get; }
    internal BaseTypes Bases { get; }
    internal StructuredMembers Members { get; }
    internal IndexSignatures IndexSignatures { get; }
    internal Func<SyntaxNode, CancellationToken, ValueTask<Type>>? ReturnBody { get; set; }
    internal Func<SyntaxNode, CancellationToken, ValueTask<TypePredicate?>>? PredicateBody { get; set; }

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
            (signature.Flags & SignatureFlags.HasRestParameter) == 0 ? ValueTask.FromResult(signature.Parameters.Count)
                : throw new InvalidOperationException("Probe requires expanded rest parameter count");

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

    public async ValueTask<IReadOnlyList<Symbol>> PropertiesAsync(Type type, CancellationToken cancellation) =>
            type is StructuredType structured ? (await Members.ResolveAsync(structured, cancellation)).Properties ?? [] : [];

    public async ValueTask<IReadOnlyList<Signature>> SignaturesAsync(Type type, bool construct, CancellationToken cancellation)
    {
        if (type is not StructuredType structured)
            return [];
        await Members.ResolveAsync(structured, cancellation);
        return (construct ? structured.ConstructSignatures : structured.CallSignatures) ?? [];
    }

    public async ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation) =>
            type is StructuredType structured ? (await Members.ResolveAsync(structured, cancellation)).IndexInfos ?? [] : [];

    public ValueTask<Type> BaseConstructorAsync(InterfaceType type, CancellationToken cancellation) =>
            throw new InvalidOperationException("Probe requires class base constructor checking");

    public ValueTask<IReadOnlyList<Signature>> DefaultConstructorsAsync(InterfaceType type, CancellationToken cancellation) =>
            throw new InvalidOperationException("Probe requires default constructors");

    public ValueTask<Type> DeclaredTypeAsync(Symbol symbol, CancellationToken cancellation) => Declared.GetAsync(symbol, cancellation);

    public ValueTask ResolveUnionAsync(UnionType type, CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires union members");

    public ValueTask ResolveIntersectionAsync(IntersectionType type, CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires intersection members");

    public ValueTask ResolveReverseMappedAsync(ReverseMappedType type, CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires reverse mapped members");

    public ValueTask<Type> ReducedAsync(Type type, CancellationToken cancellation) =>
        type is UnionOrIntersectionType ? throw new InvalidOperationException("Probe requires type reduction") : ValueTask.FromResult(type);

    public ValueTask<Type> ApparentAsync(Type type, CancellationToken cancellation) => Instantiation.ApparentTypeAsync(type, cancellation);

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

    public async ValueTask<Type> SymbolTypeAsync(Symbol symbol, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var data = links.Values.Get(symbol);
        if (data.ResolvedType is { } cached)
            return cached;
        Type result;
        if ((symbol.CheckFlags & CheckFlags.Instantiated) != 0)
            result = (await Instantiation.Engine.InstantiateAsync(
                await SymbolTypeAsync(data.Target!, cancellation),
                data.Mapper,
                cancellation: cancellation))!;
        else if ((symbol.CheckFlags & CheckFlags.Mapped) != 0)
            result = await Instantiation.Members.SymbolTypeAsync(symbol, cancellation);
        else if ((symbol.Flags & (SymbolFlags.Function | SymbolFlags.Method)) != 0)
            result = context.NewObjectType(ObjectFlags.Anonymous, symbol);
        else if (symbol.ValueDeclaration is ITypedNode { Type: { } annotation } declaration)
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
        return data.ResolvedType ??= result;
    }
}
