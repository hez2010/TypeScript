package requestfilesystem

// Original test inputs are captured without changing assertions or reading tracking wrappers.
import (
    "encoding/json"
    "os"
    "slices"
    "sync"
    "github.com/microsoft/TypeScript/tsc/internal/project"
    "github.com/microsoft/TypeScript/tsc/internal/tspath"
    "github.com/microsoft/TypeScript/tsc/internal/vfs"
    "github.com/microsoft/TypeScript/tsc/internal/vfs/trackingvfs"
)
var csharpRequests sync.Map
var csharpRequestOutput sync.Mutex
func csharpUntracked(value vfs.FS) vfs.FS {
    switch value := value.(type) {
    case *trackingvfs.FS: return csharpUntracked(value.Inner)
    case *countingLayeredFileSystem: return csharpUntracked(value.LayeredFileSystem)
    default: return value
    }
}
func csharpRecordRequest(name string, params *RequestFileSystem, base vfs.FS, cwd string, result vfs.FS) {
    if os.Getenv("CSHARP_REQUEST_FS_RECORD") == "" { return }
    input := CSharpInput{Name: name, Cwd: cwd, CaseSensitive: base.UseCaseSensitiveFileNames(), Files: map[string]string{}, Symlinks: map[string]string{}}
    if previous, ok := base.(*requestFileSystem); ok {
        stored, found := csharpRequests.Load(previous); if !found { panic("uncaptured request filesystem base") }
        input = stored.(CSharpInput); input.Name = name; input.Steps = slices.Clone(input.Steps)
    } else {
        disk := csharpUntracked(base)
        roots := map[string]bool{tspath.GetPathComponents(cwd, "")[0]: true, "/": true, "C:/": true, "c:/": true}
        for name := range params.Files { roots[tspath.GetPathComponents(tspath.GetNormalizedAbsolutePath(name, cwd), "")[0]] = true }
        var pending []string; for root := range roots { if root != "" { pending = append(pending, root) } }
        visited := map[string]bool{}
        for len(pending) > 0 {
            directory := pending[len(pending)-1]; pending = pending[:len(pending)-1]
            real := disk.Realpath(directory); canonical := string(tspath.ToPath(real, cwd, input.CaseSensitive))
            if visited[canonical] { continue }; visited[canonical] = true
            if disk.DirectoryExists(directory) { input.Directories = append(input.Directories, directory) }
            entries := disk.GetAccessibleEntries(directory)
            for _, child := range entries.Files {
                path := tspath.CombinePaths(directory, child)
                if _, linked := entries.Symlinks[child]; linked { input.Symlinks[path] = disk.Realpath(path); path = input.Symlinks[path] }
                if text, ok := disk.ReadFile(path); ok { input.Files[path] = text }
            }
            for _, child := range entries.Directories {
                path := tspath.CombinePaths(directory, child)
                if _, linked := entries.Symlinks[child]; linked { input.Symlinks[path] = disk.Realpath(path); path = input.Symlinks[path] }
                pending = append(pending, path)
            }
        }
    }
    encoded, err := json.Marshal(params); if err != nil { panic(err) }; owned := &RequestFileSystem{}
    if err := json.Unmarshal(encoded, owned); err != nil { panic(err) }
    input.Steps = append(input.Steps, CSharpStep{Base: len(input.Steps), Request: owned})
    if result != nil { csharpRequests.Store(result, input) }
    data, err := json.Marshal(input); if err != nil { panic(err) }
    csharpRequestOutput.Lock(); defer csharpRequestOutput.Unlock()
    file, err := os.OpenFile(os.Getenv("CSHARP_REQUEST_FS_RECORD"), os.O_WRONLY|os.O_CREATE|os.O_APPEND, 0o644)
    if err != nil { panic(err) }; defer file.Close(); if _, err := file.Write(append(data, '\n')); err != nil { panic(err) }
}
func csharpNewForUpdate(name string, params *RequestFileSystem, base vfs.FS, cwd string, summary *project.FileChangeSummary) (vfs.FS, error) {
    result, err := NewForUpdate(params, base, cwd, summary); csharpRecordRequest(name, params, base, cwd, result); return result, err
}
func csharpNewRequestFileSystem(name string, params *RequestFileSystem, base vfs.FS, cwd string) (*requestFileSystem, error) {
    var changes project.FileChangeSummary
    result, err := csharpNewForUpdate(name, params, base, cwd, &changes); if err != nil { return nil, err }; return result.(*requestFileSystem), nil
}
