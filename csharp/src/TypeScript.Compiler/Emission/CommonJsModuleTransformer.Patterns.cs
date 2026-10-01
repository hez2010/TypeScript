using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class CommonJsModuleTransformer
{
    // Retain native destructuring so iterators, defaults, rest and abrupt completion keep their semantics.
    private sealed class AssignmentPatternRewriter(CommonJsModuleTransformer owner) : SyntaxRewriter(owner.Context, owner.Cancellation)
    {
        protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
        {
            switch (node)
            {
                case IdentifierNode name when owner.CanAssign(name):
                    var target = await owner.IdentifierAsync(name);
                    var exports = await owner.ExportsAsync(name);
                    if (exports.Count == 0) return target;
                    var value = Context.NewUniqueName("value"u8, new() { Flags = GeneratedIdentifierFlags.Optimistic });
                    SyntaxNode expression = owner.Assign(target, value);
                    foreach (var export in exports) expression = owner.ExportExpression(export, expression);
                    var setter = owner.F.NewSetAccessorDeclaration(null, owner.F.NewIdentifier("value"u8), null,
                        new([owner.F.NewParameterDeclaration(null, null, value, null, null, null)]), null, null,
                        owner.F.NewBlock(new([owner.F.NewExpressionStatement(expression)]), false));
                    return owner.Located(owner.Property(owner.Object([setter]), "value"u8), name, false);
                case ArrayLiteralExpressionNode or ObjectLiteralExpressionNode or SpreadElementNode or SpreadAssignmentNode
                    or ParenthesizedExpressionNode or PartiallyEmittedExpressionNode:
                    return await VisitEachChildAsync(node);
                case PropertyAssignmentNode property:
                    var updated = Context.Clone(property);
                    updated.Name = await owner.VisitAsync(property.Name);
                    updated.Initializer = await VisitAsync(property.Initializer);
                    return updated;
                case ShorthandPropertyAssignmentNode shorthand:
                    var reference = (await VisitAsync(shorthand.Name))!;
                    var initializer = await owner.VisitAsync(shorthand.ObjectAssignmentInitializer);
                    if (reference == shorthand.Name && initializer == shorthand.ObjectAssignmentInitializer) return node;
                    initializer = owner.PreserveAssignedName(shorthand.Name!, reference, shorthand.ObjectAssignmentInitializer, initializer);
                    if (initializer is not null) reference = owner.Assign(reference, initializer);
                    return EmitContext.CopyRange(owner.Located(owner.F.NewPropertyAssignment(null, shorthand.Name, null, null, reference), node, false), node);
                case BinaryExpressionNode { OperatorToken.Kind: K.EqualsToken } assignment:
                    var binary = Context.Clone(assignment);
                    binary.Left = await VisitAsync(assignment.Left); binary.Right = await owner.VisitAsync(assignment.Right);
                    binary.Right = owner.PreserveAssignedName(assignment.Left!, binary.Left!, assignment.Right, binary.Right);
                    return binary;
                default:
                    return await owner.VisitAsync(node);
            }
        }
    }

    private SyntaxNode? PreserveAssignedName(SyntaxNode name, SyntaxNode target, SyntaxNode? original, SyntaxNode? value)
    {
        if (name is not IdentifierNode || target is IdentifierNode || !new NamedEvaluation(Context).IsAnonymous(original)) return value;
        var key = Context.StringLiteralFromNode(name);
        return F.NewElementAccessExpression(Object([F.NewPropertyAssignment(null, F.NewComputedPropertyName(key), null, null, value)]), null, key, NodeFlags.None);
    }
}
