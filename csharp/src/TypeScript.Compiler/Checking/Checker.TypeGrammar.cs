using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly HashSet<Symbol> checkedInferParameters = [];

    private void EmptyTypeListError(SyntaxNode node, NodeList? list, int code)
    {
        if (list is not { Count: 0 } || SemanticSyntax.Source(node) is not { ParseDiagnostics.Count: 0 } file)
            return;
        int start = list.Pos - 1, end = CheckerDiagnostic.TokenRange(file, list.End).Start + 1;
        Error(node, CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code)) with
        { Start = start, Length = end - start });
    }

    private async ValueTask CheckMethodNameAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (SemanticSyntax.Name(node) is PrivateIdentifierNode
            && DeclarationOrder.Ancestor(node, SemanticSyntax.ClassLike) is null)
            Error(node, 18016);
        if (SemanticSyntax.Name(node) is not ComputedPropertyNameNode computed)
            return;
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0
            && computed.Expression is not (StringLiteralNode or NumericLiteralNode or NoSubstitutionTemplateLiteralNode
                or PrefixUnaryExpressionNode { Operator: K.PlusToken or K.MinusToken, Operand: NumericLiteralNode })
            && !LateMembers.LateSyntax(computed))
        {
            int code = node.Parent switch
            {
                InterfaceDeclarationNode => 1169,
                TypeLiteralNode => 1170,
                ClassDeclarationNode or ClassExpressionNode when (node.Flags & NodeFlags.Ambient) != 0 => 1165,
                ClassDeclarationNode or ClassExpressionNode when node is MethodDeclarationNode { Body: null } => 1168,
                _ => 0
            };
            if (code != 0)
                Error(computed, code);
        }
        await ComputedNameAsync(computed, cancellation);
    }

    private void PropertySignatureGrammar(PropertySignatureDeclarationNode node)
    {
        if (DeclarationModifiers(node))
            return;
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0 || MappedMemberGrammar(node))
            return;
        if (node.Name is ComputedPropertyNameNode computed
            && computed.Expression is not (StringLiteralNode or NumericLiteralNode or NoSubstitutionTemplateLiteralNode
                or PrefixUnaryExpressionNode { Operator: K.PlusToken or K.MinusToken, Operand: NumericLiteralNode })
            && !LateMembers.LateSyntax(computed))
        {
            Error(node.Name, node.Parent is InterfaceDeclarationNode ? 1169 : 1170);
            return;
        }
        if (node.Initializer is not null)
            Error(node.Initializer, node.Parent is InterfaceDeclarationNode ? 1246 : 1247);
    }

    private void SourceFileGrammar(SourceFileNode file)
    {
        if ((file.Flags & NodeFlags.Ambient) == 0 || file.ParseDiagnostics.Count != 0)
            return;
        foreach (var node in file.Statements!)
            if (node is VariableStatementNode or FunctionDeclarationNode or ClassDeclarationNode or EnumDeclarationNode
                or ModuleDeclarationNode
                && !SemanticSyntax.HasModifier(node, K.DeclareKeyword)
                && !SemanticSyntax.HasModifier(node, K.ExportKeyword)
                && !SemanticSyntax.HasModifier(node, K.DefaultKeyword))
            {
                ErrorOnFirstToken(node, 1046);
                break;
            }
    }

    private bool MappedMemberGrammar(SyntaxNode node)
    {
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0
            || SemanticSyntax.Name(node) is not ComputedPropertyNameNode { Expression: BinaryExpressionNode { OperatorToken.Kind: K.InKeyword } })
            return false;
        var members = node.Parent switch
        {
            ClassDeclarationNode declaration => declaration.Members,
            ClassExpressionNode expression => expression.Members,
            InterfaceDeclarationNode declaration => declaration.Members,
            TypeLiteralNode literal => literal.Members,
            _ => null
        };
        if (members is not { Count: > 0 })
            return false;
        Error(members[0], 7061);
        return true;
    }

    private async ValueTask CheckInferTypeAsync(InferTypeNode node, CancellationToken cancellation)
    {
        bool valid = false;
        for (var current = (SyntaxNode)node; current.Parent is { } parent; current = parent)
            if (parent is ConditionalTypeNode conditional && conditional.ExtendsType == current)
            {
                valid = true;
                break;
            }
        if (!valid && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
            Error(node, 1338);
        var declaration = node.TypeParameter!;
        await FunctionDeclarations.TypeParameterAsync(declaration, cancellation);
        var symbol = program.Symbols.Declaration(declaration)!;
        if (symbol.Declarations.Count > 1 && !checkedInferParameters.Contains(symbol))
        {
            var parameter = program.Scopes.Parameter(symbol);
            var constraint = await Instantiation.Constraints.ConstraintAsync(parameter, cancellation);
            bool identical = true;
            foreach (var other in symbol.Declarations.OfType<TypeParameterDeclarationNode>())
                if (other.Constraint is { } annotation && constraint is not null
                    && !await IdenticalAsync(await Nodes.FromNodeAsync(annotation, cancellation), constraint, cancellation))
                {
                    identical = false;
                    break;
                }
            cancellation.ThrowIfCancellationRequested();
            checkedInferParameters.Add(symbol);
            if (!identical)
                foreach (var other in symbol.Declarations.OfType<TypeParameterDeclarationNode>())
                    Error(other.Name!, 2838, symbol.Name);
        }
        RegisterUnused(node);
    }

    private async ValueTask CheckTemplateTypeAsync(TemplateLiteralTypeNode node, CancellationToken cancellation)
    {
        foreach (TemplateLiteralTypeSpanNode span in node.TemplateSpans!)
            await RelationDiagnostics.CheckAsync(await Nodes.FromNodeAsync(span.Type!, cancellation), context.TemplateConstraintType,
                RelationKind.Assignable, span.Type, null, 2322, cancellation);
        await Nodes.FromNodeAsync(node, cancellation);
    }

    private async ValueTask CheckMappedTypeAsync(MappedTypeNode node, CancellationToken cancellation)
    {
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0 && node.Members is { Count: > 0 })
            Error(node.Members[0], 7061);
        await FunctionDeclarations.TypeParameterAsync(node.TypeParameter!, cancellation);
        if (node.Type is null && NoImplicitAny)
            Error(node, 7039);
        var type = (MappedType)await Nodes.FromNodeAsync(node, cancellation);
        var nameType = await Instantiation.Mapped.NameAsync(type, cancellation);
        var key = nameType ?? await Instantiation.Mapped.ConstraintAsync(type, cancellation);
        await RelationDiagnostics.CheckAsync(key, context.StringNumberSymbolType, RelationKind.Assignable,
            node.NameType ?? node.TypeParameter!.Constraint!, null, 2322, cancellation);
    }

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
