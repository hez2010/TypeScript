using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private static bool EmitParseNode(SyntaxNode? node) => node is not null && (node.Flags & NodeFlags.Synthesized) == 0;

    internal async ValueTask<bool> IsImplementationOfOverloadAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        if (!EmitParseNode(node))
            return false;
        using var query = await EnterQueryAsync(node, cancellation);
        if (SemanticSyntax.Body(node) is not { } body
            || body.End <= body.Pos
            || node is GetAccessorDeclarationNode or SetAccessorDeclarationNode)
            return false;
        var signatures = await Signatures.OfSymbolAsync(program.Symbols.Declaration(node), cancellation);
        if (signatures.Count > 1)
            return true;
        return signatures.Count == 1 && signatures[0] != await FullSignatureAsync(node, cancellation)
            && signatures[0].Declaration is { } declaration && declaration != node && (declaration.Flags & NodeFlags.JSDoc) == 0;
    }

    internal async ValueTask<bool> IsLateBoundDeclarationAsync(SyntaxNode? node, CancellationToken cancellation = default)
    {
        if (!EmitParseNode(node))
            return false;
        using var query = await EnterQueryAsync(node, cancellation);
        return program.Symbols.Declaration(node!) is { } symbol && (symbol.CheckFlags & Binding.CheckFlags.Late) != 0;
    }

    internal async ValueTask<bool> IsLiteralConstDeclarationAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        if (!EmitParseNode(node))
            return false;
        using var query = await EnterQueryAsync(node, cancellation);
        return (VariableTypes.Readonly(node) || node is VariableDeclarationNode && VariableTypes.Constant(node))
            && program.Symbols.Declaration(node) is { } symbol && (await Values.GetAsync(symbol, cancellation)).IsFreshLiteral;
    }

    internal async ValueTask<bool> IsGlobalSymbolObjectReferenceAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        if (node is not PropertyAccessExpressionNode { Name: IdentifierNode } access)
            return false;
        using var query = await EnterQueryAsync(node, cancellation);
        return access.Expression switch
        {
            IdentifierNode { Text: "Symbol" } identifier => ResolveReference(identifier, cancellation)
                == program.Symbols.Lookup(program.Symbols.Globals, "Symbol", SymbolFlags.Value | SymbolFlags.ExportValue),
            PropertyAccessExpressionNode { Expression: IdentifierNode { Text: "globalThis" } global, Name: IdentifierNode { Text: "Symbol" } }
                => ResolveReference(global, cancellation) == program.Symbols.GlobalThisSymbol,
            _ => false
        };
    }

    internal async ValueTask<SourceFileNode?> GetExternalModuleFileForEmitAsync(
        SyntaxNode declaration,
        CancellationToken cancellation = default)
    {
        if (!EmitParseNode(declaration))
            return null;
        using var query = await EnterQueryAsync(declaration, cancellation);
        return await ExternalModuleFileForEmitAsync(declaration, cancellation);
    }

    private async ValueTask<SourceFileNode?> ExternalModuleFileForEmitAsync(SyntaxNode declaration, CancellationToken cancellation)
    {
        var specifier = declaration switch
        {
            ImportDeclarationNode import => import.ModuleSpecifier,
            ExportDeclarationNode export => export.ModuleSpecifier,
            ImportEqualsDeclarationNode { ModuleReference: ExternalModuleReferenceNode reference } => reference.Expression,
            ModuleDeclarationNode { Name: StringLiteralNode name } => name,
            ImportTypeNode { Argument: LiteralTypeNode { Literal: StringLiteralNode literal } } => literal,
            CallExpressionNode { Arguments: { Count: > 0 } arguments } => arguments[0],
            _ => null
        };
        if (specifier is null)
            return null;
        var attributes = declaration is ImportTypeNode importType ? importType.Attributes : AliasTargets.Attributes(declaration);
        var module = await ResolveImportModuleAsync(specifier, specifier,
            attributes is null ? null : await ImportAttributesExpressionAsync(attributes, cancellation), cancellation,
            reportUnresolved: false);
        return module?.Declarations.OfType<SourceFileNode>().FirstOrDefault();
    }

    internal async ValueTask<bool> IsImportRequiredByAugmentationAsync(
        ImportDeclarationNode declaration,
        CancellationToken cancellation = default)
    {
        if (!EmitParseNode(declaration))
            return false;
        using var query = await EnterQueryAsync(declaration, cancellation);
        var file = SemanticSyntax.Source(declaration)!;
        var fileSymbol = program.Symbols.Binding(file)?.Symbol;
        if (fileSymbol is null)
            return false;
        var target = await ExternalModuleFileForEmitAsync(declaration, cancellation);
        if (target is null || target == file)
            return false;
        foreach (var symbol in (await ExportsAsync(fileSymbol, cancellation)).Values)
        {
            var merged = program.Symbols.Merger.GetMergedSymbol(symbol)!;
            if (merged != symbol && merged.Declarations.Any(d => SemanticSyntax.Source(d) == target))
                return true;
        }
        return false;
    }

    internal async ValueTask<bool> IsOptionalParameterForEmitAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(node, cancellation);
        return node is ParameterDeclarationNode parameter && await OptionalSyntaxParameterAsync(parameter, cancellation);
    }

    internal async ValueTask<bool> RequiresImplicitUndefinedForEmitAsync(SyntaxNode declaration, Symbol? symbol, SyntaxNode? enclosing,
        CancellationToken cancellation = default)
    {
        if (!EmitParseNode(declaration))
            return false;
        using var query = await EnterQueryAsync(declaration, cancellation);
        if (enclosing is not null)
            RequireNode(enclosing);
        if (declaration is ParameterDeclarationNode parameter)
            return await ParameterRequiresImplicitUndefinedAsync(parameter, enclosing, cancellation);
        if (declaration is PropertyDeclarationNode or PropertySignatureDeclarationNode)
        {
            symbol ??= program.Symbols.Declaration(declaration);
            if (symbol is null)
                return false;
            var type = await Values.GetAsync(symbol, cancellation);
            return (symbol.Flags & (SymbolFlags.Property | SymbolFlags.Optional)) == (SymbolFlags.Property | SymbolFlags.Optional)
                && declaration is (PropertyDeclarationNode { PostfixToken.Kind: SyntaxKind.QuestionToken }
                    or PropertySignatureDeclarationNode { PostfixToken.Kind: SyntaxKind.QuestionToken })
                && links.ReverseMappedSymbols.TryGet(symbol)?.MappedType is not null
                && (type is UnionType union ? union.Types.Any(t => t == context.UndefinedType) : type == context.UndefinedType);
        }
        throw new ArgumentException("Expected a parameter or property declaration", nameof(declaration));
    }

    private async ValueTask<bool> ParameterRequiresImplicitUndefinedAsync(ParameterDeclarationNode parameter, SyntaxNode? enclosing,
        CancellationToken cancellation)
    {
        if (!context.StrictNullChecks)
            return false;
        bool optional = await OptionalSyntaxParameterAsync(parameter, cancellation);
        bool property = parameter.Modifiers?.Any(m => m.Kind is SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword
            or SyntaxKind.ProtectedKeyword or SyntaxKind.ReadonlyKeyword) == true;
        if (!(parameter.Initializer is not null && !optional && (!property || SemanticSyntax.FunctionDeclarationLike(enclosing))
            || optional && parameter.Initializer is null && property))
            return false;
        if (parameter.Type is not { } annotation)
            return true;
        var type = await Nodes.FromNodeAsync(annotation, cancellation);
        return type != context.ErrorType && (type is UnionType union
            ? union.Types.All(t => (t.Flags & TypeFlags.Undefined) == 0) : (type.Flags & TypeFlags.Undefined) == 0);
    }

    private bool CollectEmitAliases => program.Symbols.Program.Configuration.Options.Boolean("verbatimModuleSyntax") != true;
    private bool PreserveEmitConstEnums => program.Symbols.Program.Configuration.Options.Boolean("preserveConstEnums") == true
        || program.Symbols.Program.Configuration.Options.Boolean("isolatedModules") == true || !CollectEmitAliases;

    internal async ValueTask<bool> IsReferencedAliasForEmitAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        if (!CollectEmitAliases || !EmitParseNode(node))
            return true;
        using var query = await EnterQueryAsync(node, cancellation);
        if (!ReferenceResolver.IsAliasDeclaration(node) || program.Symbols.Declaration(node) is not { } symbol)
            return false;
        var data = links.Aliases.Get(symbol);
        if (data.Referenced)
            return true;
        return data.AliasTarget is { } target && SemanticSyntax.HasModifier(node, SyntaxKind.ExportKeyword)
            && (await program.Aliases.FlagsAsync(target, cancellation: cancellation) & SymbolFlags.Value) != 0
            && (PreserveEmitConstEnums || (target.Flags & (SymbolFlags.ConstEnum | SymbolFlags.ConstEnumOnlyModule)) == 0);
    }

    internal async ValueTask<bool> IsValueAliasForEmitAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        if (!CollectEmitAliases || !EmitParseNode(node))
            return true;
        using var query = await EnterQueryAsync(node, cancellation);
        return await ValueAliasForEmitAsync(node, cancellation);
    }

    private async ValueTask<bool> ValueAliasForEmitAsync(SyntaxNode node, CancellationToken cancellation)
    {
        switch (node)
        {
            case ImportEqualsDeclarationNode:
                return await AliasValueForEmitAsync(program.Symbols.Declaration(node), false, cancellation);
            case ImportClauseNode or NamespaceImportNode or ImportSpecifierNode or ExportSpecifierNode:
                return await AliasValueForEmitAsync(program.Symbols.Declaration(node), true, cancellation);
            case ExportDeclarationNode { ExportClause: NamespaceExportNode }:
                return true;
            case ExportDeclarationNode { ExportClause: NamedExportsNode { Elements: { } elements } }:
                foreach (var element in elements)
                    if (await ValueAliasForEmitAsync(element, cancellation))
                        return true;
                return false;
            case ExportAssignmentNode assignment:
                return assignment.Expression is not IdentifierNode
                    || await AliasValueForEmitAsync(program.Symbols.Declaration(node), true, cancellation);
            case BinaryExpressionNode { Right: IdentifierNode } when CommonJsVisibilityExport(node):
                return await AliasValueForEmitAsync(program.Symbols.Declaration(node), true, cancellation);
            default:
                return false;
        }
    }

    private async ValueTask<bool> AliasValueForEmitAsync(Symbol? symbol, bool excludeTypeOnly, CancellationToken cancellation)
    {
        if (symbol is null)
            return false;
        if (symbol.ValueDeclaration is { } declaration && SemanticSyntax.Source(declaration) is { } file)
            await program.AliasTargets.ExternalModuleAsync(program.Symbols.Declaration(file), false, cancellation);
        var target = program.Symbols.ExportedValue(await program.Aliases.ResolveAsync(symbol, cancellation))!;
        if (target == UnknownSymbol)
            return !excludeTypeOnly || await program.Aliases.TypeOnlyAsync(symbol, cancellation: cancellation) is null;
        return (await program.Aliases.FlagsAsync(symbol, excludeTypeOnly, true, cancellation) & SymbolFlags.Value) != 0
            && (PreserveEmitConstEnums || (target.Flags & (SymbolFlags.ConstEnum | SymbolFlags.ConstEnumOnlyModule)) == 0);
    }

    internal async ValueTask<bool> IsTopLevelValueImportEqualsAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        if (!CollectEmitAliases)
            return true;
        if (!EmitParseNode(node))
            return false;
        using var query = await EnterQueryAsync(node, cancellation);
        return node is ImportEqualsDeclarationNode { Parent: SourceFileNode, ModuleReference: { } reference }
            && reference is not ExternalModuleReferenceNode && reference.End > reference.Pos
            && await AliasValueForEmitAsync(program.Symbols.Declaration(node), false, cancellation);
    }

    internal async ValueTask<bool> IsNameResolvableForEmitAsync(SyntaxNode location, string name, CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(location, cancellation);
        return program.Symbols.NameResolver(cancellation).Resolve(location, name,
            SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace) is not null;
    }
}
