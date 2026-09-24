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
        Iteration.CheckAsync(IterationUse.Destructuring | (possiblyOutOfBounds ? IterationUse.PossiblyOutOfBounds : 0),
            type, context.UndefinedType, pattern, cancellation);

    public ValueTask<Type> InitialVariableAsync(VariableDeclarationNode variable, CancellationToken cancellation) =>
        variable.Initializer is { } initializer ? InitializerTypeAsync(initializer, cancellation)
            : variable.Parent?.Parent?.Kind == TypeScript.Compiler.Syntax.SyntaxKind.ForInStatement ? ValueTask.FromResult<Type>(context.StringType)
            : variable.Parent?.Parent?.Kind == TypeScript.Compiler.Syntax.SyntaxKind.ForOfStatement
                ? ForOfElementAsync(
                    (ForInOrOfStatementNode)variable.Parent.Parent,
                    cancellation) : ValueTask.FromResult<Type>(context.ErrorType);

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
