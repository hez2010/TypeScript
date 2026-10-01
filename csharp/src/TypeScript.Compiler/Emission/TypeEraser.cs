using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

/// <summary>Removes type syntax while retaining runtime TypeScript constructs for subsequent transforms.</summary>
public sealed class TypeEraser(EmitContext context, CompilerOptions options, CancellationToken cancellation = default)
    : SyntaxRewriter(context, cancellation)
{
    private SyntaxNode? current;

    protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
    {
        if (IsStatement(node) && SemanticSyntax.HasModifier(node, K.DeclareKeyword))
            return Elide(node);
        var parent = current;
        current = node;
        try
        {
            return await VisitWorkerAsync(node, parent);
        }
        finally { current = parent; }
    }

    private async ValueTask<SyntaxNode?> VisitWorkerAsync(SyntaxNode node, SyntaxNode? parent)
    {
        switch (node.Kind)
        {
            case K.PublicKeyword: case K.PrivateKeyword: case K.ProtectedKeyword: case K.AbstractKeyword:
            case K.OverrideKeyword: case K.ConstKeyword: case K.DeclareKeyword: case K.ReadonlyKeyword:
            case K.ArrayType: case K.TupleType: case K.OptionalType: case K.RestType: case K.TypeLiteral:
            case K.TypePredicate: case K.TypeParameter: case K.AnyKeyword: case K.UnknownKeyword:
            case K.BooleanKeyword: case K.StringKeyword: case K.NumberKeyword: case K.NeverKeyword:
            case K.VoidKeyword: case K.SymbolKeyword: case K.ConstructorType: case K.FunctionType:
            case K.TypeQuery: case K.TypeReference: case K.UnionType: case K.IntersectionType:
            case K.ConditionalType: case K.ParenthesizedType: case K.ThisType: case K.TypeOperator:
            case K.IndexedAccessType: case K.MappedType: case K.LiteralType: case K.IndexSignature:
            case K.JSImportDeclaration: case K.NamespaceExportDeclaration:
                return null;
            case K.InKeyword: case K.OutKeyword:
                return parent is BinaryExpressionNode ? node : null;
            case K.TypeAliasDeclaration: case K.JSTypeAliasDeclaration: case K.InterfaceDeclaration:
                return Elide(node);
        }

        switch (node)
        {
            case ModuleDeclarationNode module:
                int state = Binder.ModuleState(module);
                bool preserve = options.PreserveConstEnums == true || options.IsolatedModules == true || options.VerbatimModuleSyntax == true;
                SyntaxNode? body = module.Body;
                while (body is ModuleDeclarationNode nested)
                    body = nested.Body;
                if (module.Name is not IdentifierNode || state == 0 || state == 1 && !preserve || body is null)
                    return Elide(node);
                break;
            case ExpressionWithTypeArgumentsNode expression:
                return await RewriteAsync(expression, n => n.TypeArguments = null);
            case PropertyDeclarationNode property:
                if ((SemanticSyntax.HasModifier(property, K.DeclareKeyword) || SemanticSyntax.HasModifier(property, K.AbstractKeyword))
                    && !(options.ExperimentalDecorators == true && property.Modifiers?.Any(n => n is DecoratorNode) == true))
                    return null;
                return await RewriteAsync(property, n => { n.PostfixToken = null; n.Type = null; });
            case ConstructorDeclarationNode constructor:
                if (Missing(constructor.Body))
                    return null;
                return await RewriteAsync(constructor, n => { n.Modifiers = null; n.TypeParameters = null; n.Type = null; n.FullSignature = null; });
            case MethodDeclarationNode method:
                if (Missing(method.Body))
                    return null;
                return await RewriteAsync(method, n => { n.PostfixToken = null; n.TypeParameters = null; n.Type = null; n.FullSignature = null; });
            case GetAccessorDeclarationNode accessor:
                if (Missing(accessor.Body) && SemanticSyntax.HasModifier(accessor, K.AbstractKeyword))
                    return null;
                return await RewriteAsync(accessor, n => { n.TypeParameters = null; n.Type = null; n.FullSignature = null; n.Body ??= EmptyBlock(); });
            case SetAccessorDeclarationNode accessor:
                if (Missing(accessor.Body) && SemanticSyntax.HasModifier(accessor, K.AbstractKeyword))
                    return null;
                return await RewriteAsync(accessor, n => { n.TypeParameters = null; n.Type = null; n.FullSignature = null; n.Body ??= EmptyBlock(); });
            case VariableDeclarationNode variable:
                var updated = (VariableDeclarationNode)(await RewriteAsync(variable, n => { n.ExclamationToken = null; n.Type = null; }))!;
                if (variable.Type is { } type && updated.Name is { } name)
                    Context.SetTypeNode(name, type);
                return updated;
            case BinaryExpressionNode binary when binary.Type is not null:
                return await RewriteAsync(binary, n => n.Type = null);
            case HeritageClauseNode { Token: K.ImplementsKeyword }:
                return null;
            case ClassDeclarationNode declaration:
                return await RewriteAsync(declaration, n => n.TypeParameters = null);
            case ClassExpressionNode expression:
                return await RewriteAsync(expression, n => n.TypeParameters = null);
            case FunctionDeclarationNode function:
                if (Missing(function.Body))
                    return Elide(function);
                return await RewriteAsync(function, RemoveSignatureTypes);
            case FunctionExpressionNode function:
                return await RewriteAsync(function, RemoveSignatureTypes);
            case ArrowFunctionNode function:
                return await RewriteAsync(function, RemoveSignatureTypes);
            case ParameterDeclarationNode parameter:
                if (parameter.Name is IdentifierNode { Text: var text } && text == "this"u8)
                    return null;
                var modifiers = new List<SyntaxNode>();
                if (parameter.Modifiers is { } originalModifiers)
                {
                    if (parent is ConstructorDeclarationNode)
                        modifiers.AddRange(originalModifiers.Where(n => n.Kind is K.PublicKeyword or K.PrivateKeyword or K.ProtectedKeyword or K.ReadonlyKeyword or K.OverrideKeyword));
                    foreach (var decorator in originalModifiers.OfType<DecoratorNode>())
                        if (await VisitAsync(decorator) is { } visited)
                            modifiers.Add(visited);
                }
                var result = (ParameterDeclarationNode)(await RewriteAsync(parameter, n => { n.Modifiers = null; n.QuestionToken = null; n.Type = null; }))!;
                if (modifiers.Count != 0)
                    result.Modifiers = new(modifiers.ToArray());
                return result;
            case CallExpressionNode call:
                return await RewriteAsync(call, n => n.TypeArguments = null);
            case NewExpressionNode expression:
                return await RewriteAsync(expression, n => n.TypeArguments = null);
            case TaggedTemplateExpressionNode template:
                return await RewriteAsync(template, n => n.TypeArguments = null);
            case NonNullExpressionNode expression:
                return await PartialAsync(expression, expression.Expression);
            case TypeAssertionNode expression:
                return await PartialAsync(expression, expression.Expression);
            case AsExpressionNode expression:
                return await PartialAsync(expression, expression.Expression);
            case SatisfiesExpressionNode expression:
                return await PartialAsync(expression, expression.Expression);
            case ParenthesizedExpressionNode parentheses:
                SyntaxNode? inner = parentheses.Expression;
                while (inner is ParenthesizedExpressionNode or PartiallyEmittedExpressionNode)
                    inner = inner switch { ParenthesizedExpressionNode n => n.Expression, PartiallyEmittedExpressionNode n => n.Expression, _ => null };
                if (inner is TypeAssertionNode or AsExpressionNode or SatisfiesExpressionNode && !await IsJSDocAssertionAsync(parentheses))
                    return await PartialAsync(parentheses, parentheses.Expression);
                break;
            case JsxSelfClosingElementNode element:
                return await RewriteAsync(element, n => n.TypeArguments = null);
            case JsxOpeningElementNode element:
                return await RewriteAsync(element, n => n.TypeArguments = null);
            case ImportEqualsDeclarationNode { IsTypeOnly: true }:
            case ImportSpecifierNode { IsTypeOnly: true }:
            case ExportSpecifierNode { IsTypeOnly: true }:
            case ExportDeclarationNode { IsTypeOnly: true }:
                return null;
            case ImportDeclarationNode import:
                if (import.ImportClause is null)
                    return import;
                var visitedClause = await VisitAsync(import.ImportClause);
                if (visitedClause is null)
                    return null;
                var importResult = Context.Clone(import);
                importResult.ImportClause = (ImportClauseNode)visitedClause;
                return importResult;
            case ImportClauseNode clause:
                if (clause.PhaseModifier == K.TypeKeyword)
                    return null;
                var bindings = await VisitAsync(clause.NamedBindings);
                if (clause.Name is null && bindings is null)
                    return null;
                var clauseResult = Context.Clone(clause);
                clauseResult.NamedBindings = bindings;
                return clauseResult;
            case NamedImportsNode imports:
                if (imports.Elements is not { Count: > 0 })
                    return imports;
                var importElements = await VisitListAsync(imports.Elements);
                if (options.VerbatimModuleSyntax != true && importElements is not { Count: > 0 })
                    return null;
                var importsResult = Context.Clone(imports);
                importsResult.Elements = importElements;
                return importsResult;
            case ExportDeclarationNode export:
                var exportClause = await VisitAsync(export.ExportClause);
                if (export.ExportClause is not null && exportClause is null)
                    return null;
                var exportResult = Context.Clone(export);
                exportResult.Modifiers = null;
                exportResult.ExportClause = exportClause;
                exportResult.ModuleSpecifier = await VisitAsync(export.ModuleSpecifier);
                exportResult.Attributes = (ImportAttributesNode?)await VisitAsync(export.Attributes);
                return exportResult;
            case NamedExportsNode exports:
                if (exports.Elements is not { Count: > 0 })
                    return exports;
                var exportElements = await VisitListAsync(exports.Elements);
                if (options.VerbatimModuleSyntax != true && exportElements is not { Count: > 0 })
                    return null;
                var exportsResult = Context.Clone(exports);
                exportsResult.Elements = exportElements;
                return exportsResult;
            case EnumDeclarationNode enumeration when SemanticSyntax.HasModifier(enumeration, K.ConstKeyword):
                return enumeration;
        }
        return await VisitEachChildAsync(node);
    }

    private SyntaxNode Elide(SyntaxNode node)
    {
        var result = Context.Factory.NewNotEmittedStatement();
        Context.SetOriginal(result, node);
        Context.SetCommentRange(result, new(node.Pos, node.End));
        result.Pos = node.Pos;
        result.End = node.End;
        return result;
    }

    private async ValueTask<SyntaxNode> PartialAsync(SyntaxNode node, SyntaxNode? expression)
    {
        var result = Context.Factory.NewPartiallyEmittedExpression(await VisitAsync(expression));
        Context.SetOriginal(result, node);
        result.Pos = node.Pos;
        result.End = node.End;
        return result;
    }

    private async ValueTask<SyntaxNode?> RewriteAsync<T>(T node, Action<T> rewrite) where T : SyntaxNode
    {
        var result = Context.Clone(node);
        rewrite(result);
        return await VisitEachChildAsync(result);
    }

    private BlockNode EmptyBlock() => Context.Factory.NewBlock(new([]), false);
    private static bool Missing(SyntaxNode? node) => node is null || node.Pos >= 0 && node.Pos == node.End;
    private static void RemoveSignatureTypes<T>(T node) where T : SyntaxNode, IFunctionSignature, IFullSignatureNode
    {
        node.TypeParameters = null;
        node.Type = null;
        node.FullSignature = null;
    }
    private async ValueTask<bool> IsJSDocAssertionAsync(SyntaxNode node)
    {
        if ((node.Flags & NodeFlags.JavaScriptFile) == 0 || SemanticSyntax.Source(Context.MostOriginal(node)) is not { } source)
            return false;
        return (await source.GetDocumentationAsync(Context.MostOriginal(node), Cancellation)).Any(comment => comment.Tags?.Any(tag => tag is JSDocTypeTagNode) == true);
    }
    private static bool IsStatement(SyntaxNode node) => node.Kind is >= K.FirstStatement and <= K.LastStatement
        || node is FunctionDeclarationNode or ClassDeclarationNode or InterfaceDeclarationNode or TypeAliasDeclarationNode
            or EnumDeclarationNode or ModuleDeclarationNode or ImportEqualsDeclarationNode or ImportDeclarationNode
            or ExportAssignmentNode or ExportDeclarationNode or NamespaceExportDeclarationNode;
}
