using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    public async ValueTask AccessErrorAsync(SyntaxNode node, DiagnosticCode code, CancellationToken cancellation, Type? type = null,
        Symbol? symbol = null, Type? index = null, Symbol? related = null)
    {
        TextSlice name = symbol is not null ? TypeDisplay.SymbolName(symbol) : CheckerDiagnostic.DeclarationName(node);
        async ValueTask<TextSlice> ReceiverAsync() => type?.Symbol == program.Symbols.GlobalThisSymbol ? "typeof globalThis"
            : await TypeDisplay.GetAsync(type!, cancellation);
        TextSlice[] arguments = code switch
        {
            DiagnosticCode.PrivateField0MustBeDeclaredInAnEnclosingClass or DiagnosticCode.CannotAssignTo0BecauseItIsAReadOnlyProperty
                or DiagnosticCode.Property0IsUsedBeforeBeingAssigned
                or DiagnosticCode.CannotAssignToPrivateMethod0PrivateMethodsAreNotWritable
                or DiagnosticCode.PrivateOrProtectedMember0CannotBeAccessedOnATypeParameter
                or DiagnosticCode.Property0ComesFromAnIndexSignatureSoItMustBeAccessedWith0 => [name],
            DiagnosticCode.Property0DoesNotExistOnType1 => [name, await ReceiverAsync()],
            DiagnosticCode.IndexSignatureInType0OnlyPermitsReading
                or DiagnosticCode.ElementImplicitlyHasAnAnyTypeBecauseType0HasNoIndexSignature => [await ReceiverAsync()],
            DiagnosticCode.Type0CannotBeUsedToIndexType1 => [await TypeDisplay.GetAsync(index!, cancellation), await ReceiverAsync()],
            DiagnosticCode.Property0IsNotAccessibleOutsideClass1BecauseItHasAPrivateIdentifier => [CheckerDiagnostic.DeclarationName(node),
                await SymbolDisplayNameAsync(program.Symbols.Declaration(DeclarationOrder.ContainingClass(symbol!.ValueDeclaration!)!)!,
                    null, SymbolFlags.All, cancellation)],
            DiagnosticCode.TheProperty0CannotBeAccessedOnType1WithinThisClassBecauseItIsShadowedByAnotherPrivateIdentifierWithTheSameSpelling =>
                [
                    CheckerDiagnostic.DeclarationName(node),
                    await ReceiverAsync()
                ],
            DiagnosticCode.PrivateIdentifiersAreNotAllowedOutsideClassBodies
                or DiagnosticCode.AConstEnumMemberCanOnlyBeAccessedUsingAStringLiteral
                or DiagnosticCode.PrivateAccessorWasDefinedWithoutAGetter => [],
            _ => throw new InvalidOperationException($"Unsupported access diagnostic {code}")
        };
        var diagnostic = CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code), arguments);
        if (code == DiagnosticCode.TheProperty0CannotBeAccessedOnType1WithinThisClassBecauseItIsShadowedByAnotherPrivateIdentifierWithTheSameSpelling
            && symbol?.ValueDeclaration is { } shadowing
            && related?.ValueDeclaration is { } original)
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

    public async ValueTask MemberErrorAsync(SyntaxNode node, DiagnosticCode code, Symbol symbol, CancellationToken cancellation,
        Type? type = null, Type? enclosing = null)
    {
        TextSlice name = TypeDisplay.SymbolName(symbol);
        TextSlice[] arguments = code switch
        {
            DiagnosticCode.ClassField0DefinedByTheParentClassIsNotAccessibleInTheChildClassViaSuper => [name],
            DiagnosticCode.Property0IsUsedBeforeItsInitialization or DiagnosticCode.Class0UsedBeforeItsDeclaration => [SyntaxNameText.Get(node)],
            DiagnosticCode.Property0IsPrivateAndOnlyAccessibleWithinClass1
                or DiagnosticCode.Property0IsProtectedAndOnlyAccessibleWithinClass1AndItsSubclasses
                or DiagnosticCode.AbstractMethod0InClass1CannotBeAccessedViaSuperExpression =>
                [
                    name,
                    await TypeDisplay.GetAsync(await DeclaringClassAsync(symbol, cancellation) ?? type!, cancellation)
                ],
            DiagnosticCode.Property0IsProtectedAndOnlyAccessibleThroughAnInstanceOfClass1ThisIsAnInstanceOfClass2 =>
                [
                    name,
                    await TypeDisplay.GetAsync(enclosing!, cancellation),
                    await TypeDisplay.GetAsync(type!, cancellation)
                ],
            DiagnosticCode.AbstractProperty0InClass1CannotBeAccessedInTheConstructor =>
                [
                    name,
                    await SymbolDisplayNameAsync(program.Symbols.Parent(symbol)!, null, SymbolFlags.All, cancellation)
                ],
            _ => throw new InvalidOperationException($"Unsupported member diagnostic {code}")
        };
        var diagnostic = CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code), arguments);
        if (code is DiagnosticCode.Property0IsUsedBeforeItsInitialization or DiagnosticCode.Class0UsedBeforeItsDeclaration
            && symbol.ValueDeclaration is { } declaration)
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
