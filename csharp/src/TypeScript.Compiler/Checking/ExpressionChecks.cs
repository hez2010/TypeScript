using System.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Checking;

internal interface IExpressionCheckHost
{
    ValueTask<bool> UndefinedIdentifierAsync(IdentifierNode node, CancellationToken cancellation);

    void ExpressionError(SyntaxNode node, DiagnosticCode code);
}

internal sealed class ExpressionChecks(TypeContext context, TypeFactQueries facts, IExpressionCheckHost host)
{
    internal ValueTask<Type> NonNullAsync(Type type, SyntaxNode node, CancellationToken cancellation = default) =>
        NonNullCoreAsync(type, node, false, cancellation);

    internal ValueTask<Type> InvocationAsync(Type type, SyntaxNode node, CancellationToken cancellation = default) =>
        NonNullCoreAsync(type, node, true, cancellation);

    private async ValueTask<Type> NonNullCoreAsync(Type type, SyntaxNode node, bool invocation, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        string? name = EntityText(node);
        if (context.StrictNullChecks && (type.Flags & TypeFlags.Unknown) != 0)
        {
            host.ExpressionError(
                node,
                name is not null && Encoding.UTF8.GetByteCount(name) < 100
                    ? DiagnosticCode.X0IsOfTypeUnknown
                    : DiagnosticCode.ObjectIsOfTypeUnknown);
            return context.ErrorType;
        }
        var nullable = await facts.GetAsync(type, TypeFacts.IsUndefinedOrNull, cancellation).ConfigureAwait(false);
        if ((nullable & TypeFacts.IsUndefinedOrNull) == 0)
            return type;
        bool undefined = (nullable & TypeFacts.IsUndefined) != 0, nullValue = (nullable & TypeFacts.IsNull) != 0;
        DiagnosticCode code = invocation ? undefined
            ? nullValue
                ? DiagnosticCode.CannotInvokeAnObjectWhichIsPossiblyNullOrUndefined
                : DiagnosticCode.CannotInvokeAnObjectWhichIsPossiblyUndefined
            : DiagnosticCode.CannotInvokeAnObjectWhichIsPossiblyNull
            : node.Kind == SyntaxKind.NullKeyword || node is IdentifierNode { Text: "undefined" } ? DiagnosticCode.TheValue0CannotBeUsedHere
            : name is { Length: > 0 }
                && Encoding.UTF8.GetByteCount(name) < 100 ? undefined
                    ? nullValue ? DiagnosticCode.X0IsPossiblyNullOrUndefined : DiagnosticCode.X0IsPossiblyUndefined
                    : DiagnosticCode.X0IsPossiblyNull
            : undefined
                ? nullValue ? DiagnosticCode.ObjectIsPossiblyNullOrUndefined : DiagnosticCode.ObjectIsPossiblyUndefined
                : DiagnosticCode.ObjectIsPossiblyNull;
        host.ExpressionError(node, code);
        var result = await facts.NonNullableAsync(type, cancellation).ConfigureAwait(false);
        return (result.Flags & (TypeFlags.Nullable | TypeFlags.Never)) != 0 ? context.ErrorType : result;
    }

    internal async ValueTask TruthinessAsync(Type type, SyntaxNode node, CancellationToken cancellation = default)
    {
        if ((type.Flags & TypeFlags.Void) != 0)
        {
            host.ExpressionError(node, DiagnosticCode.AnExpressionOfTypeVoidCannotBeTestedForTruthiness);
            return;
        }
        int semantics = await SyntacticTruthinessAsync(node, cancellation).ConfigureAwait(false);
        if (semantics != 3)
            host.ExpressionError(
                node,
                semantics == 1 ? DiagnosticCode.ThisKindOfExpressionIsAlwaysTruthy : DiagnosticCode.ThisKindOfExpressionIsAlwaysFalsy);
    }

    internal async ValueTask<int> SyntacticTruthinessAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        var pending = new Stack<SyntaxNode>();
        pending.Push(node);
        int result = 0;
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            current = SkipOuter(current);
            if (current is ConditionalExpressionNode conditional)
            {
                pending.Push(conditional.WhenFalse!);
                pending.Push(conditional.WhenTrue!);
                continue;
            }
            result |= current switch
            {
                NumericLiteralNode number => number.Text is "0" or "1" ? 3 : 1,
                StringLiteralNode text => text.Text.Length != 0 ? 1 : 2,
                NoSubstitutionTemplateLiteralNode text => text.Text.Length != 0 ? 1 : 2,
                {
                    Kind: SyntaxKind.ArrayLiteralExpression or SyntaxKind.ArrowFunction or SyntaxKind.BigIntLiteral
                        or SyntaxKind.ClassExpression
                    or SyntaxKind.FunctionExpression or SyntaxKind.JsxElement or SyntaxKind.JsxSelfClosingElement or SyntaxKind.ObjectLiteralExpression
                    or SyntaxKind.RegularExpressionLiteral
                } => 1,
                { Kind: SyntaxKind.VoidExpression or SyntaxKind.NullKeyword } => 2,
                IdentifierNode identifier when await host.UndefinedIdentifierAsync(identifier, cancellation).ConfigureAwait(false) => 2,
                _ => 3
            };
        }
        return result;
    }

    internal static SyntaxNode SkipOuter(SyntaxNode node)
    {
        while (true)
        {
            var next = node switch
            {
                ParenthesizedExpressionNode p => p.Expression,
                AsExpressionNode a => a.Expression,
                TypeAssertionNode a => a.Expression,
                NonNullExpressionNode n => n.Expression,
                SatisfiesExpressionNode s => s.Expression,
                ExpressionWithTypeArgumentsNode a => a.Expression,
                PartiallyEmittedExpressionNode e => e.Expression,
                _ => null
            };
            if (next is null)
                return node;
            node = next;
        }
    }

    internal async ValueTask NullishOperandsAsync(SyntaxNode left, SyntaxNode right, CancellationToken cancellation = default)
    {
        SyntaxNode? invalid = null;
        if (left.Parent?.Parent is BinaryExpressionNode parent)
        {
            if (parent.Left is BinaryExpressionNode && parent.OperatorToken?.Kind == SyntaxKind.BarBarToken)
                invalid = parent.Left;
        }
        else if (left is BinaryExpressionNode leftBinary)
        {
            if (leftBinary.OperatorToken?.Kind is SyntaxKind.BarBarToken or SyntaxKind.AmpersandAmpersandToken)
                invalid = left;
        }
        else if (right is BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.AmpersandAmpersandToken })
            invalid = right;
        if (invalid is not null && SemanticSyntax.Source(invalid)?.ParseDiagnostics.Count == 0)
            host.ExpressionError(invalid, DiagnosticCode.X0And1OperationsCannotBeMixedWithoutParentheses);
        var target = SkipOuter(left);
        int semantics = await NullishnessAsync(target, cancellation).ConfigureAwait(false);
        if (semantics != 3)
            host.ExpressionError(
                target,
                semantics == 1
                    ? DiagnosticCode.ThisExpressionIsAlwaysNullish
                    : DiagnosticCode.RightOperandOfIsUnreachableBecauseTheLeftOperandIsNeverNullish);
    }

    internal async ValueTask<int> NullishnessAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        node = SkipOuter(node);
        switch (node.Kind)
        {
            case SyntaxKind.AwaitExpression or SyntaxKind.CallExpression or SyntaxKind.TaggedTemplateExpression
                or SyntaxKind.ElementAccessExpression
                or SyntaxKind.MetaProperty or SyntaxKind.NewExpression or SyntaxKind.PropertyAccessExpression or SyntaxKind.YieldExpression or SyntaxKind.ThisKeyword:
                return 3;
            case SyntaxKind.BinaryExpression:
                var binary = (BinaryExpressionNode)node;
                switch (binary.OperatorToken!.Kind)
                {
                    case SyntaxKind.BarBarToken or SyntaxKind.BarBarEqualsToken or SyntaxKind.AmpersandAmpersandToken
                        or SyntaxKind.AmpersandAmpersandEqualsToken:
                        return 3;
                    case SyntaxKind.CommaToken or SyntaxKind.EqualsToken:
                        return await NullishnessAsync(binary.Right!, cancellation).ConfigureAwait(false);
                    case SyntaxKind.QuestionQuestionToken or SyntaxKind.QuestionQuestionEqualsToken:
                        int left = await NullishnessAsync(binary.Left!, cancellation).ConfigureAwait(false);
                        return (left & 2) | ((left & 1) != 0
                            ? await NullishnessAsync(binary.Right!, cancellation).ConfigureAwait(false)
                            : 0);
                    default:
                        return 2;
                }
            case SyntaxKind.ConditionalExpression:
                var conditional = (ConditionalExpressionNode)node;
                return await NullishnessAsync(conditional.WhenTrue!, cancellation).ConfigureAwait(false)
                    | await NullishnessAsync(conditional.WhenFalse!, cancellation).ConfigureAwait(false);
            case SyntaxKind.NullKeyword:
                return 1;
            case SyntaxKind.Identifier:
                return await host.UndefinedIdentifierAsync((IdentifierNode)node, cancellation).ConfigureAwait(false) ? 1 : 3;
            default:
                return 2;
        }
    }

    internal static string? EntityText(SyntaxNode node)
    {
        if (!ConstantEvaluator.EntityName(node))
            return null;
        var names = new Stack<string>();
        while (node is PropertyAccessExpressionNode { Name: IdentifierNode name } property)
        {
            names.Push(name.Text);
            node = property.Expression!;
        }
        names.Push(((IdentifierNode)node).Text);
        return string.Join('.', names);
    }
}
