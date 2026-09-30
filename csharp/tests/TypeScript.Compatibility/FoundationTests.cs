using System.Runtime.InteropServices;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class FoundationTests
{
    public static void Run(Utf8String repository)
    {
        int assertions = 0;
        void Check(bool valid, Utf8String message)
        {
            assertions++;
            if (!valid)
                throw new InvalidDataException(message.ToString());
        }
        foreach (var message in Messages.All)
        {
            Check(ReferenceEquals(message, DiagnosticLocalization.GetMessage(message.Code)), Utf8String.ConcatMany("Diagnostic identity "u8, Utf8String.EnumName(message.Code)));
            Check(message.Format() == message.Text, Utf8String.ConcatMany("Diagnostic fallback "u8, Utf8String.EnumName(message.Code)));
        }
        foreach (Utf8String locale in new Utf8String[]        {
            "cs-CZ"u8,
            "de-DE"u8,
            "es-ES"u8,
            "fr-FR"u8,
            "it-IT"u8,
            "ja-JP"u8,
            "ko-KR"u8,
            "pl-PL"u8,
            "pt-BR"u8,
            "ru-RU"u8,
            "tr-TR"u8,
            "zh-CN"u8,
            "zh-TW"u8
        })
            Check(
                Messages.Unterminated_string_literal.Format(locale) != Messages.Unterminated_string_literal.Text,
                Utf8String.ConcatMany("Native locale "u8, locale));
        Check(
            Messages.Unterminated_string_literal.Format("ja"u8) == Messages.Unterminated_string_literal.Format("ja-JP"u8),
            "Locale language fallback"u8);
        Utf8String highSurrogate = new(new byte[] { 0xED, 0xA0, 0x80 });
        Check(DiagnosticLocalization.Format("a {0}, {1}, {{0}}"u8, ["😀"u8, highSurrogate]) == "a 😀, �, {😀}"u8,
            "Diagnostic interpolation normalizes lone surrogates"u8);
        byte[] sourceBytes = "a😀\r\n名字\u2028z"u8.ToArray();
        var source = new SourceText(sourceBytes);
        sourceBytes[0] = 0;
        Check(source.Text.Span.StartsWith((byte)'a') && source.Bytes.Span[0] == 'a', "Source owns bytes"u8);
        Check(source.Length == 17 && source.LineStarts.SequenceEqual([0, 7, 16])
            && source.GetLineAndCharacter(10) == (1, 3), "Source locations count UTF-8 bytes"u8);
        int mappingFailure = 0;
        Parallel.For(0, 8, worker =>
        {
            for (int step = 0; step < 1024; step++)
            {
                int position = (step * 7 + worker) % (source.Length + 1);
                int line = position < 7 ? 0 : position < 16 ? 1 : 2;
                if (source.GetLineAndCharacter(position) != (line, position - source.LineStarts[line]))
                    Interlocked.Exchange(ref mappingFailure, 1);
            }
        });
        Check(mappingFailure == 0, "Shared line indexing handles concurrent nonmonotonic offsets"u8);
        byte[] malformedBytes = [0xFF, 0x41, 0xED, 0xA0, 0x80, 0xF0, 0x9F, 0x98, 0x80, 0xE2, 0x82];
        var malformedSource = new SourceText(malformedBytes);
        Check(malformedSource.Text.Span.SequenceEqual(malformedBytes), "Source retains malformed bytes without transcoding"u8);
        int[] points = [0xFFFD, 'A', 0xD800, 0x1F600, 0xFFFD, 0xFFFD];
        int[] widths = [1, 1, 3, 4, 1, 1];
        int cursor = 0;
        for (int i = 0; i < points.Length; i++)
        {
            int point = Wtf8.Decode(malformedBytes.AsSpan(cursor), out int width);
            Check(point == points[i] && width == widths[i], "WTF-8 decodes scalars and lone surrogates while consuming malformed bytes individually"u8);
            cursor += width;
        }
        for (int prefix = 0; prefix < 128; prefix++)
        {
            var vectorSource = new SourceText(new Utf8String('a', prefix) + "é漢😀z"u8);
            Check(vectorSource.Length == prefix + 10 && vectorSource.Text[prefix..].Span.SequenceEqual("é漢😀z"u8)
                && vectorSource.Text[(prefix + 5)..(prefix + 9)].Span.SequenceEqual("😀"u8),
                "Byte slices preserve Unicode across vector lanes and scalar tails"u8);
            Utf8String lower = new('a', prefix), upper = new('A', prefix);
            Check((lower + "漢"u8).StartsWith(upper, StringComparison.OrdinalIgnoreCase)
                && ("漢"u8 + lower).EndsWith(upper, StringComparison.OrdinalIgnoreCase),
                "ASCII prefix and suffix comparisons stop at Unicode boundaries across vector lanes"u8);
        }
        Check(Utf8String.Copy("ɐ"u8).StartsWith("Ɐ"u8, StringComparison.OrdinalIgnoreCase)
            && Utf8String.Copy("ɐ"u8).EndsWith("Ɐ"u8, StringComparison.OrdinalIgnoreCase)
            && Utf8String.Copy("Ɐx"u8).StartsWith("ɐ"u8, StringComparison.OrdinalIgnoreCase)
            && Utf8String.Copy("xⱯ"u8).EndsWith("ɐ"u8, StringComparison.OrdinalIgnoreCase),
            "Case-insensitive prefixes and suffixes allow different UTF-8 byte widths"u8);
        Check(!Utf8String.Copy("name.mts"u8).EndsWith(".CTS"u8, StringComparison.OrdinalIgnoreCase)
            && !Utf8String.Copy("file:"u8).StartsWith("https:"u8, StringComparison.OrdinalIgnoreCase),
            "ASCII prefix and suffix mismatches"u8);
        Check(SourceEncoding.Decode([0xFF, 0xFE, 0, 0xD8, 0x41, 0]) == highSurrogate + "A"u8,
            "UTF-16 LE input transcodes directly to WTF-8"u8);
        Check(SourceEncoding.Decode([0xFE, 0xFF, 0xD8, 0, 0, 0x41]) == highSurrogate + "A"u8,
            "UTF-16 BE input transcodes directly to WTF-8"u8);
        Check(SourceEncoding.Decode([0xEF, 0xBB, 0xBF, 0x41]) == "A"u8, "UTF-8 BOM"u8);
        Check(SourceEncoding.Decode([0xFF, 0xFE, 0x3D, 0xD8, 0, 0xDE]) == "😀"u8, "Input surrogate pairs become one scalar"u8);
        byte[] destination = new byte[16];
        destination.AsSpan().Fill(0xCC);
        int encodedLength = Wtf8.Encode("A\ud800😀", destination.AsSpan(1, 8));
        Check(encodedLength == 8 && destination.AsSpan(1, encodedLength).SequenceEqual(
            new byte[] { 0x41, 0xED, 0xA0, 0x80, 0xF0, 0x9F, 0x98, 0x80 }), "Explicit CLR text conversion preserves WTF-8"u8);
        Check(destination[0] == 0xCC && destination[9] == 0xCC, "Encoding stays within destination"u8);
        foreach (Utf8String value in new Utf8String[] { ""u8, "ASCII"u8, "名字😀"u8, highSurrogate })
        {
            var fromMemory = new SourceText(value);
            var fromBytes = new SourceText(value.Span);
            Check(fromMemory.Bytes.Span.SequenceEqual(fromBytes.Bytes.Span) && fromMemory.Length == value.Length,
                "Source memory and span inputs retain identical bytes"u8);
        }
        Utf8String code = "const shared = 'shared'; const other = shared; const escaped = 'a\\nb';"u8;
        var tree = Parser.ParseSourceFile(new("slices.ts"u8), new SourceText(code));
        Check(MemoryMarshal.TryGetArray(tree.Source.Bytes, out var sourceOwner), "Source has a stable owner"u8);
        foreach (var identifier in tree.DescendantsAndSelf().OfType<IdentifierNode>())
            Check(MemoryMarshal.TryGetArray(identifier.Text.Memory, out var owner)
                && ReferenceEquals(owner.Array, sourceOwner.Array)
                && tree.Source.Bytes.Span.Slice(owner.Offset, owner.Count).SequenceEqual(identifier.Text.Span),
                "Identifiers borrow UTF-8 source memory"u8);
        var literals = tree.DescendantsAndSelf().OfType<StringLiteralNode>().ToArray();
        Check(MemoryMarshal.TryGetArray(literals[0].Text.Memory, out var literalOwner)
            && ReferenceEquals(literalOwner.Array, sourceOwner.Array), "Unescaped literals borrow UTF-8 source memory"u8);
        Check(literals[1].Text == "a\nb"u8 && MemoryMarshal.TryGetArray(literals[1].Text.Memory, out var escapedOwner)
            && !ReferenceEquals(escapedOwner.Array, sourceOwner.Array), "Escaped literals own decoded UTF-8 memory"u8);
        Utf8String firstName = new SourceText("first:shared"u8).Text[6..];
        Utf8String secondName = new SourceText("second:shared"u8).Text[7..];
        var names = new Dictionary<Utf8String, int>(Utf8StringComparer.Ordinal) { [firstName] = 1 };
        Check(names[secondName] == 1 && names.GetAlternateLookup<ReadOnlySpan<byte>>()["shared"u8] == 1,
            "Owned and borrowed UTF-8 keys share content hashes"u8);
        Utf8String? missing = null;
        Check(default(Utf8String).IsEmpty && missing is null && (Utf8String?)Utf8String.Empty is { IsEmpty: true },
            "Missing and empty UTF-8 text remain distinct"u8);
        Check(Utf8String.Frame(["a"u8, "bc"u8]) != Utf8String.Frame(["ab"u8, "c"u8])
            && Utf8String.Frame(["\0"u8, highSurrogate]) == Utf8String.Concat("1:\0"u8, "3:"u8, highSurrogate),
            "Cache framing counts bytes and preserves arbitrary text"u8);
        Check(Utf8StringComparer.OrdinalIgnoreCase.Equals("éA"u8, "Éa"u8)
            && Utf8StringComparer.OrdinalIgnoreCase.GetHashCode("éA"u8) == Utf8StringComparer.OrdinalIgnoreCase.GetHashCode("Éa"u8),
            "Unicode case-insensitive equality and hashing agree"u8);
        var builder = new Utf8StringBuilder().Append("x"u8).AppendCodePoint(0x1F600).AppendCodePoint(0xD800).Append(12);
        Check(builder.ToUtf8String() == Utf8String.Concat("x😀"u8, highSurrogate, "12"u8),
            "Builder distinguishes scalar encoding from numeric formatting"u8);
        var converter = new Utf8StringJsonConverter();
        var jsonOptions = new JsonSerializerOptions();
        Utf8String jsonName = Utf8String.Concat("name😀"u8, highSurrogate, "\\\""u8);
        Utf8String jsonValue = Utf8String.Concat(highSurrogate, "\n日"u8);
        foreach (bool indented in new[] { false, true })
        {
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output, new() { Indented = indented }))
            {
                writer.WriteStartObject();
                converter.WriteAsPropertyName(writer, jsonName, jsonOptions);
                converter.Write(writer, jsonValue, jsonOptions);
                writer.WriteEndObject();
            }
            var reader = new Utf8JsonReader(output.ToArray());
            Check(reader.Read() && reader.Read()
                && converter.ReadAsPropertyName(ref reader, typeof(Utf8String), jsonOptions) == jsonName,
                "JSON dictionary names retain WTF-8 escapes"u8);
            Check(reader.Read() && converter.Read(ref reader, typeof(Utf8String), jsonOptions) == jsonValue,
                "JSON values retain WTF-8 escapes"u8);
        }
        foreach (Utf8String locale in new Utf8String[] { "en"u8, "fr_fr"u8, "ja-JP"u8, "zh-Hant-TW"u8, "i-klingon"u8, "x-private"u8, "en-US-u-ca-gregory"u8, "en-t-h0"u8 })
            Check(LocaleIdentifier.IsValid(locale), "Locale without OS globalization"u8);
        foreach (Utf8String locale in new Utf8String[] { ""u8, "invalid-value"u8, "zz"u8, "en-foobar"u8, "en-u"u8, "en-u-ca-gregory-ca-buddhist"u8 })
            Check(!LocaleIdentifier.IsValid(locale), "Invalid locale without OS globalization"u8);
        foreach (var (input, expected) in new[]
        {
            (Utf8String.Copy("a/./b/../c"u8), Utf8String.Copy("a/c"u8)),
            (Utf8String.Copy("c:\\a\\..\\b"u8), Utf8String.Copy("c:/b"u8)),
            (Utf8String.Copy("file:///c:/a/../../b"u8), Utf8String.Copy("file:///c:/b"u8)),
            (Utf8String.Copy("http://host/a/../b/"u8), Utf8String.Copy("http://host/b/"u8)),
            (Utf8String.Copy("^/untitled/../a"u8), Utf8String.Copy("^/a"u8)),
            (Utf8String.Copy("../../x"u8), Utf8String.Copy("../../x"u8))
        })
            Check(CompilerPath.Normalize(input) == Utf8String.Copy(expected), Utf8String.ConcatMany("Path normalize "u8, Utf8String.Copy(input)));
        Check(CompilerPath.RootLength("file:///c%3a/a"u8) == 13, "URL encoded drive root"u8);
        Check(CompilerPath.Relative("/a/b"u8, "/a/c/d"u8, true) == "../c/d"u8, "Relative path"u8);
        Check(!CompilerPath.Contains("/a/b"u8, "/a/bad"u8, true), "Path component boundary"u8);
        Check(CompilerPath.Combine("/a"u8, "/b"u8, "c"u8) == "/b/c"u8, "Absolute path overrides prior path"u8);
        Check(!CompilerPath.IsAbsolute("C:relative"u8), "Drive-relative path"u8);
        Utf8String longSegment = new('a', 300);
        Check(CompilerPath.Normalize(Utf8String.ConcatMany("/"u8, longSegment, "/../b/"u8)) == "/b/"u8, "Pooled path normalization"u8);
        Check(CompilerPath.Normalize(Utf8String.ConcatMany("../../"u8, longSegment, "/.."u8)) == "../.."u8, "Pooled relative path normalization"u8);
        Check(CompilerPath.Relative(Utf8String.ConcatMany("/"u8, longSegment, "/b/"u8), Utf8String.ConcatMany("/"u8, longSegment, "/c/"u8), true) == "../c"u8, "Long relative paths"u8);
        Check(CompilerPath.Contains(Utf8String.ConcatMany("/"u8, longSegment, "/"u8), Utf8String.ConcatMany("/"u8, longSegment, "/b"u8), true), "Long path containment"u8);
        foreach (var (number, expected) in new (double, Utf8String)[]
        {
            (1e-7, Utf8String.Copy("1e-7"u8)), (1e-6, Utf8String.Copy("0.000001"u8)), (-1e-6, Utf8String.Copy("-0.000001"u8)),
            (1e20, Utf8String.Copy("100000000000000000000"u8)), (-1e20, Utf8String.Copy("-100000000000000000000"u8)),
            (1e21, Utf8String.Copy("1e+21"u8)), (double.Epsilon, Utf8String.Copy("5e-324"u8)),
            (double.MaxValue, Utf8String.Copy("1.7976931348623157e+308"u8))
        })
            Check(TokenFacts.NumberText(number) == expected, "Span numeric formatting boundary"u8);

        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/tsconfig.json"u8] = Encoding.UTF8.GetBytes(
                "{ // JSONC\n\"extends\": \"./base\", \"compilerOptions\": {\"strict\": null, \"outDir\":\"dist\"}, \"include\":[\"src/**/*\"], }"),
            ["/project/base.json"u8] = Encoding.UTF8.GetBytes("{\"compilerOptions\":{\"strict\":true,\"target\":\"esnext\"}}"),
            ["/project/src/main.ts"u8] = Encoding.UTF8.GetBytes("export const value = 1;"),
            ["/project/src/nested/child.ts"u8] = Encoding.UTF8.GetBytes("export {};"),
            ["/project/src/ignored.js"u8] = Encoding.UTF8.GetBytes(""),
            ["/project/node_modules/pkg/index.ts"u8] = Encoding.UTF8.GetBytes(""),
            ["/project/a.rsp"u8] = Encoding.UTF8.GetBytes("--strict false @b.rsp \"space name.ts\""),
            ["/project/b.rsp"u8] = Encoding.UTF8.GetBytes("@a.rsp --target esnext --outDir output"),
        });
        byte[] read = fs.ReadFile("/project/src/main.ts"u8)!;
        read[0] = 0;
        Check(fs.ReadFile("/project/src/main.ts"u8)![0] == 'e', "VFS read ownership"u8);
        Check(fs.GetAccessibleEntries("/project/src"u8).Directories.SequenceEqual([Utf8String.Copy("nested"u8)]), "VFS directory entries"u8);
        fs.WriteFile(Utf8String.Copy([0x2F, 0x70, 0x72, 0x6F, 0x6A, 0x65, 0x63, 0x74, 0x2F, 0x73, 0x75, 0x72, 0x72, 0x6F, 0x67, 0x61, 0x74, 0x65, 0x2D, 0xED, 0xA0, 0x80, 0x2E, 0x74, 0x73]), [1, 2]);
        fs.AppendFile(Utf8String.Copy([0x2F, 0x70, 0x72, 0x6F, 0x6A, 0x65, 0x63, 0x74, 0x2F, 0x73, 0x75, 0x72, 0x72, 0x6F, 0x67, 0x61, 0x74, 0x65, 0x2D, 0xED, 0xA0, 0x80, 0x2E, 0x74, 0x73]), [3]);
        Check(fs.ReadFile(Utf8String.Copy([0x2F, 0x70, 0x72, 0x6F, 0x6A, 0x65, 0x63, 0x74, 0x2F, 0x73, 0x75, 0x72, 0x72, 0x6F, 0x67, 0x61, 0x74, 0x65, 0x2D, 0xED, 0xA0, 0x80, 0x2E, 0x74, 0x73]))!.SequenceEqual(new byte[] { 1, 2, 3 }), "VFS surrogate path and append"u8);
        var parsed = new CommandLineParser(fs, "/project"u8).Parse(["@a.rsp"u8, "--noEmit"u8, "--strictNullChecks"u8, "--not-an-option"u8]);
        Check(parsed.Diagnostics is [{ Code: DiagnosticCode.UnknownCompilerOption0 }], "CLI unknown option"u8);
        Check(parsed.Options.Boolean("strict"u8) == false && parsed.Options.Boolean("noEmit"u8) == true, "CLI boolean values"u8);
        Check(parsed.Options.String("outDir"u8) == "/project/output"u8 && parsed.Options.String("target"u8) == "esnext"u8, "CLI paths and enums"u8);
        Check(parsed.FileNames.SequenceEqual([Utf8String.Copy("space name.ts"u8)]), "Response file cycles and quoted filename"u8);
        var configParser = new ConfigParser(fs, "/project"u8);
        Check(configParser.FindConfig("/project/src/nested"u8) == "/project/tsconfig.json"u8, "Find parent config"u8);
        ParsedConfig config = configParser.Parse("tsconfig.json"u8);
        Check(config.Diagnostics.Length == 0, Utf8String.Copy("Config diagnostics: "u8) + Utf8String.Join("; "u8, config.Diagnostics.Select(d => d.Format())));
        Check(
            config.Options.Boolean("strict"u8) is null && config.Options.Get("strict"u8)?.ValueKind == System.Text.Json.JsonValueKind.Null,
            "Explicit null overrides inherited true"u8);
        Check(config.Options.String("outDir"u8) == "/project/dist"u8 && config.Options.String("target"u8) == "esnext"u8, "Inherited options"u8);
        Check(
            config.FileNames.SequenceEqual([Utf8String.Copy("/project/src/main.ts"u8), Utf8String.Copy("/project/src/nested/child.ts"u8)]),
            "Recursive include, extensions and node_modules exclusion"u8);
        fs.WriteFile("/project/nested/tsconfig.json"u8, Encoding.UTF8.GetBytes("{\"extends\":\"../tsconfig\"}"));
        Check(
            configParser.Parse("nested/tsconfig.json"u8).FileNames.SequenceEqual(config.FileNames),
            "Inherited include keeps base config directory"u8);
        fs.WriteFile("/project/cycle.json"u8, Encoding.UTF8.GetBytes("{\"extends\":\"./cycle\"}"));
        Check(
            configParser.Parse("cycle.json"u8).Diagnostics.Any(
                d => d.Code == DiagnosticCode.CircularityDetectedWhileResolvingConfigurationColon0),
            "Config inheritance cycle diagnostic"u8);
        var directives = TestDirectives.Parse(
            "// @target: es6, es2015, esnext\n// @strict: *, -false\n// @filename: a.ts\nconst a = 1;\n// @symlink: link.ts\n// @filename: b.ts\nexport {};"u8,
            "test.ts"u8);
        Check(directives.Units.Length == 2 && directives.Units[0].Content == "const a = 1;"u8, "Test unit expansion"u8);
        Check(directives.Symlinks["link.ts"u8] == "a.ts"u8, "Test symlink directive"u8);
        var variations = TestDirectives.Expand(directives.Options, new HashSet<Utf8String> { "target"u8, "strict"u8 });
        Check(variations.Count == 2 && variations.All(c => c["strict"u8] == "true"u8), "Test wildcard/exclusions and enum aliases"u8);

        var libraries = new LibraryFileSystem(new PhysicalFileSystem());
        Utf8String originalLibraries = Utf8String.FromString(Path.Combine(repository.ToString(), "tsc/internal/bundled/libs"));
        int libraryCount = 0;
        foreach (Utf8String reference in Directory.EnumerateFiles(originalLibraries.ToString(), "*.d.ts").Select(Utf8String.FromString))
        {
            Utf8String name = Utf8String.FromString(Path.GetFileName(reference.ToString())), path = CompilerPath.Combine(libraries.LibraryDirectory, name);
            byte[] expected = File.ReadAllBytes(reference.ToString()), actual = libraries.ReadFile(path) ?? throw new FileNotFoundException(path.ToString());
            Check(actual.AsSpan().SequenceEqual(expected), Utf8String.Copy("Library bytes "u8) + name);
            Check(libraries.Stat(path)?.Length == expected.Length, Utf8String.Copy("Library size "u8) + name);
            libraryCount++;
        }
        Check(libraryCount > 100, "Library inventory"u8);
        if (LibraryFileSystem.Embedded)
        {
            try
            {
                libraries.WriteFile(CompilerPath.Combine(libraries.LibraryDirectory, "lib.d.ts"u8), []);
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
        Check(clone.DescendantsAndSelf().Count() == 20001 && !ReferenceEquals(clone.GetChild(0), deep.GetChild(0)), "Deep typed AST clone"u8);
        Check(clone.GetChild(0).Parent == clone, "Clone parent ownership"u8);
        var unicode = Parser.ParseSourceFile(new("/unicode.ts"u8), new SourceText("const 日本語 = '😀';"u8));
        Check(unicode.ParseDiagnostics.Count == 0 && unicode.Statements!.End == unicode.Source.Bytes.Length, "AST list byte positions"u8);
        Utf8String documentedText = "/** @template T\n * @param {T} value input\n * @returns {T} result\n */\nfunction identity(value) { return value; }"u8;
        var documented = Parser.ParseSourceFile(new("/documented.js"u8), new SourceText(documentedText));
        var function = (FunctionDeclarationNode)documented.Statements![0];
        Check(function.TypeParameters?.Count == 1 && function.Type is TypeReferenceNode, "JSDoc template and return annotation"u8);
        Check(function.Parameters![0] is ParameterDeclarationNode { Type: TypeReferenceNode }, "JSDoc parameter annotation"u8);
        var documentation = documented.GetDocumentation(function);
        Check(
            documentation.Count == 1 && documentation[0].Tags?.Count == 3 && documentation[0].Parent == function,
            "JSDoc source ownership and tags"u8);
        Parallel.For(0, 32, _ =>
        {
            if (!ReferenceEquals(documented.GetDocumentation(function)[0], documentation[0]))
                throw new InvalidDataException("JSDoc query identity changed");
        });
        assertions++;
        var documentedClone = documented.DeepClone<SourceFileNode>();
        Check(documentedClone.ReparsedClones.Count == documented.ReparsedClones.Count, "Cloned source retains reparse mapping"u8);
        Check(
            !ReferenceEquals(documentedClone.GetDocumentation(documentedClone.Statements![0])[0], documentation[0]),
            "Cloned source owns its documentation cache"u8);
        var declarationDocs = Parser.ParseSourceFile(new("/documented.ts"u8), new SourceText("/** A value. {@link Other} */\nconst x = 1;"u8));
        var lazyDocs = declarationDocs.GetDocumentation(declarationDocs.Statements![0]);
        Check(
            lazyDocs.Count == 1 && lazyDocs[0].DescendantsAndSelf().Any(n => n.Kind == SyntaxKind.JSDocLink),
            "Lazy TypeScript documentation links"u8);
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
            Parser.ParseSourceFile(new("/canceled.ts"u8), new("const value = 1;"u8), canceled.Token);
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
