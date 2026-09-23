using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Checking;

internal interface IExpressionCheckHost
{
    ValueTask<bool> UndefinedIdentifierAsync(IdentifierNode node, CancellationToken cancellation);

    void ExpressionError(SyntaxNode node, int code);
}

internal sealed class ExpressionChecks(TypeContext context, TypeFactQueries facts, IExpressionCheckHost host)
{
    internal async ValueTask<Type> NonNullAsync(Type type, SyntaxNode node, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        string? name = EntityText(node);
        if (context.StrictNullChecks && (type.Flags & TypeFlags.Unknown) != 0)
        {
            host.ExpressionError(node, name is not null && Wtf8.Encode(name).Length < 100 ? 18046 : 2571);
            return context.ErrorType;
        }
        var nullable = await facts.GetAsync(type, TypeFacts.IsUndefinedOrNull, cancellation).ConfigureAwait(false);
        if ((nullable & TypeFacts.IsUndefinedOrNull) == 0)
            return type;
        bool undefined = (nullable & TypeFacts.IsUndefined) != 0, nullValue = (nullable & TypeFacts.IsNull) != 0;
        int code = node.Kind == SyntaxKind.NullKeyword || node is IdentifierNode { Text: "undefined" } ? 18050
            : name is { Length: > 0 } && Wtf8.Encode(name).Length < 100 ? undefined ? nullValue ? 18049 : 18048 : 18047
            : undefined ? nullValue ? 2533 : 2532 : 2531;
        host.ExpressionError(node, code);
        var result = await facts.NonNullableAsync(type, cancellation).ConfigureAwait(false);
        return (result.Flags & (TypeFlags.Nullable | TypeFlags.Never)) != 0 ? context.ErrorType : result;
    }

    internal async ValueTask TruthinessAsync(Type type, SyntaxNode node, CancellationToken cancellation = default)
    {
        if ((type.Flags & TypeFlags.Void) != 0)
        {
            host.ExpressionError(node, 1345);
            return;
        }
        int semantics = await SyntacticTruthinessAsync(node, cancellation).ConfigureAwait(false);
        if (semantics != 3)
            host.ExpressionError(node, semantics == 1 ? 2872 : 2873);
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

    private static string? EntityText(SyntaxNode node)
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
