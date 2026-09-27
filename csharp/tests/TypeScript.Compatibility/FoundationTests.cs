using System.Runtime.InteropServices;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class FoundationTests
{
    public static void Run(string repository)
    {
        int assertions = 0;
        void Check(bool valid, string message)
        {
            assertions++;
            if (!valid)
                throw new InvalidDataException(message);
        }
        foreach (var message in Messages.All)
        {
            Check(ReferenceEquals(message, DiagnosticLocalization.GetMessage(message.Code)), $"Diagnostic identity {message.Code}");
            Check(message.Format() == message.Text, $"Diagnostic fallback {message.Code}");
        }
        foreach (string locale in new[]
        {
            "cs-CZ",
            "de-DE",
            "es-ES",
            "fr-FR",
            "it-IT",
            "ja-JP",
            "ko-KR",
            "pl-PL",
            "pt-BR",
            "ru-RU",
            "tr-TR",
            "zh-CN",
            "zh-TW"
        })
            Check(
                Messages.Unterminated_string_literal.Format(locale) != Messages.Unterminated_string_literal.Text,
                $"Native locale {locale}");
        Check(
            Messages.Unterminated_string_literal.Format("ja") == Messages.Unterminated_string_literal.Format("ja-JP"),
            "Locale language fallback");
        Check(
            DiagnosticLocalization.Format("a {0}, {1}, {{0}}", ["😀", "\ud800"]) == "a 😀, �, {😀}",
            "Diagnostic interpolation and malformed UTF-16");
        var sourceBytes = Wtf8.Encode("a😀\r\n名字\u2028z");
        var source = new SourceText(sourceBytes);
        sourceBytes[0] = 0;
        Check(source.Text.Span.StartsWith('a') && source.Bytes.Span[0] == 'a', "Source owns bytes");
        Check(source.GetLineAndCharacter(source.ToBytePosition(7)) == (1, 2), "Byte/UTF-16/line mapping");
        Check(SourceEncoding.Decode([0xFF, 0xFE, 0, 0xD8, 0x41, 0]) == "\ud800A", "UTF-16 LE source preserves surrogate");
        Check(SourceEncoding.Decode([0xFE, 0xFF, 0xD8, 0, 0, 0x41]) == "\ud800A", "UTF-16 BE source preserves surrogate");
        Check(SourceEncoding.Decode([0xEF, 0xBB, 0xBF, 0x41]) == "A", "UTF-8 BOM");
        byte[] destination = new byte[16];
        destination.AsSpan().Fill(0xCC);
        int encodedLength = Wtf8.Encode("A\ud800😀", destination.AsSpan(1, 8));
        Check(encodedLength == 8 && destination.AsSpan(1, encodedLength).SequenceEqual(
            new byte[] { 0x41, 0xED, 0xA0, 0x80, 0xF0, 0x9F, 0x98, 0x80 }), "Span encoding preserves WTF-8");
        Check(destination[0] == 0xCC && destination[9] == 0xCC, "Span encoding stays within destination");
        Check(Wtf8.Encode("", Span<byte>.Empty) == 0, "Empty span encoding");
        foreach (string text in new[] { "😀", "\ud800" })
        {
            bool rejected = false;
            try
            { Wtf8.Encode(text, destination.AsSpan(0, 2)); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Span encoding rejects a short destination");
        }
        foreach (string text in new[] { "", "ASCII", "名字😀", "\ud800", "\udfff", "\ud800\ud800\udfff\udfff" })
        {
            var fromString = new SourceText(text);
            var fromBytes = new SourceText(Wtf8.Encode(text));
            Check(MemoryMarshal.TryGetString(fromString.Text.Memory, out string? owner, out int offset, out int length)
                && ReferenceEquals(owner, text) && offset == 0 && length == text.Length && fromString.Text == fromBytes.Text, "Source retains UTF-16 string");
            Check(fromString.Bytes.Span.SequenceEqual(fromBytes.Bytes.Span)
                && fromString.Bytes.Length == Encoding.UTF8.GetByteCount(text), "Lossless source byte count");
            for (int i = 0; i <= text.Length; i++)
                Check(fromString.ToBytePosition(i) == fromBytes.ToBytePosition(i), "String source positions");
        }
        const string code = "const shared = 'shared'; const other = shared; const escaped = 'a\\nb';";
        var tree = Parser.ParseSourceFile(new("slices.ts"), new SourceText(code));
        foreach (var identifier in tree.DescendantsAndSelf().OfType<IdentifierNode>())
            Check(MemoryMarshal.TryGetString(identifier.Text.Memory, out string? owner, out int offset, out int length)
                && ReferenceEquals(owner, code) && code.AsSpan(offset, length).SequenceEqual(identifier.Text.Span),
                "Identifiers borrow the original source");
        var literals = tree.DescendantsAndSelf().OfType<StringLiteralNode>().ToArray();
        Check(MemoryMarshal.TryGetString(literals[0].Text.Memory, out string? literalOwner, out _, out _)
            && ReferenceEquals(literalOwner, code), "Unescaped literals borrow the source");
        Check(literals[1].Text == "a\nb" && !MemoryMarshal.TryGetString(literals[1].Text.Memory, out _, out _, out _),
            "Decoded text owns a character buffer");
        TextSlice firstName = new SourceText("first:shared").Text[6..];
        TextSlice secondName = new SourceText("second:shared").Text[7..];
        var names = new Dictionary<TextSlice, int> { [firstName] = 1 };
        Check(names[secondName] == 1 && names[TextSlice.Copy("shared")] == 1, "Slice keys compare by content across owners");
        Check(default(TextSlice) == "" && TextSlice.FromNullable(null) is null
            && TextSlice.FromNullable("") is { IsEmpty: true }, "Missing and empty text remain distinct");
        Check(TextSlice.Frame(["a", "bc"]) != TextSlice.Frame(["ab", "c"])
            && TextSlice.Frame(["\0", "\ud800"]) == "1:\0" + "1:\ud800", "Slice cache framing preserves boundaries");
        foreach (string locale in new[] { "en", "fr_fr", "ja-JP", "zh-Hant-TW", "i-klingon", "x-private", "en-US-u-ca-gregory", "en-t-h0" })
            Check(LocaleIdentifier.IsValid(locale), "Locale without OS globalization");
        foreach (string locale in new[] { "", "invalid-value", "zz", "en-foobar", "en-u", "en-u-ca-gregory-ca-buddhist" })
            Check(!LocaleIdentifier.IsValid(locale), "Invalid locale without OS globalization");
        foreach (var (input, expected) in new[]
        {
            ("a/./b/../c", "a/c"),
            ("c:\\a\\..\\b", "c:/b"),
            ("file:///c:/a/../../b", "file:///c:/b"),
            ("http://host/a/../b/", "http://host/b/"),
            ("^/untitled/../a", "^/a"),
            ("../../x", "../../x")
        })
            Check(CompilerPath.Normalize(input) == expected, $"Path normalize {input}");
        Check(CompilerPath.RootLength("file:///c%3a/a") == 13, "URL encoded drive root");
        Check(CompilerPath.Relative("/a/b", "/a/c/d", true) == "../c/d", "Relative path");
        Check(!CompilerPath.Contains("/a/b", "/a/bad", true), "Path component boundary");
        Check(CompilerPath.Combine("/a", "/b", "c") == "/b/c", "Absolute path overrides prior path");
        Check(!CompilerPath.IsAbsolute("C:relative"), "Drive-relative path");
        string longSegment = new('a', 300);
        Check(CompilerPath.Normalize($"/{longSegment}/../b/") == "/b/", "Pooled path normalization");
        Check(CompilerPath.Normalize($"../../{longSegment}/..") == "../..", "Pooled relative path normalization");
        Check(CompilerPath.Relative($"/{longSegment}/b/", $"/{longSegment}/c/", true) == "../c", "Long relative paths");
        Check(CompilerPath.Contains($"/{longSegment}/", $"/{longSegment}/b", true), "Long path containment");
        foreach (var (number, expected) in new (double, string)[]
        {
            (1e-7, "1e-7"), (1e-6, "0.000001"), (-1e-6, "-0.000001"),
            (1e20, "100000000000000000000"), (-1e20, "-100000000000000000000"),
            (1e21, "1e+21"), (double.Epsilon, "5e-324"),
            (double.MaxValue, "1.7976931348623157e+308")
        })
            Check(TokenFacts.NumberText(number) == expected, "Span numeric formatting boundary");

        var fs = new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/tsconfig.json"] = Encoding.UTF8.GetBytes(
                "{ // JSONC\n\"extends\": \"./base\", \"compilerOptions\": {\"strict\": null, \"outDir\":\"dist\"}, \"include\":[\"src/**/*\"], }"),
            ["/project/base.json"] = Encoding.UTF8.GetBytes("{\"compilerOptions\":{\"strict\":true,\"target\":\"esnext\"}}"),
            ["/project/src/main.ts"] = Encoding.UTF8.GetBytes("export const value = 1;"),
            ["/project/src/nested/child.ts"] = Encoding.UTF8.GetBytes("export {};"),
            ["/project/src/ignored.js"] = Encoding.UTF8.GetBytes(""),
            ["/project/node_modules/pkg/index.ts"] = Encoding.UTF8.GetBytes(""),
            ["/project/a.rsp"] = Encoding.UTF8.GetBytes("--strict false @b.rsp \"space name.ts\""),
            ["/project/b.rsp"] = Encoding.UTF8.GetBytes("@a.rsp --target esnext --outDir output"),
        });
        byte[] read = fs.ReadFile("/project/src/main.ts")!;
        read[0] = 0;
        Check(fs.ReadFile("/project/src/main.ts")![0] == 'e', "VFS read ownership");
        Check(fs.GetAccessibleEntries("/project/src").Directories.SequenceEqual(["nested"]), "VFS directory entries");
        fs.WriteFile("/project/surrogate-\ud800.ts", [1, 2]);
        fs.AppendFile("/project/surrogate-\ud800.ts", [3]);
        Check(fs.ReadFile("/project/surrogate-\ud800.ts")!.SequenceEqual(new byte[] { 1, 2, 3 }), "VFS surrogate path and append");
        var parsed = new CommandLineParser(fs, "/project").Parse(["@a.rsp", "--noEmit", "--strictNullChecks", "--not-an-option"]);
        Check(parsed.Diagnostics is [{ Code: DiagnosticCode.UnknownCompilerOption0 }], "CLI unknown option");
        Check(parsed.Options.Boolean("strict") == false && parsed.Options.Boolean("noEmit") == true, "CLI boolean values");
        Check(parsed.Options.String("outDir") == "/project/output" && parsed.Options.String("target") == "esnext", "CLI paths and enums");
        Check(parsed.FileNames.SequenceEqual(["space name.ts"]), "Response file cycles and quoted filename");
        var configParser = new ConfigParser(fs, "/project");
        Check(configParser.FindConfig("/project/src/nested") == "/project/tsconfig.json", "Find parent config");
        ParsedConfig config = configParser.Parse("tsconfig.json");
        Check(config.Diagnostics.Length == 0, "Config diagnostics: " + string.Join("; ", config.Diagnostics.Select(d => d.Format())));
        Check(
            config.Options.Boolean("strict") is null && config.Options.Get("strict")?.ValueKind == System.Text.Json.JsonValueKind.Null,
            "Explicit null overrides inherited true");
        Check(config.Options.String("outDir") == "/project/dist" && config.Options.String("target") == "esnext", "Inherited options");
        Check(
            config.FileNames.SequenceEqual(["/project/src/main.ts", "/project/src/nested/child.ts"]),
            "Recursive include, extensions and node_modules exclusion");
        fs.WriteFile("/project/nested/tsconfig.json", Encoding.UTF8.GetBytes("{\"extends\":\"../tsconfig\"}"));
        Check(
            configParser.Parse("nested/tsconfig.json").FileNames.SequenceEqual(config.FileNames),
            "Inherited include keeps base config directory");
        fs.WriteFile("/project/cycle.json", Encoding.UTF8.GetBytes("{\"extends\":\"./cycle\"}"));
        Check(
            configParser.Parse("cycle.json").Diagnostics.Any(
                d => d.Code == DiagnosticCode.CircularityDetectedWhileResolvingConfigurationColon0),
            "Config inheritance cycle diagnostic");
        var directives = TestDirectives.Parse(
            "// @target: es6, es2015, esnext\n// @strict: *, -false\n// @filename: a.ts\nconst a = 1;\n// @symlink: link.ts\n// @filename: b.ts\nexport {};",
            "test.ts");
        Check(directives.Units.Length == 2 && directives.Units[0].Content == "const a = 1;", "Test unit expansion");
        Check(directives.Symlinks["link.ts"] == "a.ts", "Test symlink directive");
        var variations = TestDirectives.Expand(directives.Options, new HashSet<string> { "target", "strict" });
        Check(variations.Count == 2 && variations.All(c => c["strict"] == "true"), "Test wildcard/exclusions and enum aliases");

        var libraries = new LibraryFileSystem(new PhysicalFileSystem());
        string originalLibraries = Path.Combine(repository, "tsc/internal/bundled/libs");
        int libraryCount = 0;
        foreach (string reference in Directory.EnumerateFiles(originalLibraries, "*.d.ts"))
        {
            string name = Path.GetFileName(reference), path = CompilerPath.Combine(libraries.LibraryDirectory, name);
            byte[] expected = File.ReadAllBytes(reference), actual = libraries.ReadFile(path) ?? throw new FileNotFoundException(path);
            Check(actual.AsSpan().SequenceEqual(expected), "Library bytes " + name);
            Check(libraries.Stat(path)?.Length == expected.Length, "Library size " + name);
            libraryCount++;
        }
        Check(libraryCount > 100, "Library inventory");
        if (LibraryFileSystem.Embedded)
        {
            try
            {
                libraries.WriteFile(CompilerPath.Combine(libraries.LibraryDirectory, "lib.d.ts"), []);
                throw new InvalidDataException("Writable bundled library");
            }
            catch (UnauthorizedAccessException)
            {
                assertions++;
            }
        }
        var factory = new NodeFactory();
        SyntaxNode deep = factory.NewKeywordTypeNode(SyntaxKind.StringKeyword);
        for (int i = 0; i < 20000; i++)
            deep = factory.NewParenthesizedTypeNode(deep);
        deep.SetParents();
        var clone = deep.DeepClone<ParenthesizedTypeNode>();
        Check(clone.DescendantsAndSelf().Count() == 20001 && !ReferenceEquals(clone.GetChild(0), deep.GetChild(0)), "Deep typed AST clone");
        Check(clone.GetChild(0).Parent == clone, "Clone parent ownership");
        var unicode = Parser.ParseSourceFile(new("/unicode.ts"), new SourceText("const 日本語 = '😀';"));
        Check(unicode.ParseDiagnostics.Count == 0 && unicode.Statements!.End == unicode.Source.Bytes.Length, "AST list byte positions");
        const string documentedText = "/** @template T\n * @param {T} value input\n * @returns {T} result\n */\nfunction identity(value) { return value; }";
        var documented = Parser.ParseSourceFile(new("/documented.js"), new SourceText(documentedText));
        var function = (FunctionDeclarationNode)documented.Statements![0];
        Check(function.TypeParameters?.Count == 1 && function.Type is TypeReferenceNode, "JSDoc template and return annotation");
        Check(function.Parameters![0] is ParameterDeclarationNode { Type: TypeReferenceNode }, "JSDoc parameter annotation");
        var documentation = documented.GetDocumentation(function);
        Check(
            documentation.Count == 1 && documentation[0].Tags?.Count == 3 && documentation[0].Parent == function,
            "JSDoc source ownership and tags");
        Parallel.For(0, 32, _ =>
        {
            if (!ReferenceEquals(documented.GetDocumentation(function)[0], documentation[0]))
                throw new InvalidDataException("JSDoc query identity changed");
        });
        assertions++;
        var documentedClone = documented.DeepClone<SourceFileNode>();
        Check(documentedClone.ReparsedClones.Count == documented.ReparsedClones.Count, "Cloned source retains reparse mapping");
        Check(
            !ReferenceEquals(documentedClone.GetDocumentation(documentedClone.Statements![0])[0], documentation[0]),
            "Cloned source owns its documentation cache");
        var declarationDocs = Parser.ParseSourceFile(new("/documented.ts"), new SourceText("/** A value. {@link Other} */\nconst x = 1;"));
        var lazyDocs = declarationDocs.GetDocumentation(declarationDocs.Statements![0]);
        Check(
            lazyDocs.Count == 1 && lazyDocs[0].DescendantsAndSelf().Any(n => n.Kind == SyntaxKind.JSDocLink),
            "Lazy TypeScript documentation links");
        try
        {
            documented.GetDocumentation(declarationDocs.Statements[0]);
            throw new InvalidDataException("Accepted foreign documentation owner");
        }
        catch (ArgumentException)
        {
            assertions++;
        }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            Parser.ParseSourceFile(new("/canceled.ts"), new("const value = 1;"), canceled.Token);
            throw new InvalidDataException("Parser ignored cancellation");
        }
        catch (OperationCanceledException)
        {
            assertions++;
        }
        Console.WriteLine(
            $"Foundations: {assertions} assertions, {Messages.All.Count} diagnostics, 13 locales, {libraryCount} libraries; embedded={LibraryFileSystem.Embedded}");
    }
}
