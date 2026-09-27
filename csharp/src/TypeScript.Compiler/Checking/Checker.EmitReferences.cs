using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly Dictionary<IdentifierNode, SyntaxNode?> emitImportReferences = [];

    private ReferenceResolver EmitReferenceResolver(CancellationToken cancellation) => new(
        program.Symbols.Program.Configuration.Options, program.Symbols.Binding, new()
        {
            ResolveName = program.Symbols.NameResolver(cancellation).Resolve,
            GetResolvedSymbol = node => links.SymbolNodes.TryGet(node)?.ResolvedSymbol,
            GetMergedSymbol = program.Symbols.Merger.GetMergedSymbol,
            GetParentOfSymbol = program.Symbols.Parent,
            GetSymbolOfDeclaration = program.Symbols.Declaration,
            GetExportSymbolOfValueSymbolIfExported = program.Symbols.ExportedValue
        });

    internal async ValueTask<SyntaxNode?> GetReferencedExportContainerForEmitAsync(IdentifierNode node, bool prefixLocals,
        CancellationToken cancellation = default)
    {
        if (!EmitParseNode(node))
            return null;
        using var query = await EnterQueryAsync(node, cancellation);
        return EmitReferenceResolver(cancellation).GetReferencedExportContainer(node, prefixLocals);
    }

    internal async ValueTask SetReferencedImportForEmitAsync(IdentifierNode node, SyntaxNode? declaration,
        CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(EmitParseNode(node) ? node : null, cancellation);
        if (declaration is not null)
            RequireNode(declaration);
        emitImportReferences[node] = declaration;
    }

    internal async ValueTask<SyntaxNode?> GetReferencedImportForEmitAsync(IdentifierNode node, CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(EmitParseNode(node) ? node : null, cancellation);
        if (!EmitParseNode(node))
            return emitImportReferences.GetValueOrDefault(node);
        var symbol = links.SymbolNodes.TryGet(node)?.ResolvedSymbol;
        if (symbol is null || symbol == UnknownSymbol)
            symbol = program.Symbols.NameResolver(cancellation).Resolve(node, node.Text,
                SymbolFlags.Value | SymbolFlags.ExportValue | SymbolFlags.Alias);
        return AliasResolver.NonLocal(symbol, SymbolFlags.Value)
            && await program.Aliases.TypeOnlyAsync(symbol!, SymbolFlags.Value, cancellation) is null
            ? AliasResolver.Declaration(symbol!) : null;
    }

    internal async ValueTask<SyntaxNode?> GetReferencedValueForEmitAsync(IdentifierNode node, CancellationToken cancellation = default)
    {
        if (!EmitParseNode(node))
            return null;
        using var query = await EnterQueryAsync(node, cancellation);
        return EmitReferenceResolver(cancellation).GetReferencedValueDeclaration(node);
    }

    internal async ValueTask<IReadOnlyList<SyntaxNode>> GetReferencedValuesForEmitAsync(IdentifierNode node,
        CancellationToken cancellation = default)
    {
        if (!EmitParseNode(node))
            return [];
        using var query = await EnterQueryAsync(node, cancellation);
        return EmitReferenceResolver(cancellation).GetReferencedValueDeclarations(node);
    }

    internal async ValueTask<SyntaxNode?> GetReferencedMemberForEmitAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        if (!EmitParseNode(node))
            return null;
        using var query = await EnterQueryAsync(node, cancellation);
        return EmitReferenceResolver(cancellation).GetReferencedMemberValueDeclaration(node);
    }

    internal async ValueTask<TextSlice> GetElementAccessNameForEmitAsync(
        ElementAccessExpressionNode node,
        CancellationToken cancellation = default)
    {
        if (!EmitParseNode(node))
            return "";
        using var query = await EnterQueryAsync(node, cancellation);
        return await AccessNames.GetAsync(node, cancellation) ?? "";
    }

    internal async ValueTask<IReadOnlyList<Symbol>> GetContainerFunctionPropertiesForEmitAsync(SyntaxNode? node,
        CancellationToken cancellation = default)
    {
        if (node is null)
            return [];
        using var query = await EnterQueryAsync(node, cancellation);
        return await ContainerFunctionPropertiesForEmitAsync(node, cancellation);
    }

    private async ValueTask<IReadOnlyList<Symbol>> ContainerFunctionPropertiesForEmitAsync(SyntaxNode node, CancellationToken cancellation)
        => program.Symbols.Declaration(node) is { } symbol
            ? await Properties.GetAsync(await Values.GetAsync(symbol, cancellation), cancellation) : [];

    internal async ValueTask<bool> IsExpandoFunctionForEmitAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        if (!EmitParseNode(node))
            return false;
        using var query = await EnterQueryAsync(node, cancellation);
        return (await ContainerFunctionPropertiesForEmitAsync(node, cancellation)).Any(p => p.ValueDeclaration is BinaryExpressionNode);
    }
}
