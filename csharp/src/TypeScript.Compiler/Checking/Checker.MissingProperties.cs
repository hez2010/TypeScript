using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal Dictionary<SyntaxNode, IReadOnlyList<Symbol>> RequiredPropertyDeclarations { get; } = [];

    private async ValueTask<int?> MissingRequiredPropertyCodeAsync(Type source, Type target, RelationKind relation,
        SyntaxNode node, CancellationToken cancellation)
    {
        if (source is not (ObjectType or IntersectionType) || target is not ObjectType)
            return null;
        if (source == GlobalObject || source is MappedType mapped && await Instantiation.Mapped.IsGenericAsync(mapped, cancellation))
            return null;
        if (await Normalization.GetAsync(source, false, cancellation) != source
            || await Normalization.GetAsync(target, true, cancellation) != target)
            return null;
        if (target is TypeReference { Target: TupleType tuple }
            && (ObjectRelations.ArrayOrTuple(source) || (tuple.CombinedFlags & ElementFlags.Variable) != 0))
            return null;
        var calls = await SignaturesAsync(source, false, cancellation);
        var constructors = await SignaturesAsync(source, true, cancellation);
        if ((calls.Count != 0 || constructors.Count != 0) && (await Properties.GetAsync(source, cancellation)).Count == 0
            && !(calls.Count != 0 && (await SignaturesAsync(target, false, cancellation)).Count != 0)
            && !(constructors.Count != 0 && (await SignaturesAsync(target, true, cancellation)).Count != 0))
            return null;
        bool requireOptional = relation is RelationKind.Subtype or RelationKind.StrictSubtype
            && (source.ObjectFlags & ObjectFlags.ObjectLiteral) == 0
            && !await EmptyArrayAsync(source, cancellation) && source is not TypeReference { Target: TupleType };
        var missing = new List<Symbol>();
        foreach (var property in await Properties.GetAsync(target, cancellation))
        {
            if (property.ValueDeclaration is { } declaration && SemanticSyntax.IsStatic(declaration)
                && SemanticSyntax.Name(declaration) is PrivateIdentifierNode)
                continue;
            if ((requireOptional || (property.Flags & SymbolFlags.Optional) == 0 && (property.CheckFlags & CheckFlags.Partial) == 0)
                && await Properties.PropertyAsync(source, property.Name, cancellation: cancellation) is null)
                missing.Add(property);
        }
        if (missing.Count == 0)
            return null;
        if (SemanticSyntax.Name(missing[0].ValueDeclaration) is PrivateIdentifierNode privateName
            && source.Symbol is { } sourceSymbol && (sourceSymbol.Flags & SymbolFlags.Class) != 0
            && await Properties.PropertyAsync(
                source,
                PrivateAccess.Name(sourceSymbol, privateName.Text),
                cancellation: cancellation) is not null)
            return null;
        if (missing.Count > 1)
        {
            bool mutableTarget = target is TypeReference { Target: TupleType { IsReadonly: false } }
                || IsArray(target) && !IsReadonlyArray(target);
            if (source is TypeReference { Target: TupleType sourceTuple })
            {
                if (sourceTuple.IsReadonly && mutableTarget || !ObjectRelations.ArrayOrTuple(target))
                    return null;
            }
            else if (IsReadonlyArray(source) && mutableTarget || target is TypeReference { Target: TupleType } && !IsArray(source))
                return null;
        }
        RequiredPropertyDeclarations[node] = missing;
        return missing.Count == 1 ? 2741 : missing.Count > 5 ? 2740 : 2739;
    }

    private async ValueTask CheckMissingPropertiesAsync(SourceFileNode file, CancellationToken cancellation)
    {
        for (int i = 0; i < DeferredMissingProperties.Count; i++)
        {
            var (node, type, suggestion) = DeferredMissingProperties[i];
            cancellation.ThrowIfCancellationRequested();
            if (SemanticSyntax.Source(node) != file || (links.Nodes.Get(node).Flags & NodeCheckFlags.TypeChecked) != 0)
                continue;
            links.Nodes.Get(node).Flags |= NodeCheckFlags.TypeChecked;
            string name = SyntaxNameText.Get(node);
            int code;
            if (await StaticPropertyAsync(name, type, cancellation).ConfigureAwait(false))
                code = 2576;
            else if (await Awaited.OfPromiseAsync(type, cancellation: cancellation).ConfigureAwait(false) is { } promised
                && await Properties.PropertyAsync(promised, name, cancellation: cancellation).ConfigureAwait(false) is not null)
                code = 2339;
            else if (LibraryFeatures.PropertyLibrary(
                (await ApparentAsync(type, cancellation).ConfigureAwait(false)).Symbol?.Name,
                name) is not null)
                code = 2550;
            else
            {
                var candidates = new List<Symbol>();
                foreach (var property in await Properties.GetAsync(type, cancellation).ConfigureAwait(false))
                    if (node.Parent is not PropertyAccessExpressionNode access
                        || await MemberAccessibility.CheckAsync(
                            access,
                            access.Expression!.Kind == SyntaxKind.SuperKeyword,
                            false,
                            type,
                            property,
                            false,
                            cancellation).ConfigureAwait(false))
                        candidates.Add(property);
                var similar = await SymbolSuggestions.FindAsync(name, candidates, SymbolFlags.Value, cancellation).ConfigureAwait(false);
                if (similar is not null)
                    code = suggestion ? 2568 : 2551;
                else
                    code = await EmptyDomTypeAsync(type, cancellation).ConfigureAwait(false) ? 2812 : 2339;
            }
            if (suggestion && code == 2568)
                ExpressionSuggestion(node, code);
            else
                Error(node, code);
        }
        DeferredMissingProperties.RemoveAll(d => SemanticSyntax.Source(d.Node) == file);
    }

    private async ValueTask<bool> EmptyDomTypeAsync(Type type, CancellationToken cancellation)
    {
        if (program.Symbols.Program.Configuration.Options.Strings("lib")?.Any(l => l is "dom" or "lib.dom.d.ts") == true)
            return false;
        var pending = new Stack<Type>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            if (current is UnionOrIntersectionType composite)
            {
                foreach (var part in composite.Types)
                    pending.Push(part);
                continue;
            }
            string? name = current.Symbol?.Name;
            if (name is not ("EventTarget" or "Node" or "Element")
                && !(name?.StartsWith("HTML", StringComparison.Ordinal) == true && name.EndsWith("Element", StringComparison.Ordinal)))
                return false;
        }
        return await Views.EmptyObjectAsync(type, cancellation).ConfigureAwait(false);
    }
}
