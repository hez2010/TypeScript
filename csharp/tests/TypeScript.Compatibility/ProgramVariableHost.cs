using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal sealed partial class ProgramTypeHost : IVariableTypeHost
{
    internal VariableTypes Variables { get; }
    internal WideningDiagnostics WideningDiagnostics { get; }
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
        => parameter.Parent is FunctionExpressionNode or ArrowFunctionNode or MethodDeclarationNode { Parent: ObjectLiteralExpressionNode }
            ? throw new InvalidOperationException("Probe requires contextual function parameters") : ValueTask.FromResult<Type?>(null);

    public ValueTask<Type?> PropertyInitializationAsync(PropertyDeclarationNode property, CancellationToken cancellation)
    {
        var members = ((ClassDeclarationNode)property.Parent!).Members!;
        if (!SemanticSyntax.IsStatic(property) && members.Any(n => n is ConstructorDeclarationNode)
            || SemanticSyntax.IsStatic(property) && members.Any(n => n is ClassStaticBlockDeclarationNode))
            throw new InvalidOperationException("Probe requires property initialization flow");
        if ((property.Flags & NodeFlags.Ambient) != 0 && ((ClassDeclarationNode)property.Parent!).HeritageClauses is { Count: > 0 })
            throw new InvalidOperationException("Probe requires inherited property inference");
        return ValueTask.FromResult<Type?>(null);
    }

    public ValueTask<Type?> BindingElementAsync(BindingElementNode element, CancellationToken cancellation)
        => Bindings.GetAsync(element, cancellation);

    public ValueTask<Type> BindingPatternAsync(SyntaxNode pattern, CancellationToken cancellation)
            => BindingPatterns.GetAsync((BindingPatternNode)pattern, false, true, cancellation);

    public ValueTask<Type> IterationVariableAsync(
        VariableDeclarationNode declaration,
        SyntaxNode statement,
        CheckMode mode,
        CancellationToken cancellation)
            => throw new InvalidOperationException("Probe requires iteration variable inference");

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

    public async ValueTask ReportImplicitAnyAsync(SyntaxNode declaration, Type type, CancellationToken cancellation)
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
