using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Emission;

internal sealed class DeclarationSymbolTracker(Action<SyntaxNode> inferenceFallback) : INodeBuilderSymbolTracker
{
    private readonly Stack<SyntaxNode> fallbacks = [];
    internal List<Diagnostic> Diagnostics { get; } = [];
    internal List<SyntaxNode> LateStatements { get; } = [];
    internal Func<SymbolAccessibilityResult, DeclarationDiagnostics.Info?>? DiagnosticSelector { get; set; }
    internal SyntaxNode? ErrorName { get; set; }
    internal Symbol? WatchedClassSymbol { get; set; }
    internal bool ClassSymbolTracked { get; set; }
    public bool NeedsSymbolAccessibility => true;

    public bool TrackSymbol(Symbol symbol, SyntaxNode? enclosingDeclaration, SymbolFlags meaning)
    {
        if ((symbol.Flags & SymbolFlags.TypeParameter) != 0)
            return false;
        throw new InvalidOperationException("Declaration symbol tracking requires an accessibility result");
    }

    public bool TrackAccessibleSymbol(Symbol symbol, SyntaxNode? enclosingDeclaration, SymbolFlags meaning,
        SymbolAccessibilityResult accessibility)
    {
        if (symbol == WatchedClassSymbol)
        {
            ClassSymbolTracked = true;
            return false;
        }
        return HandleAccessibility(accessibility);
    }

    internal bool HandleAccessibility(SymbolAccessibilityResult result)
    {
        if (result.Accessibility == SymbolAccessibility.Accessible)
        {
            foreach (var alias in result.AliasesToMakeVisible ?? [])
                if (!LateStatements.Contains(alias))
                    LateStatements.Add(alias);
            return false;
        }
        if (result.Accessibility == SymbolAccessibility.NotResolved)
            return false;
        var info = (DiagnosticSelector ?? throw new InvalidOperationException("Declaration diagnostic has no context"))(result);
        if (info is null)
            return false;
        Diagnostics.Add(info.Name is { } name
            ? CheckerDiagnostic.Create(result.ErrorNode ?? info.Node, info.Message,
                CheckerDiagnostic.DeclarationName(name), result.ErrorSymbolName, result.ErrorModuleName)
            : CheckerDiagnostic.Create(result.ErrorNode ?? info.Node, info.Message, result.ErrorSymbolName, result.ErrorModuleName));
        return true;
    }

    public void PushErrorFallbackNode(SyntaxNode node) => fallbacks.Push(node);
    public void PopErrorFallbackNode() => fallbacks.Pop();
    private SyntaxNode? ErrorLocation => ErrorName ?? (fallbacks.TryPeek(out var node) ? node : null);
    private Utf8String ErrorDeclarationName
    {
        get
        {
            if (ErrorName is { } name)
                return CheckerDiagnostic.DeclarationName(name);
            if (fallbacks.TryPeek(out var node))
            {
                if (DeclarationDiagnostics.Name(node) is { } fallbackName)
                    return CheckerDiagnostic.DeclarationName(fallbackName);
                if (node is ExportAssignmentNode assignment)
                    return assignment.IsExportEquals ? "export="u8 : "default"u8;
            }
            return "(Missing)"u8;
        }
    }

    private void Report(DiagnosticMessage message, params Utf8String[] arguments)
    {
        if (ErrorLocation is { } location)
            Diagnostics.Add(CheckerDiagnostic.Create(location, message, arguments));
    }

    public void ReportInaccessibleThisError() => Report(
        Messages.The_inferred_type_of_0_references_an_inaccessible_1_type_A_type_annotation_is_necessary, ErrorDeclarationName, "this"u8);

    public void ReportInaccessibleUniqueSymbolError() => Report(
        Messages.The_inferred_type_of_0_references_an_inaccessible_1_type_A_type_annotation_is_necessary, ErrorDeclarationName, "unique symbol"u8);

    public void ReportCyclicStructureError() => Report(
        Messages.The_inferred_type_of_0_references_a_type_with_a_cyclic_structure_which_cannot_be_trivially_serialized_A_type_annotation_is_necessary,
        ErrorDeclarationName);

    public void ReportLikelyUnsafeImportRequiredError(Utf8String specifier, Utf8String symbolName)
    {
        if (symbolName.Length != 0)
            Report(Messages.The_inferred_type_of_0_cannot_be_named_without_a_reference_to_2_from_1_This_is_likely_not_portable_A_type_annotation_is_necessary,
                ErrorDeclarationName, specifier, symbolName);
        else
            Report(Messages.The_inferred_type_of_0_cannot_be_named_without_a_reference_to_1_This_is_likely_not_portable_A_type_annotation_is_necessary,
                ErrorDeclarationName, specifier);
    }

    public void ReportTruncationError() => Report(
        Messages.The_inferred_type_of_this_node_exceeds_the_maximum_length_the_compiler_will_serialize_An_explicit_type_annotation_is_needed);

    public void ReportNonSerializableProperty(Utf8String propertyName) => Report(
        Messages.The_type_of_this_node_cannot_be_serialized_because_its_property_0_cannot_be_serialized, propertyName);

    public void ReportPrivateInBaseOfClassExpression(Utf8String propertyName)
    {
        if (ErrorLocation is not { } location)
            return;
        var diagnostic = CheckerDiagnostic.Create(location, Messages.Property_0_of_exported_anonymous_class_type_may_not_be_private_or_protected,
            propertyName);
        if (location.Parent is VariableDeclarationNode)
            diagnostic = diagnostic with { RelatedInformation = [CheckerDiagnostic.Create(location,
                Messages.Add_a_type_annotation_to_the_variable_0, ErrorDeclarationName)] };
        Diagnostics.Add(diagnostic);
    }

    public void ReportNonlocalAugmentation(SourceFileNode containingFile, Symbol parentSymbol, Symbol augmentingSymbol)
    {
        var primary = parentSymbol.Declarations.FirstOrDefault(node => SemanticSyntax.Source(node) == containingFile);
        if (primary is null)
            return;
        foreach (var augmentation in augmentingSymbol.Declarations.Where(node => SemanticSyntax.Source(node) != containingFile))
            Diagnostics.Add(CheckerDiagnostic.Create(augmentation, Messages.Declaration_augments_declaration_in_another_file_This_cannot_be_serialized)
                with { RelatedInformation = [CheckerDiagnostic.Create(primary,
                    Messages.This_is_the_declaration_being_augmented_Consider_moving_the_augmenting_declaration_into_the_same_file)] });
    }

    public void ReportInferenceFallback(SyntaxNode node) => inferenceFallback(node);
}
