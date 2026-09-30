using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal async ValueTask<ConstantResult> GetEnumMemberValueForEmitAsync(EnumMemberNode node, CancellationToken cancellation = default)
    {
        if (!EmitParseNode(node))
            return default;
        using var query = await EnterQueryAsync(node, cancellation);
        return await EnumValues.GetAsync(node, cancellation);
    }

    internal async ValueTask<object?> GetConstantValueForEmitAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(node, cancellation);
        if (node is EnumMemberNode member)
            return (await EnumValues.GetAsync(member, cancellation)).Value;
        if (links.SymbolNodes.Get(node).ResolvedSymbol is null)
            await CachedExpressionAsync(node, 0, cancellation);
        var symbol = links.SymbolNodes.Get(node).ResolvedSymbol;
        if (symbol is null && ConstantEvaluator.EntityName(node))
            symbol = await program.EntityNames.ResolveAsync(node, SymbolFlags.Value, true, cancellation: cancellation);
        return symbol is { ValueDeclaration: EnumMemberNode declaration } && (symbol.Flags & SymbolFlags.EnumMember) != 0
            && SemanticSyntax.HasModifier(declaration.Parent!, K.ConstKeyword)
            ? (await EnumValues.GetAsync(declaration, cancellation)).Value : null;
    }

    internal async ValueTask<ModifierFlags> GetEffectiveDeclarationFlagsForEmitAsync(SyntaxNode node, ModifierFlags mask,
        CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(node, cancellation);
        var root = SemanticSyntax.RootDeclaration(node);
        var flags = SyntacticFlags(root);
        if (root is VariableDeclarationNode)
            root = root.Parent!;
        if (root is VariableDeclarationListNode)
        {
            flags |= SyntacticFlags(root);
            root = root.Parent!;
        }
        if (root is VariableStatementNode)
            flags |= SyntacticFlags(root);
        if (node.Parent is not (InterfaceDeclarationNode or ClassDeclarationNode or ClassExpressionNode)
            && (node.Flags & NodeFlags.Ambient) != 0)
        {
            var container = node.Parent;
            while (container is not null && !Binder.IsContainer(container))
                container = container.Parent;
            var containerFlags = container is null ? 0 : program.Symbols.Binding(container)?.Get(container)?.Flags ?? container.Flags;
            if ((containerFlags & NodeFlags.ExportContext) != 0 && (flags & ModifierFlags.Ambient) == 0
                && !(node.Parent is ModuleBlockNode { Parent: ModuleDeclarationNode { Keyword: K.GlobalKeyword } }))
                flags |= ModifierFlags.Export;
            flags |= ModifierFlags.Ambient;
        }
        return flags & mask;
    }

    private static ModifierFlags SyntacticFlags(SyntaxNode node)
    {
        ModifierFlags flags = 0;
        if (node is IModifiedNode { Modifiers: { } modifiers })
            foreach (var modifier in modifiers)
                flags |= ModifierFlag(modifier.Kind);
        return flags;
    }

    private static ModifierFlags ModifierFlag(SyntaxKind kind) => kind switch
    {
        K.PublicKeyword => ModifierFlags.Public,
        K.PrivateKeyword => ModifierFlags.Private,
        K.ProtectedKeyword => ModifierFlags.Protected,
        K.ReadonlyKeyword => ModifierFlags.Readonly,
        K.OverrideKeyword => ModifierFlags.Override,
        K.ExportKeyword => ModifierFlags.Export,
        K.AbstractKeyword => ModifierFlags.Abstract,
        K.DeclareKeyword => ModifierFlags.Ambient,
        K.StaticKeyword => ModifierFlags.Static,
        K.AccessorKeyword => ModifierFlags.Accessor,
        K.AsyncKeyword => ModifierFlags.Async,
        K.DefaultKeyword => ModifierFlags.Default,
        K.ConstKeyword => ModifierFlags.Const,
        K.InKeyword => ModifierFlags.In,
        K.OutKeyword => ModifierFlags.Out,
        K.Decorator => ModifierFlags.Decorator,
        _ => 0
    };

    internal async ValueTask<bool> IsThisPropertyAssignmentRedundantForEmitAsync(SyntaxNode? node, CancellationToken cancellation = default)
    {
        if (node is null)
            return false;
        using var query = await EnterQueryAsync(node, cancellation);
        if (program.Symbols.Declaration(node) is not { Parent: { } parent } symbol
            || await Declared.GetAsync(parent, cancellation) is not InterfaceType parentType)
            return false;
        foreach (var baseType in await Bases.GetAsync(parentType, cancellation))
        {
            var property = await Properties.PropertyAsync(baseType, symbol.Name, cancellation: cancellation);
            if (property is null)
                continue;
            if ((property.Flags & (SymbolFlags.Accessor | SymbolFlags.Method | SymbolFlags.Function)) != 0)
                return true;
            if (IsReadonly(property) == IsReadonly(symbol)
                && (property.Flags & SymbolFlags.Optional) == (symbol.Flags & SymbolFlags.Optional)
                && await Relations.RelatedAsync(await Values.GetAsync(symbol, cancellation), await Values.GetAsync(property, cancellation),
                    RelationKind.Identity, cancellation))
                return true;
        }
        return false;
    }
}
