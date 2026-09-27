using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : ITypeKeyHost, IIndexedTypeHost
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

    public async ValueTask InvalidIndexAsync(SyntaxNode node, Type objectType, Type indexType, int code, CancellationToken cancellation,
        Type? fullIndex = null, string? suggestion = null)
    {
        var key = (node, code == 2514 ? null : objectType, code == 2514 ? null : fullIndex ?? indexType, code);
        if (!indexErrors.Add(key))
            return;
        try
        {
            string receiver = code is 2514 or 2538 or 7015 ? "" : await TypeDisplay.GetAsync(objectType, cancellation);
            string index = code is 2493 or 2339 or 2551 or 2576 ? MappedMembers.PropertyName(indexType) : "";
            string[] arguments = code switch
            {
                2514 or 7015 => [],
                2339 => [index, receiver],
                2493 => [receiver, CountText(((TupleType)((TypeReference)objectType).Target!).ElementInfos.Count), index],
                2536 => [await TypeDisplay.GetAsync(indexType, cancellation), receiver],
                2537 => [receiver, await TypeDisplay.GetAsync(indexType, cancellation)],
                2538 => [node is BigIntLiteralNode ? "bigint" : await TypeDisplay.GetAsync(indexType, cancellation)],
                2551 => [index, receiver, suggestion!],
                2576 =>
                    [
                        index,
                        receiver,
                        receiver + "[" + CheckerDiagnostic.DeclarationName(((ElementAccessExpressionNode)node).ArgumentExpression!) + "]"
                    ],
                2862 => [receiver],
                7052 => [receiver, suggestion!],
                7053 => [await TypeDisplay.GetAsync(fullIndex ?? indexType, cancellation), receiver],
                _ => throw new InvalidOperationException($"Unsupported index diagnostic {code}")
            };
            var diagnostic = CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code), arguments);
            if (code == 7053)
            {
                Diagnostic? reason = null;
                if ((indexType.Flags & TypeFlags.EnumLiteral) != 0)
                    reason = CheckerDiagnostic.Create(node, Messages.Property_0_does_not_exist_on_type_1,
                        "[" + await TypeDisplay.GetAsync(indexType, cancellation) + "]", receiver);
                else if (indexType is UniqueSymbolType unique)
                    reason = CheckerDiagnostic.Create(node, Messages.Property_0_does_not_exist_on_type_1,
                        "[" + TypeDisplay.SymbolName(unique.Symbol!) + "]", receiver);
                else if ((indexType.Flags & TypeFlags.StringOrNumberLiteral) != 0)
                    reason = CheckerDiagnostic.Create(node, Messages.Property_0_does_not_exist_on_type_1,
                        MappedMembers.PropertyName(indexType), receiver);
                else if ((indexType.Flags & (TypeFlags.Number | TypeFlags.String)) != 0)
                    reason = CheckerDiagnostic.Create(node, Messages.No_index_signature_with_a_parameter_of_type_0_was_found_on_type_1,
                        await TypeDisplay.GetAsync(indexType, cancellation), receiver);
                if (reason is not null)
                    diagnostic = diagnostic with { MessageChain = [reason] };
            }
            cancellation.ThrowIfCancellationRequested();
            Diagnostics.Add(code);
            diagnosticFiles.Add((node, diagnostic));
        }
        catch
        {
            indexErrors.Remove(key);
            throw;
        }
    }
}
