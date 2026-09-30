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

        private void Emit(SyntaxNode node)
        {
            switch (node)
            {
                case IdentifierNode id:
                    output.Append(SemanticSyntax.Source(id) == sourceFile ? OriginalText(id) ?? id.Text : id.Text);
                    break;
                case PrivateIdentifierNode id:
                    output.Append(SemanticSyntax.Source(id) == sourceFile ? OriginalText(id) ?? id.Text : id.Text);
                    break;
                case StringLiteralNode literal:
                    output.Append(
                        OriginalText(literal) ?? QuoteSymbolText(
                            literal.Text,
                            (literal.TokenFlags & TokenFlags.SingleQuote) != 0 ? (byte)'\'' : (byte)'"',
                            !neverAsciiEscape && noAsciiEscape?.Contains(literal) != true).Span);
                    break;
                case NumericLiteralNode literal:
                    output.Append(NumberText(literal).Span);
                    break;
                case BigIntLiteralNode literal:
                    output.Append(literal.Text.Span);
                    break;
                case RegularExpressionLiteralNode literal:
                    output.Append(literal.Text.Span);
                    break;
                case NoSubstitutionTemplateLiteralNode literal:
                    output.Append(
                        OriginalText(literal) ?? QuoteSymbolText(
                            literal.Text,
                            (byte)'`',
                            !neverAsciiEscape && noAsciiEscape?.Contains(literal) != true).Span);
                    break;
                case TemplateHeadNode literal:
                    output.Append((byte)'`').Append(
                        TemplateText(literal.Text, literal.RawText, noAsciiEscape?.Contains(literal) == true).Span).Append("${"u8);
                    break;
                case TemplateMiddleNode literal:
                    output.Append((byte)'}').Append(
                        TemplateText(literal.Text, literal.RawText, noAsciiEscape?.Contains(literal) == true).Span).Append("${"u8);
                    break;
                case TemplateTailNode literal:
                    output.Append((byte)'}').Append(
                        TemplateText(literal.Text, literal.RawText, noAsciiEscape?.Contains(literal) == true).Span).Append((byte)'`');
                    break;
                case ComputedPropertyNameNode computed:
                    Push(T(Utf8Literals.OpenBracket), N(computed.Expression), T(Utf8Literals.CloseBracket));
                    break;
                case QualifiedNameNode qualified:
                    Push(N(qualified.Left), T(Utf8Literals.Dot), N(qualified.Right));
                    break;
                case MetaPropertyNode meta:
                    Push(T(Utf8String.Concat(TokenFacts.Text(meta.KeywordToken), "."u8)), N(meta.Name));
                    break;
                case PropertyAccessExpressionNode property:
                    Push(N(property.Expression), T(property.QuestionDotToken is not null ? Utf8Literals.OptionalAccess
                        : property.Expression is NumericLiteralNode number && (number.TokenFlags & TokenFlags.WithSpecifier) == 0
                            && NumberText(number).Span.IndexOfAny([(byte)'.', (byte)'e', (byte)'E']) < 0 ? Utf8Literals.ParentDirectory : Utf8Literals.Dot), N(property.Name));
                    break;
                case ElementAccessExpressionNode element:
                    Push(N(element.Expression), T(element.QuestionDotToken is null ? Utf8Literals.OpenBracket : Utf8Literals.OptionalElementAccess), N(element.ArgumentExpression), T(Utf8Literals.CloseBracket));
                    break;
                case CallExpressionNode call:
                    Push(N(call.Expression), N(call.QuestionDotToken), TypeArguments(call.TypeArguments), Parameters(call.Arguments));
                    break;
                case NewExpressionNode call:
                    Push(T(Utf8Literals.NewPrefix), N(call.Expression), TypeArguments(call.TypeArguments), List(call.Arguments, Utf8Literals.OpenParen, Utf8Literals.CommaSpace, Utf8Literals.CloseParen));
                    break;
                case TaggedTemplateExpressionNode tagged:
                    Push(N(tagged.Tag), N(tagged.QuestionDotToken), TypeArguments(tagged.TypeArguments), T(Utf8Literals.Space), N(tagged.Template));
                    break;
                case TemplateExpressionNode template:
                    Push(N(template.Head), List(template.TemplateSpans, Utf8String.Empty, Utf8String.Empty, Utf8String.Empty));
                    break;
                case TemplateSpanNode span:
                    Push(N(span.Expression), N(span.Literal));
                    break;
                case ParenthesizedExpressionNode paren:
                    Push(T(Utf8Literals.OpenParen), N(paren.Expression), T(Utf8Literals.CloseParen));
                    break;
                case BinaryExpressionNode binary:
                    Push(
                        N(binary.Left),
                        Annotation(binary.Type),
                        T(binary.OperatorToken?.Kind == K.CommaToken ? Utf8Literals.CommaSpace : Utf8Literals.Space + TokenFacts.Text(binary.OperatorToken!.Kind) + Utf8Literals.Space),
                        N(binary.Right));
                    break;
                case ConditionalExpressionNode conditional:
                    Push(N(conditional.Condition), T(Utf8Literals.QuestionSeparator), N(conditional.WhenTrue), T(Utf8Literals.ColonSeparator), N(conditional.WhenFalse));
                    break;
                case PrefixUnaryExpressionNode unary:
                    Push(T(TokenFacts.Text(unary.Operator)), T(UnarySpace(unary) ? Utf8Literals.Space : Utf8String.Empty), N(unary.Operand));
                    break;
                case PostfixUnaryExpressionNode unary:
                    Push(N(unary.Operand), T(TokenFacts.Text(unary.Operator)));
                    break;
                case DeleteExpressionNode unary:
                    Push(T(Utf8Literals.Delete), N(unary.Expression));
                    break;
                case TypeOfExpressionNode unary:
                    Push(T(Utf8Literals.Typeof), N(unary.Expression));
                    break;
                case VoidExpressionNode unary:
                    Push(T(Utf8Literals.Void), N(unary.Expression));
                    break;
                case AwaitExpressionNode unary:
                    Push(T(Utf8Literals.AwaitPrefix), N(unary.Expression));
                    break;
                case NonNullExpressionNode unary:
                    Push(N(unary.Expression), T(Utf8Literals.Exclamation));
                    break;
                case YieldExpressionNode yield:
                    Push(T(Utf8Literals.Yield), N(yield.AsteriskToken), T(yield.Expression is null ? Utf8String.Empty : Utf8Literals.Space), N(yield.Expression));
                    break;
                case AsExpressionNode assertion:
                    Push(N(assertion.Expression), T(Utf8Literals.As), N(assertion.Type));
                    break;
                case SatisfiesExpressionNode assertion:
                    Push(N(assertion.Expression), T(Utf8Literals.Satisfies), N(assertion.Type));
                    break;
                case TypeAssertionNode assertion:
                    Push(T(Utf8Literals.LessThan), N(assertion.Type), T(Utf8Literals.GreaterThan), N(assertion.Expression));
                    break;
                case ExpressionWithTypeArgumentsNode instantiation:
                    Push(N(instantiation.Expression), TypeArguments(instantiation.TypeArguments));
                    break;
                case ArrayLiteralExpressionNode array:
                    Push(List(array.Elements, Utf8Literals.OpenBracket, Utf8Literals.CommaSpace, HasTrailingComma(array.Elements) ? Utf8Literals.CommaCloseBracket : Utf8Literals.CloseBracket, true));
                    break;
                case OmittedExpressionNode:
                    break;
                case ObjectLiteralExpressionNode obj:
                    Push(
                        obj.Properties is { Count: > 0 }
                            ? List(
                                obj.Properties,
                                Utf8Literals.OpenBraceSpace,
                                Utf8Literals.CommaSpace,
                                HasTrailingComma(obj.Properties) && sourceFile is { ScriptKind: not ScriptKind.JSON } ? Utf8Literals.CommaSpaceCloseBrace : Utf8Literals.SpaceCloseBrace)
                            : T(Utf8Literals.EmptyBraces));
                    break;
                case PropertyAssignmentNode property:
                    Push(
                        Modifiers(property),
                        N(property.Name),
                        N(property.PostfixToken),
                        Annotation(property.Type),
                        T(Utf8Literals.ColonSpace),
                        N(property.Initializer));
                    break;
                case ShorthandPropertyAssignmentNode shorthand:
                    Push(
                        Modifiers(shorthand),
                        N(shorthand.Name),
                        N(shorthand.PostfixToken),
                        Annotation(shorthand.Type),
                        Initializer(shorthand.ObjectAssignmentInitializer));
                    break;
                case SpreadAssignmentNode spread:
                    Push(T(Utf8Literals.Ellipsis), N(spread.Expression));
                    break;
                case SpreadElementNode spread:
                    Push(T(Utf8Literals.Ellipsis), N(spread.Expression));
                    break;
                case ArrowFunctionNode arrow:
                    Push(
                        Modifiers(arrow),
                        TypeArguments(arrow.TypeParameters),
                        SimpleArrow(arrow) ? N(arrow.Parameters![0]) : Parameters(arrow.Parameters),
                        Annotation(arrow.Type),
                        T(Utf8Literals.Space), N(arrow.EqualsGreaterThanToken), T(Utf8Literals.Space),
                        N(arrow.Body));
                    break;
                case FunctionExpressionNode function:
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
                    break;
                case FunctionDeclarationNode function:
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
                    break;
                case MethodDeclarationNode method:
                    Push(
                        Modifiers(method),
                        N(method.AsteriskToken),
                        N(method.Name),
                        N(method.PostfixToken),
                        TypeArguments(method.TypeParameters),
                        Parameters(method.Parameters),
                        Annotation(method.Type),
                        Body(method.Body));
                    break;
                case GetAccessorDeclarationNode accessor:
                    Push(
                        Modifiers(accessor),
                        T(Utf8Literals.GetPrefix),
                        N(accessor.Name),
                        TypeArguments(accessor.TypeParameters),
                        Parameters(accessor.Parameters),
                        Annotation(accessor.Type),
                        Body(accessor.Body));
                    break;
                case SetAccessorDeclarationNode accessor:
                    Push(
                        Modifiers(accessor),
                        T(Utf8Literals.SetPrefix),
                        N(accessor.Name),
                        TypeArguments(accessor.TypeParameters),
                        Parameters(accessor.Parameters),
                        Annotation(accessor.Type),
                        Body(accessor.Body));
                    break;
                case ConstructorDeclarationNode constructor:
                    Push(
                        Modifiers(constructor),
                        T(Utf8Literals.Constructor),
                        TypeArguments(constructor.TypeParameters),
                        Parameters(constructor.Parameters),
                        Annotation(constructor.Type),
                        Body(constructor.Body));
                    break;
                case ParameterDeclarationNode parameter:
                    Push(
                        Modifiers(parameter),
                        N(parameter.DotDotDotToken),
                        N(parameter.Name),
                        N(parameter.QuestionToken),
                        Annotation(parameter.Type),
                        Initializer(parameter.Initializer));
                    break;
                case PropertyDeclarationNode property:
                    Push(
                        Modifiers(property),
                        N(property.Name),
                        N(property.PostfixToken),
                        Annotation(property.Type),
                        Initializer(property.Initializer),
                        T(Utf8Literals.Semicolon));
                    break;
                case ClassExpressionNode type:
                    Push(
                        Modifiers(type),
                        T(Utf8Literals.Class),
                        T(type.Name is null ? Utf8String.Empty : Utf8Literals.Space),
                        N(type.Name),
                        TypeArguments(type.TypeParameters),
                        List(type.HeritageClauses, Utf8Literals.Space, Utf8Literals.Space, Utf8String.Empty),
                        T(Utf8Literals.Space),
                        Braces(type.Members, Utf8Literals.Space, true));
                    break;
                case ClassDeclarationNode type:
                    Push(
                        Modifiers(type),
                        T(Utf8Literals.Class),
                        T(type.Name is null ? Utf8String.Empty : Utf8Literals.Space),
                        N(type.Name),
                        TypeArguments(type.TypeParameters),
                        List(type.HeritageClauses, Utf8Literals.Space, Utf8Literals.Space, Utf8String.Empty),
                        T(Utf8Literals.Space),
                        Braces(type.Members, Utf8Literals.Space, true));
                    break;
                case ClassStaticBlockDeclarationNode block:
                    Push(T(Utf8Literals.StaticPrefix), N(block.Body));
                    break;
                case HeritageClauseNode heritage:
                    Push(T(Utf8String.Concat(TokenFacts.Text(heritage.Token), " "u8)), List(heritage.Types, Utf8String.Empty, Utf8Literals.CommaSpace, Utf8String.Empty));
                    break;
                case BlockNode block:
                    Push(Braces(block.Statements, Utf8Literals.Space, true));
                    break;
                case ModuleBlockNode block:
                    Push(Braces(block.Statements, Utf8Literals.Space, true));
                    break;
                case SourceFileNode file:
                    EmitSourceFile(file);
                    break;
                case ExpressionStatementNode statement:
                    Push(N(statement.Expression), T(Utf8Literals.Semicolon));
                    break;
                case ReturnStatementNode statement:
                    Push(T(Utf8Literals.Return), T(statement.Expression is null ? Utf8String.Empty : Utf8Literals.Space), N(statement.Expression), T(Utf8Literals.Semicolon));
                    break;
                case ThrowStatementNode statement:
                    Push(T(Utf8Literals.Throw), N(statement.Expression), T(Utf8Literals.Semicolon));
                    break;
                case VariableStatementNode statement:
                    Push(Modifiers(statement), N(statement.DeclarationList), T(Utf8Literals.Semicolon));
                    break;
                case VariableDeclarationListNode list:
                    Push(T(VariableKind(list.Flags)), List(list.Declarations, Utf8String.Empty, Utf8Literals.CommaSpace, Utf8String.Empty));
                    break;
                case VariableDeclarationNode variable:
                    Push(N(variable.Name), N(variable.ExclamationToken), Annotation(variable.Type), Initializer(variable.Initializer));
                    break;
                case BindingPatternNode pattern:
                    Push(
                        pattern.Kind == K.ObjectBindingPattern
                            ? Braces(pattern.Elements, Utf8Literals.CommaSpace)
                            : List(pattern.Elements, Utf8Literals.OpenBracket, Utf8Literals.CommaSpace, Utf8Literals.CloseBracket, true));
                    break;
                case BindingElementNode element:
                    Push(
                        N(element.DotDotDotToken),
                        element.PropertyName is null ? default : S(N(element.PropertyName), T(Utf8Literals.ColonSpace)),
                        N(element.Name),
                        Initializer(element.Initializer));
                    break;
                case IfStatementNode statement:
                    Push(
                        T(Utf8Literals.If),
                        N(statement.Expression),
                        T(Utf8Literals.CloseParenSpace),
                        N(statement.ThenStatement),
                        statement.ElseStatement is null ? default : S(T(Utf8Literals.Else), N(statement.ElseStatement)));
                    break;
                case WhileStatementNode statement:
                    Push(T(Utf8Literals.While), N(statement.Expression), T(Utf8Literals.CloseParenSpace), N(statement.Statement));
                    break;
                case DoStatementNode statement:
                    Push(T(Utf8Literals.Do), N(statement.Statement), T(Utf8Literals.DoWhileSuffix), N(statement.Expression), T(Utf8Literals.CloseParenSemicolon));
                    break;
                case ForStatementNode statement:
                    Push(T(Utf8Literals.ForStatementPrefix), N(statement.Initializer), T(statement.Condition is null ? Utf8Literals.Semicolon : Utf8Literals.SemicolonSpace), N(statement.Condition),
                        T(statement.Incrementor is null ? Utf8Literals.Semicolon : Utf8Literals.SemicolonSpace), N(statement.Incrementor), T(Utf8Literals.CloseParenSpace), N(statement.Statement));
                    break;
                case ForInOrOfStatementNode statement:
                    Push(
                        T(Utf8Literals.ForPrefix),
                        statement.AwaitModifier is null ? default : T(Utf8Literals.AwaitPrefix),
                        T(Utf8Literals.OpenParen),
                        N(statement.Initializer),
                        T(statement.Kind == K.ForOfStatement ? Utf8Literals.Of : Utf8Literals.In),
                        N(statement.Expression),
                        T(Utf8Literals.CloseParenSpace),
                        N(statement.Statement));
                    break;
                case BreakStatementNode statement:
                    Push(T(Utf8Literals.Break), statement.Label is null ? default : S(T(Utf8Literals.Space), N(statement.Label)), T(Utf8Literals.Semicolon));
                    break;
                case ContinueStatementNode statement:
                    Push(T(Utf8Literals.Continue), statement.Label is null ? default : S(T(Utf8Literals.Space), N(statement.Label)), T(Utf8Literals.Semicolon));
                    break;
                case LabeledStatementNode statement:
                    Push(N(statement.Label), T(Utf8Literals.ColonSpace), N(statement.Statement));
                    break;
                case WithStatementNode statement:
                    Push(T(Utf8Literals.WithStatementPrefix), N(statement.Expression), T(Utf8Literals.CloseParenSpace), N(statement.Statement));
                    break;
                case SwitchStatementNode statement:
                    Push(T(Utf8Literals.Switch), N(statement.Expression), T(Utf8Literals.CloseParenSpace), N(statement.CaseBlock));
                    break;
                case CaseBlockNode block:
                    Push(Braces(block.Clauses, Utf8Literals.Space, true));
                    break;
                case CaseOrDefaultClauseNode clause:
                    Push(
                        clause.Expression is null ? T(Utf8Literals.Default) : S(T(Utf8Literals.Case), N(clause.Expression)),
                        T(Utf8Literals.Colon),
                        List(clause.Statements, Utf8Literals.Space, Utf8Literals.Space, Utf8String.Empty));
                    break;
                case TryStatementNode statement:
                    Push(
                        T(Utf8Literals.Try),
                        N(statement.TryBlock),
                        statement.CatchClause is null ? default : S(T(Utf8Literals.Space), N(statement.CatchClause)),
                        statement.FinallyBlock is null ? default : S(T(Utf8Literals.Finally), N(statement.FinallyBlock)));
                    break;
                case CatchClauseNode clause:
                    Push(
                        T(Utf8Literals.Catch),
                        clause.VariableDeclaration is null ? default : S(T(Utf8Literals.SpaceOpenParen), N(clause.VariableDeclaration), T(Utf8Literals.CloseParen)),
                        T(Utf8Literals.Space),
                        N(clause.Block));
                    break;
                case TypeAliasDeclarationNode alias:
                    Push(Modifiers(alias), T(Utf8Literals.TypePrefix), N(alias.Name), TypeArguments(alias.TypeParameters), T(Utf8Literals.AssignmentSeparator), N(alias.Type), T(Utf8Literals.Semicolon));
                    break;
                case InterfaceDeclarationNode type:
                    Push(
                        Modifiers(type),
                        T(Utf8Literals.InterfacePrefix),
                        N(type.Name),
                        TypeArguments(type.TypeParameters),
                        List(type.HeritageClauses, Utf8Literals.Space, Utf8Literals.Space, Utf8String.Empty),
                        T(Utf8Literals.Space),
                        Braces(type.Members, Utf8Literals.Space, true));
                    break;
                case EnumDeclarationNode enumeration:
                    Push(Modifiers(enumeration), T(Utf8Literals.Enum), N(enumeration.Name), T(Utf8Literals.Space), Braces(enumeration.Members, Utf8Literals.CommaSpace, true));
                    break;
                case EnumMemberNode member:
                    Push(N(member.Name), Initializer(member.Initializer));
                    break;
                case ModuleDeclarationNode module:
                    EmitModule(module);
                    break;
                case ImportDeclarationNode import:
                    Push(
                        Modifiers(import),
                        T(Utf8Literals.ImportPrefix),
                        N(import.ImportClause),
                        import.ImportClause is null ? default : T(Utf8Literals.From),
                        N(import.ModuleSpecifier),
                        import.Attributes is null ? default : S(T(Utf8Literals.Space), N(import.Attributes)), T(Utf8Literals.Semicolon));
                    break;
                case ImportClauseNode clause:
                    Push(clause.PhaseModifier == K.Unknown ? default : T(Utf8String.Concat(TokenFacts.Text(clause.PhaseModifier), " "u8)), N(clause.Name),
                        clause.Name is not null && clause.NamedBindings is not null ? T(Utf8Literals.CommaSpace) : default, N(clause.NamedBindings));
                    break;
                case NamespaceImportNode import:
                    Push(T(Utf8Literals.NamespaceImportPrefix), N(import.Name));
                    break;
                case NamedImportsNode imports:
                    Push(Braces(imports.Elements, Utf8Literals.CommaSpace));
                    break;
                case ImportSpecifierNode import:
                    Push(
                        import.IsTypeOnly ? T(Utf8Literals.TypePrefix) : default,
                        import.PropertyName is null ? default : S(N(import.PropertyName), T(Utf8Literals.As)),
                        N(import.Name));
                    break;
                case ImportEqualsDeclarationNode import:
                    Push(
                        Modifiers(import),
                        T(import.IsTypeOnly ? Utf8Literals.ImportType : Utf8Literals.ImportPrefix),
                        N(import.Name),
                        T(Utf8Literals.AssignmentSeparator),
                        N(import.ModuleReference),
                        T(Utf8Literals.Semicolon));
                    break;
                case ExternalModuleReferenceNode reference:
                    Push(T(Utf8Literals.Require), N(reference.Expression), T(Utf8Literals.CloseParen));
                    break;
                case ExportDeclarationNode export:
                    Push(
                        Modifiers(export),
                        T(export.IsTypeOnly ? Utf8Literals.ExportType : Utf8Literals.ExportPrefix),
                        export.ExportClause is null ? T(Utf8Literals.Asterisk) : N(export.ExportClause),
                        export.ModuleSpecifier is null ? default : S(T(Utf8Literals.From), N(export.ModuleSpecifier)),
                        export.Attributes is null ? default : S(T(Utf8Literals.Space), N(export.Attributes)), T(Utf8Literals.Semicolon));
                    break;
                case ExportAssignmentNode export:
                    Push(Modifiers(export), T(export.IsExportEquals ? Utf8Literals.ExportAssignmentPrefix : Utf8Literals.ExportDefault), N(export.Expression), T(Utf8Literals.Semicolon));
                    break;
                case NamedExportsNode exports:
                    Push(Braces(exports.Elements, Utf8Literals.CommaSpace));
                    break;
                case ExportSpecifierNode export:
                    Push(
                        export.IsTypeOnly ? T(Utf8Literals.TypePrefix) : default,
                        export.PropertyName is null ? default : S(N(export.PropertyName), T(Utf8Literals.As)),
                        N(export.Name));
                    break;
                case NamespaceExportNode export:
                    Push(T(Utf8Literals.NamespaceImportPrefix), N(export.Name));
                    break;
                case NamespaceExportDeclarationNode export:
                    Push(T(Utf8Literals.ExportAsNamespace), N(export.Name), T(Utf8Literals.Semicolon));
                    break;
                case ImportAttributesNode attributes:
                    Push(T(Utf8String.Concat(TokenFacts.Text(attributes.Token), " "u8)), Braces(attributes.Attributes, Utf8Literals.CommaSpace));
                    break;
                case ImportAttributeNode attribute:
                    Push(N(attribute.Name), T(Utf8Literals.ColonSpace), N(attribute.Value));
                    break;
                case DecoratorNode decorator:
                    Push(T(Utf8Literals.At), N(decorator.Expression));
                    break;
                case TypeParameterDeclarationNode parameter:
                    Push(
                        Modifiers(parameter),
                        N(parameter.Name),
                        parameter.Constraint is null ? default : S(T(Utf8Literals.Extends), N(parameter.Constraint)),
                        Initializer(parameter.DefaultType));
                    break;
                case TypeReferenceNode reference:
                    Push(N(reference.TypeName), TypeArguments(reference.TypeArguments));
                    break;
                case TypeQueryNode query:
                    Push(T(Utf8Literals.Typeof), N(query.ExprName), TypeArguments(query.TypeArguments));
                    break;
                case TypeLiteralNode literal:
                    Push(TypeMembers(literal));
                    break;
                case NotEmittedTypeElementNode:
                    break;
                case ArrayTypeNode array:
                    Push(N(array.ElementType), T(Utf8Literals.EmptyBrackets));
                    break;
                case TupleTypeNode tuple:
                    Push(singleLine?.Contains(tuple) == true ? List(tuple.Elements, Utf8Literals.OpenBracket, Utf8Literals.CommaSpace, Utf8Literals.CloseBracket, true)
                        : tuple.Elements is { Count: > 0 } ? List(tuple.Elements, Utf8Literals.OpenBracketSpace, Utf8Literals.CommaSpace, Utf8Literals.SpaceCloseBracket) : T(Utf8Literals.SpacedBrackets));
                    break;
                case NamedTupleMemberNode member:
                    Push(N(member.DotDotDotToken), N(member.Name), N(member.QuestionToken), T(Utf8Literals.ColonSpace), N(member.Type));
                    break;
                case OptionalTypeNode optional:
                    Push(N(optional.Type), T(Utf8Literals.QuestionMark));
                    break;
                case RestTypeNode rest:
                    Push(T(Utf8Literals.Ellipsis), N(rest.Type));
                    break;
                case UnionTypeNode union:
                    Push(List(union.Types, Utf8String.Empty, Utf8Literals.UnionSeparator, Utf8String.Empty));
                    break;
                case IntersectionTypeNode intersection:
                    Push(List(intersection.Types, Utf8String.Empty, Utf8Literals.IntersectionSeparator, Utf8String.Empty));
                    break;
                case ConditionalTypeNode conditional:
                    Push(
                        N(conditional.CheckType),
                        T(Utf8Literals.Extends),
                        N(conditional.ExtendsType),
                        T(Utf8Literals.QuestionSeparator),
                        N(conditional.TrueType),
                        T(Utf8Literals.ColonSeparator),
                        N(conditional.FalseType));
                    break;
                case InferTypeNode infer:
                    Push(T(Utf8Literals.Infer), N(infer.TypeParameter));
                    break;
                case ParenthesizedTypeNode parenthesized:
                    Push(T(Utf8Literals.OpenParen), N(parenthesized.Type), T(Utf8Literals.CloseParen));
                    break;
                case TypeOperatorNode operation:
                    Push(T(Utf8String.Concat(TokenFacts.Text(operation.Operator), " "u8)), N(operation.Type));
                    break;
                case IndexedAccessTypeNode indexed:
                    Push(N(indexed.ObjectType), T(Utf8Literals.OpenBracket), N(indexed.IndexType), T(Utf8Literals.CloseBracket));
                    break;
                case ImportTypeNode import:
                    Push(T(import.IsTypeOf ? Utf8Literals.TypeofImport : Utf8Literals.ImportCallPrefix), N(import.Argument),
                        import.Attributes is null
                            ? default
                            : S(
                                T(Utf8String.Concat(", { "u8, TokenFacts.Text(import.Attributes.Token), ": "u8)),
                                Braces(import.Attributes.Attributes, Utf8Literals.CommaSpace),
                                T(Utf8Literals.SpaceCloseBrace)),
                        T(Utf8Literals.CloseParen),
                        import.Qualifier is null ? default : S(T(Utf8Literals.Dot), N(import.Qualifier)), TypeArguments(import.TypeArguments));
                    break;
                case MappedTypeNode mapped:
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
                    break;
                case LiteralTypeNode literal:
                    Push(N(literal.Literal));
                    break;
                case TypePredicateNode predicate:
                    Push(
                        predicate.AssertsModifier is null ? default : T(Utf8Literals.Asserts),
                        N(predicate.ParameterName),
                        predicate.Type is null ? default : S(T(Utf8Literals.Is), N(predicate.Type)));
                    break;
                case FunctionTypeNode function:
                    Push(TypeArguments(function.TypeParameters), Parameters(function.Parameters), T(Utf8Literals.ArrowSeparator), N(function.Type));
                    break;
                case ConstructorTypeNode function:
                    Push(
                        Modifiers(function),
                        T(Utf8Literals.NewPrefix),
                        TypeArguments(function.TypeParameters),
                        Parameters(function.Parameters),
                        T(Utf8Literals.ArrowSeparator),
                        N(function.Type));
                    break;
                case PropertySignatureDeclarationNode property:
                    Push(
                        Modifiers(property),
                        N(property.Name),
                        N(property.PostfixToken),
                        Annotation(property.Type),
                        Initializer(property.Initializer),
                        T(Utf8Literals.Semicolon));
                    break;
                case MethodSignatureDeclarationNode method:
                    Push(
                        Modifiers(method),
                        N(method.Name),
                        N(method.PostfixToken),
                        TypeArguments(method.TypeParameters),
                        Parameters(method.Parameters),
                        Annotation(method.Type),
                        T(Utf8Literals.Semicolon));
                    break;
                case CallSignatureDeclarationNode signature:
                    Push(TypeArguments(signature.TypeParameters), Parameters(signature.Parameters), Annotation(signature.Type), T(Utf8Literals.Semicolon));
                    break;
                case ConstructSignatureDeclarationNode signature:
                    Push(
                        T(Utf8Literals.NewPrefix),
                        TypeArguments(signature.TypeParameters),
                        Parameters(signature.Parameters),
                        Annotation(signature.Type),
                        T(Utf8Literals.Semicolon));
                    break;
                case IndexSignatureDeclarationNode signature:
                    Push(Modifiers(signature), List(signature.Parameters, Utf8Literals.OpenBracket, Utf8Literals.CommaSpace, Utf8Literals.CloseBracket, true), Annotation(signature.Type), T(Utf8Literals.Semicolon));
                    break;
                case TemplateLiteralTypeNode template:
                    Push(N(template.Head), List(template.TemplateSpans, Utf8String.Empty, Utf8String.Empty, Utf8String.Empty));
                    break;
                case TemplateLiteralTypeSpanNode span:
                    Push(N(span.Type), N(span.Literal));
                    break;
                case JsxElementNode jsx:
                    Push(N(jsx.OpeningElement), List(jsx.Children, Utf8String.Empty, Utf8String.Empty, Utf8String.Empty), N(jsx.ClosingElement));
                    break;
                case JsxSelfClosingElementNode jsx:
                    Push(T(Utf8Literals.LessThan), N(jsx.TagName), TypeArguments(jsx.TypeArguments), N(jsx.Attributes), T(Utf8Literals.JsxSelfClosingTagEnd));
                    break;
                case JsxOpeningElementNode jsx:
                    Push(T(Utf8Literals.LessThan), N(jsx.TagName), TypeArguments(jsx.TypeArguments), N(jsx.Attributes), T(Utf8Literals.GreaterThan));
                    break;
                case JsxClosingElementNode jsx:
                    Push(T(Utf8Literals.JsxClosingTagPrefix), N(jsx.TagName), T(Utf8Literals.GreaterThan));
                    break;
                case JsxAttributesNode attributes:
                    Push(List(attributes.Properties, attributes.Properties is { Count: > 0 } ? Utf8Literals.Space : Utf8String.Empty, Utf8Literals.Space, Utf8String.Empty));
                    break;
                case JsxAttributeNode attribute:
                    Push(N(attribute.Name), attribute.Initializer is null ? default : S(T(Utf8Literals.EqualsToken), N(attribute.Initializer)));
                    break;
                case JsxSpreadAttributeNode attribute:
                    Push(T(Utf8Literals.OpenBraceEllipsis), N(attribute.Expression), T(Utf8Literals.CloseBrace));
                    break;
                case JsxExpressionNode expression:
                    Push(T(Utf8Literals.OpenBrace), N(expression.DotDotDotToken), N(expression.Expression), T(Utf8Literals.CloseBrace));
                    break;
                case JsxTextNode text:
                    output.Append(text.Text.Span);
                    break;
                case JsxFragmentNode fragment:
                    Push(T(Utf8Literals.EmptyJsxOpeningFragment), List(fragment.Children, Utf8String.Empty, Utf8String.Empty, Utf8String.Empty), T(Utf8Literals.JsxClosingFragment));
                    break;
                case JsxNamespacedNameNode name:
                    Push(N(name.Namespace), T(Utf8Literals.Colon), N(name.Name));
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
