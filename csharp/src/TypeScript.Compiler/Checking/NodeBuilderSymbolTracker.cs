using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal interface INodeBuilderSymbolTracker
{
    bool TrackSymbol(Symbol symbol, SyntaxNode? enclosingDeclaration, SymbolFlags meaning);

    bool NeedsSymbolAccessibility => false;

    bool TrackAccessibleSymbol(Symbol symbol, SyntaxNode? enclosingDeclaration, SymbolFlags meaning,
        SymbolAccessibilityResult accessibility) => TrackSymbol(symbol, enclosingDeclaration, meaning);

    void ReportInaccessibleThisError();

    void ReportPrivateInBaseOfClassExpression(Utf8String propertyName);

    void ReportInaccessibleUniqueSymbolError();

    void ReportCyclicStructureError();

    void ReportLikelyUnsafeImportRequiredError(Utf8String specifier, Utf8String symbolName);

    void ReportTruncationError();

    void ReportNonlocalAugmentation(SourceFileNode containingFile, Symbol parentSymbol, Symbol augmentingSymbol);

    void ReportNonSerializableProperty(Utf8String propertyName);

    void ReportInferenceFallback(SyntaxNode node);

    void PushErrorFallbackNode(SyntaxNode node);

    void PopErrorFallbackNode();
}
