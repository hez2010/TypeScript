using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.LanguageServices;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal async ValueTask<IReadOnlyList<SyntaxNode>> GetDefinitionDeclarationsAsync(SyntaxNode node, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(node, cancellation);
        if (node is IdentifierNode && node.Parent is ShorthandPropertyAssignmentNode shorthand)
        {
            var value = await program.EntityNames.ResolveAsync(shorthand.Name, SymbolFlags.Value | SymbolFlags.Alias, true, cancellation: cancellation);
            return [.. value?.Declarations ?? [], .. await ContextualDefinitionDeclarationsAsync(node, cancellation)];
        }
        if (node.Parent is BindingElementNode { Parent: BindingPatternNode { Kind: K.ObjectBindingPattern } pattern, DotDotDotToken: null } binding
            && node == (binding.PropertyName ?? binding.Name) && DefinitionName(node) is { } name)
        {
            var type = await TypeAtLocationAsync(QuerySyntax.Reparsed(pattern), cancellation);
            List<SyntaxNode> declarations = [];
            foreach (var part in type is UnionType union ? union.Types : [type])
                if (await Properties.PropertyAsync(part, name, cancellation: cancellation) is { } property) declarations.AddRange(property.Declarations);
            return declarations;
        }
        node = LanguageServiceDocument.DeclarationNameForKeyword(node);
        if (await SymbolAtLocationAsync(QuerySyntax.Reparsed(node), cancellation) is { } symbol)
        {
            if ((symbol.Flags & SymbolFlags.Class) != 0 && (symbol.Flags & (SymbolFlags.Function | SymbolFlags.Variable)) == 0
                && node.Kind == K.ConstructorKeyword && symbol.Members.GetValueOrDefault(Symbol.InternalConstructor) is { } constructor) symbol = constructor;
            if ((symbol.Flags & SymbolFlags.Alias) != 0 && await ResolveSymbolAsync(symbol, cancellation) is { } target && target != UnknownSymbol) symbol = target;
            var contextual = await ContextualDefinitionDeclarationsAsync(node, cancellation);
            if (contextual.Count != 0) return contextual;
            if (symbol.Declarations.Count != 0) return symbol.Declarations;
        }
        List<SyntaxNode> indexes = [];
        if (node is IdentifierNode && node.Parent is PropertyAccessExpressionNode access && access.Name == node)
        {
            var key = await LiteralNameTypeAsync(node, cancellation);
            var type = await TypeAtLocationAsync(QuerySyntax.Reparsed(access.Expression!), cancellation);
            foreach (var part in DefinitionTypeParts(type))
                foreach (var info in await IndexesAsync(part, cancellation))
                    if (info.Declaration is { } declaration && !indexes.Contains(declaration)
                        && await IndexSignatures.ApplicableTypeAsync(key, info.KeyType, cancellation)) indexes.Add(declaration);
        }
        return indexes;
    }

    private async ValueTask<IReadOnlyList<SyntaxNode>> ContextualDefinitionDeclarationsAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var name = node.Parent is ComputedPropertyNameNode && node is StringLiteralNode or NoSubstitutionTemplateLiteralNode or NumericLiteralNode ? node.Parent : node;
        if (name.Parent is not { } element || element.DeclarationName != name || element.Parent is not (ObjectLiteralExpressionNode or JsxAttributesNode)
            || await Contexts.GetAsync(element.Parent, cancellation: cancellation) is not { } type) return [];
        var properties = await GetPropertySymbolsFromContextualTypeAsync(element, type, false, cancellation);
        if (properties.Any(property => property.ValueDeclaration is { Parent: ObjectLiteralExpressionNode } declaration && declaration.DeclarationName == node)
            && await WithoutSourceInferenceAsync(element.Parent, () => Contexts.GetAsync(element.Parent, ContextFlags.IgnoreNodeInferences, cancellation), cancellation) is { } withoutInference
            && await GetPropertySymbolsFromContextualTypeAsync(element, withoutInference, false, cancellation) is { Count: > 0 } alternatives) properties = alternatives;
        return properties.SelectMany(property => property.Declarations).ToArray();
    }

    internal async ValueTask<IReadOnlyList<SyntaxNode>> GetTypeDefinitionDeclarationsAsync(SyntaxNode node, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(node, cancellation);
        node = LanguageServiceDocument.DeclarationNameForKeyword(node);
        if (await SymbolAtLocationAsync(QuerySyntax.Reparsed(node), cancellation) is not { } symbol) return [];
        var type = await TypeOfSymbolAtLocationAsync(symbol, node, cancellation);
        if ((type.Symbol == symbol || type.Symbol is not null && symbol.ValueDeclaration is VariableDeclarationNode variable
                && variable.Initializer == type.Symbol.ValueDeclaration) && await SignaturesAsync(type, false, cancellation) is [var signature])
            type = await Signatures.ReturnAsync(signature, cancellation);
        List<SyntaxNode> declarations = [];
        if (await FirstKnownTypeArgumentAsync(type, cancellation) is { } argument) declarations.AddRange(TypeDeclarations(argument));
        declarations.AddRange(TypeDeclarations(type));
        return declarations.Count != 0 ? declarations : (symbol.Flags & SymbolFlags.Value) == 0 && (symbol.Flags & SymbolFlags.Type) != 0 ? symbol.Declarations : [];
    }

    private static IEnumerable<Type> DefinitionTypeParts(Type type) => type switch
    { UnionType union => union.Types, IntersectionType intersection => intersection.Types, _ => [type] };
    private static IEnumerable<SyntaxNode> TypeDeclarations(Type type) => DefinitionTypeParts(type).SelectMany(part => part.Symbol?.Declarations ?? []).Distinct();
    private async ValueTask<Type?> FirstKnownTypeArgumentAsync(Type type, CancellationToken cancellation)
    {
        static bool Known(Utf8String name) => name.ToString() is "Array" or "ArrayLike" or "ReadonlyArray" or "Promise" or "PromiseLike" or "Iterable"
            or "IterableIterator" or "AsyncIterable" or "Set" or "WeakSet" or "ReadonlySet" or "Map" or "WeakMap" or "ReadonlyMap"
            or "Partial" or "Required" or "Readonly" or "Pick" or "Omit" or "NonNullable";
        Symbol? Global(Utf8String name) => program.Symbols.Lookup(program.Symbols.Globals, name, SymbolFlags.Type);
        if (type is TypeReference reference && type.Symbol is { } symbol && Known(symbol.Name) && Global(symbol.Name) is { } global && global == reference.Target?.Symbol)
            return (await TypeArgumentsAsync(reference, cancellation)).FirstOrDefault();
        if (type.Alias is { } alias && Known(alias.Symbol.Name) && Global(alias.Symbol.Name) == alias.Symbol) return alias.TypeArguments.FirstOrDefault();
        return null;
    }

    internal async ValueTask<Symbol?> GetOverriddenMemberAsync(SyntaxNode node, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(node, cancellation);
        var member = node.Parent;
        while (member is not null && member.Parent is not (ClassDeclarationNode or ClassExpressionNode)) member = member.Parent;
        if (member?.DeclarationName is not { } name) return null;
        var heritage = member.Parent switch { ClassDeclarationNode c => c.HeritageClauses, ClassExpressionNode c => c.HeritageClauses, _ => null };
        if (heritage?.OfType<HeritageClauseNode>().FirstOrDefault(clause => clause.Token == K.ExtendsKeyword)?.Types?.FirstOrDefault() is not ExpressionWithTypeArgumentsNode basis) return null;
        var expression = basis.Expression!;
        while (expression is ParenthesizedExpressionNode parentheses) expression = parentheses.Expression!;
        var symbol = expression is ClassExpressionNode ? expression.BindingSymbol : await SymbolAtLocationAsync(QuerySyntax.Reparsed(expression), cancellation);
        if (symbol is null || DefinitionName(name) is not { } text) return null;
        var type = SemanticSyntax.IsStatic(member) ? await Values.GetAsync(symbol, cancellation) : await Declared.GetAsync(symbol, cancellation);
        return await Properties.PropertyAsync(type, text, cancellation: cancellation);
    }
    private static Utf8String? DefinitionName(SyntaxNode node) => node switch
    {
        IdentifierNode n => n.Text, PrivateIdentifierNode n => n.Text, StringLiteralNode n => n.Text,
        NumericLiteralNode n => n.Text, NoSubstitutionTemplateLiteralNode n => n.Text, _ => null,
    };
}
