using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : ITypeNormalizationHost, ITypeRelationHost, ITypeIdentityHost
{
    internal TypeNormalization Normalization { get; }
    internal RelationKeys RelationKeys { get; }
    internal TypeRelations Relations { get; }
    internal TypeIdentity Identity { get; }

    public ValueTask<Ternary> MappedRelationAsync(
        RelationOperation operation,
        MappedType source,
        MappedType target,
        CancellationToken cancellation)
            => Generics.MappedAsync(operation, source, target, cancellation);

    public ValueTask<Type> SimplifyAsync(Type type, bool writing, CancellationToken cancellation)
            => type is IndexedAccessType indexed ? Indexed.SimplifyAsync(indexed, writing, cancellation)
                : type is ConditionalType conditional ? Conditionals.SimplifyAsync(conditional, writing, cancellation)
                : ValueTask.FromResult(type);

    public ValueTask<Ternary> IdentityAsync(RelationOperation operation, Type source, Type target, CancellationToken cancellation)
            => Identity.CompareAsync(operation, source, target, cancellation);

    public async ValueTask<Ternary> RelatedAsync(
        RelationOperation operation,
        Type source,
        Type target,
        RecursionFlags recursion,
        IntersectionState intersection,
        CancellationToken cancellation)
    {
        TypeScript.Compiler.Diagnostics.CompilationCapture.ProbeMark mark = TypeScript.Compiler.Diagnostics.CompilationCapture.Mark();
        try
        {
            return await Structural.RelatedAsync(operation, source, target, recursion, intersection, cancellation).ConfigureAwait(false);
        }
        finally
        {
            TypeScript.Compiler.Diagnostics.CompilationCapture.Report(TypeScript.Compiler.Diagnostics.AllocationProbes.CheckerRelated, mark);
        }
    }

    private readonly Dictionary<(Symbol Source, Symbol Target), bool> enumRelations = [];

    public async ValueTask<bool> EnumRelatedAsync(
        Symbol source,
        Symbol target,
        CancellationToken cancellation,
        RelationOperation? operation = null)
    {
        cancellation.ThrowIfCancellationRequested();
        source = (source.Flags & SymbolFlags.EnumMember) != 0 ? program.Symbols.Parent(source)! : source;
        target = (target.Flags & SymbolFlags.EnumMember) != 0 ? program.Symbols.Parent(target)! : target;
        if (source == target)
            return true;
        if (source.Name != target.Name || (source.Flags & SymbolFlags.RegularEnum) == 0 || (target.Flags & SymbolFlags.RegularEnum) == 0)
            return false;
        var key = (source, target);
        if (enumRelations.TryGetValue(key, out bool cached) && (cached || operation is null))
            return cached;
        var targetType = await Values.GetAsync(target, cancellation).ConfigureAwait(false);
        foreach (var member in await Properties.GetAsync(
            await Values.GetAsync(source, cancellation).ConfigureAwait(false),
            cancellation).ConfigureAwait(false))
        {
            if ((member.Flags & SymbolFlags.EnumMember) == 0)
                continue;
            var other = await Properties.PropertyAsync(targetType, member.Name, cancellation: cancellation).ConfigureAwait(false);
            if (other is null || (other.Flags & SymbolFlags.EnumMember) == 0)
            {
                if (operation is not null)
                    operation.ExplainArguments(DiagnosticCode.Property0IsMissingInType1, TypeDisplay.SymbolName(member),
                        await TypeDisplay.GetAsync(
                            await Declared.GetAsync(target, cancellation),
                            NodeBuilderFlags.UseFullyQualifiedType,
                            cancellation));
                return enumRelations[key] = false;
            }
            var value = (await EnumValues.GetAsync(
                member.Declarations.OfType<EnumMemberNode>().First(),
                cancellation).ConfigureAwait(false)).Value;
            var otherValue = (await EnumValues.GetAsync(
                other.Declarations.OfType<EnumMemberNode>().First(),
                cancellation).ConfigureAwait(false)).Value;
            bool equal = value is double number && otherValue is double otherNumber ? number == otherNumber : Equals(value, otherValue);
            if (!equal && (value is not null && otherValue is not null || value is Utf8String || otherValue is Utf8String))
            {
                if (operation is not null)
                {
                    Utf8String ValueText(object v) => v switch
                    {
                        Utf8String text => QuoteSymbolText(text, '"', false),
                        double number => TokenFacts.NumberText(number),
                        bool boolean => boolean ? Utf8Literals.True : Utf8Literals.False,
                        System.Numerics.BigInteger integer => Utf8String.Concat(Utf8String.Format(integer), "n"u8),
                        _ => throw new InvalidOperationException("Unexpected enum constant")
                    };
                    if (value is not null && otherValue is not null)
                        operation.ExplainArguments(
                            DiagnosticCode.EachDeclarationOf01DiffersInItsValueWhere2WasExpectedBut3WasGiven,
                            TypeDisplay.SymbolName(target),
                            TypeDisplay.SymbolName(other),
                            ValueText(otherValue), ValueText(value));
                    else
                        operation.ExplainArguments(
                            DiagnosticCode.OneValueOf01IsTheString2AndTheOtherIsAssumedToBeAnUnknownNumericValue,
                            TypeDisplay.SymbolName(target),
                            TypeDisplay.SymbolName(other),
                            ValueText(value ?? otherValue!));
                }
                return enumRelations[key] = false;
            }
        }
        return enumRelations[key] = true;
    }

    public async ValueTask ComplexityOverflowAsync(Type source, Type target, CancellationToken cancellation)
    {
        Diagnostics.Add(DiagnosticCode.ExcessiveComplexityComparingTypes0And1);
        TrackDiagnostic(
            DiagnosticNode,
            DiagnosticCode.ExcessiveComplexityComparingTypes0And1,
            await TypeDisplay.GetAsync(source, cancellation),
            await TypeDisplay.GetAsync(target, cancellation));
    }

    public ValueTask<Type> ConditionalBranchAsync(ConditionalType type, bool whenTrue, CancellationToken cancellation)
        => whenTrue ? Instantiation.Constraints.ConditionalTrueAsync(type, cancellation: cancellation)
            : Instantiation.Constraints.ConditionalFalseAsync(type, cancellation);

    public ValueTask<Ternary?> VarianceAsync(RelationOperation operation, Type source, Type target, CancellationToken cancellation)
        => Variances.RelateAsync(operation, source, target, cancellation: cancellation);

    public ValueTask<Ternary?> VarianceAsync(RelationOperation operation, Type source, Type target,
        IntersectionState intersection, Func<ValueTask<Ternary>>? structuralFallback, CancellationToken cancellation)
        => Variances.RelateAsync(operation, source, target, intersection, cancellation, structuralFallback);
}
