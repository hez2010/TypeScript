using TypeScript.Compiler.Text;
using TypeScript.Compiler.Configuration;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace TypeScript.Compiler.Diagnostics;

public enum DiagnosticCategory
{
    Warning,
    Error,
    Suggestion,
    Message
}

public sealed record DiagnosticMessage(DiagnosticCode Code, DiagnosticCategory Category, Utf8String Key, Utf8String Text,
    bool ReportsUnnecessary = false, bool ReportsDeprecated = false, bool ElidedInCompatibilityPyramid = false)
{
    public Utf8String Format(Utf8String? locale = null, params ReadOnlySpan<Utf8String> arguments) =>
        DiagnosticLocalization.Format(DiagnosticLocalization.Text(this, locale), arguments);
}

public sealed record Diagnostic(DiagnosticMessage Message, int Start, int Length, Utf8String[] Arguments)
{
    public DiagnosticCode Code => Message.Code;
    public Utf8String? FileName { get; init; }
    public Utf8String? Source { get; init; }
    public IReadOnlyList<Diagnostic> MessageChain { get; init; } = [];
    public IReadOnlyList<Diagnostic> RelatedInformation { get; init; } = [];

    public Utf8String Format(Utf8String? locale = null)
    {
        Utf8String text = Message.Format(locale, Arguments);
        if (MessageChain.Count == 0)
            return text;
        var result = new Utf8StringBuilder(text);
        var pending = new Stack<(Diagnostic Diagnostic, int Depth)>();
        for (int i = MessageChain.Count - 1; i >= 0; i--)
            pending.Push((MessageChain[i], 1));
        while (pending.TryPop(out var entry))
        {
            result.Append((byte)'\n').Append((byte)' ', entry.Depth * 2).Append(entry.Diagnostic.Message.Format(locale, entry.Diagnostic.Arguments));
            for (int i = entry.Diagnostic.MessageChain.Count - 1; i >= 0; i--)
                pending.Push((entry.Diagnostic.MessageChain[i], entry.Depth + 1));
        }
        return result.ToUtf8String();
    }
}

public static class DiagnosticLocalization
{
    private static readonly ConcurrentDictionary<Utf8String, FrozenDictionary<Utf8String, Utf8String>> Locales = new(Utf8StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenDictionary<DiagnosticCode, DiagnosticMessage> ByCode = Messages.All.ToFrozenDictionary(m => m.Code);

    public static DiagnosticMessage GetMessage(DiagnosticCode code) => ByCode[code];

    public static Utf8String Text(DiagnosticMessage message, Utf8String? locale)
    {
        Utf8String? supported = MatchLocale(locale);
        if (supported is not { } supportedLocale)
            return message.Text;
        var dictionary = Locales.GetOrAdd(supportedLocale, static key =>
        {
            using Stream source = typeof(DiagnosticLocalization).Assembly.GetManifestResourceStream($"TypeScript.Locales.{key}.json.gz")
                ?? throw new InvalidDataException($"Missing diagnostic locale {key}");
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using JsonDocument document = JsonDocument.Parse(gzip);
            return document.RootElement.EnumerateObject().ToFrozenDictionary(
                JsonStrings.GetName,
                p => JsonStrings.GetString(p.Value),
                Utf8StringComparer.Ordinal);
        });
        return dictionary.GetValueOrDefault(message.Key, message.Text);
    }

    public static Utf8String? MatchLocale(Utf8String? locale)
    {
        if (locale is not { IsEmpty: false } localeText)
            return null;
        // BCP-47 language matching, including Chinese script/region distinctions.
        Utf8String tag = localeText.Replace((byte)'_', (byte)'-').ToLowerInvariant();
        int separator = tag.IndexOf((byte)'-');
        Utf8String language = separator < 0 ? tag : tag[..separator];
        return language switch
        {
            _ when language == "cs"u8 => Utf8Literals.CsCZ,
            _ when language == "de"u8 => Utf8Literals.DeDE,
            _ when language == "es"u8 => Utf8Literals.EsES,
            _ when language == "fr"u8 => Utf8Literals.FrFR,
            _ when language == "it"u8 => Utf8Literals.ItIT,
            _ when language == "ja"u8 => Utf8Literals.JaJP,
            _ when language == "ko"u8 => Utf8Literals.KoKR,
            _ when language == "pl"u8 => Utf8Literals.PlPL,
            _ when language == "pt"u8 => Utf8Literals.PtBR,
            _ when language == "ru"u8 => Utf8Literals.RuRU,
            _ when language == "tr"u8 => Utf8Literals.TrTR,
            _ when language == "zh"u8 && (tag.Contains("hant"u8, StringComparison.Ordinal) || tag == "zh-tw"u8 || tag == "zh-hk"u8 || tag == "zh-mo"u8) => Utf8Literals.ZhTW,
            _ when language == "zh"u8 => Utf8Literals.ZhCN,
            _ => (Utf8String?)null,
        };
    }

    public static Utf8String Format(Utf8String text, ReadOnlySpan<Utf8String> arguments)
    {
        if (arguments.IsEmpty)
            return text;
        var result = new Utf8StringBuilder(text.Length);
        int start = 0;
        while (start < text.Length)
        {
            int open = text.IndexOf((byte)'{', start);
            if (open < 0)
                break;
            int close = text.IndexOf((byte)'}', open + 1);
            if (close < 0)
                break;
            ReadOnlySpan<byte> digits = text.AsSpan(open + 1, close - open - 1);
            if (digits.IsEmpty || digits.ContainsAnyExceptInRange((byte)'0', (byte)'9'))
            {
                result.Append(text.AsSpan(start, open - start + 1));
                start = open + 1;
                continue;
            }
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                || (uint)index >= (uint)arguments.Length)
                throw new ArgumentException("Invalid diagnostic placeholder", nameof(arguments));
            result.Append(text.AsSpan(start, open - start));
            // Diagnostic output is valid Unicode; source/literal storage remains lossless WTF-8.
            ReadOnlySpan<byte> argument = arguments[index].Span;
            while (!argument.IsEmpty)
            {
                int point = Wtf8.Decode(argument, out int width);
                result.AppendCodePoint(Rune.IsValid(point) ? point : 0xFFFD);
                argument = argument[width..];
            }
            start = close + 1;
        }
        return result.Append(text.AsSpan(start)).ToUtf8String();
    }
}
