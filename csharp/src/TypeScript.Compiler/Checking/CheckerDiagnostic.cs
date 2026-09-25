using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal static class CheckerDiagnostic
{
    internal static string DeclarationName(SyntaxNode node)
    {
        if (node.Pos == node.End)
            return "(Missing)";
        var file = SemanticSyntax.Source(node);
        if (file is null)
            return SyntaxNameText.Get(node);
        var (start, _) = TokenRange(file, node.Pos);
        return file.Source.Text[file.Source.ToUtf16Position(start)..file.Source.ToUtf16Position(node.End)];
    }

    internal static Diagnostic Create(SyntaxNode? node, DiagnosticMessage message, params string[] arguments)
    {
        var file = SemanticSyntax.Source(node);
        var (start, end) = node is null || file is null ? (0, 0) : ErrorRange(file, node);
        return new(message, start, end - start, [.. arguments]) { FileName = file?.FileName };
    }

    internal static (int Start, int End) TokenRange(SourceFileNode file, int position)
    {
        var scanner = ScannerAt(file, position);
        return (file.Source.ToBytePosition(scanner.TokenStart), file.Source.ToBytePosition(scanner.Position));
    }

    private static Scanner ScannerAt(SourceFileNode file, int position)
    {
        var scanner = new Scanner(file.Source, jsx: file.ScriptKind is ScriptKind.JSX or ScriptKind.TSX);
        scanner.ResetPosition(file.Source.ToUtf16Position(Math.Max(0, position)));
        scanner.Scan();
        return scanner;
    }

    internal static (int Start, int End) ErrorRange(SourceFileNode file, SyntaxNode node)
    {
        SyntaxNode? errorNode = node;
        switch (node)
        {
            case SourceFileNode:
                var token = TokenRange(file, 0);
                return token.Start == file.Source.Bytes.Length ? (0, 0) : token;
            case FunctionDeclarationNode or MethodDeclarationNode when (node.Flags & NodeFlags.Reparsed) != 0:
                break;
            case FunctionDeclarationNode or MethodDeclarationNode or VariableDeclarationNode or BindingElementNode
                or ClassDeclarationNode or InterfaceDeclarationNode or ModuleDeclarationNode or EnumDeclarationNode
                or EnumMemberNode or FunctionExpressionNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode
                or TypeAliasDeclarationNode or PropertyDeclarationNode or PropertySignatureDeclarationNode or NamespaceImportNode
                or ClassExpressionNode:
                errorNode = SemanticSyntax.Name(node);
                break;
            case ArrowFunctionNode arrow:
                int start = TokenRange(file, node.Pos).Start;
                if (arrow.Body is BlockNode block)
                {
                    int line = file.Source.GetLineAndCharacter(block.Pos).Line;
                    if (line < file.Source.GetLineAndCharacter(block.End).Line)
                    {
                        int end = file.Source.LineStarts[line];
                        while (end < file.Source.Length && file.Source.Text[end] is not ('\r' or '\n' or '\u2028' or '\u2029'))
                            end++;
                        return (start, file.Source.ToBytePosition(end));
                    }
                }
                return (start, node.End);
            case CaseOrDefaultClauseNode clause:
                return (TokenRange(file, node.Pos).Start, clause.Statements is { Count: > 0 } statements ? statements[0].Pos : node.End);
            case ReturnStatementNode or YieldExpressionNode:
                return TokenRange(file, node.Pos);
            case SatisfiesExpressionNode satisfies:
                if (OriginatingSatisfiesTag(file, satisfies) is { } tag)
                    return TokenRange(file, tag.TagName!.Pos);
                return TokenRange(file, satisfies.Expression!.End);
            case ConstructorDeclarationNode when (node.Flags & NodeFlags.Reparsed) == 0:
                var scanner = ScannerAt(file, node.Pos);
                int constructorStart = scanner.TokenStart;
                while (scanner.Kind is not (SyntaxKind.ConstructorKeyword or SyntaxKind.StringLiteral or SyntaxKind.EndOfFile))
                    scanner.Scan();
                return (file.Source.ToBytePosition(constructorStart), file.Source.ToBytePosition(scanner.Position));
        }
        if (errorNode is null)
            return TokenRange(file, node.Pos);
        int position = errorNode.Pos;
        if (position != errorNode.End && errorNode is not JsxTextNode)
            position = TokenRange(file, position).Start;
        return (position, errorNode.End);
    }

    private static JSDocSatisfiesTagNode? OriginatingSatisfiesTag(SourceFileNode file, SatisfiesExpressionNode node)
    {
        var type = node.Type!;
        if ((type.Flags & NodeFlags.Reparsed) == 0)
            return null;
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if ((current.Flags & NodeFlags.HasJSDoc) == 0)
                continue;
            JSDocSatisfiesTagNode? first = null;
            foreach (var comment in file.GetDocumentation(current))
                foreach (var tag in comment.Tags?.OfType<JSDocSatisfiesTagNode>() ?? [])
                {
                    first ??= tag;
                    if (tag.TypeExpression is ITypedNode { Type: { } declared } && declared.Pos == type.Pos && declared.End == type.End)
                        return tag;
                }
            return first;
        }
        return null;
    }
}
