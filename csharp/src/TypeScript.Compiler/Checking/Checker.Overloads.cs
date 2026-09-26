using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private async ValueTask CheckOverloadDeclarationsAsync(Symbol symbol, CancellationToken cancellation)
    {
        var data = links.Values.Get(symbol);
        if (data.FunctionOrConstructorChecked)
            return;
        data.FunctionOrConstructorChecked = true;
        try
        {
            await CheckAsync().ConfigureAwait(false);
        }
        catch
        {
            data.FunctionOrConstructorChecked = false;
            throw;
        }

        async ValueTask CheckAsync()
        {
            bool constructor = (symbol.Flags & SymbolFlags.Constructor) != 0, duplicateBodies = false, hasOverloads = false, hasClass = false;
            SyntaxNode? body = null, previous = null, lastNonAmbient = null;
            var declarations = new List<SyntaxNode>();
            foreach (var node in symbol.Declarations)
            {
                cancellation.ThrowIfCancellationRequested();
                bool ambient = (node.Flags & NodeFlags.Ambient) != 0 || node.Parent is InterfaceDeclarationNode or TypeLiteralNode;
                if (ambient)
                    previous = null;
                hasClass |= SemanticSyntax.ClassLike(node) && (node.Flags & NodeFlags.Ambient) == 0;
                if (node is not FunctionDeclarationNode and not MethodDeclarationNode and not MethodSignatureDeclarationNode
                    and not ConstructorDeclarationNode)
                    continue;
                declarations.Add(node);
                bool present = SemanticSyntax.Body(node) is { } implementation && implementation.End > implementation.Pos;
                if (present && body is not null)
                    duplicateBodies = true;
                else if (previous is not null
                    && previous.Parent == node.Parent
                    && previous.End != node.Pos
                    && (previous.Flags & NodeFlags.Reparsed) == 0)
                    await MissingAsync(previous).ConfigureAwait(false);
                if (present)
                    body ??= node;
                else
                    hasOverloads = true;
                previous = node;
                if (!ambient)
                    lastNonAmbient = node;
            }
            if (duplicateBodies)
                foreach (var declaration in declarations)
                    Error(constructor ? declaration : SemanticSyntax.Name(declaration) ?? declaration, constructor ? 2392 : 2393);
            if (hasClass && !constructor && (symbol.Flags & SymbolFlags.Function) != 0)
                foreach (var declaration in symbol.Declarations)
                    if (declaration is ClassDeclarationNode or FunctionDeclarationNode)
                        Error(SemanticSyntax.Name(declaration) ?? declaration, declaration is ClassDeclarationNode ? 2813 : 2814);
            if (lastNonAmbient is not null
                && SemanticSyntax.Body(lastNonAmbient) is null
                && !SemanticSyntax.HasModifier(lastNonAmbient, SyntaxKind.AbstractKeyword)
                && !OptionalOverload(lastNonAmbient))
                await MissingAsync(lastNonAmbient).ConfigureAwait(false);
            if (!hasOverloads || declarations.Count == 0)
                return;
            var canonical = body?.Parent == declarations[0].Parent ? body : declarations[0];
            foreach (var group in declarations.GroupBy(SemanticSyntax.Source))
            {
                var first = group.First();
                var localCanonical = body?.Parent == first.Parent ? body : first;
                foreach (var declaration in group)
                {
                    if (Effective(declaration, SyntaxKind.ExportKeyword) != Effective(localCanonical!, SyntaxKind.ExportKeyword))
                        Error(SemanticSyntax.Name(declaration) ?? declaration, 2383);
                    else if (Effective(declaration, SyntaxKind.DeclareKeyword) != Effective(localCanonical!, SyntaxKind.DeclareKeyword))
                        Error(SemanticSyntax.Name(declaration) ?? declaration, 2384);
                    else if (Effective(declaration, SyntaxKind.PrivateKeyword) != Effective(canonical!, SyntaxKind.PrivateKeyword)
                        || Effective(declaration, SyntaxKind.ProtectedKeyword) != Effective(canonical!, SyntaxKind.ProtectedKeyword))
                        Error(SemanticSyntax.Name(declaration) ?? declaration, 2385);
                    else if (Effective(declaration, SyntaxKind.AbstractKeyword) != Effective(canonical!, SyntaxKind.AbstractKeyword))
                        Error(SemanticSyntax.Name(declaration) ?? declaration, 2512);
                    if (OptionalOverload(declaration) != OptionalOverload(canonical!))
                        Error(SemanticSyntax.Name(declaration) ?? declaration, 2386);
                }
            }
            if (body is null)
                return;
            var implementationSignature = await SignatureAssignability.ErasedAsync(
                await Signatures.FromDeclarationAsync(body, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);
            foreach (var overload in await Signatures.OfSymbolAsync(symbol, cancellation: cancellation).ConfigureAwait(false))
            {
                var target = await SignatureAssignability.ErasedAsync(overload, cancellation).ConfigureAwait(false);
                var sourceReturn = await Signatures.ReturnAsync(implementationSignature, cancellation).ConfigureAwait(false);
                var targetReturn = await Signatures.ReturnAsync(target, cancellation).ConfigureAwait(false);
                bool returns = targetReturn == context.VoidType
                    || await AssignableAsync(targetReturn, sourceReturn, cancellation).ConfigureAwait(false)
                    || await AssignableAsync(sourceReturn, targetReturn, cancellation).ConfigureAwait(false);
                if (!returns
                    || !await Relations.SignatureAsync(
                        implementationSignature,
                        target,
                        SignatureAssignability,
                        SignatureInstantiation,
                        true,
                        cancellation).ConfigureAwait(false))
                {
                    Error(overload.Declaration!, CheckerDiagnostic.Create(overload.Declaration!,
                        Messages.This_overload_signature_is_not_compatible_with_its_implementation_signature) with
                    {
                        RelatedInformation = [CheckerDiagnostic.Create(body, Messages.The_implementation_signature_is_declared_here)]
                    });
                    break;
                }
            }

            async ValueTask MissingAsync(SyntaxNode node)
            {
                var name = SemanticSyntax.Name(node);
                if (name is not null && name.Pos == name.End)
                    return;
                SyntaxNode? next = null;
                bool seen = false;
                if (node.Parent is { } parent)
                    for (int i = 0; i < parent.ChildCount; i++)
                    {
                        var child = parent.GetChild(i);
                        if (seen)
                        {
                            next = child;
                            break;
                        }
                        seen = child == node;
                    }
                if (next is not null && next.Pos == node.End && next.Kind == node.Kind)
                {
                    var nextName = SemanticSyntax.Name(next);
                    bool same = name is not null
                        && nextName is not null && (name is PrivateIdentifierNode
                            && nextName is PrivateIdentifierNode
                            && SyntaxNameText.Get(name) == SyntaxNameText.Get(nextName)
                        || name is ComputedPropertyNameNode computed && nextName is ComputedPropertyNameNode other
                            && await IdenticalAsync(
                                await ComputedNameAsync(computed, cancellation).ConfigureAwait(false),
                                await ComputedNameAsync(other, cancellation).ConfigureAwait(false),
                                cancellation).ConfigureAwait(false)
                        || OverloadNameText(name) is { } text && text == OverloadNameText(nextName));
                    if (same)
                    {
                        if (node is MethodDeclarationNode or MethodSignatureDeclarationNode
                            && SemanticSyntax.IsStatic(node) != SemanticSyntax.IsStatic(next))
                            Error(nextName ?? next, SemanticSyntax.IsStatic(node) ? 2387 : 2388);
                        return;
                    }
                    if (SemanticSyntax.Body(next) is { } nextBody && nextBody.End > nextBody.Pos)
                    {
                        Error(nextName ?? next, 2389, name is null ? "" : CheckerDiagnostic.DeclarationName(name));
                        return;
                    }
                }
                Error(name ?? node, constructor ? 2390 : SemanticSyntax.HasModifier(node, SyntaxKind.AbstractKeyword) ? 2516 : 2391);
            }
        }
    }

    private bool Effective(SyntaxNode node, SyntaxKind modifier)
    {
        var root = SemanticSyntax.RootDeclaration(node);
        var modified = root is VariableDeclarationNode && root.Parent?.Parent is VariableStatementNode statement ? statement : root;
        bool specified = SemanticSyntax.HasModifier(modified, modifier);
        if (node.Parent is InterfaceDeclarationNode || SemanticSyntax.ClassLike(node.Parent))
            return specified;
        if (modifier == SyntaxKind.DeclareKeyword)
            return specified || (node.Flags & NodeFlags.Ambient) != 0;
        if (modifier == SyntaxKind.ExportKeyword && (node.Flags & NodeFlags.Ambient) != 0
            && !SemanticSyntax.HasModifier(modified, SyntaxKind.DeclareKeyword)
            && node.Parent is not ModuleBlockNode { Parent: ModuleDeclarationNode { Keyword: SyntaxKind.GlobalKeyword } })
        {
            var container = SemanticSyntax.DeclarationContainer(node);
            if (container is ModuleBlockNode)
                container = container.Parent;
            if (container is not null
                && ((container.Flags | (program.Symbols.Binding(container)?.Get(container)?.Flags ?? 0)) & NodeFlags.ExportContext) != 0)
                return true;
        }
        return specified;
    }

    private static bool OptionalOverload(SyntaxNode node) => node switch
    {
        MethodDeclarationNode method => method.PostfixToken?.Kind == SyntaxKind.QuestionToken,
        MethodSignatureDeclarationNode method => method.PostfixToken?.Kind == SyntaxKind.QuestionToken,
        _ => false
    };

    private static string? OverloadNameText(SyntaxNode node) => node switch
    {
        IdentifierNode identifier => identifier.Text,
        StringLiteralNode literal => literal.Text,
        NoSubstitutionTemplateLiteralNode literal => literal.Text,
        NumericLiteralNode literal => literal.Text,
        _ => null
    };

    private async ValueTask CheckFunctionOverloadsAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (SemanticSyntax.Name(node) is ComputedPropertyNameNode computed
            && computed.Expression is not (StringLiteralNode or NumericLiteralNode or NoSubstitutionTemplateLiteralNode
                or PrefixUnaryExpressionNode { Operator: SyntaxKind.PlusToken or SyntaxKind.MinusToken, Operand: NumericLiteralNode })
            && !await LateMembers.BindableAsync(node, cancellation))
            return;
        var symbol = program.Symbols.Declaration(node)!;
        if ((node.Flags & NodeFlags.JavaScriptFile) == 0)
            await CheckOverloadDeclarationsAsync(
                program.Symbols.Binding(node)?.Get(node)?.LocalSymbol ?? symbol,
                cancellation).ConfigureAwait(false);
        if (symbol.Parent is not null)
            await CheckOverloadDeclarationsAsync(symbol, cancellation).ConfigureAwait(false);
    }
}
