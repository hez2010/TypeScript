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
            => ContextualProperties.GetAsync(type, name, cancellation: cancellation);

    public ValueTask DeprecatedPropertyAsync(Symbol property, SyntaxNode node, CancellationToken cancellation)
        => PropertyDeprecatedAsync(property, node, node is ElementAccessExpressionNode element ? element.ArgumentExpression!
            : node is IndexedAccessTypeNode indexed ? indexed.IndexType! : node, cancellation);

    public void InvalidIndex(SyntaxNode node, Type objectType, Type indexType, int code)
    {
        if (indexErrors.Add((node, code == 2514 ? null : objectType, code == 2514 ? null : indexType, code)))
            Diagnostics.Add(code);
    }
}
