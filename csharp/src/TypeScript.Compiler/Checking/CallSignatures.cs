using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface ICallSignatureHost : IConstraintCheckHost
{
    bool IsArray(Type type);

    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<bool> SpecialArityAsync(
        SyntaxNode node,
        IReadOnlyList<SyntaxNode> arguments,
        Signature signature,
        CancellationToken cancellation);

    void ExpressionError(SyntaxNode node, int code);
}

internal sealed class CallSignatures(TypeContext context, CheckerSymbols symbols, SignatureParameters parameters,
    TypeConstraints constraints, TypeInstantiation instantiation, BaseTypes bases, TypeRelations relations, ICallSignatureHost host)
{
    internal Signature Any { get; } = context.NewSignature(0, null, [], null, [], context.AnyType, null, 0);
    internal Signature Unknown { get; } = context.NewSignature(0, null, [], null, [], context.ErrorType, null, 0);
    internal Signature Resolving { get; } = context.NewSignature(0, null, [], null, [], context.AnyType, null, 0);
    internal Signature SilentNever { get; } = context.NewSignature(0, null, [], null, [], context.SilentNeverType, null, 0);
    private readonly Dictionary<(Signature, SignatureFlags), Signature> optional = [];

    internal async ValueTask<Type?> NonArrayRestAsync(Signature signature, CancellationToken cancellation = default)
    {
        var rest = await parameters.EffectiveRestAsync(signature, cancellation).ConfigureAwait(false);
        return rest is not null && !host.IsArray(rest) && (rest.Flags & TypeFlags.Any) == 0 ? rest : null;
    }

    internal static int MinimumTypes(Signature signature)
    {
        int count = 0;
        for (int i = 0; i < signature.TypeParameters.Count; i++)
            if (signature.TypeParameters[i].Symbol?.Declarations.OfType<TypeParameterDeclarationNode>().Any(d => d.DefaultType is not null) != true)
                count = i + 1;
        return count;
    }

    internal static bool TypeArity(Signature signature, IReadOnlyList<SyntaxNode> nodes) => nodes.Count == 0
            || nodes.Count >= MinimumTypes(signature) && nodes.Count <= signature.TypeParameters.Count;

    internal async ValueTask<bool> ArityAsync(SyntaxNode node, IReadOnlyList<SyntaxNode> arguments, Signature signature,
        bool trailingComma = false, CancellationToken cancellation = default)
    {
        int count = await parameters.CountAsync(signature, cancellation).ConfigureAwait(false);
        int minimum = await parameters.MinimumAsync(signature, cancellation: cancellation).ConfigureAwait(false);
        int argumentCount;
        bool incomplete;
        if (node is NewExpressionNode { Arguments: null })
            return minimum == 0;
        if (node is TaggedTemplateExpressionNode tag)
        {
            argumentCount = arguments.Count;
            var literal = tag.Template is TemplateExpressionNode template
                ? ((TemplateSpanNode)template.TemplateSpans![^1]).Literal!
                : tag.Template!;
            incomplete = literal.Pos == literal.End || literal switch
            {
                TemplateTailNode tail => (tail.TemplateFlags & TokenFlags.Unterminated) != 0,
                NoSubstitutionTemplateLiteralNode text => (text.TemplateFlags & TokenFlags.Unterminated) != 0,
                _ => false
            };
        }
        else if (node is CallExpressionNode or NewExpressionNode)
        {
            argumentCount = arguments.Count + (trailingComma ? 1 : 0);
            incomplete = CallArguments.List(node)!.End == node.End;
            int spread = CallArguments.SpreadIndex(arguments);
            if (spread >= 0)
                return spread >= minimum
                    && (await parameters.HasRestAsync(signature, cancellation).ConfigureAwait(false) || spread < count);
        }
        else if (node is BinaryExpressionNode)
        {
            argumentCount = 1;
            incomplete = false;
        }
        else
            return await host.SpecialArityAsync(node, arguments, signature, cancellation).ConfigureAwait(false);
        if (!await parameters.HasRestAsync(signature, cancellation).ConfigureAwait(false) && argumentCount > count)
            return false;
        if (incomplete || argumentCount >= minimum)
            return true;
        for (int i = argumentCount; i < minimum; i++)
        {
            var type = await parameters.AtAsync(signature, i, cancellation).ConfigureAwait(false);
            if (!(type is UnionType union ? union.Types : [type]).Any(t => (t.Flags & TypeFlags.Void) != 0))
                return false;
        }
        return true;
    }

    internal async ValueTask<IReadOnlyList<Type>?> TypeArgumentsAsync(Signature signature, IReadOnlyList<SyntaxNode> nodes,
        bool reportErrors = false, CancellationToken cancellation = default)
    {
        var supplied = new List<Type>();
        foreach (var node in nodes)
            supplied.Add(await host.TypeFromNodeAsync(node, cancellation).ConfigureAwait(false));
        var arguments = await constraints.FillMissingArgumentsAsync(supplied, signature.TypeParameters,
            ((signature.Declaration?.Flags ?? 0) & NodeFlags.JavaScriptFile) != 0,
            (s, t, token) => relations.RelatedAsync(s, t, RelationKind.Identity, token), cancellation).ConfigureAwait(false);
        TypeMapper? mapper = null;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (i >= signature.TypeParameters.Count)
                throw new InvalidOperationException("Type argument count must be checked first");
            var constraint = await constraints.ConstraintAsync(signature.TypeParameters[i], cancellation).ConfigureAwait(false);
            if (constraint is null)
                continue;
            mapper ??= TypeMapper.Create(signature.TypeParameters.Cast<Type>().ToArray(), arguments.ToArray());
            var target = await bases.WithThisAsync(
                (await instantiation.InstantiateAsync(constraint, mapper, cancellation: cancellation).ConfigureAwait(false))!,
                arguments[i], cancellation: cancellation).ConfigureAwait(false);
            if (!await relations.RelatedAsync(arguments[i], target, RelationKind.Assignable, cancellation).ConfigureAwait(false))
            {
                if (reportErrors)
                    await host.CheckConstraintAsync(arguments[i], target, nodes[i], cancellation).ConfigureAwait(false);
                return null;
            }
        }
        return arguments;
    }

    internal List<Signature> Reorder(IReadOnlyList<Signature> signatures, SignatureFlags chain)
    {
        SyntaxNode? lastParent = null;
        Symbol? lastSymbol = null;
        int index = 0, cutoff = 0, specialized = -1;
        var result = new List<Signature>();
        foreach (var item in signatures)
        {
            var symbol = item.Declaration is { } declaration ? symbols.Declaration(declaration) : null;
            var parent = item.Declaration?.Parent;
            if (lastSymbol is null || lastSymbol == symbol)
            {
                if (lastParent is not null && parent == lastParent)
                    index++;
                else
                {
                    lastParent = parent;
                    index = cutoff;
                }
            }
            else
            {
                index = result.Count;
                cutoff = result.Count;
                lastParent = parent;
            }
            lastSymbol = symbol;
            int insert = index;
            if ((item.Flags & SignatureFlags.HasLiteralTypes) != 0)
            {
                insert = ++specialized;
                cutoff++;
            }
            var signature = item;
            if (chain != 0 && (signature.Flags & SignatureFlags.CallChainFlags) != chain)
            {
                if (!optional.TryGetValue((item, chain), out signature))
                {
                    signature = context.CloneSignature(item);
                    signature.Flags |= chain;
                    optional[(item, chain)] = signature;
                }
            }
            result.Insert(insert, signature);
        }
        return result;
    }
}
