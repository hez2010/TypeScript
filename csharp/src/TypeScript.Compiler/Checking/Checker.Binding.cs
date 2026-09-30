using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : IBindingTypeHost
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
        var symbol = await program.Globals.AliasAsync(Utf8Literals.Omit, 2, Declared, cancellation);
        return symbol is null ? context.ErrorType
            : await References.AliasInstantiationAsync(symbol, [source, keys], cancellation: cancellation);
    }

    public void BindingError(SyntaxNode node, DiagnosticCode code) => Error(node, code);
}
