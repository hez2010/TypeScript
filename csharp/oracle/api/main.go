package main

import (
    "bufio"
    "bytes"
    "context"
    "encoding/json"
    "os"
    "strconv"
    "strings"
    "time"
    "github.com/microsoft/TypeScript/tsc/internal/csharpapi"
    "github.com/microsoft/TypeScript/tsc/internal/bundled"
    tsjson "github.com/microsoft/TypeScript/tsc/internal/json"
    "github.com/microsoft/TypeScript/tsc/internal/lsp/lsproto"
    "github.com/microsoft/TypeScript/tsc/internal/ls/lsconv"
    "github.com/microsoft/TypeScript/tsc/internal/project"
    "github.com/microsoft/TypeScript/tsc/internal/testutil/projecttestutil"
    "github.com/microsoft/TypeScript/tsc/internal/vfs/vfstest"
)
type input struct {
    Cwd string `json:"cwd"`
    CaseSensitive bool `json:"caseSensitive"`
    Files map[string]string `json:"files"`
    Lsp bool `json:"lsp"`
    Requests []struct { Method, Save, Session string; Params json.RawMessage } `json:"requests"`
}
func main() {
    lines := bufio.NewScanner(os.Stdin); lines.Buffer(make([]byte, 4096), 128*1024*1024)
    output := json.NewEncoder(os.Stdout)
    for lines.Scan() {
        var item input; if err := json.Unmarshal(lines.Bytes(), &item); err != nil { panic(err) }
        if err := output.Encode(process(item)); err != nil { panic(err) }
    }
    if err := lines.Err(); err != nil { panic(err) }
}
func process(item input) []any {
    ctx := context.Background(); if item.Cwd == "" { item.Cwd = "/" }
    init := &project.SessionInit{BackgroundCtx: ctx, FS: bundled.WrapFS(vfstest.FromMap(item.Files, item.CaseSensitive)),
        Options: &project.SessionOptions{CurrentDirectory: item.Cwd, DefaultLibraryPath: bundled.LibPath(), PositionEncoding: lsproto.PositionEncodingKindUTF8, DebounceDelay: time.Hour}}
    var editor *project.Session
    if item.Lsp { init.Client = &projecttestutil.ClientMock{}; editor = project.NewSession(init); defer editor.Close() }
    sessions := map[string]*api.Session{}
    defer func() { for _, session := range sessions { session.Close() } }()
    values := map[string]json.RawMessage{}; output := []any{}
    for _, request := range item.Requests {
        params := substitute(request.Params, values)
        if request.Method == "$editor" {
            var change struct { Kind, FileName, Text string; Version int32 }
            if err := json.Unmarshal(params, &change); err != nil { panic(err) }
            uri := lsconv.FileNameToDocumentURI(change.FileName)
            switch change.Kind {
            case "open": editor.DidOpenFile(ctx, uri, change.Version, change.Text, lsproto.LanguageKindTypeScript)
            case "change": editor.DidChangeFile(ctx, uri, change.Version, []lsproto.TextDocumentContentChangePartialOrWholeDocument{{WholeDocument: &lsproto.TextDocumentContentChangeWholeDocument{Text: change.Text}}})
            case "close": editor.DidCloseFile(ctx, uri)
            default: panic("unknown editor event")
            }
            output = append(output, map[string]any{"result": nil}); continue
        }
        session := sessions[request.Session]
        if session == nil { if editor != nil { session = api.NewLSPSession(editor, nil) } else { session = api.NewStandaloneSession(init, nil) }; sessions[request.Session] = session }
        if request.Method == "$close" { session.Close(); output = append(output, map[string]any{"result": nil}); continue }
        result, err := session.HandleRequest(ctx, request.Method, tsjson.Value(params))
        if err != nil { output = append(output, map[string]any{"error": err.Error()}); continue }
        encoded, err := tsjson.Marshal(result); if err != nil { panic(err) }
        if request.Save != "" { values[request.Save] = json.RawMessage(encoded) }
        output = append(output, map[string]any{"result": json.RawMessage(encoded)})
    }
    return output
}
func substitute(value json.RawMessage, values map[string]json.RawMessage) json.RawMessage {
    value = bytes.TrimSpace(value)
    if len(value) == 0 { return json.RawMessage("null") }
    switch value[0] {
    case '{':
        var object map[string]json.RawMessage
        if err := json.Unmarshal(value, &object); err != nil { panic(err) }
        if raw, ok := object["$ref"]; ok {
            var reference string
            if err := json.Unmarshal(raw, &reference); err != nil { panic(err) }
            segments := strings.Split(reference, "."); result := values[segments[0]]
            for _, key := range segments[1:] {
                if result[0] == '[' { var array []json.RawMessage; if err := json.Unmarshal(result, &array); err != nil { panic(err) }; i, _ := strconv.Atoi(key); result = array[i] } else {
                    var object map[string]json.RawMessage; if err := json.Unmarshal(result, &object); err != nil { panic(err) }; result = object[key]
                }
            }; return result
        }
        // Preserve caller object order: configuration diagnostics depend on it.
        decoder := json.NewDecoder(bytes.NewReader(value)); decoder.Token()
        var result bytes.Buffer; result.WriteByte('{'); first := true
        for decoder.More() {
            key, err := decoder.Token(); if err != nil { panic(err) }
            var child json.RawMessage; if err := decoder.Decode(&child); err != nil { panic(err) }
            if !first { result.WriteByte(',') }; first = false
            name, err := json.Marshal(key); if err != nil { panic(err) }; result.Write(name); result.WriteByte(':'); result.Write(substitute(child, values))
        }
        result.WriteByte('}'); return result.Bytes()
    case '[':
        var array []json.RawMessage; if err := json.Unmarshal(value, &array); err != nil { panic(err) }
        for i, child := range array { array[i] = substitute(child, values) }
        result, err := json.Marshal(array); if err != nil { panic(err) }; return result
    default: return value
    }
}
