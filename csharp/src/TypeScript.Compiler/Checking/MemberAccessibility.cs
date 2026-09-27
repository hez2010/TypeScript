using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IMemberAccessibilityHost
{
    CheckFlags AccessFlags(Symbol symbol, bool writing);

    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type?> ContextualThisAsync(SyntaxNode node, CancellationToken cancellation);

    bool ClassInstanceProperty(SyntaxNode declaration);

    ValueTask MemberErrorAsync(
        SyntaxNode node,
        DiagnosticCode code,
        Symbol symbol,
        CancellationToken cancellation,
        Type? type = null,
        Type? enclosing = null);
}

internal sealed class MemberAccessibility(CheckerSymbols symbols, CheckerLinks links, DeclaredTypes declared, BaseTypes bases,
    TypeConstraints constraints, TypeProperties properties, IMemberAccessibilityHost host)
{
    internal async ValueTask<bool> CheckAsync(SyntaxNode node, bool super, bool writing, Type containingType, Symbol property,
        bool report = true, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        SyntaxNode? error = report ? node switch
        {
            PropertyAccessExpressionNode access => access.Name,
            QualifiedNameNode qualified => qualified.Right,
            ImportTypeNode => node,
            BindingElementNode binding => binding.PropertyName ?? binding.Name,
            _ => SemanticSyntax.Name(node)
        } : null;
        var flags = host.AccessFlags(property, writing);
        bool abstractMember = IsAbstract(property, writing);
        if (super)
        {
            if (abstractMember)
                return await FailAsync(DiagnosticCode.AbstractMethod0InClass1CannotBeAccessedViaSuperExpression);
            if ((flags & CheckFlags.ContainsStatic) == 0 && property.Declarations.Any(host.ClassInstanceProperty))
                return await FailAsync(DiagnosticCode.ClassField0DefinedByTheParentClassIsNotAccessibleInTheChildClassViaSuper);
        }
        if (abstractMember
            && await AnyPropertyAsync(
                property,
                p => ValueTask.FromResult((p.Flags & SymbolFlags.Method) == 0),
                cancellation).ConfigureAwait(false)
            && (FlowReferences.Receiver(node)?.Kind == SyntaxKind.ThisKeyword || ThisInitializedBinding(node)
                || node.Parent?.Kind == SyntaxKind.ObjectBindingPattern
                    && node.Parent.Parent is VariableDeclarationNode { Initializer.Kind: SyntaxKind.ThisKeyword })
            && symbols.Parent(property) is { Flags: var parentFlags } && (parentFlags & SymbolFlags.Class) != 0 && UsedDuringInitialization(node))
            return await FailAsync(DiagnosticCode.AbstractProperty0InClass1CannotBeAccessedInTheConstructor);
        var privateFlag = writing ? CheckFlags.ContainsWritePrivate : CheckFlags.ContainsPrivate;
        var protectedFlag = writing ? CheckFlags.ContainsWriteProtected : CheckFlags.ContainsProtected;
        if ((flags & (privateFlag | protectedFlag)) == 0)
            return true;
        if ((flags & privateFlag) != 0)
        {
            var declaration = symbols.Parent(property)?.Declarations.FirstOrDefault(SemanticSyntax.ClassLike);
            return declaration is not null && DeclarationOrder.Ancestor(node.Parent, n => n == declaration) is not null
                || await FailAsync(DiagnosticCode.Property0IsPrivateAndOnlyAccessibleWithinClass1);
        }
        if (super)
            return true;
        Type? enclosing = null;
        for (var container = DeclarationOrder.ContainingClass(node); container is not null; container = DeclarationOrder.ContainingClass(container))
        {
            var type = await declared.GetAsync(symbols.Declaration(container)!, cancellation).ConfigureAwait(false);
            if (await DerivedFromDeclaringAsync(type, property, writing, cancellation).ConfigureAwait(false))
            {
                enclosing = type;
                break;
            }
        }
        if (enclosing is null)
        {
            var type = await EnclosingThisClassAsync(node, cancellation).ConfigureAwait(false);
            if (type is not null && await DerivedFromDeclaringAsync(type, property, writing, cancellation).ConfigureAwait(false))
                enclosing = type;
            if ((flags & CheckFlags.ContainsStatic) != 0 || enclosing is null)
                return await FailAsync(DiagnosticCode.Property0IsProtectedAndOnlyAccessibleWithinClass1AndItsSubclasses);
        }
        if ((flags & CheckFlags.ContainsStatic) != 0)
            return true;
        Type? receiver = containingType;
        if (receiver is TypeParameter parameter)
            receiver = parameter.IsThisType ? await constraints.ConstraintAsync(parameter, cancellation).ConfigureAwait(false)
                : await constraints.BaseConstraintAsync(parameter, cancellation).ConfigureAwait(false);
        if (receiver is null || !await bases.HasBaseAsync(receiver, enclosing, cancellation).ConfigureAwait(false))
        {
            if (receiver is not null)
                await FailAsync(
                    DiagnosticCode.Property0IsProtectedAndOnlyAccessibleThroughAnInstanceOfClass1ThisIsAnInstanceOfClass2,
                    enclosing,
                    receiver);
            return false;
        }
        return true;

        async ValueTask<bool> FailAsync(DiagnosticCode code, Type? enclosingType = null, Type? receiverType = null)
        {
            if (error is not null)
                await host.MemberErrorAsync(
                    error,
                    code,
                    property,
                    cancellation,
                    receiverType ?? containingType,
                    enclosingType).ConfigureAwait(false);
            return false;
        }
    }

    private async ValueTask<bool> DerivedFromDeclaringAsync(Type type, Symbol property, bool writing, CancellationToken cancellation)
        => !await AnyPropertyAsync(property, async candidate =>
        {
            if ((host.AccessFlags(candidate, writing) & (writing ? CheckFlags.ContainsWriteProtected : CheckFlags.ContainsProtected)) == 0)
                return false;
            var parent = symbols.Parent(candidate);
            if (parent is null || (parent.Flags & SymbolFlags.Class) == 0)
                throw new InvalidOperationException("Protected property has no declaring class");
            return !await bases.HasBaseAsync(
                type,
                await declared.GetAsync(parent, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);
        }, cancellation).ConfigureAwait(false);

    private async ValueTask<bool> AnyPropertyAsync(Symbol property, Func<Symbol, ValueTask<bool>> predicate, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if ((property.CheckFlags & CheckFlags.Synthetic) == 0)
            return await predicate(property).ConfigureAwait(false);
        var type = links.Values.Get(property).ContainingType as UnionOrIntersectionType ?? throw new InvalidOperationException("Synthetic property has no containing type");
        foreach (var part in type.Types)
            if (await properties.PropertyAsync(part, property.Name, cancellation: cancellation).ConfigureAwait(false) is { } member
                && await AnyPropertyAsync(member, predicate, cancellation).ConfigureAwait(false))
                return true;
        return false;
    }

    private async ValueTask<Type?> EnclosingThisClassAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var container = MissingNamePrefixes.ThisContainer(node, false, false);
        Type? type = null;
        if (container is IFunctionSignature { Parameters.Count: > 0 } signature
            && signature.Parameters[0] is ParameterDeclarationNode { Name: IdentifierNode { Text: "this" }, Type: { } annotation })
            type = await host.TypeFromNodeAsync(annotation, cancellation).ConfigureAwait(false);
        if (type is TypeParameter parameter)
            type = await constraints.ConstraintAsync(parameter, cancellation).ConfigureAwait(false);
        else if (type is null && container is IFunctionSignature)
            type = await host.ContextualThisAsync(container, cancellation).ConfigureAwait(false);
        return type is not null && (type.ObjectFlags & (ObjectFlags.ClassOrInterface | ObjectFlags.Reference)) != 0
            ? type is TypeReference reference && (type.ObjectFlags & ObjectFlags.Reference) != 0 ? reference.Target : type : null;
    }

    private static bool IsAbstract(Symbol symbol, bool writing)
    {
        if ((symbol.CheckFlags & CheckFlags.Synthetic) != 0 || symbol.ValueDeclaration is null)
            return false;
        var declaration = writing ? symbol.Declarations.OfType<SetAccessorDeclarationNode>().FirstOrDefault() : null;
        SyntaxNode selected = declaration ?? ((symbol.Flags & SymbolFlags.GetAccessor) != 0
            ? symbol.Declarations.OfType<GetAccessorDeclarationNode>().FirstOrDefault()
            : null)
            ?? symbol.ValueDeclaration;
        return SemanticSyntax.HasModifier(selected, SyntaxKind.AbstractKeyword);
    }

    private static bool ThisInitializedBinding(SyntaxNode node) => node is PropertyAssignmentNode or ShorthandPropertyAssignmentNode
        && node.Parent?.Parent is BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.EqualsToken, Right.Kind: SyntaxKind.ThisKeyword };

    private static bool UsedDuringInitialization(SyntaxNode node)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (current is PropertyDeclarationNode || current is ConstructorDeclarationNode { Body: { } body } && body.Pos != body.End)
                return true;
            if (SemanticSyntax.ClassLike(current) || SemanticSyntax.FunctionDeclarationLike(current))
                return false;
        }
        return false;
    }
}
