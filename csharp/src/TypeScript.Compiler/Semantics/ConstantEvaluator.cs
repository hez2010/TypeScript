using TypeScript.Compiler.Text;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Semantics;

[Flags]
internal enum OuterExpressionKinds : ushort
{
    Parentheses = 1 << 0,
    TypeAssertions = 1 << 1,
    NonNullAssertions = 1 << 2,
    PartiallyEmitted = 1 << 3,
    TypeArguments = 1 << 4,
    Satisfies = 1 << 5,
    ExcludeJSDocAssertion = 1 << 6,
    Assignments = 1 << 7,
    Comma = 1 << 8,
    Assertions = TypeAssertions | NonNullAssertions | Satisfies,
    All = Parentheses | Assertions | PartiallyEmitted | TypeArguments
}

internal readonly record struct ConstantResult(object? Value, bool IsSyntacticallyString = false,
    bool ResolvedOtherFiles = false, bool HasExternalReferences = false);

internal sealed class ConstantEvaluator
{
    private readonly Func<SyntaxNode, SyntaxNode?, CancellationToken, ValueTask<ConstantResult>> entity;
    private readonly OuterExpressionKinds outer;
    private readonly Func<SyntaxNode, CancellationToken, ValueTask<bool>>? jsdocAssertion;

    internal ConstantEvaluator(Func<SyntaxNode, SyntaxNode?, CancellationToken, ValueTask<ConstantResult>> entity,
        OuterExpressionKinds outer = 0, Func<SyntaxNode, CancellationToken, ValueTask<bool>>? jsdocAssertion = null)
    {
        if ((outer & OuterExpressionKinds.ExcludeJSDocAssertion) != 0 && jsdocAssertion is null)
            throw new ArgumentException("JSDoc assertion classification is required for this skip mode", nameof(jsdocAssertion));
        this.entity = entity;
        this.outer = outer | OuterExpressionKinds.Parentheses;
        this.jsdocAssertion = jsdocAssertion;
    }

    internal async ValueTask<ConstantResult> EvaluateAsync(
        SyntaxNode expression,
        SyntaxNode? location = null,
        CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        expression = await SkipAsync(expression, cancellation).ConfigureAwait(false);
        switch (expression)
        {
            case PrefixUnaryExpressionNode prefix:
                var operand = await EvaluateAsync(prefix.Operand!, location, cancellation).ConfigureAwait(false);
                object? unary = operand.Value is double number ? prefix.Operator switch
                {
                    SyntaxKind.PlusToken => number,
                    SyntaxKind.MinusToken => -number,
                    SyntaxKind.TildeToken => JsNumber.BitwiseNot(number),
                    _ => null
                } : null;
                return new(unary, false, operand.ResolvedOtherFiles, operand.HasExternalReferences);
            case BinaryExpressionNode binary:
                var left = await EvaluateAsync(binary.Left!, location, cancellation).ConfigureAwait(false);
                var right = await EvaluateAsync(binary.Right!, location, cancellation).ConfigureAwait(false);
                bool text = (left.IsSyntacticallyString || right.IsSyntacticallyString)
                    && binary.OperatorToken!.Kind == SyntaxKind.PlusToken;
                object? value = null;
                if (left.Value is double a && right.Value is double b)
                    value = binary.OperatorToken!.Kind switch
                    {
                        SyntaxKind.BarToken => JsNumber.BitwiseOr(a, b),
                        SyntaxKind.AmpersandToken => JsNumber.BitwiseAnd(a, b),
                        SyntaxKind.GreaterThanGreaterThanToken => JsNumber.SignedRightShift(a, b),
                        SyntaxKind.GreaterThanGreaterThanGreaterThanToken => JsNumber.UnsignedRightShift(a, b),
                        SyntaxKind.LessThanLessThanToken => JsNumber.LeftShift(a, b),
                        SyntaxKind.CaretToken => JsNumber.BitwiseXor(a, b),
                        SyntaxKind.AsteriskToken => a * b,
                        SyntaxKind.SlashToken => a / b,
                        SyntaxKind.PlusToken => a + b,
                        SyntaxKind.MinusToken => a - b,
                        SyntaxKind.PercentToken => JsNumber.Remainder(a, b),
                        SyntaxKind.AsteriskAsteriskToken => JsNumber.Exponentiate(a, b),
                        _ => null
                    };
                else if (left.Value is Utf8String or double
                    && right.Value is Utf8String or double
                    && binary.OperatorToken!.Kind == SyntaxKind.PlusToken)
                    value = Utf8String.Concat(ToText(left.Value), ToText(right.Value));
                return new(
                    value,
                    text,
                    left.ResolvedOtherFiles || right.ResolvedOtherFiles,
                    left.HasExternalReferences || right.HasExternalReferences);
            case StringLiteralNode literal:
                return new(literal.Text, true);
            case NoSubstitutionTemplateLiteralNode literal:
                return new(literal.Text, true);
            case NumericLiteralNode numeric:
                return new(JsNumber.FromString(numeric.Text));
            case TemplateExpressionNode template:
                var builder = new Utf8StringBuilder().Append(((TemplateHeadNode)template.Head!).Text.Span);
                bool otherFiles = false, external = false;
                foreach (var node in template.TemplateSpans!)
                {
                    var span = (TemplateSpanNode)node;
                    var part = await EvaluateAsync(span.Expression!, location, cancellation).ConfigureAwait(false);
                    if (part.Value is null)
                        return new(null, true);
                    builder.Append(ToText(part.Value).Span);
                    builder.Append((span.Literal switch
                    {
                        TemplateMiddleNode middle => middle.Text,
                        TemplateTailNode tail => tail.Text,
                        _ => throw new InvalidOperationException("Unexpected template literal")
                    }).Span);
                    otherFiles |= part.ResolvedOtherFiles;
                    external |= part.HasExternalReferences;
                }
                return new(Utf8String.FromBuilder(builder), true, otherFiles, external);
            case IdentifierNode:
                return await entity(expression, location, cancellation).ConfigureAwait(false);
            case PropertyAccessExpressionNode property when EntityName(property.Expression!):
            case ElementAccessExpressionNode element when EntityName(element.Expression!):
                return await entity(expression, location, cancellation).ConfigureAwait(false);
            default:
                return default;
        }
    }

    private async ValueTask<SyntaxNode> SkipAsync(SyntaxNode node, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            SyntaxNode? next = node switch
            {
                ParenthesizedExpressionNode parentheses when (outer & OuterExpressionKinds.ExcludeJSDocAssertion) == 0
                    || !await jsdocAssertion!(node, cancellation).ConfigureAwait(false) => parentheses.Expression,
                TypeAssertionNode assertion when (outer & OuterExpressionKinds.TypeAssertions) != 0 => assertion.Expression,
                AsExpressionNode assertion when (outer & OuterExpressionKinds.TypeAssertions) != 0 => assertion.Expression,
                SatisfiesExpressionNode satisfies when (outer & (OuterExpressionKinds.TypeArguments | OuterExpressionKinds.Satisfies)) != 0 => satisfies.Expression,
                ExpressionWithTypeArgumentsNode arguments when (outer & OuterExpressionKinds.TypeArguments) != 0 => arguments.Expression,
                NonNullExpressionNode nonNull when (outer & OuterExpressionKinds.NonNullAssertions) != 0 => nonNull.Expression,
                PartiallyEmittedExpressionNode emitted when (outer & OuterExpressionKinds.PartiallyEmitted) != 0 => emitted.Expression,
                BinaryExpressionNode binary when binary.OperatorToken?.Kind == SyntaxKind.EqualsToken
                    && (outer & OuterExpressionKinds.Assignments) != 0
                    || binary.OperatorToken?.Kind == SyntaxKind.CommaToken && (outer & OuterExpressionKinds.Comma) != 0 => binary.Right,
                _ => null
            };
            if (next is null)
                return node;
            node = next;
        }
    }

    internal static bool EntityName(SyntaxNode node)
    {
        while (node is PropertyAccessExpressionNode { Name: IdentifierNode } property)
            node = property.Expression!;
        return node is IdentifierNode;
    }

    internal static Utf8String ToText(object value) => value switch
    {
        Utf8String text => text,
        double number => TokenFacts.NumberText(number),
        bool boolean => boolean ? Utf8Literals.True : Utf8Literals.False,
        BigInteger integer => Utf8String.Format(integer),
        _ => throw new ArgumentException("Unsupported constant value", nameof(value))
    };

    internal static bool IsTruthy(object value) => value switch
    {
        Utf8String text => text.Length != 0,
        double number => number != 0 && !double.IsNaN(number),
        bool boolean => boolean,
        BigInteger integer => integer != 0,
        _ => throw new ArgumentException("Unsupported constant value", nameof(value))
    };
}
