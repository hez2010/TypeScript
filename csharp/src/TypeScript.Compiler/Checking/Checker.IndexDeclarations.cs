using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : IIndexDeclarationHost
{
    public void DuplicatePropertyError(SyntaxNode node, Symbol symbol) => Error(node, 2300,
        SemanticSyntax.Name(symbol.ValueDeclaration ?? symbol.Declarations.FirstOrDefault()) is ComputedPropertyNameNode name
            ? CheckerDiagnostic.DeclarationName(name) : TypeDisplay.SymbolName(symbol));

    private readonly HashSet<(SyntaxNode Node, int Code, string First, string Second, string Third, string Fourth)> indexConstraintDiagnostics = [];

    public async ValueTask DuplicateIndexErrorAsync(SyntaxNode node, Type type, CancellationToken cancellation)
    {
        string name = await TypeDisplay.GetAsync(type, cancellation);
        if (indexConstraintDiagnostics.Add((node, 2374, name, "", "", "")))
            TrackDiagnostic(node, 2374, name);
    }

    public async ValueTask IndexPropertyErrorAsync(
        SyntaxNode node,
        Symbol property,
        Type value,
        IndexInfo index,
        CancellationToken cancellation)
    {
        string name = await SymbolDisplayNameAsync(property, null, SymbolFlags.All, cancellation);
        string valueText = await TypeDisplay.GetAsync(value, cancellation);
        string keyText = await TypeDisplay.GetAsync(index.KeyType, cancellation);
        string indexText = await TypeDisplay.GetAsync(index.ValueType, cancellation);
        if (!indexConstraintDiagnostics.Add((node, 2411, name, valueText, keyText, indexText)))
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
        Diagnostics.Add(2411);
        diagnosticFiles.Add((node, diagnostic));
    }

    public async ValueTask IndexSignatureErrorAsync(SyntaxNode node, IndexInfo source, IndexInfo target, CancellationToken cancellation)
    {
        string sourceKey = await TypeDisplay.GetAsync(source.KeyType, cancellation);
        string sourceValue = await TypeDisplay.GetAsync(source.ValueType, cancellation);
        string targetKey = await TypeDisplay.GetAsync(target.KeyType, cancellation);
        string targetValue = await TypeDisplay.GetAsync(target.ValueType, cancellation);
        if (indexConstraintDiagnostics.Add((node, 2413, sourceKey, sourceValue, targetKey, targetValue)))
        {
            Diagnostics.Add(2413);
            TrackDiagnostic(node, 2413, sourceKey, sourceValue, targetKey, targetValue);
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
                Error(node.Parameters?.FirstOrDefault() is ParameterDeclarationNode p ? p.Name! : node, 1096);
                return;
            }
            var parameter = (ParameterDeclarationNode)parameters[0];
            if (parameters.HasTrailingComma)
                TrailingCommaError(node, parameters, 1025);
            if (parameter.DotDotDotToken is not null)
            {
                Error(parameter.DotDotDotToken, 1017);
                return;
            }
            if (parameter.Modifiers is not null)
            {
                Error(parameter.Name!, 1018);
                return;
            }
            if (parameter.QuestionToken is not null)
            {
                Error(parameter.QuestionToken, 1019);
                return;
            }
            if (parameter.Initializer is not null)
            {
                Error(parameter.Name!, 1020);
                return;
            }
            if (parameter.Type is null)
            {
                Error(parameter.Name!, 1022);
                return;
            }
            var type = await Nodes.FromNodeAsync(parameter.Type, cancellation).ConfigureAwait(false);
            var parts = type is UnionType union ? union.Types : [type];
            if (parts.Any(t => (t.Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0)
                || await IsGenericTypeAsync(type, cancellation).ConfigureAwait(false))
            {
                Error(parameter.Name!, 1337);
                return;
            }
            foreach (var part in parts)
                if (!await Instantiation.Members.ValidIndexKeyAsync(part, cancellation).ConfigureAwait(false))
                {
                    Error(parameter.Name!, 1268);
                    return;
                }
            if (node.Type is null)
                Error(node, 1021);
        }
    }
}
