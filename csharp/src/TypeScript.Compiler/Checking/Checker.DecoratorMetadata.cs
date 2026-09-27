using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private async ValueTask DecoratorMetadataAsync(SyntaxNode node, DecoratorNode decorator, CancellationToken cancellation)
    {
        await ExternalHelpersAsync(decorator, ["__metadata"], cancellation);
        var annotations = new List<SyntaxNode?>();
        switch (node)
        {
            case ClassDeclarationNode:
                if (PropertyInitialization.Members(node).OfType<ConstructorDeclarationNode>().FirstOrDefault(c => c.Body is not null) is { } constructor)
                    AddParameters(constructor);
                break;
            case GetAccessorDeclarationNode or SetAccessorDeclarationNode:
                var annotation = AccessorAnnotation(node);
                if (annotation is null)
                    annotation = program.Symbols.Declaration(node)!.Declarations.Where(d => d.Kind != node.Kind).Select(AccessorAnnotation).FirstOrDefault(t => t is not null);
                annotations.Add(annotation);
                break;
            case MethodDeclarationNode method:
                AddParameters(method);
                annotations.Add(method.Type);
                break;
            case PropertyDeclarationNode property:
                annotations.Add(property.Type);
                break;
            case ParameterDeclarationNode parameter:
                annotations.Add(ParameterAnnotation(parameter));
                AddParameters(parameter.Parent!);
                annotations.Add((parameter.Parent as ITypedNode)?.Type);
                break;
        }
        foreach (var annotation in annotations)
        {
            if (await MetadataEntityAsync(annotation, cancellation) is not { } entity)
                continue;
            var root = entity;
            while (root is QualifiedNameNode qualified)
                root = qualified.Left!;
            var meaning = (entity is IdentifierNode ? SymbolFlags.Type : SymbolFlags.Namespace) | SymbolFlags.Alias;
            var symbol = await program.EntityNames.ResolveAsync(root, meaning, true, true, cancellation: cancellation);
            if (symbol is null || (symbol.Flags & SymbolFlags.Alias) == 0)
                continue;
            bool value = (await program.Aliases.FlagsAsync(symbol, cancellation: cancellation) & SymbolFlags.Value) != 0;
            if (value && (await program.Aliases.ResolveAsync(symbol, cancellation)).Flags is var flags
                && (flags & (SymbolFlags.ConstEnum | SymbolFlags.ConstEnumOnlyModule)) == 0
                && await program.Aliases.TypeOnlyAsync(symbol, cancellation: cancellation) is null)
                await AliasReferences.MarkAsync(symbol, entity, cancellation);
            else if (IsolatedModules && ModuleKind >= 5 && !value && !symbol.Declarations.Any(AliasResolver.IsTypeOnly))
            {
                var diagnostic = CheckerDiagnostic.Create(entity,
                    Messages.A_type_referenced_in_a_decorated_signature_must_be_imported_with_import_type_or_a_namespace_import_when_isolatedModules_and_emitDecoratorMetadata_are_enabled);
                if (AliasResolver.Declaration(symbol) is { } declaration)
                    diagnostic = diagnostic with
                    { RelatedInformation = [CheckerDiagnostic.Create(declaration, Messages.X_0_was_imported_here, symbol.Name)] };
                Error(entity, diagnostic);
            }
        }
        void AddParameters(SyntaxNode function)
        {
            foreach (ParameterDeclarationNode parameter in ((IFunctionSignature)function).Parameters!)
                annotations.Add(ParameterAnnotation(parameter));
        }
    }

    private static SyntaxNode? AccessorAnnotation(SyntaxNode node) => node switch
    {
        GetAccessorDeclarationNode getter => getter.Type,
        SetAccessorDeclarationNode setter => setter.Parameters!.OfType<ParameterDeclarationNode>().FirstOrDefault(p => p.Name is not IdentifierNode { Text.Span: "this" })?.Type,
        _ => null
    };

    private static SyntaxNode? ParameterAnnotation(ParameterDeclarationNode node) => node.DotDotDotToken is null ? node.Type : node.Type switch
    {
        ArrayTypeNode array => array.ElementType,
        TypeReferenceNode { TypeArguments: { Count: > 0 } arguments } => arguments[0],
        _ => null
    };

    private async ValueTask<SyntaxNode?> MetadataEntityAsync(SyntaxNode? node, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        IReadOnlyList<SyntaxNode>? types;
        switch (node)
        {
            case TypeReferenceNode reference:
                return reference.TypeName;
            case ParenthesizedTypeNode parentheses:
                return await MetadataEntityAsync(parentheses.Type, cancellation);
            case NamedTupleMemberNode member:
                return await MetadataEntityAsync(member.Type, cancellation);
            case UnionTypeNode union:
                types = union.Types;
                break;
            case IntersectionTypeNode intersection:
                types = intersection.Types;
                break;
            case ConditionalTypeNode conditional:
                types = [conditional.TrueType!, conditional.FalseType!];
                break;
            default:
                return null;
        }
        SyntaxNode? common = null;
        foreach (var type in types!)
        {
            if (type.Kind == SyntaxKind.NeverKeyword || !context.StrictNullChecks
                && (type.Kind == SyntaxKind.UndefinedKeyword || type is LiteralTypeNode { Literal.Kind: SyntaxKind.NullKeyword }))
                continue;
            var entity = await MetadataEntityAsync(type, cancellation);
            if (entity is null)
                return null;
            if (common is null)
                common = entity;
            else if (common is not IdentifierNode a || entity is not IdentifierNode b || a.Text != b.Text)
                return null;
        }
        return common;
    }
}
