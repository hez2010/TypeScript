using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IValueExpressionHost
{
    ValueTask<Type> CheckExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask<Type> ImportMetaTypeAsync(CancellationToken cancellation);

    bool IsReadonly(Symbol symbol);

    void ExpressionError(SyntaxNode node, DiagnosticCode code);
}

internal sealed class ValueExpressionChecks(TypeContext context, CheckerLinks links, CheckerSymbols symbols,
    SymbolTypes values, TypeFactQueries facts, IValueExpressionHost host)
{
    internal async ValueTask<Type> DeleteAsync(DeleteExpressionNode node, CancellationToken cancellation = default)
    {
        await host.CheckExpressionAsync(node.Expression!, 0, cancellation).ConfigureAwait(false);
        var expression = node.Expression!;
        while (expression is ParenthesizedExpressionNode parentheses)
            expression = parentheses.Expression!;
        if (expression is not PropertyAccessExpressionNode and not ElementAccessExpressionNode)
        {
            host.ExpressionError(expression, DiagnosticCode.TheOperandOfADeleteOperatorMustBeAPropertyReference);
            return context.BooleanType;
        }
        if (expression is PropertyAccessExpressionNode { Name: PrivateIdentifierNode })
            host.ExpressionError(expression, DiagnosticCode.TheOperandOfADeleteOperatorCannotBeAPrivateIdentifier);
        if (links.SymbolNodes.TryGet(expression)?.ResolvedSymbol is { } resolved && symbols.ExportedValue(resolved) is { } symbol)
        {
            if (host.IsReadonly(symbol))
                host.ExpressionError(expression, DiagnosticCode.TheOperandOfADeleteOperatorCannotBeAReadOnlyProperty);
            else
            {
                var type = await values.GetAsync(symbol, cancellation).ConfigureAwait(false);
                if (context.StrictNullChecks && (type.Flags & (TypeFlags.AnyOrUnknown | TypeFlags.Never)) == 0
                    && !(context.ExactOptionalPropertyTypes ? (symbol.Flags & SymbolFlags.Optional) != 0
                        : await facts.GetAsync(type, TypeFacts.IsUndefined, cancellation).ConfigureAwait(false) != 0))
                    host.ExpressionError(expression, DiagnosticCode.TheOperandOfADeleteOperatorMustBeOptional);
            }
        }
        return context.BooleanType;
    }

    internal async ValueTask<Type> MetaAsync(MetaPropertyNode node, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        TextSlice name = ((IdentifierNode)node.Name!).Text;
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
        {
            if (node.KeywordToken == SyntaxKind.NewKeyword && name != "target")
                host.ExpressionError(node.Name!, DiagnosticCode.X0IsNotAValidMetaPropertyForKeyword1DidYouMean2);
            else if (node.KeywordToken == SyntaxKind.ImportKeyword && name != "meta")
                host.ExpressionError(
                    node.Name!,
                    name == "defer"
                        ? DiagnosticCode.X0Expected
                        : node.Parent is CallExpressionNode { Expression: var expression } && expression == node
                            ? DiagnosticCode.X0IsNotAValidMetaPropertyForKeywordImportDidYouMeanMetaOrDefer
                            : DiagnosticCode.X0IsNotAValidMetaPropertyForKeyword1DidYouMean2);
        }
        if (node.KeywordToken == SyntaxKind.NewKeyword)
        {
            var container = MissingNamePrefixes.ThisContainer(node, false, false);
            if (container is not ConstructorDeclarationNode and not FunctionDeclarationNode and not FunctionExpressionNode)
            {
                host.ExpressionError(
                    node,
                    DiagnosticCode.MetaProperty0IsOnlyAllowedInTheBodyOfAFunctionDeclarationFunctionExpressionOrConstructor);
                return context.ErrorType;
            }
            return await values.GetAsync(
                symbols.Declaration(container is ConstructorDeclarationNode ? container.Parent! : container)!,
                cancellation).ConfigureAwait(false);
        }
        if (node.KeywordToken != SyntaxKind.ImportKeyword)
            throw new InvalidOperationException("Unexpected meta-property keyword");
        if (name == "defer")
            return context.ErrorType;
        var options = symbols.Program.Configuration.Options;
        TextSlice module = options.String("module") ?? options.Number("module") switch
        {
            4 => "system",
            6 => "es2020",
            7 => "es2022",
            99 => "esnext",
            100 => "node16",
            101 => "node18",
            102 => "node20",
            199 => "nodenext",
            200 => "preserve",
            null or 0 => options.EmitTargetYear >= 2022 ? "es2022" : "commonjs",
            _ => "other"
        };
        if (module == "none")
            module = options.EmitTargetYear >= 2022 ? "es2022" : "commonjs";
        if (module.Span is "node16" or "node18" or "node20" or "nodenext")
        {
            if (symbols.Program.SourceFiles.First(f => f.Syntax == SemanticSyntax.Source(node)).ImpliedFormat != ReferenceResolutionMode.Import)
                host.ExpressionError(node, DiagnosticCode.TheImportMetaMetaPropertyIsNotAllowedInFilesWhichWillBuildIntoCommonJSOutput);
        }
        else if (module.Span is not ("es2020" or "es2022" or "esnext" or "system" or "preserve"))
            host.ExpressionError(
                node,
                DiagnosticCode.TheImportMetaMetaPropertyIsOnlyAllowedWhenTheModuleOptionIsEs2020Es2022EsnextSystemNode16Node18Node20OrNodenext);
        return name == "meta" ? await host.ImportMetaTypeAsync(cancellation).ConfigureAwait(false) : context.ErrorType;
    }

    internal void ConstEnum(SyntaxNode node, Type type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if ((type.ObjectFlags & ObjectFlags.Anonymous) == 0 || type.Symbol is not { } symbol || (symbol.Flags & SymbolFlags.ConstEnum) == 0)
            return;
        bool allowed = node.Parent is PropertyAccessExpressionNode property && property.Expression == node
            || node.Parent is ElementAccessExpressionNode element && element.Expression == node
            || node is IdentifierNode or QualifiedNameNode && ImportOrExport(node)
            || node.Parent is TypeQueryNode query && query.ExprName == node || node.Parent is ExportSpecifierNode;
        if (!allowed)
            host.ExpressionError(
                node,
                DiagnosticCode.XConstEnumsCanOnlyBeUsedInPropertyOrIndexAccessExpressionsOrTheRightHandSideOfAnImportDeclarationOrExportAssignmentOrTypeQuery);
        var options = symbols.Program.Configuration.Options;
        var first = node;
        while (first is PropertyAccessExpressionNode or QualifiedNameNode)
            first = first is PropertyAccessExpressionNode access ? access.Expression! : ((QualifiedNameNode)first).Left!;
        if (options.Boolean("isolatedModules") == true || options.Boolean("verbatimModuleSyntax") == true && allowed
            && symbols.NameResolver(cancellation).Resolve(
                node,
                ((IdentifierNode)first).Text,
                SymbolFlags.Alias,
                excludeGlobals: true) is null)
        {
            var declaration = symbol.ValueDeclaration!;
            var source = SemanticSyntax.Source(declaration)!;
            var redirect = symbols.Program.ProjectReferences.Outputs.GetValueOrDefault(source.FileName);
            bool preserved = redirect is not null && (redirect.Project.Options.Boolean("preserveConstEnums") == true
                || redirect.Project.Options.Boolean("isolatedModules") == true || redirect.Project.Options.Boolean("verbatimModuleSyntax") == true);
            if ((declaration.Flags & NodeFlags.Ambient) != 0 && !ReferenceSyntax.ValidTypeOnlyUse(node) && !preserved)
                host.ExpressionError(node, DiagnosticCode.CannotAccessAmbientConstEnumsWhen0IsEnabled);
        }
    }

    private static bool ImportOrExport(SyntaxNode node)
    {
        while (node.Parent is QualifiedNameNode)
            node = node.Parent;
        return node.Parent is ImportEqualsDeclarationNode import && import.ModuleReference == node
            || node.Parent is ExportAssignmentNode export && export.Expression == node;
    }
}
