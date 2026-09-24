using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface ICallArgumentHost
{
    ValueTask<Type> CheckExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask<Type> CachedExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference type, CancellationToken cancellation);

    ValueTask<bool> ArrayLikeAsync(Type type, CancellationToken cancellation);

    ValueTask<bool> MutableArrayLikeAsync(Type type, CancellationToken cancellation);

    bool IsArray(Type type);

    bool IsReadonlyArray(Type type);

    ValueTask<Type> SpreadElementAsync(Type type, SyntaxNode node, CancellationToken cancellation);

    ValueTask<IReadOnlyList<SyntaxNode>> SpecialArgumentsAsync(SyntaxNode node, CancellationToken cancellation);
}

internal sealed class CallArguments(TypeContext context, TypeAlgebra algebra, ExpressionContexts expressions,
    TypeInference inference, TypePredicates predicates, TypeConstraints constraints, TypeWidening widening,
    TupleTypes tuples, IndexedTypes indexed, OptionalExpressions optional, FlowTypes flows, ICallArgumentHost host)
{
    internal static NodeList? List(SyntaxNode node) =>
        node switch { CallExpressionNode call => call.Arguments, NewExpressionNode call => call.Arguments, _ => null };

    internal static NodeList? TypeNodes(SyntaxNode node) => node switch
    {
        CallExpressionNode call => call.TypeArguments,
        NewExpressionNode call => call.TypeArguments,
        TaggedTemplateExpressionNode tag => tag.TypeArguments,
        _ => null
    };

    internal static SyntaxNode? Target(SyntaxNode node) => node switch
    {
        CallExpressionNode call => call.Expression,
        NewExpressionNode call => call.Expression,
        TaggedTemplateExpressionNode tag => tag.Tag,
        DecoratorNode decorator => decorator.Expression,
        _ => null
    };

    internal static bool Spread(SyntaxNode node) => node is SpreadElementNode or SyntheticExpressionNode { IsSpread: true };

    internal static int SpreadIndex(IReadOnlyList<SyntaxNode> arguments)
    {
        for (int i = 0; i < arguments.Count; i++)
            if (Spread(arguments[i]))
                return i;
        return -1;
    }

    internal static SyntheticExpressionNode Synthetic(SyntaxNode parent, Type type, bool spread = false, SyntaxNode? label = null) =>
            new() { Type = type, IsSpread = spread, TupleNameSource = label, Parent = parent, Pos = parent.Pos, End = parent.End };

    internal async ValueTask<IReadOnlyList<SyntaxNode>> EffectiveAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (node is not CallExpressionNode and not NewExpressionNode)
            return await host.SpecialArgumentsAsync(node, cancellation).ConfigureAwait(false);
        var arguments = (IReadOnlyList<SyntaxNode>?)List(node) ?? [];
        int firstSpread = SpreadIndex(arguments);
        if (firstSpread < 0)
            return arguments;
        var result = arguments.Take(firstSpread).ToList();
        foreach (var argument in arguments.Skip(firstSpread))
        {
            Type? type = argument is SpreadElementNode spread
                ? flows.ActiveLoopCount != 0 ? await host.CheckExpressionAsync(spread.Expression!, 0, cancellation).ConfigureAwait(false)
                    : await host.CachedExpressionAsync(spread.Expression!, 0, cancellation).ConfigureAwait(false) : null;
            if (type is TypeReference { Target: TupleType tuple } reference)
            {
                var types = await host.TypeArgumentsAsync(reference, cancellation).ConfigureAwait(false);
                for (int i = 0; i < types.Count; i++)
                {
                    var info = tuple.ElementInfos[i];
                    result.Add(Synthetic(argument, (info.Flags & ElementFlags.Rest) != 0
                        ? await tuples.ArrayAsync(types[i], cancellation: cancellation).ConfigureAwait(false) : types[i],
                        (info.Flags & ElementFlags.Variable) != 0, info.LabeledDeclaration));
                }
            }
            else
                result.Add(argument);
        }
        return result;
    }

    internal async ValueTask<Type> SpreadAsync(IReadOnlyList<SyntaxNode> arguments, int start, Type rest,
        InferenceContext? inferenceContext = null, CheckMode mode = 0, CancellationToken cancellation = default)
    {
        bool isConst = await inference.ConstVariableAsync(rest, cancellation).ConfigureAwait(false);
        if (arguments.Count > 0 && start >= arguments.Count - 1 && Spread(arguments[^1]))
        {
            var argument = arguments[^1];
            var type = argument is SyntheticExpressionNode synthetic ? (Type)synthetic.Type!
                : await expressions.CheckWithAsync(
                    ((SpreadElementNode)argument).Expression!,
                    rest,
                    inferenceContext,
                    mode,
                    cancellation).ConfigureAwait(false);
            if (await host.ArrayLikeAsync(type, cancellation).ConfigureAwait(false))
                return await MutableAsync(type, cancellation).ConfigureAwait(false);
            return await tuples.ArrayAsync(await host.SpreadElementAsync(type,
                argument is SpreadElementNode spread ? spread.Expression! : argument,
                cancellation).ConfigureAwait(false),
                isConst,
                cancellation).ConfigureAwait(false);
        }
        var types = new List<Type>();
        var infos = new List<TupleElementInfo>();
        for (int i = start; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            Type type;
            ElementFlags flags;
            if (Spread(argument))
            {
                var spreadType = argument is SyntheticExpressionNode synthetic ? (Type)synthetic.Type!
                    : await host.CheckExpressionAsync(((SpreadElementNode)argument).Expression!, 0, cancellation).ConfigureAwait(false);
                if (await host.ArrayLikeAsync(spreadType, cancellation).ConfigureAwait(false))
                {
                    type = spreadType;
                    flags = ElementFlags.Variadic;
                }
                else
                {
                    type = await host.SpreadElementAsync(
                        spreadType,
                        argument is SpreadElementNode spread ? spread.Expression! : argument,
                        cancellation).ConfigureAwait(false);
                    flags = ElementFlags.Rest;
                }
            }
            else
            {
                var contextual = rest is TypeReference { Target: TupleType }
                    ? await expressions.ElementAsync(
                        rest,
                        i - start,
                        arguments.Count - start,
                        cancellation: cancellation).ConfigureAwait(false) ?? context.UnknownType
                    : await indexed.GetAsync(
                        rest,
                        context.GetNumberLiteralType(i - start),
                        AccessFlags.Contextual,
                        cancellation: cancellation).ConfigureAwait(false);
                var argumentType = await expressions.CheckWithAsync(
                    argument,
                    contextual,
                    inferenceContext,
                    mode,
                    cancellation).ConfigureAwait(false);
                type = isConst
                    || predicates.Maybe(
                        contextual,
                        TypeFlags.Primitive | TypeFlags.Index | TypeFlags.TemplateLiteral | TypeFlags.StringMapping,
                        cancellation)
                    ? await algebra.RegularTypeAsync(
                        argumentType,
                        cancellation).ConfigureAwait(false) : await widening.LiteralAsync(argumentType, cancellation).ConfigureAwait(false);
                flags = ElementFlags.Required;
            }
            types.Add(type);
            infos.Add(new(flags, (argument as SyntheticExpressionNode)?.TupleNameSource));
        }
        bool mutable = false;
        if (isConst)
            foreach (var part in rest is UnionType union ? union.Types : [rest])
                if (await host.MutableArrayLikeAsync(part, cancellation).ConfigureAwait(false))
                {
                    mutable = true;
                    break;
                }
        return await tuples.CreateAsync(types, infos, isConst && !mutable, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Type> MutableAsync(Type type, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        if (type is UnionType)
            return (await algebra.MapAsync(
                type,
                async part => await MutableAsync(part, cancellation).ConfigureAwait(false),
                cancellation: cancellation).ConfigureAwait(false))!;
        var constraint = await constraints.BaseConstraintOrTypeAsync(type, cancellation).ConfigureAwait(false);
        if ((type.Flags & TypeFlags.Any) != 0 || host.IsArray(constraint) && !host.IsReadonlyArray(constraint)
            || constraint is TypeReference { Target: TupleType { IsReadonly: false } })
            return type;
        if (type is TypeReference { Target: TupleType tuple } reference)
            return await tuples.CreateAsync(
                await host.TypeArgumentsAsync(reference, cancellation).ConfigureAwait(false),
                tuple.ElementInfos,
                false,
                cancellation).ConfigureAwait(false);
        return await tuples.CreateAsync([type], [new(ElementFlags.Variadic)], false, cancellation).ConfigureAwait(false);
    }

    internal static SyntaxNode? ThisNode(SyntaxNode node)
    {
        if (node is BinaryExpressionNode binary)
            return binary.Right;
        var target = node is CallExpressionNode or TaggedTemplateExpressionNode ? Target(node) : null;
        while (true)
            switch (target)
            {
                case ParenthesizedExpressionNode parentheses:
                    target = parentheses.Expression;
                    break;
                case AsExpressionNode assertion:
                    target = assertion.Expression;
                    break;
                case TypeAssertionNode assertion:
                    target = assertion.Expression;
                    break;
                case NonNullExpressionNode nonNull:
                    target = nonNull.Expression;
                    break;
                case SatisfiesExpressionNode satisfies:
                    target = satisfies.Expression;
                    break;
                case PropertyAccessExpressionNode property:
                    return property.Expression;
                case ElementAccessExpressionNode element:
                    return element.Expression;
                default:
                    return null;
            }
    }

    internal async ValueTask<Type> ThisTypeAsync(SyntaxNode? node, CancellationToken cancellation = default)
    {
        if (node is null)
            return context.VoidType;
        var type = await host.CheckExpressionAsync(node, 0, cancellation).ConfigureAwait(false);
        return await optional.ReceiverAsync(type, node, cancellation).ConfigureAwait(false);
    }
}
