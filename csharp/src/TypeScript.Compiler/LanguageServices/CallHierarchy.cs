using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed record CallHierarchyItem(Utf8String Name, DocumentSymbolKind Kind, Utf8String Uri, DocumentRange Range,
    DocumentRange SelectionRange, Utf8String Detail = default);
public sealed record CallHierarchyCall(CallHierarchyItem Item, IReadOnlyList<DocumentRange> FromRanges);

public sealed partial class LanguageServiceDocument
{
    public async ValueTask<CallHierarchyItem[]?> PrepareCallHierarchyAsync(ProjectSnapshot project, DocumentPosition position,
        CancellationToken cancellation = default)
    {
        using var request = new ProjectRequest(cancellation);
        using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request);
        var hierarchy = new CallHierarchy(this, project.Program!, lease.Checker, cancellation);
        List<CallHierarchyItem> items = [];
        HashSet<(Utf8String, DocumentRange)> seen = [];
        foreach (var node in await hierarchy.DeclarationsAsync(position, false))
            if (await hierarchy.ItemAsync(node) is { } item && seen.Add((item.Uri, item.SelectionRange))) items.Add(item);
        return items.Count == 0 ? null : items.ToArray();
    }

    public async ValueTask<CallHierarchyCall[]?> GetOutgoingCallsAsync(ProjectSnapshot project, DocumentPosition position,
        CancellationToken cancellation = default)
    {
        using var request = new ProjectRequest(cancellation);
        using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request);
        var hierarchy = new CallHierarchy(this, project.Program!, lease.Checker, cancellation);
        List<CallHierarchyCall> calls = [];
        foreach (var node in await hierarchy.DeclarationsAsync(position, true))
            MergeCallHierarchy(calls, await hierarchy.OutgoingAsync(node));
        return calls.Count == 0 ? null : calls.ToArray();
    }

    public async ValueTask<CallHierarchyCall[]?> GetIncomingCallsAsync(ProjectSnapshot project, DocumentPosition position,
        CancellationToken cancellation = default)
    {
        using var request = new ProjectRequest(cancellation);
        using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request);
        List<CallHierarchyCall> calls = [];
        foreach (var target in await IncomingCallTargetsAsync(project, position, lease.Checker, cancellation))
        {
            var file = project.Program!.GetFile(DocumentUris.ToFileName(target.Uri));
            if (file is null) continue;
            var service = new LanguageServiceDocument(project.Program, file, projections[0].Encoding);
            var data = await service.ReferenceDataAsync(project, target.Range.Start, new(ReferenceUse.References), lease.Checker, cancellation);
            MergeCallHierarchy(calls, SortCallHierarchy(await service.IncomingCallsAsync(project, data, lease.Checker, cancellation)));
        }
        return calls.Count == 0 ? null : calls.ToArray();
    }

    internal async ValueTask<DocumentLocation[]> IncomingCallTargetsAsync(ProjectSnapshot project, DocumentPosition position,
        Checker checker, CancellationToken cancellation)
    {
        var hierarchy = new CallHierarchy(this, project.Program!, checker, cancellation);
        List<DocumentLocation> targets = [];
        foreach (var node in await hierarchy.DeclarationsAsync(position, true))
        {
            if (node is SourceFileNode or ModuleDeclarationNode or ClassStaticBlockDeclarationNode || CallHierarchy.ReferenceNode(node) is not { } location) continue;
            var file = SemanticSyntax.Source(location)!;
            var projection = ProjectionForFile(project.Program!, file);
            int start = new Scanner(file.Source).SkipTriviaAt(location.Pos);
            if (projection.ToRange(start, start, MappingFeature.CallHierarchy).Fidelity == MappingFidelity.None) continue;
            targets.Add(new(DocumentUris.FromFileName(projection.OriginalFileName), projection.ToRange(start, start).Range));
        }
        return targets.ToArray();
    }

    internal ValueTask<List<CallHierarchyCall>> IncomingCallsAsync(ProjectSnapshot project, IReadOnlyList<ReferenceData> data,
        Checker checker, CancellationToken cancellation) => new CallHierarchy(this, project.Program!, checker, cancellation).IncomingAsync(data);

    internal static IEnumerable<CallHierarchyCall> SortCallHierarchy(IEnumerable<CallHierarchyCall> calls) => calls
        .OrderBy(call => call.Item.Uri).ThenBy(call => call.FromRanges[0].Start.Line).ThenBy(call => call.FromRanges[0].Start.Character)
        .ThenBy(call => call.FromRanges[0].End.Line).ThenBy(call => call.FromRanges[0].End.Character);

    internal static void MergeCallHierarchy(List<CallHierarchyCall> calls, IEnumerable<CallHierarchyCall> incoming)
    {
        foreach (var call in incoming)
        {
            int index = calls.FindIndex(existing => existing.Item.Uri == call.Item.Uri && existing.Item.SelectionRange == call.Item.SelectionRange);
            if (index < 0) calls.Add(call);
            else calls[index] = calls[index] with { FromRanges = calls[index].FromRanges.Concat(call.FromRanges).Distinct().ToArray() };
        }
    }

    private sealed class CallHierarchy(LanguageServiceDocument service, CompilerProgram program, Checker checker, CancellationToken cancellation)
    {
        private sealed record Site(SyntaxNode Declaration, SourceFileNode Source, int Start, int End);

        private static bool Assigned(SyntaxNode? node) => node is FunctionExpressionNode or ArrowFunctionNode or ClassExpressionNode
            && node.DeclarationName is null && node.Parent is VariableDeclarationNode or PropertyDeclarationNode
            && node.Parent is IInitializedNode parent && parent.Initializer == node && node.Parent.DeclarationName is IdentifierNode
            && (node.Parent is PropertyDeclarationNode || ((node.Parent.Flags | (node.Parent.Parent?.Flags ?? 0)
                | (node.Parent.Parent?.Parent is VariableStatementNode statement ? statement.Flags : 0)) & NodeFlags.Const) != 0);
        private static bool Named(SyntaxNode node) => node is FunctionExpressionNode or ClassExpressionNode && node.DeclarationName is IdentifierNode;
        private static bool Valid(SyntaxNode node) => node is SourceFileNode or FunctionDeclarationNode or ClassDeclarationNode
            or ClassStaticBlockDeclarationNode or MethodDeclarationNode or MethodSignatureDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode
            || node is ModuleDeclarationNode { Name: IdentifierNode } || Named(node) || Assigned(node);
        private static bool Possible(SyntaxNode node) => node is SourceFileNode or ModuleDeclarationNode or FunctionDeclarationNode or FunctionExpressionNode
            or ClassDeclarationNode or ClassExpressionNode or ClassStaticBlockDeclarationNode or MethodDeclarationNode or MethodSignatureDeclarationNode
            or GetAccessorDeclarationNode or SetAccessorDeclarationNode;
        internal static SyntaxNode? ReferenceNode(SyntaxNode node) => node is SourceFileNode ? node : node.DeclarationName
            ?? (Assigned(node) ? node.Parent!.DeclarationName : node.ModifierList?.FirstOrDefault(modifier => modifier.Kind == K.DefaultKeyword));
        private ValueTask<Symbol?> SymbolAsync(SyntaxNode node) => node is not ClassStaticBlockDeclarationNode && ReferenceNode(node) is { } name
            ? checker.GetSymbolAtLocationAsync(name, cancellation) : ValueTask.FromResult<Symbol?>(null);

        private async ValueTask<SyntaxNode?> ImplementationAsync(SyntaxNode node)
        {
            if (!SemanticSyntax.FunctionDeclarationLike(node) || SemanticSyntax.Body(node) is not null) return node;
            if (node is ConstructorDeclarationNode) return DecoratorSyntax.Members(node.Parent)?.FirstOrDefault(member => member is ConstructorDeclarationNode { Body: not null });
            if (node is FunctionDeclarationNode or MethodDeclarationNode)
                return (await SymbolAsync(node))?.ValueDeclaration is { } declaration && SemanticSyntax.FunctionDeclarationLike(declaration) && SemanticSyntax.Body(declaration) is not null ? declaration : null;
            return node;
        }

        private async ValueTask<SyntaxNode[]> InitialAsync(SyntaxNode node)
        {
            if (node is ClassStaticBlockDeclarationNode) return [node];
            if (SemanticSyntax.FunctionDeclarationLike(node) && await ImplementationAsync(node) is { } implementation) return [implementation];
            var symbol = await SymbolAsync(node);
            if (symbol is null || symbol.Declarations.Count == 0) return [node];
            List<SyntaxNode> declarations = [];
            SyntaxNode? last = null;
            foreach (var declaration in symbol.Declarations.OrderBy(d => SemanticSyntax.Source(d)!.FileName).ThenBy(d => d.Pos))
                if (Valid(declaration))
                {
                    if (last is null || last.Parent != declaration.Parent || last.End != declaration.Pos) declarations.Add(declaration);
                    last = declaration;
                }
            return declarations.Count == 0 ? [node] : declarations.ToArray();
        }

        private async ValueTask<SyntaxNode[]> ResolveAsync(SyntaxNode? node)
        {
            bool followed = false;
            while (node is not null)
            {
                cancellation.ThrowIfCancellationRequested();
                if (Valid(node)) return await InitialAsync(node);
                if (Possible(node) && Ancestor(node, Valid) is { } ancestor) return await InitialAsync(ancestor);
                if (QuerySyntax.DeclarationName(node))
                {
                    var parent = node.Parent!;
                    if (Valid(parent)) return await InitialAsync(parent);
                    if (Possible(parent) && Ancestor(parent, Valid) is { } container) return await InitialAsync(container);
                    return parent is IInitializedNode { Initializer: { } init } && Assigned(init) ? [init] : [];
                }
                if (node is ConstructorDeclarationNode) return node.Parent is { } parent && Valid(parent) ? [parent] : [];
                if (node.Kind == K.StaticKeyword && node.Parent is ClassStaticBlockDeclarationNode) { node = node.Parent; continue; }
                if (node is VariableDeclarationNode { Initializer: { } initializer } && Assigned(initializer)) return [initializer];
                if (!followed && await checker.GetSymbolAtLocationAsync(node, cancellation) is { } symbol)
                {
                    if ((symbol.Flags & SymbolFlags.Alias) != 0) symbol = await checker.GetAliasedSymbolAsync(symbol, cancellation);
                    if (symbol.ValueDeclaration is { } declaration) { node = declaration; followed = true; continue; }
                }
                break;
            }
            return [];
        }

        internal async ValueTask<SyntaxNode[]> DeclarationsAsync(DocumentPosition position, bool allowSource)
        {
            List<SyntaxNode> declarations = [];
            HashSet<SyntaxNode> seen = [];
            foreach (var (projection, mapped) in service.FromPosition(position, MappingFeature.CallHierarchy))
            {
                if (mapped.Fidelity is not (MappingFidelity.Exact or MappingFidelity.Atom)) continue;
                var node = mapped.Position == 0 ? projection.File : await SyntaxNavigation.GetTouchingPropertyNameAsync(projection.File, mapped.Position, cancellation);
                if (!allowSource && node is SourceFileNode) continue;
                foreach (var declaration in await ResolveAsync(node)) if (seen.Add(declaration)) declarations.Add(declaration);
            }
            return declarations.ToArray();
        }

        private async ValueTask<Utf8String> NameTextAsync(SyntaxNode name, SyntaxNode printNode)
        {
            if (name is IdentifierNode or StringLiteralNode or NoSubstitutionTemplateLiteralNode or NumericLiteralNode) return ReferenceNavigation.Text(name);
            if (name is ComputedPropertyNameNode { Expression: StringLiteralNode or NoSubstitutionTemplateLiteralNode or NumericLiteralNode } computed) return ReferenceNavigation.Text(computed.Expression);
            if (await checker.GetSymbolAtLocationAsync(name, cancellation) is { } symbol
                && await checker.GetSymbolDisplayNameAsync(symbol, cancellation: cancellation) is { IsEmpty: false } text) return text;
            var writer = new EmitTextWriter(inlineDisplay: true);
            new SyntaxPrinter(new() { RemoveComments = true }).Write(printNode, SemanticSyntax.Source(printNode), writer, cancellation: cancellation);
            return writer.Text;
        }

        private async ValueTask<(Utf8String Text, int Start, int End)> NameAsync(SyntaxNode node)
        {
            var file = SemanticSyntax.Source(node)!;
            if (node is SourceFileNode) return (file.FileName, 0, 0);
            if (node is FunctionDeclarationNode or ClassDeclarationNode && node.DeclarationName is null
                && node.ModifierList?.FirstOrDefault(modifier => modifier.Kind == K.DefaultKeyword) is { } modifier)
                return ((Utf8String)"default"u8, new Scanner(file.Source).SkipTriviaAt(modifier.Pos), modifier.End);
            int keyword = new Scanner(file.Source).SkipTriviaAt(node.ModifierList is { Count: > 0 } modifiers ? modifiers[^1].End : node.Pos);
            if (node is ClassStaticBlockDeclarationNode)
            {
                var symbol = await checker.GetSymbolAtLocationAsync(node.Parent!, cancellation);
                var prefix = symbol is null ? default : await checker.GetSymbolDisplayNameAsync(symbol, cancellation: cancellation) + " "u8;
                return (prefix + "static {}"u8, keyword, keyword + 6);
            }
            var name = Assigned(node) ? node.Parent!.DeclarationName : node.DeclarationName;
            if (name is null || name.End == name.Pos)
            {
                if (node is FunctionDeclarationNode or FunctionExpressionNode) return ((Utf8String)"(anonymous)"u8, keyword, keyword + 8);
                if (node is ClassDeclarationNode or ClassExpressionNode) return ((Utf8String)"(anonymous)"u8, keyword, keyword + 5);
                if (name is null) throw new InvalidOperationException("Call hierarchy declaration has no name");
            }
            return (await NameTextAsync(name, node), new Scanner(file.Source).SkipTriviaAt(name.Pos), name.End);
        }

        private static SyntaxNode? AssignedName(SyntaxNode node) => node.Parent switch
        {
            PropertyAssignmentNode property => property.Name,
            BindingElementNode binding => binding.Name,
            BinaryExpressionNode binary when binary.Right == node => binary.Left is IdentifierNode ? binary.Left : SyntaxLanguageService.AccessName(binary.Left),
            VariableDeclarationNode { Name: IdentifierNode variable } => variable,
            _ => null,
        };

        private async ValueTask<Utf8String> ContainerAsync(SyntaxNode node)
        {
            SyntaxNode? name = null;
            if (Assigned(node))
            {
                var parent = node.Parent!;
                if (parent is PropertyDeclarationNode && parent.Parent is ClassDeclarationNode or ClassExpressionNode)
                    name = parent.Parent is ClassExpressionNode ? AssignedName(parent.Parent) : parent.Parent.DeclarationName;
                if (name is null && parent.Parent?.Parent?.Parent is ModuleBlockNode { Parent: ModuleDeclarationNode { Name: IdentifierNode module } }) return module.Text;
            }
            else if (node is GetAccessorDeclarationNode or SetAccessorDeclarationNode or MethodDeclarationNode)
                name = node.Parent is ObjectLiteralExpressionNode obj ? AssignedName(obj) : null;
            if (name is null && node is GetAccessorDeclarationNode or SetAccessorDeclarationNode or MethodDeclarationNode) name = node.Parent?.DeclarationName;
            if (name is not null) return await NameTextAsync(name, name);
            if (node is FunctionDeclarationNode or ClassDeclarationNode or ModuleDeclarationNode
                && node.Parent is ModuleBlockNode { Parent: ModuleDeclarationNode { Name: IdentifierNode moduleName } }) return moduleName.Text;
            return default;
        }

        internal async ValueTask<CallHierarchyItem?> ItemAsync(SyntaxNode node)
        {
            var file = SemanticSyntax.Source(node)!;
            var (text, start, end) = await NameAsync(node);
            var projection = service.ProjectionForFile(program, file);
            var selection = projection.ToRange(start, end, MappingFeature.CallHierarchy);
            if (selection.Fidelity is not (MappingFidelity.Exact or MappingFidelity.Atom)) return null;
            var full = projection.ToRange(new Scanner(file.Source).SkipTriviaAt(node.Pos, stopAtComments: true), node.End, MappingFeature.CallHierarchy);
            var range = full.Fidelity == MappingFidelity.None || projection.IsMapped && !Contains(full.Range, selection.Range) ? selection.Range : full.Range;
            return new(text, SyntaxLanguageService.SymbolKind(node), DocumentUris.FromFileName(projection.OriginalFileName), range, selection.Range, await ContainerAsync(node));
        }

        private static SyntaxNode? Target(SyntaxNode node) => node switch
        {
            TaggedTemplateExpressionNode tag => tag.Tag, JsxOpeningElementNode jsx => jsx.TagName, JsxSelfClosingElementNode jsx => jsx.TagName,
            PropertyAccessExpressionNode or ElementAccessExpressionNode or ClassStaticBlockDeclarationNode => node,
            CallExpressionNode call => call.Expression, NewExpressionNode call => call.Expression, DecoratorNode decorator => decorator.Expression,
            _ => null,
        };

        private static bool IncomingTarget(SyntaxNode node)
        {
            if (node.Parent is PropertyAccessExpressionNode access && access.Name == node
                || node.Parent is ElementAccessExpressionNode element && element.ArgumentExpression == node) return true;
            var target = ReferenceNavigation.SkipOuter(node);
            return target.Parent is { } parent && Target(parent) == target;
        }

        internal async ValueTask<List<CallHierarchyCall>> IncomingAsync(IReadOnlyList<ReferenceData> data)
        {
            List<Site> sites = [];
            foreach (var entry in data.SelectMany(query => query.Groups).SelectMany(group => group.Entries))
            {
                cancellation.ThrowIfCancellationRequested();
                if (entry.Kind != ReferenceEntryKind.Node || entry.Node is not { } node || !IncomingTarget(node)) continue;
                var file = SemanticSyntax.Source(node)!;
                sites.Add(new(Ancestor(node, Valid) ?? file, file, new Scanner(file.Source).SkipTriviaAt(node.Pos), node.End));
            }
            return await ConvertAsync(sites);
        }

        private async ValueTask<List<CallHierarchyCall>> ConvertAsync(List<Site> sites)
        {
            List<CallHierarchyCall> calls = [];
            foreach (var group in sites.GroupBy(site => site.Declaration))
            {
                cancellation.ThrowIfCancellationRequested();
                var ranges = group.Select(site => service.ProjectionForFile(program, site.Source).ToRange(site.Start, site.End, MappingFeature.CallHierarchy))
                    .Where(mapped => mapped.Fidelity != MappingFidelity.None).Select(mapped => mapped.Range)
                    .OrderBy(range => range.Start.Line).ThenBy(range => range.Start.Character).ThenBy(range => range.End.Line).ThenBy(range => range.End.Character).ToArray();
                if (ranges.Length != 0 && await ItemAsync(group.Key) is { } item) calls.Add(new(item, ranges));
            }
            return calls;
        }

        internal async ValueTask<IEnumerable<CallHierarchyCall>> OutgoingAsync(SyntaxNode declaration)
        {
            if ((declaration.Flags & NodeFlags.Ambient) != 0 || declaration is MethodSignatureDeclarationNode) return [];
            List<SyntaxNode?> roots = [];
            switch (declaration)
            {
                case SourceFileNode source: roots.AddRange(source.Statements ?? new([])); break;
                case ModuleDeclarationNode module:
                    if (!SemanticSyntax.HasModifier(module, K.DeclareKeyword) && module.Body is ModuleBlockNode block) roots.AddRange(block.Statements ?? new([]));
                    break;
                case FunctionDeclarationNode or FunctionExpressionNode or ArrowFunctionNode or MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode:
                    if (await ImplementationAsync(declaration) is { } impl)
                    { roots.AddRange(((IFunctionSignature)impl).Parameters ?? new([])); roots.Add(SemanticSyntax.Body(impl)); }
                    break;
                case ClassDeclarationNode or ClassExpressionNode:
                    roots.AddRange(declaration.ModifierList ?? new([]));
                    roots.Add((declaration is ClassDeclarationNode cls ? cls.HeritageClauses : ((ClassExpressionNode)declaration).HeritageClauses)?.FirstOrDefault(clause => clause is HeritageClauseNode { Token: K.ExtendsKeyword }) is HeritageClauseNode heritage
                        && heritage.Types?.FirstOrDefault() is ExpressionWithTypeArgumentsNode type ? type.Expression : null);
                    foreach (var member in DecoratorSyntax.Members(declaration) ?? new([]))
                    {
                        roots.AddRange(member.ModifierList ?? new([]));
                        if (member is PropertyDeclarationNode property) roots.Add(property.Initializer);
                        else if (member is ConstructorDeclarationNode { Body: not null } ctor) { roots.AddRange(ctor.Parameters ?? new([])); roots.Add(ctor.Body); }
                        else if (member is ClassStaticBlockDeclarationNode) roots.Add(member);
                    }
                    break;
                case ClassStaticBlockDeclarationNode staticBlock: roots.Add(staticBlock.Body); break;
            }
            Stack<SyntaxNode> pending = new(roots.OfType<SyntaxNode>().Reverse());
            List<Site> sites = [];
            while (pending.TryPop(out var node))
            {
                cancellation.ThrowIfCancellationRequested();
                if ((node.Flags & NodeFlags.Ambient) != 0) continue;
                if (Valid(node))
                {
                    if (node is ClassDeclarationNode or ClassExpressionNode)
                        Push((DecoratorSyntax.Members(node) ?? new([])).Select(member => (member.DeclarationName as ComputedPropertyNameNode)?.Expression));
                    continue;
                }
                if (node is IdentifierNode or ImportEqualsDeclarationNode or ImportDeclarationNode or ExportDeclarationNode or InterfaceDeclarationNode or TypeAliasDeclarationNode) continue;
                if (Target(node) is { } target)
                {
                    var file = SemanticSyntax.Source(target)!;
                    foreach (var called in await ResolveAsync(target)) sites.Add(new(called, file, new Scanner(file.Source).SkipTriviaAt(target.Pos), target.End));
                }
                switch (node)
                {
                    case ClassStaticBlockDeclarationNode: break;
                    case TypeAssertionNode assertion: Push([assertion.Expression]); break;
                    case AsExpressionNode assertion: Push([assertion.Expression]); break;
                    case SatisfiesExpressionNode assertion: Push([assertion.Expression]); break;
                    case VariableDeclarationNode or ParameterDeclarationNode: Push([node.DeclarationName, ((IInitializedNode)node).Initializer]); break;
                    case CallExpressionNode call: Push([call.Expression, .. call.Arguments ?? new([])]); break;
                    case NewExpressionNode call: Push([call.Expression, .. call.Arguments ?? new([])]); break;
                    case TaggedTemplateExpressionNode tag: Push([tag.Tag, tag.Template]); break;
                    case JsxOpeningElementNode jsx: Push([jsx.TagName, jsx.Attributes]); break;
                    case JsxSelfClosingElementNode jsx: Push([jsx.TagName, jsx.Attributes]); break;
                    case DecoratorNode decorator: Push([decorator.Expression]); break;
                    default: if (!QuerySyntax.PartOfType(node)) Push(Enumerable.Range(0, node.ChildCount).Select(node.GetChild)); break;
                }
            }
            return SortCallHierarchy(await ConvertAsync(sites)).ToArray();
            void Push(IEnumerable<SyntaxNode?> nodes) { foreach (var child in nodes.Reverse()) if (child is not null) pending.Push(child); }
        }
    }
}
