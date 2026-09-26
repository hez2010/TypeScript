using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private void HeritageGrammar(SyntaxNode node, NodeList? clauses, bool isInterface = false)
    {
        if (clauses is null || SemanticSyntax.Source(node) is not { ParseDiagnostics.Count: 0 } file)
            return;
        bool extendsSeen = false, implementsSeen = false;
        foreach (HeritageClauseNode clause in clauses)
        {
            if (clause.Token == K.ExtendsKeyword)
            {
                if (extendsSeen || implementsSeen)
                {
                    ErrorOnFirstToken(clause, extendsSeen ? 1172 : 1173);
                    return;
                }
                if (!isInterface && clause.Types is { Count: > 1 } types)
                {
                    ErrorOnFirstToken(types[1], 1174);
                    return;
                }
                extendsSeen = true;
            }
            else
            {
                if (isInterface || implementsSeen)
                {
                    ErrorOnFirstToken(clause, isInterface ? 1176 : 1175);
                    return;
                }
                implementsSeen = true;
            }
            if (clause.Types is not { } elements)
                continue;
            if (elements.HasTrailingComma)
            {
                int position = Math.Max(elements.Pos, elements.End - 1);
                Error(clause, new Diagnostic(DiagnosticLocalization.GetMessage(1009), position, 1, []) { FileName = file.FileName });
            }
            else if (elements.Count == 0)
                Error(clause, new Diagnostic(DiagnosticLocalization.GetMessage(1097), elements.Pos, 0,
                    [clause.Token == K.ExtendsKeyword ? "extends" : "implements"])
                { FileName = file.FileName });
            else
                foreach (var element in elements)
                    InstantiationGrammar(element, TypeReferences.Arguments(element));
        }
    }

    private async ValueTask TupleTypeGrammarAsync(TupleTypeNode node, CancellationToken cancellation)
    {
        bool optionalSeen = false, restSeen = false;
        bool grammar = SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0;
        foreach (var element in node.Elements!)
        {
            cancellation.ThrowIfCancellationRequested();
            var flags = TypeNodes.ElementInfo(element).Flags;
            if ((flags & ElementFlags.Variadic) != 0)
            {
                var type = await Nodes.FromNodeAsync(((ITypedNode)element).Type!, cancellation);
                if (!await ArrayLikeAsync(type, cancellation))
                {
                    Error(element, 2574);
                    break;
                }
                if (IsArray(type) || type is TypeReference { Target: TupleType tuple }
                    && tuple.ElementInfos.Any(e => (e.Flags & ElementFlags.Rest) != 0))
                    flags |= ElementFlags.Rest;
            }
            int code = 0;
            if ((flags & ElementFlags.Rest) != 0)
            {
                if (restSeen)
                    code = 1265;
                restSeen = true;
            }
            else if ((flags & ElementFlags.Optional) != 0)
            {
                if (restSeen)
                    code = 1266;
                optionalSeen = true;
            }
            else if ((flags & ElementFlags.Required) != 0 && optionalSeen)
                code = 1257;
            if (code != 0)
            {
                if (grammar)
                    Error(element, code);
                break;
            }
        }
        await Nodes.FromNodeAsync(node, cancellation);
    }

    private void NamedTupleMemberGrammar(NamedTupleMemberNode node)
    {
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0)
            return;
        if (node.DotDotDotToken is not null && node.QuestionToken is not null)
            Error(node, 5085);
        if (node.Type is OptionalTypeNode)
            Error(node.Type, 5086);
        if (node.Type is RestTypeNode)
            Error(node.Type, 5087);
    }

    private async ValueTask JsDocTypeGrammarAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if ((node.Flags & NodeFlags.JavaScriptFile) != 0 || SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0)
            return;
        if (node is JSDocNullableTypeNode or JSDocNonNullableTypeNode)
        {
            var inner = ((ITypedNode)node).Type!;
            bool postfix = node.Pos == inner.Pos;
            var type = await Nodes.FromNodeAsync(inner, cancellation);
            if (node is JSDocNullableTypeNode && type != context.NeverType && type != context.VoidType && context.StrictNullChecks)
                type = await Algebra.UnionAsync(postfix ? [type, context.UndefinedType]
                    : [type, context.UndefinedType, context.NullType], cancellation: cancellation);
            Error(node, postfix ? 17019 : 17020, node is JSDocNullableTypeNode ? "?" : "!", await TypeDisplay.GetAsync(type, cancellation));
        }
        else
            Error(node, 8020);
    }

    private void TypeOperatorGrammar(TypeOperatorNode node)
    {
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0)
            return;
        if (node.Operator == K.ReadonlyKeyword)
        {
            if (node.Type is not (ArrayTypeNode or TupleTypeNode))
                ErrorOnFirstToken(node, 1354);
            return;
        }
        if (node.Operator != K.UniqueKeyword)
            return;
        if (node.Type?.Kind != K.SymbolKeyword)
        {
            Error(node.Type ?? node, 1005, "symbol");
            return;
        }
        var parent = node.Parent;
        while (parent is ParenthesizedTypeNode)
            parent = parent.Parent;
        switch (parent)
        {
            case VariableDeclarationNode declaration:
                if (declaration.Name is not IdentifierNode)
                    Error(node, 1333);
                else if (declaration.Parent is not VariableDeclarationListNode { Parent: VariableStatementNode })
                    Error(node, 1334);
                else if ((declaration.Parent.Flags & NodeFlags.Const) == 0)
                    Error(declaration.Name, 1332);
                break;
            case PropertyDeclarationNode property:
                if (!SemanticSyntax.IsStatic(property) || !SemanticSyntax.HasModifier(property, K.ReadonlyKeyword))
                    Error(property.Name!, 1331);
                break;
            case PropertySignatureDeclarationNode property:
                if (!SemanticSyntax.HasModifier(property, K.ReadonlyKeyword))
                    Error(property.Name!, 1330);
                break;
            default:
                Error(node, 1335);
                break;
        }
    }
}
