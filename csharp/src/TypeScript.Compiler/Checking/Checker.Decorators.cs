using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private bool CanDecorate(SyntaxNode node)
    {
        if (LegacyDecorators && SemanticSyntax.Name(node) is PrivateIdentifierNode)
            return false;
        return node switch
        {
            ClassDeclarationNode => true,
            ClassExpressionNode => !LegacyDecorators,
            PropertyDeclarationNode => LegacyDecorators ? node.Parent is ClassDeclarationNode
                : SemanticSyntax.ClassLike(node.Parent) && !SemanticSyntax.HasModifier(node, SyntaxKind.AbstractKeyword)
                    && !SemanticSyntax.HasModifier(node, SyntaxKind.DeclareKeyword),
            MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode => SemanticSyntax.Body(node) is not null
                && (LegacyDecorators ? node.Parent is ClassDeclarationNode : SemanticSyntax.ClassLike(node.Parent)),
            ParameterDeclarationNode => LegacyDecorators
                && node.Parent is ConstructorDeclarationNode or MethodDeclarationNode or SetAccessorDeclarationNode
                && SemanticSyntax.Body(node.Parent) is not null && node.Parent.Parent is ClassDeclarationNode
                && SemanticSyntax.Name(node) is not IdentifierNode { Text: "this" },
            _ => false
        };
    }

    private bool DecoratorGrammar(SyntaxNode node)
    {
        if (node is not IModifiedNode { Modifiers: { } modifiers } || SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0)
            return false;
        bool leading = false, seenModifier = false, otherModifier = false, exportBeforeDecorator = false;
        DecoratorNode? firstLeading = null, afterExport = null;
        foreach (var modifier in modifiers)
        {
            if (modifier is DecoratorNode)
            {
                if (node is ParameterDeclarationNode { Name: IdentifierNode { Text: "this" } })
                {
                    Error(node, 1433);
                    return true;
                }
                if (!CanDecorate(node))
                {
                    ErrorOnFirstToken(node, node is MethodDeclarationNode && SemanticSyntax.Body(node) is null ? 1249 : 1206);
                    return true;
                }
                if (LegacyDecorators && node is GetAccessorDeclarationNode or SetAccessorDeclarationNode)
                {
                    var accessors = program.Symbols.Declaration(node)!.Declarations.Where(
                        n => n is GetAccessorDeclarationNode or SetAccessorDeclarationNode).ToArray();
                    if (accessors.Length > 1 && node == accessors[1] && HasDecorators(accessors[0]))
                    {
                        ErrorOnFirstToken(node, 1207);
                        return true;
                    }
                }
                if (otherModifier)
                {
                    Error(modifier, 1206);
                    return true;
                }
                if (leading && seenModifier)
                {
                    Error(modifier, CheckerDiagnostic.Create(modifier,
                        Messages.Decorators_may_not_appear_after_export_or_export_default_if_they_also_appear_before_export) with
                    { RelatedInformation = [CheckerDiagnostic.Create(firstLeading!, Messages.Decorator_used_before_export_here)] });
                    return true;
                }
                if (!seenModifier)
                {
                    leading = true;
                    firstLeading ??= (DecoratorNode)modifier;
                }
                else
                {
                    exportBeforeDecorator = true;
                    afterExport ??= (DecoratorNode)modifier;
                }
            }
            else
            {
                if (modifier.Kind == SyntaxKind.DefaultKeyword && exportBeforeDecorator)
                {
                    Error(afterExport!, 1206);
                    return true;
                }
                seenModifier = true;
                otherModifier |= modifier.Kind is not (SyntaxKind.ExportKeyword or SyntaxKind.DefaultKeyword);
            }
        }
        return false;
    }

    private static bool HasDecorators(SyntaxNode node) => node is IModifiedNode { Modifiers: { } modifiers }
        && modifiers.Any(m => m is DecoratorNode);

    private async ValueTask CheckDecoratorsAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (!HasDecorators(node) || !CanDecorate(node))
            return;
        var decorators = ((IModifiedNode)node).Modifiers!.OfType<DecoratorNode>().ToArray();
        var first = decorators[0];
        if (LegacyDecorators)
        {
            await ExternalHelpersAsync(first, node is ParameterDeclarationNode ? ["__decorate", "__param"] : ["__decorate"], cancellation);
            if (program.Symbols.Program.Configuration.Options.Boolean("emitDecoratorMetadata") == true)
                await DecoratorMetadataAsync(node, first, cancellation);
        }
        else if (TargetYear < int.MaxValue)
        {
            await ExternalHelpersAsync(first, ["__esDecorate", "__runInitializers"], cancellation);
            if (node is ClassDeclarationNode && (SemanticSyntax.Name(node) is null
                || PropertyInitialization.Members(node).Any(m => HasDecorators(m) && CanDecorate(m) || m is ClassStaticBlockDeclarationNode
                    || SemanticSyntax.IsStatic(m) && (SemanticSyntax.Name(m) is PrivateIdentifierNode
                        || !(TargetYear >= 2022 && UseDefineForClassFields) && m is PropertyDeclarationNode { Initializer: not null })))
                || SemanticSyntax.Name(node) is PrivateIdentifierNode && (node is MethodDeclarationNode or GetAccessorDeclarationNode
                    or SetAccessorDeclarationNode
                    || SemanticSyntax.HasModifier(node, SyntaxKind.AccessorKeyword)))
                await ExternalHelpersAsync(first, ["__setFunctionName"], cancellation);
            if (!SemanticSyntax.ClassLike(node) && SemanticSyntax.Name(node) is ComputedPropertyNameNode)
                await ExternalHelpersAsync(first, ["__propKey"], cancellation);
        }
        foreach (var decorator in decorators)
        {
            DecoratorExpressionGrammar(decorator);
            var signature = await CallResolution.GetAsync(decorator, cancellation: cancellation);
            DeprecatedSignature(decorator, signature);
            var result = await Signatures.ReturnAsync(signature, cancellation);
            if ((result.Flags & TypeFlags.Any) != 0)
                continue;
            if (await DecoratorSignatureAsync(decorator, cancellation) is { ResolvedReturnType: { } target })
                await RelationDiagnostics.CheckAsync(result, target, RelationKind.Assignable, decorator.Expression!, null,
                    node is ParameterDeclarationNode || LegacyDecorators && node is PropertyDeclarationNode ? 1271 : 1270, cancellation);
        }
    }

    private void DecoratorExpressionGrammar(DecoratorNode decorator)
    {
        if (SemanticSyntax.Source(decorator)?.ParseDiagnostics.Count != 0 || decorator.Expression is ParenthesizedExpressionNode)
            return;
        var node = decorator.Expression!;
        bool callAllowed = true;
        SyntaxNode? invalid = null;
        while (true)
        {
            if (node is ExpressionWithTypeArgumentsNode instantiated)
            {
                node = instantiated.Expression!;
                continue;
            }
            if (node is NonNullExpressionNode nonNull)
            {
                node = nonNull.Expression!;
                continue;
            }
            if (node is CallExpressionNode call)
            {
                if (!callAllowed)
                    invalid = node;
                if (call.QuestionDotToken is not null)
                    invalid = call.QuestionDotToken;
                node = call.Expression!;
                callAllowed = false;
                continue;
            }
            if (node is PropertyAccessExpressionNode property)
            {
                if (property.QuestionDotToken is not null)
                    invalid = property.QuestionDotToken;
                node = property.Expression!;
                callAllowed = false;
                continue;
            }
            if (node is not IdentifierNode)
                invalid = node;
            break;
        }
        if (invalid is not null)
            Error(decorator.Expression!, CheckerDiagnostic.Create(decorator.Expression!,
                Messages.Expression_must_be_enclosed_in_parentheses_to_be_used_as_a_decorator) with
            { RelatedInformation = [CheckerDiagnostic.Create(invalid, Messages.Invalid_syntax_in_decorator)] });
    }

    private async ValueTask<int> DecoratorArgumentCountAsync(DecoratorNode decorator, Signature signature, CancellationToken cancellation)
    {
        int count = await Parameters.CountAsync(signature, cancellation);
        if (!LegacyDecorators)
            return Math.Min(Math.Max(count, 1), 2);
        return decorator.Parent switch
        {
            ClassDeclarationNode or ClassExpressionNode => 1,
            PropertyDeclarationNode property => SemanticSyntax.HasModifier(property, SyntaxKind.AccessorKeyword) ? 3 : 2,
            ParameterDeclarationNode => 3,
            _ => count <= 2 ? 2 : 3
        };
    }

    private static int DecoratorHead(DecoratorNode decorator) => decorator.Parent switch
    {
        ClassDeclarationNode or ClassExpressionNode => 1238,
        ParameterDeclarationNode => 1239,
        PropertyDeclarationNode => 1240,
        _ => 1241
    };

    private async ValueTask<Signature> ResolveDecoratorAsync(
        DecoratorNode decorator,
        List<Signature>? candidates,
        CheckMode mode,
        CancellationToken cancellation)
    {
        var type = await Expressions.CheckAsync(decorator.Expression!, cancellation: cancellation);
        var apparent = await Views.ApparentAsync(type, cancellation);
        if (apparent == context.ErrorType)
            return await CallResolution.UntypedAsync(decorator, true, cancellation);
        var calls = await SignaturesAsync(apparent, false, cancellation);
        var constructors = await SignaturesAsync(apparent, true, cancellation);
        if ((type.Flags & TypeFlags.Any) != 0 || (apparent.Flags & TypeFlags.Any) != 0 && type is TypeParameter
            || calls.Count == 0 && constructors.Count == 0 && apparent is not UnionType
                && ((await Views.ReducedAsync(
                    apparent,
                    cancellation)).Flags & TypeFlags.Never) == 0 && await AssignableAsync(type, GlobalFunction, cancellation))
            return await CallResolution.UntypedAsync(decorator, false, cancellation);
        bool uncalled = calls.Count != 0;
        foreach (var signature in calls)
            if (signature.MinArgumentCount != 0 || signature.HasRestParameter
                || signature.Parameters.Count >= await DecoratorArgumentCountAsync(decorator, signature, cancellation))
            {
                uncalled = false;
                break;
            }
        if (uncalled && decorator.Expression is not ParenthesizedExpressionNode)
        {
            Error(decorator, 1329, CheckerDiagnostic.DeclarationName(decorator.Expression!));
            return await CallResolution.UntypedAsync(decorator, true, cancellation);
        }
        if (calls.Count == 0)
        {
            await InvocationErrorWithHeadAsync(decorator.Expression!, apparent, false, DecoratorHead(decorator), cancellation);
            return await CallResolution.UntypedAsync(decorator, true, cancellation);
        }
        if (await DecoratorSignatureAsync(decorator, cancellation) is null)
            return await CallResolution.UntypedAsync(decorator, true, cancellation);
        return await CallResolution.OverloadAsync(decorator, calls, candidates, mode, cancellation: cancellation);
    }
}
