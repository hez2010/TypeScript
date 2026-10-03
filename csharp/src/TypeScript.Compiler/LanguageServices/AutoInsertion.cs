using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    public async ValueTask<DocumentTextEdit?> GetAutoInsertionAsync(DocumentPosition position, Utf8String character,
        UserPreferences? preferences = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (preferences?.EnableAutoClosingTags == false || character != ">"u8) return null;
        var candidates = FromPosition(position, MappingFeature.AutoInsert);
        if (candidates.Count != 1 || candidates[0].Position.Fidelity != MappingFidelity.Exact) return null;
        var (projection, mapped) = candidates[0];
        var token = await SyntaxNavigation.FindPrecedingTokenAsync(projection.File, mapped.Position, cancellation: cancellation);
        if (token is null) return null;
        var parent = token.Kind == K.GreaterThanToken && token.Parent is JsxOpeningElementNode or JsxOpeningFragmentNode
            ? token.Parent.Parent : token is JsxTextNode ? token.Parent : null;
        Utf8String closing = default;
        if (parent is JsxElementNode element && UnclosedTag(element)) closing = "</"u8 + TagText(element.OpeningElement!.TagName!) + ">"u8;
        else if (parent is JsxFragmentNode fragment && UnclosedFragment(fragment)) closing = "</>"u8;
        return closing.IsEmpty ? null : new(new(position, position), "$0"u8 + closing.Replace("$"u8, "\\$"u8));

        bool UnclosedTag(JsxElementNode node)
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!SameTag(node.OpeningElement!.TagName!, node.ClosingElement!.TagName!)) return true;
                if (node.Parent is not JsxElementNode outer || !SameTag(node.OpeningElement.TagName!, outer.OpeningElement!.TagName!)) return false;
                node = outer;
            }
        }
        bool UnclosedFragment(JsxFragmentNode node)
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if ((node.ClosingFragment!.Flags & NodeFlags.ThisNodeHasError) != 0) return true;
                if (node.Parent is not JsxFragmentNode outer) return false;
                node = outer;
            }
        }
        bool SameTag(SyntaxNode left, SyntaxNode right)
        {
            while (left.Kind == right.Kind)
            {
                cancellation.ThrowIfCancellationRequested();
                switch (left)
                {
                    case IdentifierNode name: return name.Text == ((IdentifierNode)right).Text;
                    case JsxNamespacedNameNode name:
                        var other = (JsxNamespacedNameNode)right;
                        return ((IdentifierNode)name.Namespace!).Text == ((IdentifierNode)other.Namespace!).Text
                            && ((IdentifierNode)name.Name!).Text == ((IdentifierNode)other.Name!).Text;
                    case PropertyAccessExpressionNode name:
                        var access = (PropertyAccessExpressionNode)right;
                        if (((IdentifierNode)name.Name!).Text != ((IdentifierNode)access.Name!).Text) return false;
                        left = name.Expression!; right = access.Expression!; break;
                    default: return left.Kind == K.ThisKeyword;
                }
            }
            return false;
        }
        Utf8String TagText(SyntaxNode node)
        {
            Stack<Utf8String> parts = new();
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (node is PropertyAccessExpressionNode access) { parts.Push("."u8 + Name(access.Name!)); node = access.Expression!; }
                else if (node is JsxNamespacedNameNode name) { parts.Push(":"u8 + Name(name.Name!)); node = name.Namespace!; }
                else if (node is QualifiedNameNode qualified) { parts.Push("."u8 + Name(qualified.Right!)); node = qualified.Left!; }
                else { parts.Push(node.Kind == K.ThisKeyword ? "this"u8 : Name(node)); break; }
            }
            return Utf8String.Concat(parts);
        }
        Utf8String Name(SyntaxNode node) => node.Pos < 0 ? node is IdentifierNode name ? name.Text : default
            : node.Pos == node.End ? default : projection.File.Source.Text[new Scanner(projection.File.Source).SkipTriviaAt(node.Pos)..node.End];
    }
}
