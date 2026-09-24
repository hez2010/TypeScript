using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private static string ImportAttributeName(SyntaxNode node) => node switch
    {
        IdentifierNode identifier => identifier.Text,
        StringLiteralNode text => text.Text,
        NoSubstitutionTemplateLiteralNode template => template.Text,
        _ => ""
    };

    private readonly Dictionary<Symbol, Type> moduleImportAttributes = [];
    private Type? globalImportAttributes;

    internal async ValueTask<Type> ModuleImportAttributesAsync(Symbol symbol, CancellationToken cancellation)
    {
        if (moduleImportAttributes.TryGetValue(symbol, out var cached))
            return cached;
        var node = symbol.Declarations.OfType<ModuleDeclarationNode>().FirstOrDefault(d => d.Name is StringLiteralNode);
        var type = node?.Attributes is { } attributes ? await Nodes.FromNodeAsync(attributes, cancellation) : context.EmptyObjectType;
        cancellation.ThrowIfCancellationRequested();
        return moduleImportAttributes[symbol] = type;
    }

    internal async ValueTask<Type> ImportAttributesExpressionAsync(ImportAttributesNode node, CancellationToken cancellation)
    {
        var data = links.TypeNodes.Get(node);
        if (data.ResolvedType is { } cached)
            return cached;
        var members = new Dictionary<string, Symbol>();
        foreach (ImportAttributeNode attribute in node.Attributes!)
        {
            var property = new Symbol(SymbolFlags.Property | SymbolFlags.Transient, ImportAttributeName(attribute.Name!));
            links.Values.Get(property).ResolvedType = await Algebra.RegularTypeAsync(
                await CachedExpressionAsync(attribute.Value!, 0, cancellation),
                cancellation);
            members[property.Name] = property;
        }
        var symbol = new Symbol(SymbolFlags.ObjectLiteral | SymbolFlags.Transient, Symbol.InternalPrefix + "importAttributes");
        var type = context.NewObjectType(
            ObjectFlags.Anonymous | ObjectFlags.MembersResolved | ObjectFlags.ObjectLiteral | ObjectFlags.NonInferrableType,
            symbol);
        type.Members = members.AsReadOnly();
        type.Properties = members.Values.ToArray();
        type.CallSignatures = type.ConstructSignatures = [];
        type.IndexInfos = [];
        cancellation.ThrowIfCancellationRequested();
        return data.ResolvedType = type;
    }

    private async ValueTask CheckImportAttributesAsync(SyntaxNode declaration, ImportAttributesNode? node, CancellationToken cancellation)
    {
        if (node is null)
            return;
        foreach (ImportAttributeNode attribute in node.Attributes!)
            if (attribute.Value is not StringLiteralNode)
                Error(attribute.Value!, 2858);
        globalImportAttributes ??= await program.Globals.GetAsync("ImportAttributes", 0, true, cancellation);
        if (globalImportAttributes != context.EmptyObjectType)
            await RelationDiagnostics.CheckAsync(await ImportAttributesExpressionAsync(node, cancellation),
                await Algebra.UnionAsync([globalImportAttributes, context.UndefinedType], cancellation: cancellation),
                RelationKind.Assignable,
                node, null, 2322, cancellation);
        bool typeOnly = declaration is ImportTypeNode
            || declaration is ImportDeclarationNode { ImportClause: { } clause } && SemanticSyntax.TypeOnly(clause)
            || declaration is ExportDeclarationNode { IsTypeOnly: true };
        var mode = node.Attributes.OfType<ImportAttributeNode>().FirstOrDefault(a => ImportAttributeName(a.Name!) == "resolution-mode");
        bool grammar = SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0;
        string? modeText = mode?.Value switch
        {
            StringLiteralNode value => value.Text,
            NoSubstitutionTemplateLiteralNode template => template.Text,
            _ => null
        };
        bool validMode = modeText is "import" or "require";
        if (typeOnly)
        {
            if (grammar && modeText is not null && !validMode)
                Error(mode!.Value!, 1453);
            return;
        }
        if (grammar && ModuleKind is not (99 or 101 or 102 or 199 or 200))
        {
            Error(node, 2823);
            return;
        }
        if (grammar && EmitModuleKind(declaration) == 1)
        {
            Error(node, 2856);
            return;
        }
        if (grammar && validMode)
            Error(node, 1454);
    }

    private async ValueTask CheckModuleAttributesAsync(ModuleDeclarationNode node, CancellationToken cancellation)
    {
        var attributes = node.Attributes!;
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
        {
            if (ModuleAugmentation(node))
                Error(attributes, 1551);
            else if (node.Name is StringLiteralNode name && !name.Text.Contains('*'))
                Error(attributes, 1550);
            foreach (var member in attributes.Members!)
            {
                if (member is not PropertySignatureDeclarationNode property)
                {
                    Error(member, 1552);
                    break;
                }
                if (property.Modifiers?.FirstOrDefault(m => m.Kind == SyntaxKind.ReadonlyKeyword) is { } readOnly)
                {
                    Error(readOnly, 1558);
                    break;
                }
                if (property.Type is null)
                {
                    Error(member, 1553);
                    break;
                }
                if (property.PostfixToken?.Kind == SyntaxKind.QuestionToken)
                {
                    Error(member, 1556);
                    break;
                }
                if (property.Name is not (StringLiteralNode or IdentifierNode or NoSubstitutionTemplateLiteralNode))
                {
                    Error(property.Name!, 1554);
                    break;
                }
                if (ImportAttributeName(property.Name!) == "resolution-mode")
                {
                    Error(property.Name!, 1557);
                    break;
                }
                if (property.Type is not LiteralTypeNode { Literal: StringLiteralNode or NoSubstitutionTemplateLiteralNode })
                {
                    Error(property.Type, 1555);
                    break;
                }
            }
        }
        await CheckedFunctionTypeAsync(attributes, cancellation);
        globalImportAttributes ??= await program.Globals.GetAsync("ImportAttributes", 0, true, cancellation);
        if (globalImportAttributes != context.EmptyObjectType)
            await RelationDiagnostics.CheckAsync(await Nodes.FromNodeAsync(attributes, cancellation), globalImportAttributes,
                RelationKind.Assignable, attributes, null, 2322, cancellation);
    }
}
