using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal async ValueTask<IReadOnlyList<Symbol>> GetSymbolsInScopeAsync(SyntaxNode location, SymbolFlags meaning,
        CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(location, cancellation).ConfigureAwait(false);
        if ((location.Flags & NodeFlags.InWithStatement) != 0)
            return [];
        var symbols = new Dictionary<Utf8String, Symbol>();
        bool isStatic = false;
        SyntaxNode? previous = null;
        for (SyntaxNode? current = location; current is not null; previous = current, current = current.Parent)
        {
            cancellation.ThrowIfCancellationRequested();
            if (current is ModuleDeclarationNode { Attributes: { } attributes } && previous == attributes)
                continue;
            var binding = program.Symbols.Binding(current);
            if (!(current is SourceFileNode && binding?.IsModule != true) && binding?.Get(current) is { } data)
                Copy(data.Locals, meaning);
            switch (current)
            {
                case SourceFileNode { ExternalModuleIndicator: null }:
                    break;
                case SourceFileNode or ModuleDeclarationNode:
                    if (program.Symbols.Declaration(current) is { } module)
                        Copy(module.Exports, meaning & SymbolFlags.ModuleMember, localExports: true);
                    break;
                case EnumDeclarationNode:
                    Copy(program.Symbols.Declaration(current)!.Exports, meaning & SymbolFlags.EnumMember);
                    break;
                case ClassExpressionNode or ClassDeclarationNode or InterfaceDeclarationNode:
                    if (current is ClassExpressionNode { Name: not null })
                        Add(program.Symbols.Binding(current)!.Get(current)!.Value.Symbol!, meaning);
                    if (!isStatic)
                        Copy(await MembersAsync(program.Symbols.Declaration(current)!, cancellation), meaning & SymbolFlags.Type);
                    break;
                case FunctionExpressionNode { Name: not null }:
                    Add(program.Symbols.Binding(current)!.Get(current)!.Value.Symbol!, meaning);
                    break;
            }
            if (current.Kind is SyntaxKind.MethodDeclaration or SyntaxKind.MethodSignature or SyntaxKind.Constructor
                or SyntaxKind.GetAccessor or SyntaxKind.SetAccessor or SyntaxKind.FunctionDeclaration or SyntaxKind.FunctionExpression)
                Add(program.Symbols.ArgumentsSymbol, meaning);
            isStatic = SemanticSyntax.IsStatic(current);
        }
        Copy(program.Symbols.Globals, meaning);
        symbols.Remove(Utf8Literals.This);
        return VisibleSymbols(symbols);

        void Add(Symbol symbol, SymbolFlags flags)
        {
            if (((symbol.Flags | (symbol.ExportSymbol?.Flags ?? 0)) & flags) != 0)
                symbols.TryAdd(symbol.Name, symbol);
        }
        void Copy(IReadOnlyDictionary<Utf8String, Symbol> source, SymbolFlags flags, bool localExports = false)
        {
            if (flags == 0)
                return;
            foreach (var symbol in source.Values)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!localExports || symbol.Name != Utf8Literals.Default
                    && !symbol.Declarations.Any(d => d is ExportSpecifierNode or NamespaceExportNode))
                    Add(symbol, flags);
            }
        }
    }

    internal async ValueTask<IReadOnlyList<Symbol>> GetExportsOfModuleAsync(Symbol symbol, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        using var query = await EnterQueryAsync(null, cancellation).ConfigureAwait(false);
        return VisibleSymbols(await program.ModuleExports.ResolveAsync(symbol, cancellation));
    }

    private static Symbol[] VisibleSymbols(IReadOnlyDictionary<Utf8String, Symbol> table) => table
        .Where(p => !ReservedMemberName(p.Key)).Select(p => p.Value).ToArray();

    private static bool ReservedMemberName(Utf8String name) => name.Length >= 2
        && name.Span.StartsWith(Symbol.InternalPrefix, StringComparison.Ordinal)
        && name[1] is not (byte)'@' and not (byte)'#';
}
