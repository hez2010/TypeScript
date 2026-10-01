using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class DeclarationTransformer
{
    private async ValueTask<SyntaxNode?> EnsureTypeAsync(SyntaxNode node, bool ignorePrivate = false)
    {
        if (!ignorePrivate && await PrivateAsync(node) || await LiteralInitializerAsync(node)) return null;
        if (node is not (ExportAssignmentNode or BindingElementNode) && node is ITypedNode { Type: { } annotation }
            && (node is not ParameterDeclarationNode || !await checker.RequiresImplicitUndefinedForEmitAsync(node, null, enclosing, Cancellation)))
        {
            if ((source.Flags & NodeFlags.JavaScriptFile) == 0) return await VisitAsync(annotation);
            if (await checker.CreateJsTypeForEmitAsync(annotation, enclosing, Context, CurrentBuilderFlags, tracker, InternalFlags, Cancellation) is { } jsType)
            { await FlushInferenceAsync(); return jsType; }
        }
        var saved = SaveContext();
        try
        {
            tracker.ErrorName = DeclarationDiagnostics.Name(node);
            SetDiagnosticContext(node);
            var type = node.HasFunctionSignature
                ? await checker.CreateReturnTypeForEmitAsync(node, enclosing, Context, CurrentBuilderFlags, tracker, InternalFlags, Cancellation)
                : await checker.CreateDeclarationTypeForEmitAsync(node, enclosing, Context, CurrentBuilderFlags, tracker, InternalFlags, Cancellation);
            await FlushInferenceAsync();
            return type ?? F.NewKeywordTypeNode(K.AnyKeyword);
        }
        finally { RestoreContext(saved); }
    }

    private async ValueTask<NodeList?> TypeParametersAsync(SyntaxNode node, NodeList? parameters)
    {
        if (await PrivateAsync(node)) return null;
        if (parameters is not null) return await VisitListAsync(parameters);
        if (node is not IFullSignatureNode { FullSignature: not null }) return null;
        var saved = SaveContext();
        try
        {
            tracker.ErrorName = SemanticSyntax.Name(node);
            SetDiagnosticContext(node);
            var result = await checker.CreateTypeParametersForEmitAsync(node, enclosing, Context, CurrentBuilderFlags, tracker, InternalFlags, Cancellation);
            await FlushInferenceAsync();
            return result;
        }
        finally { RestoreContext(saved); }
    }

    private async ValueTask<NodeList> ParametersAsync(SyntaxNode node, NodeList? parameters)
    {
        if (parameters is null || parameters.Count == 0 || await PrivateAsync(node)) return new([]);
        List<SyntaxNode> result = [];
        foreach (var parameter in parameters) result.Add(await ParameterAsync((ParameterDeclarationNode)parameter));
        return new(result.ToArray());
    }

    private async ValueTask<ParameterDeclarationNode> ParameterAsync(ParameterDeclarationNode node)
    {
        var saved = SaveContext();
        try
        {
            SetDiagnosticContext(node);
            var result = Context.Clone(node);
            result.Modifiers = null;
            result.Name = await BindingNameAsync(node.Name);
            result.QuestionToken = await checker.IsOptionalParameterForEmitAsync(node, Cancellation) ? node.QuestionToken ?? F.NewToken(K.QuestionToken) : null;
            result.Type = await EnsureTypeAsync(node, ignorePrivate: true);
            result.Initializer = await InitializerAsync(node);
            return result;
        }
        finally { RestoreContext(saved); }
    }

    private async ValueTask<bool> LiteralInitializerAsync(SyntaxNode node) => node is IInitializedNode { Initializer: not null }
        && (node is VariableDeclarationNode or ParameterDeclarationNode || node is PropertyDeclarationNode or PropertySignatureDeclarationNode && !await PrivateAsync(node))
        && await checker.IsLiteralConstDeclarationAsync(Context.MostOriginal(node), Cancellation);

    private async ValueTask<SyntaxNode?> InitializerAsync(SyntaxNode node)
    {
        if (!await LiteralInitializerAsync(node)) return null;
        if (!Primitive(Unwrap(((IInitializedNode)node).Initializer!))) tracker.ReportInferenceFallback(node);
        var result = await checker.CreateLiteralConstForEmitAsync(Context.MostOriginal(node), Context, Cancellation);
        if (result is not null && ConstantEvaluator.EntityName(result)
            && node is IInitializedNode { Initializer: { } initializer } && ConstantEvaluator.EntityName(Unwrap(initializer)))
            await CheckEntityAsync(Unwrap(initializer));
        await FlushInferenceAsync();
        return result;
    }

    private async ValueTask<bool> PrivateAsync(SyntaxNode node) =>
        await checker.GetEffectiveDeclarationFlagsForEmitAsync(Context.MostOriginal(node), ModifierFlags.Private, Cancellation) != 0;

    private static SyntaxNode Unwrap(SyntaxNode node)
    {
        while (node is ParenthesizedExpressionNode { Expression: { } expression }) node = expression;
        return node;
    }
    private static bool Primitive(SyntaxNode node) => node.Kind is K.TrueKeyword or K.FalseKeyword or K.NumericLiteral or K.StringLiteral
        or K.NoSubstitutionTemplateLiteral or K.BigIntLiteral || node is PrefixUnaryExpressionNode { Operator: K.MinusToken, Operand: NumericLiteralNode or BigIntLiteralNode }
        or PrefixUnaryExpressionNode { Operator: K.PlusToken, Operand: NumericLiteralNode };

    private async ValueTask<SyntaxNode?> BindingNameAsync(SyntaxNode? node)
    {
        if (node is null) return null;
        return await new BindingNameRewriter(this).VisitAsync(node);
    }

    private sealed class BindingNameRewriter(DeclarationTransformer owner) : SyntaxRewriter(owner.Context, owner.Cancellation)
    {
        protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
        {
            if (node is BindingPatternNode) return await VisitEachChildAsync(node);
            if (node is not BindingElementNode binding) return node;
            if (binding.PropertyName is ComputedPropertyNameNode { Expression: { } expression } && ConstantEvaluator.EntityName(expression))
                await owner.CheckEntityAsync(expression);
            var result = Context.Clone(binding);
            result.Name = await VisitAsync(binding.Name);
            result.Initializer = null;
            return result;
        }
    }

    private async ValueTask<SyntaxNode?> SubtreeWorkerAsync(SyntaxNode node)
    {
        switch (node)
        {
            case ParameterDeclarationNode parameter: return await ParameterAsync(parameter);
            case VariableDeclarationNode variable: return await VariableAsync(variable);
            case PropertyDeclarationNode or PropertySignatureDeclarationNode: return await PropertyAsync(node);
            case IndexSignatureDeclarationNode index:
                var updatedIndex = Context.Clone(index);
                updatedIndex.Modifiers = Modifiers(index);
                updatedIndex.Parameters = await ParametersAsync(index, index.Parameters);
                updatedIndex.Type = await VisitAsync(index.Type) ?? F.NewKeywordTypeNode(K.AnyKeyword);
                return updatedIndex;
            case IFunctionSignature: return await SignatureAsync(node);
            case TypeParameterDeclarationNode parameter:
                if (parameter.Parent is MethodDeclarationNode method && await PrivateAsync(method))
                {
                    var result = Context.Clone(parameter); result.Constraint = result.DefaultType = null; return result;
                }
                return await VisitEachChildAsync(parameter);
            case MappedTypeNode mapped:
                var updatedMapped = Context.Clone(mapped);
                updatedMapped.TypeParameter = (TypeParameterDeclarationNode?)await VisitAsync(mapped.TypeParameter);
                updatedMapped.NameType = await VisitAsync(mapped.NameType);
                updatedMapped.Type = await VisitAsync(mapped.Type) ?? F.NewKeywordTypeNode(K.AnyKeyword);
                updatedMapped.Members = null;
                return updatedMapped;
            case HeritageClauseNode heritage:
                List<SyntaxNode> retained = [];
                foreach (var type in heritage.Types ?? new([]))
                {
                    var heritageName = type is ExpressionWithTypeArgumentsNode element ? element.Expression : type is TypeReferenceNode referenceType ? referenceType.TypeName : type;
                    if (heritageName is not null && (heritageName.Pos != heritageName.End || heritageName.Pos < 0)
                        && (heritageName is QualifiedNameNode || ConstantEvaluator.EntityName(heritageName) || heritage.Token == K.ExtendsKeyword && heritageName.Kind == K.NullKeyword)) retained.Add(type);
                }
                if (retained.Count == 0) return null;
                var updatedHeritage = Context.Clone(heritage);
                updatedHeritage.Types = await VisitListAsync(new(retained.ToArray(), heritage.Types?.Pos ?? -1, heritage.Types?.End ?? -1));
                return updatedHeritage;
            case TypeReferenceNode reference:
                if (reference.TypeName is not null) await CheckEntityAsync(reference.TypeName);
                return await VisitEachChildAsync(node);
            case ExpressionWithTypeArgumentsNode expression:
                if (expression.Expression is { } entity && ConstantEvaluator.EntityName(entity)) await CheckEntityAsync(entity);
                return await VisitEachChildAsync(node);
            case TypeQueryNode query:
                if (query.ExprName is not null) await CheckEntityAsync(query.ExprName);
                return await VisitEachChildAsync(node);
            case QualifiedNameNode { Right: PrivateIdentifierNode privateName }:
                Report(node, Messages.Declaration_emit_elides_private_members_but_0_refers_to_a_private_member_Write_an_explicit_type_here, privateName.Text);
                return await VisitEachChildAsync(node);
            case ConditionalTypeNode conditional:
                var updatedConditional = Context.Clone(conditional);
                updatedConditional.CheckType = await VisitAsync(conditional.CheckType);
                updatedConditional.ExtendsType = await VisitAsync(conditional.ExtendsType);
                var saved = enclosing;
                try { enclosing = conditional.TrueType!; updatedConditional.TrueType = await VisitAsync(conditional.TrueType); }
                finally { enclosing = saved; }
                updatedConditional.FalseType = await VisitAsync(conditional.FalseType);
                return updatedConditional;
            case ImportTypeNode import:
                if (import.Argument is not LiteralTypeNode { Literal: StringLiteralNode }) return import;
                var updatedImport = Context.Clone(import);
                updatedImport.TypeArguments = await VisitListAsync(import.TypeArguments);
                return updatedImport;
            case TupleTypeNode tuple:
                var updatedTuple = (await VisitEachChildAsync(tuple))!;
                if (tuple.Pos >= 0 && tuple.End <= source.Source.Text.Length && source.Source.Text.Span[tuple.Pos..tuple.End].IndexOfAny((byte)'\r', (byte)'\n') < 0)
                    Context.AddFlags(updatedTuple, EmitFlags.SingleLine);
                return updatedTuple;
            case JSDocTypeExpressionNode or JSDocNonNullableTypeNode:
                return await VisitAsync(((ITypedNode)node).Type);
            case JSDocTypeLiteralNode literal:
                return Replaced(F.NewTypeLiteralNode(new(await VisitArrayAsync(literal.JSDocPropertyTags))), node);
            case JSDocParameterOrPropertyTagNode { Kind: K.JSDocPropertyTag } property:
                return Replaced(F.NewPropertySignatureDeclaration(null, await VisitAsync(property.TagName), null, await VisitAsync(property.TypeExpression), null), node);
            case JSDocAllTypeNode:
                return Replaced(F.NewKeywordTypeNode(K.AnyKeyword), node);
            case JSDocNullableTypeNode nullable:
                return Replaced(F.NewUnionTypeNode(new([(await VisitAsync(nullable.Type))!, F.NewLiteralTypeNode(F.NewKeywordExpression(K.NullKeyword))])), node);
            case JSDocOptionalTypeNode optional:
                return Replaced(F.NewUnionTypeNode(new([(await VisitAsync(optional.Type))!, F.NewKeywordTypeNode(K.UndefinedKeyword)])), node);
            case JSDocVariadicTypeNode variadic:
                return Replaced(F.NewArrayTypeNode(await VisitAsync(variadic.Type)), node);
            default: return await VisitEachChildAsync(node);
        }
    }

    private async ValueTask<SyntaxNode?> PropertyAsync(SyntaxNode node)
    {
        if (SemanticSyntax.Name(node) is PrivateIdentifierNode) return null;
        var result = Context.Clone(node);
        ((IModifiedNode)result).Modifiers = Modifiers(node);
        ((ITypedNode)result).Type = await EnsureTypeAsync(node);
        ((IInitializedNode)result).Initializer = await InitializerAsync(node);
        if (result is PropertyDeclarationNode { PostfixToken.Kind: K.ExclamationToken } property) property.PostfixToken = null;
        if (node is PropertySignatureDeclarationNode && (node.Flags & NodeFlags.Reparsed) != 0)
            await PreservePropertyDocumentationAsync(result, node);
        return result;
    }

    private async ValueTask<SyntaxNode?> SignatureAsync(SyntaxNode node)
    {
        if (SemanticSyntax.Name(node) is PrivateIdentifierNode) return null;
        var isPrivate = await PrivateAsync(node);
        if (isPrivate && node is MethodDeclarationNode or MethodSignatureDeclarationNode)
        {
            if (checker.Symbols.Declaration(node)?.Declarations.FirstOrDefault() is { } first && first != node) return null;
            var property = node is MethodSignatureDeclarationNode
                ? (SyntaxNode)F.NewPropertySignatureDeclaration(Modifiers(node), SemanticSyntax.Name(node), null, null, null)
                : F.NewPropertyDeclaration(Modifiers(node), SemanticSyntax.Name(node), null, null, null);
            return Located(property, node);
        }
        var result = Context.Clone(node);
        var signature = (IFunctionSignature)result;
        var original = (IFunctionSignature)node;
        if (result is IModifiedNode modified) modified.Modifiers = Modifiers(node);
        if (result is IFullSignatureNode full) full.FullSignature = null;
        signature.TypeParameters = node is ConstructorDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode ? null
            : node is FunctionTypeNode or ConstructorTypeNode ? await VisitListAsync(original.TypeParameters)
            : await TypeParametersAsync(node, original.TypeParameters);
        signature.Parameters = node is GetAccessorDeclarationNode or SetAccessorDeclarationNode
            ? await AccessorParametersAsync(node, isPrivate) : await ParametersAsync(node, original.Parameters);
        signature.Type = node is ConstructorDeclarationNode or SetAccessorDeclarationNode ? null
            : node is FunctionTypeNode or ConstructorTypeNode ? await VisitAsync(original.Type) : await EnsureTypeAsync(node);
        switch (result)
        {
            case FunctionDeclarationNode function: function.AsteriskToken = null; function.Body = null; break;
            case MethodDeclarationNode method: method.AsteriskToken = null; method.Body = null; break;
            case ConstructorDeclarationNode constructor: constructor.Body = null; break;
            case GetAccessorDeclarationNode getter: getter.Body = null; break;
            case SetAccessorDeclarationNode setter: setter.Body = null; break;
        }
        return result;
    }

    private async ValueTask<NodeList> AccessorParametersAsync(SyntaxNode node, bool isPrivate)
    {
        List<SyntaxNode> parameters = [];
        var originals = ((IFunctionSignature)node).Parameters;
        if (!isPrivate && originals is { Count: > 0 } && SemanticSyntax.Name(originals[0]) is IdentifierNode { Text: var name } && name == "this"u8)
            parameters.Add(await ParameterAsync((ParameterDeclarationNode)originals[0]));
        if (node is SetAccessorDeclarationNode)
        {
            SyntaxNode? value = null;
            if (!isPrivate && originals is not null && originals.Count > parameters.Count) value = await ParameterAsync((ParameterDeclarationNode)originals[parameters.Count]);
            parameters.Add(value ?? F.NewParameterDeclaration(null, null, F.NewIdentifier("value"u8), null, isPrivate ? null : F.NewKeywordTypeNode(K.AnyKeyword), null));
        }
        return new(parameters.ToArray());
    }

    private T Located<T>(T node, SyntaxNode original) where T : SyntaxNode
    {
        Context.SetCommentRange(node, new(original.Pos, original.End));
        return node;
    }
    private T Replaced<T>(T node, SyntaxNode original) where T : SyntaxNode
    {
        Context.SetOriginal(node, original);
        return node;
    }

    private static readonly (ModifierFlags Flag, K Kind)[] ModifierOrder =
    [
        (ModifierFlags.Export, K.ExportKeyword), (ModifierFlags.Ambient, K.DeclareKeyword), (ModifierFlags.Default, K.DefaultKeyword),
        (ModifierFlags.Const, K.ConstKeyword), (ModifierFlags.Public, K.PublicKeyword), (ModifierFlags.Private, K.PrivateKeyword),
        (ModifierFlags.Protected, K.ProtectedKeyword), (ModifierFlags.Abstract, K.AbstractKeyword), (ModifierFlags.Static, K.StaticKeyword),
        (ModifierFlags.Override, K.OverrideKeyword), (ModifierFlags.Readonly, K.ReadonlyKeyword), (ModifierFlags.Accessor, K.AccessorKeyword),
        (ModifierFlags.Async, K.AsyncKeyword), (ModifierFlags.In, K.InKeyword), (ModifierFlags.Out, K.OutKeyword)
    ];
    private static ModifierFlags CombinedFlags(SyntaxNode node)
    {
        var root = SemanticSyntax.RootDeclaration(node);
        if (root is VariableDeclarationNode) root = root.Parent!;
        if (root is VariableDeclarationListNode) root = root.Parent!;
        ModifierFlags result = 0;
        foreach (var modifier in root.ModifierList ?? new([]))
        {
            foreach (var (flag, kind) in ModifierOrder) if (modifier.Kind == kind) result |= flag;
            if (modifier.Kind == K.Decorator) result |= ModifierFlags.Decorator;
        }
        return result;
    }
    private NodeList? Modifiers(SyntaxNode node)
    {
        var current = CombinedFlags(node);
        var flags = current & ~(ModifierFlags.Public | ModifierFlags.Async | ModifierFlags.Override);
        if (node.Parent is not SourceFileNode) flags &= ~ModifierFlags.Ambient;
        else if (needsDeclare && node is not InterfaceDeclarationNode) flags |= ModifierFlags.Ambient;
        if (node.Parent is SourceFileNode file && (file.ExternalModuleIndicator is not null || checker.Symbols.Binding(file)?.CommonJSModuleIndicator is not null)
            && (node.Kind == K.JSTypeAliasDeclaration || node is ModuleDeclarationNode && (node.Flags & NodeFlags.Reparsed) != 0)) flags |= ModifierFlags.Export;
        if ((flags & ModifierFlags.Default) != 0) flags = (flags | ModifierFlags.Export) & ~ModifierFlags.Ambient;
        if (flags == current && !(node.ModifierList?.Any(modifier => (modifier.Flags & NodeFlags.Reparsed) != 0) ?? false))
            return node.ModifierList is { } modifiers ? new(modifiers.Where(modifier => modifier.Kind != K.Decorator).ToArray()) : null;
        return FromModifierFlags(flags);
    }
    private NodeList? FromModifierFlags(ModifierFlags flags)
    {
        var nodes = ModifierOrder.Where(item => (flags & item.Flag) != 0).Select(item => (SyntaxNode)F.NewToken(item.Kind)).ToArray();
        return nodes.Length == 0 ? null : new(nodes);
    }
}
