using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private Symbol? importMetaProperty;

    internal async ValueTask<Symbol?> GetSymbolAtLocationAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        await queryGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            RequireNode(node);
            RequireUsable();
            return await SymbolAtLocationAsync(QuerySyntax.Reparsed(node), cancellation).ConfigureAwait(false);
        }
        finally
        {
            queryGate.Release();
        }
    }

    // Tool-facing lookup follows syntax context. Checker algorithms use their
    // context-specific declaration, alias and expression services directly.
    private async ValueTask<Symbol?> SymbolAtLocationAsync(SyntaxNode node, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (node is SourceFileNode)
            return program.Symbols.Binding(node)?.IsModule == true ? program.Symbols.Declaration(node) : null;
        if ((node.Flags & NodeFlags.InWithStatement) != 0)
            return null;
        var parent = node.Parent;
        var grandParent = parent?.Parent;
        if (QuerySyntax.DeclarationOrImportName(node))
        {
            var symbol = program.Symbols.Declaration(parent!);
            return symbol is not null && (parent is ImportSpecifierNode import && import.PropertyName == node
                || parent is ExportSpecifierNode export && export.PropertyName == node)
                    ? await program.Aliases.ImmediateAsync(symbol, cancellation) : symbol;
        }
        if (node is StringLiteralNode or NumericLiteralNode or NoSubstitutionTemplateLiteralNode
            && parent is ComputedPropertyNameNode && QuerySyntax.Declaration(grandParent))
            return program.Symbols.Declaration(grandParent!);
        if (node is IdentifierNode identifier)
        {
            if (QuerySyntax.ImportOrExportAssignment(node))
                return await NameSymbolAsync(node, cancellation);
            if (parent is BindingElementNode binding && grandParent?.Kind == SyntaxKind.ObjectBindingPattern
                && binding.PropertyName == node)
            {
                var property = await Properties.PropertyAsync(await TypeAtLocationAsync(grandParent, cancellation),
                    identifier.Text, cancellation: cancellation);
                if (property is not null)
                    return property;
            }
            else if (parent is MetaPropertyNode meta && meta.Name == node)
            {
                if (meta.KeywordToken == SyntaxKind.NewKeyword && identifier.Text == "target")
                    return (await ValueExpressions.MetaAsync(meta, cancellation)).Symbol;
                if (meta.KeywordToken == SyntaxKind.ImportKeyword && identifier.Text == "meta")
                    return await ImportMetaPropertyAsync(cancellation);
                return null;
            }
            else if (parent?.Kind == SyntaxKind.JSDocParameterTag && SemanticSyntax.Name(parent) == node)
            {
                var host = DocumentationHost(node);
                if (host is IFunctionSignature { Parameters: { } parameters })
                    foreach (var parameter in parameters)
                        if (SemanticSyntax.Name(parameter) is IdentifierNode name && name.Text == identifier.Text)
                            return program.Symbols.Declaration(parameter);
            }
        }
        switch (node.Kind)
        {
            case SyntaxKind.Identifier or SyntaxKind.PrivateIdentifier or SyntaxKind.PropertyAccessExpression or SyntaxKind.QualifiedName:
                if (!FlowReferences.ThisInQuery(node))
                    return await NameSymbolAsync(node, cancellation);
                goto case SyntaxKind.ThisKeyword;
            case SyntaxKind.ThisKeyword:
                var container = MissingNamePrefixes.ThisContainer(node, false, false);
                if (container is IFunctionSignature
                    && (await Signatures.FromDeclarationAsync(container, cancellation)).ThisParameter is { } thisParameter)
                    return thisParameter;
                if (QuerySyntax.Expression(node))
                    return (await Expressions.CheckAsync(node, cancellation: cancellation)).Symbol;
                goto case SyntaxKind.ThisType;
            case SyntaxKind.ThisType:
                return (await Nodes.FromNodeAsync(node, cancellation)).Symbol;
            case SyntaxKind.SuperKeyword or SyntaxKind.MetaProperty:
                return (await Expressions.CheckAsync(node, cancellation: cancellation)).Symbol;
            case SyntaxKind.ConstructorKeyword:
                return parent is ConstructorDeclarationNode ? program.Symbols.Declaration(parent.Parent!) : null;
            case SyntaxKind.StringLiteral or SyntaxKind.NoSubstitutionTemplateLiteral:
                if (ModuleSpecifierLocation(node))
                    return await ResolveImportModuleAsync(node, node, await ModuleSpecifierAttributesAsync(node, cancellation),
                        cancellation, ignoreErrors: true);
                if (parent is CallExpressionNode
                    {
                        Expression: PropertyAccessExpressionNode { Expression: IdentifierNode { Text: "Object" }, Name: IdentifierNode { Text: "defineProperty" } },
                        Arguments: { Count: 3 } arguments
                    } && arguments[1] == node)
                    return program.Symbols.Declaration(parent);
                goto case SyntaxKind.NumericLiteral;
            case SyntaxKind.NumericLiteral:
                Type? objectType = parent is ElementAccessExpressionNode element && element.ArgumentExpression == node
                    ? await ExpressionTypeForQueryAsync(element.Expression!, cancellation)
                    : parent is LiteralTypeNode && grandParent is IndexedAccessTypeNode indexed
                        ? await Nodes.FromNodeAsync(indexed.ObjectType!, cancellation) : null;
                return objectType is null ? null
                    : await Properties.PropertyAsync(objectType, node switch
                    {
                        StringLiteralNode text => text.Text,
                        NoSubstitutionTemplateLiteralNode text => text.Text,
                        NumericLiteralNode number => number.Text,
                        _ => throw new InvalidOperationException("Expected a literal property key")
                    }, cancellation: cancellation);
            case SyntaxKind.DefaultKeyword or SyntaxKind.FunctionKeyword or SyntaxKind.EqualsGreaterThanToken or SyntaxKind.ClassKeyword:
                return parent is null ? null : program.Symbols.Declaration(parent);
            case SyntaxKind.ImportType:
                return node is ImportTypeNode { Argument: LiteralTypeNode { Literal: StringLiteralNode literal } }
                    ? await SymbolAtLocationAsync(literal, cancellation) : null;
            case SyntaxKind.ExportKeyword:
                return parent is ExportAssignmentNode ? program.Symbols.Binding(parent)?.Get(parent)?.Symbol : null;
            case SyntaxKind.InstanceOfKeyword:
                if (parent is BinaryExpressionNode binary)
                {
                    var type = await ExpressionTypeForQueryAsync(binary.Right!, cancellation);
                    return (await HasInstanceMethodAsync(type, cancellation))?.Symbol ?? type.Symbol;
                }
                return null;
            case SyntaxKind.JsxNamespacedName:
                return QuerySyntax.JsxTag(node) ? await IntrinsicTagSymbolAsync(parent!, cancellation) : null;
            default:
                return null;
        }
    }

    private async ValueTask<Symbol?> NameSymbolAsync(SyntaxNode name, CancellationToken cancellation)
    {
        if (QuerySyntax.DeclarationName(name))
            return program.Symbols.Declaration(name.Parent!);
        if (name.Parent is ExportAssignmentNode && EntityExpression(name))
        {
            var symbol = await program.EntityNames.ResolveAsync(
                name,
                SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace | SymbolFlags.Alias,
                true, cancellation: cancellation);
            if (symbol is not null && symbol != UnknownSymbol)
                return symbol;
        }
        else if (name is IdentifierNode or QualifiedNameNode && QuerySyntax.ImportOrExportAssignment(name))
        {
            if (name is IdentifierNode && QuerySyntax.RightSide(name))
                name = name.Parent!;
            return await program.EntityNames.ResolveAsync(name, name is IdentifierNode || name.Parent is QualifiedNameNode
                ? SymbolFlags.Namespace : SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace,
                dontResolveAlias: true, cancellation: cancellation);
        }
        if (name is IdentifierNode or QualifiedNameNode)
        {
            var root = name;
            while (root.Parent is QualifiedNameNode)
                root = root.Parent;
            if (root.Parent is ImportTypeNode import && import.Qualifier == root)
            {
                await Nodes.FromNodeAsync(import, cancellation);
                var symbol = links.SymbolNodes.Get(name).ResolvedSymbol;
                return symbol == UnknownSymbol ? null : symbol;
            }
        }
        while (QuerySyntax.RightSide(name))
            name = name.Parent!;
        var outerName = name;
        while (outerName.Parent is PropertyAccessExpressionNode or QualifiedNameNode)
            outerName = outerName.Parent;
        bool heritageReference = outerName.Parent is TypeReferenceNode { Parent: HeritageClauseNode } reference
            && reference.TypeName == outerName;
        if (outerName.Parent is ExpressionWithTypeArgumentsNode || heritageReference)
        {
            var meaning = name.Parent is ExpressionWithTypeArgumentsNode or TypeReferenceNode
                ? QuerySyntax.PartOfType(name) ? SymbolFlags.Type : SymbolFlags.Value : SymbolFlags.Namespace;
            if (name.Parent is ExpressionWithTypeArgumentsNode { Parent: HeritageClauseNode { Token: SyntaxKind.ExtendsKeyword } clause }
                && SemanticSyntax.ClassLike(clause.Parent))
                meaning |= SymbolFlags.Value;
            if (EntityExpression(name)
                && await program.EntityNames.ResolveAsync(
                    name,
                    meaning | SymbolFlags.Alias,
                    true,
                    cancellation: cancellation) is { } symbol)
                return symbol;
        }
        bool documentation = (name.Flags & NodeFlags.JSDoc) != 0 && DeclarationOrder.Ancestor(name, n => n.Kind is
            SyntaxKind.JSDocNameReference or SyntaxKind.JSDocLink or SyntaxKind.JSDocLinkCode or SyntaxKind.JSDocLinkPlain) is not null;
        if (QuerySyntax.Expression(name))
        {
            if (name.Pos >= 0 && name.Pos == name.End)
                return null;
            if (name is IdentifierNode identifier)
            {
                if (QuerySyntax.JsxTag(name) && IntrinsicJsx(name))
                    return await IntrinsicTagSymbolAsync(name.Parent!, cancellation);
                var meaning = documentation ? SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace : SymbolFlags.Value;
                var symbol = await program.EntityNames.ResolveAsync(name, meaning, true, true,
                    documentation ? DocumentationHost(name) : null, cancellation);
                if (symbol is null && documentation
                    && DeclarationOrder.Ancestor(name, n => SemanticSyntax.ClassLike(n) || n is InterfaceDeclarationNode) is { } owner)
                {
                    var container = program.Symbols.Declaration(owner)!;
                    symbol = program.Symbols.Merger.GetMergedSymbol(
                        program.Symbols.Lookup(
                        await program.ExportsAsync(container, cancellation),
                        identifier.Text, meaning)) ?? await Properties.PropertyAsync(await Declared.GetAsync(container, cancellation),
                            identifier.Text, cancellation: cancellation);
                }
                return symbol;
            }
            if (name is PrivateIdentifierNode)
            {
                var symbol = ResolveReference(name, cancellation);
                return symbol == UnknownSymbol ? null : symbol;
            }
            if (name is PropertyAccessExpressionNode or QualifiedNameNode)
            {
                var data = links.SymbolNodes.Get(name);
                if (data.ResolvedSymbol is not null)
                    return data.ResolvedSymbol;
                if (name is PropertyAccessExpressionNode access)
                {
                    await Access.PropertyAsync(access, cancellation: cancellation);
                    if (data.ResolvedSymbol is null && access.Name is not PrivateIdentifierNode)
                        data.ResolvedSymbol = await ApplicableIndexSymbolAsync(
                            await CachedExpressionAsync(access.Expression!, 0, cancellation),
                            await LiteralNameTypeAsync(access.Name!, cancellation), cancellation);
                }
                else
                    await Access.QualifiedAsync((QualifiedNameNode)name, cancellation: cancellation);
                return data.ResolvedSymbol ?? (documentation && name is QualifiedNameNode
                    ? await DocumentationMemberAsync(name, cancellation) : null);
            }
        }
        else if (name is IdentifierNode or QualifiedNameNode)
        {
            var root = name;
            while (root.Parent is QualifiedNameNode)
                root = root.Parent;
            if (root.Parent is TypeReferenceNode)
            {
                var symbol = await program.EntityNames.ResolveAsync(
                    name,
                    name.Parent is TypeReferenceNode ? SymbolFlags.Type : SymbolFlags.Namespace,
                    true, true, cancellation: cancellation);
                return symbol is not null && symbol != UnknownSymbol ? symbol : heritageReference ? null : References.Unresolved(name);
            }
        }
        return name.Parent is TypePredicateNode ? await program.EntityNames.ResolveAsync(name,
            SymbolFlags.FunctionScopedVariable, true, cancellation: cancellation) : null;
    }

    private static bool EntityExpression(SyntaxNode node)
    {
        while (node is PropertyAccessExpressionNode access && access.Name is IdentifierNode)
            node = access.Expression!;
        return node is IdentifierNode;
    }

    private static SyntaxNode? DocumentationHost(SyntaxNode node)
    {
        var owner = DeclarationOrder.Ancestor(node.Parent, n => n is JSDocNode)?.Parent;
        if (owner is PropertySignatureDeclarationNode { Type: IFunctionSignature } property)
            return property.Type;
        return owner is IFunctionSignature ? owner : null;
    }

    private async ValueTask<Symbol?> DocumentationMemberAsync(SyntaxNode name, CancellationToken cancellation)
    {
        var pending = new Stack<QualifiedNameNode>();
        Symbol? symbol;
        while ((symbol = await program.EntityNames.ResolveAsync(name, SymbolFlags.Type | SymbolFlags.Namespace | SymbolFlags.Value,
            true, true, DocumentationHost(name), cancellation)) is null && name is QualifiedNameNode qualified)
        {
            pending.Push(qualified);
            name = qualified.Left!;
        }
        while (symbol is not null && pending.TryPop(out var qualified))
        {
            Type? type = null;
            if ((symbol.Flags & SymbolFlags.Value) != 0
                && await Properties.PropertyAsync(
                    await Values.GetAsync(symbol, cancellation),
                    "prototype",
                    cancellation: cancellation) is { } prototype)
                type = await Values.GetAsync(prototype, cancellation);
            type ??= await Declared.GetAsync(symbol, cancellation);
            symbol = await Properties.PropertyAsync(type, ((IdentifierNode)qualified.Right!).Text, cancellation: cancellation);
        }
        return symbol;
    }

    private async ValueTask<Symbol> ImportMetaPropertyAsync(CancellationToken cancellation)
    {
        if (importMetaProperty is not null)
            return importMetaProperty;
        var type = await ImportMetaTypeAsync(cancellation);
        var owner = new Symbol(SymbolFlags.Transient, "ImportMetaExpression");
        var symbol = new Symbol(SymbolFlags.Property | SymbolFlags.Transient, "meta") { Parent = owner, CheckFlags = CheckFlags.Readonly };
        owner.MemberTable.Add("meta", symbol);
        links.Values.Get(symbol).ResolvedType = type;
        cancellation.ThrowIfCancellationRequested();
        return importMetaProperty = symbol;
    }

    private async ValueTask<Symbol?> IntrinsicTagSymbolAsync(SyntaxNode opening, CancellationToken cancellation)
    {
        await JsxIntrinsicTypeAsync(opening, cancellation);
        var symbol = links.SymbolNodes.Get(opening).ResolvedSymbol;
        return symbol == UnknownSymbol ? null : symbol;
    }

    private async ValueTask<Symbol?> ApplicableIndexSymbolAsync(Type type, Type key, CancellationToken cancellation)
    {
        var indexes = await IndexesAsync(type, cancellation);
        var info = await IndexSignatures.ApplicableAsync(indexes, key, cancellation);
        if (info is null || info == Members.AnyBaseIndex)
            return null;
        if (info.IndexSymbol is not null)
            return info.IndexSymbol;
        var declarations = new List<SyntaxNode>();
        if (info.Declaration is not null)
            declarations.Add(info.Declaration);
        else
            foreach (var index in indexes)
                if (index.Declaration is not null && await IndexSignatures.ApplicableTypeAsync(key, index.KeyType, cancellation))
                    declarations.Add(index.Declaration);
        if (declarations.Count == 0)
            return null;
        var symbol = new Symbol(SymbolFlags.Property | SymbolFlags.Transient, Symbol.InternalPrefix + "index")
        { CheckFlags = CheckFlags.IndexSymbol, Parent = type.Symbol, ValueDeclaration = declarations[0] };
        symbol.DeclarationList.AddRange(declarations);
        links.Values.Get(symbol).ResolvedType = info.ValueType;
        cancellation.ThrowIfCancellationRequested();
        return info.IndexSymbol = symbol;
    }

    private static bool ModuleSpecifierLocation(SyntaxNode node) => node.Parent is ImportDeclarationNode import
        && import.ModuleSpecifier == node
        || node.Parent is ExportDeclarationNode export && export.ModuleSpecifier == node
        || node.Parent is ExternalModuleReferenceNode && node.Parent.Parent is ImportEqualsDeclarationNode
        || node.Parent?.Parent is VariableDeclarationNode { Initializer: { } initializer } && SemanticSyntax.RequireCall(initializer)
        || node.Parent is CallExpressionNode call && IsImportCall(call)
        || node.Parent is LiteralTypeNode literal && literal.Parent is ImportTypeNode { Argument: var argument } && argument == literal;

    private async ValueTask<Type?> ModuleSpecifierAttributesAsync(SyntaxNode node, CancellationToken cancellation)
    {
        ImportAttributesNode? attributes = node.Parent switch
        {
            ImportDeclarationNode import => import.Attributes,
            ExportDeclarationNode export => export.Attributes,
            LiteralTypeNode { Parent: ImportTypeNode import } => import.Attributes,
            _ => null
        };
        if (attributes is not null)
            return await ImportAttributesExpressionAsync(attributes, cancellation);
        if (node.Parent is CallExpressionNode { Arguments: { Count: > 1 } arguments } call && IsImportCall(call)
            && await Properties.PropertyAsync(
                await CachedExpressionAsync(arguments[1], 0, cancellation),
                "with",
                cancellation: cancellation) is { } property)
            return await Values.GetAsync(property, cancellation);
        return null;
    }
}
