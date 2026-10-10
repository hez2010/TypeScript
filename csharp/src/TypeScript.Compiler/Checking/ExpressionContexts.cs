using TypeScript.Compiler.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IExpressionContextHost
{
    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> CheckExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask<Type> DeclarationInitializerAsync(SyntaxNode declaration, CheckMode mode, CancellationToken cancellation);

    ValueTask<Type?> ContextualParameterAsync(ParameterDeclarationNode node, CancellationToken cancellation);

    ValueTask<Type?> OtherContextAsync(SyntaxNode node, ContextFlags flags, CancellationToken cancellation);

    ValueTask<Type> DiscriminateJsxContextAsync(JsxAttributesNode node, UnionType type, CancellationToken cancellation);

    ValueTask<Type?> StaticPropertyContextAsync(PropertyDeclarationNode node, ContextFlags flags, CancellationToken cancellation);

    ValueTask<Type?> ContextualPropertyAsync(Type type, Utf8String name, CancellationToken cancellation);

    ValueTask<Type?> ObjectElementContextAsync(SyntaxNode node, ContextFlags flags, CancellationToken cancellation);

    ValueTask<Type> DiscriminateObjectContextAsync(ObjectLiteralExpressionNode node, UnionType type, CancellationToken cancellation);

    ValueTask<Type?> IteratedContextAsync(Type type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference type, CancellationToken cancellation);

    ValueTask<Type> LiteralNameTypeAsync(SyntaxNode name, CancellationToken cancellation);

    ValueTask<bool> ConstArgumentAsync(SyntaxNode node, CancellationToken cancellation);

    bool InlineImportAttributes(SyntaxNode node);
}

internal sealed class ExpressionContexts(TypeContext context, TypeAlgebra algebra, TypeViews views, TypeWidening widening,
    TypeConstraints constraints, TypeInstantiation instantiation, TypePredicates predicates, TypeInference inference,
    TypeProperties properties, SymbolTypes values, TupleTypes tuples, BindingPatterns bindingPatterns,
    Dictionary<Type, SyntaxNode> patterns, IExpressionContextHost host)
{
    private readonly List<(SyntaxNode Node, Type? Type, bool Cache)> contexts = [];
    private readonly List<(SyntaxNode Node, InferenceContext? Context)> inferences = [];
    private readonly Dictionary<SyntaxNode, Type> contextFree = [];
    internal int ContextDepth => contexts.Count;
    internal int InferenceDepth => inferences.Count;

    internal Type? AppliedContext(SyntaxNode node, ContextFlags flags)
    {
        for (int i = contexts.Count - 1; i >= 0; i--)
            if (contexts[i].Node == node && (flags == 0 || !contexts[i].Cache))
                return contexts[i].Type;
        return null;
    }

    internal async ValueTask<T> WithAsync<T>(
        SyntaxNode node,
        Type type,
        Func<ValueTask<T>> action,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        contexts.Add((node, type, false));
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            contexts.RemoveAt(contexts.Count - 1);
        }
    }

    internal Type? CachedContextFree(SyntaxNode node) => contextFree.GetValueOrDefault(node);

    internal void SetContextFree(SyntaxNode node, Type type)
    {
        context.RequireOwned(type);
        contextFree[node] = type;
    }

    internal async ValueTask<Type> ContextFreeAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (contextFree.TryGetValue(node, out var cached))
            return cached;
        contexts.Add((node, context.AnyType, false));
        try
        {
            var type = await host.CheckExpressionAsync(node, CheckMode.SkipContextSensitive, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            return contextFree[node] = type;
        }
        finally
        {
            contexts.RemoveAt(contexts.Count - 1);
        }
    }

    internal async ValueTask<T> CachedAsync<T>(SyntaxNode node, Func<ValueTask<T>> action, CancellationToken cancellation = default)
    {
        var type = await GetAsync(node, cancellation: cancellation).ConfigureAwait(false);
        contexts.Add((node, type, true));
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            contexts.RemoveAt(contexts.Count - 1);
        }
    }

    internal InferenceContext? InferenceFor(SyntaxNode node)
    {
        for (int i = inferences.Count - 1; i >= 0; i--)
            if (DeclarationOrder.Ancestor(node, n => n == inferences[i].Node) is not null)
                return inferences[i].Context;
        return null;
    }

    internal ValueTask<Type> CheckWithAsync(SyntaxNode node, Type type, InferenceContext? inferenceContext = null,
        CheckMode mode = 0, CancellationToken cancellation = default)
        => inferenceContext is null ? CheckWithCoreAsync(node, type, null, mode, cancellation)
            : inferenceContext.RunAsync(() => CheckWithCoreAsync(node, type, inferenceContext, mode, cancellation), cancellation);

    private async ValueTask<Type> CheckWithCoreAsync(SyntaxNode node, Type type, InferenceContext? inferenceContext,
        CheckMode mode, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        var contextNode = node.Kind == SyntaxKind.JsxAttributes && node.Parent is not JsxSelfClosingElementNode
            ? node.Parent!.Parent!
            : node;
        contexts.Add((contextNode, type, false));
        inferences.Add((contextNode, inferenceContext));
        try
        {
            var result = await host.CheckExpressionAsync(node,
                mode | CheckMode.Contextual | (inferenceContext is null ? 0 : CheckMode.Inferential), cancellation).ConfigureAwait(false);
            if (predicates.Maybe(result, TypeFlags.Literal, cancellation)
                && await LiteralAsync(
                    result,
                    await InstantiateAsync(type, node, 0, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false))
                result = await algebra.RegularTypeAsync(result, cancellation).ConfigureAwait(false);
            return result;
        }
        finally
        {
            inferenceContext?.IntraExpressionSites.Clear();
            inferences.RemoveAt(inferences.Count - 1);
            contexts.RemoveAt(contexts.Count - 1);
        }
    }

    internal async ValueTask<Type?> GetAsync(SyntaxNode node, ContextFlags flags = 0, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if ((node.Flags & NodeFlags.InWithStatement) != 0)
                return null;
            foreach (var entry in contexts)
                if (entry.Node == node && (flags == 0 || !entry.Cache))
                    return entry.Type;
            switch (node.Parent)
            {
                case VariableDeclarationNode or ParameterDeclarationNode or PropertyDeclarationNode or PropertySignatureDeclarationNode
                    or BindingElementNode:
                    if (node.Parent is not IInitializedNode { Initializer: { } initializer } || initializer != node)
                        return null;
                    var declared = await DeclarationAsync(node.Parent, flags, cancellation).ConfigureAwait(false);
                    if (declared is not null)
                        return declared;
                    return (flags & ContextFlags.SkipBindingPatterns) == 0
                        && node.Parent is INamedNode { Name: BindingPatternNode { Elements.Count: > 0 } pattern }
                        ? await bindingPatterns.GetAsync(pattern, true, false, cancellation).ConfigureAwait(false) : null;
                case AsExpressionNode or TypeAssertionNode:
                    if (!SemanticSyntax.ConstAssertion(node.Parent))
                        return await host.TypeFromNodeAsync(((ITypedNode)node.Parent).Type!, cancellation).ConfigureAwait(false);
                    node = node.Parent;
                    continue;
                case SatisfiesExpressionNode satisfies:
                    return await host.TypeFromNodeAsync(satisfies.Type!, cancellation).ConfigureAwait(false);
                case ParenthesizedExpressionNode or NonNullExpressionNode:
                    node = node.Parent;
                    continue;
                case PropertyAssignmentNode or ShorthandPropertyAssignmentNode:
                    return await host.ObjectElementContextAsync(node.Parent, flags, cancellation).ConfigureAwait(false);
                case SpreadAssignmentNode:
                    node = node.Parent.Parent!;
                    continue;
                case ArrayLiteralExpressionNode array:
                    int position = array.Elements!.IndexOf(node);
                    if (position < 0)
                        return null;
                    var arrayContext = await ApparentAsync(array, flags, cancellation).ConfigureAwait(false);
                    var spreadIndexes = array.Elements!.Select((e, i) => (e, i)).Where(p => p.e is SpreadElementNode).Select(p => p.i).ToArray();
                    return await ElementAsync(arrayContext, position, array.Elements!.Count,
                        spreadIndexes.Length == 0 ? -1 : spreadIndexes[0],
                        spreadIndexes.Length == 0 ? -1 : spreadIndexes[^1],
                        cancellation).ConfigureAwait(false);
                case ConditionalExpressionNode conditional:
                    if (node == conditional.Condition)
                        return null;
                    node = conditional;
                    continue;
                case BinaryExpressionNode binary when binary.Type is null
                    && binary.OperatorToken!.Kind is SyntaxKind.BarBarToken or SyntaxKind.QuestionQuestionToken:
                    var logical = await GetAsync(binary, flags, cancellation).ConfigureAwait(false);
                    return node == binary.Right && (logical is null || patterns.ContainsKey(logical))
                        ? await host.CheckExpressionAsync(binary.Left!, CheckMode.TypeOnly, cancellation).ConfigureAwait(false) : logical;
                case BinaryExpressionNode binary when binary.Type is null
                    && binary.OperatorToken!.Kind is SyntaxKind.AmpersandAmpersandToken or SyntaxKind.CommaToken:
                    if (node != binary.Right)
                        return null;
                    node = binary;
                    continue;
                case BinaryExpressionNode { Type: { } annotation }:
                    return await host.TypeFromNodeAsync(annotation, cancellation).ConfigureAwait(false);
                case ExportAssignmentNode export:
                    return export.Type is null ? null : await host.TypeFromNodeAsync(export.Type, cancellation).ConfigureAwait(false);
                case ArrowFunctionNode or ReturnStatementNode or YieldExpressionNode or AwaitExpressionNode or CallExpressionNode
                    or NewExpressionNode or DecoratorNode or BinaryExpressionNode or TemplateSpanNode or JsxExpressionNode
                    or JsxAttributeNode or JsxSpreadAttributeNode or JsxOpeningElementNode or JsxSelfClosingElementNode or JsxElementNode or ImportAttributeNode:
                    return await host.OtherContextAsync(node, flags, cancellation).ConfigureAwait(false);
                default:
                    return null;
            }
        }
    }

    internal async ValueTask<Type?> DeclarationAsync(
        SyntaxNode declaration,
        ContextFlags flags = 0,
        CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (declaration is ITypedNode { Type: { } annotation })
            return await host.TypeFromNodeAsync(annotation, cancellation).ConfigureAwait(false);
        if (declaration is ParameterDeclarationNode parameter)
            return await host.ContextualParameterAsync(parameter, cancellation).ConfigureAwait(false);
        if (declaration is PropertyDeclarationNode property && SemanticSyntax.IsStatic(property))
            return await host.StaticPropertyContextAsync(property, flags, cancellation).ConfigureAwait(false);
        if (declaration is not BindingElementNode element)
            return null;
        var name = element.PropertyName ?? element.Name!;
        if (name is BindingPatternNode || name is ComputedPropertyNameNode computed
            && computed.Expression is not StringLiteralNode and not NumericLiteralNode)
            return null;
        var parent = element.Parent!.Parent!;
        var parentType = await DeclarationAsync(parent, flags, cancellation).ConfigureAwait(false);
        if (parentType is null && parent is not BindingElementNode && parent is IInitializedNode { Initializer: not null })
            parentType = await host.DeclarationInitializerAsync(parent,
                element.DotDotDotToken is null ? CheckMode.Normal : CheckMode.RestBindingElement, cancellation).ConfigureAwait(false);
        if (parentType is null)
            return null;
        if (element.Parent!.Kind == SyntaxKind.ArrayBindingPattern)
            return await ElementAsync(
                parentType,
                ((BindingPatternNode)element.Parent).Elements!.IndexOf(element),
                cancellation: cancellation).ConfigureAwait(false);
        var key = await host.LiteralNameTypeAsync(name, cancellation).ConfigureAwait(false);
        if ((key.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0
            && await properties.PropertyAsync(
                parentType,
                MappedMembers.PropertyName(key),
                cancellation: cancellation).ConfigureAwait(false) is { } symbol)
            return await values.GetAsync(symbol, cancellation).ConfigureAwait(false);
        return null;
    }

    internal async ValueTask<Type?> ApparentAsync(SyntaxNode node, ContextFlags flags = 0, CancellationToken cancellation = default)
    {
        var contextual = node is MethodDeclarationNode { Parent: ObjectLiteralExpressionNode }
            ? await host.ObjectElementContextAsync(node, flags, cancellation).ConfigureAwait(false)
            : await GetAsync(node, flags, cancellation).ConfigureAwait(false);
        var instantiated = await InstantiateAsync(contextual, node, flags, cancellation).ConfigureAwait(false);
        if (instantiated is null || (flags & ContextFlags.NoConstraints) != 0 && (instantiated.Flags & TypeFlags.TypeVariable) != 0)
            return null;
        var apparent = await algebra.MapAsync(instantiated, async part => part is MappedType ? part
            : await views.ApparentAsync(part, cancellation).ConfigureAwait(false), true, cancellation).ConfigureAwait(false);
        if (apparent is UnionType union)
        {
            if (node is ObjectLiteralExpressionNode literal)
                return await host.DiscriminateObjectContextAsync(literal, union, cancellation).ConfigureAwait(false);
            if (node is JsxAttributesNode attributes)
                return await host.DiscriminateJsxContextAsync(attributes, union, cancellation).ConfigureAwait(false);
        }
        return apparent;
    }

    internal async ValueTask<Type?> ElementAsync(Type? type, int index, int length = -1, int firstSpread = -1, int lastSpread = -1,
        CancellationToken cancellation = default)
    {
        if (type is null)
            return null;
        return await algebra.MapAsync(type, async part =>
        {
            if (part is TypeReference { Target: TupleType tuple } reference)
            {
                var arguments = await host.TypeArgumentsAsync(reference, cancellation).ConfigureAwait(false);
                if ((firstSpread < 0 || index < firstSpread) && index < tuple.FixedLength)
                    return values.NonMissing(arguments[index], (tuple.ElementInfos[index].Flags & ElementFlags.Optional) != 0);
                int offset = length >= 0 && (lastSpread < 0 || index > lastSpread) ? length - index : 0;
                int endFixed = offset > 0 && (tuple.CombinedFlags & ElementFlags.Variable) != 0
                    ? tuple.ElementInfos.Reverse().TakeWhile(e => (e.Flags & ElementFlags.Fixed) != 0).Count() : 0;
                if (offset > 0 && offset <= endFixed)
                    return arguments[arguments.Count - offset];
                return await tuples.SliceElementAsync(
                    reference,
                    firstSpread < 0 ? tuple.FixedLength : Math.Min(tuple.FixedLength, firstSpread),
                    length >= 0 && lastSpread >= 0 ? Math.Min(endFixed, length - lastSpread) : endFixed,
                    noReductions: true, cancellation: cancellation).ConfigureAwait(false);
            }
            if ((firstSpread < 0 || index < firstSpread)
                && await host.ContextualPropertyAsync(
                    part,
                    Utf8String.Format(index),
                    cancellation).ConfigureAwait(false) is { } property)
                return property;
            return await host.IteratedContextAsync(part, cancellation).ConfigureAwait(false);
        }, true, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type?> InstantiateAsync(
        Type? type,
        SyntaxNode node,
        ContextFlags flags,
        CancellationToken cancellation = default)
    {
        if (type is null || !predicates.Maybe(type, TypeFlags.Instantiable, cancellation) || InferenceFor(node) is not { } inferenceContext)
            return type;
        if ((flags & ContextFlags.Signature) != 0)
        {
            foreach (var info in inferenceContext.Inferences)
                if (info.HasCandidates || info.Parameter is TypeParameter parameter
                    && await constraints.DefaultAsync(parameter, cancellation).ConfigureAwait(false) is not null)
                {
                    var result = await InstantiableAsync(type, inferenceContext.NonFixingMapper, cancellation).ConfigureAwait(false);
                    if ((result.Flags & TypeFlags.AnyOrUnknown) == 0)
                        return result;
                    break;
                }
        }
        if (inferenceContext.ReturnMapper is { } mapper)
        {
            var result = await InstantiableAsync(type, mapper, cancellation).ConfigureAwait(false);
            if ((result.Flags & TypeFlags.AnyOrUnknown) == 0)
                return result is UnionType union
                    && union.Types.Contains(context.RegularFalseType)
                    && union.Types.Contains(context.RegularTrueType)
                    ? algebra.Filter(result, t => t != context.RegularFalseType && t != context.RegularTrueType) : result;
        }
        return type;
    }

    private async ValueTask<Type> InstantiableAsync(Type type, TypeMapper mapper, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        if ((type.Flags & TypeFlags.Instantiable) != 0)
            return (await instantiation.InstantiateAsync(type, mapper, cancellation: cancellation).ConfigureAwait(false))!;
        if (type is UnionOrIntersectionType composite)
        {
            var types = new List<Type>();
            foreach (var part in composite.Types)
                types.Add(await InstantiableAsync(part, mapper, cancellation).ConfigureAwait(false));
            return type is UnionType ? await algebra.UnionAsync(
                types,
                UnionReduction.None,
                cancellation: cancellation).ConfigureAwait(false)
                : await algebra.IntersectionAsync(types, cancellation: cancellation).ConfigureAwait(false);
        }
        return type;
    }

    internal async ValueTask<bool> LiteralAsync(Type candidate, Type? contextual, CancellationToken cancellation = default)
    {
        if (contextual is null)
            return false;
        var pending = new Stack<Type>();
        pending.Push(contextual);
        while (pending.TryPop(out var type))
        {
            cancellation.ThrowIfCancellationRequested();
            if (type is UnionOrIntersectionType composite)
            {
                foreach (var part in composite.Types)
                    pending.Push(part);
                continue;
            }
            if ((type.Flags & TypeFlags.InstantiableNonPrimitive) != 0)
            {
                var constraint = await constraints.BaseConstraintAsync(type, cancellation).ConfigureAwait(false) ?? context.UnknownType;
                if (Matches(constraint, true))
                    return true;
                pending.Push(constraint);
            }
            else if (Matches(type, false))
                return true;
        }
        return false;
        bool Matches(Type type, bool constraint) =>
            (constraint ? predicates.Maybe(type, TypeFlags.String, cancellation)
                : (type.Flags & (TypeFlags.StringLiteral | TypeFlags.Index | TypeFlags.TemplateLiteral | TypeFlags.StringMapping)) != 0)
                && predicates.Maybe(candidate, TypeFlags.StringLiteral, cancellation)
            || (constraint ? predicates.Maybe(type, TypeFlags.Number, cancellation) : (type.Flags & TypeFlags.NumberLiteral) != 0)
                && predicates.Maybe(candidate, TypeFlags.NumberLiteral, cancellation)
            || (constraint ? predicates.Maybe(type, TypeFlags.BigInt, cancellation) : (type.Flags & TypeFlags.BigIntLiteral) != 0)
                && predicates.Maybe(candidate, TypeFlags.BigIntLiteral, cancellation)
            || !constraint
                && (type.Flags & TypeFlags.BooleanLiteral) != 0
                && predicates.Maybe(candidate, TypeFlags.BooleanLiteral, cancellation)
            || (constraint ? predicates.Maybe(type, TypeFlags.ESSymbol, cancellation) : (type.Flags & TypeFlags.UniqueESSymbol) != 0)
                && predicates.Maybe(candidate, TypeFlags.UniqueESSymbol, cancellation);
    }

    internal async ValueTask<bool> ConstAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        // The middle branch below resolves the contextual type and then runs const-parameter
        // inference on it. That query is skipped when it provably cannot find a const type parameter:
        // see TypeParameterQueryPossible.
        bool typeParameterPossible = TypeParameterQueryPossible(node);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var parent = node.Parent;
            if (parent is not null && SemanticSyntax.ConstAssertion(parent) || host.InlineImportAttributes(node))
                return true;
            if (typeParameterPossible
                && await host.ConstArgumentAsync(node, cancellation).ConfigureAwait(false)
                && await GetAsync(node, cancellation: cancellation).ConfigureAwait(false) is { } type
                && await inference.ConstVariableAsync(type, cancellation).ConfigureAwait(false))
                return true;
            if (parent is ParenthesizedExpressionNode or ArrayLiteralExpressionNode or SpreadElementNode)
                node = parent;
            else if (parent is PropertyAssignmentNode or ShorthandPropertyAssignmentNode or TemplateSpanNode)
                node = parent.Parent!;
            else
                return false;
        }
    }

    /// <summary>
    /// True when resolving the contextual type of <paramref name="node"/> can plausibly yield a type
    /// that is or contains a const type parameter, which is the only case the const-variable branch
    /// of <see cref="ConstAsync"/> can act on. Two ways in: a type parameter declared by an enclosing
    /// signature, class, interface, alias, mapped type, conditional type or infer clause, or a
    /// contextual type supplied by a callee signature, which is reachable from argument, decorator,
    /// tagged-template and JSX positions. Both walks are pure type tests over the ancestors; anything
    /// not covered here keeps the query, so the shortcut only removes work it can prove irrelevant.
    /// </summary>
    private static bool TypeParameterQueryPossible(SyntaxNode node)
    {
        for (var current = node; current is not null; current = current.Parent)
            switch (current)
            {
                case IFunctionSignature { TypeParameters: { Count: > 0 } }:
                case ClassDeclarationNode { TypeParameters: { Count: > 0 } }:
                case ClassExpressionNode { TypeParameters: { Count: > 0 } }:
                case InterfaceDeclarationNode { TypeParameters: { Count: > 0 } }:
                case TypeAliasDeclarationNode { TypeParameters: { Count: > 0 } }:
                case MappedTypeNode or ConditionalTypeNode or InferTypeNode:
                case CallExpressionNode or NewExpressionNode or TaggedTemplateExpressionNode or DecoratorNode:
                case JsxOpeningElementNode or JsxSelfClosingElementNode or JsxAttributeNode or JsxSpreadAttributeNode:
                    return true;
            }
        return false;
    }

    internal async ValueTask<Type> MutableAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation = default)
    {
        Diagnostics.CompilationCapture.ProbeMark mutableMark = Diagnostics.CompilationCapture.Mark();
        try
        {
            Diagnostics.CompilationCapture.ProbeMark checkMark = Diagnostics.CompilationCapture.Mark();
            var type = await host.CheckExpressionAsync(node, mode, cancellation).ConfigureAwait(false);
            Diagnostics.CompilationCapture.Report(Diagnostics.AllocationProbes.ObjectCheckExpr, checkMark);
            Diagnostics.CompilationCapture.ProbeMark constMark = Diagnostics.CompilationCapture.Mark();
            bool constant = await ConstAsync(node, cancellation).ConfigureAwait(false);
            Diagnostics.CompilationCapture.Report(Diagnostics.AllocationProbes.MutableConst, constMark);
            if (constant)
                return await algebra.RegularTypeAsync(type, cancellation).ConfigureAwait(false);
            if (node is AsExpressionNode or TypeAssertionNode)
                return type;
            Diagnostics.CompilationCapture.ProbeMark contextualMark = Diagnostics.CompilationCapture.Mark();
            var contextual = await InstantiateAsync(
                await GetAsync(node, cancellation: cancellation).ConfigureAwait(false),
                node,
                0,
                cancellation).ConfigureAwait(false);
            Diagnostics.CompilationCapture.Report(Diagnostics.AllocationProbes.MutableContextual, contextualMark);
            Diagnostics.CompilationCapture.ProbeMark widenMark = Diagnostics.CompilationCapture.Mark();
            try
            {
                return await WidenLiteralAsync(type, contextual, cancellation).ConfigureAwait(false);
            }
            finally
            {
                Diagnostics.CompilationCapture.Report(Diagnostics.AllocationProbes.MutableWiden, widenMark);
            }
        }
        finally
        {
            Diagnostics.CompilationCapture.Report(Diagnostics.AllocationProbes.ObjectMutable, mutableMark);
        }
    }

    internal async ValueTask<Type> WidenLiteralAsync(Type type, Type? contextual, CancellationToken cancellation = default)
    {
        if (!await LiteralAsync(type, contextual, cancellation).ConfigureAwait(false))
        {
            type = await widening.LiteralAsync(type, cancellation).ConfigureAwait(false);
            type = (await algebra.MapAsync(type, part => ValueTask.FromResult<Type?>((part.Flags & TypeFlags.UniqueESSymbol) != 0
                ? context.ESSymbolType : part), cancellation: cancellation).ConfigureAwait(false))!;
        }
        return await algebra.RegularTypeAsync(type, cancellation).ConfigureAwait(false);
    }
}
