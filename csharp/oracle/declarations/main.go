package main

import (
	"bufio"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"os"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/checker"
	"github.com/microsoft/TypeScript/tsc/internal/compiler"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/outputpaths"
	"github.com/microsoft/TypeScript/tsc/internal/printer"
	"github.com/microsoft/TypeScript/tsc/internal/sourcemap"
	"github.com/microsoft/TypeScript/tsc/internal/transformers/declarations"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions/tsoptionstest"
	"github.com/microsoft/TypeScript/tsc/internal/tspath"
)

type request struct {
	Text    string            `json:"text"`
	File    string            `json:"file"`
	Files   map[string]string `json:"files"`
	Options map[string]any    `json:"options"`
	Map     bool              `json:"map"`
	NewLine string            `json:"newLine"`
}

type declarationHost struct {
	*compiler.Program
	resolver printer.EmitResolver
}

func (h *declarationHost) GetEmitResolver() printer.EmitResolver { return h.resolver }
func (h *declarationHost) GetEffectiveDeclarationFlags(node *ast.Node, flags ast.ModifierFlags) ast.ModifierFlags {
	return h.resolver.GetEffectiveDeclarationFlags(node, flags)
}
func (h *declarationHost) GetOutputPathsFor(file *ast.SourceFile, force bool) declarations.OutputPaths {
	return outputpaths.GetOutputPathsFor(file, h.Options(), h, outputpaths.ForceEmitPaths{Dts: force})
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
	if input.File == "" {
		input.File = "/source/input.ts"
	}
	files := map[string]string{input.File: input.Text}
	for name, text := range input.Files {
		files[name] = text
	}
	options := map[string]any{"noLib": true, "module": "esnext", "moduleResolution": "bundler", "outDir": "/out", "declaration": true}
	for name, value := range input.Options {
		options[name] = value
	}
	config, err := json.Marshal(map[string]any{"compilerOptions": options, "files": []string{input.File}})
	if err != nil {
		panic(err)
	}
	files["/source/tsconfig.json"] = string(config)
	host := tsoptionstest.NewVFSParseConfigHost(files, "/source", true)
	parsed, _ := tsoptions.GetParsedCommandLineOfConfigFile("/source/tsconfig.json", nil, nil, host, nil)
	program := compiler.NewProgram(compiler.ProgramOptions{Config: parsed, Host: compiler.NewCompilerHost("/source", host.FS(), "", nil, nil, nil), SingleThreaded: core.TSTrue})
	c, _ := checker.NewChecker(program, nil)
	source := program.GetSourceFile(input.File)
	context := printer.NewEmitContext()
	tx := declarations.NewDeclarationTransformer(&declarationHost{program, c.GetEmitResolver()}, context, parsed.CompilerOptions(), "/out/input.d.ts", "/out/input.d.ts.map")
	source = tx.TransformSourceFile(source)
	newLine, newlineKind := "\n", core.NewLineKindLF
	if input.NewLine == "\r\n" {
		newLine, newlineKind = "\r\n", core.NewLineKindCRLF
	}
	p := printer.NewPrinter(printer.PrinterOptions{OnlyPrintJSDocStyle: true, RemoveComments: parsed.CompilerOptions().RemoveComments.IsTrue(),
		OmitBraceSourceMapPositions: true, NewLine: newlineKind, SourceMap: input.Map}, printer.PrintHandlers{}, context)
	var generator *sourcemap.Generator
	if input.Map {
		generator = sourcemap.NewGenerator("input.d.ts", "", "/out", tspath.ComparePathsOptions{UseCaseSensitiveFileNames: true, CurrentDirectory: "/"})
	}
	writer := printer.NewTextWriter(newLine, 4)
	p.Write(source.AsNode(), source, writer, generator)
	output := map[string]any{"textBase64": base64.StdEncoding.EncodeToString([]byte(writer.String())), "diagnostics": diagnostics(tx.GetDiagnostics())}
	if generator != nil {
		output["map"] = generator.RawSourceMap()
	}
	return output
}

func main() {
	scanner := bufio.NewScanner(os.Stdin)
	scanner.Buffer(make([]byte, 4096), 32*1024*1024)
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
