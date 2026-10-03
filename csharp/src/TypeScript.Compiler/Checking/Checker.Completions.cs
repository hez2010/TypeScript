using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal bool IsArgumentsSymbol(Symbol symbol) => symbol == program.Symbols.ArgumentsSymbol;
    internal bool IsUndefinedSymbol(Symbol symbol) => symbol == program.Symbols.UndefinedSymbol;
    internal bool IsUnknownSymbol(Symbol symbol) => symbol == program.Symbols.UnknownSymbol;

    internal async ValueTask<Type> GetCompletionParameterTypeAsync(Signature signature, int position, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(null, cancellation);
        var type = await Parameters.AtAsync(signature, position, cancellation);
        return type is IndexType { Target: TypeParameter { IsThisType: true } target }
            && await Instantiation.Constraints.BaseConstraintAsync(target, cancellation) is { } constraint
            ? await Keys.GetAsync(constraint, cancellation: cancellation) : type;
    }

    internal async ValueTask<Type> GetCompletionAccessTypeAsync(SyntaxNode access, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(access, cancellation);
        if (access is ImportTypeNode) return await Nodes.FromNodeAsync(access, cancellation);
        var expression = access is PropertyAccessExpressionNode property ? property.Expression! : ((QualifiedNameNode)access).Left!;
        return await Widening.GetAsync(await Expressions.CheckAsync(expression, cancellation: cancellation), cancellation);
    }

    internal async ValueTask<Type> GetWidenedMemberTypeAsync(Symbol symbol, SyntaxNode location, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(location, cancellation);
        return await Widening.GetAsync(await TypeOfSymbolAtLocationAsync(symbol, location, cancellation), cancellation);
    }

    internal async ValueTask<bool> MemberNeedsOverrideAsync(SyntaxNode owner, SyntaxNode member, Symbol symbol, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(owner, cancellation);
        if (program.Symbols.Program.Configuration.Options.NoImplicitOverride != true || (owner.Flags & NodeFlags.Ambient) != 0
            || SemanticSyntax.HasModifier(member, SyntaxKind.OverrideKeyword) || program.Symbols.Declaration(owner) is not { } classSymbol
            || await Declared.GetAsync(classSymbol, cancellation) is not InterfaceType type || ClassBases.BaseNode(type) is null) return false;
        var bases = await Bases.GetAsync(type, cancellation);
        if (bases.Count == 0) return false;
        bool isStatic = SemanticSyntax.IsStatic(member);
        var thisType = isStatic ? await Values.GetAsync(classSymbol, cancellation) : await Bases.WithThisAsync(type, null, cancellation: cancellation);
        var baseType = isStatic ? await ClassBases.ConstructorAsync(type, cancellation) : await Bases.WithThisAsync(bases[0], type.ThisType, cancellation: cancellation);
        return await Properties.PropertyAsync(thisType, symbol.Name, cancellation: cancellation) is not null
            && await Properties.PropertyAsync(baseType, symbol.Name, cancellation: cancellation) is { Declarations.Length: > 0 } inherited
            && (!inherited.Declarations.Any(declaration => SemanticSyntax.HasModifier(declaration, SyntaxKind.AbstractKeyword))
                || SemanticSyntax.HasModifier(member, SyntaxKind.AbstractKeyword));
    }

    internal async ValueTask<Type?> GetObjectMethodCompletionTypeAsync(Symbol symbol, SyntaxNode location, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(location, cancellation);
        var type = await Widening.GetAsync(await TypeOfSymbolAtLocationAsync(symbol, location, cancellation), cancellation);
        if (type is UnionType { Types.Count: < 10 } union) type = await Algebra.UnionAsync(union.Types, UnionReduction.Subtype, cancellation: cancellation);
        if (type is UnionType parts)
        {
            Type? function = null;
            foreach (var part in parts.Types)
            {
                if ((await SignaturesAsync(part, false, cancellation)).Count == 0) continue;
                if (function is not null) return null;
                function = part;
            }
            if (function is null) return null;
            type = function;
        }
        return (await SignaturesAsync(type, false, cancellation)).Count == 1 ? type : null;
    }

    internal async ValueTask<IReadOnlyList<Symbol>> GetCompletionJsxTagsAsync(SyntaxNode location, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(location, cancellation);
        return await Properties.GetAsync(await JsxTypeAsync(Utf8Literals.IntrinsicElements, location, cancellation), cancellation);
    }

    internal async ValueTask<Type?> GetCompletionPromiseTypeAsync(Type type, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(null, cancellation);
        return await Awaited.OfPromiseAsync(type, cancellation: cancellation);
    }

    internal async ValueTask<IReadOnlyList<LiteralType>> GetStringCompletionTypesAsync(Type? type, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(null, cancellation);
        if (type is null) return [];
        List<LiteralType> result = [];
        HashSet<Type> seen = [];
        HashSet<Utf8String> values = [];
        Stack<Type> pending = []; pending.Push(type);
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!seen.Add(current)) continue;
            if (current is TypeParameter && await Instantiation.Constraints.BaseConstraintAsync(current, cancellation) is { } constraint) current = constraint;
            if (current is UnionType union) for (int i = union.Types.Count - 1; i >= 0; i--) pending.Push(union.Types[i]);
            else if (current is LiteralType { Value: Utf8String value } literal && (current.Flags & TypeFlags.EnumLiteral) == 0 && values.Add(value)) result.Add(literal);
        }
        return result;
    }

    internal async ValueTask<IReadOnlyList<Symbol>> GetCompletionModuleExportsAsync(Symbol symbol, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(null, cancellation);
        List<Symbol> result = [.. VisibleSymbols(await program.ModuleExports.ResolveAsync(symbol, cancellation))];
        var exported = await program.AliasTargets.ExternalModuleAsync(symbol, false, cancellation);
        if (exported is not null && exported != symbol)
        {
            var type = await Values.GetAsync(exported, cancellation);
            if ((type.Flags & TypeFlags.Primitive) == 0 || (type.ObjectFlags & ObjectFlags.Class) != 0
                || Instantiation.IsArrayType(type) || type is TypeReference { Target: TupleType }) result.AddRange(await Properties.GetAsync(type, cancellation));
        }
        return result;
    }

    internal async ValueTask<IReadOnlyList<(Symbol Symbol, bool Exported)>> GetLocalExportCompletionsAsync(SyntaxNode container, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(container, cancellation);
        var locals = program.Symbols.Binding(container)?.Get(container)?.Locals;
        var exports = program.Symbols.Declaration(container)?.Exports;
        return locals is null ? [] : locals.Select(pair => (pair.Value, exports?.ContainsKey(pair.Key) == true)).ToArray();
    }

    internal async ValueTask<Type?> GetTypeArgumentConstraintAsync(SyntaxNode node, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(node, cancellation);
        if (!QuerySyntax.PartOfType(node) || node.Parent is not { } parent) return null;
        var arguments = parent switch
        {
            CallExpressionNode call => call.TypeArguments, NewExpressionNode call => call.TypeArguments,
            TaggedTemplateExpressionNode tag => tag.TypeArguments, JsxOpeningElementNode jsx => jsx.TypeArguments,
            JsxSelfClosingElementNode jsx => jsx.TypeArguments, _ => TypeReferences.Arguments(parent),
        };
        int index = arguments?.IndexOf(node) ?? -1;
        if (index < 0) return null;
        var invocation = parent.Parent is DecoratorNode decorator ? decorator : parent;
        var expression = invocation switch
        {
            CallExpressionNode call => call.Expression, NewExpressionNode call => call.Expression, DecoratorNode dec => dec.Expression,
            TaggedTemplateExpressionNode tag => tag.Tag, JsxOpeningElementNode jsx => jsx.TagName,
            JsxSelfClosingElementNode jsx => jsx.TagName, _ => null,
        };
        if (expression is not null)
        {
            if (invocation is JsxOpeningElementNode or JsxSelfClosingElementNode && IntrinsicJsx(expression)) return context.NeverType;
            return await AcrossSignaturesAsync(await SignaturesAsync(await Expressions.CheckAsync(expression, cancellation: cancellation), invocation is NewExpressionNode, cancellation));
        }
        if (parent is ExpressionWithTypeArgumentsNode { Parent: ExpressionStatementNode, Expression: { } callee })
        {
            var type = await Expressions.CheckAsync(callee, cancellation: cancellation);
            var call = await AcrossSignaturesAsync(await SignaturesAsync(type, false, cancellation));
            var construct = await AcrossSignaturesAsync(await SignaturesAsync(type, true, cancellation));
            return (construct.Flags & TypeFlags.Never) != 0 ? call : (call.Flags & TypeFlags.Never) != 0 ? construct
                : await Algebra.IntersectionAsync([call, construct], cancellation: cancellation);
        }
        if (parent is not (TypeReferenceNode or ImportTypeNode or ExpressionWithTypeArgumentsNode)) return null;
        var reference = await Nodes.FromNodeAsync(parent, cancellation);
        if (reference == context.ErrorType || links.SymbolNodes.TryGet(parent)?.ResolvedSymbol is not { } symbol) return null;
        IReadOnlyList<Type> parameters = (symbol.Flags & SymbolFlags.TypeAlias) != 0 ? program.Scopes.Local(symbol, cancellation) : [];
        if (parameters.Count == 0 && reference is TypeReference { Target: InterfaceType target })
            parameters = target.AllTypeParameters.Skip(target.OuterTypeParameterCount).Take(target.AllTypeParameters.Count - target.OuterTypeParameterCount - (target.ThisType is null ? 0 : 1)).ToArray();
        if (index >= parameters.Count || await Instantiation.Constraints.ConstraintAsync(parameters[index], cancellation) is not { } constraint) return null;
        var effective = await References.EffectiveArgumentsAsync(parent, parameters.Cast<TypeParameter>().ToArray(), cancellation);
        return await Instantiation.Engine.InstantiateAsync(constraint, TypeMapper.Create(parameters.ToArray(), effective.ToArray()), cancellation: cancellation);

        async ValueTask<Type> AcrossSignaturesAsync(IReadOnlyList<Signature> signatures)
        {
            List<Type> constraints = [];
            foreach (var signature in signatures)
                if (index < signature.TypeParameters.Count && await Instantiation.Constraints.ConstraintAsync(signature.TypeParameters[index], cancellation) is { } value) constraints.Add(value);
            return await Algebra.UnionAsync(constraints, cancellation: cancellation);
        }
    }

    internal async ValueTask<SymbolFlags> GetCompletionSymbolFlagsAsync(Symbol symbol, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(null, cancellation);
        return await program.Aliases.FlagsAsync(symbol, cancellation: cancellation);
    }

    internal async ValueTask<bool> IsDeprecatedCompletionAsync(Symbol symbol, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(null, cancellation);
        if ((symbol.Flags & SymbolFlags.Alias) != 0) symbol = await program.Aliases.ResolveAsync(symbol, cancellation);
        if (symbol.Declarations.Length == 0) return false;
        foreach (var declaration in symbol.Declarations)
        {
            for (var node = declaration; node is not null; node = node.Parent)
                if ((node.Flags & NodeFlags.PossiblyContainsDeprecatedTag) != 0 && SemanticSyntax.Source(node) is { } file)
                    await file.GetDocumentationAsync(node, cancellation);
            if (!program.Deprecations.Declaration(declaration)) return false;
        }
        return true;
    }

    internal async ValueTask<bool> IsCompletionPropertyAccessibleAsync(SyntaxNode node, Type type, Symbol property, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(node, cancellation);
        if ((type.Flags & TypeFlags.Any) != 0) return true;
        if (property.ValueDeclaration?.DeclarationName is PrivateIdentifierNode)
        {
            var containing = DeclarationOrder.ContainingClass(property.ValueDeclaration);
            return (node.Flags & NodeFlags.OptionalChain) == 0 && containing is not null
                && DeclarationOrder.Ancestor(node, candidate => candidate == containing) is not null;
        }
        return await MemberAccessibility.CheckAsync(node, node is PropertyAccessExpressionNode { Expression.Kind: SyntaxKind.SuperKeyword },
            false, type, property, false, cancellation);
    }

    internal async ValueTask<IReadOnlyList<Symbol>> GetPossibleCompletionPropertiesAsync(Type type, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(null, cancellation);
        return await PossibleCompletionPropertiesAsync(type, cancellation);
    }

    private async ValueTask<IReadOnlyList<Symbol>> PossibleCompletionPropertiesAsync(Type type, CancellationToken cancellation)
    {
        if (type is not UnionType union) return await GetApparentPropertiesAsync(type, cancellation);
        List<Symbol> result = [];
        HashSet<Utf8String> names = [];
        foreach (var part in union.Types)
            foreach (var property in await GetApparentPropertiesAsync(part, cancellation))
                if (names.Add(property.Name) && await Properties.CachedPropertyAsync(union, property.Name, false, cancellation) is { } combined)
                    result.Add(combined);
        return result;
    }

    internal async ValueTask<Type?> GetCompletionThisTypeAsync(SyntaxNode node, SyntaxNode? container, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(node, cancellation);
        node = QuerySyntax.Reparsed(node);
        if ((node.Flags & (NodeFlags.JSDoc | NodeFlags.Reparsed)) == NodeFlags.JSDoc) return null;
        return await ThisExpressions.AtAsync(node, false, container is null ? null : QuerySyntax.Reparsed(container), cancellation);
    }

    internal async ValueTask<bool> IsGlobalCompletionTypeAsync(Type type, SourceFileNode file, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(file, cancellation);
        foreach (Utf8String name in new Utf8String[] { "self"u8, "global"u8, "globalThis"u8 })
            if (program.Symbols.Globals.GetValueOrDefault(name) is { } symbol && await Values.GetAsync(symbol, cancellation) == type) return true;
        return false;
    }

    internal async ValueTask<IReadOnlyList<Symbol>> GetObjectCompletionPropertiesAsync(Type contextual, Type? completions, SyntaxNode node, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(node, cancellation);
        bool distinct = completions is not null && completions != contextual;
        List<Type> types = [];
        foreach (var type in contextual is UnionType union ? union.Types : [contextual])
            if (await Awaited.OfPromiseAsync(type, cancellation: cancellation) is null) types.Add(type);
        var filtered = await Algebra.UnionAsync(types, cancellation: cancellation);
        if (distinct && (completions!.Flags & TypeFlags.AnyOrUnknown) == 0)
            filtered = await Algebra.UnionAsync([filtered, completions], cancellation: cancellation);
        if (filtered is UnionType composite)
        {
            types.Clear();
            foreach (var type in composite.Types)
            {
                if ((type.Flags & TypeFlags.Primitive) != 0 || await ArrayLikeAsync(type, cancellation)
                    || await InvalidDiscriminantAsync(type, node, cancellation)
                    || (await SignaturesAsync(type, false, cancellation)).Count != 0 || (await SignaturesAsync(type, true, cancellation)).Count != 0
                    || (type.ObjectFlags & ObjectFlags.Class) != 0 && NonPublic(await GetApparentPropertiesAsync(type, cancellation))) continue;
                types.Add(type);
            }
            filtered = await Algebra.UnionAsync(types, cancellation: cancellation);
        }
        var properties = await PossibleCompletionPropertiesAsync(filtered, cancellation);
        if ((filtered.ObjectFlags & ObjectFlags.Class) != 0 && NonPublic(properties)) return [];
        return distinct ? properties.Where(property => property.Declarations.Length == 0 || property.Declarations.Any(declaration => declaration.Parent != node)).ToArray() : properties;

        bool NonPublic(IReadOnlyList<Symbol> properties) => properties.Any(property => (AccessFlags(property, false) & (CheckFlags.ContainsPrivate | CheckFlags.ContainsProtected)) != 0);
    }
}
