using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : ITypeInferenceHost, IInferredConstraintHost, ITypeWideningHost, IReverseMappedInferenceHost
{
    internal TypeInference Inference { get; }
    internal InferredConstraints InferredConstraints { get; }
    internal TypeWidening Widening { get; }
    internal ReverseMappedInference ReverseInference { get; }
    internal SignatureInstantiation SignatureInstantiation { get; }
    internal HashSet<SyntaxNode> SkippedInferenceNodes { get; } = [];
    internal Dictionary<Type, SyntaxNode> InferencePatterns { get; } = [];

    public bool SkipDirectInference(SyntaxNode node) => SkippedInferenceNodes.Contains(node);

    public bool HasInferencePattern(Type type) => InferencePatterns.ContainsKey(type);

    public ValueTask<Type> CovariantInferenceAsync(InferenceInfo inference, Signature signature, CancellationToken cancellation)
            => Inference.CovariantAsync(inference, signature, cancellation);

    public ValueTask<Symbol?> ObjectPropertyAsync(Type type, Utf8String name, CancellationToken cancellation)
            => Properties.ObjectPropertyAsync(type, name, cancellation);

    public async ValueTask<Type> EnumBaseAsync(Type type, CancellationToken cancellation)
            => (type.Flags & TypeFlags.EnumLike) != 0 && type.Symbol is { Flags: var flags, Parent: { } parent }
                && (flags & SymbolFlags.EnumMember) != 0 ? await Declared.GetAsync(parent, cancellation) : type;

    public ValueTask<Type?> IntraContextualTypeAsync(SyntaxNode node, CancellationToken cancellation)
            => Contexts.GetAsync(node, cancellation: cancellation);

    public ValueTask<Type?> ReverseMappedInferenceAsync(
        Type source,
        MappedType target,
        IndexType constraint,
        CancellationToken cancellation)
            => ReverseInference.HomomorphicAsync(source, target, constraint, cancellation);

    public ValueTask<bool> InferableIndexAsync(Type type, CancellationToken cancellation) =>
        ObjectRelations.InferableIndexAsync(type, cancellation);

    public ValueTask<IReadOnlyList<VarianceFlags>> InferenceVariancesAsync(TypeReference target, CancellationToken cancellation)
            => Variances.OfTypeAsync((InterfaceType)target, cancellation);

    public async ValueTask<IReadOnlyList<Symbol>> ObjectPropertiesAsync(Type type, CancellationToken cancellation)
            => type is ObjectType obj ? (await Members.ResolveAsync(obj, cancellation)).Properties ?? [] : [];

    public async ValueTask<(IReadOnlyList<Type> Source, IReadOnlyList<Type> Target, IReadOnlyList<VarianceFlags> Variances)> AliasInferenceArgumentsAsync(
        TypeAlias source, TypeAlias target, CancellationToken cancellation)
    {
        await Declared.GetAsync(source.Symbol, cancellation);
        var parameters = links.TypeAliases.Get(source.Symbol).TypeParameters!;
        bool js = ((source.Symbol.ValueDeclaration?.Flags ?? 0) & NodeFlags.JavaScriptFile) != 0;
        var left = await Instantiation.Constraints.FillMissingArgumentsAsync(
            source.TypeArguments,
            parameters,
            js,
            IdenticalAsync,
            cancellation);
        var right = await Instantiation.Constraints.FillMissingArgumentsAsync(
            target.TypeArguments,
            parameters,
            js,
            IdenticalAsync,
            cancellation);
        return (left, right, await Variances.GetAsync(source.Symbol, parameters, cancellation));
    }

    public async ValueTask<IReadOnlyList<TypeParameter>> ReferenceParametersAsync(TypeReferenceNode node, CancellationToken cancellation)
    {
        var type = await Nodes.FromNodeAsync(node, cancellation);
        if (type == context.ErrorType)
            return [];
        var symbol = await References.SymbolAsync(node, cancellation);
        if ((symbol.Flags & SymbolFlags.TypeAlias) != 0 && links.TypeAliases.Get(symbol).TypeParameters is { Count: > 0 } parameters)
            return parameters;
        if (type is TypeReference { Target: InterfaceType target })
            return target.AllTypeParameters.Skip(target.OuterTypeParameterCount)
                .Take(
                    target.AllTypeParameters.Count - target.OuterTypeParameterCount - (target.ThisType is null
                        ? 0
                        : 1)).Cast<TypeParameter>().ToArray();
        return [];
    }

    public async ValueTask<Type> EffectiveArgumentAsync(
        TypeReferenceNode node,
        IReadOnlyList<TypeParameter> parameters,
        int index,
        CancellationToken cancellation)
        => index < (node.TypeArguments?.Count ?? 0) ? await Nodes.FromNodeAsync(node.TypeArguments![index], cancellation)
            : (await References.EffectiveArgumentsAsync(node, parameters, cancellation))[index];

    public async ValueTask<bool> MutableArrayLikeAsync(Type type, CancellationToken cancellation)
        => IsArray(type) && !IsReadonlyArray(type) || type is TypeReference { Target: TupleType { IsReadonly: false } }
            || (type.Flags & (TypeFlags.Any | TypeFlags.Nullable)) == 0 && await AssignableAsync(type, AnyArray, cancellation);

    public ValueTask<bool> ConstTypeVariableAsync(Type type, CancellationToken cancellation) =>
        Inference.ConstVariableAsync(type, cancellation);

    public ValueTask<bool> DefinitelyUnrelatedAsync(Type source, Type target, CancellationToken cancellation)
            => Inference.DefinitelyUnrelatedAsync(source, target, cancellation);

    public ValueTask<Type> EmptyInferenceObjectAsync(Type type, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var members = new Dictionary<Utf8String, Symbol>();
        foreach (var part in type is UnionType union ? union.Types : (IReadOnlyList<Type>)[type])
            if (part is LiteralType { Value: Utf8String })
            {
                Utf8String name = MappedMembers.PropertyName(part);
                var property = new Symbol(SymbolFlags.Property | SymbolFlags.Transient, name);
                links.Values.Get(property).ResolvedType = context.AnyType;
                if (part.Symbol is { } symbol)
                {
                    property.DeclarationList = property.DeclarationList.AddRange(symbol.Declarations);
                    property.ValueDeclaration = symbol.ValueDeclaration;
                }
                members[name] = property;
            }
        var result = context.NewObjectType(ObjectFlags.Anonymous);
        result.Members = members.AsReadOnly();
        result.Properties = members.Values.ToArray();
        result.CallSignatures = [];
        result.ConstructSignatures = [];
        result.IndexInfos = (type.Flags & TypeFlags.String) != 0
            ? [context.NewIndexInfo(context.StringType, context.EmptyObjectType, false)]
            : [];
        result.ObjectFlags |= ObjectFlags.MembersResolved;
        return ValueTask.FromResult<Type>(result);
    }
}
