using System.Numerics;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    private sealed partial class CompletionQuery
    {
        private async ValueTask<CompletionItem?> SwitchSnippetAsync()
        {
            if (jsxOpen || contextToken?.Kind is K.DotToken or K.QuestionDotToken
                || Ancestor(contextToken, node => node is CaseBlockNode) is not CaseBlockNode block
                || await checker.GetTypeAtLocationAsync(((SwitchStatementNode)block.Parent!).Expression!, cancellation) is not UnionType union
                || union.Types.Any(type => (type.Flags & (TypeFlags.StringLiteral | TypeFlags.NumberLiteral | TypeFlags.BigIntLiteral)) == 0)) return null;
            var values = await CaseValuesAsync(block);
            var printer = new SnippetPrinter(preferences.FormatCodeSettings, options.EmitTargetYear);
            var factory = printer.Factory;
            var imports = CreateImportAdder();
            List<SyntaxNode> expressions = [];
            foreach (var type in union.Types)
            {
                cancellation.ThrowIfCancellationRequested();
                SyntaxNode? expression;
                if ((type.Flags & TypeFlags.EnumLiteral) != 0)
                {
                    if (type.Symbol?.ValueDeclaration is { } declaration && await checker.GetConstantValueForEmitAsync(declaration, cancellation) is { } value
                        && !values.Add(value)) continue;
                    Dictionary<SyntaxNode, Symbol> symbols = [];
                    if (await checker.TypeToTypeNodeAsync(type, block, NodeBuilderFlags.None, cancellation, identifierSymbols: symbols) is not { } node) return null;
                    node = imports is null ? ImportTypeRewriter.Rewrite(node, symbols, factory, cancellation).Node : (await imports.RewriteTypeAsync(node, symbols, factory))!;
                    if (TypeToExpression(node, factory) is not { } converted) return null;
                    expression = converted;
                }
                else
                {
                    if (type is not LiteralType literal || values.Contains(literal.Value!)) continue;
                    expression = literal.Value switch
                    {
                        Utf8String text => factory.NewStringLiteral(text, RenameQuote(File, preferences) == "'"u8 ? TokenFlags.SingleQuote : 0),
                        double number => number < 0 ? factory.NewPrefixUnaryExpression(K.MinusToken, factory.NewNumericLiteral(TokenFacts.NumberText(-number), 0))
                            : factory.NewNumericLiteral(TokenFacts.NumberText(number), 0),
                        BigInteger integer => integer.Sign < 0 ? factory.NewPrefixUnaryExpression(K.MinusToken, factory.NewBigIntLiteral(Utf8String.Format(-integer) + "n"u8, 0))
                            : factory.NewBigIntLiteral(Utf8String.Format(integer) + "n"u8, 0),
                        _ => null,
                    };
                }
                if (expression is not null) expressions.Add(expression);
            }
            if (expressions.Count == 0) return null;
            var plain = new SyntaxPrinter(new() { RemoveComments = true, NewLine = preferences.FormatCodeSettings.NewLineCharacter }, printer.Context);
            var result = new Utf8StringBuilder();
            Utf8String label = default;
            for (int i = 0; i < expressions.Count; i++)
            {
                var clause = factory.NewCaseOrDefaultClause(K.CaseClause, expressions[i], new([]));
                if (i == 0) label = plain.Print(clause, cancellation: cancellation) + " ..."u8;
                else result.Append(preferences.FormatCodeSettings.NewLineCharacter);
                result.Append(capabilities.Snippets ? await printer.PrintAsync(clause, File, cancellation) : plain.Print(clause, cancellation: cancellation));
                if (capabilities.Snippets) { result.Append("$"u8); result.Append(Utf8String.Format(i + 1)); }
            }
            var edits = imports is null ? [] : await imports.EditsAsync();
            return new(label, 15, SortText: "15"u8, InsertText: result.ToUtf8String(), InsertTextFormat: capabilities.Snippets ? 2 : null,
                Data: new(projection.OriginalFileName, position, label, supplementalIndex, "SwitchCases/"u8), AdditionalTextEdits: edits.Length == 0 ? null : edits);
        }

        private SyntaxNode? TypeToExpression(SyntaxNode type, NodeFactory factory)
        {
            Dictionary<SyntaxNode, SyntaxNode?> converted = [];
            Stack<(SyntaxNode Node, bool Finish)> pending = []; pending.Push((type, false));
            while (pending.TryPop(out var item))
            {
                cancellation.ThrowIfCancellationRequested();
                var node = item.Node;
                if (!item.Finish)
                {
                    pending.Push((node, true));
                    switch (node)
                    {
                        case TypeReferenceNode reference: pending.Push((reference.TypeName!, false)); break;
                        case TypeQueryNode query: pending.Push((query.ExprName!, false)); break;
                        case QualifiedNameNode qualified: pending.Push((qualified.Left!, false)); break;
                        case IndexedAccessTypeNode indexed: pending.Push((indexed.ObjectType!, false)); pending.Push((indexed.IndexType!, false)); break;
                        case ParenthesizedTypeNode parentheses: pending.Push((parentheses.Type!, false)); break;
                    }
                    continue;
                }
                converted[node] = node switch
                {
                    IdentifierNode => node,
                    TypeReferenceNode reference => converted[reference.TypeName!],
                    TypeQueryNode query => converted[query.ExprName!],
                    QualifiedNameNode qualified when converted[qualified.Left!] is { } left => factory.NewPropertyAccessExpression(left, null, qualified.Right, 0),
                    IndexedAccessTypeNode indexed when converted[indexed.ObjectType!] is { } owner && converted[indexed.IndexType!] is { } index
                        => factory.NewElementAccessExpression(owner, null, index, 0),
                    ParenthesizedTypeNode parentheses when converted[parentheses.Type!] is { } expression
                        => expression is IdentifierNode ? expression : factory.NewParenthesizedExpression(expression),
                    LiteralTypeNode { Literal: StringLiteralNode literal } => factory.NewStringLiteral(literal.Text, RenameQuote(File, preferences) == "'"u8 ? TokenFlags.SingleQuote : 0),
                    LiteralTypeNode { Literal: NumericLiteralNode literal } => factory.NewNumericLiteral(literal.Text, literal.TokenFlags),
                    _ => null,
                };
            }
            return converted[type];
        }
    }
}
