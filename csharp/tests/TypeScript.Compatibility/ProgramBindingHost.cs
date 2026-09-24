using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal sealed partial class ProgramTypeHost : IBindingTypeHost
{
    internal BindingTypes Bindings { get; }

    public ValueTask<Type> InitializerTypeAsync(SyntaxNode expression, CancellationToken cancellation) =>
        links.TypeNodes.Get(expression).ResolvedType is { } type ? ValueTask.FromResult(type) : ExpressionAsync(expression, cancellation);

    public ValueTask<Type> BindingIterationAsync(Type type, SyntaxNode? pattern, bool possiblyOutOfBounds, CancellationToken cancellation) =>
        Bindings.ArrayIterationAsync(type, pattern, possiblyOutOfBounds, cancellation);

    public ValueTask<Type> IterableTypeAsync(CancellationToken cancellation) =>
        program.Globals.GetAsync("Iterable", 3, false, cancellation);

    public ValueTask<Type> IterableElementAsync(Type type, SyntaxNode? node, bool possiblyOutOfBounds, CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires Symbol.iterator protocol validation");

    public ValueTask InvalidArrayIterationAsync(Type type, SyntaxNode node, CancellationToken cancellation) =>
        throw new InvalidOperationException("Probe requires iterator diagnostic and awaited-type hints");

    public ValueTask<Type> InitialVariableAsync(VariableDeclarationNode variable, CancellationToken cancellation) =>
        variable.Initializer is { } initializer ? InitializerTypeAsync(initializer, cancellation)
            : variable.Parent?.Parent?.Kind == TypeScript.Compiler.Syntax.SyntaxKind.ForInStatement ? ValueTask.FromResult<Type>(context.StringType)
            : variable.Parent?.Parent?.Kind == TypeScript.Compiler.Syntax.SyntaxKind.ForOfStatement
                ? throw new InvalidOperationException("Probe requires for-of initial types") : ValueTask.FromResult<Type>(context.ErrorType);

    public async ValueTask<bool> ArrayLikeAsync(Type type, CancellationToken cancellation) =>
        IsArray(type) || (type.Flags & TypeFlags.Nullable) == 0
            && await AssignableAsync(type, program.Globals.AnyReadonlyArrayType!, cancellation);

    public async ValueTask<Type> OmitAsync(Type source, Type keys, CancellationToken cancellation)
    {
        var symbol = await program.Globals.AliasAsync("Omit", 2, Declared, cancellation);
        return symbol is null ? context.ErrorType
            : await References.AliasInstantiationAsync(symbol, [source, keys], cancellation: cancellation);
    }

    public void BindingError(SyntaxNode node, int code) => Error(node, code);
}
