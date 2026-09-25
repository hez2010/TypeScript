using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IBindingPatternHost
{
    int TargetYear { get; }
    Type AnyArray { get; }

    ValueTask<Type> LiteralNameTypeAsync(SyntaxNode name, CancellationToken cancellation);

    ValueTask<Type> PatternInitializerAsync(BindingElementNode element, Type contextual, CancellationToken cancellation);

    ValueTask<Type> IterableOfAsync(Type element, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation);

    ValueTask ReportImplicitAnyAsync(SyntaxNode declaration, Type type, CancellationToken cancellation);
}

internal sealed class BindingPatterns(TypeContext context, CheckerLinks links, TypeAlgebra algebra, TypeWidening widening,
    TypeProperties properties, TupleTypes tuples, Dictionary<Type, SyntaxNode> patterns, IBindingPatternHost host)
{
    private readonly List<SyntaxNode> active = [];

    internal bool Contains(SyntaxNode node) => active.Contains(node);

    internal int ActiveCount => active.Count;

    internal async ValueTask<Type> GetAsync(BindingPatternNode pattern, bool includePattern = false, bool reportErrors = false,
        CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (includePattern)
            active.Add(pattern);
        try
        {
            return pattern.Kind == SyntaxKind.ObjectBindingPattern
                ? await ObjectAsync(pattern, includePattern, reportErrors, cancellation).ConfigureAwait(false)
                : await ArrayAsync(pattern, includePattern, reportErrors, cancellation).ConfigureAwait(false);
        }
        finally
        {
            if (includePattern)
                active.RemoveAt(active.Count - 1);
        }
    }

    private async ValueTask<Type> ObjectAsync(
        BindingPatternNode pattern,
        bool includePattern,
        bool reportErrors,
        CancellationToken cancellation)
    {
        var members = new Dictionary<string, Symbol>(StringComparer.Ordinal);
        IndexInfo? stringIndex = null;
        var flags = ObjectFlags.ObjectLiteral | ObjectFlags.ContainsObjectOrArrayLiteral;
        foreach (BindingElementNode element in pattern.Elements!)
        {
            if (element.DotDotDotToken is not null)
            {
                stringIndex = context.NewIndexInfo(context.StringType, context.AnyType);
                continue;
            }
            var name = await host.LiteralNameTypeAsync(element.PropertyName ?? element.Name!, cancellation).ConfigureAwait(false);
            if ((name.Flags & TypeFlags.StringOrNumberLiteralOrUnique) == 0)
            {
                flags |= ObjectFlags.ObjectLiteralPatternWithComputedProperties;
                continue;
            }
            var symbol = new Symbol(SymbolFlags.Property | SymbolFlags.Transient
                | (element.Initializer is not null ? SymbolFlags.Optional : 0), MappedMembers.PropertyName(name));
            links.Values.Get(symbol).ResolvedType = await ElementAsync(
                element,
                includePattern,
                reportErrors,
                cancellation).ConfigureAwait(false);
            members[symbol.Name] = symbol;
        }
        var result = context.NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved | flags);
        result.Members = members.AsReadOnly();
        result.Properties = members.Values.Order(algebra.Order).ToArray();
        result.CallSignatures = result.ConstructSignatures = [];
        result.IndexInfos = stringIndex is null ? [] : [stringIndex];
        if (includePattern)
            patterns[result] = pattern;
        return result;
    }

    private async ValueTask<Type> ArrayAsync(
        BindingPatternNode pattern,
        bool includePattern,
        bool reportErrors,
        CancellationToken cancellation)
    {
        var elements = pattern.Elements!;
        var rest = elements.LastOrDefault() is BindingElementNode { DotDotDotToken: not null } last ? last : null;
        if (elements.Count == 0 || elements.Count == 1 && rest is not null)
            return host.TargetYear >= 2015
                ? await host.IterableOfAsync(context.AnyType, cancellation).ConfigureAwait(false)
                : host.AnyArray;
        int minLength = 0;
        for (int i = 0; i < elements.Count; i++)
            if (elements[i] != rest && elements[i] is BindingElementNode { Initializer: null, Name: not null })
                minLength = i + 1;
        var types = new Type[elements.Count];
        var infos = new TupleElementInfo[elements.Count];
        for (int i = 0; i < elements.Count; i++)
        {
            types[i] = elements[i] is BindingElementNode { Name: not null } element
                ? await ElementAsync(element, includePattern, reportErrors, cancellation).ConfigureAwait(false) : context.AnyType;
            infos[i] = new(elements[i] == rest ? ElementFlags.Rest : i >= minLength ? ElementFlags.Optional : ElementFlags.Required);
        }
        var result = await tuples.CreateAsync(types, infos, cancellation: cancellation).ConfigureAwait(false);
        if (includePattern)
        {
            result = context.CloneTypeReference((TypeReference)result);
            patterns[result] = pattern;
            result.ObjectFlags |= ObjectFlags.ContainsObjectOrArrayLiteral;
        }
        return result;
    }

    internal async ValueTask<Type> ElementAsync(BindingElementNode element, bool includePattern, bool reportErrors,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (element.Initializer is not null)
        {
            var contextual = element.Name is BindingPatternNode pattern
                ? await GetAsync(pattern, true, false, cancellation).ConfigureAwait(false) : context.UnknownType;
            var type = await host.PatternInitializerAsync(element, contextual, cancellation).ConfigureAwait(false);
            if (!VariableTypes.Constant(element) && !VariableTypes.Readonly(element))
                type = await widening.LiteralAsync(type, cancellation).ConfigureAwait(false);
            return context.StrictNullChecks
                ? await algebra.UnionAsync([type, context.UndefinedType], cancellation: cancellation).ConfigureAwait(false) : type;
        }
        if (element.Name is BindingPatternNode nested)
            return await GetAsync(nested, includePattern, reportErrors, cancellation).ConfigureAwait(false);
        var root = SemanticSyntax.RootDeclaration(element);
        if (root is ParameterDeclarationNode)
            root = root.Parent!;
        bool privateAmbient = (root.Flags & NodeFlags.Ambient) != 0
            && (SemanticSyntax.HasModifier(root, SyntaxKind.PrivateKeyword) || root is INamedNode { Name: PrivateIdentifierNode });
        if (reportErrors && !privateAmbient)
            await host.ReportImplicitAnyAsync(element, context.AnyType, cancellation).ConfigureAwait(false);
        return includePattern ? context.NonInferrableAnyType : context.AnyType;
    }

    internal async ValueTask<Type> PadAsync(Type type, BindingPatternNode pattern, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (pattern.Kind == SyntaxKind.ObjectBindingPattern && (type.ObjectFlags & ObjectFlags.ObjectLiteral) != 0)
        {
            var missing = new List<(BindingElementNode Element, string Name)>();
            foreach (BindingElementNode element in pattern.Elements!)
                if (element.Initializer is not null)
                {
                    var name = await host.LiteralNameTypeAsync(element.PropertyName ?? element.Name!, cancellation).ConfigureAwait(false);
                    if ((name.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0
                        && await properties.PropertyAsync(
                            type,
                            MappedMembers.PropertyName(name),
                            cancellation: cancellation).ConfigureAwait(false) is null)
                        missing.Add((element, MappedMembers.PropertyName(name)));
                }
            if (missing.Count == 0)
                return type;
            var members = (await properties.GetAsync(
                type,
                cancellation).ConfigureAwait(false)).ToDictionary(p => p.Name, StringComparer.Ordinal);
            foreach (var (element, name) in missing)
            {
                var symbol = new Symbol(SymbolFlags.Property | SymbolFlags.Optional | SymbolFlags.Transient, name);
                links.Values.Get(symbol).ResolvedType = await ElementAsync(element, false, false, cancellation).ConfigureAwait(false);
                members[name] = symbol;
            }
            var result = context.NewObjectType(ObjectFlags.Anonymous, type.Symbol);
            result.Members = members.AsReadOnly();
            result.Properties = members.Values.Order(algebra.Order).ToArray();
            result.CallSignatures = result.ConstructSignatures = [];
            result.IndexInfos = await host.IndexesAsync(type, cancellation).ConfigureAwait(false);
            result.ObjectFlags = type.ObjectFlags;
            return result;
        }
        if (pattern.Kind == SyntaxKind.ArrayBindingPattern && type is TypeReference { Target: TupleType tuple } reference)
        {
            var types = (await host.TypeArgumentsAsync(reference, cancellation).ConfigureAwait(false)).ToList();
            if ((tuple.CombinedFlags & ElementFlags.Variable) != 0 || types.Count >= pattern.Elements!.Count)
                return type;
            var infos = tuple.ElementInfos.ToList();
            for (int i = types.Count; i < pattern.Elements.Count; i++)
            {
                var element = pattern.Elements[i] as BindingElementNode;
                if (i == pattern.Elements.Count - 1 && element?.DotDotDotToken is not null)
                    continue;
                types.Add(element?.Initializer is not null
                    ? await ElementAsync(element, false, false, cancellation).ConfigureAwait(false) : context.AnyType);
                infos.Add(new(ElementFlags.Optional));
                if (element is { Initializer: null })
                    await host.ReportImplicitAnyAsync(element, context.AnyType, cancellation).ConfigureAwait(false);
            }
            return await tuples.CreateAsync(types, infos, tuple.IsReadonly, cancellation).ConfigureAwait(false);
        }
        return type;
    }
}
