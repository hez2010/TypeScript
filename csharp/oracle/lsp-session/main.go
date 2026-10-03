package main

import (
    "context"
    "encoding/json"
    "errors"
    "fmt"
    "os"
    "sync"
    "time"

    "github.com/microsoft/TypeScript/tsc/internal/bundled"
    tsjson "github.com/microsoft/TypeScript/tsc/internal/json"
    "github.com/microsoft/TypeScript/tsc/internal/jsonrpc"
    "github.com/microsoft/TypeScript/tsc/internal/lsp"
    "github.com/microsoft/TypeScript/tsc/internal/lsp/lsproto"
    "github.com/microsoft/TypeScript/tsc/internal/testutil/contentmappertest"
    "github.com/microsoft/TypeScript/tsc/internal/vfs"
    "github.com/microsoft/TypeScript/tsc/internal/vfs/vfstest"
)

type writer struct { target lsp.Writer; mu sync.Mutex }
func (w *writer) Write(message *lsproto.Message) error { w.mu.Lock(); defer w.mu.Unlock(); return w.target.Write(message) }
type reader struct { target lsp.Reader; writer *writer; fs vfs.FS }
func (r *reader) Read() (*lsproto.Message, error) {
    for {
        message, err := r.target.Read()
        if err != nil { return nil, err }
        if message.Kind != jsonrpc.MessageKindRequest || message.AsRequest().Method != "$test/writeFiles" { return message, nil }
        request := message.AsRequest()
        data, err := tsjson.Marshal(request.Params); if err != nil { return nil, err }
        var files map[string]string
        if err := json.Unmarshal(data, &files); err != nil { return nil, err }
        for name, text := range files { if err := r.fs.WriteFile(name, text); err != nil { return nil, err } }
        if err := r.writer.Write((&lsproto.ResponseMessage{ID: request.ID, Result: lsproto.Null{}}).Message()); err != nil { return nil, err }
    }
}
func main() {
    var input struct { Files map[string]string; Cwd string; CaseSensitive bool; ProgressDelayMilliseconds int }
    data, err := os.ReadFile(os.Args[1]); if err != nil { panic(err) }
    if err := json.Unmarshal(data, &input); err != nil { panic(err) }
    if input.Cwd == "" { input.Cwd = "/" }
    fs := bundled.WrapFS(vfstest.FromMap(input.Files, input.CaseSensitive))
    output := &writer{target: lsp.ToWriter(os.Stdout)}
    server := lsp.NewServer(&lsp.ServerOptions{In: &reader{target: lsp.ToReader(os.Stdin), writer: output, fs: fs}, Out: output,
        Err: os.Stderr, Cwd: input.Cwd, FS: fs, DefaultLibraryPath: bundled.LibPath(), Spawn: contentmappertest.NewSpawner().Spawn,
        ProgressDelay: time.Duration(input.ProgressDelayMilliseconds) * time.Millisecond})
    if err := server.Run(context.Background()); err != nil && !errors.Is(err, context.Canceled) { fmt.Fprintln(os.Stderr, err); os.Exit(1) }
}
