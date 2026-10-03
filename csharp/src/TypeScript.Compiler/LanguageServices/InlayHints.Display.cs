using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    private sealed partial class InlayHintQuery
    {
        private IReadOnlyList<InlayHintLabelPart> DisplayParts(SyntaxNode root, IReadOnlyDictionary<SyntaxNode, Symbol> symbols)
        {
            List<InlayHintLabelPart> parts = [];
            Stack<(SyntaxNode? Node, Utf8String Text)> stack = new(); stack.Push((root, default));
            List<(SyntaxNode? Node, Utf8String Text)> children = [];
            while (stack.TryPop(out var item))
            {
                cancellation.ThrowIfCancellationRequested();
                if (item.Node is not { } node) { parts.Add(new(item.Text)); continue; }
                var token = TokenFacts.Text(node.Kind);
                if (!token.IsEmpty) { parts.Add(new(token)); continue; }
                if (Literal(node)) { parts.Add(new(LiteralText(node))); continue; }
                children.Clear();
                switch (node)
                {
                    case IdentifierNode n:
                        parts.Add(symbols.GetValueOrDefault(n)?.Declarations.FirstOrDefault()?.DeclarationName is { } name
                            ? NodePart(n.Text, name) : new(n.Text)); break;
                    case QualifiedNameNode n: Visit(n.Left); Add("."u8); Visit(n.Right); break;
                    case TypePredicateNode n:
                        if (n.AssertsModifier is not null) Add("asserts "u8);
                        Visit(n.ParameterName); if (n.Type is not null) { Add(" is "u8); Visit(n.Type); } break;
                    case TypeReferenceNode n: Visit(n.TypeName); Arguments(n.TypeArguments, ","u8); break;
                    case TypeParameterDeclarationNode n:
                        List(n.ModifierList, default); Visit(n.Name);
                        if (n.Constraint is not null) { Add(" extends "u8); Visit(n.Constraint); }
                        if (n.DefaultType is not null) { Add(" = "u8); Visit(n.DefaultType); } break;
                    case ParameterDeclarationNode n:
                        List(n.ModifierList, " "u8); if (n.DotDotDotToken is not null) Add("..."u8);
                        Visit(n.Name); if (n.QuestionToken is not null) Add("?"u8); Annotation(n.Type); break;
                    case ConstructorTypeNode n: Add("new "u8); Signature(n); Add(" => "u8); Visit(n.Type); break;
                    case TypeQueryNode n: Add("typeof "u8); Visit(n.ExprName); Arguments(n.TypeArguments, ", "u8); break;
                    case TypeLiteralNode n: Braces(n.Members, "; "u8); break;
                    case ArrayTypeNode n: Visit(n.ElementType); Add("[]"u8); break;
                    case TupleTypeNode n: Add("["u8); List(n.Elements, ", "u8); Add("]"u8); break;
                    case NamedTupleMemberNode n:
                        if (n.DotDotDotToken is not null) Add("..."u8); Visit(n.Name);
                        if (n.QuestionToken is not null) Add("?"u8); Add(": "u8); Visit(n.Type); break;
                    case OptionalTypeNode n: Visit(n.Type); Add("?"u8); break;
                    case RestTypeNode n: Add("..."u8); Visit(n.Type); break;
                    case UnionTypeNode n: List(n.Types, " | "u8); break;
                    case IntersectionTypeNode n: List(n.Types, " & "u8); break;
                    case ConditionalTypeNode n:
                        Visit(n.CheckType); Add(" extends "u8); Visit(n.ExtendsType); Add(" ? "u8); Visit(n.TrueType); Add(" : "u8); Visit(n.FalseType); break;
                    case InferTypeNode n: Add("infer "u8); Visit(n.TypeParameter); break;
                    case ParenthesizedTypeNode n: Add("("u8); Visit(n.Type); Add(")"u8); break;
                    case TypeOperatorNode n: Add(TokenFacts.Text(n.Operator)); Visit(n.Type); break;
                    case IndexedAccessTypeNode n: Visit(n.ObjectType); Add("["u8); Visit(n.IndexType); Add("]"u8); break;
                    case MappedTypeNode n:
                        Add("{ "u8);
                        if (n.ReadonlyToken is not null) { Sign(n.ReadonlyToken); Add("readonly "u8); }
                        Add("["u8); Visit(n.TypeParameter);
                        if (n.NameType is not null) { Add(" as "u8); Visit(n.NameType); }
                        Add("]"u8);
                        if (n.QuestionToken is not null) { Sign(n.QuestionToken); Add("?"u8); }
                        Add(": "u8); Visit(n.Type); Add("; }"u8); break;
                    case LiteralTypeNode n: Visit(n.Literal); break;
                    case FunctionTypeNode n: Signature(n); Add(" => "u8); Visit(n.Type); break;
                    case ImportTypeNode n:
                        if (n.IsTypeOf) Add("typeof "u8); Add("import("u8); Visit(n.Argument); Add(")"u8);
                        if (n.Qualifier is not null) { Add("."u8); Visit(n.Qualifier); } Arguments(n.TypeArguments, ", "u8); break;
                    case PropertySignatureDeclarationNode n:
                        Modifiers(n); Visit(n.Name); Visit(n.PostfixToken); Annotation(n.Type); break;
                    case IndexSignatureDeclarationNode n: Add("["u8); List(n.Parameters, ", "u8); Add("]"u8); Annotation(n.Type); break;
                    case MethodSignatureDeclarationNode n:
                        Modifiers(n); Visit(n.Name); Visit(n.PostfixToken); Signature(n); Annotation(n.Type); break;
                    case CallSignatureDeclarationNode n: Signature(n); Annotation(n.Type); break;
                    case ConstructSignatureDeclarationNode n: Add("new "u8); Signature(n); Annotation(n.Type); break;
                    case BindingPatternNode n when n.Kind == K.ArrayBindingPattern: Add("["u8); List(n.Elements, ", "u8); Add("]"u8); break;
                    case BindingPatternNode n: Braces(n.Elements, ", "u8); break;
                    case BindingElementNode n: Visit(n.Name); break;
                    case PrefixUnaryExpressionNode n: Add(TokenFacts.Text(n.Operator)); Visit(n.Operand); break;
                    case TemplateLiteralTypeNode n: Visit(n.Head); foreach (var span in (IReadOnlyList<SyntaxNode>?)n.TemplateSpans ?? []) Visit(span); break;
                    case TemplateLiteralTypeSpanNode n: Visit(n.Type); Visit(n.Literal); break;
                    case TemplateHeadNode or TemplateMiddleNode or TemplateTailNode: Add(LiteralText(node)); break;
                    case ComputedPropertyNameNode n: Add("["u8); Visit(n.Expression); Add("]"u8); break;
                    case PropertyAccessExpressionNode n: Visit(n.Expression); Add("."u8); Visit(n.Name); break;
                    case ElementAccessExpressionNode n: Visit(n.Expression); Add("["u8); Visit(n.ArgumentExpression); Add("]"u8); break;
                    default:
                        if (node.Kind == K.ThisType) Add("this"u8);
                        else throw new InvalidOperationException($"Unsupported inlay hint type syntax: {node.Kind}");
                        break;
                }
                for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
            }
            return parts;

            void Add(Utf8String text) => children.Add((null, text));
            void Visit(SyntaxNode? node) { if (node is not null) children.Add((node, default)); }
            void List(NodeList? list, Utf8String separator)
            { for (int i = 0; list is not null && i < list.Count; i++) { if (i > 0) Add(separator); Visit(list[i]); } }
            void Arguments(NodeList? list, Utf8String separator)
            { if (list is { Count: > 0 }) { Add("<"u8); List(list, separator); Add(">"u8); } }
            void Signature(IFunctionSignature node)
            { Arguments(node.TypeParameters, ", "u8); Add("("u8); List(node.Parameters, ", "u8); Add(")"u8); }
            void Annotation(SyntaxNode? type) { if (type is not null) { Add(": "u8); Visit(type); } }
            void Braces(NodeList? list, Utf8String separator)
            { Add("{"u8); if (list is { Count: > 0 }) { Add(" "u8); List(list, separator); Add(" "u8); } Add("}"u8); }
            void Modifiers(SyntaxNode node) { if (node.ModifierList is { Count: > 0 } list) { List(list, " "u8); Add(" "u8); } }
            void Sign(SyntaxNode node) { if (node.Kind is K.PlusToken or K.MinusToken) Add(TokenFacts.Text(node.Kind)); }
        }

        private Utf8String LiteralText(SyntaxNode node)
        {
            if (node is StringLiteralNode text) return Checker.QuoteSymbolText(text.Text, quote, false);
            if (node.Kind is K.TemplateHead or K.TemplateMiddle or K.TemplateTail)
            {
                var raw = node switch { TemplateHeadNode n => n.RawText, TemplateMiddleNode n => n.RawText, TemplateTailNode n => n.RawText, _ => default };
                if (raw.IsEmpty) raw = Checker.QuoteSymbolText(Text(node), '`', false)[1..^1];
                return node.Kind switch { K.TemplateHead => "`"u8 + raw + "${"u8, K.TemplateMiddle => "}"u8 + raw + "${"u8, _ => "}"u8 + raw + "`"u8 };
            }
            return Text(node);
        }
    }
}
