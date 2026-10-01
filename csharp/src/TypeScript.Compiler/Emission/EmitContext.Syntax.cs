using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Emission;

public sealed partial class EmitContext
{
    public void AssignCommentAndSourceMapRanges(SyntaxNode target, SyntaxNode source)
    {
        SetCommentRange(target, GetCommentRange(source));
        SetSourceMapRange(target, GetSourceMapRange(source));
    }

    public IdentifierNode GetDeclarationName(SyntaxNode node, bool allowComments = false, bool allowSourceMaps = false,
        EmitFlags flags = EmitFlags.None, bool ignoreAssignedName = false)
    {
        var name = (ignoreAssignedName ? null : GetAssignedName(node)) ?? SemanticSyntax.Name(node);
        if (name is not IdentifierNode identifier)
            return NewGeneratedNameForNode(node);
        var result = Clone(identifier);
        if (!allowComments)
            flags |= EmitFlags.NoComments;
        if (!allowSourceMaps)
            flags |= EmitFlags.NoSourceMap;
        AddFlags(result, flags);
        return result;
    }

    public IdentifierNode GetLocalName(SyntaxNode node, bool allowComments = false, bool allowSourceMaps = false, bool ignoreAssignedName = false)
        => GetDeclarationName(node, allowComments, allowSourceMaps, EmitFlags.LocalName, ignoreAssignedName);
    public IdentifierNode GetExportName(SyntaxNode node, bool allowComments = false, bool allowSourceMaps = false, bool ignoreAssignedName = false)
        => GetDeclarationName(node, allowComments, allowSourceMaps, EmitFlags.ExportName, ignoreAssignedName);

    public PropertyAccessExpressionNode GetNamespaceMemberName(IdentifierNode ns, IdentifierNode name, bool allowComments = false, bool allowSourceMaps = false)
    {
        if (GetAutoGenerateInfo(name) is null)
            name = Clone(name);
        var result = Factory.NewPropertyAccessExpression(ns, null, name, NodeFlags.None);
        AssignCommentAndSourceMapRanges(result, name);
        if (!allowComments)
            AddFlags(result, EmitFlags.NoComments);
        if (!allowSourceMaps)
            AddFlags(result, EmitFlags.NoSourceMap);
        return result;
    }

    public SyntaxNode GetExternalModuleOrNamespaceExportName(IdentifierNode? ns, SyntaxNode node, bool allowComments = false, bool allowSourceMaps = false)
        => ns is not null && SemanticSyntax.HasModifier(node, SyntaxKind.ExportKeyword)
            ? GetNamespaceMemberName(ns, GetDeclarationName(node, allowComments, allowSourceMaps), allowComments, allowSourceMaps)
            : GetExportName(node, allowComments, allowSourceMaps);

    public SyntaxNode CreateExpressionFromEntityName(SyntaxNode node)
    {
        var names = new Stack<QualifiedNameNode>();
        while (node is QualifiedNameNode name)
        {
            names.Push(name);
            node = name.Left!;
        }
        node = Clone(node);
        while (names.TryPop(out var name))
        {
            var access = CopyRange(Factory.NewPropertyAccessExpression(node, null, Clone(name.Right!), NodeFlags.None), name);
            node = access;
        }
        return node;
    }

    internal SyntaxNode? ConstantExpression(object? value) => value switch
    {
        Utf8String text => Factory.NewStringLiteral(text, TokenFlags.None),
        double number when double.IsNaN(number) => Factory.NewIdentifier("NaN"u8),
        double number when double.IsPositiveInfinity(number) => Factory.NewIdentifier("Infinity"u8),
        double number when number < 0 => Factory.NewPrefixUnaryExpression(SyntaxKind.MinusToken, ConstantExpression(-number)),
        double number => Factory.NewNumericLiteral(TokenFacts.NumberText(number), TokenFlags.None),
        _ => null
    };
}
