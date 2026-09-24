using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : IFunctionContextHost, IFunctionBodyHost, IFunctionExpressionHost, IFunctionDeclarationHost, IFunctionWideningHost, IFunctionThisHost, IAwaitExpressionHost
{
    internal FunctionContexts FunctionContexts { get; }
    internal FunctionBodies FunctionBodies { get; }
    internal FunctionExpressions Functions { get; }
    internal FunctionDeclarations FunctionDeclarations { get; }
    internal FunctionWidening FunctionWidening { get; }
    internal FunctionThis FunctionThis { get; }
    internal AwaitExpressions AwaitExpressions { get; }
    internal TypeReferenceChecks TypeReferenceChecks { get; }
    internal IndexDeclarationChecks IndexDeclarationChecks { get; }
    public Type GlobalThisMarker => program.Globals.Types["ThisType"];

    public bool ExportsReceiver(SyntaxNode node) => node is IdentifierNode identifier
        && program.Symbols.Binding(node)?.CommonJSModuleIndicator is not null
            && (program.ReferenceSymbols.Resolve(identifier).Flags & SymbolFlags.ModuleExports) != 0;

    public void ExpressionSuggestion(SyntaxNode node, int code)
    {
        if (suggestionLocations.Add((node, code)))
            Suggestions.Add(code);
    }

    public async ValueTask<Type> CheckedFunctionTypeAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var pending = new Stack<(SyntaxNode Node, bool Visited)>();
        pending.Push((node, false));
        while (pending.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            if (item.Visited)
            {
                if (item.Node is TypeReferenceNode reference)
                    await TypeReferenceChecks.CheckAsync(reference, cancellation);
                else if (item.Node is TypeLiteralNode literal)
                    await IndexDeclarationChecks.TypeLiteralAsync(literal, cancellation);
                if (item.Node is InferTypeNode)
                    RegisterUnused(item.Node);
                else if (item.Node is FunctionTypeNode or ConstructorTypeNode or MethodSignatureDeclarationNode
                    or CallSignatureDeclarationNode or ConstructSignatureDeclarationNode)
                {
                    await FunctionDeclarations.GrammarAsync(item.Node, cancellation).ConfigureAwait(false);
                    await FunctionDeclarations.CheckAsync(item.Node, cancellation).ConfigureAwait(false);
                }
            }
            else
            {
                pending.Push((item.Node, true));
                for (int i = item.Node.ChildCount - 1; i >= 0; i--)
                    pending.Push((item.Node.GetChild(i), false));
            }
        }
        return await Nodes.FromNodeAsync(node, cancellation);
    }

    public void TopLevelAwait(SyntaxNode node)
        => TopLevelAwait(node, 1375, 1378);

    private void TopLevelAwait(SyntaxNode node, int moduleRequired, int invalidMode)
    {
        var file = SemanticSyntax.Source(node)!;
        var options = program.Symbols.Program.Configuration.Options;
        if (program.Symbols.Binding(file)?.IsModule != true
            && options.String("moduleDetection") != "force"
            && options.Number("moduleDetection") != 3)
            Error(node, moduleRequired);
        var module = options.String("module") ?? options.Number("module") switch
        {
            4 => "system",
            7 => "es2022",
            99 => "esnext",
            100 => "node16",
            101 => "node18",
            102 => "node20",
            199 => "nodenext",
            200 => "preserve",
            null or 0 => TargetYear >= 2022 ? "es2022" : "commonjs",
            _ => "other"
        };
        if (module == "none")
            module = TargetYear >= 2022 ? "es2022" : "commonjs";
        bool nodeModule = module is "node16" or "node18" or "node20" or "nodenext";
        if (nodeModule && program.Symbols.Program.SourceFiles.First(f => f.Syntax == file).ImpliedFormat == ReferenceResolutionMode.Require)
            Error(node, 1309);
        else if (TargetYear < 2017 || !nodeModule && module is not ("es2022" or "esnext" or "preserve" or "system"))
            Error(node, invalidMode);
    }

    internal HashSet<SyntaxNode> UnusedIdentifierScopes { get; } = [];
    internal List<BindingElementNode> RenamedBindingElements { get; } = [];
    internal Action<SyntaxNode>? BeforeFunctionDeclaration { get; set; }
    public Type AnyReadonlyArray => program.Globals.AnyReadonlyArrayType!;

    public async ValueTask<Type> CachedExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (mode != 0)
            return await Expressions.CheckAsync(node, mode, cancellation);
        var data = links.TypeNodes.Get(node);
        if (data.ResolvedType is { } type)
            return type;
        type = await FlowTypes.StableAsync(() => Expressions.CheckAsync(node, cancellation: cancellation), cancellation);
        cancellation.ThrowIfCancellationRequested();
        Functions.RecordExpressionCache(node, type);
        return data.ResolvedType = type;
    }

    public async ValueTask<Type> PromiseResultAsync(SyntaxNode node, Type type, bool reportMissing, CancellationToken cancellation)
    {
        var target = await program.Globals.GetAsync("Promise", 1, true, cancellation);
        Type result = context.UnknownType;
        if (target != context.EmptyGenericType)
        {
            type = await Awaited.GetAsync(
                await Awaited.UnwrapAsync(type, cancellation),
                false,
                cancellation: cancellation) ?? context.UnknownType;
            result = context.CreateTypeReference((InterfaceType)target, [type]);
        }
        if (reportMissing)
        {
            if (result == context.UnknownType)
            {
                Error(node, 2697);
                return context.ErrorType;
            }
            if (program.Symbols.Lookup(program.Symbols.Globals, "Promise", SymbolFlags.Value) is null)
                Error(node, 2705);
        }
        return result;
    }

    public async ValueTask<Type?> UnwrapReturnAsync(SyntaxNode node, Type type, CancellationToken cancellation)
    {
        if (SemanticSyntax.Generator(node))
        {
            var result = (await Iterators.GeneratorAsync(
                type,
                SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword),
                cancellation)).Return;
            if (result is null)
                return context.ErrorType;
            return SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword)
                ? await Awaited.GetAsync(await Awaited.UnwrapAsync(result, cancellation), false, cancellation: cancellation) : result;
        }
        return SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword)
            ? await Awaited.GetAsync(type, false, cancellation: cancellation) ?? context.ErrorType : type;
    }

    public async ValueTask<Type?> InvokedParameterAsync(
        ParameterDeclarationNode node,
        CallExpressionNode call,
        CancellationToken cancellation)
    {
        var arguments = await CallArguments.EffectiveAsync(call, cancellation);
        int index = ((IFunctionSignature)node.Parent!).Parameters!.ToList().IndexOf(node);
        if (node.DotDotDotToken is not null)
            return await CallArguments.SpreadAsync(arguments, index, context.AnyType, cancellation: cancellation);
        var data = links.Signatures.Get(call);
        var cached = data.ResolvedSignature;
        data.ResolvedSignature = CallSignatures.Any;
        try
        {
            return index < arguments.Count ? await Widening.LiteralAsync(
                await Expressions.CheckAsync(arguments[index], cancellation: cancellation),
                cancellation)
                : node.Initializer is not null ? null : context.UndefinedWideningType;
        }
        finally
        {
            if (data.ResolvedSignature == CallSignatures.Any)
                data.ResolvedSignature = cached;
        }
    }

    public async ValueTask<Type?> GeneratorContextReturnAsync(SyntaxNode node, Type type, CancellationToken cancellation) =>
        await Generators.ContextReturnAsync(type, SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword), cancellation);

    public ValueTask<(IReadOnlyList<Type> Yield, IReadOnlyList<Type> Next)> YieldTypesAsync(
        SyntaxNode node,
        CheckMode mode,
        CancellationToken cancellation) =>
        Generators.AggregateAsync(node, mode, cancellation);

    public async ValueTask<Type> GeneratorResultAsync(SyntaxNode node, Type yield, Type result, Type? next, CancellationToken cancellation) =>
        await Generators.CreateAsync(yield, result, next ?? await Generators.ContextualAsync(node, IterationTypeKind.Next, cancellation),
            SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword), cancellation);

    public ValueTask<Type> WidenIterationAsync(SyntaxNode node, Type type, Type? contextual, int kind, CancellationToken cancellation) =>
        Generators.WidenAsync(
            type,
            contextual,
            (IterationTypeKind)kind,
            SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword),
            cancellation);

    public ValueTask ReportReturnWideningAsync(SyntaxNode node, Type type, WideningKind kind, CancellationToken cancellation) =>
        FunctionWidening.ReportAsync(node, type, kind, cancellation);

    public async ValueTask<Type?> ContextualIterationAsync(SyntaxNode node, Type type, WideningKind kind, CancellationToken cancellation) =>
        (await Iterators.GeneratorAsync(type, SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword), cancellation)).Get(
            kind == WideningKind.GeneratorYield
                ? IterationTypeKind.Yield
                : kind == WideningKind.GeneratorNext ? IterationTypeKind.Next : IterationTypeKind.Return);

    public ValueTask CheckFunctionDeclarationAsync(SyntaxNode node, CancellationToken cancellation)
    {
        BeforeFunctionDeclaration?.Invoke(node);
        cancellation.ThrowIfCancellationRequested();
        return FunctionDeclarations.CheckAsync(node, cancellation);
    }

    public ValueTask FunctionGrammarAsync(SyntaxNode node, CancellationToken cancellation) =>
        FunctionDeclarations.GrammarAsync(node, cancellation);

    public void RegisterUnused(SyntaxNode node) => UnusedIdentifierScopes.Add(node);

    public ValueTask FunctionNameAsync(SyntaxNode node, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if ((node as INamedNode)?.Name is IdentifierNode
            {
                Text: "require" or "exports" or "Promise" or "WeakMap" or "WeakSet"
            or "Reflect" or "globalThis"
            })
            throw new InvalidOperationException("Checker requires generated-name collision checks");
        return ValueTask.CompletedTask;
    }

    public async ValueTask SignatureEnvironmentAsync(SyntaxNode node, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        await CheckUnmatchedDocumentationParametersAsync(node, cancellation).ConfigureAwait(false);
        if (SemanticSyntax.Generator(node))
            AsyncYieldHelpers(node);
        if (SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword)
            && TargetYear < 2017 && program.Symbols.Program.Configuration.Options.Boolean("importHelpers") == true)
            throw new InvalidOperationException("Checker requires async emit helpers");
    }

    public async ValueTask CheckFunctionReturnAsync(SyntaxNode node, SyntaxNode annotation, Type type, CancellationToken cancellation)
    {
        if (SemanticSyntax.Generator(node))
        {
            if (type == context.VoidType)
                Error(annotation, 2505);
            else if (!await Generators.AssignableReturnAsync(type, SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword), cancellation))
                Error(annotation, 2322);
            return;
        }
        if (!SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword) || type == context.ErrorType
            || (type.Flags & TypeFlags.Any) != 0 && type.Alias is not null)
            return;
        var promise = await program.Globals.GetAsync("Promise", 1, true, cancellation);
        if (promise != context.EmptyGenericType && !(type is TypeReference reference && reference.Target == promise))
        {
            await Awaited.GetAsync(type, false, cancellation: cancellation);
            Error(annotation, 1064);
            return;
        }
        await Awaited.GetAsync(type, false, node, 1058, cancellation);
    }

    public ValueTask ParameterEnvironmentAsync(ParameterDeclarationNode node, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (node.Modifiers is { Count: > 0 })
        {
            ClassMemberModifiers(node);
            if (ParameterProperty(node))
            {
                if (node.Parent is not ConstructorDeclarationNode { Body: not null })
                    Error(node, 2369);
                if (node.Name is BindingPatternNode)
                    Error(node, 1187);
                if (node.DotDotDotToken is not null)
                    Error(node, 1317);
            }
        }
        if (node.Name is IdentifierNode { Text: "eval" or "arguments" or "require" or "exports" or "globalThis" })
            throw new InvalidOperationException("Checker requires parameter declaration-name checks");
        return ValueTask.CompletedTask;
    }

    public async ValueTask<bool> BindingEnvironmentAsync(BindingElementNode node, CancellationToken cancellation)
    {
        if (node.DotDotDotToken is not null && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
        {
            var elements = ((BindingPatternNode)node.Parent!).Elements!;
            if (elements[^1] != node)
                Error(node, 2462);
            else
            {
                DestructuringTrailingComma(elements, node);
                if (node.PropertyName is not null)
                    Error(node.Name!, 2566);
                else if (node.Initializer is not null)
                    Error(node.Initializer, 1186);
            }
        }
        if (node.PropertyName is PrivateIdentifierNode && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
            Error(node.PropertyName, 18064);
        if (node.PropertyName is not null && node.Name is IdentifierNode
            && SemanticSyntax.RootDeclaration(node) is ParameterDeclarationNode
            && SemanticSyntax.Body(SemanticSyntax.RootDeclaration(node).Parent!) is null)
        {
            RenamedBindingElements.Add(node);
            return true;
        }
        if (node.DotDotDotToken is not null && node.Parent!.Kind == SyntaxKind.ObjectBindingPattern && TargetYear < 2018
            && program.Symbols.Program.Configuration.Options.Boolean("importHelpers") == true)
            throw new InvalidOperationException("Checker requires object-rest emit helpers");
        if (node.PropertyName is ComputedPropertyNameNode computed)
            await ObjectLiterals.ComputedAsync(computed, cancellation);
        return false;
    }

    public async ValueTask NonNullBindingAsync(Type type, SyntaxNode node, CancellationToken cancellation)
    {
        var nonNull = await ExpressionChecks.NonNullAsync(type, node, cancellation);
        if ((nonNull.Flags & TypeFlags.Void) != 0)
            Error(node, 2532);
    }

    public ValueTask FunctionModifiersAsync(SyntaxNode node, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (SemanticSyntax.ClassLike(node.Parent) || node is ConstructorDeclarationNode)
        {
            ClassMemberModifiers(node);
            return ValueTask.CompletedTask;
        }
        if (node is ConstructorTypeNode { Modifiers: { Count: 1 } constructorModifiers }
            && constructorModifiers[0].Kind == SyntaxKind.AbstractKeyword)
            return ValueTask.CompletedTask;
        if (node is IModifiedNode { Modifiers: { } modifiers }
            && modifiers.Any(
                m => m.Kind is not (SyntaxKind.AsyncKeyword or SyntaxKind.ExportKeyword or SyntaxKind.DefaultKeyword
                    or SyntaxKind.DeclareKeyword)))
            throw new InvalidOperationException("Checker requires function modifier grammar");
        return ValueTask.CompletedTask;
    }

    public ValueTask TypeParameterModifiersAsync(TypeParameterDeclarationNode node, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (node.Modifiers is { } modifiers && modifiers.Any(m => m.Kind != SyntaxKind.ConstKeyword))
            throw new InvalidOperationException("Checker requires type-parameter modifier grammar");
        return ValueTask.CompletedTask;
    }
}
