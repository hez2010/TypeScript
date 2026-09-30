using System.Runtime.InteropServices;
using TypeScript.Compiler.Text;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using F = TypeScript.Compiler.Checking.TypeFlags;

namespace TypeScript.Compiler.Checking;

internal sealed partial class TypeAlgebra
{
    private readonly Dictionary<(TypeCacheKey Types, Utf8String Texts), TemplateLiteralType> templates = new()
    {
        [(new TypeCacheKey([context.NumberType]), Utf8Literals.EmptyFrame)] = context.NumericStringType
    };
    private readonly Dictionary<(Symbol Symbol, Type Target), StringMappingType> stringMappings = [];

    internal async ValueTask<Type> TemplateAsync(
        IReadOnlyList<Utf8String> texts,
        IReadOnlyList<Type> types,
        CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        RequireOwned(types);
        if (texts.Count != types.Count + 1)
            throw new ArgumentException("Template text/type arity mismatch");
        for (int i = 0; i < types.Count; i++)
            if ((types[i].Flags & (F.Never | F.Union)) != 0)
            {
                if (!CheckCrossProduct(types))
                    return context.ErrorType;
                int index = i;
                return await MapAsync(types[i], async replacement =>
                {
                    var replaced = types.ToArray();
                    replaced[index] = replacement;
                    return await TemplateAsync(texts, replaced, cancellation).ConfigureAwait(false);
                },
                    cancellation: cancellation).ConfigureAwait(false) ?? throw new InvalidOperationException("Template mapping removed all constituents");
            }
        if (types.Contains(context.WildcardType))
            return context.WildcardType;
        var newTypes = new List<Type>();
        var newTexts = new List<Utf8String>();
        var text = new Utf8StringBuilder().Append(texts[0].Span);
        var frames = new Stack<(IReadOnlyList<Utf8String> Texts, IReadOnlyList<Type> Types, int Index, Utf8String? Suffix)>();
        frames.Push((texts, types, 0, null));
        while (frames.TryPop(out var frame))
        {
            cancellation.ThrowIfCancellationRequested();
            if (frame.Index == frame.Types.Count)
            {
                if (frame.Suffix is not null)
                    text.Append(frame.Suffix);
                continue;
            }
            var type = frame.Types[frame.Index];
            Utf8String following = frame.Texts[frame.Index + 1];
            frames.Push((frame.Texts, frame.Types, frame.Index + 1, frame.Suffix));
            if ((type.Flags & (F.Literal | F.Nullable)) != 0)
            {
                text.Append(TemplateString(type).Span);
                text.Append(following.Span);
            }
            else if (type is TemplateLiteralType template)
            {
                text.Append(template.Texts[0].Span);
                frames.Push((template.Texts, template.Types, 0, following));
            }
            else if (await host.IsGenericIndexAsync(type, cancellation).ConfigureAwait(false) || IsPatternPlaceholder(type))
            {
                newTypes.Add(type);
                newTexts.Add(Wtf8.CombineSurrogatePairs(text.ToUtf8String()));
                text.Clear();
                text.Append(following.Span);
            }
            else
                return context.StringType;
        }
        if (newTypes.Count == 0)
            return context.GetStringLiteralType(Wtf8.CombineSurrogatePairs(text.ToUtf8String()));
        newTexts.Add(Wtf8.CombineSurrogatePairs(text.ToUtf8String()));
        if (newTexts.All(t => t.Length == 0))
        {
            if (newTypes.All(t => (t.Flags & F.String) != 0))
                return context.StringType;
            if (newTypes.Count == 1 && IsPatternLiteral(newTypes[0]))
                return newTypes[0];
        }
        // Length framing is unambiguous for embedded NULs and lone surrogates.
        var key = (new TypeCacheKey(newTypes.ToArray()), Utf8String.Frame(CollectionsMarshal.AsSpan(newTexts)));
        if (!templates.TryGetValue(key, out var result))
            templates.Add(key, result = context.NewTemplateLiteralType(newTexts.ToArray(), newTypes.ToArray()));
        return result;
    }

    internal static Utf8String TemplateString(Type type) => type is LiteralType literal ? literal.Value switch
    {
        Utf8String text => text,
        double number => TokenFacts.NumberText(number),
        BigInteger integer => Utf8String.Format(integer),
        bool boolean => boolean ? Utf8Literals.True : Utf8Literals.False,
        _ => Utf8String.Empty
    } : type is IntrinsicType intrinsic && (type.Flags & F.Nullable) != 0 ? intrinsic.IntrinsicName : Utf8String.Empty;

    internal async ValueTask<Type> StringMappingAsync(Symbol symbol, Type type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((type.Flags & (F.Union | F.Never)) != 0)
            return await MapAsync(
                type,
                async t => await StringMappingAsync(symbol, t, cancellation).ConfigureAwait(false),
                cancellation: cancellation).ConfigureAwait(false)
                ?? throw new InvalidOperationException("String mapping removed all constituents");
        if (type is LiteralType { Value: Utf8String value })
            return context.GetStringLiteralType(ApplyStringMapping(symbol.Name, value));
        if (type is TemplateLiteralType template)
        {
            var texts = template.Texts.ToArray();
            var types = template.Types.ToArray();
            if (symbol.Name.Span.SequenceEqual("Uppercase"u8) || symbol.Name.Span.SequenceEqual("Lowercase"u8))
            {
                for (int i = 0; i < texts.Length; i++)
                    texts[i] = ApplyStringMapping(symbol.Name, texts[i]);
                for (int i = 0; i < types.Length; i++)
                    types[i] = await StringMappingAsync(symbol, types[i], cancellation).ConfigureAwait(false);
            }
            else if (symbol.Name.Span.SequenceEqual("Capitalize"u8) || symbol.Name.Span.SequenceEqual("Uncapitalize"u8))
            {
                if (texts[0].Length != 0)
                    texts[0] = ApplyStringMapping(symbol.Name, texts[0]);
                else
                    types[0] = await StringMappingAsync(symbol, types[0], cancellation).ConfigureAwait(false);
            }
            return await TemplateAsync(texts, types, cancellation).ConfigureAwait(false);
        }
        if (type is StringMappingType mapping && mapping.Symbol == symbol)
            return type;
        if ((type.Flags & (F.Any | F.String | F.StringMapping)) != 0
            || await host.IsGenericIndexAsync(type, cancellation).ConfigureAwait(false))
            return GenericStringMapping(symbol, type);
        if (IsPatternPlaceholder(type))
            return GenericStringMapping(symbol, await TemplateAsync([Utf8String.Empty, Utf8String.Empty], [type], cancellation).ConfigureAwait(false));
        return type;
    }

    private StringMappingType GenericStringMapping(Symbol symbol, Type type)
    {
        if (!stringMappings.TryGetValue((symbol, type), out var result))
            stringMappings.Add((symbol, type), result = context.NewStringMappingType(symbol, type));
        return result;
    }

    internal static Utf8String ApplyStringMapping(Utf8String name, Utf8String text) => name.Span switch
    {
        _ when name.Span.SequenceEqual("Uppercase"u8) => JsCase.Upper(text),
        _ when name.Span.SequenceEqual("Lowercase"u8) => JsCase.Lower(text),
        _ when name.Span.SequenceEqual("Capitalize"u8) => Utf8String.Concat(JsCase.Upper(text[..JsCase.FirstScalarLength(text)]), text[JsCase.FirstScalarLength(text)..]),
        _ when name.Span.SequenceEqual("Uncapitalize"u8) => Utf8String.Concat(JsCase.Lower(text[..JsCase.FirstScalarLength(text)]), text[JsCase.FirstScalarLength(text)..]),
        _ => text
    };
}
