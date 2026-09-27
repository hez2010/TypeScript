using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : IBinaryExpressionHost, IAwaitedTypeHost
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
            {
                Diagnostics.Add(2318);
                TrackDiagnostic(null, 2318);
            }
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

    public async ValueTask OperatorErrorAsync(SyntaxNode? node, SyntaxKind op, Type left, Type right, bool suggestAwait,
        CancellationToken cancellation)
    {
        if (node is null)
            return;
        bool comparison = op is SyntaxKind.EqualsEqualsToken or SyntaxKind.EqualsEqualsEqualsToken
            or SyntaxKind.ExclamationEqualsToken or SyntaxKind.ExclamationEqualsEqualsToken;
        string leftText = await TypeDisplay.GetAsync(left, cancellation), rightText = await TypeDisplay.GetAsync(right, cancellation);
        var diagnostic = CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(comparison ? 2367 : 2365),
            comparison ? [leftText, rightText] : [TokenFacts.Text(op), leftText, rightText]);
        if (suggestAwait)
            diagnostic = diagnostic with { RelatedInformation = [CheckerDiagnostic.Create(node, Messages.Did_you_forget_to_use_await)] };
        Error(node, diagnostic);
    }

    public void ArithmeticError(SyntaxNode node, Type type, int code, bool suggestAwait)
    {
        var diagnostic = CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code));
        if (suggestAwait)
            diagnostic = diagnostic with { RelatedInformation = [CheckerDiagnostic.Create(node, Messages.Did_you_forget_to_use_await)] };
        Error(node, diagnostic);
    }

    public void BinaryDiagnostic(SyntaxNode node, int code, bool suggestion = false)
    {
        if (!suggestion)
        {
            string[] arguments = code switch
            {
                2447 => [TokenFacts.Text(node.Kind)!, node.Kind == SyntaxKind.BarToken ? "||"
                    : node.Kind == SyntaxKind.AmpersandToken ? "&&" : "!=="],
                2839 when node is BinaryExpressionNode comparison =>
                    [comparison.OperatorToken!.Kind is SyntaxKind.EqualsEqualsToken or SyntaxKind.EqualsEqualsEqualsToken
                        ? "false"
                        : "true"],
                2469 when node.Parent is BinaryExpressionNode binary => [TokenFacts.Text(binary.OperatorToken!.Kind)!],
                _ => []
            };
            Error(node, code, arguments);
        }
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
        => AssignmentChecks.OperatorAsync(left, op, right, leftType, valueType, cancellation);

    public ValueTask<Type> DestructuringAsync(
        SyntaxNode left,
        Type source,
        CheckMode mode,
        bool rightIsThis,
        CancellationToken cancellation)
            => CheckDestructuringAsync(left, source, mode, rightIsThis, cancellation);

    public async ValueTask<Type> RelationalKeywordAsync(
        BinaryExpressionNode node,
        Type left,
        Type right,
        CheckMode mode,
        CancellationToken cancellation)
    {
        if (left == context.SilentNeverType || right == context.SilentNeverType)
            return context.SilentNeverType;
        if (node.OperatorToken!.Kind == SyntaxKind.InstanceOfKeyword)
        {
            if ((left.Flags & TypeFlags.Any) == 0 && await AllAssignableKindAsync(left, TypeFlags.Primitive, cancellation))
                Error(node.Left!, 2358);
            var signature = await CallResolution.GetAsync(node, mode: mode, cancellation: cancellation);
            if (signature == CallSignatures.Resolving)
                return context.SilentNeverType;
            await RelationDiagnostics.CheckAsync(await Signatures.ReturnAsync(signature, cancellation), context.BooleanType,
                RelationKind.Assignable, node.Right!, node.Right!, 2861, cancellation);
        }
        else
        {
            if (node.Left is PrivateIdentifierNode)
            {
                if (TargetYear < int.MaxValue || !UseDefineForClassFields)
                    await PrivateEmitHelpersAsync(node.Left, false, false, cancellation);
                if (links.SymbolNodes.TryGet(node.Left)?.ResolvedSymbol is null && PrivateAccess.ContainingClass(node.Left) is not null)
                    MissingProperty(node.Left, right, await UncheckedJsAsync(node.Left, right.Symbol, cancellation));
            }
            else
                await RelationDiagnostics.CheckAsync(await NonNullAsync(left, node.Left!, cancellation), context.StringNumberSymbolType,
                    RelationKind.Assignable, node.Left!, node.Left!, 2322, cancellation);
            if (await RelationDiagnostics.CheckAsync(await NonNullAsync(right, node.Right!, cancellation), context.NonPrimitiveType,
                RelationKind.Assignable, node.Right!, node.Right!, 2322, cancellation))
                foreach (var part in right is UnionType union ? union.Types : [right])
                    if (part == context.UnknownEmptyObjectType || part is IntersectionType
                        && await Views.EmptyAnonymousAsync(
                            await Instantiation.Constraints.BaseConstraintOrTypeAsync(part, cancellation),
                            cancellation))
                    {
                        Error(node.Right!, 2638);
                        break;
                    }
        }
        return context.BooleanType;
    }

    private async ValueTask<bool> AllAssignableKindAsync(Type type, TypeFlags flags, CancellationToken cancellation)
    {
        foreach (var part in type is UnionType union ? union.Types : [type])
            if (!await AssignableKindAsync(part, flags, cancellation))
                return false;
        return true;
    }
}
