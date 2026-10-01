package main

import (
	"bufio"
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"os"
	"sort"
	"strings"
	"sync"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/bundled"
	"github.com/microsoft/TypeScript/tsc/internal/compiler"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/execute/incremental"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions/tsoptionstest"
)

type step struct {
	Edits map[string]*string `json:"edits"`
	Fresh bool `json:"fresh"`
	FailWrite string `json:"failWrite"`
}
type request struct {
	Files map[string]string `json:"files"`
	Steps []step `json:"steps"`
	Config string `json:"config"`
	Cwd string `json:"cwd"`
	CaseInsensitive bool `json:"caseInsensitive"`
	SingleThreaded bool `json:"singleThreaded"`
	HashWithText bool `json:"hashWithText"`
}

func diagnostics(items []*ast.Diagnostic) []any {
	result := []any{}
	var text func(*ast.Diagnostic, int) string
	text = func(item *ast.Diagnostic, depth int) string {
		value := item.String()
		for _, child := range item.MessageChain() { value += "\n" + strings.Repeat("  ", depth + 1) + text(child, depth + 1) }
		return value
	}
	for _, item := range compiler.SortAndDeduplicateDiagnostics(items) {
		file := ""; if item.File() != nil { file = item.File().FileName() }
		result = append(result, map[string]any{"file": file, "code": item.Code(), "start": item.Pos(), "length": item.Len(),
			"message": text(item, 0), "related": diagnostics(item.RelatedInformation())})
	}
	return result
}

func execute(input request) (result any) {
	defer func() { if value := recover(); value != nil { result = map[string]any{"error": fmt.Sprint(value)} } }()
	if input.Cwd == "" { input.Cwd = "/source" }; if input.Config == "" { input.Config = input.Cwd + "/tsconfig.json" }
	host := tsoptionstest.NewVFSParseConfigHost(input.Files, input.Cwd, !input.CaseInsensitive)
	host.Vfs = bundled.WrapFS(host.Vfs)
	ctx := context.Background()
	var previous *incremental.Program
	cycles := []any{}
	for _, step := range input.Steps {
		for file, text := range step.Edits { if text == nil { _ = host.FS().Remove(file) } else if err := host.FS().WriteFile(file, *text); err != nil { panic(err) } }
		config, errors := tsoptions.GetParsedCommandLineOfConfigFile(input.Config, nil, nil, host, nil)
		if config == nil { cycles = append(cycles, map[string]any{"diagnostics": diagnostics(errors)}); continue }
		compilerHost := compiler.NewCompilerHost(input.Cwd, host.FS(), bundled.LibPath(), nil, nil, nil)
		if step.Fresh { previous = nil }
		if previous == nil { previous = incremental.ReadBuildInfoProgram(config, incremental.NewBuildInfoReader(compilerHost), compilerHost) }
		program := compiler.NewProgram(compiler.ProgramOptions{Config: config, Host: compilerHost, SingleThreaded: core.IfElse(input.SingleThreaded, core.TSTrue, core.TSFalse)})
		current := incremental.NewProgram(program, previous, incremental.CreateHost(compilerHost), nil, input.HashWithText)
		items := compiler.GetDiagnosticsOfAnyProgram(ctx, current, nil, false, current.GetBindDiagnostics, current.GetSemanticDiagnostics)
		writes := []map[string]any{}
		var mutex sync.Mutex
		emitted := current.Emit(ctx, compiler.EmitOptions{WriteFile: func(file, text string, data *compiler.WriteFileData) error {
			mutex.Lock(); defer mutex.Unlock()
			if file == step.FailWrite { return fmt.Errorf("test write failure") }
			if err := host.FS().WriteFile(file, text); err != nil { return err }
			entry := map[string]any{"path": file}
			if data.BuildInfo != nil { entry["buildInfo"] = data.BuildInfo } else { entry["textBase64"] = base64.StdEncoding.EncodeToString([]byte(text)) }
			writes = append(writes, entry)
			return nil
		}})
		items = append(items, emitted.Diagnostics...)
		sort.Slice(writes, func(i,j int) bool { return writes[i]["path"].(string) < writes[j]["path"].(string) })
		names := emitted.EmittedFiles; if names == nil { names = []string{} }
		sources := []string{}; for _, file := range program.GetSourceFiles() { sources = append(sources, file.FileName()) }
		cycle := map[string]any{"diagnostics": diagnostics(items), "emitted": names, "writes": writes, "sources": sources, "skipped": emitted.EmitSkipped, "changedDeclaration": current.HasChangedDtsFile()}
		if text, ok := host.FS().ReadFile(config.GetBuildInfoFileName()); ok { cycle["buildInfo"] = json.RawMessage(text) }
		cycles = append(cycles, cycle)
		previous = current
	}
	return cycles
}

func main() {
	scanner := bufio.NewScanner(os.Stdin); scanner.Buffer(make([]byte, 65536), 64*1024*1024)
	writer := bufio.NewWriter(os.Stdout); defer writer.Flush()
	for scanner.Scan() {
		var input request; if err := json.Unmarshal(scanner.Bytes(), &input); err != nil { panic(err) }
		output, err := json.Marshal(execute(input)); if err != nil { panic(err) }
		fmt.Fprintln(writer, string(output))
	}
	if err := scanner.Err(); err != nil { panic(err) }
}
