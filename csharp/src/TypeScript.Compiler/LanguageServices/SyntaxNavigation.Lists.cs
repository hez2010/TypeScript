using TypeScript.Compiler.Ast;

namespace TypeScript.Compiler.LanguageServices;

public static partial class SyntaxNavigation
{
    internal static async ValueTask<NodeList?> ContainingListAsync(SyntaxNode node, SourceFileNode file, CancellationToken cancellation)
    {
        if (node.Parent is null) return null;
        NodeList? result = null;
        foreach (var child in await ChildrenAsync(node.Parent, file, cancellation))
            if (child.List is { } list && list.Pos <= node.Pos && node.End <= list.End) result = list;
        return result;
    }

    internal static IReadOnlyList<SyntaxNode> NonDocumentationChildren(SyntaxNode node, SourceFileNode file, CancellationToken cancellation)
    {
        List<SyntaxNode> result = [];
        if (node.ChildCount == 0) return result;
        int position = node.Pos;
        for (int i = 0; i < node.ChildCount; i++)
        {
            var child = node.GetChild(i);
            ScanTo(child.Pos); result.Add(child); position = child.End;
        }
        ScanTo(node.End);
        return result;

        void ScanTo(int end)
        {
            var scanner = ScanAt(file, position);
            while (position < end)
            {
                cancellation.ThrowIfCancellationRequested();
                result.Add(file.GetOrCreateToken(scanner.Kind, scanner.FullStart, scanner.Position, node, scanner.Flags));
                if (scanner.Position <= position) break;
                position = scanner.Position; scanner.Scan();
            }
        }
    }
}
