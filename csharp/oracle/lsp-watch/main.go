package main

import (
    "bufio"
    "encoding/json"
    "fmt"
    "os"
    "github.com/microsoft/TypeScript/tsc/internal/project"
    "github.com/microsoft/TypeScript/tsc/internal/lsp/lspwatcher"
)

func main() {
    scanner := bufio.NewScanner(os.Stdin)
    scanner.Buffer(make([]byte, 65536), 16 << 20)
    encoder := json.NewEncoder(os.Stdout)
    for scanner.Scan() {
        var input project.CSharpWatchInput
        if err := json.Unmarshal(scanner.Bytes(), &input); err != nil { panic(err) }
        result := probe(input)
        if err := encoder.Encode(result); err != nil { panic(err) }
    }
    if err := scanner.Err(); err != nil { panic(err) }
}

func probe(input project.CSharpWatchInput) (result any) {
    defer func() { if failure := recover(); failure != nil { fmt.Fprintln(os.Stderr, failure); result = map[string]bool{"error": true} } }()
    if input.Mode == "root" { return lspwatcher.CSharpRootFromGlob(input.Pattern) }
    return project.CSharpWatchProbe(input)
}
