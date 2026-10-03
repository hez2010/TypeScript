using System.Numerics;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    private sealed partial class CompletionQuery
    {
        private readonly List<LiteralType> literals = [];
        private Symbol? recommended;

        private async ValueTask ContextualCompletionsAsync()
        {
            if (previous is null || (await ContextualTypeAsync(previous) ?? await TypeArgumentPropertyConstraintAsync(previous)) is not { } contextual) return;
            foreach (var type in contextual is UnionType union ? union.Types : [contextual])
            {
                if (!jsxIdentifier && previous is not (StringLiteralNode or NoSubstitutionTemplateLiteralNode) && type is LiteralType literal
                    && (type.Flags & TypeFlags.EnumLiteral) == 0 && (type.Flags & (TypeFlags.StringLiteral | TypeFlags.NumberLiteral | TypeFlags.BigIntLiteral)) != 0) literals.Add(literal);
                if (recommended is null && type.Symbol is { } symbol && (symbol.Flags & (SymbolFlags.EnumMember | SymbolFlags.Enum | SymbolFlags.Class)) != 0
                    && !((symbol.Flags & SymbolFlags.Class) != 0 && symbol.Declarations.Any(declaration => SemanticSyntax.ClassLike(declaration) && SemanticSyntax.HasModifier(declaration, K.AbstractKeyword))))
                    recommended = await FirstSymbolInChainAsync(symbol, previous);
            }
        }

        private async ValueTask FilterCaseValuesAsync()
        {
            var clause = Ancestor(contextToken, node => node.Kind == K.CaseClause) as CaseOrDefaultClauseNode;
            if (clause is null || contextToken!.Kind != K.CaseKeyword && (clause.Expression is null || Ancestor(contextToken, node => node == clause.Expression) is null)) return;
            var values = await CaseValuesAsync((CaseBlockNode)clause.Parent!);
            literals.RemoveAll(literal => literal.Value is not null && values.Contains(literal.Value));
            for (int i = symbols.Count - 1; i >= 0; i--)
                if (symbols[i].Symbol.ValueDeclaration is EnumMemberNode member && await checker.GetConstantValueForEmitAsync(member, cancellation) is { } value && values.Contains(value)) symbols.RemoveAt(i);
        }

        private async ValueTask<HashSet<object>> CaseValuesAsync(CaseBlockNode block)
        {
            HashSet<object> values = [];
            foreach (var clause in (block.Clauses ?? new([])).OfType<CaseOrDefaultClauseNode>())
            {
                if (clause.Kind != K.CaseClause || clause.Expression is not { } expression) continue;
                while (expression is ParenthesizedExpressionNode parentheses) expression = parentheses.Expression!;
                object? value = expression switch
                {
                    StringLiteralNode text => text.Text, NoSubstitutionTemplateLiteralNode text => text.Text,
                    NumericLiteralNode number => JsNumber.FromString(number.Text), BigIntLiteralNode integer => ExpressionTypes.BigInt(integer.Text), _ => null,
                };
                if (value is null && await checker.GetSymbolAtLocationAsync(clause.Expression, cancellation) is { ValueDeclaration: EnumMemberNode member })
                    value = await checker.GetConstantValueForEmitAsync(member, cancellation);
                if (value is not null) values.Add(value);
            }
            return values;
        }

        private async ValueTask<Symbol?> FirstSymbolInChainAsync(Symbol symbol, SyntaxNode enclosing)
        {
            HashSet<Symbol> seen = [];
            while (seen.Add(symbol))
            {
                if (await checker.GetAccessibleSymbolChainAsync(symbol, enclosing, SymbolFlags.All, cancellation: cancellation) is { Count: > 0 } chain) return chain[0];
                if (symbol.Parent is not { } parent) return null;
                if (parent.Declarations.Any(declaration => declaration is SourceFileNode)) return symbol;
                symbol = parent;
            }
            return null;
        }

        private Utf8String LiteralName(LiteralType literal) => literal.Value switch
        {
            Utf8String text => Quote(text), double number => double.IsFinite(number) ? TokenFacts.NumberText(number) : "null"u8,
            BigInteger integer => Utf8String.Format(integer) + "n"u8, _ => throw new InvalidOperationException("Unsupported completion literal"),
        };

        private async ValueTask<Type?> ContextualTypeAsync(SyntaxNode token)
        {
            var parent = token.Parent;
            switch (token.Kind)
            {
                case K.Identifier: return await ContextualFromParentAsync(token);
                case K.EqualsToken:
                    return parent switch
                    {
                        VariableDeclarationNode { Initializer: { } initializer } => await checker.GetContextualTypeAsync(initializer, cancellation: cancellation),
                        BinaryExpressionNode { Left: { } operand } => await checker.GetTypeAtLocationAsync(operand, cancellation),
                        JsxAttributeNode => await checker.GetContextualTypeForJsxAttributeAsync(parent, cancellation), _ => null,
                    };
                case K.NewKeyword: return parent is null ? null : await checker.GetContextualTypeAsync(parent, cancellation: cancellation);
                case K.CaseKeyword: return parent is CaseOrDefaultClauseNode clause ? await SwitchedTypeAsync(clause) : null;
                case K.OpenBraceToken:
                    return parent is JsxExpressionNode { Parent: not (JsxElementNode or JsxFragmentNode) } jsx && jsx.Parent is not null
                        ? await checker.GetContextualTypeForJsxAttributeAsync(jsx.Parent, cancellation) : null;
                case K.OpenBracketToken:
                    return parent is ArrayLiteralExpressionNode array ? await ArrayContextAsync(array) : null;
                case K.CloseBracketToken: return null;
                case K.QuestionToken: return parent is ConditionalExpressionNode ? await ConditionalContextAsync(parent) : null;
                case K.ColonToken when parent is ConditionalExpressionNode: return await ConditionalContextAsync(parent);
                case K.CommaToken when parent is ArrayLiteralExpressionNode elements: return await ArrayContextAsync(elements);
            }
            if (await CompletionArgumentAsync(token) is { } argument)
                return await checker.GetContextualTypeForArgumentAtIndexAsync(argument.Invocation, argument.Index, cancellation);
            if (Equality(token.Kind) && parent is BinaryExpressionNode { Left: { } left, OperatorToken: { } op } && Equality(op.Kind))
                return await checker.GetTypeAtLocationAsync(left, cancellation);
            return await checker.GetContextualTypeAsync(token, ContextFlags.IgnoreNodeInferences, cancellation)
                ?? await checker.GetContextualTypeAsync(token, cancellation: cancellation);
        }

        private async ValueTask<Type?> ContextualFromParentAsync(SyntaxNode node, ContextFlags flags = 0)
        {
            var parent = node.Parent;
            while (parent is ParenthesizedExpressionNode) parent = parent.Parent;
            return parent switch
            {
                NewExpressionNode => await checker.GetContextualTypeAsync(parent, flags, cancellation),
                BinaryExpressionNode { OperatorToken: { } op } binary when Equality(op.Kind) => await checker.GetTypeAtLocationAsync(node == binary.Right ? binary.Left! : binary.Right!, cancellation),
                CaseOrDefaultClauseNode clause => await SwitchedTypeAsync(clause),
                _ => await checker.GetContextualTypeAsync(node, flags, cancellation),
            };
        }

        private ValueTask<Type> SwitchedTypeAsync(CaseOrDefaultClauseNode clause) => checker.GetTypeAtLocationAsync(((SwitchStatementNode)clause.Parent!.Parent!).Expression!, cancellation);
        private async ValueTask<Type?> ArrayContextAsync(ArrayLiteralExpressionNode array)
            => await checker.GetContextualTypeForArrayLiteralAtPositionAsync(await checker.GetContextualTypeAsync(array, cancellation: cancellation), array, position, cancellation);
        private async ValueTask<Type?> ConditionalContextAsync(SyntaxNode node)
            => await CompletionArgumentAsync(node) is { } argument ? await checker.GetContextualTypeForArgumentAtIndexAsync(argument.Invocation, argument.Index, cancellation)
                : await checker.GetContextualTypeAsync(node, ContextFlags.IgnoreNodeInferences, cancellation) ?? await checker.GetContextualTypeAsync(node, cancellation: cancellation);
        private ValueTask<(SyntaxNode Invocation, int Index, int Count)?> CompletionArgumentAsync(SyntaxNode node)
            => new SignatureHelpQuery(checker, program, File, new(), cancellation, MapDocumentation).CompletionArgumentAsync(node, position);
        private static bool Equality(K token) => token is K.EqualsEqualsToken or K.EqualsEqualsEqualsToken or K.ExclamationEqualsToken or K.ExclamationEqualsEqualsToken;
    }
}
