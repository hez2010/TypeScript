using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IExplicitValueHost
{
    ValueTask<Type?> ExplicitThisAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> SuperAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type?> IteratedTypeAsync(ForInOrOfStatementNode node, Type expression, CancellationToken cancellation);

    Utf8String PrivatePropertyName(Symbol symbol, PrivateIdentifierNode name);

    void MissingExplicitAnnotation(Symbol symbol, SyntaxNode declaration);
}

internal sealed class ExplicitValueTypes(CheckerLinks links, CheckerSymbols symbols, ReferenceSymbols references,
    AliasResolver aliases, SymbolTypes values, TypeProperties properties, IExplicitValueHost host)
{
    private readonly HashSet<Symbol> resolving = [];
    internal int ResolvingCount => resolving.Count;

    internal async ValueTask<Type?> DottedAsync(SyntaxNode node, bool reportMissing = false, CancellationToken cancellation = default)
    {
        List<SyntaxNode> accesses = [];
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if ((node.Flags & NodeFlags.InWithStatement) != 0)
                return null;
            if (node is ParenthesizedExpressionNode parentheses)
                node = parentheses.Expression!;
            else if (node is PropertyAccessExpressionNode access)
            {
                accesses.Add(access.Name!);
                node = access.Expression!;
            }
            else
                break;
        }
        Type? type = node switch
        {
            IdentifierNode identifier => await SymbolAsync(
                symbols.ExportedValue(references.Resolve(identifier, cancellation))!,
                reportMissing,
                cancellation).ConfigureAwait(false),
            { Kind: SyntaxKind.ThisKeyword } => await host.ExplicitThisAsync(node, cancellation).ConfigureAwait(false),
            { Kind: SyntaxKind.SuperKeyword } => await host.SuperAsync(node, cancellation).ConfigureAwait(false),
            _ => null
        };
        for (int i = accesses.Count - 1; type is not null && i >= 0; i--)
        {
            Utf8String? name = accesses[i] is PrivateIdentifierNode privateName
                ? type.Symbol is { } symbol ? host.PrivatePropertyName(symbol, privateName) : (Utf8String?)null : SyntaxNameText.Get(accesses[i]);
            var property = name is null
                ? null
                : await properties.PropertyAsync(type, name.Value, cancellation: cancellation).ConfigureAwait(false);
            type = property is null ? null : await SymbolAsync(property, reportMissing, cancellation).ConfigureAwait(false);
        }
        return type;
    }

    internal async ValueTask<Type?> SymbolAsync(Symbol symbol, bool reportMissing = false, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        symbol = (await aliases.SymbolAsync(symbol, cancellation: cancellation).ConfigureAwait(false))!;
        if (!resolving.Add(symbol))
            return null;
        try
        {
            if ((symbol.Flags & (SymbolFlags.Function | SymbolFlags.Method | SymbolFlags.Class | SymbolFlags.ValueModule)) != 0)
                return await values.GetAsync(symbol, cancellation).ConfigureAwait(false);
            if ((symbol.Flags & (SymbolFlags.Variable | SymbolFlags.Property)) != 0)
            {
                if ((symbol.CheckFlags & CheckFlags.Mapped) != 0 && links.MappedSymbols.Get(symbol).SyntheticOrigin is { } origin
                    && await SymbolAsync(origin, reportMissing, cancellation).ConfigureAwait(false) is not null)
                    return await values.GetAsync(symbol, cancellation).ConfigureAwait(false);
                if (symbol.ValueDeclaration is { } declaration)
                {
                    if (Annotated(declaration))
                        return await values.GetAsync(symbol, cancellation).ConfigureAwait(false);
                    if (declaration is VariableDeclarationNode
                        && declaration.Parent?.Parent is ForInOrOfStatementNode { Kind: SyntaxKind.ForOfStatement } loop
                        && await DottedAsync(loop.Expression!, cancellation: cancellation).ConfigureAwait(false) is { } expressionType)
                        return await host.IteratedTypeAsync(loop, expressionType, cancellation).ConfigureAwait(false);
                    if (reportMissing)
                        host.MissingExplicitAnnotation(symbol, declaration);
                }
            }
            return null;
        }
        finally
        {
            resolving.Remove(symbol);
        }
    }

    private static bool Annotated(SyntaxNode node) => node is ITypedNode { Type: not null }
        && node is VariableDeclarationNode or PropertyDeclarationNode or PropertySignatureDeclarationNode or ParameterDeclarationNode
        || node is BinaryExpressionNode { Right: IFunctionSignature { Type: not null } };
}
