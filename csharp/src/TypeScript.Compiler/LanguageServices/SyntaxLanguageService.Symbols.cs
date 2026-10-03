using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public enum DocumentSymbolKind
{
    File = 1, Module, Namespace, Package, Class, Method, Property, Field, Constructor, Enum, Interface, Function,
    Variable, Constant, String, Number, Boolean, Array, Object, Key, Null, EnumMember, Struct, Event, Operator, TypeParameter,
}

public sealed class DocumentSymbol(Utf8String name, DocumentSymbolKind kind, DocumentRange range, DocumentRange selectionRange, DocumentSymbol[] children)
{
    public Utf8String Name { get; } = name;
    public DocumentSymbolKind Kind { get; } = kind;
    public DocumentRange Range { get; } = range;
    public DocumentRange SelectionRange { get; } = selectionRange;
    public DocumentSymbol[] Children { get; internal set; } = children;
}

public sealed partial class LanguageServiceDocument
{
    public async ValueTask<DocumentSymbol[]> GetDocumentSymbolsAsync(CancellationToken cancellation = default)
    {
        List<DocumentSymbol> result = [];
        HashSet<(Utf8String, DocumentSymbolKind, DocumentRange)> seen = [];
        foreach (var projection in projections)
            foreach (var symbol in await SyntaxLanguageService.GetDocumentSymbolsAsync(projection, cancellation).ConfigureAwait(false))
                if (seen.Add((symbol.Name, symbol.Kind, symbol.Range))) result.Add(symbol);
        return result.ToArray();
    }
}

public static partial class SyntaxLanguageService
{
    public static ValueTask<DocumentSymbol[]> GetDocumentSymbolsAsync(SourceFileNode file, PositionEncoding encoding = PositionEncoding.Utf8,
        CancellationToken cancellation = default) => GetDocumentSymbolsAsync(new DocumentProjection(file, null, encoding), cancellation);

    internal static async ValueTask<DocumentSymbol[]> GetDocumentSymbolsAsync(DocumentProjection projection, CancellationToken cancellation)
    {
        var collector = new DocumentSymbolCollector(projection, cancellation);
        return await collector.MergeAsync(await collector.ChildrenAsync(projection.File).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private sealed class DocumentSymbolCollector(DocumentProjection projection, CancellationToken cancellation)
    {
        private readonly SourceFileNode file = projection.File;

        internal async ValueTask<DocumentSymbol[]> ChildrenAsync(SyntaxNode? node)
        {
            List<DocumentSymbol> result = [];
            HashSet<Utf8String> targets = [];
            if (node is not null)
                for (int i = 0; i < node.ChildCount; i++)
                    await VisitAsync(node.GetChild(i), result, targets).ConfigureAwait(false);
            return result.ToArray();
        }

        private async ValueTask<DocumentSymbol[]> NodeAsync(SyntaxNode? node, HashSet<Utf8String> targets)
        {
            List<DocumentSymbol> result = [];
            if (node is not null) await VisitAsync(node, result, targets).ConfigureAwait(false);
            return result.ToArray();
        }

        private async ValueTask VisitAsync(SyntaxNode node, List<DocumentSymbol> symbols, HashSet<Utf8String> targets)
        {
            await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
            cancellation.ThrowIfCancellationRequested();
            if ((node.Flags & NodeFlags.Reparsed) == 0)
                foreach (var doc in await file.GetDocumentationAsync(node, cancellation).ConfigureAwait(false))
                    if (doc.Tags is { } tags)
                        foreach (var tag in tags)
                            if (tag is JSDocTypedefTagNode or JSDocCallbackTagNode) Add(symbols, tag);

            switch (node.Kind)
            {
                case K.ClassDeclaration: case K.ClassExpression: case K.InterfaceDeclaration: case K.EnumDeclaration:
                    if (SemanticSyntax.ClassLike(node) && DeclarationText(node) is { IsEmpty: false } className) targets.Add(className);
                    Add(symbols, node, children: await ChildrenAsync(node).ConfigureAwait(false));
                    return;
                case K.ModuleDeclaration:
                    Add(symbols, node, children: await ChildrenAsync(InteriorModule((ModuleDeclarationNode)node)).ConfigureAwait(false));
                    return;
                case K.Constructor:
                    var constructor = (ConstructorDeclarationNode)node;
                    Add(symbols, node, children: await ChildrenAsync(constructor.Body).ConfigureAwait(false));
                    if (constructor.Parameters is { } parameters)
                        foreach (var parameter in parameters)
                            if (ParameterProperty(parameter)) Add(symbols, parameter);
                    return;
                case K.FunctionDeclaration: case K.FunctionExpression: case K.ArrowFunction: case K.MethodDeclaration: case K.GetAccessor: case K.SetAccessor:
                    if (DeclarationText(node) is { IsEmpty: false } functionName) targets.Add(functionName);
                    Add(symbols, node, children: await ChildrenAsync(SemanticSyntax.Body(node)).ConfigureAwait(false));
                    return;
                case K.VariableDeclaration: case K.BindingElement: case K.PropertyAssignment: case K.PropertyDeclaration:
                    if (node.DeclarationName is BindingPatternNode pattern) await VisitAsync(pattern, symbols, targets).ConfigureAwait(false);
                    else if (node.DeclarationName is not null)
                        Add(symbols, node, children: await ChildrenAsync((node as IInitializedNode)?.Initializer).ConfigureAwait(false));
                    return;
                case K.SpreadAssignment:
                    Add(symbols, node, ((SpreadAssignmentNode)node).Expression);
                    return;
                case K.MethodSignature: case K.PropertySignature: case K.CallSignature: case K.ConstructSignature: case K.IndexSignature:
                case K.EnumMember: case K.ShorthandPropertyAssignment: case K.TypeAliasDeclaration: case K.ImportEqualsDeclaration: case K.ExportSpecifier:
                    Add(symbols, node);
                    return;
                case K.ImportClause:
                    var import = (ImportClauseNode)node;
                    if (import.Name is { } name) Add(symbols, name, name);
                    if (import.NamedBindings is NamespaceImportNode ns) Add(symbols, ns);
                    else if (import.NamedBindings is NamedImportsNode { Elements: { } elements })
                        foreach (var element in elements) Add(symbols, element);
                    return;
                case K.BinaryExpression: case K.CallExpression:
                    if (Expando(node) is { } expando)
                    {
                        var (target, targetFunction, definition, propertyName) = expando;
                        if (AccessName(targetFunction) is { } accessName && LiteralText(accessName) == "prototype"u8)
                        {
                            targetFunction = AccessBase(targetFunction);
                            if (targetFunction is IdentifierNode id) targets.Add(id.Text);
                        }
                        if (targetFunction is IdentifierNode owner && targets.Contains(owner.Text))
                        {
                            List<DocumentSymbol> members = [];
                            Add(members, target, propertyName, await NodeAsync(definition, []).ConfigureAwait(false));
                            Add(symbols, node, targetFunction, members.ToArray());
                            return;
                        }
                    }
                    break;
                case K.ExportAssignment when node is ExportAssignmentNode { IsExportEquals: true } export:
                    Add(symbols, node, children: await NodeAsync(export.Expression, targets).ConfigureAwait(false));
                    return;
            }
            for (int i = 0; i < node.ChildCount; i++) await VisitAsync(node.GetChild(i), symbols, targets).ConfigureAwait(false);
        }

        private void Add(List<DocumentSymbol> symbols, SyntaxNode node, SyntaxNode? name = null, DocumentSymbol[]? children = null)
        {
            if ((node.Flags & NodeFlags.Reparsed) != 0) return;
            int start = SmartIndenter.Start(node, file);
            name ??= Name(node);
            Utf8String text;
            int nameStart, nameEnd;
            if (node is ModuleDeclarationNode module && module.Name is not StringLiteralNode && module.Keyword != K.GlobalKeyword)
            {
                var pieces = new List<Utf8String> { LiteralText(module.Name) };
                while (module.Body is ModuleDeclarationNode inner) { module = inner; pieces.Add(LiteralText(module.Name)); }
                text = Utf8String.Join("."u8, pieces);
                nameStart = SmartIndenter.Start(name!, file); nameEnd = module.Name!.End;
            }
            else if (node is ExportAssignmentNode { IsExportEquals: true })
            {
                text = "export="u8;
                bool missing = name is null || name.Pos == name.End && name.Kind != K.EndOfFile;
                nameStart = missing ? start : SmartIndenter.Start(name!, file); nameEnd = missing ? node.End : name!.End;
            }
            else if (name is not null)
            {
                text = NameText(name, file); nameStart = Math.Max(SmartIndenter.Start(name, file), start); nameEnd = Math.Max(name.End, start);
            }
            else { text = UnnamedLabel(node, file); nameStart = nameEnd = start; }
            if (text.IsEmpty) return;
            text = TruncateSymbolText(text);
            var (selectionRange, selectionFidelity) = projection.ToRange(nameStart, nameEnd, MappingFeature.DocumentSymbols);
            if (selectionFidelity is not (MappingFidelity.Exact or MappingFidelity.Atom)) return;
            var (range, fidelity) = projection.ToRange(start, node.End, MappingFeature.DocumentSymbols);
            symbols.Add(new(text, SymbolKind(node), fidelity == MappingFidelity.None ? selectionRange : range, selectionRange, children ?? []));
        }

        internal async ValueTask<DocumentSymbol[]> MergeAsync(DocumentSymbol[] symbols)
        {
            await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
            cancellation.ThrowIfCancellationRequested();
            Dictionary<Utf8String, List<int>> targets = [];
            Dictionary<Utf8String, int> namespaces = [];
            for (int i = 0; i < symbols.Length; i++)
            {
                var symbol = symbols[i];
                if (Anonymous(symbol.Name)) continue;
                if (symbol.Kind is DocumentSymbolKind.Class or DocumentSymbolKind.Function or DocumentSymbolKind.Variable)
                {
                    if (!targets.TryGetValue(symbol.Name, out var indices)) targets[symbol.Name] = indices = [];
                    indices.Add(i);
                }
                if (symbol.Kind == DocumentSymbolKind.Namespace) namespaces.TryAdd(symbol.Name, i);
            }
            HashSet<int> merged = [];
            for (int i = 0; i < symbols.Length; i++)
            {
                var symbol = symbols[i];
                if (symbol.Children.Length != 0) symbol.Children = await MergeAsync(symbol.Children).ConfigureAwait(false);
                if (Anonymous(symbol.Name)) continue;
                if (symbol.Kind == DocumentSymbolKind.Property && targets.TryGetValue(symbol.Name, out var indices))
                    for (int j = indices.Count - 1; j >= 0; j--)
                    { await MergeChildrenAsync(symbols[indices[j]], symbol).ConfigureAwait(false); merged.Add(i); }
                if (symbol.Kind == DocumentSymbolKind.Namespace && namespaces.TryGetValue(symbol.Name, out int index) && index != i)
                { await MergeChildrenAsync(symbols[index], symbol).ConfigureAwait(false); merged.Add(i); }
            }
            return symbols.Where((_, i) => !merged.Contains(i)).ToArray();
        }

        private async ValueTask MergeChildrenAsync(DocumentSymbol target, DocumentSymbol source)
        {
            target.Children = (await MergeAsync([.. target.Children, .. source.Children]).ConfigureAwait(false))
                .OrderBy(symbol => symbol.Range.Start.Line).ThenBy(symbol => symbol.Range.Start.Character)
                .ThenBy(symbol => symbol.Range.End.Line).ThenBy(symbol => symbol.Range.End.Character).ToArray();
        }
    }
}
