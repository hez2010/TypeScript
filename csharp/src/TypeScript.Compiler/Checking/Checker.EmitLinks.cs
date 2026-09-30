using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal Action<SyntaxNode>? BeforeEmitLinkedReference { get; set; }

    internal async ValueTask MarkLinkedReferencesForEmitAsync(SourceFileNode file, CancellationToken cancellation = default)
    {
        if (!EmitParseNode(file))
            return;
        using var query = await EnterQueryAsync(file, cancellation);
        using var transaction = AliasReferences.BeginTransaction();
        var pending = new Stack<SyntaxNode>();
        for (int i = file.ChildCount - 1; i >= 0; i--)
            pending.Push(file.GetChild(i));
        while (pending.TryPop(out var node))
        {
            cancellation.ThrowIfCancellationRequested();
            if (node is ImportDeclarationNode || node is ImportEqualsDeclarationNode && !SemanticSyntax.HasModifier(node, K.ExportKeyword))
                continue;
            BeforeEmitLinkedReference?.Invoke(node);
            await MarkLinkedReferenceForEmitAsync(node, cancellation);
            for (int i = node.ChildCount - 1; i >= 0; i--)
                pending.Push(node.GetChild(i));
        }
        cancellation.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private async ValueTask MarkLinkedReferenceForEmitAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (!CollectEmitAliases
            || (node.Flags & NodeFlags.Ambient) != 0 && node is not (PropertyDeclarationNode or PropertySignatureDeclarationNode)
            || (node.Flags & NodeFlags.InWithStatement) != 0 || QuerySyntax.JsxTag(node) && IntrinsicJsx(node))
            return;
        if (node is IdentifierNode identifier)
        {
            if (!EmitIdentifierCanBeReferenced(identifier))
                return;
            bool candidate = QuerySyntax.Expression(node) || node.Parent is ShorthandPropertyAssignmentNode;
            if (candidate && !(node.Parent is PropertyAccessExpressionNode access && access.Expression == node)
                && !(node.Parent is ExportSpecifierNode { IsTypeOnly: true })
                && !(node.Parent?.Parent?.Parent is ExportDeclarationNode { IsTypeOnly: true }))
            {
                var left = node.Parent switch { PropertyAccessExpressionNode p => p.Expression, QualifiedNameNode q => q.Left, _ => node };
                if (left != node)
                    return;
                await AliasReferences.IdentifierAsync(identifier, cancellation);
                return;
            }
        }
        if (node is PropertyAccessExpressionNode or QualifiedNameNode)
        {
            for (var current = node; current is PropertyAccessExpressionNode or QualifiedNameNode; current = current.Parent!)
                if (QuerySyntax.PartOfType(current))
                    return;
            await MarkEmitPropertyAliasAsync(node, cancellation);
            return;
        }
        if (node is ExportAssignmentNode { Expression: IdentifierNode exported })
        {
            var symbol = program.Symbols.ExportedValue(
                await program.EntityNames.ResolveAsync(exported, SymbolFlags.All, true, true, node, cancellation));
            if (symbol is not null)
                await AliasReferences.MarkAsync(symbol, exported, cancellation);
            return;
        }
        if (node is JsxOpeningElementNode or JsxSelfClosingElementNode or JsxOpeningFragmentNode)
        {
            await MarkJsxFactoryAsync(node, cancellation, directAliasMark: true);
            return;
        }
        if (node is ImportEqualsDeclarationNode import)
        {
            if ((import.ModuleReference is not ExternalModuleReferenceNode external || ExternalModuleSyntax(import, external.Expression))
                && SemanticSyntax.HasModifier(import, K.ExportKeyword))
                await MarkEmitExportAliasAsync(import, cancellation);
            return;
        }
        if (node is ExportSpecifierNode specifier)
        {
            if (!specifier.IsTypeOnly && specifier.Parent?.Parent is ExportDeclarationNode { ModuleSpecifier: null, IsTypeOnly: false }
                && (specifier.PropertyName ?? specifier.Name) is IdentifierNode name)
            {
                var symbol = program.Symbols.NameResolver(cancellation).Resolve(name, name.Text, SymbolFlags.All, isUse: true);
                if (symbol == program.Symbols.UndefinedSymbol || symbol == program.Symbols.GlobalThisSymbol
                    || symbol?.Declarations.FirstOrDefault() is { } declaration
                        && SemanticSyntax.DeclarationContainer(declaration) is SourceFileNode source && program.Symbols.Binding(source)?.IsModule != true)
                    return;
                var target = symbol is not null && (symbol.Flags & SymbolFlags.Alias) != 0
                    ? await program.Aliases.ResolveAsync(symbol, cancellation) : symbol;
                if (target is null || (await program.Aliases.FlagsAsync(target, cancellation: cancellation) & SymbolFlags.Value) != 0)
                {
                    await MarkEmitExportAliasAsync(specifier, cancellation);
                    await AliasReferences.IdentifierAsync(name, cancellation);
                }
            }
            return;
        }
        if (program.Symbols.Program.Configuration.Options.EmitDecoratorMetadata == true
            && HasDecorators(node)
            && CanDecorate(node))
            await DecoratorMetadataAsync(node, ((IModifiedNode)node).Modifiers!.OfType<DecoratorNode>().First(), cancellation);
    }

    private bool EmitIdentifierCanBeReferenced(IdentifierNode node)
    {
        if (node.Parent is ShorthandPropertyAssignmentNode { ObjectAssignmentInitializer: not null } shorthand
            && shorthand.Name == node && ReferenceSyntax.AssignmentTarget(shorthand.Parent!) is null)
            return false;
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is MetaPropertyNode)
                return false;
            if (parent is DecoratorNode { Parent: { } decorated } && !CanDecorate(decorated))
                return false;
            if (parent is ForInOrOfStatementNode { Initializer: VariableDeclarationListNode { Declarations.Count: 0 }, Expression: { } expression }
                && Descendant(node, expression))
                return false;
            if (parent is ComputedPropertyNameNode computed && (computed.Parent is EnumMemberNode
                || computed.Parent is not (GetAccessorDeclarationNode or SetAccessorDeclarationNode)
                    && computed.Parent?.Parent is TypeLiteralNode or ClassDeclarationNode or ClassExpressionNode or InterfaceDeclarationNode
                    && computed.Expression is BinaryExpressionNode { OperatorToken.Kind: K.InKeyword }))
                return false;
            if (parent is HeritageClauseNode heritage)
            {
                if (heritage.Parent is InterfaceDeclarationNode)
                    return false;
                if (SemanticSyntax.ClassLike(heritage.Parent) && heritage.Token == K.ExtendsKeyword)
                {
                    var clauses = heritage.Parent switch
                    {
                        ClassDeclarationNode declaration => declaration.HeritageClauses,
                        ClassExpressionNode classExpression => classExpression.HeritageClauses,
                        _ => null
                    };
                    var first = clauses?.OfType<HeritageClauseNode>()
                        .FirstOrDefault(h => h.Token == K.ExtendsKeyword)?.Types?.FirstOrDefault();
                    if (first is not null && !Descendant(node, first))
                        return false;
                }
            }
        }
        return true;

        static bool Descendant(SyntaxNode node, SyntaxNode ancestor)
        {
            for (SyntaxNode? current = node; current is not null; current = current.Parent)
                if (current == ancestor)
                    return true;
            return false;
        }
    }

    private async ValueTask MarkEmitExportAliasAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (program.Symbols.Declaration(node) is not { } symbol)
            return;
        var target = await program.Aliases.ResolveAsync(symbol, cancellation);
        if (target == UnknownSymbol || (await program.Aliases.FlagsAsync(symbol, true, cancellation: cancellation) & SymbolFlags.Value) != 0
            && (target.Flags & (SymbolFlags.ConstEnum | SymbolFlags.ConstEnumOnlyModule)) == 0)
            await AliasReferences.MarkDirectAsync(symbol, cancellation);
    }

    private async ValueTask MarkEmitPropertyAliasAsync(SyntaxNode node, CancellationToken cancellation)
    {
        for (var current = node; current.Parent is not null; current = current.Parent)
            if (current.Parent is ImportEqualsDeclarationNode import && current == import.ModuleReference)
                return;
        var left = node is PropertyAccessExpressionNode access ? access.Expression : ((QualifiedNameNode)node).Left;
        if (left is not IdentifierNode identifier || identifier.Text == Utf8Literals.This)
            return;
        var parent = ResolveReference(identifier, cancellation);
        if (parent == UnknownSymbol)
            return;
        if (IsolatedModules || PreserveEmitConstEnums && EmitExportExpression(node))
        {
            await AliasReferences.MarkAsync(parent, node, cancellation);
            return;
        }
        var type = await CachedExpressionAsync(left, 0, cancellation);
        if ((type.Flags & TypeFlags.Any) != 0 || type == context.SilentNeverType)
        {
            await AliasReferences.MarkAsync(parent, node, cancellation);
            return;
        }
        var right = node is PropertyAccessExpressionNode property ? property.Name! : ((QualifiedNameNode)node).Right!;
        bool widen = ReferenceSyntax.AssignmentKind(node) != 0 || node.Parent is CallExpressionNode call && call.Expression == node;
        var apparent = await Views.ApparentAsync(widen ? await Widening.GetAsync(type, cancellation) : type, cancellation);
        Symbol? member;
        if (right is PrivateIdentifierNode)
        {
            var lexical = ResolveReference(right, cancellation);
            member = lexical == UnknownSymbol ? null : await Properties.PropertyAsync(apparent, lexical.Name, cancellation: cancellation);
        }
        else
            member = await Properties.PropertyAsync(apparent, ((IdentifierNode)right).Text, cancellation: cancellation);
        await AliasReferences.PropertyAsync(node, member, type, cancellation);
    }

    private static bool EmitExportExpression(SyntaxNode location)
    {
        for (var node = location; node is not null; node = node.Parent)
            if (node.Parent is ExportAssignmentNode export && export.Expression == node
                && node is IdentifierNode or PropertyAccessExpressionNode
                || node.Parent is ExportSpecifierNode specifier && (specifier.Name == node || specifier.PropertyName == node))
                return true;
        return false;
    }
}
