package project

// Installed only in the archived oracle by the original-project replay runner.
// This records snapshot inputs and observations without changing the test assertions.
import (
    "context"
    "encoding/json"
    "maps"
    "os"
    "slices"
    "strings"
    "sync"
    "sync/atomic"

    "github.com/microsoft/TypeScript/tsc/internal/ast"
    "github.com/microsoft/TypeScript/tsc/internal/compiler"
    "github.com/microsoft/TypeScript/tsc/internal/core"
    tsjson "github.com/microsoft/TypeScript/tsc/internal/json"
    "github.com/microsoft/TypeScript/tsc/internal/ls/lsconv"
    "github.com/microsoft/TypeScript/tsc/internal/ls/autoimport"
    "github.com/microsoft/TypeScript/tsc/internal/ls/lsutil"
    "github.com/microsoft/TypeScript/tsc/internal/lsp/lsproto"
    "github.com/microsoft/TypeScript/tsc/internal/tspath"
    "github.com/microsoft/TypeScript/tsc/internal/vfs"
)

type csharpSnapshotRecorder struct {
    name string
    id uint64
    npm map[string]any
    session *Session
    programs map[*compiler.Program]int
    snapshots map[*Snapshot]int
    mu sync.Mutex
}
var csharpSnapshotRecorders sync.Map
var csharpSnapshotOutput sync.Mutex
var csharpSnapshotNext atomic.Uint64
func csharpRecordLogs() bool { return os.Getenv("CSHARP_PROJECT_LOGS") != "" }

func CSharpRegisterSession(name string, session *Session) {
    if os.Getenv("CSHARP_PROJECT_RECORD") == "" { return }
    csharpSnapshotRecorders.Store(session.SnapshotHost, &csharpSnapshotRecorder{name: name, id: csharpSnapshotNext.Add(1), session: session, programs: map[*compiler.Program]int{}, snapshots: map[*Snapshot]int{session.Snapshot(): 0}})
}

func CSharpNewSession(name string, init *SessionInit) *Session {
    session := NewSession(init); CSharpRegisterSession(name, session); return session
}

func CSharpRegisterNpm(session *Session, registry string, packages map[string]string) {
    if value, ok := csharpSnapshotRecorders.Load(session.SnapshotHost); ok {
        recorder := value.(*csharpSnapshotRecorder)
        recorder.npm = map[string]any{"registry": registry, "packages": packages}
    }
}

func CSharpRecordNpmCalls(session *Session, calls any) {
    value, ok := csharpSnapshotRecorders.Load(session.SnapshotHost); if !ok { return }
    recorder := value.(*csharpSnapshotRecorder)
    data, err := json.Marshal(map[string]any{"observation": "npm", "session": recorder.id, "name": recorder.name, "calls": calls})
    if err != nil { panic(err) }
    csharpSnapshotOutput.Lock(); defer csharpSnapshotOutput.Unlock()
    file, err := os.OpenFile(os.Getenv("CSHARP_PROJECT_RECORD"), os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o644)
    if err != nil { panic(err) }; defer file.Close()
    if _, err = file.Write(append(data, '\n')); err != nil { panic(err) }
}

func csharpDiskFiles(fs vfs.FS, cwd string) (map[string]string, map[string]string, []string) {
    result := map[string]string{}
    links := map[string]string{}
    pending := []string{tspath.GetPathComponents(cwd, "")[0]}
    if len(pending[0]) == 0 { pending[0] = "/" }
    visited := map[string]bool{}
    for len(pending) > 0 {
        directory := pending[len(pending)-1]; pending = pending[:len(pending)-1]
        real := fs.Realpath(directory); if visited[real] { continue }; visited[real] = true
        entries := fs.GetAccessibleEntries(directory)
        for _, name := range entries.Files {
            path := tspath.CombinePaths(directory, name)
            if _, linked := entries.Symlinks[name]; linked { links[path] = fs.Realpath(path); path = links[path] }
            if text, ok := fs.ReadFile(path); ok { result[path] = text }
        }
        for _, name := range entries.Directories {
            path := tspath.CombinePaths(directory, name)
            if _, linked := entries.Symlinks[name]; linked { links[path] = fs.Realpath(path); path = links[path] }
            pending = append(pending, path)
        }
    }
    directories := slices.Collect(maps.Keys(visited)); slices.Sort(directories)
    return result, links, directories
}

func csharpCompilerOptions(options *core.CompilerOptions) any {
    if options == nil { return nil }
    data, err := tsjson.Marshal(options); if err != nil { panic(err) }
    return json.RawMessage(data)
}

func csharpRecordBeforeClone(before *Snapshot, change SnapshotChange, overlays map[tspath.Path]*Overlay) func(*Snapshot) {
    value, ok := csharpSnapshotRecorders.Load(before.host)
    if !ok { return func(*Snapshot) {} }
    recorder := value.(*csharpSnapshotRecorder)
    host := before.host
    disk := host.fs; if change.fs != nil { disk = change.fs }
    files, symlinks, directories := csharpDiskFiles(disk, host.options.CurrentDirectory)
    params := map[string]any{}
    api := change.apiRequest
    if api != nil {
        if api.OpenProjects != nil { params["openProjects"] = slices.Collect(maps.Keys(api.OpenProjects.Keys())) }
        if api.CloseProjects != nil { params["closeProjects"] = slices.Collect(maps.Keys(api.CloseProjects.Keys())) }
        if api.OpenFiles != nil { params["openFiles"] = core.Map(slices.Collect(maps.Keys(api.OpenFiles.Keys())), func(uri lsproto.DocumentUri) string { return uri.FileName() }) }
        if api.CloseFiles != nil { params["closeFiles"] = slices.Collect(maps.Keys(api.CloseFiles.Keys())) }
        if api.RemovePrograms != nil { params["removePrograms"] = slices.Collect(maps.Keys(api.RemovePrograms.Keys())) }
        if api.EnsurePrograms != nil { params["ensurePrograms"] = slices.Collect(maps.Keys(api.EnsurePrograms.Keys())) }
        if api.EnsureFiles != nil { params["ensureFiles"] = core.Map(slices.Collect(maps.Keys(api.EnsureFiles.Keys())), func(uri lsproto.DocumentUri) string { return uri.FileName() }) }
        params["ensureAllPrograms"] = api.EnsureAllPrograms
        creates, reconfigures := []any{}, []any{}
        program := func(value *APICreateProgramRequest) map[string]any {
            roots := value.RootFileNames; if roots == nil { roots = []string{} }
            refs := core.Map(value.ProjectReferences, func(reference *core.ProjectReference) string { return reference.Path })
            if refs == nil { refs = []string{} }
            return map[string]any{"rootFiles": roots, "compilerOptions": csharpCompilerOptions(value.CompilerOptions), "references": refs}
        }
        for _, value := range api.CreatePrograms { creates = append(creates, program(value)) }
        for _, value := range api.ReconfigurePrograms { object := program(&value.APICreateProgramRequest); object["id"] = value.ProgramID; reconfigures = append(reconfigures, object) }
        params["createPrograms"] = creates; params["reconfigurePrograms"] = reconfigures
    }
    params["ensureEditorFiles"] = core.Map(change.Documents, func(uri lsproto.DocumentUri) string { return uri.FileName() })
    params["ensureConfiguredFiles"] = core.Map(change.ConfiguredProjectDocuments, func(uri lsproto.DocumentUri) string { return uri.FileName() })
    params["resourceProjects"] = change.Projects
    if change.compilerOptionsForInferredProjects != nil { params["inferredOptions"] = csharpCompilerOptions(change.compilerOptionsForInferredProjects) }
    if change.newConfig != nil { params["preferences"] = change.newConfig; params["locale"] = change.newConfig.Locale; params["customConfigFileName"] = change.newConfig.CustomConfigFileName }
    params["typeAcquisitionDisabled"] = recorder.session.Config().IsATADisabled()
    if len(change.ataChanges) != 0 {
        ids := []string{}; for id := range change.ataChanges { ids = append(ids, string(id)) }; slices.Sort(ids)
        params["acquireTypes"] = ids
    }
    if change.contentMapperContributions != nil {
        mappers := []any{}
        for _, mapper := range change.contentMapperContributions.Mappers {
            mappers = append(mappers, map[string]any{"package": mapper.Package, "extensions": mapper.Extensions,
                "options": json.RawMessage(mapper.Options), "packageDirectory": mapper.PackageDirectory,
                "name": mapper.Name, "version": mapper.Version, "exec": mapper.Exec, "compilerOptions": mapper.CompilerOptions,
                "dynamicConfig": mapper.DynamicConfig, "contributionId": mapper.ContributionID})
        }
        params["inferredContentMappers"] = mappers
    }
    if change.ProjectTree != nil {
        params["loadProjectTrees"] = true
        params["requestedProjectTrees"] = change.ProjectTree.Projects()
    }
    notifications := []any{}
    notification := func(kind FileChangeKind, name string, document *Overlay) {
        event := map[string]any{"kind": int(kind), "fileName": name, "version": 0, "text": "", "scriptKind": 0, "edits": []any{}}
        if document != nil {
            event["version"] = document.Version(); event["text"] = document.Content(); event["scriptKind"] = int(document.Kind())
            if kind == FileChangeKindChange { event["edits"] = []any{map[string]any{"text": document.Content()}} }
        }
        notifications = append(notifications, event)
    }
    for uri := range change.fileChanges.Closed.Keys() { notification(FileChangeKindClose, uri.FileName(), nil) }
    for uri := range change.fileChanges.Changed.Keys() {
        name := uri.FileName(); overlay := overlays[host.toPath(name)]
        if overlay == nil { notification(FileChangeKindWatchChange, name, nil) } else { notification(FileChangeKindChange, name, overlay) }
    }
    for uri := range change.fileChanges.Created.Keys() { notification(FileChangeKindWatchCreate, uri.FileName(), nil) }
    for uri := range change.fileChanges.Deleted.Keys() { notification(FileChangeKindWatchDelete, uri.FileName(), nil) }
    for _, uri := range []lsproto.DocumentUri{change.fileChanges.Opened, change.fileChanges.Reopened} {
        if uri != "" { notification(FileChangeKindOpen, uri.FileName(), overlays[host.toPath(uri.FileName())]) }
    }
    params["fileChanges"] = notifications
    params["replaceFileSystem"] = change.replaceFileSystem
    params["invalidateAll"] = change.fileChanges.InvalidateAll
    if change.fileSystemOverride { params["fileSystem"] = files }
    row := map[string]any{"name": recorder.name, "session": recorder.id, "cwd": host.options.CurrentDirectory, "caseSensitive": disk.UseCaseSensitiveFileNames(),
        "files": files, "symlinks": symlinks, "directories": directories, "params": params, "encoding": string(host.options.PositionEncoding),
        "typingsLocation": host.options.TypingsLocation, "npm": recorder.npm, "pushDiagnostics": recorder.session.options.PushDiagnosticsEnabled,
        "runExternalCode": host.options.RunExternalCode}
    return func(after *Snapshot) {
        recorder.mu.Lock(); defer recorder.mu.Unlock()
        base, known := recorder.snapshots[before]; if !known { panic("unrecorded snapshot parent") }
        next := len(recorder.snapshots); recorder.snapshots[after] = next
        row["base"] = base; row["snapshot"] = next
        if csharpRecordLogs() && after.builderLogs != nil { row["logs"] = after.builderLogs.String() }
        result := map[string]any{}
        if after.apiError != nil { result["error"] = true } else {
            states, created := []any{}, []string{}
            mapped := map[string]any{}
            queries := map[string]string{}
            for _, project := range after.ProjectCollection.Projects() {
                roots, sources, identity, config := []string{}, []any{}, 0, ""
                if project.CommandLine != nil { roots = append(roots, project.CommandLine.FileNames()...) }
                if project.Kind == KindConfigured { config = project.ConfigFileName() }
                if project.Program != nil {
                    identity = recorder.programs[project.Program]
                    if identity == 0 { identity = len(recorder.programs)+1; recorder.programs[project.Program] = identity }
                    for _, file := range project.Program.GetSourceFiles() {
                        if project.Program.IsSourceFileDefaultLibrary(file.Path()) { continue }
                        sources = append(sources, map[string]any{"fileName": file.FileName(), "text": file.Text(), "kind": int(file.ScriptKind)})
                        if file.ContentMapper() != "" {
                            segments := [][]int{}
                            for _, segment := range file.SpanMap().Segments() {
                                segments = append(segments, []int{int(segment.VirtualStart), int(segment.VirtualEnd-segment.VirtualStart),
                                    int(segment.OriginalStart), int(segment.OriginalEnd-segment.OriginalStart), int(segment.Kind), int(segment.Features)})
                            }
                            mapped[file.FileName()] = map[string]any{"text": file.Text(), "extension": ".ts", "mappings": segments}
                        }
                    }
                }
                states = append(states, map[string]any{"id": project.ID(), "kind": int(project.Kind), "roots": roots, "config": config,
                    "program": identity, "dirty": project.IsDirty(), "sources": sources, "updateKind": int(project.ProgramUpdateKind)})
            }
            for name := range files { if strings.HasSuffix(name, ".ts") || strings.HasSuffix(name, ".js") { queries[name] = "" } }
            for _, overlay := range overlays { queries[overlay.FileName()] = "" }
            for name := range queries { if project := after.GetDefaultProject(lsconv.FileNameToDocumentURI(name)); project != nil { queries[name] = string(project.ID()) } }
            queryNames := []string{}; for name := range queries { queryNames = append(queryNames, name) }; slices.Sort(queryNames); row["queries"] = queryNames
            for _, project := range after.CreatedPrograms() { created = append(created, string(project.ID())) }
            pushes := []any{}
            recorder.session.csharpProjectDiagnosticChanges(before, after, func(ctx context.Context, name string, diagnostics []*ast.Diagnostic, converters *lsconv.Converters) {
                values := []*lsproto.Diagnostic{}
                for _, diag := range diagnostics { values = append(values, lsconv.DiagnosticToLSPPush(recorder.session.WithCurrentLocale(ctx), converters, diag)) }
                value, err := tsjson.Marshal(&lsproto.PublishDiagnosticsParams{Uri: lsconv.FileNameToDocumentURI(name), Diagnostics: values})
                if err != nil { panic(err) }; pushes = append(pushes, json.RawMessage(value))
            })
            result["pushes"] = pushes
            result["projects"] = states; result["defaults"] = queries; result["created"] = created
            if len(mapped) != 0 { row["mapperResults"] = mapped }
            if change.AutoImports != "" {
                exports := []any{}
                if project := after.GetDefaultProject(change.AutoImports); project != nil && project.Program != nil &&
                    after.AutoImportRegistry().IsPreparedForImportingFile(change.AutoImports.FileName(), project.ID(), after.GetPreferences(change.AutoImports.FileName())) {
                    params["prepareAutoImports"] = change.AutoImports.FileName()
                    file := project.Program.GetSourceFile(change.AutoImports.FileName())
                    view := autoimport.NewView(after.AutoImportRegistry(), file, project.ID(), project.Program, nil,
                        after.GetPreferences(file.FileName()).ModuleSpecifierPreferences())
                    for _, e := range view.Search("", autoimport.QueryKindWordPrefix) {
                        exports = append(exports, map[string]any{"module": e.ModuleID, "exportName": e.ExportName,
                            "fileName": e.ModuleFileName, "syntax": int(e.Syntax), "flags": uint32(e.Flags), "name": e.Name(),
                            "targetModule": e.Target.ModuleID, "targetName": e.Target.ExportName, "typeOnly": e.IsTypeOnly,
                            "path": e.Path, "packageName": e.PackageName, "kind": int(e.ScriptElementKind),
                            "deprecated": e.ScriptElementKindModifiers & lsutil.ScriptElementKindModifierDeprecated != 0})
                    }
                }
                if params["prepareAutoImports"] != nil { result["autoImports"] = exports }
            }
        }
        row["expected"] = result
        data, err := json.Marshal(row); if err != nil { panic(err) }
        csharpSnapshotOutput.Lock()
        output, err := os.OpenFile(os.Getenv("CSHARP_PROJECT_RECORD"), os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o644)
        if err == nil { _, err = output.Write(append(data, '\n')); output.Close() }
        csharpSnapshotOutput.Unlock()
        if err != nil { panic(err) }
    }
}
