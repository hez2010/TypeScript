package project

// Test-only recording of the original overlay test operations and their observations.
// The product implementation is unchanged; each C# replay starts from the recorded input state.
import (
    "encoding/json"
    "fmt"
    "os"
    "slices"
    "sync"
    "testing"

    "github.com/microsoft/TypeScript/tsc/internal/ls/lsconv"
    "github.com/microsoft/TypeScript/tsc/internal/tspath"
)

var csharpOverlayMu sync.Mutex

func csharpOverlayFiles(fs *overlayFS) []map[string]any {
    result := []map[string]any{}
    for _, overlay := range fs.overlays {
        result = append(result, map[string]any{"fileName": overlay.FileName(), "text": overlay.Content(),
            "version": overlay.Version(), "kind": int(overlay.Kind()), "matchesDisk": overlay.MatchesDiskText()})
    }
    slices.SortFunc(result, func(a, b map[string]any) int {
        left, right := a["fileName"].(string), b["fileName"].(string)
        if left < right { return -1 }; if left > right { return 1 }; return 0
    })
    return result
}

func recordProcessChanges(t *testing.T, fs *overlayFS, changes []FileChange) (summary FileChangeSummary, overlays map[tspath.Path]*Overlay) {
    names := map[string]bool{"/script": true, "/test1.ts": true, "/test2.ts": true}
    for _, change := range changes { names[change.URI.FileName()] = true }
    for _, overlay := range fs.overlays { names[overlay.FileName()] = true }
    disk := map[string]*string{}
    for name := range names {
        text, ok := fs.host.ReadFile(name)
        if ok { disk[name] = &text } else { disk[name] = nil }
    }
    events := []map[string]any{}
    for _, change := range changes {
        edits := []map[string]any{}
        for _, edit := range change.Changes {
            if edit.Partial != nil { edits = append(edits, map[string]any{"text": edit.Partial.Text, "range": edit.Partial.Range})
            } else if edit.WholeDocument != nil { edits = append(edits, map[string]any{"text": edit.WholeDocument.Text}) }
        }
        events = append(events, map[string]any{"kind": int(change.Kind), "fileName": change.URI.FileName(),
            "version": change.Version, "text": change.Content, "scriptKind": int(lsconv.LanguageKindToScriptKind(change.LanguageKind)), "edits": edits})
    }
    row := map[string]any{"name": t.Name(), "files": disk, "overlays": csharpOverlayFiles(fs), "changes": events,
        "caseSensitive": fs.host.UseCaseSensitiveFileNames(), "encoding": string(fs.positionEncoding)}
    defer func() {
        failure := recover()
        result := map[string]any{"panicked": failure != nil}
        if failure == nil {
            paths := func(values []string) []string { slices.Sort(values); return values }
            closed, changed, created, deleted := []string{}, []string{}, []string{}, []string{}
            for uri := range summary.Closed.Keys() { closed = append(closed, uri.FileName()) }
            for uri := range summary.Changed.Keys() { changed = append(changed, uri.FileName()) }
            for uri := range summary.Created.Keys() { created = append(created, uri.FileName()) }
            for uri := range summary.Deleted.Keys() { deleted = append(deleted, uri.FileName()) }
            opened, reopened := "", ""
            if summary.Opened != "" { opened = summary.Opened.FileName() }; if summary.Reopened != "" { reopened = summary.Reopened.FileName() }
            documents := map[string]any{}
            for name := range names {
                file := fs.GetFile(name)
                if file == nil { documents[name] = nil; continue }
                hash := file.Hash()
                documents[name] = map[string]any{"text": file.Content(), "version": file.Version(), "kind": int(file.Kind()),
                    "matchesDisk": file.MatchesDiskText(), "isOverlay": file.IsOverlay(), "hash": fmt.Sprintf("%016x%016x", hash.Hi, hash.Lo)}
            }
            result["summary"] = map[string]any{"opened": opened, "reopened": reopened, "closed": paths(closed), "changed": paths(changed),
                "created": paths(created), "deleted": paths(deleted), "outside": summary.IncludesWatchChangeOutsideNodeModules}
            result["overlays"] = csharpOverlayFiles(fs); result["documents"] = documents
        }
        row["expected"] = result
        data, err := json.Marshal(row); if err != nil { panic(err) }
        csharpOverlayMu.Lock()
        output, err := os.OpenFile(os.Getenv("CSHARP_OVERLAY_RECORD"), os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o644)
        if err == nil { _, err = output.Write(append(data, '\n')); output.Close() }
        csharpOverlayMu.Unlock()
        if err != nil { panic(err) }
        if failure != nil { panic(failure) }
    }()
    return fs.processChanges(changes)
}
