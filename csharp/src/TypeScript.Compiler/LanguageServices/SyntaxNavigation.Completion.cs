using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public static partial class SyntaxNavigation
{
    internal static async ValueTask<SyntaxNode?> GetLastTokenAsync(SyntaxNode node, SourceFileNode file, CancellationToken cancellation)
    {
        while (node.Kind > K.LastToken)
        {
            cancellation.ThrowIfCancellationRequested();
            SyntaxNode? lastChild = null;
            foreach (var group in await ChildrenAsync(node, file, cancellation))
            {
                if (group.Node is { } child) lastChild = child;
                else if (group.List?.LastOrDefault(Visible) is { } last) lastChild = last;
            }
            if (IsJSDoc(node) && lastChild is null) return null;
            var scanner = ScanAt(file, lastChild?.End ?? node.Pos);
            SyntaxNode? token = null;
            for (int start = lastChild?.End ?? node.Pos; start < node.End;)
            {
                int end = scanner.Position;
                token = file.GetOrCreateToken(scanner.Kind, scanner.FullStart, end, node, scanner.Flags);
                if (end <= start) break;
                start = end;
                scanner.Scan();
            }
            if (token is not null) return token;
            if (lastChild is null || lastChild.Kind <= K.LastToken) return lastChild;
            node = lastChild;
        }
        return null;
    }

    internal static async ValueTask<SourceCommentRange?> GetEnclosingCommentAsync(SourceFileNode file, int position, SyntaxNode? preceding, CancellationToken cancellation)
    {
        var current = await GetTokenAtPositionAsync(file, position, cancellation).ConfigureAwait(false);
        for (var node = current; node is not null; node = node.Parent)
            if (node.Kind == K.JSDoc) { current = node.Parent!; break; }
        if (SmartIndenter.Start(current, file) <= position && position < current.End) return null;
        var ranges = preceding is null ? new List<SourceCommentRange>() : SyntaxPrinter.CommentRanges(file.Source.Text, preceding.End, true).ToList();
        if (current.Kind != K.JsxText) ranges.AddRange(SyntaxPrinter.CommentRanges(file.Source.Text, current.Pos, false));
        foreach (var range in ranges)
            if (range.Pos < position && position < range.End || position == range.End
                && (range.Kind == K.SingleLineCommentTrivia || position == file.Source.Length)) return range;
        return null;
    }

    public static async ValueTask<bool> IsCompletedNodeAsync(SyntaxNode? node, SourceFileNode file, CancellationToken cancellation = default)
    {
        List<SyntaxNode>? statements = null;
        bool result = false;
        while (node is not null && (node.Pos != node.End || node.Kind == K.EndOfFile))
        {
            cancellation.ThrowIfCancellationRequested();
            switch (node.Kind)
            {
                case K.ClassDeclaration: case K.InterfaceDeclaration: case K.EnumDeclaration:
                case K.ObjectLiteralExpression: case K.ObjectBindingPattern: case K.TypeLiteral:
                case K.Block: case K.ModuleBlock: case K.CaseBlock: case K.NamedImports: case K.NamedExports:
                    result = await EndsWithAsync(node, K.CloseBraceToken, file, cancellation).ConfigureAwait(false); break;
                case K.CatchClause: node = ((CatchClauseNode)node).Block; continue;
                case K.NewExpression when ((NewExpressionNode)node).Arguments is null: result = true; break;
                case K.NewExpression: case K.CallExpression: case K.ParenthesizedExpression: case K.ParenthesizedType:
                    result = await EndsWithAsync(node, K.CloseParenToken, file, cancellation).ConfigureAwait(false); break;
                case K.FunctionType: case K.ConstructorType: node = ((ITypedNode)node).Type; continue;
                case K.Constructor: case K.GetAccessor: case K.SetAccessor: case K.FunctionDeclaration:
                case K.FunctionExpression: case K.MethodDeclaration: case K.MethodSignature:
                case K.ConstructSignature: case K.CallSignature: case K.ArrowFunction:
                    if ((FunctionBody(node) ?? ((ITypedNode)node).Type) is { } body) { node = body; continue; }
                    result = await FindChildOfKindAsync(node, K.CloseParenToken, file, cancellation).ConfigureAwait(false) is not null; break;
                case K.ModuleDeclaration: node = ((ModuleDeclarationNode)node).Body; continue;
                case K.IfStatement:
                    var conditional = (IfStatementNode)node; node = conditional.ElseStatement ?? conditional.ThenStatement; continue;
                case K.ExpressionStatement:
                    (statements ??= []).Add(node); node = ((ExpressionStatementNode)node).Expression; continue;
                case K.ArrayLiteralExpression: case K.ArrayBindingPattern: case K.ElementAccessExpression:
                case K.ComputedPropertyName: case K.TupleType:
                    result = await EndsWithAsync(node, K.CloseBracketToken, file, cancellation).ConfigureAwait(false); break;
                case K.IndexSignature:
                    if (((IndexSignatureDeclarationNode)node).Type is { } type) { node = type; continue; }
                    result = await FindChildOfKindAsync(node, K.CloseBracketToken, file, cancellation).ConfigureAwait(false) is not null; break;
                case K.CaseClause: case K.DefaultClause: result = false; break;
                case K.ForStatement: node = ((ForStatementNode)node).Statement; continue;
                case K.ForInStatement: case K.ForOfStatement: node = ((ForInOrOfStatementNode)node).Statement; continue;
                case K.WhileStatement: node = ((WhileStatementNode)node).Statement; continue;
                case K.DoStatement:
                    if (await FindChildOfKindAsync(node, K.WhileKeyword, file, cancellation).ConfigureAwait(false) is null)
                    { node = ((DoStatementNode)node).Statement; continue; }
                    result = await EndsWithAsync(node, K.CloseParenToken, file, cancellation).ConfigureAwait(false); break;
                case K.TypeQuery: node = ((TypeQueryNode)node).ExprName; continue;
                case K.TypeOfExpression: node = ((TypeOfExpressionNode)node).Expression; continue;
                case K.DeleteExpression: node = ((DeleteExpressionNode)node).Expression; continue;
                case K.VoidExpression: node = ((VoidExpressionNode)node).Expression; continue;
                case K.YieldExpression: node = ((YieldExpressionNode)node).Expression; continue;
                case K.SpreadElement: node = ((SpreadElementNode)node).Expression; continue;
                case K.TaggedTemplateExpression: node = ((TaggedTemplateExpressionNode)node).Template; continue;
                case K.TemplateExpression: node = ((TemplateExpressionNode)node).TemplateSpans?.LastOrDefault(); continue;
                case K.TemplateSpan:
                    result = ((TemplateSpanNode)node).Literal is { } literal && literal.Pos != literal.End; break;
                case K.ExportDeclaration:
                    result = ((ExportDeclarationNode)node).ModuleSpecifier is { } export && export.Pos != export.End; break;
                case K.ImportDeclaration:
                    result = ((ImportDeclarationNode)node).ModuleSpecifier is { } import && import.Pos != import.End; break;
                case K.PrefixUnaryExpression: node = ((PrefixUnaryExpressionNode)node).Operand; continue;
                case K.BinaryExpression: node = ((BinaryExpressionNode)node).Right; continue;
                case K.ConditionalExpression: node = ((ConditionalExpressionNode)node).WhenFalse; continue;
                default: result = true; break;
            }
            break;
        }
        if (!result && statements is not null)
            foreach (var statement in statements)
                if (await FindChildOfKindAsync(statement, K.SemicolonToken, file, cancellation).ConfigureAwait(false) is not null) return true;
        return result;
    }

    private static async ValueTask<bool> EndsWithAsync(SyntaxNode node, K kind, SourceFileNode file, CancellationToken cancellation)
    {
        SyntaxNode? last = null;
        foreach (var group in await ChildrenAsync(node, file, cancellation).ConfigureAwait(false))
            if (group.Node is { } child) last = child;
            else if (group.List?.LastOrDefault(Visible) is { } item) last = item;
        int start = last?.End ?? node.Pos;
        K previous = K.Unknown, current = last?.Kind ?? K.Unknown;
        var scanner = ScanAt(file, start);
        while (start < node.End)
        {
            cancellation.ThrowIfCancellationRequested();
            previous = current; current = scanner.Kind;
            if (scanner.Position <= start) break;
            start = scanner.Position; scanner.Scan();
        }
        return current == kind || current == K.SemicolonToken && previous == kind;
    }

    internal static SyntaxNode? FunctionBody(SyntaxNode node) => node switch
    {
        FunctionDeclarationNode n => n.Body, FunctionExpressionNode n => n.Body, ArrowFunctionNode n => n.Body,
        MethodDeclarationNode n => n.Body, ConstructorDeclarationNode n => n.Body,
        GetAccessorDeclarationNode n => n.Body, SetAccessorDeclarationNode n => n.Body, _ => null,
    };

    internal static ValueTask<SyntaxNode?> FirstTokenAsync(SyntaxNode node, SourceFileNode file, CancellationToken cancellation)
    {
        while (node.Kind > K.LastToken)
        {
            cancellation.ThrowIfCancellationRequested();
            SyntaxNode? first = node.ChildCount != 0 && (node.Flags & NodeFlags.Reparsed) == 0 ? node.GetChild(0) : null;
            if (node.Pos < (first?.Pos ?? node.End))
            {
                var scanner = ScanAt(file, node.Pos);
                return new(file.GetOrCreateToken(scanner.Kind, scanner.FullStart, scanner.Position, node, scanner.Flags));
            }
            if (first is null || first.Kind < K.FirstNode) return new(first);
            node = first;
        }
        return new((SyntaxNode?)null);
    }

    internal static async ValueTask<SyntaxNode?> LastChildAsync(SyntaxNode node, SourceFileNode file, CancellationToken cancellation)
    {
        SyntaxNode? last = null;
        foreach (var group in await ChildrenAsync(node, file, cancellation).ConfigureAwait(false))
            if (group.Node is { } child) last = child;
            else if (group.List?.LastOrDefault(Visible) is { } item) last = item;
        int start = last?.End ?? node.Pos;
        var scanner = ScanAt(file, start);
        while (start < node.End)
        {
            cancellation.ThrowIfCancellationRequested();
            last = file.GetOrCreateToken(scanner.Kind, scanner.FullStart, scanner.Position, node, scanner.Flags);
            if (scanner.Position <= start) break;
            start = scanner.Position; scanner.Scan();
        }
        return last;
    }

    internal static async ValueTask<SyntaxNode?> LastTokenAsync(SyntaxNode node, SourceFileNode file, CancellationToken cancellation)
    {
        while (node.Kind > K.LastToken)
        {
            var child = await LastChildAsync(node, file, cancellation).ConfigureAwait(false);
            if (child is null || child.Kind < K.FirstNode) return child;
            node = child;
        }
        return null;
    }

    internal static async ValueTask<bool> IsAutomaticSemicolonCandidateAsync(int position, SyntaxNode context, SourceFileNode file, CancellationToken cancellation)
    {
        bool Signature(K kind) => kind is K.CallSignature or K.ConstructSignature or K.IndexSignature or K.PropertySignature or K.MethodSignature;
        bool Function(K kind) => kind is K.FunctionDeclaration or K.Constructor or K.MethodDeclaration or K.GetAccessor or K.SetAccessor;
        bool Statement(K kind) => kind is K.VariableStatement or K.ExpressionStatement or K.DoStatement or K.ContinueStatement or K.BreakStatement
            or K.ReturnStatement or K.ThrowStatement or K.DebuggerStatement or K.PropertyDeclaration or K.TypeAliasDeclaration or K.ImportDeclaration
            or K.ImportEqualsDeclaration or K.ExportDeclaration or K.NamespaceExportDeclaration or K.ExportAssignment;
        SyntaxNode? node = context;
        while (node is not null && node.End == position && !Signature(node.Kind) && !Function(node.Kind)
            && node.Kind != K.ModuleDeclaration && !Statement(node.Kind)) node = node.Parent;
        if (node is null || node.End != position) return false;
        var last = await LastTokenAsync(node, file, cancellation).ConfigureAwait(false);
        if (last?.Kind == K.SemicolonToken) return false;
        if (Signature(node.Kind)) { if (last?.Kind == K.CommaToken) return false; }
        else if (node.Kind == K.ModuleDeclaration)
        {
            if ((await LastChildAsync(node, file, cancellation).ConfigureAwait(false))?.Kind == K.ModuleBlock) return false;
        }
        else if (Function(node.Kind))
        {
            var child = await LastChildAsync(node, file, cancellation).ConfigureAwait(false);
            if (child is { Kind: K.Block, Parent.HasFunctionSignature: true }) return false;
        }
        else if (!Statement(node.Kind)) return false;
        if (node.Kind == K.DoStatement) return true;
        var top = node;
        while (top.Parent is { } parent) top = parent;
        var next = await FindNextTokenAsync(node, top, file, cancellation).ConfigureAwait(false);
        return next is null || next.Kind == K.CloseBraceToken || !SmartIndenter.SameLine(node.End, SmartIndenter.Start(next, file), file);
    }
}
