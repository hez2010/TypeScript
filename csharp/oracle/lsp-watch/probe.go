package project

import (
    "context"
    "errors"
    "github.com/microsoft/TypeScript/tsc/internal/collections"
    "github.com/microsoft/TypeScript/tsc/internal/lsp/lsproto"
    "github.com/microsoft/TypeScript/tsc/internal/tspath"
    "slices"
)

type CSharpWatchInput struct {
    Mode string `json:"mode"`
    Pattern string `json:"pattern"`
    Files []string `json:"files"`
    Workspace string `json:"workspace"`
    Library string `json:"library"`
    Typings string `json:"typings"`
    CurrentDirectory string `json:"currentDirectory"`
    CaseSensitive bool `json:"caseSensitive"`
    Relative bool `json:"relative"`
}

type CSharpWatchPattern struct {
    Pattern string `json:"pattern"`
    BaseURI string `json:"baseUri"`
    Kind lsproto.WatchKind `json:"kind"`
}

func CSharpWatchProbe(input CSharpWatchInput) any {
    if input.Mode == "registry" { return csharpRegistryProbe(input.Pattern) }
    if input.Mode == "components" { return getPathComponentsForWatching(input.Pattern, "") }
    if input.Mode == "parents" {
        parents, _ := tspath.GetCommonParents(input.Files, minWatchLocationDepth, getPathComponentsForWatching,
            tspath.ComparePathsOptions{UseCaseSensitiveFileNames: input.CaseSensitive})
        slices.Sort(parents)
        if parents == nil { parents = []string{} }
        return parents
    }
    var patterns PatternsAndIgnored
    var watchers Watchers
    switch input.Mode {
    case "resolution":
        files := &collections.SyncSet[tspath.Path]{}
        for _, file := range input.Files { files.Add(tspath.ToPath(file, input.CurrentDirectory, input.CaseSensitive)) }
        patterns = createResolutionLookupGlobMapper(input.Workspace, input.Library, input.CurrentDirectory, input.CaseSensitive)(files)
    case "typings":
        patterns = getTypingsLocationsGlobs(input.Files, input.Typings, input.Workspace, input.CurrentDirectory, input.CaseSensitive)
    case "exact":
        watchers = NewWatchedFilesForPaths("test", 7, input.Relative, input.Workspace, input.CurrentDirectory, input.CaseSensitive).Clone(input.Files).Watchers()
    default: panic(input.Mode)
    }
    if input.Mode != "exact" {
        watchers = NewWatchedFiles("test", 7, input.Relative, func(_ int) PatternsAndIgnored { return patterns }).Watchers()
    }
    result := []CSharpWatchPattern{}
    for _, watcher := range append(watchers.WorkspaceWatchers, watchers.OutsideWorkspaceWatchers...) {
        next := CSharpWatchPattern{Kind: *watcher.Kind}
        if watcher.GlobPattern.Pattern != nil { next.Pattern = *watcher.GlobPattern.Pattern } else {
            next.Pattern = watcher.GlobPattern.RelativePattern.Pattern
            next.BaseURI = string(*watcher.GlobPattern.RelativePattern.BaseUri.URI)
        }
        result = append(result, next)
    }
    return result
}

type csharpWatchClient struct {
    Client
    active map[WatcherID]bool
    fail bool
    attempts, removed int
}
func (c *csharpWatchClient) WatchFiles(_ context.Context, id WatcherID, watchers []*lsproto.FileSystemWatcher) error {
    c.attempts++
    if c.fail && *watchers[0].GlobPattern.Pattern == "/b/*" || c.active[id] { return errors.New("registration failure") }
    c.active[id] = true
    return nil
}
func (c *csharpWatchClient) UnwatchFiles(_ context.Context, id WatcherID) error { delete(c.active, id); c.removed++; return nil }
func csharpRegistryProbe(mode string) any {
    client := &csharpWatchClient{active: map[WatcherID]bool{}}
    session := &Session{client: client, watches: newWatchRegistry()}
    a := NewWatchedFiles("a", 7, false, func(_ int) PatternsAndIgnored { return PatternsAndIgnored{patternsInsideWorkspace: []string{"/a-shared/*"}} })
    b := NewWatchedFiles("b", 7, false, func(_ int) PatternsAndIgnored { return PatternsAndIgnored{patternsInsideWorkspace: []string{"/a-shared/*", "/b/*"}} })
    ctx := context.Background()
    if mode == "shared" { session.updateWatch(ctx, (*WatchedFiles[int])(nil), a) }
    client.fail = true; session.updateWatch(ctx, (*WatchedFiles[int])(nil), b)
    client.fail = false; retryErrors := len(session.updateWatch(ctx, (*WatchedFiles[int])(nil), b))
    session.updateWatch(ctx, b, (*WatchedFiles[int])(nil))
    if mode == "shared" { session.updateWatch(ctx, a, (*WatchedFiles[int])(nil)) }
    return map[string]int{"attempts": client.attempts, "unregistered": client.removed, "active": len(client.active), "retryErrors": retryErrors}
}
