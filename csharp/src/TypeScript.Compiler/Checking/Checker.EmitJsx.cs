using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private SyntaxNode? emitJsxFactory;
    private readonly Dictionary<SourceFileNode, SyntaxNode> emitLocalJsxFactories = [];
    private readonly Dictionary<SourceFileNode, SyntaxNode> emitLocalJsxFragments = [];

    internal async ValueTask<SyntaxNode?> GetJsxFactoryForEmitAsync(SyntaxNode? location, bool fragment = false,
        CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(location, cancellation);
        if (fragment)
        {
            if (location is not null && SemanticSyntax.Source(location) is { } file)
            {
                if (emitLocalJsxFragments.TryGetValue(file, out var cached))
                    return cached;
                if (JsxPragma(file, "jsxfrag") is { } pragma)
                {
                    var parsed = ParseEmitJsxFactory(pragma);
                    if (parsed is not null)
                        emitLocalJsxFragments[file] = parsed;
                    return parsed;
                }
            }
            return program.Symbols.Program.Configuration.Options.JsxFragmentFactory is { Length: > 0 } configured
                ? ParseEmitJsxFactory(configured) : null;
        }
        if (location is not null)
        {
            _ = JsxFactoryName(location, location is JsxOpeningFragmentNode);
            if (SemanticSyntax.Source(location) is { } file && emitLocalJsxFactories.TryGetValue(file, out var local))
                return local;
        }
        return emitJsxFactory;
    }

    private TextSlice CacheEmitJsxFactory(TextSlice name, SourceFileNode? file = null, bool fragment = false)
    {
        if (file is null ? emitJsxFactory is not null : (fragment ? emitLocalJsxFragments : emitLocalJsxFactories).ContainsKey(file))
            return name;
        var parsed = file is not null || program.Symbols.Program.Configuration.Options.JsxFactory is { Length: > 0 }
            ? ParseEmitJsxFactory(name) : null;
        if (parsed is null && file is null)
        {
            int dot = name.Span.LastIndexOf('.');
            var factory = new NodeFactory();
            parsed = factory.NewQualifiedName(factory.NewIdentifier(name[..dot]), factory.NewIdentifier(name[(dot + 1)..]));
        }
        if (parsed is null)
            return name;
        if (file is null)
            emitJsxFactory ??= parsed;
        else if (fragment)
            emitLocalJsxFragments.TryAdd(file, parsed);
        else
            emitLocalJsxFactories.TryAdd(file, parsed);
        return name;
    }

    private static SyntaxNode? ParseEmitJsxFactory(TextSlice name)
    {
        var node = Parser.ParseIsolatedEntityName(name);
        if (node is null)
            return null;
        foreach (var child in node.DescendantsAndSelf())
            child.Pos = child.End = -1;
        return node;
    }
}
