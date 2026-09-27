using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal interface INodeBuilderSymbolTracker
{
    bool TrackSymbol(Symbol symbol, SyntaxNode? enclosingDeclaration, SymbolFlags meaning);

    void ReportInaccessibleThisError();

    void ReportPrivateInBaseOfClassExpression(TextSlice propertyName);

    void ReportInaccessibleUniqueSymbolError();

    void ReportCyclicStructureError();

    void ReportLikelyUnsafeImportRequiredError(TextSlice specifier, TextSlice symbolName);

    void ReportTruncationError();

    void ReportNonlocalAugmentation(SourceFileNode containingFile, Symbol parentSymbol, Symbol augmentingSymbol);

    void ReportNonSerializableProperty(TextSlice propertyName);

    void ReportInferenceFallback(SyntaxNode node);

    void PushErrorFallbackNode(SyntaxNode node);

    void PopErrorFallbackNode();
}
