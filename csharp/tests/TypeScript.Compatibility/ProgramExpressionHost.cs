using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal sealed partial class ProgramTypeHost : IExpressionTypeHost, IExpressionCheckHost
{
    internal ExpressionTypes Expressions { get; }
    internal Action? BeforeExpressionFinish { get; set; }
    internal ExpressionChecks ExpressionChecks { get; }
    internal TypePredicates Predicates { get; }
    internal HashSet<SyntaxNode> DeferredExpressions { get; } = [];
    internal List<int> Suggestions { get; } = [];
    private readonly HashSet<(SyntaxNode, int)> suggestionLocations = [];

    public void ExpressionError(SyntaxNode node, int code) => Error(node, code);

    public void DeferExpression(SyntaxNode node) => DeferredExpressions.Add(node);

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

    public ValueTask<Type> FinishExpressionAsync(SyntaxNode node, Type type, CheckMode mode, CancellationToken cancellation)
    {
        BeforeExpressionFinish?.Invoke();
        if ((type.ObjectFlags & ObjectFlags.Anonymous) != 0 && type.Symbol is { Flags: var flags } && (flags & SymbolFlags.ConstEnum) != 0)
            throw new InvalidOperationException("Probe requires const enum access checks");
        return GenericExpressions.FinishAsync(node, type, mode, cancellation);
    }

    public async ValueTask<Type> OtherExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation)
    {
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
        if (node is AwaitExpressionNode awaitExpression)
            return await AwaitExpressions.CheckAsync(awaitExpression, cancellation);
        if (node is AsExpressionNode or TypeAssertionNode && SemanticSyntax.ConstAssertion(node))
        {
            var expression = node is AsExpressionNode assertion ? assertion.Expression! : ((TypeAssertionNode)node).Expression!;
            var type = await Expressions.CheckAsync(expression, mode, cancellation);
            if (!await ConstArgumentAsync(expression, cancellation))
                Error(expression, 1355);
            return await Algebra.RegularTypeAsync(type, cancellation);
        }
        if (node is ArrayLiteralExpressionNode array)
            return await ArrayLiterals.CheckAsync(array, mode, cancellation);
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
        throw new InvalidOperationException($"Probe requires expression checking for {node.Kind}");
    }

    public async ValueTask KnownTruthyAsync(Type type, SyntaxNode node, SyntaxNode? body, CancellationToken cancellation)
    {
        if (!context.StrictNullChecks)
            return;
        if ((await Facts.GetAsync(type, TypeFacts.Truthy, cancellation)) == 0)
            return;
        var calls = await SignaturesAsync(type, false, cancellation);
        await program.Globals.GetAsync("Promise", 1, false, cancellation);
        var constraint = await Instantiation.Constraints.BaseConstraintOrTypeAsync(type, cancellation);
        if (calls.Count == 0 && (constraint is UnionType union ? union.Types.All(
            t => (t.Flags & (TypeFlags.Primitive | TypeFlags.Never)) != 0)
            : (constraint.Flags & (TypeFlags.Primitive | TypeFlags.Never)) != 0))
            return;
        throw new InvalidOperationException("Probe requires callable/awaitable condition analysis");
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
