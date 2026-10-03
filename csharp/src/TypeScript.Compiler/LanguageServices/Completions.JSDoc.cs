using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

internal static class JSDocCompletion
{
    internal static async ValueTask<CompletionItem?> CreateAsync(DocumentProjection projection, int position,
        CompletionCapabilities capabilities, UserPreferences preferences, CancellationToken cancellation)
    {
        var file = projection.File;
        if (preferences.EnableJSDocCompletions == false || !ValidPosition(file, position)) return null;
        var newLine = preferences.FormatCodeSettings.NewLineCharacter;
        if (newLine.IsEmpty) newLine = "\n"u8;
        var template = await TemplateAsync(file, position, preferences.GenerateReturnInDocTemplate == true, newLine, cancellation);
        if (template is null) return null;
        var text = capabilities.Snippets ? ToSnippet(template.Value, newLine) : template.Value;
        int lineStart = LineStart(file, position);
        var prefix = TrimRight(file.Source.Text[lineStart..position]);
        int start = position;
        for (int i = prefix.Length - 1; i >= 0 && prefix[i] == '*'; i--)
            if (i > 0 && prefix[i - 1] == '/') { start = lineStart + i - 1; break; }
        if (prefix.EndsWith("/"u8)) start = lineStart + prefix.Length - 1;
        int end = position + SuffixEnd(file.Source.Text[position..LineEnd(file, position)]);
        var (range, fidelity) = projection.ToRange(start, end);
        return new("/** */"u8, 1, Messages.JSDoc_comment.Format(preferences.Locale), "\0"u8,
            capabilities.Snippets ? 2 : null, fidelity == MappingFidelity.Exact ? new(text, range, capabilities.InsertReplace ? range : null) : null,
            capabilities.CommitCharacters ? [] : null);
    }

    internal static bool ValidPosition(SourceFileNode file, int position)
    {
        var text = file.Source.Text;
        var prefix = TrimRight(text[LineStart(file, position)..position]);
        if (!prefix.EndsWith("/**"u8))
        {
            int start = SkipWhitespace(prefix, 0);
            if (start + 3 > prefix.Length || prefix[start] != '/') return false;
            for (int i = start + 1; i < prefix.Length; i++) if (prefix[i] != '*') return false;
        }
        var suffix = text[position..LineEnd(file, position)];
        suffix = TrimRight(suffix[SkipWhitespace(suffix, 0)..]);
        if (suffix.IsEmpty) return true;
        if (!suffix.EndsWith("/"u8)) return false;
        for (int i = 0; i < suffix.Length - 1; i++) if (suffix[i] != '*') return false;
        return true;
    }

    private static async ValueTask<Utf8String?> TemplateAsync(SourceFileNode file, int position, bool generateReturn,
        Utf8String newLine, CancellationToken cancellation)
    {
        SyntaxNode token;
        JSDocNode? existing;
        bool hasComment;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            token = await SyntaxNavigation.GetTokenAtPositionAsync(file, position, cancellation);
            existing = null;
            for (var node = token; node is not null; node = node.Parent)
                if (node is JSDocNode doc) { existing = doc; break; }
            hasComment = TrimRight(file.Source.Text[LineStart(file, position)..position]).EndsWith("/**"u8);
            int suffixEnd = SuffixEnd(file.Source.Text[position..LineEnd(file, position)]);
            bool nonEmpty = existing is { Comment.Count: > 0 } or { Tags.Count: > 0 };
            if (nonEmpty && hasComment && suffixEnd == 0)
            {
                file = await Parser.ParseSourceFileAsync(new(file.FileName, file.ScriptKind),
                    new SourceText(file.Source.Text[..position] + " */"u8 + file.Source.Text[position..]), cancellation);
                continue;
            }
            if (nonEmpty) return null;
            if (existing is null && hasComment)
                token = await SyntaxNavigation.GetTokenAtPositionAsync(file, SkipWhitespace(file.Source.Text, position + suffixEnd, true), cancellation);
            break;
        }
        int tokenStart = SmartIndenter.Start(token, file);
        if (existing is null && !hasComment && tokenStart < position) return null;
        var owner = Owner(token, cancellation);
        if (owner is null || SmartIndenter.Start(owner.Value.Node, file) < position) return null;
        var lastDoc = (await file.GetDocumentationAsync(owner.Value.Node, cancellation)).LastOrDefault();
        if (lastDoc is not null && existing is not null && lastDoc != existing) return null;
        int lineStart = LineStart(file, position);
        var indentation = file.Source.Text[lineStart..Math.Min(position, SkipWhitespace(file.Source.Text, lineStart))];
        var tags = new Utf8StringBuilder();
        if (owner.Value.Function is IFunctionSignature { Parameters: { } parameters })
            for (int i = 0; i < parameters.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var parameter = (ParameterDeclarationNode)parameters[i];
                var name = parameter.Name is IdentifierNode identifier ? identifier.Text : Utf8String.FromString($"param{i}");
                tags.Append(indentation).Append(" * @param "u8);
                if (file.ScriptKind is ScriptKind.JS or ScriptKind.JSX)
                    tags.Append(parameter.DotDotDotToken is null ? "{any} "u8 : "{...any} "u8);
                tags.Append(name).Append(newLine);
            }
        if (generateReturn && HasReturn(owner.Value.Function, cancellation)) tags.Append(indentation).Append(" * @returns"u8).Append(newLine);
        if (tags.Length == 0 || lastDoc is { Tags.Count: > 0 }) return "/** */"u8;
        return "/**"u8 + newLine + indentation + " * "u8 + newLine + tags.ToUtf8String() + indentation + " */"u8
            + (tokenStart == position ? newLine + indentation : Utf8String.Empty);
    }

    private static (SyntaxNode Node, SyntaxNode? Function)? Owner(SyntaxNode token, CancellationToken cancellation)
    {
        for (SyntaxNode? node = token; node is not null; node = node.Parent)
        {
            cancellation.ThrowIfCancellationRequested();
            // These redirects inspect the initializer/expression without walking back to its parent.
            var candidate = node;
            while (candidate is PropertyAssignmentNode or ExpressionStatementNode)
            {
                cancellation.ThrowIfCancellationRequested();
                candidate = candidate is PropertyAssignmentNode assignment ? assignment.Initializer : ((ExpressionStatementNode)candidate).Expression;
            }
            switch (candidate)
            {
                case FunctionDeclarationNode or FunctionExpressionNode or MethodDeclarationNode or ConstructorDeclarationNode or MethodSignatureDeclarationNode or ArrowFunctionNode:
                    return (candidate, candidate);
                case ClassDeclarationNode or InterfaceDeclarationNode or EnumDeclarationNode or EnumMemberNode or TypeAliasDeclarationNode:
                    return (candidate, null);
                case PropertySignatureDeclarationNode property:
                    return (candidate, property.Type is FunctionTypeNode ? property.Type : null);
                case VariableStatementNode statement:
                    var declarations = ((VariableDeclarationListNode)statement.DeclarationList!).Declarations!;
                    SyntaxNode? initializer = declarations.Count == 1 ? ((VariableDeclarationNode)declarations[0]).Initializer : null;
                    while (initializer is ParenthesizedExpressionNode parentheses) initializer = parentheses.Expression;
                    return (candidate, initializer is FunctionExpressionNode or ArrowFunctionNode ? initializer
                        : initializer is ClassExpressionNode cls ? cls.Members?.FirstOrDefault(member => member is ConstructorDeclarationNode) : null);
                case SourceFileNode: return null;
                case ModuleDeclarationNode when candidate.Parent is not ModuleDeclarationNode: return (candidate, null);
                case BinaryExpressionNode binary:
                    if (!SyntaxLanguageService.IsAssignmentDeclaration(binary)) return null;
                    return (candidate, binary.Right is IFunctionSignature ? binary.Right : null);
                case PropertyDeclarationNode { Initializer: FunctionExpressionNode or ArrowFunctionNode } property:
                    return (candidate, property.Initializer);
            }
        }
        return null;
    }

    private static bool HasReturn(SyntaxNode? function, CancellationToken cancellation)
    {
        if (function is FunctionTypeNode) return true;
        var body = function is null ? null : SyntaxNavigation.FunctionBody(function);
        if (body is null) return false;
        if (function is ArrowFunctionNode && body is not BlockNode) return true;
        if (body is not BlockNode) return false;
        Stack<SyntaxNode> pending = new(); pending.Push(body);
        while (pending.TryPop(out var node))
        {
            cancellation.ThrowIfCancellationRequested();
            if (node is ReturnStatementNode) return true;
            if (node.Kind is K.CaseBlock or K.Block or K.IfStatement or K.DoStatement or K.WhileStatement or K.ForStatement
                or K.ForInStatement or K.ForOfStatement or K.WithStatement or K.SwitchStatement or K.CaseClause or K.DefaultClause
                or K.LabeledStatement or K.TryStatement or K.CatchClause)
                for (int i = node.ChildCount - 1; i >= 0; i--) pending.Push(node.GetChild(i));
        }
        return false;
    }

    private static Utf8String ToSnippet(Utf8String template, Utf8String newLine)
    {
        if (template == "/** */"u8) return "/**"u8 + newLine + " * $0"u8 + newLine + " */"u8;
        var lines = template.Replace("$"u8, "\\$"u8).ToString().Split(newLine.ToString());
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart(' ', '\t');
            if (trimmed.StartsWith('/')) lines[i] = trimmed;
            else if (trimmed.StartsWith('*')) lines[i] = " " + trimmed;
        }
        int index = 1;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (i > 0 && lines[i - 1].StartsWith("/**", StringComparison.Ordinal) && line.Trim(' ', '\t') == "*")
            { lines[i] += "$0"; continue; }
            if (line == " * @returns") { lines[i] += " ${" + index++ + "}"; continue; }
            if (!line.StartsWith(" * @param ", StringComparison.Ordinal)) continue;
            var rest = line[10..];
            string type = "";
            if (rest.StartsWith('{'))
            {
                int end = rest.IndexOf('}');
                if (end < 0) continue;
                type = " " + rest[..(end + 1)]; rest = rest[(end + 1)..].TrimStart(' ', '\t');
            }
            // Keep the reference's escaped type text and its extra space before explicit types.
            if (rest.Length == 0 || rest.Any(char.IsWhiteSpace)) continue;
            lines[i] = " * @param " + (type is " {any}" or " {*}" ? "{${" + index++ + ":*}} " : type.Length == 0 ? "" : type + " ")
                + rest + " ${" + index++ + "}";
        }
        return Utf8String.FromString(string.Join(newLine.ToString(), lines));
    }

    private static int LineStart(SourceFileNode file, int position) => file.Source.LineStarts[file.Source.GetLineAndCharacter(position).Line];
    private static int LineEnd(SourceFileNode file, int position)
    {
        var starts = file.Source.LineStarts;
        int line = file.Source.GetLineAndCharacter(position).Line;
        int end = line + 1 < starts.Length ? starts[line + 1] : file.Source.Length;
        while (end > position && TokenFacts.IsLineBreak(Wtf8.DecodeLast(file.Source.Text.Span[..end], out int width))) end -= width;
        return end;
    }
    private static int SkipWhitespace(Utf8String text, int position, bool includeLineBreak = false)
    {
        while (position < text.Length)
        {
            int point = Wtf8.Decode(text.Span[position..], out int width);
            if (!TokenFacts.IsWhiteSpace(point) && !(includeLineBreak && TokenFacts.IsLineBreak(point))) break;
            position += width;
        }
        return position;
    }
    private static Utf8String TrimRight(Utf8String text)
    {
        int end = text.Length;
        while (end > 0 && TokenFacts.IsWhiteSpace(Wtf8.DecodeLast(text.Span[..end], out int width))) end -= width;
        return text[..end];
    }
    private static int SuffixEnd(Utf8String suffix)
    {
        int pos = SkipWhitespace(suffix, 0);
        while (pos < suffix.Length && suffix[pos] == '*') pos++;
        return pos < suffix.Length && suffix[pos] == '/' ? pos + 1 : 0;
    }
}
