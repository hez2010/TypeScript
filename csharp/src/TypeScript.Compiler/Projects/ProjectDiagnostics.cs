using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Projects;

internal static class ProjectDiagnostics
{
    internal static void Changes(ProjectWorkspaceSnapshot? previous, ProjectWorkspaceSnapshot current,
        Action<ProjectSnapshot, bool> publish)
    {
        var oldOpen = OpenProjects(previous);
        if (current.UserPreferences.EnableValidation == false)
        {
            if (previous?.UserPreferences.EnableValidation != false && previous is not null)
                foreach (var project in previous.Projects)
                    if (oldOpen.Contains(project.Id)) publish(project, false);
            return;
        }
        var newOpen = OpenProjects(current);
        foreach (var project in current.Projects)
            if (previous?.GetProject(project.Id) is null && ShouldPublish(project, current) && newOpen.Contains(project.Id)) publish(project, true);
        if (previous is null) return;
        foreach (var old in previous.Projects)
        {
            if (current.GetProject(old.Id) is not { } project)
            { if (old.Kind == ProjectKind.Configured) publish(old, false); }
            else if (!ReferenceEquals(old, project) && ShouldPublish(project, current) && newOpen.Contains(project.Id)) publish(project, true);
        }
        foreach (var project in current.Projects)
        {
            if (project.Kind != ProjectKind.Configured || previous.GetProject(project.Id) is not { } old) continue;
            bool wasOpen = oldOpen.Contains(project.Id), isOpen = newOpen.Contains(project.Id);
            if (isOpen && !wasOpen && (ReferenceEquals(project, old) || !ShouldPublish(project, current))) publish(project, true);
            else if (!isOpen && wasOpen) publish(project, false);
        }
    }

    private static bool ShouldPublish(ProjectSnapshot project, ProjectWorkspaceSnapshot snapshot) =>
        project.Kind == ProjectKind.Configured && project.Program is not null && project.ProgramLastUpdate == snapshot.Id
            && project.ProgramDiagnosticsChanged && project.ProgramUpdateKind != ProjectProgramUpdateKind.None;

    private static HashSet<Utf8String> OpenProjects(ProjectWorkspaceSnapshot? snapshot)
    {
        HashSet<Utf8String> result = [];
        if (snapshot is null) return result;
        foreach (var path in snapshot.FileSystem.Overlay.Overlays.Keys)
        {
            if (snapshot.GetDefaultProject(path) is { Kind: ProjectKind.Configured } selected) result.Add(selected.Id);
            else foreach (var project in snapshot.Projects)
                if (project.Kind == ProjectKind.Configured && project.ContainsFile(path)) result.Add(project.Id);
        }
        return result;
    }

    internal static DocumentDiagnostic[] Get(ProjectSnapshot project, PositionEncoding encoding, DiagnosticClientOptions options)
    {
        if (project.Program is not { } program) return [];
        Dictionary<Utf8String, DocumentProjection?> projections = [];
        var diagnostics = DiagnosticCollection.SortAndDeduplicate(program.Diagnostics.Concat(project.Resource!.Checkers.GlobalDiagnostics));
        return diagnostics.Select(diagnostic => DiagnosticPresentation.Convert(diagnostic, Projection, options)).ToArray();

        DocumentProjection? Projection(Utf8String? name)
        {
            if (name is not { } fileName) return null;
            if (projections.TryGetValue(fileName, out var cached)) return cached;
            var result = Find(fileName); projections.Add(fileName, result); return result;
        }
        DocumentProjection? Find(Utf8String fileName)
        {
            var comparison = program.UseCaseSensitiveFileNames ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (program.GetFile(fileName) is { } file) return new(file.Syntax, file.Mapping, encoding);
            var config = program.Configuration.SourceFile;
            if (config is not null && config.FileName.Equals(fileName, comparison)) return new(config, null, encoding);
            foreach (var reference in program.ProjectReferences.Projects.Values)
                if (reference.SourceFile is { } source && source.FileName.Equals(fileName, comparison)) return new(source, null, encoding);
            return program.FileSystem is SnapshotFileSystem fs && fs.TryGetCachedDocument(fileName, out var document) && document is not null
                ? new(new ConfigSyntax(fileName, new SourceText(document.Text), [], default).Source, null, encoding) : null;
        }
    }
}
