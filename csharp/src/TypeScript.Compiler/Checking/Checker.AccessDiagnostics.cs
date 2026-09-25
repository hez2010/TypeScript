using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    public async ValueTask AccessErrorAsync(SyntaxNode node, int code, CancellationToken cancellation, Type? type = null,
        Symbol? symbol = null, Type? index = null, Symbol? related = null)
    {
        string name = symbol is not null ? TypeDisplay.SymbolName(symbol) : CheckerDiagnostic.DeclarationName(node);
        async ValueTask<string> ReceiverAsync() => type?.Symbol == program.Symbols.GlobalThisSymbol ? "typeof globalThis"
            : await TypeDisplay.GetAsync(type!, cancellation);
        string[] arguments = code switch
        {
            1111 or 2540 or 2565 or 2803 or 4105 or 4111 => [name],
            2339 => [name, await ReceiverAsync()],
            2542 or 7017 => [await ReceiverAsync()],
            2536 => [await TypeDisplay.GetAsync(index!, cancellation), await ReceiverAsync()],
            18013 => [CheckerDiagnostic.DeclarationName(node),
                TypeDisplay.SymbolName(program.Symbols.Declaration(DeclarationOrder.ContainingClass(symbol!.ValueDeclaration!)!)!)],
            18014 => [CheckerDiagnostic.DeclarationName(node), await ReceiverAsync()],
            18016 or 2476 or 2806 => [],
            _ => throw new InvalidOperationException($"Unsupported access diagnostic {code}")
        };
        var diagnostic = CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code), arguments);
        if (code == 18014 && symbol?.ValueDeclaration is { } shadowing && related?.ValueDeclaration is { } original)
            diagnostic = diagnostic with
            {
                RelatedInformation = [
                CheckerDiagnostic.Create(shadowing, Messages.The_shadowing_declaration_of_0_is_defined_here, arguments[0]),
                CheckerDiagnostic.Create(
                    original,
                    Messages.The_declaration_of_0_that_you_probably_intended_to_use_is_defined_here,
                    arguments[0])]
            };
        cancellation.ThrowIfCancellationRequested();
        Error(node, diagnostic);
    }

    public async ValueTask MemberErrorAsync(SyntaxNode node, int code, Symbol symbol, CancellationToken cancellation,
        Type? type = null, Type? enclosing = null)
    {
        string name = TypeDisplay.SymbolName(symbol);
        string[] arguments = code switch
        {
            2855 => [name],
            2729 or 2449 => [SyntaxNameText.Get(node)],
            2341 or 2445 or 2513 =>
                [
                    name,
                    await TypeDisplay.GetAsync(await DeclaringClassAsync(symbol, cancellation) ?? type!, cancellation)
                ],
            2446 => [name, await TypeDisplay.GetAsync(enclosing!, cancellation), await TypeDisplay.GetAsync(type!, cancellation)],
            2715 => [name, TypeDisplay.SymbolName(program.Symbols.Parent(symbol)!)],
            _ => throw new InvalidOperationException($"Unsupported member diagnostic {code}")
        };
        var diagnostic = CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code), arguments);
        if (code is 2729 or 2449 && symbol.ValueDeclaration is { } declaration)
            diagnostic = diagnostic with
            {
                RelatedInformation = [CheckerDiagnostic.Create(
                declaration,
                Messages.X_0_is_declared_here,
                arguments[0])]
            };
        cancellation.ThrowIfCancellationRequested();
        Error(node, diagnostic);
    }
}
