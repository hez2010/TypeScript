using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal sealed partial class ProgramTypeHost : IBinaryExpressionHost, IAwaitedTypeHost
{
    internal BinaryExpressions Binary { get; }
    internal AwaitedTypes Awaited { get; }
    private readonly Dictionary<bool, Symbol?> awaitedSymbols = [];
    public int TargetYear => program.Symbols.Program.Configuration.Options.EmitTargetYear;
    public bool AllowUnreachableCode => program.Symbols.Program.Configuration.Options.Boolean("allowUnreachableCode") == true;

    public ValueTask<Type> CheckExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation)
            => Expressions.CheckAsync(node, mode, cancellation);

    public ValueTask<Type> PromiseTypeAsync(CancellationToken cancellation) => program.Globals.GetAsync("Promise", 1, false, cancellation);

    public async ValueTask<Symbol?> AwaitedSymbolAsync(bool reportErrors, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (awaitedSymbols.TryGetValue(reportErrors, out var cached))
            return cached;
        var symbol = program.Symbols.Globals.GetValueOrDefault("Awaited");
        if (symbol is null || (symbol.Flags & SymbolFlags.TypeAlias) == 0)
        {
            if (reportErrors)
                Diagnostics.Add(2318);
            return awaitedSymbols[reportErrors] = null;
        }
        await Declared.GetAsync(symbol, cancellation);
        if (links.TypeAliases.Get(symbol).TypeParameters?.Count != 1)
        {
            if (reportErrors)
                Error(symbol.Declarations.OfType<TypeAliasDeclarationNode>().First(), 2317);
            return awaitedSymbols[reportErrors] = null;
        }
        return awaitedSymbols[reportErrors] = symbol;
    }

    public ValueTask<Type> AliasInstantiationAsync(Symbol symbol, Type argument, CancellationToken cancellation)
            => References.AliasInstantiationAsync(symbol, [argument], cancellation: cancellation);

    public void AwaitedError(SyntaxNode node, int code, Type type, Type? thisType = null) => Error(node, code);

    public ValueTask<Type?> AwaitedAsync(Type type, bool promiseOnly, CancellationToken cancellation)
            =>
                promiseOnly
                    ? Awaited.OfPromiseAsync(type, cancellation: cancellation)
                    : Awaited.NoAliasAsync(type, cancellation: cancellation);

    public void OperatorError(SyntaxNode? node, SyntaxKind op, Type left, Type right, bool suggestAwait)
    {
        if (node is not null)
            Error(node, op is SyntaxKind.EqualsEqualsToken or SyntaxKind.EqualsEqualsEqualsToken
            or SyntaxKind.ExclamationEqualsToken or SyntaxKind.ExclamationEqualsEqualsToken ? 2367 : 2365);
    }

    public void ArithmeticError(SyntaxNode node, Type type, int code, bool suggestAwait) => Error(node, code);

    public void BinaryDiagnostic(SyntaxNode node, int code, bool suggestion = false)
    {
        if (!suggestion)
            Error(node, code);
        else if (suggestionLocations.Add((node, code)))
            Suggestions.Add(code);
    }

    public async ValueTask<bool> GlobalNaNAsync(SyntaxNode node, CancellationToken cancellation)
            => node is IdentifierNode { Text: "NaN" } && program.Symbols.Globals.GetValueOrDefault("NaN") is { } symbol
                && await program.EntityNames.ResolveAsync(node, SymbolFlags.Value, true, cancellation: cancellation) == symbol;

    public ValueTask AssignmentAsync(
        SyntaxNode left,
        SyntaxKind op,
        SyntaxNode right,
        Type leftType,
        Type valueType,
        CancellationToken cancellation)
    {
        var target = left;
        while (true)
        {
            var inner = target switch
            {
                ParenthesizedExpressionNode p => p.Expression,
                AsExpressionNode a => a.Expression,
                TypeAssertionNode a => a.Expression,
                NonNullExpressionNode n => n.Expression,
                SatisfiesExpressionNode s => s.Expression,
                _ => null
            };
            if (inner is null)
                break;
            target = inner;
        }
        if (target is not (IdentifierNode or PropertyAccessExpressionNode or ElementAccessExpressionNode))
        {
            Error(left, 2364);
            return ValueTask.CompletedTask;
        }
        if ((target.Flags & NodeFlags.OptionalChain) != 0)
        {
            Error(left, 2779);
            return ValueTask.CompletedTask;
        }
        throw new InvalidOperationException("Probe requires assignment references and elaboration");
    }

    public ValueTask<Type> DestructuringAsync(
        SyntaxNode left,
        Type source,
        CheckMode mode,
        bool rightIsThis,
        CancellationToken cancellation)
            => throw new InvalidOperationException("Probe requires destructuring assignment checking");

    public ValueTask<Type> RelationalKeywordAsync(
        BinaryExpressionNode node,
        Type left,
        Type right,
        CheckMode mode,
        CancellationToken cancellation)
            => throw new InvalidOperationException("Probe requires in/instanceof checking");
}
