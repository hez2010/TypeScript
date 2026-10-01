using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class ClassFieldsTransformer
{
    private static bool AutoAccessor(SyntaxNode node) => node is PropertyDeclarationNode && SemanticSyntax.HasModifier(node, K.AccessorKeyword);
    private bool LowerAutoAccessors => options.EmitTargetYear < int.MaxValue || environment?.HoistInitializers == true;
    private PrivateIdentifierNode AccessorStorage(SyntaxNode name) => Context.NewGeneratedPrivateNameForNode(name, new(Suffix: "_accessor_storage"u8));
    private BinaryExpressionNode? ComputedNameCache(SyntaxNode name)
    {
        SyntaxNode node = ((ComputedPropertyNameNode)name).Expression!;
        while (true)
        {
            node = NamedEvaluation.SkipOuter(node);
            if (node is BinaryExpressionNode { OperatorToken.Kind: K.CommaToken } comma) node = comma.Right!;
            else return node is BinaryExpressionNode { OperatorToken.Kind: K.EqualsToken, Left: IdentifierNode identifier } assignment
                && Context.GetAutoGenerateInfo(identifier) is not null ? assignment : null;
        }
    }
    private async ValueTask<SyntaxNode> AutoAccessorAsync(PropertyDeclarationNode node)
    {
        var commentRange = Context.GetCommentRange(node);
        var sourceRange = Context.GetSourceMapRange(node);
        SyntaxNode getterName = node.Name!, setterName = node.Name!;
        if (node.Name is ComputedPropertyNameNode computed && !Inlineable(computed.Expression!))
        {
            var getter = Context.Clone(computed);
            var setter = Context.Clone(computed);
            if (ComputedNameCache(computed) is { } cache)
            {
                getter.Expression = await VisitAsync(computed.Expression);
                setter.Expression = cache.Left;
            }
            else
            {
                var temp = Temp();
                Context.SetSourceMapRange(temp, new(computed.Expression!.Pos, computed.Expression.End));
                var assignment = Assign(temp, (await VisitAsync(computed.Expression))!);
                Context.SetSourceMapRange(assignment, new(computed.Expression.Pos, computed.Expression.End));
                getter.Expression = assignment; setter.Expression = temp;
            }
            getterName = getter; setterName = setter;
        }
        var modifiers = FilterModifiers(node.Modifiers, K.AccessorKeyword);
        var backing = Context.Clone(node);
        backing.Modifiers = modifiers; backing.Name = AccessorStorage(node.Name!); backing.PostfixToken = null; backing.Type = null;
        Context.AddFlags(backing, EmitFlags.NoComments);
        Context.SetSourceMapRange(backing, sourceRange);
        SyntaxNode receiver = Static(node) ? (SyntaxNode?)environment!.This ?? environment.Constructor ?? environment.Node.DeclarationName ?? F.NewKeywordExpression(K.ThisKeyword)
            : F.NewKeywordExpression(K.ThisKeyword);
        var getterAccess = F.NewPropertyAccessExpression(receiver, null, AccessorStorage(node.Name!), NodeFlags.None);
        var getterDeclaration = F.NewGetAccessorDeclaration(modifiers, getterName, null, new([]), null, null,
            F.NewBlock(new([F.NewReturnStatement(getterAccess)]), false));
        Context.SetOriginal(getterDeclaration, node);
        Context.SetCommentRange(getterDeclaration, commentRange);
        Context.SetSourceMapRange(getterDeclaration, sourceRange);
        var setterAccess = F.NewPropertyAccessExpression(receiver, null, AccessorStorage(node.Name!), NodeFlags.None);
        var setterModifiers = modifiers is null ? null : new NodeList(modifiers.Select(m => (SyntaxNode)F.NewToken(m.Kind)).ToArray());
        var setterDeclaration = F.NewSetAccessorDeclaration(setterModifiers, setterName, null,
            new([F.NewParameterDeclaration(null, null, F.NewIdentifier("value"u8), null, null, null)]), null, null,
            F.NewBlock(new([F.NewExpressionStatement(Assign(setterAccess, F.NewIdentifier("value"u8)))]), false));
        Context.SetOriginal(setterDeclaration, node);
        Context.AddFlags(setterDeclaration, EmitFlags.NoComments);
        Context.SetSourceMapRange(setterDeclaration, sourceRange);
        return F.NewSyntaxList(await VisitArrayAsync([backing, getterDeclaration, setterDeclaration]));
    }
}
