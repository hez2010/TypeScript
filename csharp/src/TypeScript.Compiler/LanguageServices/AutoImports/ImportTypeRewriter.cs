using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

internal static class ImportTypeRewriter
{
    internal static (SyntaxNode Node, IReadOnlyList<Symbol> Imports) Rewrite(SyntaxNode root,
        IReadOnlyDictionary<SyntaxNode, Symbol> identifierSymbols, NodeFactory factory, CancellationToken cancellation)
    {
        List<Symbol> imports = [];
        Dictionary<SyntaxNode, Symbol> replacements = [];
        Dictionary<SyntaxNode, SyntaxNode> copies = [];
        Stack<(SyntaxNode Node, bool Finish)> pending = []; pending.Push((root, false));
        while (pending.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            var node = item.Node;
            if (!item.Finish)
            {
                if (node is ImportTypeNode { Argument: LiteralTypeNode { Literal: StringLiteralNode }, Qualifier: { } qualifier }
                    && identifierSymbols.TryGetValue(FirstIdentifier(qualifier), out var symbol))
                {
                    replacements[node] = symbol;
                    imports.Add(symbol);
                }
                pending.Push((node, true));
                for (int i = node.ChildCount - 1; i >= 0; i--) pending.Push((node.GetChild(i), false));
                continue;
            }
            bool changed = replacements.ContainsKey(node);
            for (int i = 0; !changed && i < node.ChildCount; i++)
                changed = copies[node.GetChild(i)] != node.GetChild(i);
            if (!changed) { copies[node] = node; continue; }
            var clone = node.ShallowClone();
            clone.Parent = null;
            clone.RewriteChildren(copies);
            factory.Cloned(clone, node);
            if (clone is ImportTypeNode import && replacements.TryGetValue(node, out var exported))
            {
                var name = ExportedName(exported);
                var qualifier = import.Qualifier!;
                if (name != FirstIdentifier(qualifier).Text) qualifier = ReplaceFirstIdentifier(qualifier, name, factory);
                clone = factory.NewTypeReferenceNode(qualifier, import.TypeArguments);
            }
            copies[node] = clone;
        }
        return (copies[root], imports);
    }

    private static IdentifierNode FirstIdentifier(SyntaxNode name)
    {
        while (name is QualifiedNameNode qualified) name = qualified.Left!;
        return (IdentifierNode)name;
    }

    private static SyntaxNode ReplaceFirstIdentifier(SyntaxNode name, Utf8String replacement, NodeFactory factory)
    {
        Stack<SyntaxNode> right = [];
        while (name is QualifiedNameNode qualified) { right.Push(qualified.Right!); name = qualified.Left!; }
        SyntaxNode result = factory.NewIdentifier(replacement);
        while (right.TryPop(out var identifier)) result = factory.NewQualifiedName(result, identifier);
        return result;
    }

    internal static Utf8String ExportedName(Symbol symbol, bool capitalize = false)
    {
        if (symbol.Name != "default"u8 && symbol.Name != "export="u8) return symbol.Name;
        var name = DefaultExportName(symbol);
        return name.IsEmpty ? ModuleIdentifier(symbol.Parent!.Name, capitalize) : name;
    }

    internal static Utf8String DefaultExportName(Symbol symbol)
    {
        foreach (var declaration in symbol.Declarations)
        {
            if (declaration is ExportAssignmentNode { Expression: { } expression })
            {
                if (NamedEvaluation.SkipOuter(expression) is IdentifierNode identifier) return identifier.Text;
                continue;
            }
            if (declaration is ExportSpecifierNode { PropertyName: { } property, BindingSymbol.Flags: SymbolFlags.Alias })
            {
                if (property is IdentifierNode identifier) return identifier.Text;
                continue;
            }
            if (declaration.DeclarationName is IdentifierNode name) return name.Text;
            if (symbol.Parent is { } parent && !ExternalModule(parent)) return parent.Name;
        }
        return default;
    }

    internal static bool ExternalModule(Symbol symbol) => (symbol.Flags & SymbolFlags.Module) != 0
        && symbol.Name.Length >= 2 && symbol.Name[0] == '"' && symbol.Name[^1] == '"';

    internal static Utf8String ModuleIdentifier(Utf8String name, bool capitalize = false)
    {
        if (name.Length >= 2 && name[0] == '"' && name[^1] == '"') name = name[1..^1];
        var extension = name.EndsWith(".d.ts"u8) ? ".d.ts"u8 : name.EndsWith(".d.mts"u8) ? ".d.mts"u8
            : name.EndsWith(".d.cts"u8) ? ".d.cts"u8 : CompilerPath.Extension(name);
        if (!extension.IsEmpty) name = name[..^extension.Length];
        if (name.EndsWith("/index"u8)) name = name[..^6];
        name = CompilerPath.BaseName(name);
        var builder = new Utf8StringBuilder();
        bool previousValid = true;
        var points = GoUnicode.Runes(name);
        for (int i = 0; i < points.Length; i++)
        {
            int point = points[i];
            bool valid = i == 0 ? TokenFacts.IsIdentifierStart(point) : TokenFacts.IsIdentifierPart(point);
            if (valid)
            {
                builder.AppendCodePoint(!previousValid || i == 0 && capitalize ? GoUnicode.Upper(point) : point);
            }
            previousValid = valid;
        }
        var result = builder.ToUtf8String();
        return result.IsEmpty || TokenFacts.FromText(result.Span) is >= K.FirstKeyword and < K.FirstContextualKeyword ? "_"u8 + result : result;
    }
}
