using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Projects;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed record Hover(Utf8String Kind, Utf8String Value, DocumentRange? Range, bool CanIncreaseVerbosity = false,
    IReadOnlyList<HoverDisplay>? Displays = null);
public sealed record HoverOptions
{
    public bool Markdown { get; init; }
    public bool SupportsVerbosity { get; init; }
    public bool SupportsVisualStudio { get; init; }
    public int VerbosityLevel { get; init; }
    public int MaximumLength { get; init; } = 500;
}

public sealed partial class LanguageServiceDocument
{
    public async ValueTask<Hover?> GetHoverAsync(ProjectSnapshot project, DocumentPosition position, HoverOptions? options = null,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (project.Program is null) throw new ArgumentException("Project has no program", nameof(project));
        options ??= new();
        using var request = new ProjectRequest(cancellation);
        List<Hover> results = [];
        foreach (var (projection, mapped) in FromPosition(position, MappingFeature.Hover))
        {
            cancellation.ThrowIfCancellationRequested();
            if (mapped.Fidelity is not (MappingFidelity.Exact or MappingFidelity.Atom)) continue;
            var file = projection.File;
            var node = await SyntaxNavigation.GetTouchingPropertyNameAsync(file, mapped.Position, cancellation);
            if (node is SourceFileNode || node is PropertyAccessExpressionNode or QualifiedNameNode && !await InCommentAsync(file, mapped.Position, node, cancellation)) continue;
            using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request, file).ConfigureAwait(false);
            var rangeNode = QuickInfoNode(node);
            var symbol = await QuickInfoSymbolAsync(lease.Checker, rangeNode, cancellation);
            var verbosity = new HoverVerbosity(options.VerbosityLevel, options.MaximumLength > 0 ? options.MaximumLength : 500);
            var info = await SymbolDisplay.QuickInfoAsync(lease.Checker, symbol, rangeNode, verbosity, options.SupportsVisualStudio, cancellation);
            if (info.Text.IsEmpty) continue;
            var docs = new SymbolDocumentation(lease.Checker);
            var documentation = await docs.HoverAsync(symbol, rangeNode, info.Declaration,
                options.Markdown, false, MapLocation, cancellation);
            HoverDisplay[]? displays = options.SupportsVisualStudio && info.Runs.Count != 0
                ? [new(await HoverImageAsync(lease.Checker, symbol, rangeNode, cancellation), info.Runs,
                    (await docs.HoverAsync(symbol, rangeNode, info.Declaration, false, true, MapLocation, cancellation)).TrimStart((byte)'\n'))] : null;
            var text = new Utf8StringBuilder();
            if (options.Markdown) SymbolDocumentation.WriteCode(text, "typescript"u8, info.Text);
            else text.Append(info.Text);
            text.Append(documentation);
            int start = await SyntaxNavigation.GetStartAsync(rangeNode, file, cancellation: cancellation), end = rangeNode.End;
            if (rangeNode is StringLiteralNode or NoSubstitutionTemplateLiteralNode && end - start > 2) { start++; end--; }
            var mappedRange = projection.ToRange(start, end, MappingFeature.Hover);
            results.Add(new(options.Markdown ? "markdown"u8 : "plaintext"u8, text.ToUtf8String(),
                mappedRange.Fidelity is MappingFidelity.Exact or MappingFidelity.Atom ? mappedRange.Range : null,
                options.SupportsVerbosity && verbosity.CanIncrease && !verbosity.Truncated, displays));

            (Utf8String, DocumentRange, MappingFidelity) MapLocation(SourceFileNode source, int start, int end)
            {
                var sourceProjection = projections.FirstOrDefault(p => p.File == source) ?? relatedProjections?.GetValueOrDefault(source);
                var programFile = project.Program!.GetFile(source.FileName);
                var originalName = sourceProjection?.OriginalFileName ?? source.FileName;
                if (sourceProjection is null && programFile?.Mapping is not null)
                    originalName = project.Program.SourceFiles.FirstOrDefault(f => f.SupplementalSourceFiles.Contains(source.FileName))?.Syntax.FileName ?? originalName;
                sourceProjection ??= new(source, programFile?.Mapping, projection.Encoding, originalName);
                var range = sourceProjection.ToRange(start, end, MappingFeature.Hover);
                return (DocumentUris.FromFileName(originalName), range.Range, range.Fidelity);
            }
        }
        if (results.Count == 0) return null;
        if (results.Count == 1) return results[0];
        var first = results[0];
        var values = results.Select(result => result.Value.TrimEnd((byte)'\n')).Distinct();
        return first with
        {
            Value = Utf8String.Join(options.Markdown ? "\n\n---\n\n"u8 : "\n\n"u8, values),
            Range = results.All(result => result.Range == first.Range) ? first.Range : null,
            CanIncreaseVerbosity = results.Any(result => result.CanIncreaseVerbosity),
            Displays = results.DistinctBy(result => result.Value.TrimEnd((byte)'\n')).SelectMany(result => result.Displays ?? []).ToArray(),
        };
    }

    private static SyntaxNode QuickInfoNode(SyntaxNode node) => node.Parent switch
    {
        NewExpressionNode expression when expression.Pos == node.Pos => expression.Expression!,
        NamedTupleMemberNode member when member.Pos == node.Pos => member,
        MetaPropertyNode { KeywordToken: K.ImportKeyword } meta when meta.Name == node => meta,
        JsxNamespacedNameNode jsx => jsx, _ => node,
    };
    private static async ValueTask<Symbol?> QuickInfoSymbolAsync(Checker checker, SyntaxNode node, CancellationToken cancellation)
    {
        var name = node.Parent is ComputedPropertyNameNode && node.Kind is K.StringLiteral or K.NoSubstitutionTemplateLiteral or K.NumericLiteral ? node.Parent : node;
        if (name.Parent is { } element && element.DeclarationName == name && element.Parent is ObjectLiteralExpressionNode or JsxAttributesNode
            && await checker.GetContextualTypeAsync(element.Parent, cancellation: cancellation) is { } contextual
            && await checker.GetPropertySymbolsFromContextualTypeAsync(element, contextual, false, cancellation) is [var property]) return property;
        return await checker.GetSymbolAtLocationAsync(node, cancellation);
    }
    private static async ValueTask<bool> InCommentAsync(SourceFileNode file, int position, SyntaxNode token, CancellationToken cancellation)
    {
        if (Ancestor(token, node => node is JSDocNode)?.Parent is { } owner) token = owner;
        if (await SyntaxNavigation.GetStartAsync(token, file, cancellation: cancellation) <= position && position < token.End) return false;
        var previous = await SyntaxNavigation.FindPrecedingTokenAsync(file, position, cancellation: cancellation);
        bool Contains(SourceCommentRange range) => range.Pos < position && position < range.End
            || position == range.End && (range.Kind == K.SingleLineCommentTrivia || position == file.Source.Length);
        return previous is not null && SyntaxPrinter.CommentRanges(file.Source.Text, previous.End, true).Any(Contains)
            || SyntaxPrinter.CommentRanges(file.Source.Text, token.Pos, false).Any(Contains);
    }
}
