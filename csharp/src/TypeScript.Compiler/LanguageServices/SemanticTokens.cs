using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compiler.LanguageServices;

public sealed record SemanticTokenLegend(IReadOnlyList<Utf8String> TokenTypes, IReadOnlyList<Utf8String> TokenModifiers);

public sealed partial class LanguageServiceDocument
{
    public async ValueTask<uint[]?> GetSemanticTokensAsync(ProjectSnapshot project, SemanticTokenLegend capabilities,
        DocumentRange? range = null, CancellationToken cancellation = default)
    {
        var program = project.Program ?? throw new ArgumentException("Project has no program", nameof(project));
        using var request = new ProjectRequest(cancellation);
        List<SemanticTokens.Token> tokens = [];
        var seen = new HashSet<(SyntaxNode, SourceFileNode)>();
        foreach (var projection in projections)
        {
            cancellation.ThrowIfCancellationRequested();
            using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request, projection.File).ConfigureAwait(false);
            var spans = range is { } selection ? projection.FromRange(selection, MappingFeature.SemanticTokens)
                : [new MappedSpan(projection.File.Pos, projection.File.End, MappingFidelity.Exact)];
            foreach (var span in spans)
                foreach (var token in await SemanticTokens.CollectAsync(lease.Checker, program, projection, span, cancellation).ConfigureAwait(false))
                    if (range is null || seen.Add((token.Node, token.Projection.File))) tokens.Add(token);
        }
        return tokens.Count == 0 ? null : SemanticTokens.Encode(tokens, capabilities, program.UseCaseSensitiveFileNames);
    }
}

public static class SemanticTokens
{
    public static SemanticTokenLegend SupportedLegend { get; } = new(Array.AsReadOnly<Utf8String>(
        ["namespace"u8, "class"u8, "enum"u8, "interface"u8, "struct"u8, "typeParameter"u8, "type"u8, "parameter"u8, "variable"u8,
        "property"u8, "enumMember"u8, "decorator"u8, "event"u8, "function"u8, "method"u8, "macro"u8, "label"u8, "comment"u8,
        "string"u8, "keyword"u8, "number"u8, "regexp"u8, "operator"u8]), Array.AsReadOnly<Utf8String>(
        ["declaration"u8, "definition"u8, "readonly"u8, "static"u8, "deprecated"u8, "abstract"u8, "async"u8, "modification"u8,
        "documentation"u8, "defaultLibrary"u8, "local"u8]));

    public static SemanticTokenLegend Negotiate(SemanticTokenLegend capabilities) => new(
        SupportedLegend.TokenTypes.Where(capabilities.TokenTypes.Contains).ToArray(),
        SupportedLegend.TokenModifiers.Where(capabilities.TokenModifiers.Contains).ToArray());

    internal enum TokenType { Namespace, Class, Enum, Interface, Struct, TypeParameter, Type, Parameter, Variable, Property,
        EnumMember, Decorator, Event, Function, Method, Macro, Label, Comment, String, Keyword, Number, Regexp, Operator }
    [Flags]
    internal enum TokenModifier { Declaration = 1, Definition = 2, Readonly = 4, Static = 8, Deprecated = 16, Abstract = 32,
        Async = 64, Modification = 128, Documentation = 256, DefaultLibrary = 512, Local = 1024 }
    internal sealed record Token(SyntaxNode Node, DocumentProjection Projection, DocumentRange Range, MappingFidelity Fidelity,
        TokenType Type, TokenModifier Modifiers);

    internal static async ValueTask<List<Token>> CollectAsync(Checker checker, CompilerProgram program,
        DocumentProjection projection, MappedSpan span, CancellationToken cancellation)
    {
        var file = projection.File;
        List<Token> tokens = [];
        Stack<(SyntaxNode Node, bool InJsx)> stack = new(); stack.Push((file, false));
        while (stack.TryPop(out var entry))
        {
            cancellation.ThrowIfCancellationRequested();
            var (node, inJsx) = entry;
            if ((node.Flags & NodeFlags.Reparsed) != 0 || node.Pos >= span.End || node.End <= span.Start) continue;
            if (node is JsxElementNode or JsxSelfClosingElementNode) inJsx = true;
            else if (node is JsxExpressionNode) inJsx = false;
            if (node is IdentifierNode { Text.IsEmpty: false } name && !inJsx
                && name.Parent is not (ImportClauseNode or ImportSpecifierNode or NamespaceImportNode) && name.Text != "Infinity"u8 && name.Text != "NaN"u8)
            {
                var symbol = await checker.GetSymbolAtLocationAsync(node, cancellation).ConfigureAwait(false);
                if (symbol is not null)
                {
                    if ((symbol.Flags & SymbolFlags.Alias) != 0) symbol = await checker.GetAliasedSymbolAsync(symbol, cancellation).ConfigureAwait(false);
                    if (Classify(symbol, HasTypeMeaning(node)) is { } type)
                    {
                        TokenModifier modifiers = 0;
                        if (node.Parent is { } parent && (parent is BindingElementNode || DeclarationType(parent.Kind) == type) && SemanticSyntax.Name(parent) == node)
                            modifiers |= TokenModifier.Declaration;
                        if (type == TokenType.Parameter && RightSide(node)) type = TokenType.Property;
                        type = await ReclassifyAsync(checker, node, type, cancellation).ConfigureAwait(false);
                        if (symbol.ValueDeclaration is { } declaration)
                        {
                            var root = SemanticSyntax.RootDeclaration(declaration);
                            bool HasModifier(K kind) => SemanticSyntax.HasModifier(root, kind)
                                || root is VariableDeclarationNode && (root.Parent is { } list && SemanticSyntax.HasModifier(list, kind)
                                    || root.Parent?.Parent is VariableStatementNode statement && SemanticSyntax.HasModifier(statement, kind));
                            var flags = root.Flags;
                            if (root is VariableDeclarationNode && root.Parent is { } declarations)
                            {
                                flags |= declarations.Flags;
                                if (declarations.Parent is VariableStatementNode statement) flags |= statement.Flags;
                            }
                            if (HasModifier(K.StaticKeyword)) modifiers |= TokenModifier.Static;
                            if (HasModifier(K.AsyncKeyword)) modifiers |= TokenModifier.Async;
                            if (type is not (TokenType.Class or TokenType.Interface) && (HasModifier(K.ReadonlyKeyword)
                                || (flags & NodeFlags.Const) != 0 || (symbol.Flags & SymbolFlags.EnumMember) != 0)) modifiers |= TokenModifier.Readonly;
                            if (type is TokenType.Variable or TokenType.Function && IsLocalDeclaration(declaration, file)) modifiers |= TokenModifier.Local;
                            if (IsLibrary(declaration)) modifiers |= TokenModifier.DefaultLibrary;
                        }
                        else if (symbol.Declarations.Any(IsLibrary)) modifiers |= TokenModifier.DefaultLibrary;
                        int start = await SyntaxNavigation.GetStartAsync(node, file, cancellation: cancellation).ConfigureAwait(false);
                        var mapped = projection.ToRange(start, node.End, MappingFeature.SemanticTokens);
                        tokens.Add(new(node, projection, mapped.Range, mapped.Fidelity, type, modifiers));
                    }
                }
            }
            for (int i = node.ChildCount - 1; i >= 0; i--) stack.Push((node.GetChild(i), inJsx));
        }
        return tokens;
        bool IsLibrary(SyntaxNode declaration) => SemanticSyntax.Source(declaration) is { } source && program.GetFile(source.FileName)?.Library == true;
    }

    private static TokenType? Classify(Symbol symbol, bool typeMeaning)
    {
        var flags = symbol.Flags;
        if ((flags & SymbolFlags.Class) != 0) return TokenType.Class;
        if ((flags & SymbolFlags.Enum) != 0) return TokenType.Enum;
        if ((flags & SymbolFlags.TypeAlias) != 0) return TokenType.Type;
        if ((flags & SymbolFlags.Interface) != 0 && typeMeaning) return TokenType.Interface;
        if ((flags & SymbolFlags.TypeParameter) != 0) return TokenType.TypeParameter;
        var declaration = symbol.ValueDeclaration ?? symbol.Declarations.FirstOrDefault();
        return declaration is null ? null : DeclarationType(BindingDeclaration(declaration).Kind);
    }

    private static TokenType? DeclarationType(K kind) => kind switch
    {
        K.VariableDeclaration => TokenType.Variable, K.Parameter => TokenType.Parameter,
        K.PropertyDeclaration or K.PropertySignature or K.PropertyAssignment or K.ShorthandPropertyAssignment or K.GetAccessor or K.SetAccessor => TokenType.Property,
        K.ModuleDeclaration => TokenType.Namespace, K.EnumDeclaration => TokenType.Enum, K.EnumMember => TokenType.EnumMember,
        K.ClassDeclaration or K.ClassExpression => TokenType.Class, K.MethodDeclaration or K.MethodSignature => TokenType.Method,
        K.FunctionDeclaration or K.FunctionExpression => TokenType.Function, K.InterfaceDeclaration => TokenType.Interface,
        K.TypeAliasDeclaration => TokenType.Type, K.TypeParameter => TokenType.TypeParameter, _ => null,
    };

    private static async ValueTask<TokenType> ReclassifyAsync(Checker checker, SyntaxNode node, TokenType type, CancellationToken cancellation)
    {
        if (type is not (TokenType.Variable or TokenType.Property or TokenType.Parameter)) return type;
        var value = await checker.GetTypeAtLocationAsync(node, cancellation).ConfigureAwait(false);
        async ValueTask<bool> HasSignatures(bool construct)
        {
            if ((await checker.SignaturesAsync(value, construct, cancellation).ConfigureAwait(false)).Count != 0) return true;
            if (value is UnionType union)
                foreach (var child in union.Types)
                    if ((await checker.SignaturesAsync(child, construct, cancellation).ConfigureAwait(false)).Count != 0) return true;
            return false;
        }
        if (type != TokenType.Parameter && await HasSignatures(true).ConfigureAwait(false)) return TokenType.Class;
        if (await HasSignatures(false).ConfigureAwait(false))
        {
            static bool HasProperties(Type value) => value is ObjectType { Properties.Count: > 0 };
            bool properties = HasProperties(value) || value is UnionType union && union.Types.Any(HasProperties);
            while (RightSide(node)) node = node.Parent!;
            if (!properties || node.Parent is CallExpressionNode call && call.Expression == node)
                return type == TokenType.Property ? TokenType.Method : TokenType.Function;
        }
        return type;
    }

    private static bool RightSide(SyntaxNode node) => node.Parent is QualifiedNameNode qualified && qualified.Right == node
        || node.Parent is PropertyAccessExpressionNode access && access.Name == node;
    private static SyntaxNode BindingDeclaration(SyntaxNode node)
    {
        while (node is BindingElementNode && node.Parent is BindingPatternNode && node.Parent.Parent is { } parent) node = parent;
        return node;
    }
    private static bool IsLocalDeclaration(SyntaxNode declaration, SourceFileNode file)
    {
        declaration = BindingDeclaration(declaration);
        if (SemanticSyntax.Source(declaration) != file) return false;
        return declaration is VariableDeclarationNode && (declaration.Parent is CatchClauseNode
            || declaration.Parent is VariableDeclarationListNode { Parent: { } statement } && (statement.Parent is not SourceFileNode || statement is CatchClauseNode))
            || declaration is FunctionDeclarationNode { Parent: not null and not SourceFileNode };
    }

    // Classification only uses the type bit of getMeaningFromLocation; identifiers are not adjusted from keyword positions.
    private static bool HasTypeMeaning(SyntaxNode node)
    {
        node = QuerySyntax.Reparsed(node);
        var parent = node.Parent;
        if (parent is null) return false;
        if (parent.Kind is K.ExportAssignment or K.ExportSpecifier or K.ExternalModuleReference or K.ImportSpecifier or K.ImportClause
            || parent is ImportEqualsDeclarationNode import && import.Name == node) return true;
        var outer = node;
        while (outer.Parent is QualifiedNameNode) outer = outer.Parent;
        if (outer.Parent is ImportEqualsDeclarationNode { ModuleReference: not ExternalModuleReferenceNode } assignment && assignment.ModuleReference == outer)
        {
            var name = node is QualifiedNameNode ? node : node.Parent is QualifiedNameNode q && q.Right == node ? q : null;
            return name?.Parent is ImportEqualsDeclarationNode;
        }
        if (QuerySyntax.DeclarationName(node)) return parent.Kind is not (K.VariableDeclaration or K.Parameter or K.BindingElement
            or K.PropertyDeclaration or K.PropertySignature or K.PropertyAssignment or K.ShorthandPropertyAssignment or K.MethodDeclaration
            or K.MethodSignature or K.Constructor or K.GetAccessor or K.SetAccessor or K.FunctionDeclaration or K.FunctionExpression
            or K.ArrowFunction or K.CatchClause or K.JsxAttribute or K.ModuleDeclaration or K.SourceFile);
        if ((node.Flags & NodeFlags.JSDoc) != 0)
            for (var ancestor = node; ancestor is not null; ancestor = ancestor.Parent)
                if (ancestor.Kind is K.JSDocNameReference or K.JSDocLink or K.JSDocLinkCode or K.JSDocLinkPlain) return true;
        var typeNode = RightSide(node) ? node.Parent! : node;
        return typeNode.Parent is TypeReferenceNode or ImportTypeNode { IsTypeOf: false }
            || typeNode.Parent is ExpressionWithTypeArgumentsNode expression && QuerySyntax.PartOfType(expression)
            || parent is TypeParameterDeclarationNode or LiteralTypeNode;
    }

    internal static uint[] Encode(List<Token> tokens, SemanticTokenLegend capabilities, bool caseSensitive)
    {
        var typeMapping = SupportedLegend.TokenTypes.Select((type, index) => (type, index)).Where(item => capabilities.TokenTypes.Contains(item.type))
            .Select((item, index) => (item.index, Client: (uint)index)).ToDictionary(item => item.index, item => item.Client);
        var modifierMapping = SupportedLegend.TokenModifiers.Select((modifier, index) => (modifier, index)).Where(item => capabilities.TokenModifiers.Contains(item.modifier))
            .Select((item, index) => (item.index, Client: 1u << index)).ToDictionary(item => item.index, item => item.Client);
        List<uint> result = [];
        uint previousLine = 0, previousCharacter = 0;
        foreach (var token in tokens.OrderBy(token => token.Range.Start.Line).ThenBy(token => token.Range.Start.Character)
            .ThenBy(token => caseSensitive ? token.Projection.File.FileName : token.Projection.File.FileName.ToLowerInvariant(), Utf8StringComparer.Ordinal).ThenBy(token => token.Node.Pos))
        {
            if (!typeMapping.TryGetValue((int)token.Type, out uint type) || token.Fidelity != MappingFidelity.Exact) continue;
            uint modifiers = 0;
            foreach (var (source, target) in modifierMapping) if (((uint)token.Modifiers & (1u << source)) != 0) modifiers |= target;
            var (start, end) = (token.Range.Start, token.Range.End);
            if (start.Line != end.Line) throw new InvalidOperationException("Semantic token spans multiple lines");
            if (result.Count != 0 && start.Line == previousLine && start.Character == previousCharacter) continue;
            result.Add(start.Line - previousLine); result.Add(start.Line == previousLine ? start.Character - previousCharacter : start.Character);
            result.Add(end.Character - start.Character); result.Add(type); result.Add(modifiers);
            previousLine = start.Line; previousCharacter = start.Character;
        }
        return result.ToArray();
    }
}
