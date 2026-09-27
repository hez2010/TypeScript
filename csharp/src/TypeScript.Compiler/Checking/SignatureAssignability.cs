using System.Runtime.CompilerServices;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface ISignatureAssignabilityHost
{
    bool StrictFunctionTypes { get; }

    bool IsArray(Type type);

    ValueTask<Type?> ArrayElementAsync(Type type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Signature>> SignaturesAsync(Type type, bool construct, CancellationToken cancellation);

    ValueTask<StructuredType> ResolveAsync(StructuredType type, CancellationToken cancellation);

    ValueTask<Signature> ContextualInstantiationAsync(
        Signature source,
        Signature target,
        RelationOperation operation,
        CancellationToken cancellation);

    ValueTask<Type> NonNullableAsync(Type type, CancellationToken cancellation);

    ValueTask<bool> SameNullableFactsAsync(Type source, Type target, CancellationToken cancellation);

    ValueTask<bool> IsGenericTypeAsync(Type type, CancellationToken cancellation);

    ValueTask ReportUnreliableAsync(Type type, CancellationToken cancellation);
}

internal sealed class SignatureAssignability(TypeContext context, SignatureParameters parameters, Signatures signatures,
    TypeInstantiation instantiation, ISignatureAssignabilityHost host)
{
    private readonly Dictionary<Signature, Signature> erased = [];

    internal async ValueTask<Ternary> OfTypesAsync(RelationOperation operation, Type source, Type target, bool construct,
        IntersectionState intersection, CancellationToken cancellation = default)
    {
        if (source == context.AnyFunctionType)
            return Ternary.True;
        if (target == context.AnyFunctionType)
            return Ternary.False;
        var sources = await host.SignaturesAsync(source, construct, cancellation).ConfigureAwait(false);
        var targets = await host.SignaturesAsync(target, construct, cancellation).ConfigureAwait(false);
        if (construct && sources.Count != 0 && targets.Count != 0)
        {
            if ((sources[0].Flags & SignatureFlags.Abstract) != 0 && (targets[0].Flags & SignatureFlags.Abstract) == 0)
            {
                operation.Explain(DiagnosticCode.CannotAssignAnAbstractConstructorTypeToANonAbstractConstructorType);
                return Ternary.False;
            }
            if (!Visible(sources[0], targets[0]))
            {
                if (operation.ReportErrors)
                    operation.ExplainArguments(
                        DiagnosticCode.CannotAssignA0ConstructorTypeToA1ConstructorType,
                        Visibility(sources[0]),
                        Visibility(targets[0]));
                return Ternary.False;
            }
        }
        Ternary result = Ternary.True;
        if ((source.ObjectFlags & ObjectFlags.Instantiated) != 0
            && (target.ObjectFlags & ObjectFlags.Instantiated) != 0
            && source.Symbol == target.Symbol
            || source is TypeReference sr
                && target is TypeReference tr
                && (source.ObjectFlags & ObjectFlags.Reference) != 0
                && (target.ObjectFlags & ObjectFlags.Reference) != 0
                && sr.Target == tr.Target)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var related = await PairAsync(operation, sources[i], targets[i], true, intersection, cancellation).ConfigureAwait(false);
                if (related == Ternary.False)
                    return related;
                result &= related;
            }
            return result;
        }
        if (sources.Count == 1 && targets.Count == 1)
            return await PairAsync(
                operation,
                sources[0],
                targets[0],
                operation.Kind == RelationKind.Comparable,
                intersection,
                cancellation).ConfigureAwait(false);
        foreach (var targetSignature in targets)
        {
            var previousExplanation = operation.Explanation;
            bool first = true;
            Ternary related = Ternary.False;
            foreach (var sourceSignature in sources)
            {
                related = operation.ReportErrors && !first
                    ? await operation.WithoutErrorsAsync(
                        () => PairAsync(
                            operation,
                            sourceSignature,
                            targetSignature,
                            true,
                            intersection,
                            cancellation)).ConfigureAwait(false)
                    : await PairAsync(operation, sourceSignature, targetSignature, true, intersection, cancellation).ConfigureAwait(false);
                if (related != Ternary.False)
                {
                    operation.RestoreExplanation(previousExplanation);
                    break;
                }
                first = false;
            }
            if (related == Ternary.False)
            {
                if (first && operation.ReportErrors)
                    operation.ExplainArguments(DiagnosticCode.Type0ProvidesNoMatchForTheSignature1, source, targetSignature);
                return related;
            }
            result &= related;
        }
        return result;
    }

    private async ValueTask<Ternary> PairAsync(
        RelationOperation operation,
        Signature source,
        Signature target,
        bool erase,
        IntersectionState intersection,
        CancellationToken cancellation)
    {
        var mode = operation.Kind == RelationKind.Subtype ? SignatureCheckMode.StrictTopSignature
            : operation.Kind == RelationKind.StrictSubtype ? SignatureCheckMode.StrictTopSignature | SignatureCheckMode.StrictArity : 0;
        if (erase)
        {
            source = await ErasedAsync(source, cancellation).ConfigureAwait(false);
            target = await ErasedAsync(target, cancellation).ConfigureAwait(false);
        }
        return await CompareAsync(operation, source, target, mode, intersection, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Signature> ErasedAsync(Signature signature, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (signature.Context != context)
            throw new ArgumentException("Signature belongs to another checker", nameof(signature));
        if (signature.TypeParameters.Count == 0)
            return signature;
        if (erased.TryGetValue(signature, out var cached))
            return cached;
        var result = await instantiation.SignatureAsync(
            signature,
            TypeMapper.ToSingle(signature.TypeParameters.ToArray(), context.AnyType),
            true,
            cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return erased[signature] = result;
    }

    internal async ValueTask<Ternary> CompareAsync(
        RelationOperation operation,
        Signature source,
        Signature target,
        SignatureCheckMode mode = 0,
        IntersectionState intersection = 0,
        CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(
            RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (source.Context != context || target.Context != context)
            throw new ArgumentException("Signature belongs to another checker");
        if (source == target)
            return Ternary.True;
        if (!((mode & SignatureCheckMode.StrictTopSignature) != 0 && await TopAsync(source, cancellation).ConfigureAwait(false))
            && await TopAsync(target, cancellation).ConfigureAwait(false))
            return Ternary.True;
        if ((mode & SignatureCheckMode.StrictTopSignature) != 0
            && await TopAsync(source, cancellation).ConfigureAwait(false)
            && !await TopAsync(target, cancellation).ConfigureAwait(false))
            return Ternary.False;
        int targetCount = await parameters.CountAsync(target, cancellation).ConfigureAwait(false);
        if (!await parameters.HasRestAsync(target, cancellation).ConfigureAwait(false)
            && ((mode & SignatureCheckMode.StrictArity) != 0 ? await parameters.HasRestAsync(source, cancellation).ConfigureAwait(false)
                || await parameters.CountAsync(source, cancellation).ConfigureAwait(false) > targetCount
                : await parameters.MinimumAsync(source, cancellation: cancellation).ConfigureAwait(false) > targetCount))
        {
            if (operation.ReportErrors && (mode & SignatureCheckMode.StrictArity) == 0)
                operation.ExplainArguments(
                    DiagnosticCode.TargetSignatureProvidesTooFewArgumentsExpected0OrMoreButGot1,
                    await parameters.MinimumAsync(source, cancellation: cancellation).ConfigureAwait(false),
                    targetCount);
            return Ternary.False;
        }
        if (source.TypeParameters.Count != 0 && !ReferenceEquals(source.TypeParameters, target.TypeParameters))
            source = await host.ContextualInstantiationAsync(source, target, operation, cancellation).ConfigureAwait(false);
        int sourceCount = await parameters.CountAsync(source, cancellation).ConfigureAwait(false);
        var sourceRest = await NonArrayRestAsync(source, cancellation).ConfigureAwait(false);
        var targetRest = await NonArrayRestAsync(target, cancellation).ConfigureAwait(false);
        if ((sourceRest ?? targetRest) is { } rest)
            await host.ReportUnreliableAsync(rest, cancellation).ConfigureAwait(false);
        var kind = target.Declaration?.Kind ?? SyntaxKind.Unknown;
        bool strict = (mode & SignatureCheckMode.Callback) == 0
            && host.StrictFunctionTypes
            && kind is not (SyntaxKind.MethodDeclaration or SyntaxKind.MethodSignature or SyntaxKind.Constructor);
        Ternary result = Ternary.True;
        var sourceThis = await parameters.ThisAsync(source, cancellation).ConfigureAwait(false);
        if (sourceThis is not null
            && sourceThis != context.VoidType
            && await parameters.ThisAsync(target, cancellation).ConfigureAwait(false) is { } targetThis)
        {
            var related = strict
                ? Ternary.False
                : await operation.CompareWithoutErrorsAsync(
                    sourceThis,
                    targetThis,
                    intersection: intersection,
                    cancellation: cancellation).ConfigureAwait(false);
            if (related == Ternary.False)
                related = await operation.CompareAsync(
                    targetThis,
                    sourceThis,
                    intersection: intersection,
                    cancellation: cancellation).ConfigureAwait(false);
            if (related == Ternary.False)
            {
                operation.Explain(DiagnosticCode.TheThisTypesOfEachSignatureAreIncompatible);
                return related;
            }
            result &= related;
        }
        bool nonArrayRest = sourceRest is not null || targetRest is not null;
        int count = nonArrayRest
            ? Math.Min(sourceCount, targetCount)
            : Math.Max(sourceCount, targetCount), restIndex = nonArrayRest ? count - 1 : -1;
        for (int i = 0; i < count; i++)
        {
            var s = i == restIndex
                ? await parameters.RestOrAnyAsync(source, i, cancellation).ConfigureAwait(false)
                : await parameters.TryAtAsync(source, i, cancellation).ConfigureAwait(false);
            var t = i == restIndex
                ? await parameters.RestOrAnyAsync(target, i, cancellation).ConfigureAwait(false)
                : await parameters.TryAtAsync(target, i, cancellation).ConfigureAwait(false);
            if (s is null || t is null || s == t && (mode & SignatureCheckMode.StrictArity) == 0)
                continue;
            Signature? sourceCallback = null, targetCallback = null;
            if ((mode & SignatureCheckMode.Callback) == 0)
            {
                if (!await InstantiatedGenericParameterAsync(source, i, cancellation).ConfigureAwait(false))
                    sourceCallback = await SingleCallAsync(s, cancellation).ConfigureAwait(false);
                if (!await InstantiatedGenericParameterAsync(target, i, cancellation).ConfigureAwait(false))
                    targetCallback = await SingleCallAsync(t, cancellation).ConfigureAwait(false);
            }
            bool callbacks = sourceCallback is not null && targetCallback is not null
                && await signatures.PredicateAsync(
                    sourceCallback,
                    cancellation).ConfigureAwait(false) is null && await signatures.PredicateAsync(
                        targetCallback,
                        cancellation).ConfigureAwait(false) is null
                && await host.SameNullableFactsAsync(s, t, cancellation).ConfigureAwait(false);
            Ternary related;
            if (callbacks)
                related = await CompareAsync(operation, targetCallback!, sourceCallback!, mode & SignatureCheckMode.StrictArity
                | (strict ? SignatureCheckMode.StrictCallback : SignatureCheckMode.BivariantCallback),
                    intersection,
                    cancellation).ConfigureAwait(false);
            else
            {
                related = (mode & SignatureCheckMode.Callback) == 0 && !strict
                    ? await operation.CompareWithoutErrorsAsync(
                        s,
                        t,
                        intersection: intersection,
                        cancellation: cancellation).ConfigureAwait(false)
                    : Ternary.False;
                if (related == Ternary.False)
                    related = await operation.CompareAsync(
                        t,
                        s,
                        intersection: intersection,
                        cancellation: cancellation).ConfigureAwait(false);
            }
            if (related != Ternary.False
                && (mode & SignatureCheckMode.StrictArity) != 0
                && i >= await parameters.MinimumAsync(source, cancellation: cancellation).ConfigureAwait(false)
                && i < await parameters.MinimumAsync(target, cancellation: cancellation).ConfigureAwait(false)
                && await operation.CompareWithoutErrorsAsync(
                    s,
                    t,
                    intersection: intersection,
                    cancellation: cancellation).ConfigureAwait(false) != Ternary.False)
                related = Ternary.False;
            if (related == Ternary.False)
            {
                if (operation.ReportErrors)
                    operation.ExplainArguments(
                        DiagnosticCode.TypesOfParameters0And1AreIncompatible,
                        await parameters.NameAsync(source, i, cancellation).ConfigureAwait(false),
                        await parameters.NameAsync(target, i, cancellation).ConfigureAwait(false));
                return related;
            }
            result &= related;
        }
        if ((mode & SignatureCheckMode.IgnoreReturnTypes) != 0)
            return result;
        var targetReturn = await signatures.NonCircularReturnAsync(target, cancellation).ConfigureAwait(false);
        if (targetReturn == context.VoidType || targetReturn == context.AnyType)
            return result;
        var sourceReturn = await signatures.NonCircularReturnAsync(source, cancellation).ConfigureAwait(false);
        var targetPredicate = await signatures.PredicateAsync(target, cancellation).ConfigureAwait(false);
        if (targetPredicate is not null)
        {
            var sourcePredicate = await signatures.PredicateAsync(source, cancellation).ConfigureAwait(false);
            if (sourcePredicate is not null)
            {
                if (sourcePredicate.Kind != targetPredicate.Kind)
                {
                    operation.Explain(DiagnosticCode.AThisBasedTypeGuardIsNotCompatibleWithAParameterBasedTypeGuard);
                    if (operation.ReportErrors)
                        operation.ExplainArguments(DiagnosticCode.TypePredicate0IsNotAssignableTo1, sourcePredicate, targetPredicate);
                    return Ternary.False;
                }
                if (sourcePredicate.Kind is TypePredicateKind.Identifier or TypePredicateKind.AssertsIdentifier
                    && sourcePredicate.ParameterIndex != targetPredicate.ParameterIndex)
                {
                    if (operation.ReportErrors)
                    {
                        operation.ExplainArguments(
                            DiagnosticCode.Parameter0IsNotInTheSamePositionAsParameter1,
                            sourcePredicate.ParameterName,
                            targetPredicate.ParameterName);
                        operation.ExplainArguments(DiagnosticCode.TypePredicate0IsNotAssignableTo1, sourcePredicate, targetPredicate);
                    }
                    return Ternary.False;
                }
                var related = sourcePredicate.Type == targetPredicate.Type ? Ternary.True
                    : sourcePredicate.Type is not null && targetPredicate.Type is not null
                        ? await operation.CompareAsync(
                            sourcePredicate.Type,
                            targetPredicate.Type,
                            intersection: intersection,
                            cancellation: cancellation).ConfigureAwait(false)
                        : Ternary.False;
                if (related == Ternary.False && operation.ReportErrors)
                    operation.ExplainArguments(DiagnosticCode.TypePredicate0IsNotAssignableTo1, sourcePredicate, targetPredicate);
                result &= related;
            }
            else if (targetPredicate.Kind is TypePredicateKind.Identifier or TypePredicateKind.This)
            {
                if (operation.ReportErrors)
                    operation.ExplainArguments(DiagnosticCode.Signature0MustBeATypePredicate, source);
                return Ternary.False;
            }
        }
        else
        {
            var related = (mode & SignatureCheckMode.BivariantCallback) != 0
                ? await operation.CompareWithoutErrorsAsync(
                    targetReturn,
                    sourceReturn,
                    intersection: intersection,
                    cancellation: cancellation).ConfigureAwait(false)
                : Ternary.False;
            if (related == Ternary.False)
                related = await operation.CompareAsync(
                    sourceReturn,
                    targetReturn,
                    intersection: intersection,
                    cancellation: cancellation).ConfigureAwait(false);
            result &= related;
            if (result == Ternary.False)
            {
                bool construct = (source.Flags & SignatureFlags.Construct) != 0;
                operation.Explain(source.Parameters.Count == 0 && target.Parameters.Count == 0
                    ? construct
                        ? DiagnosticCode.ConstructSignaturesWithNoArgumentsHaveIncompatibleReturnTypes0And1
                        : DiagnosticCode.CallSignaturesWithNoArgumentsHaveIncompatibleReturnTypes0And1 : construct
                            ? DiagnosticCode.ConstructSignatureReturnTypes0And1AreIncompatible
                            : DiagnosticCode.CallSignatureReturnTypes0And1AreIncompatible,
                    sourceReturn,
                    targetReturn);
            }
        }
        return result;
    }

    private async ValueTask<bool> TopAsync(Signature signature, CancellationToken cancellation)
    {
        if (signature.TypeParameters.Count != 0
            || signature.ThisParameter is { } receiver
                && ((await parameters.ParameterAsync(receiver, cancellation).ConfigureAwait(false)).Flags & TypeFlags.Any) == 0
            || signature.Parameters.Count != 1 || !signature.HasRestParameter)
            return false;
        var type = await parameters.ParameterAsync(signature.Parameters[0], cancellation).ConfigureAwait(false);
        var rest = host.IsArray(type) ? await host.ArrayElementAsync(type, cancellation).ConfigureAwait(false) : type;
        return rest is not null
            && (rest.Flags & (TypeFlags.Any | TypeFlags.Never)) != 0
            && ((await signatures.ReturnAsync(signature, cancellation).ConfigureAwait(false)).Flags & TypeFlags.AnyOrUnknown) != 0;
    }

    private async ValueTask<Type?> NonArrayRestAsync(Signature signature, CancellationToken cancellation)
    {
        var type = await parameters.EffectiveRestAsync(signature, cancellation).ConfigureAwait(false);
        return type is not null && !host.IsArray(type) && (type.Flags & TypeFlags.Any) == 0 ? type : null;
    }

    private async ValueTask<bool> InstantiatedGenericParameterAsync(Signature signature, int position, CancellationToken cancellation)
            =>
                signature.Target is { } target
                    && await parameters.TryAtAsync(target, position, cancellation).ConfigureAwait(false) is { } type
                    && await host.IsGenericTypeAsync(type, cancellation).ConfigureAwait(false);

    private async ValueTask<Signature?> SingleCallAsync(Type type, CancellationToken cancellation)
    {
        type = await host.NonNullableAsync(type, cancellation).ConfigureAwait(false);
        if (type is not ObjectType obj)
            return null;
        var resolved = await host.ResolveAsync(obj, cancellation).ConfigureAwait(false);
        return resolved.Properties is null or { Count: 0 }
            && resolved.IndexInfos.Count == 0
            && resolved.CallSignatures.Count == 1
            && resolved.ConstructSignatures.Count == 0
            ? resolved.CallSignatures[0] : null;
    }

    private static bool Visible(Signature source, Signature target)
    {
        if (source.Declaration is null || target.Declaration is null)
            return true;
        bool sourcePrivate = SemanticSyntax.HasModifier(
            source.Declaration,
            SyntaxKind.PrivateKeyword), sourceProtected = SemanticSyntax.HasModifier(source.Declaration, SyntaxKind.ProtectedKeyword);
        bool targetPrivate = SemanticSyntax.HasModifier(
            target.Declaration,
            SyntaxKind.PrivateKeyword), targetProtected = SemanticSyntax.HasModifier(target.Declaration, SyntaxKind.ProtectedKeyword);
        return targetPrivate || targetProtected && !sourcePrivate || !targetProtected && !sourcePrivate && !sourceProtected;
    }

    private static string Visibility(Signature signature) => signature.Declaration is not { } declaration ? "public"
        : SemanticSyntax.HasModifier(declaration, SyntaxKind.PrivateKeyword) ? "private"
        : SemanticSyntax.HasModifier(declaration, SyntaxKind.ProtectedKeyword) ? "protected" : "public";
}
