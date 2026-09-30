using System.Text;
using System.Collections.Frozen;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Storage;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Experiments;

public readonly record struct ProjectDiagnostic(Utf8String File, DiagnosticCode Code, int Pos, int Length, Utf8String Message);

public sealed class SliceSymbol<TStore>(
    SliceFile<TStore> file,
    NodeId declaration,
    Utf8String name,
    bool exported,
    bool typeAlias,
    bool constant,
    bool blockScoped,
    bool unresolved = false) where TStore : INodeStore
{
    public SliceFile<TStore> File { get; } = file;
    public NodeId Declaration { get; } = declaration;
    public Utf8String Name { get; } = name;
    public bool Exported { get; } = exported;
    public bool TypeAlias { get; } = typeAlias;
    public bool Constant { get; } = constant;
    public bool BlockScoped { get; } = blockScoped;
    public bool Unresolved { get; } = unresolved;
}

public sealed class SliceProject<TStore> where TStore : INodeStore
{
    public FrozenDictionary<Utf8String, SliceFile<TStore>> Files { get; }
    public List<ProjectDiagnostic> Diagnostics { get; } = [];
    public Dictionary<Utf8String, Dictionary<Utf8String, SliceSymbol<TStore>>> Locals { get; } = new(Utf8StringComparer.Ordinal);
    private readonly Dictionary<SliceSymbol<TStore>, TypeAtom[]> types = [];
    private readonly HashSet<(Utf8String File, Utf8String Name)> typeOnlyImports = [];
    private readonly CancellationToken cancellation;
    private static readonly TypeAtom[] Any = [new(AtomKind.Any)];

    public SliceProject(IEnumerable<SliceFile<TStore>> files, CancellationToken cancellation = default)
    {
        this.cancellation = cancellation;
        Files = files.ToFrozenDictionary(file => file.Name, Utf8StringComparer.Ordinal);
        Bind();
    }

    private void Bind()
    {
        using var profile = Compiler.Diagnostics.NativeProfile.Enter(Utf8Literals.TypeScriptBind);
        foreach (var file in Files.Values.OrderBy(file => file.Name, Utf8StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            var locals = new Dictionary<Utf8String, SliceSymbol<TStore>>(Utf8StringComparer.Ordinal);
            Locals.Add(file.Name, locals);
            foreach (var diagnostic in file.Diagnostics)
                Diagnostics.Add(new(file.Name, diagnostic.Code, diagnostic.Pos, diagnostic.Length, diagnostic.Message));
            foreach (NodeId statement in file.Statements)
            {
                Compiler.Diagnostics.NativeProfile.Poll();
                if (file.Store.Header(statement).Kind == SyntaxKind.TypeAliasDeclaration)
                {
                    var alias = file.Store.Get<TypeAliasDeclarationData>(statement);
                    Declare(statement, alias.Name, !alias.Modifiers.IsNull, true, false, false);
                }
                else if (file.Store.Header(statement).Kind == SyntaxKind.VariableStatement)
                {
                    var variable = file.Store.Get<VariableStatementData>(statement);
                    var list = file.Store.Get<VariableDeclarationListData>(variable.DeclarationList);
                    foreach (NodeId declaration in file.Store.Get<NodeListData>(list.Declarations).Nodes)
                        Declare(
                            declaration,
                            file.Store.Get<VariableDeclarationData>(declaration).Name,
                            !variable.Modifiers.IsNull,
                            false,
                            (file.Store.Header(variable.DeclarationList).Flags & 2) != 0,
                            (file.Store.Header(variable.DeclarationList).Flags & 3) != 0);
                }
            }
            void Declare(NodeId declaration, NodeId nameId, bool exported, bool alias, bool constant, bool blockScoped)
            {
                Utf8String name = file.Store.Get<IdentifierData>(nameId).Text;
                var symbol = new SliceSymbol<TStore>(file, declaration, name, exported, alias, constant, blockScoped);
                if (!locals.TryAdd(name, symbol))
                    Report(
                        file,
                        nameId,
                        alias ? DiagnosticCode.DuplicateIdentifier0 : DiagnosticCode.CannotRedeclareBlockScopedVariable0,
                        Utf8String.ConcatMany(Utf8Literals.DuplicateDeclaration, name, Utf8Literals.QuotePeriod));
            }
        }
        foreach (var file in Files.Values.OrderBy(file => file.Name, Utf8StringComparer.Ordinal))
        {
            foreach (NodeId statement in file.Statements)
            {
                if (file.Store.Header(statement).Kind != SyntaxKind.ImportDeclaration)
                    continue;
                var import = file.Store.Get<ImportDeclarationData>(statement);
                Utf8String specifier = file.Store.Get<StringLiteralData>(import.ModuleSpecifier).Text;
                Utf8String? resolved = ResolveModule(file.Name, specifier);
                if (resolved is null)
                    Report(
                        file,
                        import.ModuleSpecifier,
                        DiagnosticCode.CannotFindModule0OrItsCorrespondingTypeDeclarations,
                        Utf8String.ConcatMany(Utf8Literals.CannotFindModule, specifier, Utf8Literals.QuotePeriod));
                var clause = file.Store.Get<ImportClauseData>(import.ImportClause);
                var named = file.Store.Get<NamedImportsData>(clause.NamedBindings);
                foreach (NodeId element in file.Store.Get<NodeListData>(named.Elements).Nodes)
                {
                    var binding = file.Store.Get<ImportSpecifierData>(element);
                    Utf8String original = file.Store.Get<IdentifierData>(
                        binding.PropertyName.IsNull ? binding.Name : binding.PropertyName).Text;
                    Utf8String local = file.Store.Get<IdentifierData>(binding.Name).Text;
                    if (binding.IsTypeOnly || clause.PhaseModifier == SyntaxKind.TypeKeyword)
                        typeOnlyImports.Add((file.Name, local));
                    SliceSymbol<TStore>? symbol = null;
                    if (resolved is not null && (!Locals[resolved.Value].TryGetValue(original, out symbol) || !symbol.Exported))
                    {
                        Report(
                            file,
                            binding.Name,
                            DiagnosticCode.Module0HasNoExportedMember1,
                            Utf8String.ConcatMany(Utf8Literals.ModuleHasNoExportedMember, original, Utf8Literals.QuotePeriod));
                        symbol = null;
                    }
                    symbol ??= new(file, element, local, false, false, false, false, unresolved: true);
                    if (!Locals[file.Name].TryAdd(local, symbol))
                        Report(file, binding.Name, DiagnosticCode.DuplicateIdentifier0, Utf8String.ConcatMany(Utf8Literals.DuplicateImport, local, Utf8Literals.QuotePeriod));
                }
            }
        }
    }

    public Utf8String? ResolveModule(Utf8String fileName, Utf8String specifier)
    {
        // Phase-1 resolution is relative virtual .ts files only. Package lookup,
        // extension substitution and host case rules belong to the full resolver.
        if (!specifier.StartsWith("./"u8, StringComparison.Ordinal) && !specifier.StartsWith("../"u8, StringComparison.Ordinal))
            return null;
        var segments = new List<Utf8String>(fileName[..(fileName.LastIndexOf((byte)'/') + 1)].Split((byte)'/', StringSplitOptions.RemoveEmptyEntries));
        foreach (Utf8String segment in specifier.Split((byte)'/'))
        {
            if (segment == Utf8Literals.Dot)
                continue;
            if (segment == Utf8Literals.ParentDirectory)
            {
                if (segments.Count == 0)
                    return null;
                segments.RemoveAt(segments.Count - 1);
            }
            else
                segments.Add(segment);
        }
        Utf8String path = Utf8Literals.Slash + Utf8String.Join((byte)'/', segments);
        if (Files.ContainsKey(path))
            return path;
        return Files.ContainsKey(path + Utf8Literals.Ts) ? path + Utf8Literals.Ts : (Utf8String?)null;
    }

    public void Check()
    {
        using var profile = Compiler.Diagnostics.NativeProfile.Enter(Utf8Literals.TypeScriptCheck);
        foreach (var file in Files.Values.OrderBy(file => file.Name, Utf8StringComparer.Ordinal))
        {
            if (file.Diagnostics.Count != 0)
                continue;
            foreach (var symbol in Locals[file.Name].Values.Where(symbol => ReferenceEquals(symbol.File, file)).Distinct())
            {
                TypeAtom[] target = TypeOf(symbol);
                if (symbol.TypeAlias || symbol.Unresolved)
                    continue;
                var declaration = file.Store.Get<VariableDeclarationData>(symbol.Declaration);
                if (!declaration.Initializer.IsNull)
                {
                    TypeAtom[] source = Expression(file, declaration.Initializer);
                    if (!declaration.Type.IsNull && !TypeRelations.Assignable<StrictAssignment>(source, target))
                        Report(
                            file,
                            declaration.Name,
                            DiagnosticCode.Type0IsNotAssignableToType1,
                            Utf8Literals.InitializerIsNotAssignableToThe);
                }
            }
        }
    }

    public TypeAtom[] TypeOf(SliceSymbol<TStore> symbol)
    {
        if (types.TryGetValue(symbol, out var cached))
            return cached;
        var stack = new Stack<(SliceSymbol<TStore> Symbol, bool Complete)>();
        var active = new HashSet<SliceSymbol<TStore>>();
        stack.Push((symbol, false));
        while (stack.TryPop(out var frame))
        {
            Compiler.Diagnostics.NativeProfile.Poll();
            cancellation.ThrowIfCancellationRequested();
            if (types.ContainsKey(frame.Symbol))
                continue;
            if (frame.Complete)
            {
                active.Remove(frame.Symbol);
                types[frame.Symbol] = EvaluateSymbol(frame.Symbol);
                continue;
            }
            if (!active.Add(frame.Symbol))
            {
                foreach (var pending in stack)
                {
                    if (!pending.Complete || !active.Contains(pending.Symbol))
                        continue;
                    var cyclic = pending.Symbol;
                    NodeId name = cyclic.TypeAlias
                        ? cyclic.File.Store.Get<TypeAliasDeclarationData>(cyclic.Declaration).Name
                        : cyclic.File.Store.Get<VariableDeclarationData>(cyclic.Declaration).Name;
                    Report(
                        cyclic.File,
                        name,
                        cyclic.TypeAlias
                            ? DiagnosticCode.TypeAlias0CircularlyReferencesItself
                            : DiagnosticCode.X0ImplicitlyHasTypeAnyBecauseItDoesNotHaveATypeAnnotationAndIsReferencedDirectlyOrIndirectlyInItsOwnInitializer,
                        Utf8String.ConcatMany(Utf8Literals.Declaration, cyclic.Name, Utf8Literals.CircularlyReferencesItself));
                    types[cyclic] = Any;
                    if (ReferenceEquals(cyclic, frame.Symbol))
                        break;
                }
                continue;
            }
            stack.Push((frame.Symbol, true));
            foreach (var dependency in Dependencies(frame.Symbol))
                if (!types.ContainsKey(dependency))
                    stack.Push((dependency, false));
        }
        return types[symbol];
    }

    private IEnumerable<SliceSymbol<TStore>> Dependencies(SliceSymbol<TStore> symbol)
    {
        if (symbol.Unresolved)
            yield break;
        var file = symbol.File;
        NodeId root = symbol.TypeAlias
            ? file.Store.Get<TypeAliasDeclarationData>(symbol.Declaration).Type
            : file.Store.Get<VariableDeclarationData>(symbol.Declaration).Type;
        if (root.IsNull)
            root = file.Store.Get<VariableDeclarationData>(symbol.Declaration).Initializer;
        var stack = new Stack<NodeId>();
        if (!root.IsNull)
            stack.Push(root);
        while (stack.TryPop(out NodeId id))
        {
            cancellation.ThrowIfCancellationRequested();
            if (file.Store.Header(id).Kind == SyntaxKind.TypeReference)
            {
                NodeId name = file.Store.Get<TypeReferenceNodeData>(id).TypeName;
                if (Lookup(file, name) is { } dependency)
                    yield return dependency;
                continue;
            }
            if (file.Store.Header(id).Kind == SyntaxKind.Identifier)
            {
                if (file.Store.Get<IdentifierData>(id).Text == Utf8Literals.Undefined && !Locals[file.Name].ContainsKey(Utf8Literals.Undefined))
                    continue;
                if (Lookup(file, id) is { } dependency)
                    yield return dependency;
                continue;
            }
            for (int i = SliceSchema.ChildSlots(file.Store, id) - 1; i >= 0; i--)
            {
                NodeId child = SliceSchema.ChildAt(file.Store, id, i);
                if (!child.IsNull)
                    stack.Push(child);
            }
        }
    }

    private SliceSymbol<TStore>? Lookup(SliceFile<TStore> file, NodeId identifier)
    {
        Utf8String name = file.Store.Get<IdentifierData>(identifier).Text;
        if (Locals[file.Name].TryGetValue(name, out var symbol))
            return symbol;
        Report(file, identifier, DiagnosticCode.CannotFindName0, Utf8String.ConcatMany(Utf8Literals.CannotFindName, name, Utf8Literals.QuotePeriod));
        return null;
    }

    private TypeAtom[] EvaluateSymbol(SliceSymbol<TStore> symbol)
    {
        if (symbol.Unresolved)
            return Any;
        var file = symbol.File;
        if (symbol.TypeAlias)
            return EvaluateType(file, file.Store.Get<TypeAliasDeclarationData>(symbol.Declaration).Type);
        var variable = file.Store.Get<VariableDeclarationData>(symbol.Declaration);
        if (!variable.Type.IsNull)
            return EvaluateType(file, variable.Type);
        TypeAtom[] result = variable.Initializer.IsNull ? Any : Expression(file, variable.Initializer);
        if (symbol.Constant)
            return result;
        return Reduce(result.SelectMany(atom => atom.Kind switch
        {
            AtomKind.StringLiteral => new TypeAtom[] { new(AtomKind.String) },
            AtomKind.NumberLiteral => new TypeAtom[] { new(AtomKind.Number) },
            AtomKind.True or AtomKind.False => new TypeAtom[] { new(AtomKind.True), new(AtomKind.False) },
            _ => new[] { atom },
        }));
    }

    private TypeAtom[] EvaluateType(SliceFile<TStore> file, NodeId root)
    {
        var values = new Dictionary<NodeId, TypeAtom[]>();
        var stack = new Stack<(NodeId Node, bool Complete)>();
        stack.Push((root, false));
        while (stack.TryPop(out var frame))
        {
            cancellation.ThrowIfCancellationRequested();
            var header = file.Store.Header(frame.Node);
            if (!frame.Complete && header.Kind is SyntaxKind.UnionType or SyntaxKind.ParenthesizedType)
            {
                stack.Push((frame.Node, true));
                if (header.Kind == SyntaxKind.ParenthesizedType)
                    stack.Push((file.Store.Get<ParenthesizedTypeNodeData>(frame.Node).Type, false));
                else
                    foreach (NodeId child in file.Store.Get<NodeListData>(file.Store.Get<UnionTypeNodeData>(frame.Node).Types).Nodes)
                        stack.Push((child, false));
                continue;
            }
            values[frame.Node] = header.Kind switch
            {
                SyntaxKind.ParenthesizedType => values[file.Store.Get<ParenthesizedTypeNodeData>(frame.Node).Type],
                SyntaxKind.UnionType => Reduce(
                    file.Store.Get<NodeListData>(
                        file.Store.Get<UnionTypeNodeData>(frame.Node).Types).Nodes.SelectMany(node => values[node])),
                SyntaxKind.TypeReference => ReferenceType(file, file.Store.Get<TypeReferenceNodeData>(frame.Node).TypeName),
                SyntaxKind.LiteralType => Expression(file, file.Store.Get<LiteralTypeNodeData>(frame.Node).Literal),
                SyntaxKind.AnyKeyword => Any,
                SyntaxKind.NeverKeyword => [],
                SyntaxKind.UnknownKeyword => [new(AtomKind.Unknown)],
                SyntaxKind.StringKeyword => [new(AtomKind.String)],
                SyntaxKind.NumberKeyword => [new(AtomKind.Number)],
                SyntaxKind.BigIntKeyword => [new(AtomKind.BigInt)],
                SyntaxKind.BooleanKeyword => [new(AtomKind.True), new(AtomKind.False)],
                SyntaxKind.SymbolKeyword => [new(AtomKind.Symbol)],
                SyntaxKind.ObjectKeyword => [new(AtomKind.Object)],
                SyntaxKind.VoidKeyword => [new(AtomKind.Void)],
                SyntaxKind.UndefinedKeyword => [new(AtomKind.Undefined)],
                _ => throw new InvalidDataException($"Unsupported type node {header.Kind}"),
            };
        }
        return values[root];
    }

    private TypeAtom[] ReferenceType(SliceFile<TStore> file, NodeId name)
    {
        var symbol = Lookup(file, name);
        if (symbol is null || symbol.Unresolved)
            return Any;
        if (!symbol.TypeAlias)
        {
            Report(
                file,
                name,
                DiagnosticCode.X0RefersToAValueButIsBeingUsedAsATypeHereDidYouMeanTypeof0,
                Utf8Literals.AValueCannotBeUsedAs);
            return Any;
        }
        return types.TryGetValue(symbol, out var type) ? type : Any;
    }

    private TypeAtom[] Expression(SliceFile<TStore> file, NodeId id)
    {
        switch (file.Store.Header(id).Kind)
        {
            case SyntaxKind.StringLiteral:
                return [new(AtomKind.StringLiteral, file.Store.Get<StringLiteralData>(id).Text)];
            case SyntaxKind.NumericLiteral:
                return [new(AtomKind.NumberLiteral, file.Store.Get<NumericLiteralData>(id).Text)];
            case SyntaxKind.TrueKeyword:
                return [new(AtomKind.True)];
            case SyntaxKind.FalseKeyword:
                return [new(AtomKind.False)];
            case SyntaxKind.NullKeyword:
                return [new(AtomKind.Null)];
            case SyntaxKind.PrefixUnaryExpression:
                var unary = file.Store.Get<PrefixUnaryExpressionData>(id);
                Utf8String literal = file.Store.Get<NumericLiteralData>(unary.Operand).Text;
                return [new(AtomKind.NumberLiteral, unary.Operator == SyntaxKind.MinusToken && literal != Utf8Literals.Zero ? Utf8Literals.Dash + literal : literal)];
            case SyntaxKind.Identifier:
                Utf8String name = file.Store.Get<IdentifierData>(id).Text;
                if (name == Utf8Literals.Undefined && !Locals[file.Name].ContainsKey(name))
                    return [new(AtomKind.Undefined)];
                if (typeOnlyImports.Contains((file.Name, name)))
                    Report(
                        file,
                        id,
                        DiagnosticCode.X0CannotBeUsedAsAValueBecauseItWasImportedUsingImportType,
                        Utf8Literals.ATypeOnlyImportCannotBe);
                var symbol = Lookup(file, id);
                if (symbol is null)
                    return Any;
                if (symbol.TypeAlias)
                {
                    Report(file, id, DiagnosticCode.X0OnlyRefersToATypeButIsBeingUsedAsAValueHere, Utf8Literals.ATypeCannotBeUsedAs);
                    return Any;
                }
                if (symbol.BlockScoped
                    && ReferenceEquals(symbol.File, file)
                    && file.Store.Header(id).Pos < file.Store.Header(symbol.Declaration).End)
                    Report(
                        file,
                        id,
                        DiagnosticCode.BlockScopedVariable0UsedBeforeItsDeclaration,
                        Utf8Literals.BlockScopedVariableUsedBeforeIts);
                return types.TryGetValue(symbol, out var type) ? type : TypeOf(symbol);
            default:
                throw new InvalidDataException("Unsupported expression");
        }
    }

    private static TypeAtom[] Reduce(IEnumerable<TypeAtom> atoms)
    {
        var set = atoms.ToHashSet();
        if (set.Contains(new(AtomKind.Any)))
            return Any;
        if (set.Contains(new(AtomKind.Unknown)))
            return [new(AtomKind.Unknown)];
        if (set.Contains(new(AtomKind.String)))
            set.RemoveWhere(atom => atom.Kind == AtomKind.StringLiteral);
        if (set.Contains(new(AtomKind.Number)))
            set.RemoveWhere(atom => atom.Kind == AtomKind.NumberLiteral);
        return [.. set];
    }

    private void Report(SliceFile<TStore> file, NodeId id, DiagnosticCode code, Utf8String message)
    {
        NodeHeader header = file.Store.Header(id);
        int pos = header.Pos;
        if (header.Kind == SyntaxKind.Identifier)
            pos = header.End - file.Store.Get<IdentifierData>(id).Text.Length;
        else if (header.End > header.Pos)
            pos += new SliceLexer<Utf8Source>(new(file.Text.AsMemory(header.Pos, header.End - header.Pos)), []).Scan().Start;
        var diagnostic = new ProjectDiagnostic(file.Name, code, pos, header.End - pos, message);
        if (!Diagnostics.Any(existing => existing.File == diagnostic.File && existing.Code == code && existing.Pos == pos))
            Diagnostics.Add(diagnostic);
    }
}
