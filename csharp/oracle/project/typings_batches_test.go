package ata

import (
    "context"
    "encoding/json"
    "os"
    "slices"
    "strings"
    "sync"
    "testing"
)

var csharpBatchMu sync.Mutex
func recordBatches(t *testing.T, ctx context.Context, names []string, slots chan struct{}, install func([]string) error) error {
    var mu sync.Mutex
    batches := [][]string{}
    err := installNpmPackages(ctx, names, slots, func(values []string) error {
        mu.Lock(); batches = append(batches, append([]string{}, values...)); mu.Unlock()
        return install(values)
    })
    slices.SortFunc(batches, func(left, right []string) int { return strings.Compare(strings.Join(left, "\x00"), strings.Join(right, "\x00")) })
    row := map[string]any{"name": t.Name(), "operation": "batch", "packages": names, "concurrency": cap(slots), "fail": err != nil,
        "expected": map[string]any{"failed": err != nil, "batches": batches}}
    bytes, marshalError := json.Marshal(row); if marshalError != nil { panic(marshalError) }
    csharpBatchMu.Lock(); defer csharpBatchMu.Unlock()
    file, error := os.OpenFile(os.Getenv("CSHARP_TYPINGS_BATCH_RECORD"), os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o644)
    if error != nil { panic(error) }; defer file.Close()
    if _, error = file.Write(append(bytes, '\n')); error != nil { panic(error) }
    return err
}
