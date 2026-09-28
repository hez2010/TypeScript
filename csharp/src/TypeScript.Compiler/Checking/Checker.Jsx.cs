using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private async ValueTask<Type> JsxAttributeTypeAsync(JsxAttributeNode node, CheckMode mode, CancellationToken cancellation) =>
        node.Initializer is null ? context.TrueType : await Contexts.MutableAsync(node.Initializer, mode, cancellation);

    private async ValueTask<Type> CheckJsxAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation)
    {
        switch (node)
        {
            case JsxExpressionNode expression:
                if (expression.Expression is null)
                    return context.ErrorType;
                if (expression.Expression is BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.CommaToken }
                    && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
                    Error(expression.Expression, DiagnosticCode.JSXExpressionsMayNotUseTheCommaOperatorDidYouMeanToWriteAnArray);
                var type = await Expressions.CheckAsync(expression.Expression, mode, cancellation);
                if (expression.DotDotDotToken is not null && type != context.AnyType && !IsArray(type))
                    Error(node, DiagnosticCode.JSXSpreadChildMustBeAnArrayType);
                return type;
            case JsxAttributesNode:
                DeferExpression(node);
                return await JsxAttributesTypeAsync(node.Parent!, mode, cancellation);
            case JsxFragmentNode fragment:
                await CheckJsxOpeningAsync(fragment.OpeningFragment!, cancellation);
                var file = SemanticSyntax.Source(node)!;
                var options = program.Symbols.Program.Configuration.Options;
                if (JsxMode is 2 or 4 or 5 && (options.JsxFactory is not null || JsxPragma(file, "jsx") is not null)
                    && options.JsxFragmentFactory is null && JsxPragma(file, "jsxfrag") is null)
                    Error(
                        node,
                        options.JsxFactory is not null
                            ? DiagnosticCode.TheJsxFragmentFactoryCompilerOptionMustBeProvidedToUseJSXFragmentsWithTheJsxFactoryCompilerOption
                            : DiagnosticCode.AnJsxFragPragmaIsRequiredWhenUsingAnJsxPragmaWithJSXFragments);
                await JsxChildrenAsync(node, 0, cancellation);
                var elementType = await JsxTypeAsync("Element", node, cancellation);
                return elementType == context.ErrorType ? context.AnyType : elementType;
            default:
                DeferExpression(node);
                return await JsxTypeAsync("Element", node, cancellation);
        }
    }

    private async ValueTask CheckJsxDeferredAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (node is JsxElementNode element)
        {
            await CheckJsxOpeningAsync(element.OpeningElement!, cancellation);
            if (IntrinsicJsx(element.ClosingElement!.TagName))
                await JsxIntrinsicTypeAsync(element.ClosingElement, cancellation);
            else
                await Expressions.CheckAsync(element.ClosingElement.TagName!, cancellation: cancellation);
            await JsxChildrenAsync(element, 0, cancellation);
        }
        else
            await CheckJsxOpeningAsync(node, cancellation);
    }

    private async ValueTask CheckJsxOpeningAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (node is not JsxOpeningFragmentNode)
            JsxGrammar(node);
        if (JsxMode == 0)
            Error(node, DiagnosticCode.CannotUseJSXUnlessTheJsxFlagIsProvided);
        await MarkJsxFactoryAsync(node, cancellation);
        var signature = await CallResolution.GetAsync(node, cancellation: cancellation);
        DeprecatedSignature(node, signature);
        if (node is JsxOpeningFragmentNode)
            return;
        Type? constraint = null;
        if (await JsxNamespaceAsync(node, cancellation) is { } ns
            && program.Symbols.Lookup(ns.Exports, "ElementType", SymbolFlags.Type) is { } elementSymbol)
            constraint = await JsxInstantiateAsync(elementSymbol, [], node, cancellation);
        Type checkedType;
        DiagnosticCode relationCode = DiagnosticCode.ItsType0IsNotAValidJSXElementType;
        if (constraint is not null && constraint != context.ErrorType)
            checkedType = IntrinsicJsx(JsxTag(node))
                ? context.GetStringLiteralType(JsxName(JsxTag(node)!))
                : await Expressions.CheckAsync(JsxTag(node)!, cancellation: cancellation);
        else
        {
            checkedType = await Signatures.ReturnAsync(signature, cancellation);
            int kind = await JsxReferenceKindAsync(node, cancellation);
            relationCode = kind == 0
                ? DiagnosticCode.ItsInstanceType0IsNotAValidJSXElement
                : kind == 1 ? DiagnosticCode.ItsReturnType0IsNotAValidJSXElement : DiagnosticCode.ItsElementType0IsNotAValidJSXElement;
            var result = await JsxTypeAsync("Element", node, cancellation);
            var instance = await JsxTypeAsync("ElementClass", node, cancellation);
            var function = await Algebra.UnionAsync([result, context.NullType], cancellation: cancellation);
            constraint = kind == 0 ? instance : kind == 1 ? function
                : instance == context.ErrorType ? null : await Algebra.UnionAsync([function, instance], cancellation: cancellation);
        }
        if (constraint is not null && constraint != context.ErrorType && !await AssignableAsync(checkedType, constraint, cancellation))
        {
            var tag = JsxTag(node)!;
            var head = CheckerDiagnostic.Create(tag, Messages.X_0_cannot_be_used_as_a_JSX_component,
                CheckerDiagnostic.DeclarationName(tag));
            await ReportRelationMessageAsync(tag, relationCode, checkedType, constraint, RelationKind.Assignable, cancellation, head);
        }
    }

    private void JsxGrammar(SyntaxNode node)
    {
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0)
            return;
        var tag = JsxTag(node)!;
        if (tag is PropertyAccessExpressionNode { Expression: JsxNamespacedNameNode namespaced })
            Error(namespaced, DiagnosticCode.JSXPropertyAccessExpressionsCannotIncludeJSXNamespaceNames);
        if (tag is JsxNamespacedNameNode name && JsxMode is 2 or 4 or 5 && !IntrinsicJsx(name.Namespace))
            Error(tag, DiagnosticCode.ReactComponentsCannotIncludeJSXNamespaceNames);
        InstantiationGrammar(node, CallArguments.TypeNodes(node));
        var seen = new HashSet<TextSlice>();
        foreach (var attribute in JsxAttributes(node)!.Properties!.OfType<JsxAttributeNode>())
        {
            if (!seen.Add(JsxName(attribute.Name!)))
            {
                Error(attribute.Name!, DiagnosticCode.JSXElementsCannotHaveMultipleAttributesWithTheSameName);
                break;
            }
            if (attribute.Initializer is JsxExpressionNode { Expression: null })
            {
                Error(attribute.Initializer, DiagnosticCode.JSXAttributesMustOnlyBeAssignedANonEmptyExpression);
                break;
            }
        }
    }

    private async ValueTask MarkJsxFactoryAsync(SyntaxNode node, CancellationToken cancellation, bool directAliasMark = false)
    {
        if (await JsxImplicitModuleAsync(node, cancellation) is not null)
            return;
        TextSlice name = JsxFactoryRoot(JsxFactoryName(node, node is JsxOpeningFragmentNode));
        if (!(node is JsxOpeningFragmentNode && name == "null"))
        {
            var flags = JsxMode is 1 or 3 ? SymbolFlags.Value & ~SymbolFlags.Enum : SymbolFlags.Value;
            var symbol = program.Symbols.NameResolver(cancellation).Resolve(JsxTag(node) ?? node, name, flags,
                JsxMode == 2
                    ? TypeScript.Compiler.Diagnostics.Messages.This_JSX_tag_requires_0_to_be_in_scope_but_it_could_not_be_found
                    : null,
                isUse: true);
            if (symbol is not null)
            {
                program.Symbols.MarkReferenced(symbol, SymbolFlags.All);
                if ((symbol.Flags & SymbolFlags.Alias) != 0
                    && await program.Aliases.TypeOnlyAsync(symbol, cancellation: cancellation) is null)
                {
                    if (directAliasMark)
                        await AliasReferences.MarkDirectAsync(symbol, cancellation);
                    else
                        await AliasReferences.MarkAsync(symbol, node, cancellation);
                }
            }
        }
        if (node is JsxOpeningFragmentNode)
        {
            TextSlice factory = JsxFactoryRoot(JsxFactoryName(node));
            program.Symbols.NameResolver(cancellation).Resolve(
                node,
                factory,
                JsxMode is 1 or 3 ? SymbolFlags.Value & ~SymbolFlags.Enum : SymbolFlags.Value,
                JsxMode == 2
                    ? TypeScript.Compiler.Diagnostics.Messages.This_JSX_tag_requires_0_to_be_in_scope_but_it_could_not_be_found
                    : null,
                isUse: true);
        }
    }

    private async ValueTask<Type> JsxFragmentTypeAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var file = SemanticSyntax.Source(node)!;
        if (jsxFragmentTypes.TryGetValue(file, out var cached))
            return cached;
        TextSlice name = JsxFactoryRoot(JsxFactoryName(node, true));
        if ((JsxMode != 2 && program.Symbols.Program.Configuration.Options.JsxFragmentFactory is null) || name == "null")
            return jsxFragmentTypes[file] = context.AnyType;
        var symbol = await JsxImplicitModuleAsync(node, cancellation);
        symbol ??= program.Symbols.NameResolver(cancellation).Resolve(
            node,
            name,
            JsxMode is 1 or 3 ? SymbolFlags.Value & ~SymbolFlags.Enum : SymbolFlags.Value,
            TypeScript.Compiler.Diagnostics.Messages.Using_JSX_fragments_requires_fragment_factory_0_to_be_in_scope_but_it_could_not_be_found,
            isUse: true);
        if (symbol is null)
            return jsxFragmentTypes[file] = context.ErrorType;
        if (symbol.Name == "Fragment")
            return jsxFragmentTypes[file] = await Values.GetAsync(symbol, cancellation);
        var resolved = (await program.Aliases.SymbolAsync(symbol, cancellation: cancellation))!;
        var fragment = program.Symbols.Lookup(
            await program.ExportsAsync(resolved, cancellation),
            "Fragment",
            SymbolFlags.BlockScopedVariable);
        return jsxFragmentTypes[file] = fragment is null ? context.ErrorType : await Values.GetAsync(fragment, cancellation);
    }
}
