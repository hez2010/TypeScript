using System.Globalization;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface ISignatureParameterHost
{
    ValueTask<Type> IndexedAccessAsync(Type objectType, Type indexType, CancellationToken cancellation);

    ValueTask<Type?> ArrayElementAsync(Type type, CancellationToken cancellation);

    Type AnyArray { get; }
}

internal sealed class SignatureParameters(TypeContext context, TypeAlgebra algebra, SymbolTypes values, TupleTypes tuples,
    ISignatureParameterHost host)
{
    internal async ValueTask<Type> ParameterAsync(Symbol parameter, CancellationToken cancellation = default)
    {
        var type = await values.GetAsync(parameter, cancellation).ConfigureAwait(false);
        return context.StrictNullChecks && parameter.ValueDeclaration is ParameterDeclarationNode declaration
            && (declaration.Initializer is not null || declaration.QuestionToken is not null)
            ? await algebra.UnionAsync([type, context.UndefinedType], cancellation: cancellation).ConfigureAwait(false) : type;
    }

    internal async ValueTask<Type?> ThisAsync(Signature signature, CancellationToken cancellation = default)
    {
        RequireOwned(signature);
        cancellation.ThrowIfCancellationRequested();
        return signature.ThisParameter is { } parameter ? await values.GetAsync(parameter, cancellation).ConfigureAwait(false) : null;
    }

    internal async ValueTask<int> CountAsync(Signature signature, CancellationToken cancellation = default)
    {
        RequireOwned(signature);
        cancellation.ThrowIfCancellationRequested();
        int count = signature.Parameters.Count;
        if (signature.HasRestParameter
            && await values.GetAsync(
                signature.Parameters[^1],
                cancellation).ConfigureAwait(false) is TypeReference { Target: TupleType tuple })
            return count + tuple.FixedLength - ((tuple.CombinedFlags & ElementFlags.Variable) != 0 ? 0 : 1);
        return count;
    }

    internal async ValueTask<int> MinimumAsync(Signature signature, bool strongUntypedJs = false, bool voidIsRequired = false,
        CancellationToken cancellation = default)
    {
        RequireOwned(signature);
        cancellation.ThrowIfCancellationRequested();
        if (!voidIsRequired && signature.ResolvedMinArgumentCount != -1)
            return signature.ResolvedMinArgumentCount;
        int minimum = -1;
        if (signature.HasRestParameter
            && await values.GetAsync(
                signature.Parameters[^1],
                cancellation).ConfigureAwait(false) is TypeReference { Target: TupleType tuple })
        {
            int required = 0;
            while (required < tuple.ElementInfos.Count && (tuple.ElementInfos[required].Flags & ElementFlags.Required) != 0)
                required++;
            if (required == tuple.ElementInfos.Count)
                required = tuple.FixedLength;
            if (required > 0)
                minimum = signature.Parameters.Count - 1 + required;
        }
        if (minimum == -1)
        {
            if (!strongUntypedJs && (signature.Flags & SignatureFlags.IsUntypedSignatureInJSFile) != 0)
                return 0;
            minimum = signature.MinArgumentCount;
        }
        if (voidIsRequired)
            return minimum;
        for (int i = minimum - 1; i >= 0; i--)
        {
            var type = await AtAsync(signature, i, cancellation).ConfigureAwait(false);
            if (!(type is UnionType union ? union.Types.Any(t => (t.Flags & TypeFlags.Void) != 0) : (type.Flags & TypeFlags.Void) != 0))
                break;
            minimum = i;
        }
        cancellation.ThrowIfCancellationRequested();
        return signature.ResolvedMinArgumentCount = minimum;
    }

    internal async ValueTask<bool> HasRestAsync(Signature signature, CancellationToken cancellation = default)
    {
        RequireOwned(signature);
        cancellation.ThrowIfCancellationRequested();
        if (!signature.HasRestParameter)
            return false;
        var rest = await values.GetAsync(signature.Parameters[^1], cancellation).ConfigureAwait(false);
        return rest is not TypeReference { Target: TupleType tuple } || (tuple.CombinedFlags & ElementFlags.Variable) != 0;
    }

    internal async ValueTask<Type> AtAsync(Signature signature, int position, CancellationToken cancellation = default)
        => await TryAtAsync(signature, position, cancellation).ConfigureAwait(false) ?? context.AnyType;

    internal async ValueTask<Type?> TryAtAsync(Signature signature, int position, CancellationToken cancellation = default)
    {
        RequireOwned(signature);
        cancellation.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        int count = signature.Parameters.Count - (signature.HasRestParameter ? 1 : 0);
        if (position < count)
            return await ParameterAsync(signature.Parameters[position], cancellation).ConfigureAwait(false);
        if (signature.HasRestParameter)
        {
            var rest = await values.GetAsync(signature.Parameters[count], cancellation).ConfigureAwait(false);
            int index = position - count;
            if (rest is not TypeReference { Target: TupleType tuple }
                || (tuple.CombinedFlags & ElementFlags.Variable) != 0
                || index < tuple.FixedLength)
                return await host.IndexedAccessAsync(rest, context.GetNumberLiteralType(index), cancellation).ConfigureAwait(false);
        }
        return null;
    }

    internal async ValueTask<Type?> EffectiveRestAsync(Signature signature, CancellationToken cancellation = default)
    {
        RequireOwned(signature);
        cancellation.ThrowIfCancellationRequested();
        if (signature.HasRestParameter)
        {
            var rest = await values.GetAsync(signature.Parameters[^1], cancellation).ConfigureAwait(false);
            if (rest is not TypeReference { Target: TupleType tuple })
                return (rest.Flags & TypeFlags.Any) != 0 ? host.AnyArray : rest;
            if ((tuple.CombinedFlags & ElementFlags.Variable) != 0)
                return await tuples.SliceAsync((TypeReference)rest, tuple.FixedLength, cancellation: cancellation).ConfigureAwait(false);
        }
        return null;
    }

    internal async ValueTask<Type> RestAtAsync(
        Signature signature,
        int position,
        bool isReadonly = false,
        CancellationToken cancellation = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        int count = await CountAsync(signature, cancellation).ConfigureAwait(false);
        int minimum = await MinimumAsync(signature, cancellation: cancellation).ConfigureAwait(false);
        var rest = await EffectiveRestAsync(signature, cancellation).ConfigureAwait(false);
        if (rest is not null && position >= count - 1)
            return position == count - 1
                ? rest
                : await tuples.ArrayAsync(
                    await host.IndexedAccessAsync(rest, context.NumberType, cancellation).ConfigureAwait(false),
                    cancellation: cancellation).ConfigureAwait(false);
        int length = count - position;
        if (length <= 0)
            return await tuples.CreateAsync([], [], isReadonly, cancellation).ConfigureAwait(false);
        var types = new Type[length];
        var infos = new TupleElementInfo[length];
        for (int i = 0; i < length; i++)
        {
            ElementFlags flags;
            if (rest is null || i < length - 1)
            {
                types[i] = await AtAsync(signature, position + i, cancellation).ConfigureAwait(false);
                flags = position + i < minimum ? ElementFlags.Required : ElementFlags.Optional;
            }
            else
            {
                types[i] = rest;
                flags = ElementFlags.Variadic;
            }
            infos[i] = new(flags, await NameableAsync(signature, position + i, cancellation).ConfigureAwait(false));
        }
        return await tuples.CreateAsync(types, infos, isReadonly, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type> RestOrAnyAsync(Signature signature, int position, CancellationToken cancellation = default)
    {
        var rest = await RestAtAsync(signature, position, cancellation: cancellation).ConfigureAwait(false);
        var element = await host.ArrayElementAsync(rest, cancellation).ConfigureAwait(false);
        return element is not null && (element.Flags & TypeFlags.Any) != 0 ? context.AnyType : rest;
    }

    internal async ValueTask<SyntaxNode?> NameableAsync(Signature signature, int position, CancellationToken cancellation = default)
    {
        RequireOwned(signature);
        cancellation.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        int count = signature.Parameters.Count - (signature.HasRestParameter ? 1 : 0);
        if (position < count)
            return ValidLabel(signature.Parameters[position].ValueDeclaration);
        if (signature.HasRestParameter)
        {
            var parameter = signature.Parameters[count];
            var rest = await values.GetAsync(parameter, cancellation).ConfigureAwait(false);
            if (rest is TypeReference { Target: TupleType tuple })
                return position - count < tuple.ElementInfos.Count ? tuple.ElementInfos[position - count].LabeledDeclaration : null;
            return ValidLabel(parameter.ValueDeclaration);
        }
        return null;
    }

    internal async ValueTask<string> NameAsync(Signature signature, int position, CancellationToken cancellation = default)
    {
        RequireOwned(signature);
        cancellation.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        int count = signature.Parameters.Count - (signature.HasRestParameter ? 1 : 0);
        if (position < count)
            return signature.Parameters[position].Name;
        var parameter = signature.Parameters[count];
        var rest = await values.GetAsync(parameter, cancellation).ConfigureAwait(false);
        return rest is TypeReference { Target: TupleType tuple }
            ? Label(tuple.ElementInfos[position - count], parameter, position - count)
            : parameter.Name;
    }

    internal static string Label(TupleElementInfo element, Symbol? rest, int index)
    {
        if (element.LabeledDeclaration is { } declaration)
            return ((IdentifierNode)((INamedNode)declaration).Name!).Text;
        if (rest?.ValueDeclaration is ParameterDeclarationNode parameter)
            return BindingLabel(parameter, index, element.Flags);
        return (rest?.Name ?? "arg") + "_" + index.ToString(CultureInfo.InvariantCulture);
    }

    private static string BindingLabel(SyntaxNode node, int index, ElementFlags flags)
    {
        while (true)
        {
            bool rest = node is ParameterDeclarationNode { DotDotDotToken: not null } or BindingElementNode { DotDotDotToken: not null };
            var name = (node as INamedNode)?.Name;
            if (name is IdentifierNode identifier)
                return rest ? (flags & ElementFlags.Variable) != 0
                    ? identifier.Text
                    : identifier.Text + "_" + index.ToString(CultureInfo.InvariantCulture)
                    : (flags & ElementFlags.Fixed) != 0 ? identifier.Text : identifier.Text + "_n";
            if (rest && name is BindingPatternNode { Kind: SyntaxKind.ArrayBindingPattern, Elements: { } elements })
            {
                var last = elements.Count == 0 ? null : elements[^1];
                bool lastRest = last is BindingElementNode { DotDotDotToken: not null };
                int count = elements.Count - (lastRest ? 1 : 0);
                if (index < count && elements[index] is BindingElementNode item)
                {
                    node = item;
                    continue;
                }
                if (index >= count && lastRest)
                {
                    node = last!;
                    index -= count;
                    continue;
                }
            }
            return "arg_" + index.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static SyntaxNode? ValidLabel(SyntaxNode? node) =>
        node is NamedTupleMemberNode or ParameterDeclarationNode { Name: IdentifierNode } ? node : null;

    private void RequireOwned(Signature signature)
    {
        if (signature.Context != context)
            throw new ArgumentException("Signature belongs to another checker", nameof(signature));
    }
}
