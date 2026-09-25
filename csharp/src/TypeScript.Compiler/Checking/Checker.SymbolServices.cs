using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal async ValueTask<Symbol> GetAliasedSymbolAsync(Symbol symbol, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        using var query = await EnterQueryAsync(null, cancellation).ConfigureAwait(false);
        return await program.Aliases.ResolveAsync(symbol, cancellation);
    }

    internal async ValueTask<Symbol?> GetExportSpecifierLocalTargetSymbolAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(node, cancellation).ConfigureAwait(false);
        if (node is ExportSpecifierNode specifier)
        {
            var export = (ExportDeclarationNode)specifier.Parent!.Parent!;
            if (export.ModuleSpecifier is not null)
                return await program.ExternalMemberAsync(export, specifier, false, cancellation);
            node = specifier.PropertyName ?? specifier.Name!;
            if (node is StringLiteralNode)
                return null;
        }
        if (node is not IdentifierNode)
            throw new ArgumentException("Expected an export specifier or identifier", nameof(node));
        return await program.EntityNames.ResolveAsync(node,
            SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace | SymbolFlags.Alias, true, cancellation: cancellation);
    }

    internal async ValueTask<Symbol?> GetShorthandAssignmentValueSymbolAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(node, cancellation).ConfigureAwait(false);
        return node is ShorthandPropertyAssignmentNode shorthand ? await program.EntityNames.ResolveAsync(shorthand.Name,
            SymbolFlags.Value | SymbolFlags.Alias, true, cancellation: cancellation) : null;
    }

    internal async ValueTask<(Symbol Parameter, Symbol Property)> GetSymbolsOfParameterPropertyDeclarationAsync(
        ParameterDeclarationNode parameter, string name, CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(parameter, cancellation).ConfigureAwait(false);
        var constructor = parameter.Parent!;
        var parameterSymbol = program.Symbols.Lookup(
            program.Symbols.Binding(constructor)!.Get(constructor)?.Locals,
            name,
            SymbolFlags.Value);
        var classSymbol = program.Symbols.Declaration(constructor.Parent!)!;
        var propertySymbol = program.Symbols.Lookup(await MembersAsync(classSymbol, cancellation), name, SymbolFlags.Value);
        return parameterSymbol is not null && propertySymbol is not null ? (parameterSymbol, propertySymbol)
            : throw new ArgumentException("Expected a parameter property declaration", nameof(parameter));
    }

    internal async ValueTask<Type> GetTypeOfSymbolAtLocationAsync(
        Symbol symbol,
        SyntaxNode? location,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        using var query = await EnterQueryAsync(location, cancellation).ConfigureAwait(false);
        symbol = program.Symbols.ExportedValue(symbol)!;
        if (location is not null)
        {
            if (location is IdentifierNode or PrivateIdentifierNode && !QuerySyntax.JsxTag(location)
                && location.Parent is not JsxAttributeNode and not JsxNamespacedNameNode)
            {
                if (QuerySyntax.RightSide(location))
                    location = location.Parent!;
                bool writing = ReferenceSyntax.AccessKind(location) != 0;
                if (QuerySyntax.Expression(location) && (ReferenceSyntax.AssignmentTarget(location) is null || writing))
                {
                    var type = writing && location is PropertyAccessExpressionNode property
                        ? await Access.PropertyAsync(property, writeOnly: true, cancellation: cancellation)
                        : await ExpressionTypeForQueryAsync(location, cancellation);
                    if (program.Symbols.ExportedValue(links.SymbolNodes.Get(location).ResolvedSymbol) == symbol)
                        return Optional.RemoveMarker(type);
                }
            }
            if (QuerySyntax.DeclarationName(location) && location.Parent is SetAccessorDeclarationNode setter
                && SymbolTypes.Annotation(setter) is not null)
                return await Values.WriteAsync(program.Symbols.Declaration(setter)!, cancellation);
            if ((location.Parent is PropertyAccessExpressionNode access && access.Name == location
                    || location.Parent is ElementAccessExpressionNode element && element.ArgumentExpression == location)
                && ReferenceSyntax.AccessKind(location.Parent!) != 0)
                return await Values.WriteAsync(symbol, cancellation);
        }
        return Values.NonMissing(await Values.GetAsync(symbol, cancellation), (symbol.Flags & SymbolFlags.Optional) != 0);
    }
}
