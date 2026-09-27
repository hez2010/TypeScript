using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
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

    public void DuplicateObjectProperty(SyntaxNode node, string name) => Error(node, 1117, CheckerDiagnostic.DeclarationName(node));

    public void ExpressionError(SyntaxNode node, int code)
    {
        if (code == 1294 && node is TypeAssertionNode assertion && SemanticSyntax.Source(node) is { } assertionFile)
        {
            int start = CheckerDiagnostic.TokenRange(assertionFile, node.Pos).Start;
            Error(node, CheckerDiagnostic.Create(node, Messages.This_syntax_is_not_allowed_when_erasableSyntaxOnly_is_enabled)
                with
            { Start = start, Length = assertion.Expression!.Pos - start });
            return;
        }
        if (code == 1308 && SemanticSyntax.Source(node) is { } file)
        {
            var (start, end) = CheckerDiagnostic.TokenRange(file, node.Pos);
            var diagnostic = CheckerDiagnostic.Create(
                node,
                Messages.X_await_expressions_are_only_allowed_within_async_functions_and_at_the_top_levels_of_modules)
                with
            { Start = start, Length = end - start };
            var container = DeclarationOrder.Ancestor(node.Parent, n => n is IFunctionSignature);
            if (container is not null and not ConstructorDeclarationNode && !SemanticSyntax.HasModifier(container, SyntaxKind.AsyncKeyword))
                diagnostic = diagnostic with
                { RelatedInformation = [CheckerDiagnostic.Create(container, Messages.Did_you_mean_to_mark_this_function_as_async)] };
            Error(node, diagnostic);
            return;
        }
        if (code == 1098 && node is IFunctionSignature signature)
        {
            EmptyTypeListError(node, signature.TypeParameters, code);
            return;
        }
        string[] arguments = code switch
        {
            1042 => [TokenFacts.Text(node.Kind)!],
            2680 => [SyntaxNameText.Get(((ParameterDeclarationNode)node).Name!)],
            2716 => [SyntaxNameText.Get(((TypeParameterDeclarationNode)node.Parent!).Name!)],
            2368 => [CheckerDiagnostic.DeclarationName(node)],
            2469 when node.Parent is PrefixUnaryExpressionNode unary => [TokenFacts.Text(unary.Operator)!],
            2736 => ["+", "bigint"],
            17013 => ["new.target"],
            18061 when node is MetaPropertyNode meta => [CheckerDiagnostic.DeclarationName(meta.Name!)],
            5076 when node is BinaryExpressionNode mixed => node.Parent is BinaryExpressionNode parent && parent.Right == node
                ? ["??", TokenFacts.Text(mixed.OperatorToken!.Kind)!]
                : [TokenFacts.Text(mixed.OperatorToken!.Kind)!, "??"],
            17012 when node.Parent is MetaPropertyNode meta => [CheckerDiagnostic.DeclarationName(node),
                TokenFacts.Text(meta.KeywordToken)!, meta.KeywordToken == SyntaxKind.NewKeyword ? "target" : "meta"],
            2564 => [CheckerDiagnostic.DeclarationName(node)],
            18046 or 18047 or 18048 or 18049 => [ExpressionChecks.EntityText(node)!],
            18050 => [node.Kind == SyntaxKind.NullKeyword ? "null" : "undefined"],
            2748 => [IsolatedModuleOptionName],
            _ => []
        };
        Error(node, code, arguments);
    }

    public async ValueTask TypeExpressionErrorAsync(SyntaxNode node, int code, Type type, CancellationToken cancellation)
    {
        string display = await TypeDisplay.GetAsync(type, cancellation);
        Error(node, code, code == 2353 ? [CheckerDiagnostic.DeclarationName(node), display] : [display]);
    }

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
        if (node is JsxElementNode or JsxSelfClosingElementNode or JsxFragmentNode or JsxExpressionNode or JsxAttributesNode)
            return await CheckJsxAsync(node, mode, cancellation);
        if (node is CallExpressionNode importCall && IsImportCall(importCall))
            return await CheckImportCallAsync(importCall, cancellation);
        if (node.Kind == SyntaxKind.MissingDeclaration)
            return context.ErrorType;
        if (node is ComputedPropertyNameNode || node.Kind == SyntaxKind.ImportKeyword)
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
            return synthetic.IsSpread ? await Indexed.GetAsync(type, context.NumberType, cancellation: cancellation) : type;
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
        {
            CheckRegularExpression(node);
            return program.Globals.Types["RegExp"];
        }
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
            scanner.ResetPosition(file.Source.ToUtf16Position(node.Pos));
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
