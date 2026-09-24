using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private bool DeclarationModifiers(SyntaxNode node)
    {
        if (node is not IModifiedNode { Modifiers: { Count: > 0 } modifiers }
            || SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0)
            return false;
        if (DecoratorGrammar(node))
            return true;
        var first = modifiers.FirstOrDefault(m => m is not DecoratorNode);
        if (first is null)
            return false;
        if (IllegalDeclarationModifier(node, first.Kind))
            return Report(first, 1184);
        if (node is ParameterDeclarationNode { Name: IdentifierNode { Text: "this" } })
            return Report(node, 1433);
        var seen = new HashSet<SyntaxKind>();
        bool moduleElement = node.Parent is SourceFileNode or ModuleBlockNode;
        foreach (var modifier in modifiers)
        {
            if (modifier is DecoratorNode)
                continue;
            var kind = modifier.Kind;
            bool parsed = (modifier.Flags & NodeFlags.Reparsed) == 0;
            if (kind != SyntaxKind.ReadonlyKeyword && node is PropertySignatureDeclarationNode or MethodSignatureDeclarationNode)
                return Report(modifier, 1070);
            if (kind != SyntaxKind.ReadonlyKeyword && node is IndexSignatureDeclarationNode
                && (kind != SyntaxKind.StaticKeyword || !SemanticSyntax.ClassLike(node.Parent)))
                return Report(modifier, 1071);
            bool access = kind is SyntaxKind.PublicKeyword or SyntaxKind.ProtectedKeyword or SyntaxKind.PrivateKeyword;
            if (access && seen.Any(k => k is SyntaxKind.PublicKeyword or SyntaxKind.ProtectedKeyword or SyntaxKind.PrivateKeyword))
                return Report(modifier, 1028);
            if (seen.Contains(kind) && kind is not SyntaxKind.ConstKeyword and not SyntaxKind.DefaultKeyword)
                return Report(modifier, 1030);
            switch (kind)
            {
                case SyntaxKind.PublicKeyword:
                case SyntaxKind.ProtectedKeyword:
                case SyntaxKind.PrivateKeyword:
                    if (parsed && seen.Any(k => k is SyntaxKind.OverrideKeyword or SyntaxKind.StaticKeyword or SyntaxKind.AccessorKeyword
                        or SyntaxKind.ReadonlyKeyword or SyntaxKind.AsyncKeyword))
                        return Report(modifier, 1029);
                    if (moduleElement)
                        return Report(modifier, 1044);
                    if (seen.Contains(SyntaxKind.AbstractKeyword))
                    {
                        if (kind == SyntaxKind.PrivateKeyword)
                            return Report(modifier, 1243);
                        if (parsed)
                            return Report(modifier, 1029);
                    }
                    if (SemanticSyntax.Name(node) is PrivateIdentifierNode)
                        return Report(modifier, 18010);
                    break;
                case SyntaxKind.StaticKeyword:
                    if (parsed && seen.Any(k => k is SyntaxKind.ReadonlyKeyword or SyntaxKind.AsyncKeyword or SyntaxKind.AccessorKeyword))
                        return Report(modifier, 1029);
                    if (moduleElement)
                        return Report(modifier, 1044);
                    if (node is ParameterDeclarationNode)
                        return Report(modifier, 1090);
                    if (seen.Contains(SyntaxKind.AbstractKeyword))
                        return Report(modifier, 1243);
                    if (parsed && seen.Contains(SyntaxKind.OverrideKeyword))
                        return Report(modifier, 1029);
                    break;
                case SyntaxKind.ConstKeyword:
                    if (node is not (EnumDeclarationNode or TypeParameterDeclarationNode))
                        return Report(node, 1248);
                    break;
                case SyntaxKind.AccessorKeyword:
                    if (seen.Contains(SyntaxKind.ReadonlyKeyword) || seen.Contains(SyntaxKind.DeclareKeyword))
                        return Report(modifier, 1243);
                    if (node is not PropertyDeclarationNode)
                        return Report(modifier, 1275);
                    break;
                case SyntaxKind.ReadonlyKeyword:
                    if (node is not (PropertyDeclarationNode or PropertySignatureDeclarationNode or IndexSignatureDeclarationNode
                        or ParameterDeclarationNode))
                        return Report(modifier, 1024);
                    if (seen.Contains(SyntaxKind.AccessorKeyword))
                        return Report(modifier, 1243);
                    break;
                case SyntaxKind.ExportKeyword:
                    if ((node.Flags & NodeFlags.Ambient) == 0 && node.Parent is SourceFileNode
                        && node is not (TypeAliasDeclarationNode or InterfaceDeclarationNode or ModuleDeclarationNode)
                        && program.Symbols.Program.Configuration.Options.Boolean("verbatimModuleSyntax") == true && EmitModuleKind(node) == 1)
                        return Report(modifier, 1287);
                    if (parsed && seen.Any(k => k is SyntaxKind.DeclareKeyword or SyntaxKind.AbstractKeyword or SyntaxKind.AsyncKeyword))
                        return Report(modifier, 1029);
                    if (SemanticSyntax.ClassLike(node.Parent))
                        return Report(modifier, 1031);
                    if (node is ParameterDeclarationNode)
                        return Report(modifier, 1090);
                    break;
                case SyntaxKind.DefaultKeyword:
                    var container = node.Parent is SourceFileNode ? node.Parent : node.Parent?.Parent;
                    if (container is ModuleDeclarationNode && !AmbientModule(container))
                        return Report(modifier, 1319);
                    if (parsed && !seen.Contains(SyntaxKind.ExportKeyword))
                        return Report(modifier, 1029);
                    break;
                case SyntaxKind.DeclareKeyword:
                    if (seen.Contains(SyntaxKind.AsyncKeyword) || seen.Contains(SyntaxKind.OverrideKeyword))
                        return Report(modifier, 1040);
                    if (SemanticSyntax.ClassLike(node.Parent) && node is not PropertyDeclarationNode)
                        return Report(modifier, 1031);
                    if (node is ParameterDeclarationNode)
                        return Report(modifier, 1090);
                    if (node.Parent is ModuleBlockNode && (node.Parent.Flags & NodeFlags.Ambient) != 0)
                        return Report(modifier, 1038);
                    if (SemanticSyntax.Name(node) is PrivateIdentifierNode)
                        return Report(modifier, 18019);
                    if (seen.Contains(SyntaxKind.AccessorKeyword))
                        return Report(modifier, 1243);
                    break;
                case SyntaxKind.AbstractKeyword:
                    if (node is not (ClassDeclarationNode or ConstructorTypeNode))
                    {
                        if (node is not (MethodDeclarationNode or PropertyDeclarationNode or GetAccessorDeclarationNode
                            or SetAccessorDeclarationNode))
                            return Report(modifier, 1242);
                        if (node.Parent is not ClassDeclarationNode || !SemanticSyntax.HasModifier(node.Parent, SyntaxKind.AbstractKeyword))
                            return Report(modifier, node is PropertyDeclarationNode ? 1253 : 1244);
                        if (seen.Contains(SyntaxKind.StaticKeyword) || seen.Contains(SyntaxKind.PrivateKeyword))
                            return Report(modifier, 1243);
                        if (seen.Contains(SyntaxKind.AsyncKeyword))
                            return Report(modifiers.First(m => m.Kind == SyntaxKind.AsyncKeyword), 1243);
                        if (parsed && (seen.Contains(SyntaxKind.OverrideKeyword) || seen.Contains(SyntaxKind.AccessorKeyword)))
                            return Report(modifier, 1029);
                    }
                    if (SemanticSyntax.Name(node) is PrivateIdentifierNode)
                        return Report(modifier, 18019);
                    break;
                case SyntaxKind.AsyncKeyword:
                    if (seen.Contains(SyntaxKind.DeclareKeyword) || (node.Parent?.Flags & NodeFlags.Ambient) != 0)
                        return Report(modifier, 1040);
                    if (node is ParameterDeclarationNode)
                        return Report(modifier, 1090);
                    if (seen.Contains(SyntaxKind.AbstractKeyword))
                        return Report(modifier, 1243);
                    break;
                case SyntaxKind.OverrideKeyword:
                    if (seen.Contains(SyntaxKind.DeclareKeyword))
                        return Report(modifier, 1243);
                    if (parsed && seen.Any(k => k is SyntaxKind.ReadonlyKeyword or SyntaxKind.AccessorKeyword or SyntaxKind.AsyncKeyword))
                        return Report(modifier, 1029);
                    break;
                case SyntaxKind.InKeyword:
                case SyntaxKind.OutKeyword:
                    return Report(modifier, 1274);
                default:
                    return Report(modifier, 1042);
            }
            seen.Add(kind);
        }
        if (node is ConstructorDeclarationNode)
        {
            foreach (var kind in new[] { SyntaxKind.StaticKeyword, SyntaxKind.OverrideKeyword, SyntaxKind.AsyncKeyword })
                if (seen.Contains(kind))
                    return Report(modifiers.First(m => m.Kind == kind), 1089);
            return false;
        }
        if (node is ImportDeclarationNode or ImportEqualsDeclarationNode && seen.Contains(SyntaxKind.DeclareKeyword))
            return Report(modifiers.First(m => m.Kind == SyntaxKind.DeclareKeyword), 1079);
        if (seen.Contains(SyntaxKind.AsyncKeyword)
            && node is not (MethodDeclarationNode or FunctionDeclarationNode or FunctionExpressionNode or ArrowFunctionNode))
            return Report(modifiers.First(m => m.Kind == SyntaxKind.AsyncKeyword), 1042);
        if (node is MethodDeclarationNode { Parent: ObjectLiteralExpressionNode }
            && (modifiers.Count != 1 || first.Kind != SyntaxKind.AsyncKeyword))
            return Report(node, 1184);
        return false;

        bool Report(SyntaxNode location, int code)
        {
            Error(location, code);
            return true;
        }
    }

    private static bool IllegalDeclarationModifier(SyntaxNode node, SyntaxKind first) => node switch
    {
        PropertyAssignmentNode or ShorthandPropertyAssignmentNode or ClassStaticBlockDeclarationNode => true,
        MethodDeclarationNode or MethodSignatureDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode
            or ConstructorDeclarationNode
            or PropertyDeclarationNode or PropertySignatureDeclarationNode or IndexSignatureDeclarationNode or ModuleDeclarationNode
            or ImportDeclarationNode or ImportEqualsDeclarationNode or ExportDeclarationNode or ExportAssignmentNode
            or FunctionExpressionNode or ArrowFunctionNode or ParameterDeclarationNode or TypeParameterDeclarationNode => false,
        _ when node.Parent is SourceFileNode or ModuleBlockNode => false,
        FunctionDeclarationNode => first != SyntaxKind.AsyncKeyword,
        ClassDeclarationNode or ConstructorTypeNode => first != SyntaxKind.AbstractKeyword,
        EnumDeclarationNode => first != SyntaxKind.ConstKeyword,
        VariableStatementNode { DeclarationList: { } list } when (list.Flags & NodeFlags.Using) != 0 => first != SyntaxKind.AwaitKeyword,
        _ => true
    };
}
