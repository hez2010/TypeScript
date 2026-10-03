using System.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed record WorkspaceSymbol(Utf8String Name, DocumentSymbolKind Kind, Utf8String Uri, DocumentRange Range, Utf8String? ContainerName);

public static partial class SyntaxLanguageService
{
    public static WorkspaceSymbol[] GetWorkspaceSymbols(IEnumerable<CompilerProgram> programs, Utf8String query,
        UserPreferences? preferences = null, PositionEncoding encoding = PositionEncoding.Utf8, CancellationToken cancellation = default)
        => WorkspaceSymbols(WorkspaceSymbolFiles(programs, preferences ?? new(), cancellation), query, encoding, cancellation);

    internal static IEnumerable<(Utf8String Path, ProgramFile File, Utf8String OriginalName)> WorkspaceSymbolFiles(
        IEnumerable<CompilerProgram> programs, UserPreferences preferences, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var files = new Dictionary<Utf8String, (ProgramFile File, Utf8String OriginalName)>();
        foreach (var program in programs)
        {
            bool hasTypeScript = program.SourceFiles.Any(file => !file.Syntax.IsDeclarationFile
                && (file.Syntax.FileName.EndsWith(".ts"u8) || file.Syntax.FileName.EndsWith(".tsx"u8)
                    || file.Syntax.FileName.EndsWith(".mts"u8) || file.Syntax.FileName.EndsWith(".cts"u8)));
            var canonicalNames = program.SourceFiles.SelectMany(file => file.SupplementalSourceFiles.Select(name => (name, file.Syntax.FileName)))
                .ToDictionary(pair => pair.name, pair => pair.FileName);
            foreach (var file in program.SourceFiles)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!hasTypeScript && file.Syntax.IsDeclarationFile || preferences.ExcludeLibrarySymbolsInNavTo == true
                    && (file.Library || file.Syntax.FileName.Contains("/node_modules/"u8, StringComparison.Ordinal))) continue;
                var path = program.UseCaseSensitiveFileNames ? file.Syntax.FileName : file.Syntax.FileName.ToLowerInvariant();
                files[path] = (file, canonicalNames.GetValueOrDefault(file.Syntax.FileName, file.Syntax.FileName));
            }
        }
        return files.Select(pair => (pair.Key, pair.Value.File, pair.Value.OriginalName));
    }

    internal static WorkspaceSymbol[] WorkspaceSymbols(IEnumerable<(Utf8String Path, ProgramFile File, Utf8String OriginalName)> files,
        Utf8String query, PositionEncoding encoding, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        List<(Utf8String Name, Utf8String Folded, SyntaxNode Node, Utf8String Path, ProgramFile File, Utf8String OriginalName, int Score)> declarations = [];
        foreach (var (path, file, originalName) in files)
            foreach (var (name, nodes) in DeclarationMap(file.Binding, cancellation))
            {
                int score = SymbolMatchScore(name, query);
                if (score < 0) continue;
                var folded = LowerSymbolName(name);
                foreach (var node in nodes) declarations.Add((name, folded, node, path, file, originalName, score));
            }
        declarations.Sort((a, b) =>
        {
            int result = a.Score.CompareTo(b.Score);
            if (result == 0) result = Utf8StringComparer.Ordinal.Compare(a.Folded, b.Folded);
            if (result == 0) result = Utf8StringComparer.Ordinal.Compare(a.Name, b.Name);
            if (result == 0) result = Utf8StringComparer.Ordinal.Compare(a.Path, b.Path);
            return result != 0 ? result : a.Node.Pos.CompareTo(b.Node.Pos);
        });
        List<WorkspaceSymbol> symbols = [];
        foreach (var item in declarations.Take(256))
        {
            cancellation.ThrowIfCancellationRequested();
            var name = Name(item.Node)!;
            var projection = new DocumentProjection(item.File.Syntax, item.File.Mapping, encoding);
            var (range, fidelity) = projection.ToRange(SmartIndenter.Start(name, item.File.Syntax), name.End, MappingFeature.DocumentSymbols);
            if (fidelity is not (MappingFidelity.Exact or MappingFidelity.Atom)) continue;
            var container = item.Node.Parent;
            while (container is not null && container.Kind is not (K.SourceFile or K.MethodDeclaration or K.MethodSignature or K.FunctionDeclaration
                or K.FunctionExpression or K.GetAccessor or K.SetAccessor or K.ClassDeclaration or K.InterfaceDeclaration or K.EnumDeclaration or K.ModuleDeclaration))
                container = container.Parent;
            var containerName = container is null ? default : DeclarationText(container);
            symbols.Add(new(item.Name, SymbolKind(item.Node), DocumentUris.FromFileName(item.OriginalName), range, containerName.IsEmpty ? null : containerName));
        }
        return symbols.ToArray();
    }

    private static Dictionary<Utf8String, List<SyntaxNode>> DeclarationMap(BoundSourceFile binding, CancellationToken cancellation)
    {
        Dictionary<Utf8String, List<SyntaxNode>> result = [];
        Stack<(SyntaxNode Node, bool Add)> pending = [];
        void Children(SyntaxNode node) { for (int i = node.ChildCount - 1; i >= 0; i--) pending.Push((node.GetChild(i), false)); }
        void Add(SyntaxNode node)
        {
            var name = DeclarationText(node);
            if (name.IsEmpty) return;
            if (!result.TryGetValue(name, out var list)) result.Add(name, list = []);
            list.Add(node);
        }
        static SyntaxNode? Body(SyntaxNode node) => node switch
        { FunctionDeclarationNode f => f.Body, FunctionExpressionNode f => f.Body, MethodDeclarationNode m => m.Body, _ => null };
        Children(binding.SourceFile);
        while (pending.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            var node = item.Node;
            if (item.Add) { Add(node); continue; }
            switch (node.Kind)
            {
                case K.FunctionDeclaration: case K.FunctionExpression: case K.MethodDeclaration: case K.MethodSignature:
                    var name = DeclarationText(node);
                    if (!name.IsEmpty)
                    {
                        if (result.TryGetValue(name, out var previous) && previous.Count != 0 && previous[^1].Parent == node.Parent
                            && ReferenceEquals(binding.Get(previous[^1])?.Symbol, binding.Get(node)?.Symbol))
                        { if (Body(node) is not null && Body(previous[^1]) is null) previous[^1] = node; }
                        else Add(node);
                    }
                    Children(node); break;
                case K.ClassDeclaration: case K.ClassExpression: case K.InterfaceDeclaration: case K.TypeAliasDeclaration: case K.EnumDeclaration:
                case K.ModuleDeclaration: case K.ImportEqualsDeclaration: case K.ImportClause: case K.NamespaceImport: case K.GetAccessor: case K.SetAccessor: case K.TypeLiteral:
                    Add(node); Children(node); break;
                case K.ImportSpecifier: case K.ExportSpecifier:
                    if (node is ImportSpecifierNode { PropertyName: not null } or ExportSpecifierNode { PropertyName: not null }) Add(node);
                    break;
                case K.Parameter:
                    if (SymbolKind(node) != DocumentSymbolKind.Property) break;
                    goto case K.VariableDeclaration;
                case K.VariableDeclaration: case K.BindingElement:
                    if (node.DeclarationName is { } variable)
                    {
                        if (variable.Kind is K.ObjectBindingPattern or K.ArrayBindingPattern) Children(variable);
                        else
                        {
                            pending.Push((node, true));
                            if (node is IInitializedNode { Initializer: { } initializer }) pending.Push((initializer, false));
                        }
                    }
                    break;
                case K.EnumMember: case K.PropertyDeclaration: case K.PropertySignature: Add(node); break;
                case K.ExportDeclaration:
                    if (((ExportDeclarationNode)node).ExportClause is NamedExportsNode exports) Children(exports);
                    break;
                case K.ImportDeclaration:
                    if (((ImportDeclarationNode)node).ImportClause is ImportClauseNode import)
                    {
                        if (import.Name is { } defaultName) Add(defaultName);
                        if (import.NamedBindings is NamespaceImportNode ns) Add(ns);
                        else if (import.NamedBindings is { } imports) Children(imports);
                    }
                    break;
                case K.BinaryExpression:
                    if (AssignmentKind(node) is AssignmentDeclaration.ExportsProperty or AssignmentDeclaration.ThisProperty or AssignmentDeclaration.Property) Add(node);
                    Children(node); break;
                default: Children(node); break;
            }
        }
        return result;
    }

    // Go uses Unicode simple lowercase mapping, including capital dotted I.
    private static int LowerSymbolPoint(int point) => point == 0x130 ? 'i' : Rune.IsValid(point) ? Rune.ToLowerInvariant(new(point)).Value : point;
    private static Utf8String LowerSymbolName(Utf8String name)
    {
        Utf8StringBuilder result = new();
        for (int position = 0; position < name.Length;)
        {
            int point = Wtf8.Decode(name.Span[position..], out int width); position += width;
            result.AppendCodePoint(LowerSymbolPoint(point));
        }
        return result.ToUtf8String();
    }

    internal static int SymbolMatchScore(Utf8String name, Utf8String pattern)
    {
        int score = 0, position = 0;
        for (int patternPosition = 0; patternPosition < pattern.Length;)
        {
            int point = Wtf8.Decode(pattern.Span[patternPosition..], out int patternWidth); patternPosition += patternWidth;
            bool exact = Rune.IsValid(point) && Rune.IsUpper(new(point));
            while (true)
            {
                if (position == name.Length) return -1;
                int current = Wtf8.Decode(name.Span[position..], out int width); position += width;
                if (exact ? current == point : LowerSymbolPoint(current) == LowerSymbolPoint(point)) break;
                score++;
            }
        }
        return score;
    }
}
