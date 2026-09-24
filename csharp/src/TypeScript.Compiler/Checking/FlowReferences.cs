using System.Globalization;
using System.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IFlowReferenceHost
{
    Symbol ResolveReference(SyntaxNode node, CancellationToken cancellation);

    Symbol? DeclarationSymbol(SyntaxNode node);

    Symbol? ExportedSymbol(Symbol symbol);

    Symbol UnknownSymbol { get; }

    ValueTask<string?> AccessNameAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<bool> ConstantOrUnassignedAsync(Symbol symbol, CancellationToken cancellation);
}

internal sealed class FlowReferences(IFlowReferenceHost host)
{
    private readonly Dictionary<SyntaxNode, int> nodeIds = [];

    internal async ValueTask<bool> MatchesAsync(SyntaxNode source, SyntaxNode target, CancellationToken cancellation = default)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (target is ParenthesizedExpressionNode or NonNullExpressionNode)
            {
                target = Receiver(target)!;
                continue;
            }
            if (target is BinaryExpressionNode binaryTarget)
            {
                if (BinaryExpressions.Assignment(binaryTarget.OperatorToken!.Kind))
                    target = binaryTarget.Left!;
                else if (binaryTarget.OperatorToken.Kind == SyntaxKind.CommaToken)
                    target = binaryTarget.Right!;
                else
                    return false;
                continue;
            }
            switch (source)
            {
                case MetaPropertyNode meta:
                    return target is MetaPropertyNode other && meta.KeywordToken == other.KeywordToken
                        && SyntaxNameText.Get(meta.Name) == SyntaxNameText.Get(other.Name);
                case IdentifierNode or PrivateIdentifierNode:
                    if (ThisInQuery(source))
                        return target.Kind == SyntaxKind.ThisKeyword;
                    return target is IdentifierNode
                        && host.ResolveReference(source, cancellation) == host.ResolveReference(target, cancellation)
                        || target is VariableDeclarationNode or BindingElementNode
                            && host.ExportedSymbol(host.ResolveReference(source, cancellation)) == host.DeclarationSymbol(target);
                case { Kind: SyntaxKind.ThisKeyword or SyntaxKind.SuperKeyword }:
                    return target.Kind == source.Kind;
                case ParenthesizedExpressionNode or NonNullExpressionNode or SatisfiesExpressionNode:
                    source = Receiver(source)!;
                    continue;
                case PropertyAccessExpressionNode or ElementAccessExpressionNode:
                    var sourceName = await host.AccessNameAsync(source, cancellation).ConfigureAwait(false);
                    if (sourceName is not null && target is PropertyAccessExpressionNode or ElementAccessExpressionNode
                        && await host.AccessNameAsync(target, cancellation).ConfigureAwait(false) is { } targetName)
                    {
                        if (sourceName != targetName)
                            return false;
                        source = Receiver(source)!;
                        target = Receiver(target)!;
                        continue;
                    }
                    if (source is ElementAccessExpressionNode { ArgumentExpression: IdentifierNode sourceArg }
                        && target is ElementAccessExpressionNode { ArgumentExpression: IdentifierNode targetArg })
                    {
                        var symbol = host.ResolveReference(sourceArg, cancellation);
                        if (symbol == host.ResolveReference(targetArg, cancellation)
                            && await host.ConstantOrUnassignedAsync(symbol, cancellation).ConfigureAwait(false))
                        {
                            source = Receiver(source)!;
                            target = Receiver(target)!;
                            continue;
                        }
                    }
                    return false;
                case QualifiedNameNode qualified:
                    if (target is PropertyAccessExpressionNode or ElementAccessExpressionNode
                        && await host.AccessNameAsync(target, cancellation).ConfigureAwait(false) is { } name
                        && ((IdentifierNode)qualified.Right!).Text == name)
                    {
                        source = qualified.Left!;
                        target = Receiver(target)!;
                        continue;
                    }
                    return false;
                case BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.CommaToken } comma:
                    source = comma.Right!;
                    continue;
                default:
                    return false;
            }
        }
    }

    internal async ValueTask<bool> ContainsAsync(
        SyntaxNode source,
        SyntaxNode target,
        bool optionalChain,
        CancellationToken cancellation = default)
    {
        while (optionalChain
            ? (source.Flags & NodeFlags.OptionalChain) != 0
            : source is PropertyAccessExpressionNode or ElementAccessExpressionNode)
        {
            source = Receiver(source)!;
            if (await MatchesAsync(source, target, cancellation).ConfigureAwait(false))
                return true;
        }
        return false;
    }

    internal async ValueTask<string?> KeyAsync(FlowState state, CancellationToken cancellation = default)
    {
        List<string> segments = [];
        var node = state.Reference;
        string root;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            switch (node)
            {
                case IdentifierNode:
                    var symbol = ThisInQuery(node) ? null : host.ResolveReference(node, cancellation);
                    if (symbol == host.UnknownSymbol)
                        return null;
                    root = RootKey(symbol);
                    break;
                case { Kind: SyntaxKind.ThisKeyword }:
                    root = RootKey(null);
                    break;
                case ParenthesizedExpressionNode or NonNullExpressionNode:
                    node = Receiver(node)!;
                    continue;
                case QualifiedNameNode qualified:
                    segments.Add(Property(((IdentifierNode)qualified.Right!).Text));
                    node = qualified.Left!;
                    continue;
                case PropertyAccessExpressionNode or ElementAccessExpressionNode:
                    if (await host.AccessNameAsync(node, cancellation).ConfigureAwait(false) is { } name)
                        segments.Add(Property(name));
                    else if (node is ElementAccessExpressionNode { ArgumentExpression: IdentifierNode argument })
                    {
                        var indexSymbol = host.ResolveReference(argument, cancellation);
                        if (!await host.ConstantOrUnassignedAsync(indexSymbol, cancellation).ConfigureAwait(false))
                            return null;
                        segments.Add(".@" + indexSymbol.Id.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                        return null;
                    node = Receiver(node)!;
                    continue;
                case BindingPatternNode or FunctionDeclarationNode or FunctionExpressionNode or ArrowFunctionNode or MethodDeclarationNode:
                    root = "n" + NodeId(node) + "#" + state.Declared.Id.ToString(CultureInfo.InvariantCulture);
                    break;
                default:
                    return null;
            }
            break;
        }
        var result = new StringBuilder(root);
        for (int i = segments.Count - 1; i >= 0; i--)
            result.Append(segments[i]);
        return result.ToString();

        string RootKey(Symbol? symbol) => (symbol is null ? "" : "s" + symbol.Id.ToString(CultureInfo.InvariantCulture))
            + ":" + state.Declared.Id.ToString(CultureInfo.InvariantCulture)
            + (state.Initial == state.Declared ? "" : "=" + state.Initial.Id.ToString(CultureInfo.InvariantCulture))
            + (state.Container is null ? "" : "@" + NodeId(state.Container));
    }

    private string NodeId(SyntaxNode node)
    {
        if (!nodeIds.TryGetValue(node, out int id))
            nodeIds.Add(node, id = nodeIds.Count + 1);
        return id.ToString(CultureInfo.InvariantCulture);
    }

    private static string Property(string name) => "." + name.Length.ToString(CultureInfo.InvariantCulture) + ":" + name;

    internal static bool ThisInQuery(SyntaxNode node)
    {
        if (node is not IdentifierNode { Text: "this" })
            return false;
        while (node.Parent is QualifiedNameNode qualified && qualified.Left == node)
            node = qualified;
        return node.Parent is TypeQueryNode;
    }

    internal static SyntaxNode Candidate(SyntaxNode node)
    {
        while (true)
        {
            switch (node)
            {
                case ParenthesizedExpressionNode parentheses:
                    node = parentheses.Expression!;
                    continue;
                case BinaryExpressionNode
                {
                    OperatorToken.Kind: SyntaxKind.EqualsToken or SyntaxKind.BarBarEqualsToken
                    or SyntaxKind.AmpersandAmpersandEqualsToken or SyntaxKind.QuestionQuestionEqualsToken
                } assignment:
                    node = assignment.Left!;
                    continue;
                case BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.CommaToken } comma:
                    node = comma.Right!;
                    continue;
                default:
                    return node;
            }
        }
    }

    internal static SyntaxNode Root(SyntaxNode node)
    {
        while (node.Parent is ParenthesizedExpressionNode
            || node.Parent is BinaryExpressionNode binary && (binary.OperatorToken!.Kind == SyntaxKind.EqualsToken && binary.Left == node
                || binary.OperatorToken.Kind == SyntaxKind.CommaToken && binary.Right == node))
            node = node.Parent;
        return node;
    }

    internal static SyntaxNode? Receiver(SyntaxNode node) => node switch
    {
        ParenthesizedExpressionNode n => n.Expression,
        NonNullExpressionNode n => n.Expression,
        SatisfiesExpressionNode n => n.Expression,
        PropertyAccessExpressionNode n => n.Expression,
        ElementAccessExpressionNode n => n.Expression,
        CallExpressionNode n => n.Expression,
        _ => null
    };
}
