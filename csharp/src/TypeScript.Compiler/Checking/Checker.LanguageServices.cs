using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal ValueTask<SyntaxNode?> CreateIndexSignatureAsync(IndexInfo info, SyntaxNode enclosing, Emission.EmitContext emission, CancellationToken cancellation) =>
        EmitSyntaxQueryAsync<SyntaxNode?>(enclosing, enclosing, NodeBuilderFlags.None, async state =>
            TypeSyntaxWithEmitFlags(await IndexSignatureSyntaxAsync(info, state, cancellation), state, emission), null, cancellation);

    internal IReadOnlyList<Type> LocalTypeParameters(Symbol symbol, CancellationToken cancellation) => program.Scopes.Local(symbol, cancellation);

    internal async ValueTask<IReadOnlyList<Symbol>> GetPropertySymbolsFromContextualTypeAsync(SyntaxNode node, Type contextual,
        bool unionSymbolOk, CancellationToken cancellation)
    {
        var name = node.DeclarationName;
        if (name is null) return [];
        if (name is ComputedPropertyNameNode computed) name = computed.Expression;
        Utf8String text = name switch
        {
            IdentifierNode id => id.Text, StringLiteralNode str => str.Text, NumericLiteralNode number => number.Text,
            NoSubstitutionTemplateLiteralNode template => template.Text,
            JsxNamespacedNameNode jsx => JsxName(jsx), _ => default,
        };
        if (text.IsEmpty) return [];
        if (contextual is not UnionType union)
            return await Properties.PropertyAsync(contextual, text, cancellation: cancellation) is { } property ? [property] : [];
        List<Type> filtered = [];
        foreach (var type in union.Types)
            if (node.Parent is not (ObjectLiteralExpressionNode or JsxAttributesNode) || !await InvalidDiscriminantAsync(type, node.Parent, cancellation)) filtered.Add(type);
        List<Symbol> symbols = [];
        foreach (var type in filtered)
            if (await Properties.PropertyAsync(type, text, cancellation: cancellation) is { } property) symbols.Add(property);
        if (unionSymbolOk && (symbols.Count == 0 || symbols.Count == union.Types.Count)
            && await Properties.PropertyAsync(contextual, text, cancellation: cancellation) is { } combined) return [combined];
        if (filtered.Count == 0 && symbols.Count == 0)
            foreach (var type in union.Types)
                if (await Properties.PropertyAsync(type, text, cancellation: cancellation) is { } property) symbols.Add(property);
        return symbols.Distinct().ToArray();
    }

    private async ValueTask<bool> InvalidDiscriminantAsync(Type type, SyntaxNode obj, CancellationToken cancellation)
    {
        var properties = obj switch { ObjectLiteralExpressionNode o => o.Properties, JsxAttributesNode j => j.Properties, _ => null };
        foreach (var property in properties ?? new([]))
        {
            if (property.DeclarationName is not { } name) continue;
            var key = name is JsxNamespacedNameNode jsx ? context.GetStringLiteralType(JsxName(jsx)) : await LiteralNameTypeAsync(name, cancellation);
            if ((key.Flags & TypeFlags.StringOrNumberLiteralOrUnique) == 0) continue;
            if (await Properties.PropertyAsync(type, MappedMembers.PropertyName(key), cancellation: cancellation) is not { } expectedProperty) continue;
            var expected = await Values.GetAsync(expectedProperty, cancellation);
            bool literal = (expected.Flags & TypeFlags.Boolean) != 0 || (expected is UnionType union
                ? union.Types.All(t => (t.Flags & TypeFlags.Unit) != 0) : (expected.Flags & TypeFlags.Unit) != 0);
            if (literal && !await Relations.RelatedAsync(await TypeAtLocationAsync(QuerySyntax.Reparsed(property), cancellation), expected, RelationKind.Assignable, cancellation)) return true;
        }
        return false;
    }

    internal async ValueTask<IReadOnlyList<Symbol>> GetRootSymbolsAsync(Symbol symbol, CancellationToken cancellation)
    {
        List<Symbol> result = [];
        Stack<(Symbol Symbol, HashSet<Symbol> Ancestors)> pending = new();
        pending.Push((symbol, []));
        while (pending.TryPop(out var entry))
        {
            cancellation.ThrowIfCancellationRequested();
            var (current, ancestors) = entry;
            if (ancestors.Contains(current)) continue;
            List<Symbol> roots = [];
            if ((current.CheckFlags & CheckFlags.Synthetic) != 0)
            {
                var containing = links.Values.TryGet(current)?.ContainingType;
                var types = containing switch { UnionType u => u.Types, IntersectionType i => i.Types, _ => (IReadOnlyList<Type>)[] };
                foreach (var type in types)
                    if (await Properties.PropertyAsync(type, current.Name, cancellation: cancellation) is { } property) roots.Add(property);
            }
            else if ((current.Flags & SymbolFlags.Transient) != 0)
            {
                if (links.Spreads.TryGet(current) is { Left: { } left, Right: { } right }) roots.AddRange([left, right]);
                else if (links.MappedSymbols.TryGet(current)?.SyntheticOrigin is { } origin) roots.Add(origin);
                else if ((links.Values.TryGet(current)?.Target ?? links.ExportTypes.TryGet(current)?.Target) is { } target) roots.Add(target);
            }
            if (roots.Count == 0) result.Add(current);
            else
            {
                var next = new HashSet<Symbol>(ancestors) { current };
                for (int i = roots.Count - 1; i >= 0; i--) pending.Push((roots[i], next));
            }
        }
        return result;
    }
}
