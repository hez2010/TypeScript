using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IVariableTypeHost
{
    bool NoImplicitAny { get; }
    bool UseUnknownInCatchVariables { get; }
    Type AutoArray { get; }
    Type AnyArray { get; }

    bool IsArray(Type type);

    ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference type, CancellationToken cancellation);

    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> DeclarationInitializerAsync(SyntaxNode declaration, CheckMode mode, CancellationToken cancellation);

    ValueTask<Type?> ContextualParameterAsync(ParameterDeclarationNode parameter, CancellationToken cancellation);

    ValueTask<Type?> FullParameterAsync(ParameterDeclarationNode parameter, CancellationToken cancellation);

    ValueTask<Type?> PropertyInitializationAsync(PropertyDeclarationNode property, CancellationToken cancellation);

    ValueTask<Type?> BindingElementAsync(BindingElementNode element, CancellationToken cancellation);

    ValueTask<Type> BindingPatternAsync(SyntaxNode pattern, CancellationToken cancellation);

    ValueTask<Type> IterationVariableAsync(
        VariableDeclarationNode declaration,
        SyntaxNode statement,
        CheckMode mode,
        CancellationToken cancellation);

    ValueTask<bool> NullOrUndefinedAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<bool> BindableNameAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> SymbolTypeAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<Type> SymbolConstructorPropertyAsync(SyntaxNode declaration, Type type, CancellationToken cancellation);

    ValueTask ReportWideningAsync(SyntaxNode declaration, Type type, CancellationToken cancellation);

    ValueTask ReportImplicitAnyAsync(SyntaxNode declaration, Type type, CancellationToken cancellation);
}

internal sealed class VariableTypes(TypeContext context, TypeAlgebra algebra, TypeWidening widening,
    CheckerSymbols symbols, Signatures signatures, IVariableTypeHost host)
{
    internal async ValueTask<Type> GetAsync(SyntaxNode declaration, bool reportErrors, CancellationToken cancellation = default)
        =>
            await WidenAsync(
                await DeclaredOrInferredAsync(declaration, true, cancellation: cancellation).ConfigureAwait(false),
                declaration,
                reportErrors,
                cancellation).ConfigureAwait(false);

    internal async ValueTask<Type?> DeclaredOrInferredAsync(SyntaxNode declaration, bool includeOptionality = true, CheckMode mode = 0,
        CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (declaration is VariableDeclarationNode variable
            && variable.Parent?.Parent is { Kind: SyntaxKind.ForInStatement or SyntaxKind.ForOfStatement } statement)
            return await host.IterationVariableAsync(variable, statement, mode, cancellation).ConfigureAwait(false);
        if (declaration is BindingElementNode binding)
            return await host.BindingElementAsync(binding, cancellation).ConfigureAwait(false);
        bool property = declaration is PropertySignatureDeclarationNode
            || declaration is PropertyDeclarationNode && !SemanticSyntax.HasModifier(declaration, SyntaxKind.AccessorKeyword);
        bool optional = includeOptionality && Optional(declaration);
        var declared = declaration is ITypedNode { Type: { } annotation }
            ? await host.TypeFromNodeAsync(annotation, cancellation).ConfigureAwait(false)
            : null;
        if (declaration.Parent is CatchClauseNode)
            return declared is not null ? (declared.Flags & TypeFlags.AnyOrUnknown) != 0 ? declared : context.ErrorType
                : host.UseUnknownInCatchVariables ? context.UnknownType : context.AnyType;
        if (declared is not null)
            return await OptionalAsync(declared, property, optional, cancellation).ConfigureAwait(false);
        var initializer = (declaration as IInitializedNode)?.Initializer;
        if (host.NoImplicitAny && declaration is VariableDeclarationNode { Name: IdentifierNode } normal
            && !Exported(normal) && (normal.Flags & NodeFlags.Ambient) == 0)
        {
            if (!Constant(normal)
                && (initializer is null || await host.NullOrUndefinedAsync(initializer, cancellation).ConfigureAwait(false)))
                return context.AutoType;
            if (initializer is ArrayLiteralExpressionNode { Elements.Count: 0 })
                return host.AutoArray;
        }
        if (declaration is ParameterDeclarationNode parameter)
        {
            if (symbols.Declaration(parameter) is null)
                return null;
            if (parameter.Parent is SetAccessorDeclarationNode setter
                && await host.BindableNameAsync(setter, cancellation).ConfigureAwait(false)
                && symbols.Declaration(setter)?.Declarations.OfType<GetAccessorDeclarationNode>().FirstOrDefault() is { } getter)
            {
                var signature = await signatures.FromDeclarationAsync(getter, cancellation).ConfigureAwait(false);
                if (parameter.Name is IdentifierNode { Text: "this" } && signature.ThisParameter is { } thisParameter)
                    return await host.SymbolTypeAsync(thisParameter, cancellation).ConfigureAwait(false);
                return await signatures.ReturnAsync(signature, cancellation).ConfigureAwait(false);
            }
            var full = await host.FullParameterAsync(parameter, cancellation).ConfigureAwait(false);
            if (full is not null)
                return full;
            if (await host.ContextualParameterAsync(parameter, cancellation).ConfigureAwait(false) is { } contextual)
                return await OptionalAsync(contextual, false, optional, cancellation).ConfigureAwait(false);
        }
        if (initializer is not null)
        {
            var type = await host.DeclarationInitializerAsync(declaration, mode, cancellation).ConfigureAwait(false);
            type = await WidenInitializerAsync(declaration, type, cancellation).ConfigureAwait(false);
            return await OptionalAsync(type, property, optional, cancellation).ConfigureAwait(false);
        }
        if (host.NoImplicitAny && declaration is PropertyDeclarationNode propertyDeclaration)
        {
            var type = await host.PropertyInitializationAsync(propertyDeclaration, cancellation).ConfigureAwait(false);
            return type is null ? null : await OptionalAsync(type, true, optional, cancellation).ConfigureAwait(false);
        }
        if (declaration is JsxAttributeNode)
            return context.TrueType;
        if ((declaration as INamedNode)?.Name is { Kind: SyntaxKind.ObjectBindingPattern or SyntaxKind.ArrayBindingPattern })
            return await host.BindingPatternAsync(((INamedNode)declaration).Name!, cancellation).ConfigureAwait(false);
        return null;
    }

    internal async ValueTask<Type> WidenInitializerAsync(SyntaxNode declaration, Type type, CancellationToken cancellation = default)
    {
        var result = Constant(declaration) || Readonly(declaration)
            ? type
            : await widening.LiteralAsync(type, cancellation).ConfigureAwait(false);
        if ((declaration.Flags & NodeFlags.JavaScriptFile) != 0)
        {
            if (result == (context.StrictNullChecks ? context.ImplicitNeverType : context.UndefinedWideningType))
            {
                await host.ReportImplicitAnyAsync(declaration, context.AnyType, cancellation).ConfigureAwait(false);
                return context.AnyType;
            }
            // Array emptiness and contextual inference require the host's inferred initializer.
            if (host.IsArray(result) && (await host.TypeArgumentsAsync((TypeReference)result, cancellation).ConfigureAwait(false))[0]
                == (context.StrictNullChecks ? context.ImplicitNeverType : context.UndefinedWideningType))
            {
                await host.ReportImplicitAnyAsync(declaration, host.AnyArray, cancellation).ConfigureAwait(false);
                return host.AnyArray;
            }
        }
        return result;
    }

    internal async ValueTask<Type> WidenAsync(
        Type? type,
        SyntaxNode declaration,
        bool reportErrors,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (type is not null)
        {
            if ((type.Flags & TypeFlags.ESSymbol) != 0)
                type = await host.SymbolConstructorPropertyAsync(declaration, type, cancellation).ConfigureAwait(false);
            if (reportErrors)
                await host.ReportWideningAsync(declaration, type, cancellation).ConfigureAwait(false);
            if ((type.Flags & TypeFlags.UniqueESSymbol) != 0
                && (declaration is BindingElementNode || declaration is not ITypedNode { Type: not null })
                && type.Symbol != symbols.Declaration(declaration))
                type = context.ESSymbolType;
            return await widening.GetAsync(type, cancellation).ConfigureAwait(false);
        }
        type = declaration is ParameterDeclarationNode { DotDotDotToken: not null } ? host.AnyArray : context.AnyType;
        if (reportErrors && !PrivateAmbient(declaration))
            await host.ReportImplicitAnyAsync(declaration, type, cancellation).ConfigureAwait(false);
        return type;
    }

    private ValueTask<Type> OptionalAsync(Type type, bool property, bool optional, CancellationToken cancellation)
        =>
            context.StrictNullChecks && optional
                ? algebra.UnionAsync([type, property ? context.UndefinedOrMissingType : context.UndefinedType], cancellation: cancellation)
                : ValueTask.FromResult(type);

    internal static bool Optional(SyntaxNode node) => node switch
    {
        ParameterDeclarationNode p => p.QuestionToken is not null,
        PropertyDeclarationNode p => p.PostfixToken?.Kind == SyntaxKind.QuestionToken,
        PropertySignatureDeclarationNode p => p.PostfixToken?.Kind == SyntaxKind.QuestionToken,
        _ => false
    };

    internal static bool Constant(SyntaxNode node) =>
        (node.Flags & NodeFlags.Constant) != 0 || node.Parent is VariableDeclarationListNode list && (list.Flags & NodeFlags.Constant) != 0;

    internal static bool Readonly(SyntaxNode node) => SemanticSyntax.HasModifier(node, SyntaxKind.ReadonlyKeyword)
            && !(node is ParameterDeclarationNode && node.Parent is ConstructorDeclarationNode);

    private static bool Exported(SyntaxNode node) => SemanticSyntax.HasModifier(node, SyntaxKind.ExportKeyword)
            || node.Parent?.Parent is VariableStatementNode statement && SemanticSyntax.HasModifier(statement, SyntaxKind.ExportKeyword);

    private static bool PrivateAmbient(SyntaxNode node) => (node.Flags & NodeFlags.Ambient) != 0
            && (SemanticSyntax.HasModifier(node, SyntaxKind.PrivateKeyword) || (node as INamedNode)?.Name is PrivateIdentifierNode);
}
