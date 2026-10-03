package main

import (
    "bufio"
    "context"
    "encoding/json"
    "os"

    "github.com/microsoft/TypeScript/tsc/internal/bundled"
    "github.com/microsoft/TypeScript/tsc/internal/collections"
    "github.com/microsoft/TypeScript/tsc/internal/compiler"
    "github.com/microsoft/TypeScript/tsc/internal/core"
    "github.com/microsoft/TypeScript/tsc/internal/ls/lsconv"
    "github.com/microsoft/TypeScript/tsc/internal/lsp/lsproto"
    "github.com/microsoft/TypeScript/tsc/internal/project"
    "github.com/microsoft/TypeScript/tsc/internal/tsoptions"
    "github.com/microsoft/TypeScript/tsc/internal/tspath"
    "github.com/microsoft/TypeScript/tsc/internal/vfs/vfstest"
)

type programRequest struct {
    ID string
    RootFiles []string
    CompilerOptions map[string]any
    References []string
}
type step struct {
    Base *int
    Edits map[string]*string
    OpenProjects, CloseProjects, OpenFiles, CloseFiles, RemovePrograms, EnsurePrograms, EnsureFiles []string
    CreatePrograms, ReconfigurePrograms []programRequest
    Changed, Created, Deleted []string
    EnsureAllPrograms bool
    FileSystem map[string]string
    ReplaceFileSystem bool
}
type request struct {
    Name, Cwd string
    CaseSensitive bool
    Files map[string]string
    Queries []string
    Steps []step
}

func main() {
    lines := bufio.NewScanner(os.Stdin); lines.Buffer(make([]byte, 4096), 128*1024*1024)
    out := json.NewEncoder(os.Stdout)
    for lines.Scan() {
        var input request
        if err := json.Unmarshal(lines.Bytes(), &input); err != nil { panic(err) }
        if err := out.Encode(process(input)); err != nil { panic(err) }
    }
    if err := lines.Err(); err != nil { panic(err) }
}

func process(input request) any {
    if input.Cwd == "" { input.Cwd = "/" }
    disk := vfstest.FromMap(input.Files, input.CaseSensitive)
    fs := bundled.WrapFS(disk)
    host := project.NewSnapshotHost(&project.SessionInit{BackgroundCtx: context.Background(), FS: fs,
        Options: &project.SessionOptions{CurrentDirectory: input.Cwd, DefaultLibraryPath: bundled.LibPath(), PositionEncoding: lsproto.PositionEncodingKindUTF8}})
    defer host.Close()
    root := host.NewRootSnapshot(); defer root.Deref()
    snapshots := []*project.Snapshot{root}
    defer func() { for _, snapshot := range snapshots[1:] { if snapshot != nil { snapshot.Deref() } } }()
    programs := map[*compiler.Program]int{}
    results := []any{}
    toPath := func(name string) tspath.Path { return tspath.ToPath(name, input.Cwd, input.CaseSensitive) }
    toURI := func(name string) lsproto.DocumentUri { return lsconv.FileNameToDocumentURI(name) }
    makeProgram := func(value programRequest) *project.APICreateProgramRequest {
        options := &core.CompilerOptions{}
        for name, value := range value.CompilerOptions { tsoptions.ParseCompilerOptions(name, value, options) }
        refs := []*core.ProjectReference{}
        for _, path := range value.References { refs = append(refs, &core.ProjectReference{Path: path}) }
        return &project.APICreateProgramRequest{RootFileNames: value.RootFiles, CompilerOptions: options, ProjectReferences: refs}
    }
    for _, update := range input.Steps {
        base := len(snapshots)-1; if update.Base != nil { base = *update.Base }
        for path, value := range update.Edits { if value == nil { disk.Remove(path) } else { disk.WriteFile(path, *value) } }
        api := &project.APISnapshotRequest{
            OpenProjects: collections.NewSetFromItems(update.OpenProjects...), CloseProjects: collections.NewSetFromItems(core.Map(update.CloseProjects, toPath)...),
            OpenFiles: collections.NewSetFromItems(core.Map(update.OpenFiles, toURI)...), CloseFiles: collections.NewSetFromItems(core.Map(update.CloseFiles, toPath)...),
            EnsureFiles: collections.NewSetFromItems(core.Map(update.EnsureFiles, toURI)...), EnsureAllPrograms: update.EnsureAllPrograms,
            EnsurePrograms: collections.NewSetFromItems(core.Map(update.EnsurePrograms, func(id string) project.ID { return project.ID(id) })...),
            RemovePrograms: collections.NewSetFromItems(core.Map(update.RemovePrograms, func(id string) project.SyntheticProjectID { return project.SyntheticProjectID(id) })...),
            ReplaceFileSystem: update.ReplaceFileSystem,
        }
        // nil sets carry different information from an explicitly present empty array.
        if update.OpenFiles == nil { api.OpenFiles = nil }; if update.CloseFiles == nil { api.CloseFiles = nil }
        if update.OpenProjects == nil { api.OpenProjects = nil }; if update.CloseProjects == nil { api.CloseProjects = nil }
        for _, value := range update.CreatePrograms { api.CreatePrograms = append(api.CreatePrograms, makeProgram(value)) }
        for _, value := range update.ReconfigurePrograms { api.ReconfigurePrograms = append(api.ReconfigurePrograms,
            &project.APIReconfigureProgramRequest{ProgramID: project.SyntheticProjectID(value.ID), APICreateProgramRequest: *makeProgram(value)}) }
        if update.FileSystem != nil { api.FileSystem = bundled.WrapFS(vfstest.FromMap(update.FileSystem, input.CaseSensitive)) }
        change := project.FileChangeSummary{Changed: *collections.NewSetFromItems(core.Map(update.Changed, toURI)...),
            Created: *collections.NewSetFromItems(core.Map(update.Created, toURI)...), Deleted: *collections.NewSetFromItems(core.Map(update.Deleted, toURI)...)}
        snapshot, err := host.CloneSnapshot(context.Background(), snapshots[base], change, api)
        snapshots = append(snapshots, snapshot)
        if err != nil { results = append(results, map[string]any{"error": true}); continue }
        states := []any{}
        for _, p := range snapshot.ProjectCollection.Projects() {
            roots := []string{}; sources := []any{}; identity := 0
            if p.CommandLine != nil { roots = append(roots, p.CommandLine.FileNames()...) }
            if p.Program != nil {
                identity = programs[p.Program]; if identity == 0 { identity = len(programs)+1; programs[p.Program] = identity }
                for _, file := range p.Program.GetSourceFiles() {
                    if p.Program.IsSourceFileDefaultLibrary(file.Path()) { continue }
                    sources = append(sources, map[string]any{"fileName": file.FileName(), "text": file.Text(), "kind": int(file.ScriptKind)})
                }
            }
            config := ""; if p.Kind == project.KindConfigured { config = p.ConfigFileName() }
            states = append(states, map[string]any{"id": p.ID(), "kind": int(p.Kind), "roots": roots,
                "config": config, "program": identity, "dirty": p.IsDirty(), "sources": sources})
        }
        defaults := map[string]string{}
        for _, name := range input.Queries { if p := snapshot.GetDefaultProject(toURI(name)); p != nil { defaults[name] = string(p.ID()) } else { defaults[name] = "" } }
        created := []string{}
        for _, p := range snapshot.CreatedPrograms() { created = append(created, string(p.ID())) }
        // Keep program source order, which is observable through the API.
        results = append(results, map[string]any{"projects": states, "defaults": defaults, "created": created})
    }
    return results
}
