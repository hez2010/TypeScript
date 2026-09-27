using System.Runtime.CompilerServices;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Checking;

internal sealed class TemplateMatching(TypeContext context, TypeAlgebra algebra, TypeConstraints constraints, TypeRelations relations)
{
    internal ValueTask<bool> MatchesAsync(
        Type source,
        TemplateLiteralType target,
        RelationOperation operation,
        CancellationToken cancellation = default)
        => MatchesAsync(source, target, (s, t, token) => operation.CompareWithoutErrorsAsync(s, t, cancellation: token), cancellation);

    internal async ValueTask<bool> MatchesAsync(Type source, TemplateLiteralType target,
        Func<Type, Type, CancellationToken, ValueTask<Ternary>> compare, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(
            RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        var inferences = await InferAsync(source, target, compare, cancellation).ConfigureAwait(false);
        if (inferences is null)
            return false;
        for (int i = 0; i < inferences.Count; i++)
            if (!await PlaceholderAsync(inferences[i], target.Types[i], compare, cancellation).ConfigureAwait(false))
                return false;
        return true;
    }

    internal ValueTask<IReadOnlyList<Type>?> InferAsync(
        Type source,
        TemplateLiteralType target,
        RelationOperation operation,
        CancellationToken cancellation = default)
        => InferAsync(source, target, (s, t, token) => operation.CompareWithoutErrorsAsync(s, t, cancellation: token), cancellation);

    internal async ValueTask<IReadOnlyList<Type>?> InferAsync(Type source, TemplateLiteralType target,
        Func<Type, Type, CancellationToken, ValueTask<Ternary>> compare, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(source);
        context.RequireOwned(target);
        if (source is LiteralType { Value: TextSlice literal })
            return await PartsAsync([literal], [], target, cancellation).ConfigureAwait(false);
        if (source is not TemplateLiteralType template)
            return null;
        if (!template.Texts.SequenceEqual(target.Texts))
            return await PartsAsync(template.Texts, template.Types, target, cancellation).ConfigureAwait(false);
        var result = new Type[template.Types.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var s = template.Types[i];
            result[i] = await compare(await constraints.BaseConstraintOrTypeAsync(s, cancellation).ConfigureAwait(false),
                await constraints.BaseConstraintOrTypeAsync(target.Types[i], cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false) != Ternary.False ? s
                : (s.Flags & (TypeFlags.Any | TypeFlags.StringLike)) != 0
                    ? s
                    : await algebra.TemplateAsync(["", ""], [s], cancellation).ConfigureAwait(false);
        }
        return Array.AsReadOnly(result);
    }

    private async ValueTask<IReadOnlyList<Type>?> PartsAsync(
        IReadOnlyList<TextSlice> texts,
        IReadOnlyList<Type> types,
        TemplateLiteralType target,
        CancellationToken cancellation)
    {
        int last = texts.Count - 1, lastTarget = target.Texts.Count - 1;
        TextSlice start = target.Texts[0], end = target.Texts[lastTarget];
        if (last == 0 && texts[0].Length < start.Length + end.Length
            || !texts[0].Span.StartsWith(start.Span) || !ScalarBoundary(texts[0].Span, start.Length)
            || !texts[last].Span.EndsWith(end.Span) || !ScalarBoundary(texts[last].Span, texts[last].Length - end.Length))
            return null;
        TextSlice remainder = texts[last][..^end.Length];
        int segment = 0, position = start.Length;
        var result = new List<Type>();
        TextSlice Text(int index) => index < last ? texts[index] : remainder;
        async ValueTask Add(int s, int p)
        {
            Type type;
            if (s == segment)
                type = context.GetStringLiteralType(Text(s)[position..p]);
            else
            {
                var parts = new TextSlice[s - segment + 1];
                parts[0] = texts[segment][position..];
                for (int i = segment + 1; i < s; i++)
                    parts[i - segment] = texts[i];
                parts[^1] = Text(s)[..p];
                type = await algebra.TemplateAsync(
                    parts,
                    types.Skip(segment).Take(s - segment).ToArray(),
                    cancellation).ConfigureAwait(false);
            }
            result.Add(type);
            segment = s;
            position = p;
        }
        for (int i = 1; i < lastTarget; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            TextSlice delimiter = target.Texts[i];
            if (delimiter.Length != 0)
            {
                int s = segment, p = position;
                while (true)
                {
                    int found = IndexOfScalarText(Text(s).Span[p..], delimiter.Span);
                    if (found >= 0)
                    {
                        p += found;
                        break;
                    }
                    if (++s == texts.Count)
                        return null;
                    p = 0;
                }
                await Add(s, p).ConfigureAwait(false);
                position += delimiter.Length;
            }
            else if (position < Text(segment).Length)
            {
                ReadOnlySpan<char> rest = Text(segment).Span[position..];
                int size = rest.Length >= 2 && char.IsSurrogatePair(rest[0], rest[1]) ? 2 : 1;
                await Add(segment, position + size).ConfigureAwait(false);
            }
            else if (segment < last)
                await Add(segment + 1, 0).ConfigureAwait(false);
            else
                return null;
        }
        await Add(last, Text(last).Length).ConfigureAwait(false);
        return result.AsReadOnly();
    }

    // WTF-8 substring matches cannot begin or end inside a paired scalar. A lone
    // surrogate remains a character, but must not match half of a supplementary rune.
    private static bool ScalarBoundary(ReadOnlySpan<char> text, int position) => position == 0 || position == text.Length
        || !char.IsSurrogatePair(text[position - 1], text[position]);

    private static int IndexOfScalarText(ReadOnlySpan<char> text, ReadOnlySpan<char> value)
    {
        int offset = 0;
        while (offset <= text.Length)
        {
            int found = text[offset..].IndexOf(value);
            if (found < 0)
                return -1;
            found += offset;
            if (ScalarBoundary(text, found) && ScalarBoundary(text, found + value.Length))
                return found;
            offset = found + 1;
        }
        return -1;
    }

    private async ValueTask<bool> PlaceholderAsync(
        Type source,
        Type target,
        Func<Type, Type, CancellationToken, ValueTask<Ternary>> compare,
        CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(
            RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        if (target is IntersectionType intersection)
        {
            foreach (var part in intersection.Types)
                if (part != context.EmptyTypeLiteralType
                    && !await PlaceholderAsync(source, part, compare, cancellation).ConfigureAwait(false))
                    return false;
            return true;
        }
        if ((target.Flags & TypeFlags.String) != 0
            || await compare(source, target, cancellation).ConfigureAwait(false) != Ternary.False)
            return true;
        if (source is LiteralType { Value: TextSlice value })
        {
            if ((target.Flags & TypeFlags.Number) != 0 && value.Length != 0 && double.IsFinite(JsNumber.FromString(value)))
                return true;
            if ((target.Flags & TypeFlags.BigInt) != 0 && BigInt(value))
                return true;
            if ((target.Flags & (TypeFlags.BooleanLiteral | TypeFlags.Nullable)) != 0)
            {
                TextSlice? text = target is IntrinsicType intrinsic
                    ? intrinsic.IntrinsicName
                    : target is LiteralType { Value: bool boolean } ? (TextSlice)(boolean ? "true" : "false") : (TextSlice?)null;
                if (value == text)
                    return true;
            }
            if (target is StringMappingType)
                return await MemberAsync(source, target, cancellation).ConfigureAwait(false);
            if (target is TemplateLiteralType template)
                return await MatchesAsync(source, template, compare, cancellation).ConfigureAwait(false);
        }
        return source is TemplateLiteralType { Texts.Count: 2 } sourceTemplate && sourceTemplate.Texts.All(t => t.Length == 0)
            && await compare(
                sourceTemplate.Types[0],
                target,
                cancellation).ConfigureAwait(false) != Ternary.False;
    }

    internal async ValueTask<bool> MemberAsync(Type source, Type target, CancellationToken cancellation = default)
    {
        if ((target.Flags & TypeFlags.Any) != 0)
            return true;
        if ((target.Flags & (TypeFlags.String | TypeFlags.TemplateLiteral)) != 0)
            return await relations.RelatedAsync(source, target, RelationKind.Assignable, cancellation).ConfigureAwait(false);
        if (target is not StringMappingType)
            return false;
        var mappings = new Stack<StringMappingType>();
        var inner = target;
        while (inner is StringMappingType mapping)
        {
            mappings.Push(mapping);
            inner = mapping.Target;
        }
        var result = source;
        while (mappings.TryPop(out var mapping))
            result = await algebra.StringMappingAsync(mapping.Symbol!, result, cancellation).ConfigureAwait(false);
        return result == source && await MemberAsync(source, inner, cancellation).ConfigureAwait(false);
    }

    internal static bool Unrelated(TemplateLiteralType source, TemplateLiteralType target)
    {
        var sourceStart = Wtf8.Encode(source.Texts[0]);
        var targetStart = Wtf8.Encode(target.Texts[0]);
        var sourceEnd = Wtf8.Encode(source.Texts[^1]);
        var targetEnd = Wtf8.Encode(target.Texts[^1]);
        int start = Math.Min(sourceStart.Length, targetStart.Length), end = Math.Min(sourceEnd.Length, targetEnd.Length);
        return !sourceStart.AsSpan(0, start).SequenceEqual(targetStart.AsSpan(0, start))
            || !sourceEnd.AsSpan(sourceEnd.Length - end).SequenceEqual(targetEnd.AsSpan(targetEnd.Length - end));
    }

    internal static bool BigInt(TextSlice value)
    {
        if (value.Length == 0)
            return false;
        var scanner = new Scanner(new SourceText(TextSlice.Concat(value, "n")), false);
        var token = scanner.Scan();
        if (token == SyntaxKind.MinusToken)
            token = scanner.Scan();
        return scanner.Diagnostics.Count == 0
            && token == SyntaxKind.BigIntLiteral
            && scanner.Position == value.Length + 1
            && (scanner.Flags & TokenFlags.ContainsSeparator) == 0;
    }
}
