package encoder_test

import (
    "encoding/base64"
    "encoding/json"
    "os"
    "sync"
    "testing"

    "github.com/microsoft/TypeScript/tsc/internal/api/encoder"
    "github.com/microsoft/TypeScript/tsc/internal/ast"
)

var csharpRecordMu sync.Mutex

func csharpRecordSource(t testing.TB, source *ast.SourceFile) ([]byte, *encoder.NodeIndexTable, error) {
    csharpRecord(t, source.AsNode(), source)
    return encoder.EncodeSourceFile(source)
}

func csharpRecordNode(t testing.TB, node *ast.Node, source *ast.SourceFile) ([]byte, *encoder.NodeIndexTable, error) {
    csharpRecord(t, node, source)
    return encoder.EncodeNode(node, source)
}

// Record inputs at the original encoder boundary. Original assertions and
// version-8 expected bytes still run against the unmodified Go encoder.
func csharpRecord(t testing.TB, node *ast.Node, source *ast.SourceFile) {
    target := os.Getenv("CSHARP_AST_RECORD")
    if target == "" { return }
    options := source.ParseOptions()
    data := map[string]any{"name": t.Name(), "fileName": source.FileName(), "path": source.Path(),
        "text": base64.StdEncoding.EncodeToString([]byte(source.Text())), "scriptKind": source.ScriptKind,
        "force": options.ExternalModuleIndicatorOptions.Force, "jsx": options.ExternalModuleIndicatorOptions.JSX}
    if node != source.AsNode() { data["nodeKind"] = node.Kind }
    if source.ContentMapper() != "" {
        segments := make([][]int, 0)
        for _, s := range source.SpanMap().Segments() {
            segments = append(segments, []int{int(s.VirtualStart), int(s.VirtualEnd), int(s.OriginalStart), int(s.OriginalEnd), int(s.Kind), int(s.Features)})
        }
        directives := make([][]int, 0)
        for _, d := range source.DiagnosticDirectives() {
            directives = append(directives, []int{d.OriginalRange.Pos(), d.OriginalRange.End(), d.VirtualRange.Pos(), d.VirtualRange.End(), int(d.Policy), int(d.UnusedCode)})
        }
        data["mapping"] = map[string]any{"original": base64.StdEncoding.EncodeToString([]byte(source.OriginalText())),
            "mapper": source.ContentMapper(), "virtualFileName": source.VirtualFileName(), "segments": segments,
            "hasSpanMap": source.SpanMap() != nil, "directives": directives}
    }
    csharpRecordMu.Lock(); defer csharpRecordMu.Unlock()
    file, err := os.OpenFile(target, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0600)
    if err != nil { t.Fatal(err) }; defer file.Close()
    if err := json.NewEncoder(file).Encode(data); err != nil { t.Fatal(err) }
}
