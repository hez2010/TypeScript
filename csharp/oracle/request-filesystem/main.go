package main

import (
    "bufio"
    "encoding/json"
    "os"
    "github.com/microsoft/TypeScript/tsc/internal/api/requestfilesystem"
)
func main() {
    input := bufio.NewScanner(os.Stdin); input.Buffer(make([]byte, 4096), 128*1024*1024)
    output := json.NewEncoder(os.Stdout)
    for input.Scan() { if err := output.Encode(requestfilesystem.CSharpReplay(input.Bytes())); err != nil { panic(err) } }
    if err := input.Err(); err != nil { panic(err) }
}
