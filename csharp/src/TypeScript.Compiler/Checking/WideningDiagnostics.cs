using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal sealed class WideningDiagnostics(
    CheckerSymbols symbols,
    TypeWidening widening,
    TypeViews views,
    TypeProperties properties,
    SymbolTypes values,
    IVariableTypeHost host,
    Func<SyntaxNode, Symbol, Type, CancellationToken, ValueTask> error)
{
    internal async ValueTask ReportAsync(SyntaxNode declaration, Type type, CancellationToken cancellation = default)
    {
        if (host.NoImplicitAny && (type.ObjectFlags & ObjectFlags.ContainsWideningType) != 0
            && !await InsideAsync(type, cancellation).ConfigureAwait(false))
            await host.ReportImplicitAnyAsync(declaration, type, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<bool> InsideAsync(Type type, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if ((type.ObjectFlags & ObjectFlags.ContainsWideningType) == 0)
            return false;
        if (type is UnionType union)
        {
            foreach (var part in union.Types)
                if (await views.EmptyObjectAsync(part, cancellation).ConfigureAwait(false))
                    return true;
            foreach (var part in union.Types)
                if (await InsideAsync(part, cancellation).ConfigureAwait(false))
                    return true;
        }
        else if (host.IsArray(type) || type is TypeReference { Target: TupleType })
        {
            foreach (var part in await host.TypeArgumentsAsync((TypeReference)type, cancellation).ConfigureAwait(false))
                if (await InsideAsync(part, cancellation).ConfigureAwait(false))
                    return true;
        }
        else if ((type.ObjectFlags & ObjectFlags.ObjectLiteral) != 0)
        {
            bool reported = false;
            foreach (var property in await properties.GetAsync(type, cancellation).ConfigureAwait(false))
            {
                var value = await values.GetAsync(property, cancellation).ConfigureAwait(false);
                if ((value.ObjectFlags & ObjectFlags.ContainsWideningType) == 0)
                    continue;
                reported = await InsideAsync(value, cancellation).ConfigureAwait(false);
                if (!reported)
                {
                    var declaration = property.Declarations.FirstOrDefault(
                        d => symbols.Binding(d)?.Get(d)?.Symbol?.ValueDeclaration is { } own
                        && own.Parent == type.Symbol?.ValueDeclaration);
                    if (declaration is not null)
                    {
                        await error(declaration, property, await widening.GetAsync(value, cancellation).ConfigureAwait(false), cancellation)
                            .ConfigureAwait(false);
                        reported = true;
                    }
                }
            }
            return reported;
        }
        return false;
    }
}
