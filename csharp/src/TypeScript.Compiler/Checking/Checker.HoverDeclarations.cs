using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal async ValueTask<Utf8String> ExpandHoverSymbolAsync(Symbol symbol, SymbolFlags meaning, HoverVerbosity verbosity, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(symbol.ValueDeclaration, cancellation);
        return await VisibilityOperationAsync(() => ChainOperationAsync(() => ContainerOperationAsync(async () =>
        {
            var state = new TypeSyntaxContext(null, true, flags: NodeBuilderFlags.IgnoreErrors | NodeBuilderFlags.MultilineObjectLiterals
                | NodeBuilderFlags.UseAliasDefinedOutsideCurrentScope, verbosity: verbosity);
            state.TypeStack.Add(await Declared.GetAsync(symbol, cancellation));
            state.TypeStack.Add(context.ErrorType);
            List<SyntaxNode> nodes = [];
            if ((symbol.Flags & SymbolFlags.Enum) != 0) nodes.Add(await HoverEnumAsync(symbol, state, cancellation));
            if ((symbol.Flags & SymbolFlags.Class) != 0) nodes.Add(await HoverClassAsync(symbol, state, cancellation));
            if ((symbol.Flags & SymbolFlags.Module) != 0) nodes.Add(await HoverModuleAsync(symbol, state, cancellation));
            if ((symbol.Flags & (SymbolFlags.Class | SymbolFlags.Interface)) == SymbolFlags.Interface && (meaning & SymbolFlags.Interface) != 0)
                nodes.Add(await HoverInterfaceAsync(symbol, state, cancellation));
            var emit = new EmitContext();
            foreach (var node in state.NoAsciiEscape) emit.AddFlags(node, EmitFlags.NoAsciiEscaping);
            foreach (var node in state.SingleLine) emit.AddFlags(node, EmitFlags.SingleLine);
            var printer = new SyntaxPrinter(new() { RemoveComments = true }, emit);
            return Utf8String.Join("\n"u8, nodes.Select(node => printer.Print(node, symbol.ValueDeclaration is { } declaration
                ? SemanticSyntax.Source(declaration) : null, cancellation: cancellation).TrimEnd((byte)'\n')));
        }, cancellation), cancellation), cancellation);
    }

    private static NodeList? HoverModifiers(SyntaxNode? declaration, NodeFactory factory, bool outer = false, bool member = false, bool isStatic = false,
        NodeList? existing = null)
    {
        var kinds = ((declaration as IModifiedNode)?.Modifiers ?? new([])).Select(node => node.Kind)
            .Concat((existing ?? new([])).Select(node => node.Kind)).ToHashSet();
        if (outer) { kinds.Remove(K.ExportKeyword); kinds.Remove(K.DeclareKeyword); }
        if (member) kinds.Remove(K.AsyncKeyword);
        if (isStatic) kinds.Add(K.StaticKeyword);
        K[] order = [K.ExportKeyword, K.DeclareKeyword, K.DefaultKeyword, K.ConstKeyword, K.PublicKeyword, K.PrivateKeyword,
            K.ProtectedKeyword, K.AbstractKeyword, K.StaticKeyword, K.OverrideKeyword, K.ReadonlyKeyword, K.AccessorKeyword, K.AsyncKeyword, K.InKeyword, K.OutKeyword];
        var nodes = order.Where(kinds.Contains).Select(kind => (SyntaxNode)factory.NewToken(kind)).ToArray();
        return nodes.Length == 0 ? null : new(nodes);
    }

    private async ValueTask<NodeList?> HoverTypeParametersAsync(Symbol symbol, TypeSyntaxContext state, CancellationToken cancellation)
    {
        List<SyntaxNode> nodes = [];
        foreach (TypeParameter parameter in program.Scopes.Local(symbol, cancellation))
            nodes.Add(await TypeParameterSyntaxAsync(parameter, await Instantiation.Constraints.ConstraintAsync(parameter, cancellation) is { } constraint
                ? await ConstraintSyntaxAsync(parameter, constraint, state, cancellation) : null, state, cancellation));
        return nodes.Count == 0 ? null : new(nodes.ToArray());
    }

    private static NodeList HoverHeritage(IEnumerable<SyntaxNode> declarations, TypeSyntaxContext state)
    {
        List<SyntaxNode> extends = [], implements = [];
        foreach (var declaration in declarations)
        {
            var clauses = declaration switch { ClassDeclarationNode c => c.HeritageClauses, ClassExpressionNode c => c.HeritageClauses,
                InterfaceDeclarationNode i => i.HeritageClauses, _ => null };
            foreach (HeritageClauseNode clause in clauses ?? new([]))
                foreach (var type in clause.Types ?? new([])) (clause.Token == K.ExtendsKeyword ? extends : implements).Add(type.DeepClone<SyntaxNode>(state.Factory));
        }
        List<SyntaxNode> result = [];
        if (extends.Count != 0) result.Add(state.Factory.NewHeritageClause(K.ExtendsKeyword, new(extends.ToArray())));
        if (implements.Count != 0) result.Add(state.Factory.NewHeritageClause(K.ImplementsKeyword, new(implements.ToArray())));
        return new(result.ToArray());
    }

    private async ValueTask<List<Symbol>> HoverOwnPropertiesAsync(InterfaceType type, IReadOnlyList<Type> bases, IReadOnlyList<Symbol> properties,
        CancellationToken cancellation)
    {
        HashSet<Utf8String> inherited = [];
        var byName = properties.ToDictionary(property => property.Name);
        foreach (var basis in bases)
            foreach (var property in await Properties.GetAsync(await Bases.WithThisAsync(basis, type.ThisType, cancellation: cancellation), cancellation))
                if (byName.TryGetValue(property.Name, out var current) && property.Parent == current.Parent) inherited.Add(property.Name);
        return properties.Where(property => !inherited.Contains(property.Name)).ToList();
    }

    private async ValueTask HoverPropertiesAsync(IReadOnlyList<Symbol> properties, List<SyntaxNode> members, TypeSyntaxContext state,
        CancellationToken cancellation, bool classElements = false, bool isStatic = false, bool modifiers = true)
    {
        properties = properties.Where(property => (property.Flags & SymbolFlags.Prototype) == 0).ToArray();
        for (int i = 0; i < properties.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (HoverTruncated(state) && i + 3 < properties.Count - 1)
            {
                members.Add(state.Factory.NewPropertySignatureDeclaration(null,
                    state.Factory.NewIdentifier("... "u8 + Utf8String.Format(properties.Count - i - 1) + " more ..."u8), null, null, null));
                i = properties.Count - 1;
            }
            int start = members.Count;
            await AddPropertySyntaxAsync(properties[i], members, state, cancellation);
            if (!classElements) continue;
            for (int j = start; j < members.Count; j++)
            {
                members[j] = members[j] switch
                {
                    PropertySignatureDeclarationNode p => state.Factory.NewPropertyDeclaration(p.Modifiers, p.Name, p.PostfixToken, p.Type, null),
                    MethodSignatureDeclarationNode m => state.Factory.NewMethodDeclaration(m.Modifiers, null, m.Name, m.PostfixToken, m.TypeParameters, m.Parameters, m.Type, null, null),
                    _ => members[j],
                };
                if (modifiers && members[j] is IModifiedNode modified)
                    modified.Modifiers = HoverModifiers(properties[i].ValueDeclaration ?? properties[i].Declarations.FirstOrDefault(),
                        state.Factory, member: true, isStatic: isStatic, existing: modified.Modifiers);
            }
        }
    }

    private static bool HoverTruncated(TypeSyntaxContext state)
    {
        if (state.Verbosity is not { Level: >= 0 } verbosity || !state.Length.Truncated()) return false;
        verbosity.Truncated = true; return true;
    }

    private async ValueTask<SyntaxNode> HoverEnumAsync(Symbol symbol, TypeSyntaxContext state, CancellationToken cancellation)
    {
        var f = state.Factory; state.Length.Add(Symbol.EscapeName(symbol.Name), 9);
        var properties = (await Properties.GetAsync(await Values.GetAsync(symbol, cancellation), cancellation)).Where(p => (p.Flags & SymbolFlags.EnumMember) != 0).ToArray();
        List<SyntaxNode> members = [];
        async ValueTask<SyntaxNode?> Initializer(Symbol member)
        {
            if (member.Declarations.OfType<EnumMemberNode>().FirstOrDefault() is not { } declaration) return null;
            return (await EnumValues.GetAsync(declaration, cancellation)).Value switch
            { Utf8String text => f.NewStringLiteral(text, 0), double number => f.NewNumericLiteral(TokenFacts.NumberText(number), 0), _ => null };
        }
        for (int i = 0; i < properties.Length; i++)
        {
            if (HoverTruncated(state) && i + 3 < properties.Length - 1)
            {
                members.Add(f.NewEnumMember(f.NewStringLiteral(" ... "u8 + Utf8String.Format(properties.Length - i - 1) + " more ... "u8, 0), null));
                members.Add(f.NewEnumMember(f.NewIdentifier(properties[^1].Name), await Initializer(properties[^1]))); break;
            }
            var property = properties[i];
            var original = property.Declarations.OfType<EnumMemberNode>().FirstOrDefault()?.Initializer;
            var initializer = original?.DeepClone<SyntaxNode>(f) ?? await Initializer(property);
            state.Length.Add(property.Name, 4 + (initializer is null ? 0 : 5));
            members.Add(f.NewEnumMember(f.NewIdentifier(property.Name), initializer));
        }
        return f.NewEnumDeclaration(HoverModifiers(symbol.Declarations.FirstOrDefault(d => d is EnumDeclarationNode), f, outer: true),
            f.NewIdentifier(Symbol.EscapeName(symbol.Name)), new(members.ToArray()));
    }

    private async ValueTask<SyntaxNode> HoverInterfaceAsync(Symbol symbol, TypeSyntaxContext state, CancellationToken cancellation)
    {
        var f = state.Factory; state.Length.Add(Symbol.EscapeName(symbol.Name), 14);
        var type = (InterfaceType)await Declared.GetAsync(symbol, cancellation);
        var parameters = await HoverTypeParametersAsync(symbol, state, cancellation);
        var bases = await BaseTypesAsync(type, cancellation);
        var resolved = await Members.ResolveAsync(type, cancellation);
        List<SyntaxNode> members = [];
        await HoverIndexesAsync(type, bases.Count == 0 ? null : await Algebra.IntersectionAsync(bases, cancellation: cancellation), members, state, cancellation);
        foreach (var signature in resolved.ConstructSignatures)
            if ((signature.Flags & SignatureFlags.Abstract) == 0) members.Add(await SignatureSyntaxAsync(signature, K.ConstructSignature, state, cancellation));
        foreach (var signature in resolved.CallSignatures) members.Add(await SignatureSyntaxAsync(signature, K.CallSignature, state, cancellation));
        await HoverPropertiesAsync(await HoverOwnPropertiesAsync(type, bases, resolved.Properties ?? [], cancellation), members, state, cancellation);
        return f.NewInterfaceDeclaration(HoverModifiers(symbol.Declarations.FirstOrDefault(d => d is InterfaceDeclarationNode), f, outer: true),
            f.NewIdentifier(Symbol.EscapeName(symbol.Name)), parameters, HoverHeritage(symbol.Declarations.Where(d => d is InterfaceDeclarationNode), state), new(members.ToArray()));
    }

    private async ValueTask<SyntaxNode> HoverClassAsync(Symbol symbol, TypeSyntaxContext state, CancellationToken cancellation)
    {
        var f = state.Factory; state.Length.Add(Symbol.EscapeName(symbol.Name), 9);
        var declarations = symbol.Declarations.Where(d => d is ClassDeclarationNode or ClassExpressionNode).ToArray();
        var original = declarations.FirstOrDefault(); var enclosing = state.Symbols.Enclosing;
        if (original is not null) state.Symbols.Enclosing = original;
        try
        {
            var parameters = await HoverTypeParametersAsync(symbol, state, cancellation);
            var declared = (InterfaceType)await Declared.GetAsync(symbol, cancellation);
            var type = await Bases.WithThisAsync(declared, null, cancellation: cancellation);
            var bases = await BaseTypesAsync(declared, cancellation);
            var staticType = await Values.GetAsync(symbol, cancellation);
            bool isClass = staticType.Symbol?.ValueDeclaration is ClassDeclarationNode or ClassExpressionNode;
            var staticBase = isClass ? await BaseConstructorAsync(declared, cancellation) : context.AnyType;
            var properties = await HoverOwnPropertiesAsync(declared, bases, await Properties.GetAsync(type, cancellation), cancellation);
            List<SyntaxNode> instance = [], statics = [], privates = [], members = [];
            static bool Private(Symbol property) => property.ValueDeclaration?.DeclarationName is PrivateIdentifierNode;
            await HoverPropertiesAsync(properties.Where(p => !Private(p)).ToArray(), instance, state, cancellation, classElements: true);
            await HoverPropertiesAsync((await Properties.GetAsync(staticType, cancellation)).Where(p => (p.Flags & SymbolFlags.Prototype) == 0
                && p.Name != "prototype"u8 && !NamespaceMember(p)).ToArray(), statics, state, cancellation, classElements: true, isStatic: true);
            await HoverPropertiesAsync(properties.Where(Private).ToArray(), privates, state, cancellation, classElements: true, modifiers: false);
            var constructors = await HoverConstructorsAsync(symbol, staticType, staticBase, isClass, state, cancellation);
            await HoverIndexesAsync(type, bases.FirstOrDefault(), members, state, cancellation);
            members.AddRange(statics); members.AddRange(constructors); members.AddRange(instance); members.AddRange(privates);
            return f.NewClassDeclaration(HoverModifiers(original, f, outer: true), original is ClassExpressionNode ? null : f.NewIdentifier(Symbol.EscapeName(symbol.Name)),
                parameters, HoverHeritage(declarations, state), new(members.ToArray()));
        }
        finally { state.Symbols.Enclosing = enclosing; }
    }

    private async ValueTask<List<SyntaxNode>> HoverConstructorsAsync(Symbol symbol, Type type, Type? basis, bool isClass,
        TypeSyntaxContext state, CancellationToken cancellation)
    {
        var signatures = await SignaturesAsync(type, true, cancellation);
        if (!isClass && symbol.ValueDeclaration is { } declaration && (declaration.Flags & NodeFlags.JavaScriptFile) != 0 && signatures.Count == 0)
        {
            state.Length.Add(21);
            return [state.Factory.NewConstructorDeclaration(new([state.Factory.NewToken(K.PrivateKeyword)]), null, new([]), null, null, null)];
        }
        if (basis is not null)
        {
            var bases = await SignaturesAsync(basis, true, cancellation);
            if (bases.Count == 0 && signatures.All(signature => signature.Parameters.Count == 0)) return [];
            if (bases.Count == signatures.Count)
            {
                bool same = true;
                for (int i = 0; i < bases.Count && same; i++)
                    same = await SignatureComparison.CompareAsync(signatures[i], bases[i], ignoreReturn: true, cancellation: cancellation,
                        compareTypes: async (left, right, ct) => await Relations.RelatedAsync(left, right, RelationKind.Identity, ct) ? Ternary.True : Ternary.False) == Ternary.True;
                if (same) return [];
            }
            var access = signatures.SelectMany(signature => (signature.Declaration as IModifiedNode)?.Modifiers ?? new([]))
                .Where(modifier => modifier.Kind is K.PrivateKeyword or K.ProtectedKeyword).Select(modifier => modifier.Kind).Distinct().Order().ToArray();
            if (access.Length != 0) return [state.Factory.NewConstructorDeclaration(new(access.Select(kind => (SyntaxNode)state.Factory.NewToken(kind)).ToArray()), null, new([]), null, null, null)];
        }
        else if (signatures.All(signature => signature.Parameters.Count == 0)) return [];
        List<SyntaxNode> result = [];
        foreach (var signature in signatures) { state.Length.Add(1); result.Add(await SignatureSyntaxAsync(signature, K.Constructor, state, cancellation)); }
        return result;
    }

    private async ValueTask HoverIndexesAsync(Type type, Type? basis, List<SyntaxNode> result, TypeSyntaxContext state, CancellationToken cancellation)
    {
        var baseInfos = basis is null ? [] : await IndexesAsync(basis, cancellation);
        foreach (var info in await IndexesAsync(type, cancellation))
        {
            if (baseInfos.FirstOrDefault(index => index.KeyType == info.KeyType) is { } inherited
                && await Relations.RelatedAsync(info.ValueType, inherited.ValueType, RelationKind.Identity, cancellation)) continue;
            result.Add(await IndexSignatureSyntaxAsync(info, state, cancellation));
        }
    }

    private static bool NamespaceMember(Symbol symbol) => (symbol.Flags & (SymbolFlags.Type | SymbolFlags.Namespace | SymbolFlags.Alias)) != 0
        || !((symbol.Flags & SymbolFlags.Prototype) != 0 || symbol.Name == "prototype"u8
            || symbol.ValueDeclaration is { Parent: ClassDeclarationNode or ClassExpressionNode } declaration && SemanticSyntax.IsStatic(declaration));

    private async ValueTask<SyntaxNode> HoverModuleAsync(Symbol symbol, TypeSyntaxContext state, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var f = state.Factory;
        var indexes = program.Symbols.Program.SourceFiles.Select((file, index) => (file.Syntax, index)).ToDictionary(item => item.Syntax, item => item.index);
        var members = (await ExportsAsync(symbol, cancellation)).Values.Where(member => NamespaceMember(member) && IdentifierName(member.Name))
            .OrderBy(member => member.Declarations.FirstOrDefault() is { } declaration && SemanticSyntax.Source(declaration) is { } file ? indexes.GetValueOrDefault(file) : int.MaxValue)
            .ThenBy(member => member.Declarations.FirstOrDefault()?.Pos ?? int.MaxValue).ThenBy(member => member.Name, Utf8StringComparer.Ordinal).ToArray();
        state.Length.Add(14);
        var name = await SymbolDisplayNameAsync(symbol, null, SymbolFlags.All, cancellation,
            SymbolFormatFlags.AllowAnyNodeKind | SymbolFormatFlags.UseOnlyExternalAliasing | SymbolFormatFlags.WriteTypeParametersOrArguments);
        SyntaxNode localName = Quoted(name) ? f.NewStringLiteral(UnquoteSymbolText(name), 0) : f.NewIdentifier(name);
        List<(SyntaxNode Node, bool Local)> statements = [];
        HashSet<Symbol> locals = [];
        for (int i = 0; i < members.Length; i++)
        {
            var member = members[i];
            if (HoverTruncated(state) && i + 3 < members.Length - 1)
            {
                statements.Add((f.NewExpressionStatement(f.NewIdentifier("... ("u8 + Utf8String.Format(members.Length - i - 1) + " more) ..."u8)), false));
                i = members.Length - 2; continue;
            }
            if ((member.Flags & SymbolFlags.Alias) != 0 && await program.Aliases.ImmediateAsync(member, cancellation) is { } target && target != UnknownSymbol)
            {
                if ((target.Flags & (SymbolFlags.Variable | SymbolFlags.Property)) != 0 && locals.Add(target))
                    statements.Add((await HoverVariableAsync(target, target.Name, state, cancellation), true));
                state.Length.Add(member.Name, 16);
                statements.Add((f.NewExportDeclaration(null, false, f.NewNamedExports(new([
                    f.NewExportSpecifier(false, member.Name == target.Name ? null : f.NewIdentifier(target.Name), f.NewIdentifier(member.Name))])), null, null), false));
                continue;
            }
            var resolved = await ResolveSymbolAsync(member, cancellation) ?? member;
            if ((resolved.Flags & (SymbolFlags.Function | SymbolFlags.Method)) != 0)
            {
                foreach (var signature in await SignaturesAsync(await Values.GetAsync(resolved, cancellation), false, cancellation))
                { state.Length.Add(1); statements.Add((await SignatureSyntaxAsync(signature, K.FunctionDeclaration, state, cancellation, f.NewIdentifier(member.Name)), false)); }
                if ((resolved.Flags & SymbolFlags.Module) == 0 || resolved.Exports.Count == 0)
                {
                    var empty = f.NewModuleBlock(new([])); state.SingleLine.Add(empty);
                    statements.Add((f.NewModuleDeclaration(null, K.NamespaceKeyword, f.NewIdentifier(member.Name), null, empty), false));
                }
            }
            else statements.Add((await HoverNamespaceMemberAsync(resolved, member.Name, state, cancellation), false));
        }
        foreach (var (node, local) in statements)
            if (!local && node is not ExportDeclarationNode && node is IModifiedNode modified)
                modified.Modifiers = new([f.NewToken(K.ExportKeyword), .. modified.Modifiers ?? new([])]);
        if (statements.Count != 0 && statements.All(statement => SemanticSyntax.HasModifier(statement.Node, K.ExportKeyword)))
            foreach (var (node, _) in statements)
                if (node is IModifiedNode modified) modified.Modifiers = new(modified.Modifiers!.Where(modifier => modifier.Kind != K.ExportKeyword).ToArray());
        var attributes = symbol.Declarations.OfType<ModuleDeclarationNode>().FirstOrDefault(declaration => declaration.Attributes is not null)?.Attributes?.DeepClone<TypeLiteralNode>(f);
        if (attributes is not null) state.SingleLine.Add(attributes);
        var body = f.NewModuleBlock(new(statements.Select(statement => statement.Node).ToArray()));
        if (statements.Count == 0) state.SingleLine.Add(body);
        return f.NewModuleDeclaration(HoverModifiers(symbol.Declarations.FirstOrDefault(declaration => declaration is ModuleDeclarationNode), f, outer: true),
            localName is IdentifierNode ? K.NamespaceKeyword : K.ModuleKeyword, localName, attributes, body);
    }

    private async ValueTask<SyntaxNode> HoverVariableAsync(Symbol symbol, Utf8String name, TypeSyntaxContext state, CancellationToken cancellation)
    {
        state.Length.Add(name, 5); var f = state.Factory;
        return f.NewVariableStatement(null, f.NewVariableDeclarationList(new([f.NewVariableDeclaration(f.NewIdentifier(name), null,
            await DeclarationTypeSyntaxAsync(await Widening.GetAsync(await Values.GetAsync(symbol, cancellation), cancellation), null, false, state, cancellation), null)]), NodeFlags.Let));
    }

    private async ValueTask<SyntaxNode> HoverNamespaceMemberAsync(Symbol symbol, Utf8String name, TypeSyntaxContext state, CancellationToken cancellation)
    {
        if ((symbol.Flags & SymbolFlags.TypeAlias) != 0)
        {
            var parameters = await HoverTypeParametersAsync(symbol, state, cancellation);
            var node = await TypeSyntaxAsync(await Declared.GetAsync(symbol, cancellation), state, cancellation, true);
            state.Length.Add(name, 8);
            return state.Factory.NewTypeAliasDeclaration(K.TypeAliasDeclaration, null, state.Factory.NewIdentifier(name), parameters, node);
        }
        if ((symbol.Flags & SymbolFlags.Enum) != 0) return await HoverEnumAsync(symbol, state, cancellation);
        if ((symbol.Flags & SymbolFlags.Class) != 0) return await HoverClassAsync(symbol, state, cancellation);
        if ((symbol.Flags & SymbolFlags.Interface) != 0) return await HoverInterfaceAsync(symbol, state, cancellation);
        if ((symbol.Flags & SymbolFlags.Module) != 0) return await HoverModuleAsync(symbol, state, cancellation);
        return await HoverVariableAsync(symbol, name, state, cancellation);
    }
}
