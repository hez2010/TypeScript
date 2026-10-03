using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : IVariableTypeHost
{
    internal VariableTypes Variables { get; }
    internal WideningDiagnostics WideningDiagnostics { get; }

    private async ValueTask WideningPropertyErrorAsync(SyntaxNode node, Symbol property, Type type, CancellationToken cancellation) =>
        Error(
            node,
            DiagnosticCode.ObjectLiteralSProperty0ImplicitlyHasAn1Type,
            await SymbolDisplayNameAsync(property, null, SymbolFlags.All, cancellation),
            await TypeDisplay.GetAsync(type, cancellation));

    internal PropertyInitialization PropertyInitializers { get; }
    internal Action<SyntaxNode>? BeforeInitializer { get; set; }

    public async ValueTask VariableDeclarationConflictAsync(SyntaxNode node, Symbol symbol, Type firstType, Type nextType,
        CancellationToken cancellation)
    {
        var name = SemanticSyntax.Name(node)!;
        Utf8String text = CheckerDiagnostic.DeclarationName(name);
        var diagnostic = CheckerDiagnostic.Create(name, DiagnosticLocalization.GetMessage(
            node is PropertyDeclarationNode or PropertySignatureDeclarationNode
                ? DiagnosticCode.SubsequentPropertyDeclarationsMustHaveTheSameTypeProperty0MustBeOfType1ButHereHasType2
                : DiagnosticCode.SubsequentVariableDeclarationsMustHaveTheSameTypeVariable0MustBeOfType1ButHereHasType2),
            text, await TypeDisplay.GetAsync(firstType, cancellation), await TypeDisplay.GetAsync(nextType, cancellation));
        if (symbol.ValueDeclaration is { } first)
            diagnostic = diagnostic with
            {
                RelatedInformation = [CheckerDiagnostic.Create(
                first,
                Messages.X_0_was_also_declared_here,
                text)]
            };
        Error(name, diagnostic);
    }

    public void CheckDeclarationFlags(SyntaxNode node, Symbol symbol, CancellationToken cancellation)
    {
        bool Same(SyntaxNode left, SyntaxNode right)
        {
            if (left is ParameterDeclarationNode && right is VariableDeclarationNode
                || left is VariableDeclarationNode && right is ParameterDeclarationNode)
                return true;
            const ModifierFlags mask = ModifierFlags.Private | ModifierFlags.Protected | ModifierFlags.Async
                | ModifierFlags.Abstract | ModifierFlags.Readonly | ModifierFlags.Static;
            return VariableTypes.Optional(left) == VariableTypes.Optional(right)
                && (SyntacticFlags(left) & mask) == (SyntacticFlags(right) & mask);
        }
        bool mismatch = false;
        if (node == symbol.ValueDeclaration)
            foreach (var declaration in symbol.Declarations)
            {
                cancellation.ThrowIfCancellationRequested();
                if (declaration != node && declaration is VariableDeclarationNode or ParameterDeclarationNode or PropertyDeclarationNode
                    or PropertySignatureDeclarationNode or BindingElementNode or PropertyAssignmentNode or ShorthandPropertyAssignmentNode
                    && !Same(declaration, node))
                {
                    mismatch = true;
                    break;
                }
            }
        else if (symbol.ValueDeclaration is { } first)
            mismatch = !Same(node, first);
        if (mismatch && SemanticSyntax.Name(node) is { } name)
            Error(name, DiagnosticCode.AllDeclarationsOf0MustHaveIdenticalModifiers, CheckerDiagnostic.DeclarationName(name));
    }

    public void CheckVariableShadowing(SyntaxNode node, CancellationToken cancellation)
    {
        var root = SemanticSyntax.RootDeclaration(node);
        if (root is ParameterDeclarationNode || ((root.Flags | (root.Parent?.Flags ?? 0)) & NodeFlags.BlockScoped) != 0
            || SemanticSyntax.Name(node) is not IdentifierNode name
            || program.Symbols.Declaration(node) is not { } symbol || (symbol.Flags & SymbolFlags.FunctionScopedVariable) == 0)
            return;
        var local = program.Symbols.NameResolver(cancellation).Resolve(node, name.Text, SymbolFlags.Variable);
        if (local is null || local == symbol || (local.Flags & SymbolFlags.BlockScopedVariable) == 0)
            return;
        var declaration = local.ValueDeclaration;
        while (declaration is not null && declaration is not VariableDeclarationListNode)
            declaration = declaration.Parent;
        if (declaration is null || (declaration.Flags & NodeFlags.BlockScoped) == 0)
            return;
        var container = declaration.Parent is VariableStatementNode statement ? statement.Parent : null;
        bool sharedScope = container is SourceFileNode or ModuleBlockNode or ModuleDeclarationNode
            || container is BlockNode { Parent: IFunctionSignature };
        if (!sharedScope)
        {
            Utf8String text = TypeDisplay.SymbolName(local);
            Error(node, DiagnosticCode.CannotInitializeOuterScopedVariable0InTheSameScopeAsBlockScopedDeclaration1, text, text);
        }
    }

    public Type AutoArray => program.Globals.AutoArrayType!;
    public bool UseUnknownInCatchVariables => program.Symbols.Program.Configuration.Options.EffectiveUseUnknownInCatchVariables;

    public ValueTask<Type> DeclarationInitializerAsync(SyntaxNode declaration, CheckMode mode, CancellationToken cancellation) =>
        DeclarationInitializerWithContextAsync(declaration, mode, null, cancellation);

    private async ValueTask<Type> DeclarationInitializerWithContextAsync(SyntaxNode declaration, CheckMode mode, Type? contextual,
        CancellationToken cancellation)
    {
        BeforeInitializer?.Invoke(declaration);
        cancellation.ThrowIfCancellationRequested();
        var node = ((IInitializedNode)declaration).Initializer!;
        Type type;
        if (await QuickExpressionTypeAsync(node, cancellation) is { } quick)
            type = quick;
        else if (contextual is not null)
            type = await Contexts.CheckWithAsync(node, contextual, mode: mode, cancellation: cancellation);
        else if (mode != 0)
            type = await Expressions.CheckAsync(node, mode, cancellation);
        else
        {
            var data = links.TypeNodes.Get(node);
            if (data.ResolvedType is { } cached)
                type = cached;
            else
            {
                type = await FlowTypes.StableAsync((Expressions, node, cancellation),
                    static state => state.Expressions.CheckAsync(state.node, cancellation: state.cancellation), cancellation);
                cancellation.ThrowIfCancellationRequested();
                Functions.RecordExpressionCache(node, type);
                data.ResolvedType = type;
            }
        }
        return SemanticSyntax.RootDeclaration(declaration) is ParameterDeclarationNode
            && declaration is INamedNode { Name: BindingPatternNode pattern }
                ? await BindingPatterns.PadAsync(type, pattern, cancellation) : type;
    }

    public async ValueTask<Type?> FullParameterAsync(ParameterDeclarationNode parameter, CancellationToken cancellation)
    {
        var signature = await FullSignatureAsync(parameter.Parent!, cancellation);
        if (signature is null)
            return null;
        int position = ((IFunctionSignature)parameter.Parent!).Parameters!.IndexOf(parameter);
        return parameter.DotDotDotToken is null ? await Parameters.AtAsync(signature, position, cancellation)
            : await Parameters.RestAtAsync(signature, position, cancellation: cancellation);
    }

    public ValueTask<Type?> ContextualParameterAsync(ParameterDeclarationNode parameter, CancellationToken cancellation)
        => parameter.Name is IdentifierNode { Text: var name } && name == "this"u8
            ? ContextualThisAsync(parameter.Parent!, cancellation) : FunctionContexts.ParameterAsync(parameter, cancellation);

    public ValueTask<Type?> PropertyInitializationAsync(PropertyDeclarationNode property, CancellationToken cancellation) =>
        PropertyInitializers.InferAsync(property, cancellation);

    public ValueTask<Type?> BindingElementAsync(BindingElementNode element, CancellationToken cancellation)
        => Bindings.GetAsync(element, cancellation);

    public ValueTask<Type> BindingPatternAsync(SyntaxNode pattern, CancellationToken cancellation)
            => BindingPatterns.GetAsync((BindingPatternNode)pattern, false, true, cancellation);

    public async ValueTask<Type> IterationVariableAsync(
        VariableDeclarationNode declaration,
        SyntaxNode statement,
        CheckMode mode,
        CancellationToken cancellation)
    {
        if (statement.Kind == SyntaxKind.ForOfStatement)
            return await ForOfElementAsync((ForInOrOfStatementNode)statement, cancellation);
        var expression = await Expressions.CheckAsync(((ForInOrOfStatementNode)statement).Expression!, mode, cancellation);
        var index = await Keys.GetAsync(await Facts.GetAsync(expression, TypeFacts.IsUndefinedOrNull, cancellation) != 0
            ? await Facts.NonNullableAsync(expression, cancellation) : expression, cancellation: cancellation);
        if ((index.Flags & (TypeFlags.TypeParameter | TypeFlags.Index)) != 0
            && await program.Globals.AliasAsync(Utf8Literals.Extract, 2, Declared, cancellation) is { } extract)
            return await References.AliasInstantiationAsync(extract, [index, context.StringType], cancellation: cancellation);
        return context.StringType;
    }

    public async ValueTask<bool> NullOrUndefinedAsync(SyntaxNode node, CancellationToken cancellation)
    {
        while (node is ParenthesizedExpressionNode parentheses)
            node = parentheses.Expression!;
        return node.Kind == SyntaxKind.NullKeyword
            || node is IdentifierNode identifier && await UndefinedIdentifierAsync(identifier, cancellation);
    }

    public async ValueTask<Type> SymbolConstructorPropertyAsync(SyntaxNode declaration, Type type, CancellationToken cancellation)
    {
        if (declaration.Parent is InterfaceDeclarationNode { Name.Text.Span: var matchedText } parent && matchedText.SequenceEqual("SymbolConstructor"u8)
            && program.Symbols.Declaration(parent) == (await program.Globals.GetAsync(Utf8Literals.SymbolConstructor, 0, false, cancellation)).Symbol)
            return Nodes.UniqueSymbol(declaration);
        return type;
    }

    public ValueTask ReportWideningAsync(SyntaxNode declaration, Type type, CancellationToken cancellation) =>
        WideningDiagnostics.ReportAsync(declaration, type, cancellation);

    public ValueTask ReportImplicitAnyAsync(SyntaxNode declaration, Type type, CancellationToken cancellation) =>
        ReportImplicitAnyAsync(declaration, type, WideningKind.Normal, cancellation);

    public async ValueTask ReportImplicitAnyAsync(SyntaxNode declaration, Type type, WideningKind kind, CancellationToken cancellation)
    {
        if ((declaration.Flags & NodeFlags.JavaScriptFile) != 0 && SemanticSyntax.Source(declaration)?.CheckJsDirective?.Enabled != true
            && program.Symbols.Program.Configuration.Options.CheckJs != true)
            return;
        Utf8String typeText = await TypeDisplay.GetAsync(await Widening.GetAsync(type, cancellation), cancellation);
        DiagnosticCode code;
        if (declaration is ParameterDeclarationNode parameter)
        {
            if (parameter.Parent is FunctionTypeNode or MethodSignatureDeclarationNode or CallSignatureDeclarationNode
                && parameter.Name is IdentifierNode name)
            {
                bool keyword = name.Text.Span.SequenceEqual("any"u8) || name.Text.Span.SequenceEqual("unknown"u8) || name.Text.Span.SequenceEqual("string"u8) || name.Text.Span.SequenceEqual("number"u8) || name.Text.Span.SequenceEqual("boolean"u8) || name.Text.Span.SequenceEqual("bigint"u8) || name.Text.Span.SequenceEqual("symbol"u8) || name.Text.Span.SequenceEqual("object"u8) || name.Text.Span.SequenceEqual("never"u8) || name.Text.Span.SequenceEqual("void"u8) || name.Text.Span.SequenceEqual("undefined"u8);
                if (keyword || await program.EntityNames.ResolveAsync(name, SymbolFlags.Type, true, cancellation: cancellation) is not null)
                {
                    code = DiagnosticCode.ParameterHasANameButNoTypeDidYouMean0Colon1;
                    int index = Signatures.Parameters(parameter.Parent!)?.IndexOf(parameter) ?? -1;
                    Report(Utf8String.Concat("arg"u8, Utf8String.Format(index)),
                        Utf8String.Concat(CheckerDiagnostic.DeclarationName(name), parameter.DotDotDotToken is null ? ""u8 : "[]"u8));
                    return;
                }
            }
            code = parameter.DotDotDotToken is not null
                ? NoImplicitAny
                    ? DiagnosticCode.RestParameter0ImplicitlyHasAnAnyType
                    : DiagnosticCode.RestParameter0ImplicitlyHasAnAnyTypeButABetterTypeMayBeInferredFromUsage
                : NoImplicitAny
                    ? DiagnosticCode.Parameter0ImplicitlyHasAn1Type
                    : DiagnosticCode.Parameter0ImplicitlyHasAn1TypeButABetterTypeMayBeInferredFromUsage;
        }
        else if (declaration is PropertyDeclarationNode or PropertySignatureDeclarationNode or BinaryExpressionNode)
            code = NoImplicitAny
                ? DiagnosticCode.Member0ImplicitlyHasAn1Type
                : DiagnosticCode.Member0ImplicitlyHasAn1TypeButABetterTypeMayBeInferredFromUsage;
        else if (declaration is BindingElementNode)
        {
            if (!NoImplicitAny)
                return;
            code = DiagnosticCode.BindingElement0ImplicitlyHasAn1Type;
        }
        else if (declaration is FunctionDeclarationNode or FunctionExpressionNode or ArrowFunctionNode or MethodDeclarationNode
            or MethodSignatureDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode)
        {
            var name = (declaration as INamedNode)?.Name;
            if (NoImplicitAny && name is null)
                code = kind == WideningKind.GeneratorYield
                    ? DiagnosticCode.GeneratorImplicitlyHasYieldType0ConsiderSupplyingAReturnTypeAnnotation
                    : DiagnosticCode.FunctionExpressionWhichLacksReturnTypeAnnotationImplicitlyHasAn0ReturnType;
            else if (!NoImplicitAny)
                code = DiagnosticCode.X0ImplicitlyHasAn1ReturnTypeButABetterTypeMayBeInferredFromUsage;
            else if ((declaration.Flags & NodeFlags.Reparsed) != 0)
                code = name is null
                    ? DiagnosticCode.ThisOverloadImplicitlyReturnsTheType0BecauseItLacksAReturnTypeAnnotation
                    : DiagnosticCode.X0WhichLacksReturnTypeAnnotationImplicitlyHasAn1ReturnType;
            else
                code = kind == WideningKind.GeneratorYield
                    ? DiagnosticCode.X0WhichLacksReturnTypeAnnotationImplicitlyHasAn1YieldType
                    : DiagnosticCode.X0WhichLacksReturnTypeAnnotationImplicitlyHasAn1ReturnType;
        }
        else
            code = NoImplicitAny
                ? DiagnosticCode.Variable0ImplicitlyHasAn1Type
                : DiagnosticCode.Variable0ImplicitlyHasAn1TypeButABetterTypeMayBeInferredFromUsage;
        var declarationName = SemanticSyntax.Name(declaration) ?? (declaration is BinaryExpressionNode binary ? binary.Left switch
        {
            PropertyAccessExpressionNode property => property.Name,
            ElementAccessExpressionNode element => element.ArgumentExpression,
            _ => binary.Left
        } : null);
        Utf8String nameText = declarationName is null ? Utf8String.Empty : CheckerDiagnostic.DeclarationName(declarationName);
        Report(
            code is DiagnosticCode.FunctionExpressionWhichLacksReturnTypeAnnotationImplicitlyHasAn0ReturnType
                or DiagnosticCode.ThisOverloadImplicitlyReturnsTheType0BecauseItLacksAReturnTypeAnnotation
                or DiagnosticCode.GeneratorImplicitlyHasYieldType0ConsiderSupplyingAReturnTypeAnnotation
                ? [typeText]
                : [nameText, typeText]);
        void Report(params Utf8String[] arguments)
        {
            if (NoImplicitAny)
                Error(declaration, code, arguments);
            else
                ExpressionSuggestion(declaration, CheckerDiagnostic.Create(declaration, DiagnosticLocalization.GetMessage(code), arguments));
        }
    }
}
