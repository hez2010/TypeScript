using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : IVariableTypeHost
{
    internal VariableTypes Variables { get; }
    internal WideningDiagnostics WideningDiagnostics { get; }
    internal PropertyInitialization PropertyInitializers { get; }
    internal Action<SyntaxNode>? BeforeInitializer { get; set; }
    public Type AutoArray => program.Globals.AutoArrayType!;
    public bool UseUnknownInCatchVariables => program.Symbols.Program.Configuration.Options.StrictOption("useUnknownInCatchVariables");

    public ValueTask<Type> DeclarationInitializerAsync(SyntaxNode declaration, CheckMode mode, CancellationToken cancellation) =>
        DeclarationInitializerWithContextAsync(declaration, mode, null, cancellation);

    private async ValueTask<Type> DeclarationInitializerWithContextAsync(SyntaxNode declaration, CheckMode mode, Type? contextual,
        CancellationToken cancellation)
    {
        BeforeInitializer?.Invoke(declaration);
        cancellation.ThrowIfCancellationRequested();
        var node = ((IInitializedNode)declaration).Initializer!;
        Type type;
        if (node is StringLiteralNode or NumericLiteralNode or BigIntLiteralNode or NoSubstitutionTemplateLiteralNode
            || node.Kind is SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword)
            type = await Expressions.CheckAsync(node, cancellation: cancellation);
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
                type = await FlowTypes.StableAsync(() => Expressions.CheckAsync(node, cancellation: cancellation), cancellation);
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
        int position = ((IFunctionSignature)parameter.Parent!).Parameters!.ToList().IndexOf(parameter);
        return parameter.DotDotDotToken is null ? await Parameters.AtAsync(signature, position, cancellation)
            : await Parameters.RestAtAsync(signature, position, cancellation: cancellation);
    }

    public ValueTask<Type?> ContextualParameterAsync(ParameterDeclarationNode parameter, CancellationToken cancellation)
        => FunctionContexts.ParameterAsync(parameter, cancellation);

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
            && await program.Globals.AliasAsync("Extract", 2, Declared, cancellation) is { } extract)
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
        if (declaration.Parent is InterfaceDeclarationNode { Name.Text: "SymbolConstructor" } parent
            && program.Symbols.Declaration(parent) == (await program.Globals.GetAsync("SymbolConstructor", 0, false, cancellation)).Symbol)
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
            && program.Symbols.Program.Configuration.Options.Boolean("checkJs") != true)
            return;
        await Widening.GetAsync(type, cancellation);
        int code;
        if (declaration is ParameterDeclarationNode parameter)
        {
            if (parameter.Parent is FunctionTypeNode or MethodSignatureDeclarationNode or CallSignatureDeclarationNode
                && parameter.Name is IdentifierNode name)
            {
                bool keyword = name.Text is "any" or "unknown" or "string" or "number" or "boolean" or "bigint" or "symbol" or "object"
                    or "never" or "void" or "undefined";
                if (keyword || await program.EntityNames.ResolveAsync(name, SymbolFlags.Type, true, cancellation: cancellation) is not null)
                {
                    code = 7051;
                    Report();
                    return;
                }
            }
            code = parameter.DotDotDotToken is not null ? NoImplicitAny ? 7019 : 7047 : NoImplicitAny ? 7006 : 7044;
        }
        else if (declaration is PropertyDeclarationNode or PropertySignatureDeclarationNode or BinaryExpressionNode)
            code = NoImplicitAny ? 7008 : 7045;
        else if (declaration is BindingElementNode)
        {
            if (!NoImplicitAny)
                return;
            code = 7031;
        }
        else if (declaration is FunctionDeclarationNode or FunctionExpressionNode or ArrowFunctionNode or MethodDeclarationNode
            or MethodSignatureDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode)
        {
            var name = (declaration as INamedNode)?.Name;
            if (NoImplicitAny && name is null)
                code = kind == WideningKind.GeneratorYield ? 7025 : 7011;
            else if (!NoImplicitAny)
                code = 7050;
            else if ((declaration.Flags & NodeFlags.Reparsed) != 0)
                code = name is null ? 7012 : 7010;
            else
                code = kind == WideningKind.GeneratorYield ? 7055 : 7010;
        }
        else
            code = NoImplicitAny ? 7005 : 7043;
        Report();
        void Report()
        {
            if (NoImplicitAny)
                Error(declaration, code);
            else if (suggestionLocations.Add((declaration, code)))
                Suggestions.Add(code);
        }
    }
}
