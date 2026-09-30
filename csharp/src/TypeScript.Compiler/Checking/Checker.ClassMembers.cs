using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private bool ClassMemberModifiers(SyntaxNode node) => DeclarationModifiers(node);

    private async ValueTask CheckPropertySourceAsync(PropertyDeclarationNode node, CancellationToken cancellation)
    {
        bool modifierError = ClassMemberModifiers(node);
        await CheckDecoratorsAsync(node, cancellation).ConfigureAwait(false);
        if (!modifierError)
            await PropertyGrammarAsync(node, cancellation).ConfigureAwait(false);
        if (node.Name is ComputedPropertyNameNode computed)
            await ComputedNameAsync(computed, cancellation).ConfigureAwait(false);
        await FunctionDeclarations.VariableAsync(node, cancellation).ConfigureAwait(false);
        if (SemanticSyntax.HasModifier(node, SyntaxKind.AbstractKeyword) && node.Initializer is not null)
            Error(
                node,
                DiagnosticCode.Property0CannotHaveAnInitializerBecauseItIsMarkedAbstract,
                CheckerDiagnostic.DeclarationName(node.Name!));
    }

    private async ValueTask CheckMethodSourceAsync(MethodDeclarationNode node, CancellationToken cancellation)
    {
        await CheckDecoratorsAsync(node, cancellation).ConfigureAwait(false);
        await FunctionDeclarations.GrammarAsync(node, cancellation).ConfigureAwait(false);
        await CheckFunctionDeclarationAsync(node, cancellation).ConfigureAwait(false);
        await CheckFunctionOverloadsAsync(node, cancellation).ConfigureAwait(false);
        await CheckMethodNameAsync(node, cancellation);
        if (node.Name is IdentifierNode { Text.Span: var matchedText } && matchedText.SequenceEqual("constructor"u8) && node.AsteriskToken is not null)
            Error(node.Name, DiagnosticCode.ClassConstructorMayNotBeAGenerator);
        await CheckSourceElementAsync(node.Body, cancellation).ConfigureAwait(false);
        await CheckFunctionPathsAsync(node, cancellation).ConfigureAwait(false);
        await CheckFullSignatureAsync(node, cancellation).ConfigureAwait(false);
        if (node.Type is null && (node.Body is null || node.Body.Pos == node.Body.End)
            && !((node.Flags & NodeFlags.Ambient) != 0
                && (SemanticSyntax.HasModifier(node, SyntaxKind.PrivateKeyword) || node.Name is PrivateIdentifierNode)))
            await ReportImplicitAnyAsync(node, context.AnyType, cancellation).ConfigureAwait(false);
        if (SemanticSyntax.HasModifier(node, SyntaxKind.AbstractKeyword) && node.Body is not null)
            Error(
                node,
                DiagnosticCode.Method0CannotHaveAnImplementationBecauseItIsMarkedAbstract,
                CheckerDiagnostic.DeclarationName(node.Name!));
        if (node.Type is null && node.Body is not null && SemanticSyntax.Generator(node))
            await Signatures.ReturnAsync(
                await Signatures.FromDeclarationAsync(node, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);
    }

    private async ValueTask CheckAccessorSourceAsync(SyntaxNode node, CancellationToken cancellation)
    {
        await CheckDecoratorsAsync(node, cancellation).ConfigureAwait(false);
        if (!await FunctionDeclarations.GrammarAsync(node, cancellation).ConfigureAwait(false))
            AccessorGrammar(node);
        await CheckFunctionDeclarationAsync(node, cancellation).ConfigureAwait(false);
        var name = SemanticSyntax.Name(node)!;
        if (name is IdentifierNode { Text.Span: var matchedText2 } && matchedText2.SequenceEqual("constructor"u8) && SemanticSyntax.ClassLike(node.Parent))
            Error(name, DiagnosticCode.ClassConstructorMayNotBeAnAccessor);
        await CheckMethodNameAsync(node, cancellation);
        var flags = node.Flags | (program.Symbols.Binding(node)?.Get(node)?.Flags ?? 0);
        if (node is GetAccessorDeclarationNode && (flags & NodeFlags.Ambient) == 0 && SemanticSyntax.Body(node) is not null
            && (flags & NodeFlags.HasImplicitReturn) != 0 && (flags & NodeFlags.HasExplicitReturn) == 0)
            Error(name, DiagnosticCode.AGetAccessorMustReturnAValue);
        var symbol = program.Symbols.Declaration(node)!;
        var getter = symbol.Declarations.OfType<GetAccessorDeclarationNode>().FirstOrDefault();
        var setter = symbol.Declarations.OfType<SetAccessorDeclarationNode>().FirstOrDefault();
        if (getter is not null && setter is not null && (links.Nodes.Get(getter).Flags & NodeCheckFlags.TypeChecked) == 0)
        {
            links.Nodes.Get(getter).Flags |= NodeCheckFlags.TypeChecked;
            if (SemanticSyntax.HasModifier(
                getter,
                SyntaxKind.AbstractKeyword) != SemanticSyntax.HasModifier(setter, SyntaxKind.AbstractKeyword))
            {
                Error(getter.Name!, DiagnosticCode.AccessorsMustBothBeAbstractOrNonAbstract);
                Error(setter.Name!, DiagnosticCode.AccessorsMustBothBeAbstractOrNonAbstract);
            }
            if (SemanticSyntax.HasModifier(getter, SyntaxKind.ProtectedKeyword)
                && !SemanticSyntax.HasModifier(setter, SyntaxKind.ProtectedKeyword)
                && !SemanticSyntax.HasModifier(setter, SyntaxKind.PrivateKeyword)
                || SemanticSyntax.HasModifier(getter, SyntaxKind.PrivateKeyword)
                    && !SemanticSyntax.HasModifier(setter, SyntaxKind.PrivateKeyword))
            {
                Error(getter.Name!, DiagnosticCode.AGetAccessorMustBeAtLeastAsAccessibleAsTheSetter);
                Error(setter.Name!, DiagnosticCode.AGetAccessorMustBeAtLeastAsAccessibleAsTheSetter);
            }
        }
        await Values.GetAsync(symbol, cancellation).ConfigureAwait(false);
        if (node is GetAccessorDeclarationNode)
            await CheckFunctionPathsAsync(node, cancellation).ConfigureAwait(false);
        await CheckSourceElementAsync(SemanticSyntax.Body(node), cancellation).ConfigureAwait(false);
    }

    private async ValueTask PropertyGrammarAsync(PropertyDeclarationNode node, CancellationToken cancellation)
    {
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0)
            return;
        if (MappedMemberGrammar(node))
            return;
        if (node.Name is StringLiteralNode { Text.Span: var matchedText3 } && matchedText3.SequenceEqual("constructor"u8))
        {
            Error(node.Name, DiagnosticCode.ClassesMayNotHaveAFieldNamedConstructor);
            return;
        }
        if (node.Name is ComputedPropertyNameNode computed
            && computed.Expression is not (StringLiteralNode or NumericLiteralNode or NoSubstitutionTemplateLiteralNode
                or PrefixUnaryExpressionNode { Operator: SyntaxKind.PlusToken or SyntaxKind.MinusToken, Operand: NumericLiteralNode })
            && !LateMembers.LateSyntax(computed))
        {
            Error(node.Name, DiagnosticCode.AComputedPropertyNameInAClassPropertyDeclarationMustHaveASimpleLiteralTypeOrAUniqueSymbolType);
            return;
        }
        if (SemanticSyntax.HasModifier(node, SyntaxKind.AccessorKeyword) && node.PostfixToken?.Kind == SyntaxKind.QuestionToken)
        {
            Error(node.PostfixToken, DiagnosticCode.AnAccessorPropertyCannotBeDeclaredOptional);
            return;
        }
        await CheckAmbientInitializerAsync(node, cancellation).ConfigureAwait(false);
        if (node.PostfixToken?.Kind == SyntaxKind.ExclamationToken)
        {
            if (node.Initializer is not null)
                Error(node.PostfixToken, DiagnosticCode.DeclarationsWithInitializersCannotAlsoHaveDefiniteAssignmentAssertions);
            else if (node.Type is null)
                Error(node.PostfixToken, DiagnosticCode.DeclarationsWithDefiniteAssignmentAssertionsMustAlsoHaveTypeAnnotations);
            else if ((node.Flags & NodeFlags.Ambient) != 0
                || SemanticSyntax.IsStatic(node)
                || SemanticSyntax.HasModifier(node, SyntaxKind.AbstractKeyword))
                Error(node.PostfixToken, DiagnosticCode.ADefiniteAssignmentAssertionIsNotPermittedInThisContext);
        }
    }

    private void AccessorGrammar(SyntaxNode node)
    {
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0)
            return;
        var body = SemanticSyntax.Body(node);
        var name = SemanticSyntax.Name(node)!;
        if ((node.Flags & NodeFlags.Ambient) == 0 && node.Parent is not TypeLiteralNode and not InterfaceDeclarationNode && body is null
            && !SemanticSyntax.HasModifier(node, SyntaxKind.AbstractKeyword))
        {
            Error(node, CheckerDiagnostic.Create(node, Messages.X_0_expected, Utf8Literals.OpenBrace) with { Start = node.End - 1, Length = 1 });
            return;
        }
        if (body is not null)
        {
            if (SemanticSyntax.HasModifier(node, SyntaxKind.AbstractKeyword))
            {
                Error(node, DiagnosticCode.AnAbstractAccessorCannotHaveAnImplementation);
                return;
            }
            if (node.Parent is TypeLiteralNode or InterfaceDeclarationNode)
            {
                Error(body, DiagnosticCode.AnImplementationCannotBeDeclaredInAmbientContexts);
                return;
            }
        }
        var signature = (IFunctionSignature)node;
        if (signature.TypeParameters is not null)
        {
            Error(name, DiagnosticCode.AnAccessorCannotHaveTypeParameters);
            return;
        }
        bool getter = node is GetAccessorDeclarationNode;
        var parameters = signature.Parameters!;
        bool receiver = parameters.FirstOrDefault() is ParameterDeclarationNode { Name: IdentifierNode { Text.Span: var matchedText4 } } && matchedText4.SequenceEqual("this"u8);
        if (parameters.Count != (getter ? 0 : 1) && !(receiver && parameters.Count == (getter ? 1 : 2)))
        {
            Error(name, getter ? DiagnosticCode.AGetAccessorCannotHaveParameters : DiagnosticCode.ASetAccessorMustHaveExactlyOneParameter);
            return;
        }
        if (getter)
            return;
        if (signature.Type is not null)
        {
            Error(name, DiagnosticCode.ASetAccessorCannotHaveAReturnTypeAnnotation);
            return;
        }
        var parameter = (ParameterDeclarationNode)parameters[^1];
        if (parameter.DotDotDotToken is not null)
            Error(parameter.DotDotDotToken, DiagnosticCode.ASetAccessorCannotHaveRestParameter);
        else if (parameter.QuestionToken is not null)
            Error(parameter.QuestionToken, DiagnosticCode.ASetAccessorCannotHaveAnOptionalParameter);
        else if (parameter.Initializer is not null)
            Error(name, DiagnosticCode.ASetAccessorParameterCannotHaveAnInitializer);
    }

    private Symbol OriginalProperty(Symbol symbol) =>
        (symbol.CheckFlags & CheckFlags.Instantiated) != 0 ? links.Values.Get(symbol).Target! : symbol;

    private async ValueTask CheckClassOverridesAsync(SyntaxNode node, InterfaceType type, Type baseType, CancellationToken cancellation)
    {
        var missing = new List<Utf8String>();
        foreach (var inherited in await Properties.GetAsync(baseType, cancellation).ConfigureAwait(false))
        {
            var original = OriginalProperty(inherited);
            if ((original.Flags & SymbolFlags.Prototype) != 0)
                continue;
            if (await Properties.PropertyAsync(type, original.Name, cancellation: cancellation).ConfigureAwait(false) is not { } candidate)
                continue;
            var derived = OriginalProperty(candidate);
            bool abstractBase = original.Declarations.Any(d => SemanticSyntax.HasModifier(d, SyntaxKind.AbstractKeyword));
            if (derived == original)
            {
                if (abstractBase && !SemanticSyntax.HasModifier(node, SyntaxKind.AbstractKeyword))
                {
                    bool implementedElsewhere = false;
                    foreach (var other in await Bases.GetAsync(type, cancellation).ConfigureAwait(false))
                        if (other != baseType
                            && await Properties.PropertyAsync(
                                other,
                                original.Name,
                                cancellation: cancellation).ConfigureAwait(false) is { } property
                            && OriginalProperty(property) != original)
                        {
                            implementedElsewhere = true;
                            break;
                        }
                    if (!implementedElsewhere)
                        missing.Add(TypeDisplay.SymbolName(original));
                }
                continue;
            }
            if (original.Declarations.Any(d => SemanticSyntax.HasModifier(d, SyntaxKind.PrivateKeyword))
                || derived.Declarations.Any(d => SemanticSyntax.HasModifier(d, SyntaxKind.PrivateKeyword)))
                continue;
            var baseFlags = original.Flags & SymbolFlags.PropertyOrAccessor;
            var derivedFlags = derived.Flags & SymbolFlags.PropertyOrAccessor;
            var at = SemanticSyntax.Name(derived.ValueDeclaration) ?? derived.ValueDeclaration!;
            if (baseFlags != 0 && derivedFlags != 0)
            {
                bool AbstractOrInterface(SyntaxNode d) =>
                    d.Parent is InterfaceDeclarationNode
                        || abstractBase && (d is not PropertyDeclarationNode || ((PropertyDeclarationNode)d).Initializer is null);
                bool abstractOrInterface = (original.CheckFlags & CheckFlags.Synthetic) != 0
                    ? original.Declarations.Any(AbstractOrInterface)
                    : original.Declarations.All(AbstractOrInterface);
                if ((original.CheckFlags & CheckFlags.Mapped) != 0
                    || derived.ValueDeclaration is BinaryExpressionNode
                    || abstractOrInterface)
                    continue;
                if (baseFlags != SymbolFlags.Property && derivedFlags == SymbolFlags.Property)
                    Error(
                        at,
                        DiagnosticCode.X0IsDefinedAsAnAccessorInClass1ButIsOverriddenHereIn2AsAnInstanceProperty,
                        TypeDisplay.SymbolName(original),
                        await TypeDisplay.GetAsync(baseType, cancellation),
                        await TypeDisplay.GetAsync(type, cancellation));
                else if (baseFlags == SymbolFlags.Property && derivedFlags != SymbolFlags.Property)
                    Error(
                        at,
                        DiagnosticCode.X0IsDefinedAsAPropertyInClass1ButIsOverriddenHereIn2AsAnAccessor,
                        TypeDisplay.SymbolName(original),
                        await TypeDisplay.GetAsync(baseType, cancellation),
                        await TypeDisplay.GetAsync(type, cancellation));
                else if (UseDefineForClassFields && (derived.Flags & SymbolFlags.Transient) == 0 && !abstractBase
                    && !derived.Declarations.Any(
                        d => SemanticSyntax.HasModifier(d, SyntaxKind.AbstractKeyword) || (d.Flags & NodeFlags.Ambient) != 0)
                    && derived.Declarations.OfType<PropertyDeclarationNode>().FirstOrDefault(p => p.Initializer is null) is { } uninitialized)
                {
                    var constructor = PropertyInitialization.Constructor(node);
                    if (uninitialized.PostfixToken?.Kind == SyntaxKind.ExclamationToken
                        || constructor is null
                        || uninitialized.Name is not IdentifierNode
                        || !context.StrictNullChecks
                        || !await PropertyInitializers.AssignedAsync(
                            uninitialized.Name,
                            type,
                            constructor,
                            cancellation).ConfigureAwait(false))
                        Error(
                            at,
                            DiagnosticCode.Property0WillOverwriteTheBasePropertyIn1IfThisIsIntentionalAddAnInitializerOtherwiseAddADeclareModifierOrRemoveTheRedundantDeclaration,
                            TypeDisplay.SymbolName(original),
                            await TypeDisplay.GetAsync(baseType, cancellation));
                }
            }
            else if ((original.Flags & SymbolFlags.Method) != 0 || (original.CheckFlags & CheckFlags.SyntheticMethod) != 0)
            {
                if ((derived.Flags & (SymbolFlags.Method | SymbolFlags.Property)) == 0
                    && (derived.CheckFlags & CheckFlags.SyntheticMethod) == 0)
                    Error(
                        at,
                        DiagnosticCode.Class0DefinesInstanceMemberFunction1ButExtendedClass2DefinesItAsInstanceMemberAccessor,
                        await TypeDisplay.GetAsync(baseType, cancellation),
                        TypeDisplay.SymbolName(original),
                        await TypeDisplay.GetAsync(type, cancellation));
            }
            else
                Error(
                    at,
                    (original.Flags & SymbolFlags.Accessor) != 0
                        ? DiagnosticCode.Class0DefinesInstanceMemberAccessor1ButExtendedClass2DefinesItAsInstanceMemberFunction
                        : DiagnosticCode.Class0DefinesInstanceMemberProperty1ButExtendedClass2DefinesItAsInstanceMemberFunction,
                    await TypeDisplay.GetAsync(baseType, cancellation), TypeDisplay.SymbolName(original),
                    await TypeDisplay.GetAsync(type, cancellation));
        }
        if (missing.Count != 0)
        {
            bool expression = node is ClassExpressionNode;
            Utf8String baseName = await TypeDisplay.GetAsync(baseType, cancellation);
            var arguments = new List<Utf8String>();
            if (!expression)
                arguments.Add(await TypeDisplay.GetAsync(type, cancellation));
            if (missing.Count == 1)
                arguments.AddRange([missing[0], baseName]);
            else
            {
                arguments.Add(baseName);
                arguments.Add(Utf8String.Join(", "u8, (missing.Count > 5 ? missing.Take(4) : missing).Select(name => Utf8String.Concat("'"u8, name, "'"u8))));
                if (missing.Count > 5)
                    arguments.Add(Utf8String.Format(missing.Count - 4));
            }
            Error(node, expression
                ? missing.Count == 1
                    ? DiagnosticCode.NonAbstractClassExpressionDoesNotImplementInheritedAbstractMember0FromClass1
                    : missing.Count > 5
                        ? DiagnosticCode.NonAbstractClassExpressionIsMissingImplementationsForTheFollowingMembersOf0Colon1And2More
                        : DiagnosticCode.NonAbstractClassExpressionIsMissingImplementationsForTheFollowingMembersOf0Colon1
                : missing.Count == 1
                    ? DiagnosticCode.NonAbstractClass0DoesNotImplementInheritedAbstractMember1FromClass2
                    : missing.Count > 5
                        ? DiagnosticCode.NonAbstractClass0IsMissingImplementationsForTheFollowingMembersOf1Colon2And3More
                        : DiagnosticCode.NonAbstractClass0IsMissingImplementationsForTheFollowingMembersOf1Colon2,
                arguments.ToArray());
        }

        var staticType = await Values.GetAsync(type.Symbol!, cancellation).ConfigureAwait(false);
        var staticBase = await ClassBases.ConstructorAsync(type, cancellation).ConfigureAwait(false);
        var baseWithThis = await Bases.WithThisAsync(baseType, type.ThisType, cancellation: cancellation).ConfigureAwait(false);
        foreach (var item in PropertyInitialization.Members(node))
            foreach (var member in item is ConstructorDeclarationNode constructor
                ? constructor.Parameters!.Where(ParameterProperty)
                : [item])
            {
                if (SemanticSyntax.HasModifier(member, SyntaxKind.DeclareKeyword) || program.Symbols.Declaration(member) is not { } symbol)
                    continue;
                bool hasOverride = SemanticSyntax.HasModifier(member, SyntaxKind.OverrideKeyword);
                bool isJs = (node.Flags & NodeFlags.JavaScriptFile) != 0;
                if (!hasOverride && program.Symbols.Program.Configuration.Options.NoImplicitOverride != true)
                    continue;
                if (hasOverride && symbol.Name == Symbol.InternalComputed)
                {
                    Error(
                        member,
                        isJs
                            ? DiagnosticCode.ThisMemberCannotHaveAJSDocCommentWithAnOverrideTagBecauseItsNameIsDynamic
                            : DiagnosticCode.ThisMemberCannotHaveAnOverrideModifierBecauseItsNameIsDynamic);
                    continue;
                }
                var thisType = SemanticSyntax.IsStatic(member) ? staticType : type;
                var inheritedType = SemanticSyntax.IsStatic(member) ? staticBase : baseWithThis;
                var property = await Properties.PropertyAsync(thisType, symbol.Name, cancellation: cancellation).ConfigureAwait(false);
                var inherited = await Properties.PropertyAsync(
                    inheritedType,
                    symbol.Name,
                    cancellation: cancellation).ConfigureAwait(false);
                if (property is not null && inherited is null && hasOverride)
                {
                    var suggestion = await SymbolSuggestions.FindAsync(
                        symbol.Name,
                        await Properties.GetAsync(inheritedType, cancellation).ConfigureAwait(false),
                        SymbolFlags.ClassMember,
                        cancellation).ConfigureAwait(false);
                    Error(
                        member,
                        suggestion is null
                            ? isJs
                                ? DiagnosticCode.ThisMemberCannotHaveAJSDocCommentWithAnOverrideTagBecauseItIsNotDeclaredInTheBaseClass0
                                : DiagnosticCode.ThisMemberCannotHaveAnOverrideModifierBecauseItIsNotDeclaredInTheBaseClass0
                            : isJs
                                ? DiagnosticCode.ThisMemberCannotHaveAJSDocCommentWithAnOverrideTagBecauseItIsNotDeclaredInTheBaseClass0DidYouMean1
                                : DiagnosticCode.ThisMemberCannotHaveAnOverrideModifierBecauseItIsNotDeclaredInTheBaseClass0DidYouMean1,
                        suggestion is null ? [await TypeDisplay.GetAsync(baseWithThis, cancellation)]
                            : [await TypeDisplay.GetAsync(baseWithThis, cancellation), TypeDisplay.SymbolName(suggestion)]);
                }
                if (property is not null
                    && inherited is { Declarations.Length: > 0 }
                    && !hasOverride
                    && (node.Flags & NodeFlags.Ambient) == 0
                    && program.Symbols.Program.Configuration.Options.NoImplicitOverride == true)
                {
                    if (!inherited.Declarations.Any(d => SemanticSyntax.HasModifier(d, SyntaxKind.AbstractKeyword)))
                        Error(
                            member,
                            ParameterProperty(member)
                                ? isJs
                                    ? DiagnosticCode.ThisParameterPropertyMustHaveAJSDocCommentWithAnOverrideTagBecauseItOverridesAMemberInTheBaseClass0
                                    : DiagnosticCode.ThisParameterPropertyMustHaveAnOverrideModifierBecauseItOverridesAMemberInBaseClass0
                                : isJs
                                    ? DiagnosticCode.ThisMemberMustHaveAJSDocCommentWithAnOverrideTagBecauseItOverridesAMemberInTheBaseClass0
                                    : DiagnosticCode.ThisMemberMustHaveAnOverrideModifierBecauseItOverridesAMemberInTheBaseClass0,
                            await TypeDisplay.GetAsync(baseWithThis, cancellation));
                    else if (SemanticSyntax.HasModifier(member, SyntaxKind.AbstractKeyword))
                        Error(
                            member,
                            DiagnosticCode.ThisMemberMustHaveAnOverrideModifierBecauseItOverridesAnAbstractMethodThatIsDeclaredInTheBaseClass0,
                            await TypeDisplay.GetAsync(baseWithThis, cancellation));
                }
            }
    }
}
