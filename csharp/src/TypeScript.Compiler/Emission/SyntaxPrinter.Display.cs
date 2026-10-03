using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Emission;

public sealed partial class SyntaxPrinter
{
    internal static IReadOnlyList<ClassifiedTextRun> PrintDisplay(SyntaxNode node, SourceFileNode? source,
        IReadOnlyDictionary<SyntaxNode, Symbol> symbols, EmitContext context, CancellationToken cancellation)
    {
        var positions = new PrintPositions();
        var printer = new SyntaxPrinter(context: context) { positions = positions };
        var writer = new EmitTextWriter(inlineDisplay: true);
        printer.Write(node, source, writer, cancellation: cancellation);
        var text = writer.Text;
        var scanner = new Scanner(new(text), false);
        Dictionary<int, Utf8String> identifiers = [];
        Dictionary<SyntaxNode, Utf8String> roles = [];
        foreach (var child in node.DescendantsAndSelf())
        {
            if (child is IdentifierNode && !roles.ContainsKey(child)) roles[child] = "text"u8;
            if (child is ParameterDeclarationNode { Name: { } name })
                foreach (var binding in name.DescendantsAndSelf().OfType<IdentifierNode>()) roles[binding] = "parameter name"u8;
            var property = child switch
            {
                PropertySignatureDeclarationNode n => n.Name, PropertyDeclarationNode n => n.Name,
                MethodSignatureDeclarationNode n => n.Name, MethodDeclarationNode n => n.Name,
                GetAccessorDeclarationNode n => n.Name, SetAccessorDeclarationNode n => n.Name,
                PropertyAssignmentNode n => n.Name, ShorthandPropertyAssignmentNode n => n.Name,
                BindingElementNode n => n.PropertyName, _ => null,
            };
            if (property is IdentifierNode) roles[property] = "property name"u8;
        }
        foreach (var (identifier, role) in roles)
            if (positions.TryGetRange(identifier, out var range) && range.End > range.Start)
                identifiers[new Scanner(new(text)).SkipTriviaAt(range.Start)] = role;
        foreach (var (identifier, symbol) in symbols)
            if (identifier is IdentifierNode && positions.TryGetRange(identifier, out var range) && range.End > range.Start)
                identifiers[new Scanner(new(text)).SkipTriviaAt(range.Start)] = DisplayParts.Classify(symbol);
        List<ClassifiedTextRun> result = [];
        for (var token = scanner.Scan(); token != SyntaxKind.EndOfFile; token = scanner.Scan())
        {
            cancellation.ThrowIfCancellationRequested();
            var value = text[scanner.TokenStart..scanner.Position];
            Utf8String classification = token switch
            {
                SyntaxKind.WhitespaceTrivia or SyntaxKind.NewLineTrivia => "whitespace"u8,
                SyntaxKind.StringLiteral or SyntaxKind.NoSubstitutionTemplateLiteral or SyntaxKind.NumericLiteral or SyntaxKind.BigIntLiteral
                    or SyntaxKind.TemplateHead or SyntaxKind.TemplateMiddle or SyntaxKind.TemplateTail => "string"u8,
                SyntaxKind.Identifier => identifiers.GetValueOrDefault(scanner.TokenStart, "text"u8),
                SyntaxKind.EqualsToken or SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.AsteriskToken => "operator"u8,
                >= SyntaxKind.FirstKeyword and <= SyntaxKind.LastKeyword => identifiers.GetValueOrDefault(scanner.TokenStart, "keyword"u8),
                SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia => "text"u8,
                _ => "punctuation"u8,
            };
            result.Add(new(classification, value));
        }
        return result;
    }
}
