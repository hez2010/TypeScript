using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Projects;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed record DocumentHighlight(DocumentRange Range, int Kind);
public sealed record MultiDocumentHighlight(Utf8String Uri, IReadOnlyList<DocumentHighlight> Highlights);

public sealed partial class LanguageServiceDocument
{
    public async ValueTask<MultiDocumentHighlight[]> GetDocumentHighlightsAsync(ProjectSnapshot project, DocumentPosition position,
        IReadOnlyList<Utf8String>? filesToSearch = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        using var request = new ProjectRequest(cancellation);
        using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request);
        var program = project.Program!;
        var maps = new DeclarationMaps(program, cancellation);
        Dictionary<Utf8String, List<DocumentHighlight>> result = [];
        foreach (var (projection, mapped) in FromPosition(position, MappingFeature.DocumentHighlights))
        {
            if (mapped.Fidelity is not (MappingFidelity.Exact or MappingFidelity.Atom)) continue;
            var file = projection.File;
            var node = await SyntaxNavigation.GetTouchingPropertyNameAsync(file, mapped.Position, cancellation);
            if (node.Parent is JsxClosingElementNode || node.Parent is JsxOpeningElementNode opening && opening.TagName == node)
            {
                var uri = DocumentUris.FromFileName(projection.OriginalFileName);
                if (!result.ContainsKey(uri)) result[uri] = [];
                if (node.Parent.Parent is JsxElementNode element)
                    foreach (var tag in new SyntaxNode?[] { element.OpeningElement, element.ClosingElement })
                        if (tag is not null) await AddSyntaxAsync(tag, projection);
                continue;
            }
            var files = (filesToSearch ?? []).Distinct().Select(name => program.GetFile(name)?.Syntax).OfType<SourceFileNode>().ToArray();
            if (files.Length == 0) files = [file];
            Dictionary<Utf8String, List<DocumentHighlight>> semantic = [];
            foreach (var group in await lease.Checker.GetReferenceGroupsAsync(node, mapped.Position, files, new(ReferenceUse.Highlights), cancellation))
                foreach (var entry in group.Entries)
                {
                    var (source, start, end) = await ReferenceRangeAsync(entry, cancellation);
                    var range = ReferenceMap(program, maps, source, start, end, MappingFeature.DocumentHighlights);
                    if (range.Fidelity is not (MappingFidelity.Exact or MappingFidelity.Atom)) continue;
                    var name = ReferenceOriginalFileName(program, source);
                    if (!semantic.TryGetValue(name, out var entries)) semantic[name] = entries = [];
                    entries.Add(new(range.Range, entry.Kind != ReferenceEntryKind.Range && entry.Node is { } reference && ReferenceNavigation.IsWriteAccess(reference) ? 3 : 2));
                }
            bool found = false;
            foreach (var source in files)
                if (semantic.TryGetValue(ReferenceOriginalFileName(program, source), out var entries))
                {
                    found = true;
                    foreach (var entry in entries) Add(DocumentUris.FromFileName(ReferenceOriginalFileName(program, source)), entry);
                }
            if (!found)
                foreach (var span in await new HighlightSyntax(file, cancellation).GetAsync(node))
                {
                    var range = projection.ToRange(span.Start, span.End, span.Unfiltered ? null : MappingFeature.DocumentHighlights);
                    if (range.Fidelity != MappingFidelity.None) Add(DocumentUris.FromFileName(projection.OriginalFileName), new(range.Range, 2));
                }
        }
        return result.Select(pair => new MultiDocumentHighlight(pair.Key, pair.Value.DistinctBy(h => h.Range).ToArray())).ToArray();

        void Add(Utf8String uri, DocumentHighlight highlight)
        { if (!result.TryGetValue(uri, out var entries)) result[uri] = entries = []; entries.Add(highlight); }
        async ValueTask AddSyntaxAsync(SyntaxNode node, DocumentProjection projection)
        {
            int start = await SyntaxNavigation.GetStartAsync(node, projection.File, cancellation: cancellation);
            var range = projection.ToRange(start, node.End, MappingFeature.DocumentHighlights);
            if (range.Fidelity != MappingFidelity.None) Add(DocumentUris.FromFileName(projection.OriginalFileName), new(range.Range, 2));
        }
    }
}

internal sealed class HighlightSyntax(SourceFileNode file, CancellationToken cancellation)
{
    internal readonly record struct Span(int Start, int End, bool Unfiltered = false);
    private readonly List<SyntaxNode> tokens = [];
    private IReadOnlyList<SyntaxNode> Children(SyntaxNode node) => SyntaxNavigation.NonDocumentationChildren(node, file, cancellation);
    private ValueTask<SyntaxNode?> FirstAsync(SyntaxNode node) => SyntaxNavigation.FirstTokenAsync(node, file, cancellation);
    private ValueTask<SyntaxNode?> ChildAsync(SyntaxNode node, K kind) => SyntaxNavigation.FindChildOfKindAsync(node, kind, file, cancellation);
    private void Add(SyntaxNode? node) { if (node is not null) tokens.Add(node); }
    private static bool Function(SyntaxNode? node) => node is IFunctionSignature;
    private static bool Loop(SyntaxNode? node) => node is ForStatementNode or ForInOrOfStatementNode or WhileStatementNode or DoStatementNode;
    private static SyntaxNode? FunctionOf(SyntaxNode node)
    { for (var parent = node.Parent; parent is not null; parent = parent.Parent) if (Function(parent)) return parent; return null; }

    internal async ValueTask<IReadOnlyList<Span>> GetAsync(SyntaxNode node)
    {
        var parent = node.Parent;
        switch (node.Kind)
        {
            case K.IfKeyword or K.ElseKeyword when parent is IfStatementNode statement:
                return await IfElseAsync(statement);
            case K.ReturnKeyword when parent is ReturnStatementNode:
                if (FunctionOf(parent) is { } function && SemanticSyntax.Body(function) is { } body)
                { await ReturnsAsync(body); await ThrowsAsync(body); }
                break;
            case K.ThrowKeyword when parent is ThrowStatementNode:
                if (ThrowOwner(parent) is { } owner)
                { await ThrowsAsync(owner); if (owner is BlockNode && Function(owner.Parent)) await ReturnsAsync(owner); }
                break;
            case K.TryKeyword or K.CatchKeyword or K.FinallyKeyword:
                if ((node.Kind == K.CatchKeyword ? parent?.Parent : parent) is TryStatementNode attempt)
                {
                    if (await FirstAsync(attempt) is { Kind: K.TryKeyword } first) Add(first);
                    if (attempt.CatchClause is not null) Add(await ChildAsync(attempt, K.CatchKeyword));
                    if (attempt.FinallyBlock is not null) Add(await ChildAsync(attempt, K.FinallyKeyword));
                }
                break;
            case K.SwitchKeyword when parent is SwitchStatementNode switchParent:
                await SwitchAsync(switchParent); break;
            case K.CaseKeyword or K.DefaultKeyword when parent is CaseOrDefaultClauseNode && parent.Parent?.Parent is SwitchStatementNode clauseSwitch:
                await SwitchAsync(clauseSwitch); break;
            case K.BreakKeyword or K.ContinueKeyword when parent is BreakStatementNode or ContinueStatementNode:
                if (BreakOwner(parent) is SwitchStatementNode selection) await SwitchAsync(selection);
                else if (BreakOwner(parent) is { } loop) await LoopAsync(loop);
                break;
            case K.ForKeyword or K.WhileKeyword or K.DoKeyword when Loop(parent):
                await LoopAsync(parent!); break;
            case K.ConstructorKeyword or K.GetKeyword or K.SetKeyword:
                bool Constructor(SyntaxNode? n) => node.Kind == K.ConstructorKeyword ? n is ConstructorDeclarationNode : n is GetAccessorDeclarationNode or SetAccessorDeclarationNode;
                if (Constructor(parent) && parent?.BindingSymbol is { } symbol)
                    foreach (var declaration in symbol.Declarations)
                        if (Constructor(declaration)) Add(Children(declaration).FirstOrDefault(n => node.Kind == K.ConstructorKeyword
                            ? n.Kind == K.ConstructorKeyword : n.Kind is K.GetKeyword or K.SetKeyword));
                break;
            case K.AwaitKeyword when parent is AwaitExpressionNode:
                await AsyncAsync(parent, false); break;
            case K.AsyncKeyword:
                await AsyncAsync(node, false); break;
            case K.YieldKeyword:
                await AsyncAsync(node, true); break;
            case K.InKeyword or K.OutKeyword:
                break;
            default:
                if (parent is not null && (QuerySyntax.Declaration(parent) || parent is VariableStatementNode))
                    foreach (var declaration in ModifierDeclarations(parent, node.Kind))
                        Add(declaration.ModifierList?.FirstOrDefault(modifier => modifier.Kind == node.Kind));
                break;
        }
        List<Span> spans = [];
        foreach (var token in tokens) spans.Add(new(await SyntaxNavigation.GetStartAsync(token, file, cancellation: cancellation), token.End));
        return spans;
    }

    private async ValueTask<IReadOnlyList<Span>> IfElseAsync(IfStatementNode statement)
    {
        while (statement.Parent is IfStatementNode parent && parent.ElseStatement == statement) statement = parent;
        while (true)
        {
            var children = Children(statement);
            if (children.FirstOrDefault() is { Kind: K.IfKeyword } first) Add(first);
            Add(children.LastOrDefault(child => child.Kind == K.ElseKeyword));
            if (statement.ElseStatement is not IfStatementNode next) break;
            statement = next;
        }
        List<Span> result = [];
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            int start = await SyntaxNavigation.GetStartAsync(token, file, cancellation: cancellation);
            bool combine = token.Kind == K.ElseKeyword && i + 1 < tokens.Count;
            if (combine)
            {
                int next = await SyntaxNavigation.GetStartAsync(tokens[i + 1], file, cancellation: cancellation);
                // The reference examines each UTF-8 byte as a rune here.
                for (int p = next - 1; p >= token.End; p--)
                    if (file.Source.Text[p] is not (9 or 11 or 12 or 32 or 0x85 or 0xA0)) { combine = false; break; }
            }
            result.Add(new(start, combine ? tokens[++i].End : token.End, combine));
        }
        return result;
    }
    private IEnumerable<SyntaxNode> Walk(SyntaxNode root, Func<SyntaxNode, bool> descend)
    {
        Stack<SyntaxNode> pending = new(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            cancellation.ThrowIfCancellationRequested(); yield return node;
            if (descend(node)) for (int i = node.ChildCount - 1; i >= 0; i--) pending.Push(node.GetChild(i));
        }
    }
    private async ValueTask ReturnsAsync(SyntaxNode root)
    {
        foreach (var node in Walk(root, node => node.Kind is K.CaseBlock or K.Block or K.IfStatement or K.DoStatement or K.WhileStatement
            or K.ForStatement or K.ForInStatement or K.ForOfStatement or K.WithStatement or K.SwitchStatement or K.CaseClause or K.DefaultClause
            or K.LabeledStatement or K.TryStatement or K.CatchClause))
            if (node is ReturnStatementNode) Add(await ChildAsync(node, K.ReturnKeyword));
    }
    private async ValueTask ThrowsAsync(SyntaxNode root)
    {
        Stack<SyntaxNode> pending = new(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            cancellation.ThrowIfCancellationRequested();
            if (node is ThrowStatementNode) { Add(await ChildAsync(node, K.ThrowKeyword)); continue; }
            if (Function(node)) continue;
            if (node is TryStatementNode attempt)
            {
                if (attempt.FinallyBlock is { } final) pending.Push(final);
                if (((SyntaxNode?)attempt.CatchClause ?? attempt.TryBlock) is { } body) pending.Push(body);
                continue;
            }
            for (int i = node.ChildCount - 1; i >= 0; i--) pending.Push(node.GetChild(i));
        }
    }
    private static SyntaxNode? ThrowOwner(SyntaxNode node)
    {
        for (; node.Parent is { } parent; node = parent)
        {
            if (parent is SourceFileNode || parent is BlockNode && Function(parent.Parent)) return parent;
            if (parent is TryStatementNode { CatchClause: not null } attempt && attempt.TryBlock == node) return node;
        }
        return null;
    }
    private static SyntaxNode? BreakOwner(SyntaxNode statement)
    {
        var label = statement is BreakStatementNode stop ? stop.Label : ((ContinueStatementNode)statement).Label;
        for (var node = statement; node is not null; node = node.Parent)
        {
            if (Function(node)) return null;
            if (!Loop(node) && (node is not SwitchStatementNode || statement is ContinueStatementNode)) continue;
            if (label is null) return node;
            for (var parent = node.Parent; parent is LabeledStatementNode labeled; parent = parent.Parent)
                if (ReferenceNavigation.Text(labeled.Label) == ReferenceNavigation.Text(label)) return node;
        }
        return null;
    }
    private async ValueTask SwitchAsync(SwitchStatementNode selection)
    {
        if (await FirstAsync(selection) is { Kind: K.SwitchKeyword } first) Add(first);
        if (selection.CaseBlock is CaseBlockNode { Clauses: { } clauses })
            foreach (var clause in clauses)
            {
                if (await FirstAsync(clause) is { Kind: K.CaseKeyword or K.DefaultKeyword } keyword) Add(keyword);
                foreach (var node in Walk(clause, n => !Function(n) && n is not (BreakStatementNode or ContinueStatementNode)))
                    if (node is BreakStatementNode && BreakOwner(node) == selection) Add(await FirstAsync(node));
            }
    }
    private async ValueTask LoopAsync(SyntaxNode loop)
    {
        if (await FirstAsync(loop) is { Kind: K.ForKeyword or K.DoKeyword or K.WhileKeyword } first)
        { Add(first); if (loop is DoStatementNode) Add(Children(loop).LastOrDefault(n => n.Kind == K.WhileKeyword)); }
        foreach (var node in Walk(loop, n => !Function(n) && n is not (BreakStatementNode or ContinueStatementNode)))
            if (node is BreakStatementNode or ContinueStatementNode && BreakOwner(node) == loop
                && await FirstAsync(node) is { Kind: K.BreakKeyword or K.ContinueKeyword } keyword) Add(keyword);
    }
    private async ValueTask AsyncAsync(SyntaxNode node, bool yield)
    {
        if (FunctionOf(node) is not { } function) return;
        if (!yield && function.ModifierList is { } modifiers)
            foreach (var modifier in modifiers) if (modifier.Kind == K.AsyncKeyword) Add(modifier);
        for (int i = 0; i < function.ChildCount; i++)
            foreach (var child in Walk(function.GetChild(i), n => !Function(n) && !SemanticSyntax.ClassLike(n)
                && n is not (InterfaceDeclarationNode or ModuleDeclarationNode or TypeAliasDeclarationNode) && !SemanticSyntax.TypeNode(n)))
                if (yield ? child is YieldExpressionNode : child is AwaitExpressionNode)
                { var keyword = await FirstAsync(child); if (keyword?.Kind == (yield ? K.YieldKeyword : K.AwaitKeyword)) Add(keyword); }
    }
    private static IEnumerable<SyntaxNode> ModifierDeclarations(SyntaxNode declaration, K kind)
    {
        var container = declaration.Parent;
        switch (container)
        {
            case ModuleBlockNode or SourceFileNode or BlockNode or CaseOrDefaultClauseNode:
                if (kind == K.AbstractKeyword && declaration is ClassDeclarationNode { Members: { } members }) return members.Append(declaration);
                return Statements(container);
            case ConstructorDeclarationNode or MethodDeclarationNode or FunctionDeclarationNode:
                var parameters = ((IFunctionSignature)container).Parameters?.AsEnumerable() ?? [];
                return SemanticSyntax.ClassLike(container.Parent) ? parameters.Concat(Members(container.Parent!)) : parameters;
            case ClassDeclarationNode or ClassExpressionNode or InterfaceDeclarationNode or TypeLiteralNode:
                var nodes = Members(container);
                if (kind is K.PublicKeyword or K.PrivateKeyword or K.ProtectedKeyword or K.ReadonlyKeyword)
                    return nodes.Concat(nodes.OfType<ConstructorDeclarationNode>().FirstOrDefault()?.Parameters?.AsEnumerable() ?? []);
                return kind == K.AbstractKeyword ? nodes.Append(container) : nodes;
            default: return [];
        }
    }
    private static IEnumerable<SyntaxNode> Members(SyntaxNode node) => node switch
    { ClassDeclarationNode n => n.Members?.AsEnumerable() ?? [], ClassExpressionNode n => n.Members?.AsEnumerable() ?? [], InterfaceDeclarationNode n => n.Members?.AsEnumerable() ?? [], TypeLiteralNode n => n.Members?.AsEnumerable() ?? [], _ => [] };
    private static IEnumerable<SyntaxNode> Statements(SyntaxNode node) => node switch
    { SourceFileNode n => n.Statements?.AsEnumerable() ?? [], BlockNode n => n.Statements?.AsEnumerable() ?? [], ModuleBlockNode n => n.Statements?.AsEnumerable() ?? [], CaseOrDefaultClauseNode n => n.Statements?.AsEnumerable() ?? [], _ => [] };
}
