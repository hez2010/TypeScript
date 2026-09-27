using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IFunctionDeclarationHost : IConstraintCheckHost
{
    bool NoImplicitAny { get; }
    int TargetYear { get; }
    Type AnyReadonlyArray { get; }
    Type AutoArray { get; }
    Type AnyArray { get; }

    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> CheckedFunctionTypeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> CachedExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask<Type> LiteralNameTypeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask CheckLiteralAssignableAsync(Type source, Type target, SyntaxNode node, SyntaxNode expression, CancellationToken cancellation);

    ValueTask SignatureEnvironmentAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask CheckFunctionReturnAsync(SyntaxNode node, SyntaxNode annotation, Type type, CancellationToken cancellation);

    ValueTask ReportImplicitAnyAsync(SyntaxNode declaration, Type type, CancellationToken cancellation);

    ValueTask ParameterEnvironmentAsync(ParameterDeclarationNode node, CancellationToken cancellation);

    ValueTask<bool> BindingEnvironmentAsync(BindingElementNode node, CancellationToken cancellation);

    ValueTask<bool> VariableAliasAsync(SyntaxNode node, Symbol symbol, CancellationToken cancellation);

    ValueTask NonNullBindingAsync(Type type, SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> BindingIterationAsync(Type type, SyntaxNode? node, bool outOfBounds, CancellationToken cancellation);

    ValueTask<bool> FunctionModifiersAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask TypeParameterModifiersAsync(TypeParameterDeclarationNode node, CancellationToken cancellation);

    void ExpressionError(SyntaxNode node, DiagnosticCode code);

    void DeferExpression(SyntaxNode node);

    void RegisterUnused(SyntaxNode node);

    void CheckVariableShadowing(SyntaxNode node, CancellationToken cancellation);

    void CheckDeclarationFlags(SyntaxNode node, Symbol symbol, CancellationToken cancellation);

    ValueTask VariableDeclarationConflictAsync(
        SyntaxNode node,
        Symbol symbol,
        Type firstType,
        Type nextType,
        CancellationToken cancellation);
}

internal sealed class FunctionDeclarations(TypeContext context, CheckerSymbols symbols, TypeParameterScopes scopes,
    TypeConstraints constraints, TypeInstantiation instantiation, BaseTypes bases, TypeRelations relations,
    SymbolTypes values, VariableTypes variables, BindingTypes bindings, TypeProperties properties,
    MemberAccessRules access, MemberAccessibility accessibility, IFunctionDeclarationHost host)
{
    internal async ValueTask<bool> GrammarAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        if (await host.FunctionModifiersAsync(node, cancellation).ConfigureAwait(false))
            return true;
        var signature = (IFunctionSignature)node;
        bool grammarError = false;
        bool grammar = SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0;
        void Error(SyntaxNode at, DiagnosticCode code)
        {
            if (grammar)
            {
                grammarError = true;
                host.ExpressionError(at, code);
            }
        }
        if (signature.TypeParameters is { Count: 0 })
        {
            Error(node, DiagnosticCode.TypeParameterListCannotBeEmpty);
            return grammarError;
        }
        bool optional = false;
        var parameters = signature.Parameters!;
        for (int i = 0; i < parameters.Count; i++)
        {
            var parameter = (ParameterDeclarationNode)parameters[i];
            if (parameter.DotDotDotToken is not null)
            {
                if (i != parameters.Count - 1)
                {
                    Error(parameter.DotDotDotToken, DiagnosticCode.ARestParameterMustBeLastInAParameterList);
                    return grammarError;
                }
                if ((parameter.Flags & NodeFlags.Ambient) == 0 && parameters.HasTrailingComma)
                    Error(parameter, DiagnosticCode.ARestParameterOrBindingPatternMayNotHaveATrailingComma);
                if (parameter.QuestionToken is not null)
                {
                    Error(parameter.QuestionToken, DiagnosticCode.ARestParameterCannotBeOptional);
                    return grammarError;
                }
                if (parameter.Initializer is not null)
                {
                    Error(parameter.Name!, DiagnosticCode.ARestParameterCannotHaveAnInitializer);
                    return grammarError;
                }
            }
            else if (parameter.QuestionToken is not null)
            {
                optional = true;
                if ((parameter.QuestionToken.Flags & NodeFlags.Reparsed) == 0 && parameter.Initializer is not null)
                {
                    Error(parameter.Name!, DiagnosticCode.ParameterCannotHaveQuestionMarkAndInitializer);
                    return grammarError;
                }
            }
            else if (optional && parameter.Initializer is null)
            {
                Error(parameter.Name!, DiagnosticCode.ARequiredParameterCannotFollowAnOptionalParameter);
                return grammarError;
            }
        }
        var file = SemanticSyntax.Source(node);
        if (node is ArrowFunctionNode arrow && file is not null)
        {
            if (arrow.TypeParameters is { Count: 1 } typeParameters && !typeParameters.HasTrailingComma
                && ((TypeParameterDeclarationNode)typeParameters[0]).Constraint is null
                && (file.FileName.EndsWith(".mts", StringComparison.OrdinalIgnoreCase)
                    || file.FileName.EndsWith(".cts", StringComparison.OrdinalIgnoreCase)))
                Error(
                    typeParameters[0],
                    DiagnosticCode.ThisSyntaxIsReservedInFilesWithTheMtsOrCtsExtensionAddATrailingCommaOrExplicitConstraint);
            var token = arrow.EqualsGreaterThanToken!;
            string text = file.Source.Text[file.Source.ToUtf16Position(token.Pos)..file.Source.ToUtf16Position(token.End)];
            if (text.Any(c => c is '\n' or '\r' or '\u2028' or '\u2029'))
            {
                Error(token, DiagnosticCode.LineTerminatorNotPermittedBeforeArrow);
                return grammarError;
            }
        }
        if (host.TargetYear >= 2016 && file is not null && SemanticSyntax.Body(node) is BlockNode { Statements: { } statements })
        {
            foreach (var statement in statements)
            {
                if (statement is not ExpressionStatementNode { Expression: StringLiteralNode literal })
                    break;
                if (literal.End < 12)
                    continue;
                var raw = file.Source.Bytes.Span[(literal.End - 12)..literal.End];
                if (!raw.SequenceEqual("\"use strict\""u8) && !raw.SequenceEqual("'use strict'"u8))
                    continue;
                var nonSimple = parameters.OfType<ParameterDeclarationNode>().Where(
                    p => p.Initializer is not null || p.Name is BindingPatternNode || p.DotDotDotToken is not null).ToArray();
                if (nonSimple.Length == 0)
                    break;
                foreach (var parameter in nonSimple)
                    host.ExpressionError(parameter, DiagnosticCode.ThisParameterIsNotAllowedWithUseStrictDirective);
                host.ExpressionError(statement, DiagnosticCode.XUseStrictDirectiveCannotBeUsedWithNonSimpleParameterList);
                return true;
            }
        }
        if (!grammarError && node is MethodDeclarationNode { Parent: ObjectLiteralExpressionNode, Body: null })
            Error(node, DiagnosticCode.X0Expected);
        return grammarError;
    }

    internal async ValueTask CheckAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        await host.SignatureEnvironmentAsync(node, cancellation).ConfigureAwait(false);
        var declaration = node as IFunctionSignature;
        var declaredParameters = node is IndexSignatureDeclarationNode index ? index.Parameters! : declaration!.Parameters!;
        var typeParameters = declaration?.TypeParameters;
        if (typeParameters is not null)
            foreach (TypeParameterDeclarationNode parameter in typeParameters)
                await TypeParameterAsync(parameter, cancellation).ConfigureAwait(false);
        foreach (ParameterDeclarationNode parameter in declaredParameters)
        {
            await host.ParameterEnvironmentAsync(parameter, cancellation).ConfigureAwait(false);
            await VariableAsync(parameter, cancellation).ConfigureAwait(false);
            if (parameter.Initializer is null && parameter.QuestionToken is not null && parameter.Name is BindingPatternNode
                && SemanticSyntax.Body(node) is not null)
                host.ExpressionError(parameter, DiagnosticCode.ABindingPatternParameterCannotBeOptionalInAnImplementationSignature);
            if (parameter.Name is IdentifierNode { Text: "this" or "new" })
            {
                if (declaredParameters[0] != parameter)
                    host.ExpressionError(parameter, DiagnosticCode.A0ParameterMustBeTheFirstParameter);
                if (node is ConstructorDeclarationNode or ConstructSignatureDeclarationNode or ConstructorTypeNode)
                    host.ExpressionError(parameter, DiagnosticCode.AConstructorCannotHaveAThisParameter);
                if (node is ArrowFunctionNode)
                    host.ExpressionError(parameter, DiagnosticCode.AnArrowFunctionCannotHaveAThisParameter);
                if (node is GetAccessorDeclarationNode or SetAccessorDeclarationNode)
                    host.ExpressionError(parameter, DiagnosticCode.XGetAndSetAccessorsCannotDeclareThisParameters);
            }
            if (parameter.DotDotDotToken is not null && parameter.Name is not BindingPatternNode
                && !await relations.RelatedAsync(await values.GetAsync(symbols.Declaration(parameter)!, cancellation).ConfigureAwait(false),
                    host.AnyReadonlyArray, RelationKind.Assignable, cancellation).ConfigureAwait(false))
                host.ExpressionError(parameter, DiagnosticCode.ARestParameterMustBeOfAnArrayType);
        }
        if (node is ITypedNode { Type: { } annotation })
            await host.CheckFunctionReturnAsync(
                node,
                annotation,
                await host.CheckedFunctionTypeAsync(annotation, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);
        else if (host.NoImplicitAny && node is ConstructSignatureDeclarationNode or CallSignatureDeclarationNode)
            host.ExpressionError(
                node,
                node is ConstructSignatureDeclarationNode
                    ? DiagnosticCode.ConstructSignatureWhichLacksReturnTypeAnnotationImplicitlyHasAnAnyReturnType
                    : DiagnosticCode.CallSignatureWhichLacksReturnTypeAnnotationImplicitlyHasAnAnyReturnType);
        else if (node is MethodSignatureDeclarationNode)
            await host.ReportImplicitAnyAsync(node, context.AnyType, cancellation).ConfigureAwait(false);
        if (node is not IndexSignatureDeclarationNode)
            host.RegisterUnused(node);
    }

    internal async ValueTask TypeParameterAsync(TypeParameterDeclarationNode node, CancellationToken cancellation)
    {
        NodeList? parameters = node.Parent switch
        {
            IFunctionSignature signature => signature.TypeParameters,
            ClassDeclarationNode declaration => declaration.TypeParameters,
            ClassExpressionNode expression => expression.TypeParameters,
            InterfaceDeclarationNode declaration => declaration.TypeParameters,
            TypeAliasDeclarationNode declaration => declaration.TypeParameters,
            _ => null
        };
        if (parameters is not null)
            foreach (var previous in parameters)
            {
                if (previous == node)
                    break;
                if (symbols.Declaration(previous) == symbols.Declaration(node))
                {
                    host.ExpressionError(node.Name!, DiagnosticCode.DuplicateIdentifier0);
                    break;
                }
            }
        await host.TypeParameterModifiersAsync(node, cancellation).ConfigureAwait(false);
        if (node.Expression is not null && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
            host.ExpressionError(node.Expression, DiagnosticCode.TypeExpected);
        if (node.Constraint is { } constraintNode)
            await host.CheckedFunctionTypeAsync(constraintNode, cancellation).ConfigureAwait(false);
        if (node.DefaultType is { } defaultNode)
            await host.CheckedFunctionTypeAsync(defaultNode, cancellation).ConfigureAwait(false);
        var parameter = scopes.Parameter(symbols.Declaration(node)!);
        await constraints.BaseConstraintAsync(parameter, cancellation).ConfigureAwait(false);
        if (await constraints.ResolvedDefaultAsync(parameter, cancellation).ConfigureAwait(false) == context.CircularConstraintType)
            host.ExpressionError(node.DefaultType!, DiagnosticCode.TypeParameter0HasACircularDefault);
        var constraint = await constraints.ConstraintAsync(parameter, cancellation).ConfigureAwait(false);
        var defaultType = await constraints.DefaultAsync(parameter, cancellation).ConfigureAwait(false);
        if (constraint is not null && defaultType is not null)
        {
            var target = await bases.WithThisAsync((await instantiation.InstantiateAsync(constraint,
                TypeMapper.Create([parameter], [defaultType]),
                cancellation: cancellation).ConfigureAwait(false))!,
                defaultType,
                cancellation: cancellation).ConfigureAwait(false);
            await host.CheckConstraintAsync(defaultType, target, node.DefaultType!, cancellation).ConfigureAwait(false);
        }
        if (node.Name!.Text is "any" or "unknown" or "never" or "number" or "string" or "boolean" or "bigint" or "symbol" or "void"
            or "object" or "undefined")
            host.ExpressionError(node.Name, DiagnosticCode.TypeParameterNameCannotBe0);
        host.DeferExpression(node);
        if (parameters is not null)
        {
            int index = parameters.IndexOf(node);
            if (node.DefaultType is { } defaultAnnotation)
                foreach (var reference in defaultAnnotation.DescendantsAndSelf().OfType<TypeReferenceNode>())
                    if (await host.TypeFromNodeAsync(reference, cancellation).ConfigureAwait(false) is TypeParameter type)
                        for (int i = index; i < parameters.Count; i++)
                            if (type.Symbol == symbols.Declaration(parameters[i]))
                                host.ExpressionError(
                                    reference,
                                    DiagnosticCode.TypeParameterDefaultsCanOnlyReferencePreviouslyDeclaredTypeParameters);
            if (node.DefaultType is null
                && parameters.Take(index).OfType<TypeParameterDeclarationNode>().Any(p => p.DefaultType is not null))
                host.ExpressionError(node, DiagnosticCode.RequiredTypeParametersMayNotFollowOptionalTypeParameters);
        }
    }

    internal async ValueTask VariableAsync(SyntaxNode node, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        var name = ((INamedNode)node).Name;
        if (name is null)
            return;
        var initializer = (node as IInitializedNode)?.Initializer;
        if (node is not BindingElementNode && node is ITypedNode { Type: { } annotation })
            await host.CheckedFunctionTypeAsync(annotation, cancellation).ConfigureAwait(false);
        if (node is BindingElementNode element)
        {
            if (await host.BindingEnvironmentAsync(element, cancellation).ConfigureAwait(false))
                return;
            var parent = node.Parent!.Parent!;
            var parentType = await bindings.ParentAsync(
                parent,
                element.DotDotDotToken is null ? CheckMode.Normal : CheckMode.RestBindingElement,
                cancellation).ConfigureAwait(false);
            var propertyName = element.PropertyName ?? name;
            if (parentType is not null && propertyName is not BindingPatternNode)
            {
                var key = await host.LiteralNameTypeAsync(propertyName, cancellation).ConfigureAwait(false);
                if ((key.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0
                    && await properties.PropertyAsync(
                        parentType,
                        MappedMembers.PropertyName(key),
                        cancellation: cancellation).ConfigureAwait(false) is { } property)
                {
                    access.MarkReferenced(property, node, node, null, cancellation);
                    await accessibility.CheckAsync(node, (parent as IInitializedNode)?.Initializer?.Kind == SyntaxKind.SuperKeyword,
                        false, parentType, property, cancellation: cancellation).ConfigureAwait(false);
                }
            }
        }
        if (name is BindingPatternNode pattern)
            foreach (var child in pattern.Elements!.OfType<BindingElementNode>())
                await VariableAsync(child, cancellation).ConfigureAwait(false);
        var root = SemanticSyntax.RootDeclaration(node);
        var function = root.Parent!;
        bool parameterDeclaration = root is ParameterDeclarationNode;
        var body = SemanticSyntax.Body(function);
        if (initializer is not null && parameterDeclaration && (body is null || body.Pos == body.End))
        {
            host.ExpressionError(node, DiagnosticCode.AParameterInitializerIsOnlyAllowedInAFunctionOrConstructorImplementation);
            return;
        }
        if (name is BindingPatternNode binding)
        {
            if ((node.Flags & NodeFlags.Ambient) != 0 || parameterDeclaration && SemanticSyntax.Body(function) is null)
                return;
            bool empty = !binding.Elements!.OfType<BindingElementNode>().Any(e => e.Name is not null);
            if (initializer is not null || empty)
            {
                var widened = await variables.GetAsync(node, false, cancellation).ConfigureAwait(false);
                if (initializer is not null)
                {
                    var value = await host.CachedExpressionAsync(initializer, 0, cancellation).ConfigureAwait(false);
                    if (context.StrictNullChecks && empty)
                        await host.NonNullBindingAsync(value, node, cancellation).ConfigureAwait(false);
                    else
                        await host.CheckLiteralAssignableAsync(
                            value,
                            await variables.GetAsync(node, false, cancellation).ConfigureAwait(false),
                            node,
                            initializer,
                            cancellation).ConfigureAwait(false);
                }
                if (empty)
                {
                    if (binding.Kind == SyntaxKind.ArrayBindingPattern)
                        await host.BindingIterationAsync(widened, node, false, cancellation).ConfigureAwait(false);
                    else if (context.StrictNullChecks)
                        await host.NonNullBindingAsync(widened, node, cancellation).ConfigureAwait(false);
                }
            }
            return;
        }
        var symbol = symbols.Declaration(node)!;
        if (await host.VariableAliasAsync(node, symbol, cancellation).ConfigureAwait(false))
            return;
        if (name is BigIntLiteralNode)
            host.ExpressionError(name, DiagnosticCode.ABigintLiteralCannotBeUsedAsAPropertyName);
        host.CheckDeclarationFlags(node, symbol, cancellation);
        var type = await values.GetAsync(symbol, cancellation).ConfigureAwait(false);
        if (type == context.AutoType)
            type = context.AnyType;
        else if (type == host.AutoArray)
            type = host.AnyArray;
        if (node != symbol.ValueDeclaration)
        {
            var declarationType = await variables.GetAsync(node, false, cancellation).ConfigureAwait(false);
            if (declarationType == context.AutoType)
                declarationType = context.AnyType;
            else if (declarationType == host.AutoArray)
                declarationType = host.AnyArray;
            if (!ErrorType(type) && !ErrorType(declarationType) && (symbol.Flags & SymbolFlags.Assignment) == 0
                && !await relations.RelatedAsync(type, declarationType, RelationKind.Identity, cancellation).ConfigureAwait(false))
                await host.VariableDeclarationConflictAsync(node, symbol, type, declarationType, cancellation).ConfigureAwait(false);
            type = declarationType;
        }
        if (initializer is not null && node.Parent?.Parent?.Kind != SyntaxKind.ForInStatement)
            await host.CheckLiteralAssignableAsync(
                await host.CachedExpressionAsync(initializer, 0, cancellation).ConfigureAwait(false),
                type,
                node,
                initializer,
                cancellation).ConfigureAwait(false);
        if (node is VariableDeclarationNode or BindingElementNode)
            host.CheckVariableShadowing(node, cancellation);
    }

    private bool ErrorType(Type type) => type == context.ErrorType || (type.Flags & TypeFlags.Any) != 0 && type.Alias is not null;
}
