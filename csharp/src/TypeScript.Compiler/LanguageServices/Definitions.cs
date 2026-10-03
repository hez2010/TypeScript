using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Projects;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed record DefinitionLocation(Utf8String Uri, DocumentRange Range, DocumentRange SelectionRange, DocumentRange? OriginRange);

public sealed partial class LanguageServiceDocument
{
    public ValueTask<DefinitionLocation[]> GetDefinitionsAsync(ProjectSnapshot project, DocumentPosition position,
        bool typeDefinition = false, CancellationToken cancellation = default) => DefinitionsAsync(project, position, typeDefinition, false, cancellation);

    public ValueTask<DefinitionLocation[]> GetSourceDefinitionsAsync(ProjectSnapshot project, DocumentPosition position,
        CancellationToken cancellation = default) => DefinitionsAsync(project, position, false, true, cancellation);

    private async ValueTask<DefinitionLocation[]> DefinitionsAsync(ProjectSnapshot project, DocumentPosition position,
        bool typeDefinition, bool sourceDefinition, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var program = project.Program ?? throw new ArgumentException("Project has no program", nameof(project));
        var sourceMaps = new DeclarationMaps(program, cancellation);
        using var request = new ProjectRequest(cancellation);
        List<DefinitionLocation> results = [];
        var feature = typeDefinition ? MappingFeature.TypeDefinition : MappingFeature.Definition;
        foreach (var (projection, mapped) in FromPosition(position, feature))
        {
            cancellation.ThrowIfCancellationRequested();
            if (mapped.Fidelity is not (MappingFidelity.Exact or MappingFidelity.Atom)) continue;
            var file = projection.File;
            var node = await SyntaxNavigation.GetTouchingPropertyNameAsync(file, mapped.Position, cancellation);
            var sourceResult = sourceDefinition ? await new SourceDefinitionQuery(project, request, file, sourceMaps, cancellation).GetAsync(node, mapped.Position) : null;
            if (node is SourceFileNode && sourceResult is null) continue;
            var origin = projection.ToRange(sourceResult?.Start ?? await SyntaxNavigation.GetStartAsync(node, file, cancellation: cancellation), sourceResult?.End ?? node.End).Range;
            var reference = typeDefinition || sourceResult is not null ? null : await DefinitionReferenceAsync(program, file, mapped.Position, cancellation);
            if (reference is not null && program.GetFile(reference.Value) is not null) { AddReference(reference.Value); continue; }
            IReadOnlyList<SyntaxNode>? declarations = sourceResult?.Declarations;
            if (declarations is null)
            {
                using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request, file).ConfigureAwait(false);
                var checker = lease.Checker;
                if (typeDefinition) declarations = await checker.GetTypeDefinitionDeclarationsAsync(node, cancellation);
                else
                {
                    if (node.Kind == K.OverrideKeyword && await checker.GetOverriddenMemberAsync(node, cancellation) is { } overridden) declarations = overridden.Declarations;
                    if (node is IdentifierNode label && node.Parent is BreakStatementNode or ContinueStatementNode)
                        for (var parent = node.Parent.Parent; parent is not null; parent = parent.Parent)
                            if (parent is LabeledStatementNode { Label: IdentifierNode target } && label.Text == target.Text) { declarations = [target]; break; }
                    if (node.Kind == K.CaseKeyword || node.Kind == K.DefaultKeyword && node.Parent?.Kind == K.DefaultClause)
                    {
                        var statement = Ancestor(node.Parent, static n => n is SwitchStatementNode);
                        if (statement is not null)
                        {
                            int start = await SyntaxNavigation.GetStartAsync(statement, file, cancellation: cancellation);
                            var location = MapLocation(file, start, start + 6, feature);
                            var range = location.Fidelity == MappingFidelity.None ? default : location.Range;
                            results.Add(new(location.Uri, range, range, null)); continue;
                        }
                    }
                    if (node.Kind is K.ReturnKeyword or K.YieldKeyword or K.AwaitKeyword
                        && Ancestor(node, SemanticSyntax.FunctionDeclarationLike) is { } function) declarations = [function];
                    if (declarations is null)
                    {
                        declarations = await checker.GetDefinitionDeclarationsAsync(node, cancellation);
                        if (await CalledDeclarationAsync(checker, node, cancellation) is { } called
                            && !(node.Parent is JsxOpeningElementNode or JsxSelfClosingElementNode
                                && called is ConstructorDeclarationNode or ConstructorTypeNode or CallSignatureDeclarationNode or ConstructSignatureDeclarationNode))
                        {
                            var symbol = await checker.GetSymbolAtLocationAsync(DeclarationNameForKeyword(node), cancellation);
                            bool matches = symbol is not null && (await checker.GetRootSymbolsAsync(symbol, cancellation)).Any(root => SymbolMatchesSignature(root, called));
                            declarations = matches ? called is ConstructorDeclarationNode
                                ? declarations.Where(declaration => declaration != called && declaration is ClassDeclarationNode or ClassExpressionNode).ToArray() : []
                                : declarations.Where(declaration => declaration != called).ToArray();
                            declarations = [.. declarations, called];
                        }
                    }
                }
            }
            if (reference is not null) AddReference(reference.Value);
            HashSet<(SourceFileNode, int, int)> seen = [];
            foreach (var declaration in declarations)
            {
                cancellation.ThrowIfCancellationRequested();
                var source = SemanticSyntax.Source(declaration);
                if (source is null) continue;
                var name = SyntaxLanguageService.Name(declaration) ?? declaration;
                int start = name is EmptyStatementNode ? name.Pos : await SyntaxNavigation.GetStartAsync(name, source, cancellation: cancellation);
                int end = name is EmptyStatementNode ? name.Pos : name.End;
                if (!seen.Add((source, start, end))) continue;
                var context = DefinitionContext(declaration) ?? declaration;
                int contextStart = await SyntaxNavigation.GetStartAsync(context, source, cancellation: cancellation), contextEnd = context.End;
                if (context is StringLiteralNode or NoSubstitutionTemplateLiteralNode && contextEnd - contextStart > 2) { contextStart++; contextEnd--; }
                contextStart = Math.Min(start, contextStart); contextEnd = Math.Max(end, contextEnd);
                var selection = MapLocation(source, start, end, feature);
                if (selection.Fidelity is not (MappingFidelity.Exact or MappingFidelity.Atom)) continue;
                var full = MapLocation(source, contextStart, contextEnd, null);
                if (full.Fidelity == MappingFidelity.None || full.Uri != selection.Uri || !Contains(full.Range, selection.Range)) full = selection;
                results.Add(new(full.Uri, full.Range, selection.Range, origin));
            }

            void AddReference(Utf8String name) => results.Add(new(DocumentUris.FromFileName(name), default, default, origin));
            (Utf8String Uri, DocumentRange Range, MappingFidelity Fidelity) MapLocation(SourceFileNode source, int start, int end, MappingFeature? feature)
            {
                var target = projections.FirstOrDefault(p => p.File == source) ?? relatedProjections?.GetValueOrDefault(source);
                var programFile = program.GetFile(source.FileName);
                var originalName = target?.OriginalFileName ?? source.FileName;
                if (target is null && programFile?.Mapping is not null)
                    originalName = program.SourceFiles.FirstOrDefault(f => f.SupplementalSourceFiles.Contains(source.FileName))?.Syntax.FileName ?? originalName;
                target ??= new(source, programFile?.Mapping, projection.Encoding, originalName);
                if (!target.IsMapped && sourceMaps.MapRange(source.FileName, start, end, projection.Encoding) is { } sourceRange)
                    return (DocumentUris.FromFileName(sourceRange.FileName), sourceRange.Range, MappingFidelity.Exact);
                var range = target.ToRange(start, end, feature);
                return (DocumentUris.FromFileName(originalName), range.Range, range.Fidelity);
            }
        }
        return results.DistinctBy(location => (location.Uri, location.SelectionRange)).ToArray();
    }

    internal static SyntaxNode DeclarationNameForKeyword(SyntaxNode node)
    {
        if (node.Kind is >= K.FirstKeyword and <= K.LastKeyword)
        {
            if (node.Parent is VariableDeclarationListNode list && list.Declarations?.FirstOrDefault()?.DeclarationName is { } first) return first;
            if (QuerySyntax.Declaration(node.Parent) && node.Parent?.DeclarationName is { } name && node.Pos < name.Pos) return name;
        }
        return node;
    }
    private static async ValueTask<SyntaxNode?> CalledDeclarationAsync(Checker checker, SyntaxNode node, CancellationToken cancellation)
    {
        var target = node;
        while (target.Parent is PropertyAccessExpressionNode access && access.Name == target) target = target.Parent;
        var invoked = target.Parent switch
        {
            CallExpressionNode call => call.Expression, NewExpressionNode call => call.Expression, TaggedTemplateExpressionNode tag => tag.Tag,
            JsxOpeningElementNode jsx => jsx.TagName, JsxSelfClosingElementNode jsx => jsx.TagName, DecoratorNode decorator => decorator.Expression, _ => null,
        };
        return invoked == target && target.Parent is { } callLike
            && (await checker.GetResolvedSignatureAsync(callLike, cancellation)).Declaration is IFunctionSignature and SyntaxNode declaration
            && declaration is not FunctionTypeNode ? declaration : null;
    }
    private static bool SymbolMatchesSignature(Symbol symbol, SyntaxNode called) => symbol == called.BindingSymbol || called.BindingSymbol?.Parent == symbol
        || called.Parent is { } parent && (parent is BinaryExpressionNode binary && binary.OperatorToken?.Kind is >= K.FirstAssignment and <= K.LastAssignment
            || parent is not (CallExpressionNode or NewExpressionNode or TaggedTemplateExpressionNode or JsxOpeningElementNode or JsxSelfClosingElementNode or DecoratorNode)
                && parent.BindingSymbol == symbol);
    private static SyntaxNode? Ancestor(SyntaxNode? node, Func<SyntaxNode, bool> match)
    { for (; node is not null; node = node.Parent) if (match(node)) return node; return null; }
    private static bool Contains(DocumentRange outer, DocumentRange inner)
    {
        static bool Before(DocumentPosition a, DocumentPosition b) => a.Line < b.Line || a.Line == b.Line && a.Character <= b.Character;
        return Before(outer.Start, inner.Start) && Before(inner.End, outer.End);
    }
    internal static SyntaxNode? DefinitionContext(SyntaxNode? node)
    {
        while (node is not null)
        {
            switch (node)
            {
                case VariableDeclarationNode when node.Parent is VariableDeclarationListNode { Declarations.Count: 1 }:
                    if (node.Parent.Parent is VariableStatementNode) return node.Parent.Parent;
                    if (node.Parent.Parent is ForInOrOfStatementNode) return null;
                    return node.Parent;
                case BindingElementNode: node = node.Parent?.Parent; continue;
                case ImportSpecifierNode: return node.Parent?.Parent?.Parent;
                case ExportSpecifierNode or NamespaceImportNode: return node.Parent?.Parent;
                case ImportClauseNode or NamespaceExportNode: return node.Parent;
                case BinaryExpressionNode: return node.Parent is ExpressionStatementNode ? node.Parent : node;
                case ForInOrOfStatementNode or SwitchStatementNode: return null;
                case PropertyAssignmentNode or ShorthandPropertyAssignmentNode:
                    var outer = node.Parent;
                    while (outer?.Parent is ArrayLiteralExpressionNode or ObjectLiteralExpressionNode or PropertyAssignmentNode or SpreadAssignmentNode or SpreadElementNode) outer = outer.Parent;
                    if (outer?.Parent is BinaryExpressionNode assignment && assignment.Left == outer
                        || outer?.Parent is ForInOrOfStatementNode) { node = outer.Parent; continue; }
                    return node;
                default: return node;
            }
        }
        return null;
    }

    private static async ValueTask<Utf8String?> DefinitionReferenceAsync(CompilerProgram program, SourceFileNode file, int position, CancellationToken cancellation)
    {
        foreach (var (references, kind) in new[] { (file.ReferencedFiles, FileIncludeKind.PathReference), (file.TypeReferenceDirectives, FileIncludeKind.TypeReference), (file.LibReferenceDirectives, FileIncludeKind.Library) })
            foreach (var reference in references)
                if (reference.Pos <= position && position <= reference.End)
                    return program.IncludeReasons.FirstOrDefault(pair => pair.Value.Any(reason => reason.Kind == kind && reason.ContainingFile == file.FileName && reason.Position == reference.Pos)).Key is { IsEmpty: false } name ? name : null;
        if (file.Imports.Count == 0 && file.ModuleAugmentations.Count == 0) return null;
        var node = await SyntaxNavigation.GetTouchingTokenAsync(file, position, cancellation);
        bool moduleSpecifier = node.Parent is ExternalModuleReferenceNode or ImportDeclarationNode
            || node.Parent is CallExpressionNode { Arguments.Count: > 0 } call && call.Arguments[0] == node
                && (SemanticSyntax.RequireCall(call) || call.Expression?.Kind == K.ImportKeyword);
        if (!moduleSpecifier) return null;
        var text = node switch { StringLiteralNode str => str.Text, NoSubstitutionTemplateLiteralNode str => str.Text, _ => default };
        if (!(text == "."u8 || text == ".."u8 || text.StartsWith("./"u8) || text.StartsWith("../"u8)
            || text.StartsWith(".\\"u8) || text.StartsWith("..\\"u8) || CompilerPath.EncodedRootLength(text) > 0)) return null;
        var resolution = program.GetFile(file.FileName)!.Resolutions.FirstOrDefault(reference => !reference.TypeReference && reference.Node == node)?.Resolution;
        return resolution is null ? null : resolution.FileName.IsEmpty ? CompilerPath.Resolve(CompilerPath.DirectoryName(file.FileName), text) : resolution.FileName;
    }
}
