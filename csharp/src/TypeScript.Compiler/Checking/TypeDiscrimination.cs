using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface ITypeDiscriminationHost
{
    ValueTask<Type?> FlowPropertyTypeAsync(Type type, string name, bool includeIndex, CancellationToken cancellation);
}

internal sealed class TypeDiscrimination(TypeContext context, TypeAlgebra algebra, TypeViews views, TypeProperties properties,
    SymbolTypes values, TypeRelations relations, DiscriminantRelations discriminants, CheckerSymbols symbols,
    ExpressionContexts contexts, ITypeDiscriminationHost host)
{
    private readonly Dictionary<(ObjectLiteralExpressionNode, UnionType), Type> objects = [];

    internal async ValueTask<Type> ObjectAsync(ObjectLiteralExpressionNode node, UnionType target, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (objects.TryGetValue((node, target), out var cached))
            return cached;
        string key = await discriminants.KeyAsync(target, cancellation).ConfigureAwait(false);
        if (key.Length != 0)
        {
            var property = node.Properties!.OfType<PropertyAssignmentNode>().FirstOrDefault(p =>
                symbols.Binding(p)?.Get(p)?.Symbol?.Name == key && Possible(p.Initializer!));
            if (property is not null)
            {
                var keyType = await algebra.RegularTypeAsync(
                    await contexts.ContextFreeAsync(property.Initializer!, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false);
                if (target.ConstituentMap!.GetValueOrDefault(keyType) is { } match && match != context.UnknownType)
                    return objects[(node, target)] = match;
            }
        }
        var names = new List<string>();
        var expressions = new List<SyntaxNode?>();
        foreach (var property in node.Properties!)
        {
            var symbol = symbols.Binding(property)?.Get(property)?.Symbol;
            if (symbol is null)
                continue;
            if ((property is PropertyAssignmentNode assignment && Possible(assignment.Initializer!)
                || property is ShorthandPropertyAssignmentNode)
                && await discriminants.PropertyAsync(target, symbol.Name, cancellation).ConfigureAwait(false))
            {
                names.Add(symbol.Name);
                expressions.Add(property is PropertyAssignmentNode p ? p.Initializer : ((INamedNode)property).Name);
            }
        }
        var sourceSymbol = symbols.Binding(node)?.Get(node)?.Symbol;
        foreach (var property in await properties.GetAsync(target, cancellation).ConfigureAwait(false))
            if ((property.Flags & SymbolFlags.Optional) != 0 && sourceSymbol?.Members.ContainsKey(property.Name) != true
                && await discriminants.PropertyAsync(target, property.Name, cancellation).ConfigureAwait(false))
            {
                names.Add(property.Name);
                expressions.Add(null);
            }
        var result = await SelectAsync(target, names, async (index, type) =>
        {
            var source = expressions[index] is { } expression
                ? await contexts.ContextFreeAsync(expression, cancellation).ConfigureAwait(false)
                : context.UndefinedType;
            foreach (var part in source is UnionType union ? union.Types : [source])
                if (await relations.RelatedAsync(part, type, RelationKind.Assignable, cancellation).ConfigureAwait(false))
                    return true;
            return false;
        }, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return objects[(node, target)] = result;
    }

    internal async ValueTask<Type?> MatchAsync(Type source, UnionType target, RelationOperation operation, CancellationToken cancellation)
    {
        if ((source.Flags & (TypeFlags.Intersection | TypeFlags.Object)) == 0)
            return null;
        if (await discriminants.MatchAsync(target, source, cancellation).ConfigureAwait(false) is { } direct)
            return direct;
        var candidates = new List<Symbol>();
        foreach (var property in await properties.GetAsync(source, cancellation).ConfigureAwait(false))
            if (await discriminants.PropertyAsync(target, property.Name, cancellation).ConfigureAwait(false))
                candidates.Add(property);
        if (candidates.Count == 0)
            return null;
        var result = await SelectAsync(target, candidates.Select(p => p.Name).ToArray(), async (index, type) =>
        {
            var from = await values.GetAsync(candidates[index], cancellation).ConfigureAwait(false);
            foreach (var part in from is UnionType union ? union.Types : [from])
                if (await operation.CompareAsync(part, type, cancellation: cancellation).ConfigureAwait(false) != Ternary.False)
                    return true;
            return false;
        }, cancellation).ConfigureAwait(false);
        return result == target ? null : result;
    }

    private async ValueTask<Type> SelectAsync(UnionType target, IReadOnlyList<string> names, Func<int, Type, ValueTask<bool>> matches,
        CancellationToken cancellation)
    {
        var include = new Ternary[target.Types.Count];
        for (int i = 0; i < include.Length; i++)
            if ((target.Types[i].Flags & TypeFlags.Primitive) == 0
                && ((await views.ReducedAsync(target.Types[i], cancellation).ConfigureAwait(false)).Flags & TypeFlags.Never) == 0)
                include[i] = Ternary.True;
        for (int n = 0; n < names.Count; n++)
        {
            bool matched = false;
            for (int i = 0; i < include.Length; i++)
                if (include[i] != Ternary.False
                    && await host.FlowPropertyTypeAsync(
                        target.Types[i],
                        names[n],
                        true,
                        cancellation).ConfigureAwait(false) is { } property)
                {
                    if (await matches(n, property).ConfigureAwait(false))
                        matched = true;
                    else
                        include[i] = Ternary.Maybe;
                }
            for (int i = 0; i < include.Length; i++)
                if (include[i] == Ternary.Maybe)
                    include[i] = matched ? Ternary.False : Ternary.True;
        }
        if (include.Contains(Ternary.False))
        {
            var filtered = await algebra.UnionAsync(target.Types.Where((_, i) => include[i] == Ternary.True).ToArray(), UnionReduction.None,
                cancellation: cancellation).ConfigureAwait(false);
            if ((filtered.Flags & TypeFlags.Never) == 0)
                return filtered;
        }
        return target;
    }

    private static bool Possible(SyntaxNode node)
    {
        while (true)
        {
            if (node is PropertyAccessExpressionNode property)
                node = property.Expression!;
            else if (node is ParenthesizedExpressionNode parentheses)
                node = parentheses.Expression!;
            else if (node is JsxExpressionNode jsx)
            {
                if (jsx.Expression is null)
                    return true;
                node = jsx.Expression;
            }
            else
                return node.Kind is SyntaxKind.StringLiteral or SyntaxKind.NumericLiteral or SyntaxKind.BigIntLiteral
                or SyntaxKind.NoSubstitutionTemplateLiteral or SyntaxKind.TemplateExpression or SyntaxKind.TrueKeyword
                or SyntaxKind.FalseKeyword or SyntaxKind.NullKeyword or SyntaxKind.Identifier or SyntaxKind.UndefinedKeyword;
        }
    }
}
