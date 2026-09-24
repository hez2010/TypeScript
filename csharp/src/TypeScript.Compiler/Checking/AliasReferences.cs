using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed class AliasReferences(CheckerSymbols symbols, CheckerLinks links, ReferenceSymbols references, AliasResolver aliases)
{
    internal async ValueTask IdentifierAsync(IdentifierNode location, CancellationToken cancellation = default)
    {
        var options = symbols.Program.Configuration.Options;
        if (options.Boolean("verbatimModuleSyntax") == true
            || (location.Flags & NodeFlags.Ambient) != 0
            || FlowReferences.ThisInQuery(location))
            return;
        await MarkAsync(references.Resolve(location, cancellation), location, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask PropertyAsync(SyntaxNode location, Symbol? property, Type parentType, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var options = symbols.Program.Configuration.Options;
        if (options.Boolean("verbatimModuleSyntax") == true || (location.Flags & NodeFlags.Ambient) != 0)
            return;
        for (var current = location; current.Parent is not null; current = current.Parent)
            if (current.Parent is ImportEqualsDeclarationNode import && import.ModuleReference == current)
                return;
        var left = location is QualifiedNameNode qualified ? qualified.Left : FlowReferences.Receiver(location);
        if (left is not IdentifierNode identifier || identifier.Text == "this")
            return;
        var parent = references.Resolve(identifier, cancellation);
        if (parent == symbols.UnknownSymbol)
            return;
        bool isolated = options.Boolean("isolatedModules") == true || options.Boolean("verbatimModuleSyntax") == true;
        if (isolated || (options.Boolean("preserveConstEnums") == true || isolated) && ExportExpression(location)
            || (parentType.Flags & TypeFlags.Any) != 0 || parentType == parentType.Context.SilentNeverType
            || !(property is not null && ((property.Flags & (SymbolFlags.ConstEnum | SymbolFlags.ConstEnumOnlyModule)) != 0
                || (property.Flags & SymbolFlags.EnumMember) != 0 && location.Parent is EnumMemberNode)))
            await MarkAsync(parent, location, cancellation).ConfigureAwait(false);
    }

    private async ValueTask MarkAsync(Symbol symbol, SyntaxNode location, CancellationToken cancellation)
    {
        var options = symbols.Program.Configuration.Options;
        List<AliasSymbolLinks> owned = [];
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (symbol == symbols.ArgumentsSymbol || symbol == symbols.UnknownSymbol
                    || !AliasResolver.NonLocal(symbol, SymbolFlags.Value) || DeclarationOrder.InTypeQuery(location))
                    return;
                var target = await aliases.ResolveAsync(symbol, cancellation).ConfigureAwait(false);
                if ((await aliases.FlagsAsync(symbol, true, cancellation: cancellation).ConfigureAwait(false)
                    & (SymbolFlags.Value | SymbolFlags.ExportValue)) == 0)
                    return;
                bool isolated = options.Boolean("isolatedModules") == true || options.Boolean("verbatimModuleSyntax") == true;
                if (!(isolated || (options.Boolean("preserveConstEnums") == true || isolated) && ExportExpression(location)
                    || (symbols.ExportedValue(target)!.Flags & (SymbolFlags.ConstEnum | SymbolFlags.ConstEnumOnlyModule)) == 0))
                    return;
                var data = links.Aliases.Get(symbol);
                if (data.Referenced)
                    return;
                data.Referenced = true;
                owned.Add(data);
                var declaration = AliasResolver.Declaration(symbol) ?? throw new InvalidOperationException("Referenced alias has no declaration");
                if (declaration is not ImportEqualsDeclarationNode { ModuleReference: not ExternalModuleReferenceNode } import
                    || (await aliases.FlagsAsync(
                        (await aliases.SymbolAsync(symbol, cancellation: cancellation).ConfigureAwait(false))!,
                        cancellation: cancellation).ConfigureAwait(false)
                        & SymbolFlags.Value) == 0)
                    return;
                var first = import.ModuleReference;
                while (first is QualifiedNameNode qualified)
                    first = qualified.Left;
                location = (IdentifierNode)first!;
                if (FlowReferences.ThisInQuery(location))
                    return;
                symbol = references.Resolve((IdentifierNode)location, cancellation);
            }
        }
        catch
        {
            foreach (var data in owned)
                data.Referenced = false;
            throw;
        }
    }

    private static bool ExportExpression(SyntaxNode location)
    {
        for (var node = location; node is not null; node = node.Parent)
            if (node.Parent is ExportAssignmentNode export && export.Expression == node
                && node is IdentifierNode or PropertyAccessExpressionNode
                || node.Parent is ExportSpecifierNode specifier && (specifier.Name == node || specifier.PropertyName == node))
                return true;
        return false;
    }
}
