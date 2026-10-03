using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Projects;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed record DocumentLocation(Utf8String Uri, DocumentRange Range);

public sealed partial class LanguageServiceDocument
{
    internal sealed record ReferenceData(SyntaxNode Node, int Position, IReadOnlyList<ReferenceGroup> Groups);

    public async ValueTask<DocumentLocation[]> GetReferencesAsync(ProjectSnapshot project, DocumentPosition position,
        bool includeDeclaration = true, CancellationToken cancellation = default)
    {
        var data = await ReferenceDataAsync(project, position, new(ReferenceUse.References), cancellation);
        return await ReferenceLocationsAsync(project, data, includeDeclaration, cancellation);
    }
    internal async ValueTask<DocumentLocation[]> ReferenceLocationsAsync(ProjectSnapshot project, IReadOnlyList<ReferenceData> data,
        bool includeDeclaration, CancellationToken cancellation)
    {
        var program = project.Program!;
        var maps = new DeclarationMaps(program, cancellation);
        List<DocumentLocation> locations = [];
        foreach (var query in data)
            foreach (var group in query.Groups)
                foreach (var entry in group.Entries)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!includeDeclaration && DeclarationOfSymbol(entry.Node, group.Symbol)) continue;
                    var (file, start, end) = await ReferenceRangeAsync(entry, cancellation);
                    var location = ReferenceMap(program, maps, file, start, end, MappingFeature.References);
                    if (location.Fidelity is MappingFidelity.Exact or MappingFidelity.Atom) locations.Add(new(location.Uri, location.Range));
                }
        return locations.Distinct().ToArray();
    }

    public async ValueTask<DefinitionLocation[]> GetImplementationsAsync(ProjectSnapshot project, DocumentPosition position,
        bool linkSupport = false, CancellationToken cancellation = default)
    {
        var data = await ReferenceDataAsync(project, position, new(ReferenceUse.References, Implementations: true), cancellation);
        return await ImplementationLocationsAsync(project, data, linkSupport, cancellation);
    }
    internal async ValueTask<DefinitionLocation[]> ImplementationLocationsAsync(ProjectSnapshot project, IReadOnlyList<ReferenceData> data,
        bool linkSupport, CancellationToken cancellation, bool dropOriginNodes = false)
    {
        var program = project.Program!;
        var maps = new DeclarationMaps(program, cancellation);
        HashSet<SyntaxNode?> seen = [];
        List<DefinitionLocation> locations = [];
        foreach (var query in data)
            foreach (var group in query.Groups)
                foreach (var entry in group.Entries)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (dropOriginNodes && entry.Node is { } origin && data.Count != 0 && origin.Pos <= data[0].Position && data[0].Position <= origin.End) continue;
                    if (!seen.Add(entry.Node)) continue;
                    var (file, start, end) = await ReferenceRangeAsync(entry, cancellation);
                    var location = ReferenceMap(program, maps, file, start, end, MappingFeature.Implementation);
                    if (location.Fidelity is not (MappingFidelity.Exact or MappingFidelity.Atom)) continue;
                    var range = location.Range;
                    if (entry.Context is { } context)
                    {
                        var (_, contextStart, contextEnd) = await ReferenceRangeAsync(new(ReferenceEntryKind.Node, context, null, file), cancellation);
                        if (contextStart != start || contextEnd != end)
                        {
                            var full = ReferenceMap(program, maps, file, contextStart, contextEnd, MappingFeature.Implementation);
                            if (full.Fidelity != MappingFidelity.None && full.Uri == location.Uri) range = full.Range;
                        }
                    }
                    var uri = linkSupport ? DocumentUris.FromFileName(ReferenceOriginalFileName(program, file)) : location.Uri;
                    locations.Add(new(uri, range, location.Range, null));
                }
        return locations.DistinctBy(location => (location.Uri, location.SelectionRange)).ToArray();
    }

    internal async ValueTask<IReadOnlyList<ReferenceData>> ReferenceDataAsync(ProjectSnapshot project, DocumentPosition position,
        ReferenceOptions options, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var program = project.Program ?? throw new ArgumentException("Project has no program", nameof(project));
        using var request = new ProjectRequest(cancellation);
        using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request);
        return await ReferenceDataAsync(project, position, options, lease.Checker, cancellation);
    }
    internal async ValueTask<IReadOnlyList<ReferenceData>> ReferenceDataAsync(ProjectSnapshot project, DocumentPosition position,
        ReferenceOptions options, Checker checker, CancellationToken cancellation, (SourceFileNode File, int Position)? sourcePosition = null)
    {
        var files = project.Program!.SourceFiles.Select(file => file.Syntax).ToArray();
        List<ReferenceData> result = [];
        var feature = options.Implementations ? MappingFeature.Implementation : options.Use == ReferenceUse.Rename ? MappingFeature.Rename : MappingFeature.References;
        var candidates = sourcePosition is { } source
            ? new List<(DocumentProjection, MappedPosition)> { (ProjectionForFile(project.Program, source.File), new(source.Position, MappingFidelity.Exact)) }
            : FromPosition(position, feature);
        foreach (var (projection, mapped) in candidates)
        {
            if (mapped.Fidelity is not (MappingFidelity.Exact or MappingFidelity.Atom)) continue;
            var node = await SyntaxNavigation.GetTouchingPropertyNameAsync(projection.File, mapped.Position, cancellation);
            if (options.Use == ReferenceUse.Rename)
            {
                node = await ReferenceNavigation.AdjustedAsync(node, true, cancellation);
                if (!RenameEligible(node)) continue;
            }
            if (options.Implementations && node is SourceFileNode) continue;
            var groups = await checker.GetReferenceGroupsAsync(node, mapped.Position, files, options, cancellation);
            if (options.Implementations)
            {
                List<ReferenceGroup> implementations = [];
                Queue<ReferenceEntry> pending = new();
                HashSet<SyntaxNode?> seenNodes = [];
                HashSet<Symbol?> seenDefinitions = [];
                void Add(IReadOnlyList<ReferenceGroup> found)
                {
                    foreach (var group in found)
                    {
                        var unique = new ReferenceGroup(group.Kind, group.Node, group.Symbol);
                        foreach (var entry in group.Entries)
                            if (seenNodes.Add(entry.Node)) { pending.Enqueue(entry); unique.Entries.Add(entry); }
                        if (unique.Entries.Count != 0 || seenDefinitions.Add(group.Symbol)) implementations.Add(unique);
                    }
                }
                Add(groups);
                while (pending.TryDequeue(out var entry))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (entry.Node is { } reference) Add(await checker.GetReferenceGroupsAsync(reference, reference.Pos, files, options, cancellation));
                }
                groups = implementations;
            }
            result.Add(new(node, mapped.Position, groups));
        }
        return result;
    }
    internal static bool DeclarationOfSymbol(SyntaxNode? node, Symbol? symbol)
    {
        if (node is null || symbol is null) return false;
        var declaration = QuerySyntax.DeclarationName(node) ? node.Parent : node.Kind == K.DefaultKeyword ? node.Parent
            : node is StringLiteralNode or NumericLiteralNode or NoSubstitutionTemplateLiteralNode && node.Parent is ComputedPropertyNameNode
                && QuerySyntax.DeclarationName(node.Parent) ? node.Parent.Parent
            : node.Kind == K.ConstructorKeyword && node.Parent is ConstructorDeclarationNode ? node.Parent.Parent : null;
        return declaration is not null && symbol.Declarations.Contains(declaration);
    }
    internal static async ValueTask<(SourceFileNode File, int Start, int End)> ReferenceRangeAsync(ReferenceEntry entry, CancellationToken cancellation)
    {
        var file = entry.File ?? SemanticSyntax.Source(entry.Node)!;
        if (entry.Start >= 0) return (file, entry.Start, entry.End);
        int start = await SyntaxNavigation.GetStartAsync(entry.Node!, file, cancellation: cancellation), end = entry.Node!.End;
        if (entry.Node is StringLiteralNode or NoSubstitutionTemplateLiteralNode && end - start > 2) { start++; end--; }
        return (file, start, end);
    }
    private (Utf8String Uri, DocumentRange Range, MappingFidelity Fidelity) ReferenceMap(CompilerProgram program, DeclarationMaps maps,
        SourceFileNode file, int start, int end, MappingFeature feature)
    {
        var projection = projections.FirstOrDefault(p => p.File == file) ?? relatedProjections?.GetValueOrDefault(file);
        var source = program.GetFile(file.FileName);
        var original = projection?.OriginalFileName ?? file.FileName;
        if (projection is null && source?.Mapping is not null)
            original = program.SourceFiles.FirstOrDefault(f => f.SupplementalSourceFiles.Contains(file.FileName))?.Syntax.FileName ?? original;
        projection ??= new(file, source?.Mapping, projections[0].Encoding, original);
        if (!projection.IsMapped && maps.MapRange(file.FileName, start, end, projection.Encoding) is { } mapped)
            return (DocumentUris.FromFileName(mapped.FileName), mapped.Range, MappingFidelity.Exact);
        var range = projection.ToRange(start, end, feature);
        return (DocumentUris.FromFileName(original), range.Range, range.Fidelity);
    }
    private Utf8String ReferenceOriginalFileName(CompilerProgram program, SourceFileNode file) =>
        (projections.FirstOrDefault(p => p.File == file) ?? relatedProjections?.GetValueOrDefault(file))?.OriginalFileName
            ?? program.SourceFiles.FirstOrDefault(f => f.SupplementalSourceFiles.Contains(file.FileName))?.Syntax.FileName ?? file.FileName;
}
