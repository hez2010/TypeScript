using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

internal enum ReferenceUse { Highlights, Other, References, Rename }
internal readonly record struct ReferenceOptions(ReferenceUse Use, bool Implementations = false, bool UseAliasesForRename = true)
{
    internal bool RenameWithAliases => Use == ReferenceUse.Rename && UseAliasesForRename;
}
internal enum ReferenceDefinitionKind { Symbol, Label, Keyword, This, String, TripleSlash }
internal enum ReferenceEntryKind { None, Range, Node, StringLiteral, SearchedLocalFoundProperty, SearchedPropertyFoundLocal }
internal sealed class ReferenceGroup(ReferenceDefinitionKind kind, SyntaxNode? node, Symbol? symbol)
{
    internal ReferenceDefinitionKind Kind { get; } = kind;
    internal SyntaxNode? Node { get; } = node;
    internal Symbol? Symbol { get; } = symbol;
    internal List<ReferenceEntry> Entries { get; } = [];
}
internal sealed record ReferenceEntry(ReferenceEntryKind Kind, SyntaxNode? Node, SyntaxNode? Context,
    SourceFileNode? File = null, int Start = -1, int End = -1)
{
    internal static ReferenceEntry FromNode(SyntaxNode node, ReferenceEntryKind kind = ReferenceEntryKind.Node) =>
        new(kind, node.DeclarationName ?? node, ReferenceNavigation.Context(node));
}

internal static class ReferenceNavigation
{
    internal const int Value = 1, Type = 2, Namespace = 4, All = Value | Type | Namespace;

    internal static async ValueTask<int> IntersectingMeaningAsync(SyntaxNode node, Symbol symbol, CancellationToken cancellation)
    {
        int meaning = await MeaningAsync(node, cancellation), previous;
        do
        {
            previous = meaning;
            foreach (var declaration in symbol.Declarations)
            {
                int declared = DeclarationMeaning(declaration);
                if ((meaning & declared) != 0) meaning |= declared;
            }
        } while (meaning != previous);
        return meaning;
    }

    internal static bool IsWriteAccess(SyntaxNode node)
    {
        var declaration = QuerySyntax.DeclarationName(node) ? node.Parent
            : node is StringLiteralNode or NoSubstitutionTemplateLiteralNode or NumericLiteralNode && node.Parent is ComputedPropertyNameNode ? node.Parent.Parent
            : node.Parent is QualifiedNameNode { Parent: JSDocParameterOrPropertyTagNode { Kind: K.JSDocParameterTag } tag } qualified && tag.Name == qualified ? tag
            : node.Parent?.Parent is BinaryExpressionNode binary && SyntaxLanguageService.IsAssignmentDeclaration(binary)
                && (binary.Left?.BindingSymbol is not null || binary.BindingSymbol is not null) && SyntaxLanguageService.Name(binary) == node ? binary : null;
        return declaration is not null && DeclarationWrites(declaration) || node.Kind == K.DefaultKeyword || ReferenceSyntax.AccessKind(node) != 0;
    }
    private static bool DeclarationWrites(SyntaxNode node)
    {
        if ((node.Flags & NodeFlags.Ambient) != 0) return true;
        if (node is PropertyAssignmentNode) return !DestructuringPattern(node.Parent);
        if (node is FunctionDeclarationNode or FunctionExpressionNode or ConstructorDeclarationNode or MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode) return SemanticSyntax.Body(node) is not null;
        if (node is VariableDeclarationNode or PropertyDeclarationNode)
            return ((IInitializedNode)node).Initializer is not null || node.Parent is CatchClauseNode;
        return node.Kind is K.BinaryExpression or K.BindingElement or K.ClassDeclaration or K.ClassExpression or K.DefaultKeyword
            or K.EnumDeclaration or K.EnumMember or K.ExportSpecifier or K.ImportClause or K.ImportEqualsDeclaration or K.ImportSpecifier
            or K.InterfaceDeclaration or K.JSDocCallbackTag or K.JSDocTypedefTag or K.JsxAttribute or K.ModuleDeclaration
            or K.NamespaceExportDeclaration or K.NamespaceImport or K.NamespaceExport or K.Parameter or K.ShorthandPropertyAssignment
            or K.TypeAliasDeclaration or K.JSTypeAliasDeclaration or K.TypeParameter;
    }
    private static bool DestructuringPattern(SyntaxNode? node)
    {
        while (node is ObjectLiteralExpressionNode or ArrayLiteralExpressionNode)
        {
            if (node.Parent is BinaryExpressionNode binary && binary.Left == node && binary.OperatorToken?.Kind == K.EqualsToken
                || node.Parent is ForInOrOfStatementNode { Kind: K.ForOfStatement } loop && loop.Initializer == node) return true;
            node = node.Parent is PropertyAssignmentNode property ? property.Parent : node.Parent;
        }
        return false;
    }

    internal static IEnumerable<int> Positions(SourceFileNode file, Utf8String name, SyntaxNode? container, CancellationToken cancellation)
    {
        if (name.IsEmpty) yield break;
        container ??= file;
        var text = file.Source.Text;
        // The reference starts with the index relative to the container slice, then searches the full text.
        int position = text.Span[Math.Clamp(container.Pos, 0, text.Length)..].IndexOf(name.Span);
        while (position >= 0 && position < container.End)
        {
            cancellation.ThrowIfCancellationRequested();
            int end = position + name.Length;
            if ((position == 0 || !TokenFacts.IsIdentifierPart(text[position - 1]))
                && (end == text.Length || !TokenFacts.IsIdentifierPart(text[end]))) yield return position;
            int start = end + 1;
            if (start > text.Length) yield break;
            int next = text.Span[start..].IndexOf(name.Span);
            position = next < 0 ? -1 : start + next;
        }
    }

    internal static Utf8String Text(SyntaxNode? node) => node switch
    {
        IdentifierNode n => n.Text, PrivateIdentifierNode n => n.Text, StringLiteralNode n => n.Text,
        NoSubstitutionTemplateLiteralNode n => n.Text, NumericLiteralNode n => n.Text, _ => default,
    };
    internal static bool LiteralProperty(SyntaxNode node) => node.Parent switch
    {
        PropertyDeclarationNode or PropertySignatureDeclarationNode or PropertyAssignmentNode or EnumMemberNode
            or MethodDeclarationNode or MethodSignatureDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode
            or ModuleDeclarationNode => node.Parent.DeclarationName == node,
        ElementAccessExpressionNode access => access.ArgumentExpression == node,
        ComputedPropertyNameNode => true,
        LiteralTypeNode { Parent: IndexedAccessTypeNode } => true,
        _ => false,
    };
    internal static bool ValidPosition(SyntaxNode node, Utf8String name) => node switch
    {
        IdentifierNode or PrivateIdentifierNode => Text(node).Length == name.Length,
        StringLiteralNode or NoSubstitutionTemplateLiteralNode => Text(node).Length == name.Length
            && (LiteralProperty(node) || node.Parent is ExternalModuleReferenceNode { Parent: ImportEqualsDeclarationNode }
                || node.Parent is ImportSpecifierNode or ExportSpecifierNode
                || node.Parent is CallExpressionNode { Arguments.Count: >= 2 } call && call.Arguments[1] == node
                    && SyntaxLanguageService.IsAssignmentDeclaration(call)),
        NumericLiteralNode => Text(node).Length == name.Length && LiteralProperty(node),
        _ => node.Kind == K.DefaultKeyword && name.Length == 7,
    };
    internal static bool ObjectBinding(SyntaxNode? node) => node is BindingElementNode
        { Parent.Kind: K.ObjectBindingPattern, Name: IdentifierNode, PropertyName: null };
    internal static bool This(SyntaxNode node) => node.Kind == K.ThisKeyword
        || node is IdentifierNode { Parent: ParameterDeclarationNode } && Text(node) == "this"u8;
    internal static bool ParameterProperty(SyntaxNode? node) => node is ParameterDeclarationNode { Parent: ConstructorDeclarationNode }
        && node.ModifierList?.Any(modifier => modifier.Kind is K.PublicKeyword or K.PrivateKeyword or K.ProtectedKeyword or K.ReadonlyKeyword or K.OverrideKeyword) == true;
    internal static bool TypeKeyword(K kind) => kind is K.AnyKeyword or K.AssertsKeyword or K.BigIntKeyword or K.BooleanKeyword
        or K.FalseKeyword or K.InferKeyword or K.KeyOfKeyword or K.NeverKeyword or K.NullKeyword or K.NumberKeyword or K.ObjectKeyword
        or K.ReadonlyKeyword or K.StringKeyword or K.SymbolKeyword or K.TypeOfKeyword or K.TrueKeyword or K.VoidKeyword
        or K.UndefinedKeyword or K.UniqueKeyword or K.UnknownKeyword;
    internal static bool ReadonlyOperator(SyntaxNode node) => node.Kind == K.ReadonlyKeyword && node.Parent is TypeOperatorNode { Operator: K.ReadonlyKeyword };
    internal static SyntaxNode? TargetLabel(SyntaxNode? node, Utf8String name)
    {
        for (; node is not null; node = node.Parent)
            if (node is LabeledStatementNode statement && Text(statement.Label) == name) return statement.Label;
        return null;
    }
    internal static SyntaxNode? Container(SyntaxNode node)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            if (parent.Kind is K.SourceFile or K.MethodDeclaration or K.MethodSignature or K.FunctionDeclaration or K.FunctionExpression
                or K.GetAccessor or K.SetAccessor or K.ClassDeclaration or K.InterfaceDeclaration or K.EnumDeclaration or K.ModuleDeclaration) return parent;
        return null;
    }
    internal static SyntaxNode? SuperContainer(SyntaxNode node)
    {
        while (node.Parent is { } parent)
        {
            node = parent;
            if (node is ComputedPropertyNameNode) node = node.Parent!;
            else if (node is PropertyDeclarationNode or PropertySignatureDeclarationNode or MethodDeclarationNode or MethodSignatureDeclarationNode
                or ConstructorDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode or ClassStaticBlockDeclarationNode) return node;
            else if (node is DecoratorNode)
            {
                if (node.Parent is ParameterDeclarationNode && SemanticSyntax.ClassElement(node.Parent.Parent)) node = node.Parent.Parent!;
                else if (SemanticSyntax.ClassElement(node.Parent)) node = node.Parent!;
            }
        }
        return null;
    }
    internal static IEnumerable<SyntaxNode> SuperTypes(SyntaxNode node)
    {
        var clauses = node switch { ClassDeclarationNode n => n.HeritageClauses, ClassExpressionNode n => n.HeritageClauses,
            InterfaceDeclarationNode n => n.HeritageClauses, _ => null };
        if (clauses is null) yield break;
        if (node is not InterfaceDeclarationNode)
        {
            var basis = clauses.OfType<HeritageClauseNode>().FirstOrDefault(c => c.Token == K.ExtendsKeyword)?.Types?.FirstOrDefault();
            if (basis is not null) yield return basis;
        }
        var others = clauses.OfType<HeritageClauseNode>().FirstOrDefault(c => c.Token == (node is InterfaceDeclarationNode ? K.ExtendsKeyword : K.ImplementsKeyword));
        foreach (var type in others?.Types ?? new([])) yield return type;
    }
    internal static SyntaxNode SkipOuter(SyntaxNode node)
    {
        while (true)
        {
            var inner = node switch { ParenthesizedExpressionNode n => n.Expression, TypeAssertionNode n => n.Expression,
                AsExpressionNode n => n.Expression, SatisfiesExpressionNode n => n.Expression, NonNullExpressionNode n => n.Expression,
                PartiallyEmittedExpressionNode n => n.Expression, _ => null };
            if (inner is null) return node;
            node = inner;
        }
    }
    internal static async ValueTask<SyntaxNode> AdjustedAsync(SyntaxNode node, bool rename, CancellationToken cancellation)
    {
        var parent = node.Parent;
        if (parent is null) return node;
        bool modifier = node.Kind != K.DefaultKeyword || rename;
        modifier = modifier && parent.ModifierList?.Contains(node) == true || node.Kind switch
        {
            K.ClassKeyword => parent is ClassDeclarationNode,
            K.FunctionKeyword => parent is FunctionDeclarationNode,
            K.InterfaceKeyword => parent is InterfaceDeclarationNode,
            K.EnumKeyword => parent is EnumDeclarationNode,
            K.TypeKeyword => parent is TypeAliasDeclarationNode,
            K.NamespaceKeyword or K.ModuleKeyword => parent is ModuleDeclarationNode,
            K.ImportKeyword => parent is ImportEqualsDeclarationNode,
            K.GetKeyword => parent is GetAccessorDeclarationNode,
            K.SetKeyword => parent is SetAccessorDeclarationNode,
            _ => false,
        };
        if (modifier)
        {
            if (parent.DeclarationName is { } name) return name;
            if (!rename)
            {
                if (parent is ClassExpressionNode or FunctionExpressionNode
                    && await SyntaxNavigation.FindChildOfKindAsync(parent, parent is ClassExpressionNode ? K.ClassKeyword : K.FunctionKeyword,
                        SemanticSyntax.Source(parent)!, cancellation) is { } keyword) return keyword;
                if (parent is ConstructorDeclarationNode) return parent;
            }
        }
        if (node.Kind is K.VarKeyword or K.ConstKeyword or K.LetKeyword && parent is VariableDeclarationListNode
            { Declarations.Count: 1 } list && list.Declarations[0].DeclarationName is IdentifierNode singleName) return singleName;
        if (node.Kind == K.TypeKeyword)
        {
            if (parent is ImportClauseNode clause && SemanticSyntax.TypeOnly(clause) && ImportLocation(clause.Parent!, rename) is { } imported) return imported;
            if (parent is ExportDeclarationNode { IsTypeOnly: true } export && ExportLocation(export, rename) is { } exported) return exported;
        }
        if (node.Kind == K.AsKeyword)
        {
            if (parent is ImportSpecifierNode { PropertyName: not null } or ExportSpecifierNode { PropertyName: not null }
                or NamespaceImportNode or NamespaceExportNode) return parent.DeclarationName ?? node;
            if (parent is ExportDeclarationNode { ExportClause: NamespaceExportNode ns }) return ns.Name!;
        }
        if (node.Kind == K.ImportKeyword && parent is ImportDeclarationNode && ImportLocation(parent, rename) is { } import) return import;
        if (node.Kind == K.ExportKeyword)
        {
            if (parent is ExportDeclarationNode export && ExportLocation(export, rename) is { } exported) return exported;
            if (parent is ExportAssignmentNode { Expression: { } expression }) return SkipOuter(expression);
        }
        if (node.Kind == K.RequireKeyword && parent is ExternalModuleReferenceNode { Expression: { } required }) return required;
        if (node.Kind == K.FromKeyword && ModuleSpecifier(parent) is { } module && parent is ImportDeclarationNode or ExportDeclarationNode) return module;
        if (node.Kind is K.ExtendsKeyword or K.ImplementsKeyword && parent is HeritageClauseNode clause2 && clause2.Token == node.Kind
            && clause2.Types is { Count: 1 } types)
            return types[0] switch { ExpressionWithTypeArgumentsNode e => e.Expression!, TypeReferenceNode t => t.TypeName!, _ => node };
        if (node.Kind == K.ExtendsKeyword)
        {
            if (parent is TypeParameterDeclarationNode { Constraint: TypeReferenceNode { TypeName: { } constraint } }) return constraint;
            if (parent is ConditionalTypeNode { ExtendsType: TypeReferenceNode { TypeName: { } extends } }) return extends;
        }
        if (node.Kind == K.InferKeyword && parent is InferTypeNode { TypeParameter.DeclarationName: { } inferred }) return inferred;
        if (node.Kind == K.InKeyword && parent is TypeParameterDeclarationNode { Parent: MappedTypeNode, Name: { } parameter }) return parameter;
        if (node.Kind == K.KeyOfKeyword && parent is TypeOperatorNode { Operator: K.KeyOfKeyword, Type: TypeReferenceNode { TypeName: { } key } }) return key;
        if (node.Kind == K.ReadonlyKeyword && parent is TypeOperatorNode { Operator: K.ReadonlyKeyword, Type: ArrayTypeNode { ElementType: TypeReferenceNode { TypeName: { } element } } }) return element;
        if (!rename)
        {
            var expression = (node.Kind, parent) switch
            {
                (K.NewKeyword, NewExpressionNode n) => n.Expression, (K.VoidKeyword, VoidExpressionNode n) => n.Expression,
                (K.TypeOfKeyword, TypeOfExpressionNode n) => n.Expression, (K.AwaitKeyword, AwaitExpressionNode n) => n.Expression,
                (K.YieldKeyword, YieldExpressionNode n) => n.Expression, (K.DeleteKeyword, DeleteExpressionNode n) => n.Expression,
                (K.InKeyword or K.InstanceOfKeyword, BinaryExpressionNode n) when n.OperatorToken == node => n.Right,
                (K.InKeyword, ForInOrOfStatementNode { Kind: K.ForInStatement } n) => n.Expression,
                (K.OfKeyword, ForInOrOfStatementNode { Kind: K.ForOfStatement } n) => n.Expression, _ => null,
            };
            if (expression is not null) return SkipOuter(expression);
            if (node.Kind == K.AsKeyword && parent is AsExpressionNode { Type: TypeReferenceNode { TypeName: { } type } }) return type;
        }
        return node;
    }
    internal static SyntaxNode? ModuleSpecifier(SyntaxNode? node) => node switch
    {
        ImportDeclarationNode n => n.ModuleSpecifier, ExportDeclarationNode n => n.ModuleSpecifier,
        ImportEqualsDeclarationNode { ModuleReference: ExternalModuleReferenceNode n } => n.Expression,
        CallExpressionNode { Arguments.Count: > 0 } n => n.Arguments[0], _ => null,
    };
    private static SyntaxNode? ImportLocation(SyntaxNode node, bool rename)
    {
        var clause = (node as ImportDeclarationNode)?.ImportClause as ImportClauseNode;
        if (clause is { Name: not null }) return clause.NamedBindings is null ? clause.Name : null;
        if (clause?.NamedBindings is NamedImportsNode names) return names.Elements is { Count: 1 } elements ? elements[0].DeclarationName : null;
        if (clause?.NamedBindings is NamespaceImportNode ns) return ns.Name;
        return rename ? null : ModuleSpecifier(node);
    }
    private static SyntaxNode? ExportLocation(ExportDeclarationNode node, bool rename) => node.ExportClause switch
    {
        NamedExportsNode { Elements.Count: 1 } named => named.Elements[0].DeclarationName,
        NamedExportsNode => null, NamespaceExportNode ns => ns.Name, _ => rename ? null : node.ModuleSpecifier,
    };
    internal static async ValueTask<int> MeaningAsync(SyntaxNode node, CancellationToken cancellation)
    {
        node = await AdjustedAsync(QuerySyntax.Reparsed(node), false, cancellation);
        var parent = node.Parent;
        if (node is SourceFileNode) return Value;
        if (parent is ExportAssignmentNode or ExportSpecifierNode or ExternalModuleReferenceNode or ImportSpecifierNode or ImportClauseNode
            || parent is ImportEqualsDeclarationNode import && import.Name == node) return All;
        var root = node;
        while (root.Parent is QualifiedNameNode) root = root.Parent;
        if (root.Parent is ImportEqualsDeclarationNode { ModuleReference: not ExternalModuleReferenceNode } internalImport && internalImport.ModuleReference == root)
        {
            var name = node is QualifiedNameNode ? node : node.Parent is QualifiedNameNode q && q.Right == node ? q : null;
            return name?.Parent is ImportEqualsDeclarationNode ? All : Namespace;
        }
        if (QuerySyntax.DeclarationName(node)) return DeclarationMeaning(parent!);
        if (node is IdentifierNode or QualifiedNameNode && (node.Flags & NodeFlags.JSDoc) != 0)
            for (var ancestor = node; ancestor is not null; ancestor = ancestor.Parent)
                if (ancestor.Kind is K.JSDocNameReference or K.JSDocLink or K.JSDocLinkCode or K.JSDocLinkPlain) return All;
        var typeNode = QuerySyntax.RightSide(node) && node.Parent is not MetaPropertyNode ? node.Parent! : node;
        if (typeNode.Kind == K.ThisType || typeNode.Kind == K.ThisKeyword && !QuerySyntax.Expression(typeNode)
            || typeNode.Parent is TypeReferenceNode or ImportTypeNode { IsTypeOf: false }
            || typeNode.Parent is ExpressionWithTypeArgumentsNode heritage && QuerySyntax.PartOfType(heritage)) return Type;
        if (root.Parent is TypeReferenceNode && root is QualifiedNameNode qualified && qualified.Right != node) return Namespace;
        root = node;
        while (root.Parent is PropertyAccessExpressionNode) root = root.Parent;
        if (root is PropertyAccessExpressionNode access && access.Name != node && root.Parent is ExpressionWithTypeArgumentsNode
            { Parent: HeritageClauseNode clause } && (clause.Token == K.ImplementsKeyword && clause.Parent is ClassDeclarationNode
                || clause.Token == K.ExtendsKeyword && clause.Parent is InterfaceDeclarationNode)) return Namespace;
        return parent is TypeParameterDeclarationNode ? Type : parent is LiteralTypeNode ? Type | Value : Value;
    }
    internal static int DeclarationMeaning(SyntaxNode node) => node.Kind switch
    {
        K.VariableDeclaration or K.Parameter or K.BindingElement or K.PropertyDeclaration or K.PropertySignature or K.PropertyAssignment
            or K.ShorthandPropertyAssignment or K.MethodDeclaration or K.MethodSignature or K.Constructor or K.GetAccessor or K.SetAccessor
            or K.FunctionDeclaration or K.FunctionExpression or K.ArrowFunction or K.CatchClause or K.JsxAttribute => Value,
        K.TypeParameter or K.InterfaceDeclaration or K.TypeAliasDeclaration or K.JSTypeAliasDeclaration or K.TypeLiteral => Type,
        K.EnumMember or K.ClassDeclaration => Value | Type,
        K.ModuleDeclaration => Namespace | (node is ModuleDeclarationNode module && (module.Name is StringLiteralNode || Binder.ModuleState(module) == 2) ? Value : 0),
        K.SourceFile => Namespace | Value, _ => All,
    };
    internal static SyntaxNode? Context(SyntaxNode node)
    {
        if (QuerySyntax.Declaration(node)) return LanguageServiceDocument.DefinitionContext(node);
        var parent = node.Parent;
        if (parent is null) return null;
        if (!QuerySyntax.Declaration(parent) && parent is not ExportAssignmentNode)
        {
            if ((node.Flags & NodeFlags.JavaScriptFile) != 0)
            {
                var binary = parent as BinaryExpressionNode ?? (parent is PropertyAccessExpressionNode or ElementAccessExpressionNode
                    && parent.Parent is BinaryExpressionNode assignment && assignment.Left == parent ? assignment : null);
                if (binary is not null && SyntaxLanguageService.IsAssignmentDeclaration(binary)) return LanguageServiceDocument.DefinitionContext(binary);
            }
            if (parent is JsxOpeningElementNode or JsxClosingElementNode) return parent.Parent;
            if (parent is JsxSelfClosingElementNode or LabeledStatementNode or BreakStatementNode or ContinueStatementNode) return parent;
            for (var computed = node; computed is not null; computed = computed.Parent)
                if (computed is ComputedPropertyNameNode) return LanguageServiceDocument.DefinitionContext(computed.Parent);
            return null;
        }
        if (parent.DeclarationName == node || parent is ConstructorDeclarationNode or ExportAssignmentNode
            || parent switch { ImportSpecifierNode n => n.PropertyName == node, ExportSpecifierNode n => n.PropertyName == node,
                BindingElementNode n => n.PropertyName == node, _ => false }
            || node.Kind == K.DefaultKeyword && SemanticSyntax.HasModifier(parent, K.ExportKeyword) && SemanticSyntax.HasModifier(parent, K.DefaultKeyword))
            return LanguageServiceDocument.DefinitionContext(parent);
        return null;
    }
}
