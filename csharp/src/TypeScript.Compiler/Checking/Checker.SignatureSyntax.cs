using TypeScript.Compiler.Text;
using System.Globalization;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly Dictionary<SyntaxNode, IReadOnlyDictionary<Utf8String, Symbol>> typeSyntaxScopes = [];
    internal int TypeSyntaxScopeCount => typeSyntaxScopes.Count;

    internal ValueTask<Utf8String> SerializeSignatureSyntaxAsync(Signature signature, K kind, SyntaxNode? enclosing,
        NodeBuilderFlags flags = NodeBuilderFlags.IgnoreErrors | NodeBuilderFlags.NoTruncation, CancellationToken cancellation = default,
        INodeBuilderSymbolTracker? tracker = null,
        NodeBuilderInternalFlags internalFlags = NodeBuilderInternalFlags.None)
    {
        if (signature.Context != context)
            throw new ArgumentException("Signature belongs to another checker", nameof(signature));
        return VisibilityQueryAsync(enclosing, () => ChainOperationAsync(() => ContainerOperationAsync(async () =>
        {
            var state = new TypeSyntaxContext(enclosing, (flags & NodeBuilderFlags.UseAliasDefinedOutsideCurrentScope) != 0,
                (flags & NodeBuilderFlags.UseOnlyExternalAliasing) != 0, flags, tracker, internalFlags);
            var node = await SignatureSyntaxAsync(signature, kind, state, cancellation);
            if (!FinishTypeSyntax(state))
                return Utf8String.Empty;
            return PrintDiagnosticNode(node, enclosing is SourceFileNode, cancellation,
                enclosing is null ? null : SemanticSyntax.Source(enclosing), state.NoAsciiEscape, state.SingleLine);
        }, cancellation), cancellation), cancellation);
    }

    private async ValueTask<SyntaxNode> ObjectTypeSyntaxAsync(StructuredType type, TypeSyntaxContext state, CancellationToken cancellation)
        => type.Symbol is null ? await ObjectTypeSyntaxWorkerAsync(type, state, cancellation)
            : await CachedTypeSyntaxAsync(type, state, () => ObjectTypeSyntaxWorkerAsync(type, state, cancellation), cancellation);

    private async ValueTask<SyntaxNode> ObjectTypeSyntaxWorkerAsync(
        StructuredType type,
        TypeSyntaxContext state,
        CancellationToken cancellation)
    {
        if ((type.Properties?.Count ?? 0) == 0 && type.IndexInfos.Count == 0)
        {
            if (type.CallSignatures.Count == 1 && type.ConstructSignatures.Count == 0)
                return await SignatureSyntaxAsync(type.CallSignatures[0], K.FunctionType, state, cancellation);
            if (type.ConstructSignatures.Count == 1 && type.CallSignatures.Count == 0)
                return await SignatureSyntaxAsync(type.ConstructSignatures[0], K.ConstructorType, state, cancellation);
        }
        var abstractSignatures = type.ConstructSignatures.Where(s => (s.Flags & SignatureFlags.Abstract) != 0).ToArray();
        if (abstractSignatures.Length != 0)
        {
            var types = abstractSignatures.Select(s => (Type)SignatureInstantiation.FromSignature(s)).ToList();
            if (type.CallSignatures.Count + type.ConstructSignatures.Count - abstractSignatures.Length
                + type.IndexInfos.Count + ((state.Flags & NodeBuilderFlags.WriteClassExpressionAsTypeLiteral) != 0
                    ? type.Properties?.Count(p => (p.Flags & SymbolFlags.Prototype) == 0) ?? 0 : type.Properties?.Count ?? 0) != 0)
            {
                if (type.WithoutAbstractConstructSignatures is not { } withoutAbstract)
                {
                    var copy = context.NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved, type.Symbol);
                    copy.Members = type.Members;
                    copy.Properties = type.Properties;
                    copy.CallSignatures = type.CallSignatures;
                    copy.ConstructSignatures = type.ConstructSignatures.Where(s => (s.Flags & SignatureFlags.Abstract) == 0).ToArray();
                    copy.IndexInfos = type.IndexInfos;
                    copy.WithoutAbstractConstructSignatures = copy;
                    type.WithoutAbstractConstructSignatures = withoutAbstract = copy;
                }
                types.Add(withoutAbstract);
            }
            return await TypeSyntaxAsync(await Algebra.IntersectionAsync(types, cancellation: cancellation), state, cancellation);
        }
        var flags = state.Flags;
        state.Flags |= NodeBuilderFlags.InObjectTypeLiteral;
        try
        {
            return await ObjectPropertySyntaxAsync(type, state, cancellation);
        }
        finally
        {
            state.Flags = flags;
        }
    }

    private async ValueTask<SyntaxNode> RecursiveTypeSyntaxAsync(Type type, TypeSyntaxContext state, CancellationToken cancellation)
    {
        if (type is UnionType && (state.Flags & NodeBuilderFlags.AllowAnonymousIdentifier) == 0)
        {
            state.EncounteredError = true;
            state.Tracker.ReportCyclicStructureError();
        }
        if (type is ObjectType { Symbol: { } symbol } && (type.ObjectFlags & ObjectFlags.InstantiationExpressionType) == 0)
        {
            if ((symbol.Flags & SymbolFlags.TypeLiteral) != 0 && symbol.Declarations.FirstOrDefault() is { } literal)
            {
                var parent = literal.Parent;
                while (parent is ParenthesizedTypeNode)
                    parent = parent.Parent;
                if (parent is TypeAliasDeclarationNode && program.Symbols.Declaration(parent) is { } alias)
                    return await SymbolTypeNodeAsync(alias, SymbolFlags.Type, null, state.Symbols, false, cancellation);
            }
            if (await NamedFunctionSyntaxAsync(symbol, state, cancellation) is { } named)
                return named;
        }
        return ElidedTypeSyntax(state);
    }

    private async ValueTask<SyntaxNode?> NamedFunctionSyntaxAsync(Symbol symbol, TypeSyntaxContext state, CancellationToken cancellation)
    {
        bool named = (symbol.Flags & SymbolFlags.Method) != 0 && IdentifierName(symbol.Name)
            && symbol.Declarations.Any(d => SemanticSyntax.HasModifier(d, K.StaticKeyword));
        if ((symbol.Flags & SymbolFlags.Function) != 0)
        {
            named |= symbol.Parent is not null;
            if (!named)
                foreach (var declaration in symbol.Declarations)
                {
                    if (declaration.Parent is SourceFileNode or ModuleBlockNode)
                    {
                        named = true;
                        break;
                    }
                    if (declaration is FunctionExpressionNode or ArrowFunctionNode
                        && declaration.Parent is VariableDeclarationNode variable
                        && variable.Parent?.Parent?.Parent is SourceFileNode or ModuleBlockNode)
                    {
                        named = true;
                        if (symbol.ValueDeclaration?.Parent is { } owner && owner != state.Symbols.Enclosing)
                            symbol = program.Symbols.Declaration(owner) ?? symbol;
                        break;
                    }
                }
        }
        if (named && ((state.Flags & NodeBuilderFlags.UseStructuralFallback) == 0
            || (await SymbolAccessibilityAsync(symbol, state.Symbols.Enclosing, SymbolFlags.Value, false, true, cancellation)).Accessibility
                == SymbolAccessibility.Accessible))
            return await SymbolTypeNodeAsync(symbol, SymbolFlags.Value, null, state.Symbols, false, cancellation);
        return null;
    }

    private async ValueTask<SyntaxNode> SignatureSyntaxAsync(Signature signature, K kind, TypeSyntaxContext state,
        CancellationToken cancellation, SyntaxNode? name = null, SyntaxNode? question = null, bool preserveParameters = false)
    {
        var f = state.Factory;
        var allocated = new List<Symbol>();
        var previous = state.Symbols.Enclosing;
        var previousMapper = state.Mapper;
        var originalFlags = state.Flags;
        GeneratedParameterScope? valueScope = null;
        using var names = state.ParameterNames?.EnterScope();
        using var qualifiedNames = state.QualifiedNames.EnterScope();
        GeneratedParameterScope? generatedScope = null;
        try
        {
            state.Mapper = signature.Mapper ?? previousMapper;
            var expanded = await ExpandedSyntaxParametersAsync(signature, allocated, cancellation);
            state.Length.Add(3);
            valueScope = EnterValueParameterScope(signature.Declaration, expanded, signature.Parameters, state, cancellation);
            generatedScope = EnterGeneratedParameterScope(signature.Declaration, signature.TypeParameters, state, cancellation);
            var typeParameters = new List<SyntaxNode>();
            if (preserveParameters && signature.Declaration is IFunctionSignature { TypeParameters: { } sourceTypeParameters })
            {
                foreach (var parameter in sourceTypeParameters)
                    typeParameters.Add(await RecoverTypeSyntaxAsync(parameter, state, cancellation));
            }
            else if ((state.Flags & NodeBuilderFlags.WriteTypeArgumentsOfSignature) != 0
                && signature.Target is { TypeParameters.Count: > 0 } target
                && signature.Mapper is { } mapper)
            {
                foreach (var parameter in target.TypeParameters)
                    typeParameters.Add(await TypeSyntaxAsync((await Instantiation.Engine.InstantiateAsync(parameter, mapper,
                        cancellation: cancellation))!, state, cancellation));
            }
            else
                foreach (var parameter in signature.TypeParameters)
                {
                    var constraint = await Instantiation.Constraints.ConstraintAsync(parameter, cancellation);
                    typeParameters.Add(await TypeParameterSyntaxAsync(parameter,
                        constraint is null ? null : await ConstraintSyntaxAsync(parameter, constraint, state, cancellation),
                        state,
                        cancellation));
                }
            state.Flags &= ~NodeBuilderFlags.SuppressAnyReturnType;
            var parameters = new List<SyntaxNode>();
            if ((state.Flags & NodeBuilderFlags.OmitThisParameter) == 0 && signature.ThisParameter is { } thisParameter)
                parameters.Add(await ParameterSyntaxAsync(thisParameter, state, cancellation));
            var selected = preserveParameters
                || expanded.Take(Math.Max(0, expanded.Count - 1)).Any(p => (p.CheckFlags & Binding.CheckFlags.RestParameter) != 0)
                ? signature.Parameters : expanded;
            foreach (var parameter in selected)
                parameters.Add(await ParameterSyntaxAsync(parameter, state, cancellation, preserveModifiers: kind == K.Constructor));
            state.Flags |= originalFlags & NodeBuilderFlags.SuppressAnyReturnType;
            var result = await ReturnTypeSyntaxAsync(signature, state, cancellation);
            state.Flags &= ~NodeBuilderFlags.SuppressAnyReturnType;
            NodeList? typeParameterList = typeParameters.Count == 0 ? null : new(typeParameters.ToArray());
            var parameterList = new NodeList(parameters.ToArray());
            name ??= f.NewIdentifier(Utf8String.Empty);
            return kind switch
            {
                K.CallSignature => f.NewCallSignatureDeclaration(typeParameterList, parameterList, result),
                K.ConstructSignature => f.NewConstructSignatureDeclaration(typeParameterList, parameterList, result),
                K.MethodSignature => f.NewMethodSignatureDeclaration(null, name, question, typeParameterList, parameterList, result),
                K.FunctionType => f.NewFunctionTypeNode(typeParameterList, parameterList,
                    result ?? f.NewTypeReferenceNode(f.NewIdentifier(Utf8String.Empty), null)),
                K.ConstructorType => f.NewConstructorTypeNode(
                    (signature.Flags & SignatureFlags.Abstract) != 0 ? new([f.NewToken(K.AbstractKeyword)]) : null,
                    typeParameterList, parameterList, result ?? f.NewTypeReferenceNode(f.NewIdentifier(Utf8String.Empty), null)),
                K.GetAccessor => f.NewGetAccessorDeclaration(null, name, null, parameterList, result, null, null),
                K.SetAccessor => f.NewSetAccessorDeclaration(null, name, null, parameterList, null, null, null),
                K.Constructor => f.NewConstructorDeclaration(null, null, parameterList, null, null, null),
                K.MethodDeclaration => f.NewMethodDeclaration(null, null, name, null, typeParameterList, parameterList, result, null, null),
                K.IndexSignature => f.NewIndexSignatureDeclaration(null, parameterList, result),
                K.FunctionDeclaration => f.NewFunctionDeclaration(
                    null,
                    null,
                    (IdentifierNode)name,
                    typeParameterList,
                    parameterList,
                    result,
                    null,
                    null),
                K.FunctionExpression => f.NewFunctionExpression(null, null, (IdentifierNode)name, typeParameterList, parameterList, result,
                    null, f.NewBlock(new([]), false)),
                K.ArrowFunction => f.NewArrowFunction(
                    null,
                    typeParameterList,
                    parameterList,
                    result,
                    null,
                    null,
                    f.NewBlock(new([]), false)),
                _ => throw new InvalidOperationException("Unsupported signature syntax kind")
            };
        }
        finally
        {
            generatedScope?.Dispose();
            valueScope?.Dispose();
            state.Mapper = previousMapper;
            state.Flags = originalFlags;
            state.Symbols.Enclosing = previous;
            foreach (var symbol in allocated)
                links.Values.Remove(symbol);
        }
    }

    private async ValueTask<IReadOnlyList<Symbol>> ExpandedSyntaxParametersAsync(Signature signature, List<Symbol> allocated,
        CancellationToken cancellation)
    {
        if (!signature.HasRestParameter || await Values.GetAsync(signature.Parameters[^1], cancellation)
            is not TypeReference { Target: TupleType tuple } restType)
            return signature.Parameters;
        var rest = signature.Parameters[^1];
        var arguments = await References.TypeArgumentsAsync(restType, cancellation);
        var names = tuple.ElementInfos.Select((e, i) => SignatureParameters.Label(e, rest, i)).ToArray();
        var used = new HashSet<Utf8String>();
        var duplicates = new List<int>();
        for (int i = 0; i < names.Length; i++)
            if (!used.Add(names[i]))
                duplicates.Add(i);
        foreach (int i in duplicates)
        {
            int suffix = 1;
            Utf8String unique;
            do
                unique = Utf8String.Concat(names[i], "_"u8, Utf8String.Format(suffix++));
            while (!used.Add(unique));
            names[i] = unique;
        }
        var result = signature.Parameters.Take(signature.Parameters.Count - 1).ToList();
        for (int i = 0; i < arguments.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var flags = tuple.ElementInfos[i].Flags;
            var parameter = new Symbol(SymbolFlags.FunctionScopedVariable | SymbolFlags.Transient, names[i])
            {
                CheckFlags = (flags & ElementFlags.Variable) != 0 ? Binding.CheckFlags.RestParameter
                    : (flags & ElementFlags.Optional) != 0 ? Binding.CheckFlags.OptionalParameter : 0
            };
            allocated.Add(parameter);
            links.Values.Get(parameter).ResolvedType = (flags & ElementFlags.Rest) != 0
                ? await Instantiation.Tuples.ArrayAsync(arguments[i], cancellation: cancellation) : arguments[i];
            result.Add(parameter);
        }
        return result;
    }

    private async ValueTask<SyntaxNode> ParameterSyntaxAsync(Symbol symbol, TypeSyntaxContext state, CancellationToken cancellation,
        bool preserveModifiers = false)
    {
        var f = state.Factory;
        var declaration = symbol.Declarations.OfType<ParameterDeclarationNode>().FirstOrDefault();
        var value = await Values.GetAsync(symbol, cancellation);
        bool optional = (symbol.CheckFlags & Binding.CheckFlags.OptionalParameter) != 0
            || declaration is not null && await OptionalSyntaxParameterAsync(declaration, cancellation);
        bool parameterProperty = declaration?.Modifiers?.Any(m => m.Kind is
            K.PublicKeyword or K.PrivateKeyword or K.ProtectedKeyword or K.ReadonlyKeyword) == true;
        bool addUndefined = context.StrictNullChecks && declaration is not null && (declaration.Flags & NodeFlags.Synthesized) == 0
            && (declaration.Initializer is not null
                && !optional
                && (!parameterProperty || SemanticSyntax.FunctionDeclarationLike(state.Symbols.Enclosing))
                || optional && declaration.Initializer is null && parameterProperty);
        if (addUndefined && declaration?.Type is { } annotation)
        {
            var annotated = await Nodes.FromNodeAsync(annotation, cancellation);
            addUndefined = annotated != context.ErrorType && (annotated is UnionType union
                ? union.Types.All(t => (t.Flags & TypeFlags.Undefined) == 0) : (annotated.Flags & TypeFlags.Undefined) == 0);
        }
        if (addUndefined)
            value = await Algebra.UnionAsync([value, context.UndefinedType], cancellation: cancellation);
        var type = await DeclarationTypeSyntaxAsync(
            value,
            declaration,
            !addUndefined && declaration?.QuestionToken is not null,
            state,
            cancellation);
        var name = declaration?.Name is { } original
            ? CloneSyntaxBindingName(
                original is QualifiedNameNode qualified ? qualified.Right! : original,
                state) : f.NewIdentifier(symbol.Name);
        state.NoAsciiEscape.Add(name);
        state.Length.Add(symbol.Name, 3);
        var modifiers = preserveModifiers && (state.Flags & NodeBuilderFlags.OmitParameterModifiers) == 0
            ? declaration?.Modifiers?.Where(m => m.Kind is not K.Decorator).Select(m => f.NewToken(m.Kind)).ToArray() : null;
        return f.NewParameterDeclaration(modifiers is { Length: > 0 } ? new(modifiers) : null,
            declaration?.DotDotDotToken is not null || (symbol.CheckFlags & Binding.CheckFlags.RestParameter) != 0
                ? f.NewToken(K.DotDotDotToken)
                : null,
            name, optional ? f.NewToken(K.QuestionToken) : null, type, null);
    }

    private async ValueTask<bool> OptionalSyntaxParameterAsync(ParameterDeclarationNode parameter, CancellationToken cancellation)
    {
        if (parameter.QuestionToken is not null)
            return true;
        var declarations = Signatures.Parameters(parameter.Parent!);
        int index = declarations is null ? -1 : declarations.IndexOf(parameter);
        if (parameter.Initializer is not null)
            return index >= await Parameters.MinimumAsync(await Signatures.FromDeclarationAsync(parameter.Parent!, cancellation),
                strongUntypedJs: true, voidIsRequired: true, cancellation);
        return parameter.Type is null && parameter.DotDotDotToken is null
            && Signatures.Invoked(parameter.Parent!) is { } invoked && index >= (invoked.Arguments?.Count ?? 0);
    }

    private static SyntaxNode CloneSyntaxBindingName(SyntaxNode name, TypeSyntaxContext state, bool typeAnnotation = false)
    {
        var result = name.DeepClone<SyntaxNode>(state.Factory);
        foreach (var node in result.DescendantsAndSelf())
        {
            node.Parent = null;
            if (node is BindingElementNode element)
                element.Initializer = null;
            if (typeAnnotation && node is StringLiteralNode literal)
                literal.TokenFlags |= state.Symbols.StringLiteralFlags;
            state.NoAsciiEscape.Add(node);
            if (node is not TypeLiteralNode || (state.Flags & NodeBuilderFlags.MultilineObjectLiterals) == 0)
                state.SingleLine.Add(node);
        }
        return result;
    }

    private async ValueTask<SyntaxNode> DeclarationTypeSyntaxAsync(Type value, SyntaxNode? declaration, bool optional,
        TypeSyntaxContext state, CancellationToken cancellation)
    {
        var flags = state.Flags;
        bool suppressed = state.SuppressInferenceFallback;
        if (value != context.ErrorType && declaration is not null && state.Symbols.Enclosing is not null && state.HasTracker)
        {
            ReportInferenceFallbacks(declaration, false, state, cancellation);
            state.SuppressInferenceFallback = true;
        }
        try
        {
            if (state.Symbols.Enclosing is not null && declaration is GetAccessorDeclarationNode or SetAccessorDeclarationNode
                && await RecoverAccessorSyntaxAsync(declaration, value, state, cancellation) is { } accessor)
                return accessor;
            if (state.Symbols.Enclosing is not null && declaration is ITypedNode { Type: { } annotation }
                && annotation is not TypePredicateNode && (value.ObjectFlags & ObjectFlags.RequiresWidening) == 0)
            {
                var annotated = await Nodes.FromNodeAsync(annotation, cancellation);
                if (annotated != value)
                {
                    var comparable = optional ? await Facts.FilterAsync(value, TypeFacts.NEUndefined, cancellation) : value;
                    if (annotated == comparable || annotated is UnionType && comparable is UnionType
                        && await Relations.RelatedAsync(annotated, comparable, RelationKind.Identity, cancellation))
                        value = annotated;
                }
                if (annotated == value)
                {
                    if (ReuseLiteralTypeSyntax(annotation, state, cancellation) is { } reused)
                        return reused;
                    if (await ReuseTypeAnnotationSyntaxAsync(annotation, state, cancellation) is { } reusableAnnotation)
                        return reusableAnnotation;
                    if (annotation is TypeQueryNode query && await ReuseTypeQuerySyntaxAsync(query, state, cancellation) is { } reusedQuery)
                        return reusedQuery;
                    return await RecoverAnnotationSyntaxAsync(annotation, state, cancellation);
                }
            }
            if (state.Symbols.Enclosing is not null
                && declaration is ITypedNode { Type: null } and IInitializedNode { Initializer: { } initializer }
                && await ReuseInitializerTypeSyntaxAsync(value, initializer, state, cancellation) is { } inferred)
                return inferred;
            if (value == context.ErrorType && state.Symbols.Enclosing is not null
                && declaration is PropertyAssignmentNode or ShorthandPropertyAssignmentNode
                && program.Symbols.Declaration(declaration) is { } property)
            {
                state.Tracker.ReportInferenceFallback(declaration);
                value = await Values.GetAsync(property, cancellation);
            }
            return await TypeSyntaxAsync(value, state, cancellation);
        }
        finally
        {
            state.Flags = flags;
            state.SuppressInferenceFallback = suppressed;
        }
    }

    private async ValueTask<SyntaxNode?> ReuseTypeQuerySyntaxAsync(
        TypeQueryNode query,
        TypeSyntaxContext state,
        CancellationToken cancellation,
        bool countLength = true)
    {
        var left = query.ExprName;
        while (left is QualifiedNameNode qualified)
            left = qualified.Left;
        if (left is not IdentifierNode identifier)
            return null;
        var original = await program.EntityNames.ResolveAsync(identifier, SymbolFlags.Value | SymbolFlags.ExportValue,
            true, true, cancellation: cancellation);
        var current = await program.EntityNames.ResolveAsync(
            identifier,
            SymbolFlags.Value | SymbolFlags.ExportValue,
            true,
            true,
            state.Symbols.Enclosing,
            cancellation);
        for (var scope = state.Symbols.Enclosing; scope is not null; scope = scope.Parent)
            if (typeSyntaxScopes.TryGetValue(scope, out var locals)
                && locals.TryGetValue(identifier.Text, out var local) && (local.Flags & SymbolFlags.Value) != 0)
            {
                current = local;
                break;
            }
        var arguments = new List<SyntaxNode>();
        if (query.TypeArguments is { } typeArguments)
            foreach (var argument in typeArguments)
            {
                long previousLength = state.Length.Value;
                var reused = ReuseLiteralTypeSyntax(argument, state, cancellation)
                    ?? await ReuseTypeAnnotationSyntaxAsync(argument, state, cancellation);
                if (reused is not null)
                {
                    state.Length.Value = previousLength;
                    arguments.Add(reused);
                }
                else
                    arguments.Add(await TypeSyntaxAsync(await Nodes.FromNodeAsync(argument, cancellation), state, cancellation));
            }
        NodeList? argumentNodes = query.TypeArguments is null ? null : new(arguments.ToArray());
        if (current != UnknownSymbol && (original is null || current is not null
            && await SameSymbolReferenceAsync(current.ExportSymbol ?? current, original.ExportSymbol ?? original, cancellation))
            && (current is null || (await SymbolAccessibilityAsync(current, state.Symbols.Enclosing,
                SymbolFlags.Value, false, true, cancellation)).Accessibility == SymbolAccessibility.Accessible))
        {
            if (countLength)
                AddReusedSyntaxLength(query, state);
            if (current is not null)
                state.Tracker.TrackSymbol(current, state.Symbols.Enclosing, SymbolFlags.Value | SymbolFlags.ExportValue);
            if (query.TypeArguments is null)
                return CloneSyntaxBindingName(query, state);
            return state.Factory.NewTypeQueryNode(CloneSyntaxBindingName(query.ExprName!, state), argumentNodes);
        }
        var symbol = await program.EntityNames.ResolveAsync(query.ExprName, SymbolFlags.Value, true, cancellation: cancellation);
        if (symbol is null || symbol == UnknownSymbol || (await SymbolAccessibilityAsync(symbol, state.Symbols.Enclosing,
            SymbolFlags.Value, false, true, cancellation)).Accessibility != SymbolAccessibility.Accessible)
        {
            state.Tracker.ReportInferenceFallback(query.ExprName!);
            int diagnostics = state.DiagnosticCount;
            bool error = state.EncounteredError;
            var fallback = await TypeSyntaxAsync(await Nodes.FromNodeAsync(query, cancellation), state, cancellation);
            bool reusable = state.DiagnosticCount == diagnostics && state.EncounteredError == error;
            state.EncounteredError = error;
            return reusable ? fallback : null;
        }
        // The pinned builder drops type arguments when this fallback returns a typeof query.
        var result = await SymbolTypeNodeAsync(symbol, SymbolFlags.Value, argumentNodes, state.Symbols, false, cancellation);
        if (countLength)
            AddReusedSyntaxLength(query, state);
        return result;
    }
}
