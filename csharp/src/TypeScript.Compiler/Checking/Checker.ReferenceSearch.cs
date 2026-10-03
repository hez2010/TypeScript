using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using R = TypeScript.Compiler.LanguageServices.ReferenceNavigation;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal async ValueTask<IReadOnlyList<ReferenceGroup>> GetReferenceGroupsAsync(SyntaxNode node, int position,
        IReadOnlyList<SourceFileNode> files, ReferenceOptions options, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(node, cancellation);
        return await new ReferenceSearch(this, files, options, cancellation).GetAsync(node, position);
    }

    private sealed partial class ReferenceSearch(Checker checker, IReadOnlyList<SourceFileNode> files, ReferenceOptions options, CancellationToken cancellation)
    {
        private CompilerProgram Program => checker.program.Symbols.Program;
        private readonly HashSet<SourceFileNode> allowedFiles = new(files);
        private readonly List<ReferenceGroup> result = [];
        private readonly Dictionary<Symbol, ReferenceGroup> groups = [];
        private readonly Dictionary<SourceFileNode, HashSet<Symbol>> searched = [];
        private readonly HashSet<SyntaxNode> seenExports = [], seenTypeReferences = [];
        private readonly Dictionary<(Symbol, Symbol), bool> inherits = [];
        private int meaning = R.All;
        private K specialKind;
        private enum ImportDirection { None, Import, Export }
        private sealed record Search(Symbol Symbol, ImportDirection Direction, Utf8String Text, IReadOnlyList<Symbol> Symbols, IReadOnlyList<Symbol> Parents)
        {
            internal bool Includes(Symbol? symbol) => symbol is not null && Symbols.Contains(symbol);
        }
        private ValueTask<Symbol?> SymbolAsync(SyntaxNode node) => checker.SymbolAtLocationAsync(QuerySyntax.Reparsed(node), cancellation);
        private ValueTask<Type> TypeAsync(SyntaxNode node) => checker.TypeAtLocationAsync(QuerySyntax.Reparsed(node), cancellation);
        private Symbol? Merged(Symbol? symbol) => checker.program.Symbols.Merger.GetMergedSymbol(symbol);
        private ValueTask<Symbol?> ShorthandAsync(SyntaxNode? node) => node is ShorthandPropertyAssignmentNode shorthand
            ? checker.program.EntityNames.ResolveAsync(shorthand.Name, SymbolFlags.Value | SymbolFlags.Alias, true, cancellation: cancellation)
            : ValueTask.FromResult<Symbol?>(null);
        private static bool Static(Symbol symbol) => symbol.ValueDeclaration is { } declaration && SemanticSyntax.HasModifier(declaration, K.StaticKeyword);
        private bool GlobalExports(SyntaxNode? node) => node is SourceFileNode && checker.program.Symbols.Binding(node)?.GlobalExports.Count > 0;
        private static bool ExternalModule(Symbol? symbol) => symbol is not null && symbol.Declarations.Any(d => d is SourceFileNode or ModuleDeclarationNode { Name: StringLiteralNode });
        private async ValueTask StackAsync()
        {
            cancellation.ThrowIfCancellationRequested();
            await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        }

        internal async ValueTask<IReadOnlyList<ReferenceGroup>> GetAsync(SyntaxNode node, int position)
        {
            if (options.Use is ReferenceUse.References or ReferenceUse.Rename) node = await R.AdjustedAsync(node, options.Use == ReferenceUse.Rename, cancellation);
            if (node is SourceFileNode file)
            {
                var resolved = Program.IncludeReasons.FirstOrDefault(pair => pair.Value.Any(reason => reason.ContainingFile == file.FileName
                    && reason.Position <= position && position <= reason.Position + reason.Length
                    && reason.Kind is FileIncludeKind.PathReference or FileIncludeKind.TypeReference or FileIncludeKind.Library)).Key;
                var target = Program.GetFile(resolved);
                return target?.Binding.Symbol is { } module ? await ModuleAsync(Merged(module)!, false) : [];
            }
            if (!options.Implementations && await SpecialAsync(node) is { } special) return special;
            var lookup = node is ConstructorDeclarationNode && node.Parent?.DeclarationName is { } className ? className : node;
            if (await SymbolAsync(lookup) is not { } symbol)
                return !options.Implementations && node is StringLiteralNode or NoSubstitutionTemplateLiteralNode ? await StringAsync(node) : [];
            if (symbol.Name == "export="u8) return symbol.Parent is { } parent ? await ModuleAsync(parent, false) : [];
            var modules = await SourceModuleAsync(symbol);
            if (modules is not null && (symbol.Flags & SymbolFlags.Transient) == 0) return modules;
            var targetModules = await NamespaceAliasAsync(node, symbol) is { } alias ? await SourceModuleAsync(alias) : null;
            await FindSymbolAsync(symbol, node);
            return Merge(modules, result, targetModules);
        }

        private async ValueTask FindSymbolAsync(Symbol original, SyntaxNode? node)
        {
            var symbol = original;
            if (node is not null)
            {
                if (node.Parent is ExportSpecifierNode export && !options.RenameWithAliases) symbol = await LocalExportAsync(node, symbol, export);
                else
                    foreach (var declaration in symbol.Declarations)
                        if (declaration.Parent is TypeLiteralNode { Parent: UnionTypeNode union }
                            && await checker.Properties.PropertyAsync(await checker.Nodes.FromNodeAsync(union, cancellation), symbol.Name, cancellation: cancellation) is { } property)
                        { symbol = property; break; }
            }
            meaning = node is null || options.Use == ReferenceUse.Rename ? R.All : await R.MeaningAsync(node, cancellation);
            int previous;
            do
            {
                previous = meaning;
                foreach (var declaration in symbol.Declarations)
                {
                    int declared = R.DeclarationMeaning(declaration);
                    if ((meaning & declared) != 0) meaning |= declared;
                }
            } while (meaning != previous);
            specialKind = node?.Kind is K.Constructor or K.ConstructorKeyword ? K.ConstructorKeyword
                : node is IdentifierNode && SemanticSyntax.ClassLike(node.Parent) ? K.ClassKeyword : K.Unknown;
            if (options.RenameWithAliases && symbol.Declarations.OfType<ExportSpecifierNode>().FirstOrDefault() is { } specifier)
                await AtExportAsync(specifier.Name!, symbol, specifier, await CreateAsync(node, original), true, true);
            else if (node?.Kind == K.DefaultKeyword && symbol.Name == "default"u8 && symbol.Parent is { } parent)
            {
                await AddAsync(node, symbol, ReferenceEntryKind.Node);
                await ImportsOfExportAsync(node, symbol, new(parent, ExportKind.Default));
            }
            else await InScopeAsync(symbol, await CreateAsync(node, symbol, symbols: await PopulateAsync(symbol, node)));
        }

        private async ValueTask<Search> CreateAsync(SyntaxNode? location, Symbol symbol, ImportDirection direction = ImportDirection.None,
            Utf8String text = default, IReadOnlyList<Symbol>? symbols = null)
        {
            if (text.IsEmpty)
            {
                var named = checker.program.Symbols.NameResolver(cancellation).GetLocalSymbolForExportDefault(symbol);
                if (named is null && (symbol.Flags & (SymbolFlags.Module | SymbolFlags.Transient)) != 0)
                    named = symbol.Declarations.FirstOrDefault(d => d is not (SourceFileNode or ModuleDeclarationNode))?.BindingSymbol;
                text = SymbolText(named ?? symbol);
                if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1];
            }
            List<Symbol> parents = [];
            if (options.Implementations && location?.Parent is PropertyAccessExpressionNode access && access.Name == location)
            {
                var lhs = await TypeAsync(access.Expression!);
                var types = lhs is UnionType or IntersectionType ? DefinitionTypeParts(lhs) : lhs.Symbol != symbol.Parent ? [lhs] : (IEnumerable<Type>)[];
                foreach (var type in types)
                    if (type.Symbol is { } parent && (parent.Flags & (SymbolFlags.Class | SymbolFlags.Interface)) != 0) parents.Add(parent);
            }
            return new(symbol, direction, text, symbols is { Count: > 0 } ? symbols : [symbol], parents);
        }
        private static Utf8String SymbolText(Symbol symbol)
        {
            if (symbol.Name.StartsWith(Symbol.InternalPrivatePrefix))
                return symbol.Declarations.Select(d => R.Text(d.DeclarationName)).FirstOrDefault(n => !n.IsEmpty);
            return symbol.Name;
        }
        private void Add(Symbol symbol, SyntaxNode node, ReferenceEntryKind kind = ReferenceEntryKind.Node)
        {
            if (!groups.TryGetValue(symbol, out var group))
            {
                group = new(ReferenceDefinitionKind.Symbol, null, symbol);
                groups.Add(symbol, group); result.Add(group);
            }
            group.Entries.Add(ReferenceEntry.FromNode(node, kind));
        }
        private async ValueTask AddAsync(SyntaxNode node, Symbol symbol, ReferenceEntryKind kind)
        {
            if (options.Use == ReferenceUse.Rename && node.Kind == K.DefaultKeyword) return;
            if (options.Implementations)
            {
                if (!groups.ContainsKey(symbol)) { var group = new ReferenceGroup(ReferenceDefinitionKind.Symbol, null, symbol); groups.Add(symbol, group); result.Add(group); }
                await ImplementationsAsync(node, n => Add(symbol, n, kind));
            }
            else Add(symbol, node, kind);
        }
        private SyntaxNode? Scope(Symbol symbol)
        {
            if (symbol.ValueDeclaration is FunctionExpressionNode or ClassExpressionNode) return symbol.ValueDeclaration;
            if (symbol.Declarations.Count == 0) return null;
            if ((symbol.Flags & (SymbolFlags.Property | SymbolFlags.Method)) != 0)
            {
                if (symbol.Declarations.FirstOrDefault(d => SemanticSyntax.HasModifier(d, K.PrivateKeyword) || d.DeclarationName is PrivateIdentifierNode) is { } member)
                    for (var parent = member.Parent; parent is not null; parent = parent.Parent) if (parent is ClassDeclarationNode) return parent;
                return null;
            }
            if (symbol.Declarations.Any(R.ObjectBinding)) return null;
            bool exposed = symbol.Parent is not null && (symbol.Flags & SymbolFlags.TypeParameter) == 0;
            if (exposed && !(ExternalModule(symbol.Parent) && !GlobalExports(symbol.Parent!.ValueDeclaration))) return null;
            SyntaxNode? scope = null;
            foreach (var declaration in symbol.Declarations)
            {
                var container = R.Container(declaration);
                if (scope is not null && scope != container || container is null
                    || container is SourceFileNode && checker.program.Symbols.Binding(container)?.IsModule != true) return null;
                scope = container;
            }
            return exposed ? SemanticSyntax.Source(scope) : scope;
        }
        private async ValueTask InScopeAsync(Symbol symbol, Search search)
        {
            if (Scope(symbol) is { } scope)
                await InContainerAsync(scope, SemanticSyntax.Source(scope)!, search, scope is not SourceFileNode source || allowedFiles.Contains(source));
            else foreach (var file in files) await ForNameAsync(file, search);
        }
        private async ValueTask ForNameAsync(SourceFileNode file, Search search)
        {
            if (await HasNameAsync(file, search.Text)) await InContainerAsync(file, file, search, true);
        }
        private async ValueTask InContainerAsync(SyntaxNode container, SourceFileNode file, Search search, bool addHere)
        {
            await StackAsync();
            if (!searched.TryGetValue(file, out var seen)) searched.Add(file, seen = []);
            bool any = false;
            foreach (var symbol in search.Symbols) any |= seen.Add(symbol);
            if (!any) return;
            foreach (int position in R.Positions(file, search.Text, container, cancellation))
            {
                var node = await SyntaxNavigation.GetTouchingPropertyNameAsync(file, position, cancellation);
                if (!R.ValidPosition(node, search.Text) || (await R.MeaningAsync(node, cancellation) & meaning) == 0
                    || await SymbolAsync(node) is not { } symbol) continue;
                if (node.Parent is ImportSpecifierNode import && import.PropertyName == node) continue;
                if (node.Parent is ExportSpecifierNode export) { await AtExportAsync(node, symbol, export, search, addHere, false); continue; }
                var (related, kind) = await RelatedAsync(symbol, node, false, options.Use != ReferenceUse.Rename || options.UseAliasesForRename,
                    (sym, root, basis) =>
                    {
                        if (basis is not null && Static(symbol) != Static(basis)) basis = null;
                        if (!search.Includes(basis ?? root ?? sym)) return null;
                        return root is not null && (sym.CheckFlags & CheckFlags.Synthetic) == 0 ? root : sym;
                    }, async root =>
                    {
                        if (search.Parents.Count == 0) return true;
                        foreach (var parent in search.Parents) if (await InheritsAsync(root.Parent, parent)) return true;
                        return false;
                    });
                if (related is null)
                {
                    if ((symbol.Flags & SymbolFlags.Transient) == 0 && symbol.ValueDeclaration is { } declaration
                        && await ShorthandAsync(declaration) is { } shorthand && search.Includes(shorthand) && declaration.DeclarationName is { } name)
                        await AddAsync(name, shorthand, ReferenceEntryKind.Node);
                    continue;
                }
                if (specialKind == K.ConstructorKeyword) await ConstructorReferencesAsync(node, related, search, addHere);
                else
                {
                    if (addHere) await AddAsync(node, related, kind);
                    if (specialKind == K.ClassKeyword && options.Use != ReferenceUse.Rename && SemanticSyntax.ClassLike(node.Parent))
                        ClassThisReferences(node.Parent!, search.Symbol);
                }
                if ((node.Flags & NodeFlags.JavaScriptFile) != 0 && node.Parent is BindingElementNode binding && RequireVariable(binding.Parent?.Parent))
                { symbol = binding.BindingSymbol; if (symbol is null) continue; }
                await ImportOrExportAsync(node, symbol, search);
            }
        }
        private async ValueTask<IReadOnlyList<Symbol>> PopulateAsync(Symbol symbol, SyntaxNode? node)
        {
            if (node is null) return [symbol];
            List<Symbol> symbols = [];
            await RelatedAsync(symbol, node, options.Use == ReferenceUse.Rename, !options.RenameWithAliases,
                (sym, root, basis) =>
                {
                    if (basis is not null && Static(symbol) != Static(basis)) basis = null;
                    symbols.Add(basis ?? root ?? sym); return null;
                }, _ => ValueTask.FromResult(!options.Implementations));
            return symbols;
        }
        private async ValueTask<(Symbol?, ReferenceEntryKind)> RelatedAsync(Symbol symbol, SyntaxNode location, bool renamePopulate,
            bool bindingAtLocation, Func<Symbol, Symbol?, Symbol?, Symbol?> visit, Func<Symbol, ValueTask<bool>> allowBases)
        {
            async ValueTask<Symbol?> FromRoot(Symbol sym)
            {
                foreach (var root in await checker.GetRootSymbolsAsync(sym, cancellation))
                {
                    if (visit(sym, root, null) is { } found) return found;
                    if (root.Parent is { } parent && (parent.Flags & (SymbolFlags.Class | SymbolFlags.Interface)) != 0 && await allowBases(root))
                        foreach (var basis in await BasePropertiesAsync(parent, root.Name))
                            if (visit(sym, root, basis) is { } inherited) return inherited;
                }
                return null;
            }
            var name = location.Parent is ComputedPropertyNameNode && location is StringLiteralNode or NoSubstitutionTemplateLiteralNode or NumericLiteralNode ? location.Parent : location;
            if (name.Parent is { } element && element.DeclarationName == name && element.Parent is ObjectLiteralExpressionNode or JsxAttributesNode)
            {
                var shorthand = await ShorthandAsync(location.Parent);
                if (shorthand is not null && renamePopulate) return (visit(shorthand, null, null), ReferenceEntryKind.SearchedLocalFoundProperty);
                if (await checker.Contexts.GetAsync(element.Parent, cancellation: cancellation) is { } contextual)
                    foreach (var property in await checker.GetPropertySymbolsFromContextualTypeAsync(element, contextual, true, cancellation))
                        if (await FromRoot(property) is { } found) return (found, ReferenceEntryKind.SearchedPropertyFoundLocal);
                if (await DestructuringPropertyAsync(location) is { } destructured && visit(destructured, null, null) is { } assigned)
                    return (assigned, ReferenceEntryKind.SearchedPropertyFoundLocal);
                if (shorthand is not null && visit(shorthand, null, null) is { } local) return (local, ReferenceEntryKind.SearchedLocalFoundProperty);
            }
            if (await NamespaceAliasAsync(location, symbol) is { } alias && visit(alias, null, null) is { } merged) return (merged, ReferenceEntryKind.Node);
            if (await FromRoot(symbol) is { } direct) return (direct, ReferenceEntryKind.Node);
            if (R.ParameterProperty(symbol.ValueDeclaration))
            {
                var parameter = symbol.ValueDeclaration!;
                var parameterSymbol = checker.program.Symbols.Lookup(checker.program.Symbols.Binding(parameter.Parent!)!.Get(parameter.Parent!)?.Locals, symbol.Name, SymbolFlags.Value);
                var classSymbol = checker.program.Symbols.Declaration(parameter.Parent!.Parent!)!;
                var property = checker.program.Symbols.Lookup(await checker.MembersAsync(classSymbol, cancellation), symbol.Name, SymbolFlags.Value);
                var other = (symbol.Flags & SymbolFlags.FunctionScopedVariable) != 0 ? property : parameterSymbol;
                return (other is null ? null : await FromRoot(other), ReferenceEntryKind.Node);
            }
            if (symbol.Declarations.OfType<ExportSpecifierNode>().FirstOrDefault() is { } export && (!renamePopulate || export.PropertyName is null)
                && await checker.ExportSpecifierLocalTargetSymbolAsync(export, cancellation) is { } target && visit(target, null, null) is { } exported)
                return (exported, ReferenceEntryKind.Node);
            if (!renamePopulate || bindingAtLocation)
            {
                var binding = !renamePopulate && bindingAtLocation ? location.Parent : symbol.Declarations.FirstOrDefault(d => d is BindingElementNode);
                if (R.ObjectBinding(binding) && await BindingPropertyAsync(binding!) is { } property)
                    return (await FromRoot(property), ReferenceEntryKind.SearchedPropertyFoundLocal);
            }
            return (null, ReferenceEntryKind.None);
        }
        private async ValueTask<Symbol?> BindingPropertyAsync(SyntaxNode binding) =>
            await checker.Properties.PropertyAsync(await TypeAsync(binding.Parent!), R.Text(binding.DeclarationName), cancellation: cancellation);
        private async ValueTask<Symbol?> NamespaceAliasAsync(SyntaxNode location, Symbol symbol)
        {
            if (location.Parent is not NamespaceExportDeclarationNode || (symbol.Flags & SymbolFlags.Alias) == 0) return null;
            var alias = await checker.program.Aliases.ResolveAsync(symbol, cancellation);
            var merged = Merged(alias);
            return alias != merged ? merged : null;
        }
        private async ValueTask<IReadOnlyList<Symbol>> BasePropertiesAsync(Symbol symbol, Utf8String name)
        {
            List<Symbol> symbols = [];
            HashSet<Symbol> seen = [];
            async ValueTask VisitAsync(Symbol current)
            {
                await StackAsync();
                if ((current.Flags & (SymbolFlags.Class | SymbolFlags.Interface)) == 0 || !seen.Add(current)) return;
                foreach (var declaration in current.Declarations)
                    foreach (var syntax in R.SuperTypes(declaration))
                    {
                        var type = await TypeAsync(syntax);
                        if (type.Symbol is null) continue;
                        if (await checker.Properties.PropertyAsync(type, name, cancellation: cancellation) is { } property)
                            symbols.AddRange(await checker.GetRootSymbolsAsync(property, cancellation));
                        await VisitAsync(type.Symbol);
                    }
            }
            await VisitAsync(symbol);
            return symbols;
        }
        private async ValueTask<bool> InheritsAsync(Symbol? symbol, Symbol parent)
        {
            await StackAsync();
            if (symbol == parent) return true;
            if (symbol is null) return false;
            var key = (symbol, parent);
            if (inherits.TryGetValue(key, out bool cached)) return cached;
            inherits[key] = false;
            foreach (var declaration in symbol.Declarations)
                foreach (var syntax in R.SuperTypes(declaration))
                    if ((await TypeAsync(syntax)).Symbol is { } basis && await InheritsAsync(basis, parent)) { inherits[key] = true; return true; }
            return false;
        }
        private async ValueTask<Symbol> LocalExportAsync(SyntaxNode node, Symbol symbol, ExportSpecifierNode export) =>
            (export.PropertyName is { } property ? property == node : export.Parent?.Parent is ExportDeclarationNode { ModuleSpecifier: null })
                ? await checker.ExportSpecifierLocalTargetSymbolAsync(export, cancellation) ?? symbol : symbol;
        private static bool RequireVariable(SyntaxNode? node)
        {
            if (node is not VariableDeclarationNode { Initializer: { } expression }) return false;
            while (expression is PropertyAccessExpressionNode access) expression = access.Expression!;
            return SemanticSyntax.RequireCall(expression);
        }
        private async ValueTask AtExportAsync(SyntaxNode node, Symbol symbol, ExportSpecifierNode export, Search search, bool addHere, bool always)
        {
            var local = await LocalExportAsync(node, symbol, export);
            if (!always && !search.Includes(local)) return;
            var declaration = (ExportDeclarationNode)export.Parent!.Parent!;
            if (export.PropertyName is null)
            {
                if (addHere && !(options.Use == ReferenceUse.Rename && R.Text(export.Name) == "default"u8)) await AddAsync(node, local!, ReferenceEntryKind.Node);
            }
            else if (export.PropertyName == node)
            {
                if (addHere && declaration.ModuleSpecifier is null) await AddAsync(node, local!, ReferenceEntryKind.Node);
                if (addHere && options.Use != ReferenceUse.Rename && seenExports.Add(export.Name!)) await AddAsync(export.Name!, export.BindingSymbol!, ReferenceEntryKind.Node);
            }
            else if (seenExports.Add(node) && addHere) await AddAsync(node, local!, ReferenceEntryKind.Node);
            if ((!options.RenameWithAliases || always) && Export(export.BindingSymbol!, R.Text(node) == "default"u8 || R.Text(export.Name) == "default"u8 ? ExportKind.Default : ExportKind.Named) is { } info)
                await ImportsOfExportAsync(node, export.BindingSymbol!, info);
            if (search.Direction != ImportDirection.Export && declaration.ModuleSpecifier is not null && export.PropertyName is null && !options.RenameWithAliases
                && await checker.ExportSpecifierLocalTargetSymbolAsync(export, cancellation) is { } imported) await ImportedSymbolAsync(imported);
        }
        private async ValueTask ImportedSymbolAsync(Symbol symbol)
        {
            foreach (var declaration in symbol.Declarations)
            {
                if (SemanticSyntax.Source(declaration) is not { } file) continue;
                await InContainerAsync(file, file, await CreateAsync(declaration, symbol, ImportDirection.Import), allowedFiles.Contains(file));
            }
        }
    }
}
