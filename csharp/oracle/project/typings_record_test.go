package ata_test

import (
    "encoding/json"
    "maps"
    "os"
    "slices"
    "sync"
    "testing"

    "github.com/microsoft/TypeScript/tsc/internal/collections"
    "github.com/microsoft/TypeScript/tsc/internal/project/ata"
    "github.com/microsoft/TypeScript/tsc/internal/project/logging"
    "github.com/microsoft/TypeScript/tsc/internal/tspath"
    "github.com/microsoft/TypeScript/tsc/internal/vfs"
)

var csharpTypingsMu sync.Mutex
func recordTyping(t *testing.T, row map[string]any) {
    row["name"] = t.Name()
    bytes, err := json.Marshal(row); if err != nil { panic(err) }
    csharpTypingsMu.Lock(); defer csharpTypingsMu.Unlock()
    file, err := os.OpenFile(os.Getenv("CSHARP_TYPINGS_RECORD"), os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o644)
    if err != nil { panic(err) }; defer file.Close()
    if _, err = file.Write(append(bytes, '\n')); err != nil { panic(err) }
}
func recordValidation(t *testing.T, name string) (ata.NameValidationResult, string, bool) {
    result, part, scope := ata.ValidatePackageName(name)
    recordTyping(t, map[string]any{"operation": "validate", "text": name, "expected": []any{int(result), part, scope}})
    return result, part, scope
}

func recordDiscovery(t *testing.T, fs vfs.FS, logger logging.Logger, info *ata.TypingsInfo,
    names []string, root string, cache *collections.SyncMap[string, *ata.CachedTyping], registry map[string]map[string]string) ([]string, []string, []string) {
    files := map[string]string{}; pending := []string{"/"}; visited := map[string]bool{}
    for len(pending) > 0 {
        directory := pending[len(pending)-1]; pending = pending[:len(pending)-1]
        real := fs.Realpath(directory); if visited[real] { continue }; visited[real] = true
        entries := fs.GetAccessibleEntries(directory)
        for _, name := range entries.Files { path := tspath.CombinePaths(directory, name); if text, ok := fs.ReadFile(path); ok { files[path] = text } }
        for _, name := range entries.Directories { pending = append(pending, tspath.CombinePaths(directory, name)) }
    }
    cached := map[string]any{}
    cache.Range(func(name string, value *ata.CachedTyping) bool { cached[name] = map[string]any{"fileName": value.TypingsLocation, "version": value.Version.String()}; return true })
    acquisition := map[string]any{"include": info.TypeAcquisition.Include, "exclude": info.TypeAcquisition.Exclude,
        "disableFilenameBasedTypeAcquisition": info.TypeAcquisition.DisableFilenameBasedTypeAcquisition.IsTrue()}
    options := map[string]any{}
    if info.CompilerOptions.Types != nil { options["types"] = info.CompilerOptions.Types }
    unresolved := []string{}
    if info.UnresolvedImports != nil { unresolved = slices.Collect(maps.Keys(info.UnresolvedImports.Keys())) }
    first, second, third := ata.DiscoverTypings(fs, logger, info, names, root, cache, registry)
    sort := func(values []string) []string { result := append([]string{}, values...); slices.Sort(result); return result }
    recordTyping(t, map[string]any{"operation": "discover", "files": files, "caseSensitive": fs.UseCaseSensitiveFileNames(),
        "fileNames": names, "projectRoot": root, "cache": cached, "registry": registry, "acquisition": acquisition,
        "compilerOptions": options, "unresolvedImports": unresolved, "expected": []any{sort(first), sort(second), sort(third)}})
    return first, second, third
}
