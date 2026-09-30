using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using S = TypeScript.Compiler.Binding.SymbolFlags;

namespace TypeScript.Compiler.Checking;

internal interface IAliasTargetHost
{
    ValueTask<Symbol?> ExternalModuleAsync(
        SyntaxNode location,
        SyntaxNode? specifier,
        ImportAttributesNode? attributes,
        CancellationToken cancellation);

    ValueTask<Symbol?> AdjustEsModuleAsync(
        Symbol module,
        Symbol target,
        SyntaxNode declaration,
        SyntaxNode specifier,
        CancellationToken cancellation);

    ValueTask<Symbol?> DefaultExportAsync(Symbol module, SyntaxNode declaration, bool dontResolveAlias, CancellationToken cancellation);

    ValueTask<Symbol?> ExternalMemberAsync(
        SyntaxNode importOrExport,
        SyntaxNode specifier,
        bool dontResolveAlias,
        CancellationToken cancellation);

    ValueTask<Symbol?> AliasExpressionAsync(SyntaxNode expression, CancellationToken cancellation);

    void TypeOnlyImportAlias(ImportEqualsDeclarationNode declaration, SyntaxNode typeOnlyDeclaration, bool exported);

    bool UsesRequireModuleExports { get; }

    ValueTask<Symbol?> ExportOfModuleAsync(Symbol module, Utf8String name, SyntaxNode declaration, CancellationToken cancellation);
}

internal sealed class AliasTargets(CheckerSymbols symbols, AliasResolver aliases, EntityNames names, IAliasTargetHost host)
{
    internal async ValueTask<Symbol?> TargetAsync(SyntaxNode? declaration, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (declaration is null)
            return null;
        switch (declaration)
        {
            case ImportEqualsDeclarationNode or VariableDeclarationNode:
                return await ImportEqualsAsync(declaration, cancellation).ConfigureAwait(false);
            case ImportClauseNode:
                var module = await host.ExternalModuleAsync(
                    declaration,
                    ModuleSpecifier(declaration),
                    Attributes(declaration.Parent!),
                    cancellation).ConfigureAwait(false);
                return module is null ? null : await DefaultAsync(module, declaration, true, cancellation).ConfigureAwait(false);
            case NamespaceImportNode or NamespaceExportNode:
                var specifier = ModuleSpecifier(declaration);
                if (specifier is null && declaration is NamespaceExportNode)
                    return null;
                var immediate = await host.ExternalModuleAsync(declaration, specifier,
                    Attributes(declaration is NamespaceImportNode ? declaration.Parent!.Parent! : declaration.Parent!),
                    cancellation).ConfigureAwait(false);
                var resolved = await EsModuleAsync(immediate, declaration, specifier!, cancellation).ConfigureAwait(false);
                aliases.MarkTypeOnly(declaration);
                return resolved;
            case ImportSpecifierNode or BindingElementNode:
                return await ImportSpecifierAsync(declaration, cancellation).ConfigureAwait(false);
            case ExportSpecifierNode export:
                return await ExportSpecifierAsync(export, S.Value | S.Type | S.Namespace, true, cancellation).ConfigureAwait(false);
            case ExportAssignmentNode assignment:
                var container = assignment.Parent is SourceFileNode ? assignment.Parent : assignment.Parent?.Parent;
                if (container is ModuleDeclarationNode { Name: not StringLiteralNode, Keyword: not SyntaxKind.GlobalKeyword })
                    return null;
                var target = await AliasLikeAsync(assignment.Expression!, cancellation).ConfigureAwait(false);
                aliases.MarkTypeOnly(assignment);
                return target;
            case BinaryExpressionNode binary:
                var binaryTarget = await AliasLikeAsync(binary.Right!, cancellation).ConfigureAwait(false);
                aliases.MarkTypeOnly(binary);
                return binaryTarget;
            case NamespaceExportDeclarationNode:
                var containingSymbol = symbols.Binding(declaration.Parent!)?.Get(declaration.Parent!)?.Symbol;
                if (containingSymbol is null)
                    return null;
                var external = await ExternalModuleAsync(containingSymbol, true, cancellation).ConfigureAwait(false);
                aliases.MarkTypeOnly(declaration);
                return external;
            case ShorthandPropertyAssignmentNode shorthand:
                return await names.ResolveAsync(
                    shorthand.Name,
                    S.Value | S.Type | S.Namespace,
                    true,
                    true,
                    cancellation: cancellation).ConfigureAwait(false);
            case PropertyAssignmentNode property:
                return await AliasLikeAsync(property.Initializer!, cancellation).ConfigureAwait(false);
            case ElementAccessExpressionNode or PropertyAccessExpressionNode:
                return declaration.Parent is BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.EqualsToken } parent
                    && parent.Left == declaration
                    ? await AliasLikeAsync(parent.Right!, cancellation).ConfigureAwait(false) : null;
            default:
                throw new InvalidOperationException("Unsupported alias declaration kind: " + declaration.Kind);
        }
    }

    private async ValueTask<Symbol?> ImportEqualsAsync(SyntaxNode declaration, CancellationToken cancellation)
    {
        if (declaration is VariableDeclarationNode
            || declaration is ImportEqualsDeclarationNode { ModuleReference: ExternalModuleReferenceNode })
        {
            var specifier = declaration is VariableDeclarationNode { Initializer: CallExpressionNode { Arguments: { Count: > 0 } arguments } }
                ? arguments[0] : ((ExternalModuleReferenceNode)((ImportEqualsDeclarationNode)declaration).ModuleReference!).Expression;
            var module = await host.ExternalModuleAsync(declaration, specifier, null, cancellation).ConfigureAwait(false);
            var resolved = await ExternalModuleAsync(module, true, cancellation).ConfigureAwait(false);
            if (resolved is not null && host.UsesRequireModuleExports
                && await host.ExportOfModuleAsync(
                    resolved,
                    Utf8Literals.ModuleExports,
                    declaration,
                    cancellation).ConfigureAwait(false) is { } exported)
                return exported;
            aliases.MarkTypeOnly(declaration);
            return resolved;
        }
        var import = (ImportEqualsDeclarationNode)declaration;
        var reference = import.ModuleReference!;
        var meaning = reference is IdentifierNode || reference.Parent is QualifiedNameNode ? S.Namespace : S.Value | S.Type | S.Namespace;
        var target = await names.ResolveAsync(reference, meaning, dontResolveAlias: true, cancellation: cancellation).ConfigureAwait(false);
        await CheckImportEqualsTypeOnlyAsync(import, cancellation).ConfigureAwait(false);
        return target;
    }

    private async ValueTask CheckImportEqualsTypeOnlyAsync(ImportEqualsDeclarationNode declaration, CancellationToken cancellation)
    {
        SyntaxNode name = declaration.ModuleReference!;
        while (true)
        {
            var symbol = await names.ResolveAsync(
                name,
                S.Value | S.Type | S.Namespace,
                true,
                true,
                cancellation: cancellation).ConfigureAwait(false);
            if (symbol is not null && await aliases.TypeOnlyAsync(symbol, cancellation: cancellation).ConfigureAwait(false) is { } typeOnly)
            {
                host.TypeOnlyImportAlias(declaration, typeOnly, typeOnly is ExportSpecifierNode or ExportDeclarationNode);
                return;
            }
            if (name is IdentifierNode)
                return;
            name = ((QualifiedNameNode)name).Left!;
        }
    }

    private async ValueTask<Symbol?> ImportSpecifierAsync(SyntaxNode declaration, CancellationToken cancellation)
    {
        if (declaration is ImportSpecifierNode import && Text(import.PropertyName ?? import.Name) == Utf8Literals.Default)
        {
            var specifier = ModuleSpecifier(import);
            if (specifier is not null)
            {
                var module = await host.ExternalModuleAsync(
                    import,
                    specifier,
                    Attributes(import.Parent!.Parent!.Parent!),
                    cancellation).ConfigureAwait(false);
                if (module is not null)
                    return await DefaultAsync(module, import, true, cancellation).ConfigureAwait(false);
            }
        }
        var root = declaration.Parent!.Parent!.Parent!;
        if (declaration is BindingElementNode)
            root = SemanticSyntax.RootDeclaration(declaration);
        var result = await host.ExternalMemberAsync(root, declaration, true, cancellation).ConfigureAwait(false);
        aliases.MarkTypeOnly(declaration);
        return result;
    }

    internal async ValueTask<Symbol?> ExportSpecifierAsync(ExportSpecifierNode declaration, S meaning, bool dontResolveAlias,
        CancellationToken cancellation = default)
    {
        var name = declaration.PropertyName ?? declaration.Name!;
        if (Text(name) == Utf8Literals.Default && ModuleSpecifier(declaration) is { } specifier)
        {
            var module = await host.ExternalModuleAsync(
                declaration,
                specifier,
                Attributes(declaration.Parent!.Parent!),
                cancellation).ConfigureAwait(false);
            if (module is not null)
                return await DefaultAsync(module, declaration, dontResolveAlias, cancellation).ConfigureAwait(false);
        }
        var export = (ExportDeclarationNode)declaration.Parent!.Parent!;
        var result = export.ModuleSpecifier is not null
            ? await host.ExternalMemberAsync(export, declaration, dontResolveAlias, cancellation).ConfigureAwait(false)
            : name is StringLiteralNode ? null
                : await names.ResolveAsync(
                    name,
                    meaning,
                    dontResolveAlias: dontResolveAlias,
                    cancellation: cancellation).ConfigureAwait(false);
        aliases.MarkTypeOnly(declaration);
        return result;
    }

    internal async ValueTask<Symbol?> ExternalModuleAsync(Symbol? module, bool dontResolveAlias, CancellationToken cancellation = default)
    {
        if (module is not null)
        {
            var exported = await aliases.SymbolAsync(
                module.Exports.GetValueOrDefault(Utf8Literals.ExportEquals),
                dontResolveAlias,
                cancellation).ConfigureAwait(false);
            if (exported is not null)
                return symbols.Merger.GetMergedSymbol(exported);
        }
        return module;
    }

    internal async ValueTask<Symbol?> EsModuleAsync(Symbol? module, SyntaxNode declaration, SyntaxNode specifier,
        CancellationToken cancellation = default)
    {
        var target = await ExternalModuleAsync(module, true, cancellation).ConfigureAwait(false);
        if (AliasResolver.NonLocal(target))
            target = symbols.Merger.GetMergedSymbol(
                await aliases.IndirectionAsync(symbols.Declaration(declaration)!, target!, cancellation).ConfigureAwait(false));
        return target is null
            ? null
            : await host.AdjustEsModuleAsync(module!, target, declaration, specifier, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Symbol?> DefaultAsync(
        Symbol module,
        SyntaxNode declaration,
        bool dontResolveAlias,
        CancellationToken cancellation)
    {
        var result = await host.DefaultExportAsync(module, declaration, dontResolveAlias, cancellation).ConfigureAwait(false);
        aliases.MarkTypeOnly(declaration);
        return result;
    }

    internal async ValueTask<Symbol?> AliasLikeAsync(SyntaxNode expression, CancellationToken cancellation = default)
    {
        if (expression is ClassExpressionNode)
            return await host.AliasExpressionAsync(expression, cancellation).ConfigureAwait(false);
        if (expression is not (IdentifierNode or QualifiedNameNode) && !EntityExpression(expression))
            return null;
        return await names.ResolveAsync(
            expression,
            S.Value | S.Type | S.Namespace,
            true,
            true,
            cancellation: cancellation).ConfigureAwait(false)
            ?? await host.AliasExpressionAsync(expression, cancellation).ConfigureAwait(false);
    }

    internal static SyntaxNode? ModuleSpecifier(SyntaxNode declaration) => declaration switch
    {
        ImportClauseNode or NamespaceExportNode => Specifier(declaration.Parent!),
        ImportEqualsDeclarationNode { ModuleReference: ExternalModuleReferenceNode reference } => reference.Expression,
        ImportEqualsDeclarationNode => null,
        NamespaceImportNode or ExportSpecifierNode => Specifier(declaration.Parent!.Parent!),
        ImportSpecifierNode => Specifier(declaration.Parent!.Parent!.Parent!),
        _ => throw new InvalidOperationException("Declaration has no import/export module specifier")
    };

    internal static SyntaxNode? Specifier(SyntaxNode node) => node switch
    {
        ImportDeclarationNode import => import.ModuleSpecifier,
        ExportDeclarationNode export => export.ModuleSpecifier,
        _ => throw new InvalidOperationException("Not an import or export declaration")
    };

    internal static ImportAttributesNode? Attributes(SyntaxNode node) => node switch
    {
        ImportDeclarationNode import => import.Attributes,
        ExportDeclarationNode export => export.Attributes,
        _ => null
    };

    private static bool EntityExpression(SyntaxNode? expression)
    {
        while (expression is PropertyAccessExpressionNode { Name: IdentifierNode } access)
            expression = access.Expression;
        return expression is IdentifierNode;
    }

    internal static Utf8String? Text(SyntaxNode? name) => name switch
    {
        IdentifierNode identifier => identifier.Text,
        StringLiteralNode literal => literal.Text,
        _ => (Utf8String?)null
    };
}
