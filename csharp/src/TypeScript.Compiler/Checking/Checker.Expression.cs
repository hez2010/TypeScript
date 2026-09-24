using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : IExpressionTypeHost, IExpressionCheckHost
{
    internal ExpressionTypes Expressions { get; }
    internal Action? BeforeExpressionFinish { get; set; }
    internal ExpressionChecks ExpressionChecks { get; }
    internal TypePredicates Predicates { get; }
    internal HashSet<SyntaxNode> DeferredExpressions { get; } = [];
    internal List<int> Suggestions { get; } = [];
    private readonly HashSet<(SyntaxNode, int)> suggestionLocations = [];

    public void ExpressionError(SyntaxNode node, int code) => Error(node, code);

    public void DeferExpression(SyntaxNode node)
    {
        var file = SemanticSyntax.Source(node);
        if (file is not null && checkedFiles.Contains(file) || !DeferredExpressions.Add(node))
            return;
        if (file is not null)
        {
            if (!deferredSourceNodes.TryGetValue(file, out var nodes))
                deferredSourceNodes.Add(file, nodes = []);
            nodes.Add(node);
        }
    }

    public ValueTask<Type> NonNullAsync(Type type, SyntaxNode node, CancellationToken cancellation) =>
        ExpressionChecks.NonNullAsync(type, node, cancellation);

    public ValueTask TruthinessAsync(Type type, SyntaxNode node, CancellationToken cancellation) =>
        ExpressionChecks.TruthinessAsync(type, node, cancellation);

    public ValueTask<bool> MaybeKindAsync(Type type, TypeFlags flags, bool baseConstraint, CancellationToken cancellation) =>
        Predicates.MaybeAsync(type, flags, baseConstraint, cancellation);

    public ValueTask<bool> AssignableKindAsync(Type type, TypeFlags flags, CancellationToken cancellation) =>
        Predicates.AssignableAsync(type, flags, cancellation: cancellation);

    public async ValueTask<bool> UndefinedIdentifierAsync(IdentifierNode node, CancellationToken cancellation)
            =>
                await program.EntityNames.ResolveAsync(
                    node,
                    SymbolFlags.Value,
                    true,
                    cancellation: cancellation) == program.Symbols.UndefinedSymbol;

    public async ValueTask<Type> FinishExpressionAsync(SyntaxNode node, Type type, CheckMode mode, CancellationToken cancellation)
    {
        BeforeExpressionFinish?.Invoke();
        type = await GenericExpressions.FinishAsync(node, type, mode, cancellation);
        ValueExpressions.ConstEnum(node, type, cancellation);
        return type;
    }

    public async ValueTask<Type> OtherExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation)
    {
        if (node.Kind == SyntaxKind.MissingDeclaration)
            return context.ErrorType;
        if (node is PrivateIdentifierNode)
        {
            var symbol = ResolveReference(node, cancellation);
            if (symbol != UnknownSymbol)
            {
                links.SymbolNodes.Get(node).ResolvedSymbol = symbol;
                MemberAccess.MarkReferenced(symbol, node, node, null, cancellation);
            }
            if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
            {
                if (PrivateAccess.ContainingClass(node) is null)
                    Error(node, 18016);
                else if (node.Parent?.Kind != SyntaxKind.ForInStatement
                    && !(node.Parent is BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.InKeyword } inExpression
                        && inExpression.Left == node))
                    Error(node, 1451);
            }
            return context.AnyType;
        }
        if (node is CallExpressionNode or NewExpressionNode or TaggedTemplateExpressionNode)
            return await Calls.CheckAsync(node, mode, cancellation);
        if (node is SyntheticExpressionNode synthetic)
        {
            var type = (Type)synthetic.Type!;
            context.RequireOwned(type);
            return type;
        }
        if (node is SpreadElementNode spread)
            return await SpreadElementAsync(
                await Expressions.CheckAsync(spread.Expression!, cancellation: cancellation),
                spread.Expression!,
                cancellation);
        if (node is FunctionExpressionNode or ArrowFunctionNode)
            return await Functions.CheckAsync(node, mode, cancellation);
        if (node is ClassExpressionNode)
        {
            await CheckClassSourceAsync(node, true, cancellation).ConfigureAwait(false);
            return await Values.GetAsync(program.Symbols.Declaration(node)!, cancellation);
        }
        if (node is AwaitExpressionNode awaitExpression)
            return await AwaitExpressions.CheckAsync(awaitExpression, cancellation);
        if (node is AsExpressionNode or TypeAssertionNode)
            return await Assertions.CheckAsync(node, mode, cancellation);
        if (node is SatisfiesExpressionNode satisfies)
            return await Assertions.SatisfiesAsync(satisfies, cancellation);
        if (node is ExpressionWithTypeArgumentsNode)
            return await InstantiationExpressions.CheckAsync(node, cancellation);
        if (node is DeleteExpressionNode delete)
            return await ValueExpressions.DeleteAsync(delete, cancellation);
        if (node is MetaPropertyNode meta)
            return await ValueExpressions.MetaAsync(meta, cancellation);
        if (node.Kind == SyntaxKind.RegularExpressionLiteral)
            return program.Globals.Types["RegExp"];
        if (node is ArrayLiteralExpressionNode array)
            return await ArrayLiterals.CheckAsync(array, mode, cancellation);
        if (node is YieldExpressionNode yield)
            return await Yields.CheckAsync(yield, cancellation);
        if (node is ObjectLiteralExpressionNode literal)
            return await ObjectLiterals.CheckAsync(literal, mode, cancellation);
        if (node.Kind == SyntaxKind.ThisKeyword)
            return await ThisExpressions.ThisAsync(node, cancellation);
        if (node.Kind == SyntaxKind.SuperKeyword)
            return await ThisExpressions.SuperAsync(node, cancellation);
        if (node is BinaryExpressionNode binary)
            return await Binary.CheckAsync(binary, mode, cancellation);
        if (node is IdentifierNode identifier)
            return await Identifiers.CheckAsync(identifier, mode, cancellation);
        if (node is PropertyAccessExpressionNode property)
            return await Access.PropertyAsync(property, mode, cancellation: cancellation);
        if (node is ElementAccessExpressionNode element)
            return await Access.ElementAsync(element, mode, cancellation);
        if (node is QualifiedNameNode qualified)
            return await Access.QualifiedAsync(qualified, mode, cancellation);
        if (node is NonNullExpressionNode nonNull)
            return await Access.NonNullChainAsync(nonNull, Facts, cancellation);
        throw new InvalidOperationException($"Checker requires expression checking for {node.Kind}");
    }

    public async ValueTask KnownTruthyAsync(Type type, SyntaxNode node, SyntaxNode? body, CancellationToken cancellation)
    {
        if (!context.StrictNullChecks)
            return;
        await CheckKnownConditionAsync(type, node, body, cancellation).ConfigureAwait(false);
    }

    public async ValueTask<bool> TemplateContextAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (await Contexts.ConstAsync(node, cancellation))
            return true;
        while (node.Parent is ParenthesizedExpressionNode parentheses)
            node = parentheses;
        if (node.Parent is ElementAccessExpressionNode access && access.ArgumentExpression == node)
            return true;
        if (await Contexts.GetAsync(node, cancellation: cancellation) is { } type)
        {
            foreach (var part in type is UnionType union ? union.Types : (IReadOnlyList<Type>)[type])
                if ((part.Flags & (TypeFlags.StringLiteral | TypeFlags.TemplateLiteral)) != 0
                    || (part.Flags & TypeFlags.InstantiableNonPrimitive) != 0
                        && Predicates.Maybe(
                            await Instantiation.Constraints.BaseConstraintAsync(part, cancellation) ?? context.UnknownType,
                            TypeFlags.StringLike,
                            cancellation))
                    return true;
            return false;
        }
        return false;
    }

    public async ValueTask CheckIncrementAsync(SyntaxNode operand, Type type, CancellationToken cancellation)
    {
        if (!await AssignableAsync(type, context.NumberOrBigIntType, cancellation))
        {
            Error(operand, 2356);
            return;
        }
        AssignmentChecks.Reference(operand, 2357, 2777);
    }

    public void LiteralGrammar(SyntaxNode node)
    {
        var file = SemanticSyntax.Source(node);
        if (file is null)
            return;
        if (node is NumericLiteralNode number)
        {
            var scanner = new Scanner(file.Source);
            scanner.ResetPosition(node.Pos);
            scanner.Scan();
            if (!scanner.TokenText.Contains('.') && (number.TokenFlags & TokenFlags.Scientific) == 0
                && JsNumber.FromString(number.Text) > JsNumber.MaxSafeInteger && suggestionLocations.Add((node, 80008)))
                Suggestions.Add(80008);
        }
        else if (node is BigIntLiteralNode && node.Parent is not LiteralTypeNode
            && !(node.Parent is PrefixUnaryExpressionNode { Parent: LiteralTypeNode }) && (node.Flags & NodeFlags.Ambient) == 0
            && program.Symbols.Program.Configuration.Options.EmitTargetYear < 2020 && file.ParseDiagnostics.Count == 0)
            Error(node, 2737);
    }
}
