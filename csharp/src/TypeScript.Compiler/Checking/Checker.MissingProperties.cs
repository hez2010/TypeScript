using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
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
