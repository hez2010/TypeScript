using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Parser
{
    private void ReparseUnhostedDocumentation(SyntaxNode parent, JSDocNode comment)
    {
        if (comment.Tags is null)
            return;
        foreach (SyntaxNode tag in comment.Tags)
        {
            if (tag is JSDocOverloadTagNode { TypeExpression: JSDocSignatureNode signature } overload
                && objectLiteralDepth == 0
                && parent.Kind is K.FunctionDeclaration or K.MethodDeclaration or K.Constructor)
            {
                var parts = DocumentationSignature(signature);
                NodeList? overloadModifiers = parent is IModifiedNode { Modifiers: { } originalModifiers }
                    ? new(originalModifiers.Select(n => n.DeepClone<SyntaxNode>()).ToArray(), originalModifiers.Pos, originalModifiers.End)
                    : null;
                SyntaxNode? nameClone = parent is INamedNode { Name: { } originalName } ? originalName.DeepClone<SyntaxNode>() : null;
                if (nameClone is not null)
                    nameClone.Flags |= NodeFlags.Reparsed;
                SyntaxNode overloadDeclaration = parent.Kind switch
                {
                    K.FunctionDeclaration => factory.NewFunctionDeclaration(
                        overloadModifiers,
                        null,
                        (IdentifierNode?)nameClone,
                        DocumentationTypeParameters(comment),
                        parts.Parameters,
                        parts.Type,
                        null,
                        null),
                    K.MethodDeclaration => factory.NewMethodDeclaration(
                        overloadModifiers,
                        null,
                        nameClone,
                        null,
                        DocumentationTypeParameters(comment),
                        parts.Parameters,
                        parts.Type,
                        null,
                        null),
                    _ => factory.NewConstructorDeclaration(
                        overloadModifiers,
                        DocumentationTypeParameters(comment),
                        parts.Parameters,
                        parts.Type,
                        null,
                        null),
                };
                FinishReparse(overloadDeclaration, overload.TagName!);
                reparsedStatements.Add(overloadDeclaration);
                continue;
            }
            if (tag is JSDocImportTagNode { ImportClause: { } importClause, ModuleSpecifier: { } moduleSpecifier } import)
            {
                var importDeclaration = factory.NewImportDeclaration(
                    K.JSImportDeclaration,
                    null,
                    (ImportClauseNode)CloneDocumentationType(importClause),
                    CloneDocumentationType(moduleSpecifier),
                    import.Attributes is null ? null : (ImportAttributesNode)CloneDocumentationType(import.Attributes));
                FinishReparse(importDeclaration, import);
                reparsedStatements.Add(importDeclaration);
                continue;
            }
            SyntaxNode? name, type;
            if (tag is JSDocTypedefTagNode typeDef)
            {
                name = typeDef.Name;
                type = typeDef.TypeExpression switch
                {
                    JSDocTypeExpressionNode { Type: { } t } => CloneDocumentationType(t),
                    JSDocTypeLiteralNode literal => CloneDocumentationType(literal),
                    _ => null,
                };
            }
            else if (tag is JSDocCallbackTagNode { TypeExpression: JSDocSignatureNode callbackSignature } callback)
            {
                name = callback.Name;
                var parts = DocumentationSignature(callbackSignature);
                type = factory.NewFunctionTypeNode(null, parts.Parameters, parts.Type ?? factory.NewKeywordTypeNode(K.AnyKeyword));
                FinishReparse(type, callbackSignature);
            }
            else
                continue;
            if (name is null || type is null)
                continue;
            var names = new List<IdentifierNode>();
            var work = new Stack<SyntaxNode>();
            work.Push(name);
            while (work.TryPop(out var part))
            {
                if (part is QualifiedNameNode { Left: { } left, Right: { } right })
                {
                    work.Push(right);
                    work.Push(left);
                }
                else if (part is ModuleDeclarationNode { Name: { } moduleName } module)
                {
                    // A trailing dot retains a namespace even when its final
                    // name is missing. The recoverable alias uses that last
                    // namespace name inside the namespace, never at file scope.
                    work.Push(module.Body ?? moduleName);
                    work.Push(moduleName);
                }
                else if (part is IdentifierNode identifier)
                    names.Add(identifier);
            }
            if (names.Count == 0)
                continue;
            if (!ValidDocumentationIdentifier(names[^1].Text))
                ErrorAt(
                    Diagnostics.Messages.Identifier_expected,
                    names[^1].Pos == names[^1].End ? names[^1].Pos - 1 : names[^1].Pos,
                    Math.Max(1, names[^1].End - names[^1].Pos));
            NodeList? typeParameters = DocumentationTypeParameters(comment, true);
            NodeList? modifiers = names.Count > 1 ? DocumentationExport(tag) : null;
            var aliasName = (IdentifierNode)CloneDocumentationType(names[^1]);
            SyntaxNode declaration = factory.NewTypeAliasDeclaration(K.JSTypeAliasDeclaration, modifiers, aliasName, typeParameters, type);
            FinishReparse(declaration, tag);
            declaration.Flags |= NodeFlags.HasJSDoc;
            documentation[declaration] = [comment];
            for (int i = names.Count - 2; i >= 0; i--)
            {
                var block = factory.NewModuleBlock(new([declaration], names[i].Pos, name.End));
                FinishReparse(block, name);
                block.Pos = names[i].Pos;
                var module = factory.NewModuleDeclaration(
                    i > 0 ? DocumentationExport(name) : null,
                    K.NamespaceKeyword,
                    CloneDocumentationType(names[i]),
                    null,
                    block);
                FinishReparse(module, name);
                module.Pos = names[i].Pos;
                declaration = module;
                reparsedClones.Add(module);
            }
            reparsedStatements.Add(declaration);
        }
    }

    private (NodeList Parameters, SyntaxNode? Type) DocumentationSignature(JSDocSignatureNode signature)
    {
        var parameters = new List<SyntaxNode>();
        if (signature.Parameters is { } list)
            foreach (SyntaxNode tag in list)
            {
                ParameterDeclarationNode parameter;
                if (tag is JSDocThisTagNode thisTag)
                {
                    var name = factory.NewIdentifier("this");
                    FinishReparse(name, tag);
                    parameter = factory.NewParameterDeclaration(
                        null,
                        null,
                        name,
                        null,
                        thisTag.TypeExpression is ITypedNode { Type: { } type } ? CloneDocumentationType(type) : null,
                        null);
                    if (thisTag.Comment is not null)
                        CopyDocumentationComment(parameter, thisTag, thisTag.Comment);
                }
                else if (tag is JSDocParameterOrPropertyTagNode { Name: IdentifierNode parameterName } parameterTag)
                {
                    SyntaxNode nameClone;
                    if (ValidDocumentationIdentifier(parameterName.Text))
                        nameClone = CloneDocumentationType(parameterName);
                    else
                    {
                        var replacement = new System.Text.StringBuilder();
                        bool first = true;
                        foreach (var rune in parameterName.Text.Span.EnumerateRunes())
                        {
                            replacement.Append(
                                (first ? TokenFacts.IsIdentifierStart(rune.Value) : TokenFacts.IsIdentifierPart(rune.Value))
                                    ? rune.ToString()
                                    : "_");
                            first = false;
                        }
                        if (replacement.Length == 0)
                            replacement.Append('_').Append(parameters.Count);
                        nameClone = factory.NewIdentifier(TextSlice.FromBuilder(replacement));
                        FinishReparse(nameClone, parameterName);
                        nameClone.Flags |= NodeFlags.ReparserTransformedLiteral;
                    }
                    SyntaxNode? type = parameterTag.TypeExpression is ITypedNode typed ? typed.Type : null;
                    TokenNode? rest = null;
                    if (type is JSDocVariadicTypeNode variadic)
                    {
                        rest = factory.NewToken(K.DotDotDotToken);
                        FinishReparse(rest, tag);
                        type = variadic.Type;
                    }
                    parameter = factory.NewParameterDeclaration(
                        null,
                        rest,
                        nameClone,
                        OptionalDocumentationParameter(parameterTag),
                        type is null ? null : CloneDocumentationType(type),
                        null);
                    if (parameterTag.Comment is not null)
                        CopyDocumentationComment(parameter, parameterTag, parameterTag.Comment);
                }
                else
                    continue;
                bool hasComment = (parameter.Flags & NodeFlags.HasJSDoc) != 0;
                FinishReparse(parameter, tag);
                if (hasComment)
                    parameter.Flags |= NodeFlags.HasJSDoc;
                parameters.Add(parameter);
            }
        SyntaxNode? returnType = signature.Type is ITypeExpressionNode { TypeExpression: ITypedNode { Type: { } result } }
            ? CloneDocumentationType(result)
            : null;
        return (new(
            parameters.ToArray(),
            signature.Parameters?.Pos ?? signature.Pos,
            signature.Parameters?.End ?? signature.End), returnType);
    }

    private void CopyDocumentationComment(SyntaxNode node, SyntaxNode tag, NodeList comment)
    {
        var children = comment.Select(n =>
        {
            var clone = n.DeepClone<SyntaxNode>();
            clone.Flags |= NodeFlags.Reparsed;
            return clone;
        }).ToArray();
        var doc = factory.NewJSDoc(new(children, comment.Pos, comment.End), null);
        FinishReparse(doc, tag);
        doc.Parent = node;
        documentation[node] = [doc];
        node.Flags |= NodeFlags.HasJSDoc;
    }

    private NodeList DocumentationExport(SyntaxNode original)
    {
        var modifier = factory.NewToken(K.ExportKeyword);
        FinishReparse(modifier, original);
        return new([modifier], original.Pos, original.End);
    }

    private NodeList? DocumentationTypeParameters(JSDocNode comment, bool typedefOrCallback = false)
    {
        if (comment.Tags is null)
            return null;
        if (!typedefOrCallback && comment.Tags.Any(t => t is JSDocTypedefTagNode or JSDocCallbackTagNode))
            return null;
        var parameters = new List<SyntaxNode>();
        foreach (JSDocTemplateTagNode template in comment.Tags.OfType<JSDocTemplateTagNode>())
            if (template.TypeParameters is { } typeParameters)
            {
                bool first = true;
                foreach (TypeParameterDeclarationNode parameter in typeParameters.OfType<TypeParameterDeclarationNode>())
                {
                    TypeParameterDeclarationNode copy;
                    if (first && template.Constraint is ITypedNode { Type: { } constraint })
                    {
                        NodeList? modifiers = parameter.Modifiers is { } original
                            ? new(original.Select(CloneDocumentationType).ToArray(), original.Pos, original.End)
                            : null;
                        copy = factory.NewTypeParameterDeclaration(modifiers, parameter.Name is null
                            ? null
                            : (IdentifierNode)CloneDocumentationType(parameter.Name), CloneDocumentationType(constraint), null,
                            parameter.DefaultType is null ? null : CloneDocumentationType(parameter.DefaultType));
                        FinishReparse(copy, parameter);
                    }
                    else
                        copy = (TypeParameterDeclarationNode)CloneDocumentationType(parameter);
                    first = false;
                    parameters.Add(copy);
                }
            }
        var templates = comment.Tags.OfType<JSDocTemplateTagNode>().ToArray();
        return parameters.Count == 0 ? null : new(parameters.ToArray(), templates[0].Pos, templates[^1].End);
    }

    private SyntaxNode CloneDocumentationType(SyntaxNode type)
    {
        SyntaxNode CloneLeaf(SyntaxNode node)
        {
            SyntaxNode clone = node.DeepClone<SyntaxNode>(factory);
            clone.Flags |= NodeFlags.Reparsed;
            reparsedClones.Add(clone);
            return clone;
        }
        if (type is not JSDocTypeLiteralNode)
            return CloneLeaf(type);
        var copies = new Dictionary<SyntaxNode, SyntaxNode>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(SyntaxNode Node, bool Visited)>();
        pending.Push((type, false));
        while (pending.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            if (item.Node is not JSDocTypeLiteralNode literal)
            {
                copies[item.Node] = CloneLeaf(item.Node);
                continue;
            }
            if (!item.Visited)
            {
                pending.Push((literal, true));
                foreach (var property in literal.JSDocPropertyTags.OfType<JSDocParameterOrPropertyTagNode>())
                    if (property.TypeExpression is ITypedNode { Type: { } propertyType })
                        pending.Push((propertyType, false));
                continue;
            }
            var properties = new List<SyntaxNode>();
            foreach (var property in literal.JSDocPropertyTags.OfType<JSDocParameterOrPropertyTagNode>())
            {
                SyntaxNode? name = property.Name is QualifiedNameNode qualified ? qualified.Right : property.Name;
                SyntaxNode? cloneName = name is null ? null : CloneLeaf(name);
                if (name is IdentifierNode identifier && !ValidDocumentationIdentifier(identifier.Text))
                {
                    cloneName = factory.NewStringLiteral(identifier.Text, 0);
                    FinishReparse(cloneName, identifier);
                    cloneName.Flags |= NodeFlags.ReparserTransformedLiteral;
                }
                var cloneProperty = factory.NewPropertySignatureDeclaration(null, cloneName, OptionalDocumentationParameter(property),
                    property.TypeExpression is ITypedNode { Type: { } propertyType } ? copies[propertyType] : null, null);
                FinishReparse(cloneProperty, property);
                if (property.Comment is not null)
                    CopyDocumentationComment(cloneProperty, property, property.Comment);
                properties.Add(cloneProperty);
            }
            SyntaxNode result = factory.NewTypeLiteralNode(new(properties.ToArray(), literal.Pos, literal.End));
            FinishReparse(result, literal);
            if (literal.IsArrayType)
            {
                result = factory.NewArrayTypeNode(result);
                FinishReparse(result, literal);
            }
            copies[literal] = result;
        }
        return copies[type];
    }

    private void FinishReparse(SyntaxNode result, SyntaxNode original)
    {
        result.Pos = original.Pos;
        result.End = original.End;
        result.Flags = context | NodeFlags.Reparsed;
        for (int i = 0; i < result.ChildCount; i++)
            result.GetChild(i).Parent = result;
    }

    private TokenNode? OptionalDocumentationParameter(JSDocParameterOrPropertyTagNode tag)
    {
        if (!tag.IsBracketed && tag.TypeExpression is not ITypedNode { Type: JSDocOptionalTypeNode })
            return null;
        var question = factory.NewToken(K.QuestionToken);
        FinishReparse(question, tag);
        return question;
    }

    private static SyntaxNode? FunctionHost(SyntaxNode node)
    {
        while (true)
        {
            if (node is IFunctionSignature)
                return node;
            SyntaxNode? next = node switch
            {
                VariableStatementNode { DeclarationList.Declarations: { Count: > 0 } declarations } => declarations[0],
                IInitializedNode { Initializer: { } initializer } => initializer,
                ExpressionStatementNode { Expression: { } expression } => expression,
                BinaryExpressionNode { Right: { } right } => right,
                ParenthesizedExpressionNode { Expression: { } expression } => expression,
                ReturnStatementNode { Expression: { } expression } => expression,
                ExportAssignmentNode { Expression: { } expression } => expression,
                SatisfiesExpressionNode { Expression: { } expression } => expression,
                _ => null,
            };
            if (next is null)
                return null;
            node = next;
        }
    }

    private void ReparseDocumentation(SyntaxNode parent, JSDocNode comment)
    {
        if (comment.Tags is null)
            return;
        foreach (SyntaxNode tag in comment.Tags)
        {
            SyntaxNode? taggedType = tag is ITypeExpressionNode { TypeExpression: ITypedNode { Type: { } type } } ? type : null;
            SyntaxNode? host = FunctionHost(parent);
            switch (tag)
            {
                case JSDocTypeTagNode when taggedType is not null:
                    if (parent is VariableStatementNode { DeclarationList.Declarations: { } declarations })
                    {
                        foreach (VariableDeclarationNode declaration in declarations.OfType<VariableDeclarationNode>())
                            if (declaration.Type is null)
                            {
                                declaration.Type = CloneDocumentationType(taggedType);
                                declaration.Type.Parent = declaration;
                                break;
                            }
                    }
                    else if (parent is ITypedNode typed && parent is not IFunctionSignature || parent is GetAccessorDeclarationNode)
                    {
                        var target = (ITypedNode)parent;
                        if (target.Type is null)
                        {
                            target.Type = CloneDocumentationType(taggedType);
                            target.Type.Parent = parent;
                        }
                    }
                    else if (parent is ExpressionStatementNode { Expression: BinaryExpressionNode binary })
                    {
                        binary.Type = CloneDocumentationType(taggedType);
                        binary.Type.Parent = binary;
                    }
                    else if (parent is ParenthesizedExpressionNode or ReturnStatementNode)
                        ApplyDocumentationCast(parent, taggedType, true);
                    else if (host is IFullSignatureNode signature
                        && host is IFunctionSignature { Type: null, TypeParameters: null } function
                        && signature.FullSignature is null &&
                        function.Parameters?.OfType<ParameterDeclarationNode>().Any(p => p.Type is not null) != true)
                    {
                        signature.FullSignature = CloneDocumentationType(taggedType);
                        signature.FullSignature.Parent = host;
                    }
                    break;
                case JSDocSatisfiesTagNode when taggedType is not null:
                    ApplyDocumentationCast(parent, taggedType, false);
                    break;
                case JSDocReturnTagNode when taggedType is not null
                    && host is IFunctionSignature function
                    && host is not IFullSignatureNode { FullSignature: not null }:
                    if (function.Type is null)
                    {
                        function.Type = CloneDocumentationType(taggedType);
                        function.Type.Parent = host;
                    }
                    break;
                case JSDocParameterOrPropertyTagNode { Kind: K.JSDocParameterTag } parameterTag
                    when host is IFunctionSignature { Parameters: { } parameters }
                    && host is not IFullSignatureNode { FullSignature: not null }:
                    int tagIndex = 0;
                    foreach (SyntaxNode sibling in comment.Tags)
                    {
                        if (ReferenceEquals(sibling, parameterTag))
                            break;
                        if (sibling.Kind == K.JSDocParameterTag)
                            tagIndex++;
                    }
                    int parameterIndex = 0;
                    foreach (ParameterDeclarationNode parameter in parameters.OfType<ParameterDeclarationNode>())
                    {
                        if (parameter.Name is IdentifierNode { Text.Span: "this" } || parameter.Name?.Kind == K.ThisKeyword)
                            continue;
                        bool matches = parameter.Name is IdentifierNode parameterName
                            ? parameterTag.Name is IdentifierNode tagName
                                && (parameterName.Text == tagName.Text || tagName.Text.Length == 0 && parameterIndex == tagIndex)
                            : parameterIndex == tagIndex;
                        parameterIndex++;
                        if (!matches)
                            continue;
                        if (parameter.Type is null && taggedType is not null)
                        {
                            parameter.Type = CloneDocumentationType(taggedType);
                            parameter.Type.Parent = parameter;
                        }
                        parameter.QuestionToken ??= OptionalDocumentationParameter(parameterTag);
                        if (parameter.QuestionToken is not null)
                            parameter.QuestionToken.Parent = parameter;
                        break;
                    }
                    break;
                case JSDocThisTagNode thisTag when host is IFunctionSignature function:
                    if (function.Parameters is { Count: > 0 } existingParameters
                        && existingParameters[0] is ParameterDeclarationNode { Name: IdentifierNode { Text.Span: "this" } })
                        break;
                    var thisName = factory.NewIdentifier("this");
                    FinishReparse(thisName, thisTag.TagName!);
                    var thisParameter = factory.NewParameterDeclaration(
                        null,
                        null,
                        thisName,
                        null,
                        taggedType is null ? null : CloneDocumentationType(taggedType),
                        null);
                    FinishReparse(thisParameter, thisTag.TagName!);
                    thisParameter.Parent = host;
                    function.Parameters = new(
                        [thisParameter, .. (IEnumerable<SyntaxNode>?)function.Parameters ?? []],
                        function.Parameters?.Pos ?? thisTag.Pos,
                        function.Parameters?.End ?? thisTag.End);
                    break;
                case JSDocTemplateTagNode:
                    if (host is IFullSignatureNode { FullSignature: not null })
                        break;
                    if (host is not IFunctionSignature { TypeParameters: null }
                        && parent is not ClassDeclarationNode { TypeParameters: null }
                        && parent is not ClassExpressionNode { TypeParameters: null })
                        break;
                    var list = DocumentationTypeParameters(comment);
                    if (list is null)
                        break;
                    if (host is IFunctionSignature f && f.TypeParameters is null)
                    {
                        f.TypeParameters = list;
                        foreach (var node in list)
                            node.Parent = host;
                    }
                    else if (parent is ClassDeclarationNode c && c.TypeParameters is null)
                    {
                        c.TypeParameters = list;
                        foreach (var node in list)
                            node.Parent = c;
                    }
                    else if (parent is ClassExpressionNode e && e.TypeParameters is null)
                    {
                        e.TypeParameters = list;
                        foreach (var node in list)
                            node.Parent = e;
                    }
                    break;
                case JSDocReadonlyTagNode or JSDocPrivateTagNode or JSDocPublicTagNode or JSDocProtectedTagNode or JSDocOverrideTagNode:
                    if (parent is ExpressionStatementNode { Expression: { } assignment })
                        parent = assignment;
                    if (objectLiteralDepth != 0 && parent.Kind is K.MethodDeclaration or K.GetAccessor or K.SetAccessor)
                        break;
                    if (parent is IModifiedNode modified
                        && parent.Kind is K.PropertyDeclaration or K.MethodDeclaration or K.GetAccessor or K.SetAccessor or K.Constructor
                            or K.BinaryExpression)
                    {
                        K kind = tag.Kind switch
                        {
                            K.JSDocReadonlyTag => K.ReadonlyKeyword,
                            K.JSDocPrivateTag => K.PrivateKeyword,
                            K.JSDocPublicTag => K.PublicKeyword,
                            K.JSDocProtectedTag => K.ProtectedKeyword,
                            _ => K.OverrideKeyword
                        };
                        var modifier = factory.NewToken(kind);
                        FinishReparse(modifier, tag);
                        modifier.Parent = parent;
                        modified.Modifiers = new(
                            [.. (IEnumerable<SyntaxNode>?)modified.Modifiers ?? [], modifier],
                            modified.Modifiers?.Pos ?? tag.Pos,
                            modified.Modifiers?.End ?? tag.End);
                    }
                    break;
                case JSDocImplementsTagNode { ClassName: { } className }:
                    if (parent is ClassDeclarationNode or ClassExpressionNode)
                    {
                        NodeList? heritage = parent is ClassDeclarationNode declaration
                            ? declaration.HeritageClauses
                            : ((ClassExpressionNode)parent).HeritageClauses;
                        SyntaxNode cloned = CloneDocumentationType(className);
                        var existing = heritage?.OfType<HeritageClauseNode>().FirstOrDefault(c => c.Token == K.ImplementsKeyword);
                        if (existing is not null)
                        {
                            existing.Types = new(
                                [.. (IEnumerable<SyntaxNode>?)existing.Types ?? [], cloned],
                                existing.Types?.Pos ?? className.Pos,
                                existing.Types?.End ?? className.End);
                            cloned.Parent = existing;
                        }
                        else
                        {
                            var clause = factory.NewHeritageClause(K.ImplementsKeyword, new([cloned], className.Pos, className.End));
                            FinishReparse(clause, className);
                            clause.Parent = parent;
                            var updated = new NodeList(
                                [.. (IEnumerable<SyntaxNode>?)heritage ?? [], clause],
                                heritage?.Pos ?? className.Pos,
                                heritage?.End ?? className.End);
                            if (parent is ClassDeclarationNode d)
                                d.HeritageClauses = updated;
                            else
                                ((ClassExpressionNode)parent).HeritageClauses = updated;
                        }
                    }
                    break;
                case JSDocAugmentsTagNode { ClassName: { TypeArguments: { } arguments } className }:
                    NodeList? bases = parent is ClassDeclarationNode d1
                        ? d1.HeritageClauses
                        : parent is ClassExpressionNode e1 ? e1.HeritageClauses : null;
                    var clause1 = bases?.OfType<HeritageClauseNode>().FirstOrDefault(c => c.Token == K.ExtendsKeyword);
                    if (clause1?.Types is { Count: 1 } types
                        && types[0] is ExpressionWithTypeArgumentsNode { TypeArguments: null } extendsTarget)
                    {
                        if (NameText(extendsTarget.Expression) == NameText(className.Expression))
                        {
                            extendsTarget.TypeArguments = new(
                                arguments.Select(CloneDocumentationType).ToArray(),
                                arguments.Pos,
                                arguments.End);
                            foreach (var arg in extendsTarget.TypeArguments)
                                arg.Parent = extendsTarget;
                        }
                    }
                    break;
            }
        }
    }

    private void ApplyDocumentationCast(SyntaxNode parent, SyntaxNode type, bool assertion)
    {
        if (parent is VariableStatementNode { DeclarationList.Declarations: { Count: > 0 } declarations })
            parent = declarations[0];
        switch (parent)
        {
            case IInitializedNode { Initializer: { } initializer } node:
                node.Initializer = DocumentationCast(type, initializer, assertion);
                node.Initializer.Parent = parent;
                break;
            case ParenthesizedExpressionNode { Expression: { } expression } node:
                node.Expression = DocumentationCast(type, expression, assertion);
                node.Expression.Parent = node;
                break;
            case ReturnStatementNode { Expression: { } expression } node:
                node.Expression = DocumentationCast(type, expression, assertion);
                node.Expression.Parent = node;
                break;
            case ExportAssignmentNode { Expression: { } expression } node:
                node.Expression = DocumentationCast(type, expression, assertion);
                node.Expression.Parent = node;
                break;
            case ExpressionStatementNode { Expression: BinaryExpressionNode { Right: { } expression } node }:
                node.Right = DocumentationCast(type, expression, assertion);
                node.Right.Parent = node;
                break;
        }
    }

    private SyntaxNode DocumentationCast(SyntaxNode type, SyntaxNode expression, bool assertion)
    {
        SyntaxNode cast = assertion
            ? factory.NewAsExpression(expression, CloneDocumentationType(type))
            : factory.NewSatisfiesExpression(expression, CloneDocumentationType(type));
        return Finish(cast, expression.Pos, expression.End);
    }

    private static bool ValidDocumentationIdentifier(ReadOnlySpan<char> text)
    {
        bool first = true;
        foreach (var rune in text.EnumerateRunes())
        {
            if (first ? !TokenFacts.IsIdentifierStart(rune.Value) : !TokenFacts.IsIdentifierPart(rune.Value))
                return false;
            first = false;
        }
        return !first;
    }

    private static TextSlice NameText(SyntaxNode? node) => SyntaxNameText.Get(node);
}
