using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly HashSet<SyntaxNode> deferredNameCollisions = [];

    private void CheckDeclarationName(SyntaxNode node)
    {
        if (SemanticSyntax.Name(node) is BindingPatternNode)
        {
            foreach (var element in SemanticSyntax.Name(node)!.DescendantsAndSelf().OfType<BindingElementNode>())
                if (element.Name is IdentifierNode)
                    CheckDeclarationName(element);
            return;
        }
        if (SemanticSyntax.Name(node) is not IdentifierNode name)
            return;
        bool ambient = (node.Flags & NodeFlags.Ambient) != 0;
        if (SemanticSyntax.ClassLike(node) && name.Text == "Object" && !ambient && EmitModuleKind(node) < 5)
            Error(name, DiagnosticCode.ClassNameCannotBeObjectWhenTargetingES5AndAboveWithModule0, ModuleKind switch
            {
                0 => "None",
                1 => "CommonJS",
                2 => "AMD",
                3 => "UMD",
                4 => "System",
                5 => "ES2015",
                6 => "ES2020",
                7 => "ES2022",
                99 => "ESNext",
                100 => "Node16",
                101 => "Node18",
                102 => "Node20",
                199 => "NodeNext",
                200 => "Preserve",
                _ => "ModuleKind(" + ModuleKind.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"
            });
        if (ambient || node is PropertyDeclarationNode or PropertySignatureDeclarationNode or MethodDeclarationNode
            or MethodSignatureDeclarationNode
            or GetAccessorDeclarationNode or SetAccessorDeclarationNode or PropertyAssignmentNode
            || AliasResolver.IsTypeOnly(node)
            || SemanticSyntax.RootDeclaration(node) is ParameterDeclarationNode parameter && SemanticSyntax.Body(parameter.Parent!) is null)
            return;
        if (program.Symbols.Program.Configuration.Options.Boolean("noEmit") == true)
            return;
        if ((node is not ModuleDeclarationNode declaration || Binder.ModuleState(declaration) == 2)
            && SemanticSyntax.DeclarationContainer(node) is SourceFileNode file && program.Symbols.Binding(file)?.IsModule == true)
        {
            int module = EmitModuleKind(node);
            if (name.Text is "require" or "exports" && module < 5
                || name.Text == "Object" && !SemanticSyntax.ClassLike(node) && module == 1)
                Error(name, DiagnosticCode.DuplicateIdentifier0CompilerReservesName1InTopLevelScopeOfAModule, name.Text, name.Text);
            if (name.Text == "Promise" && TargetYear < 2017
                && file.DescendantsAndSelf().Any(n => SemanticSyntax.HasModifier(n, SyntaxKind.AsyncKeyword)))
                Error(name, DiagnosticCode.DuplicateIdentifier0CompilerReservesName1InTopLevelScopeOfAModuleContainingAsyncFunctions);
        }
        if (TargetYear <= 2021 && name.Text is "WeakMap" or "WeakSet" or "Reflect")
            deferredNameCollisions.Add(node);
    }

    private void MarkPrivateIdentifierScopes(SyntaxNode node)
    {
        if (TargetYear == int.MaxValue && UseDefineForClassFields)
            return;
        foreach (var member in PropertyInitialization.Members(node))
            if (SemanticSyntax.Name(member) is PrivateIdentifierNode)
                for (var scope = DeclarationOrder.BlockContainer(member); scope is not null; scope = DeclarationOrder.BlockContainer(scope))
                    links.Nodes.Get(scope).Flags |= NodeCheckFlags.ContainsClassWithPrivateIdentifiers;
    }

    private void CheckDeferredDeclarationNames(SourceFileNode file, CancellationToken cancellation)
    {
        foreach (var node in deferredNameCollisions.Where(n => SemanticSyntax.Source(n) == file))
        {
            cancellation.ThrowIfCancellationRequested();
            string name = ((IdentifierNode)SemanticSyntax.Name(node)!).Text;
            var scope = DeclarationOrder.BlockContainer(node);
            if (name is "WeakMap" or "WeakSet")
            {
                if (scope is not null && (links.Nodes.Get(scope).Flags & NodeCheckFlags.ContainsClassWithPrivateIdentifiers) != 0)
                    Error(node, DiagnosticCode.CompilerReservesName0WhenEmittingPrivateIdentifierDownlevel, name);
            }
            else
            {
                bool collision = node is ClassExpressionNode
                    ? PropertyInitialization.Members(node).Any(
                        m => (links.Nodes.Get(m).Flags & NodeCheckFlags.ContainsSuperPropertyInStaticInitializer) != 0)
                    : (links.Nodes.Get(
                        node is FunctionExpressionNode
                            ? node
                            : scope ?? node).Flags & NodeCheckFlags.ContainsSuperPropertyInStaticInitializer) != 0;
                if (collision)
                    Error(
                        node,
                        DiagnosticCode.DuplicateIdentifier0CompilerReservesName1WhenEmittingSuperReferencesInStaticInitializers,
                        name,
                        "Reflect");
            }
        }
    }
}
