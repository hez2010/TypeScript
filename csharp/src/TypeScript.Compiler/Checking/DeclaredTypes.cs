using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using S = TypeScript.Compiler.Binding.SymbolFlags;

namespace TypeScript.Compiler.Checking;

internal interface IDeclaredTypeHost
{
    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<object?> EnumValueAsync(EnumMemberNode member, CancellationToken cancellation);

    bool IsDynamicEnumName(EnumMemberNode member);

    void CircularTypeAlias(Symbol symbol, TypeAliasDeclarationNode declaration);
}

internal sealed class DeclaredTypes(TypeContext context, CheckerLinks links, CheckerSymbols symbols,
    TypeParameterScopes scopes, AliasResolver aliases, TypeAlgebra algebra, TypeResolutionStack resolutions, IDeclaredTypeHost host)
{
    internal async ValueTask<Type> GetAsync(Symbol symbol, CancellationToken cancellation = default)
        => await TryGetAsync(symbol, cancellation).ConfigureAwait(false) ?? context.ErrorType;

    internal async ValueTask<Type?> TryGetAsync(Symbol symbol, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if ((symbol.Flags & (S.Class | S.Interface)) != 0)
            return await scopes.ClassOrInterfaceAsync(symbol, cancellation).ConfigureAwait(false);
        if ((symbol.Flags & S.TypeParameter) != 0)
            return scopes.Parameter(symbol);
        if ((symbol.Flags & S.TypeAlias) != 0)
            return await TypeAliasAsync(symbol, cancellation).ConfigureAwait(false);
        if ((symbol.Flags & S.Enum) != 0)
            return await EnumAsync(symbol, cancellation).ConfigureAwait(false);
        var data = links.DeclaredTypes.Get(symbol);
        if ((symbol.Flags & S.EnumMember) != 0)
        {
            if (data.DeclaredType is not null)
                return data.DeclaredType;
            var parent = symbols.Parent(symbol) ?? throw new InvalidOperationException("Enum member has no parent symbol");
            var type = await EnumAsync(parent, cancellation).ConfigureAwait(false);
            return data.DeclaredType ??= type;
        }
        if ((symbol.Flags & S.Alias) != 0)
        {
            if (data.DeclaredType is not null)
                return data.DeclaredType;
            var target = await aliases.ResolveAsync(symbol, cancellation).ConfigureAwait(false);
            var type = await GetAsync(target, cancellation).ConfigureAwait(false);
            return data.DeclaredType = type;
        }
        return null;
    }

    private async ValueTask<Type> TypeAliasAsync(Symbol symbol, CancellationToken cancellation)
    {
        var data = links.TypeAliases.Get(symbol);
        if (data.DeclaredType is { } cached)
            return cached;
        if (!resolutions.Push(symbol, TypeSystemPropertyName.DeclaredType))
            return context.ErrorType;
        bool active = true;
        var parametersBefore = data.TypeParameters;
        var instancesBefore = data.Instantiations;
        try
        {
            var declaration = symbol.Declarations.OfType<TypeAliasDeclarationNode>().FirstOrDefault()
                ?? throw new InvalidOperationException("Type alias has no declaration");
            var type = await host.TypeFromNodeAsync(declaration.Type!, cancellation).ConfigureAwait(false);
            context.RequireOwned(type);
            bool resolved = resolutions.Pop();
            active = false;
            if (resolved)
            {
                var parameters = scopes.Local(symbol, cancellation).Cast<TypeParameter>().ToArray();
                if (parameters.Length != 0)
                {
                    data.TypeParameters = Array.AsReadOnly(parameters);
                    data.Instantiations = new() { [TypeCacheKey.Instantiation(parameters, null, false)] = type };
                }
                if (type == context.IntrinsicMarkerType && symbol.Name == "BuiltinIteratorReturn")
                {
                    bool strict = symbols.Program.Configuration.Options.Boolean("strictBuiltinIteratorReturn")
                        ?? symbols.Program.Configuration.Options.Boolean("strict") ?? false;
                    type = strict ? context.UndefinedType : context.AnyType;
                }
            }
            else
            {
                host.CircularTypeAlias(symbol, declaration);
                type = context.ErrorType;
            }
            cancellation.ThrowIfCancellationRequested();
            return data.DeclaredType ??= type;
        }
        catch
        {
            data.TypeParameters = parametersBefore;
            data.Instantiations = instancesBefore;
            throw;
        }
        finally
        {
            if (active)
                resolutions.Pop();
        }
    }

    private async ValueTask<Type> EnumAsync(Symbol symbol, CancellationToken cancellation)
    {
        var data = links.DeclaredTypes.Get(symbol);
        if (data.DeclaredType is { } cached)
            return cached;
        var members = new List<Type>();
        var assignments = new List<(DeclaredTypeLinks Links, Type? Previous, Type Assigned)>();
        try
        {
            foreach (var declaration in symbol.Declarations.OfType<EnumDeclarationNode>())
                foreach (var member in declaration.Members?.OfType<EnumMemberNode>() ?? [])
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (host.IsDynamicEnumName(member))
                        continue;
                    var memberSymbol = symbols.Declaration(member) ?? throw new InvalidOperationException("Enum member has no symbol");
                    var value = await host.EnumValueAsync(member, cancellation).ConfigureAwait(false);
                    var type = value switch
                    {
                        string text => context.GetEnumLiteralType(text, symbol, memberSymbol),
                        double number => context.GetEnumLiteralType(number, symbol, memberSymbol),
                        null => ComputedEnum(memberSymbol),
                        _ => throw new InvalidOperationException("Invalid enum value")
                    };
                    var entry = links.DeclaredTypes.Get(memberSymbol);
                    var fresh = context.GetFreshLiteralType(type);
                    assignments.Add((entry, entry.DeclaredType, fresh));
                    entry.DeclaredType = fresh;
                    members.Add(type);
                }
            var result = members.Count == 0 ? ComputedEnum(symbol)
                : await algebra.UnionAsync(
                    members,
                    alias: context.CreateAlias(symbol, []),
                    cancellation: cancellation).ConfigureAwait(false);
            if (result is UnionType)
            {
                result.Flags |= TypeFlags.EnumLiteral;
                result.Symbol = symbol;
            }
            cancellation.ThrowIfCancellationRequested();
            return data.DeclaredType = result;
        }
        catch
        {
            for (int i = assignments.Count - 1; i >= 0; i--)
            {
                var assignment = assignments[i];
                if (assignment.Links.DeclaredType == assignment.Assigned)
                    assignment.Links.DeclaredType = assignment.Previous;
            }
            throw;
        }
    }

    private LiteralType ComputedEnum(Symbol symbol)
    {
        var type = context.NewComputedEnumType(symbol);
        context.GetFreshLiteralType(type);
        return type;
    }
}
