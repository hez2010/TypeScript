using TypeScript.Compiler.Text;
using System.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal static Utf8String PrintDiagnosticNode(SyntaxNode node, bool neverAsciiEscape = false, CancellationToken cancellation = default,
        SourceFileNode? sourceFile = null, IReadOnlySet<SyntaxNode>? noAsciiEscape = null, IReadOnlySet<SyntaxNode>? singleLine = null,
        bool multiline = false) =>
        new SymbolNodePrinter(neverAsciiEscape, cancellation, sourceFile, noAsciiEscape, singleLine, multiline).Print(node);

    // Comment-free AST output used by symbol and type serialization.
    // The explicit work stack also handles input-shaped expression/function nesting.
    private sealed class SymbolNodePrinter(bool neverAsciiEscape, CancellationToken cancellation, SourceFileNode? sourceFile,
        IReadOnlySet<SyntaxNode>? noAsciiEscape, IReadOnlySet<SyntaxNode>? singleLine, bool multiline)
    {
        private readonly record struct Part(SyntaxNode? Node = null, Utf8String? Text = null, IReadOnlyList<Part>? Parts = null,
            int IndentationChange = 0, bool NewLine = false);

        private readonly Stack<Part> pending = [];
        private readonly Utf8StringBuilder output = new();
        private int indentation;

        internal Utf8String Print(SyntaxNode node)
        {
            pending.Push(N(node));
            while (pending.TryPop(out var part))
            {
                cancellation.ThrowIfCancellationRequested();
                indentation += part.IndentationChange;
                if (part.NewLine)
                    output.Append((byte)'\n').Append((byte)' ', indentation * 4);
                else if (part.Text is { } text)
                    output.Append(text.Span);
                else if (part.Parts is { } parts)
                    for (int i = parts.Count - 1; i >= 0; i--)
                        pending.Push(parts[i]);
                else if (part.Node is { } child)
                    Emit(child);
            }
            if (output.Length != 0 && output[^1] == ';')
                output.Length--;
            return Utf8String.FromBuilder(output);
        }

        private static Part N(SyntaxNode? node) => new(Node: node);

        private static Part T(Utf8String text) => new(Text: text);

        private static Part S(params ReadOnlySpan<Part> parts) => new(Parts: parts.ToArray());

        private void Push(params ReadOnlySpan<Part> parts)
        {
            for (int i = parts.Length - 1; i >= 0; i--)
                pending.Push(parts[i]);
        }

        private static Part List(IReadOnlyList<SyntaxNode>? nodes, Utf8String before, Utf8String separator, Utf8String after, bool required = false)
        {
            if (nodes is null && !required || nodes is { Count: 0 } && before == Utf8Literals.Space && after.Length == 0)
                return default;
            var parts = new List<Part> { T(before) };
            if (nodes is not null)
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (i != 0)
                        parts.Add(T(separator));
                    parts.Add(N(nodes[i]));
                }
            parts.Add(T(after));
            return new(Parts: parts);
        }

        private static Part Modifiers(SyntaxNode node) => node is IModifiedNode { Modifiers.Count: > 0 } modified
                    ? List(modified.Modifiers, Utf8String.Empty, Utf8Literals.Space, Utf8Literals.Space) : default;

        private static Part Annotation(SyntaxNode? type) => type is null ? default : S(T(Utf8Literals.ColonSpace), N(type));

        private static Part Initializer(SyntaxNode? value) => value is null ? default : S(T(Utf8Literals.AssignmentSeparator), N(value));

        private static Part Parameters(NodeList? nodes) => List(nodes, Utf8Literals.OpenParen, Utf8Literals.CommaSpace, Utf8Literals.CloseParen, true);

        private static Part TypeArguments(NodeList? nodes) => List(nodes, Utf8Literals.LessThan, Utf8Literals.CommaSpace, Utf8Literals.GreaterThan);

        private static Part Braces(NodeList? nodes, Utf8String separator, bool spaceWhenEmpty = false) => nodes is { Count: > 0 }
                    ? List(nodes, Utf8Literals.OpenBraceSpace, separator, Utf8Literals.SpaceCloseBrace) : T(spaceWhenEmpty ? Utf8Literals.SpacedBraces : Utf8Literals.EmptyBraces);

        private static Part Body(SyntaxNode? body) => body is null ? T(Utf8Literals.Semicolon) : S(T(Utf8Literals.Space), N(body));

        private Part TypeMembers(TypeLiteralNode node)
        {
            if (!multiline || singleLine?.Contains(node) == true || node.Members is not { Count: > 0 } members)
                return Braces(node.Members, Utf8Literals.Space);
            var parts = new List<Part> { T(Utf8Literals.OpenBrace), new(IndentationChange: 1) };
            foreach (var member in members)
            {
                parts.Add(new(NewLine: true));
                parts.Add(N(member));
            }
            parts.Add(new(IndentationChange: -1, NewLine: true));
            parts.Add(T(Utf8Literals.CloseBrace));
            return new(Parts: parts);
        }

        // Keep each emitter's span temporaries out of the dispatcher's stack frame.
        private void Emit(SyntaxNode node)
        {
            switch (node)
            {
                case IdentifierNode id:
                    Emit(id);
                    break;
                case PrivateIdentifierNode id:
                    Emit(id);
                    break;
                case StringLiteralNode literal:
                    Emit(literal);
                    break;
                case NumericLiteralNode literal:
                    Emit(literal);
                    break;
                case BigIntLiteralNode literal:
                    Emit(literal);
                    break;
                case RegularExpressionLiteralNode literal:
                    Emit(literal);
                    break;
                case NoSubstitutionTemplateLiteralNode literal:
                    Emit(literal);
                    break;
                case TemplateHeadNode literal:
                    Emit(literal);
                    break;
                case TemplateMiddleNode literal:
                    Emit(literal);
                    break;
                case TemplateTailNode literal:
                    Emit(literal);
                    break;
                case ComputedPropertyNameNode computed:
                    Emit(computed);
                    break;
                case QualifiedNameNode qualified:
                    Emit(qualified);
                    break;
                case MetaPropertyNode meta:
                    Emit(meta);
                    break;
                case PropertyAccessExpressionNode property:
                    Emit(property);
                    break;
                case ElementAccessExpressionNode element:
                    Emit(element);
                    break;
                case CallExpressionNode call:
                    Emit(call);
                    break;
                case NewExpressionNode call:
                    Emit(call);
                    break;
                case TaggedTemplateExpressionNode tagged:
                    Emit(tagged);
                    break;
                case TemplateExpressionNode template:
                    Emit(template);
                    break;
                case TemplateSpanNode span:
                    Emit(span);
                    break;
                case ParenthesizedExpressionNode paren:
                    Emit(paren);
                    break;
                case BinaryExpressionNode binary:
                    Emit(binary);
                    break;
                case ConditionalExpressionNode conditional:
                    Emit(conditional);
                    break;
                case PrefixUnaryExpressionNode unary:
                    Emit(unary);
                    break;
                case PostfixUnaryExpressionNode unary:
                    Emit(unary);
                    break;
                case DeleteExpressionNode unary:
                    Emit(unary);
                    break;
                case TypeOfExpressionNode unary:
                    Emit(unary);
                    break;
                case VoidExpressionNode unary:
                    Emit(unary);
                    break;
                case AwaitExpressionNode unary:
                    Emit(unary);
                    break;
                case NonNullExpressionNode unary:
                    Emit(unary);
                    break;
                case YieldExpressionNode yield:
                    Emit(yield);
                    break;
                case AsExpressionNode assertion:
                    Emit(assertion);
                    break;
                case SatisfiesExpressionNode assertion:
                    Emit(assertion);
                    break;
                case TypeAssertionNode assertion:
                    Emit(assertion);
                    break;
                case ExpressionWithTypeArgumentsNode instantiation:
                    Emit(instantiation);
                    break;
                case ArrayLiteralExpressionNode array:
                    Emit(array);
                    break;
                case OmittedExpressionNode:
                    break;
                case ObjectLiteralExpressionNode obj:
                    Emit(obj);
                    break;
                case PropertyAssignmentNode property:
                    Emit(property);
                    break;
                case ShorthandPropertyAssignmentNode shorthand:
                    Emit(shorthand);
                    break;
                case SpreadAssignmentNode spread:
                    Emit(spread);
                    break;
                case SpreadElementNode spread:
                    Emit(spread);
                    break;
                case ArrowFunctionNode arrow:
                    Emit(arrow);
                    break;
                case FunctionExpressionNode function:
                    Emit(function);
                    break;
                case FunctionDeclarationNode function:
                    Emit(function);
                    break;
                case MethodDeclarationNode method:
                    Emit(method);
                    break;
                case GetAccessorDeclarationNode accessor:
                    Emit(accessor);
                    break;
                case SetAccessorDeclarationNode accessor:
                    Emit(accessor);
                    break;
                case ConstructorDeclarationNode constructor:
                    Emit(constructor);
                    break;
                case ParameterDeclarationNode parameter:
                    Emit(parameter);
                    break;
                case PropertyDeclarationNode property:
                    Emit(property);
                    break;
                case ClassExpressionNode type:
                    Emit(type);
                    break;
                case ClassDeclarationNode type:
                    Emit(type);
                    break;
                case ClassStaticBlockDeclarationNode block:
                    Emit(block);
                    break;
                case HeritageClauseNode heritage:
                    Emit(heritage);
                    break;
                case BlockNode block:
                    Emit(block);
                    break;
                case ModuleBlockNode block:
                    Emit(block);
                    break;
                case SourceFileNode file:
                    Emit(file);
                    break;
                case ExpressionStatementNode statement:
                    Emit(statement);
                    break;
                case ReturnStatementNode statement:
                    Emit(statement);
                    break;
                case ThrowStatementNode statement:
                    Emit(statement);
                    break;
                case VariableStatementNode statement:
                    Emit(statement);
                    break;
                case VariableDeclarationListNode list:
                    Emit(list);
                    break;
                case VariableDeclarationNode variable:
                    Emit(variable);
                    break;
                case BindingPatternNode pattern:
                    Emit(pattern);
                    break;
                case BindingElementNode element:
                    Emit(element);
                    break;
                case IfStatementNode statement:
                    Emit(statement);
                    break;
                case WhileStatementNode statement:
                    Emit(statement);
                    break;
                case DoStatementNode statement:
                    Emit(statement);
                    break;
                case ForStatementNode statement:
                    Emit(statement);
                    break;
                case ForInOrOfStatementNode statement:
                    Emit(statement);
                    break;
                case BreakStatementNode statement:
                    Emit(statement);
                    break;
                case ContinueStatementNode statement:
                    Emit(statement);
                    break;
                case LabeledStatementNode statement:
                    Emit(statement);
                    break;
                case WithStatementNode statement:
                    Emit(statement);
                    break;
                case SwitchStatementNode statement:
                    Emit(statement);
                    break;
                case CaseBlockNode block:
                    Emit(block);
                    break;
                case CaseOrDefaultClauseNode clause:
                    Emit(clause);
                    break;
                case TryStatementNode statement:
                    Emit(statement);
                    break;
                case CatchClauseNode clause:
                    Emit(clause);
                    break;
                case TypeAliasDeclarationNode alias:
                    Emit(alias);
                    break;
                case InterfaceDeclarationNode type:
                    Emit(type);
                    break;
                case EnumDeclarationNode enumeration:
                    Emit(enumeration);
                    break;
                case EnumMemberNode member:
                    Emit(member);
                    break;
                case ModuleDeclarationNode module:
                    Emit(module);
                    break;
                case ImportDeclarationNode import:
                    Emit(import);
                    break;
                case ImportClauseNode clause:
                    Emit(clause);
                    break;
                case NamespaceImportNode import:
                    Emit(import);
                    break;
                case NamedImportsNode imports:
                    Emit(imports);
                    break;
                case ImportSpecifierNode import:
                    Emit(import);
                    break;
                case ImportEqualsDeclarationNode import:
                    Emit(import);
                    break;
                case ExternalModuleReferenceNode reference:
                    Emit(reference);
                    break;
                case ExportDeclarationNode export:
                    Emit(export);
                    break;
                case ExportAssignmentNode export:
                    Emit(export);
                    break;
                case NamedExportsNode exports:
                    Emit(exports);
                    break;
                case ExportSpecifierNode export:
                    Emit(export);
                    break;
                case NamespaceExportNode export:
                    Emit(export);
                    break;
                case NamespaceExportDeclarationNode export:
                    Emit(export);
                    break;
                case ImportAttributesNode attributes:
                    Emit(attributes);
                    break;
                case ImportAttributeNode attribute:
                    Emit(attribute);
                    break;
                case DecoratorNode decorator:
                    Emit(decorator);
                    break;
                case TypeParameterDeclarationNode parameter:
                    Emit(parameter);
                    break;
                case TypeReferenceNode reference:
                    Emit(reference);
                    break;
                case TypeQueryNode query:
                    Emit(query);
                    break;
                case TypeLiteralNode literal:
                    Emit(literal);
                    break;
                case NotEmittedTypeElementNode:
                    break;
                case ArrayTypeNode array:
                    Emit(array);
                    break;
                case TupleTypeNode tuple:
                    Emit(tuple);
                    break;
                case NamedTupleMemberNode member:
                    Emit(member);
                    break;
                case OptionalTypeNode optional:
                    Emit(optional);
                    break;
                case RestTypeNode rest:
                    Emit(rest);
                    break;
                case UnionTypeNode union:
                    Emit(union);
                    break;
                case IntersectionTypeNode intersection:
                    Emit(intersection);
                    break;
                case ConditionalTypeNode conditional:
                    Emit(conditional);
                    break;
                case InferTypeNode infer:
                    Emit(infer);
                    break;
                case ParenthesizedTypeNode parenthesized:
                    Emit(parenthesized);
                    break;
                case TypeOperatorNode operation:
                    Emit(operation);
                    break;
                case IndexedAccessTypeNode indexed:
                    Emit(indexed);
                    break;
                case ImportTypeNode import:
                    Emit(import);
                    break;
                case MappedTypeNode mapped:
                    Emit(mapped);
                    break;
                case LiteralTypeNode literal:
                    Emit(literal);
                    break;
                case TypePredicateNode predicate:
                    Emit(predicate);
                    break;
                case FunctionTypeNode function:
                    Emit(function);
                    break;
                case ConstructorTypeNode function:
                    Emit(function);
                    break;
                case PropertySignatureDeclarationNode property:
                    Emit(property);
                    break;
                case MethodSignatureDeclarationNode method:
                    Emit(method);
                    break;
                case CallSignatureDeclarationNode signature:
                    Emit(signature);
                    break;
                case ConstructSignatureDeclarationNode signature:
                    Emit(signature);
                    break;
                case IndexSignatureDeclarationNode signature:
                    Emit(signature);
                    break;
                case TemplateLiteralTypeNode template:
                    Emit(template);
                    break;
                case TemplateLiteralTypeSpanNode span:
                    Emit(span);
                    break;
                case JsxElementNode jsx:
                    Emit(jsx);
                    break;
                case JsxSelfClosingElementNode jsx:
                    Emit(jsx);
                    break;
                case JsxOpeningElementNode jsx:
                    Emit(jsx);
                    break;
                case JsxClosingElementNode jsx:
                    Emit(jsx);
                    break;
                case JsxAttributesNode attributes:
                    Emit(attributes);
                    break;
                case JsxAttributeNode attribute:
                    Emit(attribute);
                    break;
                case JsxSpreadAttributeNode attribute:
                    Emit(attribute);
                    break;
                case JsxExpressionNode expression:
                    Emit(expression);
                    break;
                case JsxTextNode text:
                    Emit(text);
                    break;
                case JsxFragmentNode fragment:
                    Emit(fragment);
                    break;
                case JsxNamespacedNameNode name:
                    Emit(name);
                    break;
                case SyntaxNode when node.Kind == K.ThisType:
                    output.Append("this"u8);
                    break;
                case SyntaxNode when node.Kind == K.EmptyStatement:
                    output.Append((byte)';');
                    break;
                case SyntaxNode when node.Kind == K.DebuggerStatement:
                    output.Append("debugger;"u8);
                    break;
                default:
                    Utf8String token = TokenFacts.Text(node.Kind);
                    if (token.Length == 0)
                        throw new NotSupportedException($"Diagnostic node printing requires {node.Kind}");
                    output.Append(token.Span);
                    if (node.Kind == K.DebuggerStatement)
                        output.Append((byte)';');
                    break;
            }
        }

        private void Emit(IdentifierNode id) =>
            output.Append(SemanticSyntax.Source(id) == sourceFile ? OriginalText(id) ?? id.Text : id.Text);

        private void Emit(PrivateIdentifierNode id) =>
            output.Append(SemanticSyntax.Source(id) == sourceFile ? OriginalText(id) ?? id.Text : id.Text);

        private void Emit(StringLiteralNode literal) =>
            output.Append(
                OriginalText(literal) ?? QuoteSymbolText(
                    literal.Text,
                    (literal.TokenFlags & TokenFlags.SingleQuote) != 0 ? (byte)'\'' : (byte)'"',
                    !neverAsciiEscape && noAsciiEscape?.Contains(literal) != true).Span);

        private void Emit(NumericLiteralNode literal) =>
            output.Append(NumberText(literal).Span);

        private void Emit(BigIntLiteralNode literal) =>
            output.Append(literal.Text.Span);

        private void Emit(RegularExpressionLiteralNode literal) =>
            output.Append(literal.Text.Span);

        private void Emit(NoSubstitutionTemplateLiteralNode literal) =>
            output.Append(
                OriginalText(literal) ?? QuoteSymbolText(
                    literal.Text,
                    (byte)'`',
                    !neverAsciiEscape && noAsciiEscape?.Contains(literal) != true).Span);

        private void Emit(TemplateHeadNode literal) =>
            output.Append((byte)'`').Append(
                TemplateText(literal.Text, literal.RawText, noAsciiEscape?.Contains(literal) == true).Span).Append("${"u8);

        private void Emit(TemplateMiddleNode literal) =>
            output.Append((byte)'}').Append(
                TemplateText(literal.Text, literal.RawText, noAsciiEscape?.Contains(literal) == true).Span).Append("${"u8);

        private void Emit(TemplateTailNode literal) =>
            output.Append((byte)'}').Append(
                TemplateText(literal.Text, literal.RawText, noAsciiEscape?.Contains(literal) == true).Span).Append((byte)'`');

        private void Emit(ComputedPropertyNameNode computed) =>
            Push(T(Utf8Literals.OpenBracket), N(computed.Expression), T(Utf8Literals.CloseBracket));

        private void Emit(QualifiedNameNode qualified) =>
            Push(N(qualified.Left), T(Utf8Literals.Dot), N(qualified.Right));

        private void Emit(MetaPropertyNode meta) =>
            Push(T(Utf8String.Concat(TokenFacts.Text(meta.KeywordToken), "."u8)), N(meta.Name));

        private void Emit(PropertyAccessExpressionNode property) =>
            Push(N(property.Expression), T(property.QuestionDotToken is not null ? Utf8Literals.OptionalAccess
                : property.Expression is NumericLiteralNode number && (number.TokenFlags & TokenFlags.WithSpecifier) == 0
                    && NumberText(number).Span.IndexOfAny([(byte)'.', (byte)'e', (byte)'E']) < 0 ? Utf8Literals.ParentDirectory : Utf8Literals.Dot), N(property.Name));

        private void Emit(ElementAccessExpressionNode element) =>
            Push(N(element.Expression), T(element.QuestionDotToken is null ? Utf8Literals.OpenBracket : Utf8Literals.OptionalElementAccess), N(element.ArgumentExpression), T(Utf8Literals.CloseBracket));

        private void Emit(CallExpressionNode call) =>
            Push(N(call.Expression), N(call.QuestionDotToken), TypeArguments(call.TypeArguments), Parameters(call.Arguments));

        private void Emit(NewExpressionNode call) =>
            Push(T(Utf8Literals.NewPrefix), N(call.Expression), TypeArguments(call.TypeArguments), List(call.Arguments, Utf8Literals.OpenParen, Utf8Literals.CommaSpace, Utf8Literals.CloseParen));

        private void Emit(TaggedTemplateExpressionNode tagged) =>
            Push(N(tagged.Tag), N(tagged.QuestionDotToken), TypeArguments(tagged.TypeArguments), T(Utf8Literals.Space), N(tagged.Template));

        private void Emit(TemplateExpressionNode template) =>
            Push(N(template.Head), List(template.TemplateSpans, Utf8String.Empty, Utf8String.Empty, Utf8String.Empty));

        private void Emit(TemplateSpanNode span) =>
            Push(N(span.Expression), N(span.Literal));

        private void Emit(ParenthesizedExpressionNode paren) =>
            Push(T(Utf8Literals.OpenParen), N(paren.Expression), T(Utf8Literals.CloseParen));

        private void Emit(BinaryExpressionNode binary) =>
            Push(
                N(binary.Left),
                Annotation(binary.Type),
                T(binary.OperatorToken?.Kind == K.CommaToken ? Utf8Literals.CommaSpace : Utf8Literals.Space + TokenFacts.Text(binary.OperatorToken!.Kind) + Utf8Literals.Space),
                N(binary.Right));

        private void Emit(ConditionalExpressionNode conditional) =>
            Push(N(conditional.Condition), T(Utf8Literals.QuestionSeparator), N(conditional.WhenTrue), T(Utf8Literals.ColonSeparator), N(conditional.WhenFalse));

        private void Emit(PrefixUnaryExpressionNode unary) =>
            Push(T(TokenFacts.Text(unary.Operator)), T(UnarySpace(unary) ? Utf8Literals.Space : Utf8String.Empty), N(unary.Operand));

        private void Emit(PostfixUnaryExpressionNode unary) =>
            Push(N(unary.Operand), T(TokenFacts.Text(unary.Operator)));

        private void Emit(DeleteExpressionNode unary) =>
            Push(T(Utf8Literals.Delete), N(unary.Expression));

        private void Emit(TypeOfExpressionNode unary) =>
            Push(T(Utf8Literals.Typeof), N(unary.Expression));

        private void Emit(VoidExpressionNode unary) =>
            Push(T(Utf8Literals.Void), N(unary.Expression));

        private void Emit(AwaitExpressionNode unary) =>
            Push(T(Utf8Literals.AwaitPrefix), N(unary.Expression));

        private void Emit(NonNullExpressionNode unary) =>
            Push(N(unary.Expression), T(Utf8Literals.Exclamation));

        private void Emit(YieldExpressionNode yield) =>
            Push(T(Utf8Literals.Yield), N(yield.AsteriskToken), T(yield.Expression is null ? Utf8String.Empty : Utf8Literals.Space), N(yield.Expression));

        private void Emit(AsExpressionNode assertion) =>
            Push(N(assertion.Expression), T(Utf8Literals.As), N(assertion.Type));

        private void Emit(SatisfiesExpressionNode assertion) =>
            Push(N(assertion.Expression), T(Utf8Literals.Satisfies), N(assertion.Type));

        private void Emit(TypeAssertionNode assertion) =>
            Push(T(Utf8Literals.LessThan), N(assertion.Type), T(Utf8Literals.GreaterThan), N(assertion.Expression));

        private void Emit(ExpressionWithTypeArgumentsNode instantiation) =>
            Push(N(instantiation.Expression), TypeArguments(instantiation.TypeArguments));

        private void Emit(ArrayLiteralExpressionNode array) =>
            Push(List(array.Elements, Utf8Literals.OpenBracket, Utf8Literals.CommaSpace, HasTrailingComma(array.Elements) ? Utf8Literals.CommaCloseBracket : Utf8Literals.CloseBracket, true));

        private void Emit(ObjectLiteralExpressionNode obj) =>
            Push(
                obj.Properties is { Count: > 0 }
                    ? List(
                        obj.Properties,
                        Utf8Literals.OpenBraceSpace,
                        Utf8Literals.CommaSpace,
                        HasTrailingComma(obj.Properties) && sourceFile is { ScriptKind: not ScriptKind.JSON } ? Utf8Literals.CommaSpaceCloseBrace : Utf8Literals.SpaceCloseBrace)
                    : T(Utf8Literals.EmptyBraces));

        private void Emit(PropertyAssignmentNode property) =>
            Push(
                Modifiers(property),
                N(property.Name),
                N(property.PostfixToken),
                Annotation(property.Type),
                T(Utf8Literals.ColonSpace),
                N(property.Initializer));

        private void Emit(ShorthandPropertyAssignmentNode shorthand) =>
            Push(
                Modifiers(shorthand),
                N(shorthand.Name),
                N(shorthand.PostfixToken),
                Annotation(shorthand.Type),
                Initializer(shorthand.ObjectAssignmentInitializer));

        private void Emit(SpreadAssignmentNode spread) =>
            Push(T(Utf8Literals.Ellipsis), N(spread.Expression));

        private void Emit(SpreadElementNode spread) =>
            Push(T(Utf8Literals.Ellipsis), N(spread.Expression));

        private void Emit(ArrowFunctionNode arrow) =>
            Push(
                Modifiers(arrow),
                TypeArguments(arrow.TypeParameters),
                SimpleArrow(arrow) ? N(arrow.Parameters![0]) : Parameters(arrow.Parameters),
                Annotation(arrow.Type),
                T(Utf8Literals.Space), N(arrow.EqualsGreaterThanToken), T(Utf8Literals.Space),
                N(arrow.Body));

        private void Emit(FunctionExpressionNode function) =>
            Push(
                Modifiers(function),
                T(Utf8Literals.Function),
                N(function.AsteriskToken),
                T(Utf8Literals.Space),
                N(function.Name),
                TypeArguments(function.TypeParameters),
                Parameters(function.Parameters),
                Annotation(function.Type),
                Body(function.Body));

        private void Emit(FunctionDeclarationNode function) =>
            Push(
                Modifiers(function),
                T(Utf8Literals.Function),
                N(function.AsteriskToken),
                T(Utf8Literals.Space),
                N(function.Name),
                TypeArguments(function.TypeParameters),
                Parameters(function.Parameters),
                Annotation(function.Type),
                Body(function.Body));

        private void Emit(MethodDeclarationNode method) =>
            Push(
                Modifiers(method),
                N(method.AsteriskToken),
                N(method.Name),
                N(method.PostfixToken),
                TypeArguments(method.TypeParameters),
                Parameters(method.Parameters),
                Annotation(method.Type),
                Body(method.Body));

        private void Emit(GetAccessorDeclarationNode accessor) =>
            Push(
                Modifiers(accessor),
                T(Utf8Literals.GetPrefix),
                N(accessor.Name),
                TypeArguments(accessor.TypeParameters),
                Parameters(accessor.Parameters),
                Annotation(accessor.Type),
                Body(accessor.Body));

        private void Emit(SetAccessorDeclarationNode accessor) =>
            Push(
                Modifiers(accessor),
                T(Utf8Literals.SetPrefix),
                N(accessor.Name),
                TypeArguments(accessor.TypeParameters),
                Parameters(accessor.Parameters),
                Annotation(accessor.Type),
                Body(accessor.Body));

        private void Emit(ConstructorDeclarationNode constructor) =>
            Push(
                Modifiers(constructor),
                T(Utf8Literals.Constructor),
                TypeArguments(constructor.TypeParameters),
                Parameters(constructor.Parameters),
                Annotation(constructor.Type),
                Body(constructor.Body));

        private void Emit(ParameterDeclarationNode parameter) =>
            Push(
                Modifiers(parameter),
                N(parameter.DotDotDotToken),
                N(parameter.Name),
                N(parameter.QuestionToken),
                Annotation(parameter.Type),
                Initializer(parameter.Initializer));

        private void Emit(PropertyDeclarationNode property) =>
            Push(
                Modifiers(property),
                N(property.Name),
                N(property.PostfixToken),
                Annotation(property.Type),
                Initializer(property.Initializer),
                T(Utf8Literals.Semicolon));

        private void Emit(ClassExpressionNode type) =>
            Push(
                Modifiers(type),
                T(Utf8Literals.Class),
                T(type.Name is null ? Utf8String.Empty : Utf8Literals.Space),
                N(type.Name),
                TypeArguments(type.TypeParameters),
                List(type.HeritageClauses, Utf8Literals.Space, Utf8Literals.Space, Utf8String.Empty),
                T(Utf8Literals.Space),
                Braces(type.Members, Utf8Literals.Space, true));

        private void Emit(ClassDeclarationNode type) =>
            Push(
                Modifiers(type),
                T(Utf8Literals.Class),
                T(type.Name is null ? Utf8String.Empty : Utf8Literals.Space),
                N(type.Name),
                TypeArguments(type.TypeParameters),
                List(type.HeritageClauses, Utf8Literals.Space, Utf8Literals.Space, Utf8String.Empty),
                T(Utf8Literals.Space),
                Braces(type.Members, Utf8Literals.Space, true));

        private void Emit(ClassStaticBlockDeclarationNode block) =>
            Push(T(Utf8Literals.StaticPrefix), N(block.Body));

        private void Emit(HeritageClauseNode heritage) =>
            Push(T(Utf8String.Concat(TokenFacts.Text(heritage.Token), " "u8)), List(heritage.Types, Utf8String.Empty, Utf8Literals.CommaSpace, Utf8String.Empty));

        private void Emit(BlockNode block) =>
            Push(Braces(block.Statements, Utf8Literals.Space, true));

        private void Emit(ModuleBlockNode block) =>
            Push(Braces(block.Statements, Utf8Literals.Space, true));

        private void Emit(SourceFileNode file) =>
            EmitSourceFile(file);

        private void Emit(ExpressionStatementNode statement) =>
            Push(N(statement.Expression), T(Utf8Literals.Semicolon));

        private void Emit(ReturnStatementNode statement) =>
            Push(T(Utf8Literals.Return), T(statement.Expression is null ? Utf8String.Empty : Utf8Literals.Space), N(statement.Expression), T(Utf8Literals.Semicolon));

        private void Emit(ThrowStatementNode statement) =>
            Push(T(Utf8Literals.Throw), N(statement.Expression), T(Utf8Literals.Semicolon));

        private void Emit(VariableStatementNode statement) =>
            Push(Modifiers(statement), N(statement.DeclarationList), T(Utf8Literals.Semicolon));

        private void Emit(VariableDeclarationListNode list) =>
            Push(T(VariableKind(list.Flags)), List(list.Declarations, Utf8String.Empty, Utf8Literals.CommaSpace, Utf8String.Empty));

        private void Emit(VariableDeclarationNode variable) =>
            Push(N(variable.Name), N(variable.ExclamationToken), Annotation(variable.Type), Initializer(variable.Initializer));

        private void Emit(BindingPatternNode pattern) =>
            Push(
                pattern.Kind == K.ObjectBindingPattern
                    ? Braces(pattern.Elements, Utf8Literals.CommaSpace)
                    : List(pattern.Elements, Utf8Literals.OpenBracket, Utf8Literals.CommaSpace, Utf8Literals.CloseBracket, true));

        private void Emit(BindingElementNode element) =>
            Push(
                N(element.DotDotDotToken),
                element.PropertyName is null ? default : S(N(element.PropertyName), T(Utf8Literals.ColonSpace)),
                N(element.Name),
                Initializer(element.Initializer));

        private void Emit(IfStatementNode statement) =>
            Push(
                T(Utf8Literals.If),
                N(statement.Expression),
                T(Utf8Literals.CloseParenSpace),
                N(statement.ThenStatement),
                statement.ElseStatement is null ? default : S(T(Utf8Literals.Else), N(statement.ElseStatement)));

        private void Emit(WhileStatementNode statement) =>
            Push(T(Utf8Literals.While), N(statement.Expression), T(Utf8Literals.CloseParenSpace), N(statement.Statement));

        private void Emit(DoStatementNode statement) =>
            Push(T(Utf8Literals.Do), N(statement.Statement), T(Utf8Literals.DoWhileSuffix), N(statement.Expression), T(Utf8Literals.CloseParenSemicolon));

        private void Emit(ForStatementNode statement) =>
            Push(T(Utf8Literals.ForStatementPrefix), N(statement.Initializer), T(statement.Condition is null ? Utf8Literals.Semicolon : Utf8Literals.SemicolonSpace), N(statement.Condition),
                T(statement.Incrementor is null ? Utf8Literals.Semicolon : Utf8Literals.SemicolonSpace), N(statement.Incrementor), T(Utf8Literals.CloseParenSpace), N(statement.Statement));

        private void Emit(ForInOrOfStatementNode statement) =>
            Push(
                T(Utf8Literals.ForPrefix),
                statement.AwaitModifier is null ? default : T(Utf8Literals.AwaitPrefix),
                T(Utf8Literals.OpenParen),
                N(statement.Initializer),
                T(statement.Kind == K.ForOfStatement ? Utf8Literals.Of : Utf8Literals.In),
                N(statement.Expression),
                T(Utf8Literals.CloseParenSpace),
                N(statement.Statement));

        private void Emit(BreakStatementNode statement) =>
            Push(T(Utf8Literals.Break), statement.Label is null ? default : S(T(Utf8Literals.Space), N(statement.Label)), T(Utf8Literals.Semicolon));

        private void Emit(ContinueStatementNode statement) =>
            Push(T(Utf8Literals.Continue), statement.Label is null ? default : S(T(Utf8Literals.Space), N(statement.Label)), T(Utf8Literals.Semicolon));

        private void Emit(LabeledStatementNode statement) =>
            Push(N(statement.Label), T(Utf8Literals.ColonSpace), N(statement.Statement));

        private void Emit(WithStatementNode statement) =>
            Push(T(Utf8Literals.WithStatementPrefix), N(statement.Expression), T(Utf8Literals.CloseParenSpace), N(statement.Statement));

        private void Emit(SwitchStatementNode statement) =>
            Push(T(Utf8Literals.Switch), N(statement.Expression), T(Utf8Literals.CloseParenSpace), N(statement.CaseBlock));

        private void Emit(CaseBlockNode block) =>
            Push(Braces(block.Clauses, Utf8Literals.Space, true));

        private void Emit(CaseOrDefaultClauseNode clause) =>
            Push(
                clause.Expression is null ? T(Utf8Literals.Default) : S(T(Utf8Literals.Case), N(clause.Expression)),
                T(Utf8Literals.Colon),
                List(clause.Statements, Utf8Literals.Space, Utf8Literals.Space, Utf8String.Empty));

        private void Emit(TryStatementNode statement) =>
            Push(
                T(Utf8Literals.Try),
                N(statement.TryBlock),
                statement.CatchClause is null ? default : S(T(Utf8Literals.Space), N(statement.CatchClause)),
                statement.FinallyBlock is null ? default : S(T(Utf8Literals.Finally), N(statement.FinallyBlock)));

        private void Emit(CatchClauseNode clause) =>
            Push(
                T(Utf8Literals.Catch),
                clause.VariableDeclaration is null ? default : S(T(Utf8Literals.SpaceOpenParen), N(clause.VariableDeclaration), T(Utf8Literals.CloseParen)),
                T(Utf8Literals.Space),
                N(clause.Block));

        private void Emit(TypeAliasDeclarationNode alias) =>
            Push(Modifiers(alias), T(Utf8Literals.TypePrefix), N(alias.Name), TypeArguments(alias.TypeParameters), T(Utf8Literals.AssignmentSeparator), N(alias.Type), T(Utf8Literals.Semicolon));

        private void Emit(InterfaceDeclarationNode type) =>
            Push(
                Modifiers(type),
                T(Utf8Literals.InterfacePrefix),
                N(type.Name),
                TypeArguments(type.TypeParameters),
                List(type.HeritageClauses, Utf8Literals.Space, Utf8Literals.Space, Utf8String.Empty),
                T(Utf8Literals.Space),
                Braces(type.Members, Utf8Literals.Space, true));

        private void Emit(EnumDeclarationNode enumeration) =>
            Push(Modifiers(enumeration), T(Utf8Literals.Enum), N(enumeration.Name), T(Utf8Literals.Space), Braces(enumeration.Members, Utf8Literals.CommaSpace, true));

        private void Emit(EnumMemberNode member) =>
            Push(N(member.Name), Initializer(member.Initializer));

        private void Emit(ModuleDeclarationNode module) =>
            EmitModule(module);

        private void Emit(ImportDeclarationNode import) =>
            Push(
                Modifiers(import),
                T(Utf8Literals.ImportPrefix),
                N(import.ImportClause),
                import.ImportClause is null ? default : T(Utf8Literals.From),
                N(import.ModuleSpecifier),
                import.Attributes is null ? default : S(T(Utf8Literals.Space), N(import.Attributes)), T(Utf8Literals.Semicolon));

        private void Emit(ImportClauseNode clause) =>
            Push(clause.PhaseModifier == K.Unknown ? default : T(Utf8String.Concat(TokenFacts.Text(clause.PhaseModifier), " "u8)), N(clause.Name),
                clause.Name is not null && clause.NamedBindings is not null ? T(Utf8Literals.CommaSpace) : default, N(clause.NamedBindings));

        private void Emit(NamespaceImportNode import) =>
            Push(T(Utf8Literals.NamespaceImportPrefix), N(import.Name));

        private void Emit(NamedImportsNode imports) =>
            Push(Braces(imports.Elements, Utf8Literals.CommaSpace));

        private void Emit(ImportSpecifierNode import) =>
            Push(
                import.IsTypeOnly ? T(Utf8Literals.TypePrefix) : default,
                import.PropertyName is null ? default : S(N(import.PropertyName), T(Utf8Literals.As)),
                N(import.Name));

        private void Emit(ImportEqualsDeclarationNode import) =>
            Push(
                Modifiers(import),
                T(import.IsTypeOnly ? Utf8Literals.ImportType : Utf8Literals.ImportPrefix),
                N(import.Name),
                T(Utf8Literals.AssignmentSeparator),
                N(import.ModuleReference),
                T(Utf8Literals.Semicolon));

        private void Emit(ExternalModuleReferenceNode reference) =>
            Push(T(Utf8Literals.Require), N(reference.Expression), T(Utf8Literals.CloseParen));

        private void Emit(ExportDeclarationNode export) =>
            Push(
                Modifiers(export),
                T(export.IsTypeOnly ? Utf8Literals.ExportType : Utf8Literals.ExportPrefix),
                export.ExportClause is null ? T(Utf8Literals.Asterisk) : N(export.ExportClause),
                export.ModuleSpecifier is null ? default : S(T(Utf8Literals.From), N(export.ModuleSpecifier)),
                export.Attributes is null ? default : S(T(Utf8Literals.Space), N(export.Attributes)), T(Utf8Literals.Semicolon));

        private void Emit(ExportAssignmentNode export) =>
            Push(Modifiers(export), T(export.IsExportEquals ? Utf8Literals.ExportAssignmentPrefix : Utf8Literals.ExportDefault), N(export.Expression), T(Utf8Literals.Semicolon));

        private void Emit(NamedExportsNode exports) =>
            Push(Braces(exports.Elements, Utf8Literals.CommaSpace));

        private void Emit(ExportSpecifierNode export) =>
            Push(
                export.IsTypeOnly ? T(Utf8Literals.TypePrefix) : default,
                export.PropertyName is null ? default : S(N(export.PropertyName), T(Utf8Literals.As)),
                N(export.Name));

        private void Emit(NamespaceExportNode export) =>
            Push(T(Utf8Literals.NamespaceImportPrefix), N(export.Name));

        private void Emit(NamespaceExportDeclarationNode export) =>
            Push(T(Utf8Literals.ExportAsNamespace), N(export.Name), T(Utf8Literals.Semicolon));

        private void Emit(ImportAttributesNode attributes) =>
            Push(T(Utf8String.Concat(TokenFacts.Text(attributes.Token), " "u8)), Braces(attributes.Attributes, Utf8Literals.CommaSpace));

        private void Emit(ImportAttributeNode attribute) =>
            Push(N(attribute.Name), T(Utf8Literals.ColonSpace), N(attribute.Value));

        private void Emit(DecoratorNode decorator) =>
            Push(T(Utf8Literals.At), N(decorator.Expression));

        private void Emit(TypeParameterDeclarationNode parameter) =>
            Push(
                Modifiers(parameter),
                N(parameter.Name),
                parameter.Constraint is null ? default : S(T(Utf8Literals.Extends), N(parameter.Constraint)),
                Initializer(parameter.DefaultType));

        private void Emit(TypeReferenceNode reference) =>
            Push(N(reference.TypeName), TypeArguments(reference.TypeArguments));

        private void Emit(TypeQueryNode query) =>
            Push(T(Utf8Literals.Typeof), N(query.ExprName), TypeArguments(query.TypeArguments));

        private void Emit(TypeLiteralNode literal) =>
            Push(TypeMembers(literal));

        private void Emit(ArrayTypeNode array) =>
            Push(N(array.ElementType), T(Utf8Literals.EmptyBrackets));

        private void Emit(TupleTypeNode tuple) =>
            Push(singleLine?.Contains(tuple) == true ? List(tuple.Elements, Utf8Literals.OpenBracket, Utf8Literals.CommaSpace, Utf8Literals.CloseBracket, true)
                : tuple.Elements is { Count: > 0 } ? List(tuple.Elements, Utf8Literals.OpenBracketSpace, Utf8Literals.CommaSpace, Utf8Literals.SpaceCloseBracket) : T(Utf8Literals.SpacedBrackets));

        private void Emit(NamedTupleMemberNode member) =>
            Push(N(member.DotDotDotToken), N(member.Name), N(member.QuestionToken), T(Utf8Literals.ColonSpace), N(member.Type));

        private void Emit(OptionalTypeNode optional) =>
            Push(N(optional.Type), T(Utf8Literals.QuestionMark));

        private void Emit(RestTypeNode rest) =>
            Push(T(Utf8Literals.Ellipsis), N(rest.Type));

        private void Emit(UnionTypeNode union) =>
            Push(List(union.Types, Utf8String.Empty, Utf8Literals.UnionSeparator, Utf8String.Empty));

        private void Emit(IntersectionTypeNode intersection) =>
            Push(List(intersection.Types, Utf8String.Empty, Utf8Literals.IntersectionSeparator, Utf8String.Empty));

        private void Emit(ConditionalTypeNode conditional) =>
            Push(
                N(conditional.CheckType),
                T(Utf8Literals.Extends),
                N(conditional.ExtendsType),
                T(Utf8Literals.QuestionSeparator),
                N(conditional.TrueType),
                T(Utf8Literals.ColonSeparator),
                N(conditional.FalseType));

        private void Emit(InferTypeNode infer) =>
            Push(T(Utf8Literals.Infer), N(infer.TypeParameter));

        private void Emit(ParenthesizedTypeNode parenthesized) =>
            Push(T(Utf8Literals.OpenParen), N(parenthesized.Type), T(Utf8Literals.CloseParen));

        private void Emit(TypeOperatorNode operation) =>
            Push(T(Utf8String.Concat(TokenFacts.Text(operation.Operator), " "u8)), N(operation.Type));

        private void Emit(IndexedAccessTypeNode indexed) =>
            Push(N(indexed.ObjectType), T(Utf8Literals.OpenBracket), N(indexed.IndexType), T(Utf8Literals.CloseBracket));

        private void Emit(ImportTypeNode import) =>
            Push(T(import.IsTypeOf ? Utf8Literals.TypeofImport : Utf8Literals.ImportCallPrefix), N(import.Argument),
                import.Attributes is null
                    ? default
                    : S(
                        T(Utf8String.Concat(", { "u8, TokenFacts.Text(import.Attributes.Token), ": "u8)),
                        Braces(import.Attributes.Attributes, Utf8Literals.CommaSpace),
                        T(Utf8Literals.SpaceCloseBrace)),
                T(Utf8Literals.CloseParen),
                import.Qualifier is null ? default : S(T(Utf8Literals.Dot), N(import.Qualifier)), TypeArguments(import.TypeArguments));

        private void Emit(MappedTypeNode mapped) =>
            Push(
                T(Utf8Literals.OpenBraceSpace),
                mapped.ReadonlyToken is null
                    ? default
                    : T(
                        Utf8String.Concat(mapped.ReadonlyToken.Kind is K.PlusToken or K.MinusToken
                            ? TokenFacts.Text(mapped.ReadonlyToken.Kind)
                            : ""u8, "readonly "u8)),
                T(Utf8Literals.OpenBracket),
                N(mapped.TypeParameter?.Name),
                T(Utf8Literals.In),
                N(mapped.TypeParameter?.Constraint),
                mapped.NameType is null ? default : S(T(Utf8Literals.As), N(mapped.NameType)),
                T(Utf8Literals.CloseBracket),
                mapped.QuestionToken is null
                    ? default
                    : T(
                        Utf8String.Concat(mapped.QuestionToken.Kind is K.PlusToken or K.MinusToken
                            ? TokenFacts.Text(mapped.QuestionToken.Kind)
                            : ""u8, "?"u8)),
                Annotation(mapped.Type), T(Utf8Literals.Semicolon), List(mapped.Members, Utf8Literals.Space, Utf8Literals.Space, Utf8String.Empty), T(Utf8Literals.SpaceCloseBrace));

        private void Emit(LiteralTypeNode literal) =>
            Push(N(literal.Literal));

        private void Emit(TypePredicateNode predicate) =>
            Push(
                predicate.AssertsModifier is null ? default : T(Utf8Literals.Asserts),
                N(predicate.ParameterName),
                predicate.Type is null ? default : S(T(Utf8Literals.Is), N(predicate.Type)));

        private void Emit(FunctionTypeNode function) =>
            Push(TypeArguments(function.TypeParameters), Parameters(function.Parameters), T(Utf8Literals.ArrowSeparator), N(function.Type));

        private void Emit(ConstructorTypeNode function) =>
            Push(
                Modifiers(function),
                T(Utf8Literals.NewPrefix),
                TypeArguments(function.TypeParameters),
                Parameters(function.Parameters),
                T(Utf8Literals.ArrowSeparator),
                N(function.Type));

        private void Emit(PropertySignatureDeclarationNode property) =>
            Push(
                Modifiers(property),
                N(property.Name),
                N(property.PostfixToken),
                Annotation(property.Type),
                Initializer(property.Initializer),
                T(Utf8Literals.Semicolon));

        private void Emit(MethodSignatureDeclarationNode method) =>
            Push(
                Modifiers(method),
                N(method.Name),
                N(method.PostfixToken),
                TypeArguments(method.TypeParameters),
                Parameters(method.Parameters),
                Annotation(method.Type),
                T(Utf8Literals.Semicolon));

        private void Emit(CallSignatureDeclarationNode signature) =>
            Push(TypeArguments(signature.TypeParameters), Parameters(signature.Parameters), Annotation(signature.Type), T(Utf8Literals.Semicolon));

        private void Emit(ConstructSignatureDeclarationNode signature) =>
            Push(
                T(Utf8Literals.NewPrefix),
                TypeArguments(signature.TypeParameters),
                Parameters(signature.Parameters),
                Annotation(signature.Type),
                T(Utf8Literals.Semicolon));

        private void Emit(IndexSignatureDeclarationNode signature) =>
            Push(Modifiers(signature), List(signature.Parameters, Utf8Literals.OpenBracket, Utf8Literals.CommaSpace, Utf8Literals.CloseBracket, true), Annotation(signature.Type), T(Utf8Literals.Semicolon));

        private void Emit(TemplateLiteralTypeNode template) =>
            Push(N(template.Head), List(template.TemplateSpans, Utf8String.Empty, Utf8String.Empty, Utf8String.Empty));

        private void Emit(TemplateLiteralTypeSpanNode span) =>
            Push(N(span.Type), N(span.Literal));

        private void Emit(JsxElementNode jsx) =>
            Push(N(jsx.OpeningElement), List(jsx.Children, Utf8String.Empty, Utf8String.Empty, Utf8String.Empty), N(jsx.ClosingElement));

        private void Emit(JsxSelfClosingElementNode jsx) =>
            Push(T(Utf8Literals.LessThan), N(jsx.TagName), TypeArguments(jsx.TypeArguments), N(jsx.Attributes), T(Utf8Literals.JsxSelfClosingTagEnd));

        private void Emit(JsxOpeningElementNode jsx) =>
            Push(T(Utf8Literals.LessThan), N(jsx.TagName), TypeArguments(jsx.TypeArguments), N(jsx.Attributes), T(Utf8Literals.GreaterThan));

        private void Emit(JsxClosingElementNode jsx) =>
            Push(T(Utf8Literals.JsxClosingTagPrefix), N(jsx.TagName), T(Utf8Literals.GreaterThan));

        private void Emit(JsxAttributesNode attributes) =>
            Push(List(attributes.Properties, attributes.Properties is { Count: > 0 } ? Utf8Literals.Space : Utf8String.Empty, Utf8Literals.Space, Utf8String.Empty));

        private void Emit(JsxAttributeNode attribute) =>
            Push(N(attribute.Name), attribute.Initializer is null ? default : S(T(Utf8Literals.EqualsToken), N(attribute.Initializer)));

        private void Emit(JsxSpreadAttributeNode attribute) =>
            Push(T(Utf8Literals.OpenBraceEllipsis), N(attribute.Expression), T(Utf8Literals.CloseBrace));

        private void Emit(JsxExpressionNode expression) =>
            Push(T(Utf8Literals.OpenBrace), N(expression.DotDotDotToken), N(expression.Expression), T(Utf8Literals.CloseBrace));

        private void Emit(JsxTextNode text) =>
            output.Append(text.Text.Span);

        private void Emit(JsxFragmentNode fragment) =>
            Push(T(Utf8Literals.EmptyJsxOpeningFragment), List(fragment.Children, Utf8String.Empty, Utf8String.Empty, Utf8String.Empty), T(Utf8Literals.JsxClosingFragment));

        private void Emit(JsxNamespacedNameNode name) =>
            Push(N(name.Namespace), T(Utf8Literals.Colon), N(name.Name));

        private Utf8String? OriginalText(SyntaxNode node)
        {
            if (sourceFile is null || node.Parent is null || node.Pos < 0 || node.End < 0)
                return null;
            var (start, _) = CheckerDiagnostic.TokenRange(sourceFile, node.Pos);
            return sourceFile.Source.Text[start..node.End];
        }

        private Utf8String NumberText(NumericLiteralNode literal) => (literal.TokenFlags & (TokenFlags.IsInvalid | TokenFlags.ContainsSeparator)) == 0
                    ? OriginalText(literal) ?? literal.Text : literal.Text;

        private static bool HasTrailingComma(NodeList? nodes) => nodes?.HasTrailingComma == true;

        private void EmitModule(ModuleDeclarationNode module)
        {
            var parts = new List<Part> { Modifiers(module) };
            if (module.Keyword != K.GlobalKeyword)
                parts.Add(T(module.Keyword == K.NamespaceKeyword ? Utf8Literals.Namespace : Utf8Literals.ModulePrefix));
            parts.Add(N(module.Name));
            var body = module.Body;
            while (body is ModuleDeclarationNode nested)
            {
                cancellation.ThrowIfCancellationRequested();
                parts.Add(T(Utf8Literals.Dot));
                parts.Add(N(nested.Name));
                body = nested.Body;
            }
            if (module.Attributes is not null)
                parts.Add(S(T(Utf8Literals.WithSeparator), N(module.Attributes)));
            parts.Add(Body(body));
            pending.Push(new(Parts: parts));
        }

        private void EmitSourceFile(SourceFileNode file)
        {
            sourceFile = file;
            var parts = new List<Part> { T(Utf8Literals.Space) };
            int index = 0;
            if (file.ScriptKind != ScriptKind.JSON)
            {
                if (file.Source.Text.Span.StartsWith("#!"u8, StringComparison.Ordinal))
                {
                    ReadOnlySpan<byte> source = file.Source.Text;
                    int lineEnd = source.IndexOfAny((byte)'\r', (byte)'\n');
                    parts.Add(T(Utf8String.Concat(lineEnd < 0 ? source : source[..lineEnd], " "u8)));
                }
                while (file.Statements is { } statements
                    && index < statements.Count
                    && statements[index] is ExpressionStatementNode { Expression: StringLiteralNode })
                    parts.Add(S(T(Utf8Literals.Space), N(statements[index++])));
                parts.Add(T(Utf8Literals.Space));
            }
            if (file.Statements is { } all && index < all.Count)
                parts.Add(List(all.Skip(index).ToArray(), Utf8Literals.Space, Utf8Literals.Space, Utf8Literals.Space));
            else
                parts.Add(T(Utf8Literals.Space));
            pending.Push(new(Parts: parts));
        }

        private Utf8String TemplateText(Utf8String text, Utf8String raw, bool noAscii = false)
        {
            if (raw.Length != 0 || text.Length == 0)
                return raw;
            return QuoteSymbolText(text, '`', !neverAsciiEscape && !noAscii)[1..^1];
        }

        private static bool UnarySpace(PrefixUnaryExpressionNode node) => node.Operator is K.PlusToken or K.MinusToken
                    && node.Operand is PrefixUnaryExpressionNode operand
                    && (operand.Operator == node.Operator || node.Operator == K.PlusToken && operand.Operator == K.PlusPlusToken
                        || node.Operator == K.MinusToken && operand.Operator == K.MinusMinusToken);

        private static bool SimpleArrow(ArrowFunctionNode node) => node.TypeParameters is null && node.Type is null
                    && node.Modifiers is null or { Count: 0 } && node.Parameters is { Count: 1, HasTrailingComma: false } parameters
                    && parameters[0] is ParameterDeclarationNode { Name: IdentifierNode, Modifiers: null, DotDotDotToken: null, QuestionToken: null, Type: null, Initializer: null } parameter
                    && parameter.Pos == node.Pos;

        private static Utf8String VariableKind(NodeFlags flags) => (flags & NodeFlags.AwaitUsing) == NodeFlags.AwaitUsing ? Utf8Literals.AwaitUsingPrefix
                    : (flags & NodeFlags.Using) != 0 ? Utf8Literals.UsingPrefix : (flags & NodeFlags.Const) != 0 ? Utf8Literals.ConstPrefix
                    : (flags & NodeFlags.Let) != 0 ? Utf8Literals.LetPrefix : Utf8Literals.Var;
    }
}
