using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal sealed partial class ProgramTypeHost : ITypeKeyHost, IIndexedTypeHost
{
    private readonly HashSet<(SyntaxNode, Type?, Type?, int)> indexErrors = [];
    internal TypeKeys Keys { get; }
    internal IndexedTypes Indexed { get; }

    public ValueTask<Type> ReducedTypeAsync(Type type, CancellationToken cancellation) => Views.ReducedAsync(type, cancellation);

    public IndexInfo EnumNumberIndex => Members.EnumNumberIndex;
    public bool NoUncheckedIndexedAccess => program.Symbols.Program.Configuration.Options.Boolean("noUncheckedIndexedAccess") == true;
    public bool NoImplicitAny => program.Symbols.Program.Configuration.Options.StrictOption("noImplicitAny");

    public ValueTask<Type> IndexedAccessAsync(
        Type objectType,
        Type indexType,
        AccessFlags flags,
        TypeAlias? alias,
        CancellationToken cancellation)
            => Indexed.GetAsync(objectType, indexType, flags, alias: alias, cancellation: cancellation);

    public ValueTask<Type?> ContextualPropertyAsync(Type type, string name, CancellationToken cancellation)
            => throw new InvalidOperationException("Probe requires contextual property checking");

    public ValueTask<Type?> ElementAccessAsync(Type original, Type apparent, Type index, Type fullIndex,
            ElementAccessExpressionNode node, AccessFlags flags, CancellationToken cancellation)
            => throw new InvalidOperationException("Probe requires element access expression checking");

    public ValueTask DeprecatedPropertyAsync(Symbol property, SyntaxNode node, CancellationToken cancellation)
    {
        if (property.Declarations.Any(d => (d.Flags & TypeScript.Compiler.Syntax.NodeFlags.HasJSDoc) != 0))
            throw new InvalidOperationException("Probe requires deprecation suggestions");
        return ValueTask.CompletedTask;
    }

    public void InvalidIndex(SyntaxNode node, Type objectType, Type indexType, int code)
    {
        if (indexErrors.Add((node, code == 2514 ? null : objectType, code == 2514 ? null : indexType, code)))
            Diagnostics.Add(code);
    }
}
