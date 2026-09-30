using TypeScript.Compiler.Text;
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
        if (SemanticSyntax.ClassLike(node) && name.Text == Utf8Literals.ObjectType && !ambient && EmitModuleKind(node) < 5)
            Error(name, DiagnosticCode.ClassNameCannotBeObjectWhenTargetingES5AndAboveWithModule0, ModuleKind switch
            {
                0 => Utf8Literals.None,
                1 => Utf8Literals.CommonJS,
                2 => Utf8Literals.AMD,
                3 => Utf8Literals.UMD,
                4 => Utf8Literals.System,
                5 => Utf8Literals.ES2015,
                6 => Utf8Literals.ES2020,
                7 => Utf8Literals.ES2022,
                99 => Utf8Literals.ESNext,
                100 => Utf8Literals.Node16,
                101 => Utf8Literals.Node18,
                102 => Utf8Literals.Node20,
                199 => Utf8Literals.NodeNext,
                200 => Utf8Literals.Preserve,
                _ => Utf8Literals.ModuleKind + Utf8String.Format(ModuleKind) + Utf8Literals.CloseParen
            });
        if (ambient || node is PropertyDeclarationNode or PropertySignatureDeclarationNode or MethodDeclarationNode
            or MethodSignatureDeclarationNode
            or GetAccessorDeclarationNode or SetAccessorDeclarationNode or PropertyAssignmentNode
            || AliasResolver.IsTypeOnly(node)
            || SemanticSyntax.RootDeclaration(node) is ParameterDeclarationNode parameter && SemanticSyntax.Body(parameter.Parent!) is null)
            return;
        if (program.Symbols.Program.Configuration.Options.NoEmit == true)
            return;
        if ((node is not ModuleDeclarationNode declaration || Binder.ModuleState(declaration) == 2)
            && SemanticSyntax.DeclarationContainer(node) is SourceFileNode file && program.Symbols.Binding(file)?.IsModule == true)
        {
            int module = EmitModuleKind(node);
            if ((name.Text.Span.SequenceEqual("require"u8) || name.Text.Span.SequenceEqual("exports"u8)) && module < 5
                || name.Text == Utf8Literals.ObjectType && !SemanticSyntax.ClassLike(node) && module == 1)
                Error(name, DiagnosticCode.DuplicateIdentifier0CompilerReservesName1InTopLevelScopeOfAModule, name.Text, name.Text);
            if (name.Text == Utf8Literals.Promise && TargetYear < 2017
                && file.DescendantsAndSelf().Any(n => SemanticSyntax.HasModifier(n, SyntaxKind.AsyncKeyword)))
                Error(name, DiagnosticCode.DuplicateIdentifier0CompilerReservesName1InTopLevelScopeOfAModuleContainingAsyncFunctions);
        }
        if (TargetYear <= 2021 && (name.Text.Span.SequenceEqual("WeakMap"u8) || name.Text.Span.SequenceEqual("WeakSet"u8) || name.Text.Span.SequenceEqual("Reflect"u8)))
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
            Utf8String name = ((IdentifierNode)SemanticSyntax.Name(node)!).Text;
            var scope = DeclarationOrder.BlockContainer(node);
            if (name.Span.SequenceEqual("WeakMap"u8) || name.Span.SequenceEqual("WeakSet"u8))
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
                        Utf8Literals.Reflect);
            }
        }
    }
}
