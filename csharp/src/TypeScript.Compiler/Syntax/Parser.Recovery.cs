using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Semantics;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Parser
{
    private static readonly Utf8String[] keywordSuggestions = Enumerable.Range((int)K.FirstKeyword, (int)K.LastKeyword - (int)K.FirstKeyword + 1)
        .Select(kind => TokenFacts.Text((K)kind)).Where(text => text.Length > 2).ToArray();

    private bool TrySemicolon() => Take(K.SemicolonToken) || Token is K.EndOfFile or K.CloseBraceToken || LineBreak;

    private async ValueTask PropertySemicolonCore(SyntaxNode name, SyntaxNode? type, SyntaxNode? initializer)
    {
        if (Token == K.AtToken && !LineBreak)
        { Error(Messages.Decorators_must_precede_the_name_and_all_keywords_of_property_declarations); return; }
        if (Token == K.OpenParenToken)
        { Error(Messages.Cannot_start_a_function_call_in_a_type_annotation); Next(); return; }
        if (type is not null && !IsSemicolon())
        {
            if (initializer is not null) Error(Messages.X_0_expected, Utf8Literals.Semicolon);
            else Error(Messages.Expected_for_property_initializer);
            return;
        }
        if (TrySemicolon()) return;
        if (initializer is not null) Error(Messages.X_0_expected, Utf8Literals.Semicolon);
        else await MissingSemicolonAfterCore(name).ConfigureAwait(false);
    }

    private async ValueTask MissingSemicolonAfterCore(SyntaxNode node)
    {
        if (node is TaggedTemplateExpressionNode { Template: { } template })
        {
            int start = new Scanner(source).SkipTriviaAt(template.Pos);
            ErrorAt(Messages.Module_declaration_names_may_only_use_or_quoted_strings, start, template.End - start);
            return;
        }
        if (node is not IdentifierNode { Text.IsEmpty: false } identifier)
        { Error(Messages.X_0_expected, Utf8Literals.Semicolon); return; }
        var text = identifier.Text;
        int position = new Scanner(source).SkipTriviaAt(node.Pos);
        if (text == "const"u8 || text == "let"u8 || text == "var"u8)
        { ErrorAt(Messages.Variable_declaration_not_allowed_at_this_location, position, node.End - position); return; }
        if (text == "declare"u8) return;
        if (text == "interface"u8)
        { InvalidName(Messages.Interface_name_cannot_be_0, Messages.Interface_must_be_given_a_name, K.OpenBraceToken); return; }
        if (text == "module"u8 || text == "namespace"u8)
        { InvalidName(Messages.Namespace_name_cannot_be_0, Messages.Namespace_must_be_given_a_name, K.OpenBraceToken); return; }
        if (text == "type"u8)
        { InvalidName(Messages.Type_alias_name_cannot_be_0, Messages.Type_alias_must_be_given_a_name, K.EqualsToken); return; }
        if (text == "is"u8)
        { ErrorAt(Messages.A_type_predicate_is_only_allowed_in_return_type_position_for_functions_and_methods, position, scanner.TokenStart - position); return; }
        var suggestion = await SpellingSuggestions.FindAsync(text, keywordSuggestions, value => ValueTask.FromResult<Utf8String?>(value),
            Utf8StringComparer.Ordinal.Compare, cancellation: cancellation).ConfigureAwait(false);
        if (suggestion is null)
            foreach (var keyword in keywordSuggestions)
                if (text.Length > keyword.Length + 2 && text.StartsWith(keyword))
                { suggestion = keyword + " "u8 + text[keyword.Length..]; break; }
        if (suggestion is { } replacement) ErrorAt(Messages.Unknown_keyword_or_identifier_Did_you_mean_0, position, node.End - position, replacement);
        else if (Token != K.Unknown) ErrorAt(Messages.Unexpected_keyword_or_identifier, position, node.End - position);

        void InvalidName(DiagnosticMessage invalid, DiagnosticMessage missing, K blank)
        { if (Token == blank) Error(missing); else Error(invalid, scanner.Value); }
    }
}
