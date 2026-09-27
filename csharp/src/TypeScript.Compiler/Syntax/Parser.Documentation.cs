using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Parser
{
    internal static async ValueTask<(ImportDeclarationNode? Node, int End, Diagnostic[] Diagnostics)> DocumentationImportAsync(
        SourceText source,
        ScriptKind scriptKind,
        int start,
        int end,
        CancellationToken cancellation = default)
    {
        var parser = new Parser(new("/documentation", scriptKind), source, cancellation, start, end, true);
        await parser.ParseStack;
        parser.Expected(K.ImportKeyword);
        int clauseStart = parser.Pos;
        IdentifierNode? name = parser.IsIdentifier ? parser.Identifier() : null;
        ImportClauseNode? clause = null;
        if (name is not null || parser.Token is K.AsteriskToken or K.OpenBraceToken)
        {
            SyntaxNode? bindings = null;
            if (name is null || parser.Take(K.CommaToken))
            {
                int bindingsStart = parser.Pos;
                if (parser.Take(K.AsteriskToken))
                {
                    parser.Expected(K.AsKeyword);
                    bindings = parser.Finish(parser.factory.NewNamespaceImport(parser.Identifier()), bindingsStart);
                }
                else
                {
                    parser.Expected(K.OpenBraceToken);
                    NodeList elements = await parser.DelimitedCore(
                        K.CloseBraceToken,
                        () => parser.ImportExportSpecifierCore(false)).ConfigureAwait(false);
                    parser.Expected(K.CloseBraceToken);
                    bindings = parser.Finish(parser.factory.NewNamedImports(elements), bindingsStart);
                }
            }
            clause = parser.Finish(parser.factory.NewImportClause(K.TypeKeyword, name, bindings), clauseStart);
            parser.Expected(K.FromKeyword);
        }
        SyntaxNode specifier;
        if (!parser.StartsExpression())
        {
            if (clause is not null && parser.Token == K.EndOfFile)
                parser.ErrorAt(Messages.Expression_expected, parser.Pos, 0);
            else
                parser.Error(Messages.Expression_expected);
            specifier = parser.Token == K.EndOfFile ? parser.Finish(parser.factory.NewIdentifier(""), parser.Pos, parser.Pos)
                : await parser.ExpressionCore().ConfigureAwait(false);
        }
        else
            specifier = await parser.ExpressionCore().ConfigureAwait(false);
        ImportAttributesNode? attributes = null;
        if (parser.Token == K.WithKeyword || !parser.LineBreak && parser.Token == K.AssertKeyword)
        {
            K token = parser.Token;
            int attributesStart = parser.Pos;
            parser.Next();
            attributes = await parser.ImportAttributesCore(token, attributesStart).ConfigureAwait(false);
        }
        ImportDeclarationNode node = parser.Finish(
            parser.factory.NewImportDeclaration(K.ImportDeclaration, null, clause, specifier, attributes),
            start);
        return (node, parser.Pos, parser.diagnostics.ToArray());
    }

    internal static async ValueTask<(
        JSDocTypeExpressionNode Node,
        int End,
        Diagnostic[] Diagnostics,
        NodeFlags SourceFlags)> DocumentationTypeAsync(
        SourceText source,
        ScriptKind scriptKind,
        int start,
        int end,
        bool mayOmitBraces,
        NodeFlags additionalContext = 0,
        CancellationToken cancellation = default)
    {
        var parser = new Parser(new("/documentation", scriptKind), source, cancellation, start, end, true);
        await parser.ParseStack;
        parser.context |= additionalContext;
        int pos = parser.Pos;
        bool braces = parser.Take(K.OpenBraceToken);
        if (!braces && !mayOmitBraces)
            parser.Error(Messages.X_0_expected, "{");
        SyntaxNode type = await parser.TypeOrPredicateCore().ConfigureAwait(false);
        if (parser.Take(K.EqualsToken))
            type = parser.Finish(parser.factory.NewJSDocOptionalType(type), type.Pos);
        if (braces)
            parser.Expected(K.CloseBraceToken);
        var node = parser.Finish(parser.factory.NewJSDocTypeExpression(type), pos);
        return (node, parser.Pos, parser.diagnostics.ToArray(), parser.sourceFlags);
    }

    internal static async ValueTask<(
        ExpressionWithTypeArgumentsNode Node,
        int End,
        Diagnostic[] Diagnostics,
        NodeFlags SourceFlags)> DocumentationHeritageAsync(
        SourceText source, ScriptKind scriptKind, int start, int end, NodeFlags context, CancellationToken cancellation)
    {
        var parser = new Parser(new("/documentation", scriptKind), source, cancellation, start, end, true);
        await parser.ParseStack;
        parser.context |= context;
        bool braces = parser.Take(K.OpenBraceToken);
        int pos = parser.Pos;
        SyntaxNode expression = parser.Identifier(true);
        while (parser.Take(K.DotToken))
            expression = parser.Finish(parser.factory.NewPropertyAccessExpression(expression, null, parser.Identifier(true), 0), pos);
        var arguments = await parser.TypeArgumentsCore(false).ConfigureAwait(false);
        var node = parser.Finish(parser.factory.NewExpressionWithTypeArguments(expression, arguments), pos);
        if (braces)
            parser.Expected(K.CloseBraceToken);
        return (node, parser.Pos, parser.diagnostics.ToArray(), parser.sourceFlags);
    }

    internal static async ValueTask<(
        TypeParameterDeclarationNode? Node,
        int End,
        Diagnostic[] Diagnostics,
        NodeFlags SourceFlags)> DocumentationTypeParameterAsync(
        SourceText source, ScriptKind scriptKind, int start, int end, CancellationToken cancellation = default)
    {
        var parser = new Parser(new("/documentation", scriptKind), source, cancellation, start, end, true);
        await parser.ParseStack;
        int pos = parser.Pos;
        bool bracketed = parser.Take(K.OpenBracketToken);
        NodeList? modifiers = await parser.ModifiersCore(true).ConfigureAwait(false);
        if (!parser.IsIdentifier && parser.Token is not (>= K.FirstKeyword and <= K.LastKeyword))
            parser.Error(Messages.Unexpected_token_A_type_parameter_name_was_expected_without_curly_braces);
        IdentifierNode name = parser.Identifier(true);
        SyntaxNode? defaultType = null;
        if (bracketed)
        {
            parser.Expected(K.EqualsToken);
            defaultType = await parser.TypeOrPredicateCore().ConfigureAwait(false);
            if (parser.Take(K.EqualsToken))
                defaultType = parser.Finish(parser.factory.NewJSDocOptionalType(defaultType), defaultType.Pos);
            parser.Expected(K.CloseBracketToken);
        }
        TypeParameterDeclarationNode? node = name.Text.Length == 0
            ? null
            : parser.Finish(parser.factory.NewTypeParameterDeclaration(modifiers, name, null, null, defaultType), pos);
        return (node, parser.Pos, parser.diagnostics.ToArray(), parser.sourceFlags);
    }
}
