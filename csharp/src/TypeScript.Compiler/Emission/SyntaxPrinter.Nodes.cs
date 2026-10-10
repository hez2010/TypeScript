using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

public sealed partial class SyntaxPrinter
{
    // Keep each emitter's span temporaries out of the dispatcher's stack frame.
    private void Emit(SyntaxNode node)
    {
        if (context.GetSnippetTabStop(node) is { } order)
        {
            pending.Push(new(Text: Utf8String.FromString("$" + order.ToString(System.Globalization.CultureInfo.InvariantCulture)), Raw: true));
            return;
        }
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
            case JsxOpeningFragmentNode:
                Literal("<>"u8);
                break;
            case JsxClosingFragmentNode:
                Literal("</>"u8);
                break;
            case PartiallyEmittedExpressionNode expression:
                var emitFlags = context.GetFlags(expression);
                Push(expression.Pos != expression.Expression!.Pos && (emitFlags & EmitFlags.NoLeadingComments) == 0
                        ? new(CommentPosition: expression.Expression.Pos, ListComment: true) : default,
                    N(expression.Expression),
                    expression.End != expression.Expression.End && (emitFlags & EmitFlags.NoTrailingComments) == 0
                        ? new(CommentPosition: expression.Expression.End) : default);
                break;
            case NotEmittedStatementNode or NotEmittedTypeElementNode:
                break;
            case SyntaxNode when node.Kind == K.ThisType:
                Literal("this"u8);
                break;
            case SyntaxNode when node.Kind is K.EmptyStatement or K.SemicolonClassElement:
                if (node.Kind == K.EmptyStatement && states.Count > 1 && ParentState()!.Node is IfStatementNode or WhileStatementNode or DoStatementNode or ForStatementNode or ForInOrOfStatementNode or WithStatementNode)
                    Literal((byte)';');
                else
                    writer.WriteTrailingSemicolon();
                break;
            case SyntaxNode when node.Kind == K.DebuggerStatement:
                Literal("debugger"u8);
                writer.WriteTrailingSemicolon();
                break;
            default:
                Utf8String token = TokenFacts.Text(node.Kind);
                if (token.Length == 0)
                    throw new NotSupportedException($"Syntax printing requires {node.Kind}");
                Literal(token.Span);
                if (node.Kind == K.DebuggerStatement)
                    Literal((byte)';');
                break;
        }
    }

    private void Emit(IdentifierNode id) =>
        Literal(HelperText(id));

    private void Emit(PrivateIdentifierNode id) =>
        Literal(nameGenerator.GenerateName(id));

    private void Emit(StringLiteralNode literal) =>
        Literal(StringText(literal));

    private void Emit(NumericLiteralNode literal) =>
        Literal(NumberText(literal).Span);

    private void Emit(BigIntLiteralNode literal) =>
        Literal(literal.Text.Span);

    private void Emit(RegularExpressionLiteralNode literal) =>
        Literal(options.TerminateUnterminatedLiterals && IsUnterminated(literal)
            ? literal.Text + (literal.Text.EndsWith("\\"u8, StringComparison.Ordinal) ? " /"u8 : "/"u8) : literal.Text);

    private void Emit(NoSubstitutionTemplateLiteralNode literal) =>
        Literal(
            OriginalText(literal) ?? Checker.QuoteSymbolText(
                literal.Text,
                (byte)'`',
                !options.NeverAsciiEscape && (context.GetFlags(literal) & EmitFlags.NoAsciiEscaping) == 0).Span);

    private void Emit(TemplateHeadNode literal) =>
        Literal(Utf8String.Concat("`"u8,
            TemplateText(literal.Text, literal.RawText, (context.GetFlags(literal) & EmitFlags.NoAsciiEscaping) != 0).Span, "${"u8));

    private void Emit(TemplateMiddleNode literal) =>
        Literal(Utf8String.Concat("}"u8,
            TemplateText(literal.Text, literal.RawText, (context.GetFlags(literal) & EmitFlags.NoAsciiEscaping) != 0).Span, "${"u8));

    private void Emit(TemplateTailNode literal) =>
        Literal(Utf8String.Concat("}"u8,
            TemplateText(literal.Text, literal.RawText, (context.GetFlags(literal) & EmitFlags.NoAsciiEscaping) != 0).Span, "`"u8));

    private void Emit(ComputedPropertyNameNode computed) =>
        Push(T(Utf8Literals.OpenBracket), N(computed.Expression), T(Utf8Literals.CloseBracket));

    private void Emit(QualifiedNameNode qualified) =>
        Push(N(qualified.Left), T(Utf8Literals.Dot), N(qualified.Right));

    private void Emit(MetaPropertyNode meta) =>
        Push(T(Utf8String.Concat(TokenFacts.Text(meta.KeywordToken), "."u8)), N(meta.Name));

    private void Emit(PropertyAccessExpressionNode property)
    {
        int dotStart = SkipTrivia(property.Expression!.End);
        bool positioned = property.Pos >= 0 && property.End >= 0 && property.Expression is { Pos: >= 0, End: >= 0 };
        bool before = positioned && LineOf(property.Expression.End) != LineOf(dotStart);
        bool after = positioned && property.Name is { Pos: >= 0 } && LineOf(dotStart) != LineOf(SkipTrivia(property.Name.Pos));
        var dot = property.Expression is NumericLiteralNode number && (number.TokenFlags & TokenFlags.WithSpecifier) == 0
            && NumberText(number).Span.IndexOfAny([(byte)'.', (byte)'e', (byte)'E']) < 0 ? Utf8Literals.ParentDirectory : Utf8Literals.Dot;
        Push(N(property.Expression), Separator(before, false), property.QuestionDotToken is not null ? N(property.QuestionDotToken) : T(dot),
            Separator(after, false), N(property.Name), EndSeparator(after), EndSeparator(before));
    }

    private void Emit(ElementAccessExpressionNode element) =>
        Push(N(element.Expression), N(element.QuestionDotToken), T(Utf8Literals.OpenBracket), N(element.ArgumentExpression), T(Utf8Literals.CloseBracket));

    private void Emit(CallExpressionNode call) =>
        Push(IndirectTarget(call, call.Expression), N(call.QuestionDotToken), TypeArguments(call.TypeArguments), Parameters(call.Arguments));

    private void Emit(NewExpressionNode call) =>
        Push(T(Utf8Literals.NewPrefix), N(call.Expression), TypeArguments(call.TypeArguments), List(call.Arguments, Utf8Literals.OpenParen, Utf8Literals.CommaSpace, Utf8Literals.CloseParen));

    private void Emit(TaggedTemplateExpressionNode tagged) =>
        Push(IndirectTarget(tagged, tagged.Tag), N(tagged.QuestionDotToken), TypeArguments(tagged.TypeArguments), T(Utf8Literals.Space), N(tagged.Template));

    private Part IndirectTarget(SyntaxNode owner, SyntaxNode? expression) => (context.GetFlags(owner) & EmitFlags.IndirectCall) != 0
        ? new(Parts: [T("(0, "u8), N(expression), T(")"u8)]) : N(expression);

    private void Emit(TemplateExpressionNode template) =>
        Push(N(template.Head), List(template.TemplateSpans, Utf8String.Empty, Utf8String.Empty, Utf8String.Empty));

    private void Emit(TemplateSpanNode span) =>
        Push(N(span.Expression), N(span.Literal));

    private void Emit(ParenthesizedExpressionNode paren) =>
        Push(T(Utf8Literals.OpenParen), N(paren.Expression), options.PreserveSourceNewlines && ClosingLines(paren, paren.Expression) > 0
            ? new(NewLine: true) : default, T(Utf8Literals.CloseParen));

    private void Emit(BinaryExpressionNode binary)
    {
        bool before = NewLineBetween(binary.Left, binary.OperatorToken), after = NewLineBetween(binary.OperatorToken, binary.Right);
        Push(N(binary.Left), Annotation(binary.Type), Separator(before, binary.OperatorToken?.Kind != K.CommaToken),
            Token(binary.OperatorToken!), Separator(after), N(binary.Right), EndSeparator(after), EndSeparator(before));
    }

    private void Emit(ConditionalExpressionNode conditional)
    {
        bool beforeQuestion = NewLineBetween(conditional.Condition, conditional.QuestionToken);
        bool afterQuestion = NewLineBetween(conditional.QuestionToken, conditional.WhenTrue);
        bool beforeColon = NewLineBetween(conditional.WhenTrue, conditional.ColonToken);
        bool afterColon = NewLineBetween(conditional.ColonToken, conditional.WhenFalse);
        Push(N(conditional.Condition), Separator(beforeQuestion), N(conditional.QuestionToken), Separator(afterQuestion), N(conditional.WhenTrue),
            EndSeparator(afterQuestion), EndSeparator(beforeQuestion), Separator(beforeColon), N(conditional.ColonToken),
            Separator(afterColon), N(conditional.WhenFalse), EndSeparator(afterColon), EndSeparator(beforeColon));
    }

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
        Push(T(Utf8Literals.Yield), N(yield.AsteriskToken), T(yield.Expression is null ? Utf8String.Empty : Utf8Literals.Space), N(ProtectFromAsi(yield.Expression)));

    private void Emit(AsExpressionNode assertion) =>
        Push(N(assertion.Expression), T(Utf8Literals.As), N(assertion.Type));

    private void Emit(SatisfiesExpressionNode assertion) =>
        Push(N(assertion.Expression), T(Utf8Literals.Satisfies), N(assertion.Type));

    private void Emit(TypeAssertionNode assertion) =>
        Push(T(Utf8Literals.LessThan), N(assertion.Type), T(Utf8Literals.GreaterThan), N(assertion.Expression));

    private void Emit(ExpressionWithTypeArgumentsNode instantiation) =>
        Push(N(instantiation.Expression), TypeArguments(instantiation.TypeArguments));

    private void Emit(ArrayLiteralExpressionNode array)
    {
        if (array.Elements is not { Count: > 0 })
        {
            Push(List(array.Elements, Utf8Literals.OpenBracket, Utf8Literals.CommaSpace, Utf8Literals.CloseBracket, true));
            return;
        }
        bool preserveLines = array.Pos >= 0 && array.Elements is { Count: > 1 } originalElements
            && originalElements.Skip(1).Where((node, index) => context.MostOriginal(node).Parent is ArrayLiteralExpressionNode parent
                && parent == context.MostOriginal(originalElements[index]).Parent && NewLineBetween(originalElements[index], node)).Any();
        if (!array.MultiLine && !preserveLines)
        {
            Push(List(array.Elements, Utf8Literals.OpenBracket, Utf8Literals.CommaSpace, HasTrailingComma(array.Elements) ? Utf8Literals.CommaCloseBracket : Utf8Literals.CloseBracket, true, indent: true));
            return;
        }
        var parts = new List<Part> { T(Utf8Literals.OpenBracket), ListPosition(array.Elements), new(IndentationChange: 1, NewLine: array.MultiLine) };
        if (array.Elements is { } elements)
            for (int i = 0; i < elements.Count; i++)
            {
                if (i != 0)
                {
                    var previous = elements[i - 1];
                    var next = elements[i];
                    bool sameLine = previous.Pos >= 0 && previous.End >= 0 && next.Pos >= 0 && next.End >= 0
                        && context.MostOriginal(previous).Parent is { } parent && parent == context.MostOriginal(next).Parent && !NewLineBetween(previous, next);
                    parts.Add(T(sameLine ? Utf8Literals.CommaSpace : Utf8Literals.Comma));
                    if (!sameLine) parts.Add(new(NewLine: true));
                }
                parts.Add(new(CommentPosition: (context.GetFlags(elements[i]) & EmitFlags.NoLeadingComments) == 0 ? context.GetCommentRange(elements[i]).Pos : -1, ListComment: true));
                parts.Add(N(elements[i]));
            }
        if (HasTrailingComma(array.Elements)) parts.Add(T(Utf8Literals.Comma));
        if (array.Elements is { Count: > 0 } arrayElements)
            parts.Add(new(CommentPosition: HasTrailingComma(array.Elements) ? arrayElements.End : arrayElements[^1].End));
        parts.Add(new(IndentationChange: -1, NewLine: true));
        parts.Add(ListPosition(array.Elements, true));
        parts.Add(T(Utf8Literals.CloseBracket));
        Push(new Part(Parts: parts));
    }

    private void Emit(ObjectLiteralExpressionNode obj)
    {
        if (obj.MultiLine && obj.Properties is { Count: > 0 })
        {
            // Pushed straight onto the work stack in reverse order instead of building a parts list:
            // a container would have to be filled (copying every part), then expanded (copying them
            // all again), and this shape is one of the printer's largest allocation sources.
            pending.Push(new(NameScopeChange: -1));
            pending.Push(T(Utf8Literals.CloseBrace));
            pending.Push(ListPosition(obj.Properties, true));
            pending.Push(new(IndentationChange: -1, NewLine: true));
            if (HasTrailingComma(obj.Properties) && sourceFile is { ScriptKind: not ScriptKind.JSON })
            {
                pending.Push(new(CommentPosition: obj.Properties!.End, TrailingComment: true));
                pending.Push(T(Utf8Literals.Comma));
            }
            if (obj.Properties is { } properties)
                for (int i = properties.Count - 1; i >= 0; i--)
                {
                    pending.Push(N(properties[i]));
                    if (i == 0)
                        pending.Push(new(CommentPosition: (context.GetFlags(properties[i]) & EmitFlags.NoLeadingComments) == 0 ? context.GetCommentRange(properties[i]).Pos : -1, ListComment: true));
                    else
                    {
                        var previous = properties[i - 1];
                        var next = properties[i];
                        bool sameLine = previous.Pos >= 0 && previous.End >= 0 && next.Pos >= 0 && next.End >= 0
                            && context.MostOriginal(previous).Parent is { } parent && parent == context.MostOriginal(next).Parent && !NewLineBetween(previous, next);
                        if (!sameLine) pending.Push(new(NewLine: true));
                        pending.Push(new(CommentPosition: (context.GetFlags(next) & EmitFlags.NoLeadingComments) == 0
                            ? context.GetCommentRange(next).Pos : -1, TrailingComment: true));
                        pending.Push(T(sameLine ? Utf8Literals.CommaSpace : Utf8Literals.Comma));
                    }
                }
            pending.Push(new(IndentationChange: 1, NewLine: true));
            pending.Push(ListPosition(obj.Properties));
            pending.Push(T(Utf8Literals.OpenBrace));
            pending.Push(Generate(obj.Properties));
            pending.Push(new(NameScopeChange: 1));
            return;
        }
        pending.Push(new(NameScopeChange: -1));
        if (obj.Properties is { Count: > 0 })
            PushList(
                obj.Properties,
                Utf8Literals.OpenBraceSpace,
                Utf8Literals.CommaSpace,
                HasTrailingComma(obj.Properties) && sourceFile is { ScriptKind: not ScriptKind.JSON } ? Utf8Literals.CommaSpaceCloseBrace : Utf8Literals.SpaceCloseBrace, indent: true);
        else
            PushList(obj.Properties, Utf8Literals.OpenBrace, default, Utf8Literals.CloseBrace, required: true);
        pending.Push(Generate(obj.Properties));
        pending.Push(new(NameScopeChange: 1));
    }

    private void Emit(PropertyAssignmentNode property)
    {
        Push(N(property.Initializer));
        Push(T(Utf8Literals.ColonSpace));
        PushAnnotation(property.Type);
        Push(Modifiers(property), N(property.Name), N(property.PostfixToken));
    }

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
            Scoped(Generate(arrow.Parameters), TypeArguments(arrow.TypeParameters),
            SimpleArrow(arrow) ? N(arrow.Parameters![0]) : Parameters(arrow.Parameters),
            Annotation(arrow.Type),
            T(Utf8Literals.Space), N(arrow.EqualsGreaterThanToken), T(Utf8Literals.Space),
            N(arrow.Body)));

    private void Emit(FunctionExpressionNode function) =>
        Push(
            Modifiers(function),
            T(Utf8Literals.Function),
            N(function.AsteriskToken),
            T(Utf8Literals.Space),
            N(function.Name),
            Scoped(Generate(function.Parameters), TypeArguments(function.TypeParameters), Parameters(function.Parameters),
                Annotation(function.Type), Body(function.Body)));

    private void Emit(FunctionDeclarationNode function) =>
        Push(
            Modifiers(function),
            T(Utf8Literals.Function),
            N(function.AsteriskToken),
            T(Utf8Literals.Space),
            N(function.Name),
            Scoped(Generate(function.Parameters), TypeArguments(function.TypeParameters), Parameters(function.Parameters),
                Annotation(function.Type), Body(function.Body)));

    private void Emit(MethodDeclarationNode method) =>
        Push(
            Modifiers(method),
            N(method.AsteriskToken),
            N(method.Name),
            N(method.PostfixToken),
            Scoped(Generate(method.Parameters), TypeArguments(method.TypeParameters), Parameters(method.Parameters),
                Annotation(method.Type), Body(method.Body)));

    private void Emit(GetAccessorDeclarationNode accessor) =>
        Push(
            Modifiers(accessor),
            T(Utf8Literals.GetPrefix),
            N(accessor.Name),
            Scoped(Generate(accessor.Parameters), TypeArguments(accessor.TypeParameters), Parameters(accessor.Parameters),
                Annotation(accessor.Type), Body(accessor.Body)));

    private void Emit(SetAccessorDeclarationNode accessor) =>
        Push(
            Modifiers(accessor),
            T(Utf8Literals.SetPrefix),
            N(accessor.Name),
            Scoped(Generate(accessor.Parameters), TypeArguments(accessor.TypeParameters), Parameters(accessor.Parameters),
                Annotation(accessor.Type), Body(accessor.Body)));

    private void Emit(ConstructorDeclarationNode constructor) =>
        Push(
            Modifiers(constructor),
            T(Utf8Literals.Constructor),
            Scoped(Generate(constructor.Parameters), TypeArguments(constructor.TypeParameters), Parameters(constructor.Parameters),
                Annotation(constructor.Type), Body(constructor.Body)));

    private void Emit(ParameterDeclarationNode parameter)
    {
        PushInitializer(parameter.Initializer);
        PushAnnotation(parameter.Type);
        Push(Modifiers(parameter), N(parameter.DotDotDotToken), N(parameter.Name), N(parameter.QuestionToken));
    }

    private void Emit(PropertyDeclarationNode property) =>
        Push(
            Modifiers(property),
            N(property.Name),
            N(property.PostfixToken),
            Annotation(property.Type),
            Initializer(property.Initializer),
            Semicolon());

    private void Emit(ClassExpressionNode type) =>
        Push(
            Modifiers(type),
            T(Utf8Literals.Class),
            T(type.Name is null ? Utf8String.Empty : Utf8Literals.Space),
            N(type.Name),
            TypeArguments(type.TypeParameters),
            new(IndentationChange: (context.GetFlags(type) & EmitFlags.Indented) != 0 ? 1 : 0),
            List(type.HeritageClauses, default, default, default),
            T(Utf8Literals.Space),
            Scoped(Generate(type.Members), BlockMembers(type.Members)),
            new(IndentationChange: (context.GetFlags(type) & EmitFlags.Indented) != 0 ? -1 : 0));

    private void Emit(ClassDeclarationNode type) =>
        Push(
            Modifiers(type),
            T(Utf8Literals.Class),
            T(type.Name is null ? Utf8String.Empty : Utf8Literals.Space),
            N(type.Name),
            TypeArguments(type.TypeParameters),
            new(IndentationChange: (context.GetFlags(type) & EmitFlags.Indented) != 0 ? 1 : 0),
            List(type.HeritageClauses, default, default, default),
            T(Utf8Literals.Space),
            Scoped(Generate(type.Members), BlockMembers(type.Members)),
            new(IndentationChange: (context.GetFlags(type) & EmitFlags.Indented) != 0 ? -1 : 0));

    private void Emit(ClassStaticBlockDeclarationNode block) =>
        Push(T(Utf8Literals.StaticPrefix), Scoped(N(block.Body)));

    private void Emit(HeritageClauseNode heritage) =>
        Push(T(Utf8String.Concat(" "u8, TokenFacts.Text(heritage.Token), " "u8)), List(heritage.Types, Utf8String.Empty, Utf8Literals.CommaSpace, Utf8String.Empty));

    private void Emit(BlockNode block) =>
        Push(BlockBody(block));

    private void Emit(ModuleBlockNode block) =>
        Push(block.Statements is not { Count: > 0 } && SingleLine(block) ? Braces(block.Statements, default, true) : BlockMembers(block.Statements, endComments: true));

    private void Emit(SourceFileNode file) =>
        EmitSourceFile(file);

    private void Emit(ExpressionStatementNode statement) =>
        Push(N(statement.Expression), sourceFile?.ScriptKind == ScriptKind.JSON && (statement.Expression!.Flags & NodeFlags.Synthesized) == 0 ? default : Semicolon());

    private void Emit(ReturnStatementNode statement) =>
        Push(T(Utf8Literals.Return), T(statement.Expression is null ? Utf8String.Empty : Utf8Literals.Space), N(ProtectFromAsi(statement.Expression)), Semicolon());

    private void Emit(ThrowStatementNode statement) =>
        Push(T(Utf8Literals.Throw), N(ProtectFromAsi(statement.Expression)), Semicolon());

    private void Emit(VariableStatementNode statement) =>
        Push(Modifiers(statement), N(statement.DeclarationList), Semicolon());

    private void Emit(VariableDeclarationListNode list)
    {
        PushList(list.Declarations, Utf8String.Empty, Utf8Literals.CommaSpace, Utf8String.Empty);
        pending.Push(T(VariableKind(list.Flags)));
    }

    private void Emit(VariableDeclarationNode variable)
    {
        PushInitializer(variable.Initializer);
        PushAnnotation(variable.Type);
        Push(N(variable.Name), N(variable.ExclamationToken));
    }

    private void Emit(BindingPatternNode pattern) =>
        Push(
            pattern.Kind == K.ObjectBindingPattern
                ? pattern.Elements is { Count: > 0, HasTrailingComma: true }
                    ? List(pattern.Elements, Utf8Literals.OpenBraceSpace, Utf8Literals.CommaSpace, ", }"u8)
                    : Braces(pattern.Elements, Utf8Literals.CommaSpace)
                : List(pattern.Elements, Utf8Literals.OpenBracket, Utf8Literals.CommaSpace,
                    pattern.Elements is { Count: > 0 } elements && (elements.HasTrailingComma || elements[^1] is OmittedExpressionNode or BindingElementNode { Name: null })
                        ? Utf8Literals.CommaCloseBracket : Utf8Literals.CloseBracket, true));

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
            T(Utf8Literals.CloseParen),
            Embedded(statement.ThenStatement),
            statement.ElseStatement is null ? default : S(options.PreserveSourceNewlines
                ? LineOrSpace(statement, statement.ThenStatement!, statement.ElseStatement) : new(NewLine: true), T("else"u8),
                statement.ElseStatement is IfStatementNode ? S(T(Utf8Literals.Space), N(statement.ElseStatement)) : Embedded(statement.ElseStatement)));

    private void Emit(WhileStatementNode statement) =>
        Push(T(Utf8Literals.While), N(statement.Expression), T(Utf8Literals.CloseParen), Embedded(statement.Statement));

    private void Emit(DoStatementNode statement) =>
        Push(T("do"u8), Embedded(statement.Statement), options.PreserveSourceNewlines
            ? LineOrSpace(statement, statement.Statement!, statement.Expression!) : statement.Statement is BlockNode ? T(Utf8Literals.Space) : new(NewLine: true),
            T(Utf8Literals.While), N(statement.Expression), T(Utf8Literals.CloseParen), Semicolon());

    private void Emit(ForStatementNode statement) =>
        Push(T(Utf8Literals.ForStatementPrefix), N(statement.Initializer), T(statement.Condition is null ? Utf8Literals.Semicolon : Utf8Literals.SemicolonSpace), N(statement.Condition),
            T(statement.Incrementor is null ? Utf8Literals.Semicolon : Utf8Literals.SemicolonSpace), N(statement.Incrementor), T(Utf8Literals.CloseParen), Embedded(statement.Statement));

    private void Emit(ForInOrOfStatementNode statement) =>
        Push(
            T(Utf8Literals.ForPrefix),
            statement.AwaitModifier is null ? default : S(N(statement.AwaitModifier), T(Utf8Literals.Space)),
            T(Utf8Literals.OpenParen),
            N(statement.Initializer),
            T(statement.Kind == K.ForOfStatement ? Utf8Literals.Of : Utf8Literals.In),
            N(statement.Expression),
            T(Utf8Literals.CloseParen),
            Embedded(statement.Statement));

    private void Emit(BreakStatementNode statement) =>
        Push(T(Utf8Literals.Break), statement.Label is null ? default : S(T(Utf8Literals.Space), N(statement.Label)), Semicolon());

    private void Emit(ContinueStatementNode statement) =>
        Push(T(Utf8Literals.Continue), statement.Label is null ? default : S(T(Utf8Literals.Space), N(statement.Label)), Semicolon());

    private void Emit(LabeledStatementNode statement) =>
        Push(N(statement.Label), T(Utf8Literals.ColonSpace), N(statement.Statement));

    private void Emit(WithStatementNode statement) =>
        Push(T(Utf8Literals.WithStatementPrefix), N(statement.Expression), T(Utf8Literals.CloseParen), Embedded(statement.Statement));

    private void Emit(SwitchStatementNode statement) =>
        Push(T(Utf8Literals.Switch), N(statement.Expression), T(Utf8Literals.CloseParenSpace), N(statement.CaseBlock));

    private void Emit(CaseBlockNode block) =>
        Push(BlockMembers(block.Clauses));

    private void Emit(CaseOrDefaultClauseNode clause)
    {
        var parts = new List<Part> { clause.Expression is null ? T(Utf8Literals.Default) : S(T(Utf8Literals.Case), N(clause.Expression)), T(Utf8Literals.Colon) };
        bool single = clause.Statements is { Count: 1 } statements && (sourceFile is null || clause.Pos < 0 || clause.End < 0
            || statements[0].Pos < 0 || statements[0].End < 0 || LineOf(SkipTrivia(clause.Pos)) == LineOf(SkipTrivia(statements[0].Pos)));
        if (!single) parts.Add(new(IndentationChange: 1));
        parts.Add(ListPosition(clause.Statements));
        SyntaxNode? previous = null;
        foreach (var statement in clause.Statements ?? new([]))
        {
            parts.Add(options.PreserveSourceNewlines ? ListBoundary(clause, previous, statement, !single, single) : single ? T(Utf8Literals.Space) : new(NewLine: true));
            parts.Add(N(statement));
            previous = statement;
        }
        if (!single) parts.Add(new(IndentationChange: -1));
        parts.Add(ListPosition(clause.Statements, true));
        Push(new Part(Parts: parts));
    }

    private void Emit(TryStatementNode statement) =>
        Push(
            T(Utf8Literals.Try),
            N(statement.TryBlock),
            statement.CatchClause is null ? default : S(LineOrSpace(statement, statement.TryBlock!, statement.CatchClause), N(statement.CatchClause)),
            statement.FinallyBlock is null ? default : S(LineOrSpace(statement, (SyntaxNode?)statement.CatchClause ?? statement.TryBlock!, statement.FinallyBlock), T("finally "u8), N(statement.FinallyBlock)));

    private void Emit(CatchClauseNode clause) =>
        Push(
            T(Utf8Literals.Catch),
            clause.VariableDeclaration is null ? default : S(T(Utf8Literals.SpaceOpenParen), N(clause.VariableDeclaration), T(Utf8Literals.CloseParen)),
            T(Utf8Literals.Space),
            N(clause.Block));

    private void Emit(TypeAliasDeclarationNode alias) =>
        Push(Modifiers(alias), T(Utf8Literals.TypePrefix), N(alias.Name), TypeArguments(alias.TypeParameters), T(Utf8Literals.AssignmentSeparator), N(alias.Type), Semicolon());

    private void Emit(InterfaceDeclarationNode type) =>
        Push(
            Modifiers(type),
            T(Utf8Literals.InterfacePrefix),
            N(type.Name),
            TypeArguments(type.TypeParameters),
            List(type.HeritageClauses, default, default, default),
            T(Utf8Literals.Space),
            Scoped(Generate(type.Members), BlockMembers(type.Members)));

    private void Emit(EnumDeclarationNode enumeration) =>
        Push(Modifiers(enumeration), T(Utf8Literals.Enum), N(enumeration.Name), T(Utf8Literals.Space), BlockMembers(enumeration.Members, comma: true));

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
            import.Attributes is null ? default : S(T(Utf8Literals.Space), N(import.Attributes)), Semicolon());

    private void Emit(ImportClauseNode clause) =>
        Push(clause.PhaseModifier == K.Unknown ? default : T(Utf8String.Concat(TokenFacts.Text(clause.PhaseModifier), " "u8)), N(clause.Name),
            clause.Name is not null && clause.NamedBindings is not null ? T(Utf8Literals.CommaSpace) : default, N(clause.NamedBindings));

    private void Emit(NamespaceImportNode import) =>
        Push(T(Utf8Literals.NamespaceImportPrefix), N(import.Name));

    private void Emit(NamedImportsNode imports) =>
        Push(NamedBindings(imports, imports.Elements));

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
            Semicolon());

    private void Emit(ExternalModuleReferenceNode reference) =>
        Push(T(Utf8Literals.Require), N(reference.Expression), T(Utf8Literals.CloseParen));

    private void Emit(ExportDeclarationNode export) =>
        Push(
            Modifiers(export),
            T(export.IsTypeOnly ? Utf8Literals.ExportType : Utf8Literals.ExportPrefix),
            export.ExportClause is null ? T(Utf8Literals.Asterisk) : N(export.ExportClause),
            export.ModuleSpecifier is null ? default : S(T(Utf8Literals.From), N(export.ModuleSpecifier)),
            export.Attributes is null ? default : S(T(Utf8Literals.Space), N(export.Attributes)), Semicolon());

    private void Emit(ExportAssignmentNode export) =>
        Push(Modifiers(export), T(export.IsExportEquals ? Utf8Literals.ExportAssignmentPrefix : Utf8Literals.ExportDefault), N(export.Expression), Semicolon());

    private void Emit(NamedExportsNode exports) =>
        Push(NamedBindings(exports, exports.Elements));

    private Part NamedBindings(SyntaxNode parent, NodeList? nodes)
    {
        if (!options.PreserveSourceNewlines || nodes is not { Count: > 0 })
            return nodes is { Count: > 0, HasTrailingComma: true }
                ? List(nodes, Utf8Literals.OpenBraceSpace, Utf8Literals.CommaSpace, ", }"u8) : Braces(nodes, Utf8Literals.CommaSpace);
        bool multiLine = (context.GetFlags(parent) & EmitFlags.MultiLine) != 0;
        var parts = new List<Part> { T("{"u8), ListPosition(nodes) };
        if (multiLine) parts.Add(new(IndentationChange: 1));
        for (int i = 0; i < nodes.Count; i++)
        {
            if (i > 0) parts.Add(T(","u8));
            bool newLine = i == 0 ? multiLine || LeadingLines(parent, nodes[i]) > 0 : SeparatingLines(nodes[i - 1], nodes[i], multiLine) > 0;
            bool indent = newLine && i > 0 && !multiLine;
            if (indent) parts.Add(new(IndentationChange: 1));
            parts.Add(newLine ? new(NewLine: true) : T(" "u8));
            parts.Add(new(CommentPosition: (context.GetFlags(nodes[i]) & EmitFlags.NoLeadingComments) == 0 ? context.GetCommentRange(nodes[i]).Pos : -1, ListComment: true));
            parts.Add(N(nodes[i]));
            if (indent) parts.Add(new(IndentationChange: -1));
        }
        if (nodes.HasTrailingComma) parts.Add(T(","u8));
        parts.Add(new(CommentPosition: nodes.HasTrailingComma && nodes.End > 0 ? nodes.End : nodes[^1].End));
        parts.Add(ListPosition(nodes, true));
        if (multiLine) parts.Add(new(IndentationChange: -1));
        parts.Add(multiLine || ClosingLines(parent, nodes[^1], nodes.End) > 0 ? new(NewLine: true) : T(" "u8));
        parts.Add(T("}"u8));
        return new(Parts: parts);
    }

    private void Emit(ExportSpecifierNode export) =>
        Push(
            export.IsTypeOnly ? T(Utf8Literals.TypePrefix) : default,
            export.PropertyName is null ? default : S(N(export.PropertyName), T(Utf8Literals.As)),
            N(export.Name));

    private void Emit(NamespaceExportNode export) =>
        Push(T(Utf8Literals.NamespaceImportPrefix), N(export.Name));

    private void Emit(NamespaceExportDeclarationNode export) =>
        Push(T(Utf8Literals.ExportAsNamespace), N(export.Name), Semicolon());

    private void Emit(ImportAttributesNode attributes)
    {
        if (ParentState()?.Node is ImportTypeNode)
            Push(T(attributes.Token == K.AssertKeyword ? "{ assert: "u8 : "{ with: "u8), Braces(attributes.Attributes, Utf8Literals.CommaSpace), T(" }"u8));
        else
            Push(T(Utf8String.Concat(TokenFacts.Text(attributes.Token), " "u8)), Braces(attributes.Attributes, Utf8Literals.CommaSpace));
    }

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
        Push(T(Utf8Literals.Typeof), N(query.ExprName), query.TypeArguments is { Count: > 0 } ? TypeArguments(query.TypeArguments) : default);

    private void Emit(TypeLiteralNode literal) =>
        PushTypeMembers(literal);

    private void Emit(ArrayTypeNode array) =>
        Push(N(array.ElementType), T(Utf8Literals.EmptyBrackets));

    private void Emit(TupleTypeNode tuple) =>
        Push((context.GetFlags(tuple) & EmitFlags.SingleLine) != 0 ? List(tuple.Elements, Utf8Literals.OpenBracket, Utf8Literals.CommaSpace, Utf8Literals.CloseBracket, true)
            : BlockMembers(tuple.Elements, comma: true, open: Utf8Literals.OpenBracket, close: Utf8Literals.CloseBracket, trailingComma: false));

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
                : S(T(Utf8Literals.CommaSpace), N(import.Attributes)),
            T(Utf8Literals.CloseParen),
            import.Qualifier is null ? default : S(T(Utf8Literals.Dot), N(import.Qualifier)), TypeArguments(import.TypeArguments));

    private void Emit(MappedTypeNode mapped)
    {
        bool single = (context.GetFlags(mapped) & EmitFlags.SingleLine) != 0;
        Push(
            T(Utf8Literals.OpenBrace), single ? T(Utf8Literals.Space) : new(IndentationChange: 1, NewLine: true),
            mapped.ReadonlyToken is null
                ? default
                : S(N(mapped.ReadonlyToken), T(mapped.ReadonlyToken.Kind is K.PlusToken or K.MinusToken ? "readonly "u8 : " "u8)),
            T(Utf8Literals.OpenBracket),
            new(PositionTarget: mapped.TypeParameter),
            N(mapped.TypeParameter?.Name),
            T(Utf8Literals.In),
            N(mapped.TypeParameter?.Constraint),
            new(PositionTarget: mapped.TypeParameter, EndPosition: true),
            mapped.NameType is null ? default : S(T(Utf8Literals.As), N(mapped.NameType)),
            T(Utf8Literals.CloseBracket),
            mapped.QuestionToken is null
                ? default
                : S(N(mapped.QuestionToken), mapped.QuestionToken.Kind is K.PlusToken or K.MinusToken ? T("?"u8) : default),
            Annotation(mapped.Type), Semicolon(),
            mapped.Members is { Count: > 0 } ? S(single ? T(Utf8Literals.Space) : new(NewLine: true),
                List(mapped.Members, default, single ? Utf8Literals.Space : "\n"u8, default)) : default,
            single ? T(Utf8Literals.Space) : new(IndentationChange: -1, NewLine: true), T(Utf8Literals.CloseBrace));
    }

    private void Emit(LiteralTypeNode literal) =>
        Push(N(literal.Literal));

    private void Emit(TypePredicateNode predicate) =>
        Push(
            predicate.AssertsModifier is null ? default : S(N(predicate.AssertsModifier), T(Utf8Literals.Space)),
            N(predicate.ParameterName),
            predicate.Type is null ? default : S(T(Utf8Literals.Is), N(predicate.Type)));

    private void Emit(FunctionTypeNode function) =>
        Push(Scoped(Generate(function.Parameters), TypeArguments(function.TypeParameters), Parameters(function.Parameters), T(Utf8Literals.ArrowSeparator), N(function.Type)));

    private void Emit(ConstructorTypeNode function) =>
        Push(
            Modifiers(function),
            T(Utf8Literals.NewPrefix),
            Scoped(Generate(function.Parameters), TypeArguments(function.TypeParameters),
            Parameters(function.Parameters),
            T(Utf8Literals.ArrowSeparator),
            N(function.Type)));

    private void Emit(PropertySignatureDeclarationNode property)
    {
        Push(Semicolon());
        PushInitializer(property.Initializer);
        PushAnnotation(property.Type);
        Push(Modifiers(property), N(property.Name), N(property.PostfixToken));
    }

    private void Emit(MethodSignatureDeclarationNode method) =>
        Push(
            Modifiers(method),
            N(method.Name),
            N(method.PostfixToken),
            Scoped(Generate(method.Parameters), TypeArguments(method.TypeParameters),
            Parameters(method.Parameters),
            Annotation(method.Type),
            Semicolon()));

    private void Emit(CallSignatureDeclarationNode signature) =>
        Push(Scoped(Generate(signature.Parameters), TypeArguments(signature.TypeParameters), Parameters(signature.Parameters), Annotation(signature.Type), Semicolon()));

    private void Emit(ConstructSignatureDeclarationNode signature) =>
        Push(
            T(Utf8Literals.NewPrefix),
            Scoped(Generate(signature.Parameters), TypeArguments(signature.TypeParameters),
            Parameters(signature.Parameters),
            Annotation(signature.Type),
            Semicolon()));

    private void Emit(IndexSignatureDeclarationNode signature) =>
        Push(Modifiers(signature), Scoped(Generate(signature.Parameters), List(signature.Parameters, Utf8Literals.OpenBracket, Utf8Literals.CommaSpace, Utf8Literals.CloseBracket, true), Annotation(signature.Type), Semicolon()));

    private void Emit(TemplateLiteralTypeNode template) =>
        Push(N(template.Head), List(template.TemplateSpans, Utf8String.Empty, Utf8String.Empty, Utf8String.Empty));

    private void Emit(TemplateLiteralTypeSpanNode span) =>
        Push(N(span.Type), N(span.Literal));

    private void Emit(JsxElementNode jsx) =>
        Push(N(jsx.OpeningElement), List(jsx.Children, Utf8String.Empty, Utf8String.Empty, Utf8String.Empty), N(jsx.ClosingElement));

    private void Emit(JsxSelfClosingElementNode jsx) =>
        Push(T(Utf8Literals.LessThan), N(jsx.TagName), TypeArguments(jsx.TypeArguments),
            T(Utf8Literals.Space), N(jsx.Attributes), T(Utf8Literals.JsxSelfClosingTagEnd));

    private void Emit(JsxOpeningElementNode jsx) =>
        Push(T(Utf8Literals.LessThan), N(jsx.TagName), TypeArguments(jsx.TypeArguments),
            jsx.Attributes is JsxAttributesNode { Properties.Count: > 0 } ? T(Utf8Literals.Space) : default, N(jsx.Attributes),
            options.PreserveSourceNewlines && ClosingLines(jsx, jsx.Attributes) > 0 ? new(NewLine: true) : default, T(Utf8Literals.GreaterThan));

    private void Emit(JsxClosingElementNode jsx) =>
        Push(T(Utf8Literals.JsxClosingTagPrefix), N(jsx.TagName), T(Utf8Literals.GreaterThan));

    private void Emit(JsxAttributesNode attributes) =>
        Push(List(attributes.Properties, default, Utf8Literals.Space, default));

    private void Emit(JsxAttributeNode attribute) =>
        Push(N(attribute.Name), attribute.Initializer is null ? default : S(T(Utf8Literals.EqualsToken), N(attribute.Initializer)));

    private void Emit(JsxSpreadAttributeNode attribute) =>
        Push(T(Utf8Literals.OpenBraceEllipsis), N(attribute.Expression), T(Utf8Literals.CloseBrace));

    private void Emit(JsxExpressionNode expression)
    {
        bool indent = !SingleLine(expression);
        Push(indent ? new(IndentationChange: 1) : default, T(Utf8Literals.OpenBrace), N(expression.DotDotDotToken),
            N(expression.Expression), T(Utf8Literals.CloseBrace), indent ? new(IndentationChange: -1) : default);
    }

    private void Emit(JsxTextNode text) =>
        Literal(text.Text.Span);

    private void Emit(JsxFragmentNode fragment) =>
        Push(N(fragment.OpeningFragment), List(fragment.Children, Utf8String.Empty, Utf8String.Empty, Utf8String.Empty), N(fragment.ClosingFragment));

    private void Emit(JsxNamespacedNameNode name) =>
        Push(N(name.Namespace), T(Utf8Literals.Colon), N(name.Name));

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

    private Utf8String TemplateText(Utf8String text, Utf8String raw, bool noAscii = false)
    {
        if (raw.Length != 0 || text.Length == 0)
            return raw;
        return Checker.QuoteSymbolText(text, '`', !options.NeverAsciiEscape && !noAscii)[1..^1];
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
