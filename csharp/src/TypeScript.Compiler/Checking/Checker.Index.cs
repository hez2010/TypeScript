using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : ITypeKeyHost, IIndexedTypeHost
{
    private readonly HashSet<(SyntaxNode, Type?, Type?, DiagnosticCode)> indexErrors = [];
    internal TypeKeys Keys { get; }
    internal IndexedTypes Indexed { get; }

    public ValueTask<Type> ReducedTypeAsync(Type type, CancellationToken cancellation) => Views.ReducedAsync(type, cancellation);

    public IndexInfo EnumNumberIndex => Members.EnumNumberIndex;
    public bool NoUncheckedIndexedAccess => program.Symbols.Program.Configuration.Options.NoUncheckedIndexedAccess == true;
    public bool NoImplicitAny => program.Symbols.Program.Configuration.Options.StrictNoImplicitAny;

    public ValueTask<Type> IndexedAccessAsync(
        Type objectType,
        Type indexType,
        AccessFlags flags,
        TypeAlias? alias,
        CancellationToken cancellation)
            => Indexed.GetAsync(objectType, indexType, flags, alias: alias, cancellation: cancellation);

    public ValueTask<Type?> ContextualPropertyAsync(Type type, TextSlice name, CancellationToken cancellation)
            => ContextualProperties.GetAsync(type, name, cancellation: cancellation);

    public ValueTask DeprecatedPropertyAsync(Symbol property, SyntaxNode node, CancellationToken cancellation)
        => PropertyDeprecatedAsync(property, node, node is ElementAccessExpressionNode element ? element.ArgumentExpression!
            : node is IndexedAccessTypeNode indexed ? indexed.IndexType! : node, cancellation);

    public async ValueTask InvalidIndexAsync(
        SyntaxNode node,
        Type objectType,
        Type indexType,
        DiagnosticCode code,
        CancellationToken cancellation,
        Type? fullIndex = null,
        TextSlice? suggestion = null)
    {
        var key = (node, code == DiagnosticCode.ATupleTypeCannotBeIndexedWithANegativeValue
            ? null
            : objectType, code == DiagnosticCode.ATupleTypeCannotBeIndexedWithANegativeValue ? null : fullIndex ?? indexType, code);
        if (!indexErrors.Add(key))
            return;
        try
        {
            TextSlice receiver = code is DiagnosticCode.ATupleTypeCannotBeIndexedWithANegativeValue
                or DiagnosticCode.Type0CannotBeUsedAsAnIndexType
                or DiagnosticCode.ElementImplicitlyHasAnAnyTypeBecauseIndexExpressionIsNotOfTypeNumber
                ? ""
                : await TypeDisplay.GetAsync(objectType, cancellation);
            TextSlice index = code is DiagnosticCode.TupleType0OfLength1HasNoElementAtIndex2 or DiagnosticCode.Property0DoesNotExistOnType1
                or DiagnosticCode.Property0DoesNotExistOnType1DidYouMean2
                or DiagnosticCode.Property0DoesNotExistOnType1DidYouMeanToAccessTheStaticMember2Instead
                ? MappedMembers.PropertyName(indexType)
                : "";
            TextSlice[] arguments = code switch
            {
                DiagnosticCode.ATupleTypeCannotBeIndexedWithANegativeValue
                    or DiagnosticCode.ElementImplicitlyHasAnAnyTypeBecauseIndexExpressionIsNotOfTypeNumber => [],
                DiagnosticCode.Property0DoesNotExistOnType1 => [index, receiver],
                DiagnosticCode.TupleType0OfLength1HasNoElementAtIndex2 =>
                    [
                        receiver,
                        CountText(((TupleType)((TypeReference)objectType).Target!).ElementInfos.Count),
                        index
                    ],
                DiagnosticCode.Type0CannotBeUsedToIndexType1 => [await TypeDisplay.GetAsync(indexType, cancellation), receiver],
                DiagnosticCode.Type0HasNoMatchingIndexSignatureForType1 => [receiver, await TypeDisplay.GetAsync(indexType, cancellation)],
                DiagnosticCode.Type0CannotBeUsedAsAnIndexType => [node is BigIntLiteralNode
                    ? "bigint"
                    : await TypeDisplay.GetAsync(indexType, cancellation)],
                DiagnosticCode.Property0DoesNotExistOnType1DidYouMean2 => [index, receiver, suggestion!.Value],
                DiagnosticCode.Property0DoesNotExistOnType1DidYouMeanToAccessTheStaticMember2Instead =>
                    [
                        index,
                        receiver,
TextSlice.ConcatMany(receiver, "[", CheckerDiagnostic.DeclarationName(((ElementAccessExpressionNode)node).ArgumentExpression!), "]")
                    ],
                DiagnosticCode.Type0IsGenericAndCanOnlyBeIndexedForReading => [receiver],
                DiagnosticCode.ElementImplicitlyHasAnAnyTypeBecauseType0HasNoIndexSignatureDidYouMeanToCall1 => [receiver, suggestion!.Value],
                DiagnosticCode.ElementImplicitlyHasAnAnyTypeBecauseExpressionOfType0CanTBeUsedToIndexType1 =>
                    [
                        await TypeDisplay.GetAsync(fullIndex ?? indexType, cancellation),
                        receiver
                    ],
                _ => throw new InvalidOperationException($"Unsupported index diagnostic {code}")
            };
            var diagnostic = CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code), arguments);
            if (code == DiagnosticCode.ElementImplicitlyHasAnAnyTypeBecauseExpressionOfType0CanTBeUsedToIndexType1)
            {
                Diagnostic? reason = null;
                if ((indexType.Flags & TypeFlags.EnumLiteral) != 0)
                {
                    var name = await TypeDisplay.GetAsync(indexType, cancellation);
                    reason = CheckerDiagnostic.Create(node, Messages.Property_0_does_not_exist_on_type_1,
                        TextSlice.Concat("[", name, "]"), receiver);
                }
                else if (indexType is UniqueSymbolType unique)
                    reason = CheckerDiagnostic.Create(node, Messages.Property_0_does_not_exist_on_type_1,
                        TextSlice.Concat("[", TypeDisplay.SymbolName(unique.Symbol!), "]"), receiver);
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
