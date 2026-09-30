using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private async ValueTask CheckClassSourceAsync(SyntaxNode node, bool expression, CancellationToken cancellation)
    {
        if (!expression && SemanticSyntax.Name(node) is null && !SemanticSyntax.HasModifier(node, SyntaxKind.DefaultKeyword)
            && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
            Error(node, DiagnosticCode.AClassDeclarationWithoutTheDefaultModifierMustHaveAName);
        if (!CheckClassModifiers(node))
        {
            HeritageGrammar(
                node,
                node is ClassDeclarationNode classNode ? classNode.HeritageClauses : ((ClassExpressionNode)node).HeritageClauses);
            EmptyTypeListError(
                node,
                node is ClassDeclarationNode c ? c.TypeParameters : ((ClassExpressionNode)node).TypeParameters,
                DiagnosticCode.TypeParameterListCannotBeEmpty);
        }
        CheckDeclarationName(node);
        MarkPrivateIdentifierScopes(node);
        ExportedDeclaration(node, true);
        await CheckDecoratorsAsync(node, cancellation).ConfigureAwait(false);
        if (!expression)
            await CheckMergedExportsAsync(node, cancellation).ConfigureAwait(false);
        if (SemanticSyntax.Name(node) is IdentifierNode name && ReservedTypeName(name.Text))
            Error(name, DiagnosticCode.ClassNameCannotBe0, name.Text);
        var parameters = node is ClassDeclarationNode declaration ? declaration.TypeParameters : ((ClassExpressionNode)node).TypeParameters;
        if (parameters is not null)
            foreach (TypeParameterDeclarationNode parameter in parameters)
                await FunctionDeclarations.TypeParameterAsync(parameter, cancellation).ConfigureAwait(false);
        var symbol = program.Symbols.Declaration(node)!;
        var type = (InterfaceType)await Declared.GetAsync(symbol, cancellation).ConfigureAwait(false);
        var withThis = await Bases.WithThisAsync(type, null, cancellation: cancellation).ConfigureAwait(false);
        var staticType = await Values.GetAsync(symbol, cancellation).ConfigureAwait(false);
        await CheckMergedTypeParametersAsync(symbol, type, cancellation).ConfigureAwait(false);
        await CheckOverloadDeclarationsAsync(symbol, cancellation).ConfigureAwait(false);
        await CheckClassDuplicatesAsync(node, cancellation);
        var baseNode = ClassBases.BaseNode(type);
        if (baseNode is not null)
        {
            if (baseNode.TypeArguments is not null)
                foreach (var argument in baseNode.TypeArguments)
                    await CheckedFunctionTypeAsync(argument, cancellation).ConfigureAwait(false);
            var bases = await Bases.GetAsync(type, cancellation).ConfigureAwait(false);
            if (bases.Count != 0)
            {
                var baseType = bases[0];
                await CheckDocumentationBaseAsync(node, baseNode, baseType, cancellation).ConfigureAwait(false);
                var constructor = await ClassBases.ConstructorAsync(type, cancellation).ConfigureAwait(false);
                var staticBase = await Views.ApparentAsync(constructor, cancellation).ConfigureAwait(false);
                var constructors = await SignaturesAsync(staticBase, true, cancellation).ConfigureAwait(false);
                foreach (var signature in constructors)
                    if (signature.Declaration is ConstructorDeclarationNode ctor
                        && SemanticSyntax.HasModifier(ctor, SyntaxKind.PrivateKeyword)
                        && DeclarationOrder.Ancestor(node, n => n == ctor.Parent) is null)
                    {
                        Error(
                            baseNode,
                            DiagnosticCode.CannotExtendAClass0ClassConstructorIsMarkedAsPrivate,
                            await FullyQualifiedNameAsync(program.Symbols.Declaration(ctor.Parent!)!, null, cancellation));
                        break;
                    }
                if (baseNode.TypeArguments is { Count: > 0 })
                    foreach (var signature in constructors)
                        if (CallSignatures.TypeArity(signature, baseNode.TypeArguments)
                            && await CallSignatures.TypeArgumentsAsync(
                                signature,
                                baseNode.TypeArguments,
                                true,
                                cancellation).ConfigureAwait(false) is null)
                            break;
                var baseWithThis = await Bases.WithThisAsync(baseType, type.ThisType, cancellation: cancellation).ConfigureAwait(false);
                if (!await AssignableAsync(withThis, baseWithThis, cancellation).ConfigureAwait(false))
                    await ClassMemberErrorsAsync(
                        node,
                        withThis,
                        baseWithThis,
                        DiagnosticCode.Class0IncorrectlyExtendsBaseClass1,
                        cancellation).ConfigureAwait(false);
                else
                    await RelationDiagnostics.CheckAsync(
                        staticType,
                        await WithoutSignaturesAsync(staticBase, cancellation).ConfigureAwait(false),
                    RelationKind.Assignable,
                        SemanticSyntax.Name(node) ?? node,
                        null,
                        DiagnosticCode.ClassStaticSide0IncorrectlyExtendsBaseClassStaticSide1,
                        cancellation).ConfigureAwait(false);
                if ((constructor.Flags & TypeFlags.TypeVariable) != 0)
                {
                    if (!await Composites.MixinAsync(
                        await SignaturesAsync(staticType, true, cancellation).ConfigureAwait(false),
                        cancellation).ConfigureAwait(false))
                        Error(
                            SemanticSyntax.Name(node) ?? node,
                            DiagnosticCode.AMixinClassMustHaveAConstructorWithASingleRestParameterOfTypeAny);
                    else if (constructors.Any(s => (s.Flags & SignatureFlags.Abstract) != 0)
                        && !SemanticSyntax.HasModifier(node, SyntaxKind.AbstractKeyword))
                        Error(
                            node,
                            DiagnosticCode.AMixinClassThatExtendsFromATypeVariableContainingAnAbstractConstructSignatureMustAlsoBeDeclaredAbstract);
                }
                if (staticBase.Symbol is not { Flags: var flags } || (flags & SymbolFlags.Class) == 0)
                    if ((constructor.Flags & TypeFlags.TypeVariable) == 0)
                        foreach (var signature in await ClassBases.ConstructorsAsync(
                            staticBase,
                            baseNode,
                            cancellation).ConfigureAwait(false))
                            if (!await IdenticalAsync(
                                await Signatures.ReturnAsync(signature, cancellation).ConfigureAwait(false),
                                baseType,
                                cancellation).ConfigureAwait(false))
                            {
                                Error(baseNode.Expression!, DiagnosticCode.BaseConstructorsMustAllHaveTheSameReturnType);
                                break;
                            }
                await CheckClassOverridesAsync(node, type, baseType, cancellation).ConfigureAwait(false);
            }
        }
        else if (PropertyInitialization.Members(node).Any(m => SemanticSyntax.HasModifier(m, SyntaxKind.OverrideKeyword)))
            foreach (var member in PropertyInitialization.Members(node).Where(
                m => SemanticSyntax.HasModifier(m, SyntaxKind.OverrideKeyword)))
                Error(
                    SemanticSyntax.Name(member) ?? member,
                    (node.Flags & NodeFlags.JavaScriptFile) != 0
                        ? DiagnosticCode.ThisMemberCannotHaveAJSDocCommentWithAnOverrideTagBecauseItsContainingClass0DoesNotExtendAnotherClass
                        : DiagnosticCode.ThisMemberCannotHaveAnOverrideModifierBecauseItsContainingClass0DoesNotExtendAnotherClass,
                    await TypeDisplay.GetAsync(type, cancellation));
        foreach (HeritageClauseNode clause in ((IEnumerable<SyntaxNode>?)(node is ClassDeclarationNode c
            ? c.HeritageClauses
            : ((ClassExpressionNode)node).HeritageClauses) ?? []).OfType<HeritageClauseNode>()
                .Where(h => h.Token == SyntaxKind.ImplementsKeyword).Take(1))
            if (clause.Token == SyntaxKind.ImplementsKeyword)
                foreach (var reference in clause.Types!)
                {
                    if (TypeReferences.Arguments(reference) is { } arguments)
                        foreach (var argument in arguments)
                            await CheckedFunctionTypeAsync(argument, cancellation).ConfigureAwait(false);
                    if (reference is ExpressionWithTypeArgumentsNode heritage
                        && (!ConstantEvaluator.EntityName(heritage.Expression!)
                            || (heritage.Expression!.Flags & NodeFlags.OptionalChain) != 0))
                        Error(
                            heritage.Expression!,
                            DiagnosticCode.AClassCanOnlyImplementAnIdentifierSlashqualifiedNameWithOptionalTypeArguments);
                    await TypeReferenceChecks.CheckAsync(reference, cancellation).ConfigureAwait(false);
                    var target = await Views.ReducedAsync(
                        await Nodes.FromNodeAsync(reference, cancellation).ConfigureAwait(false),
                        cancellation).ConfigureAwait(false);
                    if (target == context.ErrorType)
                        continue;
                    if (!await Bases.ValidAsync(target, cancellation).ConfigureAwait(false))
                    {
                        Error(
                            reference,
                            DiagnosticCode.AClassCanOnlyImplementAnObjectTypeOrIntersectionOfObjectTypesWithStaticallyKnownMembers);
                        continue;
                    }
                    var targetWithThis = await Bases.WithThisAsync(target, type.ThisType, cancellation: cancellation).ConfigureAwait(false);
                    if (!await AssignableAsync(withThis, targetWithThis, cancellation).ConfigureAwait(false))
                        await ClassMemberErrorsAsync(
                            node,
                            withThis,
                            targetWithThis,
                            (target.Symbol?.Flags & SymbolFlags.Class) != 0
                                ? DiagnosticCode.Class0IncorrectlyImplementsClass1DidYouMeanToExtend1AndInheritItsMembersAsASubclass
                                : DiagnosticCode.Class0IncorrectlyImplementsInterface1,
                            cancellation).ConfigureAwait(false);
                }
        await IndexDeclarationChecks.CheckAsync(type, false, cancellation).ConfigureAwait(false);
        if (staticType is StructuredType structured)
            await IndexDeclarationChecks.CheckAsync(structured, true, cancellation).ConfigureAwait(false);
        await IndexDeclarationChecks.DuplicateIndexesAsync(node, cancellation).ConfigureAwait(false);
        await PropertyInitializers.CheckAsync(node, cancellation).ConfigureAwait(false);
        if (expression)
            DeferExpression(node);
        else
        {
            foreach (var member in PropertyInitialization.Members(node))
                await CheckSourceElementAsync(member, cancellation).ConfigureAwait(false);
            RegisterUnused(node);
        }
    }

    private async ValueTask<Type> WithoutSignaturesAsync(Type type, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(
            RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        if (type is ObjectType objectType)
        {
            var resolved = await Members.ResolveAsync(objectType, cancellation).ConfigureAwait(false);
            if (resolved.CallSignatures.Count != 0 || resolved.ConstructSignatures.Count != 0)
            {
                var result = context.NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved, type.Symbol);
                result.Members = resolved.Members;
                result.Properties = resolved.Properties;
                return result;
            }
        }
        else if (type is IntersectionType intersection)
        {
            var parts = new List<Type>();
            foreach (var part in intersection.Types)
                parts.Add(await WithoutSignaturesAsync(part, cancellation).ConfigureAwait(false));
            return await Algebra.IntersectionAsync(parts, cancellation: cancellation).ConfigureAwait(false);
        }
        return type;
    }

    private async ValueTask ClassMemberErrorsAsync(
        SyntaxNode node,
        Type source,
        Type target,
        DiagnosticCode broadCode,
        CancellationToken cancellation)
    {
        bool reported = false;
        foreach (var member in PropertyInitialization.Members(node))
        {
            if (SemanticSyntax.IsStatic(member)
                || program.Symbols.Declaration(member) is not { } symbol
                || symbol.Name == Symbol.InternalComputed)
                continue;
            var property = await Properties.PropertyAsync(source, symbol.Name, cancellation: cancellation).ConfigureAwait(false);
            var inherited = await Properties.PropertyAsync(target, symbol.Name, cancellation: cancellation).ConfigureAwait(false);
            if (property is not null
                && inherited is not null
                && !await AssignableAsync(
                    await Values.GetAsync(property, cancellation).ConfigureAwait(false),
                    await Values.GetAsync(inherited, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false))
            {
                var location = SemanticSyntax.Name(member) ?? member;
                var head = CheckerDiagnostic.Create(location,
                    Messages.Property_0_in_type_1_is_not_assignable_to_the_same_property_in_base_type_2,
                    TypeDisplay.SymbolName(property),
                    await TypeDisplay.GetAsync(source, cancellation),
                    await TypeDisplay.GetAsync(target, cancellation));
                await ReportRelationMessageAsync(
                    location,
                    DiagnosticCode.Type0IsNotAssignableToType1,
                    await Values.GetAsync(property, cancellation),
                    await Values.GetAsync(inherited, cancellation), RelationKind.Assignable, cancellation, head);
                reported = true;
            }
        }
        if (!reported)
            await RelationDiagnostics.CheckAsync(
                source,
                target,
                RelationKind.Assignable,
                SemanticSyntax.Name(node) ?? node,
                null,
                broadCode,
                cancellation).ConfigureAwait(false);
    }

    private bool CheckClassModifiers(SyntaxNode node) => DeclarationModifiers(node);

    private static bool ParameterProperty(SyntaxNode node) => node is ParameterDeclarationNode
        && node is IModifiedNode { Modifiers: { } modifiers }
        && modifiers.Any(
            m => m.Kind is SyntaxKind.PublicKeyword or SyntaxKind.ProtectedKeyword or SyntaxKind.PrivateKeyword
                or SyntaxKind.ReadonlyKeyword or SyntaxKind.OverrideKeyword);

    private async ValueTask CheckClassDuplicatesAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var members = PropertyInitialization.Members(node);
        var seen = new Dictionary<(Utf8String, bool), int>();
        var privateNames = new Dictionary<Utf8String, int>(Utf8StringComparer.Ordinal);
        bool ambient = (node.Flags & NodeFlags.Ambient) != 0;
        foreach (var member in members)
        {
            if (member is ConstructorDeclarationNode constructor)
            {
                foreach (var parameter in constructor.Parameters!.Where(ParameterProperty))
                    Check(parameter, false, 1);
                continue;
            }
            var symbol = program.Symbols.Declaration(member);
            bool @static = SemanticSyntax.IsStatic(member);
            if (!ambient
                && @static
                && symbol?.Name is { } name
                && (name == Utf8Literals.Prototype || !UseDefineForClassFields && (name.Span.SequenceEqual("name"u8) || name.Span.SequenceEqual("length"u8) || name.Span.SequenceEqual("caller"u8) || name.Span.SequenceEqual("arguments"u8))))
                Error(
                    SemanticSyntax.Name(member)!,
                    DiagnosticCode.StaticProperty0ConflictsWithBuiltInPropertyFunction0OfConstructorFunction1,
                    name,
                    program.Symbols.Declaration(node) is { } owner
                    ? await SymbolDisplayNameAsync(owner, null, SymbolFlags.All, cancellation) : Utf8Literals.AnonymousClass);
            Check(
                member,
                @static,
                member is PropertyDeclarationNode && !SemanticSyntax.HasModifier(member, SyntaxKind.AccessorKeyword)
                    ? 1
                    : member is GetAccessorDeclarationNode or SetAccessorDeclarationNode
                        || SemanticSyntax.HasModifier(member, SyntaxKind.AccessorKeyword)
                        ? 2
                        : 0);
            if (symbol is not null && SemanticSyntax.Name(member) is PrivateIdentifierNode)
            {
                int flags = privateNames.GetValueOrDefault(symbol.Name);
                if (flags != 3)
                {
                    flags |= @static ? 2 : 1;
                    privateNames[symbol.Name] = flags;
                    if (flags == 3)
                        foreach (var duplicate in members)
                            if (program.Symbols.Declaration(duplicate)?.Name == symbol.Name)
                                Error(
                                    SemanticSyntax.Name(duplicate)!,
                                    DiagnosticCode.DuplicateIdentifier0StaticAndInstanceElementsCannotShareTheSamePrivateName,
                                    CheckerDiagnostic.DeclarationName(SemanticSyntax.Name(duplicate)!));
                }
            }
        }
        void Check(SyntaxNode member, bool @static, int kind)
        {
            if (kind == 0 || program.Symbols.Declaration(member) is not { Declarations.Length: > 1 } symbol)
                return;
            int prior = seen.GetValueOrDefault((symbol.Name, @static));
            if (prior == 0)
                seen[(symbol.Name, @static)] = kind;
            else if (prior == 1 || prior == 2 && kind != 2)
            {
                foreach (var duplicate in members)
                    if (duplicate is ConstructorDeclarationNode ctor)
                    {
                        foreach (var parameter in ctor.Parameters!.Where(ParameterProperty))
                            if (program.Symbols.Declaration(parameter)?.Name == symbol.Name)
                                DuplicatePropertyError(SemanticSyntax.Name(parameter)!, program.Symbols.Declaration(parameter)!);
                    }
                    else if (program.Symbols.Declaration(duplicate)?.Name == symbol.Name && SemanticSyntax.IsStatic(duplicate) == @static)
                        DuplicatePropertyError(SemanticSyntax.Name(duplicate)!, program.Symbols.Declaration(duplicate)!);
                seen[(symbol.Name, @static)] = 3;
            }
        }
    }

    private async ValueTask CheckConstructorSourceAsync(ConstructorDeclarationNode node, CancellationToken cancellation)
    {
        await FunctionDeclarations.GrammarAsync(node, cancellation).ConfigureAwait(false);
        await CheckFunctionDeclarationAsync(node, cancellation).ConfigureAwait(false);
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
        {
            if (node.TypeParameters is not null)
                ListError(node, node.TypeParameters, DiagnosticCode.TypeParametersCannotAppearOnAConstructorDeclaration);
            if (node.Type is not null)
                Error(node.Type, DiagnosticCode.TypeAnnotationCannotAppearOnAConstructorDeclaration);
        }
        await CheckSourceElementAsync(node.Body, cancellation).ConfigureAwait(false);
        await CheckOverloadDeclarationsAsync(program.Symbols.Declaration(node)!, cancellation).ConfigureAwait(false);
        if (node.Body is null)
            return;
        var type = (InterfaceType)await Declared.GetAsync(program.Symbols.Declaration(node.Parent!)!, cancellation).ConfigureAwait(false);
        if (ClassBases.BaseNode(type) is not { } baseNode)
            return;
        bool extendsNull = baseNode.Expression?.Kind == SyntaxKind.NullKeyword;
        var super = ImmediateNodes(
            node.Body,
            false).OfType<CallExpressionNode>().FirstOrDefault(c => c.Expression?.Kind == SyntaxKind.SuperKeyword);
        if (super is null)
        {
            if (!extendsNull)
                Error(node, DiagnosticCode.ConstructorsForDerivedClassesMustContainASuperCall);
            return;
        }
        if (extendsNull)
            Error(super, DiagnosticCode.AConstructorCannotContainASuperCallWhenItsClassExtendsNull);
        bool rootRequired = !UseDefineForClassFields && (PropertyInitialization.Members(node.Parent!).Any(
            m => SemanticSyntax.Name(m) is PrivateIdentifierNode
            || m is PropertyDeclarationNode { Initializer: not null }
                && !SemanticSyntax.IsStatic(m)) || node.Parameters!.Any(ParameterProperty));
        if (!rootRequired)
            return;
        var parent = super.Parent;
        while (parent is ParenthesizedExpressionNode)
            parent = parent.Parent;
        if (parent is not ExpressionStatementNode || parent.Parent != node.Body)
        {
            Error(
                super,
                DiagnosticCode.ASuperCallMustBeARootLevelStatementWithinAConstructorOfADerivedClassThatContainsInitializedPropertiesParameterPropertiesOrPrivateIdentifiers);
            return;
        }
        bool valid = false;
        foreach (var statement in ((BlockNode)node.Body).Statements!)
        {
            if (statement is ExpressionStatementNode expression
                && MemberAccessRules.SkipParentheses(expression.Expression!) is CallExpressionNode { Expression.Kind: SyntaxKind.SuperKeyword })
            {
                valid = true;
                break;
            }
            if (ImmediateNodes(statement, true).Any(n => n.Kind is SyntaxKind.ThisKeyword or SyntaxKind.SuperKeyword))
                break;
        }
        if (!valid)
            Error(
                node,
                DiagnosticCode.ASuperCallMustBeTheFirstStatementInTheConstructorToReferToSuperOrThisWhenADerivedClassContainsInitializedPropertiesParameterPropertiesOrPrivateIdentifiers);
    }

    private static IEnumerable<SyntaxNode> ImmediateNodes(SyntaxNode root, bool skipProperties)
    {
        var pending = new Stack<SyntaxNode>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (!skipProperties && node is IFunctionSignature
                || skipProperties && (node is ArrowFunctionNode or FunctionDeclarationNode or FunctionExpressionNode
                    or PropertyDeclarationNode
                    || node is BlockNode
                        && node.Parent is ConstructorDeclarationNode or MethodDeclarationNode or GetAccessorDeclarationNode
                            or SetAccessorDeclarationNode))
                continue;
            yield return node;
            for (int i = node.ChildCount - 1; i >= 0; i--)
                pending.Push(node.GetChild(i));
        }
    }
}
