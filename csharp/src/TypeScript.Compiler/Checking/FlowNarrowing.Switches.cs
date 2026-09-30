using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class FlowNarrowing
{
    private sealed class SwitchData
    {
        internal Type[]? Types { get; set; }
        internal Utf8String[]? Witnesses { get; set; }
        internal bool WitnessesComputed { get; set; }
        internal int Exhaustive { get; set; }
    }

    private readonly Dictionary<SwitchStatementNode, SwitchData> switches = [];

    private SwitchData SwitchLinks(SwitchStatementNode node)
    {
        if (!switches.TryGetValue(node, out var data))
            switches.Add(node, data = new());
        return data;
    }

    internal async ValueTask<Type> SwitchAsync(FlowState state, Type type, FlowNode flow, CancellationToken cancellation = default)
    {
        var statement = (SwitchStatementNode)flow.Node!;
        var expression = SkipParentheses(statement.Expression!);
        if (await references.MatchesAsync(state.Reference, expression, cancellation).ConfigureAwait(false))
            return await SwitchDiscriminantAsync(type, flow, cancellation).ConfigureAwait(false);
        if (expression is TypeOfExpressionNode typeOf
            && await references.MatchesAsync(state.Reference, typeOf.Expression!, cancellation).ConfigureAwait(false))
            return await SwitchTypeofAsync(type, flow, cancellation).ConfigureAwait(false);
        if (expression.Kind == SyntaxKind.TrueKeyword)
            return await SwitchTrueAsync(state, type, flow, cancellation).ConfigureAwait(false);
        if (context.StrictNullChecks)
        {
            if (await references.ContainsAsync(expression, state.Reference, true, cancellation).ConfigureAwait(false))
                type = await SwitchOptionalAsync(
                    type,
                    flow,
                    t => (t.Flags & (TypeFlags.Undefined | TypeFlags.Never)) == 0,
                    cancellation).ConfigureAwait(false);
            else if (expression is TypeOfExpressionNode optionalTypeof
                && await references.ContainsAsync(optionalTypeof.Expression!, state.Reference, true, cancellation).ConfigureAwait(false))
                type = await SwitchOptionalAsync(type, flow, t => (t.Flags & TypeFlags.Never) == 0
                    && !(t is LiteralType { Value: Utf8String { Span: var matchedText } } && matchedText.SequenceEqual("undefined"u8) && (t.Flags & TypeFlags.StringLiteral) != 0),
                    cancellation).ConfigureAwait(false);
        }
        if (await DiscriminantAccessAsync(state, type, expression, cancellation).ConfigureAwait(false) is { } access)
        {
            if (flow.ClauseStart < flow.ClauseEnd && type is UnionType union
                && await host.AccessNameAsync(access, cancellation).ConfigureAwait(false) is { Length: > 0 } name
                && await discriminants.KeyAsync(union, cancellation).ConfigureAwait(false) == name)
            {
                var clauses = await ClauseTypesAsync(statement, cancellation).ConfigureAwait(false);
                var candidates = clauses[flow.ClauseStart..flow.ClauseEnd].Select(
                    t => union.ConstituentMap!.GetValueOrDefault(t) ?? context.UnknownType).ToArray();
                var candidate = await algebra.UnionAsync(candidates, cancellation: cancellation).ConfigureAwait(false);
                if (candidate != context.UnknownType)
                    return candidate;
            }
            return await PropertyAsync(
                type,
                access,
                t => SwitchDiscriminantAsync(t, flow, cancellation),
                cancellation).ConfigureAwait(false);
        }
        return type;
    }

    private async ValueTask<Type> SwitchDiscriminantAsync(Type type, FlowNode flow, CancellationToken cancellation)
    {
        var types = await ClauseTypesAsync((SwitchStatementNode)flow.Node!, cancellation).ConfigureAwait(false);
        if (types.Length == 0)
            return type;
        var clauses = types[flow.ClauseStart..flow.ClauseEnd];
        bool hasDefault = flow.ClauseStart == flow.ClauseEnd || clauses.Contains(context.NeverType);
        if ((type.Flags & TypeFlags.Unknown) != 0 && !hasDefault)
        {
            var ground = new Type[clauses.Length];
            for (int i = 0; i < clauses.Length; i++)
                if ((clauses[i].Flags & (TypeFlags.Primitive | TypeFlags.NonPrimitive)) != 0)
                    ground[i] = clauses[i];
                else if ((clauses[i].Flags & TypeFlags.Object) != 0)
                    ground[i] = context.NonPrimitiveType;
                else
                    return type;
            return await algebra.UnionAsync(ground, cancellation: cancellation).ConfigureAwait(false);
        }
        var discriminant = await algebra.UnionAsync(clauses, cancellation: cancellation).ConfigureAwait(false);
        Type? caseType = (discriminant.Flags & TypeFlags.Never) != 0 ? context.NeverType : null;
        if (caseType is null && (discriminant.Flags & TypeFlags.Primitive) != 0 && Uniform(type))
        {
            var regular = await algebra.RegularTypeAsync(discriminant, cancellation).ConfigureAwait(false);
            if (algebra.UnionContains((UnionType)type, regular, false))
                caseType = regular;
        }
        caseType ??= await ReplacePrimitivesAsync(await algebra.FilterAsync(type,
            t => ComparableAsync(discriminant, t, cancellation),
            cancellation).ConfigureAwait(false),
            discriminant,
            cancellation).ConfigureAwait(false);
        if (!hasDefault)
            return caseType;
        var defaultType = await algebra.FilterAsync(type, async part =>
        {
            if (!await UnitLikeAsync(part, cancellation).ConfigureAwait(false))
                return true;
            Type unit = context.UndefinedType;
            if ((part.Flags & TypeFlags.Undefined) == 0)
                unit = await algebra.RegularTypeAsync(
                    part is IntersectionType intersection ? intersection.Types.FirstOrDefault(t => t.IsUnit) ?? part : part,
                    cancellation).ConfigureAwait(false);
            foreach (var clause in types)
                if (clause.IsUnit && await ComparableAsync(clause, unit, cancellation).ConfigureAwait(false))
                    return false;
            return true;
        }, cancellation).ConfigureAwait(false);
        return (caseType.Flags & TypeFlags.Never) != 0 ? defaultType
            : await algebra.UnionAsync([caseType, defaultType], cancellation: cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Type> SwitchTypeofAsync(Type type, FlowNode flow, CancellationToken cancellation)
    {
        var statement = (SwitchStatementNode)flow.Node!;
        if (Witnesses(statement) is not { } witnesses)
            return type;
        if (HasDefault(statement, flow))
        {
            var exclude = ExcludeFacts(flow.ClauseStart, flow.ClauseEnd, witnesses);
            return await algebra.FilterAsync(
                type,
                async t => await facts.GetAsync(t, exclude, cancellation).ConfigureAwait(false) == exclude,
                cancellation).ConfigureAwait(false);
        }
        List<Type> types = [];
        for (int i = flow.ClauseStart; i < flow.ClauseEnd; i++)
            types.Add(
                witnesses[i].Length == 0
                    ? context.NeverType
                    : await TypeNameAsync(type, witnesses[i], true, cancellation).ConfigureAwait(false));
        return await algebra.UnionAsync(types, cancellation: cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Type> SwitchTrueAsync(FlowState state, Type type, FlowNode flow, CancellationToken cancellation)
    {
        var statement = (SwitchStatementNode)flow.Node!;
        var clauses = statement.CaseBlock!.Clauses!;
        for (int i = 0; i < flow.ClauseStart; i++)
            if (clauses[i] is CaseOrDefaultClauseNode { Kind: SyntaxKind.CaseClause } clause)
                type = await NarrowAsync(state, type, clause.Expression!, false, cancellation).ConfigureAwait(false);
        if (HasDefault(statement, flow))
        {
            for (int i = flow.ClauseEnd; i < clauses.Count; i++)
                if (clauses[i] is CaseOrDefaultClauseNode { Kind: SyntaxKind.CaseClause } clause)
                    type = await NarrowAsync(state, type, clause.Expression!, false, cancellation).ConfigureAwait(false);
            return type;
        }
        List<Type> types = [];
        for (int i = flow.ClauseStart; i < flow.ClauseEnd; i++)
            types.Add(clauses[i] is CaseOrDefaultClauseNode { Kind: SyntaxKind.CaseClause } clause
                ? await NarrowAsync(state, type, clause.Expression!, true, cancellation).ConfigureAwait(false) : context.NeverType);
        return await algebra.UnionAsync(types, cancellation: cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Type> SwitchOptionalAsync(Type type, FlowNode flow, Func<Type, bool> predicate, CancellationToken cancellation)
        => flow.ClauseStart != flow.ClauseEnd
            && (await ClauseTypesAsync(
                (SwitchStatementNode)flow.Node!,
                cancellation).ConfigureAwait(false))[flow.ClauseStart..flow.ClauseEnd].All(predicate)
            ? await facts.FilterAsync(type, TypeFacts.NEUndefinedOrNull, cancellation).ConfigureAwait(false) : type;

    internal async ValueTask<bool> ExhaustiveAsync(SwitchStatementNode statement, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var data = SwitchLinks(statement);
        if (data.Exhaustive == 0)
        {
            data.Exhaustive = 1;
            try
            {
                bool result = await ComputeExhaustiveAsync(statement, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if (data.Exhaustive == 1)
                    data.Exhaustive = result ? 3 : 2;
            }
            catch
            {
                data.Exhaustive = 0;
                throw;
            }
        }
        else if (data.Exhaustive == 1)
            data.Exhaustive = 2;
        return data.Exhaustive == 3;
    }

    private async ValueTask<bool> ComputeExhaustiveAsync(SwitchStatementNode statement, CancellationToken cancellation)
    {
        if (statement.Expression is TypeOfExpressionNode typeOf)
        {
            if (Witnesses(statement) is not { } witnesses)
                return false;
            var constraint = await constraints.BaseConstraintOrTypeAsync(
                await host.CachedFlowExpressionAsync(typeOf.Expression!, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);
            var exclude = ExcludeFacts(0, 0, witnesses);
            if ((constraint.Flags & TypeFlags.AnyOrUnknown) != 0)
                return (TypeFacts.AllTypeofNE & exclude) == TypeFacts.AllTypeofNE;
            foreach (var part in constraint is UnionType union ? union.Types : (IReadOnlyList<Type>)[constraint])
                if (await facts.GetAsync(part, exclude, cancellation).ConfigureAwait(false) == exclude)
                    return false;
            return true;
        }
        var type = await constraints.BaseConstraintOrTypeAsync(
            await host.CachedFlowExpressionAsync(statement.Expression!, cancellation).ConfigureAwait(false),
            cancellation).ConfigureAwait(false);
        if (!DiscriminantRelations.Literal(type))
            return false;
        var types = await ClauseTypesAsync(statement, cancellation).ConfigureAwait(false);
        if (types.Length == 0 || types.Any(t => (t.Flags & (TypeFlags.Unit | TypeFlags.Never)) == 0))
            return false;
        var regular = await algebra.MapAsync(
            type,
            async t => await algebra.RegularTypeAsync(t, cancellation).ConfigureAwait(false),
            cancellation: cancellation).ConfigureAwait(false) ?? context.NeverType;
        return regular is UnionType regularUnion ? regularUnion.Types.All(types.Contains) : types.Contains(regular);
    }

    private async ValueTask<Type[]> ClauseTypesAsync(SwitchStatementNode statement, CancellationToken cancellation)
    {
        var data = SwitchLinks(statement);
        if (data.Types is not null)
            return data.Types;
        var clauses = statement.CaseBlock!.Clauses!;
        var types = new Type[clauses.Count];
        for (int i = 0; i < clauses.Count; i++)
            types[i] = clauses[i] is CaseOrDefaultClauseNode { Kind: SyntaxKind.CaseClause } clause
                ? await algebra.RegularTypeAsync(
                    await host.ExpressionAsync(clause.Expression!, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false) : context.NeverType;
        cancellation.ThrowIfCancellationRequested();
        return data.Types = types;
    }

    private Utf8String[]? Witnesses(SwitchStatementNode statement)
    {
        var data = SwitchLinks(statement);
        if (data.WitnessesComputed)
            return data.Witnesses;
        var clauses = statement.CaseBlock!.Clauses!;
        Utf8String[]? witnesses = new Utf8String[clauses.Count];
        for (int i = 0; i < clauses.Count; i++)
            if (clauses[i] is CaseOrDefaultClauseNode { Kind: SyntaxKind.CaseClause } clause)
            {
                if (StringLike(clause.Expression!) is not { } text)
                {
                    witnesses = null;
                    break;
                }
                if (!witnesses.Contains(text))
                    witnesses[i] = text;
            }
        data.WitnessesComputed = true;
        return data.Witnesses = witnesses;
    }

    private static TypeFacts ExcludeFacts(int start, int end, Utf8String[] witnesses)
    {
        TypeFacts result = 0;
        for (int i = 0; i < witnesses.Length; i++)
            if ((i < start || i >= end) && witnesses[i].Length != 0)
                result |= witnesses[i].Span switch
                {
                    var matchedText2 when matchedText2.SequenceEqual("string"u8) => TypeFacts.TypeofNEString,
                    var matchedText3 when matchedText3.SequenceEqual("number"u8) => TypeFacts.TypeofNENumber,
                    var matchedText4 when matchedText4.SequenceEqual("bigint"u8) => TypeFacts.TypeofNEBigInt,
                    var matchedText5 when matchedText5.SequenceEqual("boolean"u8) => TypeFacts.TypeofNEBoolean,
                    var matchedText6 when matchedText6.SequenceEqual("symbol"u8) => TypeFacts.TypeofNESymbol,
                    var matchedText7 when matchedText7.SequenceEqual("undefined"u8) => TypeFacts.NEUndefined,
                    var matchedText8 when matchedText8.SequenceEqual("object"u8) => TypeFacts.TypeofNEObject,
                    var matchedText9 when matchedText9.SequenceEqual("function"u8) => TypeFacts.TypeofNEFunction,
                    _ => TypeFacts.TypeofNEHostObject
                };
        return result;
    }

    private static bool HasDefault(SwitchStatementNode statement, FlowNode flow)
        =>
            flow.ClauseStart == flow.ClauseEnd
                || statement.CaseBlock!.Clauses!.Skip(flow.ClauseStart).Take(flow.ClauseEnd - flow.ClauseStart).Any(n => n.Kind == SyntaxKind.DefaultClause);

    private static SyntaxNode SkipParentheses(SyntaxNode node)
    {
        while (node is ParenthesizedExpressionNode parent)
            node = parent.Expression!;
        return node;
    }
}
