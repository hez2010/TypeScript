using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using S = TypeScript.Compiler.Binding.SymbolFlags;

namespace TypeScript.Compiler.Checking;

internal interface ISymbolTypeHost
{
    ValueTask<Type> VariableAsync(Symbol symbol, bool reportErrors, CancellationToken cancellation);

    bool ContextSensitiveParameter(Symbol symbol);

    ValueTask<Type> ReverseMappedAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> ReturnFromBodyAsync(SyntaxNode declaration, CancellationToken cancellation);

    ValueTask<Type> BaseConstructorAsync(InterfaceType type, CancellationToken cancellation);

    Type CircularSymbol(Symbol symbol);

    void ImplicitAccessor(Symbol symbol, SyntaxNode declaration);

    void CircularAccessor(Symbol symbol, SyntaxNode? annotation, SyntaxNode? getter);
}

// Read and write types share checker-owned symbol links, but setter annotations
// and deferred composite properties can retain distinct write types.
internal sealed class SymbolTypes(TypeContext context, CheckerLinks links, CheckerSymbols symbols,
    DeclaredTypes declared, AliasResolver aliases, AliasTargets targets, TypeAlgebra algebra,
    TypeInstantiation instantiation, MappedMembers mapped, TypeResolutionStack resolutions, ISymbolTypeHost host)
{
    internal async ValueTask<Type> GetAsync(Symbol symbol, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        long symbolMark = Diagnostics.CompilationCapture.Mark();
        try
        {
            return await GetCoreAsync(symbol, cancellation).ConfigureAwait(false);
        }
        finally
        {
            Diagnostics.CompilationCapture.Report(Diagnostics.AllocationProbes.SymbolTypesGet, symbolMark);
        }
    }

    private async ValueTask<Type> GetCoreAsync(Symbol symbol, CancellationToken cancellation)
    {
        if ((symbol.CheckFlags & CheckFlags.DeferredType) != 0)
            return await DeferredAsync(symbol, false, cancellation).ConfigureAwait(false);
        if ((symbol.CheckFlags & CheckFlags.Instantiated) != 0)
            return await InstantiatedAsync(symbol, false, cancellation).ConfigureAwait(false);
        if ((symbol.CheckFlags & CheckFlags.Mapped) != 0)
            return await mapped.SymbolTypeAsync(symbol, cancellation).ConfigureAwait(false);
        if ((symbol.CheckFlags & CheckFlags.ReverseMapped) != 0)
            return await host.ReverseMappedAsync(symbol, cancellation).ConfigureAwait(false);
        if ((symbol.Flags & S.Accessor) != 0)
            return await AccessorAsync(symbol, false, cancellation).ConfigureAwait(false);
        if ((symbol.Flags & (S.Variable | S.Property)) != 0)
            return await VariableAsync(symbol, cancellation).ConfigureAwait(false);
        var data = links.Values.Get(symbol);
        if ((symbol.Flags & (S.Function | S.Method | S.Class | S.Enum | S.ValueModule)) != 0)
        {
            if (data.ResolvedType is { } cached)
                return cached;
            var result = await FunctionClassModuleAsync(symbol, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            return data.ResolvedType = result;
        }
        if ((symbol.Flags & S.EnumMember) != 0)
        {
            if (data.ResolvedType is { } cached)
                return cached;
            var result = await declared.GetAsync(symbol, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            return data.ResolvedType = result;
        }
        return (symbol.Flags & S.Alias) != 0 ? await AliasAsync(symbol, cancellation).ConfigureAwait(false) : context.ErrorType;
    }

    internal async ValueTask<Type> WriteAsync(Symbol symbol, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if ((symbol.CheckFlags & CheckFlags.SyntheticProperty) != 0)
        {
            if ((symbol.CheckFlags & CheckFlags.DeferredType) != 0)
                return await DeferredAsync(symbol, true, cancellation).ConfigureAwait(false);
            var data = links.Values.Get(symbol);
            return data.WriteType ?? data.ResolvedType ?? throw new InvalidOperationException("Synthetic property has no type");
        }
        if ((symbol.Flags & S.Property) != 0)
            return NonMissing(await GetAsync(symbol, cancellation).ConfigureAwait(false), (symbol.Flags & S.Optional) != 0);
        if ((symbol.Flags & S.Accessor) != 0)
            return (symbol.CheckFlags & CheckFlags.Instantiated) != 0
                ? await InstantiatedAsync(symbol, true, cancellation).ConfigureAwait(false)
                : await AccessorAsync(symbol, true, cancellation).ConfigureAwait(false);
        return await GetAsync(symbol, cancellation).ConfigureAwait(false);
    }

    internal Type NonMissing(Type type, bool optional)
        => context.ExactOptionalPropertyTypes && optional ? algebra.Filter(type, t => t != context.MissingType) : type;

    private async ValueTask<Type> DeferredAsync(Symbol symbol, bool write, CancellationToken cancellation)
    {
        var data = links.Values.Get(symbol);
        if ((write ? data.WriteType : data.ResolvedType) is { } cached)
            return cached;
        var deferred = links.DeferredSymbols.Get(symbol);
        var constituents = write ? deferred.WriteConstituents : deferred.Constituents;
        var result = write && constituents.Count == 0 ? await DeferredAsync(symbol, false, cancellation).ConfigureAwait(false)
            : deferred.Parent is UnionType ? await algebra.UnionAsync(constituents, cancellation: cancellation).ConfigureAwait(false)
            : deferred.Parent is IntersectionType ? await algebra.IntersectionAsync(
                constituents,
                cancellation: cancellation).ConfigureAwait(false)
            : throw new InvalidOperationException("Deferred property has no composite parent");
        cancellation.ThrowIfCancellationRequested();
        if (write)
            data.WriteType = result;
        else
            data.ResolvedType = result;
        return result;
    }

    private async ValueTask<Type> InstantiatedAsync(Symbol symbol, bool write, CancellationToken cancellation)
    {
        var data = links.Values.Get(symbol);
        if ((write ? data.WriteType : data.ResolvedType) is { } cached)
            return cached;
        var target = data.Target ?? throw new InvalidOperationException("Instantiated symbol has no target");
        var source = write
            ? await WriteAsync(target, cancellation).ConfigureAwait(false)
            : await GetAsync(target, cancellation).ConfigureAwait(false);
        var result = await instantiation.InstantiateAsync(source, data.Mapper, cancellation: cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Symbol instantiation returned no type");
        cancellation.ThrowIfCancellationRequested();
        if (write)
            data.WriteType = result;
        else
            data.ResolvedType = result;
        return result;
    }

    private async ValueTask<Type> VariableAsync(Symbol symbol, CancellationToken cancellation)
    {
        var data = links.Values.Get(symbol);
        if (data.ResolvedType is { } cached)
            return cached;
        Type result;
        if ((symbol.Flags & S.Prototype) != 0)
        {
            var classType = (InterfaceType)await declared.GetAsync(symbols.Parent(symbol)!, cancellation).ConfigureAwait(false);
            int count = classType.AllTypeParameters.Count - (classType.ThisType is null ? 0 : 1);
            result = count == 0
                ? classType
                : context.CreateTypeReference(classType, Enumerable.Repeat<Type>(context.AnyType, count).ToArray());
        }
        else if (symbol == symbols.RequireSymbol)
            result = context.AnyType;
        else
        {
            if (!resolutions.Push(symbol, TypeSystemPropertyName.Type))
                return host.CircularSymbol(symbol);
            bool active = true;
            try
            {
                result = await host.VariableAsync(symbol, !host.ContextSensitiveParameter(symbol), cancellation).ConfigureAwait(false);
                bool resolved = resolutions.Pop();
                active = false;
                if (!resolved)
                    result = host.CircularSymbol(symbol);
            }
            finally
            {
                if (active)
                    resolutions.Pop();
            }
        }
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(result);
        if (data.ResolvedType is null && !host.ContextSensitiveParameter(symbol))
            data.ResolvedType = result;
        return result;
    }

    private async ValueTask<Type> FunctionClassModuleAsync(Symbol symbol, CancellationToken cancellation)
    {
        if ((symbol.Flags & S.Module) != 0 && symbol.ValueDeclaration is ModuleDeclarationNode { Body: null })
            return context.AnyType;
        if ((symbol.Flags & S.ValueModule) != 0 && symbol.ValueDeclaration is SourceFileNode file
            && symbols.Binding(file)?.CommonJSModuleIndicator is not null)
        {
            var target = await targets.ExternalModuleAsync(symbol, false, cancellation).ConfigureAwait(false);
            if (target != symbol)
                return await GetAsync(target!, cancellation).ConfigureAwait(false);
        }
        var type = context.NewObjectType(ObjectFlags.Anonymous, symbol);
        if ((symbol.Flags & S.Class) != 0)
        {
            var baseConstructor = await host.BaseConstructorAsync(
                (InterfaceType)await declared.GetAsync(symbol, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);
            var baseVariable = (baseConstructor.Flags & TypeFlags.TypeVariable) != 0 ? baseConstructor
                : (baseConstructor as IntersectionType)?.Types.FirstOrDefault(t => (t.Flags & TypeFlags.TypeVariable) != 0);
            return baseVariable is null
                ? type
                : await algebra.IntersectionAsync([type, baseVariable], cancellation: cancellation).ConfigureAwait(false);
        }
        return context.StrictNullChecks && (symbol.Flags & S.Optional) != 0
            ? await algebra.UnionAsync([type, context.UndefinedOrMissingType], cancellation: cancellation).ConfigureAwait(false) : type;
    }

    private async ValueTask<Type> AliasAsync(Symbol symbol, CancellationToken cancellation)
    {
        var data = links.Values.Get(symbol);
        if (data.ResolvedType is { } cached)
            return cached;
        if (!resolutions.Push(symbol, TypeSystemPropertyName.Type))
            return context.ErrorType;
        bool active = true;
        Type? assigned = null;
        try
        {
            var target = await aliases.ResolveAsync(symbol, cancellation).ConfigureAwait(false);
            var export = await targets.TargetAsync(AliasResolver.Declaration(symbol), cancellation).ConfigureAwait(false);
            if (data.ResolvedType is null)
            {
                var result = (await aliases.FlagsAsync(target, cancellation: cancellation).ConfigureAwait(false) & S.Value) != 0
                    ? await GetAsync(target, cancellation).ConfigureAwait(false) : context.ErrorType;
                cancellation.ThrowIfCancellationRequested();
                data.ResolvedType = assigned = result;
            }
            bool resolved = resolutions.Pop();
            active = false;
            if (!resolved)
                host.CircularSymbol(export ?? symbol);
            return data.ResolvedType;
        }
        catch
        {
            if (assigned is not null && data.ResolvedType == assigned)
                data.ResolvedType = null;
            throw;
        }
        finally
        {
            if (active)
                resolutions.Pop();
        }
    }

    private async ValueTask<Type> AccessorAsync(Symbol symbol, bool write, CancellationToken cancellation)
    {
        var data = links.Values.Get(symbol);
        if ((write ? data.WriteType : data.ResolvedType) is { } cached)
            return cached;
        if (!resolutions.Push(symbol, write ? TypeSystemPropertyName.WriteType : TypeSystemPropertyName.Type))
            return context.ErrorType;
        bool active = true;
        try
        {
            var getter = symbol.Declarations.OfType<GetAccessorDeclarationNode>().FirstOrDefault();
            SyntaxNode? setter = symbol.Declarations.OfType<SetAccessorDeclarationNode>().FirstOrDefault();
            var accessor = symbol.Declarations.OfType<PropertyDeclarationNode>().FirstOrDefault(
                n => SemanticSyntax.HasModifier(n, SyntaxKind.AccessorKeyword));
            Type? result;
            if (write)
            {
                setter ??= accessor;
                result = await AnnotationAsync(setter, cancellation).ConfigureAwait(false);
            }
            else
            {
                result = await AnnotationAsync(getter, cancellation).ConfigureAwait(false)
                    ?? await AnnotationAsync(setter, cancellation).ConfigureAwait(false)
                    ?? await AnnotationAsync(accessor, cancellation).ConfigureAwait(false);
                if (result is null && getter?.Body is not null)
                    result = await host.ReturnFromBodyAsync(getter, cancellation).ConfigureAwait(false);
                if (result is null && accessor is not null)
                    result = await host.VariableAsync(symbol, true, cancellation).ConfigureAwait(false);
                if (result is null)
                {
                    if ((setter ?? getter ?? (SyntaxNode?)accessor) is { } missing)
                        host.ImplicitAccessor(symbol, missing);
                    result = context.AnyType;
                }
            }
            bool resolved = resolutions.Pop();
            active = false;
            if (!resolved)
            {
                var annotated = write ? Annotation(setter) is null ? null : setter
                    : new SyntaxNode?[] { getter, setter, accessor }.FirstOrDefault(d => Annotation(d) is not null);
                host.CircularAccessor(symbol, annotated, write ? null : getter);
                result = context.AnyType;
            }
            result ??= await AccessorAsync(symbol, false, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (write)
                return data.WriteType ??= result;
            return data.ResolvedType ??= result;
        }
        finally
        {
            if (active)
                resolutions.Pop();
        }
    }

    private ValueTask<Type?> AnnotationAsync(SyntaxNode? declaration, CancellationToken cancellation)
        => Annotation(declaration) is { } node ? FromNodeAsync(node, cancellation) : ValueTask.FromResult<Type?>(null);

    private async ValueTask<Type?> FromNodeAsync(SyntaxNode node, CancellationToken cancellation)
        => await host.TypeFromNodeAsync(node, cancellation).ConfigureAwait(false);

    internal static SyntaxNode? Annotation(SyntaxNode? declaration) => declaration switch
    {
        GetAccessorDeclarationNode getter => getter.Type,
        PropertyDeclarationNode property => property.Type,
        SetAccessorDeclarationNode { Parameters: { Count: > 0 } parameters } => ((ParameterDeclarationNode)parameters[
            parameters.Count == 2 && parameters[0] is ParameterDeclarationNode { Name: IdentifierNode { Text.Span: var matchedText } } && matchedText.SequenceEqual("this"u8) ? 1 : 0]).Type,
        _ => null
    };
}
