using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Projects;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

internal sealed class CrossProjectReferences(ProjectSession session, PositionEncoding encoding, ReferenceOptions options,
    CancellationToken cancellation) : IAsyncDisposable
{
    internal sealed record ProjectResult(ProjectSnapshot Project, LanguageServiceDocument Service, Checker Checker,
        IReadOnlyList<LanguageServiceDocument.ReferenceData> Data, bool Original);
    private sealed record Target(Utf8String FileName, DocumentPosition Position);
    private sealed record Definition(Target Target, Func<Target?> Source, Func<Target?> Generated);
    private readonly List<ProjectWorkspaceSnapshot.Lease> snapshots = [];
    private readonly List<IDisposable> checkerResources = [];
    private readonly Dictionary<Utf8String, ProjectResult?> results = [];
    private readonly Queue<(ProjectSnapshot Project, Target Target, bool Original)> pending = [];
    private Utf8String defaultProject;
    private Utf8String[] initialProjects = [];
    private Definition? definition;

    internal async ValueTask<IReadOnlyList<ProjectResult>> SearchAsync(ProjectWorkspaceSnapshot initial, Utf8String fileName, DocumentPosition position,
        (SourceFileNode File, int Position)? sourcePosition = null)
    {
        snapshots.Add(initial.Acquire());
        if (initial.GetDefaultProject(fileName) is not { } project) return [];
        defaultProject = project.Id;
        initialProjects = initial.Projects.Where(p => p.Kind != ProjectKind.Synthetic && p.ContainsFile(initial.Host.Path(fileName))).Select(p => p.Id).ToArray();
        Enqueue(project, new(fileName, position), false);
        foreach (var id in initialProjects)
            if (id != defaultProject && initial.GetProject(id) is { } other) Enqueue(other, new(fileName, position), false);
        while (true)
        {
            while (pending.TryDequeue(out var item))
            {
                cancellation.ThrowIfCancellationRequested();
                if (item.Project.Program?.GetFile(item.Target.FileName) is not { } file) continue;
                var request = new ProjectRequest(cancellation); checkerResources.Add(request);
                var lease = await item.Project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request); checkerResources.Add(lease);
                var service = new LanguageServiceDocument(item.Project.Program, file, encoding);
                var data = await service.ReferenceDataAsync(item.Project, item.Target.Position, options, lease.Checker, cancellation,
                    item.Project.Id == defaultProject ? sourcePosition : null);
                results[item.Project.Id] = new(item.Project, service, lease.Checker, data, item.Original);
                var maps = new DeclarationMaps(item.Project.Program, cancellation);
                foreach (var group in data.SelectMany(query => query.Groups))
                {
                    if (group.Kind is not (ReferenceDefinitionKind.Symbol or ReferenceDefinitionKind.This) || group.Symbol is not { } symbol) continue;
                    if (item.Project.Id == defaultProject && definition is null)
                        foreach (var declaration in symbol.Declarations)
                            if (await VisibleAsync(lease.Checker, declaration, cancellation) && await DefinitionAsync(item.Project, declaration, maps) is { } found) { definition = found; break; }
                    foreach (var declaration in symbol.Declarations)
                    {
                        var (source, start, _) = await LanguageServiceDocument.ReferenceRangeAsync(ReferenceEntry.FromNode(declaration), cancellation);
                        Target? target = CompilerPath.IsDeclarationFile(source.FileName) ? MappedTarget(maps.SourcePosition(source.FileName, start), maps)
                            : item.Project.IsReferenceSource(source.FileName) ? Point(item.Project, source, start) : null;
                        if (target is null) continue;
                        var snapshot = await session.GetConfiguredSnapshotAsync(target.FileName, cancellation); snapshots.Add(snapshot);
                        foreach (var owner in snapshot.Snapshot.Projects)
                            if (owner.Kind != ProjectKind.Synthetic && owner.ContainsFile(snapshot.Snapshot.Host.Path(target.FileName))) Enqueue(owner, target, true);
                    }
                }
            }
            if (definition is null) break;
            var trees = await session.GetProjectTreeSnapshotAsync(results.Where(pair => pair.Value is not null).Select(pair => pair.Key).ToArray(), cancellation);
            snapshots.Add(trees);
            foreach (var candidate in trees.Snapshot.Projects)
            {
                cancellation.ThrowIfCancellationRequested();
                if (candidate.Kind == ProjectKind.Synthetic || candidate.Program is null || results.ContainsKey(candidate.Id)) continue;
                var target = Contains(candidate, definition.Target) ? definition.Target
                    : definition.Source() is { } source && Contains(candidate, source) ? source
                    : definition.Generated() is { } generated && Contains(candidate, generated) ? generated : null;
                if (target is not null) Enqueue(candidate, target, false);
            }
            if (pending.Count == 0) break;
        }
        List<ProjectResult> ordered = [];
        HashSet<Utf8String> seen = [];
        void Add(Utf8String id)
        { if (seen.Add(id) && results.GetValueOrDefault(id) is { } result) ordered.Add(result); }
        Add(defaultProject);
        foreach (var id in initialProjects) Add(id);
        foreach (var (id, result) in results) if (result?.Original == false) Add(id);
        foreach (var (id, result) in results) if (result?.Original == true) Add(id);
        return ordered;
    }
    private void Enqueue(ProjectSnapshot project, Target target, bool original)
    { if (results.TryAdd(project.Id, null)) pending.Enqueue((project, target, original)); }
    private static bool Contains(ProjectSnapshot project, Target target)
    {
        if (project.Program is not { } program) return false;
        var path = CompilerPath.Resolve(program.CurrentDirectory, target.FileName);
        return project.ContainsFile(program.UseCaseSensitiveFileNames ? path : path.ToLowerInvariant());
    }
    private static async ValueTask<bool> VisibleAsync(Checker checker, SyntaxNode? declaration, CancellationToken cancellation)
    {
        for (; declaration is not null; declaration = declaration.Parent)
        {
            if (await checker.IsDeclarationVisibleAsync(declaration, cancellation)) return true;
            if (declaration.Parent is IInitializedNode initialized && initialized.Initializer == declaration) continue;
            if (declaration is PropertyDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode or MethodDeclarationNode)
            {
                if (SemanticSyntax.HasModifier(declaration, K.PrivateKeyword) || declaration.DeclarationName is PrivateIdentifierNode) return false;
                continue;
            }
            if (declaration is not (ConstructorDeclarationNode or PropertyAssignmentNode or ShorthandPropertyAssignmentNode or ObjectLiteralExpressionNode
                or ClassExpressionNode or ArrowFunctionNode or FunctionExpressionNode)) return false;
        }
        return false;
    }
    private async ValueTask<Definition?> DefinitionAsync(ProjectSnapshot project, SyntaxNode declaration, DeclarationMaps maps)
    {
        var (file, start, _) = await LanguageServiceDocument.ReferenceRangeAsync(ReferenceEntry.FromNode(declaration), cancellation);
        if (Point(project, file, start) is not { } target) return null;
        Target? source = null, generated = null;
        bool sourceRead = false, generatedRead = false;
        return new(target,
            () => { if (!sourceRead) { sourceRead = true; source = MappedTarget(maps.SourcePosition(file.FileName, start), maps); } return source; },
            () => { if (!generatedRead) { generatedRead = true; generated = MappedTarget(maps.GeneratedPosition(file.FileName, start), maps); } return generated; });
    }
    private Target? Point(ProjectSnapshot project, SourceFileNode file, int offset)
    {
        var original = project.Program!.SourceFiles.FirstOrDefault(source => source.SupplementalSourceFiles.Contains(file.FileName))?.Syntax.FileName ?? file.FileName;
        var projection = new DocumentProjection(file, project.Program.GetFile(file.FileName)?.Mapping, encoding, original);
        var mapped = projection.ToRange(offset, offset);
        return mapped.Fidelity == MappingFidelity.None ? null : new(original, mapped.Range.Start);
    }
    private Target? MappedTarget(DeclarationMaps.Position? position, DeclarationMaps maps) => position is { } mapped && maps.Read(mapped.FileName) is { } text
        ? new(mapped.FileName, new DocumentLineMap(text.Text).ToPosition(mapped.Offset, encoding)) : null;

    internal static async ValueTask<CallHierarchyCall[]?> IncomingCallsAsync(ProjectSession session, ProjectWorkspaceSnapshot snapshot,
        Utf8String fileName, DocumentPosition position, PositionEncoding encoding, CancellationToken cancellation)
    {
        if (snapshot.GetDefaultProject(fileName) is not { Program: { } program } project || program.GetFile(fileName) is not { } file) return null;
        var service = new LanguageServiceDocument(program, file, encoding);
        DocumentLocation[] targets;
        using (var request = new ProjectRequest(cancellation))
        using (var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request))
            targets = await service.IncomingCallTargetsAsync(project, position, lease.Checker, cancellation);
        List<CallHierarchyCall> calls = [];
        foreach (var target in targets)
        {
            await using var search = new CrossProjectReferences(session, encoding, new(ReferenceUse.References), cancellation);
            List<CallHierarchyCall> combined = [];
            HashSet<(Utf8String, DocumentRange)> seen = [];
            foreach (var result in await search.SearchAsync(snapshot, DocumentUris.ToFileName(target.Uri), target.Range.Start))
                foreach (var call in await result.Service.IncomingCallsAsync(result.Project, result.Data, result.Checker, cancellation))
                    if (seen.Add((call.Item.Uri, call.Item.Range))) combined.Add(call);
            LanguageServiceDocument.MergeCallHierarchy(calls, LanguageServiceDocument.SortCallHierarchy(combined));
        }
        return calls.Count == 0 ? null : calls.ToArray();
    }

    internal static async ValueTask<CodeLens> ResolveCodeLensAsync(ProjectSession session, ProjectWorkspaceSnapshot snapshot,
        CodeLens lens, Utf8String? showLocationsCommand, PositionEncoding encoding, CancellationToken cancellation)
    {
        var fileName = DocumentUris.ToFileName(lens.Data.Uri);
        var project = snapshot.GetDefaultProject(fileName)!;
        var service = new LanguageServiceDocument(project.Program!, project.Program!.GetFile(fileName)!, encoding);
        var source = service.CodeLensSource(lens.Data);
        bool implementations = lens.Data.Kind == "implementations"u8;
        List<DocumentLocation> locations = [];
        if (implementations || lens.Data.Kind == "references"u8)
        {
            await using var search = new CrossProjectReferences(session, encoding, new(ReferenceUse.References, Implementations: implementations), cancellation);
            foreach (var found in await search.SearchAsync(snapshot, fileName, lens.Range.Start, (source, lens.Data.Position)))
                if (implementations)
                    locations.AddRange((await found.Service.ImplementationLocationsAsync(found.Project, found.Data, false, cancellation, dropOriginNodes: true))
                        .Select(item => new DocumentLocation(item.Uri, item.SelectionRange)));
                else locations.AddRange(await found.Service.ReferenceLocationsAsync(found.Project, found.Data, false, cancellation));
        }
        return LanguageServiceDocument.ResolveCodeLens(lens, locations.Distinct().ToArray(), showLocationsCommand);
    }

    internal static async ValueTask<DocumentLocation[]> ReferencesAsync(ProjectSession session, ProjectWorkspaceSnapshot snapshot,
        Utf8String fileName, DocumentPosition position, bool includeDeclaration, PositionEncoding encoding, CancellationToken cancellation)
    {
        await using var search = new CrossProjectReferences(session, encoding, new(ReferenceUse.References), cancellation);
        List<DocumentLocation> result = [];
        foreach (var project in await search.SearchAsync(snapshot, fileName, position))
            result.AddRange(await project.Service.ReferenceLocationsAsync(project.Project, project.Data, includeDeclaration, cancellation));
        return result.Distinct().ToArray();
    }
    internal static async ValueTask<DefinitionLocation[]> ImplementationsAsync(ProjectSession session, ProjectWorkspaceSnapshot snapshot,
        Utf8String fileName, DocumentPosition position, bool linkSupport, PositionEncoding encoding, CancellationToken cancellation)
    {
        await using var search = new CrossProjectReferences(session, encoding, new(ReferenceUse.References, Implementations: true), cancellation);
        List<DefinitionLocation> result = [];
        foreach (var project in await search.SearchAsync(snapshot, fileName, position))
            result.AddRange(await project.Service.ImplementationLocationsAsync(project.Project, project.Data, linkSupport, cancellation));
        return result.DistinctBy(location => (location.Uri, location.SelectionRange)).ToArray();
    }

    internal static async ValueTask<VisualStudioReference[]> VisualStudioReferencesAsync(ProjectSession session, ProjectWorkspaceSnapshot snapshot,
        Utf8String fileName, DocumentPosition position, bool classified, PositionEncoding encoding, CancellationToken cancellation)
    {
        await using var search = new CrossProjectReferences(session, encoding, new(ReferenceUse.References), cancellation);
        List<VisualStudioReference> result = [];
        foreach (var project in await search.SearchAsync(snapshot, fileName, position))
            result.AddRange(await project.Service.VisualStudioReferencesAsync(project.Project, project.Data, project.Checker, classified, result.Count, cancellation));
        return result.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        for (int i = checkerResources.Count - 1; i >= 0; i--) checkerResources[i].Dispose();
        for (int i = snapshots.Count - 1; i >= 0; i--) await snapshots[i].DisposeAsync();
    }

    internal static async ValueTask<WorkspaceEdit?> RenameAsync(ProjectSession session, ProjectWorkspaceSnapshot snapshot, Utf8String fileName,
        DocumentPosition position, Utf8String newName, RenameCapabilities capabilities, PositionEncoding encoding, CancellationToken cancellation)
    {
        var preferences = snapshot.UserPreferences;
        if (snapshot.GetDefaultProject(fileName) is { Program: { } program } owner && program.GetFile(fileName) is { } file)
        {
            var service = new LanguageServiceDocument(program, file, encoding);
            var info = await service.GetRenameInfoAsync(owner, position, newName, preferences, capabilities, cancellation);
            if (info.CanRename && !info.FileToRename.IsEmpty)
            {
                var rename = new RenameFileChange(DocumentUris.FromFileName(info.FileToRename), DocumentUris.FromFileName(info.NewFileName));
                if (capabilities.WillRenameFiles) return new(DocumentChanges: [rename]);
                return await RenameFilesAsync(session, [rename], capabilities, encoding, true, cancellation);
            }
        }
        await using var search = new CrossProjectReferences(session, encoding, new(ReferenceUse.Rename, UseAliasesForRename: preferences.UseAliasesForRename), cancellation);
        Dictionary<Utf8String, List<DocumentTextEdit>> changes = [];
        HashSet<(Utf8String, DocumentRange)> seen = [];
        foreach (var project in await search.SearchAsync(snapshot, fileName, position))
        {
            var edits = await project.Service.RenameEditsAsync(project.Project, project.Data, project.Checker, newName, preferences, capabilities, cancellation);
            foreach (var (uri, items) in edits?.Changes ?? new Dictionary<Utf8String, IReadOnlyList<DocumentTextEdit>>())
                foreach (var item in items)
                    if (seen.Add((uri, item.Range)))
                    {
                        if (!changes.TryGetValue(uri, out var target)) changes.Add(uri, target = []);
                        target.Add(item);
                    }
        }
        return changes.Count == 0 ? null : new(changes.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<DocumentTextEdit>)pair.Value));
    }

    internal static async ValueTask<WorkspaceEdit?> RenameFilesAsync(ProjectSession session, IReadOnlyList<RenameFileChange> files,
        RenameCapabilities capabilities, PositionEncoding encoding, bool sendRenameFile, CancellationToken cancellation)
    {
        if (files.Count == 0) return null;
        await using var snapshot = await session.GetWorkspaceSnapshotForFilesAsync(files.Select(file => DocumentUris.ToFileName(file.OldUri)).ToArray(), cancellation);
        List<WorkspaceDocumentChange> pending = [];
        foreach (var project in snapshot.Snapshot.Projects)
        {
            if (project.Kind == ProjectKind.Synthetic || project.Program is not { } program) continue;
            var service = program.SourceFiles.FirstOrDefault() is { } source ? new LanguageServiceDocument(program, source, encoding)
                : program.Configuration.SourceFile is { } config ? new LanguageServiceDocument(config, encoding) : null;
            if (service is null) continue;
            foreach (var file in files)
                pending.AddRange(await service.GetEditsForFileRenameAsync(project, DocumentUris.ToFileName(file.OldUri), DocumentUris.ToFileName(file.NewUri), snapshot.Snapshot.UserPreferences, cancellation));
        }

        return CombineFileRenames(pending, files, capabilities, sendRenameFile);
    }

    internal static WorkspaceEdit? CombineFileRenames(IEnumerable<WorkspaceDocumentChange> pending, IReadOnlyList<RenameFileChange> files,
        RenameCapabilities capabilities, bool sendRenameFile)
    {
        List<WorkspaceDocumentChange> changes = [];
        Dictionary<(Utf8String, DocumentRange), Utf8String> seenEdits = [];
        HashSet<Utf8String> seenRenames = [];
        foreach (var change in pending)
        {
            if (change is RenameFileChange rename) { if (seenRenames.Add(rename.OldUri)) changes.Add(rename); }
            else if (change is TextDocumentChange text)
            {
                List<DocumentTextEdit> edits = [];
                foreach (var edit in text.Edits)
                {
                    var key = (text.Uri, edit.Range);
                    if (seenEdits.TryGetValue(key, out var previous) && previous == edit.NewText) continue;
                    seenEdits[key] = edit.NewText; edits.Add(edit);
                }
                if (edits.Count != 0) changes.Add(text with { Edits = edits });
            }
        }
        if (sendRenameFile) changes.AddRange(files);
        if (changes.Count == 0) return null;
        if (capabilities.DocumentChanges) return new(DocumentChanges: changes);
        return new(changes.OfType<TextDocumentChange>().GroupBy(change => change.Uri)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<DocumentTextEdit>)group.SelectMany(change => change.Edits).ToArray()));
    }
}
