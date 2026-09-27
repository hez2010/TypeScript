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
            Error(node, 1267, CheckerDiagnostic.DeclarationName(node.Name!));
    }

    private async ValueTask CheckMethodSourceAsync(MethodDeclarationNode node, CancellationToken cancellation)
    {
        await CheckDecoratorsAsync(node, cancellation).ConfigureAwait(false);
        await FunctionDeclarations.GrammarAsync(node, cancellation).ConfigureAwait(false);
        await CheckFunctionDeclarationAsync(node, cancellation).ConfigureAwait(false);
        await CheckFunctionOverloadsAsync(node, cancellation).ConfigureAwait(false);
        await CheckMethodNameAsync(node, cancellation);
        if (node.Name is IdentifierNode { Text: "constructor" } && node.AsteriskToken is not null)
            Error(node.Name, 1368);
        await CheckSourceElementAsync(node.Body, cancellation).ConfigureAwait(false);
        await CheckFunctionPathsAsync(node, cancellation).ConfigureAwait(false);
        await CheckFullSignatureAsync(node, cancellation).ConfigureAwait(false);
        if (node.Type is null && (node.Body is null || node.Body.Pos == node.Body.End)
            && !((node.Flags & NodeFlags.Ambient) != 0
                && (SemanticSyntax.HasModifier(node, SyntaxKind.PrivateKeyword) || node.Name is PrivateIdentifierNode)))
            await ReportImplicitAnyAsync(node, context.AnyType, cancellation).ConfigureAwait(false);
        if (SemanticSyntax.HasModifier(node, SyntaxKind.AbstractKeyword) && node.Body is not null)
            Error(node, 1245, CheckerDiagnostic.DeclarationName(node.Name!));
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
        if (name is IdentifierNode { Text: "constructor" } && SemanticSyntax.ClassLike(node.Parent))
            Error(name, 1341);
        await CheckMethodNameAsync(node, cancellation);
        var flags = node.Flags | (program.Symbols.Binding(node)?.Get(node)?.Flags ?? 0);
        if (node is GetAccessorDeclarationNode && (flags & NodeFlags.Ambient) == 0 && SemanticSyntax.Body(node) is not null
            && (flags & NodeFlags.HasImplicitReturn) != 0 && (flags & NodeFlags.HasExplicitReturn) == 0)
            Error(name, 2378);
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
                Error(getter.Name!, 2676);
                Error(setter.Name!, 2676);
            }
            if (SemanticSyntax.HasModifier(getter, SyntaxKind.ProtectedKeyword)
                && !SemanticSyntax.HasModifier(setter, SyntaxKind.ProtectedKeyword)
                && !SemanticSyntax.HasModifier(setter, SyntaxKind.PrivateKeyword)
                || SemanticSyntax.HasModifier(getter, SyntaxKind.PrivateKeyword)
                    && !SemanticSyntax.HasModifier(setter, SyntaxKind.PrivateKeyword))
            {
                Error(getter.Name!, 2808);
                Error(setter.Name!, 2808);
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
        if (node.Name is StringLiteralNode { Text: "constructor" })
        {
            Error(node.Name, 18006);
            return;
        }
        if (node.Name is ComputedPropertyNameNode computed
            && computed.Expression is not (StringLiteralNode or NumericLiteralNode or NoSubstitutionTemplateLiteralNode
                or PrefixUnaryExpressionNode { Operator: SyntaxKind.PlusToken or SyntaxKind.MinusToken, Operand: NumericLiteralNode })
            && !LateMembers.LateSyntax(computed))
        {
            Error(node.Name, 1166);
            return;
        }
        if (SemanticSyntax.HasModifier(node, SyntaxKind.AccessorKeyword) && node.PostfixToken?.Kind == SyntaxKind.QuestionToken)
        {
            Error(node.PostfixToken, 1276);
            return;
        }
        await CheckAmbientInitializerAsync(node, cancellation).ConfigureAwait(false);
        if (node.PostfixToken?.Kind == SyntaxKind.ExclamationToken)
        {
            if (node.Initializer is not null)
                Error(node.PostfixToken, 1263);
            else if (node.Type is null)
                Error(node.PostfixToken, 1264);
            else if ((node.Flags & NodeFlags.Ambient) != 0
                || SemanticSyntax.IsStatic(node)
                || SemanticSyntax.HasModifier(node, SyntaxKind.AbstractKeyword))
                Error(node.PostfixToken, 1255);
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
            Error(node, CheckerDiagnostic.Create(node, Messages.X_0_expected, "{") with { Start = node.End - 1, Length = 1 });
            return;
        }
        if (body is not null)
        {
            if (SemanticSyntax.HasModifier(node, SyntaxKind.AbstractKeyword))
            {
                Error(node, 1318);
                return;
            }
            if (node.Parent is TypeLiteralNode or InterfaceDeclarationNode)
            {
                Error(body, 1183);
                return;
            }
        }
        var signature = (IFunctionSignature)node;
        if (signature.TypeParameters is not null)
        {
            Error(name, 1094);
            return;
        }
        bool getter = node is GetAccessorDeclarationNode;
        var parameters = signature.Parameters!;
        bool receiver = parameters.FirstOrDefault() is ParameterDeclarationNode { Name: IdentifierNode { Text: "this" } };
        if (parameters.Count != (getter ? 0 : 1) && !(receiver && parameters.Count == (getter ? 1 : 2)))
        {
            Error(name, getter ? 1054 : 1049);
            return;
        }
        if (getter)
            return;
        if (signature.Type is not null)
        {
            Error(name, 1095);
            return;
        }
        var parameter = (ParameterDeclarationNode)parameters[^1];
        if (parameter.DotDotDotToken is not null)
            Error(parameter.DotDotDotToken, 1053);
        else if (parameter.QuestionToken is not null)
            Error(parameter.QuestionToken, 1051);
        else if (parameter.Initializer is not null)
            Error(name, 1052);
    }

    private Symbol OriginalProperty(Symbol symbol) =>
        (symbol.CheckFlags & CheckFlags.Instantiated) != 0 ? links.Values.Get(symbol).Target! : symbol;

    private async ValueTask CheckClassOverridesAsync(SyntaxNode node, InterfaceType type, Type baseType, CancellationToken cancellation)
    {
        var missing = new List<string>();
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
                    Error(at, 2610, TypeDisplay.SymbolName(original), await TypeDisplay.GetAsync(baseType, cancellation),
                        await TypeDisplay.GetAsync(type, cancellation));
                else if (baseFlags == SymbolFlags.Property && derivedFlags != SymbolFlags.Property)
                    Error(at, 2611, TypeDisplay.SymbolName(original), await TypeDisplay.GetAsync(baseType, cancellation),
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
                        Error(at, 2612, TypeDisplay.SymbolName(original), await TypeDisplay.GetAsync(baseType, cancellation));
                }
            }
            else if ((original.Flags & SymbolFlags.Method) != 0 || (original.CheckFlags & CheckFlags.SyntheticMethod) != 0)
            {
                if ((derived.Flags & (SymbolFlags.Method | SymbolFlags.Property)) == 0
                    && (derived.CheckFlags & CheckFlags.SyntheticMethod) == 0)
                    Error(at, 2423, await TypeDisplay.GetAsync(baseType, cancellation), TypeDisplay.SymbolName(original),
                        await TypeDisplay.GetAsync(type, cancellation));
            }
            else
                Error(at, (original.Flags & SymbolFlags.Accessor) != 0 ? 2426 : 2425,
                    await TypeDisplay.GetAsync(baseType, cancellation), TypeDisplay.SymbolName(original),
                    await TypeDisplay.GetAsync(type, cancellation));
        }
        if (missing.Count != 0)
        {
            bool expression = node is ClassExpressionNode;
            string baseName = await TypeDisplay.GetAsync(baseType, cancellation);
            var arguments = new List<string>();
            if (!expression)
                arguments.Add(await TypeDisplay.GetAsync(type, cancellation));
            if (missing.Count == 1)
                arguments.AddRange([missing[0], baseName]);
            else
            {
                arguments.Add(baseName);
                arguments.Add(string.Join(", ", (missing.Count > 5 ? missing.Take(4) : missing).Select(name => "'" + name + "'")));
                if (missing.Count > 5)
                    arguments.Add((missing.Count - 4).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            Error(node, expression
                ? missing.Count == 1 ? 2653 : missing.Count > 5 ? 2650 : 2656
                : missing.Count == 1 ? 2515 : missing.Count > 5 ? 2655 : 2654, arguments.ToArray());
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
                if (!hasOverride && program.Symbols.Program.Configuration.Options.Boolean("noImplicitOverride") != true)
                    continue;
                if (hasOverride && symbol.Name == Symbol.InternalPrefix + "computed")
                {
                    Error(member, isJs ? 4128 : 4127);
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
                    Error(member, suggestion is null ? isJs ? 4122 : 4113 : isJs ? 4123 : 4117,
                        suggestion is null ? [await TypeDisplay.GetAsync(baseWithThis, cancellation)]
                            : [await TypeDisplay.GetAsync(baseWithThis, cancellation), TypeDisplay.SymbolName(suggestion)]);
                }
                if (property is not null
                    && inherited is { Declarations.Count: > 0 }
                    && !hasOverride
                    && (node.Flags & NodeFlags.Ambient) == 0
                    && program.Symbols.Program.Configuration.Options.Boolean("noImplicitOverride") == true)
                {
                    if (!inherited.Declarations.Any(d => SemanticSyntax.HasModifier(d, SyntaxKind.AbstractKeyword)))
                        Error(member, ParameterProperty(member) ? isJs ? 4120 : 4115 : isJs ? 4119 : 4114,
                            await TypeDisplay.GetAsync(baseWithThis, cancellation));
                    else if (SemanticSyntax.HasModifier(member, SyntaxKind.AbstractKeyword))
                        Error(member, 4116, await TypeDisplay.GetAsync(baseWithThis, cancellation));
                }
            }
    }
}
