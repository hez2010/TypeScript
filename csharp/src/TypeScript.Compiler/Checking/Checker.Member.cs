using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

// Source-backed member algorithms with explicitly annotated value dependencies.
// Body inference, computed names, class bases and composite members are not fixtures.
internal sealed partial class Checker : ISignatureHost, IStructuredMemberHost, IBaseTypeHost, IIndexSignatureHost, ISymbolTypeHost, IClassBaseHost
{
    internal Signatures Signatures { get; }
    internal BaseTypes Bases { get; }
    internal ClassBases ClassBases { get; }
    internal StructuredMembers Members { get; }
    internal IndexSignatures IndexSignatures { get; }
    internal SymbolTypes Values { get; }
    internal Func<SyntaxNode, CancellationToken, ValueTask<Type>>? ReturnBody { get; set; }
    internal Func<SyntaxNode, CancellationToken, ValueTask<TypePredicate?>>? PredicateBody { get; set; }
    internal Func<Symbol, bool, CancellationToken, ValueTask<Type>>? VariableBody { get; set; }
    internal Func<Symbol, bool>? SensitiveParameter { get; set; }

    public async ValueTask<Signature?> FullSignatureAsync(SyntaxNode declaration, CancellationToken cancellation)
    {
        if ((declaration.Flags & NodeFlags.JavaScriptFile) == 0
            || declaration is not (FunctionDeclarationNode or MethodDeclarationNode or FunctionExpressionNode or ArrowFunctionNode)
            || declaration is not IFullSignatureNode { FullSignature: { } annotation })
            return null;
        var signatures = await SignaturesAsync(
            await Nodes.FromNodeAsync(annotation, cancellation).ConfigureAwait(false),
            false,
            cancellation).ConfigureAwait(false);
        return signatures.Count == 1 ? signatures[0] : null;
    }

    public ValueTask<Type?> ContextualTypeAsync(SyntaxNode declaration, CancellationToken cancellation) =>
        Contexts.GetAsync(declaration, ContextFlags.Signature, cancellation);

    public async ValueTask<bool> BindableNameAsync(SyntaxNode node, CancellationToken cancellation) =>
        (node as INamedNode)?.Name is not ComputedPropertyNameNode
            || program.Symbols.Binding(node)?.Get(node)?.Symbol?.Name is { } name && name != Symbol.InternalPrefix + "computed"
            || await LateMembers.BindableAsync(node, cancellation);

    public ValueTask<Type> ReturnFromBodyAsync(SyntaxNode declaration, CancellationToken cancellation) =>
        ReturnBody?.Invoke(declaration, cancellation) ?? FunctionBodies.ReturnAsync(declaration, cancellation: cancellation);

    public ValueTask<TypePredicate?> PredicateFromBodyAsync(SyntaxNode declaration, CancellationToken cancellation) =>
        PredicateBody?.Invoke(declaration, cancellation) ?? FunctionBodies.PredicateAsync(declaration, cancellation);

    public ValueTask<int> ParameterCountAsync(Signature signature, CancellationToken cancellation) =>
        Parameters.CountAsync(signature, cancellation);

    public void CircularReturn(Signature signature)
    {
        if (signature.Declaration is not { } declaration)
            return;
        if (declaration is ITypedNode { Type: { } annotation })
            Error(annotation, DiagnosticCode.ReturnTypeAnnotationCircularlyReferencesItself);
        else if (NoImplicitAny)
        {
            if ((SemanticSyntax.Name(declaration) ?? AssignedDeclarationName(declaration)) is { } name)
                Error(
                    name,
                    DiagnosticCode.X0ImplicitlyHasReturnTypeAnyBecauseItDoesNotHaveAReturnTypeAnnotationAndIsReferencedDirectlyOrIndirectlyInOneOfItsReturnExpressions,
                    CheckerDiagnostic.DeclarationName(name));
            else
                Error(
                    declaration,
                    DiagnosticCode.FunctionImplicitlyHasReturnTypeAnyBecauseItDoesNotHaveAReturnTypeAnnotationAndIsReferencedDirectlyOrIndirectlyInOneOfItsReturnExpressions);
        }
    }

    private static SyntaxNode? AssignedDeclarationName(SyntaxNode node)
    {
        if (node is not (FunctionExpressionNode or ArrowFunctionNode or ClassExpressionNode))
            return null;
        return node.Parent switch
        {
            PropertyAssignmentNode property => property.Name,
            BindingElementNode element => element.Name,
            VariableDeclarationNode { Name: IdentifierNode name } => name,
            BinaryExpressionNode binary when binary.Right == node => binary.Left switch
            {
                IdentifierNode name => name,
                PropertyAccessExpressionNode access => access.Name,
                ElementAccessExpressionNode access => StripParentheses(access.ArgumentExpression!) is
                    StringLiteralNode or NumericLiteralNode or NoSubstitutionTemplateLiteralNode ? StripParentheses(access.ArgumentExpression!) : null,
                _ => null
            },
            _ => null
        };

        static SyntaxNode StripParentheses(SyntaxNode expression)
        {
            while (expression is ParenthesizedExpressionNode parentheses)
                expression = parentheses.Expression!;
            return expression;
        }
    }

    public ValueTask<IReadOnlyDictionary<TextSlice, Symbol>> ExportsAsync(Symbol symbol, CancellationToken cancellation) =>
        LateMembers.TableAsync(symbol, true, cancellation);

    public ValueTask<IReadOnlyList<IndexInfo>> IndexInfosAsync(
        Symbol symbol,
        IReadOnlyList<Symbol> siblings,
        CancellationToken cancellation) =>
            IndexSignatures.ResolveAsync(symbol, siblings, cancellation);

    public ValueTask<IReadOnlyList<Type>> BaseTypesAsync(InterfaceType type, CancellationToken cancellation) =>
        Bases.GetAsync(type, cancellation);

    public ValueTask<Type> WithThisAsync(Type type, Type argument, CancellationToken cancellation) =>
        Bases.WithThisAsync(type, argument, cancellation: cancellation);

    public ValueTask<IReadOnlyList<Symbol>> PropertiesAsync(Type type, CancellationToken cancellation) =>
        Properties.GetAsync(type, cancellation);

    public async ValueTask<IReadOnlyList<Signature>> SignaturesAsync(Type type, bool construct, CancellationToken cancellation)
    {
        type = await Views.ReducedApparentAsync(type, cancellation);
        if (type is not StructuredType structured)
            return [];
        await Members.ResolveAsync(structured, cancellation);
        return (construct ? structured.ConstructSignatures : structured.CallSignatures) ?? [];
    }

    public async ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation)
    {
        type = await Views.ReducedApparentAsync(type, cancellation);
        return type is StructuredType structured ? (await Members.ResolveAsync(structured, cancellation)).IndexInfos : [];
    }

    public ValueTask<Type> BaseConstructorAsync(InterfaceType type, CancellationToken cancellation) =>
        ClassBases.ConstructorAsync(type, cancellation);

    public ValueTask<IReadOnlyList<Signature>> DefaultConstructorsAsync(InterfaceType type, CancellationToken cancellation) =>
        ClassBases.DefaultsAsync(type, cancellation);

    public ValueTask<Type> DeclaredTypeAsync(Symbol symbol, CancellationToken cancellation) => Declared.GetAsync(symbol, cancellation);

    public ValueTask ResolveUnionAsync(UnionType type, CancellationToken cancellation) => Composites.UnionAsync(type, cancellation);

    public ValueTask ResolveIntersectionAsync(IntersectionType type, CancellationToken cancellation) =>
        Composites.IntersectionAsync(type, cancellation);

    public ValueTask ResolveReverseMappedAsync(ReverseMappedType type, CancellationToken cancellation) =>
        ReverseInference.ResolveAsync(type, cancellation);

    public ValueTask<Type> ReducedAsync(Type type, CancellationToken cancellation) => Views.ReducedAsync(type, cancellation);

    public ValueTask<Type> ApparentAsync(Type type, CancellationToken cancellation) => Views.ApparentAsync(type, cancellation);

    public ValueTask<IReadOnlyList<Type>> ClassBasesAsync(InterfaceType type, CancellationToken cancellation) =>
        ClassBases.GetAsync(type, cancellation);

    public async ValueTask ClassBaseErrorAsync(SyntaxNode node, DiagnosticCode code, Type type, CancellationToken cancellation)
    {
        var displayedType = code == DiagnosticCode.BaseConstructorReturnType0IsNotAnObjectTypeOrIntersectionOfObjectTypesWithStaticallyKnownMembers
            ? await Views.ReducedAsync(type, cancellation)
            : type;
        var diagnostic = CheckerDiagnostic.Create(
            node,
            DiagnosticLocalization.GetMessage(code),
            code == DiagnosticCode.NoBaseConstructorHasTheSpecifiedNumberOfTypeArguments ? []
            : [code == DiagnosticCode.X0IsReferencedDirectlyOrIndirectlyInItsOwnBaseExpression && type.Symbol is { } symbol
                ? TypeDisplay.SymbolName(symbol)
                : await TypeDisplay.GetAsync(displayedType, cancellation)]);
        if (code == DiagnosticCode.BaseConstructorReturnType0IsNotAnObjectTypeOrIntersectionOfObjectTypesWithStaticallyKnownMembers)
            diagnostic = await NeverIntersectionNoteAsync(diagnostic, type, cancellation);
        if (code == DiagnosticCode.Type0IsNotAConstructorFunctionType
            && type is TypeParameter { Symbol: { Declarations.Count: > 0 } parameterSymbol })
        {
            Type result = context.UnknownType;
            if (await Instantiation.Constraints.ConstraintAsync(type, cancellation) is { } constraint
                && (await SignaturesAsync(constraint, true, cancellation)).FirstOrDefault() is { } constructor)
                result = await Signatures.ReturnAsync(constructor, cancellation);
            diagnostic = diagnostic with
            {
                RelatedInformation = [CheckerDiagnostic.Create(parameterSymbol.Declarations[0],
                Messages.Did_you_mean_for_0_to_be_constrained_to_type_new_args_Colon_any_1,
                TypeDisplay.SymbolName(parameterSymbol), await TypeDisplay.GetAsync(result, cancellation))]
            };
        }
        Error(node, diagnostic);
    }

    public ValueTask<Type> IndexedAccessAsync(Type objectType, Type indexType, CancellationToken cancellation) =>
            Instantiation.IndexedAccessAsync(objectType, indexType, 0, null, cancellation);

    public async ValueTask CircularBaseAsync(SyntaxNode declaration, Type type, CancellationToken cancellation) =>
        Error(
            declaration,
            DiagnosticCode.Type0RecursivelyReferencesItselfAsABaseType,
            await TypeDisplay.GetAsync(type, NodeBuilderFlags.WriteArrayAsGenericType, cancellation));

    public void InvalidInterfaceBase(SyntaxNode declaration) =>
        Error(declaration, DiagnosticCode.AnInterfaceCanOnlyExtendAnObjectTypeOrIntersectionOfObjectTypesWithStaticallyKnownMembers);

    public async ValueTask<bool> LateIndexAsync(SyntaxNode declaration, CancellationToken cancellation) =>
        LateMembers.Name(declaration) is { } name && LateMembers.LateSyntax(name)
            && await LateIndexTypeAsync(await ComputedKeyAsync(declaration, cancellation), cancellation);

    public ValueTask<Type> ComputedKeyAsync(SyntaxNode declaration, CancellationToken cancellation) =>
        LateMembers.Name(declaration) is ElementAccessExpressionNode element ? CachedExpressionAsync(
            element.ArgumentExpression!,
            0,
            cancellation)
            : ComputedNameAsync((ComputedPropertyNameNode)LateMembers.Name(declaration)!, cancellation);

    public ValueTask<bool> AssignableAsync(Type source, Type target, CancellationToken cancellation) =>
        Relations.RelatedAsync(source, target, RelationKind.Assignable, cancellation);

    public async ValueTask<bool> SymbolNameAsync(Symbol symbol, CancellationToken cancellation) =>
        symbol.Name.Span.StartsWith(Symbol.InternalPrefix + "@", StringComparison.Ordinal)
            || symbol.Declarations.FirstOrDefault() is INamedNode { Name: ComputedPropertyNameNode name }
                && await AssignableKindAsync(await ComputedNameAsync(name, cancellation), TypeFlags.ESSymbol, cancellation);

    public async ValueTask<bool> NumericNameAsync(Symbol symbol, CancellationToken cancellation) =>
        IndexSignatures.NumericName(symbol.Name) || symbol.Declarations.FirstOrDefault() is INamedNode { Name: ComputedPropertyNameNode name }
            && await AssignableKindAsync(await ComputedNameAsync(name, cancellation), TypeFlags.Number, cancellation);

    public ValueTask<Type> SymbolTypeAsync(Symbol symbol, CancellationToken cancellation) => Values.GetAsync(symbol, cancellation);

    public async ValueTask<Type> VariableAsync(Symbol symbol, bool reportErrors, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (VariableBody is not null)
            return await VariableBody(symbol, reportErrors, cancellation);
        if (symbol.ValueDeclaration is SourceFileNode { ScriptKind: ScriptKind.JSON } json)
        {
            if (json.Statements is not { Count: > 0 })
                return context.EmptyObjectType;
            var expression = ((ExpressionStatementNode)json.Statements[0]).Expression!;
            return await Widening.GetAsync(
                await Widening.LiteralAsync(
                    await Expressions.CheckAsync(expression, cancellation: cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);
        }
        if ((symbol.Flags & SymbolFlags.ModuleExports) != 0)
        {
            if (symbol.Name == "exports")
            {
                var module = program.Symbols.Declaration(symbol.ValueDeclaration!)!;
                var target = await program.AliasTargets.ExternalModuleAsync(module, false, cancellation).ConfigureAwait(false);
                return target is null ? context.AnyType : await Values.GetAsync(target, cancellation).ConfigureAwait(false);
            }
            var moduleType = context.NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved, symbol);
            moduleType.Members = symbol.Members;
            moduleType.Properties = symbol.Members.Values.ToArray();
            return moduleType;
        }
        if (symbol.ValueDeclaration is BinaryExpressionNode or CallExpressionNode)
            return await AssignmentDeclarationTypeAsync(symbol, cancellation).ConfigureAwait(false);
        if (symbol.ValueDeclaration is VariableDeclarationNode or ParameterDeclarationNode or PropertyDeclarationNode
            or PropertySignatureDeclarationNode or BindingElementNode)
            return await Variables.GetAsync(symbol.ValueDeclaration, reportErrors, cancellation);
        if (symbol.ValueDeclaration is PropertyAssignmentNode or ShorthandPropertyAssignmentNode)
            return await ObjectLiterals.PropertyAsync(symbol.ValueDeclaration, true, 0, cancellation);
        if (symbol.ValueDeclaration is MethodDeclarationNode method)
            return await Functions.CheckAsync(method, cancellation: cancellation);
        if (symbol.ValueDeclaration is JsxAttributeNode attribute)
            return await JsxAttributeTypeAsync(attribute, 0, cancellation);
        if (symbol.ValueDeclaration is ExportAssignmentNode assignment)
            return assignment.Type is not null ? await Nodes.FromNodeAsync(assignment.Type, cancellation)
                : await Variables.WidenAsync(
                    await CachedExpressionAsync(assignment.Expression!, 0, cancellation),
                    assignment,
                    false,
                    cancellation);
        Type result;
        if (symbol.ValueDeclaration is ITypedNode { Type: { } annotation } declaration)
        {
            result = await TypeFromNodeAsync(annotation, cancellation);
            if (context.StrictNullChecks && ((symbol.Flags & SymbolFlags.Optional) != 0
                || declaration is ParameterDeclarationNode { QuestionToken: not null }))
                result = await Algebra.UnionAsync(
                    [result, declaration is ParameterDeclarationNode ? context.UndefinedType : context.UndefinedOrMissingType],
                    cancellation: cancellation);
        }
        else
            throw new InvalidOperationException($"Checker requires value inference for {symbol.Name}");
        cancellation.ThrowIfCancellationRequested();
        return result;
    }

    public bool ContextSensitiveParameter(Symbol symbol)
    {
        if (SensitiveParameter is not null)
            return SensitiveParameter(symbol);
        return symbol.ValueDeclaration is { } declaration
            && SemanticSyntax.RootDeclaration(declaration) is ParameterDeclarationNode parameter
            && FunctionSyntax.Contextual(parameter.Parent!) && FunctionSyntax.Sensitive(parameter.Parent!, program.Symbols);
    }

    public ValueTask<Type> ReverseMappedAsync(Symbol symbol, CancellationToken cancellation) =>
        ReverseInference.SymbolAsync(symbol, cancellation);

    public Type CircularSymbol(Symbol symbol)
    {
        if (symbol.ValueDeclaration is { } declaration)
        {
            if (declaration is ITypedNode { Type: not null })
            {
                Error(declaration, DiagnosticCode.X0IsReferencedDirectlyOrIndirectlyInItsOwnTypeAnnotation, TypeDisplay.SymbolName(symbol));
                return context.ErrorType;
            }
            if (NoImplicitAny
                && (declaration is not ParameterDeclarationNode || ((ParameterDeclarationNode)declaration).Initializer is not null))
                Error(
                    declaration,
                    DiagnosticCode.X0ImplicitlyHasTypeAnyBecauseItDoesNotHaveATypeAnnotationAndIsReferencedDirectlyOrIndirectlyInItsOwnInitializer,
                    TypeDisplay.SymbolName(symbol));
        }
        else if ((symbol.Flags & SymbolFlags.Alias) != 0 && AliasResolver.Declaration(symbol) is { } alias)
            Error(
                alias,
                DiagnosticCode.CircularDefinitionOfImportAlias0,
                alias is ExportAssignmentNode { Expression: IdentifierNode exported }
                ? exported.Text : TypeDisplay.SymbolName(symbol));
        return context.AnyType;
    }

    public void ImplicitAccessor(Symbol symbol, SyntaxNode declaration)
    {
        if ((declaration.Flags & NodeFlags.Ambient) != 0 && (SemanticSyntax.HasModifier(declaration, SyntaxKind.PrivateKeyword)
            || (declaration as INamedNode)?.Name is PrivateIdentifierNode))
            return;
        if (program.Symbols.Program.Configuration.Options.StrictOption("noImplicitAny"))
            Error(
                declaration,
                declaration is SetAccessorDeclarationNode
                    ? DiagnosticCode.Property0ImplicitlyHasTypeAnyBecauseItsSetAccessorLacksAParameterTypeAnnotation
                    : declaration is GetAccessorDeclarationNode
                        ? DiagnosticCode.Property0ImplicitlyHasTypeAnyBecauseItsGetAccessorLacksAReturnTypeAnnotation
                        : DiagnosticCode.Member0ImplicitlyHasAn1Type,
                declaration is SetAccessorDeclarationNode or GetAccessorDeclarationNode
                    ? [TypeDisplay.SymbolName(symbol)] : [TypeDisplay.SymbolName(symbol), "any"]);
    }

    public void CircularAccessor(Symbol symbol, SyntaxNode? annotation, SyntaxNode? getter)
    {
        if (annotation is not null)
            Error(annotation, DiagnosticCode.X0IsReferencedDirectlyOrIndirectlyInItsOwnTypeAnnotation, TypeDisplay.SymbolName(symbol));
        else if (getter is not null && program.Symbols.Program.Configuration.Options.StrictOption("noImplicitAny"))
            Error(
                getter,
                DiagnosticCode.X0ImplicitlyHasReturnTypeAnyBecauseItDoesNotHaveAReturnTypeAnnotationAndIsReferencedDirectlyOrIndirectlyInOneOfItsReturnExpressions,
                TypeDisplay.SymbolName(symbol));
    }
}
