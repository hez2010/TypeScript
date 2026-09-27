using TypeScript.Compiler.Text;
using System.Globalization;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IPrivateAccessHost
{
    int TargetYear { get; }
    bool UseDefineForClassFields { get; }

    bool PlainJavaScript(SourceFileNode file);

    ValueTask PrivateEmitHelpersAsync(SyntaxNode node, bool read, bool write, CancellationToken cancellation);

    ValueTask AccessErrorAsync(
        SyntaxNode node,
        DiagnosticCode code,
        CancellationToken cancellation,
        Type? type = null,
        Symbol? symbol = null,
        Type? index = null,
        Symbol? related = null);
}

internal sealed class PrivateAccess(TypeContext context, CheckerSymbols symbols, TypeProperties properties, IPrivateAccessHost host)
{
    internal async ValueTask<(Symbol? Property, Type? Result)> ResolveAsync(SyntaxNode node, Type left, Type apparent,
        PrivateIdentifierNode name, bool anyLike, int assignment, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (host.TargetYear < int.MaxValue || !host.UseDefineForClassFields)
            await host.PrivateEmitHelpersAsync(node, assignment != 1, assignment != 0, cancellation).ConfigureAwait(false);
        Symbol? lexical = null;
        for (var container = ContainingClass(name); container is not null; container = DeclarationOrder.ContainingClass(container))
        {
            var owner = symbols.Binding(container)!.Get(container)!.Value.Symbol!;
            TextSlice key = Name(owner, name.Text);
            lexical = owner.Members.GetValueOrDefault(key) ?? owner.Exports.GetValueOrDefault(key);
            if (lexical is not null)
                break;
        }
        if (assignment != 0 && lexical?.ValueDeclaration is MethodDeclarationNode)
            await host.AccessErrorAsync(
                name,
                DiagnosticCode.CannotAssignToPrivateMethod0PrivateMethodsAreNotWritable,
                cancellation,
                symbol: lexical).ConfigureAwait(false);
        if (anyLike)
        {
            if (lexical is not null)
                return (null, apparent == context.ErrorType || (apparent.Flags & TypeFlags.Any) != 0 && apparent.Alias is not null
                    ? context.ErrorType
                    : apparent);
            if (ContainingClass(name) is null)
            {
                await host.AccessErrorAsync(
                    name,
                    DiagnosticCode.PrivateIdentifiersAreNotAllowedOutsideClassBodies,
                    cancellation).ConfigureAwait(false);
                return (null, context.AnyType);
            }
        }
        var property = lexical is null
            ? null
            : await properties.PropertyAsync(left, lexical.Name, cancellation: cancellation).ConfigureAwait(false);
        if (property is null)
        {
            if (await ReportScopeAsync(left, name, lexical, cancellation).ConfigureAwait(false))
                return (null, context.ErrorType);
            if (ContainingClass(name) is { } container && SemanticSyntax.Source(container) is { } file && host.PlainJavaScript(file))
                await host.AccessErrorAsync(
                    name,
                    DiagnosticCode.PrivateField0MustBeDeclaredInAnEnclosingClass,
                    cancellation).ConfigureAwait(false);
        }
        else if ((property.Flags & (SymbolFlags.SetAccessor | SymbolFlags.GetAccessor)) == SymbolFlags.SetAccessor && assignment != 1)
            await host.AccessErrorAsync(
                node,
                DiagnosticCode.PrivateAccessorWasDefinedWithoutAGetter,
                cancellation,
                symbol: property).ConfigureAwait(false);
        return (property, null);
    }

    private async ValueTask<bool> ReportScopeAsync(Type type, PrivateIdentifierNode name, Symbol? lexical, CancellationToken cancellation)
    {
        foreach (var property in await properties.GetAsync(type, cancellation).ConfigureAwait(false))
        {
            if (property.ValueDeclaration is not { } declaration
                || SemanticSyntax.Name(declaration) is not PrivateIdentifierNode privateName
                || privateName.Text != name.Text)
                continue;
            var owner = DeclarationOrder.ContainingClass(declaration);
            if (lexical?.ValueDeclaration is { } lexicalDeclaration
                && DeclarationOrder.ContainingClass(lexicalDeclaration) is { } lexicalClass
                && DeclarationOrder.Ancestor(lexicalClass, n => n == owner) is not null)
                await host.AccessErrorAsync(
                    name,
                    DiagnosticCode.TheProperty0CannotBeAccessedOnType1WithinThisClassBecauseItIsShadowedByAnotherPrivateIdentifierWithTheSameSpelling,
                    cancellation,
                    type,
                    lexical,
                    related: property).ConfigureAwait(false);
            else
                await host.AccessErrorAsync(
                    name,
                    DiagnosticCode.Property0IsNotAccessibleOutsideClass1BecauseItHasAPrivateIdentifier,
                    cancellation,
                    type,
                    property).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    internal static TextSlice Name(Symbol owner, TextSlice description) =>
TextSlice.ConcatMany(Symbol.InternalPrefix + "#", TextSlice.Format(owner.Id), "@", description);

    internal static SyntaxNode? ContainingClass(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null && !SemanticSyntax.ClassLike(current); current = current.Parent)
            if (current is DecoratorNode)
                return SemanticSyntax.ClassLike(current.Parent)
                    ? DeclarationOrder.ContainingClass(current.Parent!)
                    : DeclarationOrder.ContainingClass(current);
        return DeclarationOrder.ContainingClass(node);
    }
}
