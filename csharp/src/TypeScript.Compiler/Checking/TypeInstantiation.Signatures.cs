using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed partial class TypeInstantiation
{
    internal TypeParameter CloneParameter(TypeParameter parameter)
    {
        context.RequireOwned(parameter);
        return new(context, parameter.Symbol) { Target = parameter };
    }

    internal async ValueTask<Signature> SignatureAsync(Signature signature, TypeMapper mapper, bool eraseTypeParameters,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (signature.Context != context)
            throw new ArgumentException("Signature belongs to a different checker context", nameof(signature));
        TypeParameter[] parameters = [];
        if (signature.TypeParameters.Count != 0 && !eraseTypeParameters)
        {
            parameters = signature.TypeParameters.Select(CloneParameter).ToArray();
            mapper = Combine(TypeMapper.Create(signature.TypeParameters.ToArray(), parameters), mapper);
            foreach (var parameter in parameters)
                parameter.Mapper = mapper;
        }
        var thisParameter = await SymbolAsync(signature.ThisParameter, mapper, cancellation).ConfigureAwait(false);
        var valueParameters = new Symbol[signature.Parameters.Count];
        for (int i = 0; i < valueParameters.Length; i++)
            valueParameters[i] = await SymbolAsync(signature.Parameters[i], mapper, cancellation).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Signature parameter instantiation returned no symbol");
        var result = context.NewSignature(signature.Flags & SignatureFlags.PropagatingFlags, signature.Declaration,
            parameters, thisParameter, valueParameters, null, null, signature.MinArgumentCount);
        result.Target = signature;
        result.Mapper = mapper;
        return result;
    }

    internal async ValueTask<IndexInfo> IndexInfoAsync(IndexInfo info, TypeMapper mapper, CancellationToken cancellation = default)
    {
        var value = await RequiredAsync(info.ValueType, mapper, cancellation).ConfigureAwait(false);
        return value == info.ValueType
            ? info
            : context.NewIndexInfo(info.KeyType, value, info.IsReadonly, info.Declaration, info.Components.ToArray());
    }

    internal async ValueTask<Symbol?> SymbolAsync(Symbol? symbol, TypeMapper mapper, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (symbol is null)
            return null;
        var data = links.Values.Get(symbol);
        if (mapper.MapsThisOnly && Thisless(symbol))
            return symbol;
        if (data.ResolvedType is { } type && !await variables.CouldContainAsync(type, cancellation).ConfigureAwait(false))
        {
            if ((symbol.Flags & SymbolFlags.SetAccessor) == 0)
                return symbol;
            if (data.WriteType is { } write && !await variables.CouldContainAsync(write, cancellation).ConfigureAwait(false))
                return symbol;
        }
        if ((symbol.CheckFlags & CheckFlags.Instantiated) != 0)
        {
            symbol = data.Target ?? throw new InvalidOperationException("Instantiated symbol has no target");
            mapper = Combine(data.Mapper, mapper);
        }
        var result = new Symbol(symbol.Flags | SymbolFlags.Transient, symbol.Name)
        {
            CheckFlags = CheckFlags.Instantiated | symbol.CheckFlags & (CheckFlags.Readonly | CheckFlags.Late | CheckFlags.OptionalParameter | CheckFlags.RestParameter),
            Parent = symbol.Parent,
            ValueDeclaration = symbol.ValueDeclaration
        };
        result.DeclarationList = result.DeclarationList.AddRange(symbol.Declarations);
        var resultLinks = links.Values.Get(result);
        resultLinks.Target = symbol;
        resultLinks.Mapper = mapper;
        resultLinks.NameType = data.NameType;
        return result;
    }

    internal static bool Thisless(Symbol symbol)
    {
        if (symbol.Declarations is not [var declaration])
            return false;
        switch (declaration.Kind)
        {
            case K.Parameter:
            case K.PropertyDeclaration:
            case K.PropertySignature:
                return ThislessVariable(declaration);
            case K.MethodDeclaration:
            case K.MethodSignature:
            case K.Constructor:
            case K.GetAccessor:
            case K.SetAccessor:
                if (declaration is not IFunctionSignature signature)
                    throw new InvalidOperationException("Function declaration has no signature data");
                return (declaration is ConstructorDeclarationNode || signature.Type is { } returnType && ThislessType(returnType))
                    && (signature.Parameters?.All(ThislessVariable) ?? true)
                    && (signature.TypeParameters?.All(n => n is TypeParameterDeclarationNode parameter
                        && (parameter.Constraint is null || ThislessType(parameter.Constraint))) ?? true);
            default:
                return false;
        }
    }

    private static bool ThislessVariable(SyntaxNode node) => node is ITypedNode { Type: { } type }
        ? ThislessType(type) : node is not IInitializedNode { Initializer: not null };

    private static bool ThislessType(SyntaxNode root)
    {
        var pending = new Stack<SyntaxNode>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            switch (node.Kind)
            {
                case K.AnyKeyword:
                case K.UnknownKeyword:
                case K.StringKeyword:
                case K.NumberKeyword:
                case K.BigIntKeyword:
                case K.BooleanKeyword:
                case K.SymbolKeyword:
                case K.ObjectKeyword:
                case K.VoidKeyword:
                case K.UndefinedKeyword:
                case K.NeverKeyword:
                case K.LiteralType:
                    continue;
                case K.ArrayType:
                    pending.Push(((ArrayTypeNode)node).ElementType!);
                    continue;
                case K.TypeReference:
                    if (((TypeReferenceNode)node).TypeArguments is { } arguments)
                        foreach (var argument in arguments)
                            pending.Push(argument);
                    continue;
                default:
                    return false;
            }
        }
        return true;
    }
}
