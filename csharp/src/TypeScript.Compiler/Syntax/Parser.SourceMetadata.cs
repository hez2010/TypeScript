using TypeScript.Compiler.Text;
using System.Collections.ObjectModel;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Parser
{
    private void ProcessSourceMetadata(SourceFileNode file)
    {
        if (file.ScriptKind == ScriptKind.JSON)
            return;
        var pragmas = new List<SourcePragma>();
        foreach (var range in LeadingPragmaComments())
            ExtractPragmas(range, pragmas);
        file.Pragmas = pragmas.AsReadOnly();
        var paths = new List<FileReference>();
        var types = new List<FileReference>();
        var libs = new List<FileReference>();
        var dependencies = new List<AmdDependency>();
        foreach (SourcePragma pragma in pragmas)
        {
            cancellation.ThrowIfCancellationRequested();
            var args = pragma.Arguments;
            switch (pragma.Name)
            {
                case var _ when pragma.Name == "reference"u8:
                    bool preserve = args.TryGetValue(Utf8Literals.PreserveOption, out var p) && p.Value == Utf8Literals.True;
                    if (args.TryGetValue(Utf8Literals.NoDefaultLib, out var noLib) && noLib.Value == Utf8Literals.True)
                        file.HasNoDefaultLib = true;
                    else if (args.TryGetValue(Utf8Literals.Types, out var type))
                    {
                        ReferenceResolutionMode mode = ReferenceResolutionMode.Unspecified;
                        if (args.TryGetValue(Utf8Literals.ResolutionMode, out var resolution))
                        {
                            mode = resolution.Value switch
                            {
                                _ when resolution.Value == "import"u8 => ReferenceResolutionMode.Import,
                                _ when resolution.Value == "require"u8 => ReferenceResolutionMode.Require,
                                _ => ReferenceResolutionMode.Unspecified
                            };
                            if (mode == ReferenceResolutionMode.Unspecified)
                                MetadataError(
                                    Messages.X_resolution_mode_should_be_either_require_or_import,
                                    resolution.Pos,
                                    resolution.End);
                        }
                        types.Add(new(type.Value, type.Pos, type.End, mode, preserve));
                    }
                    else if (args.TryGetValue(Utf8Literals.Lib, out var lib))
                        libs.Add(new(lib.Value, lib.Pos, lib.End, Preserve: preserve));
                    else if (args.TryGetValue(Utf8Literals.Path, out var path))
                        paths.Add(new(path.Value, path.Pos, path.End, Preserve: preserve));
                    else
                        MetadataError(Messages.Invalid_reference_directive_syntax, pragma.Range.Pos, pragma.Range.End);
                    break;
                case var _ when pragma.Name == "ts-check"u8 || pragma.Name == "ts-nocheck"u8:
                    file.CheckJsDirective = new(pragma.Name == Utf8Literals.TsCheck, pragma.Range);
                    break;
                case var _ when pragma.Name == "amd-dependency"u8:
                    dependencies.Add(
                        new(args[Utf8Literals.Path].Value, args.TryGetValue(Utf8Literals.Name, out var dependencyName) ? dependencyName.Value : (Utf8String?)null));
                    break;
                case var _ when pragma.Name == "amd-module"u8:
                    if (!Utf8String.IsNullOrEmpty(file.ModuleName))
                        MetadataError(Messages.An_AMD_module_cannot_have_multiple_name_assignments, pragma.Range.Pos, pragma.Range.End);
                    file.ModuleName = args[Utf8Literals.Name].Value;
                    break;
            }
        }
        file.ReferencedFiles = paths.AsReadOnly();
        file.TypeReferenceDirectives = types.AsReadOnly();
        file.LibReferenceDirectives = libs.AsReadOnly();
        file.AmdDependencies = dependencies.AsReadOnly();
        file.ExternalModuleIndicator = FindExternalModuleIndicator(file, options.ForceExternalModule, options.JsxExternalModule);
        CollectModuleReferences(file);
    }

    private void CollectModuleReferences(SourceFileNode file)
    {
        var imports = new List<SyntaxNode>();
        var augmentations = new List<SyntaxNode>();
        var ambient = new List<Utf8String>();
        var pending = new Stack<(SyntaxNode Node, bool Ambient)>();
        if (file.Statements is { } statements)
            for (int i = statements.Count - 1; i >= 0; i--)
                pending.Push((statements[i], false));
        while (pending.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            SyntaxNode? specifier = item.Node switch
            {
                ImportDeclarationNode import => import.ModuleSpecifier,
                ExportDeclarationNode export => export.ModuleSpecifier,
                ImportEqualsDeclarationNode { ModuleReference: ExternalModuleReferenceNode external } => external.Expression,
                _ => null,
            };
            if (specifier is StringLiteralNode { Text.Length: > 0 } literal && (!item.Ambient || !RelativeModuleName(literal.Text)))
                imports.Add(literal);
            if (item.Node is not ModuleDeclarationNode module
                || module.Name is null
                || module.Name is not StringLiteralNode && module.Keyword != K.GlobalKeyword)
                continue;
            if (!item.Ambient && !file.IsDeclarationFile && module.Modifiers?.Any(m => m.Kind == K.DeclareKeyword) != true)
                continue;
            Utf8String name = module.Name switch { StringLiteralNode text => text.Text, IdentifierNode identifier => identifier.Text, _ => Utf8String.Empty };
            if (file.ExternalModuleIndicator is not null || item.Ambient && !RelativeModuleName(name))
                augmentations.Add(module.Name);
            else if (!item.Ambient)
            {
                ambient.Add(name);
                if (module.Body is ModuleBlockNode { Statements: { } body })
                    for (int i = body.Count - 1; i >= 0; i--)
                        pending.Push((body[i], true));
            }
        }
        bool javascript = file.ScriptKind is ScriptKind.JS or ScriptKind.JSX;
        if (javascript || (file.Flags & NodeFlags.PossiblyContainsDynamicImport) != 0)
        {
            // Walk actual syntax nodes once. Searching source text for the words
            // import/require can count one call twice when its argument contains
            // that word, and misses escaped identifiers.
            IEnumerable<SyntaxNode> nodes = file.DescendantsAndSelf();
            if (javascript)
                nodes = nodes.Concat(
                    documentation.Values.SelectMany(comments => comments).SelectMany(comment => comment.DescendantsAndSelf()));
            var seen = new HashSet<(int Pos, int End)>();
            foreach (SyntaxNode node in nodes.Distinct<SyntaxNode>(ReferenceEqualityComparer.Instance).OrderBy(n => n.Pos))
            {
                cancellation.ThrowIfCancellationRequested();
                if (node is ImportTypeNode { Argument: LiteralTypeNode { Literal: StringLiteralNode typeLiteral } }
                    && seen.Add((typeLiteral.Pos, typeLiteral.End)))
                    imports.Add(typeLiteral);
                else if (node is CallExpressionNode { Arguments: { Count: > 0 } args } call
                    && args[0].Kind is K.StringLiteral or K.NoSubstitutionTemplateLiteral &&
                    (call.Expression?.Kind == K.ImportKeyword
                        || call.Expression is MetaPropertyNode { KeywordToken: K.ImportKeyword, Name.Text.Span: var matchedText } && matchedText.SequenceEqual("defer"u8)
                        || javascript
                            && args.Count == 1
                            && call.Expression is IdentifierNode { Text.Span: var matchedText2 } && matchedText2.SequenceEqual("require"u8)) && seen.Add((args[0].Pos, args[0].End)))
                    imports.Add(args[0]);
            }
        }
        file.Imports = imports.AsReadOnly();
        file.ModuleAugmentations = augmentations.AsReadOnly();
        file.AmbientModuleNames = ambient.AsReadOnly();
    }

    private static bool RelativeModuleName(ReadOnlySpan<byte> name) => name.SequenceEqual("."u8) || name.SequenceEqual(".."u8)
        || name.StartsWith("./"u8, StringComparison.Ordinal)
        || name.StartsWith("../"u8, StringComparison.Ordinal) ||
            name.StartsWith(
                ".\\"u8,
                StringComparison.Ordinal) || name.StartsWith("..\\"u8, StringComparison.Ordinal) || CompilerPath.EncodedRootLength(name) > 0;

    private void MetadataError(DiagnosticMessage message, int pos, int end)
    {
        int start = pos;
        ErrorAt(message, start, end - start);
    }

    private static SyntaxNode? FindExternalModuleIndicator(SourceFileNode file, bool force, bool jsx)
    {
        if (file.ScriptKind == ScriptKind.JSON)
            return null;
        if (file.Statements is { } statements)
            foreach (SyntaxNode statement in statements)
                if (statement.Kind is K.ImportDeclaration or K.ExportDeclaration or K.ExportAssignment ||
                    statement is ImportEqualsDeclarationNode { ModuleReference.Kind: K.ExternalModuleReference } ||
                    statement is IModifiedNode { Modifiers: { } modifiers } && modifiers.Any(m => m.Kind == K.ExportKeyword))
                    return statement;
        if ((file.Flags & NodeFlags.PossiblyContainsImportMeta) != 0)
            foreach (SyntaxNode node in file.DescendantsAndSelf())
                if (node is MetaPropertyNode { KeywordToken: K.ImportKeyword, Name.Text.Span: var matchedText3 } && matchedText3.SequenceEqual("meta"u8))
                    return node;
        if (file.IsDeclarationFile)
            return null;
        if (jsx)
            foreach (SyntaxNode node in file.DescendantsAndSelf())
                if (node.Kind is K.JsxOpeningElement or K.JsxSelfClosingElement or K.JsxFragment)
                    return node;
        return force ? file : null;
    }

    // Metadata and AST ranges share the source's UTF-8 byte coordinates.
    private List<SourceCommentRange> LeadingPragmaComments()
    {
        Utf8String text = source.Text;
        var ranges = new List<SourceCommentRange>();
        int at = 0;
        if (text.Span.StartsWith("#!"u8, StringComparison.Ordinal))
            while (at < text.Length && !TokenFacts.IsLineBreak(Wtf8.Decode(text.Span[at..], out int width)))
                at += width;
        while (at < text.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            int ch = Wtf8.Decode(text.Span[at..], out int width);
            if (TokenFacts.IsLineBreak(ch))
            {
                if (ranges.Count != 0)
                    ranges[^1] = ranges[^1] with { HasTrailingNewLine = true };
                at += width;
                continue;
            }
            if (TokenFacts.IsWhiteSpace(ch))
            {
                at += width;
                continue;
            }
            if (ch != '/' || at + 1 >= text.Length || text[at + 1] is not ((byte)'/' or (byte)'*'))
                break;
            int start = at;
            bool single = text[at + 1] == '/';
            at += 2;
            if (single)
                while (at < text.Length && !TokenFacts.IsLineBreak(Wtf8.Decode(text.Span[at..], out width)))
                    at += width;
            else
            {
                int close = text.Span[at..].IndexOf("*/"u8, StringComparison.Ordinal);
                if (close >= 0)
                    close += at;
                at = close < 0 ? text.Length : close + 2;
            }
            ranges.Add(new(single ? K.SingleLineCommentTrivia : K.MultiLineCommentTrivia, start, at, single && at < text.Length));
        }
        return ranges;
    }

    private void ExtractPragmas(SourceCommentRange range, List<SourcePragma> pragmas)
    {
        ReadOnlySpan<byte> text = source.Text.Span.Slice(range.Pos, range.End - range.Pos);
        int at = 2;
        if (range.Kind == K.SingleLineCommentTrivia)
        {
            bool triple = at < text.Length && text[at] == '/';
            if (triple)
                at++;
            SkipPragmaBlanks(text, ref at);
            if (triple && at < text.Length && text[at] == '<')
            {
                at++;
                Utf8String tag = PragmaName(text, ref at);
                if (!(tag == "reference"u8 || tag == "amd-dependency"u8 || tag == "amd-module"u8))
                    return;
                if (at < text.Length && !PragmaWhitespace(Wtf8.Decode(text[at..], out _)) && text[at] is not ((byte)'/' or (byte)'>'))
                    return;
                var arguments = new Dictionary<Utf8String, PragmaArgument>(Utf8StringComparer.Ordinal);
                while (at < text.Length)
                {
                    SkipPragmaBlanks(text, ref at);
                    if (text[at..].StartsWith("/>"u8, StringComparison.Ordinal))
                        break;
                    Utf8String name = PragmaName(text, ref at);
                    if (name.Length == 0)
                        break;
                    SkipPragmaBlanks(text, ref at);
                    if (at == text.Length || text[at++] != '=')
                        break;
                    SkipPragmaBlanks(text, ref at);
                    if (at == text.Length || text[at] is not ((byte)'\'' or (byte)'"'))
                        break;
                    int quote = text[at++];
                    int valueStart = at, close = text[at..].IndexOf((byte)quote);
                    if (close < 0)
                        break;
                    at += close;
                    arguments[name] = new(
                        name,
                        Utf8String.Copy(text[valueStart..at]),
                        range.Pos + valueStart,
                        range.Pos + at);
                    at++;
                }
                if (tag == Utf8Literals.AmdDependency && !arguments.ContainsKey(Utf8Literals.Path) || tag == Utf8Literals.AmdModule && !arguments.ContainsKey(Utf8Literals.Name))
                    return;
                pragmas.Add(new(tag, range, new ReadOnlyDictionary<Utf8String, PragmaArgument>(arguments)));
            }
            else if (at < text.Length && text[at] == '@')
            {
                at++;
                Utf8String name = PragmaName(text, ref at);
                if (at < text.Length && !PragmaWhitespace(Wtf8.Decode(text[at..], out _)) && text[at] != ':')
                    return;
                if (name == "ts-check"u8 || name == "ts-nocheck"u8)
                    pragmas.Add(new(name, range, ReadOnlyDictionary<Utf8String, PragmaArgument>.Empty));
            }
            return;
        }
        if (text.EndsWith("*/"u8, StringComparison.Ordinal))
            text = text[..^2];
        while (at < text.Length)
        {
            int next = text[at..].IndexOf((byte)'@');
            if (next < 0)
                break;
            at += next + 1;
            int nameStart = at;
            while (at < text.Length && !PragmaWhitespace(Wtf8.Decode(text[at..], out _)))
                at++;
            if (at == nameStart)
                continue;
            Utf8String name = Utf8String.Copy(text[nameStart..at]).ToLowerInvariant();
            int lineEnd = at;
            while (lineEnd < text.Length && !TokenFacts.IsLineBreak(Wtf8.Decode(text[lineEnd..], out _)))
                lineEnd++;
            if (name == "jsx"u8 || name == "jsxfrag"u8 || name == "jsximportsource"u8 || name == "jsxruntime"u8)
            {
                SkipPragmaBlanks(text, ref at);
                int valueStart = at;
                while (at < text.Length && !PragmaWhitespace(Wtf8.Decode(text[at..], out _)))
                    at++;
                if (at != valueStart)
                {
                    var argument = new PragmaArgument(
                        Utf8Literals.Factory,
                        Utf8String.Copy(text[valueStart..at]),
                        range.Pos + valueStart,
                        range.Pos + at);
                    pragmas.Add(
                        new(
                            name,
                            range,
                            new ReadOnlyDictionary<Utf8String, PragmaArgument>(
                                new Dictionary<Utf8String, PragmaArgument>(Utf8StringComparer.Ordinal) { [Utf8Literals.Factory] = argument })));
                }
            }
            at = lineEnd;
        }
    }

    private static void SkipPragmaBlanks(ReadOnlySpan<byte> text, ref int at)
    {
        while (at < text.Length)
        {
            int point = Wtf8.Decode(text[at..], out int width);
            if (!PragmaWhitespace(point) || TokenFacts.IsLineBreak(point))
                break;
            at += width;
        }
    }

    private static bool PragmaWhitespace(int ch) => ch == '\uFEFF' || System.Text.Rune.IsValid(ch) && System.Text.Rune.IsWhiteSpace(new System.Text.Rune(ch)) && ch != '\u0085';

    private static Utf8String PragmaName(ReadOnlySpan<byte> text, ref int at)
    {
        int start = at;
        while (at < text.Length && text[at] is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or (byte)'-')
            at++;
        return Utf8String.Copy(text[start..at]).ToLowerInvariant();
    }
}
