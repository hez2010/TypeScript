using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : IIdentifierTypeHost, IReferenceTypeNarrowingHost, ISymbolNarrowingHost, IAssignmentCheckHost, IRelationDiagnosticHost
{
    internal IdentifierTypes Identifiers { get; }
    internal AssignmentChecks AssignmentChecks { get; }
    internal RelationDiagnostics RelationDiagnostics { get; }
    internal HashSet<(SyntaxNode Node, bool Construct)> AssignmentHints { get; } = [];
    public bool NoCheck => program.Symbols.Program.Configuration.Options.Boolean("noCheck") == true;
    internal ReferenceTypeNarrowing ReferenceNarrowing { get; }
    internal SymbolNarrowing SymbolNarrowing { get; }
    internal AliasReferences AliasReferences { get; }
    internal MissingNamePrefixes MissingNames { get; }
    internal HashSet<SyntaxNode> ContextualBindingPatterns { get; } = [];

    public bool ContextualBindingPattern(SyntaxNode pattern) =>
        ContextualBindingPatterns.Contains(pattern) || BindingPatterns.Contains(pattern);

    public ValueTask<Type> ThisExpressionAsync(SyntaxNode node, CancellationToken cancellation) =>
        ThisExpressions.ThisAsync(node, cancellation);

    public ValueTask MarkIdentifierAsync(IdentifierNode node, CancellationToken cancellation) =>
        AliasReferences.IdentifierAsync(node, cancellation);

    public async ValueTask CheckDeprecatedAsync(IdentifierNode node, Symbol symbol, CancellationToken cancellation)
    {
        if (program.Deprecations.Symbol(symbol) && await program.Deprecations.UncalledAsync(node, symbol, FlowReferences, cancellation))
            program.Suggestion(node, 6385, node.Text);
    }

    public ValueTask<Type> NarrowedSymbolAsync(Symbol symbol, SyntaxNode location, CancellationToken cancellation) =>
        SymbolNarrowing.GetAsync(symbol, location, cancellation);

    public ValueTask<Type> NarrowableReferenceAsync(Type type, SyntaxNode node, CheckMode mode, CancellationToken cancellation) =>
        ReferenceNarrowing.GetAsync(type, node, mode, cancellation);

    public void IdentifierError(SyntaxNode node, int code, Symbol symbol, Type? type = null) => Error(node, code);

    public void CircularInitializer(Symbol symbol) => CircularSymbol(symbol);

    public ValueTask<Type?> ContextualReferenceAsync(SyntaxNode node, bool skipBindingPatterns, CancellationToken cancellation) =>
        Contexts.GetAsync(node, skipBindingPatterns ? ContextFlags.SkipBindingPatterns : 0, cancellation);

    public async ValueTask<Type?> OtherContextAsync(SyntaxNode node, ContextFlags flags, CancellationToken cancellation)
    {
        while (node.Parent is ParenthesizedExpressionNode or NonNullExpressionNode)
            node = node.Parent;
        if (node.Parent is ImportAttributeNode attribute)
        {
            var attributes = globalImportAttributes ?? await program.Globals.GetAsync("ImportAttributes", 0, false, cancellation);
            return await ContextualPropertyAsync(attributes, ImportAttributeName(attribute.Name!), cancellation);
        }
        if (node.Parent is DecoratorNode decorator)
            return await DecoratorSignatureAsync(decorator, cancellation) is { } decoratorSignature
                ? DecoratorFunction(decoratorSignature)
                : null;
        if (node.Parent is IInitializedNode initialized && initialized.Initializer == node
            && node.Parent is VariableDeclarationNode or ParameterDeclarationNode or PropertyDeclarationNode)
        {
            if (node.Parent is ITypedNode { Type: { } annotation })
                return await Nodes.FromNodeAsync(annotation, cancellation);
            if (node.Parent is VariableDeclarationNode)
                return null;
            throw new InvalidOperationException("Checker requires contextual declaration initializers");
        }
        if (node.Parent is CallExpressionNode call && call.Expression != node)
        {
            return await ContextualCallArgumentAsync(call, node, cancellation);
        }
        if (node.Parent is CallExpressionNode)
            return null;
        if (node.Parent is NewExpressionNode construct)
            return await ContextualCallArgumentAsync(construct, node, cancellation);
        if (node.Parent is TemplateSpanNode { Parent: TemplateExpressionNode { Parent: TaggedTemplateExpressionNode tag } })
            return await ContextualCallArgumentAsync(tag, node, cancellation);
        if (node.Parent is AwaitExpressionNode awaitExpression)
        {
            var contextual = await Contexts.GetAsync(awaitExpression, flags, cancellation);
            if (contextual is null)
                return null;
            var awaited = await Awaited.GetAsync(contextual, false, cancellation: cancellation);
            if (awaited is null)
                return null;
            var promise = await program.Globals.GetAsync("PromiseLike", 1, false, cancellation);
            return await Algebra.UnionAsync([awaited, promise == context.EmptyGenericType ? context.UnknownType
                : context.CreateTypeReference((InterfaceType)promise, [awaited])], cancellation: cancellation);
        }
        if (node.Parent is YieldExpressionNode yield)
            return await Generators.OperandContextAsync(yield, flags, cancellation);
        if (node.Parent is ReturnStatementNode or ArrowFunctionNode)
        {
            var function = DeclarationOrder.Ancestor(node.Parent, n => n is IFunctionSignature)!;
            var contextual = await FunctionContexts.ReturnAsync(function, flags, cancellation);
            if (contextual is null)
                return null;
            if (SemanticSyntax.Generator(function))
            {
                contextual = await Generators.ReturnExpressionAsync(
                    contextual,
                    SemanticSyntax.HasModifier(function, SyntaxKind.AsyncKeyword),
                    cancellation);
                if (contextual is null)
                    return null;
            }
            if (SemanticSyntax.HasModifier(function, SyntaxKind.AsyncKeyword))
            {
                var awaited = await Awaited.GetAsync(contextual, false, cancellation: cancellation);
                if (awaited is null)
                    return null;
                var promise = await program.Globals.GetAsync("PromiseLike", 1, false, cancellation);
                return await Algebra.UnionAsync([awaited, promise == context.EmptyGenericType ? context.UnknownType
                    : context.CreateTypeReference((InterfaceType)promise, [awaited])], cancellation: cancellation);
            }
            return contextual;
        }
        if (node.Parent is BinaryExpressionNode binary)
        {
            if (BinaryExpressions.Assignment(binary.OperatorToken!.Kind) && binary.Right == node)
                return await AssignmentContextAsync(binary, cancellation).ConfigureAwait(false);
            if (binary.OperatorToken.Kind is SyntaxKind.BarBarToken or SyntaxKind.AmpersandAmpersandToken
                or SyntaxKind.QuestionQuestionToken)
                throw new InvalidOperationException("Checker requires logical expression contextual types");
            return null;
        }
        if (node.Parent is AsExpressionNode assertion)
            return await Nodes.FromNodeAsync(assertion.Type!, cancellation);
        if (node.Parent is TypeAssertionNode typeAssertion)
            return await Nodes.FromNodeAsync(typeAssertion.Type!, cancellation);
        if (node.Parent is PropertyAccessExpressionNode or ElementAccessExpressionNode or QualifiedNameNode)
            return null;
        if (node.Parent is TypeOfExpressionNode or VoidExpressionNode or PrefixUnaryExpressionNode or PostfixUnaryExpressionNode
            or NonNullExpressionNode or IfStatementNode or WhileStatementNode or DoStatementNode or SwitchStatementNode
            or ExpressionStatementNode or ExportSpecifierNode or TypeQueryNode)
            return null;
        throw new InvalidOperationException($"Checker requires contextual reference typing in {node.Parent?.Kind}");
    }

    public ValueTask<Type?> BindingParentAsync(SyntaxNode declaration, CancellationToken cancellation) =>
        Bindings.ParentAsync(declaration, cancellation: cancellation);

    public ValueTask<Type> BindingFromParentAsync(
        BindingElementNode element,
        Type parent,
        bool noTupleBoundsCheck,
        CancellationToken cancellation) => Bindings.FromParentAsync(element, parent, noTupleBoundsCheck, cancellation);

    public ValueTask<bool> SomeAssignedAsync(SyntaxNode declaration, CancellationToken cancellation) =>
        Assignments.SomeAsync(declaration, cancellation);

    public bool ContextSensitiveFunction(SyntaxNode node) => node is FunctionExpressionNode or ArrowFunctionNode
        or MethodDeclarationNode { Parent: ObjectLiteralExpressionNode }
            ? program.IsContextSensitive(node) : false;

    public ValueTask<Signature?> ContextualSignatureAsync(SyntaxNode node, CancellationToken cancellation) =>
        program.ContextualSignatures.TryGetValue(node, out var signature) ? ValueTask.FromResult<Signature?>(signature)
            : FunctionContexts.GetAsync(node, cancellation);

    public TypeMapper? NonFixingMapper(SyntaxNode node) =>
        Contexts.InferenceFor(node)?.NonFixingMapper;

    public bool ExportsPropertyAssignment(SyntaxNode left)
    {
        if ((left.Flags & NodeFlags.JavaScriptFile) == 0
            || left.Parent is not BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.EqualsToken })
            return false;
        var receiver = left switch
        {
            PropertyAccessExpressionNode property => property.Expression,
            ElementAccessExpressionNode element when element.ArgumentExpression is StringLiteralNode or NumericLiteralNode
                or NoSubstitutionTemplateLiteralNode => element.Expression,
            _ => null
        };
        return receiver is IdentifierNode { Text: "exports" }
            or PropertyAccessExpressionNode { Expression: IdentifierNode { Text: "module" }, Name: IdentifierNode { Text: "exports" } }
            or ElementAccessExpressionNode { Expression: IdentifierNode { Text: "module" }, ArgumentExpression: StringLiteralNode { Text: "exports" } };
    }

    public ValueTask<Type> PropertyWriteAsync(PropertyAccessExpressionNode left, CancellationToken cancellation)
            => Access.PropertyAsync(left, writeOnly: true, cancellation: cancellation);

    public async ValueTask<Type?> AssignedPropertyTypeAsync(PropertyAccessExpressionNode left, CancellationToken cancellation)
        =>
            await FlowPropertyTypeAsync(
                await ExpressionAsync(left.Expression!, cancellation),
                SyntaxNameText.Get(left.Name),
                false,
                cancellation);

    public async ValueTask CheckAssignableAsync(
        Type source,
        Type target,
        SyntaxNode errorNode,
        SyntaxNode expression,
        bool exactOptionalMismatch,
        CancellationToken cancellation)
    {
        await RelationDiagnostics.CheckAsync(
            source,
            target,
            RelationKind.Assignable,
            errorNode,
            expression,
            exactOptionalMismatch ? 2412 : null,
            cancellation);
    }

    public void AssignmentError(SyntaxNode node, int code) => Error(node, code);

    public async ValueTask<bool> ReportRelationAsync(
        Type source,
        Type target,
        RelationKind kind,
        SyntaxNode? node,
        int? headCode,
        CancellationToken cancellation)
    {
        bool related = await Relations.RelatedAsync(source, target, kind, cancellation);
        if (!related && node is not null)
        {
            if (await ExcessProperties.UnknownPropertyAsync(source, target, kind, Relations, cancellation) is { } excess)
            {
                Error((excess.ValueDeclaration as INamedNode)?.Name ?? node, relationDiagnosticHead ?? 2353);
                return false;
            }
            int code = headCode ?? (context.ExactOptionalPropertyTypes
                && (await RelationDiagnostics.ExactOptionalPropertiesAsync(source, target, cancellation)).Count != 0
                ? 2375
                : 2322);
            Error(node, relationDiagnosticHead ?? code);
        }
        return related;
    }

    public void CallOrConstructHint(SyntaxNode node, bool construct) => AssignmentHints.Add((node, construct));

    public ValueTask<bool> ElaborateComplexAsync(
        SyntaxNode node,
        Type source,
        Type target,
        RelationKind kind,
        CancellationToken cancellation)
            => node is JsxAttributesNode ? throw new InvalidOperationException("Checker requires JSX error elaboration")
                : LiteralElaboration.CheckAsync(node, source, target, kind, cancellation);

}
