using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

/// <summary>Computes indentation in columns from byte positions, syntax, and existing source indentation.</summary>
public static class SmartIndenter
{
    public static ValueTask<int> GetIndentationAsync(int position, SourceFileNode file, FormatCodeSettings? options = null,
        bool assumeNewLineBeforeCloseBrace = false, CancellationToken cancellation = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        return new Indentation(file, options ?? new(), cancellation).AtPositionAsync(position, assumeNewLineBeforeCloseBrace);
    }

    public static ValueTask<int> GetIndentationForNodeAsync(SyntaxNode node, SourceFileNode file, FormatCodeSettings? options = null,
        (int Start, int End)? ignoreActualIndentationRange = null, CancellationToken cancellation = default)
    {
        var worker = new Indentation(file, options ?? new(), cancellation);
        var (line, character) = file.Source.GetLineAndCharacter(Start(node, file));
        return worker.ForNodeAsync(node, line, character, ignoreActualIndentationRange, 0, false);
    }

    internal static int Start(SyntaxNode node, SourceFileNode file)
    {
        if (node.Pos == node.End && node.Kind != K.EndOfFile) return node.Pos;
        var scanner = new Scanner(file.Source);
        return node.Kind is >= K.FirstJSDocNode and <= K.LastJSDocNode or K.JsxText
            ? scanner.SkipTriviaAt(node.Pos, stopAtComments: true)
            : scanner.SkipTriviaAt(node.Pos, inJSDoc: (node.Flags & NodeFlags.JSDoc) != 0);
    }

    internal static bool SameLine(int start, int end, SourceFileNode file) =>
        file.Source.GetLineAndCharacter(start).Line == file.Source.GetLineAndCharacter(end).Line;

    internal static bool ShouldIndentChild(FormatCodeSettings options, SyntaxNode parent, SyntaxNode? child, SourceFileNode? file,
        bool isNextChild = false) => NodeWillIndentChild(options, parent, child, file, false)
            && !(isNextChild && child?.Kind is K.ReturnStatement or K.ThrowStatement or K.ContinueStatement or K.BreakStatement && parent.Kind != K.Block);

    internal static bool NodeWillIndentChild(FormatCodeSettings options, SyntaxNode parent, SyntaxNode? child, SourceFileNode? file,
        bool indentByDefault)
    {
        var kind = child?.Kind ?? K.Unknown;
        switch (parent.Kind)
        {
            case K.ExpressionStatement: case K.ClassDeclaration: case K.ClassExpression: case K.InterfaceDeclaration:
            case K.EnumDeclaration: case K.TypeAliasDeclaration: case K.ArrayLiteralExpression: case K.Block: case K.ModuleBlock:
            case K.ObjectLiteralExpression: case K.TypeLiteral: case K.MappedType: case K.TupleType: case K.ParenthesizedExpression:
            case K.PropertyAccessExpression: case K.CallExpression: case K.NewExpression: case K.VariableStatement:
            case K.ExportAssignment: case K.ReturnStatement: case K.ConditionalExpression: case K.ArrayBindingPattern:
            case K.ObjectBindingPattern: case K.JsxOpeningElement: case K.JsxOpeningFragment: case K.JsxSelfClosingElement:
            case K.JsxExpression: case K.MethodSignature: case K.CallSignature: case K.ConstructSignature: case K.Parameter:
            case K.FunctionType: case K.ConstructorType: case K.ParenthesizedType: case K.TaggedTemplateExpression:
            case K.AwaitExpression: case K.NamedExports: case K.NamedImports: case K.ExportSpecifier: case K.ImportSpecifier:
            case K.PropertyDeclaration: case K.CaseClause: case K.DefaultClause: return true;
            case K.CaseBlock: return options.IndentSwitchCase != false;
            case K.VariableDeclaration: case K.PropertyAssignment: case K.BinaryExpression:
                if (options.IndentMultiLineObjectLiteralBeginningOnBlankLine != true && file is not null && kind == K.ObjectLiteralExpression)
                    return SameLine(child!.Pos, child.End, file);
                if (parent.Kind == K.BinaryExpression && file is not null && kind == K.JsxElement)
                    return !SameLine(new Scanner(file.Source).SkipTriviaAt(parent.Pos), new Scanner(file.Source).SkipTriviaAt(child!.Pos), file);
                return parent.Kind != K.BinaryExpression || indentByDefault;
            case K.DoStatement: case K.WhileStatement: case K.ForInStatement: case K.ForOfStatement: case K.ForStatement:
            case K.IfStatement: case K.FunctionDeclaration: case K.FunctionExpression: case K.MethodDeclaration:
            case K.Constructor: case K.GetAccessor: case K.SetAccessor: return kind != K.Block;
            case K.ArrowFunction:
                return file is not null && kind == K.ParenthesizedExpression ? SameLine(child!.Pos, child.End, file) : kind != K.Block;
            case K.ExportDeclaration: return kind != K.NamedExports;
            case K.ImportDeclaration: return child is not ImportClauseNode clause || clause.NamedBindings is { Kind: not K.NamedImports };
            case K.JsxElement: return kind != K.JsxClosingElement;
            case K.JsxFragment: return kind != K.JsxClosingFragment;
            case K.IntersectionType: case K.UnionType: case K.SatisfiesExpression:
                return kind is not (K.TypeLiteral or K.TupleType or K.MappedType) && indentByDefault;
            case K.TryStatement: return kind != K.Block && indentByDefault;
            default: return indentByDefault;
        }
    }

    internal static int FirstNonWhitespaceColumn(int start, int end, SourceFileNode file, FormatCodeSettings options) =>
        FirstNonWhitespace(start, end, file, options).Column;

    internal static (int Character, int Column) FirstNonWhitespace(int start, int end, SourceFileNode file, FormatCodeSettings options)
    {
        int pos = start, column = 0;
        while (pos < end)
        {
            int ch = Wtf8.Decode(file.Source.Text.Span[pos..], out int width);
            if (!TokenFacts.IsWhiteSpace(ch)) break;
            // Keep the reference's column arithmetic, including non-aligned tabs.
            if (ch == '\t') { if (options.TabSize > 0) column += options.TabSize + column % options.TabSize; }
            else column++;
            pos += width;
        }
        return (pos - start, column);
    }

    internal sealed class Indentation(SourceFileNode file, FormatCodeSettings options, CancellationToken cancellation)
    {
        private int Line(int pos) => file.Source.GetLineAndCharacter(pos).Line;
        private int StartLine(SyntaxNode node) => Line(Start(node, file));
        private int LineStart(int pos) => file.Source.LineStarts[Line(pos)];

        internal async ValueTask<int> AtPositionAsync(int position, bool assumeNewLineBeforeCloseBrace)
        {
            cancellation.ThrowIfCancellationRequested();
            if (position > file.Source.Length) return options.BaseIndentSize;
            if (options.IndentStyle == IndentStyle.None) return 0;
            var preceding = await SyntaxNavigation.FindPrecedingTokenAsync(file, position, excludeJSDoc: true, cancellation: cancellation).ConfigureAwait(false);
            var comment = await SyntaxNavigation.GetEnclosingCommentAsync(file, position, preceding, cancellation).ConfigureAwait(false);
            if (comment is { Kind: K.MultiLineCommentTrivia } range) return CommentIndent(position, range.Pos);
            if (preceding is null) return options.BaseIndentSize;
            if (preceding.Kind is K.StringLiteral or K.RegularExpressionLiteral or K.NoSubstitutionTemplateLiteral or K.TemplateHead or K.TemplateMiddle or K.TemplateTail
                && Start(preceding, file) <= position && position < preceding.End) return 0;
            var current = await SyntaxNavigation.GetTokenAtPositionAsync(file, position, cancellation).ConfigureAwait(false);
            if (options.IndentStyle == IndentStyle.Block || current is { Kind: K.OpenBraceToken, Parent.Kind: K.ObjectLiteralExpression })
                return BlockIndent(position);
            if (preceding is { Kind: K.CommaToken, Parent: { Kind: not K.BinaryExpression } }
                && await ContainingListAsync(preceding).ConfigureAwait(false) is { } commaList && commaList.IndexOf(preceding) is > 0 and var commaIndex)
            {
                int actual = DeriveFromList(commaList, commaIndex - 1);
                if (actual != -1) return actual;
            }
            var container = await ListByRangeAsync(position, position, preceding.Parent).ConfigureAwait(false);
            if (container is not null && !(container.Pos <= preceding.Pos && preceding.End <= container.End))
            {
                int delta = current.Parent?.Kind is K.FunctionExpression or K.ArrowFunction ? 0 : options.IndentSize;
                int actual = ListStartIndent(container);
                return (actual == -1 ? 0 : actual) + delta;
            }
            SyntaxNode? previous = null;
            for (SyntaxNode? node = preceding; node is not null; previous = node, node = node.Parent)
            {
                cancellation.ThrowIfCancellationRequested();
                if ((position < node.End || !await SyntaxNavigation.IsCompletedNodeAsync(node, file, cancellation).ConfigureAwait(false))
                    && ShouldIndentChild(options, node, previous, file, true))
                {
                    var (startLine, startChar) = file.Source.GetLineAndCharacter(Start(node, file));
                    var next = await SyntaxNavigation.FindNextTokenAsync(preceding, node, file, cancellation).ConfigureAwait(false);
                    int brace = next?.Kind == K.OpenBraceToken ? 1 : next?.Kind == K.CloseBraceToken && StartLine(next) == Line(position) ? 2 : 0;
                    int delta = brace == 0 ? (Line(position) != startLine ? options.IndentSize : 0)
                        : assumeNewLineBeforeCloseBrace && brace == 2 ? options.IndentSize : 0;
                    return await ForNodeAsync(node, startLine, startChar, null, delta, true).ConfigureAwait(false);
                }
                int actual = await ListItemIndentAsync(node, true).ConfigureAwait(false);
                if (actual != -1) return actual;
            }
            return options.BaseIndentSize;
        }

        internal async ValueTask<int> ForNodeAsync(SyntaxNode current, int startLine, int startCharacter,
            (int Start, int End)? ignoreRange, int delta, bool isNextChild)
        {
            while (current.Parent is { } parent)
            {
                cancellation.ThrowIfCancellationRequested();
                int start = Start(current, file);
                bool useActual = ignoreRange is not { } range || start < range.Start || start > range.End;
                var container = await ContainingListAsync(current).ConfigureAwait(false);
                var (parentLine, parentCharacter) = file.Source.GetLineAndCharacter(container?.Pos ?? Start(parent, file));
                bool sameLine = parentLine == startLine || await StartsWithElseAsync(parent, current, startLine).ConfigureAwait(false);
                if (useActual)
                {
                    bool listIndentsChild = container is { Count: > 0 } && StartLine(container[0]) > parentLine;
                    int actual = await ListItemIndentAsync(current, listIndentsChild).ConfigureAwait(false);
                    if (actual != -1) return actual + delta;
                    if ((current.IsDeclarationNode && (current.Kind != K.TypeParameter || current.Parent is not null) || IsStatement(current.Kind))
                        && (parent.Kind == K.SourceFile || !sameLine))
                        return Column(startLine, startCharacter) + delta;
                }
                if (ShouldIndentChild(options, parent, current, file, isNextChild) && !sameLine) delta += options.IndentSize;
                bool trueStart = parent is CallExpressionNode { Arguments: { } args, Expression: { } expression }
                    && args.IndexOf(current) >= 0 && Line(expression.End) == startLine;
                current = parent;
                (startLine, startCharacter) = trueStart ? file.Source.GetLineAndCharacter(Start(current, file)) : (parentLine, parentCharacter);
            }
            return delta + options.BaseIndentSize;
        }

        private async ValueTask<int> ListItemIndentAsync(SyntaxNode node, bool listIndentsChild)
        {
            if (node.Parent?.Kind == K.VariableDeclarationList || await ContainingListAsync(node).ConfigureAwait(false) is not { } list) return -1;
            int index = list.IndexOf(node);
            if (index != -1 && DeriveFromList(list, index) is not -1 and var actual) return actual;
            int indent = ListStartIndent(list);
            return (indent == -1 ? 0 : indent) + (listIndentsChild ? options.IndentSize : 0);
        }

        private int ListStartIndent(NodeList list)
        {
            var (line, character) = file.Source.GetLineAndCharacter(list.Pos);
            return Column(line, character);
        }

        private int DeriveFromList(NodeList list, int index)
        {
            var (line, character) = file.Source.GetLineAndCharacter(Start(list[index], file));
            for (int i = index; i >= 0; i--)
            {
                if (list[i].Kind == K.CommaToken) continue;
                if (Line(list[i].End) != line) return Column(line, character);
                (line, character) = file.Source.GetLineAndCharacter(Start(list[i], file));
            }
            return -1;
        }

        private int Column(int line, int character)
        {
            int start = file.Source.LineStarts[line];
            return FirstNonWhitespaceColumn(start, start + character, file, options);
        }

        internal async ValueTask<bool> StartsWithElseAsync(SyntaxNode parent, SyntaxNode child, int line)
        {
            if (parent is not IfStatementNode conditional || conditional.ElseStatement != child) return false;
            var keyword = await SyntaxNavigation.FindPrecedingTokenAsync(file, child.Pos, cancellation: cancellation).ConfigureAwait(false);
            return keyword is not null && StartLine(keyword) == line;
        }

        internal ValueTask<NodeList?> ContainingListAsync(SyntaxNode node) => ListByRangeAsync(Start(node, file), node.End, node.Parent);

        private async ValueTask<NodeList?> ListByRangeAsync(int start, int end, SyntaxNode? node)
        {
            NodeList? first = null, second = null;
            switch (node)
            {
                case TypeReferenceNode n: first = n.TypeArguments; break;
                case ObjectLiteralExpressionNode n: first = n.Properties; break;
                case ArrayLiteralExpressionNode n: first = n.Elements; break;
                case TypeLiteralNode n: first = n.Members; break;
                case IFunctionSignature n when node.Kind is K.FunctionDeclaration or K.FunctionExpression or K.ArrowFunction or K.MethodDeclaration
                    or K.MethodSignature or K.CallSignature or K.Constructor or K.ConstructorType or K.ConstructSignature:
                    first = n.TypeParameters; second = n.Parameters; break;
                case GetAccessorDeclarationNode n: first = n.Parameters; break;
                case ClassDeclarationNode n: first = n.TypeParameters; break;
                case ClassExpressionNode n: first = n.TypeParameters; break;
                case InterfaceDeclarationNode n: first = n.TypeParameters; break;
                case TypeAliasDeclarationNode n: first = n.TypeParameters; break;
                case JSDocTemplateTagNode n: first = n.TypeParameters; break;
                case NewExpressionNode n: first = n.TypeArguments; second = n.Arguments; break;
                case CallExpressionNode n: first = n.TypeArguments; second = n.Arguments; break;
                case VariableDeclarationListNode n: first = n.Declarations; break;
                case BindingPatternNode n: first = n.Elements; break;
                case NamedImportsNode n: first = n.Elements; break;
                case NamedExportsNode n: first = n.Elements; break;
            }
            return await ContainsAsync(first).ConfigureAwait(false) ? first : await ContainsAsync(second).ConfigureAwait(false) ? second : null;
            async ValueTask<bool> ContainsAsync(NodeList? list)
            {
                if (list is null) return false;
                var prior = await SyntaxNavigation.FindPrecedingTokenAsync(file, list.Pos, cancellation: cancellation).ConfigureAwait(false);
                var scanner = new Scanner(file.Source, jsx: file.ScriptKind is ScriptKind.JSX or ScriptKind.TSX);
                scanner.ResetPosition(list.End); scanner.Scan();
                return (prior?.End ?? list.Pos) <= start && end <= (scanner.Kind == K.EndOfFile ? list.End : scanner.TokenStart);
            }
        }

        private int CommentIndent(int position, int commentStart)
        {
            int previousLine = Line(position) - 1, commentLine = Line(commentStart);
            if (previousLine <= commentLine) return FirstNonWhitespaceColumn(file.Source.LineStarts[commentLine], position, file, options);
            int start = file.Source.LineStarts[previousLine];
            var (character, column) = FirstNonWhitespace(start, position, file, options);
            return column == 0 ? 0 : file.Source.Text[start + character] == '*' ? column - 1 : column;
        }

        private int BlockIndent(int position)
        {
            int current = position;
            while (current > 0)
            {
                int ch = Wtf8.Decode(file.Source.Text.Span[current..], out int width);
                if (!TokenFacts.IsWhiteSpace(ch) && !TokenFacts.IsLineBreak(ch)) break;
                current -= width;
            }
            return FirstNonWhitespaceColumn(LineStart(current), current, file, options);
        }
    }

    private static bool IsStatement(K kind) => kind is K.EmptyStatement or K.VariableStatement or K.ExpressionStatement or K.IfStatement
        or K.DoStatement or K.WhileStatement or K.ForStatement or K.ForInStatement or K.ForOfStatement or K.ContinueStatement
        or K.BreakStatement or K.ReturnStatement or K.WithStatement or K.SwitchStatement or K.LabeledStatement or K.ThrowStatement
        or K.TryStatement or K.DebuggerStatement or K.NotEmittedStatement;
}
