using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IExpressionTypeHost
{
    bool SkipDirectInference(SyntaxNode node);

    ValueTask<Type> OtherExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask<Type> FinishExpressionAsync(SyntaxNode node, Type type, CheckMode mode, CancellationToken cancellation);

    ValueTask<Type> NonNullAsync(Type type, SyntaxNode node, CancellationToken cancellation);

    ValueTask TruthinessAsync(Type type, SyntaxNode node, CancellationToken cancellation);

    ValueTask KnownTruthyAsync(Type type, SyntaxNode node, SyntaxNode? body, CancellationToken cancellation);

    ValueTask<bool> TemplateContextAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<bool> MaybeKindAsync(Type type, TypeFlags flags, bool baseConstraint, CancellationToken cancellation);

    ValueTask<bool> AssignableKindAsync(Type type, TypeFlags flags, CancellationToken cancellation);

    ValueTask CheckIncrementAsync(SyntaxNode operand, Type type, CancellationToken cancellation);

    void LiteralGrammar(SyntaxNode node);

    void DeferExpression(SyntaxNode node);

    void ExpressionError(SyntaxNode node, DiagnosticCode code);

    ValueTask TypeExpressionErrorAsync(SyntaxNode node, DiagnosticCode code, Type type, CancellationToken cancellation);
}

internal sealed class ExpressionTypes(TypeContext context, TypeAlgebra algebra, TypeFactQueries facts, TypeRelations relations,
    TypeInstantiation instantiation, ConstantEvaluator evaluator, IExpressionTypeHost host)
{
    internal SyntaxNode? CurrentNode { get; private set; }
    private Type? typeofType;

    internal async ValueTask<Type> CheckAsync(SyntaxNode node, CheckMode mode = 0, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        var previous = CurrentNode;
        CurrentNode = node;
        instantiation.ResetExpressionCount();
        try
        {
            var type = await WorkerAsync(node, mode, cancellation).ConfigureAwait(false);
            return await host.FinishExpressionAsync(node, type, mode, cancellation).ConfigureAwait(false);
        }
        finally
        {
            CurrentNode = previous;
        }
    }

    private async ValueTask<Type> WorkerAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation)
    {
        switch (node)
        {
            case StringLiteralNode literal:
                return host.SkipDirectInference(node)
                    ? context.BlockedStringType
                    : context.GetFreshLiteralType(context.GetStringLiteralType(literal.Text));
            case NoSubstitutionTemplateLiteralNode literal:
                return host.SkipDirectInference(node)
                    ? context.BlockedStringType
                    : context.GetFreshLiteralType(context.GetStringLiteralType(literal.Text));
            case NumericLiteralNode numeric:
                host.LiteralGrammar(node);
                return context.GetFreshLiteralType(context.GetNumberLiteralType(JsNumber.FromString(numeric.Text)));
            case BigIntLiteralNode bigint:
                host.LiteralGrammar(node);
                return context.GetFreshLiteralType(context.GetBigIntLiteralType(BigInt(bigint.Text)));
            case { Kind: SyntaxKind.NullKeyword or SyntaxKind.OmittedExpression }:
                return node.Kind == SyntaxKind.NullKeyword ? context.NullWideningType : context.UndefinedWideningType;
            case { Kind: SyntaxKind.TrueKeyword }:
                return context.TrueType;
            case { Kind: SyntaxKind.FalseKeyword }:
                return context.FalseType;
            case ParenthesizedExpressionNode parentheses:
                return await CheckAsync(parentheses.Expression!, mode, cancellation).ConfigureAwait(false);
            case TypeOfExpressionNode typeOf:
                await CheckAsync(typeOf.Expression!, cancellation: cancellation).ConfigureAwait(false);
                return typeofType ??= await algebra.UnionAsync(
                    new[] { "bigint", "boolean", "function", "number", "object", "string", "symbol", "undefined" }
                    .Select(context.GetStringLiteralType).ToArray(), cancellation: cancellation).ConfigureAwait(false);
            case VoidExpressionNode:
                host.DeferExpression(node);
                return context.UndefinedWideningType;
            case NonNullExpressionNode nonNull when (node.Flags & NodeFlags.OptionalChain) == 0:
                return await facts.NonNullableAsync(
                    await CheckAsync(nonNull.Expression!, cancellation: cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false);
            case PrefixUnaryExpressionNode prefix:
                return await PrefixAsync(prefix, cancellation).ConfigureAwait(false);
            case PostfixUnaryExpressionNode postfix:
                var operand = await CheckAsync(postfix.Operand!, cancellation: cancellation).ConfigureAwait(false);
                if (operand == context.SilentNeverType)
                    return operand;
                await host.CheckIncrementAsync(
                    postfix.Operand!,
                    await host.NonNullAsync(operand, postfix.Operand!, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false);
                return await UnaryResultAsync(operand, cancellation).ConfigureAwait(false);
            case ConditionalExpressionNode conditional:
                var condition = await CheckAsync(conditional.Condition!, mode, cancellation).ConfigureAwait(false);
                await host.TruthinessAsync(condition, conditional.Condition!, cancellation).ConfigureAwait(false);
                await host.KnownTruthyAsync(condition, conditional.Condition!, conditional.WhenTrue, cancellation).ConfigureAwait(false);
                var whenTrue = await CheckAsync(conditional.WhenTrue!, mode, cancellation).ConfigureAwait(false);
                var whenFalse = await CheckAsync(conditional.WhenFalse!, mode, cancellation).ConfigureAwait(false);
                return await algebra.UnionAsync(
                    [whenTrue, whenFalse],
                    UnionReduction.Subtype,
                    cancellation: cancellation).ConfigureAwait(false);
            case TemplateExpressionNode template:
                return await TemplateAsync(template, cancellation).ConfigureAwait(false);
            default:
                return await host.OtherExpressionAsync(node, mode, cancellation).ConfigureAwait(false);
        }
    }

    private async ValueTask<Type> PrefixAsync(PrefixUnaryExpressionNode node, CancellationToken cancellation)
    {
        var operand = await CheckAsync(node.Operand!, cancellation: cancellation).ConfigureAwait(false);
        if (operand == context.SilentNeverType)
            return operand;
        if (node.Operand is NumericLiteralNode numeric && node.Operator is SyntaxKind.MinusToken or SyntaxKind.PlusToken)
        {
            double number = JsNumber.FromString(numeric.Text);
            return context.GetFreshLiteralType(context.GetNumberLiteralType(node.Operator == SyntaxKind.MinusToken ? -number : number));
        }
        if (node.Operand is BigIntLiteralNode bigint && node.Operator == SyntaxKind.MinusToken)
            return context.GetFreshLiteralType(context.GetBigIntLiteralType(-BigInt(bigint.Text)));
        switch (node.Operator)
        {
            case SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.TildeToken:
                await host.NonNullAsync(operand, node.Operand!, cancellation).ConfigureAwait(false);
                if (await host.MaybeKindAsync(operand, TypeFlags.ESSymbolLike, true, cancellation).ConfigureAwait(false))
                    host.ExpressionError(node.Operand!, DiagnosticCode.The0OperatorCannotBeAppliedToTypeSymbol);
                if (node.Operator == SyntaxKind.PlusToken)
                {
                    if (await host.MaybeKindAsync(operand, TypeFlags.BigIntLike, true, cancellation).ConfigureAwait(false))
                        await host.TypeExpressionErrorAsync(
                            node.Operand!,
                            DiagnosticCode.Operator0CannotBeAppliedToType1,
                            operand,
                            cancellation).ConfigureAwait(false);
                    return context.NumberType;
                }
                return await UnaryResultAsync(operand, cancellation).ConfigureAwait(false);
            case SyntaxKind.ExclamationToken:
                await host.TruthinessAsync(operand, node.Operand!, cancellation).ConfigureAwait(false);
                return await facts.GetAsync(operand, TypeFacts.Truthy | TypeFacts.Falsy, cancellation).ConfigureAwait(false) switch
                {
                    TypeFacts.Truthy => context.FalseType,
                    TypeFacts.Falsy => context.TrueType,
                    _ => context.BooleanType
                };
            case SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken:
                await host.CheckIncrementAsync(
                    node.Operand!,
                    await host.NonNullAsync(operand, node.Operand!, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false);
                return await UnaryResultAsync(operand, cancellation).ConfigureAwait(false);
            default:
                return context.ErrorType;
        }
    }

    private async ValueTask<Type> UnaryResultAsync(Type operand, CancellationToken cancellation)
    {
        if (!await host.MaybeKindAsync(operand, TypeFlags.BigIntLike, false, cancellation).ConfigureAwait(false))
            return context.NumberType;
        return await host.AssignableKindAsync(operand, TypeFlags.AnyOrUnknown, cancellation).ConfigureAwait(false)
            || await host.MaybeKindAsync(
                operand,
                TypeFlags.NumberLike,
                false,
                cancellation).ConfigureAwait(false) ? context.NumberOrBigIntType : context.BigIntType;
    }

    private async ValueTask<Type> TemplateAsync(TemplateExpressionNode template, CancellationToken cancellation)
    {
        var texts = new List<string> { ((TemplateHeadNode)template.Head!).Text };
        var types = new List<Type>();
        foreach (var node in template.TemplateSpans!)
        {
            var span = (TemplateSpanNode)node;
            var type = await CheckAsync(span.Expression!, cancellation: cancellation).ConfigureAwait(false);
            if (await host.MaybeKindAsync(type, TypeFlags.ESSymbolLike, true, cancellation).ConfigureAwait(false))
                host.ExpressionError(
                    span.Expression!,
                    DiagnosticCode.ImplicitConversionOfASymbolToAStringWillFailAtRuntimeConsiderWrappingThisExpressionInString);
            texts.Add(
                span.Literal switch
                {
                    TemplateMiddleNode middle => middle.Text,
                    TemplateTailNode tail => tail.Text,
                    _ => throw new InvalidOperationException("Unexpected template token")
                });
            types.Add(
                await relations.RelatedAsync(
                    type,
                    context.TemplateConstraintType,
                    RelationKind.Assignable,
                    cancellation).ConfigureAwait(false)
                    ? type
                    : context.StringType);
        }
        if (template.Parent is not TaggedTemplateExpressionNode
            && (await evaluator.EvaluateAsync(template, template, cancellation).ConfigureAwait(false)).Value is string value)
            return context.GetFreshLiteralType(context.GetStringLiteralType(value));
        return await host.TemplateContextAsync(template, cancellation).ConfigureAwait(false)
            ? await algebra.TemplateAsync(texts, types, cancellation).ConfigureAwait(false) : context.StringType;
    }

    internal static BigInteger BigInt(string text)
    {
        var span = text.AsSpan().TrimEnd('n');
        int radix = span.StartsWith(
            "0x",
            StringComparison.OrdinalIgnoreCase) ? 16 : span.StartsWith("0b", StringComparison.OrdinalIgnoreCase) ? 2
            : span.StartsWith("0o", StringComparison.OrdinalIgnoreCase) ? 8 : 10;
        if (radix == 10)
            return BigInteger.Parse(span, CultureInfo.InvariantCulture);
        BigInteger result = 0;
        foreach (char ch in span[2..])
        {
            int digit = ch is >= '0' and <= '9'
                ? ch - '0'
                : ch is >= 'a' and <= 'f' ? ch - 'a' + 10 : ch is >= 'A' and <= 'F' ? ch - 'A' + 10 : -1;
            if (digit < 0 || digit >= radix)
                throw new FormatException("Invalid bigint literal");
            result = result * radix + digit;
        }
        return result;
    }
}
