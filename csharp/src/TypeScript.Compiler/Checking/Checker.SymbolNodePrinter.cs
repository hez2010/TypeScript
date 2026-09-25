using System.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal static string PrintDiagnosticNode(SyntaxNode node, bool neverAsciiEscape = false, CancellationToken cancellation = default,
        SourceFileNode? sourceFile = null) => new SymbolNodePrinter(neverAsciiEscape, cancellation, sourceFile).Print(node);

    // Single-line, comment-free AST output used by symbol and type serialization.
    // The explicit work stack also handles input-shaped expression/function nesting.
    private sealed class SymbolNodePrinter(bool neverAsciiEscape, CancellationToken cancellation, SourceFileNode? sourceFile)
    {
        private readonly record struct Part(SyntaxNode? Node = null, string? Text = null, IReadOnlyList<Part>? Parts = null);

        private readonly Stack<Part> pending = [];
        private readonly StringBuilder output = new();

        internal string Print(SyntaxNode node)
        {
            pending.Push(N(node));
            while (pending.TryPop(out var part))
            {
                cancellation.ThrowIfCancellationRequested();
                if (part.Text is { } text)
                    output.Append(text);
                else if (part.Parts is { } parts)
                    for (int i = parts.Count - 1; i >= 0; i--)
                        pending.Push(parts[i]);
                else if (part.Node is { } child)
                    Emit(child);
            }
            if (output.Length != 0 && output[^1] == ';')
                output.Length--;
            return output.ToString();
        }

        private static Part N(SyntaxNode? node) => new(Node: node);

        private static Part T(string text) => new(Text: text);

        private static Part S(params ReadOnlySpan<Part> parts) => new(Parts: parts.ToArray());

        private void Push(params ReadOnlySpan<Part> parts)
        {
            for (int i = parts.Length - 1; i >= 0; i--)
                pending.Push(parts[i]);
        }

        private static Part List(IReadOnlyList<SyntaxNode>? nodes, string before, string separator, string after, bool required = false)
        {
            if (nodes is null && !required || nodes is { Count: 0 } && before == " " && after.Length == 0)
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
                    ? List(modified.Modifiers, "", " ", " ") : default;

        private static Part Annotation(SyntaxNode? type) => type is null ? default : S(T(": "), N(type));

        private static Part Initializer(SyntaxNode? value) => value is null ? default : S(T(" = "), N(value));

        private static Part Parameters(NodeList? nodes) => List(nodes, "(", ", ", ")", true);

        private static Part TypeArguments(NodeList? nodes) => List(nodes, "<", ", ", ">");

        private static Part Braces(NodeList? nodes, string separator, bool spaceWhenEmpty = false) => nodes is { Count: > 0 }
                    ? List(nodes, "{ ", separator, " }") : T(spaceWhenEmpty ? "{ }" : "{}");

        private static Part Body(SyntaxNode? body) => body is null ? T(";") : S(T(" "), N(body));

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
                            (literal.TokenFlags & TokenFlags.SingleQuote) != 0 ? '\'' : '"',
                            !neverAsciiEscape));
                    break;
                case NumericLiteralNode literal:
                    output.Append(NumberText(literal));
                    break;
                case BigIntLiteralNode literal:
                    output.Append(literal.Text);
                    break;
                case RegularExpressionLiteralNode literal:
                    output.Append(literal.Text);
                    break;
                case NoSubstitutionTemplateLiteralNode literal:
                    output.Append(OriginalText(literal) ?? QuoteSymbolText(literal.Text, '`', !neverAsciiEscape));
                    break;
                case TemplateHeadNode literal:
                    output.Append('`').Append(TemplateText(literal.Text, literal.RawText)).Append("${");
                    break;
                case TemplateMiddleNode literal:
                    output.Append('}').Append(TemplateText(literal.Text, literal.RawText)).Append("${");
                    break;
                case TemplateTailNode literal:
                    output.Append('}').Append(TemplateText(literal.Text, literal.RawText)).Append('`');
                    break;
                case ComputedPropertyNameNode computed:
                    Push(T("["), N(computed.Expression), T("]"));
                    break;
                case QualifiedNameNode qualified:
                    Push(N(qualified.Left), T("."), N(qualified.Right));
                    break;
                case MetaPropertyNode meta:
                    Push(T(TokenFacts.Text(meta.KeywordToken) + "."), N(meta.Name));
                    break;
                case PropertyAccessExpressionNode property:
                    Push(N(property.Expression), T(property.QuestionDotToken is not null ? "?."
                        : property.Expression is NumericLiteralNode number && (number.TokenFlags & TokenFlags.WithSpecifier) == 0
                            && NumberText(number).IndexOfAny(['.', 'e', 'E']) < 0 ? ".." : "."), N(property.Name));
                    break;
                case ElementAccessExpressionNode element:
                    Push(N(element.Expression), T(element.QuestionDotToken is null ? "[" : "?.["), N(element.ArgumentExpression), T("]"));
                    break;
                case CallExpressionNode call:
                    Push(N(call.Expression), N(call.QuestionDotToken), TypeArguments(call.TypeArguments), Parameters(call.Arguments));
                    break;
                case NewExpressionNode call:
                    Push(T("new "), N(call.Expression), TypeArguments(call.TypeArguments), List(call.Arguments, "(", ", ", ")"));
                    break;
                case TaggedTemplateExpressionNode tagged:
                    Push(N(tagged.Tag), N(tagged.QuestionDotToken), TypeArguments(tagged.TypeArguments), T(" "), N(tagged.Template));
                    break;
                case TemplateExpressionNode template:
                    Push(N(template.Head), List(template.TemplateSpans, "", "", ""));
                    break;
                case TemplateSpanNode span:
                    Push(N(span.Expression), N(span.Literal));
                    break;
                case ParenthesizedExpressionNode paren:
                    Push(T("("), N(paren.Expression), T(")"));
                    break;
                case BinaryExpressionNode binary:
                    Push(
                        N(binary.Left),
                        Annotation(binary.Type),
                        T(binary.OperatorToken?.Kind == K.CommaToken ? ", " : " " + TokenFacts.Text(binary.OperatorToken!.Kind) + " "),
                        N(binary.Right));
                    break;
                case ConditionalExpressionNode conditional:
                    Push(N(conditional.Condition), T(" ? "), N(conditional.WhenTrue), T(" : "), N(conditional.WhenFalse));
                    break;
                case PrefixUnaryExpressionNode unary:
                    Push(T(TokenFacts.Text(unary.Operator)), T(UnarySpace(unary) ? " " : ""), N(unary.Operand));
                    break;
                case PostfixUnaryExpressionNode unary:
                    Push(N(unary.Operand), T(TokenFacts.Text(unary.Operator)));
                    break;
                case DeleteExpressionNode unary:
                    Push(T("delete "), N(unary.Expression));
                    break;
                case TypeOfExpressionNode unary:
                    Push(T("typeof "), N(unary.Expression));
                    break;
                case VoidExpressionNode unary:
                    Push(T("void "), N(unary.Expression));
                    break;
                case AwaitExpressionNode unary:
                    Push(T("await "), N(unary.Expression));
                    break;
                case NonNullExpressionNode unary:
                    Push(N(unary.Expression), T("!"));
                    break;
                case YieldExpressionNode yield:
                    Push(T("yield"), N(yield.AsteriskToken), T(yield.Expression is null ? "" : " "), N(yield.Expression));
                    break;
                case AsExpressionNode assertion:
                    Push(N(assertion.Expression), T(" as "), N(assertion.Type));
                    break;
                case SatisfiesExpressionNode assertion:
                    Push(N(assertion.Expression), T(" satisfies "), N(assertion.Type));
                    break;
                case TypeAssertionNode assertion:
                    Push(T("<"), N(assertion.Type), T(">"), N(assertion.Expression));
                    break;
                case ExpressionWithTypeArgumentsNode instantiation:
                    Push(N(instantiation.Expression), TypeArguments(instantiation.TypeArguments));
                    break;
                case ArrayLiteralExpressionNode array:
                    Push(List(array.Elements, "[", ", ", HasTrailingComma(array.Elements) ? ",]" : "]", true));
                    break;
                case OmittedExpressionNode:
                    break;
                case ObjectLiteralExpressionNode obj:
                    Push(
                        obj.Properties is { Count: > 0 }
                            ? List(
                                obj.Properties,
                                "{ ",
                                ", ",
                                HasTrailingComma(obj.Properties) && sourceFile is { ScriptKind: not ScriptKind.JSON } ? ", }" : " }")
                            : T("{}"));
                    break;
                case PropertyAssignmentNode property:
                    Push(
                        Modifiers(property),
                        N(property.Name),
                        N(property.PostfixToken),
                        Annotation(property.Type),
                        T(": "),
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
                    Push(T("..."), N(spread.Expression));
                    break;
                case SpreadElementNode spread:
                    Push(T("..."), N(spread.Expression));
                    break;
                case ArrowFunctionNode arrow:
                    Push(
                        Modifiers(arrow),
                        TypeArguments(arrow.TypeParameters),
                        SimpleArrow(arrow) ? N(arrow.Parameters![0]) : Parameters(arrow.Parameters),
                        Annotation(arrow.Type),
                        T(" => "),
                        N(arrow.Body));
                    break;
                case FunctionExpressionNode function:
                    Push(
                        Modifiers(function),
                        T("function"),
                        N(function.AsteriskToken),
                        T(" "),
                        N(function.Name),
                        TypeArguments(function.TypeParameters),
                        Parameters(function.Parameters),
                        Annotation(function.Type),
                        Body(function.Body));
                    break;
                case FunctionDeclarationNode function:
                    Push(
                        Modifiers(function),
                        T("function"),
                        N(function.AsteriskToken),
                        T(" "),
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
                        T("get "),
                        N(accessor.Name),
                        TypeArguments(accessor.TypeParameters),
                        Parameters(accessor.Parameters),
                        Annotation(accessor.Type),
                        Body(accessor.Body));
                    break;
                case SetAccessorDeclarationNode accessor:
                    Push(
                        Modifiers(accessor),
                        T("set "),
                        N(accessor.Name),
                        TypeArguments(accessor.TypeParameters),
                        Parameters(accessor.Parameters),
                        Annotation(accessor.Type),
                        Body(accessor.Body));
                    break;
                case ConstructorDeclarationNode constructor:
                    Push(
                        Modifiers(constructor),
                        T("constructor"),
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
                        T(";"));
                    break;
                case ClassExpressionNode type:
                    Push(
                        Modifiers(type),
                        T("class"),
                        T(type.Name is null ? "" : " "),
                        N(type.Name),
                        TypeArguments(type.TypeParameters),
                        List(type.HeritageClauses, " ", " ", ""),
                        T(" "),
                        Braces(type.Members, " ", true));
                    break;
                case ClassDeclarationNode type:
                    Push(
                        Modifiers(type),
                        T("class"),
                        T(type.Name is null ? "" : " "),
                        N(type.Name),
                        TypeArguments(type.TypeParameters),
                        List(type.HeritageClauses, " ", " ", ""),
                        T(" "),
                        Braces(type.Members, " ", true));
                    break;
                case ClassStaticBlockDeclarationNode block:
                    Push(T("static "), N(block.Body));
                    break;
                case HeritageClauseNode heritage:
                    Push(T(TokenFacts.Text(heritage.Token) + " "), List(heritage.Types, "", ", ", ""));
                    break;
                case BlockNode block:
                    Push(Braces(block.Statements, " ", true));
                    break;
                case ModuleBlockNode block:
                    Push(Braces(block.Statements, " ", true));
                    break;
                case SourceFileNode file:
                    EmitSourceFile(file);
                    break;
                case ExpressionStatementNode statement:
                    Push(N(statement.Expression), T(";"));
                    break;
                case ReturnStatementNode statement:
                    Push(T("return"), T(statement.Expression is null ? "" : " "), N(statement.Expression), T(";"));
                    break;
                case ThrowStatementNode statement:
                    Push(T("throw "), N(statement.Expression), T(";"));
                    break;
                case VariableStatementNode statement:
                    Push(Modifiers(statement), N(statement.DeclarationList), T(";"));
                    break;
                case VariableDeclarationListNode list:
                    Push(T(VariableKind(list.Flags)), List(list.Declarations, "", ", ", ""));
                    break;
                case VariableDeclarationNode variable:
                    Push(N(variable.Name), N(variable.ExclamationToken), Annotation(variable.Type), Initializer(variable.Initializer));
                    break;
                case BindingPatternNode pattern:
                    Push(
                        pattern.Kind == K.ObjectBindingPattern
                            ? Braces(pattern.Elements, ", ")
                            : List(pattern.Elements, "[", ", ", "]", true));
                    break;
                case BindingElementNode element:
                    Push(
                        N(element.DotDotDotToken),
                        element.PropertyName is null ? default : S(N(element.PropertyName), T(": ")),
                        N(element.Name),
                        Initializer(element.Initializer));
                    break;
                case IfStatementNode statement:
                    Push(
                        T("if ("),
                        N(statement.Expression),
                        T(") "),
                        N(statement.ThenStatement),
                        statement.ElseStatement is null ? default : S(T(" else "), N(statement.ElseStatement)));
                    break;
                case WhileStatementNode statement:
                    Push(T("while ("), N(statement.Expression), T(") "), N(statement.Statement));
                    break;
                case DoStatementNode statement:
                    Push(T("do "), N(statement.Statement), T(" while ("), N(statement.Expression), T(");"));
                    break;
                case ForStatementNode statement:
                    Push(T("for ("), N(statement.Initializer), T(statement.Condition is null ? ";" : "; "), N(statement.Condition),
                        T(statement.Incrementor is null ? ";" : "; "), N(statement.Incrementor), T(") "), N(statement.Statement));
                    break;
                case ForInOrOfStatementNode statement:
                    Push(
                        T("for "),
                        statement.AwaitModifier is null ? default : T("await "),
                        T("("),
                        N(statement.Initializer),
                        T(statement.Kind == K.ForOfStatement ? " of " : " in "),
                        N(statement.Expression),
                        T(") "),
                        N(statement.Statement));
                    break;
                case BreakStatementNode statement:
                    Push(T("break"), statement.Label is null ? default : S(T(" "), N(statement.Label)), T(";"));
                    break;
                case ContinueStatementNode statement:
                    Push(T("continue"), statement.Label is null ? default : S(T(" "), N(statement.Label)), T(";"));
                    break;
                case LabeledStatementNode statement:
                    Push(N(statement.Label), T(": "), N(statement.Statement));
                    break;
                case WithStatementNode statement:
                    Push(T("with ("), N(statement.Expression), T(") "), N(statement.Statement));
                    break;
                case SwitchStatementNode statement:
                    Push(T("switch ("), N(statement.Expression), T(") "), N(statement.CaseBlock));
                    break;
                case CaseBlockNode block:
                    Push(Braces(block.Clauses, " ", true));
                    break;
                case CaseOrDefaultClauseNode clause:
                    Push(
                        clause.Expression is null ? T("default") : S(T("case "), N(clause.Expression)),
                        T(":"),
                        List(clause.Statements, " ", " ", ""));
                    break;
                case TryStatementNode statement:
                    Push(
                        T("try "),
                        N(statement.TryBlock),
                        statement.CatchClause is null ? default : S(T(" "), N(statement.CatchClause)),
                        statement.FinallyBlock is null ? default : S(T(" finally "), N(statement.FinallyBlock)));
                    break;
                case CatchClauseNode clause:
                    Push(
                        T("catch"),
                        clause.VariableDeclaration is null ? default : S(T(" ("), N(clause.VariableDeclaration), T(")")),
                        T(" "),
                        N(clause.Block));
                    break;
                case TypeAliasDeclarationNode alias:
                    Push(Modifiers(alias), T("type "), N(alias.Name), TypeArguments(alias.TypeParameters), T(" = "), N(alias.Type), T(";"));
                    break;
                case InterfaceDeclarationNode type:
                    Push(
                        Modifiers(type),
                        T("interface "),
                        N(type.Name),
                        TypeArguments(type.TypeParameters),
                        List(type.HeritageClauses, " ", " ", ""),
                        T(" "),
                        Braces(type.Members, " ", true));
                    break;
                case EnumDeclarationNode enumeration:
                    Push(Modifiers(enumeration), T("enum "), N(enumeration.Name), T(" "), Braces(enumeration.Members, ", ", true));
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
                        T("import "),
                        N(import.ImportClause),
                        import.ImportClause is null ? default : T(" from "),
                        N(import.ModuleSpecifier),
                        import.Attributes is null ? default : S(T(" "), N(import.Attributes)), T(";"));
                    break;
                case ImportClauseNode clause:
                    Push(clause.PhaseModifier == K.Unknown ? default : T(TokenFacts.Text(clause.PhaseModifier) + " "), N(clause.Name),
                        clause.Name is not null && clause.NamedBindings is not null ? T(", ") : default, N(clause.NamedBindings));
                    break;
                case NamespaceImportNode import:
                    Push(T("* as "), N(import.Name));
                    break;
                case NamedImportsNode imports:
                    Push(Braces(imports.Elements, ", "));
                    break;
                case ImportSpecifierNode import:
                    Push(
                        import.IsTypeOnly ? T("type ") : default,
                        import.PropertyName is null ? default : S(N(import.PropertyName), T(" as ")),
                        N(import.Name));
                    break;
                case ImportEqualsDeclarationNode import:
                    Push(
                        Modifiers(import),
                        T(import.IsTypeOnly ? "import type " : "import "),
                        N(import.Name),
                        T(" = "),
                        N(import.ModuleReference),
                        T(";"));
                    break;
                case ExternalModuleReferenceNode reference:
                    Push(T("require("), N(reference.Expression), T(")"));
                    break;
                case ExportDeclarationNode export:
                    Push(
                        Modifiers(export),
                        T(export.IsTypeOnly ? "export type " : "export "),
                        export.ExportClause is null ? T("*") : N(export.ExportClause),
                        export.ModuleSpecifier is null ? default : S(T(" from "), N(export.ModuleSpecifier)),
                        export.Attributes is null ? default : S(T(" "), N(export.Attributes)), T(";"));
                    break;
                case ExportAssignmentNode export:
                    Push(Modifiers(export), T(export.IsExportEquals ? "export = " : "export default "), N(export.Expression), T(";"));
                    break;
                case NamedExportsNode exports:
                    Push(Braces(exports.Elements, ", "));
                    break;
                case ExportSpecifierNode export:
                    Push(
                        export.IsTypeOnly ? T("type ") : default,
                        export.PropertyName is null ? default : S(N(export.PropertyName), T(" as ")),
                        N(export.Name));
                    break;
                case NamespaceExportNode export:
                    Push(T("* as "), N(export.Name));
                    break;
                case NamespaceExportDeclarationNode export:
                    Push(T("export as namespace "), N(export.Name), T(";"));
                    break;
                case ImportAttributesNode attributes:
                    Push(T(TokenFacts.Text(attributes.Token) + " "), Braces(attributes.Attributes, ", "));
                    break;
                case ImportAttributeNode attribute:
                    Push(N(attribute.Name), T(": "), N(attribute.Value));
                    break;
                case DecoratorNode decorator:
                    Push(T("@"), N(decorator.Expression));
                    break;
                case TypeParameterDeclarationNode parameter:
                    Push(
                        Modifiers(parameter),
                        N(parameter.Name),
                        parameter.Constraint is null ? default : S(T(" extends "), N(parameter.Constraint)),
                        Initializer(parameter.DefaultType));
                    break;
                case TypeReferenceNode reference:
                    Push(N(reference.TypeName), TypeArguments(reference.TypeArguments));
                    break;
                case TypeQueryNode query:
                    Push(T("typeof "), N(query.ExprName), TypeArguments(query.TypeArguments));
                    break;
                case TypeLiteralNode literal:
                    Push(Braces(literal.Members, " "));
                    break;
                case ArrayTypeNode array:
                    Push(N(array.ElementType), T("[]"));
                    break;
                case TupleTypeNode tuple:
                    Push(tuple.Elements is { Count: > 0 } ? List(tuple.Elements, "[ ", ", ", " ]") : T("[ ]"));
                    break;
                case NamedTupleMemberNode member:
                    Push(N(member.DotDotDotToken), N(member.Name), N(member.QuestionToken), T(": "), N(member.Type));
                    break;
                case OptionalTypeNode optional:
                    Push(N(optional.Type), T("?"));
                    break;
                case RestTypeNode rest:
                    Push(T("..."), N(rest.Type));
                    break;
                case UnionTypeNode union:
                    Push(List(union.Types, "", " | ", ""));
                    break;
                case IntersectionTypeNode intersection:
                    Push(List(intersection.Types, "", " & ", ""));
                    break;
                case ConditionalTypeNode conditional:
                    Push(
                        N(conditional.CheckType),
                        T(" extends "),
                        N(conditional.ExtendsType),
                        T(" ? "),
                        N(conditional.TrueType),
                        T(" : "),
                        N(conditional.FalseType));
                    break;
                case InferTypeNode infer:
                    Push(T("infer "), N(infer.TypeParameter));
                    break;
                case ParenthesizedTypeNode parenthesized:
                    Push(T("("), N(parenthesized.Type), T(")"));
                    break;
                case TypeOperatorNode operation:
                    Push(T(TokenFacts.Text(operation.Operator) + " "), N(operation.Type));
                    break;
                case IndexedAccessTypeNode indexed:
                    Push(N(indexed.ObjectType), T("["), N(indexed.IndexType), T("]"));
                    break;
                case ImportTypeNode import:
                    Push(T(import.IsTypeOf ? "typeof import(" : "import("), N(import.Argument),
                        import.Attributes is null
                            ? default
                            : S(
                                T(", { " + TokenFacts.Text(import.Attributes.Token) + ": "),
                                Braces(import.Attributes.Attributes, ", "),
                                T(" }")),
                        T(")"),
                        import.Qualifier is null ? default : S(T("."), N(import.Qualifier)), TypeArguments(import.TypeArguments));
                    break;
                case MappedTypeNode mapped:
                    Push(
                        T("{ "),
                        mapped.ReadonlyToken is null
                            ? default
                            : T(
                                (mapped.ReadonlyToken.Kind is K.PlusToken or K.MinusToken
                                    ? TokenFacts.Text(mapped.ReadonlyToken.Kind)
                                    : "") + "readonly "),
                        T("["),
                        N(mapped.TypeParameter?.Name),
                        T(" in "),
                        N(mapped.TypeParameter?.Constraint),
                        mapped.NameType is null ? default : S(T(" as "), N(mapped.NameType)),
                        T("]"),
                        mapped.QuestionToken is null
                            ? default
                            : T(
                                (mapped.QuestionToken.Kind is K.PlusToken or K.MinusToken
                                    ? TokenFacts.Text(mapped.QuestionToken.Kind)
                                    : "") + "?"),
                        Annotation(mapped.Type), T(";"), List(mapped.Members, " ", " ", ""), T(" }"));
                    break;
                case LiteralTypeNode literal:
                    Push(N(literal.Literal));
                    break;
                case TypePredicateNode predicate:
                    Push(
                        predicate.AssertsModifier is null ? default : T("asserts "),
                        N(predicate.ParameterName),
                        predicate.Type is null ? default : S(T(" is "), N(predicate.Type)));
                    break;
                case FunctionTypeNode function:
                    Push(TypeArguments(function.TypeParameters), Parameters(function.Parameters), T(" => "), N(function.Type));
                    break;
                case ConstructorTypeNode function:
                    Push(
                        Modifiers(function),
                        T("new "),
                        TypeArguments(function.TypeParameters),
                        Parameters(function.Parameters),
                        T(" => "),
                        N(function.Type));
                    break;
                case PropertySignatureDeclarationNode property:
                    Push(
                        Modifiers(property),
                        N(property.Name),
                        N(property.PostfixToken),
                        Annotation(property.Type),
                        Initializer(property.Initializer),
                        T(";"));
                    break;
                case MethodSignatureDeclarationNode method:
                    Push(
                        Modifiers(method),
                        N(method.Name),
                        N(method.PostfixToken),
                        TypeArguments(method.TypeParameters),
                        Parameters(method.Parameters),
                        Annotation(method.Type),
                        T(";"));
                    break;
                case CallSignatureDeclarationNode signature:
                    Push(TypeArguments(signature.TypeParameters), Parameters(signature.Parameters), Annotation(signature.Type), T(";"));
                    break;
                case ConstructSignatureDeclarationNode signature:
                    Push(
                        T("new "),
                        TypeArguments(signature.TypeParameters),
                        Parameters(signature.Parameters),
                        Annotation(signature.Type),
                        T(";"));
                    break;
                case IndexSignatureDeclarationNode signature:
                    Push(Modifiers(signature), List(signature.Parameters, "[", ", ", "]", true), Annotation(signature.Type), T(";"));
                    break;
                case TemplateLiteralTypeNode template:
                    Push(N(template.Head), List(template.TemplateSpans, "", "", ""));
                    break;
                case TemplateLiteralTypeSpanNode span:
                    Push(N(span.Type), N(span.Literal));
                    break;
                case JsxElementNode jsx:
                    Push(N(jsx.OpeningElement), List(jsx.Children, "", "", ""), N(jsx.ClosingElement));
                    break;
                case JsxSelfClosingElementNode jsx:
                    Push(T("<"), N(jsx.TagName), TypeArguments(jsx.TypeArguments), N(jsx.Attributes), T("/>"));
                    break;
                case JsxOpeningElementNode jsx:
                    Push(T("<"), N(jsx.TagName), TypeArguments(jsx.TypeArguments), N(jsx.Attributes), T(">"));
                    break;
                case JsxClosingElementNode jsx:
                    Push(T("</"), N(jsx.TagName), T(">"));
                    break;
                case JsxAttributesNode attributes:
                    Push(List(attributes.Properties, attributes.Properties is { Count: > 0 } ? " " : "", " ", ""));
                    break;
                case JsxAttributeNode attribute:
                    Push(N(attribute.Name), attribute.Initializer is null ? default : S(T("="), N(attribute.Initializer)));
                    break;
                case JsxSpreadAttributeNode attribute:
                    Push(T("{..."), N(attribute.Expression), T("}"));
                    break;
                case JsxExpressionNode expression:
                    Push(T("{"), N(expression.DotDotDotToken), N(expression.Expression), T("}"));
                    break;
                case JsxTextNode text:
                    output.Append(text.Text);
                    break;
                case JsxFragmentNode fragment:
                    Push(T("<>"), List(fragment.Children, "", "", ""), T("</>"));
                    break;
                case JsxNamespacedNameNode name:
                    Push(N(name.Namespace), T(":"), N(name.Name));
                    break;
                case SyntaxNode when node.Kind == K.ThisType:
                    output.Append("this");
                    break;
                case SyntaxNode when node.Kind == K.EmptyStatement:
                    output.Append(';');
                    break;
                case SyntaxNode when node.Kind == K.DebuggerStatement:
                    output.Append("debugger;");
                    break;
                default:
                    string token = TokenFacts.Text(node.Kind);
                    if (token.Length == 0)
                        throw new NotSupportedException($"Diagnostic node printing requires {node.Kind}");
                    output.Append(token);
                    if (node.Kind == K.DebuggerStatement)
                        output.Append(';');
                    break;
            }
        }

        private string? OriginalText(SyntaxNode node)
        {
            if (sourceFile is null || node.Parent is null || node.Pos < 0 || node.End < 0)
                return null;
            var (start, _) = CheckerDiagnostic.TokenRange(sourceFile, node.Pos);
            return sourceFile.Source.Text[sourceFile.Source.ToUtf16Position(start)..sourceFile.Source.ToUtf16Position(node.End)];
        }

        private string NumberText(NumericLiteralNode literal) => (literal.TokenFlags & (TokenFlags.IsInvalid | TokenFlags.ContainsSeparator)) == 0
                    ? OriginalText(literal) ?? literal.Text : literal.Text;

        private static bool HasTrailingComma(NodeList? nodes) => nodes?.HasTrailingComma == true;

        private void EmitModule(ModuleDeclarationNode module)
        {
            var parts = new List<Part> { Modifiers(module) };
            if (module.Keyword != K.GlobalKeyword)
                parts.Add(T(module.Keyword == K.NamespaceKeyword ? "namespace " : "module "));
            parts.Add(N(module.Name));
            var body = module.Body;
            while (body is ModuleDeclarationNode nested)
            {
                cancellation.ThrowIfCancellationRequested();
                parts.Add(T("."));
                parts.Add(N(nested.Name));
                body = nested.Body;
            }
            if (module.Attributes is not null)
                parts.Add(S(T(" with "), N(module.Attributes)));
            parts.Add(Body(body));
            pending.Push(new(Parts: parts));
        }

        private void EmitSourceFile(SourceFileNode file)
        {
            sourceFile = file;
            var parts = new List<Part> { T(" ") };
            int index = 0;
            if (file.ScriptKind != ScriptKind.JSON)
            {
                if (file.Source.Text.StartsWith("#!", StringComparison.Ordinal))
                    parts.Add(T(file.Source.Text.Split(['\r', '\n'], 2)[0] + " "));
                while (file.Statements is { } statements
                    && index < statements.Count
                    && statements[index] is ExpressionStatementNode { Expression: StringLiteralNode })
                    parts.Add(S(T(" "), N(statements[index++])));
                parts.Add(T(" "));
            }
            if (file.Statements is { } all && index < all.Count)
                parts.Add(List(all.Skip(index).ToArray(), " ", " ", " "));
            else
                parts.Add(T(" "));
            pending.Push(new(Parts: parts));
        }

        private string TemplateText(string text, string raw)
        {
            if (raw.Length != 0 || text.Length == 0)
                return raw;
            return QuoteSymbolText(text, '`', !neverAsciiEscape)[1..^1];
        }

        private static bool UnarySpace(PrefixUnaryExpressionNode node) => node.Operator is K.PlusToken or K.MinusToken
                    && node.Operand is PrefixUnaryExpressionNode operand
                    && (operand.Operator == node.Operator || node.Operator == K.PlusToken && operand.Operator == K.PlusPlusToken
                        || node.Operator == K.MinusToken && operand.Operator == K.MinusMinusToken);

        private static bool SimpleArrow(ArrowFunctionNode node) => node.TypeParameters is null && node.Type is null
                    && node.Modifiers is null or { Count: 0 } && node.Parameters is { Count: 1, HasTrailingComma: false } parameters
                    && parameters[0] is ParameterDeclarationNode { Name: IdentifierNode, Modifiers: null, DotDotDotToken: null, QuestionToken: null, Type: null, Initializer: null } parameter
                    && parameter.Pos == node.Pos;

        private static string VariableKind(NodeFlags flags) => (flags & NodeFlags.AwaitUsing) == NodeFlags.AwaitUsing ? "await using "
                    : (flags & NodeFlags.Using) != 0 ? "using " : (flags & NodeFlags.Const) != 0 ? "const "
                    : (flags & NodeFlags.Let) != 0 ? "let " : "var ";
    }
}
