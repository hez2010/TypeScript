using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : IIndexDeclarationHost
{
    public void DuplicatePropertyError(SyntaxNode node, Symbol symbol) => Error(node, DiagnosticCode.DuplicateIdentifier0,
        SemanticSyntax.Name(symbol.ValueDeclaration ?? symbol.Declarations.FirstOrDefault()) is ComputedPropertyNameNode name
            ? CheckerDiagnostic.DeclarationName(name) : TypeDisplay.SymbolName(symbol));

    private readonly HashSet<(SyntaxNode Node, DiagnosticCode Code, Utf8String First, Utf8String Second, Utf8String Third, Utf8String Fourth)> indexConstraintDiagnostics = [];

    public async ValueTask DuplicateIndexErrorAsync(SyntaxNode node, Type type, CancellationToken cancellation)
    {
        Utf8String name = await TypeDisplay.GetAsync(type, cancellation);
        if (indexConstraintDiagnostics.Add((node, DiagnosticCode.DuplicateIndexSignatureForType0, name, Utf8String.Empty, Utf8String.Empty, Utf8String.Empty)))
            TrackDiagnostic(node, DiagnosticCode.DuplicateIndexSignatureForType0, name);
    }

    public async ValueTask IndexPropertyErrorAsync(
        SyntaxNode node,
        Symbol property,
        Type value,
        IndexInfo index,
        CancellationToken cancellation)
    {
        Utf8String name = await SymbolDisplayNameAsync(property, null, SymbolFlags.All, cancellation);
        Utf8String valueText = await TypeDisplay.GetAsync(value, cancellation);
        Utf8String keyText = await TypeDisplay.GetAsync(index.KeyType, cancellation);
        Utf8String indexText = await TypeDisplay.GetAsync(index.ValueType, cancellation);
        if (!indexConstraintDiagnostics.Add(
            (node, DiagnosticCode.Property0OfType1IsNotAssignableTo2IndexType3, name, valueText, keyText, indexText)))
            return;
        var diagnostic = CheckerDiagnostic.Create(node, Messages.Property_0_of_type_1_is_not_assignable_to_2_index_type_3,
            name, valueText, keyText, indexText);
        if (property.ValueDeclaration is { } declaration && declaration != node
            && (declaration is BinaryExpressionNode || SemanticSyntax.Name(declaration) is ComputedPropertyNameNode))
            diagnostic = diagnostic with
            {
                RelatedInformation = [CheckerDiagnostic.Create(
                declaration,
                Messages.X_0_is_declared_here,
                name)]
            };
        Diagnostics.Add(DiagnosticCode.Property0OfType1IsNotAssignableTo2IndexType3);
        diagnosticFiles.Add((node, diagnostic));
    }

    public async ValueTask IndexSignatureErrorAsync(SyntaxNode node, IndexInfo source, IndexInfo target, CancellationToken cancellation)
    {
        Utf8String sourceKey = await TypeDisplay.GetAsync(source.KeyType, cancellation);
        Utf8String sourceValue = await TypeDisplay.GetAsync(source.ValueType, cancellation);
        Utf8String targetKey = await TypeDisplay.GetAsync(target.KeyType, cancellation);
        Utf8String targetValue = await TypeDisplay.GetAsync(target.ValueType, cancellation);
        if (indexConstraintDiagnostics.Add(
            (node, DiagnosticCode.X0IndexType1IsNotAssignableTo2IndexType3, sourceKey, sourceValue, targetKey, targetValue)))
        {
            Diagnostics.Add(DiagnosticCode.X0IndexType1IsNotAssignableTo2IndexType3);
            TrackDiagnostic(node, DiagnosticCode.X0IndexType1IsNotAssignableTo2IndexType3, sourceKey, sourceValue, targetKey, targetValue);
        }
    }

    private async ValueTask CheckIndexSignatureSourceAsync(IndexSignatureDeclarationNode node, CancellationToken cancellation)
    {
        ClassMemberModifiers(node);
        if (TypeScript.Compiler.Binding.SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
            await GrammarAsync().ConfigureAwait(false);
        await FunctionDeclarations.CheckAsync(node, cancellation).ConfigureAwait(false);

        async ValueTask GrammarAsync()
        {
            if (node.Parameters is not { Count: 1 } parameters)
            {
                Error(
                    node.Parameters?.FirstOrDefault() is ParameterDeclarationNode p ? p.Name! : node,
                    DiagnosticCode.AnIndexSignatureMustHaveExactlyOneParameter);
                return;
            }
            var parameter = (ParameterDeclarationNode)parameters[0];
            if (parameters.HasTrailingComma)
                TrailingCommaError(node, parameters, DiagnosticCode.AnIndexSignatureCannotHaveATrailingComma);
            if (parameter.DotDotDotToken is not null)
            {
                Error(parameter.DotDotDotToken, DiagnosticCode.AnIndexSignatureCannotHaveARestParameter);
                return;
            }
            if (parameter.Modifiers is not null)
            {
                Error(parameter.Name!, DiagnosticCode.AnIndexSignatureParameterCannotHaveAnAccessibilityModifier);
                return;
            }
            if (parameter.QuestionToken is not null)
            {
                Error(parameter.QuestionToken, DiagnosticCode.AnIndexSignatureParameterCannotHaveAQuestionMark);
                return;
            }
            if (parameter.Initializer is not null)
            {
                Error(parameter.Name!, DiagnosticCode.AnIndexSignatureParameterCannotHaveAnInitializer);
                return;
            }
            if (parameter.Type is null)
            {
                Error(parameter.Name!, DiagnosticCode.AnIndexSignatureParameterMustHaveATypeAnnotation);
                return;
            }
            var type = await Nodes.FromNodeAsync(parameter.Type, cancellation).ConfigureAwait(false);
            var parts = type is UnionType union ? union.Types : [type];
            if (parts.Any(t => (t.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0)
                || await IsGenericTypeAsync(type, cancellation).ConfigureAwait(false))
            {
                Error(
                    parameter.Name!,
                    DiagnosticCode.AnIndexSignatureParameterTypeCannotBeALiteralTypeOrGenericTypeConsiderUsingAMappedObjectTypeInstead);
                return;
            }
            foreach (var part in parts)
                if (!await Instantiation.Members.ValidIndexKeyAsync(part, cancellation).ConfigureAwait(false))
                {
                    Error(parameter.Name!, DiagnosticCode.AnIndexSignatureParameterTypeMustBeStringNumberSymbolOrATemplateLiteralType);
                    return;
                }
            if (node.Type is null)
                Error(node, DiagnosticCode.AnIndexSignatureMustHaveATypeAnnotation);
        }
    }
}
