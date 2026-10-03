package requestfilesystem

// Copied only into the pinned oracle archive by the compatibility runner.
import (
    "encoding/json"
    "slices"
    "testing/fstest"
    "io/fs"
    "github.com/microsoft/TypeScript/tsc/internal/project"
    "github.com/microsoft/TypeScript/tsc/internal/tspath"
    "github.com/microsoft/TypeScript/tsc/internal/vfs"
    "github.com/microsoft/TypeScript/tsc/internal/vfs/vfstest"
    "github.com/microsoft/TypeScript/tsc/internal/lsp/lsproto"
)

type CSharpInput struct {
    Name string `json:"name"`
    Cwd string `json:"cwd"`
    CaseSensitive bool `json:"caseSensitive"`
    Files map[string]string `json:"files"`
    Directories []string `json:"directories"`
    Symlinks map[string]string `json:"symlinks"`
    Steps []CSharpStep `json:"steps"`
}
type CSharpStep struct {
    Base int `json:"base"`
    Request *RequestFileSystem `json:"request"`
    Queries []string `json:"queries"`
}
func CSharpReplay(data []byte) any {
    var input CSharpInput
    if err := json.Unmarshal(data, &input); err != nil { panic(err) }
    files := map[string]any{}
    for name, text := range input.Files { files[name] = text }
    for _, name := range input.Directories { if tspath.IsDiskPathRoot(name) { continue }; if _, ok := files[name]; !ok { files[name] = &fstest.MapFile{Mode: fs.ModeDir|0o555} } }
    for name, target := range input.Symlinks { files[name] = &fstest.MapFile{Data: []byte(target), Mode: fs.ModeSymlink|0o777} }
    systems := []vfs.FS{vfstest.FromMap(files, input.CaseSensitive)}
    results := []any{}
    for _, step := range input.Steps {
        var changes project.FileChangeSummary
        value, err := NewForUpdate(step.Request, systems[step.Base], input.Cwd, &changes)
        if err != nil { systems = append(systems, nil); results = append(results, map[string]any{"error": true}); continue }
        systems = append(systems, value)
        s := value.(*requestFileSystem)
        queries := []any{}
        for _, path := range step.Queries { queries = append(queries, csharpQuery(s, path)) }
        paths := func(values map[lsproto.DocumentUri]struct{}) []string {
            result := []string{}; for uri := range values { result = append(result, string(tspath.ToPath(uri.FileName(), input.Cwd, input.CaseSensitive))) }; slices.Sort(result); return result
        }
        _, chained := s.base.(*requestFileSystem)
        results = append(results, map[string]any{"kind": string(s.kind), "compacted": !chained, "queries": queries,
            "changes": map[string]any{"changed": paths(changes.Changed.Keys()), "created": paths(changes.Created.Keys()), "deleted": paths(changes.Deleted.Keys()),
                "invalidateAll": changes.InvalidateAll, "outsideNodeModules": changes.IncludesWatchChangeOutsideNodeModules}})
    }
    return results
}
func csharpQuery(s *requestFileSystem, path string) any {
    entries := s.GetAccessibleEntries(path)
    nonnil := func(value []string) []string { return append([]string{}, value...) }
    links := []string{}; for name := range entries.Symlinks { links = append(links, name) }; slices.Sort(links)
    var content any; if text, ok := s.ReadFile(path); ok { content = text }
    var stat any; if info := s.Stat(path); info != nil { stat = map[string]any{"name": info.Name(), "directory": info.IsDir(), "size": info.Size()} }
    aliases := nonnil(s.aliasesForPath(path)); slices.Sort(aliases)
    return map[string]any{"file": s.FileExists(path), "directory": s.DirectoryExists(path), "content": content, "realpath": s.Realpath(path),
        "files": nonnil(entries.Files), "directories": nonnil(entries.Directories), "symlinks": links, "stat": stat, "aliases": aliases}
}
