using System.Globalization;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly Dictionary<SyntaxNode, IReadOnlyDictionary<string, Symbol>> typeSyntaxScopes = [];
    internal int TypeSyntaxScopeCount => typeSyntaxScopes.Count;

    private async ValueTask<SyntaxNode> ObjectTypeSyntaxAsync(StructuredType type, TypeSyntaxContext state, CancellationToken cancellation)
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
                + type.IndexInfos.Count + (type.Properties?.Count ?? 0) != 0)
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
        return await ObjectPropertySyntaxAsync(type, state, cancellation);
    }

    private async ValueTask<SyntaxNode> RecursiveTypeSyntaxAsync(Type type, TypeSyntaxContext state, CancellationToken cancellation)
    {
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
            if (named)
                return await SymbolTypeNodeAsync(symbol, SymbolFlags.Value, null, state.Symbols, false, cancellation);
        }
        return state.Factory.NewKeywordTypeNode(K.AnyKeyword);
    }

    private async ValueTask<SyntaxNode> SignatureSyntaxAsync(Signature signature, K kind, TypeSyntaxContext state,
        CancellationToken cancellation, SyntaxNode? name = null, SyntaxNode? question = null)
    {
        var f = state.Factory;
        var allocated = new List<Symbol>();
        var previous = state.Symbols.Enclosing;
        SyntaxNode? scope = null;
        try
        {
            var expanded = await ExpandedSyntaxParametersAsync(signature, allocated, cancellation);
            if (previous is not null && signature.Declaration is not null && expanded.Count != 0)
            {
                var locals = new Dictionary<string, Symbol>(StringComparer.Ordinal);
                for (int i = 0; i < expanded.Count; i++)
                {
                    var parameter = expanded[i];
                    var original = i < signature.Parameters.Count ? signature.Parameters[i] : null;
                    if (parameter != original)
                    {
                        if (original is not null)
                            locals[original.Name] = original;
                    }
                    else if (parameter.Declarations.OfType<ParameterDeclarationNode>().FirstOrDefault()?.Name is BindingPatternNode pattern)
                    {
                        foreach (var element in pattern.DescendantsAndSelf().OfType<BindingElementNode>())
                            if (element.Name is IdentifierNode && program.Symbols.Declaration(element) is { } symbol)
                                locals[symbol.Name] = symbol;
                    }
                    else
                        locals[parameter.Name] = parameter;
                }
                scope = f.NewBlock(new([]), false);
                scope.Parent = previous;
                typeSyntaxScopes.Add(scope, locals);
                state.Symbols.Enclosing = scope;
            }
            var typeParameters = new List<SyntaxNode>();
            foreach (var parameter in signature.TypeParameters)
            {
                var constraint = await Instantiation.Constraints.ConstraintAsync(parameter, cancellation);
                typeParameters.Add(await TypeParameterSyntaxAsync(parameter,
                    constraint is null ? null : await TypeSyntaxAsync(constraint, state, cancellation), state, cancellation));
            }
            var parameters = new List<SyntaxNode>();
            if (signature.ThisParameter is { } thisParameter)
                parameters.Add(await ParameterSyntaxAsync(thisParameter, state, cancellation));
            var selected = expanded.Take(Math.Max(0, expanded.Count - 1)).Any(p => (p.CheckFlags & Binding.CheckFlags.RestParameter) != 0)
                ? signature.Parameters : expanded;
            foreach (var parameter in selected)
                parameters.Add(await ParameterSyntaxAsync(parameter, state, cancellation));
            var returnType = await Signatures.ReturnAsync(signature, cancellation);
            var predicate = await Signatures.PredicateAsync(signature, cancellation);
            SyntaxNode result;
            if (predicate is not null)
            {
                SyntaxNode parameterName = predicate.Kind is TypePredicateKind.This or TypePredicateKind.AssertsThis
                    ? f.NewThisTypeNode() : f.NewIdentifier(predicate.ParameterName);
                state.NoAsciiEscape.Add(parameterName);
                result = f.NewTypePredicateNode(
                    predicate.Kind is TypePredicateKind.AssertsThis or TypePredicateKind.AssertsIdentifier
                        ? f.NewToken(K.AssertsKeyword)
                        : null,
                    parameterName, predicate.Type is null ? null : await TypeSyntaxAsync(predicate.Type, state, cancellation));
            }
            else
                result = await DeclarationTypeSyntaxAsync(returnType, signature.Declaration, false, state, cancellation);
            NodeList? typeParameterList = typeParameters.Count == 0 ? null : new(typeParameters.ToArray());
            var parameterList = new NodeList(parameters.ToArray());
            return kind switch
            {
                K.CallSignature => f.NewCallSignatureDeclaration(typeParameterList, parameterList, result),
                K.ConstructSignature => f.NewConstructSignatureDeclaration(typeParameterList, parameterList, result),
                K.MethodSignature => f.NewMethodSignatureDeclaration(null, name, question, typeParameterList, parameterList, result),
                K.FunctionType => f.NewFunctionTypeNode(typeParameterList, parameterList, result),
                K.ConstructorType => f.NewConstructorTypeNode(
                    (signature.Flags & SignatureFlags.Abstract) != 0 ? new([f.NewToken(K.AbstractKeyword)]) : null,
                    typeParameterList, parameterList, result),
                K.GetAccessor => f.NewGetAccessorDeclaration(null, name, null, parameterList, result, null, null),
                K.SetAccessor => f.NewSetAccessorDeclaration(null, name, null, parameterList, null, null, null),
                _ => throw new InvalidOperationException("Unsupported signature syntax kind")
            };
        }
        finally
        {
            state.Symbols.Enclosing = previous;
            if (scope is not null)
                typeSyntaxScopes.Remove(scope);
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
        var used = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new List<int>();
        for (int i = 0; i < names.Length; i++)
            if (!used.Add(names[i]))
                duplicates.Add(i);
        foreach (int i in duplicates)
        {
            int suffix = 1;
            string unique;
            do
                unique = names[i] + "_" + (suffix++).ToString(CultureInfo.InvariantCulture);
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

    private async ValueTask<SyntaxNode> ParameterSyntaxAsync(Symbol symbol, TypeSyntaxContext state, CancellationToken cancellation)
    {
        var f = state.Factory;
        var declaration = symbol.Declarations.OfType<ParameterDeclarationNode>().FirstOrDefault();
        var value = await Values.GetAsync(symbol, cancellation);
        bool optional = (symbol.CheckFlags & Binding.CheckFlags.OptionalParameter) != 0
            || declaration is not null && await OptionalSyntaxParameterAsync(declaration, cancellation);
        bool addUndefined = context.StrictNullChecks && declaration is not null
            && (declaration.Initializer is not null && !optional
                || optional && declaration.Initializer is null && declaration.Modifiers?.Any(m => m.Kind is
                    K.PublicKeyword or K.PrivateKeyword or K.ProtectedKeyword or K.ReadonlyKeyword) == true);
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
        return f.NewParameterDeclaration(null,
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
        int index = declarations is null ? -1 : declarations.ToList().IndexOf(parameter);
        if (parameter.Initializer is not null)
            return index >= await Parameters.MinimumAsync(await Signatures.FromDeclarationAsync(parameter.Parent!, cancellation),
                strongUntypedJs: true, voidIsRequired: true, cancellation);
        return parameter.Type is null && parameter.DotDotDotToken is null
            && Signatures.Invoked(parameter.Parent!) is { } invoked && index >= (invoked.Arguments?.Count ?? 0);
    }

    private static SyntaxNode CloneSyntaxBindingName(SyntaxNode name, TypeSyntaxContext state)
    {
        var result = name.DeepClone<SyntaxNode>(state.Factory);
        foreach (var node in result.DescendantsAndSelf())
        {
            node.Parent = null;
            if (node is BindingElementNode element)
                element.Initializer = null;
            state.NoAsciiEscape.Add(node);
            state.SingleLine.Add(node);
        }
        return result;
    }

    private async ValueTask<SyntaxNode> DeclarationTypeSyntaxAsync(Type value, SyntaxNode? declaration, bool optional,
        TypeSyntaxContext state, CancellationToken cancellation)
    {
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
                if (annotation is TypeQueryNode query && await ReuseTypeQuerySyntaxAsync(query, state, cancellation) is { } reusedQuery)
                    return reusedQuery;
            }
        }
        return await TypeSyntaxAsync(value, state, cancellation);
    }

    private async ValueTask<SyntaxNode?> ReuseTypeQuerySyntaxAsync(
        TypeQueryNode query,
        TypeSyntaxContext state,
        CancellationToken cancellation)
    {
        var left = query.ExprName;
        while (left is QualifiedNameNode qualified)
            left = qualified.Left;
        if (left is not IdentifierNode identifier)
            return null;
        var original = await program.EntityNames.ResolveAsync(identifier, SymbolFlags.Value, true, true, cancellation: cancellation);
        var current = await program.EntityNames.ResolveAsync(
            identifier,
            SymbolFlags.Value,
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
                arguments.Add(ReuseLiteralTypeSyntax(argument, state, cancellation)
                    ?? await TypeSyntaxAsync(await Nodes.FromNodeAsync(argument, cancellation), state, cancellation));
        NodeList? argumentNodes = query.TypeArguments is null ? null : new(arguments.ToArray());
        if (current != UnknownSymbol && (original is null || current is not null
            && await SameSymbolReferenceAsync(current.ExportSymbol ?? current, original.ExportSymbol ?? original, cancellation))
            && (current is null || (await SymbolAccessibilityAsync(current, state.Symbols.Enclosing,
                SymbolFlags.Value, false, true, cancellation)).Accessibility == SymbolAccessibility.Accessible))
        {
            if (query.TypeArguments is null)
                return CloneSyntaxBindingName(query, state);
            return state.Factory.NewTypeQueryNode(CloneSyntaxBindingName(query.ExprName!, state), argumentNodes);
        }
        var symbol = await program.EntityNames.ResolveAsync(query.ExprName, SymbolFlags.Value, true, cancellation: cancellation);
        if (symbol is null || symbol == UnknownSymbol || (await SymbolAccessibilityAsync(symbol, state.Symbols.Enclosing,
            SymbolFlags.Value, false, true, cancellation)).Accessibility != SymbolAccessibility.Accessible)
            return null;
        // The pinned builder drops type arguments when this fallback returns a typeof query.
        return await SymbolTypeNodeAsync(symbol, SymbolFlags.Value, argumentNodes, state.Symbols, false, cancellation);
    }
}
