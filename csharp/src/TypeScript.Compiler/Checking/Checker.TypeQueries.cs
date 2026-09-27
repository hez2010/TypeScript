using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal async ValueTask<Type> GetTypeAtLocationAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        using var query = await EnterQueryAsync(node, cancellation).ConfigureAwait(false);
        return await TypeAtLocationAsync(QuerySyntax.Reparsed(node), cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Type> TypeAtLocationAsync(SyntaxNode node, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (node is SourceFileNode && program.Symbols.Binding(node)?.IsModule != true
            || (node.Flags & NodeFlags.InWithStatement) != 0)
            return context.ErrorType;

        InterfaceType? classType = null;
        bool implements = false;
        if (node is ExpressionWithTypeArgumentsNode or TypeReferenceNode && node.Parent is HeritageClauseNode heritage
            && SemanticSyntax.ClassLike(heritage.Parent))
        {
            classType = (InterfaceType)await Declared.GetAsync(program.Symbols.Declaration(heritage.Parent!)!, cancellation);
            implements = heritage.Token == SyntaxKind.ImplementsKeyword;
        }
        if (QuerySyntax.PartOfType(node))
        {
            var type = await Nodes.FromNodeAsync(node, cancellation);
            return classType is null ? type : await Bases.WithThisAsync(type, classType.ThisType, cancellation: cancellation);
        }
        if (QuerySyntax.Expression(node))
            return await Algebra.RegularTypeAsync(await ExpressionTypeForQueryAsync(QuerySyntax.RightSide(node) ? node.Parent! : node,
                cancellation), cancellation);
        if (classType is not null && !implements)
        {
            var baseType = (await Bases.GetAsync(classType, cancellation)).FirstOrDefault();
            return baseType is null
                ? context.ErrorType
                : await Bases.WithThisAsync(baseType, classType.ThisType, cancellation: cancellation);
        }
        if (QuerySyntax.TypeDeclaration(node))
            return program.Symbols.Declaration(node) is { } declaredSymbol
                ? await Declared.GetAsync(declaredSymbol, cancellation) : context.ErrorType;
        if (node is IdentifierNode && node.Parent is { } parent && QuerySyntax.TypeDeclaration(parent)
            && SemanticSyntax.Name(parent) == node)
            return await Declared.GetAsync(program.Symbols.Declaration(parent)!, cancellation);
        if (node is BindingElementNode)
            return await Variables.DeclaredOrInferredAsync(node, cancellation: cancellation) ?? context.ErrorType;
        if (program.Symbols.Declaration(node) is { } declaration)
            return await Values.GetAsync(declaration, cancellation);
        if (node.Parent is ImportSpecifierNode or ExportSpecifierNode || node is not BindingPatternNode
            && SemanticSyntax.Name(node.Parent) == node)
        {
            var symbol = program.Symbols.Declaration(node.Parent!);
            if (symbol is not null)
            {
                if (node.Parent is ImportSpecifierNode import && import.PropertyName == node
                    || node.Parent is ExportSpecifierNode export && export.PropertyName == node)
                    symbol = await program.Aliases.ImmediateAsync(symbol, cancellation);
                return symbol is null ? context.ErrorType : await Values.GetAsync(symbol, cancellation);
            }
            return context.ErrorType;
        }
        if (node is BindingPatternNode)
            return await Variables.DeclaredOrInferredAsync(node.Parent!, cancellation: cancellation) ?? context.ErrorType;
        if (node is IdentifierNode or QualifiedNameNode && QuerySyntax.ImportOrExportAssignment(node))
        {
            var name = node is IdentifierNode && QuerySyntax.RightSide(node) ? node.Parent! : node;
            var meaning = name is IdentifierNode || name.Parent is QualifiedNameNode ? SymbolFlags.Namespace
                : SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace;
            var symbol = await program.EntityNames.ResolveAsync(name, meaning, dontResolveAlias: true, cancellation: cancellation);
            if (symbol is not null)
            {
                var declared = await Declared.GetAsync(symbol, cancellation);
                return declared != context.ErrorType ? declared : await Values.GetAsync(symbol, cancellation);
            }
        }
        if (node is ImportAttributesNode attributes)
            return await ImportAttributesExpressionAsync(attributes, cancellation);
        // The pinned reference returns its error type for meta-property keyword tokens.
        return context.ErrorType;
    }

    private async ValueTask<Type> ExpressionTypeForQueryAsync(SyntaxNode node, CancellationToken cancellation) =>
        await QuickExpressionTypeAsync(node, cancellation)
            ?? await Expressions.CheckAsync(node, CheckMode.TypeOnly, cancellation);

    private async ValueTask<Type?> QuickExpressionTypeAsync(SyntaxNode node, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        var expression = node;
        while (expression is ParenthesizedExpressionNode parentheses)
            expression = parentheses.Expression!;
        if (expression is AwaitExpressionNode awaited)
        {
            var type = await QuickExpressionTypeAsync(awaited.Expression!, cancellation);
            return type is null ? null : await Awaited.GetAsync(type, true, cancellation: cancellation);
        }
        bool construct = expression is NewExpressionNode;
        if (expression is CallExpressionNode call && call.Expression?.Kind != SyntaxKind.SuperKeyword
            && !SemanticSyntax.RequireCall(call) && !IsImportCall(call) && !SymbolCall(call, cancellation) || construct)
        {
            var callee = construct ? ((NewExpressionNode)expression).Expression! : ((CallExpressionNode)expression).Expression!;
            var type = await Expressions.CheckAsync(callee, cancellation: cancellation);
            bool chain = OptionalExpressions.Chain(expression);
            var nonOptional = chain
                ? await Optional.ReceiverAsync(type, callee, cancellation)
                : await NonNullAsync(type, callee, cancellation);
            var signature = await GenericExpressions.SingleAsync(chain ? type : nonOptional, construct, true, cancellation);
            if (signature is null || signature.TypeParameters.Count != 0)
                return null;
            var result = await Signatures.ReturnAsync(signature, cancellation);
            return chain ? await Optional.PropagateAsync(result, expression, nonOptional != type, cancellation) : result;
        }
        if (expression is AsExpressionNode or TypeAssertionNode && !SemanticSyntax.ConstAssertion(expression))
            return await Nodes.FromNodeAsync(((ITypedNode)expression).Type!, cancellation);
        if (node is StringLiteralNode or NoSubstitutionTemplateLiteralNode or NumericLiteralNode or BigIntLiteralNode
            or RegularExpressionLiteralNode || node.Kind is SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword)
            return await Expressions.CheckAsync(node, cancellation: cancellation);
        return null;
    }

    private bool SymbolCall(CallExpressionNode call, CancellationToken cancellation)
    {
        var expression = call.Expression is PropertyAccessExpressionNode { Name: IdentifierNode { Text.Span: "for" } } property
            ? property.Expression : call.Expression;
        return expression is IdentifierNode { Text.Span: "Symbol" } identifier
            && program.Symbols.Lookup(program.Symbols.Globals, "Symbol", SymbolFlags.Value) is { } global
            && program.Symbols.NameResolver(cancellation).Resolve(identifier, "Symbol", SymbolFlags.Value) == global;
    }
}
