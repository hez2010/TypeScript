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

public sealed record DiagnosticMessage(int Code, DiagnosticCategory Category, string Key, string Text,
    bool ReportsUnnecessary = false, bool ReportsDeprecated = false, bool ElidedInCompatibilityPyramid = false)
{
    public string Format(string? locale = null, params ReadOnlySpan<string> arguments) =>
        DiagnosticLocalization.Format(DiagnosticLocalization.Text(this, locale), arguments);
}

public sealed record Diagnostic(DiagnosticMessage Message, int Start, int Length, string[] Arguments)
{
    public int Code => Message.Code;
    public string? FileName { get; init; }
    public string? Source { get; init; }
    public IReadOnlyList<Diagnostic> RelatedInformation { get; init; } = [];

    public string Format(string? locale = null) => Message.Format(locale, Arguments);
}

public static class DiagnosticLocalization
{
    private static readonly ConcurrentDictionary<string, FrozenDictionary<string, string>> Locales = new(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenDictionary<int, DiagnosticMessage> ByCode = Messages.All.ToFrozenDictionary(m => m.Code);

    public static DiagnosticMessage GetMessage(int code) => ByCode[code];

    public static string Text(DiagnosticMessage message, string? locale)
    {
        string? supported = MatchLocale(locale);
        if (supported is null)
            return message.Text;
        var dictionary = Locales.GetOrAdd(supported, static key =>
        {
            using Stream source = typeof(DiagnosticLocalization).Assembly.GetManifestResourceStream($"TypeScript.Locales.{key}.json.gz")
                ?? throw new InvalidDataException($"Missing diagnostic locale {key}");
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using JsonDocument document = JsonDocument.Parse(gzip);
            return document.RootElement.EnumerateObject().ToFrozenDictionary(
                p => p.Name,
                p => p.Value.GetString()!,
                StringComparer.Ordinal);
        });
        return dictionary.GetValueOrDefault(message.Key, message.Text);
    }

    public static string? MatchLocale(string? locale)
    {
        if (string.IsNullOrEmpty(locale))
            return null;
        // BCP-47 language matching, including Chinese script/region distinctions.
        string tag = locale.Replace('_', '-').ToLowerInvariant();
        string language = tag.Split('-')[0];
        return language switch
        {
            "cs" => "cs-CZ",
            "de" => "de-DE",
            "es" => "es-ES",
            "fr" => "fr-FR",
            "it" => "it-IT",
            "ja" => "ja-JP",
            "ko" => "ko-KR",
            "pl" => "pl-PL",
            "pt" => "pt-BR",
            "ru" => "ru-RU",
            "tr" => "tr-TR",
            "zh" when tag.Contains("hant", StringComparison.Ordinal) || tag is "zh-tw" or "zh-hk" or "zh-mo" => "zh-TW",
            "zh" => "zh-CN",
            _ => null,
        };
    }

    public static string Format(string text, ReadOnlySpan<string> arguments)
    {
        if (arguments.IsEmpty)
            return text;
        var result = new StringBuilder(text.Length);
        int start = 0;
        while (start < text.Length)
        {
            int open = text.IndexOf('{', start);
            if (open < 0)
                break;
            int close = text.IndexOf('}', open + 1);
            if (close < 0)
                break;
            ReadOnlySpan<char> digits = text.AsSpan(open + 1, close - open - 1);
            if (digits.IsEmpty || digits.ContainsAnyExceptInRange('0', '9'))
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
            foreach (Rune rune in arguments[index].EnumerateRunes())
                result.Append(rune.ToString());
            start = close + 1;
        }
        return result.Append(text.AsSpan(start)).ToString();
    }
}
