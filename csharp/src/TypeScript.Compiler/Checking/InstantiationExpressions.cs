using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IInstantiationExpressionHost
{
    ValueTask<Type> CheckExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask<Type> CheckedFunctionTypeAsync(SyntaxNode node, CancellationToken cancellation);

    void InstantiationGrammar(SyntaxNode node, NodeList? arguments);

    void ExpressionError(SyntaxNode node, int code);

    ValueTask InapplicableInstantiationAsync(SyntaxNode node, Type type, CancellationToken cancellation);
}

internal sealed class InstantiationExpressions(TypeContext context, TypeAlgebra algebra, TypeConstraints constraints,
    StructuredMembers members, CallSignatures rules, SignatureInstantiation signatures, IInstantiationExpressionHost host)
{
    private readonly Dictionary<(SyntaxNode, Type), Type> cache = [];
    internal int CacheCount => cache.Count;

    internal async ValueTask<Type> CheckAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        var (expression, arguments) = Parts(node);
        host.InstantiationGrammar(node, arguments);
        if (arguments is not null)
            foreach (var argument in arguments)
                await host.CheckedFunctionTypeAsync(argument, cancellation).ConfigureAwait(false);
        if (node is ExpressionWithTypeArgumentsNode)
        {
            var parent = node.Parent;
            while (parent is ParenthesizedExpressionNode)
                parent = parent.Parent;
            if (parent is BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.InstanceOfKeyword } binary)
                for (var current = node; current is not null && current != parent; current = current.Parent)
                    if (current == binary.Right)
                    {
                        host.ExpressionError(node, 2848);
                        break;
                    }
        }
        return await GetAsync(
            await host.CheckExpressionAsync(expression, 0, cancellation).ConfigureAwait(false),
            node,
            cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type> GetAsync(Type type, SyntaxNode node, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        var arguments = Parts(node).Arguments;
        if (type == context.SilentNeverType
            || type == context.ErrorType
            || (type.Flags & TypeFlags.Any) != 0 && type.Alias is not null
            || arguments is null)
            return type;
        var key = (node, type);
        if (cache.TryGetValue(key, out var cached))
            return cached;
        bool someApplicable = false;
        Type? nonApplicable = null;
        var result = await InstantiateAsync(type).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        cache[key] = result;
        try
        {
            if (!someApplicable || nonApplicable is not null)
                await host.InapplicableInstantiationAsync(node, someApplicable ? nonApplicable! : type, cancellation).ConfigureAwait(false);
        }
        catch
        {
            if (cache.GetValueOrDefault(key) == result)
                cache.Remove(key);
            throw;
        }
        return result;

        async ValueTask<IReadOnlyList<Signature>> InstantiateSignaturesAsync(IReadOnlyList<Signature> source)
        {
            var result = new List<Signature>();
            foreach (var signature in source)
                if (signature.TypeParameters.Count != 0 && CallSignatures.TypeArity(signature, arguments))
                {
                    var types = await rules.TypeArgumentsAsync(signature, arguments, true, cancellation).ConfigureAwait(false);
                    result.Add(types is null ? signature : await signatures.GetAsync(signature, types,
                        ((signature.Declaration?.Flags ?? 0) & NodeFlags.JavaScriptFile) != 0,
                        cancellation: cancellation).ConfigureAwait(false));
                }
            return source.SequenceEqual(result) ? source : result;
        }

        async ValueTask<Type> InstantiateAsync(Type source)
        {
            bool hasSignatures = false, applicable = false;
            var result = await PartAsync(source).ConfigureAwait(false);
            someApplicable |= applicable;
            if (hasSignatures && !applicable)
                nonApplicable ??= source;
            return result;

            async ValueTask<Type> PartAsync(Type part)
            {
                await Task.CompletedTask.ConfigureAwait(
                    RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
                cancellation.ThrowIfCancellationRequested();
                if (part is ObjectType objectType)
                {
                    var resolved = await members.ResolveAsync(objectType, cancellation).ConfigureAwait(false);
                    var calls = await InstantiateSignaturesAsync(resolved.CallSignatures).ConfigureAwait(false);
                    var constructors = await InstantiateSignaturesAsync(resolved.ConstructSignatures).ConfigureAwait(false);
                    hasSignatures |= resolved.CallSignatures.Count != 0 || resolved.ConstructSignatures.Count != 0;
                    applicable |= calls.Count != 0 || constructors.Count != 0;
                    if (calls != resolved.CallSignatures || constructors != resolved.ConstructSignatures)
                    {
                        var symbol = new Symbol(SymbolFlags.Transient, Symbol.InternalPrefix + "instantiationExpression");
                        symbol.DeclarationList.AddRange(
                            (part.Symbol ?? throw new InvalidOperationException("Instantiation source must have a symbol")).Declarations);
                        var value = (InstantiationExpressionType)context.NewObjectType(
                            ObjectFlags.Anonymous | ObjectFlags.InstantiationExpressionType | ObjectFlags.MembersResolved,
                            symbol);
                        value.Members = resolved.Members;
                        value.Properties = resolved.Properties;
                        value.CallSignatures = calls;
                        value.ConstructSignatures = constructors;
                        value.IndexInfos = resolved.IndexInfos;
                        value.Node = node;
                        return value;
                    }
                }
                else if ((part.Flags & TypeFlags.InstantiableNonPrimitive) != 0)
                {
                    var constraint = await constraints.BaseConstraintAsync(part, cancellation).ConfigureAwait(false);
                    if (constraint is not null
                        && await PartAsync(constraint).ConfigureAwait(false) is { } instantiated
                        && instantiated != constraint)
                        return instantiated;
                }
                else if (part is UnionType)
                    return (await algebra.MapAsync(
                        part,
                        async t => await InstantiateAsync(t).ConfigureAwait(false),
                        cancellation: cancellation).ConfigureAwait(false))!;
                else if (part is IntersectionType intersection)
                {
                    var types = new List<Type>();
                    foreach (var constituent in intersection.Types)
                        types.Add(await PartAsync(constituent).ConfigureAwait(false));
                    return await algebra.IntersectionAsync(types, cancellation: cancellation).ConfigureAwait(false);
                }
                return part;
            }
        }
    }

    private static (SyntaxNode Expression, NodeList? Arguments) Parts(SyntaxNode node) => node switch
    {
        ExpressionWithTypeArgumentsNode expression => (expression.Expression!, expression.TypeArguments),
        TypeQueryNode query => (query.ExprName!, query.TypeArguments),
        ImportTypeNode import => (import.Qualifier ?? import.Argument!, import.TypeArguments),
        _ => throw new ArgumentException("Expected an instantiation expression or type query", nameof(node))
    };
}
