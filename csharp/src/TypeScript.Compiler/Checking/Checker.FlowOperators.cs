using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private async ValueTask<Type> NarrowRelationalKeywordAsync(FlowState state, Type type, BinaryExpressionNode expression,
        bool assumeTrue, CancellationToken cancellation)
    {
        if (expression.OperatorToken!.Kind == SyntaxKind.InstanceOfKeyword)
        {
            var left = FlowReferences.Candidate(expression.Left!);
            if (!await FlowReferences.MatchesAsync(state.Reference, left, cancellation))
                return assumeTrue
                    && context.StrictNullChecks
                    && await FlowReferences.ContainsAsync(left, state.Reference, true, cancellation)
                    ? await Facts.AdjustAsync(type, TypeFacts.NEUndefinedOrNull, cancellation) : type;
            var rightType = await ExpressionAsync(expression.Right!, cancellation);
            if (!await DerivedAsync(rightType, GlobalObject, cancellation))
                return type;
            if (await FlowEffects.GetAsync(expression, cancellation) is { } signature
                && await Signatures.PredicateAsync(
                    signature,
                    cancellation) is { Kind: TypePredicateKind.Identifier, ParameterIndex: 0, Type: { } asserted })
                return await FlowNarrowing.NarrowedAsync(type, asserted, assumeTrue, true, cancellation);
            if (!await DerivedAsync(rightType, GlobalFunction, cancellation))
                return type;
            var instance = (await Algebra.MapAsync(
                rightType,
                async part => await InstanceTypeAsync(part, cancellation),
                cancellation: cancellation))!;
            if ((type.Flags & TypeFlags.Any) != 0 && (instance == GlobalObject || instance == GlobalFunction)
                || !assumeTrue && !((instance.Flags & TypeFlags.Object) != 0 && !await Views.EmptyAnonymousAsync(instance, cancellation)))
                return type;
            return await FlowNarrowing.NarrowedAsync(type, instance, assumeTrue, true, cancellation);
        }
        var target = FlowReferences.Candidate(expression.Right!);
        if (expression.Left is PrivateIdentifierNode)
        {
            if (!await FlowReferences.MatchesAsync(state.Reference, target, cancellation))
                return type;
            var symbol = ResolveReference(expression.Left, cancellation);
            if (symbol == UnknownSymbol)
                return type;
            var targetType = symbol.ValueDeclaration is { } declaration && SemanticSyntax.IsStatic(declaration)
                ? await Values.GetAsync(symbol.Parent!, cancellation) : await Declared.GetAsync(symbol.Parent!, cancellation);
            return await FlowNarrowing.NarrowedAsync(type, targetType, assumeTrue, true, cancellation);
        }
        if ((type == context.MissingType || type is UnionType union && union.Types.Contains(context.MissingType))
            && state.Reference is PropertyAccessExpressionNode or ElementAccessExpressionNode
            && await FlowReferences.MatchesAsync(FlowReferences.Receiver(state.Reference)!, target, cancellation))
        {
            var key = await ExpressionAsync(expression.Left!, cancellation);
            if ((key.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0
                && await AccessNames.GetAsync(state.Reference, cancellation) == MappedMembers.PropertyName(key))
                return await Facts.FilterAsync(type, assumeTrue ? TypeFacts.NEUndefined : TypeFacts.EQUndefined, cancellation);
        }
        if (!await FlowReferences.MatchesAsync(state.Reference, target, cancellation))
            return type;
        var nameType = await ExpressionAsync(expression.Left!, cancellation);
        if ((nameType.Flags & TypeFlags.StringOrNumberLiteralOrUnique) == 0)
            return type;
        string name = MappedMembers.PropertyName(nameType);
        foreach (var part in type is UnionType parts ? parts.Types : [type])
            if (await PropertyPresenceAsync(part, name, true, cancellation))
                return await Algebra.FilterAsync(type, part => PropertyPresenceAsync(part, name, assumeTrue, cancellation), cancellation);
        if (assumeTrue && await program.Globals.AliasAsync("Record", 2, Declared, cancellation) is { } record)
            return await Algebra.IntersectionAsync([type,
                await References.AliasInstantiationAsync(record, [nameType, context.UnknownType], cancellation: cancellation)],
                cancellation: cancellation);
        return type;
    }

    private async ValueTask<Type> InstanceTypeAsync(Type type, CancellationToken cancellation)
    {
        if (await Properties.PropertyAsync(type, "prototype", cancellation: cancellation) is { } property
            && await Values.GetAsync(property, cancellation) is { } prototype && (prototype.Flags & TypeFlags.Any) == 0)
            return prototype;
        var signatures = await SignaturesAsync(type, true, cancellation);
        if (signatures.Count == 0)
            return context.EmptyObjectType;
        var instances = new List<Type>();
        foreach (var signature in signatures)
            instances.Add(await Signatures.ReturnAsync(await SignatureAssignability.ErasedAsync(signature, cancellation), cancellation));
        return await Algebra.UnionAsync(instances, cancellation: cancellation);
    }

    private async ValueTask<bool> PropertyPresenceAsync(Type type, string name, bool assumeTrue, CancellationToken cancellation)
    {
        if (await Properties.PropertyAsync(type, name, cancellation: cancellation) is { } property)
            return (property.Flags & SymbolFlags.Optional) != 0 || (property.CheckFlags & CheckFlags.Partial) != 0 || assumeTrue;
        return await ApplicableIndexAsync(type, name, cancellation) is not null || !assumeTrue;
    }
}
