using TypeScript.Compiler.Text;
using System.Numerics;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal interface IBinaryExpressionHost
{
    int TargetYear { get; }
    bool AllowUnreachableCode { get; }

    ValueTask<Type> CheckExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask<Type> NonNullAsync(Type type, SyntaxNode node, CancellationToken cancellation);

    ValueTask TruthinessAsync(Type type, SyntaxNode node, CancellationToken cancellation);

    ValueTask KnownTruthyAsync(Type type, SyntaxNode node, SyntaxNode? body, CancellationToken cancellation);

    ValueTask<Type?> AwaitedAsync(Type type, bool promiseOnly, CancellationToken cancellation);

    ValueTask AssignmentAsync(SyntaxNode left, K op, SyntaxNode right, Type leftType, Type valueType, CancellationToken cancellation);

    ValueTask<Type> DestructuringAsync(SyntaxNode left, Type source, CheckMode mode, bool rightIsThis, CancellationToken cancellation);

    ValueTask<Type> RelationalKeywordAsync(BinaryExpressionNode node, Type left, Type right, CheckMode mode, CancellationToken cancellation);

    ValueTask<bool> GlobalNaNAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask OperatorErrorAsync(SyntaxNode? node, K op, Type left, Type right, bool suggestAwait, CancellationToken cancellation);

    void ArithmeticError(SyntaxNode node, Type type, DiagnosticCode code, bool suggestAwait);

    void BinaryDiagnostic(SyntaxNode node, DiagnosticCode code, bool suggestion = false, params Utf8String[] arguments);
}

internal sealed class BinaryExpressions(TypeContext context, TypeAlgebra algebra, TypePredicates predicates,
    TypeFactQueries facts, TypeWidening widening, TypeRelations relations, ExpressionChecks checks,
    ConstantEvaluator evaluator, IBinaryExpressionHost host)
{
    internal async ValueTask<Type> CheckAsync(BinaryExpressionNode node, CheckMode mode = 0, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        var left = node.Left!;
        var right = node.Right!;
        var op = node.OperatorToken!.Kind;
        if (op == K.EqualsToken && left.Kind is K.ObjectLiteralExpression or K.ArrayLiteralExpression)
            return await host.DestructuringAsync(
                left,
                await host.CheckExpressionAsync(right, mode, cancellation).ConfigureAwait(false),
                mode,
                right.Kind == K.ThisKeyword,
                cancellation).ConfigureAwait(false);
        var leftType = await host.CheckExpressionAsync(left, mode, cancellation).ConfigureAwait(false);
        var rightType = await host.CheckExpressionAsync(right, mode, cancellation).ConfigureAwait(false);
        if (Logical(op))
        {
            var parent = left.Parent?.Parent;
            while (parent is ParenthesizedExpressionNode || parent is BinaryExpressionNode binary && Logical(binary.OperatorToken!.Kind))
                parent = parent.Parent;
            if (op == K.AmpersandAmpersandToken || parent is IfStatementNode)
                await host.KnownTruthyAsync(leftType, left, (parent as IfStatementNode)?.ThenStatement, cancellation).ConfigureAwait(false);
            if (op is not (K.QuestionQuestionToken or K.QuestionQuestionEqualsToken))
                await host.TruthinessAsync(leftType, left, cancellation).ConfigureAwait(false);
        }
        switch (op)
        {
            case K.AsteriskToken or K.AsteriskAsteriskToken or K.AsteriskEqualsToken or K.AsteriskAsteriskEqualsToken
                or K.SlashToken or K.SlashEqualsToken or K.PercentToken or K.PercentEqualsToken or K.MinusToken or K.MinusEqualsToken
                or K.LessThanLessThanToken or K.LessThanLessThanEqualsToken or K.GreaterThanGreaterThanToken or K.GreaterThanGreaterThanEqualsToken
                or K.GreaterThanGreaterThanGreaterThanToken or K.GreaterThanGreaterThanGreaterThanEqualsToken
                or K.BarToken or K.BarEqualsToken or K.CaretToken or K.CaretEqualsToken or K.AmpersandToken or K.AmpersandEqualsToken:
                if (leftType == context.SilentNeverType || rightType == context.SilentNeverType)
                    return context.SilentNeverType;
                leftType = await host.NonNullAsync(leftType, left, cancellation).ConfigureAwait(false);
                rightType = await host.NonNullAsync(rightType, right, cancellation).ConfigureAwait(false);
                if ((leftType.Flags & TypeFlags.BooleanLike) != 0 && (rightType.Flags & TypeFlags.BooleanLike) != 0
                    && op is K.BarToken or K.BarEqualsToken or K.CaretToken or K.CaretEqualsToken or K.AmpersandToken
                        or K.AmpersandEqualsToken)
                {
                    host.BinaryDiagnostic(node.OperatorToken, DiagnosticCode.The0OperatorIsNotAllowedForBooleanTypesConsiderUsing1Instead);
                    return context.NumberType;
                }
                bool leftOk = await ArithmeticAsync(
                    left,
                    leftType,
                    DiagnosticCode.TheLeftHandSideOfAnArithmeticOperationMustBeOfTypeAnyNumberBigintOrAnEnumType,
                    cancellation).ConfigureAwait(false);
                bool rightOk = await ArithmeticAsync(
                    right,
                    rightType,
                    DiagnosticCode.TheRightHandSideOfAnArithmeticOperationMustBeOfTypeAnyNumberBigintOrAnEnumType,
                    cancellation).ConfigureAwait(false);
                Type result;
                if (await predicates.AssignableAsync(leftType, TypeFlags.AnyOrUnknown, cancellation: cancellation).ConfigureAwait(false)
                    && await predicates.AssignableAsync(rightType, TypeFlags.AnyOrUnknown, cancellation: cancellation).ConfigureAwait(false)
                    || !predicates.Maybe(leftType, TypeFlags.BigIntLike, cancellation)
                        && !predicates.Maybe(rightType, TypeFlags.BigIntLike, cancellation))
                    result = context.NumberType;
                else if (await BothBigIntAsync(leftType, rightType, cancellation).ConfigureAwait(false))
                {
                    if (op is K.GreaterThanGreaterThanGreaterThanToken or K.GreaterThanGreaterThanGreaterThanEqualsToken)
                        await OperatorAsync(node, op, leftType, rightType, null, cancellation).ConfigureAwait(false);
                    else if (op is K.AsteriskAsteriskToken or K.AsteriskAsteriskEqualsToken && host.TargetYear < 2016)
                        host.BinaryDiagnostic(
                            node,
                            DiagnosticCode.ExponentiationCannotBePerformedOnBigintValuesUnlessTheTargetOptionIsSetToEs2016OrLater);
                    result = context.BigIntType;
                }
                else
                {
                    await OperatorAsync(
                        node,
                        op,
                        leftType,
                        rightType,
                        (s, t) => BothBigIntAsync(s, t, cancellation),
                        cancellation).ConfigureAwait(false);
                    result = context.ErrorType;
                }
                if (leftOk && rightOk)
                {
                    await AssignAsync(left, op, right, leftType, result, cancellation).ConfigureAwait(false);
                    if (op is K.LessThanLessThanToken or K.LessThanLessThanEqualsToken or K.GreaterThanGreaterThanToken
                        or K.GreaterThanGreaterThanEqualsToken
                        or K.GreaterThanGreaterThanGreaterThanToken or K.GreaterThanGreaterThanGreaterThanEqualsToken
                        && (await evaluator.EvaluateAsync(
                            right,
                            right,
                            cancellation).ConfigureAwait(false)).Value is double shift && Math.Abs(shift) >= 32)
                    {
                        SyntaxNode? parent = right.Parent?.Parent;
                        while (parent?.Parent is ParenthesizedExpressionNode)
                            parent = parent.Parent;
                        host.BinaryDiagnostic(
                            node,
                            DiagnosticCode.ThisOperationCanBeSimplifiedThisShiftIsIdenticalTo012,
                            parent is not EnumMemberNode,
                            CheckerDiagnostic.DeclarationName(left), TokenFacts.Text(op), TokenFacts.NumberText(shift % 32));
                    }
                }
                return result;
            case K.PlusToken or K.PlusEqualsToken:
                if (leftType == context.SilentNeverType || rightType == context.SilentNeverType)
                    return context.SilentNeverType;
                if (!await predicates.AssignableAsync(leftType, TypeFlags.StringLike, cancellation: cancellation).ConfigureAwait(false)
                    && !await predicates.AssignableAsync(rightType, TypeFlags.StringLike, cancellation: cancellation).ConfigureAwait(false))
                {
                    leftType = await host.NonNullAsync(leftType, left, cancellation).ConfigureAwait(false);
                    rightType = await host.NonNullAsync(rightType, right, cancellation).ConfigureAwait(false);
                }
                Type? addition = null;
                if (await StrictKindAsync(leftType, TypeFlags.NumberLike, cancellation).ConfigureAwait(false)
                    && await StrictKindAsync(rightType, TypeFlags.NumberLike, cancellation).ConfigureAwait(false))
                    addition = context.NumberType;
                else if (await StrictKindAsync(leftType, TypeFlags.BigIntLike, cancellation).ConfigureAwait(false)
                    && await StrictKindAsync(rightType, TypeFlags.BigIntLike, cancellation).ConfigureAwait(false))
                    addition = context.BigIntType;
                else if (await StrictKindAsync(leftType, TypeFlags.StringLike, cancellation).ConfigureAwait(false)
                    || await StrictKindAsync(rightType, TypeFlags.StringLike, cancellation).ConfigureAwait(false))
                    addition = context.StringType;
                else if ((leftType.Flags & TypeFlags.Any) != 0 || (rightType.Flags & TypeFlags.Any) != 0)
                    addition = leftType == context.ErrorType || rightType == context.ErrorType ? context.ErrorType : context.AnyType;
                if (addition is not null
                    && !await SymbolsAllowedAsync(left, right, leftType, rightType, cancellation).ConfigureAwait(false))
                    return addition;
                if (addition is null)
                {
                    var close = TypeFlags.NumberLike | TypeFlags.BigIntLike | TypeFlags.StringLike | TypeFlags.AnyOrUnknown;
                    await OperatorAsync(
                        node,
                        op,
                        leftType,
                        rightType,
                        async (s, t) => await predicates.AssignableAsync(s, close, cancellation: cancellation).ConfigureAwait(false)
                        && await predicates.AssignableAsync(t, close, cancellation: cancellation).ConfigureAwait(false),
                        cancellation).ConfigureAwait(false);
                    return context.AnyType;
                }
                if (op == K.PlusEqualsToken)
                    await AssignAsync(left, op, right, leftType, addition, cancellation).ConfigureAwait(false);
                return addition;
            case K.LessThanToken or K.GreaterThanToken or K.LessThanEqualsToken or K.GreaterThanEqualsToken:
                if (await SymbolsAllowedAsync(left, right, leftType, rightType, cancellation).ConfigureAwait(false))
                {
                    leftType = await ComparisonBaseAsync(
                        await host.NonNullAsync(leftType, left, cancellation).ConfigureAwait(false),
                        cancellation).ConfigureAwait(false);
                    rightType = await ComparisonBaseAsync(
                        await host.NonNullAsync(rightType, right, cancellation).ConfigureAwait(false),
                        cancellation).ConfigureAwait(false);
                    if (!await OrderedAsync(leftType, rightType, cancellation).ConfigureAwait(false))
                        await OperatorAsync(
                            node,
                            op,
                            leftType,
                            rightType,
                            (s, t) => OrderedAsync(s, t, cancellation),
                            cancellation).ConfigureAwait(false);
                }
                return context.BooleanType;
            case K.EqualsEqualsToken or K.ExclamationEqualsToken or K.EqualsEqualsEqualsToken or K.ExclamationEqualsEqualsToken:
                if ((mode & CheckMode.TypeOnly) == 0)
                {
                    if ((LiteralObject(left) || LiteralObject(right))
                        && ((left.Flags & NodeFlags.JavaScriptFile) == 0
                            || op is K.EqualsEqualsEqualsToken or K.ExclamationEqualsEqualsToken))
                        host.BinaryDiagnostic(
                            node,
                            DiagnosticCode.ThisConditionWillAlwaysReturn0SinceJavaScriptComparesObjectsByReferenceNotValue);
                    bool leftNaN = await host.GlobalNaNAsync(SkipParentheses(left), cancellation).ConfigureAwait(false);
                    bool rightNaN = await host.GlobalNaNAsync(SkipParentheses(right), cancellation).ConfigureAwait(false);
                    if (leftNaN || rightNaN)
                        host.BinaryDiagnostic(node, DiagnosticCode.ThisConditionWillAlwaysReturn0);
                    if (!await EqualityAsync(leftType, rightType, cancellation).ConfigureAwait(false))
                        await OperatorAsync(
                            node,
                            op,
                            leftType,
                            rightType,
                            (s, t) => EqualityAsync(s, t, cancellation),
                            cancellation).ConfigureAwait(false);
                }
                return context.BooleanType;
            case K.InKeyword or K.InstanceOfKeyword:
                return await host.RelationalKeywordAsync(node, leftType, rightType, mode, cancellation).ConfigureAwait(false);
            case K.AmpersandAmpersandToken or K.AmpersandAmpersandEqualsToken:
                var conjunction = leftType;
                if (await facts.GetAsync(leftType, TypeFacts.Truthy, cancellation).ConfigureAwait(false) != 0)
                    conjunction = await algebra.UnionAsync(
                        [
                                await FalsyAsync(
                                    context.StrictNullChecks
                                        ? leftType
                                        : await widening.LiteralBaseAsync(rightType, cancellation).ConfigureAwait(false),
                                    cancellation).ConfigureAwait(false),
                                rightType
                            ],
                        cancellation: cancellation).ConfigureAwait(false);
                await AssignAsync(left, op, right, leftType, rightType, cancellation).ConfigureAwait(false);
                return conjunction;
            case K.BarBarToken or K.BarBarEqualsToken:
                var disjunction = leftType;
                if (await facts.GetAsync(leftType, TypeFacts.Falsy, cancellation).ConfigureAwait(false) != 0)
                    disjunction = await algebra.UnionAsync(
                        [
                                await facts.NonNullableAsync(
                                    await facts.FilterAsync(leftType, TypeFacts.Truthy, cancellation).ConfigureAwait(false),
                                    cancellation).ConfigureAwait(false),
                                rightType
                            ],
                        UnionReduction.Subtype,
                        cancellation: cancellation).ConfigureAwait(false);
                await AssignAsync(left, op, right, leftType, rightType, cancellation).ConfigureAwait(false);
                return disjunction;
            case K.QuestionQuestionToken or K.QuestionQuestionEqualsToken:
                if (op == K.QuestionQuestionToken)
                    await checks.NullishOperandsAsync(left, right, cancellation).ConfigureAwait(false);
                var coalescing = leftType;
                if (await facts.GetAsync(leftType, TypeFacts.EQUndefinedOrNull, cancellation).ConfigureAwait(false) != 0)
                    coalescing = await algebra.UnionAsync(
                        [await facts.NonNullableAsync(leftType, cancellation).ConfigureAwait(false), rightType],
                        UnionReduction.Subtype,
                        cancellation: cancellation).ConfigureAwait(false);
                await AssignAsync(left, op, right, leftType, rightType, cancellation).ConfigureAwait(false);
                return coalescing;
            case K.EqualsToken:
                await AssignAsync(left, op, right, leftType, rightType, cancellation).ConfigureAwait(false);
                return rightType;
            case K.CommaToken:
                if (!host.AllowUnreachableCode && SideEffectFree(left) && !IndirectCall(node))
                {
                    var file = TypeScript.Compiler.Binding.SemanticSyntax.Source(left);
                    var scanner = file is null ? null : new Scanner(file.Source);
                    if (scanner is not null)
                        scanner.ResetPosition(left.Pos);
                    scanner?.Scan();
                    int start = scanner is not null ? scanner.TokenStart : left.Pos;
                    if (file?.ParseDiagnostics.Any(
                        d => d.Code == DiagnosticCode.JSXExpressionsMustHaveOneParentElement
                            && d.Start <= start
                            && start < d.Start + d.Length) != true)
                        host.BinaryDiagnostic(left, DiagnosticCode.LeftSideOfCommaOperatorIsUnusedAndHasNoSideEffects);
                }
                return rightType;
            default:
                throw new InvalidOperationException("Unknown binary operator");
        }
    }

    private ValueTask AssignAsync(SyntaxNode left, K op, SyntaxNode right, Type target, Type value, CancellationToken cancellation)
        => Assignment(op) ? host.AssignmentAsync(left, op, right, target, value, cancellation) : ValueTask.CompletedTask;

    private ValueTask<bool> StrictKindAsync(Type type, TypeFlags flags, CancellationToken cancellation) =>
        predicates.AssignableAsync(type, flags, true, cancellation);

    private async ValueTask<bool> BothBigIntAsync(Type left, Type right, CancellationToken cancellation)
            => await predicates.AssignableAsync(left, TypeFlags.BigIntLike, cancellation: cancellation).ConfigureAwait(false)
                && await predicates.AssignableAsync(right, TypeFlags.BigIntLike, cancellation: cancellation).ConfigureAwait(false);

    private async ValueTask<bool> ArithmeticAsync(SyntaxNode node, Type type, DiagnosticCode code, CancellationToken cancellation)
    {
        if (await relations.RelatedAsync(type, context.NumberOrBigIntType, RelationKind.Assignable, cancellation).ConfigureAwait(false))
            return true;
        var awaited = await host.AwaitedAsync(type, true, cancellation).ConfigureAwait(false);
        bool suggestAwait = awaited is not null
            && await relations.RelatedAsync(
                awaited,
                context.NumberOrBigIntType,
                RelationKind.Assignable,
                cancellation).ConfigureAwait(false);
        host.ArithmeticError(node, type, code, suggestAwait);
        return false;
    }

    private async ValueTask<bool> SymbolsAllowedAsync(SyntaxNode left, SyntaxNode right, Type a, Type b, CancellationToken cancellation)
    {
        SyntaxNode? invalid = await predicates.MaybeAsync(a, TypeFlags.ESSymbolLike, true, cancellation).ConfigureAwait(false) ? left
            : await predicates.MaybeAsync(b, TypeFlags.ESSymbolLike, true, cancellation).ConfigureAwait(false) ? right : null;
        if (invalid is null)
            return true;
        host.BinaryDiagnostic(invalid, DiagnosticCode.The0OperatorCannotBeAppliedToTypeSymbol);
        return false;
    }

    private async ValueTask OperatorAsync(SyntaxNode? node, K op, Type left, Type right,
        Func<Type, Type, ValueTask<bool>>? compatible, CancellationToken cancellation)
    {
        bool withAwait = false;
        if (compatible is not null)
        {
            var awaitedLeft = await host.AwaitedAsync(left, false, cancellation).ConfigureAwait(false);
            var awaitedRight = await host.AwaitedAsync(right, false, cancellation).ConfigureAwait(false);
            withAwait = !(awaitedLeft == left && awaitedRight == right) && awaitedLeft is not null && awaitedRight is not null
                && await compatible(awaitedLeft, awaitedRight).ConfigureAwait(false);
        }
        if (!withAwait && compatible is not null)
        {
            var leftBase = await widening.LiteralBaseAsync(left, cancellation).ConfigureAwait(false);
            var rightBase = await widening.LiteralBaseAsync(right, cancellation).ConfigureAwait(false);
            if (!await compatible(leftBase, rightBase).ConfigureAwait(false))
                (left, right) = (leftBase, rightBase);
        }
        await host.OperatorErrorAsync(node, op, left, right, withAwait, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<bool> OrderedAsync(Type left, Type right, CancellationToken cancellation)
    {
        if (((left.Flags | right.Flags) & TypeFlags.Any) != 0)
            return true;
        bool a = await relations.RelatedAsync(
            left,
            context.NumberOrBigIntType,
            RelationKind.Assignable,
            cancellation).ConfigureAwait(false);
        bool b = await relations.RelatedAsync(
            right,
            context.NumberOrBigIntType,
            RelationKind.Assignable,
            cancellation).ConfigureAwait(false);
        return a && b || !a && !b && await ComparableAsync(left, right, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<bool> EqualityAsync(Type left, Type right, CancellationToken cancellation)
        => (right.Flags & TypeFlags.Nullable) != 0
            || await relations.RelatedAsync(left, right, RelationKind.Comparable, cancellation).ConfigureAwait(false)
            || (left.Flags & TypeFlags.Nullable) != 0 || await relations.RelatedAsync(
                right,
                left,
                RelationKind.Comparable,
                cancellation).ConfigureAwait(false);

    private async ValueTask<bool> ComparableAsync(Type left, Type right, CancellationToken cancellation)
            => await relations.RelatedAsync(left, right, RelationKind.Comparable, cancellation).ConfigureAwait(false)
                || await relations.RelatedAsync(right, left, RelationKind.Comparable, cancellation).ConfigureAwait(false);

    private async ValueTask<Type> ComparisonBaseAsync(Type type, CancellationToken cancellation)
    {
        if ((type.Flags & (TypeFlags.StringLiteral | TypeFlags.TemplateLiteral | TypeFlags.StringMapping)) != 0)
            return context.StringType;
        if ((type.Flags & (TypeFlags.NumberLiteral | TypeFlags.Enum)) != 0)
            return context.NumberType;
        if ((type.Flags & TypeFlags.BigIntLiteral) != 0)
            return context.BigIntType;
        if ((type.Flags & TypeFlags.BooleanLiteral) != 0)
            return context.BooleanType;
        return type is UnionType
            ? await algebra.MapAsync(
                type,
                async t => await ComparisonBaseAsync(t, cancellation).ConfigureAwait(false),
                cancellation: cancellation).ConfigureAwait(false) ?? context.NeverType
            : type;
    }

    private async ValueTask<Type> FalsyAsync(Type type, CancellationToken cancellation)
        => await algebra.MapAsync(
            type,
            t => ValueTask.FromResult<Type?>(
            (t.Flags & TypeFlags.String) != 0 ? context.GetStringLiteralType(Utf8String.Empty)
            : (t.Flags & TypeFlags.Number) != 0 ? context.GetNumberLiteralType(0) : (t.Flags & TypeFlags.BigInt) != 0 ? context.GetBigIntLiteralType(BigInteger.Zero)
            : t == context.RegularFalseType
                || t == context.FalseType
                || (t.Flags & (TypeFlags.Void | TypeFlags.Nullable | TypeFlags.AnyOrUnknown)) != 0
                || t is LiteralType { Value: Utf8String { IsEmpty: true } or 0d } || t is LiteralType { Value: BigInteger integer }
                    && integer.IsZero ? t : context.NeverType),
            cancellation: cancellation).ConfigureAwait(false) ?? context.NeverType;

    internal static bool Assignment(K op) => op is >= K.FirstAssignment and <= K.LastAssignment;

    internal static bool Logical(K op) => op is K.AmpersandAmpersandToken or K.BarBarToken or K.QuestionQuestionToken
            or K.AmpersandAmpersandEqualsToken or K.BarBarEqualsToken or K.QuestionQuestionEqualsToken;

    private static bool LiteralObject(SyntaxNode node) =>
        node.Kind is K.ObjectLiteralExpression or K.ArrayLiteralExpression or K.RegularExpressionLiteral
            or K.FunctionExpression or K.ClassExpression;

    private static SyntaxNode SkipParentheses(SyntaxNode node)
    {
        while (node is ParenthesizedExpressionNode p)
            node = p.Expression!;
        return node;
    }

    internal static bool SideEffectFree(SyntaxNode node)
    {
        var pending = new Stack<SyntaxNode>();
        pending.Push(node);
        while (pending.TryPop(out var current))
        {
            current = SkipParentheses(current);
            if (current is ConditionalExpressionNode conditional)
            {
                pending.Push(conditional.WhenFalse!);
                pending.Push(conditional.WhenTrue!);
                continue;
            }
            if (current is BinaryExpressionNode binary)
            {
                if (Assignment(binary.OperatorToken!.Kind))
                    return false;
                pending.Push(binary.Right!);
                pending.Push(binary.Left!);
                continue;
            }
            if (current is PrefixUnaryExpressionNode prefix
                && prefix.Operator is K.ExclamationToken or K.PlusToken or K.MinusToken or K.TildeToken)
                continue;
            if (current.Kind is not (K.Identifier or K.StringLiteral or K.RegularExpressionLiteral or K.TaggedTemplateExpression
                or K.TemplateExpression
                or K.NoSubstitutionTemplateLiteral or K.NumericLiteral or K.BigIntLiteral or K.TrueKeyword or K.FalseKeyword or K.NullKeyword
                or K.UndefinedKeyword or K.FunctionExpression or K.ClassExpression or K.ArrowFunction or K.ArrayLiteralExpression or K.ObjectLiteralExpression
                or K.TypeOfExpression or K.NonNullExpression or K.JsxSelfClosingElement or K.JsxElement))
                return false;
        }
        return true;
    }

    private static bool IndirectCall(BinaryExpressionNode node) => node.Parent is ParenthesizedExpressionNode parent
            && node.Left is NumericLiteralNode { Text.Span: var matchedText } && matchedText.SequenceEqual("0"u8)
            && (parent.Parent is CallExpressionNode call && call.Expression == parent || parent.Parent is TaggedTemplateExpressionNode)
            && (node.Right is PropertyAccessExpressionNode || node.Right is ElementAccessExpressionNode || node.Right is IdentifierNode { Text.Span: var matchedText2 } && matchedText2.SequenceEqual("eval"u8));
}
