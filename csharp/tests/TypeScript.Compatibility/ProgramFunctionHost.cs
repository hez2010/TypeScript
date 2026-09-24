using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal sealed partial class ProgramTypeHost : IFunctionContextHost, IFunctionBodyHost, IFunctionExpressionHost, IFunctionDeclarationHost, IFunctionWideningHost, IFunctionThisHost, IAwaitExpressionHost
{
    internal FunctionContexts FunctionContexts { get; }
    internal FunctionBodies FunctionBodies { get; }
    internal FunctionExpressions Functions { get; }
    internal FunctionDeclarations FunctionDeclarations { get; }
    internal FunctionWidening FunctionWidening { get; }
    internal FunctionThis FunctionThis { get; }
    internal AwaitExpressions AwaitExpressions { get; }
    internal TypeReferenceChecks TypeReferenceChecks { get; }
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
    {
        var file = SemanticSyntax.Source(node)!;
        var options = program.Symbols.Program.Configuration.Options;
        if (program.Symbols.Binding(file)?.IsModule != true
            && options.String("moduleDetection") != "force"
            && options.Number("moduleDetection") != 3)
            Error(node, 1375);
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
            Error(node, 1378);
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

    public async ValueTask<bool> ConstantReferenceAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (node is not IdentifierNode identifier)
            return false;
        var symbol = program.ReferenceSymbols.Resolve(identifier, cancellation);
        return symbol.ValueDeclaration is FunctionExpressionNode || await ConstantOrUnassignedAsync(symbol, cancellation);
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
            throw new InvalidOperationException("Probe requires generator return iteration types");
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

    public ValueTask<Type?> GeneratorContextReturnAsync(SyntaxNode node, Type type, CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires contextual generator validation");

    public ValueTask<(IReadOnlyList<Type> Yield, IReadOnlyList<Type> Next)> YieldTypesAsync(
        SyntaxNode node,
        CheckMode mode,
        CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires generator yield/next inference");

    public ValueTask<Type> GeneratorResultAsync(SyntaxNode node, Type yield, Type result, Type? next, CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires generator result types");

    public ValueTask<Type> WidenIterationAsync(SyntaxNode node, Type type, Type? contextual, int kind, CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires contextual iteration widening");

    public ValueTask ReportReturnWideningAsync(SyntaxNode node, Type type, WideningKind kind, CancellationToken cancellation) =>
        FunctionWidening.ReportAsync(node, type, kind, cancellation);

    public ValueTask<Type?> ContextualIterationAsync(SyntaxNode node, Type type, WideningKind kind, CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires contextual generator iteration types");

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
            throw new InvalidOperationException("Probe requires generated-name collision checks");
        return ValueTask.CompletedTask;
    }

    public ValueTask SignatureEnvironmentAsync(SyntaxNode node, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if ((node.Flags & NodeFlags.JavaScriptFile) != 0)
            throw new InvalidOperationException("Probe requires JSDoc signature declarations");
        if (SemanticSyntax.Generator(node))
            throw new InvalidOperationException("Probe requires generator signature validation");
        if (SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword)
            && TargetYear < 2017 && program.Symbols.Program.Configuration.Options.Boolean("importHelpers") == true)
            throw new InvalidOperationException("Probe requires async emit helpers");
        return ValueTask.CompletedTask;
    }

    public async ValueTask CheckFunctionReturnAsync(SyntaxNode node, SyntaxNode annotation, Type type, CancellationToken cancellation)
    {
        if (SemanticSyntax.Generator(node))
            throw new InvalidOperationException("Probe requires generator annotation validation");
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
            throw new InvalidOperationException("Probe requires parameter modifiers/decorators");
        if (node.Name is IdentifierNode { Text: "eval" or "arguments" or "require" or "exports" or "globalThis" })
            throw new InvalidOperationException("Probe requires parameter declaration-name checks");
        return ValueTask.CompletedTask;
    }

    public async ValueTask BindingEnvironmentAsync(BindingElementNode node, CancellationToken cancellation)
    {
        if (node.PropertyName is PrivateIdentifierNode && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
            Error(node.PropertyName, 18017);
        if (node.PropertyName is not null && node.Name is IdentifierNode
            && SemanticSyntax.Body(SemanticSyntax.RootDeclaration(node).Parent!) is null)
            RenamedBindingElements.Add(node);
        if (node.DotDotDotToken is not null && node.Parent!.Kind == SyntaxKind.ObjectBindingPattern && TargetYear < 2018
            && program.Symbols.Program.Configuration.Options.Boolean("importHelpers") == true)
            throw new InvalidOperationException("Probe requires object-rest emit helpers");
        if (node.PropertyName is ComputedPropertyNameNode computed)
            await ObjectLiterals.ComputedAsync(computed, cancellation);
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
        if (node is IModifiedNode { Modifiers: { } modifiers } && modifiers.Any(m => m.Kind != SyntaxKind.AsyncKeyword))
            throw new InvalidOperationException("Probe requires function modifier grammar");
        return ValueTask.CompletedTask;
    }

    public ValueTask TypeParameterModifiersAsync(TypeParameterDeclarationNode node, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (node.Modifiers is { } modifiers && modifiers.Any(m => m.Kind != SyntaxKind.ConstKeyword))
            throw new InvalidOperationException("Probe requires type-parameter modifier grammar");
        return ValueTask.CompletedTask;
    }
}
