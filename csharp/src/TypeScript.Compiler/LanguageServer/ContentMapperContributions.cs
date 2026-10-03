using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Protocol;
using TypeScript.Compiler.Semantics;
using static TypeScript.Compiler.LanguageServer.LspJson;

namespace TypeScript.Compiler.LanguageServer;

internal static class ContentMapperContributions
{
    internal static ContentMapper[] Parse(JsonElement values)
    {
        List<ContentMapper> mappers = [];
        HashSet<Utf8String> claimed = [];
        int index = 0;
        foreach (var value in Array(values))
        {
            var contributor = String(value, "contributorId"u8);
            if (contributor.IsEmpty) throw Error("content mapper contribution requires a contributorId");
            var identity = contributor + "["u8 + Utf8String.Format(index++) + "]"u8;
            var extensions = Array(Get(value, "extensions"u8)).Select(String).ToArray();
            foreach (var extension in extensions)
                if (extension.Length <= 1 || extension[0] != '.' || CompilerPath.Extension("file"u8 + extension) != extension
                    || new Utf8String[] { ".ts"u8, ".tsx"u8, ".mts"u8, ".cts"u8, ".js"u8, ".jsx"u8, ".mjs"u8, ".cjs"u8, ".json"u8 }
                        .Any(native => GoUnicode.EqualFold(GoUnicode.Runes(native.Span), GoUnicode.Runes(extension.Span))))
                    throw Error($"content mapper contribution {Quote(identity)} has invalid extension {Quote(extension)}");
            var inferred = Get(value, "inferredProjectContribution"u8);
            if (inferred.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) continue;
            var manifest = Get(inferred, "manifest"u8);
            var name = String(manifest, "name"u8);
            var exec = Array(Get(manifest, "exec"u8)).Select(String).ToArray();
            if (name.IsEmpty || exec.Length == 0) throw Error($"content mapper contribution {Quote(identity)} requires a manifest name and exec");
            var compilerOptions = Get(manifest, "compilerOptions"u8);
            foreach (var option in Array(compilerOptions).Select(String))
                if (OptionDefinitions.Find(option) is null)
                    throw Error($"content mapper contribution {Quote(identity)} requests unknown compiler option {Quote(option)}");
            foreach (var extension in extensions)
            {
                var lower = new StringBuilder();
                foreach (int rune in GoUnicode.Runes(extension.Span)) lower.Append(new Rune(GoUnicode.Lower(rune)));
                if (!claimed.Add(Utf8String.FromString(lower.ToString()))) throw Error($"content mapper contributions both claim extension {Quote(extension)}");
            }
            var cwd = Get(manifest, "cwd"u8);
            if (cwd.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) && !CompilerPath.IsAbsolute(String(cwd)))
                throw Error($"content mapper contribution {Quote(identity)} has non-absolute cwd");
            var options = Get(inferred, "options"u8);
            if (options.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                using var empty = JsonDocument.Parse("{}"); options = empty.RootElement.Clone();
            }
            mappers.Add(new(identity, extensions, options.Clone(), String(cwd), name, String(manifest, "version"u8), exec,
                compilerOptions.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : compilerOptions.Clone(), Boolean(manifest, "dynamicConfig"u8))
                { ContributionId = identity });
        }
        return mappers.ToArray();
    }

    private static string Quote(Utf8String value)
    {
        var text = new StringBuilder("\"");
        foreach (int point in GoUnicode.Runes(value.Span))
        {
            var escape = point switch { '"' => "\\\"", '\\' => "\\\\", '\a' => "\\a", '\b' => "\\b", '\f' => "\\f",
                '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\v' => "\\v", _ => null };
            if (escape is not null) text.Append(escape);
            else if (point < 32 || point == 127) text.Append("\\x").Append(point.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            else if (point != ' ' && Rune.GetUnicodeCategory(new Rune(point)) is System.Globalization.UnicodeCategory.Control
                or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.OtherNotAssigned
                or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.SpaceSeparator
                or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)
                text.Append(point <= 0xffff ? "\\u" : "\\U").Append(point.ToString(point <= 0xffff ? "x4" : "x8", System.Globalization.CultureInfo.InvariantCulture));
            else text.Append(new Rune(point));
        }
        return text.Append('"').ToString();
    }
    private static RpcException Error(string message) => new(-32603, Utf8String.FromString(message));
}
