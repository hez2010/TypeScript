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
                case "reference":
                    bool preserve = args.TryGetValue("preserve", out var p) && p.Value == "true";
                    if (args.TryGetValue("no-default-lib", out var noLib) && noLib.Value == "true")
                        file.HasNoDefaultLib = true;
                    else if (args.TryGetValue("types", out var type))
                    {
                        ReferenceResolutionMode mode = ReferenceResolutionMode.Unspecified;
                        if (args.TryGetValue("resolution-mode", out var resolution))
                        {
                            mode = resolution.Value switch
                            {
                                "import" => ReferenceResolutionMode.Import,
                                "require" => ReferenceResolutionMode.Require,
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
                    else if (args.TryGetValue("lib", out var lib))
                        libs.Add(new(lib.Value, lib.Pos, lib.End, Preserve: preserve));
                    else if (args.TryGetValue("path", out var path))
                        paths.Add(new(path.Value, path.Pos, path.End, Preserve: preserve));
                    else
                        MetadataError(Messages.Invalid_reference_directive_syntax, pragma.Range.Pos, pragma.Range.End);
                    break;
                case "ts-check" or "ts-nocheck":
                    file.CheckJsDirective = new(pragma.Name == "ts-check", pragma.Range);
                    break;
                case "amd-dependency":
                    dependencies.Add(
                        new(args["path"].Value, args.TryGetValue("name", out var dependencyName) ? dependencyName.Value : null));
                    break;
                case "amd-module":
                    if (!string.IsNullOrEmpty(file.ModuleName))
                        MetadataError(Messages.An_AMD_module_cannot_have_multiple_name_assignments, pragma.Range.Pos, pragma.Range.End);
                    file.ModuleName = args["name"].Value;
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
        var ambient = new List<string>();
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
            TextSlice name = module.Name switch { StringLiteralNode text => text.Text, IdentifierNode identifier => identifier.Text, _ => "" };
            if (file.ExternalModuleIndicator is not null || item.Ambient && !RelativeModuleName(name))
                augmentations.Add(module.Name);
            else if (!item.Ambient)
            {
                ambient.Add(name.ToString());
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
                        || call.Expression is MetaPropertyNode { KeywordToken: K.ImportKeyword, Name.Text.Span: "defer" }
                        || javascript
                            && args.Count == 1
                            && call.Expression is IdentifierNode { Text.Span: "require" }) && seen.Add((args[0].Pos, args[0].End)))
                    imports.Add(args[0]);
            }
        }
        file.Imports = imports.AsReadOnly();
        file.ModuleAugmentations = augmentations.AsReadOnly();
        file.AmbientModuleNames = ambient.AsReadOnly();
    }

    private static bool RelativeModuleName(ReadOnlySpan<char> name) => name is "." or ".."
        || name.StartsWith("./", StringComparison.Ordinal)
        || name.StartsWith("../", StringComparison.Ordinal) ||
            name.StartsWith(
                ".\\",
                StringComparison.Ordinal) || name.StartsWith("..\\", StringComparison.Ordinal) || CompilerPath.EncodedRootLength(name) > 0;

    private void MetadataError(DiagnosticMessage message, int pos, int end)
    {
        int start = source.ToUtf16Position(pos);
        ErrorAt(message, start, source.ToUtf16Position(end) - start);
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
                if (node is MetaPropertyNode { KeywordToken: K.ImportKeyword, Name.Text.Span: "meta" })
                    return node;
        if (file.IsDeclarationFile)
            return null;
        if (jsx)
            foreach (SyntaxNode node in file.DescendantsAndSelf())
                if (node.Kind is K.JsxOpeningElement or K.JsxSelfClosingElement or K.JsxFragment)
                    return node;
        return force ? file : null;
    }

    // Metadata ranges are published in the same byte coordinate space as AST
    // nodes. The local comment/attribute scan uses the SourceText UTF-16 view.
    private List<SourceCommentRange> LeadingPragmaComments()
    {
        TextSlice text = source.Text;
        var ranges = new List<SourceCommentRange>();
        int at = 0;
        if (text.Span.StartsWith("#!", StringComparison.Ordinal))
            while (at < text.Length && !TokenFacts.IsLineBreak(text[at]))
                at++;
        while (at < text.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            char ch = text[at];
            if (TokenFacts.IsLineBreak(ch))
            {
                if (ranges.Count != 0)
                    ranges[^1] = ranges[^1] with { HasTrailingNewLine = true };
                at++;
                continue;
            }
            if (TokenFacts.IsWhiteSpace(ch))
            {
                at++;
                continue;
            }
            if (ch != '/' || at + 1 >= text.Length || text[at + 1] is not ('/' or '*'))
                break;
            int start = at;
            bool single = text[at + 1] == '/';
            at += 2;
            if (single)
                while (at < text.Length && !TokenFacts.IsLineBreak(text[at]))
                    at++;
            else
            {
                int close = text.Span[at..].IndexOf("*/", StringComparison.Ordinal);
                if (close >= 0)
                    close += at;
                at = close < 0 ? text.Length : close + 2;
            }
            ranges.Add(new(single ? K.SingleLineCommentTrivia : K.MultiLineCommentTrivia, start, at, single && at < text.Length));
        }
        return ranges;
    }

    private void ExtractPragmas(SourceCommentRange utf16Range, List<SourcePragma> pragmas)
    {
        ReadOnlySpan<char> text = source.Text.Span.Slice(utf16Range.Pos, utf16Range.End - utf16Range.Pos);
        var range = utf16Range with { Pos = source.ToBytePosition(utf16Range.Pos), End = source.ToBytePosition(utf16Range.End) };
        int at = 2;
        if (utf16Range.Kind == K.SingleLineCommentTrivia)
        {
            bool triple = at < text.Length && text[at] == '/';
            if (triple)
                at++;
            SkipPragmaBlanks(text, ref at);
            if (triple && at < text.Length && text[at] == '<')
            {
                at++;
                string tag = PragmaName(text, ref at);
                if (tag is not ("reference" or "amd-dependency" or "amd-module"))
                    return;
                if (at < text.Length && !PragmaWhitespace(text[at]) && text[at] is not ('/' or '>'))
                    return;
                var arguments = new Dictionary<string, PragmaArgument>(StringComparer.Ordinal);
                while (at < text.Length)
                {
                    SkipPragmaBlanks(text, ref at);
                    if (text[at..].StartsWith("/>", StringComparison.Ordinal))
                        break;
                    string name = PragmaName(text, ref at);
                    if (name.Length == 0)
                        break;
                    SkipPragmaBlanks(text, ref at);
                    if (at == text.Length || text[at++] != '=')
                        break;
                    SkipPragmaBlanks(text, ref at);
                    if (at == text.Length || text[at] is not ('\'' or '"'))
                        break;
                    char quote = text[at++];
                    int valueStart = at, close = text[at..].IndexOf(quote);
                    if (close < 0)
                        break;
                    at += close;
                    arguments[name] = new(
                        name,
                        text[valueStart..at].ToString(),
                        source.ToBytePosition(utf16Range.Pos + valueStart),
                        source.ToBytePosition(utf16Range.Pos + at));
                    at++;
                }
                if (tag == "amd-dependency" && !arguments.ContainsKey("path") || tag == "amd-module" && !arguments.ContainsKey("name"))
                    return;
                pragmas.Add(new(tag, range, new ReadOnlyDictionary<string, PragmaArgument>(arguments)));
            }
            else if (at < text.Length && text[at] == '@')
            {
                at++;
                string name = PragmaName(text, ref at);
                if (at < text.Length && !PragmaWhitespace(text[at]) && text[at] != ':')
                    return;
                if (name is "ts-check" or "ts-nocheck")
                    pragmas.Add(new(name, range, ReadOnlyDictionary<string, PragmaArgument>.Empty));
            }
            return;
        }
        if (text.EndsWith("*/", StringComparison.Ordinal))
            text = text[..^2];
        while (at < text.Length)
        {
            int next = text[at..].IndexOf('@');
            if (next < 0)
                break;
            at += next + 1;
            int nameStart = at;
            while (at < text.Length && !PragmaWhitespace(text[at]))
                at++;
            if (at == nameStart)
                continue;
            string name = text[nameStart..at].ToString().ToLowerInvariant();
            int lineEnd = at;
            while (lineEnd < text.Length && !TokenFacts.IsLineBreak(text[lineEnd]))
                lineEnd++;
            if (name is "jsx" or "jsxfrag" or "jsximportsource" or "jsxruntime")
            {
                SkipPragmaBlanks(text, ref at);
                int valueStart = at;
                while (at < text.Length && !PragmaWhitespace(text[at]))
                    at++;
                if (at != valueStart)
                {
                    var argument = new PragmaArgument(
                        "factory",
                        text[valueStart..at].ToString(),
                        source.ToBytePosition(utf16Range.Pos + valueStart),
                        source.ToBytePosition(utf16Range.Pos + at));
                    pragmas.Add(
                        new(
                            name,
                            range,
                            new ReadOnlyDictionary<string, PragmaArgument>(
                                new Dictionary<string, PragmaArgument>(StringComparer.Ordinal) { ["factory"] = argument })));
                }
            }
            at = lineEnd;
        }
    }

    private static void SkipPragmaBlanks(ReadOnlySpan<char> text, ref int at)
    {
        while (at < text.Length && PragmaWhitespace(text[at]) && !TokenFacts.IsLineBreak(text[at]))
            at++;
    }

    private static bool PragmaWhitespace(char ch) => ch == '\uFEFF' || char.IsWhiteSpace(ch) && ch != '\u0085';

    private static string PragmaName(ReadOnlySpan<char> text, ref int at)
    {
        int start = at;
        while (at < text.Length && text[at] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '-')
            at++;
        return text[start..at].ToString().ToLowerInvariant();
    }
}
