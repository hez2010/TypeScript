using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Parser
{
    internal static (ImportDeclarationNode? Node, int End, Diagnostic[] Diagnostics) DocumentationImport(SourceText source, ScriptKind scriptKind, int start, int end)
    {
        var parser = new Parser(new("/documentation", scriptKind), source, default, start, end, true);
        SyntaxNode node = parser.Import(null, start);
        if (node is ImportDeclarationNode { ImportClause: { } clause }) clause.PhaseModifier = K.TypeKeyword;
        return (node as ImportDeclarationNode, parser.Pos, parser.diagnostics.ToArray());
    }
    internal static (JSDocTypeExpressionNode Node, int End, Diagnostic[] Diagnostics, NodeFlags SourceFlags) DocumentationType(
        SourceText source, ScriptKind scriptKind, int start, int end, bool mayOmitBraces, NodeFlags additionalContext = 0)
    {
        var parser = new Parser(new("/documentation", scriptKind), source, default, start, end, true);
        parser.context |= additionalContext;
        int pos = parser.Pos;
        bool braces = parser.Take(K.OpenBraceToken);
        if (!braces && !mayOmitBraces) parser.Error(Messages.X_0_expected, "{");
        SyntaxNode type = parser.Type();
        if (parser.Take(K.EqualsToken)) type = parser.Finish(parser.factory.NewJSDocOptionalType(type), type.Pos);
        if (braces) parser.Expected(K.CloseBraceToken);
        var node = parser.Finish(parser.factory.NewJSDocTypeExpression(type), pos);
        return (node, parser.Pos, parser.diagnostics.ToArray(), parser.sourceFlags);
    }
}
