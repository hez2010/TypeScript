using TypeScript.Compiler.Text;
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
    public bool AllowUnreachableCode => program.Symbols.Program.Configuration.Options.AllowUnreachableCode == true;

    public ValueTask<Type> CheckExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation)
            => Expressions.CheckAsync(node, mode, cancellation);

    public ValueTask<Type> PromiseTypeAsync(CancellationToken cancellation) => program.Globals.GetAsync(Utf8Literals.Promise, 1, false, cancellation);

    public async ValueTask<Symbol?> AwaitedSymbolAsync(bool reportErrors, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (awaitedSymbols.TryGetValue(reportErrors, out var cached))
            return cached;
        var symbol = program.Symbols.Globals.GetValueOrDefault(Utf8Literals.Awaited);
        if (symbol is null || (symbol.Flags & SymbolFlags.TypeAlias) == 0)
        {
            if (reportErrors)
            {
                Diagnostics.Add(DiagnosticCode.CannotFindGlobalType0);
                TrackDiagnostic(null, DiagnosticCode.CannotFindGlobalType0, Utf8Literals.Awaited);
            }
            return awaitedSymbols[reportErrors] = null;
        }
        await Declared.GetAsync(symbol, cancellation);
        if (links.TypeAliases.Get(symbol).TypeParameters?.Count != 1)
        {
            if (reportErrors)
                Error(symbol.Declarations.OfType<TypeAliasDeclarationNode>().First(), DiagnosticCode.GlobalType0MustHave1TypeParameterS);
            return awaitedSymbols[reportErrors] = null;
        }
        return awaitedSymbols[reportErrors] = symbol;
    }

    public ValueTask<Type> AliasInstantiationAsync(Symbol symbol, Type argument, CancellationToken cancellation)
            => References.AliasInstantiationAsync(symbol, [argument], cancellation: cancellation);

    public async ValueTask AwaitedErrorAsync(
        SyntaxNode node,
        DiagnosticCode code,
        Type type,
        Type? thisType,
        CancellationToken cancellation)
    {
        var diagnostic = CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code));
        if (thisType is not null)
        {
            var detail = CheckerDiagnostic.Create(
                node,
                DiagnosticLocalization.GetMessage(DiagnosticCode.TheThisContextOfType0IsNotAssignableToMethodSThisOfType1),
                await TypeDisplay.GetAsync(type, cancellation), await TypeDisplay.GetAsync(thisType, cancellation));
            diagnostic = code == DiagnosticCode.TheThisContextOfType0IsNotAssignableToMethodSThisOfType1
                ? detail
                : diagnostic with { MessageChain = [detail] };
        }
        Error(node, diagnostic);
    }

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
        var (leftText, rightText) = await RelationTypeNamesAsync(left, right, cancellation);
        var diagnostic = CheckerDiagnostic.Create(
            node,
            DiagnosticLocalization.GetMessage(
                comparison
                    ? DiagnosticCode.ThisComparisonAppearsToBeUnintentionalBecauseTheTypes0And1HaveNoOverlap
                    : DiagnosticCode.Operator0CannotBeAppliedToTypes1And2),
            comparison ? [leftText, rightText] : [TokenFacts.Text(op), leftText, rightText]);
        if (suggestAwait)
            diagnostic = diagnostic with { RelatedInformation = [CheckerDiagnostic.Create(node, Messages.Did_you_forget_to_use_await)] };
        Error(node, diagnostic);
    }

    public void ArithmeticError(SyntaxNode node, Type type, DiagnosticCode code, bool suggestAwait)
    {
        var diagnostic = CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code));
        if (suggestAwait)
            diagnostic = diagnostic with { RelatedInformation = [CheckerDiagnostic.Create(node, Messages.Did_you_forget_to_use_await)] };
        Error(node, diagnostic);
    }

    public void BinaryDiagnostic(SyntaxNode node, DiagnosticCode code, bool suggestion = false, params Utf8String[] suppliedArguments)
    {
        if (!suggestion)
        {
            Utf8String[] arguments = code switch
            {
                DiagnosticCode.The0OperatorIsNotAllowedForBooleanTypesConsiderUsing1Instead => [TokenFacts.Text(node.Kind)!, node.Kind is SyntaxKind.BarToken
                    or SyntaxKind.BarEqualsToken ? Utf8Literals.LogicalOr
                    : node.Kind is SyntaxKind.AmpersandToken or SyntaxKind.AmpersandEqualsToken ? Utf8Literals.LogicalAnd : Utf8Literals.StrictNotEquals],
                DiagnosticCode.ThisConditionWillAlwaysReturn0SinceJavaScriptComparesObjectsByReferenceNotValue
                    or DiagnosticCode.ThisConditionWillAlwaysReturn0 when node is BinaryExpressionNode equality =>
                    [equality.OperatorToken!.Kind is SyntaxKind.EqualsEqualsToken or SyntaxKind.EqualsEqualsEqualsToken
                        ? Utf8Literals.False
                        : Utf8Literals.True],
                DiagnosticCode.The0OperatorCannotBeAppliedToTypeSymbol when node.Parent is BinaryExpressionNode binary => [TokenFacts.Text(binary.OperatorToken!.Kind)!],
                _ => suppliedArguments
            };
            if (code == DiagnosticCode.ThisConditionWillAlwaysReturn0 && node is BinaryExpressionNode comparison)
            {
                bool IsNaN(SyntaxNode expression) => MemberAccessRules.SkipParentheses(expression) is IdentifierNode { Text.Span: var matchedText } identifier && matchedText.SequenceEqual("NaN"u8)
                    && links.SymbolNodes.TryGet(identifier)?.ResolvedSymbol == program.Symbols.Globals.GetValueOrDefault(Utf8Literals.NaN);
                bool left = IsNaN(comparison.Left!), right = IsNaN(comparison.Right!);
                var diagnostic = CheckerDiagnostic.Create(node, Messages.This_condition_will_always_return_0, arguments);
                if (left != right)
                {
                    var location = left ? comparison.Right! : comparison.Left!;
                    Utf8String name = ExpressionChecks.EntityText(MemberAccessRules.SkipParentheses(location)) ?? Utf8Literals.Ellipsis;
                    Utf8String prefix = comparison.OperatorToken!.Kind is SyntaxKind.ExclamationEqualsToken
                        or SyntaxKind.ExclamationEqualsEqualsToken
                        ? Utf8Literals.Exclamation
                        : Utf8String.Empty;
                    diagnostic = diagnostic with
                    {
                        RelatedInformation = [CheckerDiagnostic.Create(
                        location,
                        Messages.Did_you_mean_0,
Utf8String.ConcatMany(prefix, Utf8Literals.NumberIsNaN, name, Utf8Literals.CloseParen))]
                    };
                }
                Error(node, diagnostic);
            }
            else
                Error(node, code, arguments);
        }
        else if (suggestionLocations.Add((node, code)))
            Suggestions.Add(code);
    }

    public async ValueTask<bool> GlobalNaNAsync(SyntaxNode node, CancellationToken cancellation)
            => node is IdentifierNode { Text.Span: var matchedText2 } && matchedText2.SequenceEqual("NaN"u8) && program.Symbols.Globals.GetValueOrDefault(Utf8Literals.NaN) is { } symbol
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
                Error(node.Left!, DiagnosticCode.TheLeftHandSideOfAnInstanceofExpressionMustBeOfTypeAnyAnObjectTypeOrATypeParameter);
            var signature = await CallResolution.GetAsync(node, mode: mode, cancellation: cancellation);
            if (signature == CallSignatures.Resolving)
                return context.SilentNeverType;
            await RelationDiagnostics.CheckAsync(await Signatures.ReturnAsync(signature, cancellation), context.BooleanType,
                RelationKind.Assignable,
                node.Right!,
                node.Right!,
                DiagnosticCode.AnObjectSSymbolHasInstanceMethodMustReturnABooleanValueForItToBeUsedOnTheRightHandSideOfAnInstanceofExpression,
                cancellation);
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
                    RelationKind.Assignable, node.Left!, node.Left!, DiagnosticCode.Type0IsNotAssignableToType1, cancellation);
            if (await RelationDiagnostics.CheckAsync(await NonNullAsync(right, node.Right!, cancellation), context.NonPrimitiveType,
                RelationKind.Assignable, node.Right!, node.Right!, DiagnosticCode.Type0IsNotAssignableToType1, cancellation))
                foreach (var part in right is UnionType union ? union.Types : [right])
                    if (part == context.UnknownEmptyObjectType || part is IntersectionType
                        && await Views.EmptyAnonymousAsync(
                            await Instantiation.Constraints.BaseConstraintOrTypeAsync(part, cancellation),
                            cancellation))
                    {
                        Error(
                            node.Right!,
                            DiagnosticCode.Type0MayRepresentAPrimitiveValueWhichIsNotPermittedAsTheRightOperandOfTheInOperator,
                            await TypeDisplay.GetAsync(right, cancellation));
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
