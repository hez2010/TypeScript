package main

import (
    "context"
    "flag"
    "fmt"
    "os"
    "strings"

    "github.com/microsoft/TypeScript/tsc/internal/bundled"
    api "github.com/microsoft/TypeScript/tsc/internal/csharpapi"
)

func main() {
    flag.Bool("api", false, "API server entry point")
    cwd := flag.String("cwd", "/", "working directory")
    async := flag.Bool("async", false, "JSON-RPC")
    timing := flag.Bool("timing", false, "collect timing")
    callbacks := flag.String("callbacks", "", "filesystem callbacks")
    pipe := flag.String("pipe", "", "pipe path")
    flag.Parse()
    var names []string
    if *callbacks != "" { names = strings.Split(*callbacks, ",") }
    server := api.NewStdioServer(&api.StdioServerOptions{
        In: os.Stdin, Out: os.Stdout, Err: os.Stderr, Cwd: *cwd, PipePath: *pipe,
        DefaultLibraryPath: bundled.LibPath(), Async: *async, CollectTiming: *timing, Callbacks: names,
    })
    if err := server.Run(context.Background()); err != nil { fmt.Fprintln(os.Stderr, err); os.Exit(1) }
}
