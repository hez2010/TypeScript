using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using S = TypeScript.Compiler.Binding.SymbolFlags;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal interface ITypeParameterScopeHost
{
    bool IsContextSensitive(SyntaxNode declaration);

    ValueTask<Signature?> FirstCallSignatureAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<Symbol?> ResolveTypeNameAsync(SyntaxNode name, CancellationToken cancellation);
}

internal sealed class TypeParameterScopes(TypeContext context, CheckerLinks links, CheckerSymbols symbols, ITypeParameterScopeHost host)
{
    private Dictionary<Symbol, Type>? constructingClasses;

    internal TypeParameter Parameter(Symbol symbol)
    {
        var data = links.DeclaredTypes.Get(symbol);
        return (TypeParameter)(data.DeclaredType ??= context.NewTypeParameter(symbol));
    }

    internal async ValueTask<IReadOnlyList<Type>> OuterAsync(SyntaxNode node, bool includeThisTypes = true,
        CancellationToken cancellation = default)
    {
        var containers = new Stack<SyntaxNode>();
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            cancellation.ThrowIfCancellationRequested();
            if (current.Kind is K.ClassDeclaration or K.ClassExpression or K.InterfaceDeclaration or K.CallSignature
                or K.ConstructSignature or K.MethodSignature or K.FunctionType or K.ConstructorType or K.FunctionDeclaration
                or K.MethodDeclaration or K.FunctionExpression or K.ArrowFunction or K.TypeAliasDeclaration
                or K.JSTypeAliasDeclaration or K.MappedType or K.ConditionalType)
                containers.Push(current);
        }
        var result = new List<Type>();
        while (containers.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            if ((current.Kind is K.FunctionExpression or K.ArrowFunction
                || current is MethodDeclarationNode { Parent: ObjectLiteralExpressionNode }) && host.IsContextSensitive(current))
            {
                var signature = await host.FirstCallSignatureAsync(DeclarationSymbol(current), cancellation).ConfigureAwait(false);
                if (signature is { TypeParameters.Count: > 0 })
                {
                    foreach (var parameter in signature.TypeParameters)
                    {
                        context.RequireOwned(parameter);
                        result.Add(parameter);
                    }
                    continue;
                }
            }
            if (current is MappedTypeNode mapped)
                result.Add(Parameter(DeclarationSymbol(mapped.TypeParameter!)));
            else if (current is ConditionalTypeNode)
            {
                foreach (var local in symbols.Binding(current)?.Get(current)?.Locals.Values ?? [])
                    if ((local.Flags & S.TypeParameter) != 0)
                        result.Add(Parameter(symbols.Merger.GetMergedSymbol(local)!));
            }
            else
            {
                Append(result, Parameters(current), cancellation);
                if (includeThisTypes && current.Kind is K.ClassDeclaration or K.ClassExpression or K.InterfaceDeclaration)
                {
                    var type = await ClassOrInterfaceAsync(DeclarationSymbol(current), cancellation).ConfigureAwait(false);
                    if (type.ThisType is { } thisType)
                        result.Add(thisType);
                }
            }
        }
        return result.AsReadOnly();
    }

    internal IReadOnlyList<Type> Local(Symbol symbol, CancellationToken cancellation = default)
    {
        var result = new List<Type>();
        AppendLocal(result, symbol, cancellation);
        return result.AsReadOnly();
    }

    private void AppendLocal(List<Type> result, Symbol symbol, CancellationToken cancellation)
    {
        foreach (var declaration in symbol.Declarations)
            if (declaration.Kind is K.InterfaceDeclaration or K.ClassDeclaration or K.ClassExpression or K.TypeAliasDeclaration
                or K.JSTypeAliasDeclaration)
                Append(result, Parameters(declaration), cancellation);
    }

    private void Append(List<Type> result, NodeList? declarations, CancellationToken cancellation)
    {
        if (declarations is null)
            return;
        foreach (var declaration in declarations)
        {
            cancellation.ThrowIfCancellationRequested();
            var parameter = Parameter(DeclarationSymbol(declaration));
            if (!result.Contains(parameter))
                result.Add(parameter);
        }
    }

    internal async ValueTask<InterfaceType> ClassOrInterfaceAsync(Symbol symbol, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        var data = links.DeclaredTypes.Get(symbol);
        if (data.DeclaredType is { } cached)
            return (InterfaceType)cached;
        bool ownsConstruction = constructingClasses is null;
        constructingClasses ??= new(ReferenceEqualityComparer.Instance);
        var kind = (symbol.Flags & S.Class) != 0 ? ObjectFlags.Class : ObjectFlags.Interface;
        var result = (InterfaceType)context.NewObjectType(kind, symbol);
        data.DeclaredType = result;
        constructingClasses.Add(symbol, result);
        try
        {
            var declaration = ClassLikeDeclaration(symbol) ?? throw new InvalidOperationException(
                "Class/interface symbol has no class-like declaration");
            var outer = await OuterAsync(declaration, false, cancellation).ConfigureAwait(false);
            var parameters = outer.ToList();
            AppendLocal(parameters, symbol, cancellation);
            if (parameters.Count != 0 || kind == ObjectFlags.Class || !await ThislessAsync(symbol, cancellation).ConfigureAwait(false))
            {
                result.ObjectFlags |= ObjectFlags.Reference;
                var thisType = context.NewTypeParameter(symbol);
                thisType.IsThisType = true;
                thisType.Constraint = result;
                result.ThisType = thisType;
                result.OuterTypeParameterCount = outer.Count;
                result.ResolvedTypeArguments = Array.AsReadOnly(parameters.ToArray());
                result.AllTypeParameters = Array.AsReadOnly<Type>([.. parameters, thisType]);
                result.Target = result;
                result.Instantiations = new() { [new TypeCacheKey(parameters.ToArray())] = result };
            }
            cancellation.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            if (ownsConstruction)
                foreach (var (constructedSymbol, constructedType) in constructingClasses)
                {
                    var entry = links.DeclaredTypes.Get(constructedSymbol);
                    if (entry.DeclaredType == constructedType)
                        entry.DeclaredType = null;
                }
            throw;
        }
        finally
        {
            if (ownsConstruction)
                constructingClasses = null;
        }
    }

    private async ValueTask<bool> ThislessAsync(Symbol symbol, CancellationToken cancellation)
    {
        foreach (var declaration in symbol.Declarations.OfType<InterfaceDeclarationNode>())
        {
            cancellation.ThrowIfCancellationRequested();
            if (((symbols.Binding(declaration)?.Get(declaration)?.Flags ?? declaration.Flags) & NodeFlags.ContainsThis) != 0)
                return false;
            foreach (var heritage in declaration.HeritageClauses?.OfType<HeritageClauseNode>() ?? [])
                if (heritage.Token == K.ExtendsKeyword && heritage.Types is { } elements)
                    foreach (var element in elements)
                    {
                        var name = element is TypeReferenceNode reference ? reference.TypeName
                            : ((ExpressionWithTypeArgumentsNode)element).Expression;
                        if (name is IdentifierNode or QualifiedNameNode || IsEntityExpression(name))
                        {
                            var parent = await host.ResolveTypeNameAsync(name!, cancellation).ConfigureAwait(false);
                            if (parent is null || (parent.Flags & S.Interface) == 0
                                || (await ClassOrInterfaceAsync(parent, cancellation).ConfigureAwait(false)).ThisType is not null)
                                return false;
                        }
                    }
        }
        return true;
    }

    private static bool IsEntityExpression(SyntaxNode? node)
    {
        while (node is PropertyAccessExpressionNode access && access.Name is IdentifierNode)
            node = access.Expression;
        return node is IdentifierNode;
    }

    private Symbol DeclarationSymbol(SyntaxNode node) => symbols.Declaration(node)
        ?? throw new InvalidOperationException("Type parameter scope has an unbound declaration");

    internal static SyntaxNode? ClassLikeDeclaration(Symbol symbol)
        => (symbol.Flags & (S.Class | S.Function)) != 0 ? symbol.ValueDeclaration
            : symbol.Declarations.FirstOrDefault(node => node is InterfaceDeclarationNode
                || node is VariableDeclarationNode { Initializer: FunctionExpressionNode or ArrowFunctionNode });

    private static NodeList? Parameters(SyntaxNode node) => node switch
    {
        IFunctionSignature function => function.TypeParameters,
        ClassDeclarationNode declaration => declaration.TypeParameters,
        ClassExpressionNode declaration => declaration.TypeParameters,
        InterfaceDeclarationNode declaration => declaration.TypeParameters,
        TypeAliasDeclarationNode declaration => declaration.TypeParameters,
        _ => null
    };
}
