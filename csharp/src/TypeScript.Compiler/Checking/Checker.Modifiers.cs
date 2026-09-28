using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
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
        SyntaxNode? first = null;
        for (int i = 0; i < modifiers.Count; i++)
            if (modifiers[i] is not DecoratorNode)
            {
                first = modifiers[i];
                break;
            }
        if (first is null)
            return false;
        if (IllegalDeclarationModifier(node, first.Kind))
            return Report(first, DiagnosticCode.ModifiersCannotAppearHere);
        if (node is ParameterDeclarationNode { Name: IdentifierNode { Text.Span: "this" } })
            return Report(node, DiagnosticCode.NeitherDecoratorsNorModifiersMayBeAppliedToThisParameters);
        UInt128 seen = 0;
        bool moduleElement = node.Parent is SourceFileNode or ModuleBlockNode;
        for (int i = 0; i < modifiers.Count; i++)
        {
            var modifier = modifiers[i];
            if (modifier is DecoratorNode)
                continue;
            var kind = modifier.Kind;
            bool parsed = (modifier.Flags & NodeFlags.Reparsed) == 0;
            if (kind != SyntaxKind.ReadonlyKeyword && node is PropertySignatureDeclarationNode or MethodSignatureDeclarationNode)
                return Report(modifier, DiagnosticCode.X0ModifierCannotAppearOnATypeMember);
            if (kind != SyntaxKind.ReadonlyKeyword && node is IndexSignatureDeclarationNode
                && (kind != SyntaxKind.StaticKeyword || !SemanticSyntax.ClassLike(node.Parent)))
                return Report(modifier, DiagnosticCode.X0ModifierCannotAppearOnAnIndexSignature);
            bool access = kind is SyntaxKind.PublicKeyword or SyntaxKind.ProtectedKeyword or SyntaxKind.PrivateKeyword;
            if (access && (Has(SyntaxKind.PublicKeyword) || Has(SyntaxKind.ProtectedKeyword)
                || Has(SyntaxKind.PrivateKeyword)))
                return Report(modifier, DiagnosticCode.AccessibilityModifierAlreadySeen);
            if (Has(kind) && kind is not SyntaxKind.ConstKeyword and not SyntaxKind.DefaultKeyword)
                return Report(modifier, DiagnosticCode.X0ModifierAlreadySeen);
            switch (kind)
            {
                case SyntaxKind.PublicKeyword:
                case SyntaxKind.ProtectedKeyword:
                case SyntaxKind.PrivateKeyword:
                    if (parsed && (Has(SyntaxKind.OverrideKeyword) || Has(SyntaxKind.StaticKeyword)
                        || Has(SyntaxKind.AccessorKeyword) || Has(SyntaxKind.ReadonlyKeyword)
                        || Has(SyntaxKind.AsyncKeyword)))
                        return Report(
                            modifier,
                            DiagnosticCode.X0ModifierMustPrecede1Modifier,
                            TokenFacts.Text(kind),
                            Seen(
                            SyntaxKind.OverrideKeyword,
                            SyntaxKind.StaticKeyword,
                            SyntaxKind.AccessorKeyword, SyntaxKind.ReadonlyKeyword, SyntaxKind.AsyncKeyword));
                    if (moduleElement)
                        return Report(modifier, DiagnosticCode.X0ModifierCannotAppearOnAModuleOrNamespaceElement);
                    if (Has(SyntaxKind.AbstractKeyword))
                    {
                        if (kind == SyntaxKind.PrivateKeyword)
                            return Report(modifier, DiagnosticCode.X0ModifierCannotBeUsedWith1Modifier, "private", "abstract");
                        if (parsed)
                            return Report(modifier, DiagnosticCode.X0ModifierMustPrecede1Modifier, TokenFacts.Text(kind), "abstract");
                    }
                    if (SemanticSyntax.Name(node) is PrivateIdentifierNode)
                        return Report(modifier, DiagnosticCode.AnAccessibilityModifierCannotBeUsedWithAPrivateIdentifier);
                    break;
                case SyntaxKind.StaticKeyword:
                    if (parsed && (Has(SyntaxKind.ReadonlyKeyword) || Has(SyntaxKind.AsyncKeyword)
                        || Has(SyntaxKind.AccessorKeyword)))
                        return Report(
                            modifier,
                            DiagnosticCode.X0ModifierMustPrecede1Modifier,
                            "static",
                            Seen(SyntaxKind.ReadonlyKeyword, SyntaxKind.AsyncKeyword, SyntaxKind.AccessorKeyword));
                    if (moduleElement)
                        return Report(modifier, DiagnosticCode.X0ModifierCannotAppearOnAModuleOrNamespaceElement);
                    if (node is ParameterDeclarationNode)
                        return Report(modifier, DiagnosticCode.X0ModifierCannotAppearOnAParameter);
                    if (Has(SyntaxKind.AbstractKeyword))
                        return Report(modifier, DiagnosticCode.X0ModifierCannotBeUsedWith1Modifier, "static", "abstract");
                    if (parsed && Has(SyntaxKind.OverrideKeyword))
                        return Report(modifier, DiagnosticCode.X0ModifierMustPrecede1Modifier, "static", "override");
                    break;
                case SyntaxKind.ConstKeyword:
                    if (node is not (EnumDeclarationNode or TypeParameterDeclarationNode))
                        return Report(node, DiagnosticCode.AClassMemberCannotHaveThe0Keyword, "const");
                    break;
                case SyntaxKind.AccessorKeyword:
                    if (Has(SyntaxKind.ReadonlyKeyword) || Has(SyntaxKind.DeclareKeyword))
                        return Report(
                            modifier,
                            DiagnosticCode.X0ModifierCannotBeUsedWith1Modifier,
                            "accessor",
                            Seen(SyntaxKind.ReadonlyKeyword, SyntaxKind.DeclareKeyword));
                    if (node is not PropertyDeclarationNode)
                        return Report(modifier, DiagnosticCode.XAccessorModifierCanOnlyAppearOnAPropertyDeclaration);
                    break;
                case SyntaxKind.ReadonlyKeyword:
                    if (node is not (PropertyDeclarationNode or PropertySignatureDeclarationNode or IndexSignatureDeclarationNode
                        or ParameterDeclarationNode))
                        return Report(modifier, DiagnosticCode.XReadonlyModifierCanOnlyAppearOnAPropertyDeclarationOrIndexSignature);
                    if (Has(SyntaxKind.AccessorKeyword))
                        return Report(modifier, DiagnosticCode.X0ModifierCannotBeUsedWith1Modifier, "readonly", "accessor");
                    break;
                case SyntaxKind.ExportKeyword:
                    if ((node.Flags & NodeFlags.Ambient) == 0 && node.Parent is SourceFileNode
                        && node is not (TypeAliasDeclarationNode or InterfaceDeclarationNode or ModuleDeclarationNode)
                        && program.Symbols.Program.Configuration.Options.VerbatimModuleSyntax == true && EmitModuleKind(node) == 1)
                        return Report(
                            modifier,
                            DiagnosticCode.ATopLevelExportModifierCannotBeUsedOnValueDeclarationsInACommonJSModuleWhenVerbatimModuleSyntaxIsEnabled);
                    if (parsed && (Has(SyntaxKind.DeclareKeyword) || Has(SyntaxKind.AbstractKeyword)
                        || Has(SyntaxKind.AsyncKeyword)))
                        return Report(
                            modifier,
                            DiagnosticCode.X0ModifierMustPrecede1Modifier,
                            "export",
                            Seen(SyntaxKind.DeclareKeyword, SyntaxKind.AbstractKeyword, SyntaxKind.AsyncKeyword));
                    if (SemanticSyntax.ClassLike(node.Parent))
                        return Report(modifier, DiagnosticCode.X0ModifierCannotAppearOnClassElementsOfThisKind);
                    if (node is ParameterDeclarationNode)
                        return Report(modifier, DiagnosticCode.X0ModifierCannotAppearOnAParameter);
                    break;
                case SyntaxKind.DefaultKeyword:
                    var container = node.Parent is SourceFileNode ? node.Parent : node.Parent?.Parent;
                    if (container is ModuleDeclarationNode && !AmbientModule(container))
                        return Report(modifier, DiagnosticCode.ADefaultExportCanOnlyBeUsedInAnECMAScriptStyleModule);
                    if (parsed && !Has(SyntaxKind.ExportKeyword))
                        return Report(modifier, DiagnosticCode.X0ModifierMustPrecede1Modifier, "export", "default");
                    break;
                case SyntaxKind.DeclareKeyword:
                    if (Has(SyntaxKind.AsyncKeyword) || Has(SyntaxKind.OverrideKeyword))
                        return Report(
                            modifier,
                            DiagnosticCode.X0ModifierCannotBeUsedInAnAmbientContext,
                            Seen(SyntaxKind.AsyncKeyword, SyntaxKind.OverrideKeyword));
                    if (SemanticSyntax.ClassLike(node.Parent) && node is not PropertyDeclarationNode)
                        return Report(modifier, DiagnosticCode.X0ModifierCannotAppearOnClassElementsOfThisKind);
                    if (node is ParameterDeclarationNode)
                        return Report(modifier, DiagnosticCode.X0ModifierCannotAppearOnAParameter);
                    if (node.Parent is ModuleBlockNode && (node.Parent.Flags & NodeFlags.Ambient) != 0)
                        return Report(modifier, DiagnosticCode.ADeclareModifierCannotBeUsedInAnAlreadyAmbientContext);
                    if (SemanticSyntax.Name(node) is PrivateIdentifierNode)
                        return Report(modifier, DiagnosticCode.X0ModifierCannotBeUsedWithAPrivateIdentifier);
                    if (Has(SyntaxKind.AccessorKeyword))
                        return Report(modifier, DiagnosticCode.X0ModifierCannotBeUsedWith1Modifier, "declare", "accessor");
                    break;
                case SyntaxKind.AbstractKeyword:
                    if (node is not (ClassDeclarationNode or ConstructorTypeNode))
                    {
                        if (node is not (MethodDeclarationNode or PropertyDeclarationNode or GetAccessorDeclarationNode
                            or SetAccessorDeclarationNode))
                            return Report(modifier, DiagnosticCode.XAbstractModifierCanOnlyAppearOnAClassMethodOrPropertyDeclaration);
                        if (node.Parent is not ClassDeclarationNode || !SemanticSyntax.HasModifier(node.Parent, SyntaxKind.AbstractKeyword))
                            return Report(
                                modifier,
                                node is PropertyDeclarationNode
                                    ? DiagnosticCode.AbstractPropertiesCanOnlyAppearWithinAnAbstractClass
                                    : DiagnosticCode.AbstractMethodsCanOnlyAppearWithinAnAbstractClass);
                        if (Has(SyntaxKind.StaticKeyword) || Has(SyntaxKind.PrivateKeyword))
                            return Report(
                                modifier,
                                DiagnosticCode.X0ModifierCannotBeUsedWith1Modifier,
                                Seen(SyntaxKind.StaticKeyword, SyntaxKind.PrivateKeyword),
                                "abstract");
                        if (Has(SyntaxKind.AsyncKeyword))
                            return Report(
                                modifiers.First(m => m.Kind == SyntaxKind.AsyncKeyword),
                                DiagnosticCode.X0ModifierCannotBeUsedWith1Modifier,
                                "async",
                                "abstract");
                        if (parsed && (Has(SyntaxKind.OverrideKeyword) || Has(SyntaxKind.AccessorKeyword)))
                            return Report(
                                modifier,
                                DiagnosticCode.X0ModifierMustPrecede1Modifier,
                                "abstract",
                                Seen(SyntaxKind.OverrideKeyword, SyntaxKind.AccessorKeyword));
                    }
                    if (SemanticSyntax.Name(node) is PrivateIdentifierNode)
                        return Report(modifier, DiagnosticCode.X0ModifierCannotBeUsedWithAPrivateIdentifier);
                    break;
                case SyntaxKind.AsyncKeyword:
                    if (Has(SyntaxKind.DeclareKeyword) || (node.Parent?.Flags & NodeFlags.Ambient) != 0)
                        return Report(modifier, DiagnosticCode.X0ModifierCannotBeUsedInAnAmbientContext);
                    if (node is ParameterDeclarationNode)
                        return Report(modifier, DiagnosticCode.X0ModifierCannotAppearOnAParameter);
                    if (Has(SyntaxKind.AbstractKeyword))
                        return Report(modifier, DiagnosticCode.X0ModifierCannotBeUsedWith1Modifier, "async", "abstract");
                    break;
                case SyntaxKind.OverrideKeyword:
                    if (Has(SyntaxKind.DeclareKeyword))
                        return Report(modifier, DiagnosticCode.X0ModifierCannotBeUsedWith1Modifier, "override", "declare");
                    if (parsed && (Has(SyntaxKind.ReadonlyKeyword) || Has(SyntaxKind.AccessorKeyword)
                        || Has(SyntaxKind.AsyncKeyword)))
                        return Report(
                            modifier,
                            DiagnosticCode.X0ModifierMustPrecede1Modifier,
                            "override",
                            Seen(SyntaxKind.ReadonlyKeyword, SyntaxKind.AccessorKeyword, SyntaxKind.AsyncKeyword));
                    break;
                case SyntaxKind.InKeyword:
                case SyntaxKind.OutKeyword:
                    return Report(modifier, DiagnosticCode.X0ModifierCanOnlyAppearOnATypeParameterOfAClassInterfaceOrTypeAlias);
                default:
                    return Report(modifier, DiagnosticCode.X0ModifierCannotBeUsedHere);
            }
            seen |= Flag(kind);
        }
        if (node is ConstructorDeclarationNode)
        {
            foreach (var kind in new[] { SyntaxKind.StaticKeyword, SyntaxKind.OverrideKeyword, SyntaxKind.AsyncKeyword })
                if (Has(kind))
                    return Report(modifiers.First(m => m.Kind == kind), DiagnosticCode.X0ModifierCannotAppearOnAConstructorDeclaration);
            return false;
        }
        if (node is ImportDeclarationNode or ImportEqualsDeclarationNode && Has(SyntaxKind.DeclareKeyword))
            return Report(
                modifiers.First(m => m.Kind == SyntaxKind.DeclareKeyword),
                DiagnosticCode.A0ModifierCannotBeUsedWithAnImportDeclaration);
        if (Has(SyntaxKind.AsyncKeyword)
            && node is not (MethodDeclarationNode or FunctionDeclarationNode or FunctionExpressionNode or ArrowFunctionNode))
            return Report(modifiers.First(m => m.Kind == SyntaxKind.AsyncKeyword), DiagnosticCode.X0ModifierCannotBeUsedHere);
        if (node is MethodDeclarationNode { Parent: ObjectLiteralExpressionNode }
            && (modifiers.Count != 1 || first.Kind != SyntaxKind.AsyncKeyword))
            return Report(first, DiagnosticCode.ModifiersCannotAppearHere);
        return false;

        bool Has(SyntaxKind kind) => (seen & Flag(kind)) != 0;

        static UInt128 Flag(SyntaxKind kind)
        {
            int index = (int)kind - (int)SyntaxKind.ConstKeyword;
            return (uint)index < 128 ? UInt128.One << index : 0;
        }

        TextSlice Seen(params SyntaxKind[] kinds)
        {
            foreach (var kind in kinds)
                if (Has(kind))
                    return TokenFacts.Text(kind);
            throw new InvalidOperationException("Sequence contains no matching element");
        }

        bool Report(SyntaxNode location, DiagnosticCode code, params TextSlice[] arguments)
        {
            Error(location, code, arguments.Length != 0 ? arguments
                : code is DiagnosticCode.X0ModifierAlreadySeen or DiagnosticCode.X0ModifierCannotBeUsedInAnAmbientContext
                    or DiagnosticCode.X0ModifierCannotBeUsedHere or DiagnosticCode.X0ModifierCannotAppearOnAModuleOrNamespaceElement
                    or DiagnosticCode.X0ModifierCannotAppearOnATypeMember or DiagnosticCode.X0ModifierCannotAppearOnAnIndexSignature
                    or DiagnosticCode.X0ModifierCannotAppearOnClassElementsOfThisKind
                    or DiagnosticCode.X0ModifierCannotAppearOnAParameter
                    or DiagnosticCode.X0ModifierCanOnlyAppearOnATypeParameterOfAClassInterfaceOrTypeAlias
                    or DiagnosticCode.X0ModifierCannotBeUsedWithAPrivateIdentifier
                    or DiagnosticCode.X0ModifierCannotAppearOnAConstructorDeclaration
                    or DiagnosticCode.A0ModifierCannotBeUsedWithAnImportDeclaration
                    ? [TokenFacts.Text(location.Kind)] : []);
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
