package main

import (
	"bufio"
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/transpile"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions/tsoptionstest"
	"os"
)

type request struct {
	Text        string         `json:"text"`
	File        string         `json:"file"`
	Options     map[string]any `json:"options"`
	Report      bool           `json:"report"`
	Declaration bool           `json:"declaration"`
}

func diagnostics(items []*ast.Diagnostic) []map[string]any {
	result := make([]map[string]any, 0, len(items))
	for _, item := range items {
		file := ""
		if item.File() != nil {
			file = item.File().FileName()
		}
		result = append(result, map[string]any{"file": file, "code": item.Code(), "start": item.Pos(), "length": item.Len(),
			"message": item.String(), "related": diagnostics(item.RelatedInformation())})
	}
	return result
}

func emit(input request) (result any) {
	defer func() {
		if value := recover(); value != nil {
			result = map[string]any{"error": fmt.Sprint(value)}
		}
	}()
	config, err := json.Marshal(map[string]any{"compilerOptions": input.Options, "files": []string{"input.ts"}})
	if err != nil {
		panic(err)
	}
	host := tsoptionstest.NewVFSParseConfigHost(map[string]string{"/tsconfig.json": string(config), "/input.ts": input.Text}, "/", true)
	parsed, _ := tsoptions.GetParsedCommandLineOfConfigFile("/tsconfig.json", nil, nil, host, nil)
	parsed.CompilerOptions().ConfigFilePath = ""
	options := transpile.Options{CompilerOptions: parsed.CompilerOptions(), FileName: input.File, ReportDiagnostics: input.Report}
	var output *transpile.Output
	if input.Declaration {
		output = transpile.TranspileDeclaration(context.Background(), input.Text, options)
	} else {
		output = transpile.TranspileModule(context.Background(), input.Text, options)
	}
	return map[string]any{"textBase64": base64.StdEncoding.EncodeToString([]byte(output.OutputText)), "mapBase64": base64.StdEncoding.EncodeToString([]byte(output.SourceMapText)), "diagnostics": diagnostics(output.Diagnostics)}
}

func main() {
	scanner := bufio.NewScanner(os.Stdin)
	scanner.Buffer(make([]byte, 4096), 64*1024*1024)
	writer := bufio.NewWriter(os.Stdout)
	defer writer.Flush()
	for scanner.Scan() {
		var input request
		if err := json.Unmarshal(scanner.Bytes(), &input); err != nil {
			panic(err)
		}
		output, err := json.Marshal(emit(input))
		if err != nil {
			panic(err)
		}
		fmt.Fprintln(writer, string(output))
	}
	if err := scanner.Err(); err != nil {
		panic(err)
	}
}
