using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using S = TypeScript.Compiler.Binding.SymbolFlags;

namespace TypeScript.Compiler.Checking;

internal interface IEntityNameHost
{
    DiagnosticMessage CannotFindName(IdentifierNode name);

    ValueTask<IReadOnlyDictionary<Utf8String, Symbol>> ExportsAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<Symbol> CommonJsNamespaceAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask MissingQualifiedAsync(SyntaxNode name, SyntaxNode right, Symbol parent, S meaning, CancellationToken cancellation);
}

internal sealed class EntityNames(CheckerSymbols symbols, AliasResolver aliases, IEntityNameHost host)
{
    internal async ValueTask<Symbol?> ResolveAsync(SyntaxNode? name, S meaning, bool ignoreErrors = false,
        bool dontResolveAlias = false, SyntaxNode? location = null, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (Missing(name))
            return null;
        Symbol? result;
        if (name is IdentifierNode identifier)
        {
            var resolver = symbols.NameResolver(cancellation);
            var message = ignoreErrors ? null : meaning == S.Namespace || (name.Flags & NodeFlags.Synthesized) != 0
                ? Messages.Cannot_find_namespace_0 : host.CannotFindName(identifier);
            var lookupLocation = location ?? name;
            if (meaning == S.Namespace)
            {
                result = symbols.Merger.GetMergedSymbol(resolver.Resolve(lookupLocation, identifier.Text, meaning, isUse: true));
                if (result is null)
                {
                    var alias = symbols.Merger.GetMergedSymbol(resolver.Resolve(lookupLocation, identifier.Text, S.Alias, isUse: true));
                    if (alias?.Name == Utf8Literals.ExportEquals)
                        result = alias.Parent;
                }
                if (result is null && message is not null)
                    resolver.Resolve(lookupLocation, identifier.Text, meaning, message, isUse: true);
            }
            else
                result = symbols.Merger.GetMergedSymbol(resolver.Resolve(lookupLocation, identifier.Text, meaning, message, isUse: true));
        }
        else
        {
            var (left, right) = name switch
            {
                QualifiedNameNode qualified => (qualified.Left, qualified.Right),
                PropertyAccessExpressionNode access => (access.Expression, access.Name),
                _ => throw new InvalidOperationException("Unknown entity name kind")
            };
            var parent = await ResolveAsync(
                left,
                S.Namespace,
                ignoreErrors,
                location: location,
                cancellation: cancellation).ConfigureAwait(false);
            if (parent is null || Missing(right))
                return null;
            if (parent == symbols.UnknownSymbol)
                return parent;
            parent = await host.CommonJsNamespaceAsync(parent, cancellation).ConfigureAwait(false);
            Utf8String text = ((IdentifierNode)right!).Text;
            result = symbols.Merger.GetMergedSymbol(
                symbols.Lookup(await host.ExportsAsync(parent, cancellation).ConfigureAwait(false), text, meaning));
            if (result is null && (parent.Flags & S.Alias) != 0)
            {
                var target = await aliases.ResolveAsync(parent, cancellation).ConfigureAwait(false);
                result = symbols.Merger.GetMergedSymbol(
                    symbols.Lookup(await host.ExportsAsync(target, cancellation).ConfigureAwait(false), text, meaning));
            }
            if (result is null && !ignoreErrors)
                await host.MissingQualifiedAsync(name!, right, parent, meaning, cancellation).ConfigureAwait(false);
        }
        if (result is not null && result != symbols.UnknownSymbol)
        {
            if ((name!.Flags & NodeFlags.Synthesized) == 0 && name is IdentifierNode or QualifiedNameNode
                && ((result.Flags & S.Alias) != 0 || name.Parent is ExportAssignmentNode))
                aliases.MarkTypeOnly(AliasDeclaration(name));
            while (!dontResolveAlias && (result.Flags & meaning) == 0 && (result.Flags & S.Alias) != 0)
                result = await aliases.ResolveAsync(result, cancellation).ConfigureAwait(false);
        }
        return result;
    }

    internal static SyntaxNode? AliasDeclaration(SyntaxNode node)
    {
        while (node.Parent is QualifiedNameNode parent)
            node = parent;
        return node.Parent is ImportClauseNode or ImportSpecifierNode or NamespaceImportNode or ExportSpecifierNode
            or ExportAssignmentNode or ImportEqualsDeclarationNode or NamespaceExportNode ? node.Parent : null;
    }

    private static bool Missing(SyntaxNode? node)
        => node is null || node.Pos >= 0 && node.Pos == node.End && node.Kind != SyntaxKind.EndOfFile;
}
