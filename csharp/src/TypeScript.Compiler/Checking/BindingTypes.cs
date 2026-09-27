using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IBindingTypeHost
{
    bool NoUncheckedIndexedAccess { get; }

    ValueTask<Type> InitialVariableAsync(VariableDeclarationNode variable, CancellationToken cancellation);

    ValueTask<Type> DeclarationInitializerAsync(SyntaxNode declaration, CheckMode mode, CancellationToken cancellation);

    ValueTask<Type> InitializerTypeAsync(SyntaxNode expression, CancellationToken cancellation);

    ValueTask<Type> LiteralNameTypeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> NonUndefinedAsync(Type type, CancellationToken cancellation);

    ValueTask<Type> BindingIterationAsync(Type type, SyntaxNode? pattern, bool possiblyOutOfBounds, CancellationToken cancellation);

    ValueTask<bool> ArrayLikeAsync(Type type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation);

    ValueTask<IndexInfo?> ApplicableIndexAsync(Type type, string name, CancellationToken cancellation);

    ValueTask<Type> OmitAsync(Type source, Type keys, CancellationToken cancellation);

    CheckFlags AccessFlags(Symbol symbol, bool write);

    bool IsReadonly(Symbol symbol);

    FlowNode? FlowOf(SyntaxNode node);

    void BindingError(SyntaxNode node, DiagnosticCode code);
}

internal sealed class BindingTypes(TypeContext context, CheckerLinks links, CheckerSymbols symbols,
    TypeAlgebra algebra, TypeFactQueries facts, TypeViews views, TypeConstraints constraints, MappedTypes mapped,
    TypeProperties properties, SymbolTypes values, TypeKeys keys, TypeRelations relations, IndexedTypes indexed,
    TupleTypes tuples, VariableTypes variables, AccessNames names, FlowTypes flows, IBindingTypeHost host)
{
    private readonly ConditionalWeakTable<SyntaxNode, FlowNode> syntheticFlows = new();

    internal FlowNode? SyntheticFlow(SyntaxNode node) => syntheticFlows.TryGetValue(node, out var flow) ? flow : null;

    internal async ValueTask<Type?> GetAsync(BindingElementNode element, CancellationToken cancellation = default)
    {
        var parent = await ParentAsync(element.Parent!.Parent!,
            element.DotDotDotToken is null ? CheckMode.Normal : CheckMode.RestBindingElement, cancellation).ConfigureAwait(false);
        return parent is null ? null : await FromParentAsync(element, parent, false, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type> InitialAsync(BindingElementNode element, CancellationToken cancellation = default)
    {
        var pending = new Stack<BindingElementNode>();
        SyntaxNode node = element;
        while (node is BindingElementNode binding)
        {
            cancellation.ThrowIfCancellationRequested();
            pending.Push(binding);
            node = binding.Parent!.Parent!;
        }
        var type = await host.InitialVariableAsync((VariableDeclarationNode)node, cancellation).ConfigureAwait(false);
        while (pending.TryPop(out var binding))
        {
            var pattern = (BindingPatternNode)binding.Parent!;
            if (pattern.Kind == SyntaxKind.ObjectBindingPattern)
                type = await DestructuredPropertyAsync(type, binding.PropertyName ?? binding.Name!, cancellation).ConfigureAwait(false);
            else if (binding.DotDotDotToken is null)
                type = await InitialArrayElementAsync(
                    type,
                    pattern.Elements!.IndexOf(binding),
                    cancellation).ConfigureAwait(false);
            else
                type = await tuples.ArrayAsync(await host.BindingIterationAsync(type, null, false, cancellation).ConfigureAwait(false),
                    cancellation: cancellation).ConfigureAwait(false);
            if (binding.Initializer is { } initializer)
                type = await algebra.UnionAsync([await host.NonUndefinedAsync(type, cancellation).ConfigureAwait(false),
                    await host.InitializerTypeAsync(initializer, cancellation).ConfigureAwait(false)],
                    cancellation: cancellation).ConfigureAwait(false);
        }
        return type;
    }

    internal async ValueTask<Type> InitialArrayElementAsync(Type type, int index, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        bool tupleLike = true;
        foreach (var part in type is UnionType union ? union.Types : [type])
        {
            if (part is TypeReference { Target: TupleType }
                || await properties.PropertyAsync(part, "0", cancellation: cancellation).ConfigureAwait(false) is not null)
                continue;
            var length = await host.ArrayLikeAsync(part, cancellation).ConfigureAwait(false)
                ? await properties.PropertyAsync(part, "length", cancellation: cancellation).ConfigureAwait(false) : null;
            if (length is not null)
            {
                var lengthType = await values.GetAsync(length, cancellation).ConfigureAwait(false);
                if ((lengthType is UnionType lengths ? lengths.Types : [lengthType]).All(t => (t.Flags & TypeFlags.NumberLiteral) != 0))
                    continue;
            }
            tupleLike = false;
            break;
        }
        if (tupleLike)
        {
            if (await properties.PropertyAsync(type, index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                cancellation: cancellation).ConfigureAwait(false) is { } property)
                return await values.GetAsync(property, cancellation).ConfigureAwait(false);
            if ((type is UnionType union ? union.Types : [type]).All(t => t is TypeReference { Target: TupleType }))
                return (await algebra.MapAsync(type, async part =>
                {
                    var tuple = (TupleType)((TypeReference)part).Target!;
                    var rest = await tuples.SliceElementAsync(
                        (TypeReference)part,
                        tuple.FixedLength,
                        cancellation: cancellation).ConfigureAwait(false);
                    int fixedCount = tuple.ElementInfos.Count(i => (i.Flags & ElementFlags.Fixed) != 0);
                    return rest is null ? context.UndefinedType : host.NoUncheckedIndexedAccess && index >= fixedCount
                        ? await algebra.UnionAsync([rest, context.UndefinedType], cancellation: cancellation).ConfigureAwait(false) : rest;
                }, cancellation: cancellation).ConfigureAwait(false))!;
        }
        var element = await host.BindingIterationAsync(type, null, false, cancellation).ConfigureAwait(false);
        return host.NoUncheckedIndexedAccess
            ? await algebra.UnionAsync([element, context.MissingType], cancellation: cancellation).ConfigureAwait(false) : element;
    }

    internal async ValueTask<Type> DestructuredPropertyAsync(Type type, SyntaxNode name, CancellationToken cancellation = default)
    {
        var key = await host.LiteralNameTypeAsync(name, cancellation).ConfigureAwait(false);
        if ((key.Flags & TypeFlags.StringOrNumberLiteralOrUnique) == 0)
            return context.ErrorType;
        var text = MappedMembers.PropertyName(key);
        if (await properties.PropertyAsync(type, text, cancellation: cancellation).ConfigureAwait(false) is { } property)
            return await values.GetAsync(property, cancellation).ConfigureAwait(false);
        if (await host.ApplicableIndexAsync(type, text, cancellation).ConfigureAwait(false) is { } index)
            return host.NoUncheckedIndexedAccess
                ? await algebra.UnionAsync(
                    [index.ValueType, context.MissingType],
                    cancellation: cancellation).ConfigureAwait(false) : index.ValueType;
        return context.ErrorType;
    }

    internal ValueTask<Type?> ParentAsync(SyntaxNode node, CheckMode mode = 0, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (mode == 0 && symbols.Declaration(node) is { } symbol
            && links.Values.Get(symbol).ResolvedType is { } type && !(context.StrictNullChecks && VariableTypes.Optional(node)))
            return ValueTask.FromResult<Type?>(type);
        return variables.DeclaredOrInferredAsync(node, false, mode, cancellation);
    }

    internal async ValueTask<Type> FromParentAsync(BindingElementNode element, Type parent, bool noTupleBoundsCheck = false,
        CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(parent);
        if ((parent.Flags & TypeFlags.Any) != 0)
            return parent;
        var pattern = (BindingPatternNode)element.Parent!;
        if (context.StrictNullChecks && (element.Flags & NodeFlags.Ambient) != 0
            && SemanticSyntax.RootDeclaration(element) is ParameterDeclarationNode)
            parent = await facts.NonNullableAsync(parent, cancellation).ConfigureAwait(false);
        else if (context.StrictNullChecks && pattern.Parent is IInitializedNode { Initializer: { } initializer }
            && await facts.GetAsync(await host.InitializerTypeAsync(initializer, cancellation).ConfigureAwait(false),
                TypeFacts.EQUndefined, cancellation).ConfigureAwait(false) == 0)
            parent = await facts.FilterAsync(parent, TypeFacts.NEUndefined, cancellation).ConfigureAwait(false);
        var flags = AccessFlags.ExpressionPosition
            | (noTupleBoundsCheck || element.Initializer is not null ? AccessFlags.AllowMissing : 0);
        Type result;
        if (pattern.Kind == SyntaxKind.ObjectBindingPattern)
        {
            if (element.DotDotDotToken is not null)
            {
                parent = await views.ReducedAsync(parent, cancellation).ConfigureAwait(false);
                if ((parent.Flags & TypeFlags.Unknown) != 0 || !await ValidSpreadAsync(parent, cancellation).ConfigureAwait(false))
                {
                    host.BindingError(element, DiagnosticCode.RestTypesMayOnlyBeCreatedFromObjectTypes);
                    return context.ErrorType;
                }
                var excluded = pattern.Elements!.OfType<BindingElementNode>().Where(e => e.DotDotDotToken is null)
                    .Select(e => e.PropertyName ?? e.Name!).ToArray();
                result = await RestAsync(parent, excluded, symbols.Declaration(element), cancellation).ConfigureAwait(false);
            }
            else
            {
                var name = element.PropertyName ?? element.Name!;
                var index = await host.LiteralNameTypeAsync(name, cancellation).ConfigureAwait(false);
                var declared = await indexed.GetAsync(parent, index, flags, name, cancellation: cancellation).ConfigureAwait(false);
                result = await DestructuringFlowAsync(element, declared, cancellation).ConfigureAwait(false);
            }
        }
        else if (pattern.Kind == SyntaxKind.ArrayBindingPattern)
        {
            var iterated = await host.BindingIterationAsync(
                parent,
                pattern,
                element.DotDotDotToken is null,
                cancellation).ConfigureAwait(false);
            int position = pattern.Elements!.IndexOf(element);
            if (element.DotDotDotToken is not null)
            {
                var baseType = (await algebra.MapAsync(parent, async part => (part.Flags & TypeFlags.InstantiableNonPrimitive) != 0
                    ? await constraints.BaseConstraintOrTypeAsync(part, cancellation).ConfigureAwait(false) : part,
                    cancellation: cancellation).ConfigureAwait(false))!;
                result = (baseType is UnionType union ? union.Types : [baseType]).All(t => t is TypeReference { Target: TupleType })
                    ? (await algebra.MapAsync(baseType, async part => await tuples.SliceAsync((TypeReference)part, position,
                        cancellation: cancellation).ConfigureAwait(false), cancellation: cancellation).ConfigureAwait(false))!
                    : await tuples.ArrayAsync(iterated, cancellation: cancellation).ConfigureAwait(false);
            }
            else if (await host.ArrayLikeAsync(parent, cancellation).ConfigureAwait(false))
            {
                var declared = await indexed.TryGetAsync(parent, context.GetNumberLiteralType(position), flags, element.Name,
                    cancellation: cancellation).ConfigureAwait(false) ?? context.ErrorType;
                result = await DestructuringFlowAsync(element, declared, cancellation).ConfigureAwait(false);
            }
            else
                result = iterated;
        }
        else
            throw new InvalidOperationException("Binding element must belong to an object or array pattern");
        if (element.Initializer is null)
            return result;
        if (SemanticSyntax.RootDeclaration(element) is ITypedNode { Type: not null })
            return context.StrictNullChecks && await facts.GetAsync(
                await host.DeclarationInitializerAsync(element, 0, cancellation).ConfigureAwait(false),
                TypeFacts.IsUndefined,
                cancellation).ConfigureAwait(false) == 0
                ? await host.NonUndefinedAsync(result, cancellation).ConfigureAwait(false) : result;
        var defaultType = await host.DeclarationInitializerAsync(element, 0, cancellation).ConfigureAwait(false);
        return await variables.WidenInitializerAsync(element,
            await algebra.UnionAsync([await host.NonUndefinedAsync(result, cancellation).ConfigureAwait(false), defaultType],
                UnionReduction.Subtype, cancellation: cancellation).ConfigureAwait(false), cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<bool> ValidSpreadAsync(Type type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        var constraint = (await algebra.MapAsync(type, async part => await constraints.BaseConstraintOrTypeAsync(part, cancellation)
            .ConfigureAwait(false), cancellation: cancellation).ConfigureAwait(false))!;
        var result = await facts.FilterAsync(constraint, TypeFacts.Truthy, cancellation).ConfigureAwait(false);
        if ((result.Flags & (TypeFlags.Any | TypeFlags.NonPrimitive | TypeFlags.Object | TypeFlags.InstantiableNonPrimitive)) != 0)
            return true;
        if (result is not UnionOrIntersectionType composite)
            return false;
        foreach (var part in composite.Types)
            if (!await ValidSpreadAsync(part, cancellation).ConfigureAwait(false))
                return false;
        return true;
    }

    internal async ValueTask<Type> RestAsync(Type source, IReadOnlyList<SyntaxNode> excluded, Symbol? symbol,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(source);
        source = algebra.Filter(source, t => (t.Flags & TypeFlags.Nullable) == 0);
        if ((source.Flags & TypeFlags.Never) != 0)
            return context.EmptyObjectType;
        if (source is UnionType)
            return (await algebra.MapAsync(
                source,
                async part => await RestAsync(part, excluded, symbol, cancellation).ConfigureAwait(false),
                cancellation: cancellation).ConfigureAwait(false))!;
        var keyTypes = new List<Type>();
        foreach (var name in excluded)
            keyTypes.Add(await host.LiteralNameTypeAsync(name, cancellation).ConfigureAwait(false));
        var omit = await algebra.UnionAsync(keyTypes, cancellation: cancellation).ConfigureAwait(false);
        var spreadable = new List<Symbol>();
        var unspreadable = new List<Type>();
        foreach (var property in await properties.GetAsync(source, cancellation).ConfigureAwait(false))
        {
            var key = await keys.PropertyAsync(
                property,
                TypeFlags.StringOrNumberLiteralOrUnique,
                false,
                cancellation).ConfigureAwait(false);
            if (!await relations.RelatedAsync(key, omit, RelationKind.Assignable, cancellation).ConfigureAwait(false)
                && (host.AccessFlags(property, false) & (CheckFlags.ContainsPrivate | CheckFlags.ContainsProtected)) == 0
                && Spreadable(property))
                spreadable.Add(property);
            else
                unspreadable.Add(key);
        }
        if ((await mapped.GenericFlagsAsync(source, cancellation).ConfigureAwait(false) & ObjectFlags.IsGenericObjectType) != 0
            || (await mapped.GenericFlagsAsync(omit, cancellation).ConfigureAwait(false) & ObjectFlags.IsGenericIndexType) != 0)
        {
            if (unspreadable.Count != 0)
                omit = await algebra.UnionAsync([omit, .. unspreadable], cancellation: cancellation).ConfigureAwait(false);
            return (omit.Flags & TypeFlags.Never) != 0 ? source : await host.OmitAsync(source, omit, cancellation).ConfigureAwait(false);
        }
        var members = new Dictionary<string, Symbol>(StringComparer.Ordinal);
        foreach (var property in spreadable)
            members[property.Name] = await SpreadSymbolAsync(property, false, cancellation).ConfigureAwait(false);
        var result = context.NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved | ObjectFlags.ObjectRestType, symbol);
        result.Members = members.AsReadOnly();
        result.Properties = members.Values.Order(algebra.Order).ToArray();
        result.CallSignatures = result.ConstructSignatures = [];
        result.IndexInfos = await host.IndexesAsync(source, cancellation).ConfigureAwait(false);
        return result;
    }

    internal static bool Spreadable(Symbol property) =>
        !property.Declarations.Any(d => d is INamedNode { Name: PrivateIdentifierNode })
            && (property.Flags & (SymbolFlags.Method | SymbolFlags.Accessor)) == 0
        || !property.Declarations.Any(d => d.Parent is ClassDeclarationNode or ClassExpressionNode);

    internal async ValueTask<Symbol> SpreadSymbolAsync(Symbol property, bool readOnly, CancellationToken cancellation = default)
    {
        bool setOnly = (property.Flags & SymbolFlags.SetAccessor) != 0 && (property.Flags & SymbolFlags.GetAccessor) == 0;
        if (!setOnly && readOnly == host.IsReadonly(property))
            return property;
        var result = new Symbol(SymbolFlags.Property | SymbolFlags.Transient | (property.Flags & SymbolFlags.Optional), property.Name)
        { CheckFlags = (property.CheckFlags & CheckFlags.Late) | (readOnly ? CheckFlags.Readonly : 0) };
        var data = links.Values.Get(result);
        data.ResolvedType = setOnly ? context.UndefinedType : await values.GetAsync(property, cancellation).ConfigureAwait(false);
        result.DeclarationList.AddRange(property.Declarations);
        data.NameType = links.Values.Get(property).NameType;
        links.MappedSymbols.Get(result).SyntheticOrigin = property;
        return result;
    }

    internal async ValueTask<Type> DestructuringFlowAsync(SyntaxNode node, Type declared, CancellationToken cancellation = default)
    {
        var reference = await SyntheticAccessAsync(node, cancellation).ConfigureAwait(false);
        return reference is null ? declared : await flows.GetAsync(reference, declared, cancellation: cancellation).ConfigureAwait(false);
    }

    private async ValueTask<SyntaxNode?> SyntheticAccessAsync(SyntaxNode node, CancellationToken cancellation)
    {
        // Build outer-to-inner accesses iteratively so deeply nested patterns do not consume the native stack.
        var pending = new Stack<SyntaxNode>();
        SyntaxNode? parent;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            pending.Push(node);
            var ancestor = node.Parent?.Parent;
            if (ancestor is BindingElementNode or PropertyAssignmentNode)
                node = ancestor;
            else if (ancestor is ArrayLiteralExpressionNode)
                node = node.Parent!;
            else
            {
                parent = ancestor switch
                {
                    VariableDeclarationNode variable => variable.Initializer,
                    BinaryExpressionNode binary => binary.Right,
                    _ => null
                };
                break;
            }
        }
        while (pending.TryPop(out var part))
        {
            node = part;
            if (parent is null || host.FlowOf(parent) is not { } flow
                || await names.GetAsync(node, cancellation).ConfigureAwait(false) is not { } name)
                return null;
            var literal = new StringLiteralNode { Text = name, Pos = node.Pos, End = node.End };
            SyntaxNode receiver = parent;
            if (!LeftHandSide(parent))
                receiver = new ParenthesizedExpressionNode { Expression = parent, Pos = node.Pos, End = node.End };
            var access = new ElementAccessExpressionNode
            {
                Expression = receiver,
                ArgumentExpression = literal,
                Parent = node,
                Pos = node.Pos,
                End = node.End
            };
            literal.Parent = access;
            if (receiver != parent)
                receiver.Parent = access;
            syntheticFlows.Add(access, flow);
            parent = access;
        }
        return parent;
    }

    private static bool LeftHandSide(SyntaxNode node) => node.Kind is SyntaxKind.PropertyAccessExpression
        or SyntaxKind.ElementAccessExpression
        or SyntaxKind.NewExpression or SyntaxKind.CallExpression or SyntaxKind.JsxElement or SyntaxKind.JsxSelfClosingElement
        or SyntaxKind.JsxFragment or SyntaxKind.TaggedTemplateExpression or SyntaxKind.ArrayLiteralExpression
        or SyntaxKind.ParenthesizedExpression or SyntaxKind.ObjectLiteralExpression or SyntaxKind.ClassExpression
        or SyntaxKind.FunctionExpression or SyntaxKind.Identifier or SyntaxKind.PrivateIdentifier or SyntaxKind.RegularExpressionLiteral
        or SyntaxKind.NumericLiteral or SyntaxKind.BigIntLiteral or SyntaxKind.StringLiteral or SyntaxKind.NoSubstitutionTemplateLiteral
        or SyntaxKind.TemplateExpression or SyntaxKind.FalseKeyword or SyntaxKind.NullKeyword or SyntaxKind.ThisKeyword
        or SyntaxKind.TrueKeyword or SyntaxKind.SuperKeyword or SyntaxKind.NonNullExpression or SyntaxKind.MetaProperty
        or SyntaxKind.ImportKeyword or SyntaxKind.ExpressionWithTypeArguments or SyntaxKind.MissingDeclaration;
}
