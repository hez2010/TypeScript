using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IObjectLiteralHost
{
    ValueTask<Type> CheckExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask<Type> LiteralNameTypeAsync(SyntaxNode name, CancellationToken cancellation);

    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> ObjectMethodAsync(MethodDeclarationNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask CheckLiteralAssignableAsync(Type source, Type target, SyntaxNode node, SyntaxNode expression, CancellationToken cancellation);

    ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation);

    bool ContextSensitive(SyntaxNode node);

    void DeferExpression(SyntaxNode node);

    void ExpressionError(SyntaxNode node, int code);

    ValueTask TypeExpressionErrorAsync(SyntaxNode node, int code, Type type, CancellationToken cancellation);

    void DuplicateObjectProperty(SyntaxNode node, string name);

    void SpreadOverride(SyntaxNode node, Symbol property, SyntaxNode spread);

    void LiteralGrammar(SyntaxNode node);
}

internal sealed class ObjectLiterals(TypeContext context, CheckerLinks links, CheckerSymbols symbols, TypeAlgebra algebra,
    TypeViews views, TypeProperties properties, SymbolTypes values, TypeRelations relations, TypePredicates predicates,
    TypeWidening widening, StructuredMembers members, ExpressionContexts contexts, BindingTypes bindings,
    ObjectSpreads spreads, Dictionary<Type, SyntaxNode> patterns, IObjectLiteralHost host)
{
    private readonly Dictionary<Type, Type> regular = [];
    private readonly Dictionary<SyntaxNode, string?> effectiveNames = [];

    internal async ValueTask<Type> CheckAsync(
        ObjectLiteralExpressionNode node,
        CheckMode mode = 0,
        CancellationToken cancellation = default)
    {
        var symbol = symbols.Declaration(node);
        if (node.Properties is null or { Count: 0 } && symbol?.Exports.Count > 0)
            return await spreads.ObjectAsync(symbol, new(symbol.Exports), [], JsLiteral(node) ? ObjectFlags.JSLiteral : 0,
                cancellation).ConfigureAwait(false);
        host.DeferExpression(node);
        bool destructuring = ReferenceSyntax.AssignmentTarget(node) is not null;
        await GrammarAsync(node, destructuring, cancellation).ConfigureAwait(false);
        return await contexts.CachedAsync(node, async () =>
        {
            var all = context.StrictNullChecks ? new Dictionary<string, Symbol>(StringComparer.Ordinal) : null;
            var table = new Dictionary<string, Symbol>(StringComparer.Ordinal);
            var ordered = new List<Symbol>();
            Type spreadType = context.EmptyObjectType;
            var contextual = await contexts.ApparentAsync(node, cancellation: cancellation).ConfigureAwait(false);
            bool patternContext = contextual is not null && patterns.TryGetValue(contextual, out var pattern)
                && pattern.Kind is SyntaxKind.ObjectBindingPattern or SyntaxKind.ObjectLiteralExpression;
            bool isConst = await contexts.ConstAsync(node, cancellation).ConfigureAwait(false);
            var checkFlags = isConst ? CheckFlags.Readonly : 0;
            var flags = ObjectFlags.FreshLiteral;
            bool computedPattern = false, stringKey = false, numberKey = false, symbolKey = false;
            int offset = 0;
            foreach (var declaration in node.Properties!)
                if (declaration is INamedNode { Name: ComputedPropertyNameNode computed })
                    await ComputedAsync(computed, cancellation).ConfigureAwait(false);
            foreach (var declaration in node.Properties)
            {
                cancellation.ThrowIfCancellationRequested();
                var member = symbols.Declaration(declaration);
                var computed = declaration is INamedNode { Name: ComputedPropertyNameNode computedName }
                    ? await ComputedAsync(computedName, cancellation).ConfigureAwait(false) : null;
                if (declaration is PropertyAssignmentNode or ShorthandPropertyAssignmentNode or MethodDeclarationNode)
                {
                    var type = declaration is MethodDeclarationNode method
                        ? await host.ObjectMethodAsync(method, mode, cancellation).ConfigureAwait(false)
                        : await PropertyAsync(declaration, destructuring, mode, cancellation).ConfigureAwait(false);
                    flags |= type.ObjectFlags & ObjectFlags.PropagatingFlags;
                    var nameType = computed is not null && (computed.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0
                        ? computed
                        : null;
                    var property = new Symbol(SymbolFlags.Property | SymbolFlags.Transient | member!.Flags,
                        nameType is null ? member.Name : MappedMembers.PropertyName(nameType))
                    { CheckFlags = checkFlags | (nameType is not null ? CheckFlags.Late : 0) };
                    var data = links.Values.Get(property);
                    if (nameType is not null)
                        data.NameType = nameType;
                    if (destructuring && DefaultValue(declaration))
                        property.Flags |= SymbolFlags.Optional;
                    else if (patternContext && (contextual!.ObjectFlags & ObjectFlags.ObjectLiteralPatternWithComputedProperties) == 0)
                    {
                        var implied = await properties.PropertyAsync(
                            contextual,
                            member.Name,
                            cancellation: cancellation).ConfigureAwait(false);
                        if (implied is not null)
                            property.Flags |= implied.Flags & SymbolFlags.Optional;
                        else if (!(await host.IndexesAsync(
                            contextual,
                            cancellation).ConfigureAwait(false)).Any(i => i.KeyType == context.StringType))
                            await host.TypeExpressionErrorAsync(
                                ((INamedNode)declaration).Name!,
                                2353,
                                contextual,
                                cancellation).ConfigureAwait(false);
                    }
                    property.DeclarationList.AddRange(member.Declarations);
                    property.Parent = member.Parent;
                    property.ValueDeclaration = member.ValueDeclaration;
                    data.ResolvedType = type;
                    data.Target = member;
                    member = property;
                    if (all is not null)
                        all[property.Name] = property;
                    if (contextual is not null && (mode & CheckMode.Inferential) != 0 && (mode & CheckMode.SkipContextSensitive) == 0
                        && declaration is PropertyAssignmentNode or MethodDeclarationNode && host.ContextSensitive(declaration))
                        contexts.InferenceFor(node)!.IntraExpressionSites.Add(
                            (declaration is PropertyAssignmentNode p ? p.Initializer! : declaration, type));
                }
                else if (declaration is SpreadAssignmentNode spread)
                {
                    if (ordered.Count > 0)
                    {
                        spreadType = await spreads.GetAsync(
                            spreadType,
                            await CreateAsync().ConfigureAwait(false),
                            symbol,
                            flags,
                            isConst,
                            cancellation).ConfigureAwait(false);
                        ordered.Clear();
                        table = new(StringComparer.Ordinal);
                        stringKey = numberKey = symbolKey = false;
                    }
                    var type = await views.ReducedAsync(
                        await host.CheckExpressionAsync(
                            spread.Expression!,
                            mode & CheckMode.Inferential,
                            cancellation).ConfigureAwait(false),
                        cancellation).ConfigureAwait(false);
                    if (await bindings.ValidSpreadAsync(type, cancellation).ConfigureAwait(false))
                    {
                        type = await spreads.MergeEmptyAsync(type, isConst, cancellation).ConfigureAwait(false);
                        if (all is not null)
                            foreach (var right in await properties.GetAsync(type, cancellation).ConfigureAwait(false))
                                if ((right.Flags & SymbolFlags.Optional) == 0 && (right.CheckFlags & CheckFlags.Partial) == 0
                                    && all.TryGetValue(right.Name, out var left))
                                    host.SpreadOverride(left.ValueDeclaration!, left, spread);
                        offset = ordered.Count;
                        if (spreadType != context.ErrorType && !((spreadType.Flags & TypeFlags.Any) != 0 && spreadType.Alias is not null))
                            spreadType = await spreads.GetAsync(
                                spreadType,
                                type,
                                symbol,
                                flags,
                                isConst,
                                cancellation).ConfigureAwait(false);
                    }
                    else
                    {
                        host.ExpressionError(spread, 2698);
                        spreadType = context.ErrorType;
                    }
                    continue;
                }
                else if (declaration is GetAccessorDeclarationNode or SetAccessorDeclarationNode)
                    host.DeferExpression(declaration);
                else
                    throw new InvalidOperationException("Unexpected object literal member");
                if (computed is not null && (computed.Flags & TypeFlags.StringOrNumberLiteralOrUnique) == 0)
                {
                    if (await relations.RelatedAsync(
                        computed,
                        context.StringNumberSymbolType,
                        RelationKind.Assignable,
                        cancellation).ConfigureAwait(false))
                    {
                        if (await relations.RelatedAsync(
                            computed,
                            context.NumberType,
                            RelationKind.Assignable,
                            cancellation).ConfigureAwait(false))
                            numberKey = true;
                        else if (await relations.RelatedAsync(
                            computed,
                            context.ESSymbolType,
                            RelationKind.Assignable,
                            cancellation).ConfigureAwait(false))
                            symbolKey = true;
                        else
                            stringKey = true;
                        computedPattern |= destructuring;
                    }
                }
                else
                    table[member!.Name] = member;
                ordered.Add(member!);
            }
            if (spreadType == context.ErrorType || (spreadType.Flags & TypeFlags.Any) != 0 && spreadType.Alias is not null)
                return context.ErrorType;
            if (spreadType != context.EmptyObjectType)
            {
                if (ordered.Count > 0)
                {
                    spreadType = await spreads.GetAsync(
                        spreadType,
                        await CreateAsync().ConfigureAwait(false),
                        symbol,
                        flags,
                        isConst,
                        cancellation).ConfigureAwait(false);
                    ordered.Clear();
                    table = new(StringComparer.Ordinal);
                    stringKey = numberKey = false;
                }
                return (await algebra.MapAsync(spreadType, async part => part == context.EmptyObjectType
                    ? await CreateAsync().ConfigureAwait(false) : part, cancellation: cancellation).ConfigureAwait(false))!;
            }
            return await CreateAsync().ConfigureAwait(false);

            async ValueTask<Type> CreateAsync()
            {
                var indexes = new List<IndexInfo>();
                bool readOnly = await contexts.ConstAsync(node, cancellation).ConfigureAwait(false);
                if (stringKey)
                    indexes.Add(await IndexAsync(context.StringType, ordered.Skip(offset), readOnly, cancellation).ConfigureAwait(false));
                if (numberKey)
                    indexes.Add(await IndexAsync(context.NumberType, ordered.Skip(offset), readOnly, cancellation).ConfigureAwait(false));
                if (symbolKey)
                    indexes.Add(await IndexAsync(context.ESSymbolType, ordered.Skip(offset), readOnly, cancellation).ConfigureAwait(false));
                var result = await spreads.ObjectAsync(
                    symbol,
                    table,
                    indexes,
                    flags | ObjectFlags.ObjectLiteral | ObjectFlags.ContainsObjectOrArrayLiteral, cancellation).ConfigureAwait(false);
                if (contextual is null && JsLiteral(node))
                    result.ObjectFlags |= ObjectFlags.JSLiteral;
                if (computedPattern)
                    result.ObjectFlags |= ObjectFlags.ObjectLiteralPatternWithComputedProperties;
                if (destructuring)
                    patterns[result] = node;
                return result;
            }
        }, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type> PropertyAsync(SyntaxNode node, bool destructuring, CheckMode mode, CancellationToken cancellation)
    {
        SyntaxNode expression = node is PropertyAssignmentNode property ? property.Initializer!
            : !destructuring && ((ShorthandPropertyAssignmentNode)node).ObjectAssignmentInitializer is { } initializer
                ? initializer : ((INamedNode)node).Name!;
        if (node is INamedNode { Name: ComputedPropertyNameNode computed })
            await ComputedAsync(computed, cancellation).ConfigureAwait(false);
        var type = await contexts.MutableAsync(expression, mode, cancellation).ConfigureAwait(false);
        if (node is ITypedNode { Type: { } annotation })
        {
            var target = await host.TypeFromNodeAsync(annotation, cancellation).ConfigureAwait(false);
            await host.CheckLiteralAssignableAsync(type, target, node, expression, cancellation).ConfigureAwait(false);
            return target;
        }
        return type;
    }

    internal async ValueTask<Type> ComputedAsync(ComputedPropertyNameNode node, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var data = links.TypeNodes.Get(node);
        if (data.ResolvedType is { } cached)
            return cached;
        data.ResolvedType = context.CircularConstraintType;
        try
        {
            if (node.Parent?.Parent is TypeLiteralNode or ClassDeclarationNode or ClassExpressionNode or InterfaceDeclarationNode
                && node.Expression is BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.InKeyword }
                && node.Parent is not GetAccessorDeclarationNode and not SetAccessorDeclarationNode)
                return data.ResolvedType = context.ErrorType;
            var type = await host.CheckExpressionAsync(node.Expression!, 0, cancellation).ConfigureAwait(false);
            data.ResolvedType = type;
            if ((type.Flags & TypeFlags.Nullable) != 0
                || !await predicates.AssignableAsync(type, TypeFlags.StringLike | TypeFlags.NumberLike | TypeFlags.ESSymbolLike,
                    cancellation: cancellation).ConfigureAwait(false)
                    && !await relations.RelatedAsync(
                        type,
                        context.StringNumberSymbolType,
                        RelationKind.Assignable,
                        cancellation).ConfigureAwait(false))
                host.ExpressionError(node, 2464);
            cancellation.ThrowIfCancellationRequested();
            return type;
        }
        catch
        {
            data.ResolvedType = null;
            throw;
        }
    }

    private async ValueTask<IndexInfo> IndexAsync(Type key, IEnumerable<Symbol> symbols, bool readOnly, CancellationToken cancellation)
    {
        var types = new List<Type>();
        var components = new List<SyntaxNode>();
        foreach (var symbol in symbols)
        {
            var name = (symbol.Declarations.FirstOrDefault() as INamedNode)?.Name;
            bool symbolName = symbol.Name.StartsWith(Symbol.InternalPrefix + "@", StringComparison.Ordinal);
            if (!symbolName && name is ComputedPropertyNameNode computed)
                symbolName = await predicates.AssignableAsync(
                    await ComputedAsync(computed, cancellation).ConfigureAwait(false),
                    TypeFlags.ESSymbol,
                    cancellation: cancellation).ConfigureAwait(false);
            bool include = key == context.StringType ? !symbolName : key == context.ESSymbolType ? symbolName
                : IndexSignatures.NumericName(symbol.Name) || name is ComputedPropertyNameNode number
                    && await predicates.AssignableAsync(await ComputedAsync(number, cancellation).ConfigureAwait(false), TypeFlags.Number,
                        cancellation: cancellation).ConfigureAwait(false);
            if (!include)
                continue;
            types.Add(await values.GetAsync(symbol, cancellation).ConfigureAwait(false));
            if (name is ComputedPropertyNameNode)
                components.Add(symbol.Declarations[0]);
        }
        return context.NewIndexInfo(key, types.Count == 0 ? context.UndefinedType
            : await algebra.UnionAsync(types, UnionReduction.Subtype, cancellation: cancellation).ConfigureAwait(false),
            readOnly,
            components: components.ToArray());
    }

    internal async ValueTask<Type> RegularAsync(Type type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((type.ObjectFlags & (ObjectFlags.ObjectLiteral | ObjectFlags.FreshLiteral)) != (ObjectFlags.ObjectLiteral | ObjectFlags.FreshLiteral))
            return type;
        if (regular.TryGetValue(type, out var cached))
            return cached;
        var resolved = await members.ResolveAsync((StructuredType)type, cancellation).ConfigureAwait(false);
        var table = new Dictionary<string, Symbol>(StringComparer.Ordinal);
        foreach (var property in resolved.Properties!)
        {
            var original = await values.GetAsync(property, cancellation).ConfigureAwait(false);
            var updated = await RegularAsync(original, cancellation).ConfigureAwait(false);
            table[property.Name] = original == updated ? property : widening.WithType(property, updated);
        }
        var result = await spreads.ObjectAsync(type.Symbol, table, resolved.IndexInfos,
            resolved.ObjectFlags & ~ObjectFlags.FreshLiteral, cancellation).ConfigureAwait(false);
        result.CallSignatures = resolved.CallSignatures;
        result.ConstructSignatures = resolved.ConstructSignatures;
        cancellation.ThrowIfCancellationRequested();
        return regular[type] = result;
    }

    private async ValueTask GrammarAsync(ObjectLiteralExpressionNode node, bool destructuring, CancellationToken cancellation)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        bool grammarErrors = SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0;
        void Error(SyntaxNode location, int code)
        {
            if (grammarErrors)
                host.ExpressionError(location, code);
        }
        foreach (var property in node.Properties!)
        {
            if (property is SpreadAssignmentNode spread)
            {
                var expression = spread.Expression!;
                while (expression is ParenthesizedExpressionNode parentheses)
                    expression = parentheses.Expression!;
                if (destructuring && expression is ArrayLiteralExpressionNode or ObjectLiteralExpressionNode)
                {
                    Error(spread.Expression!, 2501);
                    return;
                }
                continue;
            }
            var name = ((INamedNode)property).Name!;
            if (name is ComputedPropertyNameNode { Expression: BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.CommaToken } comma })
                Error(comma, 1171);
            if (!destructuring && property is ShorthandPropertyAssignmentNode { ObjectAssignmentInitializer: not null })
                Error(property, 1312);
            if (name is PrivateIdentifierNode)
                Error(name, 18016);
            if (property is IModifiedNode { Modifiers: { } modifiers })
                foreach (var modifier in modifiers)
                    if (modifier.Kind != SyntaxKind.Decorator
                        && (modifier.Kind != SyntaxKind.AsyncKeyword || property is not MethodDeclarationNode))
                        Error(modifier, 1042);
            int kind = property switch
            {
                PropertyAssignmentNode or ShorthandPropertyAssignmentNode => 1,
                MethodDeclarationNode => 2,
                GetAccessorDeclarationNode => 4,
                SetAccessorDeclarationNode => 8,
                _ => throw new InvalidOperationException("Unexpected object member")
            };
            if (kind == 1)
            {
                var postfix = property is PropertyAssignmentNode assignment
                    ? assignment.PostfixToken
                    : ((ShorthandPropertyAssignmentNode)property).PostfixToken;
                if (postfix?.Kind == SyntaxKind.ExclamationToken)
                    Error(postfix, 1255);
                if (postfix?.Kind == SyntaxKind.QuestionToken)
                    Error(postfix, 1162);
                if (name is NumericLiteralNode)
                    host.LiteralGrammar(name);
                if (name is BigIntLiteralNode)
                    host.ExpressionError(name, 1539);
            }
            else if (property is MethodDeclarationNode { PostfixToken: { } postfix })
                Error(postfix, postfix.Kind == SyntaxKind.QuestionToken ? 1162 : 1255);
            if (destructuring)
                continue;
            if (!effectiveNames.TryGetValue(name, out var text))
            {
                var type = name is ComputedPropertyNameNode computed
                    ? await host.CheckExpressionAsync(computed.Expression!, CheckMode.TypeOnly, cancellation).ConfigureAwait(false)
                    : await host.LiteralNameTypeAsync(name, cancellation).ConfigureAwait(false);
                text = (type.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0 ? MappedMembers.PropertyName(type) : null;
                effectiveNames[name] = text;
            }
            if (text is null)
                continue;
            if (!seen.TryGetValue(text, out int old))
                seen[text] = kind;
            else if ((kind & old & 2) != 0)
                Error(name, 2300);
            else if ((kind & old & 1) != 0)
            {
                if (SemanticSyntax.Source(name)?.ParseDiagnostics.Count == 0)
                    host.DuplicateObjectProperty(name, text);
            }
            else if ((kind & 12) != 0 && (old & 12) != 0)
            {
                if (old != 12 && kind != old)
                    seen[text] = kind | old;
                else
                {
                    Error(name, 1118);
                    return;
                }
            }
            else
            {
                Error(name, 1119);
                return;
            }
        }
    }

    private static bool JsLiteral(SyntaxNode node) =>
        (node.Flags & NodeFlags.JavaScriptFile) != 0 && SemanticSyntax.Source(node)?.ScriptKind != ScriptKind.JSON;

    internal static bool DefaultValue(SyntaxNode node) => node switch
    {
        BindingElementNode element => element.Initializer is not null,
        PropertyAssignmentNode property => DefaultValue(property.Initializer!),
        ShorthandPropertyAssignmentNode property => property.ObjectAssignmentInitializer is not null,
        BinaryExpressionNode binary => binary.OperatorToken!.Kind == SyntaxKind.EqualsToken,
        _ => false
    };
}
