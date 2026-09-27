using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using F = TypeScript.Compiler.Checking.TypeFlags;
using O = TypeScript.Compiler.Checking.ObjectFlags;

namespace TypeScript.Compiler.Checking;

internal sealed class TypeVariables(Func<TypeReference, CancellationToken, ValueTask<IReadOnlyList<Type>>> typeArguments)
{
    internal async ValueTask<bool> CouldContainAsync(Type type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if ((type.Flags & F.StructuredOrInstantiable) == 0)
            return false;
        var flags = type.ObjectFlags;
        if ((flags & O.CouldContainTypeVariablesComputed) != 0)
            return (flags & O.CouldContainTypeVariables) != 0;
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        bool result = (type.Flags & F.Instantiable) != 0;
        if (!result && (type.Flags & F.Object) != 0 && !IsNonGenericTopLevel(type))
        {
            if ((flags & O.Reference) != 0)
            {
                var reference = (TypeReference)type;
                result = reference.Node is not null;
                if (!result)
                    foreach (var argument in await typeArguments(reference, cancellation).ConfigureAwait(false))
                        if (await CouldContainAsync(argument, cancellation).ConfigureAwait(false))
                        {
                            result = true;
                            break;
                        }
            }
            result |= (flags & O.Anonymous) != 0 && type.Symbol is { Declarations.Length: > 0 } symbol
                && (symbol.Flags & (SymbolFlags.Function | SymbolFlags.Method | SymbolFlags.Class | SymbolFlags.TypeLiteral | SymbolFlags.ObjectLiteral)) != 0
                || (flags & (O.Mapped | O.ReverseMapped | O.ObjectRestType | O.InstantiationExpressionType)) != 0;
        }
        if (!result && type is UnionOrIntersectionType composite && (type.Flags & F.EnumLiteral) == 0 && !IsNonGenericTopLevel(type))
            foreach (var part in composite.Types)
                if (await CouldContainAsync(part, cancellation).ConfigureAwait(false))
                {
                    result = true;
                    break;
                }
        type.ObjectFlags |= O.CouldContainTypeVariablesComputed | (result ? O.CouldContainTypeVariables : O.None);
        return result;
    }

    internal static bool IsNonGenericTopLevel(Type type)
    {
        if (type.Alias is not { TypeArguments.Count: 0 } alias)
            return false;
        var declaration = alias.Symbol.Declarations.FirstOrDefault(n => n.Kind == SyntaxKind.TypeAliasDeclaration)
            ?? alias.Symbol.Declarations.FirstOrDefault(n => n.Kind == SyntaxKind.JSTypeAliasDeclaration);
        for (var parent = declaration?.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is SourceFileNode)
                return true;
            if (parent is not ModuleDeclarationNode)
                return false;
        }
        return false;
    }
}
